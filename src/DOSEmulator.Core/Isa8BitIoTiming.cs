namespace DOSEmulator.Core;

/// <summary>Virtual486's limited normal 8-bit ISA I/O path: six clocks at 8 MHz per byte.
/// Device effects and the bus trace occur at completion, after intervening scheduled events.
/// Wider transactions consume byte-cycle time but retain the device's existing width rules;
/// splitting, IOCHRDY/NOWS, arbitration, DMA bus ownership and pin-level timing are pending.</summary>
public sealed class Isa8BitIoTiming(DeviceScheduler clock) : IIoPortTiming
{
    public const uint Frequency = 8_000_000;
    public const uint ClocksPerByte = 6;
    public const ulong NanosecondsPerByte = 750;
    public long Accesses { get; private set; }
    public long ByteCycles { get; private set; }
    public ulong ElapsedNanoseconds { get; private set; }

    public void CompleteAccess(int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        ulong duration = (uint)width * NanosecondsPerByte;
        clock.AdvanceTo(checked(clock.Now + duration));
        Accesses++; ByteCycles += width; ElapsedNanoseconds = checked(ElapsedNanoseconds + duration);
    }
}
