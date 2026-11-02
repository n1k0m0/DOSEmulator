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

/// <summary>Optional width-preserving physical MMIO peer; complete transactions never cross A20, fixed maps or decoder boundaries.</summary>
public interface IMemoryTransactionDecoder : IMemoryDecoder
{
    /// <summary>Reads one accepted complete word/dword, including the device's transfer-width restrictions.</summary>
    uint ReadDecoded(uint address, int width);
    /// <summary>Writes one accepted complete word/dword as a single device transaction.</summary>
    void WriteDecoded(uint address, uint value, int width);
}

/// <summary>Pure bus-admission query for a peer whose ordered work must finish before a transaction can start.</summary>
public interface IMemoryTransactionReadiness : IMemoryTransactionDecoder
{
    /// <summary>Returns the next device deadline to service before retry, or null when the bus access can start.</summary>
    ulong? TransactionReadyAt(uint address, bool write);
}

/// <summary>Explicit register-latch writes that cannot alter guest RAM, mappings, event deadlines or interrupt lines.</summary>
public interface IImmediateMemoryWrite
{
    /// <summary>Purely admits one aligned absolute physical DWORD; ordinary opaque memory peers never opt in.</summary>
    bool IsImmediateDwordWrite(uint address);
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

    /// <summary>Checks every A20/resource/peer boundary before admitting an explicit isolated parameter-latch write.</summary>
    public bool IsImmediateDwordWrite(uint address)
    {
        uint physical=Translate(address);
        if((physical&3)!=0||(ulong)address+4>0x1_0000_0000UL||(ulong)physical+4>0x1_0000_0000UL)return false;
        Mapping? first=null;
        foreach(var mapping in _mappings)if(physical>=mapping.Start&&(ulong)physical<(ulong)mapping.Start+mapping.Length){first=mapping;break;}
        for(uint lane=1;lane<4;lane++)
        {
            uint at=Translate(address+lane);if(at!=(ulong)physical+lane)return false;
            Mapping? other=null;
            foreach(var mapping in _mappings)if(at>=mapping.Start&&(ulong)at<(ulong)mapping.Start+mapping.Length){other=mapping;break;}
            if(!ReferenceEquals(first,other))return false;
        }
        if(first is not null)return first.Device is IImmediateMemoryWrite fixedPeer&&fixedPeer.IsImmediateDwordWrite(physical);
        return TransactionDecoder(address,4,out physical) is IImmediateMemoryWrite peer&&peer.IsImmediateDwordWrite(physical);
    }

    /// <summary>Queries only the actual translated unmapped peer; RAM, fixed resources and higher-priority decoders cannot be stalled by another card.</summary>
    public ulong? TransactionReadyAt(uint address, bool write) => TransactionDecoder(address, 1, out uint physical) is IMemoryTransactionReadiness peer
        ? peer.TransactionReadyAt(physical, write) : null;

    /// <summary>Preflights a bounded contiguous byte-device span without bus effects; register-transaction decoders retain their own width contract.</summary>
    public bool IsContiguousByteDeviceSpan(uint address, int width)
    {
        if(width is <1 or >8||(ulong)address+(uint)width>0x1_0000_0000UL)return false;
        uint physical=Translate(address);object? selected=null;
        if((ulong)physical+(uint)width>0x1_0000_0000UL)return false;
        for(int lane=0;lane<width;lane++)
        {
            uint at=Translate(address+(uint)lane);if(at!=(ulong)physical+(uint)lane)return false;
            var mapping=_mappings.FirstOrDefault(m=>at>=m.Start&&(ulong)at<(ulong)m.Start+m.Length);
            object? peer=mapping?.Device??(object?)ResolveDynamic(at);
            if(peer is null||peer is IMemoryTransactionDecoder||selected is not null&&!ReferenceEquals(selected,peer))return false;
            selected=peer;
        }
        return true;
    }

    /// <summary>Checks the selected board's sole registered physical fallback peer by identity.</summary>
    public bool HasOnlyDynamicDecoder(IMemoryDecoder decoder) =>
        _dynamicDecoders.Count == 1 && ReferenceEquals(_dynamicDecoders[0], decoder);

    /// <summary>Checks every constructor-fixed fallback peer in its actual decode priority order.</summary>
    public bool HasDynamicDecoders(params IMemoryDecoder[] peers) => _dynamicDecoders.SequenceEqual(peers, ReferenceEqualityComparer.Instance);

    /// <summary>Offers only a contiguous translated span with no fixed-map priority or decoder change in any lane.</summary>
    private IMemoryTransactionDecoder? TransactionDecoder(uint address, int width, out uint physical)
    {
        physical = Translate(address);
        if ((ulong)address + (uint)width > 0x1_0000_0000UL || (ulong)physical + (uint)width > 0x1_0000_0000UL ||
            physical < Ram.Length) return null;
        for (int lane = 0; lane < width; lane++)
        {
            uint at = physical + (uint)lane;
            if (Translate(address + (uint)lane) != at) return null;
            foreach(var mapping in _mappings)
                if(at>=mapping.Start&&(ulong)at<(ulong)mapping.Start+mapping.Length)return null;
        }
        if (ResolveDynamic(physical) is not IMemoryTransactionDecoder peer) return null;
        for (int lane = 1; lane < width; lane++) if (!ReferenceEquals(ResolveDynamic(physical + (uint)lane), peer)) return null;
        return peer;
    }

    /// <summary>Accepts a complete board resource only outside all installed RAM, fixed mappings and 32-bit wrap.</summary>
    public bool IsFixedWindowAvailable(uint start, uint length)
    {
        ulong end = (ulong)start + length;
        if (length == 0 || end > 0x1_0000_0000UL || start < Ram.Length) return false;
        foreach(var mapping in _mappings)
            if((ulong)start<(ulong)mapping.Start+mapping.Length&&mapping.Start<end)return false;
        return true;
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
