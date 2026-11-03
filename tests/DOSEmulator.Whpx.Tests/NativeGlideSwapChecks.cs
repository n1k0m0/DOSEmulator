// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Checks cooperative native presents, real PIC service while a provider is blocked, and joined host lifetime without a renderer or window.</summary>
internal static class NativeGlideSwapChecks
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _checks;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Swap(ulong interval);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Triple(ulong a, ulong b, ulong c);

    /// <summary>Counts independent assertions and rejects the first contract violation.</summary>
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }

    /// <summary>Requires the declared error rather than accepting silent token reuse or an unrelated failure.</summary>
    private static void Reject<T>(Action action) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected, "Expected " + typeof(T).Name);
    }

    /// <summary>Calls a private product entry while preserving the original exception type for negative controls.</summary>
    private static object? Invoke(object target, string method, params object[] arguments)
    {
        try { return target.GetType().GetMethod(method, Hidden)!.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    /// <summary>Binds owner operations on the actual product worker, with a finite injectable provider for error oracles.</summary>
    private sealed class Worker : IDisposable
    {
        internal readonly object Value;
        internal readonly Func<nint, uint, uint> Begin;
        internal readonly Func<uint, uint> Poll;
        internal readonly Action Drain, Reset;
        internal readonly Func<bool> Pending;
        /// <summary>Resolves the product worker without widening its production visibility.</summary>
        internal Worker(Func<nint, uint, ulong>? provider = null)
        {
            var type = typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlideSwapWorker", true)!;
            Value = Activator.CreateInstance(type, Hidden, null, provider is null ? [] : [provider], null)!;
            Begin = type.GetMethod("Begin", Hidden)!.CreateDelegate<Func<nint, uint, uint>>(Value);
            Poll = type.GetMethod("Poll", Hidden)!.CreateDelegate<Func<uint, uint>>(Value);
            Drain = type.GetMethod("Drain", Hidden)!.CreateDelegate<Action>(Value);
            Reset = type.GetMethod("Reset", Hidden)!.CreateDelegate<Action>(Value);
            Pending = type.GetProperty("HasPending", Hidden)!.GetGetMethod(true)!.CreateDelegate<Func<bool>>(Value);
        }
        /// <summary>Joins the actual native worker before its callback lifetime ends.</summary>
        public void Dispose() => ((IDisposable)Value).Dispose();
    }

    /// <summary>Proves single-request capacity, immutable arguments, ordered drain and nonreused completion tokens.</summary>
    private static void Ownership()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int calls = 0, callbackThread = 0; nint observedFunction = 0; uint observedInterval = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        using var worker = new Worker((function, interval) =>
        {
            calls++; observedFunction = function; observedInterval = interval; callbackThread = Environment.CurrentManagedThreadId;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Own finite provider was not released.");
            return 0xfedcba9876543210;
        });
        try
        {
            Check(worker.Value.GetType().GetField("_thread", Hidden)!.GetValue(worker.Value) is null, "Worker started before use");
            worker.Drain(); Check(!worker.Pending(), "Empty drain created work");
            Reject<InvalidDataException>(() => worker.Poll(0)); Reject<ArgumentException>(() => worker.Begin(0, 1));
            uint token = worker.Begin(123, 0x89abcdef);
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "Provider did not start");
            Check(token != 0 && worker.Pending() && worker.Poll(token) == 0, "Blocked provider completed early");
            Reject<InvalidOperationException>(() => worker.Begin(456, 0));
            Reject<InvalidDataException>(() => worker.Poll(token + 1)); Reject<InvalidDataException>(() => worker.Poll(0));
            using var drainStarted = new ManualResetEventSlim(); using var drained = new ManualResetEventSlim();
            var waiter = new Thread(() => { drainStarted.Set(); worker.Drain(); drained.Set(); }); waiter.Start();
            Check(drainStarted.Wait(TimeSpan.FromSeconds(2)) && !drained.Wait(20), "Later provider work passed an unfinished swap");
            release.Set(); Check(waiter.Join(TimeSpan.FromSeconds(2)) && drained.IsSet, "Drain did not finish");
            Check(worker.Pending() && worker.Poll(token) == 1 && !worker.Pending(), "Completion was consumed by drain or retained twice");
            Check(calls == 1 && observedFunction == 123 && observedInterval == 0x89abcdef && callbackThread != ownerThread,
                "Native argument, invocation count or execution owner differs");
            Reject<InvalidDataException>(() => worker.Poll(token));
            uint next = worker.Begin(456, uint.MaxValue); worker.Drain();
            Check(next > token && calls == 2 && observedFunction == 456 && observedInterval == uint.MaxValue, "Request identity or interval was reused");
            Reject<InvalidDataException>(() => worker.Poll(token)); worker.Reset();
            Reject<InvalidDataException>(() => worker.Poll(next)); Check(!worker.Pending(), "Reset retained a completion");
            uint afterReset = worker.Begin(789, 0); worker.Drain();
            Check(afterReset > next && worker.Poll(afterReset) == 1 && calls == 3, "Reset reused a token or lost the next swap");
            worker.Value.GetType().GetField("_issued", Hidden)!.SetValue(worker.Value, uint.MaxValue);
            Reject<OverflowException>(() => worker.Begin(1, 0)); Check(!worker.Pending() && calls == 3, "Token overflow submitted native work");
        }
        finally { release.Set(); }
    }

    /// <summary>Checks error publication, recovery, reset barriers and join-before-unload on a deliberately blocked finite call.</summary>
    private static void Lifetime()
    {
        bool fail = true;
        using (var worker = new Worker((_, _) => fail ? throw new IOException("Own finite provider failure") : 0UL))
        {
            uint token = worker.Begin(1, 0); Reject<IOException>(worker.Drain);
            Reject<IOException>(() => worker.Poll(token)); Check(!worker.Pending(), "Failed completion was replayable");
            fail = false; token = worker.Begin(1, 1); worker.Drain(); Check(worker.Poll(token) == 1, "Worker did not recover after reported error");
        }
        foreach (bool dispose in new[] { false, true })
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim();
            using var worker = new Worker((_, _) =>
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Own finite provider was not released."); return 0; });
            uint token = worker.Begin(1, 1); Check(entered.Wait(TimeSpan.FromSeconds(2)), "Lifetime provider did not enter");
            try
            {
                var waiter = new Thread(() => { started.Set(); if (dispose) worker.Dispose(); else worker.Reset(); finished.Set(); }); waiter.Start();
                Check(started.Wait(TimeSpan.FromSeconds(2)) && !finished.Wait(20), "Reset/dispose abandoned accepted native work");
                release.Set(); Check(waiter.Join(TimeSpan.FromSeconds(2)) && finished.IsSet, "Reset/dispose did not finish after native return");
                if (dispose)
                {
                    var thread = (Thread)worker.Value.GetType().GetField("_thread", Hidden)!.GetValue(worker.Value)!;
                    Check(!thread.IsAlive, "Disposed native worker still runs");
                    Reject<ObjectDisposedException>(() => worker.Begin(1, 0)); Reject<ObjectDisposedException>(() => worker.Poll(token));
                    Reject<ObjectDisposedException>(worker.Drain); Reject<ObjectDisposedException>(worker.Reset); worker.Dispose();
                }
                else { Check(!worker.Pending(), "Reset retained pending work"); Reject<InvalidDataException>(() => worker.Poll(token)); }
            }
            finally { release.Set(); }
        }
        ulong observed = 0; int nativeThread = 0, owner = Environment.CurrentManagedThreadId;
        Swap native = value => { observed = value; nativeThread = Environment.CurrentManagedThreadId; return ulong.MaxValue; };
        using (var worker = new Worker())
        {
            uint token = worker.Begin(Marshal.GetFunctionPointerForDelegate(native), uint.MaxValue); worker.Drain();
            Check(worker.Poll(token) == 1 && observed == uint.MaxValue && nativeThread != owner, "Actual unmanaged swap argument or thread differs");
        }
        GC.KeepAlive(native);
    }

    /// <summary>Checks real native translations, capability negotiation, input ownership and active-renderer snapshot rejection.</summary>
    private static void Transport()
    {
        var memory = new MemoryBus(4 << 20);
        using var native = new WhpxMappedMachine(memory, new(), new(), new()); native.SetEntry(0, 0x1000);
        var type = typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlidePassThrough", true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(type, Hidden, null,
            [typeof(WhpxMappedMachine).GetField("_partition", Hidden)!.GetValue(native)!, memory, Path.Combine(Environment.SystemDirectory, "kernel32.dll")], null)!;
        var functions = (nint[])type.GetField("_functions", Hidden)!.GetValue(bridge)!;
        // Invokes the real transport with this fixture's bounded argument block.
        uint Call(uint operation, uint arguments) => (uint)Invoke(bridge, "Invoke", operation, arguments, 0u)!;
        // Writes one declared little-endian input word in the fixture's guest RAM.
        void Word(uint at, uint word) => BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan((int)at, 4), word);
        memory.Ram.AsSpan(0x3000, 96).Fill(0xcc); Word(0x2000, 0x3000);
        Check(Call(45, 0x2000) == 0x4d475331, "Cooperative transport version is not advertised");
        byte[] expected = new byte[80]; "2.43 MiniGlide 1.0"u8.CopyTo(expected);
        Check(memory.Ram.AsSpan(0x3000, 80).SequenceEqual(expected) && memory.Ram.AsSpan(0x3050, 16).ToArray().All(x => x == 0xcc), "Version capability changed its public buffer or guard");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int swaps = 0; ulong interval = 0; bool timedOut = false, returned = false, clearedAfterReturn = false;
        Swap swap = value => { swaps++; interval = value; entered.Set(); timedOut = !release.Wait(TimeSpan.FromSeconds(3)); returned = true; return 0; };
        Triple clear = (_, _, _) => { clearedAfterReturn = returned; return 0; };
        functions[16] = Marshal.GetFunctionPointerForDelegate(swap); functions[14] = Marshal.GetFunctionPointerForDelegate(clear);
        try
        {
            Word(0x2000, 0xfedcba98); uint token = Call(0xfffd, 0x2000); Word(0x2000, 0);
            Check(entered.Wait(TimeSpan.FromSeconds(2)) && interval == 0xfedcba98 && swaps == 1, "Native present retained a guest pointer or changed its interval");
            Check(Call(0xfffc, token) == 0, "Native pending call falsely returned done");
            Reject<InvalidOperationException>(() => Call(0xfffd, 0x2000)); Reject<InvalidDataException>(() => Call(0xfffc, token + 1));
            Reject<InvalidOperationException>(() => Invoke(bridge, "RequireSnapshotSupport"));
            release.Set(); Call(14, 0x2000);
            Check(clearedAfterReturn && Call(0xfffc, token) == 1 && !timedOut, "Later rendering preceded the completed present");
            Reject<InvalidDataException>(() => Call(0xfffc, token));
            Invoke(bridge, "RequireSnapshotSupport");
            type.GetField("_window", Hidden)!.SetValue(bridge, (nint)1);
            try { Reject<InvalidOperationException>(() => Invoke(bridge, "RequireSnapshotSupport")); }
            finally { type.GetField("_window", Hidden)!.SetValue(bridge, (nint)0); }
            Word(0x2000, 1); uint old = Call(0xfffd, 0x2000); Invoke(bridge, "ResetGeneration");
            Reject<InvalidDataException>(() => Call(0xfffc, old));
            Reject<InvalidDataException>(() => Call(0xfffd, (uint)memory.Ram.Length - 2));
            Check(swaps == 2, "Invalid pointer or stale token called the native provider");
        }
        finally { release.Set(); GC.KeepAlive(swap); GC.KeepAlive(clear); }
    }

    /// <summary>Executes real native guest OUT/POLL instructions and an IRQ0/EOI/IRET handler while the actual native swap is blocked.</summary>
    private static void InterruptProgress()
    {
        var memory = new MemoryBus(4 << 20); var ports = new IoPortBus(); var pic = new Pic8259(); var scheduler = new DeviceScheduler();
        var pit = new Pit8254(scheduler, () => { pic.SetLine(0, true); pic.SetLine(0, false); });
        ports.Map(0x20, 2, pic); ports.Map(0x40, 4, pit);
        int acknowledged = 0, eois = 0;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        pic.InterruptAcknowledged += (irq, _, _) => { if (irq == 0 && ++acknowledged == 3) release.Set(); };
        ports.Accessed += (port, value, _, write) => { if (write && port == 0x20 && value == 0x20) eois++; };
        string? previous = Environment.GetEnvironmentVariable("DOSE_GLIDE_HOST_DLL"); WhpxMappedMachine native;
        try
        {
            Environment.SetEnvironmentVariable("DOSE_GLIDE_HOST_DLL", Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
            native = new WhpxMappedMachine(memory, ports, pic, scheduler, WhpxCpuContract.IntelP54cB5Subset, 100_000_000);
        }
        finally { Environment.SetEnvironmentVariable("DOSE_GLIDE_HOST_DLL", previous); }
        using (native)
        {
            object bridge = typeof(WhpxMappedMachine).GetField("_glidePassThrough", Hidden)!.GetValue(native)!;
            var functions = (nint[])bridge.GetType().GetField("_functions", Hidden)!.GetValue(bridge)!;
            int swaps = 0; bool timeout = false;
            Swap swap = value => { swaps++; entered.Set(); timeout = !release.Wait(TimeSpan.FromSeconds(3)); return 0; };
            functions[16] = Marshal.GetFunctionPointerForDelegate(swap);
            // Own real-mode program: PIT mode 2, 1193 clocks; start one swap; STI; poll until complete; CLI/HLT.
            var code = new List<byte> { 0xfa, 0xbc, 0, 0x80, 0xb0, 0xfe, 0xe6, 0x21, 0xb0, 0x34, 0xe6, 0x43,
                0xb8, 0xa9, 4, 0xe6, 0x40, 0x88, 0xe0, 0xe6, 0x40,
                0x66, 0xb8, 0xfd, 0xff, 0, 0, 0x66, 0xb9, 0, 0x20, 0, 0, 0xba, 0x10, 0xf1, 0x66, 0xef,
                0x66, 0x89, 0xc3, 0xfb };
            int loop = code.Count; code.AddRange([0x66, 0xb8, 0xfc, 0xff, 0, 0, 0x66, 0x89, 0xd9, 0x66, 0xef, 0x66, 0x85, 0xc0, 0x74]);
            code.Add(unchecked((byte)(loop - code.Count - 1))); code.AddRange([0xfa, 0xf4]); code.ToArray().CopyTo(memory.Ram, 0x1000);
            byte[] handler = [0xff, 6, 0, 0x30, 0x50, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf]; handler.CopyTo(memory.Ram, 0x1100);
            BinaryPrimitives.WriteUInt16LittleEndian(memory.Ram.AsSpan(0x20, 2), 0x1100);
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x2000, 4), 1);
            native.SetEntry(0, 0x1000);
            try
            {
                native.RunUntilHalt(TimeSpan.FromSeconds(5), 100000);
                Check(entered.IsSet && !timeout && swaps == 1, "Actual native present did not complete through its IRQ-controlled release");
                Check(acknowledged >= 3 && pit.InterruptPulses >= 3 && eois >= 3 && BitConverter.ToUInt16(memory.Ram, 0x3000) >= 3,
                    "Blocked native present prevented actual guest PIT/IRQ0/EOI/IRET progress");
                Invoke(bridge, "RequireSnapshotSupport");
            }
            finally { release.Set(); GC.KeepAlive(swap); }
        }
    }

    /// <summary>Runs all finite host-lifetime and actual native owner controls without opening external services.</summary>
    public static void Run()
    {
        _checks = 0; Ownership(); Lifetime(); Transport(); InterruptProgress();
        Console.WriteLine($"PASS: cooperative Glide swaps {_checks} ownership/lifetime/native ABI/IRQ controls; exact intervals, one-shot completion, stale/reset rejection, joined shutdown, unchanged version bytes and real guest IRQ0/EOI/IRET during blocked present; no renderer/window/endpoint.");
    }
}
