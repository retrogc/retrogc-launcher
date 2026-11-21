RetroGC MiSTer background launcher
=================================

Install the file "retrogc.sh" to :

  /media/fat/Scripts/retrogc.sh

Then make it executable.

The file retrogc.sh is the file you select from:

  Main menu -> Scripts -> retrogc.sh

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

The card is kept mounted read-only while it is inserted (pulling it out
then never leaves it "dirty" for Windows). Every step is logged to:

  /tmp/retrogc-launcher.log

Updating retrogc.sh: a running watcher keeps the old code. After copying
the new file, select retrogc.sh twice (OFF, then ON) or reboot. The first
log line shows the running version.
