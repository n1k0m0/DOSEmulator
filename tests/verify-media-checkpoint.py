# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent DOSCOW01/FAT12/FAT16 and media-cut oracle; no emulator implementation imports."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import struct


def check(value, reason):
    """Fail closed on missing identities, raw bytes, actual guest execution or cold-branch invariants."""
    if not value:
        raise AssertionError(reason)


def safe(path):
    """Refuse personal play media before any file is opened, including indirect PC/manifest dependencies."""
    path = Path(path).resolve()
    check('/artifacts/doom2-play' not in path.as_posix().lower(), 'personal play media excluded')
    return path


def read(path):
    """Read one explicitly named bounded test metadata document."""
    return json.loads(safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Stream a complete explicitly named source or sealed generation without allocating a disk-sized buffer."""
    with safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


class Disk:
    """Read logical sectors by independently replaying valid journal records over a protected RAW base."""

    def __init__(self, basis, journal):
        """Certify header identity and every complete record before exposing the latest sector map."""
        self.basis = safe(basis)
        self.sectors = self.basis.stat().st_size // 512
        check(self.sectors > 0 and self.basis.stat().st_size % 512 == 0, 'whole raw sectors')
        self.changed = {}
        self.records = 0
        if journal is not None:
            data = safe(journal).read_bytes()
            check(len(data) >= 48 and (len(data) - 48) % 552 == 0, 'complete journal prefix')
            check(data[:8] == b'DOSCOW01' and struct.unpack_from('<q', data, 8)[0] == self.sectors, 'journal header/capacity')
            check(data[16:48].hex().upper() == sha(self.basis), 'journal base digest')
            for offset in range(48, len(data), 552):
                record = data[offset:offset + 552]
                lba = struct.unpack_from('<q', record)[0]
                check(0 <= lba < self.sectors and hashlib.sha256(record[:520]).digest() == record[520:], 'journal record identity')
                self.changed[lba] = record[8:520]
                self.records += 1

    def sector(self, lba):
        """Read one complete logical sector from an explicitly checked journal or the unchanged base."""
        check(0 <= lba < self.sectors, 'sector outside raw device')
        if lba in self.changed:
            return self.changed[lba]
        with self.basis.open('rb') as stream:
            stream.seek(lba * 512)
            value = stream.read(512)
        check(len(value) == 512, 'short base sector')
        return value

    def file(self, filename):
        """Walk the actual DOS root directory and FAT chain, independently of screen text and report metadata."""
        boot = self.sector(0)
        if struct.unpack_from('<H', boot, 11)[0] != 512:
            starts = [struct.unpack_from('<I', boot, 446 + 16 * i + 8)[0] for i in range(4) if boot[446 + 16 * i + 4] != 0]
            check(starts, 'partitioned DOS volume')
            start = starts[0]
            boot = self.sector(start)
        else:
            start = 0
        bps = struct.unpack_from('<H', boot, 11)[0]
        spc, reserved, fats = boot[13], struct.unpack_from('<H', boot, 14)[0], boot[16]
        roots = struct.unpack_from('<H', boot, 17)[0]
        total = struct.unpack_from('<H', boot, 19)[0] or struct.unpack_from('<I', boot, 32)[0]
        fat_sectors = struct.unpack_from('<H', boot, 22)[0]
        root_sectors = (roots * 32 + 511) // 512
        data_start = reserved + fats * fat_sectors + root_sectors
        clusters = (total - data_start) // spc
        check(bps == 512 and spc > 0 and spc & (spc - 1) == 0 and 0 < clusters < 65525 and roots > 0, 'FAT12/16 volume bounds')
        check(start + total <= self.sectors and fat_sectors > 0 and reserved > 0 and fats in (1, 2), 'FAT media geometry')
        root = b''.join(self.sector(start + reserved + fats * fat_sectors + i) for i in range(root_sectors))
        entries = [root[i:i + 32] for i in range(0, len(root), 32)]
        entry = next((v for v in entries if v[0] not in (0, 0xe5) and not v[11] & 0x18 and v[:11] == filename), None)
        check(entry is not None, 'actual own DOS file exists')
        cluster, size = struct.unpack_from('<H', entry, 26)[0], struct.unpack_from('<I', entry, 28)[0]
        check(size < 65536, 'bounded own DOS proof file')
        fat = b''.join(self.sector(start + reserved + i) for i in range(fat_sectors))
        seen, content = set(), bytearray()
        while len(content) < size:
            check(2 <= cluster < clusters + 2 and cluster not in seen, 'finite in-range file cluster chain')
            seen.add(cluster)
            for i in range(spc):
                content.extend(self.sector(start + data_start + (cluster - 2) * spc + i))
            if clusters < 4085:
                at = cluster + cluster // 2
                value = struct.unpack_from('<H', fat, at)[0]
                cluster = value >> 4 if cluster & 1 else value & 0xfff
            else:
                cluster = struct.unpack_from('<H', fat, cluster * 2)[0]
        return bytes(content[:size])


def checkpoint_metadata(cp, report):
    """Certify the exact owner cut and physical bindings without treating recorded virtual time as resumable CPU state."""
    check(cp['schemaVersion'] == 1 and cp['kind'] == 'DOSE-MEDIA-1' and len(cp['id']) == 32, 'media-only format/id')
    check(cp == report['mediaCheckpoint'], 'manifest equals actual capture result')
    check(cp['virtualNanoseconds'] == report['status']['virtualNanoseconds'], 'common actual owner boundary')
    check(len(cp['media']) == 5, 'four real IDE disks and own floppy')
    check([(v['device'], v['slot']) for v in cp['media']] == [('ide', i) for i in range(4)] + [('floppy', 0)], 'fixed physical slots')
    check([v['sectors'] for v in cp['media']] == [65536, 4096, 16384, 3145728, 2880], 'actual tested capacities')
    check(not cp['machine']['builtInTestDisk'], 'no substituted boot fixture')
    for row in cp['media']:
        check(len(row['baseSha256']) == 64 and row['baseSha256'] == row['baseSha256'].upper(), 'base digest text')
        if row['device'] == 'ide':
            check(row['journalName'] == f"ide{row['slot']}.cow" and len(row['journalSha256']) == 64, 'sealed writable IDE journal')
            check(row['journalBytes'] >= 48 and (row['journalBytes'] - 48) % 552 == 0, 'complete declared generation')
        else:
            check(row['journalName'] is None and row['journalBytes'] == 0 and row['journalSha256'] is None, 'read-only own probe media')


def verify(summary):
    """Certify all actual guest phases, isolated branches and preserved complete media generations."""
    check(summary['headlessColdBoots'] == 24 and len(summary['systems']) == 4, 'actual final cold starts')
    negatives, records, files = 0, 0, 0
    for system in summary['systems']:
        rows = [v for v in summary['cases'] if v['system'] == system['system']]
        check([v['phase'] for v in rows] == ['seed', 'capture', 'origin-change', 'branch-read', 'branch-change', 'second-read'], 'six actual phases')
        reports = {v['phase']: read(v['report']) for v in rows}
        for phase, report in reports.items():
            check(report['status']['fault'] is None and report['scenario']['complete'], 'completed real DOS execution')
            check('MEDIA-DOS-RETURN' in report['text'] and report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal'], 'KBC and DOS return')
            if system['system'].startswith('emm386'):
                check(report['cpu']['pagingEnabled'] and report['cpu']['virtual8086Instructions'] > 0, 'actual monitor path')
        cp_directory = safe(system['checkpoint'])
        cp = read(cp_directory / 'media.json')
        checkpoint_metadata(cp, reports['capture'])
        original = read(system['originPC'])
        a, b = read(system['firstBranchPC']), read(system['secondBranchPC'])
        check(a['id'] == b['id'] == cp['machine']['id'] and a['hardware'] == b['hardware'] == cp['machine']['hardware'], 'PC identity/hardware preserved')
        for row in cp['media']:
            item = next(v for v in cp['machine']['hardDisks' if row['device'] == 'ide' else 'floppies'] if v['slot'] == row['slot'])
            basis = safe(cp_directory / item['imagePath'])
            check(sha(basis) == row['baseSha256'] and basis.stat().st_size == row['sectors'] * 512, 'protected base identity')
            if row['journalName'] is not None:
                journal = cp_directory / row['journalName']
                check(journal.stat().st_size == row['journalBytes'] and sha(journal) == row['journalSha256'], 'sealed generation unchanged after all branches')
                check(system['captureJournalHashes'][row['journalName']] == row['journalSha256'], 'copy equals active committed prefix at capture')
                disk = Disk(basis, journal)
                records += disk.records
                if row['slot'] == 0:
                    expected = ('DOSE-' + system['system'].upper() + '-BEFORE\r\n').encode('ascii')
                    check(disk.file(b'SNAP    TXT') == expected, 'positive file contents frozen at capture')
                    files += 1
            else:
                check(item['overlayPath'] is None, 'readonly image stays readonly')
        for phase, pc_path, word in [('origin-change', system['originPC'], 'AFTER'),
                                    ('branch-change', system['firstBranchPC'], 'BRANCH'),
                                    ('second-read', system['secondBranchPC'], 'BEFORE')]:
            definition = read(pc_path)
            root = safe(pc_path).parent
            disk0 = definition['hardDisks'][0]
            disk = Disk(root / disk0['imagePath'], root / disk0['overlayPath'])
            value = disk.file(b'SNAP    TXT')
            check(value == ('DOSE-' + system['system'].upper() + '-' + word + '\r\n').encode('ascii'), 'independent actual FAT file ' + phase)
            files += 1
        for phase in ['capture', 'branch-read', 'second-read']:
            old = read(next(v['hardDiskOracle'] for v in rows if v['phase'] == phase))
            check(old['passed'], 'independent actual PIO/RAM/program/geometry/DOS oracle')
        for field, value in [('schemaVersion', 2), ('kind', 'DOSE-FULL-SNAPSHOT'), ('id', 'bad'),
                             ('virtualNanoseconds', cp['virtualNanoseconds'] + 1), ('media', [])]:
            changed = copy.deepcopy(cp)
            changed[field] = value
            try:
                checkpoint_metadata(changed, reports['capture'])
            except (AssertionError, KeyError):
                negatives += 1
            else:
                raise AssertionError('altered checkpoint metadata accepted: ' + field)
        changed = copy.deepcopy(reports['capture'])
        changed['status']['virtualNanoseconds'] += 1
        try:
            checkpoint_metadata(cp, changed)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('altered owner cut accepted')
        for slot in range(4):
            changed = copy.deepcopy(cp)
            changed['media'][slot]['journalSha256'] = '0' * 64
            try:
                checkpoint_metadata(changed, reports['capture'])
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('altered generation row accepted')
    return dict(passed=True, headlessColdBoots=24, mediaOnly=True, runningStateSnapshot=False,
                physicalIdeSlots=4, readonlyFloppySlots=1, independentFatFileContents=files,
                recordsCertified=records, additionalNegativeControlsRejected=negatives,
                hostConnections=False, nativeInputSent=False,
                scope='Complete media cuts and independent cold branches; CPU/RAM/device/scheduler snapshots remain open')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--summary', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(read(args.summary)), indent=2))
