"""Create a NEW private installed-DOS copy with local game files, never exports media.

Only test preparation edits the new FAT16 copy. Original DOS and game files
are read-only; all subsequent guest writes require another private overlay.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
fat_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fat_oracle)


def prepare(base, overlay, game, destination, demo_tics=32):
    disk, journal = fat_oracle.materialize(base, overlay)
    start = struct.unpack_from("<I", disk, 454)[0] * 512
    volume = fat_oracle.Fat(disk, start)
    assert volume.bits == 16
    old, _ = volume.files()
    additions = {name: (Path(game) / name).read_bytes() for name in ("DOOM2.EXE", "DOOM2.WAD")}
    assert additions["DOOM2.EXE"][:2] == b"MZ" and additions["DOOM2.WAD"][:4] == b"IWAD"
    # Own neutral configuration; never copy or overwrite the user's settings/saves.
    additions["DOOMTEST.CFG"] = b"snd_musicdevice 0\nsnd_sfxdevice 0\nuse_mouse 0\nuse_joystick 0\nscreenblocks 10\ndetaillevel 0\n"
    # Own tic commands in id's documented original demo format, no copied demo.
    assert 1 <= demo_tics <= 256
    additions["FASTDEMO.LMP"] = bytes([109,2,1,1,0,0,0,1,0,1,0,0,0]) + bytes([12,0,4,0])*demo_tics + b"\x80"
    assert not set(old) & set(additions)
    result = bytearray(disk)
    fat_start = start + volume.reserved * 512
    root_start = fat_start + volume.copies * volume.spf * 512
    cluster_bytes = volume.spc * 512
    free = [c for c in range(2, volume.clusters + 2) if volume.entry(c) == 0]
    entries = [root_start + i * 32 for i in range(volume.entries) if result[root_start + i * 32] in (0, 0xe5)]
    assert len(entries) >= len(additions)
    for (name, content), entry in zip(additions.items(), entries):
        count = (len(content) + cluster_bytes - 1) // cluster_bytes
        assert 0 < count <= len(free)
        chain, free = free[:count], free[count:]
        for i, cluster in enumerate(chain):
            for copy in range(volume.copies):
                struct.pack_into("<H", result, fat_start + copy * volume.spf * 512 + cluster * 2,
                                 chain[i + 1] if i + 1 < count else 0xffff)
            address = start + (volume.data_sector + (cluster - 2) * volume.spc) * 512
            chunk = content[i * cluster_bytes:(i + 1) * cluster_bytes]
            result[address:address + cluster_bytes] = chunk.ljust(cluster_bytes, b"\0")
        stem, extension = name.split(".")
        result[entry:entry + 32] = bytes(32)
        result[entry:entry + 11] = (stem.ljust(8) + extension.ljust(3)).encode("ascii")
        result[entry + 11] = 0x20
        struct.pack_into("<HI", result, entry + 26, chain[0], len(content))
    new, allocation = fat_oracle.Fat(result, start).files()
    assert new == old | additions
    assert result[:512] == disk[:512] and result[start:start + 512] == disk[start:start + 512]
    with Path(destination).open("xb") as file:
        file.write(result)
    return dict(passed=True, privateCopy=True, originalDosFilesUnchanged=True, originals=len(old), files=len(new),
                addedFiles={n: dict(bytes=len(d), sha256=hashlib.sha256(d).hexdigest().upper()) for n, d in additions.items()},
                allocation=allocation, originalJournal=journal, imageSha256=hashlib.sha256(result).hexdigest().upper())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("base", "overlay", "game", "destination"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--demo-tics",type=int,default=32)
    args = parser.parse_args()
    print(json.dumps(prepare(args.base, args.overlay, args.game, args.destination,args.demo_tics), indent=2))
