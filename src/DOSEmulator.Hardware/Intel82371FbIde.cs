// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Read-only actual busmaster cursor and device handshake at an owner boundary.</summary>
public sealed record PiixIdeChannelState(byte Command, byte Status, uint DescriptorTablePointer, uint DescriptorAddress,
    uint RegionAddress, int DescriptorOffset, int DescriptorRemaining, bool EndOfTable, long BytesTransferred,
    int DeviceIndex, AtaDmaDirection Direction, bool DmaReady, int SectorOffset, int SectorsRemaining,
    bool AtaBusy, bool WriteCommitPending, long EventToken)
{
    /// <summary>Hardware active bit, independent from the software start bit and the ATA completion IRQ.</summary>
    public bool Active => (Status & 1) != 0;
}

/// <summary>Original 82371FB A1 function-one subset: legacy I/O gates, relocatable BMIBA, and physical PRD-driven MDMA0.
/// PCI/ATA timing uses named nominal grants; CF9, electrical FIFOs and chipset reset domains remain separate.</summary>
public sealed partial class Intel82371FbIde : IPciConfigurationFunction, IIoPortDecoder
{
    /// <summary>One functional 32-byte MDMA0 grant; not a measured PCI/IDE line-buffer or arbitration calibration.</summary>
    public const ulong BurstDelayNanoseconds = 7_680;
    private readonly DeviceScheduler _scheduler;
    private readonly Intel82437Fx _host;
    private readonly AtaPioController _primary, _secondary;
    private readonly IoPortBus _ports;
    private readonly byte[] _config = new byte[256];
    private readonly long[] _events = new long[2];
    private readonly Channel[] _channels = [new(), new()];

    private sealed partial class Channel
    {
        public byte Command, Status;
        public uint Prdt, DescriptorAddress, RegionAddress;
        public int RegionLength, RegionOffset, DescriptorIndex;
        public bool Loaded, Eot, IrqHigh;
        public long BytesTransferred;
    }

    /// <summary>Binds DMA-capable owned ATA channels and the fixed host target without registering or replacing bus mappings.</summary>
    public Intel82371FbIde(DeviceScheduler scheduler, Intel82437Fx host, AtaPioController primary,
        AtaPioController secondary, IoPortBus ports)
    {
        ArgumentNullException.ThrowIfNull(scheduler); ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(primary); ArgumentNullException.ThrowIfNull(secondary); ArgumentNullException.ThrowIfNull(ports);
        if (!primary.DmaAvailable || !secondary.DmaAvailable || ReferenceEquals(primary, secondary))
            throw new ArgumentException("PIIX IDE requires two distinct DMA-capable ATA channels.");
        _scheduler = scheduler; _host = host; _primary = primary; _secondary = secondary; _ports = ports;
        for (int i = 0; i < _config.Length; i++) _config[i] = ResetValue(i);
        _channels[0].IrqHigh = primary.InterruptAsserted; _channels[1].IrqHigh = secondary.InterruptAsserted;
        primary.DmaStateChanged += WakePrimary; secondary.DmaStateChanged += WakeSecondary;
        primary.InterruptChanged += PrimaryInterrupt; secondary.InterruptChanged += SecondaryInterrupt;
    }

    /// <summary>Current 16-bit BM I/O base, retaining the original chip's limited BAR width and fixed I/O bit.</summary>
    public ushort BusMasterBase => (ushort)(BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan(0x20, 4)) & 0xfff0);
    /// <summary>PCI I/O-space decode enable, independent from device state and the busmaster execution gate.</summary>
    public bool IoSpaceEnabled => (_config[4] & 1) != 0;
    /// <summary>PCI master permission; clearing it pauses new cycles without rewinding a cursor or ATA state.</summary>
    public bool BusMasterEnabled => (_config[4] & 4) != 0;
    /// <summary>Whether both PCI IOSE and this channel's IDETIM.DE currently decode its legacy target ports.</summary>
    public bool LegacyEnabled(int channel)
    { ValidateChannel(channel); return IoSpaceEnabled && (_config[0x41 + channel * 2] & 0x80) != 0; }
    /// <summary>Returns one exact nonmutating diagnostic cursor without serializing a component graph.</summary>
    public PiixIdeChannelState GetChannelState(int channel)
    {
        ValidateChannel(channel); var c = _channels[channel]; var ata = Ata(channel);
        bool busy = (ata.DmaStatus & 0x80) != 0;
        return new(c.Command, c.Status, c.Prdt, c.DescriptorAddress, c.RegionAddress, c.RegionOffset,
            Math.Max(0, c.RegionLength - c.RegionOffset), c.Eot, c.BytesTransferred, ata.DmaDriveIndex,
            ata.DmaDirection, ata.DmaReady, ata.DmaOffset, ata.DmaRemainingSectors, busy,
            ata.DmaDirection == AtaDmaDirection.MemoryToDevice && ata.DmaOffset == 512 && busy, _events[channel]);
    }
    /// <summary>Rejects a missing channel before any register, timer or peer is touched.</summary>
    private static void ValidateChannel(int channel)
    { if ((uint)channel >= 2) throw new ArgumentOutOfRangeException(nameof(channel)); }
    /// <summary>Resolves a fixed channel peer, never the currently selected drive or a newly constructed device.</summary>
    private AtaPioController Ata(int channel) => channel == 0 ? _primary : _secondary;
    /// <summary>Returns the deliberately selected MSE=1 subset reset response and documented A1 register bytes.</summary>
    private static byte ResetValue(int offset) => offset switch
    {
        0 => 0x86, 1 => 0x80, 2 => 0x30, 3 => 0x12, 4 => 2, 6 => 0x80, 7 => 2,
        8 => 2, 9 => 0x80, 10 => 1, 11 => 1, 0x20 => 1, _ => 0
    };
    /// <summary>Original PIIX masks; SIDETIM, PIIX3 and native-mode BARs remain absent.</summary>
    private static byte WritableMask(int offset) => offset switch
    { 4 => 5, 7 => 0x38, 0x0d => 0xf0, 0x20 => 0xf0, 0x21 => 0xff,
        0x40 or 0x42 => 0xff, 0x41 or 0x43 => 0xb3, _ => 0 };
    /// <summary>Checks complete byte-lane extents before invoking register or decode effects.</summary>
    private static void ValidateTransaction(int offset, int width)
    {
        WidthMask(width);
        if (offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset));
    }
    /// <summary>Returns the exact mask for a supported complete transaction width.</summary>
    private static uint WidthMask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Reads one little-endian configuration transaction without changing gates, IRQs or virtual time.</summary>
    public uint ReadConfiguration(int offset, int width)
    {
        ValidateTransaction(offset, width); uint value = 0;
        for (int lane = 0; lane < width; lane++) value |= (uint)_config[offset + lane] << (8 * lane);
        return value;
    }
    /// <summary>Commits all configuration lanes before applying only the master-permission transition.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        ValidateTransaction(offset, width); bool before = BusMasterEnabled;
        for (int lane = 0; lane < width; lane++)
        {
            int at = offset + lane; byte incoming = (byte)(value >> (8 * lane)), mask = WritableMask(at);
            if (at == 7) _config[at] &= (byte)~(incoming & mask);
            else _config[at] = (byte)((_config[at] & ~mask) | (incoming & mask));
        }
        if (before != BusMasterEnabled) { WakeChannel(0); WakeChannel(1); }
    }
    /// <summary>Accepts only a complete BM window in fixed-board-free space; a colliding BAR stays a readable inert target choice.</summary>
    public bool Decodes(ushort port, int width)
    {
        WidthMask(width); int offset = port - BusMasterBase;
        return IoSpaceEnabled && offset >= 0 && offset <= 16 - width && _ports.IsFixedWindowAvailable(BusMasterBase, 16);
    }
    /// <summary>Maps only actual task and alt-status ports; 3F7/377 always remain outside this device for ISA/FDC forwarding.</summary>
    private static int LegacyChannel(ushort port) => port is >= 0x1f0 and <= 0x1f7 or 0x3f6 ? 0 :
        port is >= 0x170 and <= 0x177 or 0x376 ? 1 : -1;
    /// <summary>Checks TIME0 or TIME1 for the currently selected drive's explicit 32-bit-to-two-word data-port path.</summary>
    private bool Pio32Enabled(int channel) => (_config[0x40 + channel * 2] & (Ata(channel).SelectedDriveIndex == 0 ? 1 : 0x10)) != 0;
    /// <summary>Reads gated legacy ports or all requested BM byte lanes without hidden status acknowledgements.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = WidthMask(width); int channel = LegacyChannel(port);
        if (channel >= 0)
        {
            if (!LegacyEnabled(channel)) return mask;
            bool data = port == (channel == 0 ? 0x1f0 : 0x170);
            if (data ? width == 1 : width != 1) return mask;
            var ata = Ata(channel);
            if (width == 4 && data)
                return Pio32Enabled(channel) ? ata.Read(port, 2) | ata.Read(port, 2) << 16 : uint.MaxValue;
            return ata.Read(port, width);
        }
        if (!Decodes(port, width)) return mask;
        int offset = port - BusMasterBase; uint result = 0;
        for (int lane = 0; lane < width; lane++) result |= (uint)ReadRegister(offset + lane) << (8 * lane);
        return result;
    }
    /// <summary>Returns one passive hardware register lane; reserved lanes float as the documented zero response.</summary>
    private byte ReadRegister(int offset)
    {
        var c = _channels[offset / 8]; int lane = offset & 7;
        return lane switch { 0 => c.Command, 2 => c.Status, >= 4 and <= 7 => (byte)(c.Prdt >> (8 * (lane - 4))), _ => 0 };
    }
    /// <summary>Writes one complete gated target transaction; initial starts occur only after all accepted BM lanes commit.</summary>
    public void Write(ushort port, uint value, int width)
    {
        WidthMask(width); int channel = LegacyChannel(port);
        if (channel >= 0)
        {
            if (!LegacyEnabled(channel)) return;
            bool data = port == (channel == 0 ? 0x1f0 : 0x170);
            if (data ? width == 1 : width != 1) return;
            var ata = Ata(channel);
            if (width == 4 && data)
            { if (Pio32Enabled(channel)) { ata.Write(port, value & 65535, 2); ata.Write(port, value >> 16, 2); } }
            else ata.Write(port, value, width);
            return;
        }
        if (!Decodes(port, width)) return;
        byte before0 = _channels[0].Command, before1 = _channels[1].Command;
        int offset = port - BusMasterBase;
        for (int lane = 0; lane < width; lane++) WriteRegister(offset + lane, (byte)(value >> (8 * lane)));
        ApplyCommandTransition(0, before0); ApplyCommandTransition(1, before1);
    }
    /// <summary>Applies masked BM register bytes; active direction and descriptor-base writes remain unchanged by explicit policy.</summary>
    private void WriteRegister(int offset, byte value)
    {
        var c = _channels[offset / 8]; int lane = offset & 7;
        if (lane == 0)
        {
            byte incoming = (byte)(value & 9);
            if ((c.Status & 1) != 0) incoming = (byte)((incoming & 1) | (c.Command & 8));
            c.Command = incoming;
        }
        else if (lane == 2) c.Status = (byte)((c.Status & ~0x66) | (value & 0x60) | (c.Status & 6 & ~value));
        else if (lane >= 4 && (c.Status & 1) == 0)
        {
            int shift = 8 * (lane - 4); c.Prdt = (c.Prdt & ~(255u << shift)) | (uint)value << shift; c.Prdt &= 0xfffffffc;
        }
    }
    /// <summary>Starts from the current table on a true start edge; stopping discards only BM execution state.</summary>
    private void ApplyCommandTransition(int channel, byte before)
    {
        var c = _channels[channel];
        if ((before & 1) == (c.Command & 1)) return;
        CancelEvent(channel); ClearCursor(c);
        if ((c.Command & 1) == 0) { c.Status &= 0xfe; return; }
        c.Status |= 1; c.DescriptorAddress = c.Prdt; WakeChannel(channel);
    }
    /// <summary>Discards controller execution cursor while preserving programmed registers and sticky IRQ/error/capability status.</summary>
    private static void ClearCursor(Channel c)
    {
        c.DescriptorAddress = c.RegionAddress = 0; c.RegionLength = c.RegionOffset = c.DescriptorIndex = 0;
        c.Loaded = c.Eot = false; c.BytesTransferred = 0;
    }
    /// <summary>Cancels only the next BM grant, leaving device-ready and already committed media work intact.</summary>
    private void CancelEvent(int channel)
    { if (_events[channel] != 0) _scheduler.Cancel(_events[channel]); _events[channel] = 0; }
    /// <summary>Handles the primary channel's explicit readiness transition.</summary>
    private void WakePrimary() => WakeChannel(0);
    /// <summary>Handles the secondary channel's explicit readiness transition.</summary>
    private void WakeSecondary() => WakeChannel(1);
    /// <summary>Observes the true nIEN-gated primary IRQ14 pin before any board routing selection.</summary>
    private void PrimaryInterrupt(bool high) => ObserveInterrupt(0, high);
    /// <summary>Observes the true nIEN-gated secondary MIRQ0 pin before any board routing selection.</summary>
    private void SecondaryInterrupt(bool high) => ObserveInterrupt(1, high);
    /// <summary>Latches a new physical ATA assertion only; W1C while high never fabricates another edge.</summary>
    private void ObserveInterrupt(int channel, bool high)
    {
        var c = _channels[channel]; if (high && !c.IrqHigh) c.Status |= 4; c.IrqHigh = high;
    }
    /// <summary>Pauses on disabled master permission or absent/mismatched device request, scheduling no polling timer.</summary>
    private void WakeChannel(int channel)
    {
        var c = _channels[channel]; var ata = Ata(channel);
        if ((c.Status & 1) == 0 || !BusMasterEnabled) { CancelEvent(channel); return; }
        bool read = (c.Command & 8) != 0;
        bool ready = ata.DmaReady && ata.DmaDirection == (read ? AtaDmaDirection.DeviceToMemory : AtaDmaDirection.MemoryToDevice);
        // START fetches its first PRD. A later descriptor is fetched only for another actual device request.
        // Terminal ATA completion can legitimately leave IRQ=1/Active=1 with no further descriptor cycle.
        if (!c.Loaded && (c.DescriptorIndex == 0 && c.BytesTransferred == 0 || ready) && !LoadDescriptor(channel)) return;
        if (!ready) { CancelEvent(channel); return; }
        if (_events[channel] != 0) return;
        if (channel == 0) _events[0] = _scheduler.ScheduleAfter(BurstDelayNanoseconds,
            () => { _events[0] = 0; TransferBurst(0); }, this, "primary-burst");
        else _events[1] = _scheduler.ScheduleAfter(BurstDelayNanoseconds,
            () => { _events[1] = 0; TransferBurst(1); }, this, "secondary-burst");
    }
    /// <summary>Fetches exactly one physical eight-byte PRD after complete PCI target and table-page checks.</summary>
    private bool LoadDescriptor(int channel)
    {
        var c = _channels[channel];
        if ((c.DescriptorAddress & 0xfffff000) != (c.Prdt & 0xfffff000) || (c.DescriptorAddress & 4095) > 4088 || c.DescriptorIndex >= 512)
        { StopWithoutPciError(channel); return false; }
        if (!_host.TryGetPciMemorySpan(c.DescriptorAddress, 8, false, out var bytes)) { PciAbort(channel); return false; }
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0xfffffffe;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) & 0xfffe; if (count == 0) count = 65536;
        if ((address & 65535) + count > 65536) { StopWithoutPciError(channel); return false; }
        c.RegionAddress = address; c.RegionLength = count; c.RegionOffset = 0; c.Eot = (bytes[7] & 0x80) != 0; c.Loaded = true;
        return true;
    }
    /// <summary>Copies one prevalidated physical grant before consuming ATA bytes, keeping full-sector device commits separate.</summary>
    private void TransferBurst(int channel)
    {
        var c = _channels[channel]; var ata = Ata(channel);
        if ((c.Status & 1) == 0 || !BusMasterEnabled || !ata.DmaReady) { WakeChannel(channel); return; }
        if (!c.Loaded && !LoadDescriptor(channel)) return;
        bool read = (c.Command & 8) != 0;
        if (ata.DmaDirection != (read ? AtaDmaDirection.DeviceToMemory : AtaDmaDirection.MemoryToDevice)) return;
        int count = Math.Min(32, Math.Min(c.RegionLength - c.RegionOffset, 512 - ata.DmaOffset));
        ulong address = (ulong)c.RegionAddress + (uint)c.RegionOffset;
        if (count <= 0 || (count & 1) != 0 || address > uint.MaxValue) { StopWithoutPciError(channel); return; }
        if (!_host.TryGetPciMemorySpan((uint)address, count, read, out var bytes)) { PciAbort(channel); return; }
        int copied = read ? ata.ReadDma(bytes) : ata.WriteDma(bytes);
        if (copied != count) throw new InvalidOperationException("ATA DMA grant changed after its complete target preflight.");
        c.RegionOffset += copied; c.BytesTransferred += copied;
        if (c.RegionOffset == c.RegionLength)
        {
            if (c.Eot) { c.Status &= 0xfe; CancelEvent(channel); return; }
            c.DescriptorIndex++; c.DescriptorAddress = unchecked(c.DescriptorAddress + 8); c.Loaded = false;
        }
        WakeChannel(channel);
    }
    /// <summary>Stops an invalid software descriptor without inventing a PCI abort, ATA completion or new IRQ.</summary>
    private void StopWithoutPciError(int channel)
    { _channels[channel].Status &= 0xfe; CancelEvent(channel); }
    /// <summary>Records an actual rejected PCI memory cycle only in this function's master-abort and BM-error latches.</summary>
    private void PciAbort(int channel)
    { _config[7] |= 0x20; _channels[channel].Status = (byte)((_channels[channel].Status | 2) & ~1); CancelEvent(channel); }
    /// <summary>Checks configuration fixed bits before any untrusted state reaches a newly prepared controller.</summary>
    public static void ValidateConfigurationBytes(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != 256) throw new InvalidDataException("Invalid PIIX IDE configuration size.");
        for (int i = 0; i < configuration.Length; i++)
        {
            int fixedMask = ~WritableMask(i) & 255;
            if ((configuration[i] & fixedMask) != (ResetValue(i) & fixedMask)) throw new InvalidDataException($"Invalid PIIX IDE fixed bits at {i:X2}.");
        }
    }
    /// <summary>Checks only parent scalars; child and peer relationships are validated by the complete pre-branch graph.</summary>
    private void ValidateState() => ValidateConfigurationBytes(_config);
    /// <summary>Verifies constructor peer identity and observed raw device IRQ pins without republishing any source.</summary>
    public bool HasBindings(DeviceScheduler scheduler, Intel82437Fx host, AtaPioController primary, AtaPioController secondary, IoPortBus ports) =>
        ReferenceEquals(_scheduler, scheduler) && ReferenceEquals(_host, host) && ReferenceEquals(_primary, primary) && ReferenceEquals(_secondary, secondary) &&
        ReferenceEquals(_ports, ports) && primary.DmaAvailable && secondary.DmaAvailable && _channels[0].IrqHigh == primary.InterruptAsserted && _channels[1].IrqHigh == secondary.InterruptAsserted;
}
