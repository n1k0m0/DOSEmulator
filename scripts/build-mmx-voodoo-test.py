# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble the original finite MMX/SST-1 client into private test-resource storage, independently from BIOS inputs."""
import argparse
import hashlib
import json
import pathlib
import subprocess


def main():
    """Require the named NASM and verify the bounded own guest image before exposing its byte identity."""
    parser = argparse.ArgumentParser()
    parser.add_argument('--nasm', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    output = pathlib.Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([args.nasm, '-Werror', '-f', 'bin', '-o', str(output), str(root / 'tests/guests/mmx-voodoo-test.asm')], check=True)
    data = output.read_bytes()
    if not 64 <= len(data) <= 4096:
        raise ValueError('Own MMX/SST-1 client exceeds its fixed isolated code area')
    boot = output.with_suffix('.boot')
    sectors = (len(data) + 511) // 512
    subprocess.run([args.nasm, '-Werror', '-f', 'bin', '-D', 'CLIENT_SECTORS=' + str(sectors), '-o', str(boot), str(root / 'tests/guests/mmx-voodoo-boot.asm')], check=True)
    boot_bytes = boot.read_bytes()
    if len(boot_bytes) != 512 or boot_bytes[510:] != bytes([0x55, 0xaa]):
        raise ValueError('Own hardware-demo boot sector differs')
    image = bytearray(1474560)
    image[:512] = boot_bytes
    image[512:512+len(data)] = data
    output.with_suffix('.img').write_bytes(image)
    print(json.dumps(dict(bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), bootSectors=sectors, floppySha256=hashlib.sha256(image).hexdigest())))


if __name__ == '__main__':
    main()
