#!/usr/bin/env bash

# RetroGC Launcher for Recalbox
#
# Watches for a USB stick labelled RETROGC and starts its game, then goes back
# to EmulationStation when the stick is removed.
#
#   retrogc.sh             toggle (enable / disable)
#   retrogc.sh --enable    start now and at every boot
#   retrogc.sh --disable   stop and remove the boot hook
#   retrogc.sh --status    show whether the watcher is running
#   retrogc.sh --daemon    run the watcher (used by the boot hook)
#
# Every path can be overridden with an RGC_* environment variable (for testing).

LABEL="${RGC_LABEL:-RETROGC}"

SCRIPT="${RGC_SCRIPT:-/recalbox/share/system/retrogc/retrogc.sh}"
STARTUP="${RGC_STARTUP:-/recalbox/share/system/custom.sh}"
BIOS_DIR="${RGC_BIOS_DIR:-/recalbox/share/bios}"
MOUNTS="${RGC_MOUNTS:-/proc/mounts}"
DISK_BY_LABEL="${RGC_DISK_BY_LABEL:-/dev/disk/by-label}"

RETROARCH="${RGC_RETROARCH:-/usr/bin/retroarch}"
RETROARCH_CONFIG="${RGC_RETROARCH_CONFIG:-/recalbox/share/system/configs/retroarch/retroarchcustom.cfg}"
CORES_DIR="${RGC_CORES_DIR:-/usr/lib/libretro}"
EMULATOR_LAUNCHER="${RGC_EMULATOR_LAUNCHER:-}"
PYTHON="${RGC_PYTHON:-python3}"

PIDFILE="${RGC_PIDFILE:-/tmp/retrogc-launcher.pid}"
LOCKDIR="${RGC_LOCKDIR:-/tmp/retrogc-launcher.lock}"
LOGFILE="${RGC_LOGFILE:-/tmp/retrogc-launcher.log}"
OWN_MOUNT="${RGC_OWN_MOUNT:-/tmp/retrogc-card}"

INSTALLER_MANIFEST="retrogc-data.rgc"
START_MARK="# >>> RetroGC Launcher >>>"
END_MARK="# <<< RetroGC Launcher <<<"

# How long a launch must survive to count as started (seconds)
LAUNCH_GRACE="${RGC_LAUNCH_GRACE:-3}"
# How long to wait for Recalbox to automount the stick (seconds)
AUTOMOUNT_WAIT="${RGC_AUTOMOUNT_WAIT:-5}"

log() { echo "[$(date +%H:%M:%S)] $*"; }
err() { echo "[$(date +%H:%M:%S)] ERROR: $*" >&2; }

# ------------------------------------------------------------
# Enable / disable (boot hook in custom.sh)
# ------------------------------------------------------------

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

    # Never add the hook twice
    strip_startup_hook

    # custom.sh runs as a shell script: give a new file a shebang
    if [[ ! -s "$STARTUP" ]]; then
        echo '#!/bin/bash' > "$STARTUP"
    fi
    echo >> "$STARTUP"

    # custom.sh is called with "start" at boot and "stop" at shutdown.
    cat >> "$STARTUP" <<EOF_HOOK
$START_MARK
if [ "\$1" = "start" ] && [ -x "$SCRIPT" ]; then
    "$SCRIPT" --daemon >"$LOGFILE" 2>&1 &
fi
$END_MARK
EOF_HOOK

    chmod +x "$STARTUP"
    sync
    echo "RetroGC Launcher enabled at boot ($STARTUP)."
}

remove_startup_hook() {
    [[ -f "$STARTUP" ]] || return 0

    strip_startup_hook
    sync
    echo "RetroGC Launcher disabled at boot."
}

start_daemon() {
    if is_running; then
        echo "RetroGC Launcher is already running (PID $(cat "$PIDFILE"))."
        return 0
    fi

    rm -f "$PIDFILE"
    rmdir "$LOCKDIR" 2>/dev/null

    if [[ ! -x "$SCRIPT" ]]; then
        echo "ERROR: launcher script not found or not executable:"
        echo "  $SCRIPT"
        return 1
    fi

    "$SCRIPT" --daemon >"$LOGFILE" 2>&1 &

    for _ in {1..20}; do
        if is_running; then
            echo "RetroGC Launcher started (PID $(cat "$PIDFILE")). Log: $LOGFILE"
            return 0
        fi
        sleep 0.1
    done

    echo "ERROR: RetroGC Launcher failed to start, see $LOGFILE"
    return 1
}

stop_daemon() {
    if ! is_running; then
        echo "RetroGC Launcher is not running."
        rm -f "$PIDFILE"
        rm -rf "$LOCKDIR"
        return 0
    fi

    local pid
    pid="$(cat "$PIDFILE")"
    echo "Stopping RetroGC Launcher (PID $pid)..."
    kill "$pid"

    # Give it time to stop a running game and restart EmulationStation
    for _ in {1..80}; do
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "RetroGC Launcher stopped."
            return 0
        fi
        sleep 0.1
    done

    echo "WARNING: RetroGC Launcher did not stop cleanly, forcing termination."
    kill -9 "$pid" 2>/dev/null || true
    rm -f "$PIDFILE"
    rm -rf "$LOCKDIR"
}

# ------------------------------------------------------------
# JSON (.rgc) reading: jq when available, else python3
# ------------------------------------------------------------

# rgc_get <file> <filter>: prints the value(s) of a simple jq-style filter
# (".a.b", ".a.b[0]", ".a.b[]"); fails when missing, null or empty.
rgc_get() {
    local file="$1" filter="$2"

    if command -v jq >/dev/null 2>&1; then
        jq -er "$filter // empty" "$file" 2>/dev/null | grep . && return 0
        return 1
    fi

    "$PYTHON" - "$file" "$filter" <<'EOF_PY'
import json, re, sys

with open(sys.argv[1], encoding="utf-8-sig") as f:
    values = [json.load(f)]

for key, index, expand in re.findall(r"\.([A-Za-z0-9_]+)|\[(\d+)\]|\[(\])", sys.argv[2]):
    out = []
    for value in values:
        if key:
            if isinstance(value, dict) and key in value:
                out.append(value[key])
        elif index:
            if isinstance(value, list) and int(index) < len(value):
                out.append(value[int(index)])
        elif isinstance(value, list):
            out.extend(value)
    values = out

values = [v for v in values if v not in (None, "")]
for v in values:
    print(v if isinstance(v, str) else json.dumps(v))
sys.exit(0 if values else 1)
EOF_PY
}

# ------------------------------------------------------------
# EmulationStation
# ------------------------------------------------------------

es_ctl() {
    local action="$1"

    if [[ -n "${RGC_ES:-}" ]]; then
        "$RGC_ES" "$action"
    elif command -v es >/dev/null 2>&1; then
        es "$action"
    elif [[ -x /etc/init.d/S31emulationstation ]]; then
        /etc/init.d/S31emulationstation "$action"
    else
        err "Could not $action EmulationStation (no 'es' command)"
        return 1
    fi
}

# ------------------------------------------------------------
# Game
# ------------------------------------------------------------

GAME_PID=""
GAME_RUNNING=0
CARD_MOUNTED_BY_US=0

find_emulator_launcher() {
    if [[ -n "$EMULATOR_LAUNCHER" ]]; then
        [[ -f "$EMULATOR_LAUNCHER" ]] && echo "$EMULATOR_LAUNCHER"
        return
    fi

    local candidate
    for candidate in \
        /usr/lib/python3*/site-packages/configgen/emulatorlauncher.py \
        /usr/lib/python3*/site-packages/configgen/emulatorlauncher.pyc; do
        if [[ -f "$candidate" ]]; then
            echo "$candidate"
            return
        fi
    done
}

# Copies a file only when its size or modification time changed.
copy_if_changed() {
    local src="$1" dst="$2"

    if [[ -f "$dst" && "$(stat -c '%s %Y' "$src")" == "$(stat -c '%s %Y' "$dst")" ]]; then
        return 0
    fi

    log "Copying system file '$src' -> '$dst'"
    cp -p "$src" "$dst"
}

# Starts the command in the background; succeeds if it is still running after
# LAUNCH_GRACE seconds.
start_game() {
    log "Running: $*"
    "$@" &
    GAME_PID=$!

    local waited=0
    while (( waited < LAUNCH_GRACE * 10 )); do
        if ! kill -0 "$GAME_PID" 2>/dev/null; then
            wait "$GAME_PID" 2>/dev/null
            err "The game exited right away (code $?)"
            GAME_PID=""
            return 1
        fi
        sleep 0.1
        waited=$((waited + 1))
    done

    GAME_RUNNING=1
    return 0
}

stop_game() {
    [[ -n "$GAME_PID" ]] || return 0

    if kill -0 "$GAME_PID" 2>/dev/null; then
        log "Stopping the game (PID $GAME_PID)"
        # emulatorlauncher kills its emulator on SIGINT. Background jobs of a
        # script ignore SIGINT though, so RetroArch itself gets SIGTERM next.
        local sig
        for sig in INT TERM; do
            kill -"$sig" "$GAME_PID" 2>/dev/null
            for _ in {1..15}; do
                kill -0 "$GAME_PID" 2>/dev/null || break 2
                sleep 0.1
            done
        done

        if kill -0 "$GAME_PID" 2>/dev/null; then
            kill -9 "$GAME_PID" 2>/dev/null
            # A direct RetroArch launch has no child, but emulatorlauncher does
            pkill -9 -f "$RETROARCH" 2>/dev/null
        fi
    fi

    wait "$GAME_PID" 2>/dev/null
    GAME_PID=""
    GAME_RUNNING=0
}

on_insert() {
    local mountpoint="$1"
    local configs=() config core rom romPath system launcher
    local runtimes=() runtime

    log "RETROGC inserted, mount point: $mountpoint"

    shopt -s nullglob
    configs=("$mountpoint"/*.rgc)
    shopt -u nullglob

    if ((${#configs[@]} == 0)); then
        err "No .rgc card definition found in: $mountpoint"
        return 1
    fi

    if [[ -f "$mountpoint/$INSTALLER_MANIFEST" ]]; then
        log "Installer card (desktop launcher only), ignored"
        return 0
    fi

    config="$(printf '%s\n' "${configs[@]}" | sort -f | head -n 1)"
    log "Using card: $config"

    if ! core="$(rgc_get "$config" '.retroarch.core')"; then
        err "Could not read retroarch.core from $config"
        return 1
    fi

    if ! rom="$(rgc_get "$config" '.retroarch.roms[0]')"; then
        err "Could not read retroarch.roms[0] from $config"
        return 1
    fi

    romPath="$mountpoint/${rom#/}"
    if [[ ! -f "$romPath" ]]; then
        err "Missing game file: $rom"
        return 1
    fi

    # BIOS / system files
    mapfile -t runtimes < <(rgc_get "$config" '.retroarch.runtimes[]' || true)
    if ((${#runtimes[@]} > 0)); then
        mkdir -p "$BIOS_DIR"
        for runtime in "${runtimes[@]}"; do
            if [[ ! -f "$mountpoint/${runtime#/}" ]]; then
                err "Missing system file: $runtime"
                return 1
            fi
            copy_if_changed "$mountpoint/${runtime#/}" "$BIOS_DIR/$(basename "$runtime")" || return 1
        done
    fi

    system="$(rgc_get "$config" '.retroarch.system' || true)"

    log "Stopping EmulationStation"
    es_ctl stop
    sleep 1

    # Recalbox's own launcher (controllers, video, per system settings)
    if [[ -n "$system" ]]; then
        launcher="$(find_emulator_launcher)"
        if [[ -n "$launcher" ]]; then
            log "Launching with Recalbox: system '$system', core '$core'"
            if start_game "$PYTHON" "$launcher" \
                -system "$system" -rom "$romPath" -emulator libretro -core "$core"; then
                return 0
            fi
            log "Recalbox launch failed, trying RetroArch directly"
        else
            log "Recalbox emulatorlauncher not found, trying RetroArch directly"
        fi
    else
        log "No retroarch.system in the card, launching RetroArch directly"
    fi

    # Direct RetroArch launch
    local corePath="$CORES_DIR/${core}_libretro.so"
    if [[ ! -f "$corePath" ]]; then
        err "Core not installed: $corePath"
        es_ctl start
        return 1
    fi

    local args=(-L "$corePath")
    [[ -f "$RETROARCH_CONFIG" ]] && args+=(--config "$RETROARCH_CONFIG")

    if ! start_game "$RETROARCH" "${args[@]}" "$romPath"; then
        es_ctl start
        return 1
    fi
}

on_remove() {
    log "RETROGC removed"

    local had_game=$GAME_RUNNING
    stop_game

    if (( CARD_MOUNTED_BY_US )); then
        umount "$OWN_MOUNT" 2>/dev/null || umount -l "$OWN_MOUNT" 2>/dev/null
        CARD_MOUNTED_BY_US=0
    fi

    # Back to EmulationStation (unless it is already back after the game ended)
    if (( had_game )); then
        log "Starting EmulationStation"
        es_ctl start
    fi
}

# ------------------------------------------------------------
# Card detection
# ------------------------------------------------------------

# Prints the block device of the RETROGC volume, if present.
find_card_device() {
    if [[ -e "$DISK_BY_LABEL/$LABEL" ]]; then
        readlink -f "$DISK_BY_LABEL/$LABEL"
        return 0
    fi

    # BusyBox blkid has no -L: parse its output instead.
    command -v blkid >/dev/null 2>&1 || return 1
    blkid 2>/dev/null | awk -F: -v label="LABEL=\"$LABEL\"" 'index($0, label) { print $1; exit }' | grep .
}

# Prints the mount point of <device>, if mounted.
find_mountpoint() {
    local dev="$1" source target _

    while read -r source target _; do
        # Skip pseudo filesystems (proc, tmpfs...): only real device paths
        [[ "$source" == /* && -e "$source" ]] || continue
        if [[ "$(readlink -f "$source")" == "$dev" ]]; then
            # /proc/mounts escapes spaces as \040
            printf '%b\n' "$target"
            return 0
        fi
    done < "$MOUNTS"

    return 1
}

# Prints "<mount point> <options>" for every mount of <device>, mount points unescaped.
list_mounts() {
    local dev="$1" source target _ options _

    while read -r source target _ options _; do
        [[ "$source" == /* && -e "$source" ]] || continue
        [[ "$(readlink -f "$source")" == "$dev" ]] || continue
        # /proc/mounts escapes spaces as \040: keep them escaped here, unescape when used
        printf '%s %s\n' "$target" "$options"
    done < "$MOUNTS"
}

# The automount is read-write: while it is, FAT marks the volume dirty, and pulling the card
# then makes Windows offer to "scan and fix" it. The card is never written, so make it read-only.
# remount_read_only <device> <reason>: remounts every read-write mount of <device>; silent when
# they are all read-only already, so it can be called in a loop.
remount_read_only() {
    local dev="$1" reason="$2" target escaped options output

    while read -r escaped options; do
        [[ -n "$escaped" ]] || continue
        [[ ",$options," == *,ro,* ]] && continue
        target="$(printf '%b' "$escaped")"

        if ! output="$(mount -o remount,ro "$target" 2>&1)"; then
            warn_once "$target" "Could not remount $target read-only ($reason): $output"
            continue
        fi

        local mounted now
        options=""
        while read -r mounted now; do
            [[ "$mounted" == "$escaped" ]] && { options="$now"; break; }
        done < <(list_mounts "$dev")
        if [[ ",$options," == *,ro,* ]]; then
            log "Remounted read-only ($reason): $target"
        else
            warn_once "$target" "Still read-write after remount ($reason): $target ($options)"
        fi
    done < <(list_mounts "$dev")
}

# warn_once <key> <message>: one error per mount point per insert (the removal loop retries).
WARNED=" "
warn_once() {
    [[ "$WARNED" == *" $1 "* ]] && return 0
    WARNED+="$1 "
    err "$2"
}

wait_for_mount() {
    local dev="$1" waited=0
    MOUNTPOINT=""
    WARNED=" "

    log "Waiting for automount of $dev..."
    while [[ -e "$dev" ]]; do
        if MOUNTPOINT="$(find_mountpoint "$dev")"; then
            log "Mounted at: $MOUNTPOINT"
            remount_read_only "$dev" "after automount"
            return 0
        fi

        # Not automounted: mount it ourselves, read-only.
        if (( waited >= AUTOMOUNT_WAIT * 4 )); then
            mkdir -p "$OWN_MOUNT"
            if mount -o ro "$dev" "$OWN_MOUNT" 2>/dev/null; then
                CARD_MOUNTED_BY_US=1
                MOUNTPOINT="$OWN_MOUNT"
                log "Mounted by RetroGC at: $MOUNTPOINT"
                return 0
            fi
            err "Could not mount $dev"
            return 1
        fi

        sleep 0.25
        waited=$((waited + 1))
    done

    return 1
}

# Waits for the card to be removed. When the game ends with the card still
# inserted, EmulationStation is started again.
wait_for_removal() {
    local dev="$1"
    log "Waiting for removal..."

    while [[ -e "$dev" ]] && find_card_device >/dev/null; do
        if (( GAME_RUNNING )) && ! kill -0 "$GAME_PID" 2>/dev/null; then
            wait "$GAME_PID" 2>/dev/null
            log "The game exited (code $?), starting EmulationStation"
            GAME_PID=""
            GAME_RUNNING=0
            es_ctl start
        fi
        # Something (automounter, Recalbox) may mount it read-write again.
        remount_read_only "$dev" "mounted read-write again"
        sleep 0.5
    done
}

cleanup_daemon() {
    if (( GAME_RUNNING )); then
        stop_game
        es_ctl start
    fi

    if (( CARD_MOUNTED_BY_US )); then
        umount "$OWN_MOUNT" 2>/dev/null || umount -l "$OWN_MOUNT" 2>/dev/null
    fi

    rm -f "$PIDFILE"
    rm -rf "$LOCKDIR"
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
    log "RetroGC Launcher watching for label '$LABEL'"

    local dev
    while true; do
        until dev="$(find_card_device)"; do
            sleep 0.5
        done
        log "Device detected: $dev"

        if wait_for_mount "$dev"; then
            on_insert "$MOUNTPOINT"
            wait_for_removal "$dev"
            on_remove
        else
            log "RETROGC was removed before it could be mounted"
            # Don't spin on a stick that can't be mounted
            while [[ -e "$dev" ]] && find_card_device >/dev/null; do
                sleep 0.5
            done
        fi
        echo
    done
}

# ------------------------------------------------------------
# Entry point
# ------------------------------------------------------------

case "${1:-}" in
    --daemon)
        daemon_main
        ;;
    --enable)
        install_startup_hook
        start_daemon
        ;;
    --disable)
        remove_startup_hook
        stop_daemon
        ;;
    --status)
        if is_running; then
            echo "RetroGC Launcher is running (PID $(cat "$PIDFILE")). Log: $LOGFILE"
        else
            echo "RetroGC Launcher is not running."
        fi
        if [[ -f "$STARTUP" ]] && grep -qF "$START_MARK" "$STARTUP"; then
            echo "Enabled at boot ($STARTUP)."
        else
            echo "Not enabled at boot."
        fi
        ;;
    "")
        if is_running; then
            remove_startup_hook
            stop_daemon
        else
            install_startup_hook
            start_daemon
        fi
        ;;
    -h|--help)
        sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        echo "Unknown option: $1 (see --help)"
        exit 1
        ;;
esac
