// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using DOSEmulator.Whpx;

/// <summary>Checks exact active wall-time accounting without WHP, civil time or a host timer.</summary>
internal static class WhpxRealtimeClockTests
{
    /// <summary>Requires an independent rational result or explicit ownership/overflow refusal.</summary>
    private static void Need(bool value, string reason)
    {
        if (!value) throw new Exception(reason);
    }

    /// <summary>Requires the requested operation to fail without hiding another exception kind.</summary>
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    /// <summary>Compares every observation with an unbounded-integer origin, including fractional ticks and exit work.</summary>
    private static void Frequency(long frequency)
    {
        long host = 517;
        var clock = new WhpxRealtimeClock(() => host, frequency);
        const ulong guestOrigin = 0x123456789;
        clock.Begin(guestOrigin);
        uint random = 0x27182818;
        for (int i = 0; i < 10000; i++)
        {
            random = unchecked(random * 1664525 + 1013904223);
            host += random % 137;
            ulong expected = guestOrigin + (ulong)((BigInteger)(host - 517) * 1_000_000_000 / frequency);
            Need(clock.Observe() == expected, "Active CPU, I/O, callback or scanout time was omitted or multiplied.");
            Need(clock.Remaining(expected + 171) == 171, "Deadline conversion changed the active time.");
            Need(clock.Remaining(expected) == 0, "Past/current deadline acquired extra time.");
        }
        clock.End();
        Reject<InvalidOperationException>(() => clock.Observe());
        host += frequency * 20;
        clock.Begin(42);
        Need(clock.Observe() == 42, "A paused or saved interval was included after resume.");
        host += frequency;
        Need(clock.Observe() == 1_000_000_042, "Resumed window is not exactly one guest second per host second.");
        clock.End();
    }

    /// <summary>Registers deterministic controls without requiring native hypervisor availability.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (long frequencyValue in new long[] { 1, 3, 7, 1_000_000, 10_000_000, 1_000_000_000 })
        {
            long frequency = frequencyValue;
            test("WHP realtime rational origin and pause at frequency " + frequency, () => Frequency(frequency));
        }
        test("WHP realtime refuses invalid clocks and duplicate ownership", () =>
        {
            Reject<ArgumentNullException>(() => new WhpxRealtimeClock(null!, 1));
            Reject<ArgumentOutOfRangeException>(() => new WhpxRealtimeClock(() => 0, 0));
            var clock = new WhpxRealtimeClock(() => 0, 1);
            Reject<InvalidOperationException>(() => clock.End());
            clock.Begin(0);
            Reject<InvalidOperationException>(() => clock.Begin(1));
            Need(clock.Observe() == 0, "Duplicate begin changed the original anchor.");
            clock.End();
        });
        test("WHP realtime rejects backwards host observations", () =>
        {
            long host = 100;
            var clock = new WhpxRealtimeClock(() => host, 1_000_000_000);
            clock.Begin(1000); host = 120;
            Need(clock.Observe() == 1020, "Initial observation differs.");
            host = 110; Reject<InvalidOperationException>(() => clock.Observe());
            host = 99; Reject<InvalidOperationException>(() => clock.Observe());
            host = 121; Need(clock.Observe() == 1021, "Failed observation changed the guest origin.");
            clock.End();
        });
        test("WHP realtime checked guest-time overflow has no partial advance", () =>
        {
            long host = 0;
            var clock = new WhpxRealtimeClock(() => host, 1_000_000_000);
            clock.Begin(ulong.MaxValue - 4); host = 5;
            Reject<OverflowException>(() => clock.Observe());
            host = 4; Need(clock.Observe() == ulong.MaxValue, "Overflow mutated the last accepted time.");
            clock.End();
        });
    }
}
