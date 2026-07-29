#!/usr/bin/env bash
set -euo pipefail

LABEL="RETROGC"
DEVICE="/dev/disk/by-label/$LABEL"
MISTER_CMD="/dev/MiSTer_cmd"
FAKE_CONSOLE_CORE="/media/fat/_Console/RetroGameCard.rbf"
FAKE_ARCADE_CORE="/media/fat/_Arcade/cores/RetroGameCard.rbf"
FAKE_ARCADE_MRA="/media/fat/_Arcade/RetroGameCard.mra"

on_insert() {
    local dev="$1"
    local mountpoint="$2"
    local config
    local configs=()
    local core
    local corePah
    local rom
    local romPath

    echo "RETROGC inserted"
    echo " Device: $dev"
    echo " Mount point: $mountpoint"
    
    # 
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

    if ! core="$(jq -er '.misterfpga_config.core' "$config")"; then
        echo "ERROR: Could not read .misterfpga_config.core from $config" >&2
        return 1
    fi

    if ! rom="$(jq -er '.misterfpga_config.rom' "$config")"; then
        echo "ERROR: Could not read .misterfpga_config.rom from $config" >&2
        return 1
    fi

    # remove possible leading slash so it cannot discard $mountpoint.
    core="${core#/}"
    corePath="$mountpoint/$core"
    rom="${rom#/}"
    romPath="$mountpoint/$rom"

    if [[ ! -f "$corePath" ]]; then
        echo " WARN: MiSTer CORE not found: $core" >&2
        #return 1
    fi

    if [[ ! -f "$romPath" ]]; then
        echo " WARN: MiSTer ROM not found: $rom" >&2
        #return 1
    fi

    if [[ ! -e "$MISTER_CMD" ]]; then
        echo "ERROR: MiSTer command device not found: $MISTER_CMD" >&2
        #return 1
    fi

    if [[ "$rom" == *.mra ]]; then
        echo " Loading MiSTer arcade rom: $romPath"
        mkdir -p /media/fat/_Console \
          && ln -sf "$corePath" "$FAKE_ARCADE_CORE" \
          && ln -sf "$romPath" "$FAKE_ARCADE_MRA"
        printf 'load_core %s\n' "_Arcade/RetroGameCard.mra" > "$MISTER_CMD"
    else
        # link our core to fat...
        mkdir -p /media/fat/_Console \
          && ln -sf "$corePath" "$FAKE_CONSOLE_CORE"
        # create mgl
        mgl="/tmp/game.mgl"
        {
          printf '%s\n' '<mistergamedescription>'
          printf '  <rbf>%s</rbf>\n' "_Console/RetroGameCard"
          printf '  <file delay="1" type="f" index="1" path="../../%s"/>\n' "$rom"
          printf '%s\n' '</mistergamedescription>'
        } > "$mgl"
        # run mgl
        echo " Loading MiSTer core: $core with $rom"
        printf 'load_core %s\n' "$mgl" > "$MISTER_CMD"
    fi
}

on_remove() {
    local dev="$1"
    local old_mountpoint="$2"

    echo "RETROGC removed"
    echo " Previous device: $dev"
    echo " Previous mount point: $old_mountpoint"

    # cleanup core link on fat device if any
    rm -f "$FAKE_CONSOLE_CORE"
    rm -f "$FAKE_ARCADE_CORE"
    rm -f "$FAKE_ARCADE_MRA"

    # load menu core
    echo "load_core menu.rbf" > "$MISTER_CMD"
}

wait_for_device() {
    echo "Waiting for RETROGC..." >&2

    while [[ ! -e "$DEVICE" ]]; do
        sleep 0.5
    done

    local dev
    dev="$(readlink -f "$DEVICE")"

    echo "Device detected: $dev" >&2
    printf '%s\n' "$dev"
}

wait_for_mount() {
    local dev="$1"
    local mountpoint

    echo "Waiting for automount..." >&2

    while [[ -e "$DEVICE" ]]; do
        if mountpoint="$(findmnt -n -o TARGET --source "$dev" 2>/dev/null)"; then
            echo "Mounted at: $mountpoint" >&2
            printf '%s\n' "$mountpoint"
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

while true; do
    dev="$(wait_for_device)"

    if mountpoint="$(wait_for_mount "$dev")"; then
        on_insert "$dev" "$mountpoint"
        wait_for_removal
        on_remove "$dev" "$mountpoint"
    else
        echo "RETROGC was removed before automount completed" >&2
    fi

    echo
done
