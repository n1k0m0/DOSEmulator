// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
namespace DOSEmulator.Core;

/// <summary>Explicit owner-only capability for one ordinary byte store with only storage/revision effects.</summary>
public interface ISimpleByteWriteDevice : IMemoryDevice
{
    /// <summary>Proves current decode and exact simple-write semantics without device reads, writes or callbacks.</summary>
    bool TryPlanSimpleByteWrite(uint offset,out SimpleByteWritePlan plan);
}

/// <summary>Stack-scoped exact target; no retained plan, scheduler action, host endpoint or arbitrary delegate.</summary>
public readonly ref struct SimpleByteWritePlan
{
    private readonly Span<byte> target;
    private readonly Span<long> revision;
    /// <summary>Binds one validated backing byte and its exact storage revision counter for this owner operation only.</summary>
    public SimpleByteWritePlan(Span<byte> target,Span<long> revision){this.target=target;this.revision=revision;}
    /// <summary>Whether both fixed-size backing ranges required for a single store were provided.</summary>
    public bool Valid => target.Length==1 && revision.Length==1;
    /// <summary>Rejects every physical backing alias of the retained code page, even through a cooperating mapping.</summary>
    public bool Aliases(ReadOnlySpan<byte> bytes) => ((ReadOnlySpan<byte>)target).Overlaps(bytes) ||
        ((ReadOnlySpan<byte>)MemoryMarshal.AsBytes(revision)).Overlaps(bytes);
    /// <summary>Matches StorePlane's equality test and one unchecked revision increment for each changed byte.</summary>
    public void Store(byte value)
    {if(target[0]==value)return;target[0]=value;revision[0]=unchecked(revision[0]+1);}
}

public sealed partial class MemoryBus
{
    /// <summary>Uses the actual current fixed mapping priority after A20 translation; arbitrary devices remain on the byte bus.</summary>
    public bool TryPlanSimpleByteWrite(uint address,out SimpleByteWritePlan plan)
    {
        plan=default;address=Translate(address);
        if(address<DecodedRamBytes && !_mappedRamPages[address>>12])return false;
        if(address<Ram.Length && _devicePages[address>>12] is {} page)
            return page.Device is ISimpleByteWriteDevice device && device.TryPlanSimpleByteWrite(address-page.Start,out plan);
        foreach(var map in _mappings)
            if(address>=map.Start && (ulong)address<(ulong)map.Start+map.Length)
                return map.Device is ISimpleByteWriteDevice device && device.TryPlanSimpleByteWrite(address-map.Start,out plan);
        return false;
    }
}
