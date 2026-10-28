// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Explicit civil RTC calendar, exact virtual fractions, battery handoff and wire-format tests; no host-clock sample is taken.</summary>
internal static class RtcHostClockTests
{
    /// <summary>Requires actual calendar ticks, port bytes or stored state, never a host-clock approximation.</summary>
    private static void Check(bool condition, string why = "Explicit RTC calendar invariant failed.")
    { if (!condition) throw new InvalidOperationException(why); }

    /// <summary>Selects one real CMOS byte with the requested NMI flag and reads the ordinary data port.</summary>
    private static byte Read(RtcCmos rtc, byte register) { rtc.Write(0x70, register, 1); return (byte)rtc.Read(0x71, 1); }
    /// <summary>Writes one existing guest CMOS/register-B byte through the real selector/data ports.</summary>
    private static void Write(RtcCmos rtc, byte register, byte value) { rtc.Write(0x70, register, 1); rtc.Write(0x71, value, 1); }
    /// <summary>Captures explicit generated component state, retaining dictionary nodes rather than ignored readonly anonymous properties.</summary>
    private static byte[] State(RtcCmos rtc) => JsonSerializer.SerializeToUtf8Bytes(rtc.SaveState(), StateValues.Json);
    /// <summary>Requires the specific failure without accepting another exception or silent continuation.</summary>
    private static void Throws<T>(Action action) where T : Exception
    {
        bool seen = false; try { action(); } catch (T) { seen = true; }
        Check(seen, "Expected " + typeof(T).Name + " was not raised.");
    }
    /// <summary>Checks all Gregorian civil fields at the binary24-hour wire boundary; expected fields are literal independent fixtures.</summary>
    private static void Fields(RtcCmos rtc, int year, int month, int day, int weekday, int hour, int minute, int second)
    {
        Write(rtc, 0x0b, 6);
        Check(Read(rtc, 0) == second && Read(rtc, 2) == minute && Read(rtc, 4) == hour && Read(rtc, 6) == weekday &&
            Read(rtc, 7) == day && Read(rtc, 8) == month && Read(rtc, 9) == year % 100 && Read(rtc, 0x32) == year / 100,
            "Actual binary CMOS date/time fields differ from independent Gregorian fixture.");
    }

    /// <summary>Preserves the historical constructor epoch and BCD24-hour answers at the same virtual boundaries.</summary>
    private static void Default()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8, 0x40);
        Check(rtc.Calendar.Ticks == new DateTime(2026, 10, 24, 12, 0, 0).Ticks && rtc.Calendar.Kind == DateTimeKind.Unspecified);
        Check(Read(rtc, 0x0b) == 2 && Read(rtc, 9) == 0x26 && Read(rtc, 7) == 0x24 && Read(rtc, 4) == 0x12 && Read(rtc, 6) == 7);
        Check(Read(rtc, 0x10) == 0x40 && Read(rtc, 0x14) == 0x21 && Read(rtc, 0x15) == 0x7f && Read(rtc, 0x16) == 2);
        clock.AdvanceTo(1_999_999_999); Check(Read(rtc, 0) == 1);
        clock.AdvanceTo(2_000_000_000); Check(Read(rtc, 0) == 2);
        Check(rtc.SaveState().Fields["_epoch"].GetDateTime() == new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc) &&
            rtc.SaveState().Fields["_epoch"].GetDateTime().Kind == DateTimeKind.Utc);
    }

    /// <summary>UTC, Local and Unspecified inputs are the same supplied civil fields, not timezone-converted instants.</summary>
    private static void Kind(DateTimeKind kind)
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(9_123_456_789); var rtc = new RtcCmos(clock, 32);
        var input = new DateTime(2028, 2, 29, 23, 58, 59, kind).AddTicks(7_654_321);
        rtc.SetCalendar(input);
        Check(rtc.Calendar.Ticks == input.Ticks && rtc.Calendar.Kind == DateTimeKind.Unspecified);
        Check(rtc.SaveState().Fields["_epoch"].GetDateTime().Kind == DateTimeKind.Unspecified);
        clock.AdvanceTo(9_123_456_889); Check(rtc.Calendar.Ticks == input.Ticks + 1);
    }

    /// <summary>Setting at arbitrary virtual origins must not add already elapsed scheduler time to the supplied wall fields.</summary>
    private static void Origin(ulong at)
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(at); var rtc = new RtcCmos(clock, 32);
        var date = new DateTime(2004, 2, 29, 1, 2, 3).AddTicks(42);
        rtc.SetCalendar(date); Check(rtc.Calendar.Ticks == date.Ticks && clock.Now == at);
        var state = rtc.SaveState(); Check(state.Fields["_calendarOriginNanoseconds"].GetUInt64() == at && state.Fields["_batteryNanoseconds"].GetUInt64() == 0);
        if (at <= ulong.MaxValue - 100) { clock.AdvanceTo(at + 100); Check(rtc.Calendar.Ticks == date.Ticks + 1); }
    }

    /// <summary>Crosses one real calendar boundary from one tick before it, including leap/nonleap centuries and century-byte carry.</summary>
    private static void Boundary(DateTime before, int year, int month, int day, int weekday)
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(4_000_000_003); var rtc = new RtcCmos(clock, 8);
        rtc.SetCalendar(before.AddTicks(9_999_999));
        clock.AdvanceTo(4_000_000_102); Check(rtc.Calendar.Second == 59, "Sub100-ns fraction rounded prematurely.");
        clock.AdvanceTo(4_000_000_103); Fields(rtc, year, month, day, weekday, 0, 0, 0);
        Check(rtc.Calendar.Ticks == new DateTime(year, month, day).Ticks);
    }

    /// <summary>Independent literal hour encodings distinguish midnight/noon, PM, BCD and binary12/24-hour modes.</summary>
    private static void Mode(byte mode, int hour, byte hourWire)
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8);
        rtc.SetCalendar(new DateTime(2028, 2, 29, hour, 58, 59)); Write(rtc, 0x0b, mode);
        bool binary = (mode & 4) != 0;
        Check(Read(rtc, 4) == hourWire && Read(rtc, 0) == (binary ? 59 : 0x59) && Read(rtc, 2) == (binary ? 58 : 0x58));
        Check(Read(rtc, 6) == 3 && Read(rtc, 7) == (binary ? 29 : 0x29) && Read(rtc, 8) == 2 &&
            Read(rtc, 9) == (binary ? 28 : 0x28) && Read(rtc, 0x32) == (binary ? 20 : 0x20) && Read(rtc, 0x0b) == mode);
        Check(rtc.Calendar.Hour == hour, "Guest output format changed the calendar itself.");
    }

    /// <summary>Retains99ns and a fractional supplied DateTime through one exact second rollover.</summary>
    private static void Fraction()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8);
        var date = new DateTime(2030, 12, 31, 23, 59, 59).AddTicks(9_999_999);
        rtc.SetCalendar(date); clock.AdvanceTo(99); Check(rtc.Calendar.Ticks == date.Ticks);
        clock.AdvanceTo(100); Check(rtc.Calendar.Ticks == date.Ticks + 1 && Read(rtc, 9) == 0x31);
        clock.AdvanceTo(999_999_999); Check(rtc.Calendar.Ticks == date.Ticks + 9_999_999);
        clock.AdvanceTo(1_000_000_000); Check(rtc.Calendar.Ticks == date.Ticks + 10_000_000);
    }

    /// <summary>A later explicit civil change resets only its own fractional origin, with no leftover subsecond from the old epoch.</summary>
    private static void RepeatedSet()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8);
        rtc.SetCalendar(new DateTime(2020, 1, 1)); clock.AdvanceTo(999_999_999);
        var replacement = new DateTime(1999, 12, 31, 23, 59, 59).AddTicks(7);
        rtc.SetCalendar(replacement); Check(rtc.Calendar.Ticks == replacement.Ticks);
        clock.AdvanceTo(1_000_000_098); Check(rtc.Calendar.Ticks == replacement.Ticks);
        clock.AdvanceTo(1_000_000_099); Check(rtc.Calendar.Ticks == replacement.Ticks + 1);
    }

    /// <summary>Battery copies the actual epoch/phase/NVRAM/registerB while preserving the target's volatile selector and own past time.</summary>
    private static void Carry(ulong targetTime)
    {
        var a = new DeviceScheduler(); a.AdvanceTo(7_000_000_001); var source = new RtcCmos(a, 8);
        var date = new DateTime(2024, 2, 29, 12, 30, 45).AddTicks(1234); source.SetCalendar(date);
        Write(source, 0x38, 0xa5); Write(source, 0x7f, 0x5a); Write(source, 0x0b, 4); source.Write(0x70, 0xf2, 1);
        a.AdvanceTo(7_000_000_100);
        var b = new DeviceScheduler(); b.AdvanceTo(targetTime); var target = new RtcCmos(b, 32); target.Write(0x70, 0x99, 1);
        source.CarryBatteryStateTo(target);
        Check(target.Calendar.Ticks == date.Ticks && target.Read(0x70, 1) == 0x99 && source.Read(0x70, 1) == 0xf2);
        Check(Read(target, 0x38) == 0xa5 && Read(target, 0x7f) == 0x5a && Read(target, 0x0b) == 4);
        b.AdvanceTo(targetTime + 1); Check(target.Calendar.Ticks == date.Ticks + 1, "Carry lost the99-ns remainder.");
        Check(b.Now == targetTime + 1 && a.Now == 7_000_000_100);
    }

    /// <summary>Self-handoff must not double-count its elapsed scheduler time.</summary>
    private static void SelfCarry()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8); var date = new DateTime(2026, 1, 1);
        rtc.SetCalendar(date); clock.AdvanceTo(99); rtc.CarryBatteryStateTo(rtc);
        clock.AdvanceTo(100); Check(rtc.Calendar.Ticks == date.Ticks + 1);
    }

    /// <summary>A battery interval crossing UInt64.MaxValue is represented as a rebased calendar plus65ns, without overflow or lost phase.</summary>
    private static void WideCarry()
    {
        var first = new DeviceScheduler(); var rtc = new RtcCmos(first, 8); first.AdvanceTo(ulong.MaxValue - 50);
        var middleClock = new DeviceScheduler(); var middle = new RtcCmos(middleClock, 8); rtc.CarryBatteryStateTo(middle);
        middleClock.AdvanceTo(100);
        long expected = new DateTime(2026, 10, 24, 12, 0, 0).Ticks + 184_467_440_737_095_516L;
        Check(middle.Calendar.Ticks == expected);
        var lastClock = new DeviceScheduler(); var last = new RtcCmos(lastClock, 8); middle.CarryBatteryStateTo(last);
        Check(last.Calendar.Ticks == expected && last.SaveState().Fields["_batteryNanoseconds"].GetUInt64() == 65);
        lastClock.AdvanceTo(34); Check(last.Calendar.Ticks == expected);
        lastClock.AdvanceTo(35); Check(last.Calendar.Ticks == expected + 1);
    }

    /// <summary>Own generated state really restores a changed epoch/anchor and exact future civil ticks.</summary>
    private static void StateRoundTrip()
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(12_345_678_901); var rtc = new RtcCmos(clock, 8);
        rtc.SetCalendar(new DateTime(2099, 12, 31, 23, 59, 59).AddTicks(8_765_432)); Write(rtc, 0x38, 0x96); Write(rtc, 0x0b, 4);
        clock.AdvanceTo(12_580_246_792); var saved = rtc.SaveState(); byte[] before = State(rtc);
        var otherClock = new DeviceScheduler(); otherClock.AdvanceTo(clock.Now); var restored = new RtcCmos(otherClock, 8); restored.LoadState(saved);
        Check(before.AsSpan().SequenceEqual(State(restored)) && restored.Calendar == rtc.Calendar);
        foreach (ulong delta in new ulong[] { 1, 8, 99, 100, 234_567_891, 1_000_000_000 })
        { clock.AdvanceTo(clock.Now + delta); otherClock.AdvanceTo(otherClock.Now + delta); Check(rtc.Calendar == restored.Calendar && State(rtc).AsSpan().SequenceEqual(State(restored))); }
    }

    /// <summary>Bad saved origins remain errors instead of being silently rebased by a read.</summary>
    private static void InvalidOrigin()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8); var saved = rtc.SaveState();
        saved.Fields["_calendarOriginNanoseconds"] = JsonSerializer.SerializeToElement(1UL, StateValues.Json); rtc.LoadState(saved);
        Throws<InvalidOperationException>(() => { _ = rtc.Calendar; });
        Check(rtc.SaveState().Fields["_calendarOriginNanoseconds"].GetUInt64() == 1);
    }

    /// <summary>Impossible Gregorian dates are rejected by typed state decoding, not converted to another date.</summary>
    private static void InvalidDate(string value)
    {
        var rtc = new RtcCmos(new DeviceScheduler(), 8); var saved = rtc.SaveState();
        saved.Fields["_epoch"] = JsonSerializer.SerializeToElement(value, StateValues.Json);
        Throws<JsonException>(() => rtc.LoadState(saved));
    }

    /// <summary>End-calendar saturation is explicit and side-effect-free, even at the maximum scheduler count.</summary>
    private static void Saturation()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8); rtc.SetCalendar(DateTime.MaxValue.AddTicks(-1));
        clock.AdvanceTo(99); Check(rtc.Calendar.Ticks == DateTime.MaxValue.Ticks - 1);
        clock.AdvanceTo(100); Check(rtc.Calendar.Ticks == DateTime.MaxValue.Ticks);
        clock.AdvanceTo(ulong.MaxValue); byte[] state = State(rtc);
        Check(rtc.Calendar.Ticks == DateTime.MaxValue.Ticks && State(rtc).AsSpan().SequenceEqual(state));
        Fields(rtc, 9999, 12, 31, 6, 23, 59, 59);
    }

    /// <summary>No accumulated scheduler age is subtracted from a minimum calendar supplied at maximum virtual time.</summary>
    private static void Minimum()
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(ulong.MaxValue); var rtc = new RtcCmos(clock, 8);
        rtc.SetCalendar(DateTime.MinValue); Fields(rtc, 1, 1, 1, 2, 0, 0, 0); Check(rtc.Calendar.Ticks == 0);
    }

    /// <summary>Calendar setters preserve ordinary NVRAM, guest mode and selector/NMI state.</summary>
    private static void Preservation()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8, 0x44);
        Write(rtc, 0x38, 0x5a); Write(rtc, 0x7e, 0xa6); Write(rtc, 0x0b, 0x86); rtc.Write(0x70, 0xb8, 1);
        rtc.SetCalendar(new DateTime(2037, 3, 4, 5, 6, 7));
        Check(rtc.Read(0x70, 1) == 0xb8 && rtc.Read(0x71, 1) == 0x5a && Read(rtc, 0x7e) == 0xa6 && Read(rtc, 0x0b) == 0x86);
        Check(Read(rtc, 0x10) == 0x44 && Read(rtc, 0x14) == 0x21 && Read(rtc, 0x0a) == 0x26 && Read(rtc, 0x0c) == 0 && Read(rtc, 0x0d) == 0x80);
        clock.AdvanceTo(1_000_000_000); Check(Read(rtc, 0) == 8, "Existing subset unexpectedly acquired SET/freeze semantics.");
    }

    /// <summary>No calendar read mutates captured RTC fields or advances the scheduler.</summary>
    private static void PureReads()
    {
        var clock = new DeviceScheduler(); clock.AdvanceTo(123); var rtc = new RtcCmos(clock, 8); rtc.SetCalendar(new DateTime(2040, 1, 1));
        rtc.Write(0x70, 0x80, 1); byte[] before = State(rtc);
        for (int i = 0; i < 100; i++) { _ = rtc.Calendar; Check(rtc.Read(0x71, 1) == 0 && rtc.Read(0x70, 1) == 0x80); }
        Check(State(rtc).AsSpan().SequenceEqual(before) && clock.Now == 123);
    }

    /// <summary>Explicit setting leaves virtual-cycle conversion/cache/remainder and deadlines untouched.</summary>
    private static void SchedulerIdentity()
    {
        var clock = new DeviceScheduler(); clock.AdvanceCycles(1, 90_000_000); var rtc = new RtcCmos(clock, 8);
        var owners = new Dictionary<object, string>();
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(clock.SaveState(owners), StateValues.Json);
        rtc.SetCalendar(new DateTime(2050, 1, 1));
        Check(before.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(clock.SaveState(owners), StateValues.Json)));
    }

    /// <summary>Setters and carry reject source/target scheduler callbacks before changing calendar or CMOS.</summary>
    private static void Callback(bool carry, bool targetDispatching)
    {
        var a = new DeviceScheduler(); var b = new DeviceScheduler(); var source = new RtcCmos(a, 8); var target = new RtcCmos(b, 8);
        byte[] beforeSource = State(source), beforeTarget = State(target); bool rejected = false;
        var dispatcher = targetDispatching ? b : a;
        dispatcher.ScheduleAfter(1, () =>
        {
            try { if (carry) source.CarryBatteryStateTo(target); else source.SetCalendar(new DateTime(2030, 1, 1)); }
            catch (InvalidOperationException) { rejected = true; }
        });
        dispatcher.AdvanceTo(1);
        Check(rejected && beforeSource.AsSpan().SequenceEqual(State(source)) && beforeTarget.AsSpan().SequenceEqual(State(target)));
    }

    /// <summary>Actual x86 byte IN/OUT instructions consume the owner-set BCD century/year through the mapped CMOS peer.</summary>
    private static void CpuPorts()
    {
        var clock = new DeviceScheduler(); var rtc = new RtcCmos(clock, 8); rtc.SetCalendar(new DateTime(2087, 6, 5, 4, 3, 2));
        var ports = new IoPortBus(); ports.Map(0x70, 2, rtc);
        var (cpu, memory) = ProtectedModeTests.Rig([0xba,0x70,0,0,0,0xb0,0x89,0xee,0x42,0xec,0xf4], io: ports);
        cpu.SetRegister(Register.Ax, 0);
        for (int i = 0; i < 6; i++) cpu.Step();
        Check(cpu.GetRegister(Register.Ax) == 0x87 && rtc.Read(0x70, 1) == 0x89 && cpu.Eip == 0x200bu && !cpu.TripleFaulted);
        Check(Read(rtc, 0x32) == 0x20 && memory.Ram[0x2009] == 0xec && clock.Now == 0);
    }

    /// <summary>Registers explicit hardware/calendar tests only; root owns the main runner registration and all builds.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("RTC explicit calendar retains deterministic legacy default", Default);
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local }) test("RTC supplied wall fields ignore timezone kind " + kind, () => Kind(kind));
        foreach (ulong at in new ulong[] { 0, 123, 5_123_456_789, ulong.MaxValue }) test("RTC explicit set at virtual origin " + at, () => Origin(at));
        foreach (var row in new (DateTime Before, int Year, int Month, int Day, int Weekday)[]
        {
            (new(1999,12,31,23,59,59),2000,1,1,7), (new(2000,2,28,23,59,59),2000,2,29,3),
            (new(2024,2,28,23,59,59),2024,2,29,5), (new(2024,2,29,23,59,59),2024,3,1,6),
            (new(2100,2,28,23,59,59),2100,3,1,2), (new(2099,12,31,23,59,59),2100,1,1,6)
        }) test("RTC exact Gregorian midnight " + row.Before.ToString("yyyy-MM-dd"), () => Boundary(row.Before,row.Year,row.Month,row.Day,row.Weekday));
        int[] hours = [0,1,11,12,13,23];
        foreach (var row in new (byte Mode, byte[] Expected)[]
        {
            (2,[0x00,0x01,0x11,0x12,0x13,0x23]), (0,[0x12,0x01,0x11,0x92,0x81,0x91]),
            (6,[0x00,0x01,0x0b,0x0c,0x0d,0x17]), (4,[0x0c,0x01,0x0b,0x8c,0x81,0x8b])
        }) for (int j=0;j<hours.Length;j++) { int at=j;test($"RTC actual wire mode{row.Mode} hour{hours[at]}",()=>Mode(row.Mode,hours[at],row.Expected[at])); }
        test("RTC supplied fractional tick and99ns rollover", Fraction);
        test("RTC repeated host-boundary set discards old phase", RepeatedSet);
        foreach (ulong at in new ulong[] { 0, 9_876_543_210 }) test("RTC battery preserves epoch and fraction targetNow="+at,()=>Carry(at));
        test("RTC battery self-handoff avoids double clock counting", SelfCarry);
        test("RTC battery wider-than-UInt64 interval preserves65ns", WideCarry);
        test("RTC changed epoch and anchor really restore and continue", StateRoundTrip);
        test("RTC malformed future origin is not normalized by reads", InvalidOrigin);
        foreach (string date in new[] { "2026-02-30T12:00:00", "10000-01-01T00:00:00" }) test("RTC typed state rejects impossible date "+date,()=>InvalidDate(date));
        test("RTC maximum calendar saturates without read mutation", Saturation);
        test("RTC minimum calendar at maximum scheduler is valid", Minimum);
        test("RTC explicit set preserves CMOS mode index and NMI", Preservation);
        test("RTC calendar reads have no hidden state/clock effects", PureReads);
        test("RTC set leaves scheduler cycle fraction and caches intact", SchedulerIdentity);
        test("RTC set rejects a scheduler callback",()=>Callback(false,false));
        test("RTC battery rejects source scheduler callback",()=>Callback(true,false));
        test("RTC battery rejects target scheduler callback",()=>Callback(true,true));
        test("RTC owner-set calendar consumed by actual x86 port instructions", CpuPorts);
    }
}
