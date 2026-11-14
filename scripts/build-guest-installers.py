#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Build two standalone Win9x installers and CD images; never run the installers."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

import pycdlib

ROOT = Path(__file__).resolve().parents[1]


def build_importer(work: Path, executable: Path):
    """Compile the bounded Win9x Winsock import client without a guest CRT."""
    vswhere = Path(os.environ['ProgramFiles(x86)'])/'Microsoft Visual Studio/Installer/vswhere.exe'
    vs = subprocess.check_output([str(vswhere), '-latest', '-products', '*',
                                  '-property', 'installationPath'], text=True).strip()
    if not vs:
        raise RuntimeError('MSVC x86 installation is required for DOSE Import')
    vcvars = Path(vs)/'VC/Auxiliary/Build/vcvarsall.bat'
    source = ROOT/'guest-tools/windows95/dose-import.c'
    obj = work/'dose-import.obj'
    batch = work/'build-import.cmd'
    batch.write_text('@echo off\n' +
        f'call "{vcvars}" x86 >nul\nif errorlevel 1 exit /b 1\n' +
        f'cl /nologo /c /O2 /arch:IA32 /GS- /Zl /Fo"{obj}" "{source}"\nif errorlevel 1 exit /b 1\n' +
        f'link /nologo /Brepro /NODEFAULTLIB /ENTRY:TransferEntry /MACHINE:X86 '
        f'/DYNAMICBASE:NO /NXCOMPAT:NO /SUBSYSTEM:WINDOWS /OUT:"{executable}" '
        f'"{obj}" kernel32.lib user32.lib wsock32.lib\n', encoding='ascii')
    subprocess.run(['cmd', '/c', str(batch)], cwd=ROOT, check=True)
    subprocess.run([sys.executable, str(ROOT/'scripts/glide-bridge/patch_pe_version.py'),
                    str(executable)], cwd=ROOT, check=True)


def main():
    """Build both native guest packages and verify their generated CD contents."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--nasm', type=Path, default=ROOT/'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    parser.add_argument('--output', type=Path, default=ROOT/'artifacts/guest-installers')
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT/'artifacts'):
        raise ValueError('Installer output must be beneath repository/artifacts')
    for tool in (args.nasm,):
        if not tool.is_file():
            raise FileNotFoundError(f'Build tool missing: {tool}')
    subprocess.run(['cmd', '/c', r'scripts\glide-bridge\build-guest.cmd'], cwd=ROOT, check=True)
    output.mkdir(parents=True, exist_ok=True)
    work = output / ('build-' + uuid.uuid4().hex[:8])
    work.mkdir()
    report = {'installerEngine': 'Own ANSI Win32 / IA32 / PE 4.0', 'guestExecution': False,
              'installerSourceSha256': hashlib.sha256((ROOT/'guest-tools/installer/installer.c').read_bytes()).hexdigest(),
              'packages': {}}
    for os_version in ('95', '98'):
        clock = work / ('clock' + os_version)
        subprocess.run([sys.executable, str(ROOT/'scripts/build-dose-time.py'),
            '--output', str(clock), '--nasm', str(args.nasm), '--windows-version', os_version],
            cwd=ROOT, check=True)
        payload = work / ('payload' + os_version)
        payload.mkdir()
        importer = payload/'DOSEIMPORT.EXE'
        build_importer(payload, importer)
        for source, name in (
            (clock/'DOSETIME.EXE', 'DOSETIME.EXE'),
            (ROOT/'artifacts/glide-bridge/guest/GLIDE2X.DLL', 'GLIDE2X.DLL'),
            (ROOT/'artifacts/glide-bridge/guest/MGLIDE.EXE', 'MGLIDE.EXE'),
            (ROOT/'guest-tools/installer/README.txt', 'README.TXT'),
            (ROOT/'guest-tools/windows95'/('APM'+os_version+'.INF'), 'APM'+os_version+'.INF'),
            (ROOT/'guest-tools/windows95/APM-README.txt', 'APMHELP.TXT'),
            (ROOT/'LICENSE', 'LICENSE.TXT'),
        ):
            shutil.copyfile(source, payload/name)
        executable = output / ('Setup-DOSE-Win'+os_version+'.exe')
        resource = payload/'installer.rc'
        source_rc = (ROOT/'guest-tools/installer/installer.rc').as_posix()
        resources = {10:'DOSETIME.EXE',11:'GLIDE2X.DLL',12:'MGLIDE.EXE',13:'README.TXT',
                     14:'APM'+os_version+'.INF',15:'APMHELP.TXT',16:'LICENSE.TXT',17:'DOSEIMPORT.EXE'}
        resource.write_text('#include "'+source_rc+'"\n' + ''.join(
            f'{number} RCDATA "{(payload/name).as_posix()}"\n' for number,name in resources.items()),encoding='ascii')
        command = ['cmd','/c',r'scripts\build-win9x-installer.cmd',str(resource),str(payload),
                   str(executable),'10' if os_version=='98' else '0',os_version]
        built = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, errors='replace')
        (work/('build'+os_version+'.log')).write_text(built.stdout+built.stderr, encoding='utf-8')
        if built.returncode:
            raise RuntimeError(built.stdout+built.stderr)
        image = output / ('DOSE-Tools-Win'+os_version+'.iso')
        iso = pycdlib.PyCdlib()
        iso.new(interchange_level=1, vol_ident='DOSETOOLS'+os_version, sys_ident='DOSE')
        files = {'SETUP.EXE': executable, 'README.TXT': payload/'README.TXT', 'LICENSE.TXT': payload/'LICENSE.TXT'}
        for name, source in files.items():
            iso.add_file(str(source), iso_path='/'+name+';1')
        iso.write(str(image)); iso.close()
        check = pycdlib.PyCdlib(); check.open(str(image))
        for name, source in files.items():
            copy = io.BytesIO(); check.get_file_from_iso_fp(copy, iso_path='/'+name+';1')
            assert copy.getvalue() == source.read_bytes(), name
        check.close()
        report['packages'][os_version] = {
            'installer': str(executable), 'installerBytes': executable.stat().st_size,
            'installerSha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
            'iso': str(image), 'isoSha256': hashlib.sha256(image.read_bytes()).hexdigest(),
            'payload': str(payload), 'mediaReadbackPassed': True,
            'files': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in payload.iterdir()},
        }
        print('Built Windows '+os_version+': '+str(executable), flush=True)
    (output/'build.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    shutil.copyfile(ROOT/'guest-tools/installer/README.txt', output/'README.TXT')
    print('Ready: '+str(output), flush=True)


if __name__ == '__main__':
    main()
