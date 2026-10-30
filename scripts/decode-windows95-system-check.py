# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Decode only an explicitly extracted own DOSEINFO.BIN; never open a disk, registry or run the guest PE.

Usage: python scripts/decode-windows95-system-check.py <private-artifacts/DOSEINFO.BIN>
       --output <new-private-artifacts/decoded.json>
The complete bytes, record boundaries, zero padding, checksum, allowed paths/value IDs
and actual API outcomes are retained. Missing values/end-of-enumeration are not fabricated
successes. A structurally complete report does not by itself prove the guest file Close/Flush;
the helper's observed process exit code0 is the separate transport-success witness.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import struct


CAPACITY = 262144
NOT_CALLED = 0xffffffff
CLASS_PATH = r'System\CurrentControlSet\Services\Class\Display'
RUN_PATH = r'Software\Microsoft\Windows\CurrentVersion\Run'
NAMES = {1: 'Class', 2: 'Driver', 3: 'DeviceDesc', 4: 'FriendlyName', 5: 'Mfg', 6: 'HardwareID',
         7: 'CompatibleIDs', 8: 'ConfigFlags', 9: 'Problem', 10: 'StatusFlags', 11: 'ClassGUID',
         12: 'BootConfig', 13: 'AllocConfig', 14: 'ForcedConfig', 15: 'BasicConfigVector',
         20: 'DriverDesc', 21: 'DevLoader', 22: 'DeviceVxDs', 23: 'InfPath', 24: 'InfSection',
         25: 'InfOption', 26: 'ProviderName', 27: 'DriverVersion', 28: 'MatchingDeviceId',
         30: 'drv', 31: 'vdd', 32: 'minivdd', 33: 'ExtLoader', 34: 'Mode', 35: 'RawMode',
         100: 'DOSETIME', 101: 'DOSE Time'}
FLAGS = {1: 'api-buffer-guard-failure', 2: 'report-capacity-reached', 4: '512-mode-limit-reached',
         8: 'registry-enumeration-limit-reached', 16: 'invalid-api-length-or-path', 32: 'registry-error'}
CAPS = {8: 'HORZRES', 10: 'VERTRES', 12: 'BITSPIXEL', 14: 'PLANES', 38: 'RASTERCAPS',
        88: 'LOGPIXELSX', 90: 'LOGPIXELSY', 116: 'VREFRESH'}


def need(value, message):
    """Keep strict bounds/allowlist checks active under optimized Python too."""
    if not value:
        raise ValueError(message)


def own(value):
    """Allow only explicit private artifact files, excluding personal play and delivered user installations."""
    raw = str(value).replace('\\', '/').lower()
    need('doom2-play' not in raw.split('/'), 'Personal overlays are forbidden.')
    path = Path(value).resolve()
    normalized = path.as_posix().lower()
    need('/artifacts/' in normalized and '/doom2-play' not in normalized and '/artifacts/windows95/' not in normalized,
         'Use only explicitly extracted private diagnostic artifacts.')
    return path


def fnv(data):
    """Recompute the guest's exact32-bit FNV1a over all TLV bytes, not a selected subset."""
    result = 0x811c9dc5
    for value in data:
        result = ((result ^ value) * 0x01000193) & 0xffffffff
    return result


def path_text(data):
    """A path is one complete bounded ANSI string; embedded or absent terminators are invalid."""
    need(1 <= len(data) <= 512 and data[-1] == 0 and 0 not in data[:-1], 'Invalid complete registry path payload.')
    return data[:-1].decode('cp1252', errors='replace')


def allowed_path(path, group):
    """Check the same selected hardware/driver scopes, without accepting unrelated software values."""
    lower = path.lower()
    if group == 1:
        return lower == r'enum\pci' or re.fullmatch(r'enum\\pci\\ven_5333&dev_8811(?:&[^\\]+)?(?:\\[^\\]+){0,2}', lower) is not None
    if group == 2:
        return lower == r'enum\root' or lower.startswith('enum\\root\\')
    if group == 3:
        return lower == CLASS_PATH.lower() or re.fullmatch(re.escape(CLASS_PATH.lower()) + r'\\[0-9]{4}(?:\\default)?', lower) is not None
    return group == 4 and lower == RUN_PATH.lower()


def value_data(kind, data):
    """Preserve raw bytes, exposing strings only within their real returned length; never assume ANSI NUL termination."""
    result = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest().upper(), 'hex': data.hex().upper()}
    if kind in (1, 2):
        result.update(text=data.rstrip(b'\0').decode('cp1252', errors='replace'), nulTerminated=bool(data and data[-1] == 0))
    elif kind == 7:
        result.update(strings=[s.decode('cp1252', errors='replace') for s in data.rstrip(b'\0').split(b'\0')],
                      doubleNulTerminated=data.endswith(b'\0\0'))
    elif kind == 4 and len(data) == 4:
        result['dword'] = struct.unpack('<I', data)[0]
    return result


def devmode(data):
    """Read the classic Win4.0 ANSI148-byte layout; positions and returned public/private sizes remain explicit."""
    need(len(data) == 148, 'Mode payload must contain the entire supplied Win4.0 DEVMODEA.')
    spec, driver, size, extra = struct.unpack_from('<4H', data, 32)
    fields = struct.unpack_from('<I', data, 40)[0]
    bpp, width, height, flags, hz = struct.unpack_from('<5I', data, 104)
    return {'deviceName': data[:32].split(b'\0', 1)[0].decode('cp1252', errors='replace'),
            'specVersion': spec, 'driverVersion': driver, 'dmSize': size, 'dmDriverExtra': extra, 'dmFields': fields,
            'bitsPerPixel': bpp, 'width': width, 'height': height, 'displayFlags': flags, 'displayFrequency': hz,
            'defaultRefreshMarker': hz in (0, 1), 'suppliedBytes': len(data),
            'rawSha256': hashlib.sha256(data).hexdigest().upper(), 'rawHex': data.hex().upper()}


def decode(data):
    """Validate the whole file before constructing the actual node/driver/mode correspondence."""
    need(64 <= len(data) <= CAPACITY, 'Own report length is outside64..262144 bytes.')
    header = struct.unpack_from('<8s14I', data)
    magic, version, header_bytes, count, total, flags, stage, osversion, guards, dropped, modes, pci, root, classes, checksum = header
    need(magic == b'DOSEINF1' and version == 1 and header_bytes == 64 and total == len(data), 'Wrong/incomplete own report header.')
    need(stage == 2 and flags & ~63 == 0 and checksum == fnv(data[64:]), 'Incomplete collection or full-record checksum mismatch.')
    need(osversion & 0x80000000 and osversion & 0xffff == 4, 'Report does not identify the guarded Windows95 family.')
    need(count <= (len(data) - 64) // 32, 'Record count cannot fit its complete headers.')
    records, contexts, queries, metric_values, offered, special, summaries = [], {}, {}, {}, [], {}, []
    offset, indexed_successes = 64, 0
    for ordinal in range(count):
        need(offset + 32 <= len(data), 'Truncated TLV header.')
        kind, context, result, meta, arg0, arg1, length, guard = struct.unpack_from('<8I', data, offset)
        need(length <= 2048 and guard in (0, 1), 'Invalid bounded TLV payload/guard.')
        end, padded = offset + 32 + length, offset + 32 + ((length + 3) & ~3)
        need(padded <= len(data) and data[end:padded] == bytes(padded - end), 'Truncated TLV payload or nonzero alignment bytes.')
        payload = data[offset + 32:end]
        row = {'ordinal': ordinal, 'kind': kind, 'context': context, 'actualResult': result, 'meta': meta,
               'arg0': arg0, 'arg1': arg1, 'payloadBytes': length, 'guardOK': bool(guard)}
        if kind == 1:
            need(context in (1, 2) and result == NOT_CALLED and arg1 == 1 and length == (16 if context == 1 else 32), 'Invalid completed void-API record.')
            if context == 1:
                values = struct.unpack('<8H', payload)
                row.update(api='GetLocalTime', systemTime=dict(zip(('year', 'month', 'dayOfWeek', 'day', 'hour', 'minute', 'second', 'milliseconds'), values)))
            else:
                values = struct.unpack('<8I', payload)
                row.update(api='GlobalMemoryStatus', memoryStatus=dict(zip(('length', 'memoryLoadPercent', 'totalPhysical', 'availablePhysical',
                                                                            'totalPageFile', 'availablePageFile', 'totalVirtual', 'availableVirtual'), values)))
        elif kind == 2:
            need(length == 0, 'Scalar metric must not contain an untyped payload.')
            if context in (0, 1):
                row.update(api='GetSystemMetrics', metric='SM_CXSCREEN' if context == 0 else 'SM_CYSCREEN')
            elif context in (0x1000, 0x1001):
                row['api'] = 'GetDC' if context == 0x1000 else 'ReleaseDC'
            else:
                need(context & 0xfffff000 == 0x2000 and context & 0xfff in CAPS, 'Unexpected GetDeviceCaps ID.')
                row.update(api='GetDeviceCaps', metric=CAPS[context & 0xfff])
            metric_values[context] = result
        elif kind == 3:
            need((context < 512 or context in (0xffffffff, 0xfffffffe)) and length == 148 and arg0 == 148, 'Unexpected mode call/index/layout.')
            mode = devmode(payload)
            need(arg1 == mode['dmSize'] | mode['dmDriverExtra'] << 16, 'Returned mode-size metadata differs.')
            row.update(api='EnumDisplaySettingsA', actualBool=result, failureLastError=meta, mode=mode)
            if context < 512:
                if result:
                    indexed_successes += 1
                    offered.append({'index': context, 'actualBool': result, 'guardOK': bool(guard), **mode})
            else:
                special['current' if context == 0xffffffff else 'registry'] = row
        elif kind == 4:
            path = path_text(payload)
            need(context > 0 and context not in contexts and meta in (1, 2, 3, 4) and allowed_path(path, meta) and arg1 == 0x20019,
                 'Unexpected, repeated or out-of-scope registry context.')
            need(arg0 == 0 or arg0 in contexts, 'Unknown registry parent context.')
            row.update(api='RegOpenKeyExA', path=path, group=meta, parentContext=arg0, access='KEY_READ')
            contexts[context] = row
        elif kind == 5:
            path = path_text(payload)
            need(meta in (1, 2, 3) and allowed_path(path, meta) and arg1 in (32, 64, 128) and arg0 <= arg1, 'Unexpected bounded registry enumeration.')
            need(context == 0 or context in contexts, 'Unknown enumeration parent context.')
            need(result != NOT_CALLED or arg0 == arg1 and flags & 8, 'A missing enumeration call is not explained by its bound.')
            row.update(api='RegEnumKeyA', parentPath=path, stoppedAtIndex=arg0, maximum=arg1,
                       exhausted=result == 259, callNotMadeAtLimit=result == NOT_CALLED)
            summaries.append(row)
        elif kind == 6:
            need(context in contexts and contexts[context]['actualResult'] == 0 and arg0 in NAMES, 'Unknown/failed key or unexpected value name.')
            path = contexts[context]['path']; run = path.lower() == RUN_PATH.lower()
            need((arg0 in (100, 101)) == run, 'Only our two own Run names may be queried in the software hive.')
            need(length <= 512 and ((result == 0 and arg1 <= 512 and length == arg1) or
                                   (result != 0 and length == 0)), 'Query payload exceeds an actually returned bounded value.')
            row.update(api='RegQueryValueExA', path=path, valueName=NAMES[arg0], registryType=meta, returnedBytes=arg1,
                       value=value_data(meta, payload) if result == 0 and arg1 <= 512 else None)
            queries.setdefault(context, {})[NAMES[arg0]] = row
        elif kind == 8:
            need(length == 0 and context in contexts and contexts[context]['actualResult'] == 0 and meta in (1, 2, 3, 4), 'Unexpected key-close outcome.')
            row.update(api='RegCloseKey', path=contexts[context]['path'])
        else:
            raise ValueError(f'Unknown TLV kind: {kind}')
        records.append(row); offset = padded
    need(offset == len(data) and indexed_successes == modes, 'Trailing bytes or indexed mode count mismatch.')
    need(set(special) == {'current', 'registry'}, 'Both explicit current/registry mode requests must be present.')
    need([m['index'] for m in offered] == list(range(modes)), 'Successful indexed mode enumeration is not contiguous.')
    bindings = []
    for context, opened in contexts.items():
        values = queries.get(context, {})
        if opened['group'] not in (1, 2) or not any(name in values for name in ('Class', 'Driver', 'DeviceDesc')):
            continue
        driver = values.get('Driver', {}).get('value') or {}
        reference = driver.get('text')
        class_context = None
        if reference and re.fullmatch(r'Display\\[0-9]{4}', reference, flags=re.IGNORECASE):
            wanted = ('System\\CurrentControlSet\\Services\\Class\\' + reference).lower()
            class_context = next((key for key, value in contexts.items() if value['path'].lower() == wanted and value['actualResult'] == 0), None)
        bindings.append({'context': context, 'path': opened['path'], 'values': values, 'driverReference': reference,
                         'displayClassContext': class_context, 'displayClassValues': queries.get(class_context, {})})
    return {'formatVersion': version, 'collectionComplete': True, 'queryResultsAcceptedWithoutRepair': True,
            'header': {'bytes': total, 'records': count, 'flags': flags, 'flagNames': [name for bit, name in FLAGS.items() if flags & bit],
                       'getVersion': osversion, 'guardFailures': guards, 'droppedRecords': dropped, 'indexedModes': modes,
                       'pciNodes': pci, 'rootDisplayNodes': root, 'displayClassNodes': classes, 'recordsFnv1a': f'{checksum:08X}'},
            'boundedReportClean': flags & (1 | 2 | 16) == 0 and guards == 0 and dropped == 0 and all(row['guardOK'] for row in records),
            'enumerationLimitsReached': bool(flags & (4 | 8)), 'displayBindings': bindings,
            'currentDisplay': {'systemMetricsWidth': metric_values.get(0), 'systemMetricsHeight': metric_values.get(1),
                               'deviceCapsWidth': metric_values.get(0x2008), 'deviceCapsHeight': metric_values.get(0x200a),
                               'deviceCapsBitsPerPixel': metric_values.get(0x200c), 'deviceCapsPlanes': metric_values.get(0x200e)},
            'offeredModes': offered, 'specialModeRequests': special, 'enumerationTerminations': summaries,
            'registryContexts': list(contexts.values()), 'records': records,
            'scope': 'Actual Win95 read-only API results and targeted hardware/driver registry values. No removal, repair, mode switch or licensing query. Structural file verification is separate from the observed process exit/Flush/Close witness.'}


def main():
    """Read one own extracted report and write a fresh private JSON result, never modifying the guest image or report."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    source, output = own(args.input), own(args.output)
    need(source.is_file() and not output.exists() and output.parent.is_dir(), 'Use one existing own report and a new output with existing parent.')
    data = source.read_bytes(); result = decode(data)
    result['input'] = {'path': str(source), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest().upper()}
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write('\n')
    print(json.dumps({'output': str(output), 'clean': result['boundedReportClean'], 'modes': result['header']['indexedModes'],
                      'displayBindings': len(result['displayBindings']), 'flags': result['header']['flagNames']}))


if __name__ == '__main__':
    main()
