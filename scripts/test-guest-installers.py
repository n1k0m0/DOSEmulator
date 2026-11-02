#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Run actual guest installer, reboot and removal checks in separate Windows copies."""
import argparse
import configparser
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

import pycdlib
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/glide-bridge'))
from install_guest import read_guest_file


def fnv(data):
    """Compute the independent guest-file hash used by the integration probe."""
    result=2166136261
    for value in data: result=((result^value)*16777619)&0xffffffff
    return result


def main():
    """Exercise installation, selected components, reboot and restoration in private guests."""
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--windows-version',choices=['95','98'],required=True)
    parser.add_argument('--upgrade-from',type=Path,help='A completed isolated installer-test branch for an update/removal check')
    args=parser.parse_args()
    version=args.windows_version
    product=ROOT/'artifacts/guest-installers'
    built=json.loads((product/'build.json').read_text())['packages'][version]
    payload=Path(built['payload'])
    work=product/('test-win'+version+'-'+uuid.uuid4().hex[:8]);work.mkdir()
    expected=[]
    for name,tag in [('GLIDE2X.DLL','GLIDE'),('DOSETIME.EXE','TIME'),('MGLIDE.EXE','LAUNCH')]:
        data=(payload/name).read_bytes()
        expected.extend([f'#define EXPECT_{tag}_HASH 0x{fnv(data):08x}UL',f'#define EXPECT_{tag}_BYTES {len(data)}UL'])
    expected.append(f'#define EXPECT_KEEP_HASH 0x{fnv(b"KEEP MY FILE"):08x}UL')
    (work/'expected.h').write_text('\n'.join(expected)+'\n')
    subprocess.run(['cmd','/c',r'scripts\build-guest-installer-check.cmd',str(work),version],cwd=ROOT,check=True)
    disc=work/'test.iso'
    iso=pycdlib.PyCdlib();iso.new(interchange_level=1,vol_ident='DOSETEST'+version)
    for name,path in {'SETUP.EXE':product/('Setup-DOSE-Win'+version+'.exe'),
                      'OTHER.EXE':product/('Setup-DOSE-Win'+('98' if version=='95' else '95')+'.exe'),
                      'CHECK.EXE':work/'CHECK.EXE'}.items():
        iso.add_file(str(path),iso_path='/'+name+';1')
    iso.write(str(disc));iso.close()
    source=ROOT/('artifacts/glide-bridge/win95-unreal-installed.dosebranch' if version=='95' else
                 'artifacts/unreal-win98-20261102-1e0df488/win98-unreal.dosebranch')
    if args.upgrade_from:
        source=args.upgrade_from.resolve()
        if not source.is_relative_to(product) or source.name!='windows.dosebranch':
            raise ValueError('Upgrade fixture must be a private installer test branch')
    branch=work/'windows.dosebranch';branch.mkdir()
    definition=json.loads((source/'branch.dospc.json').read_text(encoding='utf-8-sig'))
    definition['id']=uuid.uuid4().hex
    definition['name']='Own Windows '+version+' guest installer / bridge test'
    for disk in definition['hardDisks']:
        disk['imagePath']=str((source/disk['imagePath']).resolve())
        if disk.get('overlayPath'):
            if disk['slot']!=0: raise ValueError('Additional writable disks need a separate copy')
            shutil.copyfile(source/disk['overlayPath'],branch/'ide0.cow');disk['overlayPath']='ide0.cow'
    definition['opticalDiscs']=[dict(slot=definition['hardware']['atapiSlot'],imagePath=str(disc))]
    definition['floppies']=[]
    (branch/'branch.dospc.json').write_text(json.dumps(definition,indent=2))
    (product/('latest-test-win'+version+'.txt')).write_text(str(work))
    print('TEST WORK '+str(work),flush=True)
    mapping=dict(zip('qwertyuiopasdfghjklzxcvbnm',[16,17,18,19,20,21,22,23,24,25,30,31,32,33,34,35,36,37,38,44,45,46,47,48,49,50]))
    mapping.update({'.':52,'\\':43,'/':53,' ':57,'-':12})
    phases=('upgrade',) if args.upgrade_from else ('install','reboot-remove')
    for phase in phases:
        output=work/phase;output.mkdir()
        launch=output/'launch.json'
        launch.write_text(json.dumps(dict(machine=str(branch/'branch.dospc.json'),
            frontend=str(ROOT/'artifacts/glide-bridge/frontend/DOSEmulator.dll'),output=str(output))))
        env=dict(os.environ,DOSE_GLIDE_HOST_DLL=str(ROOT/'artifacts/glide-bridge/host/Glide2x-dgVoodoo-x64.dll'))
        error=(output/'stderr.txt').open('w')
        proc=subprocess.Popen([str(ROOT/'artifacts/glide-bridge/controller/Probe.exe'),str(launch)],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=error,text=True,encoding='utf-8',env=env)
        log=(output/'commands.jsonl').open('w')
        ready=json.loads(proc.stdout.readline());assert ready['ready']
        def call(command,**values):
            """Send one runner command, record its response and reject guest-runner failures."""
            request=dict(command=command,**values);proc.stdin.write(json.dumps(request)+'\n');proc.stdin.flush()
            line=proc.stdout.readline()
            if not line:raise RuntimeError('Guest runner ended unexpectedly')
            response=json.loads(line);log.write(json.dumps(dict(request=request,response=response))+'\n');log.flush()
            if not response.get('ok'):raise RuntimeError(response)
            return response['result']
        def run(ms):
            """Advance the test guest for a bounded virtual duration."""
            return call('run',milliseconds=ms)
        def keys(codes):
            """Inject translated PS/2 scan codes into the isolated guest keyboard."""
            call('keys',scanCodes=codes)
        def key(code):
            """Press and release one guest key with time for its message processing."""
            keys([code]);run(70);keys([code|128]);run(70)
        def picture(name):
            """Capture the guest framebuffer as both raw proof and a viewable PNG."""
            call('status',ppm=name+'.ppm');Image.open(output/(name+'.ppm')).save(output/(name+'.png'))
        def start(command,delay=1500):
            """Open the guest Run dialog and type a command using its keyboard layout."""
            keys([224,91,19]);run(150);keys([147,224,219]);run(900)
            keys([29,30]);run(80);keys([158,157]);run(80)
            for char in command:
                if version=='95' and char==':':keys([42,52]);run(70);keys([180,170]);run(70)
                elif version=='95' and char=='\\':keys([29,56,12]);run(70);keys([140,184,157]);run(70)
                elif version=='95' and char=='/':keys([42,8]);run(70);keys([136,170]);run(70)
                elif version=='95' and char=='y':key(44)
                elif version=='95' and char=='-':key(53)
                elif char==':':keys([42,39]);run(70);keys([167,170]);run(70)
                else:key(mapping[char])
            key(28);run(delay)
        try:
            for part in range(3):run(30000);print(version+' '+phase+' BOOT '+str((part+1)*30),flush=True)
            picture('desktop')
            if phase in ('install','upgrade'):
                start('d:\\check.exe /seed')
                start('d:\\check.exe /wrong')
                if phase=='upgrade':
                    start('d:\\check.exe /readonly-maintenance')
                    start('d:\\check.exe /tools-only-install',4000)
                    start('d:\\check.exe /tools-only-check')
                start('d:\\setup.exe');picture('component-selection')
                start('d:\\check.exe /install-ui',5000);picture('installation-complete')
                start('d:\\check.exe /close-ui')
                start('d:\\check.exe /installed')
                start('c:\\dosetools\\mglide.exe /preview',250);picture('miniglide-preview');run(2500)
            if phase in ('reboot-remove','upgrade'):
                if phase=='reboot-remove':start('d:\\check.exe /reboot-check')
                start('c:\\dosetools\\dsetup.exe /uninstall');picture('remove-confirmation')
                start('d:\\check.exe /confirm-remove',4000);picture('removal-complete')
                start('d:\\check.exe /close-ui')
                start('d:\\check.exe /removed')
            start('d:\\check.exe /shutdown',1000)
            run(20000);picture('shutdown')
            call('exit');proc.wait(20)
        finally:
            if proc.poll() is None:proc.terminate();proc.wait(20)
            error.close();log.close()
        data=read_guest_file(branch,'PKGTEST.INI')
        if data is None:raise RuntimeError('The guest did not write its test report')
        (work/('guest-'+phase+'.ini')).write_bytes(data)
        report=configparser.ConfigParser();report.read_string(data.decode('cp1252'))
        sections={'install':['WrongVersion','Installed'],'reboot-remove':['Reboot','Removed'],
                  'upgrade':['WrongVersion','ToolsOnly','Installed','Removed']}[phase]
        for section in sections:
            assert section in report,(section,data)
            assert all(value=='1' for key,value in report[section].items() if key not in ('failures','exitcode')),(section,dict(report[section]))
            assert report[section].get('failures','0')=='0',(section,dict(report[section]))
        print(version+' '+phase+' PASSED '+str({s:dict(report[s]) for s in sections}),flush=True)
    (work/'result.json').write_text(json.dumps(dict(passed=True,windows=version,work=str(work),
        installerSha256=built['installerSha256'],upgradeFixture=str(args.upgrade_from) if args.upgrade_from else None),indent=2))


if __name__=='__main__':main()
