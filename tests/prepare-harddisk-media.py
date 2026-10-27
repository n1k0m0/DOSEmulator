# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create a FAT12 disk containing only our original four-hard-disk probe."""
import importlib.util
import json
from pathlib import Path
import sys


def prepare(directory, destination):
    """Reuse the independently read-back FAT12 writer with explicit own-program names and label."""
    spec = importlib.util.spec_from_file_location('own_floppy', Path(__file__).with_name('prepare-et4000-media.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.prepare(directory, destination, ('HD4TEST',), b'HD4TEST ', b'HD4 TEST   ')


if __name__ == '__main__':
    print(json.dumps(prepare(*sys.argv[1:]), indent=2))
