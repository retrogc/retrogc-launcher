#!/usr/bin/env bash
set -euo pipefail

LABEL="RETROGC"
DEVICE="/dev/disk/by-label/$LABEL"
MISTER_CMD="/dev/MiSTer_cmd"

on_insert() {
    local dev="$1"
    local mountpoint="$2"
    local config="$mountpoint/retrogc.json"
    local core

    echo "RETROGC inserted"
    echo " Device: $dev"
    echo " Mount point: $mountpoint"

    if [[ ! -f "$config" ]]; then
        echo "ERROR: Missing configuration file: $config" >&2
        return 1
    fi

    if ! core="$(jq -er '.misterfpga_config.core' "$config")"; then
        echo "ERROR: Could not read .misterfpga_config.core from $config" >&2
        return 1
    fi

    # The JSON value is relative to the inserted device's root.
    # Remove a possible leading slash so it cannot discard $mountpoint.
    core="${core#/}"
    core="$mountpoint/$core"

    if [[ ! -f "$core" ]]; then
        echo "ERROR: MiSTer MRA not found: $core" >&2
        return 1
    fi

    if [[ ! -e "$MISTER_CMD" ]]; then
        echo "ERROR: MiSTer command device not found: $MISTER_CMD" >&2
        return 1
    fi

    echo " Loading MiSTer core: $core"
    printf 'load_core %s\n' "$core" > "$MISTER_CMD"
}

on_remove() {
    local dev="$1"
    local old_mountpoint="$2"

    echo "RETROGC removed"
    echo " Previous device: $dev"
    echo " Previous mount point: $old_mountpoint"
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
