# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Disable the real-mode CD path in a private Win95 bridge overlay for CDFS testing."""

from pathlib import Path

from install_guest import read_guest_file, replace_file


def main() -> None:
    """Back up and disable the real-mode CD path only in a named CDFS test branch."""
    branch = Path(__import__("sys").argv[1])
    for path, old, new in (
        ("CONFIG.SYS", b"DEVICE=C:\\DOSECD.SYS", b"REM    C:\\DOSECD.SYS"),
        ("AUTOEXEC.BAT", b"C:\\WINDOWS\\COMMAND\\MSCDEX.EXE", b"REM INDOWS\\COMMAND\\MSCDEX.EXE"),
    ):
        data = read_guest_file(branch, path)
        if data is None or len(old) != len(new):
            raise ValueError(f"Unexpected Win95 startup file: {path}")
        if data.count(new) == 1:
            print(path, "already disabled")
            continue
        if data.count(old) != 1:
            raise ValueError(f"Unexpected Win95 startup file: {path}")
        (branch / f"before-cdfs-{path}").write_bytes(data)
        result = replace_file(branch, path, data.replace(old, new))
        print(path, result.installed_size, result.unchanged)


if __name__ == "__main__":
    main()
