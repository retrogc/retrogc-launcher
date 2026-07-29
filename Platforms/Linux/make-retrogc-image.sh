#!/usr/bin/env bash

set -euo pipefail

# ------------------------------------------------------------
# RETROGC FAT32 image builder
# ------------------------------------------------------------

VERSION="1.0"

# Colors
RED="\033[1;31m"
GREEN="\033[1;32m"
YELLOW="\033[1;33m"
BLUE="\033[1;34m"
CYAN="\033[1;36m"
MAGENTA="\033[1;35m"
RESET="\033[0m"

info()    { echo -e "${BLUE}ℹ${RESET} $*"; }
success() { echo -e "${GREEN}✔${RESET} $*"; }
warn()    { echo -e "${YELLOW}⚠${RESET} $*"; }
error()   { echo -e "${RED}✘${RESET} $*" >&2; exit 1; }
title()   { echo -e "\n${MAGENTA}════════════════════════════════════${RESET}\n$*\n${MAGENTA}════════════════════════════════════${RESET}\n"; }

# ------------------------------------------------------------
# Arguments
# ------------------------------------------------------------

if [[ $# -ne 2 ]]; then
    echo
    echo "Usage:"
    echo "  $0 <folder_to_copy> <output.img.xz>"
    echo
    echo "Example:"
    echo "  $0 ~/roms cps1.img.xz"
    exit 1
fi

SRC="$(realpath "$1")"
OUTPUT="$(realpath "$2")"

[[ -d "$SRC" ]] || error "Source folder does not exist: $SRC"

title "🎮 RETROGC FAT32 Image Builder v$VERSION"

# ------------------------------------------------------------
# Dependencies
# ------------------------------------------------------------

for cmd in parted mkfs.fat mount umount rsync xz truncate; do
    command -v "$cmd" >/dev/null || error "Missing dependency: $cmd"
done

success "Dependencies OK"

# ------------------------------------------------------------
# Calculate image size
# ------------------------------------------------------------

info "Calculating source size..."
SIZE_BYTES=$(du -sb "$SRC" | awk '{print $1}')
# Add 25% free space
SIZE_BYTES=$((SIZE_BYTES + SIZE_BYTES / 4))
# Minimum size
MIN_MB=64
SIZE_MB=$(( (SIZE_BYTES / 1024 / 1024) + 1 ))
if (( SIZE_MB < MIN_MB )); then
    SIZE_MB=$MIN_MB
fi
# Round to multiple of 16MB
SIZE_MB=$(( ((SIZE_MB + 15) / 16) * 16 ))
# Add FAT overhead
SIZE_MB=$((SIZE_MB + 16))

success "Image size: ${CYAN}${SIZE_MB} MiB${RESET}"

# ------------------------------------------------------------
# Temporary files
# ------------------------------------------------------------

WORKDIR=$(mktemp -d)
IMAGE="$WORKDIR/retrogc.img"
MOUNT="$WORKDIR/mount"

mkdir "$MOUNT"

cleanup()
{
    if mountpoint -q "$MOUNT"; then
        sudo umount "$MOUNT"
    fi
    rm -rf "$WORKDIR"
}

trap cleanup EXIT

# ------------------------------------------------------------
# Create empty image
# ------------------------------------------------------------

info "Creating empty image..."
truncate -s "${SIZE_MB}M" "$IMAGE"

# ------------------------------------------------------------
# Partition
# ------------------------------------------------------------

info "Creating partition table..."
parted -s "$IMAGE" mklabel msdos
parted -s "$IMAGE" mkpart primary fat32 1MiB 100%
success "Partition created"

# ------------------------------------------------------------
# Setup loop device
# ------------------------------------------------------------

info "Attaching loop device..."
LOOP=$(sudo losetup --find --show --partscan "$IMAGE")
PART="${LOOP}p1"
trap 'sudo losetup -d "$LOOP" 2>/dev/null || true; cleanup' EXIT

sleep 1

# ------------------------------------------------------------
# Format FAT32
# ------------------------------------------------------------

info "Formatting FAT32..."
sudo mkfs.fat -F32 -n RETROGC "$PART"
success "Filesystem created"

# ------------------------------------------------------------
# Copy files
# ------------------------------------------------------------

info "Copying files..."
sudo mount "$PART" "$MOUNT"
sudo rsync  -rlt --info=progress2  "$SRC/" "$MOUNT/"
sync

sudo umount "$MOUNT"

# ------------------------------------------------------------
# Detach loop
# ------------------------------------------------------------

sudo losetup -d "$LOOP"
success "Image generated"

# ------------------------------------------------------------
# Compress
# ------------------------------------------------------------

info "Compressing with xz..."
xz -T0 -9 -c "$IMAGE" > "$OUTPUT"

SIZE=$(du -h "$OUTPUT" | awk '{print $1}')

title "🎉 DONE!"

echo -e "${GREEN}Output:${RESET} $OUTPUT"
echo -e "${GREEN}Compressed size:${RESET} $SIZE"
echo -e "${GREEN}Label:${RESET} RETROGC"
echo
