using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>AT-style 486 external numeric-error latch. No cycle/pin propagation timing model.</summary>
public sealed class FpuErrorLatch(Action<bool> interrupt, Action<bool> ignore) : IIoPortDevice
{
    public bool FerrAsserted { get; private set; }
    public bool InterruptAsserted { get; private set; }
    public bool IgnoreNumericError { get; private set; }
    public long ErrorEdges { get; private set; }
    public long Acknowledgements { get; private set; }

    public void SetFerr(bool asserted)
    {
        if (FerrAsserted == asserted) return;
        FerrAsserted = asserted;
        if (asserted)
        {
            ErrorEdges++;
            InterruptAsserted = true;
            interrupt(true);
        }
        else
        {
            IgnoreNumericError = false;
            ignore(false);
            // Clearing the FPU alone does not clear the AT latch or PIC IRR.
        }
    }

    public uint Read(ushort port, int width) => width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
    public void Write(ushort port, uint value, int width)
    {
        if (port != 0xf0 || width != 1) return;
        Acknowledgements++;
        InterruptAsserted = false;
        interrupt(false);
        IgnoreNumericError = FerrAsserted;
        ignore(IgnoreNumericError);
    }
}
