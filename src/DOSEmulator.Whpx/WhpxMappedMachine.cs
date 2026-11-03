// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Whpx;

/// <summary>
/// Physical-bus WHP owner. RAM, ROM, MMIO, port I/O and a cascaded PIC use DOSE
/// devices; host-clock slices drive the supplied scheduler. GUI/headless native
/// owners use separately typed snapshots; the software CPU remains available.
/// </summary>
public sealed class WhpxMappedMachine : IDisposable
{
    // The complete guest-visible integer, segment, control, debug, x87/MMX and XMM
    // register file admitted by this lane. APIC and XSAVE extensions remain unadmitted.
    private static readonly uint[] SavedRegisters =
    [
        .. Enumerable.Range(0, 0x21).Select(i => (uint)i),
        .. Enumerable.Range(0x21, 7).Select(i => (uint)i),
        .. Enumerable.Range(0x1000, 0x1a).Select(i => (uint)i),
        // The native host TSC keeps running while stopped and is never the P54C guest clock.
        // RDTSC/RDMSR/WRMSR use the saved virtual scheduler plus TscEpochTicks instead.
        0x2001, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007,
        WhpxNative.PendingInterruption, WhpxNative.InterruptState, WhpxNative.PendingEvent,
        WhpxNative.DeliverabilityNotifications
    ];
    private static readonly uint[] GlideInputRegisters = [WhpxNative.Rax, WhpxNative.Rcx, 6];
    private static readonly uint[] GlideOutputRegisters = [WhpxNative.Rax, WhpxNative.Rip];
    private static readonly uint[] CatchupRegisters = [WhpxNative.Rflags, WhpxNative.InterruptState, WhpxNative.PendingInterruption, WhpxNative.PendingEvent];
    /// <summary>Services the owning board reset edges before another native opcode; true inhibits fetch while RESET is held.</summary>
    internal Func<bool>? ServiceHardwareBoundary { get; set; }
    private readonly IoPortBus _ports;
    private readonly MemoryBus _bus;
    private readonly Pic8259 _pic;
    private readonly DeviceScheduler _scheduler;
    private readonly WhpxCpuContract _cpuContract;
    private readonly uint _nominalCpuHz;
    private readonly byte[] _exit = new byte[4096];
    private readonly WhpxNative.RegisterValue[] _glideInputValues = new WhpxNative.RegisterValue[3];
    private readonly WhpxNative.RegisterValue[] _glideOutputValues = new WhpxNative.RegisterValue[2];
    private readonly WhpxNative.RegisterValue[] _catchupValues = new WhpxNative.RegisterValue[4];
    private readonly WhpxPhysicalMemory _memory;
    private readonly WhpxInstructionEmulator _emulator;
    private readonly WhpxVideoBatch _videoBatch;
    private readonly WhpxGlideBatch _glideBatch;
    private readonly WhpxGlidePassThrough? _glidePassThrough;
    private readonly bool _enableVideoBatch = Environment.GetEnvironmentVariable("DOSE_WHPX_VIDEO_BATCH") != "0";
    private readonly WhpxRealtimeClock _clock = new();
    private readonly WhpxDeadlineTimer _deadlineTimer;
    private readonly bool _traceExceptions = Environment.GetEnvironmentVariable("DOSE_WHPX_TRACE_EXCEPTIONS") == "1";
    private readonly uint _timeScale;
    private nint _partition;
    private ulong _tscEpochTicks;
    private long _processorGeneration;
    private bool _vp, _running, _disposed, _halted, _interruptWindowArmed, _realtimeWindow, _busWritesOnly;
    private bool _runtimeCountersAvailable = true;
    private ulong? _nativeRuntimeSample;

    /// <summary>Builds one native CPU with explicit instruction exits and the owning DOSE physical device graph.</summary>
    public WhpxMappedMachine(MemoryBus memory, IoPortBus ports, Pic8259 pic, DeviceScheduler scheduler,
        WhpxCpuContract cpuContract = WhpxCpuContract.Host, uint nominalCpuHz = 0,
        uint virtualTimeScale = 1)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(pic);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (cpuContract is WhpxCpuContract.IntelP54cB5Subset or WhpxCpuContract.IntelP55cMmxSubset ? nominalCpuHz == 0 : nominalCpuHz != 0)
            throw new ArgumentException("Only the P54C contract accepts a positive nominal CPU frequency.", nameof(nominalCpuHz));
        if (!Enum.IsDefined(cpuContract)) throw new ArgumentOutOfRangeException(nameof(cpuContract));
        if (virtualTimeScale != 1) throw new ArgumentOutOfRangeException(nameof(virtualTimeScale), "WHP real-time execution requires an exact 1:1 clock; synthetic time multipliers are unsupported.");
        if (!WhpxRealModeMachine.IsAvailable()) throw new PlatformNotSupportedException("Windows Hypervisor Platform is unavailable.");
        _ports = ports; _bus = memory; _pic = pic; _scheduler = scheduler; _cpuContract = cpuContract;
        _nominalCpuHz = nominalCpuHz; _timeScale = virtualTimeScale;
        if (nominalCpuHz != 0) _tscEpochTicks = scheduler.ClockTicks(nominalCpuHz);
        WhpxPhysicalMemory? mappedMemory = null;
        WhpxInstructionEmulator? instructionEmulator = null;
        WhpxDeadlineTimer? deadlineTimer = null;
        try
        {
            WhpxNative.Check(WhpxNative.WHvCreatePartition(out _partition), "create mapped partition");
            uint one = 1;
            WhpxNative.Check(WhpxNative.WHvSetPartitionProperty(_partition, 0x1fff, in one, 4), "set processor count");
            if (cpuContract != WhpxCpuContract.Host)
            {
                ulong exits = 0xfUL; // CPUID, MSR, RDTSC(P), exception compatibility
                WhpxNative.Check(WhpxNative.WHvGetCapability(2, out ulong available, 8, out uint written), "query extended exits");
                if (written != 8 || (available & exits) != exits)
                    throw new PlatformNotSupportedException("WHP does not support the required P54C/486 CPU instruction exits.");
                WhpxNative.Check(WhpxNative.WHvSetPartitionProperty(_partition, 1, in exits, 8), "set CPU instruction exits");
                ulong bitmap = 1UL << 13;
                if (_traceExceptions) bitmap |= (1UL << 6) | (1UL << 8) | (1UL << 11) |
                    (1UL << 12) | (1UL << 14);
                WhpxNative.Check(WhpxNative.WHvSetPartitionProperty(_partition, 2, in bitmap, 8), "set exception exits");
                ulong msrExits = 0x7; // unhandled MSRs and TSC reads/writes
                WhpxNative.Check(WhpxNative.WHvGetCapability(4, out ulong availableMsrs, 8, out written), "query MSR exits");
                if (written != 8 || (availableMsrs & msrExits) != msrExits)
                    throw new PlatformNotSupportedException("WHP does not support the required MSR exits.");
                WhpxNative.Check(WhpxNative.WHvSetPartitionProperty(_partition, 5, in msrExits, 8), "set MSR exit bitmap");
            }
            WhpxNative.Check(WhpxNative.WHvSetupPartition(_partition), "setup mapped partition");
            mappedMemory = new WhpxPhysicalMemory(_partition, memory);
            WhpxNative.Check(WhpxNative.WHvCreateVirtualProcessor(_partition, 0, 0), "create mapped VP");
            _vp = true;
            instructionEmulator = new WhpxInstructionEmulator(_partition, memory, ports);
            _memory = mappedMemory;
            _emulator = instructionEmulator;
            _videoBatch = new WhpxVideoBatch(_partition, memory);
            _glideBatch = new WhpxGlideBatch(_partition,memory,_clock.Observe)
            { ReadBoundaryMemoryVersion=()=>_memory.TranslationMemoryVersion };
            string? glideHost = Environment.GetEnvironmentVariable("DOSE_GLIDE_HOST_DLL");
            if (!string.IsNullOrWhiteSpace(glideHost))
                _glidePassThrough = new WhpxGlidePassThrough(_partition, memory, glideHost);
            deadlineTimer = new WhpxDeadlineTimer(_partition);
            _deadlineTimer = deadlineTimer;
        }
        catch
        {
            _glidePassThrough?.Dispose();
            deadlineTimer?.Dispose();
            instructionEmulator?.Dispose();
            if (_vp) WhpxNative.WHvDeleteVirtualProcessor(_partition, 0);
            mappedMemory?.Dispose();
            if (_partition != 0) WhpxNative.WHvDeletePartition(_partition);
            throw;
        }
    }

    /// <summary>Starts a self-owned real-mode program at a stopped owner boundary.</summary>
    public void SetEntry(ushort cs, ushort ip)
    {
        EnsureStopped();
        uint[] names = [WhpxNative.Cs, WhpxNative.Rip, WhpxNative.Rflags,
            WhpxNative.Ss, WhpxNative.Ds, WhpxNative.Es, WhpxNative.Rsp];
        WhpxNative.RegisterValue[] values =
            [new((ulong)cs << 4, 0xffffUL | ((ulong)cs << 32) | (0x9bUL << 48)), new(ip), new(2),
                new(0, 0xffffUL | (0x93UL << 48)), new(0, 0xffffUL | (0x93UL << 48)),
                new(0, 0xffffUL | (0x93UL << 48)), new(0x8000)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, (uint)names.Length, values), "set mapped entry");
        if (_cpuContract == WhpxCpuContract.IntelP55cMmxSubset && cs == 0xf000 && ip == 0xfff0)
            SetRegister(WhpxNative.Rdx, new(0x543));
        _halted = false;
        _nativeRuntimeSample = null;
        if (_nominalCpuHz != 0) _tscEpochTicks = _scheduler.ClockTicks(_nominalCpuHz);
    }

    /// <summary>Reads a selected authoritative native register at a stopped CPU boundary.</summary>
    public ulong GetRegister(uint name)
    {
        EnsureStopped();
        return ReadRegister(name).Low;
    }

    /// <summary>Captures WHP CPU state only at a stopped boundary; owner RAM and devices are captured separately.</summary>
    public WhpxCpuState CaptureCpuState()
    {
        EnsureStopped();
        var values = new WhpxNative.RegisterValue[SavedRegisters.Length];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, SavedRegisters,
            (uint)SavedRegisters.Length, values), "capture mapped CPU registers");
        return new WhpxCpuState(SavedRegisters.Zip(values, (name, value) => new WhpxRegisterState(name, value.Low, value.High)).ToArray(),
            _cpuContract, _nominalCpuHz, _timeScale, _tscEpochTicks, _halted, "host-monotonic-1:1-v1");
    }

    /// <summary>Restores an exact register set into a fresh stopped VP after the owner restored RAM and devices.</summary>
    public void RestoreCpuState(WhpxCpuState state)
    {
        EnsureStopped();
        ValidateCpuState(state);
        if (state.Contract != _cpuContract || state.NominalCpuHz != _nominalCpuHz || state.VirtualTimeScale != _timeScale)
            throw new InvalidDataException("WHP CPU state register topology differs.");
        var values = state.Registers.Select(item => new WhpxNative.RegisterValue(item.Low, item.High)).ToArray();
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, SavedRegisters,
            (uint)SavedRegisters.Length, values), "restore mapped CPU registers");
        _halted = state.Halted;
        _nativeRuntimeSample = null;
        _tscEpochTicks = state.TscEpochTicks;
        _interruptWindowArmed = (state.Registers.Single(item => item.Name == WhpxNative.DeliverabilityNotifications).Low & 2) != 0;
        _processorGeneration++;
    }

    /// <summary>Rejects malformed clock policies and complete register topologies before opening a snapshot media branch.</summary>
    internal static void ValidateCpuState(WhpxCpuState state)
    {
        if (state is null || state.ClockPolicy != "host-monotonic-1:1-v1" || state.VirtualTimeScale != 1 ||
            !Enum.IsDefined(state.Contract) ||
            (state.Contract is WhpxCpuContract.IntelP54cB5Subset or WhpxCpuContract.IntelP55cMmxSubset ? state.NominalCpuHz == 0 : state.NominalCpuHz != 0) ||
            state.Registers is null || state.Registers.Length != SavedRegisters.Length ||
            state.Registers.Where((item, index) => item.Name != SavedRegisters[index]).Any())
            throw new InvalidDataException("Invalid WHP CPU topology or real-time policy.");
    }

    /// <summary>Runs in bounded host-clock slices. Device events and PIC pins are sampled at each WHP exit.</summary>
    public WhpxMappedRunResult RunUntilHalt(TimeSpan timeout, int maximumExits = 10000) => Run(timeout, maximumExits, null);

    /// <summary>Keeps one host origin across owner slices, audio, scanouts and input work in a continuous active session.</summary>
    internal void BeginRealtimeWindow(bool busWritesOnly = false)
    {
        EnsureStopped();
        if (_realtimeWindow) throw new InvalidOperationException("WHP real-time execution is already active.");
        if (!_clock.Active) _clock.Begin(_scheduler.Now); _realtimeWindow = true; _busWritesOnly = busWritesOnly;
        try { _memory.ActivateSharedRam(); _memory.Refresh(); }
        catch { _memory.DeactivateSharedRam(); _realtimeWindow = false; _clock.End(); throw; }
    }

    /// <summary>Freezes elapsed time at an explicit owner pause; loading later establishes a fresh host origin.</summary>
    internal void EndRealtimeWindow(bool retain = false)
    {
        EnsureStopped();
        if (!_realtimeWindow) throw new InvalidOperationException("WHP has no active real-time window.");
        _realtimeWindow = false;
        if (!retain) { _memory.DeactivateSharedRam(); _clock.End(); }
    }

    /// <summary>Excludes explicit UI pause/checkpoint time and publishes the complete managed RAM mirror.</summary>
    internal void PauseRealtime()
    {
        EnsureStopped();
        if (_realtimeWindow) throw new InvalidOperationException("Pause requires a completed native slice.");
        if (_clock.Active) { SynchronizeClock(null); _clock.End(); }
        _memory.DeactivateSharedRam();
    }
    /// <summary>Reports the real selector and instruction pointer at a stopped native exit.</summary>
    internal string Position => $"{(ReadRegister(WhpxNative.Cs).High >> 32) & 0xffff:X4}:{ReadRegister(WhpxNative.Rip).Low:X8}";
    /// <summary>Resets the native CPU at a completed exit while preserving mapped RAM and powered devices.</summary>
    internal void ResetProcessor(bool init = false)
    {
        _glidePassThrough?.ResetGeneration();
        _nativeRuntimeSample = null;
        _processorGeneration++;
        uint[] retainedNames = init ? SavedRegisters.Where(n => n >= 0x1000 && n <= 0x1019).ToArray() : [];
        var retained = new WhpxNative.RegisterValue[retainedNames.Length];
        ulong cr0 = init ? (ReadRegister(0x1c).Low & 0x60000000) | 0x10 : 0x60000010;
        if (init) WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, retainedNames, (uint)retainedNames.Length, retained), "retain INIT x87");
        WhpxNative.Check(WhpxNative.WHvDeleteVirtualProcessor(_partition, 0), "reset native VP"); _vp = false;
        WhpxNative.Check(WhpxNative.WHvCreateVirtualProcessor(_partition, 0, 0), "recreate native VP"); _vp = true;
        uint[] names = [WhpxNative.Cs, WhpxNative.Rip, WhpxNative.Rflags, WhpxNative.Rdx, 0x1c];
        WhpxNative.RegisterValue[] values = [new(0xf0000, 0xffffUL | (0xf000UL << 32) | (0x9bUL << 48)), new(0xfff0), new(2), new(_cpuContract == WhpxCpuContract.IntelP55cMmxSubset ? 0x543UL : 0x524UL), new(cr0)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, (uint)names.Length, values), "native reset vector");
        if (init) WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, retainedNames, (uint)retainedNames.Length, retained), "restore INIT x87");
        _halted = false; _interruptWindowArmed = false;
        if (!init && _nominalCpuHz != 0) _tscEpochTicks = _scheduler.ClockTicks(_nominalCpuHz);
    }

    /// <summary>Replays host waiting chronologically, charging actual native execution and indivisible instruction assistance before offering overdue IRQs.</summary>
    private void SynchronizeClock(ulong? deadline, ulong executedNanoseconds = 0, bool instructionBlocked = false)
    {
        ulong observed = _clock.Observe();
        if (deadline is { } end) observed = Math.Min(observed, end);
        if (observed <= _scheduler.Now) return;
        // A real stalled bus instruction or held RESET cannot service an IRQ.
        // Devices must still complete at their original active host deadlines.
        if (instructionBlocked) { _scheduler.AdvanceTo(observed); return; }
        // Actual guest execution and instruction assistance consume time even
        // with IF masked. Descheduling the host does not execute guest code.
        if (executedNanoseconds != 0)
            _scheduler.AdvanceTo(_scheduler.Now + Math.Min(executedNanoseconds, observed - _scheduler.Now));
        if (_scheduler.Now >= observed) return;
        if (!_pic.HasPending && (_scheduler.NextEventTime is not { } due || due > observed))
        { _scheduler.AdvanceTo(observed); return; }
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, CatchupRegisters, 4, _catchupValues), "read host catch-up interruptibility");
        bool enabled = (_catchupValues[0].Low & 0x200) != 0;
        if (_halted && !enabled) { _scheduler.AdvanceTo(observed); return; }
        if (!enabled || (_catchupValues[1].Low & 1) != 0 || (_catchupValues[2].Low & 1) != 0 ||
            (_catchupValues[3].Low & 1) != 0 || _pic.HasPending) return;
        while (_scheduler.NextEventTime is { } next && next <= observed)
        {
            _scheduler.AdvanceTo(next);
            if (_pic.HasPending) return;
        }
        _scheduler.AdvanceTo(observed);
    }

    /// <summary>Reads optional actual VP runtime; an older host retains conservative wall-time accounting instead of inventing guest progress.</summary>
    private ulong? ReadNativeRuntime()
    {
        if (!_runtimeCountersAvailable) return null;
        try
        {
            int result = WhpxNative.WHvGetVirtualProcessorCounters(_partition, 0, 0, out var counters, 16, out uint written);
            if (result >= 0 && written == 16) return counters.TotalRuntime100ns;
        }
        catch (EntryPointNotFoundException) { }
        _runtimeCountersAvailable = false;
        return null;
    }

    /// <summary>Charges only the measured scheduled VP interval, bounded by its host call; unavailable or reset counters keep the conservative elapsed interval.</summary>
    private ulong NativeExecutionNanoseconds(ulong? before, long timestamp)
    {
        ulong elapsed = HostNanosecondsSince(timestamp);
        ulong? after = before.HasValue ? ReadNativeRuntime() : null;
        _nativeRuntimeSample = after;
        if (before is { } a && after is { } b && b >= a)
            return (ulong)UInt128.Min((UInt128)(b - a) * 100, elapsed);
        return elapsed;
    }

    /// <summary>Accounts for one indivisible emulated instruction without skipping its actual owner work.</summary>
    private static ulong HostNanosecondsSince(long timestamp) =>
        (ulong)((UInt128)(ulong)(Stopwatch.GetTimestamp() - timestamp) * 1_000_000_000 / (ulong)Stopwatch.Frequency);

    /// <summary>Waits for an actual device/owner deadline during HLT, without fast-forwarding idle time.</summary>
    private void WaitForHaltedEvent(ulong target)
    {
        ulong wake = _scheduler.NextEventTime is { } next ? Math.Min(next, target) : target;
        ulong remaining = _clock.Remaining(wake);
        _deadlineTimer.WaitHalted(Math.Min(remaining, 10_000_000));
    }

    /// <summary>Runs until a virtual deadline, clocking due devices even while the guest is halted.</summary>
    public WhpxMappedRunResult RunUntilVirtualTime(ulong deadline, TimeSpan timeout, int maximumExits = 10000)
    {
        EnsureStopped();
        if (deadline < _scheduler.Now) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (deadline == _scheduler.Now)
            return new(0, 0, 0, 0, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
        return Run(timeout, maximumExits, deadline);
    }

    /// <summary>Executes bounded native slices with shared RAM, complete active host time, every due device event and PIC delivery.</summary>
    private WhpxMappedRunResult Run(TimeSpan timeout, int maximumExits, ulong? virtualDeadline)
    {
        EnsureStopped();
        if (timeout <= TimeSpan.Zero || maximumExits <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        bool ownsClock = !_realtimeWindow;
        if (ownsClock) _clock.Begin(_scheduler.Now);
        _running = true;
        int io = 0, mmio = 0, interrupts = 0;
        using var deadline = new CancellationTokenSource(timeout);
        Func<ulong>? glideBoundary=null;
        try
        {
            _memory.ActivateSharedRam();
            _memory.Refresh();
            if (TryInjectPic()) interrupts++;
            for (int exits = 0; exits < maximumExits; exits++)
            {
                if (deadline.IsCancellationRequested) throw new TimeoutException("WHP mapped execution timed out.");
                SynchronizeClock(virtualDeadline);
                _memory.Refresh();
                bool resetHeld = ServiceHardwareBoundary?.Invoke() == true;
                if (resetHeld) SynchronizeClock(virtualDeadline, instructionBlocked: true);
                if (virtualDeadline is { } reached && _scheduler.Now >= reached)
                    return new(io, mmio, interrupts, exits, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                if (resetHeld) { _deadlineTimer.WaitHalted(100_000); continue; }
                if (_halted)
                {
                    if (TryInjectPic()) { interrupts++; _halted = false; }
                    else if (virtualDeadline is not { } haltedTarget)
                        return new(io, mmio, interrupts, exits, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                    else
                    {
                        WaitForHaltedEvent(haltedTarget);
                        continue;
                    }
                }
                ulong budget = 1_000_000;
                if (_scheduler.NextEventTime is { } nextEvent)
                    budget = Math.Min(budget, nextEvent > _scheduler.Now ? nextEvent - _scheduler.Now : 10_000);
                if (virtualDeadline is { } untilRun) budget = Math.Min(budget, _clock.Remaining(untilRun));
                _deadlineTimer.Arm(budget);
                ulong? runtimeBefore = _nativeRuntimeSample ?? ReadNativeRuntime();
                long cpuStarted = Stopwatch.GetTimestamp();
                WhpxNative.Check(WhpxNative.WHvRunVirtualProcessor(_partition, 0, _exit, (uint)_exit.Length), "run mapped VP");
                ulong executed = NativeExecutionNanoseconds(runtimeBefore, cpuStarted);
                _deadlineTimer.CheckFailure();
                SynchronizeClock(virtualDeadline, executed);
                long assistStarted = Stopwatch.GetTimestamp();
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(_exit);
                switch (reason)
                {
                    case 1:
                        _emulator.ClearFpuContinuation();
                        // Admission is checked before instruction emulation: a stalled access has no partial register or memory effects.
                        if (WaitForMemoryAdmission(virtualDeadline, deadline.Token)) { mmio++; break; }
                        try
                        {
                            ulong blockEnd=Math.Min(virtualDeadline??ulong.MaxValue,_scheduler.NextEventTime??ulong.MaxValue);
                            glideBoundary??=()=>
                            {
                                long generation=_processorGeneration;deadline.Token.ThrowIfCancellationRequested();
                                SynchronizeClock(virtualDeadline);_memory.Refresh();
                                bool held=ServiceHardwareBoundary?.Invoke()==true;
                                if(held||generation!=_processorGeneration||_pic.HasPending||_interruptWindowArmed||
                                    virtualDeadline is {} until&&_scheduler.Now>=until)return 0;
                                return Math.Min(virtualDeadline??ulong.MaxValue,_scheduler.NextEventTime??ulong.MaxValue);
                            };
                            if (_pic.HasPending || _interruptWindowArmed ||
                                !_glideBatch.TryRun(_exit,blockEnd,glideBoundary)&&(!_enableVideoBatch||!_videoBatch.TryRun(_exit)))
                                _emulator.Emulate(_exit);
                            // The VP stays stopped. Every additional instruction has its own current code/data checks, real bus write and owner time boundary.
                            for(int assisted=0;assisted<8&&_emulator.HasFpuContinuation;assisted++)
                            {
                                SynchronizeClock(virtualDeadline);_memory.Refresh();
                                if(ServiceHardwareBoundary?.Invoke()==true||_pic.HasPending||_interruptWindowArmed||
                                    virtualDeadline is {} end&&_scheduler.Now>=end||!_emulator.TryCompleteConsecutiveFpuStore())break;
                            }
                        }
                        catch (NotSupportedException error)
                        {
                            var cs = ReadRegister(WhpxNative.Cs);
                            var ds = ReadRegister(WhpxNative.Ds);
                            var es = ReadRegister(WhpxNative.Es);
                            throw new NotSupportedException($"{error.Message} RIP={ReadRegister(WhpxNative.Rip).Low:X}, " +
                                $"CS={cs.Low:X}/{cs.High:X}, DS={ds.Low:X}/{ds.High:X}, ES={es.Low:X}/{es.High:X}, " +
                                $"RSI={ReadRegister(6).Low:X}, RDI={ReadRegister(7).Low:X}, " +
                                $"RCX={ReadRegister(WhpxNative.Rcx).Low:X}, RFLAGS={ReadRegister(WhpxNative.Rflags).Low:X}, " +
                                $"CR0={ReadRegister(0x1c).Low:X}, CR3={ReadRegister(0x1e).Low:X}.", error);
                        }
                        mmio++;
                        break;
                    case 2:
                        HandleIoExit();
                        io++;
                        break;
                    case 7: // interruptibility changed to a PIC-deliverable window
                        _interruptWindowArmed = false; // WHP consumes the one-shot notification.
                        break;
                    case 8:
                        _deadlineTimer.Disarm();
                        _halted = true;
                        if (TryInjectPic()) { interrupts++; _halted = false; }
                        else if (virtualDeadline is not null) continue;
                        else return new(io, mmio, interrupts, exits + 1, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                        break;
                    case 0x1000:
                        HandleMsrExit();
                        break;
                    case 0x1001:
                        HandleCpuidExit();
                        break;
                    case 0x1002:
                        HandleExceptionExit();
                        break;
                    case 0x1003:
                        HandleRdtscExit();
                        break;
                    case 0x2001: // host-requested time slice
                        break;
                    default:
                        throw new NotSupportedException($"WHP mapped exit 0x{reason:X} at RIP={ReadRegister(WhpxNative.Rip).Low:X}.");
                }
                SynchronizeClock(virtualDeadline, HostNanosecondsSince(assistStarted));
                _memory.Refresh();
                if (reason != 8 && TryInjectPic()) interrupts++;
                if (virtualDeadline is { } until && _scheduler.Now >= until)
                    return new(io, mmio, interrupts, exits + 1, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
            }
            throw new InvalidOperationException($"WHP mapped exit budget exhausted at reason=0x{BinaryPrimitives.ReadUInt32LittleEndian(_exit):X}, RIP=0x{ReadRegister(WhpxNative.Rip).Low:X}.");
        }
        finally
        {
            try
            {
                _deadlineTimer.Disarm();
                if (ownsClock || !_busWritesOnly) _memory.DeactivateSharedRam();
            }
            finally { _running = false; if (ownsClock) { _clock.End(); } }
        }
    }

    /// <summary>Waits on the existing host-paced owner clock while prior device work blocks a new MMIO transaction; no guest opcode is skipped or retired.</summary>
    private bool WaitForMemoryAdmission(ulong? virtualDeadline, CancellationToken cancellation)
    {
        if(_bus.DynamicDecoderCount==0)return false;
        ulong address=BinaryPrimitives.ReadUInt64LittleEndian(_exit.AsSpan(72));
        if(address>uint.MaxValue)return false;
        uint access=BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(68))&3;
        if(access>1)return false;
        if(_bus.TransactionReadyAt((uint)address,access==1) is null)return false;
        var initialCs=ReadRegister(WhpxNative.Cs);ulong initialRip=ReadRegister(WhpxNative.Rip).Low;
        while(_bus.TransactionReadyAt((uint)address,access==1) is { } ready)
        {
            cancellation.ThrowIfCancellationRequested();
            _deadlineTimer.Disarm();SynchronizeClock(virtualDeadline, instructionBlocked: true);_memory.Refresh();
            if(ServiceHardwareBoundary?.Invoke()==true)return true;
            var currentCs=ReadRegister(WhpxNative.Cs);
            if(currentCs.Low!=initialCs.Low||currentCs.High!=initialCs.High||ReadRegister(WhpxNative.Rip).Low!=initialRip)return true;
            if(virtualDeadline is { } end&&_scheduler.Now>=end)return true;
            if(_bus.TransactionReadyAt((uint)address,access==1) is null)return false;
            ulong wake=Math.Min(ready,_scheduler.NextEventTime??ready);
            if(virtualDeadline is { } target)wake=Math.Min(wake,target);
            _deadlineTimer.WaitHalted(Math.Min(_clock.Remaining(wake),10_000_000));
        }
        return false;
    }

    /// <summary>Acknowledges a real pending PIC vector only when the native CPU can accept it, otherwise requests a delivery window.</summary>
    private bool TryInjectPic()
    {
        if (!_pic.HasPending)
        {
            SetInterruptWindow(false);
            return false;
        }
        if ((ReadRegister(WhpxNative.PendingInterruption).Low & 1) != 0 ||
            (ReadRegister(WhpxNative.PendingEvent).Low & 1) != 0)
        {
            SetInterruptWindow(false);
            return false;
        }
        if ((ReadRegister(WhpxNative.Rflags).Low & 0x200) == 0 ||
            (ReadRegister(WhpxNative.InterruptState).Low & 1) != 0)
        {
            SetInterruptWindow(true);
            return false;
        }
        SetInterruptWindow(false);
        if (_pic.Acknowledge() is not { } vector) return false;
        SetRegister(WhpxNative.PendingInterruption, new(1UL | ((ulong)vector << 16)));
        // A queued accepted interrupt wakes the cached HLT state even when the
        // injection occurs at the beginning of a new owner slice.
        _halted = false;
        _nativeRuntimeSample = null;
        return true;
    }

    /// <summary>Updates only the native interrupt-window notification bit while preserving other notifications.</summary>
    private void SetInterruptWindow(bool enabled)
    {
        if (_interruptWindowArmed == enabled) return;
        ulong current = ReadRegister(WhpxNative.DeliverabilityNotifications).Low;
        ulong wanted = enabled ? current | 2UL : current & ~2UL;
        if (wanted != current)
            SetRegister(WhpxNative.DeliverabilityNotifications, new(wanted));
        _interruptWindowArmed = enabled;
    }

    /// <summary>Returns the selected P54C identity or the 486 contract's architectural invalid-opcode fault.</summary>
    private void HandleCpuidExit()
    {
        if (_cpuContract == WhpxCpuContract.Host) throw new InvalidOperationException("Unexpected CPUID exit without a CPU contract.");
        if (_cpuContract == WhpxCpuContract.Intel486Dx) { InjectFault(6, false); return; }
        uint leaf = (uint)ReadRegister(WhpxNative.Rax).Low;
        uint eax = 0, ebx = 0, ecx = 0, edx = 0;
        if (leaf == 0) { eax = 1; ebx = 0x756e6547; edx = 0x49656e69; ecx = 0x6c65746e; }
        else if (leaf == 1) { eax = _cpuContract == WhpxCpuContract.IntelP55cMmxSubset ? 0x543u : 0x524u; edx = 0x135u | (_cpuContract == WhpxCpuContract.IntelP55cMmxSubset ? 1u << 23 : 0); }
        SetRegister(WhpxNative.Rax, new(eax)); SetRegister(WhpxNative.Rbx, new(ebx));
        SetRegister(WhpxNative.Rcx, new(ecx)); SetRegister(WhpxNative.Rdx, new(edx));
        AdvanceInterceptedInstruction();
    }

    /// <summary>Reports the nominal guest TSC from virtual device time under the selected CPU's privilege and opcode rules.</summary>
    private void HandleRdtscExit()
    {
        if (_cpuContract == WhpxCpuContract.Host) throw new InvalidOperationException("Unexpected RDTSC exit without a CPU contract.");
        if (_cpuContract == WhpxCpuContract.Intel486Dx ||
            (BinaryPrimitives.ReadUInt64LittleEndian(_exit.AsSpan(80)) & 1) != 0)
        { InjectFault(6, false); return; } // P54C has no RDTSCP.
        if ((ReadRegister(WhpxNative.Rflags).Low & 0x20000) != 0 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(_exit.AsSpan(8)) & 3) != 0 &&
            (ReadRegister(0x1f).Low & 4) != 0)
        { InjectFault(13, true); return; }
        SetTimestampResult(VirtualTsc());
        AdvanceInterceptedInstruction();
    }

    /// <summary>Handles only the admitted P54C TSC MSR and injects the selected CPU fault for other accesses.</summary>
    private void HandleMsrExit()
    {
        if (_cpuContract == WhpxCpuContract.Host) throw new InvalidOperationException("Unexpected MSR exit without a CPU contract.");
        if (_cpuContract == WhpxCpuContract.Intel486Dx) { InjectFault(6, false); return; }
        uint info = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(48));
        uint number = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(52));
        if ((BinaryPrimitives.ReadUInt16LittleEndian(_exit.AsSpan(8)) & 3) != 0 ||
            (ReadRegister(WhpxNative.Rflags).Low & 0x20000) != 0 || number != 0x10)
        { InjectFault(13, true); return; }
        if ((info & 1) != 0)
        {
            ulong value = ((ReadRegister(WhpxNative.Rdx).Low & 0xffff_ffff) << 32) |
                (ReadRegister(WhpxNative.Rax).Low & 0xffff_ffff);
            _tscEpochTicks = unchecked(_scheduler.ClockTicks(_nominalCpuHz) - value);
        }
        else SetTimestampResult(VirtualTsc());
        AdvanceInterceptedInstruction();
    }

    /// <summary>Derives the nominal P54C timestamp from the owning scheduler and the guest's saved write epoch.</summary>
    private ulong VirtualTsc() => unchecked(_scheduler.ClockTicks(_nominalCpuHz) - _tscEpochTicks);

    /// <summary>Commits the low and high halves of an architectural timestamp result to EAX and EDX.</summary>
    private void SetTimestampResult(ulong value)
    {
        SetRegister(WhpxNative.Rax, new((uint)value));
        SetRegister(WhpxNative.Rdx, new((uint)(value >> 32)));
    }

    /// <summary>Queues one architectural guest exception without replacing an already pending event.</summary>
    private void InjectFault(uint vector, bool errorCode)
    {
        if ((ReadRegister(WhpxNative.PendingEvent).Low & 1) != 0)
            throw new InvalidOperationException("WHP already has a pending guest event.");
        ulong eventBits = 1UL | (errorCode ? 1UL << 8 : 0) | ((ulong)vector << 16);
        SetRegister(WhpxNative.PendingEvent, new(eventBits));
    }

    /// <summary>Preserves the narrow real-mode copy contract or reinjects the actual exception and its error information.</summary>
    private void HandleExceptionExit()
    {
        uint info = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(68));
        byte vector = _exit[72];
        uint error = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(76));
        ulong parameter = BinaryPrimitives.ReadUInt64LittleEndian(_exit.AsSpan(80));
        if (vector == 13 && TryCompleteLegacyRealModeCopy())
        {
            if (_traceExceptions) Console.WriteLine($"WHP-COMPAT-COPY: time={_scheduler.Now}.");
            return;
        }
        if (_traceExceptions) Console.WriteLine($"WHP-EXCEPTION: time={_scheduler.Now}, vector={vector:X2}, error={error:X8}, " +
            $"RIP={ReadRegister(WhpxNative.Rip).Low:X}, CS={ReadRegister(WhpxNative.Cs).Low:X}, " +
            $"DS={ReadRegister(WhpxNative.Ds).Low:X}/{ReadRegister(WhpxNative.Ds).High:X}, " +
            $"ES={ReadRegister(WhpxNative.Es).Low:X}/{ReadRegister(WhpxNative.Es).High:X}, " +
            $"RSI={ReadRegister(6).Low:X}, RDI={ReadRegister(7).Low:X}, RCX={ReadRegister(WhpxNative.Rcx).Low:X}, " +
            $"RFLAGS={ReadRegister(WhpxNative.Rflags).Low:X}, CR0={ReadRegister(0x1c).Low:X}, " +
            $"CR2={ReadRegister(0x1d).Low:X}, CR3={ReadRegister(0x1e).Low:X}, " +
            $"parameter={parameter:X}, bytes={Convert.ToHexString(_exit.AsSpan(52, 16))}.");
        if ((ReadRegister(WhpxNative.PendingEvent).Low & 1) != 0)
            throw new InvalidOperationException("WHP exception exit found an already pending event.");
        ulong eventBits = 1UL | ((info & 1UL) << 8) | ((ulong)vector << 16) | ((ulong)error << 32);
        SetRegister(WhpxNative.PendingEvent, new(eventBits, parameter));
    }

    // The existing software CPU permits these Windows 95 loader copies
    // with 32-bit real-mode offsets and 64-KiB segment caches. Real hardware
    // raises #GP. Preserve that loader contract only for plain RAM on both ends.
    private bool TryCompleteLegacyRealModeCopy()
    {
        int width, length;
        if (_exit[48] >= 4 && _exit.AsSpan(52, 4).SequenceEqual(new byte[] { 0xf3, 0x67, 0x66, 0xa5 }))
        { width = 4; length = 4; }
        else if (_exit[48] >= 3 && _exit.AsSpan(52, 3).SequenceEqual(new byte[] { 0xf3, 0x67, 0xa5 }))
        { width = 2; length = 3; }
        else return false;
        if ((ReadRegister(0x1c).Low & 0x8000_0001UL) != 0 ||
            (ReadRegister(WhpxNative.Rflags).Low & 0x20000) != 0)
            return false;
        var ds = ReadRegister(WhpxNative.Ds);
        var es = ReadRegister(WhpxNative.Es);
        if (ds.Low != 0 || es.Low != 0 || (ds.High & 0xffff_ffff) != 0xffff ||
            (es.High & 0xffff_ffff) != 0xffff) return false;

        ulong rsi = ReadRegister(6).Low, rdi = ReadRegister(7).Low;
        ulong rcx = ReadRegister(WhpxNative.Rcx).Low;
        uint count = (uint)rcx;
        if (count is 0 or > 65536 || rsi <= 0xffff && rdi <= 0xffff) return false;
        int direction = (ReadRegister(WhpxNative.Rflags).Low & 0x400) != 0 ? -1 : 1;
        long source = (uint)rsi, destination = (uint)rdi;
        for (uint element = 0; element < count; element++)
        {
            long from = source + (long)element * direction * width;
            long to = destination + (long)element * direction * width;
            if (from < 0 || to < 0 || from + width - 1 > uint.MaxValue || to + width - 1 > uint.MaxValue)
                return false;
            for (int lane = 0; lane < width; lane++)
                if (!_bus.IsPlainRamByte((uint)(from + lane)) || !_bus.IsPlainRamByte((uint)(to + lane)))
                    return false;
        }
        Span<byte> value = stackalloc byte[4];
        for (uint element = 0; element < count; element++)
        {
            uint from = (uint)(source + (long)element * direction * width);
            uint to = (uint)(destination + (long)element * direction * width);
            for (uint lane = 0; lane < width; lane++) value[(int)lane] = _bus.ReadByte(from + lane);
            for (uint lane = 0; lane < width; lane++) _bus.WriteByte(to + lane, value[(int)lane]);
        }
        ulong rip = ReadRegister(WhpxNative.Rip).Low;
        uint[] names = [6, 7, WhpxNative.Rcx, WhpxNative.Rip];
        WhpxNative.RegisterValue[] values =
        [new((rsi & ~0xffff_ffffUL) | unchecked((uint)(source + (long)direction * count * width))),
         new((rdi & ~0xffff_ffffUL) | unchecked((uint)(destination + (long)direction * count * width))),
         new(rcx & ~0xffff_ffffUL),
         new((rip & ~0xffffUL) | (ushort)(rip + (uint)length))];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 4,
            values), "complete Windows 95 real-mode copy");
        return true;
    }

    /// <summary>Advances RIP by the validated native exit instruction length.</summary>
    private void AdvanceInterceptedInstruction()
    {
        int length = _exit[10] & 15;
        if (length is < 1 or > 15) throw new NotSupportedException("WHP CPU instruction exit has no valid length.");
        SetRegister(WhpxNative.Rip, new(ReadRegister(WhpxNative.Rip).Low + (uint)length));
    }

    /// <summary>Routes actual scalar ports directly and string I/O through the checked decoder.</summary>
    private void HandleIoExit()
    {
        int length = _exit[10] & 15;
        uint info = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(68));
        int width = (int)((info >> 1) & 7);
        if ((info & 0x30) != 0)
        {
            try { _emulator.EmulateIo(_exit); }
            catch (NotSupportedException error)
            {
                var cs = ReadRegister(WhpxNative.Cs);
                var ds = ReadRegister(WhpxNative.Ds);
                var es = ReadRegister(WhpxNative.Es);
                throw new NotSupportedException($"{error.Message} RIP={ReadRegister(WhpxNative.Rip).Low:X}, " +
                    $"CS={cs.Low:X}/{cs.High:X}, DS={ds.Low:X}/{ds.High:X}, ES={es.Low:X}/{es.High:X}, " +
                    $"RDX={ReadRegister(WhpxNative.Rdx).Low:X}, RSI={ReadRegister(6).Low:X}, " +
                    $"RDI={ReadRegister(7).Low:X}, RCX={ReadRegister(WhpxNative.Rcx).Low:X}, RFLAGS={ReadRegister(WhpxNative.Rflags).Low:X}, " +
                    $"CR0={ReadRegister(0x1c).Low:X}, CR3={ReadRegister(0x1e).Low:X}, CR4={ReadRegister(0x1f).Low:X}.", error);
            }
            return;
        }
        if (length is < 1 or > 15 || width is not (1 or 2 or 4))
            throw new NotSupportedException("Malformed WHP I/O exit.");
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(_exit.AsSpan(72));
        ulong nextIp = BinaryPrimitives.ReadUInt64LittleEndian(_exit.AsSpan(32)) + (uint)length;
        if ((info & 1) != 0 && port == WhpxGlidePassThrough.Port && width == 4)
        {
            if (_glidePassThrough is null)
                throw new InvalidOperationException("The guest Glide bridge was called without DOSE_GLIDE_HOST_DLL configured.");
            WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0,
                GlideInputRegisters, 3, _glideInputValues),
                "read Glide call registers");
            uint result = _glidePassThrough.Invoke((uint)_glideInputValues[0].Low,
                (uint)_glideInputValues[1].Low, (uint)_glideInputValues[2].Low);
            _glideOutputValues[0] = new(result);
            _glideOutputValues[1] = new(nextIp);
            WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0,
                GlideOutputRegisters, 2, _glideOutputValues),
                "complete Glide host call");
            return;
        }
        ulong rax = ReadRegister(WhpxNative.Rax).Low;
        uint mask = width == 4 ? uint.MaxValue : (uint)((1UL << (width * 8)) - 1);
        if ((info & 1) != 0)
        {
            _ports.Write(port, (uint)rax & mask, width);
            SetRegister(WhpxNative.Rip, new(nextIp));
        }
        else
        {
            uint read = _ports.Read(port, width) & mask;
            uint[] names = [WhpxNative.Rax, WhpxNative.Rip];
            WhpxNative.RegisterValue[] values = [new((rax & ~mask) | read), new(nextIp)];
            WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 2, values), "complete scalar port input");
        }
    }

    /// <summary>Reads the requested native register through the owning partition.</summary>
    private WhpxNative.RegisterValue ReadRegister(uint name)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = new WhpxNative.RegisterValue[1];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, 1, values), "read mapped register");
        return values[0];
    }

    /// <summary>Writes the requested native register through the owning partition.</summary>
    private void SetRegister(uint name, WhpxNative.RegisterValue value)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = [value];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 1, values), "set mapped register");
    }

    /// <summary>Rejects register or storage access during native execution or after disposal.</summary>
    private void EnsureStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("WHP owner state is accessible only while stopped.");
    }

    /// <summary>Admits device-only frontend input between completed slices while keeping native RAM and active clock ownership.</summary>
    internal void RequireDeviceBoundary()
    {
        EnsureStopped();
        if (_realtimeWindow || _scheduler.IsDispatching) throw new InvalidOperationException("WHP device input requires a completed owner slice outside device callbacks.");
    }

    /// <summary>Rejects a whole-machine capture or disposal inside an active window whose bus-writes-only observers share live native RAM.</summary>
    internal void RequireCheckpointBoundary()
    {
        EnsureStopped();
        if (_realtimeWindow || _bus.HasExternalRam) throw new InvalidOperationException("WHP checkpoints require the end of the complete real-time window.");
    }

    /// <summary>Checks external-renderer support before a full capture can publish any CPU, RAM or media files.</summary>
    internal void RequireSnapshotSupport()
    {
        EnsureStopped();
        _glidePassThrough?.RequireSnapshotSupport();
    }

    /// <summary>Joins the deadline helper and releases native CPU, shared RAM and partition resources at a complete stopped window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        PauseRealtime(); RequireCheckpointBoundary();
        _disposed = true;
        _deadlineTimer.Dispose();
        _glidePassThrough?.Dispose();
        _emulator.Dispose();
        if (_vp) WhpxNative.WHvDeleteVirtualProcessor(_partition, 0);
        _memory.Dispose();
        if (_partition != 0) WhpxNative.WHvDeletePartition(_partition);
    }
}

public readonly record struct WhpxMappedRunResult(int IoExits, int MmioExits, int PicInterrupts,
    int TotalExits, ulong Rip, ulong VirtualNanoseconds);

public enum WhpxCpuContract { Host, Intel486Dx, IntelP54cB5Subset, IntelP55cMmxSubset }
public sealed record WhpxCpuState(WhpxRegisterState[] Registers, WhpxCpuContract Contract,
    uint NominalCpuHz, uint VirtualTimeScale, ulong TscEpochTicks, bool Halted, string ClockPolicy = "host-monotonic-1:1-v1");
public readonly record struct WhpxRegisterState(uint Name, ulong Low, ulong High);
