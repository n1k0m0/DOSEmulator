// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text;

namespace DOSEmulator.Hardware;

public sealed partial class AtapiCdRom
{
    /// <summary>Rejects every unsupported or reserved CDB bit rather than silently accepting another protocol generation.</summary>
    private bool ValidPacket(params (int Index, byte Mask)[] writable)
    {
        for (int i = 1; i < 12; i++)
        {
            byte allowed = 0;
            foreach (var field in writable) if (field.Index == i) allowed |= field.Mask;
            if ((_packet[i] & ~allowed) != 0) { CheckCondition(5, 0x24); return false; }
        }
        return true;
    }
    /// <summary>Reads one original big-endian packet integer independently from little-endian ATA data words.</summary>
    private uint Packet32(int index) => BinaryPrimitives.ReadUInt32BigEndian(_packet.AsSpan(index, 4));
    /// <summary>Reads a two-byte original packet allocation or logical block count.</summary>
    private ushort Packet16(int index) => BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(index, 2));
    /// <summary>Requires actual closed, present and started read-only media without contacting the backend.</summary>
    private bool RequireMedia()
    {
        if (_trayOpen || !HasDisc) { CheckCondition(2, 0x3a); return false; }
        if (!_started) { CheckCondition(2, 4, 2); return false; }
        return true;
    }
    /// <summary>Executes a complete12-byte packet; inquiry bypasses attention, while sense uses the explicitly chosen UA-priority option.</summary>
    private void ExecutePacket()
    {
        byte command = _packet[0];
        _task[1] = 0; _clearSenseOnCompletion = _clearUaOnCompletion = false;
        if (command != 3) _senseKey = _senseAsc = _senseAscq = 0;
        if (_unitAttention && command is not (3 or 0x12)) { CheckCondition(6, _uaAsc); return; }
        switch (command)
        {
            case 0:
                if (ValidPacket() && RequireMedia()) NoData(); break;
            case 0x12:
                if (ValidPacket((4, 255))) BeginResponse(Inquiry(), _packet[4]); break;
            case 3:
                if (!ValidPacket((4, 255))) break;
                byte[] sense = new byte[18]; sense[0] = 0x70; sense[2] = _unitAttention ? (byte)6 : _senseKey; sense[7] = 10;
                sense[12] = _unitAttention ? _uaAsc : _senseAsc; sense[13] = _unitAttention ? (byte)0 : _senseAscq;
                _clearSenseOnCompletion = true; _clearUaOnCompletion = _unitAttention;
                BeginResponse(sense, _packet[4]); break;
            case 0x25:
                if (!ValidPacket() || !RequireMedia()) break;
                byte[] capacity = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(capacity, (uint)(DiscBlocks - 1));
                BinaryPrimitives.WriteUInt32BigEndian(capacity.AsSpan(4), 2048);
                BeginResponse(capacity, 8); break;
            case 0x28:
                if (ValidPacket((2, 255), (3, 255), (4, 255), (5, 255), (7, 255), (8, 255))) BeginReadCommand(Packet32(2), Packet16(7));
                break;
            case 0xa8:
                if (ValidPacket((2, 255), (3, 255), (4, 255), (5, 255), (6, 255), (7, 255), (8, 255), (9, 255))) BeginReadCommand(Packet32(2), Packet32(6));
                break;
            case 0x43:
                if (ValidPacket((1, 2), (6, 255), (7, 255), (8, 255), (9, 0xc0)) && RequireMedia()) ReadToc();
                break;
            case 0x5a:
                if (ValidPacket((2, 255), (7, 255), (8, 255))) ModeSense(); break;
            case 0x1e:
                if (ValidPacket((4, 1))) { _preventRemoval = (_packet[4] & 1) != 0; NoData(); } break;
            case 0x1b:
                if (ValidPacket((4, 3))) StartStop(); break;
            case 0x2b:
                if (!ValidPacket((2, 255), (3, 255), (4, 255), (5, 255)) || !RequireMedia()) break;
                if (Packet32(2) >= DiscBlocks) CheckCondition(5, 0x21); else NoData();
                break;
            default: CheckCondition(5, 0x20); break;
        }
    }
    /// <summary>Stages only the allocated response bytes; invalid PIO limits are rejected before any medium access.</summary>
    private void BeginResponse(ReadOnlySpan<byte> response, int allocation)
    {
        int length = Math.Min(response.Length, allocation);
        if (length == 0) { NoData(); return; }
        if (_byteLimit == 0 || _byteLimit == 1 && length > 1) { CheckCondition(5, 0x24); return; }
        Array.Clear(_data); response[..length].CopyTo(_data);
        _readCommand = _identify = false; _payloadLength = length; _payloadOffset = 0;
        _phase = AtapiPhase.DataInWait; Status = 0x80; Schedule();
    }
    /// <summary>Schedules a real separate completion instead of treating absence of payload as an extra data DRQ.</summary>
    private void NoData() { _phase = AtapiPhase.StatusWait; Status = 0x80; Schedule(); }
    /// <summary>Validates the entire cooked address range and byte limit before scheduling the first backend read.</summary>
    private void BeginReadCommand(uint lba, uint blocks)
    {
        if (!RequireMedia()) return;
        if (blocks == 0) { NoData(); return; }
        if (lba >= DiscBlocks || (ulong)lba + blocks > (ulong)DiscBlocks) { CheckCondition(5, 0x21); return; }
        if (_byteLimit < 2) { CheckCondition(5, 0x24); return; }
        _readCommand = true; _identify = false; _readLba = lba; _remainingBlocks = blocks;
        _payloadOffset = _payloadLength = 0; _phase = AtapiPhase.DataInWait; Status = 0x80; Schedule();
    }
    /// <summary>Reports the named own subset using the original ATAPI2/response-format1 inquiry byte, not a modern SCSI format.</summary>
    private static byte[] Inquiry()
    {
        byte[] data = new byte[36]; data[0] = 5; data[1] = 0x80; data[3] = 0x21; data[4] = 31;
        Encoding.ASCII.GetBytes("DOSE    ").CopyTo(data, 8);
        Encoding.ASCII.GetBytes("ATAPI PIO CD-ROM ").CopyTo(data, 16);
        Encoding.ASCII.GetBytes("0001").CopyTo(data, 32); return data;
    }
    /// <summary>Returns only original identify information actually supported by this PIO/non-overlapped twelve-byte target.</summary>
    private void BuildIdentify()
    {
        Array.Clear(_data);
        // All unspecified words stay zero; ASCII identify words retain ATA byte ordering.
        void Word(int index, ushort value) { _data[index * 2] = (byte)value; _data[index * 2 + 1] = (byte)(value >> 8); }
        // Writes only the fixed own ASCII field, padding to its exact ATA word-swapped extent.
        void Text(int index, string value, int length)
        { value = value.PadRight(length); for (int i = 0; i < length; i += 2) { _data[index * 2 + i] = (byte)value[i + 1]; _data[index * 2 + i + 1] = (byte)value[i]; } }
        Word(0, 0x85a0); Word(49, 0x0200);
        Text(10, "DOSE-ATAPI-000000001", 20); Text(23, "0001", 8); Text(27, "DOSE ATAPI PIO CD-ROM SUBSET", 40);
        // Version words73/74 remain zero: the implemented subset makes no claim of complete SFF conformance.
    }
    /// <summary>Encodes one original LBA or binary MSF address; cooked images with unrepresentable TOC fields are rejected explicitly.</summary>
    private bool TocAddress(byte[] target, int index, long lba, bool msf)
    {
        if (!msf)
        { if ((ulong)lba > uint.MaxValue) { CheckCondition(5, 0x24); return false; } BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(index), (uint)lba); return true; }
        long frames = lba + 150;
        if (frames / 4500 > 255) { CheckCondition(5, 0x24); return false; }
        target[index] = 0; target[index + 1] = (byte)(frames / 4500); target[index + 2] = (byte)(frames / 75 % 60); target[index + 3] = (byte)(frames % 75); return true;
    }
    /// <summary>Uses revision2.5 byte9 format bits, reporting a single Mode-1 track and its actual lead-out.</summary>
    private void ReadToc()
    {
        int format = _packet[9] >> 6; bool msf = (_packet[1] & 2) != 0;
        byte track = _packet[6]; byte[] data;
        if (format == 0)
        {
            if (track is not (0 or 1 or 0xaa)) { CheckCondition(5, 0x24); return; }
            data = new byte[track == 0xaa ? 12 : 20];
            data[1] = (byte)(data.Length - 2); data[2] = data[3] = 1;
            if (track != 0xaa) { data[5] = 0x14; data[6] = 1; if (!TocAddress(data, 8, 0, msf)) return; }
            int at = data.Length - 8; data[at + 1] = 0x14; data[at + 2] = 0xaa;
            if (!TocAddress(data, at + 4, DiscBlocks, msf)) return;
        }
        else if (format == 1 && track == 0)
        {
            data = new byte[12]; data[1] = 10; data[2] = data[3] = 1; data[5] = 0x14; data[6] = 1;
            if (!TocAddress(data, 8, 0, msf)) return;
        }
        else { CheckCondition(5, 0x24); return; }
        BeginResponse(data, Packet16(7));
    }
    /// <summary>Reports only fixed read-only mode pages, without pretending to implement saved parameters or audio playback.</summary>
    private void ModeSense()
    {
        int pc = _packet[2] >> 6, page = _packet[2] & 63;
        if (pc == 3) { CheckCondition(5, 0x39); return; }
        byte[] codes = page == 0x3f ? [1, 0x0d, 0x0e, 0x2a] : [(byte)page];
        var data = new List<byte>(8); data.AddRange(new byte[8]);
        foreach (byte code in codes)
        {
            int length = code switch { 1 or 0x0d => 8, 0x0e => 16, 0x2a => 24, _ => 0 };
            if (length == 0) { CheckCondition(5, 0x24); return; }
            byte[] values = new byte[length]; values[0] = code; values[1] = (byte)(length - 2);
            if (code == 0x2a && pc != 1)
            {
                values[6] = (byte)(0x29 | (_preventRemoval && pc == 0 ? 2 : 0));
                values[9] = values[15] = 150; values[13] = 2;
            }
            data.AddRange(values);
        }
        byte[] result = data.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)(result.Length - 2));
        result[2] = _trayOpen ? (byte)0x71 : HasDisc ? (byte)1 : (byte)0x70;
        BeginResponse(result, Packet16(7));
    }
    /// <summary>Changes only the virtual tray/spindle access state; the immutable image is never unmounted, written or replaced.</summary>
    private void StartStop()
    {
        bool loadEject = (_packet[4] & 2) != 0, start = (_packet[4] & 1) != 0;
        if (loadEject && !start)
        {
            if (_preventRemoval) { CheckCondition(5, 0x53, 2); return; }
            if (!_trayOpen) { _trayOpen = true; _unitAttention = true; _uaAsc = 0x28; }
            _started = false;
        }
        else if (start)
        {
            if (loadEject && _trayOpen) { _trayOpen = false; _unitAttention = true; _uaAsc = 0x28; }
            if (_trayOpen || !HasDisc) { CheckCondition(2, 0x3a); return; }
            _started = true;
        }
        else _started = false;
        NoData();
    }
}
