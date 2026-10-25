"""Check real MS-DOS FORMAT.COM's FDC protocol and resulting private FAT12 disk."""
import json
import importlib.util
import pathlib
import struct
import sys


def source(name):
    spec = importlib.util.spec_from_file_location(name, pathlib.Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def verify(base, cow, trace, memory=None):
    fat = source("verify-msdos-install")
    floppy = source("verify-floppy-format")
    disk, journal = fat.materialize(base, cow)
    assert len(disk) == 1474560
    volume = fat.Fat(disk)
    assert volume.bits == 12 and volume.sectors == 2880 and volume.spc == 1
    files, allocation = volume.files()
    assert not files and allocation["allocatedClusters"] == 0
    labels = [volume.root[i:i + 11] for i in range(0, len(volume.root), 32) if volume.root[i + 11] & 8]
    assert b"M2TEST     " in labels
    assert disk[volume.start + 43:volume.start + 54] == b"M2TEST     "
    data_start = volume.data_sector * 512
    assert disk[data_start:] == b"\xf6" * (len(disk) - data_start), "Data area differs from actual FDC format fill"
    packets = floppy.parse_protocol(trace)
    formats = [packet for packet in packets if packet["command"][0] == 0x4d]
    assert len(formats) == 160
    tracks = []
    # POST uses 9FC0h, while a real memory manager may relocate the EBDA.
    # With a dump, derive the expected buffer from the independent BDA pointer.
    ebda = struct.unpack_from("<H", pathlib.Path(memory).read_bytes(), 0x40e)[0] if memory else 0x9fc0
    bounce = (ebda << 4) + 0x200
    assert (bounce & 0xffff) + 72 <= 65536
    for packet in formats:
        assert packet["command"] == [0x4d, 1 | (packet["result"][4] << 2), 2, 18, 0x6c, 0xf6]
        assert packet["result"][0] & 0xc0 == 0 and packet["result"][1:3] == [0, 0]
        assert packet["result"][6] == 2 and packet["maskedAfterResult"]
        assert packet["dma"] == {"address": bounce & 0xffff, "count": 71, "page": bounce >> 16, "mode": 0x4a, "masked": False}
        tracks.append((packet["result"][3], packet["result"][4]))
    assert tracks == [(cylinder, head) for cylinder in range(80) for head in range(2)]
    return {"passed": True, "readOnlyVerification": True, "formatTrackCommands": len(formats),
            "all80CylindersBothHeads": True, "realDma2": True, "ebdaSegment": ebda, "dmaLinearAddress": bounce,
            "label": "M2TEST", "files": 0,
            "allocation": allocation, "allDataSectorFillBytesChecked": True, "journal": journal}


if __name__ == "__main__":
    print(json.dumps(verify(*sys.argv[1:5]), indent=2))
