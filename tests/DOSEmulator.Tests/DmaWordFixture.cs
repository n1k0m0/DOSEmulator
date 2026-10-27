// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Explicit test-only word source/sink, not an emulated sound card. Never attached to the application.</summary>
internal sealed class DmaWordFixture(Dma8237 dma, DeviceScheduler clock) : IIoPortDevice
{
    public const ulong Period = 100_000;
    public sealed record Beat(int Batch, int Channel, bool Write, ulong Time, string Result, ushort Value, bool TerminalCount);
    public sealed record Batch(int Id, int Channel, bool Write, ulong Start, int Attempts, ushort Seed);
    /// <summary>Observed actual synthetic DMA word grants used by the independent guest fixture.</summary>
    public List<Beat> Beats { get; } = [];
    /// <summary>Recorded synthetic-source/sink batches with their explicit timing and transfer parameters.</summary>
    public List<Batch> Batches { get; } = [];
    private int _channel = 5, _attempts, _remaining, _transferred, _blocked, _invalid;
    private ushort _value;
    private bool _busy, _write;
    /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
    public uint Read(ushort port, int width) => (port, width) switch
    {
        (0x300, 1) => 0xd6,
        (0x302, 2) => _value,
        (0x308, 1) => _busy ? 0x80u : 0,
        (0x30a, 2) => (uint)_transferred,
        (0x30c, 2) => (uint)_blocked,
        (0x30e, 2) => (uint)_invalid,
        _ => throw new InvalidOperationException("Invalid test fixture read.")
    };
    /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (_busy) throw new InvalidOperationException("Reconfiguration of an active test fixture.");
        switch (port, width)
        {
            case (0x301, 1) when value is >= 5 and <= 7: _channel = (int)value; break;
            case (0x302, 2): _value = (ushort)value; break;
            case (0x304, 2) when value is > 0 and <= 65535: _attempts = (int)value; break;
            case (0x300, 1) when value is 1 or 2:
                if (_attempts == 0) throw new InvalidOperationException("No fixture request count.");
                _write = value == 2; _remaining = _attempts; _transferred = _blocked = _invalid = 0; _busy = true;
                Batches.Add(new(Batches.Count, _channel, _write, clock.Now, _attempts, _value));
                clock.ScheduleAfter(Period, Tick); break;
            default: throw new InvalidOperationException("Invalid test fixture write.");
        }
    }
    /// <summary>Performs one synthetic source/sink DMA request at the fixture's real scheduled device deadline.</summary>
    private void Tick()
    {
        ushort value = _value;
        DmaReadResult result = _write ? dma.TryWriteWord(_channel, value) : dma.TryReadWord(_channel, out value);
        Beats.Add(new(Batches.Count - 1, _channel, _write, clock.Now, result.ToString(), value, dma.IsTerminalCount(_channel)));
        if (result == DmaReadResult.Transferred) { _transferred++; _value = _write ? unchecked((ushort)(_value + 1)) : value; }
        else if (result == DmaReadResult.Blocked) _blocked++; else _invalid++;
        if (--_remaining == 0) _busy = false; else clock.ScheduleAfter(Period, Tick);
    }
}
