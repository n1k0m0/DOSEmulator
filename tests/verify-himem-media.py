# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Read-only FAT/journal audit: only private CONFIG.SYS plus its exact backup."""
import hashlib
import importlib.util
import json
import struct
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


def verify(base, original, configured):
    """Validates himem media evidence from actual guest state, immutable program bytes and independently expected results."""
    before, _ = oracle.materialize(base, original)
    after, journal = oracle.materialize(base, configured)

    def files(data):
        """Reads and validates the named FAT files from the supplied private media snapshot."""
        start = struct.unpack_from("<I", data, 454)[0] * 512
        return oracle.Fat(data, start).files()

    old, _ = files(before)
    new, allocation = files(after)
    assert "CONFIG.PRE" not in old and set(new) == set(old) | {"CONFIG.PRE"}
    assert new["CONFIG.PRE"] == old["CONFIG.SYS"]
    first_line, rest = new["CONFIG.SYS"].split(b"\r\n", 1)
    assert first_line.rstrip() == b"DEVICE=C:\\DOS\\HIMEM.SYS /TESTMEM:ON"
    assert rest == old["CONFIG.SYS"]
    for name, data in old.items():
        if name != "CONFIG.SYS":
            assert new[name] == data, ("Changed original file", name)
    driver = "DOS/HIMEM.SYS"
    assert driver in old
    return dict(passed=True, originalFiles=len(old), files=len(new), originalConfigurationBackedUp=True,
                onlyChangedOriginal="CONFIG.SYS", onlyAddedFile="CONFIG.PRE", allocation=allocation,
                originalHimemSha256=hashlib.sha256(old[driver]).hexdigest().upper(),
                originalHimemBytes=len(old[driver]), allOtherOriginalFilesUnchanged=True, journal=journal)


if __name__ == "__main__":
    print(json.dumps(verify(*sys.argv[1:]), indent=2))
