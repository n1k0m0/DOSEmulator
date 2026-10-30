// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics.CodeAnalysis;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class Ne2000
{
    /// <summary>Checks the entire serialized controller and its exact live callbacks before opening or branching media.
    /// The optional immutable port also binds the payload to the already validated board definition.</summary>
    public static void ValidateSnapshot(StateBlock state, SchedulerState scheduler, string node, ushort? expectedBase = null)
    {
        DeviceScheduler.ValidateState(scheduler); var r = new StateReader(state, "DOSEmulator.Hardware.Ne2000");
        ushort address = r.Value<ushort>("_basePort");
        RequireNetwork(address <= 0xffe0 && (address & 31) == 0 && (!expectedBase.HasValue || address == expectedBase.Value), "constructor port binding");
        byte[] ram = r.Value<byte[]>("_ram"), par = r.Value<byte[]>("_physicalAddress"), mar = r.Value<byte[]>("_multicast"), tally = r.Value<byte[]>("_tally");
        byte[] fifo = r.Value<byte[]>("_loopbackFifo"); byte fifoCursor = r.Value<byte>("_fifoCursor");
        RequireNetwork(ram is { Length: RamLength } && par is { Length: 6 } && mar is { Length: 8 } && tally is { Length: 3 } &&
            fifo is { Length: 8 } && fifoCursor <= 7 && tally.All(value => value <= 0xc0), "packet RAM/register array shape");
        byte command = r.Value<byte>("_command"), isr = r.Value<byte>("_isr"), imr = r.Value<byte>("_imr"), data = r.Value<byte>("_dataConfig"),
            rx = r.Value<byte>("_rxConfig"), tx = r.Value<byte>("_txConfig");
        r.Value<byte>("_txStatus"); r.Value<byte>("_rxStatus");
        r.Value<byte>("_pageStart"); r.Value<byte>("_pageStop");
        r.Value<byte>("_boundary"); r.Value<byte>("_currentPage"); r.Value<byte>("_transmitPage");
        r.Value<ushort>("_remoteAddress"); r.Value<ushort>("_remoteCount");
        r.Value<ushort>("_transmitCount"); ushort latch = r.Value<ushort>("_remoteReadLatch"); byte valid = r.Value<byte>("_remoteReadValidBytes");
        r.Value<bool>("_remotePci"); bool auto = r.Value<bool>("_remoteAutoPacket"), irq = r.Value<bool>("_interruptAsserted");
        bool cable = r.Value<bool>("_cableConnected"), enabled = r.Value<bool>("_externalEnabled"); r.Value<byte>("_autoPacketNext");
        bool lostCarrier = r.Value<bool>("_transmitCarrierLost");
        byte[] outgoing = r.Value<byte[]>("_transmitFrame"), incoming = r.Value<byte[]>("_receiveFrame"); byte[][] queue = r.Value<byte[][]>("_receiveQueue");
        byte activeTx = r.Value<byte>("_activeTransmitConfig");
        long transmit = r.Value<long>("_transmitEvent"), receive = r.Value<long>("_receiveEvent");
        ulong transmitAt = r.Value<ulong>("_transmitDeadline"), receiveAt = r.Value<ulong>("_receiveDeadline");
        ulong txEligible = r.Value<ulong>("_transmitEligibleAt"), rxEligible = r.Value<ulong>("_receiveEligibleAt"); r.Finish();
        RequireNetwork((imr & 0x80) == 0 && (data & 0x80) == 0 && (rx & 0xc0) == 0 && (tx & 0xe0) == 0 && (activeTx & 0xe0) == 0 &&
            valid <= 2 && irq == ((isr & imr & 0x7f) != 0), "register masks and masked IRQ");
        RequireNetwork(outgoing is not null && incoming is not null && queue is not null &&
            (outgoing.Length == 0 || outgoing.Length is >= 14 and <= MaximumFrameLength + 4) &&
            (incoming.Length == 0 || incoming.Length is >= 14 and <= MaximumFrameLength) &&
            queue.Length + (incoming.Length == 0 ? 0 : 1) <= ReceiveQueueLimit &&
            queue.All(frame => frame is not null && frame.Length is >= 14 and <= MaximumFrameLength), "bounded copied frame storage");
        RequireNetwork((transmit != 0) == (outgoing.Length != 0) && (transmit != 0) == ((command & 4) != 0) &&
            (receive != 0) == (incoming.Length != 0) && (queue.Length == 0 || receive != 0) &&
            ((command & 1) == 0 || queue.Length == 0) &&
            (enabled || (transmit == 0 && receive == 0 && queue.Length == 0)) &&
            (cable || (receive == 0 && queue.Length == 0)), "active frames and command gates");
        RequireNetwork(!lostCarrier || (transmit != 0 && (activeTx & 6) == 0), "captured physical carrier loss");
        RequireNetwork(transmit == 0 || (activeTx & 6) != 0 || cable || lostCarrier, "disconnected active normal transmission");
        RequireNetwork((command & 1) == 0 || transmit != 0 || receive != 0 || (isr & 0x80) != 0, "quiesced software STOP completion");
        RequireNetwork(outgoing.Length <= MaximumFrameLength + ((activeTx & 1) != 0 ? 4 : 0), "captured CRC-inhibit frame size");
        int mode = (command >> 3) & 7;
        RequireNetwork(valid == 0 || (mode == 1 || (mode == 3 && auto)), "prefetched remote read mode");
        RequireNetwork(valid != 1 || (latch & 0xff00) == 0xff00, "last single-byte prefetch high lane");
        RequireNetwork(!auto || mode == 3, "automatic packet command");
        RequireNetwork((txEligible <= scheduler.Now || txEligible - scheduler.Now <= 9_600) &&
            (rxEligible <= scheduler.Now || rxEligible - scheduler.Now <= 9_600), "finite physical interframe phase");
        ValidateNetworkEvent(scheduler, node, "transmit", transmit, transmitAt, outgoing.Length == 0 ? 0 : WireDuration(outgoing.Length, (activeTx & 1) == 0) + 9_600);
        ValidateNetworkEvent(scheduler, node, "receive", receive, receiveAt, incoming.Length == 0 ? 0 : WireDuration(incoming.Length) + 9_600);
        var active = scheduler.Events.Where(e => e.Owner == node && !scheduler.Cancelled.Contains(e.Id));
        RequireNetwork(active.All(e => e.Operation == "transmit" || e.Operation == "receive"), "unknown active NIC callback");
    }
    /// <summary>Checks one exact token/owner/operation/deadline association while allowing old cancelled queue entries to remain.</summary>
    internal static void ValidateNetworkEvent(SchedulerState scheduler, string node, string operation, long token, ulong deadline, ulong maximumRemaining)
    {
        var active = scheduler.Events.Where(e => e.Owner == node && e.Operation == operation && !scheduler.Cancelled.Contains(e.Id)).ToArray();
        RequireNetwork(token >= 0 && (token == 0 ? deadline == 0 && active.Length == 0 :
            token <= scheduler.Sequence && deadline > scheduler.Now && deadline - scheduler.Now <= maximumRemaining && active.Length == 1 &&
            active[0].Id == token && active[0].Time == deadline && active[0].Payload.Length == 0), "exact " + operation + " scheduler association");
    }
    /// <summary>Raises a controlled invalid-container failure before any controller or media state can be adopted.</summary>
    private static void RequireNetwork([DoesNotReturnIf(false)] bool value, string detail)
    { if (!value) throw new InvalidDataException("Invalid NE2000 state: " + detail + "."); }
}
