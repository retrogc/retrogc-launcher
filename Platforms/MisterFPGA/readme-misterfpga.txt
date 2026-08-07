RetroGC MiSTer background launcher
=================================

Install these files:

  /media/fat/Scripts/retrogc-launcher.sh
  /media/fat/Scripts/retrogc-launcher-daemon.sh

Then make both executable.

The file retrogc-launcher.sh is the file you select from:

  Main menu -> Scripts -> retrogc-launcher.sh

Selecting it toggles the background launcher.

ON:
  - installs a hook in /media/fat/linux/user-startup.sh
  - starts the watcher immediately
  - watcher automatically starts after every MiSTer boot

OFF:
  - removes the startup hook
  - stops the watcher
  - daemon cleanup removes the temporary fake core links and returns
    to menu.rbf

The watcher uses /tmp for its lock, PID and generated MGL, so those do
not persist across reboot.
