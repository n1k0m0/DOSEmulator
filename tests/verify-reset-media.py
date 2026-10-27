# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent, read-only FAT/journal comparison after actual guest resets."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

spec=importlib.util.spec_from_file_location("fat_oracle",Path(__file__).with_name("verify-msdos-install.py"))
oracle=importlib.util.module_from_spec(spec);spec.loader.exec_module(oracle)

def verify(base, original, modified, name, expected):
    """Validates reset media evidence from actual guest state, immutable program bytes and independently expected results."""
    base=Path(base)
    before=base.read_bytes() if original=="-" else oracle.materialize(base,original)[0]
    after,journal=oracle.materialize(base,modified)
    def files(data):
        """Reads and validates the named FAT files from the supplied private media snapshot."""
        start=struct.unpack_from("<I",data,454)[0]*512
        return oracle.Fat(data,start).files()
    old,_=files(before);new,allocation=files(after)
    assert name not in old and set(new)==set(old)|{name}
    assert new[name]==expected.encode("ascii")+b"\r\n",new[name]
    for path,data in old.items():assert new[path]==data,("Changed original file",path)
    return {"passed":True,"readOnlyVerification":True,"originalFiles":len(old),"files":len(new),
        "onlyNewFile":name,"originalFilesUnchanged":True,"allocation":allocation,"journal":journal,
        "newFileSha256":hashlib.sha256(new[name]).hexdigest().upper()}

if __name__=="__main__":print(json.dumps(verify(*sys.argv[1:]),indent=2))
