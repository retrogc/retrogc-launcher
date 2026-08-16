![](Data/retrogc-256.png)

# 🎮 Retro Game Card

[![Platforms](https://img.shields.io/badge/Platforms-Windows%20%7C%20Linux%20%7C%20MiSTer%20FPGA-blueviolet)](https://github.com/Cpasjuste/RetroGameCard)
[![Language](https://img.shields.io/badge/Language-C%23-blue)](https://github.com/Cpasjuste/RetroGameCard)

RetroGameCard is a simple, portable "card" format for SD cards / USB drives that lets a single, self-contained game
(retroarch + rom) be automatically detected and launched by the RetroGameCard C# launcher (Windows and Linux). It is
also compatible with MiSTer FPGA via the provided `retrogc.sh` helper script.

The launcher offers a clean UI with icons and visual indicators for inserted cards, autostart state, and runner status,
think retro cartridge shelf but with modern polish. Use the UI to see which card is inserted, view cover/box art and
such, making your computer works like an old gaming console.

---

## ✨ Key ideas

- One game per RetroGameCard. Each card is self-contained: all required files for that single game live on the card.
- When the RetroGameCard launcher is running on the host system, inserting a RetroGameCard will automatically detect it
  and autoload the game.
- The launcher provides a simple, clean UI that shows which RetroGameCard is inserted and the current game state, with
  icons, colors, and optional cover art.
- Great for game preservation and for building a physical-style collection similar to old console cartridges.

---

## 🛠️ Supported platforms

- Windows (x64)
- Linux (x64)
- MiSTer FPGA

---

## 🚀 Quick start

1. Install RetroArch on Windows or Linux before using this tool:
    - Website: https://www.retroarch.com
    - Windows: download the installer or prebuilt binaries from the site
    - Linux: install via your distro package manager or download from the site
2. Run RetroGameCard launcher (should be found in any RetroGameCard inserted under `/app` directory)
3. Insert a RetroGameCard (sdcard, usb pen, etc...) and have fun !

---

## What belongs on a RetroGameCard

- A RetroGameCard must contain a configuration file at the root named with the `.rgc` extension (for example:
  `mygame.rgc`). This file describes the card and how to run the game.
- Example .rgc cards config files are provided in the `GameCards/` folder, use those as your authoritative examples and
  templates.

## Scripts and tooling

- The repository includes Linux scripts to:
    - Build a RetroGameCard image from a game folder (folder → image).
    - Restore an image to an SD card or USB drive (image → device).
- There is also a `retrogc.sh` script to support MiSTer FPGA workflows, it integrates a RetroGameCard into MiSTer setups
  where appropriate.
- Check the `GameCards/` folder and the `scripts/` (or top-level scripts) for the exact usage examples and parameters.

## Visuals, icons and screenshots

- The launcher supports icons (small status icons) and color highlights in the UI. If you want your card to show a
  cover, include a `cover.png` (or `cover.jpg`) in the card root and reference it from the `.rgc`.

## How auto-launch works

- The launcher monitors removable drives. When it detects a card/disk labeled "RETROGC" with an `.rgc` config at the
  root, it reads the config, displays the card details in the UI, and launches the game using RetroArch.
- The launcher is cross-platform and supports both Windows and Linux.

## Creating and testing a RetroGameCard

1. Copy one of the `.rgc` config file example from `GameCards/` and adapt it for your game.
2. Place the `.rgc` file at the root of the card image or folder and add all required game files.
3. Use the included build script to create a card image or to write the contents to a target device.
4. Run the C# launcher on your host. Insert the SD/USB device and the launcher will detect and autostart the game.

## Why use RetroGameCard?

- Simple, robust format for single-game cards that works across multiple platforms.
- Makes physical-style collections easy to manage and share.
- Helps with long-term preservation since each card is self-contained and easy to archive.

## Where to look next

- `GameCards/` - Real examples of .rgc files and game layouts.
- `Platforms/` - Build and restore helpers; `retrogc.sh` for MiSTer compatibility.
- Launcher code (C#) - For advanced configuration and to see how detection and autostarting are implemented.

## 💡 Tips

- Remove unused retroarch (windows) assets to lower game cards size
    - database → remove the whole folder
    - shaders → remove the whole folder
    - assets → ozone → keep only this folder
    - overlays → remove the whole folder

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
- For changes to example game cards, add/modify a folder under `GameCards/` with a clear README and a valid `.rgc` file.

---

## License

See the project's LICENSE file for details (if present).

---

If you'd like, I can further style the README with more icons, add sample screenshots, or include a ready-made GitHub
Actions workflow to produce release binaries. Would you like any of these additions?