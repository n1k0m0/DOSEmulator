// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Virtual486's limited normal 8-bit ISA I/O path: six clocks at 8 MHz per byte.
/// Device effects and the bus trace occur at completion, after intervening scheduled events.
/// Wider transactions consume byte-cycle time but retain the device's existing width rules;
/// splitting, IOCHRDY/NOWS, arbitration, DMA bus ownership and pin-level timing are pending.</summary>
public sealed partial class Isa8BitIoTiming(DeviceScheduler clock) : IIoPortTiming
{
    public const uint Frequency = 8_000_000;
    public const uint ClocksPerByte = 6;
    public const ulong NanosecondsPerByte = 750;
    /// <summary>Number of timed ISA accesses observed by this timing policy.</summary>
    public long Accesses { get; private set; }
    /// <summary>Accumulated ISA byte-cycle count used by the reference bus timing policy.</summary>
    public long ByteCycles { get; private set; }
    /// <summary>Accumulated virtual nanoseconds consumed by timed accesses.</summary>
    public ulong ElapsedNanoseconds { get; private set; }

    /// <summary>Consumes the reference ISA access time before the attached device observes the port operation.</summary>
    public void CompleteAccess(int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        ulong duration = (uint)width * NanosecondsPerByte;
        clock.AdvanceTo(checked(clock.Now + duration));
        Accesses++; ByteCycles += width; ElapsedNanoseconds = checked(ElapsedNanoseconds + duration);
    }
}
