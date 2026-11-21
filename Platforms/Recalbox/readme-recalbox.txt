RetroGC Recalbox background launcher
====================================

Watches for a USB stick labelled RETROGC: insert a game card and its game
starts, remove it and you are back in EmulationStation.

Install (over SSH, user root, password recalboxroot)
----------------------------------------------------

  mkdir -p /recalbox/share/system/retrogc
  copy retrogc.sh to /recalbox/share/system/retrogc/retrogc.sh
  chmod +x /recalbox/share/system/retrogc/retrogc.sh
  /recalbox/share/system/retrogc/retrogc.sh --enable

The script can also be copied over the network share:
  \\RECALBOX\share\system\retrogc\retrogc.sh

Do NOT put it in /recalbox/share/userscripts: Recalbox runs the scripts of that
folder on EmulationStation events.

Commands
--------

  retrogc.sh             toggle (enable / disable)
  retrogc.sh --enable    start the watcher now and at every boot
  retrogc.sh --disable   stop the watcher and remove the boot hook
  retrogc.sh --status    show whether the watcher is running

--enable adds a small marked block to /recalbox/share/system/custom.sh (the
Recalbox startup script); --disable removes it and leaves the rest of the file
untouched.

How games are started
---------------------

The card's "retroarch" section is used ("version" is ignored, Recalbox ships
its own RetroArch):

  - with "retroarch.system" (Recalbox system name: nes, snes, neogeo, psx,
    fbneo, amiga600..., the same as the ROM folder in /recalbox/share/roms),
    the game is started by Recalbox's own launcher, which sets up controllers
    and video like a game started from EmulationStation. Full list:
    https://wiki.recalbox.com/en/tutorials/themes/tree/systems-list
  - without it (or if that fails), RetroArch is started directly with
    /usr/lib/libretro/<core>_libretro.so and Recalbox's retroarchcustom.cfg.

"retroarch.runtimes" (BIOS files) are copied to /recalbox/share/bios.
The libretro core must exist on Recalbox under the same name.
Installer cards (retrogc-data.rgc) are ignored, they are for Windows / Linux.

Logs
----

  /tmp/retrogc-launcher.log

The watcher uses /tmp for its lock, PID and log, so those do not persist
across reboot.
