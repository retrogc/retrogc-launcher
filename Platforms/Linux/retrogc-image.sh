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
    echo -e "${WHITE}RETROGC FAT32 Image Builder / Restorer / Extractor${RESET}"
    echo
    echo "Build:   $0 --build <folder> <output.img.xz>"
    echo "Restore: $0 --restore <image.img.xz> <device>"
    echo "Extract: $0 --extract <image.img.xz> <output-folder>"
    echo
    echo "Examples:"
    echo "  $0 --build ~/roms cps1.img.xz"
    echo "  $0 --restore cps1.img.xz /dev/sdb"
    echo "  $0 --extract cps1.img.xz ./extracted/cps1"
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
    --extract)
        [[ $# -eq 3 ]] || error "Usage: $0 --extract <image.img.xz> <output-folder>"

        IMAGE="$(realpath "$2")"
        EXTRACT_DIR="$3"

        [[ -f "$IMAGE" ]] || error "Image file does not exist: $IMAGE"
        [[ -n "$EXTRACT_DIR" ]] || error "Output folder must not be empty"
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

    for cmd in parted mkfs.fat mount umount rsync xz truncate mcopy; do
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
    
    # Absolute minimum FAT32 size (64 MB recommended to satisfy FAT32 spec limits)
    MIN_MB=64
    (( SIZE_MB < MIN_MB )) && SIZE_MB=$MIN_MB
    
    # Round UP to the nearest multiple of 8 MB
    SIZE_MB=$(( (SIZE_MB + 7) / 8 * 8 ))
    
    success "Image size: ${CYAN}${SIZE_MB} MiB${RESET}"

    WORKDIR=$(mktemp -d)
    IMAGE="$WORKDIR/retrogc.img"

    cleanup() {
        rm -rf "$WORKDIR"
    }
    trap cleanup EXIT

    info "Creating image..."
    truncate -s "${SIZE_MB}M" "$IMAGE"

    info "Creating partition table..."
    parted -s "$IMAGE" mklabel msdos
    parted -s "$IMAGE" mkpart primary fat32 1MiB 100%
    success "Partition created"
    
    info "Formatting FAT32 partition..."
    # Partition begins at 1 MiB = sector 2048.
    OFFSET_SECTORS=2048
    OFFSET_BYTES=$((OFFSET_SECTORS * 512))
    IMAGE_BYTES=$((SIZE_MB * 1024 * 1024))
    PARTITION_BLOCKS=$(((IMAGE_BYTES - OFFSET_BYTES) / 1024))

    # Format the partition directly inside the image file.
    mkfs.fat \
        --offset="$OFFSET_SECTORS" \
        -F 32 \
        -n RETROGC \
        "$IMAGE" \
        "$PARTITION_BLOCKS" \
        > /dev/null

    success "FAT32 filesystem created"

    info "Copying files..."
    shopt -s dotglob nullglob
    FILES=("$SRC"/*)
    shopt -u dotglob nullglob

    if [[ ${#FILES[@]} -gt 0 ]]; then
        mcopy -s \
            -i "${IMAGE}@@${OFFSET_BYTES}" \
            "${FILES[@]}" \
            ::/
    fi
    success "Files copied"   

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

# ============================================================
# EXTRACT
# ============================================================

if [[ "$MODE" == "--extract" ]]; then
    title "📂 RETROGC Image Extractor v$VERSION"

    for cmd in xz mcopy find mktemp realpath; do
        command -v "$cmd" >/dev/null || error "Missing dependency: $cmd"
    done
    success "Dependencies OK"

    # Require an empty destination to avoid overwriting existing files.
    if [[ -e "$EXTRACT_DIR" && ! -d "$EXTRACT_DIR" ]]; then
        error "Output path exists and is not a directory: $EXTRACT_DIR"
    fi

    mkdir -p -- "$EXTRACT_DIR"
    EXTRACT_DIR="$(realpath "$EXTRACT_DIR")"

    if [[ -n "$(find "$EXTRACT_DIR" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
        error "Output folder must be empty: $EXTRACT_DIR"
    fi

    WORKDIR="$(mktemp -d)"
    RAW_IMAGE="$WORKDIR/retrogc.img"

    cleanup() {
        rm -rf -- "$WORKDIR"
    }
    trap cleanup EXIT

    info "Decompressing image..."
    xz --decompress --stdout -- "$IMAGE" > "$RAW_IMAGE"
    success "Image decompressed"

    # Images produced by --build have their FAT32 partition at 1 MiB.
    OFFSET_BYTES=$((2048 * 512))

    info "Extracting files to ${CYAN}${EXTRACT_DIR}${RESET}..."

    # Keep the wildcard quoted so mcopy expands it inside the image.
    mcopy -s \
        -i "${RAW_IMAGE}@@${OFFSET_BYTES}" \
        '::/*' \
        "$EXTRACT_DIR/"

    title "🎉 EXTRACTION COMPLETE!"
    echo -e "${GREEN}Image:${RESET}  $IMAGE"
    echo -e "${GREEN}Output:${RESET} $EXTRACT_DIR"

    exit 0
fi

