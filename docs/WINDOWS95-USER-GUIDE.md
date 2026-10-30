# Windows 95 in DOSE

The supplied German Windows 95 OEM/950 installation uses the original S3 Trio64 and Sound Blaster drivers. The DOSE menus and DOSE Tools installer are in English. This installation does not constitute acceptance of every M4 feature or the original Windows 95 RTM release.

Run `run-win95.cmd` for the 100 MHz PC. The separate 133 MHz starter is in `artifacts/Windows95`. Both starters select the validated C19 networking runtime and retain their separate C18 64 MiB working disks. They do not compile the current source tree. Earlier C18/V12 runtimes and original disks are retained separately. The regular automation is paused at the user's request.

## Network cards

Open **MENU / NETWORK** and select **ADAPTER 1** or **ADAPTER 2**. Each slot offers **NE2000 ISA** or **RTL8029 PCI**, with its own **NAT**, **HOST-ONLY** or **BRIDGED** mode. Model, mode and resource changes cold-restart the PC; shut Windows down first. Switch **CABLE** on after configuring the card. Existing working PCs do not acquire a network card automatically.

For the supplied 950a Windows installation, the original RTL8029 **Windows 95 A** driver is required. Its private data disk is `artifacts/Windows95/network-drivers-c19/rtl8029-win95a.img`: mount it as A: and select **Have Disk** when Windows requests a manufacturer driver. Add Microsoft's TCP/IP protocol and use automatic address assignment. The driver files have not been silently written into your working Windows disk.

NAT and Host-only passed actual Windows TCP/UDP transfers. Bridged requires separately installed Npcap and an explicitly selected host adapter; physical Bridged LAN operation remains untested on the current host because Npcap is absent. [Configuration, original drivers and snapshot behavior](NETWORKING.md).

## Time and date

Open the DOSE menu with **Ctrl+Alt+F10**, then **TIME AND SPEED**. Keep **HOST CLOCK SYNC: ON** to supply the host's local date and time to the emulated RTC. Loading a snapshot or resuming a paused PC adopts the current host local time.

Windows 95 also keeps a software clock. **DOSE Tools – Time Synchronization** installs `C:\DOSETOOLS\DOSETIME.EXE`, creates `C:\Share` if needed, and starts its background helper automatically with Windows. The helper reads the emulated RTC and updates Windows through Windows time APIs. An existing Share directory is retained. Creating this directory does not yet enable file transfer, clipboard sharing, or a host folder.

CMOS has whole-second resolution. A slow emulator can delay a Windows timer callback; clock synchronization does not make CPU execution run in real time.

## RAM

The Windows 95 board profiles support **16, 32, 64, or 128 MiB**. The repaired desktop presets use 64 MiB; the generic profile templates still default to 32 MiB. In **MACHINE / HARDWARE**, select **RAM** and use the menu controls to choose a size. This change cold-restarts the PC: shut Windows down first. Use **SAVED MACHINES → SAVE THIS PC...** to retain the chosen configuration for a later launch. A full snapshot retains its original RAM configuration.

The older experimental 430FX profiles retain their fixed 32 MiB contract. Windows reports slightly less usable physical memory than the configured amount because of the PC's reserved memory regions.

## Display

Change Windows display settings through **Control Panel → Display → Settings**. The original driver lists modes; a listed mode is not by itself evidence that its complete drawing path has been tested.

The supported mode set and its limits are:

| Resolution | Color depth |
| --- | --- |
| 640×480 | 256 colors, 16-bit High Color, 32-bit True Color |
| 800×600 | 256 colors, 16-bit High Color |
| 1024×768 | 256 colors, 16-bit High Color |
| 1280×1024 | 8-bit, 256 colors |

The emulated card has 2 MiB VRAM. The 1280×1024 mode uses indexed color; do not select 1280×1024 at 16 or 32 bits. True Color stores 24 visible color bits in four bytes per pixel. The current firmware offers it at 640×480. Actual Windows file/GDI/Wave checks cover 800×600 and 1024×768 High Color, 1280×1024 indexed color, and 640×480 True Color; the remaining listed combinations also have device/firmware controls. This is still a functional subset of the original card. When Windows requests a restart after changing a mode, perform a normal Windows restart.

A stale legacy S3 device entry can coexist with the functioning PCI S3 entry in an older installed Windows configuration. The private repaired copies remove the proven stale legacy entry using Windows Device Manager. There is one emulated PCI graphics card. Remaining unused entries under the driver's class registry key do not by themselves represent another installed device.

## Smoothness and video controls

The optimized CPU execution path reduces host work, but universal 1:1 real-time speed has not been achieved. The title's **SPEED** value measures actual emulated time divided by host elapsed time; **FPS** alone does not measure emulation speed. The 133 MHz profile requires more host CPU work than the 100 MHz profile.

Under **VIDEO SETTINGS**, **DEBUG OVERLAY** hides or shows the lower status area. **MULTICORE VIDEO** optionally shares pixel conversion with another host thread. It improves that conversion stage on sufficiently large frames, but measurements did not establish an overall Windows or FPS gain. CPU/device scheduling, sound DMA and disk completion remain on the emulation owner.

Always use Windows' normal shutdown before changing hardware or closing a working disk. The previous frozen runtime and original working disks are retained as fallback; old full snapshots remain bound to their exact VM build.
