DOSE Tools - Time Synchronization

This package installs a small background clock helper for the tested Windows 95
OEM/950 guest. Its interface and messages are in English.

1. Start Windows 95 in DOSE.
2. Insert dose-tools-time.img into the emulated floppy drive A:.
3. In Windows, choose Start > Run and enter A:\SETUP.EXE.
4. Confirm the English setup dialog. Setup creates C:\DOSETOOLS and C:\Share,
   copies its own program to C:\DOSETOOLS\DOSETIME.EXE, and registers the helper
   to start with Windows. Existing C:\Share contents are preserved.

The helper reads the emulated RTC and uses real Windows time APIs. DOSE's
TIME AND SPEED menu controls whether the RTC follows the host's local calendar.
With host synchronization enabled, startup, restore and resume use current host
time. Paused CPU/device execution remains paused. The helper checks every
500 milliseconds of guest time. The RTC has whole-second resolution; the tested
Windows calendar difference is bounded to one second.

The helper also notifies Windows when it changes the clock so the taskbar can
refresh. It does not change the selected CPU frequency or speed up guest work.
The window title reports actual emulation speed; the target is 100 percent.

Setup refuses to overwrite an existing DOSETIME.EXE. It creates a guest-local
C:\Share directory. This version implements time synchronization; file import,
export, clipboard sharing and host-folder mapping are separate work.

For diagnostics, DOSETIME.EXE /once writes an own 512-byte C:\DOSETIME.BIN proof.
DOSETIME.EXE /background runs the hidden timer helper. Never run these guest
executables on the host. This is a data disk, not a boot disk.
