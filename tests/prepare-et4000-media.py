# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create a fresh FAT12 floppy containing only our three original ET4000 COMs."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys


def prepare(directory, destination, names=('ET640', 'ET800', 'ET1024'), oem=b'ET4KTEST', label=b'ET4000 TEST'):
    """Build only the named original COMs; defaults retain the byte-identical ET4000 fixture."""
    directory, destination = Path(directory), Path(destination)
    files = {name+'.COM': (directory/(name+'.COM')).read_bytes() for name in names}
    image = bytearray(1474560)
    assert len(oem) == 8 and len(label) == 11
    image[:11] = b'\xeb\x3c\x90' + oem
    struct.pack_into('<HBHBHHBHHHII', image, 11, 512, 1, 1, 2, 224, 2880, 0xf0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[43:54] = label
    image[54:62] = b'FAT12   '
    image[510:512] = b'\x55\xaa'
    fat = bytearray(4608)
    fat[:3] = b'\xf0\xff\xff'
    cluster = 2
    for number, (name, data) in enumerate(files.items()):
        assert 0 < len(data) < 65536
        count = (len(data)+511)//512
        entry = 19*512 + number*32
        stem, ext = name.split('.')
        image[entry:entry+11] = (stem.ljust(8)+ext).encode('ascii')
        image[entry+11] = 0x20
        struct.pack_into('<HI', image, entry+26, cluster, len(data))
        start = (33+cluster-2)*512
        image[start:start+len(data)] = data
        for n in range(cluster, cluster+count):
            value = n+1 if n < cluster+count-1 else 0xfff
            at = n*3//2
            old = int.from_bytes(fat[at:at+2], 'little')
            fat[at:at+2] = ((old & 15 | value << 4) if n & 1 else (old & 0xf000 | value)).to_bytes(2, 'little')
        cluster += count
    image[512:10*512] = fat
    image[10*512:19*512] = fat
    spec = importlib.util.spec_from_file_location('fat_oracle', Path(__file__).with_name('verify-msdos-install.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    assert module.Fat(image).files()[0] == files
    with destination.open('xb') as stream:
        stream.write(image)
    return dict(onlyOwnPrograms=True, files={n: hashlib.sha256(d).hexdigest().upper() for n, d in files.items()},
                floppySha256=hashlib.sha256(image).hexdigest().upper())


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[3] == '--vbe':
        result = prepare(sys.argv[1], sys.argv[2], names=('VBE640', 'VBE800', 'VBE1024'), oem=b'VBETEST ', label=b'VBE TEST   ')
    else:
        result = prepare(*sys.argv[1:])
    print(json.dumps(result, indent=2))
