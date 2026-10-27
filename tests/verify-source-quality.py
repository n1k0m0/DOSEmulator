# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Check original source headers and Python function documentation without opening private artifacts."""
import ast
import collections
import json
import re
from pathlib import Path
import sys


def verify(root):
    """Inspect only repository source trees, retaining full third-party Apache notices and excluding build outputs."""
    extensions = {'.cs', '.py', '.ps1', '.asm', '.inc', '.cpp', '.shader', '.cmd', '.props', '.csproj', '.slnx'}
    files = [p for folder in ('src', 'tests', 'scripts', 'firmware') for p in (root / folder).rglob('*')
             if p.is_file() and p.suffix in extensions and not {'bin', 'obj', '__pycache__'} & set(p.parts)]
    files += [p for p in root.iterdir() if p.is_file() and p.suffix in extensions]
    problems, counts, functions, shader_functions, assembly_routines, native_functions = [], collections.Counter(), 0, 0, 0, 0
    for path in files:
        text = path.read_text(encoding='utf-8-sig')
        counts[path.suffix] += 1
        if 'SPDX-License-Identifier:' not in text[:1800] and 'Licensed under the Apache License' not in text[:1800]:
            problems.append(f'{path.relative_to(root)}: missing license header')
        if path.suffix == '.py':
            tree = ast.parse(text, str(path))
            for node in ast.walk(tree):
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    functions += 1
                    if not ast.get_docstring(node):
                        problems.append(f'{path.relative_to(root)}:{node.lineno}: {node.name} lacks a docstring')
        if path.suffix == '.shader':
            # Preserve original notices, but ignore inactive example code in block comments.
            code = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
            lines = code.splitlines()
            for index, line in enumerate(lines):
                if re.match(r'^(?:void|bool|int|float|vec[234])\s+\w+\(', line):
                    shader_functions += 1
                    if not index or not lines[index-1].strip().startswith('//'):
                        problems.append(f'{path.relative_to(root)}: shader function lacks a purpose comment')
            for lo, hi in re.findall(r'\bsmoothstep\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,', code):
                if float(lo) >= float(hi):
                    problems.append(f'{path.relative_to(root)}: undefined reversed smoothstep edges')
        if path.suffix in ('.asm', '.inc'):
            called = set(re.findall(r'\bcall\s+([A-Za-z_]\w*)', text, re.I))
            lines = text.splitlines()
            for index, line in enumerate(lines):
                label = re.match(r'^(\w+):', line)
                if label and (label[1] in called or label[1] in ('start', 'entry')):
                    assembly_routines += 1
                    previous = index - 1
                    while previous >= 0 and not lines[previous].strip():
                        previous -= 1
                    if previous < 0 or not lines[previous].lstrip().startswith(';'):
                        problems.append(f'{path.relative_to(root)}:{index+1}: callable assembly entry lacks a comment')
        if path.suffix == '.cpp':
            lines = text.splitlines()
            for index, line in enumerate(lines):
                if re.match(r'\s*(?:void|int)\s+\w+\([^;]*\)\s*(?:override\s*)?\{', line):
                    native_functions += 1
                    if not index or not lines[index-1].strip().startswith('//'):
                        problems.append(f'{path.relative_to(root)}:{index+1}: native adapter function lacks a comment')
    assert (root / 'LICENSE').is_file() and (root / 'REUSE.toml').is_file()
    return dict(passed=not problems, files=len(files), extensions=dict(sorted(counts.items())), pythonFunctions=functions,
                shaderFunctions=shader_functions, assemblyCallableEntries=assembly_routines, nativeAdapterFunctions=native_functions, problems=problems)


if __name__ == '__main__':
    result = verify(Path(__file__).resolve().parents[1])
    print(json.dumps(result, indent=2))
    sys.exit(0 if result['passed'] else 1)
