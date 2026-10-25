"""Independent own-opcode/control-word/UD-frame checks; no x87 arithmetic claim."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path,memory_path,program_path,system):
    r=json.loads(Path(report_path).read_text(encoding="utf-8-sig"));m=Path(memory_path).read_bytes();p=Path(program_path).read_bytes()
    cpu=r["cpu"];output=r["status"]["debugOutput"]
    assert r["status"]["fault"] is None and r["scenario"]["complete"]
    assert r["scenario"]["inputBytesSent"]==r["scenario"]["inputBytesTotal"]
    assert r["ataSectorsWritten"]==r["floppySectorsWritten"]==r["hardwareResetCount"]==0 and r["floppySectorsRead"]>0
    assert "CPU-X87-PASS\r\n" in output and "CPU-X87-FAIL" not in output
    assert ("FreeCom version 0.86" if system=="freedos" else "MS-DOS Version 6.22") in output
    assert re.search(r"\r\nCPU-X87-GUEST-END\r\n(?:\r\n)?C:\\>$",output)
    markers=re.findall(r"CPU-X87-PROOF=([0-9A-F]{8})\r\n",output);assert len(markers)==1
    address=int(markers[0],16);raw=lambda a:struct.unpack_from("<I",m,a)[0]
    def physical(a):
        if not cpu["pagingEnabled"]:return a
        pde=raw((cpu["cr3"]&~4095)+(a>>22)*4);assert pde&1
        pte=raw((pde&~4095)+((a>>12)&1023)*4);assert pte&1
        return (pte&~4095)|(a&4095)
    read=lambda a,n:bytes(m[physical(a+i)] for i in range(n))
    proof=read(address,128);u16=lambda n:struct.unpack_from("<H",proof,n)[0];u32=lambda n:struct.unpack_from("<I",proof,n)[0]
    labels=struct.unpack_from("<5H",p,p.index(b"CPXFADDR")+8);label_proof,ud,after,code_end,handler=labels
    assert proof[:8]==b"CPXF0001" and [u32(n) for n in (8,12,16)]==[1,1,15]
    assert u32(24)&0x200000==0 and (u32(28)^u32(20))&0x40000==0x40000
    assert [u16(n) for n in (32,34,36,38,54,56)]==[0x37f,0x37f,0,0xb7f,0x37f,0xb7f]
    assert u32(40)==0xa55a0000 and u16(44)==1 and u16(46)==ud and u16(48)==u16(52) and u32(64)==0
    basis=u16(52)<<4;assert basis+label_proof==address
    assert p[ud-0x100:after-0x100]==bytes.fromhex("0fa2") and read(basis+0x100,code_end-0x100)==p[:code_end-0x100]
    assert cpu["x87Instructions"]==13 and cpu["fpuControlWord"]==0x37f and cpu["fpuStatusWord"]==0 and cpu["fpuTagWord"]==0xffff
    assert cpu["eflags"]&0x380000==0 and not cpu["tripleFaulted"]
    managed=system.startswith("emm386")
    assert bool(u16(60)&1)==managed and cpu["protectedMode"]==cpu["pagingEnabled"]==managed
    if managed:assert cpu["virtual8086Entries"]>1000 and cpu["virtual8086Entries"]-cpu["virtual8086Exits"] in (0,1)
    else:assert not cpu["virtual8086"]
    return dict(passed=True,system=system,proofSha256=hashlib.sha256(proof).hexdigest().upper(),
                cpuidUdFrame=True,idBitFixed=True,acBitWritable=True,controlRestored=True,x87Instructions=13,
                guestReturned=True,arithmeticTested=False,register80BitTested=False)


def negative_controls(report,memory,program,system,directory):
    verify(report,memory,program,system);r=json.loads(Path(report).read_text(encoding="utf-8-sig"));m=Path(memory).read_bytes();p=Path(program).read_bytes()
    a=int(re.search(r"CPU-X87-PROOF=([0-9A-F]{8})",r["status"]["debugOutput"])[1],16)
    directory=Path(directory);directory.mkdir(exist_ok=False);mp=directory/"corrupted.private-ram.bin";rp=directory/"corrupted.json";pp=directory/"corrupted.com";controls=[]
    def reject(name,rr=report,mm=memory,pr=program):
        try:verify(rr,mm,pr,system)
        except AssertionError:controls.append(dict(name=name,rejected=True))
        else:raise AssertionError("Accepted corrupted x87 proof: "+name)
    assert m[a:a+8]==b"CPXF0001"
    for offset in (26,34,46,54):
        changed=bytearray(m);changed[a+offset]^=0x20 if offset==26 else 1;mp.write_bytes(changed);reject("proof-"+str(offset),mm=mp)
    changed=bytearray(p);changed[0]^=1;pp.write_bytes(changed);reject("executed-opcode",pr=pp)
    changed=copy.deepcopy(r);changed["cpu"]["x87Instructions"]=0;rp.write_text(json.dumps(changed),encoding="utf-8");reject("x87-counter",rr=rp)
    return dict(passed=True,negativeControls=controls)


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    for name in ("report","memory","program"):parser.add_argument("--"+name,type=Path,required=True)
    parser.add_argument("--system",choices=("msdos","freedos","emm386-noems","emm386-ram"),required=True);parser.add_argument("--negative-controls",type=Path)
    a=parser.parse_args();args=(a.report,a.memory,a.program,a.system)
    print(json.dumps(negative_controls(*args,a.negative_controls) if a.negative_controls else verify(*args),indent=2))
