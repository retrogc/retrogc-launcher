#!/usr/bin/env bash

DAEMON="/media/fat/Scripts/retrogc-launcher-daemon.sh"
STARTUP="/media/fat/linux/user-startup.sh"

PIDFILE="/tmp/retrogc-launcher.pid"
LOCKDIR="/tmp/retrogc-launcher.lock"

START_MARK="# >>> RetroGC Launcher >>>"
END_MARK="# <<< RetroGC Launcher <<<"


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

    cat >> "$STARTUP" <<EOF

$START_MARK
if [ -x "$DAEMON" ]; then
    "$DAEMON" >/tmp/retrogc-launcher.log 2>&1 &
fi
$END_MARK
EOF

    sync

    echo "RetroGC Launcher enabled."
}


remove_startup_hook() {
    [[ ! -f "$STARTUP" ]] && return 0

    sed -i \
        "/$START_MARK/,/$END_MARK/d" \
        "$STARTUP"

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

    if [[ ! -x "$DAEMON" ]]; then
        echo "ERROR: daemon not found or not executable:"
        echo "  $DAEMON"
        return 1
    fi

    "$DAEMON" >/tmp/retrogc-launcher.log 2>&1 &

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


if is_running; then
    remove_startup_hook
    stop_daemon
else
    install_startup_hook
    start_daemon
fi