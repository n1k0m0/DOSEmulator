// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class AtapiCdRom
{
    /// <summary>Typed temporary import values; each stored field is read exactly once before any live object or medium is touched.</summary>
    private sealed record SnapshotData(bool HasDisc, long DiscBlocks, int DeviceNumber, byte[] Task, byte[] Packet, byte[] Data,
        byte Features, byte Control, AtapiPhase Phase, int PacketOffset, int PayloadLength, int PayloadOffset, int PhaseLength,
        int Offset, ushort ByteLimit, long ReadLba, uint RemainingBlocks, bool ReadCommand, bool Identify, bool PendingInterrupt,
        bool Selected, bool HardwareResetHeld, bool SrstActive, bool SrstPreserved, bool TrayOpen, bool PreventRemoval, bool Started,
        bool UnitAttention, byte UaAsc, byte SenseKey, byte SenseAsc, byte SenseAscq, bool ClearSense, bool ClearUa, bool ShadowAbort,
        long Event, ulong Deadline, byte Status, ulong Generation, long CommandsReceived, long PacketsCompleted,
        long ReadBlockCount, long DataBytesRead);

    /// <summary>Parses the full current-build field set, including constructor invariants, without normalizing history or skipping unknown fields.</summary>
    private static SnapshotData ReadSnapshot(StateBlock state)
    {
        var r = new StateReader(state, "DOSEmulator.Hardware.AtapiCdRom");
        var result = new SnapshotData(r.Value<bool>("HasDisc"), r.Value<long>("DiscBlocks"), r.Value<int>("DeviceNumber"),
            r.Value<byte[]>("_task"), r.Value<byte[]>("_packet"), r.Value<byte[]>("_data"),
            r.Value<byte>("_features"), r.Value<byte>("_control"), r.Value<AtapiPhase>("_phase"),
            r.Value<int>("_packetOffset"), r.Value<int>("_payloadLength"), r.Value<int>("_payloadOffset"),
            r.Value<int>("_phaseLength"), r.Value<int>("_offset"), r.Value<ushort>("_byteLimit"),
            r.Value<long>("_readLba"), r.Value<uint>("_remainingBlocks"), r.Value<bool>("_readCommand"),
            r.Value<bool>("_identify"), r.Value<bool>("_pendingInterrupt"), r.Value<bool>("_selected"),
            r.Value<bool>("_hardwareResetHeld"), r.Value<bool>("_srstActive"), r.Value<bool>("_srstPreserved"),
            r.Value<bool>("_trayOpen"), r.Value<bool>("_preventRemoval"), r.Value<bool>("_started"),
            r.Value<bool>("_unitAttention"), r.Value<byte>("_uaAsc"), r.Value<byte>("_senseKey"),
            r.Value<byte>("_senseAsc"), r.Value<byte>("_senseAscq"), r.Value<bool>("_clearSenseOnCompletion"),
            r.Value<bool>("_clearUaOnCompletion"), r.Value<bool>("_shadowAbort"), r.Value<long>("_event"),
            r.Value<ulong>("_deadline"), r.Value<byte>("Status"), r.Value<ulong>("Generation"),
            r.Value<long>("CommandsReceived"), r.Value<long>("PacketsCompleted"),
            r.Value<long>("ReadBlockCount"), r.Value<long>("DataBytesRead"));
        r.Finish(); return result;
    }

    /// <summary>Rejects malformed ATAPI state before image reopening, branch creation, callback binding or ownership adoption.</summary>
    public static void ValidateSnapshotState(StateBlock state, SchedulerState scheduler, long? expectedDiscBlocks,
        int deviceNumber, bool selected, string owner)
    {
        DeviceScheduler.ValidateState(scheduler);
        var s = ReadSnapshot(state); ValidateFields(s, expectedDiscBlocks, deviceNumber, selected);
        if (string.IsNullOrEmpty(owner)) throw new InvalidDataException("Invalid ATAPI scheduler owner.");
        var cancelled = scheduler.Cancelled.ToHashSet();
        var owned = scheduler.Events.Where(e => e.Owner == owner).ToArray();
        if (owned.Any(e => e.Operation != "phase" || e.Payload.Length != 0))
            throw new InvalidDataException("Unknown ATAPI phase callback.");
        var live = owned.Where(e => !cancelled.Contains(e.Id)).ToArray();
        bool waiting = IsWaiting(s.Phase);
        if (waiting)
        {
            if (s.Event <= 0 || s.Deadline <= scheduler.Now || s.Deadline - scheduler.Now > PhaseDelayNanoseconds ||
                live.Length != 1 || live[0].Id != s.Event || live[0].Time != s.Deadline)
                throw new InvalidDataException("ATAPI phase token/deadline differs from its live callback.");
        }
        else if (s.Event != 0 || s.Deadline != 0 || live.Length != 0)
            throw new InvalidDataException("Inactive ATAPI phase owns a live callback.");
    }

    /// <summary>Returns the saved physical source after parsing its full field set; the caller separately checks topology and scheduler state.</summary>
    public static bool SavedInterruptAsserted(StateBlock state)
    {
        var s = ReadSnapshot(state); ValidateFields(s, s.HasDisc ? s.DiscBlocks : null, s.DeviceNumber, s.Selected);
        return s.PendingInterrupt && s.Selected && (s.Control & 2) == 0 && !s.HardwareResetHeld && !s.SrstActive;
    }

    /// <summary>Checks the exact board-reset device image, preserving the actual tray and cumulative diagnostics across assert and release.</summary>
    public static void ValidateHardResetSnapshot(StateBlock state, bool asserted)
    {
        var s = ReadSnapshot(state); ValidateFields(s, s.HasDisc ? s.DiscBlocks : null, s.DeviceNumber, s.Selected);
        byte[] signature = [0, 1, 1, 1, 0x14, 0xeb, 0, 0];
        if (s.HardwareResetHeld != asserted || s.Phase != (asserted ? AtapiPhase.HardwareResetHeld : AtapiPhase.Idle) ||
            s.Status != (asserted ? 0x80 : 0) || s.Control != (asserted ? 4 : 0) || s.Features != 0 ||
            !s.Task.AsSpan().SequenceEqual(signature) || s.Packet.Any(b => b != 0) || s.Data.Any(b => b != 0) ||
            s.PacketOffset != 0 || s.PayloadLength != 0 || s.PayloadOffset != 0 || s.PhaseLength != 0 || s.Offset != 0 ||
            s.ByteLimit != 0 || s.ReadLba != 0 || s.RemainingBlocks != 0 || s.ReadCommand || s.Identify ||
            s.PendingInterrupt || s.SrstActive || s.SrstPreserved || s.PreventRemoval || s.Started == s.TrayOpen ||
            !s.UnitAttention || s.UaAsc != 0x29 || s.SenseKey != 0 || s.SenseAsc != 0 || s.SenseAscq != 0 ||
            s.ClearSense || s.ClearUa || s.ShadowAbort || s.Event != 0 || s.Deadline != 0)
            throw new InvalidDataException("ATAPI device differs from its held board-reset image.");
    }

    /// <summary>Checks restored values only; media, interrupt pins, time and the queued phase callback are not republished or reread.</summary>
    public void AfterStateRestore() => ValidateFields(ReadSnapshot(SaveState()), HasDisc ? DiscBlocks : null, DeviceNumber, _selected);

    /// <summary>Recognizes only phases that actually created the one named persistent callback.</summary>
    private static bool IsWaiting(AtapiPhase phase) => phase is AtapiPhase.PacketReadyWait or AtapiPhase.CommandWait or
        AtapiPhase.DataInWait or AtapiPhase.StatusWait or AtapiPhase.SoftResetWait;

    /// <summary>Recognizes reserved-bit shapes only after successful packet decoding; incomplete or not-yet-executed CDBs may still be malformed.</summary>
    private static bool ValidAcceptedPacketShape(ReadOnlySpan<byte> packet)
    {
        byte command = packet[0];
        if (command is not (0 or 3 or 0x12 or 0x25 or 0x28 or 0xa8 or 0x43 or 0x5a or 0x1e or 0x1b or 0x2b)) return false;
        for (int i = 1; i < 12; i++)
        {
            int mask = command switch
            {
                3 or 0x12 => i == 4 ? 255 : 0,
                0x28 => i is >= 2 and <= 5 or 7 or 8 ? 255 : 0,
                0xa8 => i is >= 2 and <= 9 ? 255 : 0,
                0x43 => i == 1 ? 2 : i is 6 or 7 or 8 ? 255 : i == 9 ? 0xc0 : 0,
                0x5a => i is 2 or 7 or 8 ? 255 : 0,
                0x1e => i == 4 ? 1 : 0,
                0x1b => i == 4 ? 3 : 0,
                0x2b => i is >= 2 and <= 5 ? 255 : 0,
                _ => 0
            };
            if ((packet[i] & ~mask) != 0) return false;
        }
        if (command == 0x43 && (packet[9] >> 6 == 0 ? packet[6] is not (0 or 1 or 0xaa) : packet[9] >> 6 != 1 || packet[6] != 0)) return false;
        if (command == 0x5a && (packet[2] >> 6 == 3 || (packet[2] & 63) is not (1 or 0x0d or 0x0e or 0x2a or 0x3f))) return false;
        return true;
    }

    /// <summary>Checks array, cursor, protocol, reset and mechanical coherence while preserving completed IDENTIFY/odd-byte history.</summary>
    private static void ValidateFields(SnapshotData s, long? expectedDiscBlocks, int deviceNumber, bool selected)
    {
        if (s.Task is null || s.Task.Length != 8 || s.Packet is null || s.Packet.Length != 12 || s.Data is null || s.Data.Length != 2048 ||
            s.HasDisc != expectedDiscBlocks.HasValue || s.DiscBlocks != (expectedDiscBlocks ?? 0) ||
            s.HasDisc && (s.DiscBlocks <= 0 || s.DiscBlocks > 0x1_0000_0000L) ||
            deviceNumber is not (0 or 1) || s.DeviceNumber != deviceNumber || s.Selected != selected ||
            (uint)s.Phase > (uint)AtapiPhase.SrstHeld || (s.Control & ~6) != 0 || s.Generation == 0 ||
            s.CommandsReceived < 0 || s.PacketsCompleted < 0 || s.ReadBlockCount < 0 || s.DataBytesRead < 0 ||
            s.PacketOffset < 0 || s.PacketOffset > 12 || (s.PacketOffset & 1) != 0 ||
            s.PayloadLength < 0 || s.PayloadLength > 2048 || s.PayloadOffset < 0 || s.PayloadOffset > s.PayloadLength ||
            s.PhaseLength < 0 || s.PhaseLength > 2048 || s.Offset < 0 || s.Offset > s.PhaseLength ||
            (s.Offset & 1) != 0 && s.Offset != s.PhaseLength || s.PhaseLength > s.ByteLimit ||
            s.ReadLba < 0 || s.ReadLba > s.DiscBlocks || s.Event < 0 || s.Task[0] != 0 || s.Task[7] != 0 ||
            s.UaAsc is not (0x28 or 0x29) || s.SenseKey is not (0 or 2 or 3 or 5 or 6) ||
            s.TrayOpen && s.Started || s.Packet.AsSpan(s.PacketOffset).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Invalid ATAPI invariant, array, counter or transfer cursor.");

        if (s.HardwareResetHeld != (s.Phase == AtapiPhase.HardwareResetHeld) ||
            s.HardwareResetHeld && (s.Control != 4 || s.SrstActive || s.SrstPreserved) ||
            !s.HardwareResetHeld && s.SrstActive != ((s.Control & 4) != 0) ||
            s.SrstPreserved && (!s.SrstActive || s.Phase is AtapiPhase.HardwareResetHeld or AtapiPhase.SrstHeld) ||
            (s.Phase == AtapiPhase.SrstHeld) != (s.SrstActive && !s.SrstPreserved) ||
            s.ClearUa && (!s.ClearSense || !s.UnitAttention) ||
            (s.ClearSense || s.ClearUa) && (s.PacketOffset != 12 || s.Packet[0] != 3 ||
                s.Phase is not (AtapiPhase.DataInWait or AtapiPhase.DataIn or AtapiPhase.StatusWait)))
            throw new InvalidDataException("Invalid ATAPI reset, attention or sense-clear phase.");

        byte expectedStatus = s.Phase switch
        {
            AtapiPhase.Idle => s.Status,
            AtapiPhase.PacketOut or AtapiPhase.DataIn => 0x48,
            _ => 0x80
        };
        if (s.Status != expectedStatus || s.Phase == AtapiPhase.Idle && s.Status is not (0 or 0x40 or 0x41) ||
            s.Phase == AtapiPhase.PacketReadyWait && s.PacketOffset != 0 ||
            s.Phase == AtapiPhase.PacketOut && (s.PacketOffset >= 12 || s.Task[2] != 1) ||
            s.Phase == AtapiPhase.CommandWait && s.PacketOffset != 12 ||
            !s.Identify && (s.Phase is AtapiPhase.DataInWait or AtapiPhase.DataIn or AtapiPhase.StatusWait) && s.PacketOffset != 12 ||
            s.Identify && (s.Phase is not (AtapiPhase.DataInWait or AtapiPhase.DataIn) || s.PacketOffset != 0 ||
                s.ReadCommand || s.PayloadLength != 512 || s.ByteLimit != 512 || s.ReadLba != 0 || s.RemainingBlocks != 0 ||
                s.PayloadOffset != 0 || s.Phase == AtapiPhase.DataInWait && s.PhaseLength != 0))
            throw new InvalidDataException("ATAPI status/packet phase is incoherent.");

        bool cleanTransfer = s.Phase is AtapiPhase.PacketReadyWait or AtapiPhase.PacketOut or AtapiPhase.CommandWait or
            AtapiPhase.SoftResetWait or AtapiPhase.HardwareResetHeld or AtapiPhase.SrstHeld;
        if (cleanTransfer && (s.PayloadLength != 0 || s.PayloadOffset != 0 || s.PhaseLength != 0 || s.Offset != 0 ||
            s.ReadLba != 0 || s.RemainingBlocks != 0 || s.ReadCommand || s.Identify) ||
            (s.Phase is AtapiPhase.SoftResetWait or AtapiPhase.HardwareResetHeld or AtapiPhase.SrstHeld) && s.PacketOffset != 0 ||
            s.Phase == AtapiPhase.DataIn && (s.PhaseLength == 0 || s.Offset >= s.PhaseLength || (s.Offset & 1) != 0 ||
                s.PayloadOffset + s.PhaseLength > s.PayloadLength || s.Task[2] != 2 ||
                (s.Task[4] | s.Task[5] << 8) != s.PhaseLength ||
                (s.PhaseLength & 1) != 0 && (s.PayloadOffset + s.PhaseLength != s.PayloadLength || s.ReadCommand && s.RemainingBlocks != 1)) ||
            s.Phase == AtapiPhase.DataInWait && (s.PayloadLength == 0 && !s.ReadCommand ||
                s.PayloadOffset == s.PayloadLength && !(s.ReadCommand && s.RemainingBlocks != 0) ||
                s.PhaseLength == 0 && (s.PayloadOffset != 0 || s.ReadCommand && s.PayloadLength != 0) ||
                s.PhaseLength != 0 && (s.Offset != s.PhaseLength || s.PayloadOffset < s.PhaseLength)) ||
            s.Phase == AtapiPhase.StatusWait && (s.Offset != s.PhaseLength || s.PayloadOffset != s.PayloadLength || s.RemainingBlocks != 0))
            throw new InvalidDataException("ATAPI payload phase cannot produce its announced next bytes.");

        bool decodedPacket = s.PacketOffset == 12 && (s.Phase is AtapiPhase.DataInWait or AtapiPhase.DataIn or AtapiPhase.StatusWait or AtapiPhase.Idle);
        if (decodedPacket && !ValidAcceptedPacketShape(s.Packet))
            throw new InvalidDataException("ATAPI accepted packet contains an unsupported opcode or reserved field.");

        bool decodedRead = decodedPacket && s.Packet[0] is 0x28 or 0xa8;
        if (decodedRead && (!s.HasDisc || s.TrayOpen || !s.Started))
            throw new InvalidDataException("Decoded ATAPI READ has no ready immutable medium.");
        uint decodedReadCount = decodedRead ? (s.Packet[0] == 0x28 ?
            BinaryPrimitives.ReadUInt16BigEndian(s.Packet.AsSpan(7, 2)) :
            BinaryPrimitives.ReadUInt32BigEndian(s.Packet.AsSpan(6, 4))) : 0;
        if (decodedRead && decodedReadCount == 0 && (s.Phase is not (AtapiPhase.StatusWait or AtapiPhase.Idle) ||
            s.ReadCommand || s.ReadLba != 0 || s.RemainingBlocks != 0 || s.PayloadLength != 0 || s.PayloadOffset != 0 || s.PhaseLength != 0 || s.Offset != 0))
            throw new InvalidDataException("Zero-block ATAPI READ cannot retain or announce a data phase.");
        if (s.ReadCommand || s.ReadLba != 0 || decodedRead && decodedReadCount != 0)
        {
            if (!s.HasDisc || s.TrayOpen || !s.Started || s.PacketOffset != 12 || s.Packet[0] is not (0x28 or 0xa8))
                throw new InvalidDataException("ATAPI cooked READ has no matching media/packet context.");
            uint initial = BinaryPrimitives.ReadUInt32BigEndian(s.Packet.AsSpan(2, 4));
            uint count = s.Packet[0] == 0x28 ? BinaryPrimitives.ReadUInt16BigEndian(s.Packet.AsSpan(7, 2)) :
                BinaryPrimitives.ReadUInt32BigEndian(s.Packet.AsSpan(6, 4));
            ulong end = (ulong)initial + count;
            if (count == 0 || end > (ulong)s.DiscBlocks || s.ReadLba < initial ||
                (ulong)s.ReadLba + s.RemainingBlocks != end || s.ReadCommand && s.RemainingBlocks == 0 && s.Phase != AtapiPhase.StatusWait ||
                !s.ReadCommand && (s.RemainingBlocks != 0 || s.Phase != AtapiPhase.Idle) ||
                s.ReadCommand && (s.ByteLimit < 2 || s.PayloadLength is not (0 or 2048) ||
                    s.Phase is not (AtapiPhase.DataInWait or AtapiPhase.DataIn or AtapiPhase.StatusWait)))
                throw new InvalidDataException("ATAPI cooked READ range/count differs from its accepted CDB.");
        }
        else if (s.RemainingBlocks != 0)
            throw new InvalidDataException("Inactive ATAPI READ retains a block count.");

        if (!IsWaiting(s.Phase) && (s.Event != 0 || s.Deadline != 0) || IsWaiting(s.Phase) && (s.Event <= 0 || s.Deadline == 0))
            throw new InvalidDataException("ATAPI phase has an invalid event envelope.");
    }
}
