# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Read-only private FAT audit of the original drivers and own copied COM file."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


def verify(base, original, configured, program, no_driver=False):
    """Validates emm386 media evidence from actual guest state, immutable program bytes and independently expected results."""
    before, _ = oracle.materialize(base, original)
    after, journal = oracle.materialize(base, configured)
    start = struct.unpack_from("<I", before, 454)[0] * 512
    old, _ = oracle.Fat(before, start).files()
    new, allocation = oracle.Fat(after, start).files()
    added = {"EMMTEST.COM"} if no_driver else {"EMMTEST.COM", "CONFIG.PRE"}
    assert not set(old) & added and set(new) == set(old) | added
    assert new["EMMTEST.COM"] == Path(program).read_bytes()
    if not no_driver:
        assert new["CONFIG.PRE"] == old["CONFIG.SYS"]
        first, second, rest = new["CONFIG.SYS"].split(b"\r\n", 2)
        assert first.rstrip() == b"DEVICE=C:\\DOS\\HIMEM.SYS /TESTMEM:ON"
        assert second.rstrip() == b"DEVICE=C:\\DOS\\EMM386.EXE NOEMS"
        assert rest == old["CONFIG.SYS"]
    for name, data in old.items():
        if no_driver or name != "CONFIG.SYS":
            assert new[name] == data, ("Changed original file", name)
    return dict(passed=True, noDriver=no_driver, originalFiles=len(old), files=len(new), added=sorted(added),
                allOtherOriginalFilesUnchanged=True, originalConfigurationBackedUp=not no_driver,
                originalDrivers={name: dict(bytes=len(old[name]), sha256=hashlib.sha256(old[name]).hexdigest().upper())
                                 for name in ("DOS/HIMEM.SYS", "DOS/EMM386.EXE")},
                ownProgramSha256=hashlib.sha256(new["EMMTEST.COM"]).hexdigest().upper(), allocation=allocation, journal=journal)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("base", "original", "configured", "program"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--no-driver", action="store_true")
    args = parser.parse_args()
    print(json.dumps(verify(args.base, args.original, args.configured, args.program, args.no_driver), indent=2))
