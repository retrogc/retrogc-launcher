#!/usr/bin/env bash

# RetroGC Launcher

LABEL="RETROGC"
DEVICE="/dev/disk/by-label/$LABEL"
MISTER_CMD="/dev/MiSTer_cmd"

SCRIPT="/media/fat/Scripts/retrogc.sh"
STARTUP="/media/fat/linux/user-startup.sh"

PIDFILE="/tmp/retrogc-launcher.pid"
LOCKDIR="/tmp/retrogc-launcher.lock"

FAKE_MGL_CORE="/media/fat/_Console/retrogc.rbf"

START_MARK="# >>> RetroGC Launcher >>>"
END_MARK="# <<< RetroGC Launcher <<<"
LOGFILE="/tmp/retrogc-launcher.log"

is_running() {
    [[ -f "$PIDFILE" ]] || return 1

    local pid
    pid="$(cat "$PIDFILE" 2>/dev/null)" || return 1
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" 2>/dev/null
}

install_startup_hook() {
    mkdir -p "$(dirname "$STARTUP")"
    touch "$STARTUP"

    if is_running; then
        echo "RetroGC Launcher is already enabled."
        return 0
    fi

    cat >> "$STARTUP" <<EOF_HOOK

$START_MARK
if [ -x "$SCRIPT" ]; then
    "$SCRIPT" --daemon >"$LOGFILE" 2>&1 &
fi
$END_MARK
EOF_HOOK

    sync
    echo "RetroGC Launcher enabled."
}

remove_startup_hook() {
    [[ ! -f "$STARTUP" ]] && return 0

    sed -i "/$START_MARK/,/$END_MARK/d" "$STARTUP"
    sync
    echo "RetroGC Launcher disabled."
}

start_daemon() {
    if is_running; then
        local pid
        pid="$(cat "$PIDFILE")"
        echo "RetroGC Launcher is already running (PID $pid)."
        return 0
    fi

    if [[ -f "$PIDFILE" ]]; then
        echo "Removing stale PID file."
        rm -f "$PIDFILE"
    fi

    if [[ ! -x "$SCRIPT" ]]; then
        echo "ERROR: launcher script not found or not executable:"
        echo "  $SCRIPT"
        return 1
    fi

    "$SCRIPT" --daemon >"$LOGFILE" 2>&1 &

    for _ in {1..20}; do
        if is_running; then
            local pid
            pid="$(cat "$PIDFILE")"
            echo "RetroGC Launcher started (PID $pid)."
            return 0
        fi
        sleep 0.1
    done

    echo "ERROR: RetroGC Launcher failed to start."
    return 1
}

stop_daemon() {
    if ! is_running; then
        echo "RetroGC Launcher is not running."
        rm -f "$PIDFILE"
        if [[ -d "$LOCKDIR" ]]; then
            rm -rf "$LOCKDIR"
        fi
        return 0
    fi

    local pid
    pid="$(cat "$PIDFILE")"
    echo "Stopping RetroGC Launcher (PID $pid)..."
    kill "$pid"

    for _ in {1..20}; do
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "RetroGC Launcher stopped."
            return 0
        fi
        sleep 0.1
    done

    echo "WARNING: RetroGC Launcher did not stop cleanly."
    echo "Forcing termination..."
    kill -9 "$pid" 2>/dev/null || true

    rm -f "$PIDFILE"
    rm -rf "$LOCKDIR"
}

# ------------------------------------------------------------
# Daemon implementation
# ------------------------------------------------------------

cleanup_game() {
    rm -f "$FAKE_MGL_CORE"

    if [[ -e "$MISTER_CMD" ]]; then
        echo "load_core menu.rbf" > "$MISTER_CMD"
    fi
}

cleanup_daemon() {
    cleanup_game
    rm -f "$PIDFILE"
    rm -rf "$LOCKDIR"
}

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

    # Arcade MRA
    if [[ "$rom" == *.mra ]]; then
        echo "Loading MiSTer arcade rom:"
        echo "  ROM : $romPath"
        echo "  CORE: $corePath"
        printf 'load_core %s\n' "$romPath" > "$MISTER_CMD"
    # Normal ROM
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

        # TODO: find why i can't use "/media/usb0/_Console/xxx.rbf"
        mkdir -p /media/fat/_Console \
          && ln -sf "$corePath" "$FAKE_MGL_CORE"
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

wait_for_device() {
    echo "Waiting for RETROGC..." >&2

    while [[ ! -e "$DEVICE" ]]; do
        sleep 0.5
    done

    DEVICE_PATH="$(readlink -f "$DEVICE")"
    echo "Device detected: $DEVICE_PATH" >&2
}

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

wait_for_removal() {
    echo "Waiting for removal..." >&2

    while [[ -e "$DEVICE" ]]; do
        sleep 0.5
    done
}

daemon_main() {
    # Single-instance lock
    if ! mkdir "$LOCKDIR" 2>/dev/null; then
        echo "RetroGC Launcher daemon is already running."
        exit 0
    fi

    trap cleanup_daemon EXIT
    trap 'exit 143' TERM INT

    echo "$$" > "$PIDFILE"

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
}

# --daemon is used by user-startup.sh; without it, act as the Scripts-window toggle.
if [[ "${1:-}" == "--daemon" ]]; then
    daemon_main
else
    if is_running; then
        remove_startup_hook
        stop_daemon
    else
        install_startup_hook
        start_daemon
    fi
fi
