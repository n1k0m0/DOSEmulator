// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Whpx;

/// <summary>
/// Experimental physical-bus WHP owner. RAM, ROM, MMIO, port I/O and a cascaded PIC
/// use DOSE devices; host-clock slices drive the supplied scheduler. Production PC
/// launchers and their deterministic snapshots retain their software CPU.
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
        0x2000, 0x2001, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007,
        WhpxNative.PendingInterruption, WhpxNative.InterruptState, WhpxNative.PendingEvent,
        WhpxNative.DeliverabilityNotifications
    ];
    private readonly IoPortBus _ports;
    private readonly MemoryBus _bus;
    private readonly Pic8259 _pic;
    private readonly DeviceScheduler _scheduler;
    private readonly WhpxCpuContract _cpuContract;
    private readonly uint _nominalCpuHz;
    private readonly byte[] _exit = new byte[4096];
    private readonly WhpxPhysicalMemory _memory;
    private readonly WhpxInstructionEmulator _emulator;
    private readonly bool _traceExceptions = Environment.GetEnvironmentVariable("DOSE_WHPX_TRACE_EXCEPTIONS") == "1";
    private readonly uint _timeScale;
    private nint _partition;
    private ulong _tscEpochTicks;
    private bool _vp, _running, _disposed, _halted, _interruptWindowArmed;

    public WhpxMappedMachine(MemoryBus memory, IoPortBus ports, Pic8259 pic, DeviceScheduler scheduler,
        WhpxCpuContract cpuContract = WhpxCpuContract.Host, uint nominalCpuHz = 0,
        uint virtualTimeScale = 1)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(pic);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (cpuContract == WhpxCpuContract.IntelP54cB5Subset ? nominalCpuHz == 0 : nominalCpuHz != 0)
            throw new ArgumentException("Only the P54C contract accepts a positive nominal CPU frequency.", nameof(nominalCpuHz));
        if (!Enum.IsDefined(cpuContract)) throw new ArgumentOutOfRangeException(nameof(cpuContract));
        if (virtualTimeScale is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(virtualTimeScale));
        if (!WhpxRealModeMachine.IsAvailable()) throw new PlatformNotSupportedException("Windows Hypervisor Platform is unavailable.");
        _ports = ports; _bus = memory; _pic = pic; _scheduler = scheduler; _cpuContract = cpuContract;
        _nominalCpuHz = nominalCpuHz; _timeScale = virtualTimeScale;
        if (nominalCpuHz != 0) _tscEpochTicks = scheduler.ClockTicks(nominalCpuHz);
        WhpxPhysicalMemory? mappedMemory = null;
        WhpxInstructionEmulator? instructionEmulator = null;
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
        }
        catch
        {
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
        _halted = false;
        if (_nominalCpuHz != 0) _tscEpochTicks = _scheduler.ClockTicks(_nominalCpuHz);
    }

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
            _cpuContract, _nominalCpuHz, _timeScale, _tscEpochTicks, _halted);
    }

    /// <summary>Restores an exact register set into a fresh stopped VP after the owner restored RAM and devices.</summary>
    public void RestoreCpuState(WhpxCpuState state)
    {
        EnsureStopped();
        if (state.Contract != _cpuContract || state.NominalCpuHz != _nominalCpuHz ||
            state.VirtualTimeScale != _timeScale ||
            state.Registers is null || state.Registers.Length != SavedRegisters.Length ||
            state.Registers.Where((item, index) => item.Name != SavedRegisters[index]).Any())
            throw new InvalidDataException("WHP CPU state register topology differs.");
        var values = state.Registers.Select(item => new WhpxNative.RegisterValue(item.Low, item.High)).ToArray();
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, SavedRegisters,
            (uint)SavedRegisters.Length, values), "restore mapped CPU registers");
        _halted = state.Halted;
        _tscEpochTicks = state.TscEpochTicks;
        _interruptWindowArmed = (state.Registers.Single(item => item.Name == WhpxNative.DeliverabilityNotifications).Low & 2) != 0;
    }

    /// <summary>Runs in bounded host-clock slices. Device events and PIC pins are sampled at each WHP exit.</summary>
    public WhpxMappedRunResult RunUntilHalt(TimeSpan timeout, int maximumExits = 10000) => Run(timeout, maximumExits, null);

    /// <summary>Runs until a virtual deadline, clocking due devices even while the guest is halted.</summary>
    public WhpxMappedRunResult RunUntilVirtualTime(ulong deadline, TimeSpan timeout, int maximumExits = 10000)
    {
        EnsureStopped();
        if (deadline < _scheduler.Now) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (deadline == _scheduler.Now)
            return new(0, 0, 0, 0, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
        return Run(timeout, maximumExits, deadline);
    }

    private WhpxMappedRunResult Run(TimeSpan timeout, int maximumExits, ulong? virtualDeadline)
    {
        EnsureStopped();
        if (timeout <= TimeSpan.Zero || maximumExits <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        _memory.Refresh(true);
        _running = true;
        int io = 0, mmio = 0, interrupts = 0;
        using var deadline = new CancellationTokenSource(timeout);
        using var timerResolution = new TimerResolution();
        using var timer = new Timer(_ => WhpxNative.WHvCancelRunVirtualProcessor(_partition, 0, 0), null,
            Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (TryInjectPic()) interrupts++;
            for (int exits = 0; exits < maximumExits; exits++)
            {
                if (deadline.IsCancellationRequested) throw new TimeoutException("WHP mapped execution timed out.");
                if (_halted)
                {
                    if (TryInjectPic()) { interrupts++; _halted = false; }
                    else if (virtualDeadline is not { } haltedTarget)
                        return new(io, mmio, interrupts, exits, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                    else
                    {
                        ulong wake = _scheduler.NextEventTime is { } next ? Math.Min(next, haltedTarget) : haltedTarget;
                        _scheduler.AdvanceTo(Math.Max(_scheduler.Now, wake));
                        _memory.Refresh();
                        if (TryInjectPic()) { interrupts++; _halted = false; }
                        if (_scheduler.Now >= haltedTarget)
                            return new(io, mmio, interrupts, exits, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                        continue;
                    }
                }
                timer.Change(2, Timeout.Infinite);
                long runStart = Stopwatch.GetTimestamp();
                try { WhpxNative.Check(WhpxNative.WHvRunVirtualProcessor(_partition, 0, _exit, (uint)_exit.Length), "run mapped VP"); }
                finally { timer.Change(Timeout.Infinite, Timeout.Infinite); }
                long runEnd = Stopwatch.GetTimestamp();
                ulong elapsedNs = (ulong)((UInt128)(ulong)(runEnd - runStart) * 1_000_000_000 / (ulong)Stopwatch.Frequency);
                _memory.PullGuestRam();
                _scheduler.AdvanceTo(checked(_scheduler.Now + elapsedNs * _timeScale));
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(_exit);
                switch (reason)
                {
                    case 1:
                        try { _emulator.Emulate(_exit); }
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
                        _halted = true;
                        if (TryInjectPic()) { interrupts++; _halted = false; }
                        else if (virtualDeadline is { } target)
                        {
                            ulong wake = _scheduler.NextEventTime is { } next ? Math.Min(next, target) : target;
                            _scheduler.AdvanceTo(Math.Max(_scheduler.Now, wake));
                            _memory.Refresh();
                            if (TryInjectPic()) { interrupts++; _halted = false; }
                            if (_scheduler.Now >= target)
                                return new(io, mmio, interrupts, exits + 1, ReadRegister(WhpxNative.Rip).Low, _scheduler.Now);
                            continue;
                        }
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
                using var stopped = new ManualResetEvent(false);
                if (timer.Dispose(stopped)) stopped.WaitOne();
                _memory.PullGuestRam();
            }
            finally { _running = false; }
        }
    }

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
        return true;
    }

    private void SetInterruptWindow(bool enabled)
    {
        if (_interruptWindowArmed == enabled) return;
        ulong current = ReadRegister(WhpxNative.DeliverabilityNotifications).Low;
        ulong wanted = enabled ? current | 2UL : current & ~2UL;
        if (wanted != current)
            SetRegister(WhpxNative.DeliverabilityNotifications, new(wanted));
        _interruptWindowArmed = enabled;
    }

    private sealed class TimerResolution : IDisposable
    {
        private readonly bool _active = WhpxNative.TimeBeginPeriod(1) == 0;
        public void Dispose()
        {
            if (_active) WhpxNative.TimeEndPeriod(1);
        }
    }

    private void HandleCpuidExit()
    {
        if (_cpuContract == WhpxCpuContract.Host) throw new InvalidOperationException("Unexpected CPUID exit without a CPU contract.");
        if (_cpuContract == WhpxCpuContract.Intel486Dx) { InjectFault(6, false); return; }
        uint leaf = (uint)ReadRegister(WhpxNative.Rax).Low;
        uint eax = 0, ebx = 0, ecx = 0, edx = 0;
        if (leaf == 0) { eax = 1; ebx = 0x756e6547; edx = 0x49656e69; ecx = 0x6c65746e; }
        else if (leaf == 1) { eax = 0x524; edx = 0x135; }
        SetRegister(WhpxNative.Rax, new(eax)); SetRegister(WhpxNative.Rbx, new(ebx));
        SetRegister(WhpxNative.Rcx, new(ecx)); SetRegister(WhpxNative.Rdx, new(edx));
        AdvanceInterceptedInstruction();
    }

    private void HandleRdtscExit()
    {
        if (_cpuContract == WhpxCpuContract.Host) throw new InvalidOperationException("Unexpected RDTSC exit without a CPU contract.");
        if (_cpuContract == WhpxCpuContract.Intel486Dx ||
            (BinaryPrimitives.ReadUInt64LittleEndian(_exit.AsSpan(80)) & 1) != 0)
        { InjectFault(6, false); return; } // P54C has no RDTSCP.
        if ((ReadRegister(WhpxNative.Rflags).Low & 0x20000) != 0 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(_exit.AsSpan(8)) & 3) != 0 &&
            (ReadRegister(0x1003).Low & 4) != 0)
        { InjectFault(13, true); return; }
        SetTimestampResult(VirtualTsc());
        AdvanceInterceptedInstruction();
    }

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

    private ulong VirtualTsc() => unchecked(_scheduler.ClockTicks(_nominalCpuHz) - _tscEpochTicks);

    private void SetTimestampResult(ulong value)
    {
        SetRegister(WhpxNative.Rax, new((uint)value));
        SetRegister(WhpxNative.Rdx, new((uint)(value >> 32)));
    }

    private void InjectFault(uint vector, bool errorCode)
    {
        if ((ReadRegister(WhpxNative.PendingEvent).Low & 1) != 0)
            throw new InvalidOperationException("WHP already has a pending guest event.");
        ulong eventBits = 1UL | (errorCode ? 1UL << 8 : 0) | ((ulong)vector << 16);
        SetRegister(WhpxNative.PendingEvent, new(eventBits));
    }

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

    private void AdvanceInterceptedInstruction()
    {
        int length = _exit[10] & 15;
        if (length is < 1 or > 15) throw new NotSupportedException("WHP CPU instruction exit has no valid length.");
        SetRegister(WhpxNative.Rip, new(ReadRegister(WhpxNative.Rip).Low + (uint)length));
    }

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
        ulong rax = ReadRegister(WhpxNative.Rax).Low;
        uint mask = width == 4 ? uint.MaxValue : (uint)((1UL << (width * 8)) - 1);
        if ((info & 1) != 0) _ports.Write(port, (uint)rax & mask, width);
        else SetRegister(WhpxNative.Rax, new((rax & ~mask) | (_ports.Read(port, width) & mask)));
        SetRegister(WhpxNative.Rip, new(ReadRegister(WhpxNative.Rip).Low + (uint)length));
    }

    private WhpxNative.RegisterValue ReadRegister(uint name)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = new WhpxNative.RegisterValue[1];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, 1, values), "read mapped register");
        return values[0];
    }

    private void SetRegister(uint name, WhpxNative.RegisterValue value)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = [value];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 1, values), "set mapped register");
    }

    private void EnsureStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("WHP owner state is accessible only while stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureStopped();
        _disposed = true;
        _emulator.Dispose();
        if (_vp) WhpxNative.WHvDeleteVirtualProcessor(_partition, 0);
        _memory.Dispose();
        if (_partition != 0) WhpxNative.WHvDeletePartition(_partition);
    }
}

public readonly record struct WhpxMappedRunResult(int IoExits, int MmioExits, int PicInterrupts,
    int TotalExits, ulong Rip, ulong VirtualNanoseconds);

public enum WhpxCpuContract { Host, Intel486Dx, IntelP54cB5Subset }
public sealed record WhpxCpuState(WhpxRegisterState[] Registers, WhpxCpuContract Contract,
    uint NominalCpuHz, uint VirtualTimeScale, ulong TscEpochTicks, bool Halted);
public readonly record struct WhpxRegisterState(uint Name, ulong Low, ulong High);
