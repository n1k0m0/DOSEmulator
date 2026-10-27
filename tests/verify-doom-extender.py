# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Private original-DOS4GW/Doom oracle: real demo return, media and WAD-derived HUD.

The local game is only read. No copyrighted pixels, executable or WAD bytes
are embedded here. The comparison decodes the user's own STBAR and PLAYPAL.
"""
import argparse
import collections
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct

spec=importlib.util.spec_from_file_location("fat_oracle",Path(__file__).with_name("verify-msdos-install.py"))
fat=importlib.util.module_from_spec(spec);spec.loader.exec_module(fat)


def wad_lumps(game):
    """Parses the local WAD directory with checked offsets for the private frame oracle."""
    data=(Path(game)/"DOOM2.WAD").read_bytes()
    assert data[:4]==b"IWAD"
    count,offset=struct.unpack_from("<II",data,4)
    return {name.rstrip(b"\0").decode("ascii"):data[a:a+n]
            for a,n,name in (struct.unpack_from("<II8s",data,offset+i*16) for i in range(count))}


def hud_reference(game):
    """Builds expected fixed HUD pixels from the local WAD resources without exporting those resources."""
    lumps=wad_lumps(game);patch=lumps["STBAR"]
    width,height,left,top=struct.unpack_from("<4h",patch)
    assert (width,height,left,top)==(320,32,0,0)
    # Gamma level zero's integer transfer followed by the VGA six-bit DAC.
    def component(value):
        """Reads the requested color component from the independently selected palette entry."""
        dac=(value+(value<128))>>2
        return (dac<<2)|(dac>>4)
    palette=[bytes(component(v) for v in lumps["PLAYPAL"][i:i+3]) for i in range(0,768,3)]
    pixels={}
    for x in range(width):
        p=struct.unpack_from("<I",patch,8+x*4)[0]
        while patch[p]!=255:
            y,length=patch[p:p+2];p+=3
            assert y+length<=height
            for j in range(length):pixels[x,y+j]=palette[patch[p+j]]
            p+=length+1
    # Static background/labels, excluding the live numbers, face, keys and arms.
    rectangles=[(0,43,3,18),(49,103,3,18),(104,143,0,31),(143,178,0,31),
                (179,234,3,18),(235,247,2,30),(272,319,3,31)]
    mask={(x,y):c for (x,y),c in pixels.items() if not any(x0<=x<=x1 and y0<=y<=y1 for x0,x1,y0,y1 in rectangles)}
    assert len(mask)>3000
    return mask,set(palette)


def argb_hash(rgb):
    """Hashes the exact rendered ARGB bytes for comparison with the saved guest frame."""
    bgra=bytearray(len(rgb)//3*4)
    bgra[0::4]=rgb[2::3];bgra[1::4]=rgb[1::3];bgra[2::4]=rgb[0::3];bgra[3::4]=b"\xff"*(len(rgb)//3)
    return hashlib.sha256(bgra).hexdigest().upper()


def verify(report_path,base,baseline,overlay,game,mode):
    """Validates doom extender evidence from actual guest state, immutable program bytes and independently expected results."""
    r=json.loads(Path(report_path).read_text(encoding="utf-8-sig"));cpu=r["cpu"];output=r["status"]["debugOutput"]
    assert r["status"]["fault"] is None and r["scenario"]["complete"]
    assert r["scenario"]["inputBytesSent"]==r["scenario"]["inputBytesTotal"] and r["hardwareResetCount"]==0
    assert r["ataSectorsRead"]>10000 and r["floppySectorsWritten"]==r["floppySectorsRead"]==0
    assert "DOS/4GW Professional Protected Mode Run-time  Version 1.95\r\n" in output
    assert "adding doom2.wad" in output and "adding fastdemo.lmp" in output and "I_StartupDPMI" in output
    match=re.findall(r"\r\ntimed (\d+) gametics in (\d+) realtics\r\n",output)
    assert len(match)==1 and int(match[0][0])==32 and int(match[0][1])>0
    assert re.search(r"\r\nDOOM-GUEST-END\r\n\r\nC:\\>$",output) and "MS-DOS Version 6.22" in output
    assert cpu["protectedInstructions"]>50_000_000 and cpu["x87Instructions"]>=15 and cpu["pageFaults"]==0
    assert cpu["fpuControlWord"]==0x37f and cpu["fpuStatusWord"]==0 and cpu["fpuTagWord"]==0xffff
    assert cpu["eflags"]&0x380000==0 and not cpu["tripleFaulted"]
    managed=mode in ("NOEMS","RAM")
    if managed:
        assert cpu["protectedMode"] and cpu["pagingEnabled"] and cpu["virtual8086Entries"]>10000
        assert cpu["virtual8086Entries"]-cpu["virtual8086Exits"] in (0,1)
        assert cpu["tr"]==40 and cpu["trCache"]["access"]==0x8b and cpu["tlbInvalidations"]>1000
    else:
        assert not cpu["protectedMode"] and not cpu["pagingEnabled"] and not cpu["virtual8086"]
        assert cpu["protectedModeEntries"]==cpu["protectedModeExits"] and cpu["protectedModeEntries"]>1000
    if mode!="none":assert "HIMEM testet den erweiterten Speicher...beendet." in output
    before=fat.materialize(base,baseline)[0] if baseline is not None else Path(base).read_bytes()
    after,journal=fat.materialize(base,overlay)
    start=struct.unpack_from("<I",before,454)[0]*512
    old,_=fat.Fat(before,start).files();new,allocation=fat.Fat(after,start).files()
    assert set(old)==set(new)
    changed=[name for name in old if old[name]!=new[name]]
    assert changed==[],changed
    for name in ("DOOM2.EXE","DOOM2.WAD"):assert old[name]==new[name]==(Path(game)/name).read_bytes()
    assert old["FASTDEMO.LMP"]==bytes([109,2,1,1,0,0,0,1,0,1,0,0,0])+bytes([12,0,4,0])*32+b"\x80"
    # Original timedemo finishes through I_Error; this path does not save defaults.
    assert r["ataSectorsWritten"]==0
    capture=r["graphicsCapture"];frames=capture["frames"]
    assert 2<=len(frames)<=128 and capture["intervalNanoseconds"]==100000000
    hud,palette=hud_reference(game);good=[];viewport_hashes=set();scene_hashes=[]
    for frame in frames:
        assert Path(frame["filename"]).name==frame["filename"]
        ppm=(Path(capture["directory"])/frame["filename"]).read_bytes();magic,dimensions,maximum,rgb=ppm.split(b"\n",3)
        assert magic==b"P6" and maximum==b"255" and dimensions==f"{frame['width']} {frame['height']}".encode()
        assert len(rgb)==frame["width"]*frame["height"]*3 and argb_hash(rgb)==frame["frameHash"]
        if (frame["width"],frame["height"])!=(320,200):continue
        colors=collections.Counter(rgb[i:i+3] for i in range(0,len(rgb),3))
        matches=sum(rgb[((y+168)*320+x)*3:((y+168)*320+x)*3+3]==c for (x,y),c in hud.items())
        if matches!=len(hud) or len(colors)<64:continue
        assert set(colors)<=palette
        good.append(dict(filename=frame["filename"],staticHudPixelsCompared=matches,colors=len(colors),frameHash=frame["frameHash"]))
        viewport_hashes.add(hashlib.sha256(rgb[:320*168*3]).hexdigest())
        scene_hashes.append(frame["frameHash"])
    assert len(good)>=2 and len(viewport_hashes)>=2,"No changing WAD-verified game scene"
    return dict(passed=True,mode=mode,originalDos4gw="1.95 Professional",gameTicks=32,realtics=int(match[0][1]),
                sampledGraphicsFrames=len(frames),wadVerifiedSceneFrames=len(good),differentViewports=len(viewport_hashes),
                staticHudPixelsCompared=len(hud),sceneFrames=good,sceneSequenceSha256=hashlib.sha256("".join(scene_hashes).encode()).hexdigest().upper(),
                protectedInstructions=cpu["protectedInstructions"],virtual8086Entries=cpu["virtual8086Entries"],
                x87Instructions=cpu["x87Instructions"],returnedToDos=True,gameAndDosFilesUnchanged=True,
                changedFiles=changed,mediaWrites=0,allocation=allocation,journal=journal,
                soundTested=False,nativeKeyboardOrMouseTested=False,fullGameCompatibility=False)


def negative_controls(report_path,base,baseline,overlay,game,mode,directory):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    verify(report_path,base,baseline,overlay,game,mode)
    report=json.loads(Path(report_path).read_text(encoding="utf-8-sig"));directory=Path(directory);directory.mkdir(exist_ok=False)
    rp=directory/"corrupted-report.json";controls=[]
    def reject(name,changed):
        """Requires an independently corrupted input to fail instead of accepting the guest's success marker alone."""
        rp.write_text(json.dumps(changed),encoding="utf-8")
        try:verify(rp,base,baseline,overlay,game,mode)
        except AssertionError:controls.append(dict(name=name,rejected=True))
        else:raise AssertionError("Accepted corrupted Doom evidence: "+name)
    for key,value in (("protectedInstructions",0),("x87Instructions",0),("pageFaults",1)):
        changed=copy.deepcopy(report);changed["cpu"][key]=value;reject("cpu-"+key,changed)
    changed=copy.deepcopy(report);changed["status"]["debugOutput"]=changed["status"]["debugOutput"].replace("timed 32 gametics","timed 31 gametics");reject("missing-demo-tick",changed)
    changed=copy.deepcopy(report);changed["scenario"]["complete"]=False;reject("unfinished-guest-return",changed)
    changed=copy.deepcopy(report);changed["graphicsCapture"]["frames"]=[];reject("missing-graphics",changed)
    changed=copy.deepcopy(report);changed["graphicsCapture"]["frames"][0]["frameHash"]="0"*64;reject("scanout-hash",changed)
    changed=copy.deepcopy(report);black_dir=directory/"black-frames";black_dir.mkdir()
    changed["graphicsCapture"]["directory"]=str(black_dir)
    for frame in changed["graphicsCapture"]["frames"]:
        rgb=bytes(frame["width"]*frame["height"]*3);frame["frameHash"]=argb_hash(rgb)
        (black_dir/frame["filename"]).write_bytes(f"P6\n{frame['width']} {frame['height']}\n255\n".encode()+rgb)
    reject("internally-consistent-black-frames",changed)
    return dict(passed=True,negativeControls=controls)


if __name__=="__main__":
    p=argparse.ArgumentParser()
    for name in ("report","base","overlay","game"):p.add_argument("--"+name,type=Path,required=True)
    p.add_argument("--baseline",type=Path)
    p.add_argument("--mode",choices=("none","HIMEM","NOEMS","RAM"),required=True);p.add_argument("--negative-controls",type=Path)
    a=p.parse_args();args=(a.report,a.base,a.baseline,a.overlay,a.game,a.mode)
    print(json.dumps(negative_controls(*args,a.negative_controls) if a.negative_controls else verify(*args),indent=2))
