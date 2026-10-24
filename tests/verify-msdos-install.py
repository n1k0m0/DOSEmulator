"""Read-only, independent FAT/journal checks of a privately installed MS-DOS disk.

Only the Python standard library is used. Never exports OS files or changes media.
The original first setup floppy provides hashes for the uncompressed system files.
"""
import hashlib
import json
import pathlib
import struct
import sys


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def materialize(base_path, cow_path):
    base = pathlib.Path(base_path).read_bytes()
    cow = pathlib.Path(cow_path).read_bytes()
    assert cow[:8] == b"DOSCOW01" and len(cow) >= 48
    assert struct.unpack_from("<q", cow, 8)[0] * 512 == len(base)
    assert cow[16:48] == hashlib.sha256(base).digest()
    assert (len(cow) - 48) % 552 == 0
    disk, written = bytearray(base), set()
    for start in range(48, len(cow), 552):
        record = cow[start:start + 552]
        assert hashlib.sha256(record[:520]).digest() == record[520:]
        lba, = struct.unpack_from("<q", record)
        assert 0 <= lba < len(base) // 512
        disk[lba * 512:(lba + 1) * 512] = record[8:520]
        written.add(lba)
    return bytes(disk), {"baseSha256": sha(base), "overlaySha256": sha(cow),
                         "journalRecords": (len(cow) - 48) // 552, "uniqueWrittenSectors": len(written)}


class Fat:
    def __init__(self, disk, start=0, length=None):
        self.disk, self.start = disk, start
        self.boot = disk[start:start + 512]
        assert self.boot[510:512] == b"\x55\xaa"
        word = lambda offset: struct.unpack_from("<H", self.boot, offset)[0]
        self.bps, self.spc, self.reserved = word(11), self.boot[13], word(14)
        self.copies, self.entries, self.spf = self.boot[16], word(17), word(22)
        self.sectors = word(19) or struct.unpack_from("<I", self.boot, 32)[0]
        assert self.bps == 512 and self.spc in (1, 2, 4, 8, 16, 32, 64) and self.copies == 2
        if length is not None:
            assert self.sectors <= length and start + self.sectors * 512 <= len(disk)
        self.root_sectors = (self.entries * 32 + 511) // 512
        self.data_sector = self.reserved + self.copies * self.spf + self.root_sectors
        self.clusters = (self.sectors - self.data_sector) // self.spc
        self.bits = 12 if self.clusters < 4085 else 16
        assert 0 < self.clusters < 65525
        fat_start = start + self.reserved * 512
        self.fat = disk[fat_start:fat_start + self.spf * 512]
        assert self.fat == disk[fat_start + self.spf * 512:fat_start + 2 * self.spf * 512], "FAT copies differ"
        root_start = start + (self.reserved + self.copies * self.spf) * 512
        self.root = disk[root_start:root_start + self.root_sectors * 512]

    def entry(self, cluster):
        if self.bits == 16:
            return struct.unpack_from("<H", self.fat, cluster * 2)[0]
        value = struct.unpack_from("<H", self.fat, cluster * 3 // 2)[0]
        return value >> 4 if cluster & 1 else value & 0xfff

    def chain(self, first):
        result, seen = [], set()
        current = first
        while current < (0xff8 if self.bits == 12 else 0xfff8):
            assert 2 <= current < self.clusters + 2 and current not in seen, ("bad/looping cluster", current)
            seen.add(current)
            result.append(current)
            current = self.entry(current)
        return result

    def bytes(self, chain):
        chunks = []
        for cluster in chain:
            start = self.start + (self.data_sector + (cluster - 2) * self.spc) * 512
            chunks.append(self.disk[start:start + self.spc * 512])
        return b"".join(chunks)

    def directory(self, raw):
        for offset in range(0, len(raw), 32):
            entry = raw[offset:offset + 32]
            if entry[0] == 0:
                return
            if entry[0] in (0xe5, 0x2e) or entry[11] & 8:
                continue
            stem, extension = entry[:8].decode("cp850").rstrip(), entry[8:11].decode("cp850").rstrip()
            name = stem + ("." + extension if extension else "")
            yield name, entry[11], struct.unpack_from("<H", entry, 26)[0], struct.unpack_from("<I", entry, 28)[0]

    def root_file(self, name):
        for found, attr, cluster, size in self.directory(self.root):
            if found == name:
                assert not attr & 16
                return self.bytes(self.chain(cluster))[:size] if cluster else b""
        raise AssertionError("Missing source file " + name)

    def files(self):
        files, owned = {}, {}

        def walk(raw, prefix=""):
            assert len(files) < 4096, "Too many directory entries"
            for name, attr, first, size in self.directory(raw):
                path = prefix + name
                chain = self.chain(first) if first else []
                for cluster in chain:
                    assert cluster not in owned, ("cross-linked cluster", cluster, path, owned.get(cluster))
                    owned[cluster] = path
                data = self.bytes(chain)
                if attr & 16:
                    assert chain, "Empty directory chain"
                    walk(data, path + "/")
                else:
                    assert len(chain) == (size + self.spc * 512 - 1) // (self.spc * 512), ("wrong allocated length", path)
                    assert path not in files
                    files[path] = data[:size]
        walk(self.root)
        allocated = {cluster for cluster in range(2, self.clusters + 2) if self.entry(cluster) != 0}
        assert set(owned) == allocated, ("orphan or bad clusters", sorted(allocated - set(owned))[:12])
        return files, {"bits": self.bits, "clusters": self.clusters, "allocatedClusters": len(owned),
                       "fatCopiesIdentical": True, "noCrosslinksOrLostClusters": True}


def verify(base_path, cow_path, source_floppy):
    disk, journal = materialize(base_path, cow_path)
    assert disk[510:512] == b"\x55\xaa"
    partitions = [disk[446 + i * 16:462 + i * 16] for i in range(4)]
    used = [entry for entry in partitions if entry[4] != 0]
    assert len(used) == 1 and used[0][0] == 0x80 and used[0][4] in (4, 6), "Missing active FAT16 partition"
    start, length = struct.unpack_from("<II", used[0], 8)
    volume = Fat(disk, start * 512, length)
    assert volume.bits == 16
    files, allocation = volume.files()
    original = pathlib.Path(source_floppy).read_bytes()
    source = Fat(original)
    comparisons = {}
    for name in ("IO.SYS", "MSDOS.SYS", "COMMAND.COM"):
        assert files[name] == source.root_file(name), ("Changed installed system file", name)
        comparisons[name] = {"size": len(files[name]), "sha256": sha(files[name]), "sourceMatch": True}
    for name in ("CHKDSK.EXE", "FORMAT.COM", "DEBUG.EXE", "SYS.COM", "FDISK.EXE", "QBASIC.EXE", "KEYB.COM"):
        path = "DOS/" + name
        assert files[path] == source.root_file(name), ("Changed uncompressed DOS tool", path)
        comparisons[path] = {"size": len(files[path]), "sha256": sha(files[path]), "sourceMatch": True}
    for name in ("CONFIG.SYS", "AUTOEXEC.BAT", "DOS/HIMEM.SYS", "DOS/EMM386.EXE", "DOS/HELP.HLP", "DOS/MEM.EXE"):
        assert name in files and len(files[name]) > 0, ("Missing installed file", name)
    return {"passed": True, "readOnlyVerification": True, "journal": journal,
            "partition": {"startSector": start, "sectors": length, "active": True, "type": used[0][4]},
            "allocation": allocation, "files": len(files), "originalDisk1Sha256": sha(original),
            "systemAndToolComparisons": comparisons, "allFileSha256": {name: sha(data) for name, data in sorted(files.items())}}


if __name__ == "__main__":
    print(json.dumps(verify(*sys.argv[1:4]), indent=2))
