# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Audit only fresh private COW copies; never modify or distribute OS files."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


def verify(base, original, configured, vcpi, ems, mode):
    """Validates vcpi ems media evidence from actual guest state, immutable program bytes and independently expected results."""
    before, _ = oracle.materialize(base, original)
    after, journal = oracle.materialize(base, configured)
    start = struct.unpack_from("<I", before, 454)[0] * 512
    old, _ = oracle.Fat(before, start).files()
    new, allocation = oracle.Fat(after, start).files()
    additions = {"VCPIPM.COM": Path(vcpi).read_bytes(), "EMSBANK.COM": Path(ems).read_bytes()}
    if mode != "no-driver":
        additions["CONFIG.PRE"] = old["CONFIG.SYS"]
    assert not set(old) & set(additions) and set(new) == set(old) | set(additions)
    for name, data in additions.items():
        assert new[name] == data
    if mode != "no-driver":
        first, second, rest = new["CONFIG.SYS"].split(b"\r\n", 2)
        # COMMAND.COM includes the space immediately before > / >> in ECHO.
        assert first == b"DEVICE=C:\\DOS\\HIMEM.SYS /TESTMEM:ON "
        assert second == b"DEVICE=C:\\DOS\\EMM386.EXE " + mode.encode("ascii") + b" "
        assert rest == old["CONFIG.SYS"]
    for name, data in old.items():
        if mode == "no-driver" or name != "CONFIG.SYS":
            assert new[name] == data, ("Changed original file", name)
    return dict(passed=True, mode=mode, originalFiles=len(old), files=len(new), added=sorted(additions),
                allOtherOriginalFilesUnchanged=True, originalConfigurationBackedUp=mode != "no-driver",
                originalDrivers={name: dict(bytes=len(old[name]), sha256=hashlib.sha256(old[name]).hexdigest().upper())
                                 for name in ("DOS/HIMEM.SYS", "DOS/EMM386.EXE")},
                allocation=allocation, journal=journal)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("base", "original", "configured", "vcpi", "ems"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--mode", choices=("no-driver", "NOEMS", "RAM"), required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.base, args.original, args.configured, args.vcpi, args.ems, args.mode), indent=2))
