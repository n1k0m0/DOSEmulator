// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Functional DP8390D/NE2000 with 16 KiB packet RAM, real remote DMA, receive rings and 10 Mbit/s frames.
/// Electrical FIFO arbitration, collisions and external loopback media are not calibrated by this local model.</summary>
public sealed partial class Ne2000 : IIoPortDevice
{
    public const int RamStart = 0x4000, RamLength = 0x4000, MaximumFrameLength = 1514, ReceiveQueueLimit = 64;
    private readonly DeviceScheduler _clock;
    private readonly ushort _basePort;
    private readonly byte[] _mac;
    private readonly Action<bool> _interrupt;
    private readonly byte[] _ram = new byte[RamLength];
    private readonly byte[] _physicalAddress = new byte[6], _multicast = new byte[8], _tally = new byte[3], _loopbackFifo = new byte[8];
    private byte _fifoCursor;
    private byte _command, _isr, _imr, _dataConfig, _rxConfig, _txConfig, _txStatus, _rxStatus;
    private byte _pageStart, _pageStop, _boundary, _currentPage, _transmitPage;
    private ushort _remoteAddress, _remoteCount, _transmitCount, _remoteReadLatch;
    private byte _remoteReadValidBytes;
    private bool _remotePci, _remoteAutoPacket, _interruptAsserted, _cableConnected, _externalEnabled = true, _transmitCarrierLost;
    private byte _autoPacketNext;
    private byte[] _transmitFrame = [], _receiveFrame = [];
    private byte[][] _receiveQueue = [];
    private byte _activeTransmitConfig;
    private long _transmitEvent, _receiveEvent;
    private ulong _transmitDeadline, _receiveDeadline, _transmitEligibleAt, _receiveEligibleAt;

    /// <summary>Binds one immutable local board identity without registering ports or connecting a host endpoint.</summary>
    public Ne2000(DeviceScheduler clock, ushort basePort, byte[] mac, Action<bool> interrupt)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(mac); ArgumentNullException.ThrowIfNull(interrupt);
        if (basePort > 0xffe0 || (basePort & 0x1f) != 0) throw new ArgumentOutOfRangeException(nameof(basePort));
        if (mac.Length != 6 || (mac[0] & 1) != 0 || mac.All(value => value == 0))
            throw new ArgumentException("A six-byte nonzero unicast Ethernet address is required.", nameof(mac));
        _clock = clock; _basePort = basePort; _mac = mac.ToArray(); _interrupt = interrupt; Reset();
    }

    /// <summary>Fixed ISA base; a PCI wrapper supplies its own relocatable bus address.</summary>
    public ushort BasePort => _basePort;
    /// <summary>Actual masked NIC interrupt level, independent of board routing.</summary>
    public bool InterruptAsserted => _interruptAsserted;
    /// <summary>Explicit virtual medium input; no NE2000 link-status register is invented.</summary>
    public bool CableConnected => _cableConnected;
    /// <summary>Copied immutable construction MAC, suitable for metadata without exposing mutable device storage.</summary>
    public byte[] MacAddress => _mac.ToArray();
    /// <summary>Pending bounded store-and-forward input count, including an active frame.</summary>
    public int PendingReceiveCount => _receiveQueue.Length + (_receiveFrame.Length == 0 ? 0 : 1);
    /// <summary>Exact scheduler token for generator/import validation.</summary>
    public long TransmitEventToken => _transmitEvent;
    /// <summary>Exact scheduler token for generator/import validation.</summary>
    public long ReceiveEventToken => _receiveEvent;
    /// <summary>Exact future frame completion deadline, or zero when no transmit is active.</summary>
    public ulong TransmitDeadline => _transmitDeadline;
    /// <summary>Exact future frame completion deadline, or zero when no receive is active.</summary>
    public ulong ReceiveDeadline => _receiveDeadline;
    /// <summary>Publishes a private frame copy after successful physical transmission; bytes exclude the generated FCS.</summary>
    public event Action<byte[]>? Transmitted;

    /// <summary>Checks immutable constructor dependencies without inspecting media, host devices or event delegates.</summary>
    public bool HasBindings(DeviceScheduler clock, ushort basePort, byte[] mac) => ReferenceEquals(clock, _clock) &&
        basePort == _basePort && mac is not null && _mac.AsSpan().SequenceEqual(mac);
    /// <summary>Sets the explicit virtual cable input without creating packets or mutating status registers.</summary>
    public void SetCableConnected(bool connected)
    {
        _cableConnected = connected;
        if (!connected)
        {
            CancelReceive(); _receiveQueue = [];
            if (_transmitEvent != 0 && (_activeTransmitConfig & 6) == 0) _transmitCarrierLost = true;
            FinishSoftwareStop();
        }
    }
    /// <summary>Gates the original RTL power-down/programming modes without changing its external cable selection.</summary>
    internal void SetExternalEnabled(bool enabled)
    {
        if (_externalEnabled == enabled) return; _externalEnabled = enabled;
        if (!enabled)
        {
            CancelReceive(); _receiveQueue = [];
            if (_transmitEvent != 0) { CancelTransmit(); _txStatus = 8; _isr |= 8; UpdateInterrupt(); }
            FinishSoftwareStop();
        }
    }
    /// <summary>Command page used by the PCI wrapper to decode original Realtek configuration registers.</summary>
    internal int RegisterPage => _command >> 6;
    /// <summary>Shares the immutable virtual clock with the PCI configuration peer, never a host time source.</summary>
    internal DeviceScheduler Clock => _clock;

    /// <summary>Resets controller work and registers while retaining packet RAM, immutable PROM and powered cable input.</summary>
    public void Reset()
    {
        CancelTransmit(); CancelReceive(); _receiveQueue = [];
        _command = 0x21; _isr = 0x80; _dataConfig = 4; _imr = _rxConfig = _txConfig = _txStatus = _rxStatus = 0;
        _pageStart = _pageStop = _boundary = _currentPage = _transmitPage = 0;
        _remoteAddress = _remoteCount = _transmitCount = _remoteReadLatch = 0; _remoteReadValidBytes = 0;
        _remotePci = _remoteAutoPacket = false; _autoPacketNext = _activeTransmitConfig = 0;
        _transmitEligibleAt = _receiveEligibleAt = 0; _externalEnabled = true;
        Array.Clear(_physicalAddress); Array.Clear(_multicast); Array.Clear(_tally); Array.Clear(_loopbackFifo); _fifoCursor = 0; UpdateInterrupt();
    }
    /// <summary>Accepts only complete supported ISA transaction widths; wide register accesses do not touch adjacent registers.</summary>
    public uint Read(ushort port, int width) => ReadOffset(port - _basePort, width, false);
    /// <summary>Accepts a fixed ISA access without changing the decoder after construction.</summary>
    public void Write(ushort port, uint value, int width) => WriteOffset(port - _basePort, value, width, false);
    /// <summary>Returns a complete data-port transfer or one register byte under the selected original board wiring.</summary>
    internal uint ReadOffset(int offset, int width, bool pci)
    {
        uint mask = WidthMask(width);
        if ((uint)offset >= 32) return mask;
        if (offset >= 0x18) { if (width == 1) { Reset(); return 0; } return mask; }
        if (offset >= 0x10)
        {
            if (offset + width > 0x18 || (width == 4 && !pci)) return mask;
            if (!_externalEnabled) return mask;
            if (width == 4) return ReadRemote(2) | ReadRemote(2) << 16;
            return ReadRemote(width);
        }
        if (width != 1) return mask;
        if (offset == 0) return _command;
        return RegisterPage switch
        {
            0 => ReadPageZero(offset, pci),
            1 => offset <= 6 ? _physicalAddress[offset - 1] : offset == 7 ? _currentPage : _multicast[offset - 8],
            2 => offset switch { 1 => _pageStart, 2 => _pageStop, 3 => pci ? (byte)0 : _boundary, 4 => _transmitPage,
                0x0c => _rxConfig, 0x0d => _txConfig, 0x0e => (byte)(_dataConfig | (pci ? 0x80 : 0)), 0x0f => _imr, _ => (byte)0 },
            _ => 0xff
        };
    }
    /// <summary>Writes complete data transfers or a selected register, with no partial effects from unsupported bus widths.</summary>
    internal void WriteOffset(int offset, uint value, int width, bool pci)
    {
        WidthMask(width);
        if ((uint)offset >= 32) return;
        if (offset >= 0x18) { if (width == 1) Reset(); return; }
        if (offset >= 0x10)
        {
            if (!_externalEnabled || offset + width > 0x18 || (width == 4 && !pci)) return;
            if (width == 4) { WriteRemote((ushort)value, 2); WriteRemote((ushort)(value >> 16), 2); }
            else WriteRemote((ushort)value, width);
            return;
        }
        if (width != 1) return;
        byte incoming = (byte)value;
        if (offset == 0) { WriteCommand(incoming, pci); return; }
        if (RegisterPage == 1)
        {
            if (offset <= 6) _physicalAddress[offset - 1] = incoming;
            else if (offset == 7) _currentPage = incoming;
            else _multicast[offset - 8] = incoming;
            return;
        }
        if (RegisterPage != 0) return;
        switch (offset)
        {
            case 1: _pageStart = incoming; break;
            case 2: _pageStop = incoming; break;
            case 3: _boundary = incoming; break;
            case 4: _transmitPage = incoming; break;
            case 5: _transmitCount = (ushort)((_transmitCount & 0xff00) | incoming); break;
            case 6: _transmitCount = (ushort)((_transmitCount & 0xff) | incoming << 8); break;
            case 7: _isr &= (byte)~(incoming & 0x7f); UpdateInterrupt(); break;
            case 8: _remoteAddress = (ushort)((_remoteAddress & 0xff00) | incoming); _remoteReadValidBytes = 0; break;
            case 9: _remoteAddress = (ushort)((_remoteAddress & 0xff) | incoming << 8); _remoteReadValidBytes = 0; break;
            case 10: _remoteCount = (ushort)((_remoteCount & 0xff00) | incoming); _remoteReadValidBytes = 0; break;
            case 11: _remoteCount = (ushort)((_remoteCount & 0xff) | incoming << 8); _remoteReadValidBytes = 0; break;
            case 12: _rxConfig = (byte)(incoming & 0x3f); _rxStatus = (byte)((_rxStatus & ~0x40) | ((incoming & 0x20) != 0 ? 0x40 : 0)); break;
            case 13: _txConfig = (byte)(incoming & 0x1f); break;
            case 14: _dataConfig = (byte)(incoming & 0x7f); break;
            case 15: _imr = (byte)(incoming & 0x7f); UpdateInterrupt(); break;
        }
    }
    /// <summary>Reads real current DMA/status lanes; tally reads clear only their own counter.</summary>
    private byte ReadPageZero(int offset, bool pci)
    {
        if (offset >= 0x0d) { int index = offset - 0x0d; byte result = _tally[index]; _tally[index] = 0; return result; }
        if (offset == 6 && (_txConfig & 6) == 2) { byte result = _loopbackFifo[_fifoCursor]; _fifoCursor = (byte)((_fifoCursor + 1) & 7); return result; }
        return offset switch { 1 => 0, 2 => _currentPage, 3 => _boundary, 4 => _txStatus, 5 => 0, 6 => 0,
            7 => _isr, 8 => (byte)_remoteAddress, 9 => (byte)(_remoteAddress >> 8),
            10 => pci ? (byte)0x50 : (byte)0, 11 => pci ? (byte)0x43 : (byte)0, 12 => _rxStatus, _ => 0 };
    }
    /// <summary>Returns a complete width mask, rejecting malformed transactions before state changes.</summary>
    private static uint WidthMask(int width) => width switch { 1 => 0xff, 2 => 0xffff, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Starts or stops actual DMA/frame operations; TXP zero writes never cancel an active transmission.</summary>
    private void WriteCommand(byte value, bool pci)
    {
        bool wasStopped = (_command & 1) != 0;
        byte priorStart = (byte)(_command & 2);
        _command = (byte)((value & ~4) | (_transmitEvent != 0 ? 4 : 0));
        if ((_command & 3) != 2) _receiveQueue = [];
        if ((value & 1) != 0)
        {
            // A software STOP admits no new work, but an already started wire
            // frame completes. STP does not clear a previously set STA latch.
            _command |= priorStart; _receiveQueue = []; FinishSoftwareStop();
        }
        else if ((value & 2) != 0)
        {
            _isr &= 0x7f;
            if (wasStopped) StartNextReceive();
        }
        int remote = (value >> 3) & 7;
        _remotePci = pci; _remoteAutoPacket = false; _remoteReadValidBytes = 0;
        if (_externalEnabled)
        {
            if (remote == 1) PrefetchRemoteRead();
            else if (remote == 2) { if (_remoteCount == 0) RemoteCompleted(); }
            else if (remote == 3 && (_dataConfig & 0x12) == 0x10 && ValidRing() && (_remoteCount >> 8) == 0x0f) BeginAutoPacket();
        }
        if ((value & 4) != 0 && (_command & 3) == 2 && _externalEnabled && _transmitEvent == 0) StartTransmit();
        UpdateInterrupt();
    }
    /// <summary>Autoinitializes the original send-packet remote removal from the boundary packet's real ring header.</summary>
    private void BeginAutoPacket()
    {
        int address = _boundary << 8;
        _autoPacketNext = _ram[address - RamStart + 1];
        ushort count = (ushort)(_ram[address - RamStart + 2] | _ram[address - RamStart + 3] << 8);
        if (_autoPacketNext < _pageStart || _autoPacketNext >= _pageStop || count < 4 || count > MaximumFrameLength + 4) return;
        _remoteAutoPacket = true; _remoteAddress = (ushort)(address + 4); _remoteCount = count;
        _remoteReadValidBytes = 0; PrefetchRemoteRead();
    }
    /// <summary>Prefetches one real remote byte/word so CRDA polling observes progress before the CPU data-port acknowledgment.</summary>
    private void PrefetchRemoteRead()
    {
        if (_remoteCount == 0) { RemoteCompleted(); return; }
        int count = Math.Min((_dataConfig & 1) != 0 ? 2 : 1, _remoteCount);
        ushort value = ReadMemory(_remoteAddress, _remotePci); AdvanceRemote(); _remoteCount--;
        if (count == 2) { value |= (ushort)(ReadMemory(_remoteAddress, _remotePci) << 8); AdvanceRemote(); _remoteCount--; }
        else value |= 0xff00;
        _remoteReadLatch = value; _remoteReadValidBytes = (byte)count;
        if (_remoteCount == 0) RemoteCompleted();
    }
    /// <summary>Returns the prefetched unit and acknowledges exactly one remote DMA host transfer.</summary>
    private uint ReadRemote(int width)
    {
        int mode = (_command >> 3) & 7;
        if ((mode != 1 && !(mode == 3 && _remoteAutoPacket)) || _remoteReadValidBytes == 0) return WidthMask(width);
        ushort value = _remoteReadLatch;
        if (width != 1 && (_dataConfig & 1) != 0 && _remoteReadValidBytes == 1 && _remoteCount != 0)
        {
            value = (ushort)((value & 0xff) | ReadMemory(_remoteAddress, _remotePci) << 8);
            AdvanceRemote(); _remoteCount--; _remoteReadValidBytes = 2;
            if (_remoteCount == 0) RemoteCompleted();
        }
        // On the ISA word board, even an IN AL acknowledges the complete
        // external word latch. Its doubled PROM wiring then yields one logical
        // ROM byte per host IN. PCI byte enables retain the unrequested lane.
        int consumed = width == 1 && (_remotePci || (_dataConfig & 1) == 0) ? 1 : _remoteReadValidBytes;
        if (width != 1 && (_dataConfig & 3) == 3) value = (ushort)(value >> 8 | value << 8);
        _remoteReadValidBytes -= (byte)consumed;
        if (_remoteReadValidBytes != 0) _remoteReadLatch = (ushort)((_remoteReadLatch >> 8) | 0xff00);
        else if (_remoteCount != 0) PrefetchRemoteRead();
        else if (_remoteAutoPacket) { _boundary = _autoPacketNext; _remoteAutoPacket = false; }
        return width == 1 ? (byte)value : value;
    }
    /// <summary>Stores one accepted remote transfer in packet RAM and completes only after the programmed count is consumed.</summary>
    private void WriteRemote(ushort value, int width)
    {
        if (((_command >> 3) & 7) != 2 || _remoteCount == 0) return;
        int count = Math.Min((_dataConfig & 1) != 0 && width != 1 ? 2 : 1, _remoteCount);
        if (count == 2 && (_dataConfig & 2) != 0) value = (ushort)(value >> 8 | value << 8);
        WriteMemory(_remoteAddress, (byte)value); AdvanceRemote(); _remoteCount--;
        if (count == 2) { WriteMemory(_remoteAddress, (byte)(value >> 8)); AdvanceRemote(); _remoteCount--; }
        if (_remoteCount == 0) RemoteCompleted();
    }
    /// <summary>Wraps remote DMA at a programmed valid receive-ring stop, otherwise retaining a 16-bit board address.</summary>
    private void AdvanceRemote()
    {
        _remoteAddress = unchecked((ushort)(_remoteAddress + 1));
        if (_pageStart < _pageStop && _remoteAddress == (_pageStop << 8)) _remoteAddress = (ushort)(_pageStart << 8);
    }
    /// <summary>Sets W1C RDC without clearing pending PRX/PTX or advancing virtual time.</summary>
    private void RemoteCompleted() { _isr |= 0x40; UpdateInterrupt(); }
    /// <summary>Reads actual ROM or packet RAM; unpopulated board memory returns the open bus value.</summary>
    private byte ReadMemory(ushort address, bool pci)
    {
        if (address < 32)
        {
            int index = pci ? address & 15 : address >> 1;
            return index < 6 ? _mac[index] : index >= 14 ? (byte)(pci && address >= 30 ? 0x42 : 0x57) : (byte)0;
        }
        return address >= RamStart && address < RamStart + RamLength ? _ram[address - RamStart] : (byte)0xff;
    }
    /// <summary>Accepts writes only in fitted packet RAM; PROM and holes remain unchanged.</summary>
    private void WriteMemory(ushort address, byte value)
    { if (address >= RamStart && address < RamStart + RamLength) _ram[address - RamStart] = value; }
    /// <summary>Captures the real contiguous programmed packet before scheduling one 10 Mbit/s wire completion.</summary>
    private void StartTransmit()
    {
        int address = _transmitPage << 8;
        int length = _transmitCount;
        // The National internal loopback uses only one eight-bit DMA lane in
        // word mode. For the Realtek DCR=43h diagnostic, retaining its normal
        // byte-addressed SRAM packet layout is an explicit model inference:
        // LS009.0 does not restate National's 2*n layout restriction.
        int stride = !_remotePci && (_txConfig & 6) == 2 && (_dataConfig & 1) != 0 ? 2 : 1;
        int frameLength = length / stride;
        _txStatus = 0;
        if (!_externalEnabled || length % stride != 0 || frameLength < 14 || frameLength > MaximumFrameLength + ((_txConfig & 1) != 0 ? 4 : 0) ||
            address < RamStart || address > RamStart + RamLength - length)
        { _txStatus = 8; _isr |= 8; _command &= 0xfb; UpdateInterrupt(); return; }
        if (stride == 1) _transmitFrame = _ram.AsSpan(address - RamStart, length).ToArray();
        else
        {
            _transmitFrame = new byte[frameLength]; int lane = (_dataConfig & 2) != 0 ? 0 : 1;
            for (int i = 0; i < frameLength; i++) _transmitFrame[i] = _ram[address - RamStart + i * 2 + lane];
        }
        _activeTransmitConfig = _txConfig;
        _transmitCarrierLost = !_cableConnected && (_txConfig & 6) == 0;
        ulong delay = checked(WireDuration(_transmitFrame.Length, (_txConfig & 1) == 0) + (_clock.Now < _transmitEligibleAt ? _transmitEligibleAt - _clock.Now : 0));
        _transmitDeadline = checked(_clock.Now + delay); _command |= 4;
        _transmitEvent = _clock.ScheduleAfter(delay, CompleteTransmit, this, "transmit");
    }
    /// <summary>Computes exact bit time for eight preamble/SFD bytes plus the supplied frame and optional generated FCS.</summary>
    public static ulong WireDuration(int frameBytes, bool generatedFcs = true)
    {
        if (frameBytes < 0 || frameBytes > MaximumFrameLength + 4) throw new ArgumentOutOfRangeException(nameof(frameBytes));
        return checked((ulong)(8 + frameBytes + (generatedFcs ? 4 : 0)) * 800);
    }
    /// <summary>Completes the sender at the wire deadline; receiver-side CRC diagnosis never becomes a transmitter failure.</summary>
    private void CompleteTransmit()
    {
        byte[] frame = _transmitFrame; byte config = _activeTransmitConfig; bool carrierLost = _transmitCarrierLost;
        _transmitEvent = 0; _transmitDeadline = 0; _transmitFrame = []; _command &= 0xfb;
        _transmitCarrierLost = false;
        _transmitEligibleAt = checked(_clock.Now + 9_600);
        int loop = (config >> 1) & 3;
        if (!_externalEnabled || loop >= 2 || (loop == 1 && (_dataConfig & 8) != 0))
        { _txStatus = 8; _isr |= 8; FinishSoftwareStop(); UpdateInterrupt(); return; }
        // DP8390D TSR.CRS does not abort a transmission. With no modeled collision or FIFO
        // underrun, the MAC still completes; a lost virtual carrier suppresses external delivery.
        bool lost = loop == 0 && (!_cableConnected || carrierLost);
        _txStatus = (byte)(lost ? 0x11 : 1); _isr |= 2;
        if (loop == 1)
        {
            _txStatus = 0x53; CompleteInternalLoopback(frame, (config & 1) == 0);
        }
        FinishSoftwareStop(); UpdateInterrupt();
        if (loop == 0 && !lost)
        {
            if ((config & 1) == 0) Transmitted?.Invoke(frame.ToArray());
            else if (frame.Length >= 18 && HasValidFcs(frame)) Transmitted?.Invoke(frame.AsSpan(0, frame.Length - 4).ToArray());
            // The external backend carries validated Ethernet frames without
            // FCS. A malformed wire FCS is discarded at that receive boundary,
            // while the actual transmitter still reports successful PTX.
        }
    }
    /// <summary>Checks the actual final four on-wire bytes without mistaking a CRC checker result for a transmit error.</summary>
    private static bool HasValidFcs(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 4) return false; int n = wire.Length - 4;
        uint supplied = (uint)(wire[n] | wire[n + 1] << 8 | wire[n + 2] << 16 | wire[n + 3] << 24);
        return supplied == EthernetCrc(wire[..n]);
    }
    /// <summary>Executes the documented local diagnostic FIFO/TSR/RSR path, which never DMA-writes SRAM or raises ISR.PRX.</summary>
    private void CompleteInternalLoopback(byte[] frame, bool generatedFcs)
    {
        byte[] wire;
        if (generatedFcs)
        {
            uint crc = EthernetCrc(frame); wire = new byte[frame.Length + 4]; frame.CopyTo(wire, 0);
            for (int i = 0; i < 4; i++) wire[frame.Length + i] = (byte)(crc >> (8 * i));
        }
        else wire = frame;
        // The physical eight-byte FIFO begins at location zero and wraps while
        // receiving. Its final count-low/count-high/count-high writes overwrite
        // three locations; the CPU starts reading at physical location zero.
        int cursor = 0;
        foreach (byte value in wire) { _loopbackFifo[cursor] = value; cursor = (cursor + 1) & 7; }
        _loopbackFifo[cursor] = (byte)wire.Length; cursor = (cursor + 1) & 7;
        _loopbackFifo[cursor] = (byte)(wire.Length >> 8); cursor = (cursor + 1) & 7;
        _loopbackFifo[cursor] = (byte)(wire.Length >> 8); _fifoCursor = 0;
        bool accepted = AcceptsDestination(frame.AsSpan(0, 6), out bool group);
        // National's explicit P31 software-CRC table specifies RSR.PRX for a
        // rejected address, even when its unexamined software CRC is bad.
        bool crcError = accepted && (generatedFcs || !HasValidFcs(wire));
        _rxStatus = (byte)((crcError ? 2 : 1) | (accepted && group ? 0x20 : 0));
        if (crcError)
        {
            IncrementTally(1);
            // National P30's generated-CRC diagnostic sets only ISR.PTX.
            // Realtek P32 explicitly reports RXE for its matching-address test;
            // both parts report a checked bad supplied FCS as a receiver error.
            if (!generatedFcs || _remotePci) _isr |= 4;
        }
    }
    /// <summary>Copies one bounded virtual ingress frame; stopped/disconnected devices cannot later replay old traffic.</summary>
    public bool QueueReceive(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < 14 || frame.Length > MaximumFrameLength) throw new ArgumentOutOfRangeException(nameof(frame));
        if (!_externalEnabled || !_cableConnected || (_command & 3) != 2 || (_txConfig & 6) != 0) return false;
        if (PendingReceiveCount >= ReceiveQueueLimit) { IncrementTally(2); return false; }
        _receiveQueue = [.. _receiveQueue, frame.ToArray()]; StartNextReceive(); return true;
    }
    /// <summary>Serializes incoming virtual frames with their true 10 Mbit/s duration and a bounded copied queue.</summary>
    private void StartNextReceive()
    {
        if (_receiveEvent != 0 || _receiveQueue.Length == 0 || (_command & 3) != 2 || !_externalEnabled) return;
        _receiveFrame = _receiveQueue[0]; _receiveQueue = _receiveQueue[1..];
        ulong delay = checked(WireDuration(_receiveFrame.Length) + (_clock.Now < _receiveEligibleAt ? _receiveEligibleAt - _clock.Now : 0));
        _receiveDeadline = checked(_clock.Now + delay);
        _receiveEvent = _clock.ScheduleAfter(delay, CompleteReceive, this, "receive");
    }
    /// <summary>Commits one completed ingress frame, then schedules only the next queued physical frame.</summary>
    private void CompleteReceive()
    {
        byte[] frame = _receiveFrame; _receiveFrame = []; _receiveEvent = 0; _receiveDeadline = 0;
        _receiveEligibleAt = checked(_clock.Now + 9_600);
        if (_cableConnected && _externalEnabled) ReceiveCompletedFrame(frame);
        FinishSoftwareStop(); StartNextReceive();
    }
    /// <summary>Applies actual MAC filtering and commits the complete ring header/data/FCS without crossing BNRY.</summary>
    private void ReceiveCompletedFrame(byte[] frame)
    {
        if (!_externalEnabled) return;
        if (frame.Length < 60 && (_rxConfig & 2) == 0) return;
        if (!AcceptsDestination(frame.AsSpan(0, 6), out bool group)) return;
        if ((_rxConfig & 0x20) != 0) { _rxStatus = (byte)(0x50 | (group ? 0x20 : 0)); IncrementTally(2); return; }
        if (!ValidRing()) { _rxStatus = 0x10; IncrementTally(2); _isr |= 4; UpdateInterrupt(); return; }
        int pages = (frame.Length + 8 + 255) / 256, next = _currentPage;
        for (int i = 0; i < pages; i++)
        {
            next++; if (next == _pageStop) next = _pageStart;
            if (next == _boundary) { _rxStatus = 0x10; _isr |= 0x90; IncrementTally(2); UpdateInterrupt(); return; }
        }
        byte status = (byte)(1 | (group ? 0x20 : 0)); int address = _currentPage << 8;
        PutRingByte(ref address, status); PutRingByte(ref address, (byte)next);
        int count = frame.Length + 4; PutRingByte(ref address, (byte)count); PutRingByte(ref address, (byte)(count >> 8));
        foreach (byte value in frame) PutRingByte(ref address, value);
        uint crc = EthernetCrc(frame); for (int i = 0; i < 4; i++) PutRingByte(ref address, (byte)(crc >> (i * 8)));
        _currentPage = (byte)next; _rxStatus = status; _isr |= 1; UpdateInterrupt();
    }
    /// <summary>Shares the actual physical/broadcast/multicast recognition rules between the normal and diagnostic receive paths.</summary>
    private bool AcceptsDestination(ReadOnlySpan<byte> address, out bool group)
    {
        group = (address[0] & 1) != 0; bool broadcast = address.IndexOfAnyExcept((byte)0xff) < 0;
        if (broadcast) return (_rxConfig & 4) != 0;
        if (group) { int hash = MulticastIndex(address); return (_rxConfig & 8) != 0 && (_multicast[hash >> 3] & (1 << (hash & 7))) != 0; }
        return (_rxConfig & 0x10) != 0 || address.SequenceEqual(_physicalAddress);
    }
    /// <summary>Publishes software-reset completion only after the last frame already on the wire has finished.</summary>
    private void FinishSoftwareStop()
    { if ((_command & 1) != 0 && _transmitEvent == 0 && _receiveEvent == 0) _isr |= 0x80; }
    /// <summary>Checks only currently programmed ring extents; arbitrary guest register programming remains representable.</summary>
    private bool ValidRing() => _pageStart >= 0x40 && _pageStart < _pageStop && _pageStop <= 0x80 &&
        _boundary >= _pageStart && _boundary < _pageStop && _currentPage >= _pageStart && _currentPage < _pageStop;
    /// <summary>Stores one proven ring byte with stop-to-start wrap.</summary>
    private void PutRingByte(ref int address, byte value)
    { _ram[address - RamStart] = value; address++; if (address == _pageStop << 8) address = _pageStart << 8; }
    /// <summary>Computes the on-wire reflected Ethernet CRC, including the final complement.</summary>
    private static uint EthernetCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        return ~crc;
    }
    /// <summary>Computes the National six-bit multicast hash from the destination address's serial least-significant-first bits.</summary>
    private static int MulticastIndex(ReadOnlySpan<byte> address)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in address)
        {
            int data = value;
            for (int bit = 0; bit < 8; bit++, data >>= 1)
            { bool carry = ((crc >> 31) ^ (uint)data) % 2 != 0; crc <<= 1; if (carry) crc ^= 0x04c11db7; }
        }
        return (int)(crc >> 26);
    }
    /// <summary>Increments the actual eight-bit tally and raises CNT on its documented high-bit transition.</summary>
    private void IncrementTally(int index)
    { if (_tally[index] < 0xc0) { _tally[index]++; if (_tally[index] == 0x80) _isr |= 0x20; } UpdateInterrupt(); }
    /// <summary>Publishes only genuine masked interrupt transitions, preserving pending unmasked causes.</summary>
    private void UpdateInterrupt()
    { bool value = (_isr & _imr & 0x7f) != 0; if (value != _interruptAsserted) { _interruptAsserted = value; _interrupt(value); } }
    /// <summary>Cancels the one real transmit callback and releases only its private pending frame.</summary>
    private void CancelTransmit()
    { if (_transmitEvent != 0) _clock.Cancel(_transmitEvent); _transmitEvent = 0; _transmitDeadline = 0; _transmitFrame = []; _transmitCarrierLost = false; _command &= 0xfb; }
    /// <summary>Cancels the one real receive callback and releases only its private active frame.</summary>
    private void CancelReceive()
    { if (_receiveEvent != 0) _clock.Cancel(_receiveEvent); _receiveEvent = 0; _receiveDeadline = 0; _receiveFrame = []; }

    /// <summary>Rejects impossible imported array, frame, token and IRQ combinations without changing a live component.</summary>
    public void ValidateSnapshot()
    {
        if (_mac.Length != 6 || _ram.Length != RamLength || _physicalAddress.Length != 6 || _multicast.Length != 8 || _tally.Length != 3 ||
            _loopbackFifo.Length != 8 || _fifoCursor > 7 || _tally.Any(value => value > 0xc0) ||
            (_imr & 0x80) != 0 || (_dataConfig & 0x80) != 0 || (_rxConfig & 0xc0) != 0 || (_txConfig & 0xe0) != 0 ||
            _remoteReadValidBytes > 2 || _transmitEvent < 0 || _receiveEvent < 0 || _receiveQueue.Length > ReceiveQueueLimit ||
            (_transmitFrame.Length != 0 && _transmitFrame.Length < 14) || _transmitFrame.Length > MaximumFrameLength + 4 ||
            (_receiveFrame.Length != 0 && _receiveFrame.Length < 14) || _receiveFrame.Length > MaximumFrameLength ||
            _interruptAsserted != ((_isr & _imr & 0x7f) != 0) ||
            ((_transmitEvent != 0) != (_transmitFrame.Length >= 14)) || ((_transmitEvent != 0) != ((_command & 4) != 0)) ||
            ((_receiveEvent != 0) != (_receiveFrame.Length >= 14)) || PendingReceiveCount > ReceiveQueueLimit ||
            (_receiveQueue.Length != 0 && _receiveEvent == 0) || (!_externalEnabled && (_transmitEvent != 0 || _receiveEvent != 0 || _receiveQueue.Length != 0)) ||
            (!_cableConnected && (_receiveEvent != 0 || _receiveQueue.Length != 0)) ||
            (_transmitCarrierLost && (_transmitEvent == 0 || (_activeTransmitConfig & 6) != 0)) ||
            (_transmitEligibleAt > _clock.Now && _transmitEligibleAt - _clock.Now > 9_600) ||
            (_receiveEligibleAt > _clock.Now && _receiveEligibleAt - _clock.Now > 9_600) ||
            (_transmitEvent == 0 ? _transmitDeadline != 0 : _transmitDeadline <= _clock.Now) ||
            (_receiveEvent == 0 ? _receiveDeadline != 0 : _receiveDeadline <= _clock.Now) ||
            _receiveQueue.Any(frame => frame is null || frame.Length < 14 || frame.Length > MaximumFrameLength))
            throw new InvalidDataException("Invalid NE2000 register, queue or scheduled-frame state.");
    }
}
