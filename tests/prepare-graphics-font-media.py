"""Create one NEW private FAT12 floppy containing our two assembled COM probes.
No operating system, font, driver or imported boot code is copied.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

spec=importlib.util.spec_from_file_location('fat_oracle',Path(__file__).with_name('verify-msdos-install.py'))
fat_oracle=importlib.util.module_from_spec(spec)
spec.loader.exec_module(fat_oracle)


def prepare(program12,program13,destination):
    files={'GFX12.COM':Path(program12).read_bytes(),'GFX13.COM':Path(program13).read_bytes()}
    assert all(0<len(data)<=65535 for data in files.values())
    image=bytearray(1474560)
    image[:3]=b'\xeb\x3c\x90'
    image[3:11]=b'GFXTEST '
    struct.pack_into('<HBHBHHBHHHII',image,11,512,1,1,2,224,2880,0xf0,9,18,2,0,0)
    image[38]=0x29
    image[43:54]=b'GFX FONT   '
    image[54:62]=b'FAT12   '
    image[510:512]=b'\x55\xaa'
    fat=bytearray(9*512)
    fat[:3]=b'\xf0\xff\xff'
    next_cluster=2
    for entry,(name,data) in enumerate(files.items()):
        count=(len(data)+511)//512
        first=next_cluster
        assert first+count<=2849
        for cluster in range(first,first+count):
            value=cluster+1 if cluster<first+count-1 else 0xfff
            offset=cluster*3//2
            old=int.from_bytes(fat[offset:offset+2],'little')
            word=(old&0xf if cluster&1 else old&0xf000)|(value<<4 if cluster&1 else value)
            fat[offset:offset+2]=word.to_bytes(2,'little')
        root=19*512+entry*32
        stem,ext=name.split('.')
        image[root:root+11]=(stem.ljust(8)+ext.ljust(3)).encode('ascii')
        image[root+11]=0x20
        struct.pack_into('<HI',image,root+26,first,len(data))
        start=(33+first-2)*512
        image[start:start+len(data)]=data
        next_cluster+=count
    image[512:10*512]=fat
    image[10*512:19*512]=fat
    recovered,allocation=fat_oracle.Fat(image).files()
    assert recovered==files
    with Path(destination).open('xb') as output:
        output.write(image)
    return dict(passed=True,onlyOwnPrograms=True,floppySha256=hashlib.sha256(image).hexdigest().upper(),
                files={name:dict(bytes=len(data),sha256=hashlib.sha256(data).hexdigest().upper()) for name,data in files.items()},allocation=allocation)


if __name__=='__main__':
    print(json.dumps(prepare(*sys.argv[1:]),indent=2))
