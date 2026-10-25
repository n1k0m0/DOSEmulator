"""Audit DOS-written private HIMEM/EMM386 configurations, retaining every original file."""
import argparse
import importlib.util
import json
from pathlib import Path
import struct

s=importlib.util.spec_from_file_location("fat_oracle",Path(__file__).with_name("verify-msdos-install.py"))
fat=importlib.util.module_from_spec(s);s.loader.exec_module(fat)

def verify(base,overlay,mode):
    before=Path(base).read_bytes();after,journal=fat.materialize(base,overlay);start=struct.unpack_from("<I",before,454)[0]*512
    old,_=fat.Fat(before,start).files();new,allocation=fat.Fat(after,start).files()
    assert "CONFIG.PRE" not in old and set(new)==set(old)|{"CONFIG.PRE"}
    assert new["CONFIG.PRE"]==old["CONFIG.SYS"]
    prefix=b"DEVICE=C:\\DOS\\HIMEM.SYS /TESTMEM:ON \r\n"
    if mode!="HIMEM":prefix+=b"DEVICE=C:\\DOS\\EMM386.EXE "+mode.encode()+b" \r\n"
    assert new["CONFIG.SYS"]==prefix+old["CONFIG.SYS"]
    for name,data in old.items():
        if name!="CONFIG.SYS":assert new[name]==data,name
    return dict(passed=True,mode=mode,files=len(new),originalFiles=len(old),allOtherFilesUnchanged=True,
                allocation=allocation,journal=journal)

if __name__=="__main__":
    p=argparse.ArgumentParser();p.add_argument("--base",type=Path,required=True);p.add_argument("--overlay",type=Path,required=True);p.add_argument("--mode",choices=("HIMEM","NOEMS","RAM"),required=True)
    a=p.parse_args();print(json.dumps(verify(a.base,a.overlay,a.mode),indent=2))
