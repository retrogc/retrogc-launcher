# Retro Game Card

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