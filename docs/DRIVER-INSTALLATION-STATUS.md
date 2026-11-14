# Windows 95/98 guest driver installation status

The emulator's device model, Windows' original device driver, and the DOSE
guest installer are separate pieces. A newer host executable does not install
drivers into an existing VM. This matrix describes only the named, tested
guest copies; it must not be applied to every user disk.

| Component | Windows 95 | Windows 98 | DOSE guest package |
|---|---|---|---|
| S3 Trio64 | Original driver used in the accepted 800×600 RGB565 guest path | Original driver used in the tested Windows 98 guest path | Not redistributed |
| Sound Blaster 16 | Original Windows driver produced checked DAC/IRQ and wave completion | Tested multimedia guest paths exist | Not redistributed |
| RTL8029 PCI | Four named safety copies have the matching original 950a miniport, Microsoft TCP/IP and DHCP; real NAT UDP/TCP passed | One separate original-driver fixture passed NAT Winsock; each working VM requires its own installation check | Driver help/floppy selection only; proprietary miniport is not bundled |
| APM | The four named safety copies have the original Windows APM files; the main copy received its previously missing files | Installation depends on the selected working copy | Version-specific INF and instructions are copied; original Windows files and actual device installation remain manual |
| DOSE time helper | Setup, startup, reboot and uninstall tested | Same | Included, creates `C:\Share` when needed |
| DOSE file import | Fresh ISO installer and actual NAT import of 2,536 bytes passed with independent FAT readback | Same; a separate 100-byte control also passed | Own manual `DOSEIMPORT.EXE`; host serves one selected file |
| MiniGlide | Guest DLL/launcher tested with the configured host bridge | Same; original Voodoo-Graphics driver is accepted only for its named Windows 98/WHPX path | Own DLL/launcher included; host renderer and original 3dfx driver are separate |

The [two guest installers](GUEST-INSTALLERS.md) do **not** provide an automatic
S3/SB16/RTL8029/3dfx driver pack. The original Windows or manufacturer files
must be installed through the guest's device setup and verified on the actual
VM. The DOSE Tools background app currently synchronizes time. `C:\Share` is
an ordinary guest folder. An explicit, bounded host-to-guest import now works
on the named fresh Windows 95/98 copies; export, clipboard and automatic
background sync remain open. [The transfer contract](GUEST-TOOLS-TRANSFER.md)
is a guest helper and local gateway, not a Windows device driver.

The installer integration harness checks installed DOSE binaries, startup,
the maintained Share file, cold boot and removal in private VM copies. It now
also compares every installed APM INF/help byte independently from the guest
FAT after the test VM stops. Fresh Windows 95 and Windows 98 installer runs
now pass that byte comparison, the DOSEIMPORT.EXE comparison and the actual
NAT import. Separate fresh uninstall branches prove that the package restores
or removes those files according to the saved originals and preserves the
imported Share file. On separate fresh copies of those accepted installer
branches, both Windows versions also passed an in-place package update from
a new ISO, a second actual NAT import, and independent readback of both Share
files and the updated package bytes. This does not prove that Windows loaded
an APM driver.

Remaining acceptance for a turnkey per-VM driver setup: enumerate the actual
devices after a cold boot; verify matching driver identity, resources and
absence of a failed duplicate; exercise video mode changes, audio playback,
network traffic and APM reboot/shutdown; then repeat after uninstall/upgrade.
Keep original driver media private and do this on fresh safety copies.
