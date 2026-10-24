namespace DOSEmulator.Core;

public interface IIoPortDevice
{
    uint Read(ushort port, int width);
    void Write(ushort port, uint value, int width);
}

/// <summary>Synchronous virtual bus time consumed before the device observes an access.</summary>
public interface IIoPortTiming
{
    void CompleteAccess(int width);
}

public sealed class IoPortBus
{
    private sealed record Mapping(IIoPortDevice Device, IIoPortTiming? Timing);
    private readonly Mapping?[] _devices = new Mapping[65536];
    public event Action<ushort, uint, int, bool>? Accessed;

    public void Map(ushort start, int length, IIoPortDevice device, IIoPortTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (length <= 0 || start + length > 65536) throw new ArgumentOutOfRangeException(nameof(length));
        for (int i = start; i < start + length; i++)
            if (_devices[i] is not null) throw new InvalidOperationException($"I/O port {i:X4} is already mapped.");
        var mapping = new Mapping(device, timing);
        for (int i = start; i < start + length; i++) _devices[i] = mapping;
    }

    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width);
        Mapping? mapping = _devices[port];
        mapping?.Timing?.CompleteAccess(width);
        uint result = (mapping?.Device.Read(port, width) ?? mask) & mask;
        Accessed?.Invoke(port, result, width, false);
        return result;
    }

    public void Write(ushort port, uint value, int width)
    {
        value &= Mask(width);
        Mapping? mapping = _devices[port];
        mapping?.Timing?.CompleteAccess(width);
        mapping?.Device.Write(port, value, width);
        Accessed?.Invoke(port, value, width, true);
    }

    private static uint Mask(int width) => width switch
    {
        1 => 0xff, 2 => 0xffff, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width))
    };
}
