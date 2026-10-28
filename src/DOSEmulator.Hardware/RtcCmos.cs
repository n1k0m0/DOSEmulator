// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Deterministic MC146818-compatible calendar/NVRAM subset. Periodic/alarm IRQs follow later.</summary>
public sealed partial class RtcCmos(DeviceScheduler scheduler, int ramMiB, byte floppyTypes = 0) : IIoPortDevice
{
    private readonly byte[] _nvram = new byte[128];
    private DateTime _epoch = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private byte _index;
    private byte _statusB = 2;
    private ulong _batteryNanoseconds;
    // Explicit virtual origin for a calendar supplied at a nonzero owner boundary; legacy construction starts at zero.
    private ulong _calendarOriginNanoseconds;

    /// <summary>Current civil calendar including100-ns precision; no timezone conversion or host-clock access occurs.</summary>
    public DateTime Calendar => CalendarAt(ElapsedNanoseconds);

    /// <summary>Rejects a setter or battery handoff from a scheduler callback; the PC owner also controls outer execution boundaries.</summary>
    private void RequireOwnerBoundary()
    {
        if (scheduler.IsDispatching) throw new InvalidOperationException("RTC calendar changes require an owner boundary outside scheduler callbacks.");
    }

    /// <summary>Combines carried battery time and elapsed virtual time without UInt64 overflow or unsigned subtraction wrap.</summary>
    private UInt128 ElapsedNanoseconds
    {
        get
        {
            if (scheduler.Now < _calendarOriginNanoseconds) throw new InvalidOperationException("RTC calendar origin is later than its scheduler.");
            return (UInt128)_batteryNanoseconds + (scheduler.Now - _calendarOriginNanoseconds);
        }
    }

    /// <summary>Converts integer nanoseconds to representable civil ticks, explicitly saturating only the upper DateTime calendar limit.</summary>
    private DateTime CalendarAt(UInt128 elapsed)
    {
        UInt128 ticks = elapsed / 100;
        long available = DateTime.MaxValue.Ticks - _epoch.Ticks;
        DateTime value = ticks > (UInt128)available ? DateTime.MaxValue : _epoch.AddTicks((long)ticks);
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    /// <summary>Installs a civil basis at the actual target scheduler boundary without touching NVRAM, selector or register B.</summary>
    private void AdoptCalendar(DateTime basis, ulong batteryNanoseconds)
    {
        RequireOwnerBoundary(); _epoch = basis; _batteryNanoseconds = batteryNanoseconds;
        _calendarOriginNanoseconds = scheduler.Now;
    }

    /// <summary>Explicit owner-supplied wall fields; Kind never converts them to UTC or another timezone. Hardware reads remain deterministic.</summary>
    public void SetCalendar(DateTime calendar) => AdoptCalendar(DateTime.SpecifyKind(calendar, DateTimeKind.Unspecified), 0);

    /// <summary>Copies CMOS/battery-backed RTC state into a replacement, preserving fractional time and avoiding target-clock double counting.</summary>
    public void CarryBatteryStateTo(RtcCmos poweredOn)
    {
        ArgumentNullException.ThrowIfNull(poweredOn); RequireOwnerBoundary(); poweredOn.RequireOwnerBoundary();
        UInt128 elapsed = ElapsedNanoseconds;
        DateTime basis = _epoch; ulong battery;
        if (elapsed <= ulong.MaxValue) battery = (ulong)elapsed;
        else
        {
            // Only a real battery handoff rebases an accumulated interval wider than the legacy UInt64 field.
            basis = CalendarAt(elapsed); battery = basis == DateTime.MaxValue ? 0 : (ulong)(elapsed % 100);
        }
        poweredOn.AdoptCalendar(basis, battery);
        _nvram.CopyTo(poweredOn._nvram, 0); poweredOn._statusB = _statusB;
    }

    /// <summary>Reads reference CMOS/time registers without sampling a host clock or replacing guest RTC I/O.</summary>
    public uint Read(ushort port, int width)
    {
        if (port == 0x70) return _index;
        if (port != 0x71 || width != 1) return 0xff;
        DateTime now = Calendar;
        // Encodes an RTC field as binary or packed BCD according to the guest register-B policy.
        byte Encode(int value) => (_statusB & 4) != 0 ? (byte)value : (byte)((value / 10 << 4) | value % 10);
        // Twelve-hour mode encodes1..12 and keeps the PM marker separate from BCD/binary digits.
        byte Hour() => (_statusB & 2) != 0 ? Encode(now.Hour) :
            (byte)(Encode((now.Hour + 11) % 12 + 1) | (now.Hour >= 12 ? 0x80 : 0));
        return (_index & 127) switch
        {
            0 => Encode(now.Second), 2 => Encode(now.Minute), 4 => Hour(),
            6 => Encode((int)now.DayOfWeek + 1), 7 => Encode(now.Day), 8 => Encode(now.Month),
            9 => Encode(now.Year % 100), 0x32 => Encode(now.Year / 100),
            0x0a => 0x26, 0x0b => _statusB, 0x0c => 0, 0x0d => 0x80,
            0x10 => floppyTypes, 0x14 => floppyTypes == 0 ? 0x20u : 0x21u, 0x15 => 0x7f, 0x16 => 2,
            0x17 or 0x30 => (byte)((ramMiB - 1) * 1024),
            0x18 or 0x31 => (byte)(((ramMiB - 1) * 1024) >> 8),
            _ => _nvram[_index & 127]
        };
    }

    /// <summary>Updates the existing writable CMOS/register-B subset; guest calendar writes remain outside this limited device contract.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == 0x70) _index = (byte)value;
        else if (port == 0x71)
        {
            if ((_index & 127) == 0x0b) _statusB = (byte)value;
            else _nvram[_index & 127] = (byte)value;
        }
    }
}
