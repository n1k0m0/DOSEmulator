#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Build only the own Win95 clock PE with NASM; never execute any generated EXE.

The output must be a NEW private directory beneath repository/artifacts.
Example: python scripts/build-dose-time.py --output <private-new-directory> --nasm <nasm.exe>
No solution build, package installation, native window or host endpoint is used.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'guest-tools' / 'windows95' / 'dose-time.asm'
IMPORTS = {
    'KERNEL32.DLL': ['GetVersion', 'GetCommandLineA', 'GetLocalTime', 'SystemTimeToFileTime',
        'SetLocalTime', 'GetLastError', 'GetModuleFileNameA', 'GetModuleHandleA',
        'CreateDirectoryA', 'GetFileAttributesA', 'CopyFileA', 'WinExec', 'CreateFileA',
        'WriteFile', 'FlushFileBuffers', 'CloseHandle', 'ExitProcess'],
    'USER32.DLL': ['MessageBoxA', 'FindWindowA', 'RegisterClassA', 'CreateWindowExA',
        'SetTimer', 'KillTimer', 'GetMessageA', 'TranslateMessage', 'DispatchMessageA',
        'DefWindowProcA', 'DestroyWindow', 'PostQuitMessage', 'PostMessageA',
        'DialogBoxIndirectParamA', 'EndDialog'],
    'ADVAPI32.DLL': ['RegCreateKeyExA', 'RegSetValueExA', 'RegCloseKey'],
}
METADATA = ['entry', 'proof', 'proofSize', 'stage', 'terminal', 'mode', 'synchronize',
    'rtcReadStable', 'rtcIndexIn', 'rtcSelectOut', 'rtcDataIn', 'rtcRestoreOut',
    'rtcExceptionHandler', 'windowProc', 'targetSystemTime', 'beforeSystemTime',
    'afterSystemTime', 'installedPath', 'runCommand', 'proofPath']


def require(condition: bool, message: str) -> None:
    """Reject a malformed own PE or output scope with a concrete diagnostic."""
    if not condition:
        raise ValueError(message)


def sha(data: bytes) -> str:
    """Seal only explicitly supplied own source and generated artifact bytes."""
    return hashlib.sha256(data).hexdigest().upper()


def validate_pe(data: bytes) -> dict:
    """Independently parse the generated PE layout/imports and passive own metadata."""
    def u16(at: int) -> int:
        """Read one bounded little-endian PE word."""
        require(0 <= at <= len(data) - 2, 'u16 outside own PE')
        return struct.unpack_from('<H', data, at)[0]

    def u32(at: int) -> int:
        """Read one bounded little-endian PE dword."""
        require(0 <= at <= len(data) - 4, 'u32 outside own PE')
        return struct.unpack_from('<I', data, at)[0]

    require(len(data) >= 512 and data[:2] == b'MZ', 'Missing own MZ header')
    pe = u32(0x3C)
    require(pe == 0x80 and data[pe:pe+4] == b'PE\0\0', 'Wrong PE signature/offset')
    require(u16(pe+4) == 0x14C and u16(pe+6) == 1, 'Expected one i386 section')
    require(u32(pe+8) == 0 and u16(pe+20) == 0xE0, 'Nondeterministic timestamp/optional header')
    require(u16(pe+22) == 0x0103, 'Expected executable/32bit/relocations-stripped image')
    op = pe + 24
    require(u16(op) == 0x10B and u32(op+28) == 0x400000, 'Expected fixed PE32 image base')
    require(u32(op+32) == 0x1000 and u32(op+36) == 0x200, 'Wrong PE alignments')
    require(u16(op+68) == 2 and u32(op+92) == 16, 'Expected GUI subsystem/16 directories')
    section = op + 0xE0
    virtual_size, virtual_rva, raw_size, raw_at = struct.unpack_from('<4I', data, section+8)
    require(data[section:section+8] == b'.text\0\0\0', 'Wrong own section name')
    require(virtual_rva == 0x1000 and raw_at == 0x200 and raw_size % 512 == 0,
        'Wrong section mapping')
    require(raw_at + raw_size == len(data) and virtual_size <= raw_size,
        'Unexpected overlay/truncation/uninitialized storage')
    require(u32(op+56) == ((virtual_rva + virtual_size + 4095) & ~4095), 'Wrong SizeOfImage')
    require(u32(section+36) == 0xE0000060, 'Unexpected own section flags')

    def off(rva: int, length: int = 1) -> int:
        """Translate only a fully initialized own section range to its file offset."""
        require(length >= 0 and virtual_rva <= rva and rva + length <= virtual_rva + virtual_size,
            'RVA outside initialized own section')
        return raw_at + rva - virtual_rva

    def c_string(rva: int) -> str:
        """Decode one bounded NUL-terminated own ASCII string."""
        start = off(rva)
        end = data.find(b'\0', start, min(start+260, raw_at+virtual_size))
        require(end != -1, 'Unterminated import/own string')
        return data[start:end].decode('ascii')

    imports_rva, imports_size = struct.unpack_from('<2I', data, op+104)
    require(imports_size == 80, 'Expected three DLL descriptors plus null')
    actual = {}
    for index in range(4):
        fields = struct.unpack_from('<5I', data, off(imports_rva+index*20, 20))
        if index == 3:
            require(fields == (0, 0, 0, 0, 0), 'Missing null import descriptor')
            break
        lookup, stamp, forward, name, iat = fields
        require(stamp == 0 and forward == 0 and lookup != iat, 'Unexpected bound/aliased imports')
        dll = c_string(name)
        require(dll in IMPORTS and dll not in actual, 'Unrecognized/duplicate imported DLL')
        names = []
        for number in range(64):
            thunk = u32(off(lookup+4*number, 4))
            require(u32(off(iat+4*number, 4)) == thunk, 'IAT differs before actual OS binding')
            if thunk == 0:
                break
            require(thunk & 0x80000000 == 0 and u16(off(thunk, 2)) == 0,
                'Ordinal/nonzero-hint import forbidden')
            names.append(c_string(thunk+2))
        else:
            raise ValueError('Unbounded import table')
        require(names == IMPORTS[dll], 'Imported API contract changed')
        actual[dll] = names
    require(actual == IMPORTS, 'Missing imported DLL/API')

    require(data.count(b'DOSETMM1') == 1 and data.count(b'DOSETM01') == 1,
        'Own metadata/proof magic must be unique')
    meta_at = data.index(b'DOSETMM1')
    require(u16(meta_at+8) == 1 and u16(meta_at+10) == len(METADATA), 'Wrong own metadata version/count')
    values = struct.unpack_from('<20I', data, meta_at+12)
    metadata = dict(zip(METADATA, values))
    require(values[2] == 512, 'Wrong proof size')
    for index, value in enumerate(values):
        if index != 2:
            off(value)
    require(u32(op+16) == metadata['entry'], 'Metadata entry mismatch')
    proof = off(metadata['proof'], 512)
    require(data[proof:proof+8] == b'DOSETM01' and u32(proof+8) == 1 and u32(proof+12) == 512,
        'Wrong proof header')
    require(metadata['stage'] == metadata['proof']+16 and metadata['terminal'] == metadata['proof']+20
        and metadata['mode'] == metadata['proof']+36, 'Wrong proof field bindings')
    require(data[off(metadata['rtcIndexIn'],2):off(metadata['rtcIndexIn'],2)+2] == b'\xE4\x70',
        'CMOS index observation must be a real IN70 instruction')
    require(data[off(metadata['rtcSelectOut'],2):off(metadata['rtcSelectOut'],2)+2] == b'\xE6\x70'
        and data[off(metadata['rtcDataIn'],2):off(metadata['rtcDataIn'],2)+2] == b'\xE4\x71',
        'CMOS read must use real OUT70/IN71')
    require(c_string(metadata['installedPath']) == r'C:\DOSETOOLS\DOSETIME.EXE', 'Wrong installed path')
    require(c_string(metadata['runCommand']) == r'"C:\DOSETOOLS\DOSETIME.EXE" /background', 'Wrong quoted startup command')
    require(c_string(metadata['proofPath']) == r'C:\DOSETIME.BIN', 'Wrong own proof path')
    return {'imageBase': 0x400000, 'entryRva': u32(op+16), 'imageSize': u32(op+56),
        'sectionVirtualSize': virtual_size, 'imports': actual, 'metadata': metadata}


def main() -> None:
    """Run NASM alone in a fresh private destination; validate/report bytes, never launch PE."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path, help='NEW directory beneath repository/artifacts')
    parser.add_argument('--nasm', type=Path, help='Explicit NASM executable; otherwise PATH nasm')
    args = parser.parse_args()
    output = args.output.resolve()
    require(output != ROOT/'artifacts' and output.is_relative_to((ROOT/'artifacts').resolve()),
        'Output must be a fresh private child beneath repository/artifacts')
    require(not output.exists(), 'Output destination already exists; never overwrite')
    nasm = args.nasm or shutil.which('nasm')
    require(nasm is not None, 'NASM is required; no automatic download/install')
    nasm = Path(nasm).resolve()
    require(nasm.is_file() and nasm.name.lower() in ('nasm', 'nasm.exe'), 'Expected NASM executable')
    source_before = SOURCE.read_bytes()
    output.mkdir(parents=True, exist_ok=False)
    executable = output/'DOSETIME.EXE'
    listing = output/'DOSETIME.lst'
    command = [str(nasm), '-Werror', '-f', 'bin', str(SOURCE), '-o', str(executable), '-l', str(listing)]
    completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=60, check=False)
    (output/'assemble.log').write_text(completed.stdout+completed.stderr, encoding='utf-8', newline='\n')
    require(completed.returncode == 0, 'NASM failed; see new private assemble.log')
    require(SOURCE.read_bytes() == source_before, 'Source changed during assembly')
    binary = executable.read_bytes()
    parsed = validate_pe(binary)
    report = {'passed': True, 'source': str(SOURCE), 'sourceSha256': sha(source_before),
        'program': str(executable), 'programBytes': len(binary), 'programSha256': sha(binary),
        'listing': str(listing), 'builderSha256': sha(Path(__file__).read_bytes()),
        'onlyNasmExecuted': True, 'guestExecuted': False, 'hostPeExecuted': False,
        'supportedOs': 'Windows95 GetVersion major4/minor0/highBit1', 'timerMilliseconds': 500,
        'centuryRegister': 0x32, 'calendarRange': [1601,9999], 'verificationMax100ns': 10000000,
        'precision': 'Absolute difference of actual local civil calendars <=1second; RTC has whole-second resolution',
        'installer': 'CopyFile fail-if-exists; own HKLM Run value; C:\\Share contents preserved; no file transfer',
        'proof': {'size': 512, 'magic': 'DOSETM01', 'previousWriterAt': 256,
            'postTimeChangeBoolAt': 92, 'postTimeChangeErrorAt': 108,
            'notificationScope': 'Actual async PostMessageA HWND_BROADCAST WM_TIMECHANGE after nonzero SetLocalTime only',
            'writerScope': 'Persisted10DWORD writer fields describe only the completed previous pass, not final close'},
        **parsed}
    (output/'build.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8', newline='\n')
    print(json.dumps({'passed': True, 'program': str(executable), 'bytes': len(binary), 'sha256': sha(binary)}))


if __name__ == '__main__':
    main()
