# DOSE Tools: first bounded guest import

This first step adds `C:\DOSETOOLS\DOSEIMPORT.EXE` to the Windows 95 and 98
guest installer. It is an explicit, one-file host-to-guest import over the
existing emulated NIC and Windows TCP/IP stack. It is not background sync,
guest-to-host export, clipboard sharing, or a VxD channel. The guest's actual
`C:\Share` contains the final file only after complete reception, disk flush
and integrity verification. The host must present one explicitly selected
source through the local gateway; the guest executable does not enumerate host
paths or open host files.

For the current development build, choose **Network → Import one file to
C:\Share…** in the emulator menu, then open **Start → Programs → DOSE Tools →
Import File from DOSE** and enter the one-time code shown in the emulator.
The equivalent command is `C:\DOSETOOLS\DOSEIMPORT.EXE <16-hex-code>`. The
headless alternative is
`DOSEmulator.Headless --serve-dose-import <selected-host-file>` followed by the
same guest command with the code printed by the host. The menu prompts only after the
listener binds. It closes after its single ACK/ABORT transaction, when the
window closes, or when that VM resets, is replaced, or changes its network
connection. The VM must have a working connected NAT
RTL8029/TCP/IP setup. This is a manual development path, not an automatically
started background service. The new GUI action is compiled and statically
reviewed; native menu interaction remains untested.

## DOSETF02 wire format

TCP target from the guest: `10.0.2.2:28595` on the tested NAT configuration.
The host gateway binds only `127.0.0.1:28595`. Each 16-byte header contains
ASCII `DOSETF02` (8 bytes), a little-endian 32-bit opcode/status and a
little-endian 32-bit payload length. Guest GET is opcode 1, length 8, followed
by the eight random code bytes. The host compares them before disclosing the
file; a wrong code closes only that connection, leaving the one-time listener
available for the correct guest until its deadline. Host
FILE is status 0, NO_FILE is 1, ERROR is 2. A FILE payload contains a
little-endian 16-bit name length (1..64), 32-bit file length (0..60000),
32-bit IEEE CRC-32, safe ASCII leaf name, and all file bytes.
The complete payload is at most 65,536 bytes and its length must equal
`10 + nameLength + fileLength`. Guest ACK is opcode 3 with detail zero;
ABORT is opcode 4 with an optional nonzero guest failure phase in the final
header word. A connection handles exactly one request.

Names admit only ASCII `A-Z`, `a-z`, `0-9`, period, underscore and hyphen; leading or
trailing periods, separators, traversal, and DOS device stems such as `CON`
and `COM1` are refused case-insensitively. Existing final or partial files are never overwritten.
The guest creates a new uniquely suffixed `.DXXXXXXXX` partial file, writes
and flushes all bytes, checks CRC-32, closes the handle and moves it to the
final leaf. On failure it closes and removes only its own partial file. A
partial left by an interrupted earlier attempt does not block a later import.
The installer and uninstaller preserve
all data in `C:\Share`.

The host must not publish or consume a queued source as complete until it
receives ACK. A disconnect, cancellation, ten-minute host deadline or malformed
transfer is an aborted attempt. The GUI binds the import to its current VM
generation. Session snapshot and media-checkpoint creation are rejected from
the start of an import until the guest's terminal ACK/ABORT. If the payload
may have reached guest RAM but no terminal reply arrives, saving remains
blocked until a guest reset or PC replacement; changing the network cable
alone does not clear this uncertain state. This closes the ordinary GUI
snapshot race but has not yet been exercised at a deterministic in-flight
Win9x snapshot cut. The standalone headless gateway has no VM lifecycle or
snapshot gate, and direct SnapshotStore calls outside MachineSession are not
covered. The code protects this selected transfer from an unrelated local
client that does not know it; Windows' Run history may retain the entered
one-time command, so the code must expire after this transfer. Both sides bound
one file and all framing, and the host may not silently substitute another
selected source. This initial protocol does not
claim atomic cross-machine rollback. The current helper was first staged into
new private COWs, then independently installed by each version's actual
`SETUP.EXE /s /apm` from its ISO on separate fresh Windows 95 and 98 boots.
Both installer runs left byte-identical importer and APM package files. Each
guest used its installed TCP/IP stack and acknowledged a 2,536-byte transfer;
after stopping the VM, an independent FAT reader found the exact bytes in
`C:\Share`. A separate 100-byte Win98 control also passed. Separate fresh
branches on both Windows versions completed silent guest uninstallation after
import: the Share bytes stayed unchanged, installed package files were
removed or restored according to their recorded pre-install state, and
maintenance records plus `.LAST` backups survived. On separate fresh copies
of those accepted installer branches, both guests updated from the revised
ISO and imported a second 2,536-byte file. Independent FAT/COW readback found
both original and second Share files unchanged/exact, plus the newly installed
package bytes. Fresh private Win95/98 branches then updated the installer to
DOSETF02, tried an incorrect code without consuming the listener, and
completed the authorized import with independent FAT/COW readback. Separate
fresh branches on both versions launched the installed helper without command
arguments, displayed its guest code prompt, entered the code through the
virtual keyboard, acknowledged the import and independently read back the
2,536 payload bytes from FAT/COW. These tests exercised the executable path
used by the Start menu shortcut, but did not click the native shortcut or GUI
menu. Clipboard,
export, sustained throughput, deterministic in-flight snapshot acceptance
and persistent user-VM integration remain
acceptance gates. The common user VMs and original media were not modified.
