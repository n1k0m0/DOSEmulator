// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly DeviceScheduler? _timestampSource;
    private ulong _tscEpochTicks;
    /// <summary>Fixed nominal guest clock; zero denotes the existing core without an attached virtual clock.</summary>
    public uint TimestampFrequency { get; }
    /// <summary>The implemented TSD/DE P54C control-register subset; other extension bits remain unavailable.</summary>
    public uint Cr4 { get; private set; }
    /// <summary>Whether this explicitly clock-bound core can advertise and execute the TSC facility.</summary>
    public bool HasTimeStampCounter => IsPentiumCore && _timestampSource is not null;
    /// <summary>Owner-boundary counter with modulo-64-bit wrap, derived only from persisted virtual clock state.</summary>
    public ulong TimestampCounter => _timestampSource is null ? 0 : unchecked(_timestampSource.ClockTicks(TimestampFrequency) - _tscEpochTicks);

    /// <summary>Checks the fixed virtual clock peer and frequency without sampling or changing either clock.</summary>
    public bool UsesTimestampClock(DeviceScheduler source, uint frequency) =>
        HasTimeStampCounter && ReferenceEquals(_timestampSource, source) && TimestampFrequency == frequency;

    /// <summary>Samples the full P54C counter, applying the historical unconditional V86 fault and protected TSD rule.</summary>
    private void ReadTimeStampCounter()
    {
        if (!HasTimeStampCounter) throw new CpuFaultException(6);
        // P54C, unlike later Intel cores, rejects RDTSC in V86 even when TSD is clear.
        if (Virtual8086 || CurrentPrivilegeLevel != 0 && (Cr4 & 4) != 0) throw Gp();
        ulong value = TimestampCounter;
        SetRegister(Register.Ax, (uint)value); SetRegister(Register.Dx, (uint)(value >> 32));
    }

    /// <summary>Checks newly imported extension state before a prepared CPU can be adopted by its owner.</summary>
    private void ValidateTimestampState()
    {
        if ((Cr4 & ~12u) != 0 || !HasTimeStampCounter && (Cr4 != 0 || _tscEpochTicks != 0))
            throw new InvalidDataException("Timestamp/control state exceeds the selected CPU clock subset.");
        if (IsPentiumCore && (Dr7 & 0xdc00) != 0x400)
            throw new InvalidDataException("Debug control state violates P54C reserved bits.");
    }
}
