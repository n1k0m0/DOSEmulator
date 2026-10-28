// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Constructor-registered physical fallback peer; its byte methods receive complete absolute bus addresses.</summary>
public interface IMemoryDecoder
{
    /// <summary>Tests one translated physical byte without reading, writing or changing the peer.</summary>
    bool Decodes(uint address);
    /// <summary>Reads an accepted absolute physical byte under the decoder's register/storage contract.</summary>
    byte ReadDecodedByte(uint address);
    /// <summary>Writes an accepted absolute physical byte without confusing it with a fixed mapping offset.</summary>
    void WriteDecodedByte(uint address, byte value);
}

public sealed partial class MemoryBus
{
    private readonly List<IMemoryDecoder> _dynamicDecoders = [];

    /// <summary>Registers a fixed peer without changing installed RAM or static-map precedence.</summary>
    public void MapDynamic(IMemoryDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (_dynamicDecoders.Any(item => ReferenceEquals(item, decoder)))
            throw new InvalidOperationException("Dynamic memory peer already registered.");
        _dynamicDecoders.Add(decoder);
    }

    /// <summary>Counts fixed fallback peers independently from their current guest-programmed decode.</summary>
    public int DynamicDecoderCount => _dynamicDecoders.Count;

    /// <summary>Checks the selected board's sole registered physical fallback peer by identity.</summary>
    public bool HasOnlyDynamicDecoder(IMemoryDecoder decoder) =>
        _dynamicDecoders.Count == 1 && ReferenceEquals(_dynamicDecoders[0], decoder);

    /// <summary>Accepts a complete board resource only outside all installed RAM, fixed mappings and 32-bit wrap.</summary>
    public bool IsFixedWindowAvailable(uint start, uint length)
    {
        ulong end = (ulong)start + length;
        if (length == 0 || end > 0x1_0000_0000UL || start < Ram.Length) return false;
        return !_mappings.Any(m => (ulong)start < (ulong)m.Start + m.Length && m.Start < end);
    }

    /// <summary>Resolves only unmapped physical fallback space in deterministic construction order.</summary>
    private IMemoryDecoder? ResolveDynamic(uint address)
    {
        if (address < Ram.Length) return null;
        foreach (var decoder in _dynamicDecoders) if (decoder.Decodes(address)) return decoder;
        return null;
    }

    /// <summary>Seals only fixed peer identities; movable windows are authoritative state of those peers.</summary>
    private string DynamicStateMap(Func<object, string> owner) => string.Concat(
        _dynamicDecoders.Select((decoder, index) => ";dynamic" + index + ":" + owner(decoder)));
}
