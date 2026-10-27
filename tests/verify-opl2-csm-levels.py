# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Check a level change really waits for the CSM edge, using real DOS OUTs/audio."""
import argparse, array, hashlib, json, math, re, sys, wave
from pathlib import Path
p=argparse.ArgumentParser()
for name in ('report','audio','io'):
    p.add_argument('--'+name,required=True,type=Path)
a=p.parse_args()
r=json.loads(a.report.read_text(encoding='utf-8-sig'))
io=[json.loads(line) for line in a.io.read_text(encoding='utf-8-sig').splitlines()]
writes=[]
for e in io:
    if e['write'] and e['port']==0x388:
        address=e['value']
    elif e['write'] and e['port']==0x389:
        writes.append((e['time'],address,e['value']))
assert not any(v&0x20 for _,reg,v in writes if 0xb0<=reg<=0xb8)
levels=[(t,v) for t,reg,v in writes if reg==0x43]
assert [v for _,v in levels]==[0,32,0]
mode_off=next(t for t,reg,v in writes if reg==8 and v==0)
start=[t for t,reg,v in writes if reg==4 and v==0x41]
stop=[t for t,reg,v in writes if reg==4 and v==0x40]
assert len(start)==len(stop)==2
assert start[0]<stop[0]<levels[1][0]<start[1]<stop[1]<levels[2][0]<mode_off
with wave.open(str(a.audio),'rb') as w:
    assert (w.getnchannels(),w.getsampwidth(),w.getframerate())==(2,2,48000)
    frames=w.getnframes();data=w.readframes(frames)
pcm=array.array('h',data)
if sys.byteorder!='little':pcm.byteswap()
assert pcm[::2]==pcm[1::2]
mono=pcm[::2]
def rms(begin,end):
    """Measures RMS over the explicit captured sample window, independently from the synthesizer."""
    chunk=mono[(begin+40000000)*48000//1000000000:(end-10000000)*48000//1000000000]
    assert len(chunk)>2000
    return math.sqrt(sum(x*x for x in chunk)/len(chunk))
before=rms(stop[0],levels[1][0]);pending=rms(levels[1][0],start[1])
latched=rms(start[1],stop[1]);again=rms(levels[2][0],mode_off)
released=next(t for t,reg,v in writes if reg==0x83 and v==15)
flushed=rms(mode_off,released)
assert .98<pending/before<1.02
assert .98<again/latched<1.02
assert .98<flushed/before<1.02
ratio=10**(-24/20)
assert .98<latched/before/ratio<1.02
assert max(map(abs,mono[-4800:]))<=1
assert r['opl']['csmTotalLevelLatch'] and r['opl']['csm'] and not r['opl']['csmEnabled']
assert r['opl']['rejectedWrites']==r['opl']['activeOperators']==0
assert not r['opl']['unsupportedProgramming'] and not r['opl']['timerInterruptAsserted']
assert r['status']['fault'] is None and r['scenario']['complete']
assert r['scenario']['inputBytesSent']==r['scenario']['inputBytesTotal']
assert r['ataSectorsWritten']==r['floppySectorsWritten']==0
assert re.search(r'\r\nCSM-LEVEL-RUN-END\r\n(?:\r\n)?C:\\>$',r['status']['debugOutput'])
assert frames==r['status']['virtualNanoseconds']*48000//1000000000
assert hashlib.sha256(data).hexdigest().upper()==r['audio']['dataSha256']
print(json.dumps(dict(passed=True,verifiedPcmFrames=frames,heldLevelRatio=pending/before,
    latchedLevelRatio=latched/before,expected24DbRatio=ratio,heldSecondRatio=again/latched,
    disabledCsmFlushRatio=flushed/before,guestReturned=True,siliconComparison=False)))
