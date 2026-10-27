// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Channel 0 timer (binary modes 2/3) and separate speaker counter 2. Channel 1 remains unimplemented.</summary>
public sealed partial class Pit8254 : IIoPortDevice
{
    private readonly DeviceScheduler scheduler;
    private readonly Action irqPulse;
    /// <summary>PIT channel-two counter used by the reference speaker gate/integration path.</summary>
    public PitSpeakerCounter SpeakerCounter { get; }
    /// <summary>Initializes Pit8254 and connects the supplied dependencies before the component is used.</summary>
    public Pit8254(DeviceScheduler scheduler, Action irqPulse)
    { this.scheduler = scheduler; this.irqPulse = irqPulse; SpeakerCounter = new(scheduler); }
    private const ulong Frequency = 1_193_182;
    private uint _reload = 65536;
    private byte _access = 3, _low;
    private bool _writeHigh, _readHigh;
    private int _mode = 3;
    private ushort? _latch;
    private long _event;
    private ulong _start, _remainder;
    private bool _running;
    /// <summary>Diagnostic count of interrupt pulses.</summary>
    public long InterruptPulses { get; private set; }
    private bool _unsupportedProgramming;
    private byte? _statusLatch;
    /// <summary>Diagnostic indication that the guest selected programming outside the implemented reference contract.</summary>
    public bool UnsupportedProgramming => _unsupportedProgramming || SpeakerCounter.UnsupportedProgramming;
    /// <summary>Current reference output signal of Pit8254, in that component's native representation.</summary>
    private bool Output => !_running || (_mode == 2
        ? ((scheduler.Now - _start) * Frequency / 1_000_000_000) % _reload != _reload - 1
        : ((scheduler.Now - _start) * Frequency / 1_000_000_000) % _reload < (_reload + 1) / 2);

    /// <summary>Derives the visible PIT count from the reference channel's loaded count, mode and current tick.</summary>
    private ushort Current()
    {
        if (!_running) return (ushort)_reload;
        ulong clocks = (scheduler.Now - _start) * Frequency / 1_000_000_000;
        // Mode 2 count is exact; mode 3 count readback remains a documented development approximation.
        return (ushort)(_reload - clocks % _reload);
    }
    /// <summary>Reads the addressed PIT channel under its programmed access and latch sequence.</summary>
    public uint Read(ushort port, int width)
    {
        if (port == 0x42 && width == 1) return SpeakerCounter.Read();
        if (port != 0x40 || width != 1) return 0xff;
        if (_statusLatch is { } status) { _statusLatch = null; return status; }
        ushort count = _latch ?? Current();
        if (_access == 1) { _latch = null; return (byte)count; }
        if (_access == 2) { _latch = null; return (byte)(count >> 8); }
        byte value = _readHigh ? (byte)(count >> 8) : (byte)count;
        _readHigh = !_readHigh; if (!_readHigh) _latch = null;
        return value;
    }
    /// <summary>Processes PIT control/count bytes and reschedules the affected reference counter output.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == 0x43)
        {
            int channel = (int)(value >> 6) & 3;
            if (channel == 2) { SpeakerCounter.Control((byte)value); return; }
            if (channel == 3)
            {
                bool count = (value & 32) == 0, status = (value & 16) == 0;
                if ((value & 8) != 0) SpeakerCounter.Latch(count, status);
                if ((value & 2) != 0)
                {
                    if (count && _latch is null) { _latch = Current(); _readHigh = false; }
                    if (status) _statusLatch ??= (byte)((Output ? 128 : 0) | (_running ? 0 : 64) | _access << 4 | _mode << 1);
                }
                if ((value & 4) != 0) _unsupportedProgramming = true;
                return;
            }
            if (channel != 0) { _unsupportedProgramming = true; return; }
            byte access = (byte)((value >> 4) & 3);
            if (access == 0) { if (_latch is null) { _latch = Current(); _readHigh = false; } return; }
            _access = access; _writeHigh = _readHigh = false;
            _mode = (int)(value >> 1) & 7; if (_mode >= 6) _mode -= 4;
            _unsupportedProgramming = _mode is not (2 or 3) || (value & 1) != 0;
            if (_event != 0) scheduler.Cancel(_event);
            _event = 0; _running = false; _latch = null; _statusLatch = null; return;
        }
        if (port == 0x42) { SpeakerCounter.Write((byte)value); return; }
        if (port != 0x40) { _unsupportedProgramming = true; return; }
        ushort raw;
        if (_access == 3)
        {
            if (!_writeHigh) { _low = (byte)value; _writeHigh = true; return; }
            raw = (ushort)(_low | ((byte)value << 8)); _writeHigh = false;
        }
        else raw = _access == 1 ? (byte)value : (ushort)((byte)value << 8);
        _reload = raw == 0 ? 65536u : raw;
        if (_event != 0) scheduler.Cancel(_event);
        _remainder = 0; _start = scheduler.Now; _running = !_unsupportedProgramming;
        if (_running) Schedule();
    }
    /// <summary>Schedules the next reference PIT output/interrupt transition from its programmed count and mode.</summary>
    private void Schedule()
    {
        ulong numerator = _reload * 1_000_000_000UL + _remainder;
        ulong delay = numerator / Frequency; _remainder = numerator % Frequency;
        _event = scheduler.ScheduleAfter(delay, () => { InterruptPulses++; irqPulse(); if (_running) Schedule(); }, this, "event0");
    }
}
