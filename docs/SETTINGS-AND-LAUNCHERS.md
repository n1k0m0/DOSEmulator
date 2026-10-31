# Settings and launchers

All root `run*.cmd` launchers resolve the same selected local emulator through
`artifacts/current/current.json`. They retain separate PC definitions, writable
disks and UI preferences. A new runtime is copied into a new immutable version
directory and verified before that pointer changes. An already running emulator
continues to use its original files; close and reopen it to use the new version.

`scripts/test-launchers.ps1` checks all launchers using an isolated fake workspace.
Passing `-Inspect` to a root launcher reports its selected runtime, PC and settings
directory without opening guest media or a native window.

| Launcher | PC |
|---|---|
| `run.cmd` | Built-in demonstration, or explicitly supplied legacy media options |
| `run-doom2.cmd` | Existing personal Doom II PC and journal |
| `run-freedos.cmd` | Existing FreeDOS hard-disk PC |
| `run-freedos-floppy.cmd` | Existing FreeDOS floppy PC |
| `run-win95.cmd`, `run-win95-apm.cmd`, `run-win95-load.cmd`, `run-win95-media.cmd` | Accepted Windows 95 safety copies with installed guest drivers; see below |
| `run-win98.cmd`, `run-win98-apm.cmd`, `run-win98-load.cmd` | Existing main, APM and load-test Windows 98 PCs |
| `run-doom2-media.cmd`, `run-win98-media.cmd` | Existing separate games and multimedia PCs |

Selecting a newer emulator does not install drivers inside an existing Windows
disk. In particular, the original Windows 95 RTL8029 miniport and the APM guest
driver remain guest installations. See [Networking](NETWORKING.md) and
[performance measurements](PERFORMANCE.md).

## Windows 95 safety copies, 31 October 2026

Following explicit approval, these four launchers now select new copies inside
`artifacts/Windows95-Drivers-C47r2-20261031`. Each directory contains
`branch.dospc.json` and its own writable `ide0.cow`. The originals below remain
unchanged and available for rollback. No original disk or journal was replaced.

| Launcher | New copy directory | Retained original directory |
|---|---|---|
| `run-win95.cmd` | `win95.dosebranch` | `artifacts/Windows95/Windows95-100MHz-c18.dosebranch` |
| `run-win95-apm.cmd` | `win95-apm.dosebranch` | `artifacts/Windows-Realtime-C44b/Windows95.dosebranch` |
| `run-win95-load.cmd` | `win95-load.dosebranch` | `artifacts/Windows-Load-C45/Windows95.dosebranch` |
| `run-win95-media.cmd` | `win95-media.dosebranch` | `artifacts/Games-Media-C46/Windows95.dosebranch` |

All four copies have the matching original RTL8029 Windows 95 A miniport,
Microsoft TCP/IP with DHCP and Windows logon selected. The main copy also received
the missing original APM driver; the other three already had it. Each copy passed
real NAT UDP/TCP transfer, normal Windows restart and shutdown. The copied source
prefixes, original configurations/journals and exact installed driver bytes are
verified. The original floppy bindings and existing frontend settings directories
are preserved. Hardware and MAC addresses are unchanged; the copies have new PC IDs.

All 13 launchers still use the same C47r2 emulator. Their `-Inspect` checks and
the isolated launcher fixture pass with the new paths, without opening a native
window. Windows 98 and Doom working media were not opened for this upgrade.
Private evidence: `artifacts/tests/win95-drivers-release-path.txt`.

## Change hardware

Open the menu with **Ctrl+Alt+F10**, then **MACHINE / HARDWARE**. Use the arrow keys
or click a visible row. Long pages scroll with the selection; Page Up/Page Down
move a page and Home selects the first row. The footer shows the full selected
value when the row is shortened.

Changes remain in a draft until **APPLY CHANGES / COLD RESTART**. Several dependent
settings can be edited together and validated as one profile. **DISCARD CHANGES**
leaves the running PC unchanged. Closing the menu with pending edits offers Apply,
Discard or Keep Editing. Shut down Windows before applying hardware changes: an
Apply performs a cold restart.

**CPU / PLATFORM** selects complete implemented profiles: 486 AT at 33 MHz,
Pentium ISA at 90 MHz, and PCI/S3/CD platforms at 100 or 133 MHz. RAM choices depend
on the platform, including the supported Windows sizes up to 128 MiB. Unsupported
clock/board combinations are rejected. Moving from PCI to ISA requires removing
PCI cards first. Video choices likewise follow the actual board; the S3 card is
part of the PCI platform.

Sound resources, mouse, floppy drives, boot device, video, gameport, COM, LPT,
MIDI and network cards are reachable from the hardware pages. Resource conflicts
are rejected before replacing the current PC or opening new media.

Successful changes to a PC opened from a saved definition are saved to that same
definition. External edits to its file are detected and reported, rather than
overwritten. A temporary demo/raw-media boot and a full snapshot load detach this
save destination. Use **SAVED MACHINES / SAVE THIS PC...** to give those sessions
their own PC file. The legacy DOS launchers remember their configuration in their
separate frontend data directory, keyed by their original launch options.

## Configure networking

Open **NETWORK ADAPTERS / ADAPTER 1** or **ADAPTER 2**. Model, mode, MAC, I/O, IRQ,
bridge interface and Host-only LAN name can be edited there. MAC and LAN rows
open English value editors. Apply pending changes first, then switch **CABLE**
on. The status row reports the real backend connection and its error, if any.

NAT uses DHCP. Host-only joins other DOSE cards with the same LAN name; give each
card a different MAC. Bridged requires separately installed Npcap and an explicit
host adapter. Missing Npcap is displayed as unavailable. DOSE does not silently
substitute another mode. **NETWORK HELP** explains driver installation and lets
you insert a selected driver floppy.

## Presentation and speed

**VIDEO SETTINGS** contains **DEBUG OVERLAY**, **MULTICORE VIDEO** and
**PRESENTATION RATE** (30/60/120 Hz, default 60). These are saved UI preferences.
The presentation limit avoids converting an image after every CPU batch; it
does not discard guest CPU instructions, device deadlines or audio work.

**MULTICORE VIDEO** remains off by default. It accelerates sufficiently large
pixel conversions, with both worker parts joined before image publication.
It is not a general CPU or Windows speed multiplier. The speed display measures
actual simulated time against host time. Continuous 100% under every game and
video workload remains an open target.

## Saved states

PC definitions are cold-start configurations. Full snapshots require exactly the
VM module build that created them. Old immutable runtimes are retained for their
old snapshots; selecting a newer runtime does not migrate or rewrite snapshots.
