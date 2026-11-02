# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Replace a guest file in an isolated FAT16/FAT32 DOSE overlay.

Only an explicitly named bridge test branch is accepted. The base image and
all other overlays stay untouched. An existing file's FAT allocation is resized;
a missing file receives a new directory entry and cluster chain.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from dataclasses import dataclass
from pathlib import Path

RECORD = 552


def u16(data: bytes, offset: int) -> int:
    """Read one little-endian word from the supplied sector bytes."""
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    """Read one little-endian dword from the supplied sector bytes."""
    return struct.unpack_from("<I", data, offset)[0]


def short_name(part: str) -> bytes:
    """Encode a checked DOS 8.3 filename for a FAT directory lookup."""
    words = part.upper().split(".")
    if len(words) > 2 or len(words[0]) > 8 or (len(words) == 2 and len(words[1]) > 3):
        raise ValueError(f"Not an 8.3 name: {part}")
    return (words[0].ljust(8) + (words[1] if len(words) == 2 else "").ljust(3)).encode("ascii")


class Overlay:
    def __init__(self, branch: Path):
        """Open and authenticate an isolated overlay, its base disk and sector index."""
        definition = json.loads((branch / "branch.dospc.json").read_text(encoding="utf-8-sig"))
        if "bridge test" not in definition["name"].lower():
            raise ValueError("Refusing to change a branch that is not named as a bridge test")
        disk = next(item for item in definition["hardDisks"] if item["slot"] == 0)
        self.base = (branch / disk["imagePath"]).resolve()
        self.journal = (branch / disk["overlayPath"]).resolve()
        if self.journal.parent != branch.resolve() or self.journal == self.base:
            raise ValueError("Overlay must be local to the named test branch")
        self.raw = self.base.open("rb")
        self.file = self.journal.open("r+b")
        header = self.file.read(48)
        if header[:8] != b"DOSCOW01" or len(header) != 48:
            raise ValueError("Invalid DOSE overlay header")
        self.sectors = struct.unpack_from("<q", header, 8)[0]
        if self.raw.seek(0, 2) != self.sectors * 512:
            raise ValueError("Base disk capacity differs from overlay")
        self.raw.seek(0)
        digest = hashlib.file_digest(self.raw, "sha256").digest()
        if digest != header[16:]:
            raise ValueError("Base disk identity differs from overlay")
        self.index: dict[int, int] = {}
        offset = 48
        while record := self.file.read(RECORD):
            if len(record) != RECORD or hashlib.sha256(record[:520]).digest() != record[520:]:
                raise ValueError(f"Invalid overlay record at {offset}")
            lba = struct.unpack_from("<q", record)[0]
            if not 0 <= lba < self.sectors:
                raise ValueError(f"Invalid LBA at {offset}")
            self.index[lba] = offset + 8
            offset += RECORD
        self.end = offset

    def sector(self, lba: int) -> bytes:
        """Return the latest checked overlay sector, falling back to the read-only base."""
        if not 0 <= lba < self.sectors:
            raise ValueError("LBA outside disk")
        if lba in self.index:
            self.file.seek(self.index[lba])
            return self.file.read(512)
        self.raw.seek(lba * 512)
        return self.raw.read(512)

    def append(self, lba: int, sector: bytes) -> None:
        """Append one checksummed sector record to this isolated writable overlay."""
        if len(sector) != 512:
            raise ValueError("Only full sectors can be written")
        record = struct.pack("<q", lba) + sector
        self.file.seek(self.end)
        self.file.write(record + hashlib.sha256(record).digest())
        self.index[lba] = self.end + 8
        self.end += RECORD

    def close(self) -> None:
        """Flush and release the overlay and its read-only base stream."""
        self.file.flush()
        self.file.close()
        self.raw.close()


@dataclass(frozen=True)
class InstallResult:
    previous_size: int | None
    installed_size: int
    created: bool
    unchanged: bool
    original: bytes | None


class FatVolume:
    def __init__(self, overlay: Overlay):
        """Validate FAT geometry and locate allocation, directory and free-space metadata."""
        self.overlay = overlay
        mbr = overlay.sector(0)
        if mbr[-2:] != b"\x55\xaa":
            raise ValueError("Invalid MBR")
        entries = [mbr[446 + i * 16:462 + i * 16] for i in range(4)]
        partitions = [u32(entry, 8) for entry in entries if entry[4] in (4, 6, 11, 12, 14)]
        if len(partitions) != 1:
            raise ValueError("Expected one FAT partition")
        self.partition = partitions[0]
        boot = overlay.sector(self.partition)
        if boot[-2:] != b"\x55\xaa" or u16(boot, 11) != 512:
            raise ValueError("Invalid FAT boot sector")
        self.spc, self.reserved, self.fats = boot[13], u16(boot, 14), boot[16]
        self.roots = u16(boot, 17)
        self.spf = u16(boot, 22) or u32(boot, 36)
        total = u16(boot, 19) or u32(boot, 32)
        if self.spc == 0 or self.spc & (self.spc - 1) or self.fats != 2 or self.spf == 0 or total == 0:
            raise ValueError("Unsupported FAT geometry")
        self.root_sectors = (self.roots * 32 + 511) // 512
        self.data_start = self.reserved + self.fats * self.spf + self.root_sectors
        self.clusters = (total - self.data_start) // self.spc
        if self.clusters < 4085 or self.clusters >= 0x0FFFFFF5:
            raise ValueError("Expected FAT16 or FAT32")
        self.fat32 = self.clusters >= 65525
        if self.fat32 != (self.roots == 0) or self.data_start >= total:
            raise ValueError("FAT type and boot geometry disagree")
        self.entry_bytes = 4 if self.fat32 else 2
        self.eoc = 0x0FFFFFFF if self.fat32 else 0xFFFF
        self.eoc_min = 0x0FFFFFF8 if self.fat32 else 0xFFF8
        self.root_cluster = u32(boot, 44) if self.fat32 else 0
        self.fsinfo_sectors = []
        if self.fat32:
            info, backup = u16(boot, 48), u16(boot, 50)
            if 0 < info < self.reserved:
                self.fsinfo_sectors.append(self.partition + info)
                if 0 < backup < self.reserved and backup + info < self.reserved:
                    self.fsinfo_sectors.append(self.partition + backup + info)

    def fat(self, cluster: int) -> int:
        """Read a cluster link from the primary FAT using the volume entry width."""
        offset = cluster * self.entry_bytes
        sector = self.overlay.sector(self.partition + self.reserved + offset // 512)
        return (u32(sector, offset % 512) & 0x0FFFFFFF) if self.fat32 else u16(sector, offset % 512)

    def chain(self, first: int) -> list[int]:
        """Follow a bounded FAT chain while rejecting cycles and invalid links."""
        if first == 0:
            return []
        seen: set[int] = set()
        result: list[int] = []
        cluster = first
        while cluster < self.eoc_min:
            if not 2 <= cluster < self.clusters + 2 or cluster in seen:
                raise ValueError("Corrupt FAT chain")
            seen.add(cluster)
            result.append(cluster)
            cluster = self.fat(cluster)
            if cluster == 0:
                raise ValueError("FAT chain enters free space")
        return result

    def cluster_lbas(self, clusters: list[int]):
        """Yield physical sector addresses for the selected data clusters."""
        for cluster in clusters:
            for lane in range(self.spc):
                yield self.partition + self.data_start + (cluster - 2) * self.spc + lane

    def directory_lbas(self, clusters: list[int] | None):
        """Yield a directory cluster chain or the fixed FAT16 root directory."""
        if clusters is None:
            first = self.partition + self.reserved + self.fats * self.spf
            yield from range(first, first + self.root_sectors)
        else:
            yield from self.cluster_lbas(clusters)

    def entry(self, clusters: list[int] | None, name: bytes):
        """Find one unambiguous short-name entry and the next reusable directory slot."""
        found = []
        free = None
        for lba in self.directory_lbas(clusters):
            sector = self.overlay.sector(lba)
            for offset in range(0, 512, 32):
                item = sector[offset:offset + 32]
                if item[0] in (0, 0xE5):
                    if free is None:
                        free = (lba, offset)
                elif item[11] != 15 and item[:11] == name:
                    found.append((item, lba, offset))
        if len(found) > 1:
            raise ValueError("Ambiguous FAT directory entry")
        return (found[0] if found else None), free

    def free_clusters(self, count: int) -> list[int]:
        """Find the requested number of free clusters without changing allocation."""
        selected = []
        for cluster in range(2, self.clusters + 2):
            if self.fat(cluster) == 0:
                selected.append(cluster)
                if len(selected) == count:
                    return selected
        raise ValueError("Not enough free FAT clusters for Glide DLL")

    def update_fat(self, links: dict[int, int]) -> None:
        """Update both FAT copies and invalidate stale FAT32 free-space hints."""
        edited: dict[int, bytearray] = {}
        for copy in range(self.fats):
            for cluster, following in links.items():
                offset = cluster * self.entry_bytes
                lba = self.partition + self.reserved + copy * self.spf + offset // 512
                data = edited.setdefault(lba, bytearray(self.overlay.sector(lba)))
                if self.fat32:
                    current = u32(data, offset % 512)
                    struct.pack_into("<I", data, offset % 512, current & 0xF0000000 | following)
                else:
                    struct.pack_into("<H", data, offset % 512, following)
        for lba, data in edited.items():
            self.overlay.append(lba, bytes(data))
        # Allocation changes invalidate cached FAT32 free-space hints. Let the
        # guest recalculate them from the two FAT copies on its next mount.
        for lba in self.fsinfo_sectors:
            info = bytearray(self.overlay.sector(lba))
            if u32(info, 0) == 0x41615252 and u32(info, 484) == 0x61417272:
                struct.pack_into("<II", info, 488, 0xFFFFFFFF, 0xFFFFFFFF)
                self.overlay.append(lba, bytes(info))

    def write_entry(self, lba: int, offset: int, entry: bytes) -> None:
        """Replace one directory entry through a new overlay sector record."""
        sector = bytearray(self.overlay.sector(lba))
        sector[offset:offset + 32] = entry
        self.overlay.append(lba, bytes(sector))

    def write_file(self, clusters: list[int], payload: bytes) -> None:
        """Write payload bytes and final-cluster padding into the selected allocation."""
        if len(payload) > len(clusters) * self.spc * 512:
            raise ValueError("FAT file allocation is too small")
        for offset, lba in enumerate(self.cluster_lbas(clusters)):
            start = offset * 512
            chunk = payload[start:start + 512]
            self.overlay.append(lba, chunk.ljust(512, b"\0"))


def locate_path(volume: FatVolume, guest_path: str):
    """Resolve the parent directory and final 8.3 entry inside the checked FAT volume."""
    parts = guest_path.replace("\\", "/").split("/")
    if any(not part for part in parts):
        raise ValueError("Invalid guest path")
    directory = volume.chain(volume.root_cluster) if volume.fat32 else None
    for part in parts[:-1]:
        located, _ = volume.entry(directory, short_name(part))
        if located is None or not located[0][11] & 16:
            raise ValueError(f"Guest directory missing: {part}")
        item = located[0]
        first = u16(item, 26) | (u16(item, 20) << 16 if volume.fat32 else 0)
        directory = volume.chain(first)
    located, free_slot = volume.entry(directory, short_name(parts[-1]))
    return directory, located, free_slot


def read_guest_file(branch: Path, guest_path: str) -> bytes | None:
    """Read one guest file from an explicitly isolated branch without changing its bytes."""
    overlay = Overlay(branch)
    try:
        volume = FatVolume(overlay)
        _, located, _ = locate_path(volume, guest_path)
        if located is None:
            return None
        item = located[0]
        if item[11] & 16:
            raise ValueError("Target is a directory")
        first = u16(item, 26) | (u16(item, 20) << 16 if volume.fat32 else 0)
        chain = volume.chain(first)
        size = u32(item, 28)
        if size > len(chain) * volume.spc * 512:
            raise ValueError("Invalid FAT file size")
        return b"".join(overlay.sector(lba) for lba in volume.cluster_lbas(chain))[:size]
    finally:
        overlay.close()


def replace_file(branch: Path, guest_path: str, replacement: bytes) -> InstallResult:
    """Resize and replace one file in a private FAT16 or FAT32 branch overlay."""
    if not replacement or len(replacement) > 16 * 1024 * 1024:
        raise ValueError("Invalid guest file replacement size")
    overlay = Overlay(branch)
    try:
        volume = FatVolume(overlay)
        directory, located, free_slot = locate_path(volume, guest_path)
        parts = guest_path.replace("\\", "/").split("/")
        existing = located is not None
        original = None
        file_chain: list[int] = []
        old_size = 0
        needed = (len(replacement) + volume.spc * 512 - 1) // (volume.spc * 512)
        if located is not None:
            old_entry, lba, offset = located
            item = bytearray(old_entry)
            if item[11] & 16:
                raise ValueError("Guest target is a directory")
            old_size = u32(item, 28)
            first = u16(item, 26) | (u16(item, 20) << 16 if volume.fat32 else 0)
            file_chain = volume.chain(first)
            if old_size > len(file_chain) * volume.spc * 512 or old_size > 16 * 1024 * 1024:
                raise ValueError("Invalid existing guest file allocation")
            original = b"".join(overlay.sector(sector) for sector in volume.cluster_lbas(file_chain))[:old_size]
            if original == replacement and len(file_chain) == needed:
                return InstallResult(old_size, len(replacement), False, True, original)
        else:
            item = bytearray(32)
            item[:11] = short_name(parts[-1])
            item[11] = 0x20
            lba = offset = -1

        added = max(0, needed - len(file_chain))
        extend_directory = free_slot is None and not existing
        if extend_directory and directory is None:
            raise ValueError("FAT16 root directory is full")
        allocated = volume.free_clusters(added + int(extend_directory)) if added or extend_directory else []
        file_added = allocated[:added]
        directory_added = allocated[added:]
        links = {}
        if len(file_chain) > needed:
            links[file_chain[needed - 1]] = volume.eoc
            links.update((cluster, 0) for cluster in file_chain[needed:])
            file_chain = file_chain[:needed]
        if file_added:
            if file_chain:
                links[file_chain[-1]] = file_added[0]
            for first, following in zip(file_added, file_added[1:] + [volume.eoc]):
                links[first] = following
            file_chain += file_added
        if directory_added:
            links[directory[-1]] = directory_added[0]
            links[directory_added[0]] = volume.eoc
            for sector in volume.cluster_lbas(directory_added):
                overlay.append(sector, bytes(512))
            free_slot = (next(volume.cluster_lbas(directory_added)), 0)
        if links:
            volume.update_fat(links)
        if not existing:
            lba, offset = free_slot
            first = file_chain[0]
            struct.pack_into("<H", item, 26, first & 0xFFFF)
            if volume.fat32:
                struct.pack_into("<H", item, 20, first >> 16)
        # Match both allocation length and logical size. An oversized chain can
        # make the guest's disk checker grow the file back to its old capacity.
        struct.pack_into("<I", item, 28, len(replacement))
        volume.write_file(file_chain, replacement)
        volume.write_entry(lba, offset, bytes(item))
        return InstallResult(old_size if existing else None, len(replacement), not existing, False, original)
    finally:
        overlay.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("branch", type=Path)
    parser.add_argument("shim", type=Path)
    parser.add_argument("--guest-path", default="WINDOWS/SYSTEM/GLIDE2X.DLL")
    args = parser.parse_args()
    result = replace_file(args.branch, args.guest_path, args.shim.read_bytes())
    verb = "Already installed" if result.unchanged else "Created" if result.created else "Replaced"
    print(f"{verb} {result.installed_size}-byte guest file at {args.guest_path} on {args.branch}; "
          f"previous size: {result.previous_size}; FAT16/FAT32 overlay only")
