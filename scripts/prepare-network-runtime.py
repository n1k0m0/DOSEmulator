# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Prepare only pinned Win64 libslirp DLL dependencies and their actual package license notices.

This does not install MSYS2/Npcap/drivers, change PATH, execute a PE, load a DLL,
open a host adapter, listen on a socket or build the emulator. Windows tar reads
only explicitly selected members from SHA256-verified official MSYS2 packages.
Downloads require --download; the complete runtime output must be a NEW directory
inside this workspace. Copy that directory as Release/native/win-x64 for deployment.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import urllib.parse
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
MIRROR = 'https://mirror.msys2.org/mingw/ucrt64/'
# Exact packages reviewed on2026-10-29; no moving package-index or latest-version resolution.
# key: version, complete package SHA256, matching MSYS2 source-package family.
PACKAGES = {
    'libslirp': ('4.9.3-1', 'C982BE43767A950F220B498F0F212EB2E2C945D93C968BD0E4F66B1B5DA94F6C', 'libslirp'),
    'glib2': ('2.90.1-1', '9A7826349279BCCA0C935B01880D812BE9DE9609AEF2205BE68FD7A6128C1695', 'glib2'),
    'gettext-runtime': ('1.0-1', 'BA693DDA4AC375AF76CE481FF3A6E7481286546CC7DC6D56C7021DAE34084157', 'gettext'),
    'libiconv': ('1.19-1', '9A500F38C2B91808741C62FAE746B3E9110B33A1ECF5C30FA0C66DBEDDDF7E16', 'libiconv'),
    'pcre2': ('10.49-1', '63172179DB724AC71E2439A7981134C1BCAB8049BDDDFD349B32B8F76ED52D1E', 'pcre2'),
    'libwinpthread': ('14.0.0.r569.g34eeafb7d-1', 'B4EBE2826E848C5E790E06EA7B37D3E0594E005F42382B0506886F17DF8127DA', 'winpthreads'),
    'libgcc': ('16.2.0-6', '80B2497F019977DD9528B1ABC8621B1328A0115AB1F724047CCBB56BF323F11D', 'gcc'),
    'libstdc++': ('16.2.0-6', '33C5E58A85A538CF843A8672991CCE591F0231D66DA3B66A57629712D64BE17F', 'gcc'),
    'libffi': ('3.8.0-1', '99AD12F4ECFA00A889EF9C5C0188592368A5BAC109D0C508CE4E04F29EA95A76', 'libffi'),
    'zlib': ('1.3.2-3', '130E6EF04C654FC2150A177600AA5ED0035087665A893479D2C85BA7485D5704', 'zlib'),
}
DLL_PACKAGES = {
    'libslirp-0.dll': 'libslirp', 'libglib-2.0-0.dll': 'glib2', 'libintl-8.dll': 'gettext-runtime',
    'libiconv-2.dll': 'libiconv', 'libpcre2-8-0.dll': 'pcre2', 'libwinpthread-1.dll': 'libwinpthread',
    'libgcc_s_seh-1.dll': 'libgcc', 'libstdc++-6.dll': 'libstdc++',
    'libffi-8.dll': 'libffi', 'libffi-9.dll': 'libffi', 'zlib1.dll': 'zlib',
}
SYSTEM_DLLS = frozenset(('advapi32.dll', 'bcrypt.dll', 'comdlg32.dll', 'crypt32.dll', 'dbghelp.dll', 'dnsapi.dll',
                        'gdi32.dll', 'imm32.dll', 'iphlpapi.dll', 'kernel32.dll', 'msvcrt.dll', 'netapi32.dll',
                        'normaliz.dll', 'ntdll.dll', 'ole32.dll', 'oleaut32.dll', 'psapi.dll', 'rpcrt4.dll',
                        'secur32.dll', 'setupapi.dll', 'shell32.dll', 'shlwapi.dll', 'ucrtbase.dll', 'user32.dll',
                        'userenv.dll', 'version.dll', 'winmm.dll', 'ws2_32.dll', 'wtsapi32.dll'))
MAX_PACKAGE = 64 * 1048576


def require(condition, message):
    """Reject an unpinned dependency, incomplete archive or existing output rather than guessing a fallback."""
    if not condition:
        raise ValueError(message)


def private_path(value):
    """Resolve only an explicitly chosen workspace preparation path, before any write/download."""
    raw = Path(value)
    require('doom2-play' not in (part.casefold() for part in raw.parts), 'Personal play media are excluded.')
    path = raw.resolve()
    require(path.is_relative_to(ROOT) and path != ROOT and 'doom2-play' not in (part.casefold() for part in path.parts), 'Use an explicit path inside this workspace.')
    require(not path.is_relative_to(ROOT / 'artifacts' / 'Windows95'), 'Delivered user installations are excluded.')
    return path


def digest(data):
    """Bind the complete supplied package member, never a selected code subset."""
    return hashlib.sha256(data).hexdigest().upper()


def write_new(path, data):
    """Create exactly one previously absent preparation file, preserving every old output."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def package_file(cache, name, download):
    """Use a complete pinned archive; downloading never replaces an existing cached file."""
    version, expected, source = PACKAGES[name]
    filename = f'mingw-w64-ucrt-x86_64-{name}-{version}-any.pkg.tar.zst'
    path = cache / ('libslirp.pkg.tar.zst' if name == 'libslirp' else filename)
    url = MIRROR + urllib.parse.quote(filename)
    if not path.exists():
        require(download, 'Missing pinned package; rerun with --download only when fetching official dependencies is intended: ' + name)
        with urllib.request.urlopen(url, timeout=30) as response, path.open('xb') as output:
            total = 0
            while True:
                chunk = response.read(1048576)
                if not chunk:
                    break
                total += len(chunk)
                require(total <= MAX_PACKAGE, 'Package exceeds the explicit64-MiB bound.')
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
    require(path.is_file() and 0 < path.stat().st_size <= MAX_PACKAGE, 'Pinned package length is invalid: ' + name)
    with path.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    require(actual == expected, 'Complete package SHA256 mismatch: ' + name)
    source_url = f'https://mirror.msys2.org/mingw/sources/mingw-w64-{source}-{version}.src.tar.zst'
    return path, dict(name=name, version=version, sha256=expected, binaryUrl=url, sourceUrl=source_url,
                      packagePage='https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-' + urllib.parse.quote(name))


def archive_members(package):
    """List and bound archive member names without extracting paths or running package install hooks."""
    value = subprocess.run(['tar', '-tf', str(package)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True, timeout=30).stdout
    require(len(value) <= 4 * 1048576, 'Package member listing exceeds its bound.')
    result = value.decode('utf-8').splitlines()
    require(len(result) == len(set(result)) and len(result) <= 100000, 'Duplicate/oversized package member listing.')
    for name in result:
        path = PurePosixPath(name)
        require(not path.is_absolute() and '..' not in path.parts and '\\' not in name, 'Unsafe archive member name.')
    return result


def member(package, name, maximum):
    """Read one exact pinned member to memory; tar never writes an archive path to the filesystem."""
    data = subprocess.run(['tar', '-xOf', str(package), name], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True, timeout=30).stdout
    require(0 < len(data) <= maximum, 'Selected archive member length is outside its bound.')
    return data


def pe_imports(data):
    """Statically validate a Win64 DLL and its complete ordinary import list without loading or executing it."""
    require(len(data) >= 512 and data[:2] == b'MZ', 'Selected DLL has no complete DOS/PE header.')
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    require(pe <= len(data) - 264 and data[pe:pe + 4] == b'PE\0\0', 'Invalid DLL PE offset/signature.')
    machine, count = struct.unpack_from('<HH', data, pe + 4)
    optional_size, characteristics = struct.unpack_from('<HH', data, pe + 20)
    opt = pe + 24
    require(machine == 0x8664 and 1 <= count <= 96 and optional_size >= 224 and characteristics & 0x2000 and
            opt + optional_size + count * 40 <= len(data) and struct.unpack_from('<H', data, opt)[0] == 0x20b,
            'Only complete AMD64 PE32+ DLLs are accepted.')
    directories = struct.unpack_from('<I', data, opt + 108)[0]
    require(directories >= 14 and optional_size >= 112 + 14 * 8, 'Missing DLL data-directory bounds.')
    require(struct.unpack_from('<II', data, opt + 112 + 13 * 8) == (0, 0), 'Delay imports need explicit dependency review.')
    sections = []
    for index in range(count):
        virtual_size, virtual_at, raw_size, raw_at = struct.unpack_from('<4I', data, opt + optional_size + index * 40 + 8)
        require(raw_at + raw_size <= len(data), 'DLL section exceeds complete member bounds.')
        sections.append((virtual_at, max(virtual_size, raw_size), raw_at, raw_size))

    def offset(rva, amount):
        """Map a whole RVA extent only when it exists in one actual stored section."""
        hits = [raw + rva - virtual for virtual, size, raw, stored in sections
                if virtual <= rva and rva + amount <= virtual + size and rva - virtual + amount <= stored]
        require(len(hits) == 1, 'DLL import RVA is absent, overlapping or outside stored bytes.')
        return hits[0]

    imports_at, imports_bytes = struct.unpack_from('<II', data, opt + 120)
    require(imports_at != 0 and 20 <= imports_bytes <= 65536, 'Invalid ordinary DLL import directory.')
    names = []
    for index in range(256):
        require((index + 1) * 20 <= imports_bytes, 'DLL import descriptor lacks a bounded terminator.')
        descriptor = struct.unpack_from('<5I', data, offset(imports_at + index * 20, 20))
        if descriptor == (0, 0, 0, 0, 0):
            return sorted(set(names))
        start = offset(descriptor[3], 1)
        end = data.find(b'\0', start, min(start + 129, len(data)))
        require(end >= start, 'DLL import name is unterminated.')
        require(offset(descriptor[3], end - start + 1) == start, 'DLL import name crosses its stored section.')
        text = data[start:end].decode('ascii').lower()
        require(re.fullmatch(r'[a-z0-9_.+\-]+\.dll', text) is not None, 'DLL import name is not one plain library filename.')
        names.append(text)
    raise ValueError('DLL imports exceed256 descriptors.')


def main():
    """Build a fresh selective DLL closure and retain every used package's actual licenses and source locations."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', default=str(ROOT / 'artifacts/network-f9220724/downloads'))
    parser.add_argument('--output', required=True)
    parser.add_argument('--download', action='store_true')
    args = parser.parse_args()
    require(os.name == 'nt', 'Use the Windows tar reader for the pinned zstd package format.')
    require(all(re.fullmatch('[0-9A-F]{64}', values[1]) for values in PACKAGES.values()), 'A reviewed package SHA256 pin is malformed.')
    cache, output = private_path(args.cache), private_path(args.output)
    require(not output.exists() and output != cache and not cache.is_relative_to(output), 'Runtime output must be a new separate directory.')
    cache.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=False)
    packages, selected, queue = {}, {}, ['libslirp-0.dll']
    while queue:
        name = queue.pop(0)
        if name in selected:
            continue
        require(name in DLL_PACKAGES, 'Unreviewed non-system DLL dependency: ' + name)
        owner = DLL_PACKAGES[name]
        if owner not in packages:
            archive, metadata = package_file(cache, owner, args.download)
            packages[owner] = (archive, metadata, archive_members(archive))
        archive, metadata, members = packages[owner]
        path = 'ucrt64/bin/' + name
        require(path in members, 'Pinned package does not contain required DLL: ' + name)
        data = member(archive, path, 32 * 1048576)
        imports = pe_imports(data)
        write_new(output / name, data)
        selected[name] = dict(name=name, bytes=len(data), sha256=digest(data), package=owner, imports=imports)
        for dependency in imports:
            if dependency in SYSTEM_DLLS or dependency.startswith(('api-ms-win-', 'ext-ms-win-')):
                continue
            require(dependency in DLL_PACKAGES, 'Unreviewed non-system DLL dependency: ' + dependency)
            queue.append(dependency)
        require(len(selected) <= 16, 'Native DLL closure exceeds its explicit bound.')
    require('libglib-2.0-0.dll' in selected, 'The selected libslirp did not import the expected GLib ABI.')
    license_records = []
    for name, (archive, metadata, members) in sorted(packages.items()):
        prefix = 'ucrt64/share/licenses/' + name + '/'
        licenses = [path for path in members if path.startswith(prefix) and not path.endswith('/')]
        require(licenses, 'Missing actual package license notices: ' + name)
        for path in licenses:
            data = member(archive, path, 1048576)
            relative = Path('licenses') / name / path[len(prefix):]
            write_new(output / relative, data)
            license_records.append(dict(path=relative.as_posix(), bytes=len(data), sha256=digest(data), package=name))
    archive, metadata, members = packages['libslirp']
    for header in ('libslirp.h', 'libslirp-version.h'):
        data = member(archive, 'ucrt64/include/slirp/' + header, 131072)
        write_new(output / 'headers' / 'slirp' / header, data)
    manifest = dict(format='DOSE-NETWORK-RUNTIME-1', slirpVersion='4.9.3', architecture='win-x64',
                    packages=[value[1] for name, value in sorted(packages.items())],
                    dlls=[selected[name] for name in sorted(selected)], licenses=license_records,
                    scope='Selective unmodified official runtime DLLs and notices only; no driver installation, DLL load, endpoint activation or guest execution.')
    write_new(output / 'network-runtime.json', (json.dumps(manifest, indent=2) + '\n').encode('utf-8'))
    notice = ['# DOSE native IPv4 networking runtime', '',
              'These unmodified libraries are dynamically loaded only on explicit Connect.',
              'No Npcap binary/driver is included. Bridged mode requires a separately installed Npcap.', '',
              'Actual package license texts are retained in licenses/. GLib, libintl and libiconv use LGPL terms;',
              'libslirp/PCRE2/winpthreads and any GCC runtime retain their own notices and exceptions.',
              'Replacement compatible libraries remain separate files. Corresponding source package locations follow.',
              'Preserve these notices and provide the required corresponding sources when redistributing LGPL binaries.', '']
    for name, (archive, metadata, members) in sorted(packages.items()):
        notice.extend([f"- {name} {metadata['version']}: {metadata['packagePage']}",
                       f"  Binary package SHA256: {metadata['sha256']}", f"  Corresponding MSYS2 source package: {metadata['sourceUrl']}"])
    write_new(output / 'README.md', ('\n'.join(notice) + '\n').encode('utf-8'))
    print(json.dumps(dict(prepared=True, output=str(output), dlls=len(selected), packages=len(packages),
                          manifestSha256=digest((output / 'network-runtime.json').read_bytes()), executedNativeCode=False)))


if __name__ == '__main__':
    main()
