#!/usr/bin/env bash
set -euo pipefail

VERSION="1.1"

LABEL="RETROGC"
LAUNCHER_REPO="https://github.com/retrogc/retrogc-launcher"
DATA_MANIFEST_URL="https://raw.githubusercontent.com/retrogc/retrogc-installer/refs/heads/main/retrogc-data.rgc"

# Partition starts at 1 MiB = sector 2048.
OFFSET_SECTORS=2048
OFFSET_BYTES=$((OFFSET_SECTORS * 512))

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
    echo -e "${WHITE}RETROGC Image Builder / Restorer / Extractor${RESET}"
    echo
    echo "Build:     $0 --build <folder> <output.img.xz>"
    echo "Installer: $0 --build-installer <output.img.xz> [options]"
    echo "Restore:   $0 --restore <image.img.xz> <device>"
    echo "Extract:   $0 --extract <image.img.xz> <output-folder>"
    echo "List:      $0 --list"
    echo
    echo "Installer card options (default: latest launcher and data releases, exFAT):"
    echo "  --fat32                     FAT32 instead of exFAT (no sudo needed, but Linux"
    echo "                              can't run the launcher straight from the card)"
    echo "  --launcher-linux <file>     use a local Linux launcher binary"
    echo "  --launcher-windows <file>   use a local Windows launcher .exe"
    echo "  --launcher-version <X.Y.Z>  launcher version written to the manifest"
    echo "  --data <retrogc-data.rgc>   use a local data manifest (archives next to it)"
    echo
    echo "Examples:"
    echo "  $0 --list"
    echo "  $0 --build ~/roms cps1.img.xz"
    echo "  $0 --build-installer retrogc-installer.img.xz"
    echo "  $0 --restore cps1.img.xz /dev/sdb"
    echo "  $0 --extract cps1.img.xz ./extracted/cps1"
    echo
}

need() {
    local cmd
    for cmd in "$@"; do
        command -v "$cmd" >/dev/null || error "Missing dependency: $cmd"
    done
}

# ------------------------------------------------------------
# Cleanup (temporary folder, loop mount, loop device)
# ------------------------------------------------------------

WORKDIR=""
MNT_DIR=""
LOOP_DEV=""

cleanup() {
    if [[ -n "$MNT_DIR" ]] && mountpoint -q "$MNT_DIR" 2>/dev/null; then
        sudo umount "$MNT_DIR" || true
    fi
    if [[ -n "$LOOP_DEV" ]]; then
        sudo losetup -d "$LOOP_DEV" || true
    fi
    if [[ -n "$WORKDIR" ]]; then
        rm -rf -- "$WORKDIR"
    fi
}
trap cleanup EXIT

# ------------------------------------------------------------
# Image helpers
# ------------------------------------------------------------

# Image size in MiB for the content of <dir>.
image_size_mb() {
    local size_bytes total_bytes size_mb
    size_bytes=$(du -sb "$1" | awk '{print $1}')
    # Safety overhead (16 MiB) for filesystem metadata and cluster slack
    total_bytes=$((size_bytes + 16 * 1024 * 1024))
    size_mb=$(( (total_bytes + 1024 * 1024 - 1) / (1024 * 1024) ))

    # Absolute minimum (64 MiB satisfies the FAT32 spec limits)
    if (( size_mb < 64 )); then
        size_mb=64
    fi

    # Round up to a multiple of 8 MiB
    echo $(( (size_mb + 7) / 8 * 8 ))
}

# create_partitioned_image <img> <size-mb> <parted-fs-type>
create_partitioned_image() {
    info "Creating image..."
    truncate -s "${2}M" "$1"

    info "Creating partition table..."
    parted -s "$1" mklabel msdos
    parted -s "$1" mkpart primary "$3" 1MiB 100%
    success "Partition created"
}

# create_fat32_image <src-dir> <img> <size-mb>: no root needed (mtools).
create_fat32_image() {
    local src="$1" image="$2" size_mb="$3"
    local files

    create_partitioned_image "$image" "$size_mb" fat32

    info "Formatting FAT32 partition..."
    # Format the partition directly inside the image file.
    mkfs.fat \
        --offset="$OFFSET_SECTORS" \
        -F 32 \
        -n "$LABEL" \
        "$image" \
        $(( (size_mb * 1024 * 1024 - OFFSET_BYTES) / 1024 )) \
        > /dev/null
    success "FAT32 filesystem created"

    info "Copying files..."
    shopt -s dotglob nullglob
    files=("$src"/*)
    shopt -u dotglob nullglob

    if [[ ${#files[@]} -gt 0 ]]; then
        mcopy -s -i "${image}@@${OFFSET_BYTES}" "${files[@]}" ::/
    fi
    success "Files copied"
}

# create_exfat_image <src-dir> <img> <size-mb>: needs sudo (loop device + mount).
create_exfat_image() {
    local src="$1" image="$2" size_mb="$3"
    local part

    # "ntfs" sets the MBR partition id 0x07, the one used by exFAT.
    create_partitioned_image "$image" "$size_mb" ntfs

    info "Attaching the image (sudo)..."
    LOOP_DEV="$(sudo losetup --find --show --partscan "$image")"
    part="${LOOP_DEV}p1"
    for _ in {1..50}; do
        [[ -b "$part" ]] && break
        sleep 0.1
    done
    [[ -b "$part" ]] || error "Partition device not found: $part"

    info "Formatting exFAT partition..."
    sudo mkfs.exfat -L "$LABEL" "$part" > /dev/null
    success "exFAT filesystem created"

    info "Copying files..."
    MNT_DIR="$WORKDIR/mnt"
    mkdir -p "$MNT_DIR"
    sudo mount -o "uid=$(id -u),gid=$(id -g)" "$part" "$MNT_DIR"
    cp -r -- "$src"/. "$MNT_DIR"/
    sync
    sudo umount "$MNT_DIR"
    sudo losetup -d "$LOOP_DEV"
    LOOP_DEV=""
    success "Files copied"
}

compress_image() {
    info "Compressing with xz..."
    xz -T0 -9 -c "$1" > "$2"
}

# ------------------------------------------------------------
# Installer card helpers
# ------------------------------------------------------------

download() {
    info "Downloading ${CYAN}$1${RESET}"
    curl -fL --progress-bar -o "$2" "$1" || error "Download failed: $1"
}

# Version of the latest launcher release ("v1.0.3" tag -> "1.0.3").
latest_launcher_version() {
    local tag
    tag="$(curl -fsSI "$LAUNCHER_REPO/releases/latest" \
        | awk 'tolower($1) == "location:" { n = split($2, p, "/"); print p[n] }' \
        | tr -d '\r')" || true
    [[ "$tag" == v* ]] || error "Could not find the latest launcher release"
    echo "${tag#v}"
}

# data_entry <manifest> <version> <platform> <field>: field of the retrogc-data entry.
data_entry() {
    jq -er --arg v "$2" --arg p "$3" --arg f "$4" \
        '.versions[] | select(.version == $v) | .platforms[$p].data[]
         | select(.name == "retrogc-data") | .[$f] // empty' "$1" 2>/dev/null \
        | head -n 1 || true
}

# ------------------------------------------------------------
# Arguments
# ------------------------------------------------------------

[[ $# -ge 1 ]] || { usage; exit 1; }

MODE="$1"

case "$MODE" in
    --build)
        [[ $# -eq 3 ]] || error "Usage: $0 --build <folder> <output.img.xz>"
        SRC="$(realpath "$2")"
        OUTPUT="$(realpath "$3")"
        [[ -d "$SRC" ]] || error "Source folder does not exist: $SRC"
        ;;
    --build-installer)
        [[ $# -ge 2 && "$2" != --* ]] || error "Usage: $0 --build-installer <output.img.xz> [options]"
        OUTPUT="$(realpath "$2")"
        shift 2

        FS="exfat"
        LAUNCHER_LINUX=""
        LAUNCHER_WINDOWS=""
        LAUNCHER_VERSION=""
        DATA_MANIFEST=""

        while [[ $# -gt 0 ]]; do
            case "$1" in
                --fat32)
                    FS="fat32"; shift ;;
                --launcher-linux|--launcher-windows|--launcher-version|--data)
                    [[ $# -ge 2 && -n "$2" ]] || error "Missing value for $1"
                    case "$1" in
                        --launcher-linux)   LAUNCHER_LINUX="$(realpath "$2")" ;;
                        --launcher-windows) LAUNCHER_WINDOWS="$(realpath "$2")" ;;
                        --launcher-version) LAUNCHER_VERSION="$2" ;;
                        --data)             DATA_MANIFEST="$(realpath "$2")" ;;
                    esac
                    shift 2 ;;
                *)
                    error "Unknown installer option: $1" ;;
            esac
        done

        [[ -z "$LAUNCHER_LINUX" || -f "$LAUNCHER_LINUX" ]] || error "Launcher not found: $LAUNCHER_LINUX"
        [[ -z "$LAUNCHER_WINDOWS" || -f "$LAUNCHER_WINDOWS" ]] || error "Launcher not found: $LAUNCHER_WINDOWS"
        [[ -z "$DATA_MANIFEST" || -f "$DATA_MANIFEST" ]] || error "Data manifest not found: $DATA_MANIFEST"
        if [[ -n "$LAUNCHER_VERSION" && ! "$LAUNCHER_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            error "Invalid launcher version (expected X.Y.Z): $LAUNCHER_VERSION"
        fi
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
    --list)
        [[ $# -eq 1 ]] || error "Usage: $0 --list"
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
# LIST
# ============================================================

if [[ "$MODE" == "--list" ]]; then
    title "🔌 RETROGC USB Drives v$VERSION"

    need lsblk awk

    # Whole disks only (-d), full paths (-p), filtered on USB transport.
    mapfile -t USB_DEVS < <(lsblk -dnpo NAME,TRAN | awk '$2=="usb"{print $1}')

    if [[ ${#USB_DEVS[@]} -eq 0 ]]; then
        warn "No USB drives found."
        exit 0
    fi

    lsblk -po NAME,SIZE,TYPE,FSTYPE,LABEL,VENDOR,MODEL,MOUNTPOINTS "${USB_DEVS[@]}"
    echo
    info "Restore with: $0 --restore <image.img.xz> /dev/sdX"
    exit 0
fi

# ============================================================
# BUILD
# ============================================================

if [[ "$MODE" == "--build" ]]; then
    title "🎮 RETROGC FAT32 Image Builder v$VERSION"

    need parted mkfs.fat xz truncate mcopy du awk
    success "Dependencies OK"

    info "Calculating source size..."
    SIZE_MB="$(image_size_mb "$SRC")"
    success "Image size: ${CYAN}${SIZE_MB} MiB${RESET}"

    WORKDIR=$(mktemp -d)
    create_fat32_image "$SRC" "$WORKDIR/retrogc.img" "$SIZE_MB"
    compress_image "$WORKDIR/retrogc.img" "$OUTPUT"
    COMPRESSED_SIZE=$(du -h "$OUTPUT" | awk '{print $1}')

    title "🎉 BUILD COMPLETE!"
    echo -e "${GREEN}Output:${RESET}          $OUTPUT"
    echo -e "${GREEN}Compressed size:${RESET} $COMPRESSED_SIZE"
    echo -e "${GREEN}Image size:${RESET}      ${SIZE_MB} MiB"
    echo -e "${GREEN}Filesystem:${RESET}      FAT32"
    echo -e "${GREEN}Label:${RESET}           $LABEL"
    exit 0
fi

# ============================================================
# BUILD INSTALLER CARD
# ============================================================

if [[ "$MODE" == "--build-installer" ]]; then
    title "💳 RETROGC Installer Card Builder v$VERSION"

    need curl jq unzip xz parted truncate du awk
    if [[ "$FS" == "fat32" ]]; then
        need mkfs.fat mcopy
    else
        need sudo losetup mkfs.exfat mount umount mountpoint
    fi
    success "Dependencies OK"

    WORKDIR=$(mktemp -d)
    CARD="$WORKDIR/card"
    mkdir -p "$CARD"

    # ---------------------------------------------------- launcher
    if [[ -z "$LAUNCHER_LINUX" || -z "$LAUNCHER_WINDOWS" ]]; then
        RELEASE_VERSION="$(latest_launcher_version)"
        success "Latest launcher release: ${CYAN}${RELEASE_VERSION}${RESET}"
        RELEASE_URL="$LAUNCHER_REPO/releases/download/v$RELEASE_VERSION"
    fi

    if [[ -n "$LAUNCHER_LINUX" ]]; then
        cp -- "$LAUNCHER_LINUX" "$CARD/retrogc-launcher"
    else
        download "$RELEASE_URL/retrogc-launcher-linux.zip" "$WORKDIR/launcher-linux.zip"
        unzip -o -q "$WORKDIR/launcher-linux.zip" retrogc-launcher -d "$CARD"
    fi
    chmod +x "$CARD/retrogc-launcher"

    if [[ -n "$LAUNCHER_WINDOWS" ]]; then
        cp -- "$LAUNCHER_WINDOWS" "$CARD/retrogc-launcher.exe"
    else
        download "$RELEASE_URL/retrogc-launcher-windows.zip" "$WORKDIR/launcher-windows.zip"
        unzip -o -q "$WORKDIR/launcher-windows.zip" retrogc-launcher.exe -d "$CARD"
    fi

    # A release version is only known for sure when both launchers come from that release.
    if [[ -z "$LAUNCHER_VERSION" && -z "$LAUNCHER_LINUX" && -z "$LAUNCHER_WINDOWS" ]]; then
        LAUNCHER_VERSION="$RELEASE_VERSION"
    fi
    if [[ -z "$LAUNCHER_VERSION" ]]; then
        warn "No --launcher-version: installed launchers won't be offered this one as an update"
    fi
    success "Launchers ready"

    # ---------------------------------------------------- data
    if [[ -n "$DATA_MANIFEST" ]]; then
        MANIFEST="$DATA_MANIFEST"
        MANIFEST_DIR="$(dirname "$DATA_MANIFEST")"
    else
        MANIFEST="$WORKDIR/online-manifest.rgc"
        MANIFEST_DIR=""
        download "$DATA_MANIFEST_URL" "$MANIFEST"
    fi

    DATA_VERSION="$(jq -er '.default' "$MANIFEST" 2>/dev/null)" \
        || error "No default version in $MANIFEST"
    [[ "$DATA_VERSION" =~ ^[A-Za-z0-9._-]+$ && "$DATA_VERSION" != .* ]] \
        || error "Invalid data version: $DATA_VERSION"
    success "Data version: ${CYAN}${DATA_VERSION}${RESET}"

    declare -A DATA_FILES=()
    for PLATFORM in win-x64 linux-x64; do
        FILE="$(data_entry "$MANIFEST" "$DATA_VERSION" "$PLATFORM" file)"
        URL="$(data_entry "$MANIFEST" "$DATA_VERSION" "$PLATFORM" url)"
        [[ -n "$FILE" || -n "$URL" ]] || error "No retrogc-data entry for $PLATFORM in version $DATA_VERSION"
        [[ -n "$FILE" ]] || FILE="$(basename "$URL")"
        [[ "$FILE" != */* && "$FILE" != .* ]] || error "Invalid data file name: $FILE"

        if [[ "$URL" =~ ^https?:// ]]; then
            if [[ -n "$MANIFEST_DIR" && -f "$MANIFEST_DIR/$FILE" ]]; then
                cp -- "$MANIFEST_DIR/$FILE" "$CARD/$FILE"
            else
                download "$URL" "$CARD/$FILE"
            fi
        else
            [[ -n "$MANIFEST_DIR" ]] || error "The online manifest references a local file: $URL"
            SOURCE="$MANIFEST_DIR/${URL:-$FILE}"
            [[ -f "$SOURCE" ]] || error "Data archive not found: $SOURCE"
            info "Copying ${CYAN}$SOURCE${RESET}"
            cp -- "$SOURCE" "$CARD/$FILE"
        fi
        DATA_FILES[$PLATFORM]="$FILE"
    done
    success "Data archives ready"

    # ---------------------------------------------------- manifest
    jq -n \
        --arg v "$DATA_VERSION" \
        --arg lv "$LAUNCHER_VERSION" \
        --arg wf "${DATA_FILES[win-x64]}" \
        --arg lf "${DATA_FILES[linux-x64]}" \
        'def data($f): {name: "retrogc-data", file: $f, url: $f};
         def launcher($f): {name: "retrogc-launcher"}
             + (if $lv == "" then {} else {version: $lv} end)
             + {file: $f, url: $f};
         {
           default: $v,
           versions: [{
             version: $v,
             platforms: {
               "win-x64":   {data: [data($wf), launcher("retrogc-launcher.exe")]},
               "linux-x64": {data: [data($lf), launcher("retrogc-launcher")]}
             }
           }]
         }' > "$CARD/retrogc-data.rgc"
    success "retrogc-data.rgc written"

    # ---------------------------------------------------- image
    info "Calculating card size..."
    SIZE_MB="$(image_size_mb "$CARD")"
    success "Image size: ${CYAN}${SIZE_MB} MiB${RESET}"

    if [[ "$FS" == "fat32" ]]; then
        create_fat32_image "$CARD" "$WORKDIR/retrogc.img" "$SIZE_MB"
    else
        create_exfat_image "$CARD" "$WORKDIR/retrogc.img" "$SIZE_MB"
    fi
    compress_image "$WORKDIR/retrogc.img" "$OUTPUT"
    COMPRESSED_SIZE=$(du -h "$OUTPUT" | awk '{print $1}')

    title "🎉 INSTALLER CARD COMPLETE!"
    echo -e "${GREEN}Output:${RESET}           $OUTPUT"
    echo -e "${GREEN}Compressed size:${RESET}  $COMPRESSED_SIZE"
    echo -e "${GREEN}Image size:${RESET}       ${SIZE_MB} MiB"
    echo -e "${GREEN}Filesystem:${RESET}       ${FS^^}"
    echo -e "${GREEN}Label:${RESET}            $LABEL"
    echo -e "${GREEN}Data version:${RESET}     $DATA_VERSION"
    echo -e "${GREEN}Launcher version:${RESET} ${LAUNCHER_VERSION:-unknown}"
    exit 0
fi

# ============================================================
# RESTORE
# ============================================================

if [[ "$MODE" == "--restore" ]]; then
    title "💾 RETROGC Image Restore v$VERSION"

    need xzcat dd lsblk
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

    need xz mcopy find mktemp realpath dd
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

    info "Decompressing image..."
    xz --decompress --stdout -- "$IMAGE" > "$RAW_IMAGE"
    success "Image decompressed"

    # mtools only reads FAT: exFAT installer cards must be loop-mounted instead.
    if [[ "$(dd if="$RAW_IMAGE" bs=1 skip=$((OFFSET_BYTES + 3)) count=5 2>/dev/null)" == "EXFAT" ]]; then
        error "exFAT images can't be extracted, mount it instead:
  xzcat $IMAGE > card.img && sudo mount -o loop,offset=$OFFSET_BYTES card.img /mnt"
    fi

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
