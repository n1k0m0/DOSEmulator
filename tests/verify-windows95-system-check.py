# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Pure decoder controls: synthesize the own DOSEINFO ABI in memory, without guest/media I/O.

Run with: python tests/verify-windows95-system-check.py
Only the decoder's Python source is loaded. No guest image, report file, registry,
PE, emulator, host endpoint or output path is opened by these controls.
"""
from pathlib import Path
import runpy
import struct
import unittest


DECODER = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'scripts' / 'decode-windows95-system-check.py'))
decode = DECODER['decode']
NOT_CALLED = 0xffffffff
QUERY_CLASS = 8
QUERY_DESCRIPTION = 11


def checksum(data):
    """Encode the public FNV1a witness independently of the decoder's implementation."""
    accumulator = 2166136261
    for value in data:
        accumulator ^= value
        accumulator *= 16777619
        accumulator %= 1 << 32
    return accumulator


def record(kind, context=0, result=0, meta=0, arg0=0, arg1=0, payload=b'', guard=1):
    """Return one complete little-endian TLV, including its required zero padding."""
    return (struct.pack('<8I', kind, context, result, meta, arg0, arg1, len(payload), guard) +
            payload + bytes((-len(payload)) % 4))


def mode():
    """Own 148-byte classic DEVMODEA, with every unused byte explicitly zero."""
    data = bytearray(148)
    data[:12] = b'DOSE-SAMPLE\0'
    struct.pack_into('<4H', data, 32, 0x0400, 0x0100, 148, 0)
    struct.pack_into('<I', data, 40, 0x001c0000)
    struct.pack_into('<5I', data, 104, 8, 1024, 768, 0, 0)
    return bytes(data)


def fixture():
    """Cover every currently emitted TLV kind, one PCI/class binding and all mode scopes."""
    layout = mode()
    pci = rb'Enum\PCI'
    node = pci + rb'\VEN_5333&DEV_8811\BUS_00&DEV_03&FUNC_00'
    display = rb'System\CurrentControlSet\Services\Class\Display\0001'
    class_value = b'Display\0'
    driver_value = rb'Display\0001' + b'\0'
    description = b'Own synthetic display\0'
    return [
        record(1, context=1, result=NOT_CALLED, arg0=16, arg1=1,
               payload=struct.pack('<8H', 1996, 1, 1, 1, 12, 0, 0, 0)),
        record(1, context=2, result=NOT_CALLED, arg0=32, arg1=1,
               payload=struct.pack('<8I', 32, 25, 32 << 20, 24 << 20, 64 << 20, 48 << 20, 2 << 30, 1 << 30)),
        record(2, context=0, result=1024),
        record(3, context=0, result=1, arg0=148, arg1=148, payload=layout),
        record(3, context=0xffffffff, result=1, arg0=148, arg1=148, payload=layout),
        record(3, context=0xfffffffe, result=1, arg0=148, arg1=148, payload=layout),
        record(4, context=1, meta=1, arg1=0x20019, payload=pci + b'\0'),
        record(4, context=2, meta=1, arg0=1, arg1=0x20019, payload=node + b'\0'),
        record(6, context=2, meta=1, arg0=1, arg1=len(class_value), payload=class_value),
        record(6, context=2, meta=1, arg0=2, arg1=len(driver_value), payload=driver_value),
        record(4, context=3, meta=3, arg1=0x20019, payload=display + b'\0'),
        record(6, context=3, meta=1, arg0=20, arg1=len(description), payload=description),
        record(5, context=1, result=259, meta=1, arg0=1, arg1=128, payload=pci + b'\0'),
        record(8, context=2, meta=1),
        record(8, context=1, meta=1),
        record(8, context=3, meta=3),
    ]


def report(records, flags=0, guard_failures=0):
    """Build one fully sealed, completed synthetic report; no filesystem paths are inputs."""
    body = b''.join(records)
    header = struct.pack('<8s14I', b'DOSEINF1', 1, 64, len(records), 64 + len(body), flags, 2,
                         0x80000004, guard_failures, 0, 1, 1, 0, 1, checksum(body))
    return header + body


class SystemCheckDecoderControls(unittest.TestCase):
    def test_complete_synthetic_abi_preserves_real_statuses_and_binding(self):
        """Preserve complete API status, indexed modes and exact PCI/class bindings."""
        self.assertEqual(checksum(b'hello'), 0x4f9f2cab)
        decoded = decode(report(fixture()))
        self.assertTrue(decoded['boundedReportClean'])
        self.assertTrue(all(row['guardOK'] for row in decoded['records']))
        self.assertEqual({row['kind'] for row in decoded['records']}, {1, 2, 3, 4, 5, 6, 8})
        self.assertEqual(decoded['header']['indexedModes'], 1)
        self.assertEqual(decoded['offeredModes'][0]['width'], 1024)
        self.assertEqual(decoded['offeredModes'][0]['bitsPerPixel'], 8)
        self.assertEqual(decoded['enumerationTerminations'][0]['actualResult'], 259)
        binding, = decoded['displayBindings']
        self.assertEqual(binding['driverReference'], r'Display\0001')
        self.assertEqual(binding['displayClassContext'], 3)
        self.assertEqual(binding['displayClassValues']['DriverDesc']['value']['text'], 'Own synthetic display')

    def test_success_requires_the_entire_declared_value(self):
        """Reject successful registry records whose bounded payload is incomplete."""
        # The empty payload with a nonzero successful returned size is the original regression.
        for returned, payload in ((8, b''), (8, b'Display'), (1, b'ab'), (513, b''), (513, bytes(513))):
            with self.subTest(returned=returned, payload_bytes=len(payload)):
                records = fixture()
                records[QUERY_DESCRIPTION] = record(6, context=3, meta=3, arg0=20, arg1=returned, payload=payload)
                with self.assertRaisesRegex(ValueError, 'actually returned bounded value'):
                    decode(report(records))

    def test_success_accepts_exact_zero_and_512_byte_values(self):
        """Accept both exact boundary sizes without inventing returned bytes."""
        for payload in (b'', bytes(range(256)) * 2):
            with self.subTest(payload_bytes=len(payload)):
                records = fixture()
                records[QUERY_DESCRIPTION] = record(6, context=3, meta=3, arg0=20, arg1=len(payload), payload=payload)
                decoded = decode(report(records))
                query = decoded['records'][QUERY_DESCRIPTION]
                self.assertTrue(decoded['boundedReportClean'])
                self.assertEqual(query['actualResult'], 0)
                self.assertEqual(query['returnedBytes'], len(payload))
                self.assertEqual(query['value']['hex'], payload.hex().upper())

    def test_failed_queries_preserve_size_and_error_without_payload(self):
        """Retain original failed-query errors and sizes while exposing no value."""
        for error, returned in ((2, 0), (2, 37), (234, 513), (234, 0xffffffff)):
            with self.subTest(error=error, returned=returned):
                records = fixture()
                records[QUERY_DESCRIPTION] = record(6, context=3, result=error, meta=3, arg0=20, arg1=returned)
                query = decode(report(records))['records'][QUERY_DESCRIPTION]
                self.assertEqual(query['actualResult'], error)
                self.assertEqual(query['returnedBytes'], returned)
                self.assertEqual(query['payloadBytes'], 0)
                self.assertIsNone(query['value'])

    def test_failed_query_cannot_claim_payload_bytes(self):
        """Reject a failed query that claims any returned value payload."""
        records = fixture()
        records[QUERY_DESCRIPTION] = record(6, context=3, result=234, meta=3, arg0=20, arg1=513, payload=b'x')
        with self.assertRaisesRegex(ValueError, 'actually returned bounded value'):
            decode(report(records))

    def test_each_tlv_guard_prevents_a_clean_report_even_with_clean_header(self):
        """Require every independent record guard even when header counters are clear."""
        for index, original in enumerate(fixture()):
            with self.subTest(record_index=index, kind=struct.unpack_from('<I', original)[0]):
                records = fixture()
                damaged_guard = bytearray(original)
                struct.pack_into('<I', damaged_guard, 28, 0)
                records[index] = bytes(damaged_guard)
                decoded = decode(report(records))
                self.assertEqual(decoded['header']['guardFailures'], 0)
                self.assertEqual(decoded['header']['flags'], 0)
                self.assertFalse(decoded['records'][index]['guardOK'])
                self.assertFalse(decoded['boundedReportClean'])

    def test_header_guard_failure_also_prevents_clean_report(self):
        """Keep a damaged header from passing otherwise intact record guards."""
        decoded = decode(report(fixture(), flags=1, guard_failures=1))
        self.assertTrue(all(row['guardOK'] for row in decoded['records']))
        self.assertEqual(decoded['header']['guardFailures'], 1)
        self.assertFalse(decoded['boundedReportClean'])

    def test_full_record_checksum_remains_required(self):
        """Reject modified report bytes without a matching full-record checksum."""
        data = bytearray(report(fixture()))
        data[-4] ^= 1
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            decode(bytes(data))


if __name__ == '__main__':
    unittest.main(verbosity=2)
