// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.CompilerServices;
namespace DOSEmulator.Core;

public sealed partial class MemoryBus
{
    /// <summary>Current one-page DRAM proof. A20 cannot fold an interior byte inside one 4-KiB page;
    /// decoded extent, every fixed mapping/hole and managed span bounds remain mandatory.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSinglePageRamSpan(uint address,int length,out Span<byte> bytes)
    {
        bytes=default;
        if((uint)(length-1)>=4096)return false;
        uint physical=Translate(address);
        if((physical&4095)+(uint)length>4096 || (ulong)physical+(uint)length>DecodedRamBytes || _mappedRamPages[physical>>12])return false;
        bytes=PhysicalRam.Slice((int)physical,length);return true;
    }
}
