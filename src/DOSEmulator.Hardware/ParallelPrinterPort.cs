// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>IBM Printer Adapter 6361507 functional byte-port contract, including the schematic's direct ACK/IRQ7 gate.</summary>
public sealed partial class ParallelPrinterPort(DeviceScheduler scheduler, Action<bool> interrupt) : IIoPortDevice
{
    public const string ProfileId = "ibm-printer-adapter-development";
    public const ushort BasePort = 0x378;
    private byte _data, _control, _pins = 0xf8;
    /// <summary>Latched eight output bits; this adapter has no PS/2 direction control.</summary>
    public byte Data => _data;
    /// <summary>Five stored control bits; the upper undriven readback bits are separate.</summary>
    public byte Control => _control;
    /// <summary>Raw physical input levels in BUSY/ACK/PE/SELECT/ERROR bit positions 7..3.</summary>
    public byte InputPins => _pins;
    /// <summary>Time of the last real change of data outputs, independent from repeated writes of the same value.</summary>
    public ulong DataChangedAt { get; private set; }
    /// <summary>Actual gated IRQ7 level; register reads do not acknowledge it.</summary>
    public bool InterruptAsserted { get; private set; }
    /// <summary>Actual rising board IRQ7 edges, independent from PIC masks and acknowledgements.</summary>
    public long InterruptEdges { get; private set; }
    /// <summary>Completed byte reads from actual data/status/control registers.</summary>
    public long ReadCount { get; private set; }
    /// <summary>Completed data/control byte writes; reserved and wide accesses are excluded.</summary>
    public long WriteCount { get; private set; }
    /// <summary>Explicit optional virtual peer; no host spooler or printer is created by selecting the card.</summary>
    public VirtualParallelPrinter? Printer { get; private set; }
    /// <summary>Actual output changes for observers and the explicitly attached peer.</summary>
    public event Action<ParallelOutputs>? OutputsChanged;
    /// <summary>Observed actual external status-pin transitions, independent from register reads.</summary>
    public event Action<ParallelPins>? InputsChanged;
    /// <summary>Live status after BUSY inversion, select-input wired OR and stable high undriven low bits.</summary>
    public byte Status => (byte)((_pins ^ 0x80) | ((_control & 8) == 0 ? 16 : 0) | 7);
    /// <summary>Physical levels of STROBE, AUTOFEED, INIT and SELECT-IN on pins 1/14/16/17.</summary>
    public byte ControlPins => (byte)((_control ^ 0x0b) & 15);
    /// <summary>A2 and A10..A15 are absent from the original decode; all 128 aliases of each register remain one device.</summary>
    public static bool Decodes(ushort port) => (port & 0x3f8) == BasePort;
    /// <summary>Connect exactly one deterministic byte-sink peer, retaining current guest latches and outputs.</summary>
    public VirtualParallelPrinter ConnectPrinter()
    {
        if (Printer is not null) throw new InvalidOperationException("A virtual printer is already connected.");
        Printer = new(this, scheduler); return Printer;
    }
    /// <summary>Apply manual external pin levels only while no automatic peer owns those lines.</summary>
    public void SetInput(ParallelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Printer is not null) throw new InvalidOperationException("The connected virtual printer owns the input pins.");
        byte value = _pins;
        bool?[] lines = [input.ErrorHigh, input.SelectedHigh, input.PaperEndHigh, input.AckHigh, input.BusyHigh];
        for (int i = 0; i < lines.Length; i++) if (lines[i] is { } high)
            value = high ? (byte)(value | 1 << (i + 3)) : (byte)(value & ~(1 << (i + 3)));
        SetPrinterPins(value);
    }
    /// <summary>Commit one peer pin update and recompute only the actual ACK/enable gate.</summary>
    internal void SetPrinterPins(byte value)
    {
        value &= 0xf8; if (value == _pins) return;
        _pins = value; UpdateInterrupt(); InputsChanged?.Invoke(new(scheduler.Now, value));
    }
    /// <summary>The original LS125 path is noninverting: enabling with ACK already high can itself raise IRQ7.</summary>
    private void UpdateInterrupt()
    {
        bool high = (_control & 16) != 0 && (_pins & 64) != 0;
        if (high == InterruptAsserted) return;
        InterruptAsserted = high; if (high) InterruptEdges++; interrupt(high);
    }
    /// <summary>Return a stable undriven bus mask without decomposing unsupported wide transfers.</summary>
    private static uint Mask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Read actual latched outputs or live status; neither operation clears IRQ or changes virtual time.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width); if (width != 1 || !Decodes(port) || (port & 3) == 3) return mask;
        ReadCount++; return (port & 3) switch { 0 => _data, 1 => Status, _ => (uint)(0xe0 | _control) };
    }
    /// <summary>Write data or control latches and notify a peer only after real output changes.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Mask(width); if (width != 1 || !Decodes(port) || (port & 3) is 1 or 3) return;
        WriteCount++; byte oldData = _data, oldControl = _control;
        if ((port & 3) == 0) { _data = (byte)value; if (_data != oldData) DataChangedAt = scheduler.Now; }
        else _control = (byte)(value & 31);
        UpdateInterrupt();
        if (oldData != _data || oldControl != _control) OutputsChanged?.Invoke(new(scheduler.Now, _data, _control));
    }
}

/// <summary>Observed guest output levels at one actual scheduler instant.</summary>
public readonly record struct ParallelOutputs(ulong Time, byte Data, byte Control);

/// <summary>One externally driven printer status-pin update at an actual scheduler instant.</summary>
public readonly record struct ParallelPins(ulong Time, byte Pins);
