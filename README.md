![](Data/retrogc-256.png)

# 🎮 Retro Game Card

[![Platforms](https://img.shields.io/badge/Platforms-Windows%20%7C%20Linux%20%7C%20MiSTer%20FPGA-blueviolet)](https://github.com/Cpasjuste/RetroGameCard)
[![Language](https://img.shields.io/badge/Language-C%23-blue)](https://github.com/Cpasjuste/RetroGameCard)

RetroGameCard is a simple portable "card" format for USB drives / SD cards that lets a single, self-contained game be 
automatically detected and launched by the RetroGameCard C# launcher (Windows and Linux). It is
also compatible with MiSTer FPGA via the provided `retrogc-launcher.sh` helper script.

The Windows / Linux launcher offers a clean UI with icons and visual indicators for inserted cards, autostart state, and runner status,
think retro cartridge shelf but with modern polish. Use the UI to see which card is inserted, view cover/box art and
such, making your computer works like an old gaming console.

---

## ✨ Key ideas

- One game per RetroGameCard. Each card is self-contained: all required files for that single game live on the card.
- When the RetroGameCard launcher is running on the host system, inserting a RetroGameCard will automatically detect it
  and autoload the game.
- Great for game preservation and for building a physical-style collection similar to old console cartridges.

---

## 🛠️ Supported platforms

- Windows (x64)
- Linux (x64)
- MiSTer FPGA

---

## 🚀 Quick start (Windows / Linux)

1. Download RetroGameCard launcher: [retrogc-launcher](https://github.com/retrogc/retrogc-launcher/releases)
2. Run RetroGameCard launcher
3. Let the launcher download required data (retroarch)
4. Insert a RetroGameCard (sdcard, usb pen, etc...) and have fun !

## 🚀 Quick start (MisterFPGA)
1. Download [retrogc-launcher.sh](https://raw.githubusercontent.com/retrogc/retrogc-launcher/refs/heads/main/Platforms/MisterFPGA/retrogc-launcher.sh) and [retrogc-launcher-daemon.sh](https://raw.githubusercontent.com/retrogc/retrogc-launcher/refs/heads/main/Platforms/MisterFPGA/retrogc-launcher-daemon.sh)
2. Copy the files to MisterFPGA script directory: `/media/fat/Scripts/`
3. Load `retrogc-launcher.sh` from Main menu > Scripts
3. More information: [readme-misterfpga.txt](https://github.com/retrogc/retrogc-launcher/blob/main/Platforms/MisterFPGA/readme-misterfpga.txt)
---

## What belongs on a RetroGameCard

- A RetroGameCard must contain a configuration file at the root named with the `.rgc` extension (for example:
  `mygame.rgc`). This file describes the card and how to run the game. It also contains all required game files: roms, bios (and cores for MisterFPGA)
- Example .rgc cards config files are provided in the [Cards](https://github.com/retrogc/retrogc-launcher/tree/main/Cards) folder, use those as your authoritative examples and
  templates. Alternatively, you'll find some complete and prebuilt cards in [retrogc-gamecards](https://github.com/retrogc/retrogc-gamecards) repository.

## Covers

- The launcher supports a game cover. If you want your card to show a
  cover, include a `cover.png` in the card root and reference it from the `.rgc`.

## How auto-launch works

- The launcher monitors removable drives. When it detects a card/disk labeled "RETROGC" with an `.rgc` config at the
  root, it reads the config, displays the card details in the UI, and launches the game using RetroArch.
- The launcher is cross-platform and supports both Windows and Linux.

## Creating and testing a RetroGameCard

1. Copy one of the `.rgc` config file example from `Cards` folder and adapt it for your game.
2. Place the `.rgc` file at the root of the card folder and add all required game files.
3. Use the Linux [retrogc-image.sh](https://github.com/retrogc/retrogc-launcher/blob/main/Platforms/Linux/retrogc-image.sh) script to create a card image or to write the contents to a target device.
4. Run the C# launcher on your host. Insert the SD/USB device and the launcher will detect and autostart the game.

## Building a card

- The repository includes the Linux [retrogc-image.sh](https://github.com/retrogc/retrogc-launcher/blob/main/Platforms/Linux/retrogc-image.sh) script to:
    - Build an image from a game folder (folder → image).
    - Restore an image to an SD card or USB drive (image → device).
- Example .rgc cards config files are provided in the [Cards](https://github.com/retrogc/retrogc-launcher/tree/main/Cards) folder, use those as your authoritative examples and
  templates. Alternatively, you'll find some complete and prebuilt cards in [retrogc-gamecards](https://github.com/retrogc/retrogc-gamecards) repository.


## Why use RetroGameCard?

- Simple, robust format for single-game cards that works across multiple platforms.
- Makes physical-style collections easy to manage and share.
- Helps with long-term preservation since each card is self-contained and easy to archive.

## 💡 Tips

- MisterFPGA
    - Find which core belong to a .mra arcade rom (cadillacs and dinosaurs):
      ```bash
      find /media/fat/_Arcade -type f -iname '*cadillacs*.mra' |
      while IFS= read -r file; do
        rbf=$(sed -n 's|.*<rbf>\([^<]*\)</rbf>.*|\1|p' "$file")
        zip=$(sed -n 's|.*zip="\([^"]*\)".*|\1|p' "$file")
        printf '%s\n\t rbf: %s\n\t zip: %s\n' "$file" "$rbf" "$zip"
      done
      ```

---

## Contributing

- If you want to add examples, improve the build scripts, or enhance the launcher UX (more icons, extra color themes, or
  screenshots), please open an issue or a pull request.

---

## License

See the project's LICENSE file for details.

---