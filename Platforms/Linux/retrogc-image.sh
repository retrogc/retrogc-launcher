#!/usr/bin/env bash
set -euo pipefail

VERSION="1.0"

# Colors
RED="\033[1;31m"; GREEN="\033[1;32m"; YELLOW="\033[1;33m"
BLUE="\033[1;34m"; CYAN="\033[1;36m"; MAGENTA="\033[1;35m"
WHITE="\033[1;37m"; RESET="\033[0m"

info()    { echo -e "${BLUE}ℹ${RESET} $*"; }
success() { echo -e "${GREEN}✔${RESET} $*"; }
warn()    { echo -e "${YELLOW}⚠${RESET} $*"; }
error()   { echo -e "${RED}✘${RESET} $*" >&2; exit 1; }
title()   { echo -e "\n${MAGENTA}════════════════════════════════════${RESET}\n$*\n${MAGENTA}════════════════════════════════════${RESET}\n"; }

usage() {
    echo
    echo -e "${WHITE}RETROGC FAT32 Image Builder / Restorer${RESET}"
    echo
    echo "Build:   $0 --build <folder> <output.img.xz>"
    echo "Restore: $0 --restore <image.img.xz> <device>"
    echo
    echo "Examples:"
    echo "  $0 --build ~/roms cps1.img.xz"
    echo "  $0 --restore cps1.img.xz /dev/sdb"
    echo
}

[[ $# -ge 1 ]] || { usage; exit 1; }

MODE="$1"

case "$MODE" in
    --build)
        [[ $# -eq 3 ]] || error "Usage: $0 --build <folder> <output.img.xz>"
        SRC="$(realpath "$2")"
        OUTPUT="$(realpath "$3")"
        [[ -d "$SRC" ]] || error "Source folder does not exist: $SRC"
        ;;
    --restore)
        [[ $# -eq 3 ]] || error "Usage: $0 --restore <image.img.xz> <device>"
        IMAGE="$2"
        DEVICE="$3"
        [[ -f "$IMAGE" ]] || error "Image file does not exist: $IMAGE"
        [[ -b "$DEVICE" ]] || error "Not a block device: $DEVICE"
        ;;
    -h|--help)
        usage
        exit 0
        ;;
    *)
        error "Unknown option: $MODE"
        ;;
esac

# ============================================================
# BUILD
# ============================================================

if [[ "$MODE" == "--build" ]]; then
    title "🎮 RETROGC FAT32 Image Builder v$VERSION"

    for cmd in parted mkfs.fat mount umount rsync xz truncate losetup; do
        command -v "$cmd" >/dev/null || error "Missing dependency: $cmd"
    done
    success "Dependencies OK"

    info "Calculating source size..."
    SIZE_BYTES=$(du -sb "$SRC" | awk '{print $1}')
    # Add safety overhead (16 MB) for FAT32 metadata, structures, and cluster slack
    OVERHEAD_BYTES=$((16 * 1024 * 1024))
    TOTAL_BYTES=$((SIZE_BYTES + OVERHEAD_BYTES))
    
    # Convert to MB with ceiling division
    SIZE_MB=$(( (TOTAL_BYTES + 1024 * 1024 - 1) / (1024 * 1024) ))
    
    # Absolute minimum FAT32 size (32 MB recommended to satisfy FAT32 spec limits)
    MIN_MB=32
    (( SIZE_MB < MIN_MB )) && SIZE_MB=$MIN_MB
    
    # Round UP to the nearest multiple of 8 MB
    SIZE_MB=$(( (SIZE_MB + 7) / 8 * 8 ))
    
    success "Image size: ${CYAN}${SIZE_MB} MiB${RESET}"

    WORKDIR=$(mktemp -d)
    IMAGE="$WORKDIR/retrogc.img"
    MOUNT="$WORKDIR/mount"
    LOOP=""

    mkdir "$MOUNT"

    cleanup() {
        mountpoint -q "$MOUNT" 2>/dev/null && sudo umount "$MOUNT" 2>/dev/null || true
        [[ -n "$LOOP" ]] && sudo losetup -d "$LOOP" 2>/dev/null || true
        rm -rf "$WORKDIR"
    }
    trap cleanup EXIT

    info "Creating image..."
    truncate -s "${SIZE_MB}M" "$IMAGE"

    info "Creating partition table..."
    parted -s "$IMAGE" mklabel msdos
    parted -s "$IMAGE" mkpart primary fat32 1MiB 100%
    success "Partition created"

    info "Attaching loop device..."
    LOOP=$(sudo losetup --find --show --partscan "$IMAGE")
    PART="${LOOP}p1"
    sleep 1

    info "Formatting FAT32..."
    sudo mkfs.fat -F32 -n RETROGC "$PART"
    success "Filesystem created"

    info "Copying files..."
    sudo mount "$PART" "$MOUNT"
    sudo rsync -rlt --info=progress2 "$SRC/" "$MOUNT/"
    sync
    sudo umount "$MOUNT"

    sudo losetup -d "$LOOP"
    LOOP=""
    success "Image generated"

    info "Compressing with xz..."
    xz -T0 -9 -c "$IMAGE" > "$OUTPUT"

    COMPRESSED_SIZE=$(du -h "$OUTPUT" | awk '{print $1}')

    title "🎉 BUILD COMPLETE!"
    echo -e "${GREEN}Output:${RESET}          $OUTPUT"
    echo -e "${GREEN}Compressed size:${RESET} $COMPRESSED_SIZE"
    echo -e "${GREEN}Image size:${RESET}      ${SIZE_MB} MiB"
    echo -e "${GREEN}Filesystem:${RESET}      FAT32"
    echo -e "${GREEN}Label:${RESET}           RETROGC"
    exit 0
fi

# ============================================================
# RESTORE
# ============================================================

if [[ "$MODE" == "--restore" ]]; then
    title "💾 RETROGC Image Restore v$VERSION"

    for cmd in xzcat dd lsblk; do
        command -v "$cmd" >/dev/null || error "Missing dependency: $cmd"
    done
    success "Dependencies OK"

    echo -e "${YELLOW}Target device:${RESET} ${RED}${DEVICE}${RESET}"
    echo
    lsblk "$DEVICE"
    echo

    warn "THIS WILL COMPLETELY OVERWRITE ${DEVICE}!"
    warn "ALL DATA ON THIS DEVICE WILL BE LOST!"
    echo

    read -r -p "$(echo -e "${YELLOW}Type YES to continue: ${RESET}")" CONFIRM
    [[ "$CONFIRM" == "YES" ]] || {
        warn "Restore cancelled."
        exit 0
    }

    info "Unmounting target partitions..."
    while read -r PART; do
        [[ -n "$PART" ]] && sudo umount -f "$PART" 2>/dev/null || true
    done < <(lsblk -lnpo NAME "$DEVICE" | tail -n +2)

    info "Restoring image to ${CYAN}${DEVICE}${RESET}..."
    xzcat "$IMAGE" | sudo dd of="$DEVICE" bs=4M status=progress conv=fsync
    sync

    title "🎉 RESTORE COMPLETE!"
    echo -e "${GREEN}Image:${RESET}  $IMAGE"
    echo -e "${GREEN}Device:${RESET} $DEVICE"
    exit 0
fi
