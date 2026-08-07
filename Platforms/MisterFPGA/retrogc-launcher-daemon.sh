#!/usr/bin/env bash

LABEL="RETROGC"
DEVICE="/dev/disk/by-label/$LABEL"
MISTER_CMD="/dev/MiSTer_cmd"

PIDFILE="/tmp/retrogc-launcher.pid"
LOCKDIR="/tmp/retrogc-launcher.lock"

FAKE_CONSOLE_CORE="/media/fat/_Console/retrogc.rbf"
FAKE_ARCADE_CORE="/media/fat/_Arcade/cores/retrogc.rbf"
FAKE_ARCADE_MRA="/media/fat/_Arcade/retrogc.mra"


# ------------------------------------------------------------
# Single-instance lock
# ------------------------------------------------------------

if ! mkdir "$LOCKDIR" 2>/dev/null; then
    echo "RetroGC Launcher daemon is already running."
    exit 0
fi


# ------------------------------------------------------------
# Game cleanup
# ------------------------------------------------------------

cleanup_game() {
    rm -f "$FAKE_CONSOLE_CORE"
    rm -f "$FAKE_ARCADE_CORE"
    rm -f "$FAKE_ARCADE_MRA"

    if [[ -e "$MISTER_CMD" ]]; then
        echo "load_core menu.rbf" > "$MISTER_CMD"
    fi
}


# ------------------------------------------------------------
# Daemon cleanup
# ------------------------------------------------------------

cleanup_daemon() {
    cleanup_game

    rm -f "$PIDFILE"
    rm -rf "$LOCKDIR"
}


# ------------------------------------------------------------
# Signals / exit
# ------------------------------------------------------------

trap cleanup_daemon EXIT
trap 'exit 143' TERM INT


# ------------------------------------------------------------
# PID
# ------------------------------------------------------------

echo "$$" > "$PIDFILE"


# ------------------------------------------------------------
# RETROGC insertion
# ------------------------------------------------------------

on_insert() {
    local dev="$1"
    local mountpoint="$2"

    local config
    local configs=()

    local core
    local corePath

    local rom
    local romPath

    local delay
    local index
    local type
    local mgl

    echo "RETROGC inserted"
    echo " Device: $dev"
    echo " Mount point: $mountpoint"

    shopt -s nullglob
    configs=("$mountpoint"/*.rgc)
    shopt -u nullglob

    if ((${#configs[@]} == 0)); then
        echo "ERROR: No .rgc configuration file found in: $mountpoint" >&2
        return 1
    fi

    config="${configs[0]}"

    if [[ ! -f "$config" ]]; then
        echo "ERROR: Missing configuration file: $config" >&2
        return 1
    fi

    echo " Using configuration: $config"

    if ! core="$(jq -er '.misterfpga.core' "$config")"; then
        echo "ERROR: Could not read .misterfpga.core from $config" >&2
        return 1
    fi

    if ! rom="$(jq -er '.misterfpga.roms[0]' "$config")"; then
        echo "ERROR: Could not read .misterfpga.roms[0] from $config" >&2
        return 1
    fi

    core="${core#/}"
    rom="${rom#/}"

    corePath="$mountpoint/$core"
    romPath="$mountpoint/$rom"

    if [[ ! -f "$corePath" ]]; then
        echo "WARN: MiSTer CORE not found: $core" >&2
        return 1
    fi

    if [[ ! -f "$romPath" ]]; then
        echo "WARN: MiSTer ROM not found: $rom" >&2
        return 1
    fi

    if [[ ! -e "$MISTER_CMD" ]]; then
        echo "ERROR: MiSTer command device not found: $MISTER_CMD" >&2
        return 1
    fi


    # --------------------------------------------------------
    # Arcade MRA
    # --------------------------------------------------------

    if [[ "$rom" == *.mra ]]; then

        echo "Loading MiSTer arcade rom:"
        echo "  ROM : $romPath"
        echo "  CORE: $corePath"

        # Modify MRA directly on RETROGC card.
        sed -i -E \
            "/<rbf>retrogc<\/rbf>/! s|<rbf>.*?</rbf>|<rbf>retrogc</rbf>|g" \
            "$romPath" \
            && sync

        mkdir -p /media/fat/_Arcade/cores

        ln -sf "$corePath" "$FAKE_ARCADE_CORE"
        ln -sf "$romPath" "$FAKE_ARCADE_MRA"

        printf 'load_core %s\n' "_Arcade/retrogc.mra" > "$MISTER_CMD"


    # --------------------------------------------------------
    # Normal ROM
    # --------------------------------------------------------

    else

        if ! delay="$(jq -er '.misterfpga.delay' "$config")"; then
            echo "WARN: Could not read .misterfpga.delay" >&2
            delay="1"
        fi

        if ! index="$(jq -er '.misterfpga.index' "$config")"; then
            echo "WARN: Could not read .misterfpga.index" >&2
            index="1"
        fi

        if ! type="$(jq -er '.misterfpga.type' "$config")"; then
            echo "WARN: Could not read .misterfpga.type" >&2
            type="f"
        fi

        mkdir -p /media/fat/_Console

        ln -sf "$corePath" "$FAKE_CONSOLE_CORE"

        mgl="/tmp/game.mgl"

        {
            printf '%s\n' '<mistergamedescription>'
            printf '  <rbf>%s</rbf>\n' "_Console/retrogc"
            printf '  <file delay="%s" index="%s" type="%s" path="../../%s"/>\n' \
                "$delay" "$index" "$type" "$rom"
            printf '%s\n' '</mistergamedescription>'
        } > "$mgl"

        echo "Loading MiSTer core: $core with $rom"
        echo "MGL:"
        cat "$mgl"

        printf 'load_core %s\n' "$mgl" > "$MISTER_CMD"
    fi
}


# ------------------------------------------------------------
# RETROGC removal
# ------------------------------------------------------------

on_remove() {
    local dev="$1"
    local old_mountpoint="$2"

    echo "RETROGC removed"
    echo " Previous device: $dev"
    echo " Previous mount point: $old_mountpoint"

    # Only clean up the currently loaded game.
    # DO NOT remove PIDFILE or LOCKDIR here.
    cleanup_game
}


# ------------------------------------------------------------
# Wait for device
# ------------------------------------------------------------

wait_for_device() {
    echo "Waiting for RETROGC..." >&2

    while [[ ! -e "$DEVICE" ]]; do
        sleep 0.5
    done

    DEVICE_PATH="$(readlink -f "$DEVICE")"

    echo "Device detected: $DEVICE_PATH" >&2
}


# ------------------------------------------------------------
# Wait for automount
# ------------------------------------------------------------

wait_for_mount() {
    local dev="$1"

    MOUNTPOINT=""

    echo "Waiting for automount..." >&2

    while [[ -e "$DEVICE" ]]; do
        if MOUNTPOINT="$(findmnt -n -o TARGET --source "$dev" 2>/dev/null)"; then
            echo "Mounted at: $MOUNTPOINT" >&2
            return 0
        fi

        sleep 0.25
    done

    return 1
}


# ------------------------------------------------------------
# Wait for removal
# ------------------------------------------------------------

wait_for_removal() {
    echo "Waiting for removal..." >&2

    while [[ -e "$DEVICE" ]]; do
        sleep 0.5
    done
}


# ------------------------------------------------------------
# Main loop
# ------------------------------------------------------------

while true; do
    wait_for_device

    if wait_for_mount "$DEVICE_PATH"; then
        on_insert "$DEVICE_PATH" "$MOUNTPOINT"

        wait_for_removal

        on_remove "$DEVICE_PATH" "$MOUNTPOINT"
    else
        echo "RETROGC was removed before automount completed" >&2
    fi

    echo
done