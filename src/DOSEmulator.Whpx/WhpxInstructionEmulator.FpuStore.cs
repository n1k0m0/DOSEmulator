// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Cpu.X86;

namespace DOSEmulator.Whpx;

internal sealed unsafe partial class WhpxInstructionEmulator
{
    /// <summary>Reuses a validated translation only within the same unretired instruction and 4-KiB page. Write/execute flags retain their separate checks;
    /// every physical byte and current code byte is still checked below, and crossing a page always invokes WHP again.</summary>
    private bool TryFpuPageByte(ulong linear,uint flags,ref ulong page,ref ulong physicalPage,out uint physical)
    {
        ulong currentPage=linear>>12;
        if(currentPage!=page)
        {
            int hr=WhpxNative.WHvTranslateGva(_partition,0,linear,flags,out ulong result,out ulong address);
            if(hr<0||result!=0||address>uint.MaxValue||(address&4095)!=(linear&4095)){physical=0;return false;}
            page=currentPage;physicalPage=address&~4095UL;
        }
        ulong translated=physicalPage+(linear&4095);physical=(uint)translated;return translated<=uint.MaxValue;
    }
    /// <summary>Completes only missing SIB/displacement bytes when a real WHP exit ends at a code-page boundary, using current executable guest RAM.</summary>
    private bool TryFpuCodeBytes(Span<byte> code,ref int available,int needed,ulong csBase,ulong rip,ulong limit,ref ulong page,ref ulong physicalPage)
    {
        if(needed>15||rip+(uint)needed-1>limit||csBase+rip+(uint)needed-1>uint.MaxValue)return false;
        for(int i=available;i<needed;i++)
        {
            if(!TryFpuPageByte(csBase+rip+(uint)i,0x14,ref page,ref physicalPage,out uint physical)||!_bus.IsPlainRamByte(physical))return false;
            code[i]=_bus.ReadByte(physical);
        }
        available=Math.Max(available,needed);return true;
    }
    /// <summary>Rounds normalized in-range raw x87 significands with integer guard/sticky/tie bits; exceptional ranges retain the exact general converter.</summary>
    private static Float80Encoding StoreFpuBinary(Float80Value value,int width,ushort control)
    {
        int precision=width==32?24:53,bias=width==32?127:1023,exponent=value.Exponent-16383;
        if(width is not (32 or 64)||!value.IntegerBit||value.Exponent==0||value.Exponent==0x7fff||exponent<1-bias||exponent>bias)
            return Float80Math.StoreBinary(value,width,control);
        int shift=64-precision;ulong fraction=value.Significand&((1UL<<shift)-1),significand=value.Significand>>shift,half=1UL<<(shift-1);
        bool up=fraction!=0&&(control>>10&3) switch{0=>fraction>half||fraction==half&&(significand&1)!=0,1=>value.Negative,2=>!value.Negative,_=>false};
        if(up&&++significand==(1UL<<precision)){significand>>=1;exponent++;}
        if(exponent>bias)return Float80Math.StoreBinary(value,width,control);
        ulong bits=(value.Negative?1UL<<(width-1):0)|(ulong)(exponent+bias)<<(precision-1)|significand&((1UL<<(precision-1))-1);
        return new(bits,fraction!=0?(ushort)32:(ushort)0,up);
    }
    /// <summary>Completes original Glide m32 setup and Motorhead m64 framebuffer FST/FSTP, with complete native-address preflight and raw x87 rounding.</summary>
    private bool TryCompleteProtectedFpuStore(byte[] exit)
    {
        if (exit.Length < 88 || exit[48] < 2 || (BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) & 3) != 1) return false;
        Span<byte> instruction=stackalloc byte[16];int available=Math.Min(16,(int)exit[48]);exit.AsSpan(52,available).CopyTo(instruction);
        ReadOnlySpan<byte> bytes = instruction[..available];
        int prefix = 0, segment = -1;
        while (prefix < bytes.Length && bytes[prefix] is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65)
        {
            segment = bytes[prefix++] switch { 0x26 => 18, 0x2e => 19, 0x36 => 20, 0x3e => 21, 0x64 => 22, _ => 23 };
        }
        if (prefix + 2 > bytes.Length || bytes[prefix] is not (0xd9 or 0xdd)) return false;
        int width=bytes[prefix]==0xdd?8:4;
        byte modrm = bytes[prefix + 1]; int mod = modrm >> 6, operation = modrm >> 3 & 7, rm = modrm & 7;
        if (mod == 3 || operation is not (2 or 3)) return false;
        uint* names = stackalloc uint[] {0,1,2,3,4,5,6,7,0x10,0x11,18,19,20,21,22,23,0x1c,0x26,
            0x1010,0x1011,0x1012,0x1013,0x1014,0x1015,0x1016,0x1017,0x1018,0x1019};
        WhpxNative.RegisterValue* values = stackalloc WhpxNative.RegisterValue[28];
        if(_usingFpuContinuation){for(int i=0;i<28;i++)values[i]=_fpuContinuationValues[i];}
        else WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition,0,names,28,values),"read native FST address and x87 registers");
        ulong rip = values[8].Low, flags = values[9].Low, cs = values[11].High;
        // Debug traps, 16-bit/V86/long-mode addressing and unmasked/pending x87 exceptions remain with the normal path.
        if ((values[16].Low & 13) != 1 || (flags & 0x20100) != 0 || (values[17].Low & 255) != 0 ||
            (cs >> 48 & 0x6098) != 0x4098 || rip > uint.MaxValue) return false;
        ulong control = values[26].Low; ushort cw = (ushort)control, sw = (ushort)(control >> 16); int top = sw >> 11 & 7;
        // On qualified WHP hosts the native masked-operation pointer/opcode image is zero. Preserve that image; do not invent another host policy.
        if ((cw & 63) != 63 || (sw & 0x80) != 0 || (control >> 32 & (1u << top)) == 0 ||
            (control >> 48) != 0 || values[26].High != 0 || values[27].Low != 0) return false;
        var st = values[18]; ushort exponent = (ushort)(st.High & 0x7fff);
        if ((st.High >> 16) != 0 || !(exponent > 0 && (st.Low >> 63) != 0 || exponent == 0 && st.Low == 0)) return false;
        int length = prefix + 2, baseReg = rm; uint offset = 0;ulong codePage=ulong.MaxValue,codePhysical=0;
        if (rm == 4)
        {
            if(!TryFpuCodeBytes(instruction,ref available,length+1,values[11].Low,rip,cs&uint.MaxValue,ref codePage,ref codePhysical))return false;
            bytes=instruction[..available];
            byte sib = bytes[length++]; baseReg = sib & 7; int index = sib >> 3 & 7;
            if (index != 4) offset = unchecked((uint)values[index].Low << (sib >> 6));
        }
        bool noBase = mod == 0 && baseReg == 5;
        if (!noBase) offset = unchecked(offset + (uint)values[baseReg].Low);
        int displacement = mod == 1 ? 1 : mod == 2 || noBase ? 4 : 0;
        if(!TryFpuCodeBytes(instruction,ref available,length+displacement,values[11].Low,rip,cs&uint.MaxValue,ref codePage,ref codePhysical))return false;
        bytes=instruction[..available];
        if (displacement == 1) offset = unchecked(offset + (uint)(sbyte)bytes[length]);
        else if (displacement == 4) offset = unchecked(offset + BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(length,4)));
        length += displacement;
        if (rip + (uint)length - 1 > (cs & uint.MaxValue)) return false;
        if (segment < 0) segment = !noBase && baseReg is 4 or 5 ? 20 : 21;
        var dataSegment = values[segment - 8]; // segment names 18..23 are contiguous at values[10..15].
        ulong attributes = dataSegment.High >> 48, limit = dataSegment.High & uint.MaxValue;
        int cpl = (int)(cs >> 32 & 3), rpl = (int)(dataSegment.High >> 32 & 3), dpl = (int)(attributes >> 5 & 3);
        if ((dataSegment.High >> 32 & 0xfffc) == 0 || (attributes & 0x409a) != 0x4092 || Math.Max(cpl,rpl) > dpl ||
            segment == 20 && (cpl != rpl || cpl != dpl) || offset > uint.MaxValue - (uint)(width-1) ||
            ((attributes & 4) == 0 ? (ulong)offset + (uint)width-1 > limit : offset <= limit) || dataSegment.Low + offset + (uint)width-1 > uint.MaxValue) return false;
        if(width==8&&cpl==3&&(values[16].Low&0x40000)!=0&&(flags&0x40000)!=0&&((dataSegment.Low+offset)&7)!=0)return false;
        uint physical = 0;ulong dataPage=ulong.MaxValue,dataPhysical=0;
        for (uint lane = 0; lane < width; lane++)
        {
            if(!TryFpuPageByte(dataSegment.Low+offset+lane,0x12,ref dataPage,ref dataPhysical,out uint translated))return false;
            if (lane == 0) physical = (uint)translated;
            if (translated != (ulong)physical + lane) return false;
        }
        if (width==4&&(physical & 3) != 0 || !_usingFpuContinuation&&physical != BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)) || _bus.IsPlainRamByte(physical)) return false;
        if(_usingFpuContinuation&&(width!=4||physical>>12!=_fpuContinuationPhysicalPage||!_bus.IsImmediateDwordWrite(physical)))return false;
        if(width==8&&!_bus.IsContiguousByteDeviceSpan(physical,width))return false;
        // Fetch the complete current instruction through execute translations before causing a single device effect.
        for (int i = 0; i < length; i++)
        {
            if(!TryFpuPageByte(values[11].Low+rip+(uint)i,0x14,ref codePage,ref codePhysical,out uint code)||
                !_bus.IsPlainRamByte(code)||_bus.ReadByte(code)!=bytes[i])return false;
        }
        var rounded = StoreFpuBinary(new(st.Low,(ushort)st.High),width*8,cw);
        if (!rounded.Commit) return false;
        ushort status = (ushort)((sw & ~0x200) | rounded.Exceptions | (rounded.RoundedUp ? 0x200 : 0));
        ulong next = control & ~(0xffffUL << 16) | (ulong)status << 16;
        uint* updatedNames = stackalloc uint[10]; WhpxNative.RegisterValue* updated = stackalloc WhpxNative.RegisterValue[10];
        updatedNames[0]=0x1018;updatedNames[1]=0x10;updated[0]=new(next,values[26].High);updated[1]=new(rip+(uint)length);uint updatedCount=2;
        if (operation == 3)
        {
            // WHP exposes logical ST0..ST7, while its abridged tag bits and TOP still name physical registers.
            next = next & ~(7UL << 27 | 1UL << (32+top)) | (ulong)((top+1)&7) << 27;
            updated[0] = new(next,values[26].High);
            for (int i = 0; i < 8; i++) { updatedNames[i+2]=(uint)(0x1010+i);updated[i+2]=values[18+(i+1)%8]; }
            updatedCount=10;
        }
        _bus.WriteDword(physical,(uint)rounded.Bits);
        if(width==8)_bus.WriteDword(physical+4,(uint)(rounded.Bits>>32));
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition,0,updatedNames,updatedCount,updated),"complete native FST raw x87 status and pop");
        RememberFpuContinuation(values,updatedNames,updated,updatedCount,bytes,length,width,operation,physical,dataSegment.Low+offset);
        return true;
    }

}
