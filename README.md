# 🎴 Retro Game Card

[![Platforms](https://img.shields.io/badge/Platforms-Windows%20%7C%20Linux%20%7C%20MiSTer%20FPGA-blueviolet)](https://github.com/Cpasjuste/RetroGameCard)
[![Language](https://img.shields.io/badge/Language-C%23-blue)](https://github.com/Cpasjuste/RetroGameCard)

A small, fast tool to generate polished "game cards", playlists and metadata for retro gaming setups.

- 🎮 Generates playlists and game-card metadata for RetroArch and MiSTer-style setups.
- 🖥️ Works on Windows and Linux desktop systems.
- 🕹️ Compatible with MiSTer FPGA file/playlists workflows.

---

## ✨ What it does

Retro Game Card scans your ROM folders and generates tidy, consistent game cards (playlists, artwork links and metadata) so your frontends (RetroArch, MiSTer, etc.) show games with proper titles and artwork. It helps automate naming, playlist creation and artwork linking so you can browse your collection cleanly.

---

## 🛠️ Supported platforms

- Windows
- Linux
- MiSTer FPGA (transfer generated files to your MiSTer storage/SD)

Note: On Windows and Linux this tool prepares files for RetroArch. RetroArch is required to actually run the games from the generated playlists.

---

## ✅ Prerequisites

1. Install RetroArch on Windows or Linux before using this tool:
   - Website: https://www.retroarch.com
   - Windows: download the installer or prebuilt binaries from the site
   - Linux: install via your distro package manager or download from the site
2. Have your ROMs and optional artwork organized in folders (recommended: per system/emulator).
3. (Optional) For MiSTer: have access to your MiSTer SD/HDD to copy generated files.

---

## 🚀 Quick start

1. Clone the repository:

```bash
git clone https://github.com/Cpasjuste/RetroGameCard.git
cd RetroGameCard
```

2. Build or run the program. Depending on the provided artifacts this project is C# based; a typical dotnet run or a bundled executable may be provided:

- Using dotnet (if project is a dotnet app):

```bash
dotnet build
dotnet run -- --input "/path/to/roms" --output "/path/to/output/playlists"
```

- Using an included executable (Windows example):

```powershell
RetroGameCard.exe --input "C:\roms" --output "C:\RetroArch\playlists"
```

- Using a Linux executable:

```bash
./RetroGameCard --input "/home/user/roms" --output "/home/user/.config/retroarch/playlists"
```

Replace --input and --output with the correct paths for your setup. Run the program with --help to list available options and flags.

3. Copy the generated files to your frontend:
- RetroArch (Windows/Linux): copy playlists/artwork to your RetroArch playlists/content directory or import them from RetroArch's menu.
- MiSTer: copy generated files to your MiSTer SD or HDD in the folders used by your MiSTer setup (commonly under a _Games or ROMs directory).

---

## 🎨 Artwork and naming

- Place artwork files next to ROMs (or in a dedicated artwork folder) and Retro Game Card will try to link them automatically.
- Filenames are normalized by the tool, but you can preview the generated output before overwriting your MiSTer or RetroArch folders.
- The tool supports common artwork types (png/jpg) and playlist metadata that RetroArch uses.

---

## 💡 Tips

- Back up your existing playlists before overwriting them.
- Use per-system folders to keep playlists clean and easy to import.
- Run the tool with a small sample first to verify naming/artwork behavior.

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
- Create a retro game card raw image backup (change device / size)
  - ```
    sudo parted /dev/sdX unit MiB print
    sudo truncate -s 256M && sudo dd if=/dev/sdX bs=4M status=progress | xz -T0 > cps1-cadillac-and-dinosaurs.img.xz
    ```
- Restore a retro game card raw image backup (change device / size)
  - ```
    sudo xzcat cps1-cadillac-and-dinosaurs.img.xz | sudo dd of=/dev/sdX bs=4M status=progress conv=fsync
    ```
---

## Contributing

Contributions and issues are welcome! If you'd like to add features (better artwork scraping, more playlist formats, per-core mapping for MiSTer), open an issue or submit a pull request with a clear description.

---

## License

See the project's LICENSE file for details (if present).

---

If you'd like, I can further style the README with more icons, add sample screenshots, or include a ready-made GitHub Actions workflow to produce release binaries. Would you like any of these additions?