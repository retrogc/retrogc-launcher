#!/usr/bin/env bash

# RetroGC Launcher

VERSION="1.1"

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

# Remove every hook block and trailing blank lines from the startup script,
# so repeated enable/disable cycles never make the file grow.
strip_startup_hook() {
    local content

    sed -i "/$START_MARK/,/$END_MARK/d" "$STARTUP"

    # $(...) drops trailing newlines, which trims trailing blank lines
    content="$(cat "$STARTUP")"
    if [[ -n "$content" ]]; then
        printf '%s\n' "$content" > "$STARTUP"
    else
        : > "$STARTUP"
    fi
}

install_startup_hook() {
    mkdir -p "$(dirname "$STARTUP")"
    touch "$STARTUP"

    if is_running; then
        echo "RetroGC Launcher is already enabled."
        return 0
    fi

    # Never add the hook twice (e.g. hook present but daemon not running)
    strip_startup_hook

    # Separate the hook from existing content with a single blank line
    if [[ -s "$STARTUP" ]]; then
        echo >> "$STARTUP"
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

    strip_startup_hook
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
        # The rom path is relative to the core home folder (<root>/games/<core>/).
        # The leading "./" is required: since Main_MiSTer 7d88019, Minimig treats
        # paths starting with ".." as relative to the storage root instead of the
        # home folder. "./../../" stays home-relative on both old and new firmware.
        {
            printf '%s\n' '<mistergamedescription>'
            printf '  <rbf>%s</rbf>\n' "_Console/retrogc"
            printf '  <file delay="%s" index="%s" type="%s" path="./../../%s"/>\n' \
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

# The automount is read-write: while it is, FAT marks the volume dirty, and pulling the card
# then makes Windows offer to "scan and fix" it. The card is never written, so make it read-only.
# remount_read_only <device> <reason>: remounts every read-write mount of <device>; silent when
# they are all read-only already, so it can be called in a loop.
remount_read_only() {
    local dev="$1" reason="$2" target options output

    while read -r target options; do
        [[ -n "$target" ]] || continue
        target="$(printf '%b' "$target")" # findmnt -r escapes spaces as \x20
        [[ ",$options," == *,ro,* ]] && continue

        if ! output="$(mount -o remount,ro "$target" 2>&1)"; then
            warn_once "$target" "could not remount $target read-only ($reason): $output"
            continue
        fi

        options="$(findmnt -n -r -o OPTIONS --mountpoint "$target" 2>/dev/null)"
        if [[ ",$options," == *,ro,* ]]; then
            echo "Remounted read-only ($reason): $target" >&2
        else
            warn_once "$target" "still read-write after remount ($reason): $target ($options)"
        fi
    done < <(findmnt -n -r -o TARGET,OPTIONS --source "$dev" 2>/dev/null)
}

# warn_once <key> <message>: one warning per mount point per insert (the removal loop retries).
WARNED=" "
warn_once() {
    [[ "$WARNED" == *" $1 "* ]] && return 0
    WARNED+="$1 "
    echo "WARNING: $2" >&2
}

wait_for_mount() {
    local dev="$1"
    MOUNTPOINT=""
    WARNED=" "

    echo "Waiting for automount..." >&2

    while [[ -e "$DEVICE" ]]; do
        # The card may be mounted more than once: use the first mount point.
        MOUNTPOINT="$(findmnt -n -o TARGET --source "$dev" 2>/dev/null | head -n 1)"
        if [[ -n "$MOUNTPOINT" ]]; then
            echo "Mounted at: $MOUNTPOINT" >&2
            remount_read_only "$dev" "after automount"
            return 0
        fi
        sleep 0.25
    done

    return 1
}

wait_for_removal() {
    local dev="$1"
    echo "Waiting for removal..." >&2

    while [[ -e "$DEVICE" ]]; do
        # Something (automounter, Main_MiSTer loading a core) may mount it read-write again.
        remount_read_only "$dev" "mounted read-write again"
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
    echo "RetroGC Launcher daemon v$VERSION started ($SCRIPT, PID $$)"

    while true; do
        wait_for_device

        if wait_for_mount "$DEVICE_PATH"; then
            on_insert "$DEVICE_PATH" "$MOUNTPOINT"
            wait_for_removal "$DEVICE_PATH"
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
