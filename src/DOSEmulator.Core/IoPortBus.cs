// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

public interface IIoPortDevice
{
    /// <summary>Reads the requested device port at the supplied bus width, including any documented read side effects.</summary>
    uint Read(ushort port, int width);
    /// <summary>Accepts one width-selected port write under the device's own register/state contract.</summary>
    void Write(ushort port, uint value, int width);
}

/// <summary>One constructor-registered fallback target with a guest-programmable complete I/O window.</summary>
public interface IIoPortDecoder : IIoPortDevice
{
    /// <summary>Tests a complete transaction without changing state; fixed board mappings retain priority.</summary>
    bool Decodes(ushort port, int width);
}

/// <summary>Synchronous virtual bus time consumed before the device observes an access.</summary>
public interface IIoPortTiming
{
    /// <summary>Consumes the reference ISA access time before the attached device observes the port operation.</summary>
    void CompleteAccess(int width);
}

public sealed class IoPortBus
{
    private sealed record Mapping(IIoPortDevice Device, IIoPortTiming? Timing);
    private readonly Mapping?[] _devices = new Mapping[65536];
    private readonly List<IIoPortDecoder> _dynamicDecoders = [];
    public event Action<ushort, uint, int, bool>? Accessed;

    /// <summary>Registers a nonoverlapping port window and optional synchronous ISA timing policy.</summary>
    public void Map(ushort start, int length, IIoPortDevice device, IIoPortTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (length <= 0 || start + length > 65536) throw new ArgumentOutOfRangeException(nameof(length));
        for (int i = start; i < start + length; i++)
            if (_devices[i] is not null) throw new InvalidOperationException($"I/O port {i:X4} is already mapped.");
        var mapping = new Mapping(device, timing);
        for (int i = start; i < start + length; i++) _devices[i] = mapping;
    }

    /// <summary>Registers a fixed fallback peer whose BAR may move later; duplicate registration leaves the graph unchanged.</summary>
    public void MapDynamic(IIoPortDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (_dynamicDecoders.Any(item => ReferenceEquals(item, decoder))) throw new InvalidOperationException("Dynamic I/O peer already registered.");
        _dynamicDecoders.Add(decoder);
    }

    /// <summary>Checks a complete fixed-board-free range without probing or invoking any device.</summary>
    public bool IsFixedWindowAvailable(ushort start, int length)
    {
        if (length <= 0 || start + length > 65536) return false;
        for (int port = start; port < start + length; port++) if (_devices[port] is not null) return false;
        return true;
    }

    /// <summary>Number of fixed registered dynamic peers, independent of their current BAR decode.</summary>
    public int DynamicDecoderCount => _dynamicDecoders.Count;
    /// <summary>Checks the selected board's sole dynamic peer by identity before full state capture.</summary>
    public bool HasOnlyDynamicDecoder(IIoPortDecoder decoder) => _dynamicDecoders.Count == 1 && ReferenceEquals(_dynamicDecoders[0], decoder);
    /// <summary>Resolves only a complete fallback transaction in deterministic construction order.</summary>
    private IIoPortDevice? ResolveDynamic(ushort port, int width)
    {
        foreach (var decoder in _dynamicDecoders) if (decoder.Decodes(port, width)) return decoder;
        return null;
    }

    /// <summary>Number of decoded bytes, checked against the constructor graph only at snapshot boundaries.</summary>
    public int StatePortCount => _devices.Count(item => item is not null);

    /// <summary>Identifies every decoded byte and timing policy; unknown external mappings cannot be silently lost.</summary>
    public string StateMap(Func<object, string> owner)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < _devices.Length; i++)
            if (_devices[i] is { } item) text.Append(i).Append(':').Append(owner(item.Device)).Append(':')
                .Append(item.Timing is null ? "none" : owner(item.Timing)).Append(';');
        for (int i = 0; i < _dynamicDecoders.Count; i++) text.Append("dynamic").Append(i).Append(':').Append(owner(_dynamicDecoders[i])).Append(';');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Completes access timing, reads/masks the selected device value and publishes the observation hook.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width);
        Mapping? mapping = _devices[port];
        mapping?.Timing?.CompleteAccess(width);
        IIoPortDevice? target = mapping?.Device ?? ResolveDynamic(port, width);
        uint result = (target?.Read(port, width) ?? mask) & mask;
        Accessed?.Invoke(port, result, width, false);
        return result;
    }

    /// <summary>Masks the access width, completes timing and routes the write before publishing its observation hook.</summary>
    public void Write(ushort port, uint value, int width)
    {
        value &= Mask(width);
        Mapping? mapping = _devices[port];
        mapping?.Timing?.CompleteAccess(width);
        (mapping?.Device ?? ResolveDynamic(port, width))?.Write(port, value, width);
        Accessed?.Invoke(port, value, width, true);
    }

    /// <summary>Returns the bit mask for the supported byte, word or doubleword access width.</summary>
    private static uint Mask(int width) => width switch
    {
        1 => 0xff, 2 => 0xffff, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width))
    };
}
