# DOSE Disk Explorer

Offline Windows tool for browsing and transferring files on the DOS/Windows 95/98 disks used by DOSE. The UI is English. It supports FAT12, FAT16 and FAT32, including long filenames, Unicode names, nested folders, binary files and empty files. It needs no administrator rights or host filesystem mount.

## Start

Build/package with PowerShell 7.4 or later:

    ./scripts/build-disk-explorer.ps1 -RunTests

The private package is written to `VMs/Tools/DiskExplorer`. Run its `Start.cmd`. The local `VMs/Open-Disks.cmd` shortcut opens the same tool. .NET Desktop Runtime 10 is required.

The VM dropdown lists the independent VM folders. **Open VM…** also accepts any `machine.dospc.json`; the **Disk** selector includes secondary IDE disks and floppies, and **Volume** selects a supported partition. **Open IMG…** pairs a standalone raw IMG with an adjacent same-name COW, creating that COW only when an import is actually saved. A VM definition always uses its explicitly attached COW path instead.

1. Shut down the guest OS, close DOSE and open its machine definition.
2. Select the disk and volume.
3. Navigate with the tree, double-click a folder, or use **Up**.
4. Use **Import files…**, **Import folder…**, or **Export selected…**.
5. Close the disk/tool before starting DOSE again.

After an offline edit, cold-start the VM. A saved running CPU/RAM state belongs to its previous media generation and is not an appropriate continuation of an externally edited disk. Pausing the VM leaves its filesystem caches and media owner active.

**Replace existing files** is off by default. Existing host exports are never overwritten: each export creates a new folder. **New folder…** creates a guest directory. **Save complete IMG…** writes the complete current disk, including committed COW changes, to a new verified raw image.

The default DOS short-name code page is 437; 850 can be selected for Western European DOS media. Long names retain their Unicode spelling. NTFS, exFAT and optical ISO filesystems are not implemented in this tool. Read-only VM attachments remain read-only; use that VM's writable configuration when one exists.

## Storage and recovery

Opening a disk is read-only and validates the COW header, base SHA-256, capacity and every complete record checksum. It does not truncate an incomplete source tail. File sharing excludes guest writers; an emulator-owned writable journal cannot be opened here.

An import or folder creation modifies a private journal first. Imported file bytes are read back and checked. FAT32 primary/backup allocation hints are marked unknown so Windows recounts actual allocation. The candidate is flushed before publication. The previous complete journal is retained next to it as `*.cow.backup-<id>`. A same-directory atomic replacement publishes the complete candidate; cancellation, malformed media, conflicts and disk-full failures leave the current journal intact. A verified lease on the new generation excludes guest writers across its replacement and adoption. No base IMG is opened for writing.

Back up the whole VM folder. Future guest/tool writes remain in its COW. To restore an older journal, close both DOSE and Disk Explorer and replace the current COW with the matching retained backup; its base image must remain the same.

Exports stage complete new host files/folders and verify readback before publication. Guest traversal, device names and host symbolic links/junctions are rejected. No DOS/Windows program from an image is executed.

## Console tool

`App/DOSEmulator.DiskTool.exe` uses the same storage implementation. Paths with spaces must be quoted. Disk and volume indexes start at zero; guest paths have no drive letter.

    DOSEmulator.DiskTool.exe list --vm "VMs/win95/machine.dospc.json"
    DOSEmulator.DiskTool.exe import --vm "VMs/win95/machine.dospc.json" --from "C:\Transfer\Example folder" --to "Share"
    DOSEmulator.DiskTool.exe export --vm "VMs/win95/machine.dospc.json" --path "Share\Example folder" --output "C:\Transfer\New export"
    DOSEmulator.DiskTool.exe mkdir --image "Disk.img" --path "Share"
    DOSEmulator.DiskTool.exe merge --image "Disk.img" --cow "Disk.cow" --output "Disk-complete.img"

Use `--help` for the complete command synopsis. Ctrl+C cancels an unpublished operation.

## Validation and limits

The separate storage tests use newly generated original FAT12, MBR/FAT16 and FAT32 filesystems. They check whole payloads, Unicode LFNs, directory trees, empty files, replacement, full-image export, source immutability, durable backup contents, cancellation, disk-full rollback, COW corruption, source locks and host-path rejection. An independent sector-level FAT/LFN oracle reads the resulting journal through the emulator's existing `CopyOnWriteDisk`, rather than through the filesystem library used by the editor.

Additional private Windows 95/FAT16 and Windows 98/FAT32 disk copies pass a complete 262157-byte Unicode-name transfer and an empty-file transfer. Independent FAT/LFN/COW readback checks the payload and a preserved existing OS file. Active VM disks, settings, launchers and emulator binaries are unchanged.

The GUI and console builds have zero warnings/errors. `--check-ui` checks the English action metadata before creating any Form or starting an application loop; it also works through the local CMD. Native GUI appearance/input verification has not been performed under the standing restriction on host test windows. These checks are not additional guest boots or emulator performance acceptance.

The tool uses the maintained [LTRData DiscUtils](https://github.com/LTRData/DiscUtils) FAT/Core/Streams packages pinned to version 1.0.90, published under MIT. The package metadata identifies source commit `414c7f7b2d82c42b07bc6fdfc18a763331334697`. Its original copyright/license notice accompanies the package in `App/LICENSES/DiscUtils-MIT.txt`; DOSE code remains Apache-2.0. This dependency is confined to the separate disk-tool projects and is not added to the emulator's five VM modules.

The transitive LTRData.Extensions 1.0.23 dependency is also MIT-licensed. Its package copyright notice and license accompany the tool in `App/LICENSES/LTRData-Extensions-MIT.txt`.
