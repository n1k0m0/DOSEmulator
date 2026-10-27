# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Compare actual DOS FM traffic/audio to a separately built YMFM executable.

Checks functional timing, all five percussion voices, gain relative to one
melodic calibration voice, tonal pitch and noise activity. This is deliberately
not a bit-exact silicon assertion: both the envelope model and FM depth differ.
"""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import wave

p=argparse.ArgumentParser()
for name in ('report','audio','io','reference-exe'):
    p.add_argument('--'+name,required=True,type=Path)
a=p.parse_args()
r=json.loads(a.report.read_text(encoding='utf-8-sig'))
io=[json.loads(line) for line in a.io.read_text(encoding='utf-8-sig').splitlines()]
writes=[e for e in io if e['write'] and e['port'] in (0x388,0x389,0x228,0x229)]
assert all(e['width']==1 for e in writes)
registers=[]
address=None
last=0
for e in writes:
    assert e['time'] >= last
    if e['port']%2==0:
        address=e['value']
        address_time=e['time']
        assert e['time']-last >= 23467 or not registers
    else:
        assert e['time']-address_time>=3353
        registers.append((e['time'],address,e['value']))
    last=e['time']
bd=[(t,v) for t,reg,v in registers if reg==0xbd]
assert [v for _,v in bd]==[0x20,0x30,0x20,0x21,0x20,0x28,0x20,0x24,0x20,0x22,0x20,0x3f,0]
assert r['opl']['rhythm'] and not r['opl']['rhythmEnabled'] and not r['opl']['unsupportedProgramming']
assert r['opl']['rejectedWrites']==r['opl']['activeOperators']==0
assert r['status']['fault'] is None and r['scenario']['complete']
assert r['scenario']['inputBytesSent']==r['scenario']['inputBytesTotal']
assert r['ataSectorsWritten']==r['floppySectorsWritten']==0
assert re.search(r'\r\nRHYTHM-RUN-END\r\n(?:\r\n)?C:\\>$',r['status']['debugOutput'])
if r['soundBlaster']:
    assert r['soundBlaster']['transferredSamples']==r['soundBlaster']['interruptRequests']==0

events=a.report.with_suffix('.ymfm-events.txt')
events.write_text(''.join(f"{e['time']} {e['port']%2} {e['value']}\n" for e in writes))
reference=a.report.with_suffix('.ymfm.private.s32')
subprocess.run([str(a.reference_exe),str(events),str(reference),str(r['status']['virtualNanoseconds'])],check=True)
chip=array.array('i',reference.read_bytes())
if sys.byteorder!='little':
    chip.byteswap()
expected_native=r['status']['virtualNanoseconds']*3579545//72000000000
assert len(chip)==expected_native==r['opl']['generatedSamples']
with wave.open(str(a.audio),'rb') as wav:
    assert (wav.getnchannels(),wav.getsampwidth(),wav.getframerate())==(2,2,48000)
    frames=wav.getnframes()
    raw=wav.readframes(frames)
pcm=array.array('h',raw)
if sys.byteorder!='little':
    pcm.byteswap()
assert pcm[::2]==pcm[1::2]
assert frames==r['status']['virtualNanoseconds']*48000//1000000000
actual=[x/32768 for x in pcm[::2]]

# Integrate the independent chip's held native samples to our host rate and
# apply the documented shared 20-Hz DC filter. No emulator waveform enters it.
reference_frames=[]
pos=index=0
value=previous=filtered=0.
decay=math.exp(-2*math.pi*20/48000)
next_sample=(72000000000+3579544)//3579545
for frame in range(frames):
    end=((frame+1)*1000000000+47999)//48000
    begin,area=pos,0.
    while pos<end:
        boundary=min(end,next_sample)
        area+=(boundary-pos)*value
        pos=boundary
        if boundary==next_sample:
            value=chip[index]/32768
            index+=1
            next_sample=((index+1)*72000000000+3579544)//3579545
    sample=area/(end-begin)
    filtered=decay*(filtered+sample-previous)
    previous=sample
    reference_frames.append(filtered)

def rms(values):
    """Measures RMS over the explicit captured sample window, independently from the synthesizer."""
    return math.sqrt(sum(x*x for x in values)/len(values))
def pitch(values):
    """Estimates signal frequency from the captured waveform independently from the chip's phase accumulator."""
    rises=[i for i in range(1,len(values)) if values[i-1]<0<=values[i]]
    assert len(rises)>10
    return (len(rises)-1)*48000/(rises[-1]-rises[0])
def window(values,start,end):
    """Selects the exact captured percussion interval using the independently observed guest markers."""
    return values[(start+80000000)*48000//1000000000:(end-30000000)*48000//1000000000]
melody_on=next(t for t,reg,v in registers if reg==0xb8 and v&0x20)
melody_off=next(t for t,reg,v in registers if t>melody_on and reg==0xb8 and not v&0x20)
cal_actual=window(actual,melody_on,melody_off)
cal_ref=window(reference_frames,melody_on,melody_off)
gain=rms(cal_actual)/rms(cal_ref)
melody_pitch,pitch_ref=pitch(cal_actual),pitch(cal_ref)
assert abs(melody_pitch/pitch_ref-1)<0.005
assert 0.05<gain<2
metrics=[]
for i,name in enumerate(('bass','hat','snare','tom','cymbal','mix')):
    on,off=bd[1+i*2][0],bd[2+i*2][0]
    x,y=window(actual,on,off),window(reference_frames,on,off)
    assert rms(x)>0.001 and rms(y)>0.001
    ratio=rms(x)/(rms(y)*gain)
    assert 0.95<ratio<1.05,(name,ratio)
    m=dict(instrument=name,actualRms=rms(x),ymfmRms=rms(y),relativeGain=ratio)
    if name in ('bass','tom'):
        f,g=pitch(x),pitch(y)
        assert abs(f/g-1)<0.005,(name,f,g)
        m.update(actualHz=f,ymfmHz=g)
    if name in ('hat','snare','cymbal'):
        crossings=lambda values: sum((values[i-1]<0)!=(values[i]<0) for i in range(1,len(values)))
        nx,ny=crossings(x),crossings(y)
        assert nx>20 and ny>20 and .85<nx/ny<1.15,(name,nx,ny)
        m.update(actualZeroCrossings=nx,ymfmZeroCrossings=ny)
    metrics.append(m)
assert rms(actual[-4800:])<1e-5
digest=hashlib.sha256(raw).hexdigest().upper()
assert digest==r['audio']['dataSha256']
print(json.dumps(dict(passed=True,independentYmfmComparison=True,siliconExact=False,
                      nativeSamples=expected_native,verifiedPcmFrames=frames,pcmSha256=digest,
                      calibrationGain=gain,melodyHz=melody_pitch,ymfmMelodyHz=pitch_ref,
                      metrics=metrics,guestReturned=True)))
