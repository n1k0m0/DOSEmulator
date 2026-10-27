// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>CT1740 MPU-401 UART protocol subset with nominal full-duplex MIDI framing and no host endpoint or synthesizer.</summary>
public sealed partial class Mpu401Uart : IIoPortDevice
{
    public const string ProfileId = "ct1740-mpu401-uart-development";
    public const ulong FrameNanoseconds = 320_000, ResetNanoseconds = 500_000, ModeNanoseconds = 10_000, IgnoredCommandNanoseconds = 2_000;
    public const int ReceiveCapacity = 16, MaximumExternalFrames = 4096;
    private readonly DeviceScheduler _clock;
    private readonly Queue<(byte Data, bool Interrupt)> _received = new();
    private readonly Queue<byte> _external = new();
    private bool _busy, _transmitting, _receiving, _receiveUart;
    private byte? _holding;
    private byte _transmitByte, _receiveByte;
    private ulong _transmitStart, _receiveStart;
    private long _commandEvent, _transmitEvent, _receiveEvent, _generation, _receiveGeneration;
    /// <summary>Jumper-selected pair, 300h/301h or 330h/331h.</summary>
    public ushort BasePort { get; }
    /// <summary>Whether the acknowledged Enter UART command has completed.</summary>
    public bool UartMode { get; private set; }
    /// <summary>Actual pending input source routed through CT1745 bit 2 and the card's shared IRQ.</summary>
    public bool InterruptAsserted { get; private set; }
    /// <summary>Rising input-source transitions, distinct from PIC acknowledge and EOI counts.</summary>
    public long InterruptEdges { get; private set; }
    /// <summary>Ready host holding register, independent from the serial shifter still on the wire.</summary>
    public bool OutputReady => !_busy && _holding is null;
    /// <summary>Input-ready/output-ready flags, with undefined low status bits held high in this model.</summary>
    public byte Status => (byte)(0x3f | (_received.Count == 0 ? 0x80 : 0) | (OutputReady ? 0 : 0x40));
    /// <summary>Unread host-side bytes including command acknowledgements.</summary>
    public int ReceiveCount => _received.Count;
    /// <summary>Queued outer serial frames, including a frame already in flight.</summary>
    public int ExternalFramesPending => _external.Count + (_receiving ? 1 : 0);
    /// <summary>Whether a full MIDI transmit frame is currently in flight.</summary>
    public bool Transmitting => _transmitting;
    /// <summary>Whether a full outer MIDI receive frame is currently in flight.</summary>
    public bool Receiving => _receiving;
    /// <summary>Completed output frames, never bytes merely accepted by a host register.</summary>
    public long TransmittedBytes { get; private set; }
    /// <summary>Completed outer input frames, including those ignored or overrun.</summary>
    public long ReceivedFrames { get; private set; }
    /// <summary>Outer frames delivered into the current UART receive buffer.</summary>
    public long ReceivedBytes { get; private set; }
    /// <summary>Frames lost because mode/reset changed, separately from buffer overrun.</summary>
    public long IgnoredInputFrames { get; private set; }
    /// <summary>New input bytes dropped while the bounded host receive buffer is full.</summary>
    public long Overruns { get; private set; }
    /// <summary>Accepted reset commands; DSP and CPU resets are separate operations.</summary>
    public long Resets { get; private set; }
    /// <summary>Command acknowledgements made available to the host.</summary>
    public long Acknowledgements { get; private set; }
    /// <summary>Host writes refused because busy or data was sent outside UART mode.</summary>
    public long RejectedWrites { get; private set; }
    /// <summary>Unsupported/non-reset commands ignored without invented intelligent-mode services.</summary>
    public long IgnoredCommands { get; private set; }
    /// <summary>Unfinished output bytes discarded by an accepted MPU reset.</summary>
    public long AbortedOutputBytes { get; private set; }
    /// <summary>Source-level changes for the attached sound card; this is not a PIC acknowledgement.</summary>
    public event Action<bool>? InterruptChanged;
    /// <summary>Only fully transmitted nominal 8N1 MIDI frames appear here.</summary>
    public event Action<MidiFrame>? Transmitted;
    /// <summary>Every completed outer receive frame with its actual delivery/overrun decision.</summary>
    public event Action<MidiReception>? Received;
    /// <summary>Observed explicitly queued outer data, useful for independent timing oracles.</summary>
    public event Action<ulong, byte[]>? InputQueued;
    /// <summary>Construct a disconnected nominal transport; no operating-system MIDI API is used.</summary>
    public Mpu401Uart(DeviceScheduler clock, ushort basePort = 0x330)
    {
        if (basePort is not (0x300 or 0x330)) throw new ArgumentException("CT1740 MIDI base must be 300h or 330h.");
        _clock = clock; BasePort = basePort;
    }
    /// <summary>Return undriven wide values without decomposing a word into destructive byte reads.</summary>
    private static uint Mask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Read live status or remove one actual response/data byte, retaining any other pending input source.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width); if (width != 1 || port < BasePort || port > BasePort + 1) return mask;
        if (port == BasePort + 1) return Status;
        if (!_received.TryDequeue(out var entry)) return 255;
        UpdateInterrupt(); return entry.Data;
    }
    /// <summary>Accept only a ready byte/command; data FFh is an ordinary MIDI byte, not a port-command reset.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Mask(width); if (width != 1 || port < BasePort || port > BasePort + 1) return;
        if (!OutputReady) { RejectedWrites++; return; }
        byte data = (byte)value;
        if (port == BasePort + 1) { Command(data); return; }
        if (!UartMode) { RejectedWrites++; return; }
        _holding = data; StartTransmit();
    }
    /// <summary>Implement Creative's reset/enter-UART commands with explicit nominal, non-silicon service delays.</summary>
    private void Command(byte data)
    {
        bool reset = data == 0xff, enter = data == 0x3f && !UartMode;
        _busy = true;
        if (reset)
        {
            Resets++; _generation++; UartMode = false; _received.Clear(); UpdateInterrupt();
            if (_transmitting) { _clock.Cancel(_transmitEvent); _transmitEvent = 0; _transmitting = false; AbortedOutputBytes++; }
            if (_holding is not null) { _holding = null; AbortedOutputBytes++; }
        }
        else if (!enter) IgnoredCommands++;
        _commandEvent = _clock.ScheduleAfter(reset ? ResetNanoseconds : enter ? ModeNanoseconds : IgnoredCommandNanoseconds, () =>
        {
            _commandEvent = 0; _busy = false;
            if (reset || enter)
            {
                if (enter) UartMode = true;
                _received.Enqueue((0xfe, enter)); Acknowledgements++; UpdateInterrupt();
            }
        }, this, "event0", () => StateValues.Pack(reset, enter));
    }
    /// <summary>Move one host holding byte into the independent 31.25-kbaud, ten-bit serial shifter.</summary>
    private void StartTransmit()
    {
        if (_transmitting || _holding is not { } data) return;
        _holding = null; _transmitByte = data; _transmitStart = _clock.Now; _transmitting = true;
        _transmitEvent = _clock.ScheduleAfter(FrameNanoseconds, () =>
        {
            _transmitEvent = 0; _transmitting = false;
            var frame = new MidiFrame(_transmitStart, _clock.Now, _transmitByte); TransmittedBytes++;
            StartTransmit(); // Publish only after any next event exists, so a reset from an observer can cancel it.
            Transmitted?.Invoke(frame);
        }, this, "event1");
    }
    /// <summary>Validate and enqueue a whole explicit outer packet; an MPU reset cannot rewind its physical wire timeline.</summary>
    public void QueueReceive(MidiInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        MidiInput owned = input.ValidatedCopy();
        if (owned.Data.Length > MaximumExternalFrames - ExternalFramesPending) throw new ArgumentException("Too many pending outer MIDI frames.");
        byte[] data = owned.Data.Select(v => (byte)v).ToArray();
        foreach (byte value in data) _external.Enqueue(value);
        StartReceive(); InputQueued?.Invoke(_clock.Now, data);
    }
    /// <summary>Clock one complete outer frame; mode/reset is sampled at both boundaries before host delivery.</summary>
    private void StartReceive()
    {
        if (_receiving || !_external.TryDequeue(out _receiveByte)) return;
        _receiving = true; _receiveStart = _clock.Now; _receiveGeneration = _generation; _receiveUart = UartMode;
        _receiveEvent = _clock.ScheduleAfter(FrameNanoseconds, () =>
        {
            _receiveEvent = 0; _receiving = false; ReceivedFrames++;
            var frame = new MidiFrame(_receiveStart, _clock.Now, _receiveByte);
            bool eligible = _receiveUart && UartMode && _receiveGeneration == _generation;
            bool accepted = eligible && _received.Count < ReceiveCapacity;
            if (accepted) { _received.Enqueue((_receiveByte, true)); ReceivedBytes++; UpdateInterrupt(); }
            else if (eligible) Overruns++; else IgnoredInputFrames++;
            StartReceive(); Received?.Invoke(new(frame, accepted, eligible && !accepted));
        }, this, "event2");
    }
    /// <summary>Keep input status, the card source and PIC edge capture as distinct state machines.</summary>
    private void UpdateInterrupt()
    {
        bool pending = _received.Any(v => v.Interrupt);
        if (pending == InterruptAsserted) return;
        InterruptAsserted = pending; if (pending) InterruptEdges++; InterruptChanged?.Invoke(pending);
    }
}

/// <summary>Observed complete serial byte at nominal start/stop-bit boundaries.</summary>
public readonly record struct MidiFrame(ulong Started, ulong Completed, byte Data);
/// <summary>One complete outer serial byte and its host-buffer delivery outcome.</summary>
public readonly record struct MidiReception(MidiFrame Frame, bool Accepted, bool Overrun);
