// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter : ISimpleByteWriteDevice
{
    /// <summary>Admits only one current simple mode-0 plane; latches, modes, raster operations and MMIO retain their original paths.</summary>
    public bool TryPlanSimpleByteWrite(uint offset,out SimpleByteWritePlan plan)
    {
        plan=default;
        if(IsTrio64 && TrioEngineMmio || !Aperture(offset,out int address) ||
            IsEt4000 && (_crtc[0x36]&0x10)!=0 || (_gc[5]&3)!=0 || _gc[1]!=0 || _gc[3]!=0 || _gc[8]!=255)return false;
        int planes=_seq[2]&15;
        if((_seq[4]&8)!=0){planes&=1<<(address&3);address>>=2;}
        else if((_seq[4]&4)==0){planes&=(address&1)==0?5:10;address>>=1;}
        if(!BitOperations.IsPow2((uint)planes))return false;
        address=BankAddress(address,true);int plane=BitOperations.TrailingZeroCount((uint)planes);
        if((uint)address>=(uint)_planes[plane].Length)return false;
        plan=new(_planes[plane].AsSpan(address,1),MemoryMarshal.CreateSpan(ref _pixelRevision,1));return true;
    }
}

public sealed partial class S3Trio64 : ISimpleByteWriteDevice
{
    /// <summary>Retains actual PCI/wake/MMIO gates before offering the canonical VGA backing plane.</summary>
    public bool TryPlanSimpleByteWrite(uint offset,out SimpleByteWritePlan plan)
    {plan=default;return MemorySpaceEnabled && _video.TrioVideoEnabled && _video.TryPlanSimpleByteWrite(offset,out plan);}
}
