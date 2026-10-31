# Durable sector journals

## C53: Windows write-through, 31 October 2026

The Win98 Explorer CD-to-hard-disk copy exposed long apparent freezes in the
real-mode BIOS storage path. The copy dialog stopped updating while actual ATA
writes continued. A sample of the active owner located almost all its observed
storage wait in `FlushFileBuffers` after individual 512-byte guest sectors.
The old run and its stopped native state are retained separately as diagnostics.

Windows journals now open with `FileOptions.WriteThrough`. Each complete
552-byte checksummed sector record is still written synchronously before the
append index or sector cache publishes its new value. The redundant whole-file
flush after each such write is removed. Windows documents write-through data
completion and NTFS metadata flushing as part of that write request:
[CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea),
[file caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching).
This follows the operating system's persistence contract; it is not a physical
power-loss qualification of every host storage controller.

Explicit guest flush, header creation, torn-tail repair and checkpoint export
retain their existing durable flushes. Non-Windows hosts retain the original
per-sector `Flush(true)` path. No disk worker, delayed acknowledgement, write
batching, different guest deadline or journal-format migration is introduced.
The 48-byte `DOSCOW01` header, 552-byte record layout, checksums, last-sector
index, bounded cache and failure-before-publication ordering are unchanged.

The regression independently queries the actual Windows handle through
`NtQueryInformationFile(FileModeInformation)`, checks synchronous write-through
on both new and reopened journals, and reads/checks the raw record before any
explicit guest flush. A buffered control handle proves the query discriminates
the modes. See [the documented mode](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/52df7798-8330-474b-ac31-9afe8075640c).
The same new mode checker rejects the old buffered-plus-flush implementation;
that does not mean the old implementation lacked durability.

A six-run mirrored-order pilot in one process over private 256-record files measured about
1175/1190 ms for buffered writes with per-write full flush, 1278/1282 ms for
write-through plus that redundant flush, and 28/32 ms for synchronous
write-through alone. These are backend pilot measurements, not a general
Windows/game speedup factor or a power-failure test.

Private qualification: `artifacts/tests/whpx-cd-copy-release-path.txt`.

The real Windows 98 SE Explorer copy selects all nine visible CD-root items
and copies them to C: on a fresh private disk. It reaches natural completion,
the virtual PS/2 cursor moves during copying, and Windows reaches its regular
safe-to-turn-off screen. An independent Joliet/VFAT/FAT32 reader verifies all
3,533 copied files, 365 directories and 663,260,856 payload bytes against the
source CD, including every file digest and the traversed entries in both FATs.
It reads the sealed own journal without opening the user's disk or the raw base.
The payload total includes repeated ISO extents; it is not an ISO-sector count.

A genuine native snapshot captured during active copying loads through the
strict common owner with identical complete 128-MiB RAM, native CPU, devices
and scheduler state, with no excluded state fields. Explorer then finishes the
copy in that restored branch. Native future execution follows wall time; this
does not claim byte-identical future schedules. The old stalled dialog and the
new completed copy establish a practical correction, not a controlled
equal-work application speedup ratio or universal 100% emulation under load.

The final build has no warnings or errors. All 15,471 common regressions,
54 native groups and the expanded GUI-owner check pass. The serialized graph
remains 48 components, 960 members and 43 callbacks. The selected C53 runtime
changes six of 124 products from C52r1: Hardware DLL/PDB in both frontends and
the common test DLL/PDB. Earlier immutable runtimes and user disks remain intact.
