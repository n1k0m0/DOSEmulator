# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Materialize a read-only DOSCOW01 source into a NEW private test base."""
import importlib.util
import json
from pathlib import Path
import sys

spec=importlib.util.spec_from_file_location('fat',Path(__file__).with_name('verify-msdos-install.py'))
fat=importlib.util.module_from_spec(spec);spec.loader.exec_module(fat)
base,overlay,destination=map(Path,sys.argv[1:4])
disk,journal=fat.materialize(base,overlay)
with destination.open('xb') as output:output.write(disk)
print(json.dumps(dict(passed=True,newPrivateBase=str(destination),bytes=len(disk),sha256=fat.sha(disk),journal=journal),indent=2))
