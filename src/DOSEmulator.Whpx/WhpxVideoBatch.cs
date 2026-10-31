// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Whpx;

/// <summary>Replays a bounded, byte-verified flat 32-bit pair-of-pixels loop through the actual VGA bus.</summary>
internal sealed class WhpxVideoBatch
{
    private readonly nint _partition;
    private readonly MemoryBus _bus;
    private readonly bool _hasVideo;
    private static readonly uint[] Names = [0, 1, 2, 3, 5, 6, 7, 0x10, 0x11, 0x13, 0x15, 0x1c, 0x26,
        WhpxNative.InterruptState, WhpxNative.PendingInterruption, WhpxNative.PendingEvent];
    private readonly WhpxNative.RegisterValue[] _values = new WhpxNative.RegisterValue[Names.Length];
    private static readonly int[] Pattern = [0x8b,0xcd,0x81,0xc5,-1,-1,-1,-1,0x88,0x07,0xc1,0xe9,0x19,0x8b,0xd5,
        0x81,0xc5,-1,-1,-1,-1,0x88,0x5f,-1,0xc1,0xea,0x19,0x8a,0x04,0x0e,0x81,0xc7,-1,-1,-1,-1,
        0x8a,0x1c,0x16,0xff,0x0d,-1,-1,-1,-1,0x8a,0x00,0x8a,0x1b,0x75,0xcd];
    /// <summary>Derived diagnostic count of completed loop iterations, never an architectural retirement counter.</summary>
    public long Loops { get; private set; }
    /// <summary>Derived subset completed by the packed-word family; a restored cache starts cold.</summary>
    public long WordLoops { get; private set; }

    /// <summary>Retains only a real fixed VGA aperture, excluding opaque custom MMIO callbacks.</summary>
    internal WhpxVideoBatch(nint partition, MemoryBus bus)
    {
        _partition = partition; _bus = bus;
        _hasVideo = bus.PhysicalMappings.Any(m => m.Start == 0xa0000 && m.Length >= 0x20000 && m.Device is VgaAdapter or S3Trio64);
    }

    /// <summary>Executes every store, lookup, counter update and arithmetic effect; unsupported contexts retain the original native decoder path.</summary>
    internal bool TryRun(byte[] exit)
    {
        if(exit[48]>=4 && exit[52]==0x66 && exit[53]==0x89 && exit[54]==0x4a && exit[55]==0xfe)return TryRunGather(exit);
        if (exit[48] >= 3 && exit[52] == 0x66 && exit[53] == 0x89 && exit[54] == 0x17) return TryRunWords(exit);
        if (!_hasVideo || !_bus.HasExternalRam || !_bus.A20Enabled || exit[48] < 2 || exit[52] != 0x88 || exit[53] != 0x07 ||
            (BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) & 3) != 1) return false;
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, Names, (uint)Names.Length, _values), "read video batch context");
        var cs = _values[9]; var ds = _values[10]; ulong flags = _values[8].Low;
        if ((_values[11].Low & 0x80000001UL) != 1 || (flags & 0x30100) != 0 ||
            (_values[12].Low & 255) != 0 || (_values[13].Low & 3) != 0 ||
            (_values[14].Low & 1) != 0 || (_values[15].Low & 1) != 0 ||
            cs.Low != 0 || ds.Low != 0 || (uint)cs.High != uint.MaxValue || (uint)ds.High != uint.MaxValue ||
            ((cs.High >> 48) & 0xff) != 0x9b || ((cs.High >> 48) & 0x6000) != 0x4000 ||
            ((cs.High >> 32) & 3) != 0 || ((ds.High >> 48) & 0xff) != 0x93 || ((ds.High >> 32) & 3) != 0 ||
            _values.Take(8).Any(v => v.Low > uint.MaxValue)) return false;
        uint ip = (uint)_values[7].Low;
        if (ip < 8 || ip > uint.MaxValue - 43 || !_bus.TryGetReadOnlyPhysicalRamSpan(ip - 8, Pattern.Length, null, out var code)) return false;
        for (int i = 0; i < Pattern.Length; i++) if (Pattern[i] >= 0 && code[i] != Pattern[i]) return false;
        Span<byte> originalCode=stackalloc byte[51];code.CopyTo(originalCode);
        uint stepA = BinaryPrimitives.ReadUInt32LittleEndian(code[4..]), stepB = BinaryPrimitives.ReadUInt32LittleEndian(code[17..]);
        uint stride = BinaryPrimitives.ReadUInt32LittleEndian(code[32..]), counter = BinaryPrimitives.ReadUInt32LittleEndian(code[41..]);
        int displacement = (sbyte)code[23];
        if (stride is 0 or > 4096 || (ulong)counter < (ulong)ip + 43 && (ulong)counter + 4 > ip - 8 ||
            !_bus.TryGetReadOnlyPhysicalRamSpan(counter, 4, null, out _)) return false;
        uint a = (uint)_values[0].Low, c = (uint)_values[1].Low, d = (uint)_values[2].Low, b = (uint)_values[3].Low;
        uint bp = (uint)_values[4].Low, si = (uint)_values[5].Low, di = (uint)_values[6].Low;
        if (BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)) != di ||
            BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(32)) != ip) return false;
        uint nextIp = ip; int completed = 0;
        while (completed < 32)
        {
            if(!_bus.A20Enabled || !code.SequenceEqual(originalCode))break;
            uint currentC=completed==0?c:bp,currentBp=completed==0?bp:unchecked(bp+stepA);
            uint second = unchecked(di + (uint)displacement);
            if (di < 0xa0000 || di >= 0xc0000 || second < 0xa0000 || second >= 0xc0000 ||
                !_bus.TryGetReadOnlyPhysicalRamSpan(counter, 4, null, out var countBytes)) break;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
            if (count is 0 or > 4096) break;
            uint shiftedC = currentC >> 25, shiftedD = currentBp >> 25;
            uint lookupA = unchecked(si + shiftedC), lookupB = unchecked(si + shiftedD);
            if (!TryRam(lookupA, counter, out byte textureA) || !TryRam(lookupB, counter, out byte textureB)) break;
            uint lightA = (a & 0xffffff00) | textureA, lightB = (b & 0xffffff00) | textureB;
            if (!TryRam(lightA, counter, out byte pixelA) || !TryRam(lightB, counter, out byte pixelB)) break;

            _bus.WriteByte(di, (byte)a); _bus.WriteByte(second, (byte)b);
            c = shiftedC; d = shiftedD; bp = unchecked(currentBp + stepB);
            uint afterDi = unchecked(di + stride); bool carry = afterDi < di; di = afterDi;
            uint afterCount = count - 1; _bus.WriteDword(counter, afterCount);
            a = (lightA & 0xffffff00) | pixelA; b = (lightB & 0xffffff00) | pixelB;
            flags = DecFlags(count, afterCount, flags, carry); completed++; Loops++;
            nextIp = afterCount == 0 ? ip + 43 : ip - 8;
            if (afterCount == 0 || completed == 32) break;
        }
        if (completed == 0) return false;
        uint[] names = [0, 1, 2, 3, 5, 7, 0x10, 0x11];
        WhpxNative.RegisterValue[] values = [new(a),new(c),new(d),new(b),new(bp),new(di),new(nextIp),new(flags)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, (uint)names.Length, values), "commit video batch context");
        return true;
    }

    /// <summary>Performs a byte-verified two-source gather loop with a register counter, preserving full SI high bits and native DEC/ADD flags.</summary>
    private bool TryRunGather(byte[] exit)
    {
        if(!_hasVideo||!_bus.HasExternalRam||!_bus.A20Enabled||
            (BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68))&3)!=1)return false;
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition,0,Names,(uint)Names.Length,_values),"read gathered-pixel context");
        var cs=_values[9];var ds=_values[10];ulong flags=_values[8].Low;
        if((_values[11].Low&0x80000001UL)!=1||(flags&0x30100)!=0||(_values[12].Low&255)!=0||
            (_values[13].Low&3)!=0||(_values[14].Low&1)!=0||(_values[15].Low&1)!=0||
            cs.Low!=0||ds.Low!=0||(uint)cs.High!=uint.MaxValue||(uint)ds.High!=uint.MaxValue||
            ((cs.High>>48)&255)!=0x9b||((cs.High>>48)&0x6000)!=0x4000||((cs.High>>32)&3)!=0||
            ((ds.High>>48)&255)!=0x93||((ds.High>>32)&3)!=0||_values.Take(8).Any(v=>v.Low>uint.MaxValue))return false;
        uint ip=(uint)_values[7].Low;
        ReadOnlySpan<byte> pattern=[0x31,0xc9,0x8a,0x48,4,0x66,0x0f,0xb6,0x30,0xc1,0xe1,8,0x83,0xc2,2,0x01,0xf1,0x83,0xc0,8,0x66,0x89,0x4a,0xfe,0x4b,0x75,0xe5];
        if(ip<20||ip>uint.MaxValue-7||!_bus.TryGetReadOnlyPhysicalRamSpan(ip-20,27,null,out var code)||!code.SequenceEqual(pattern))return false;
        uint a=(uint)_values[0].Low,c=(uint)_values[1].Low,d=(uint)_values[2].Low,b=(uint)_values[3].Low,si=(uint)_values[5].Low;
        if(BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72))!=unchecked(d-2)||BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(32))!=ip)return false;
        uint nextIp=ip;int completed=0;
        while(completed<32)
        {
            if(!_bus.A20Enabled||!code.SequenceEqual(pattern)||b is 0 or >4096)break;
            uint ca=a,cc=c,cd=d,csi=si;bool carry=(flags&1)!=0;
            if(completed!=0)
            {
                if(a>uint.MaxValue-8||!_bus.TryGetReadOnlyPhysicalRamSpan(a,5,null,out var source))break;
                csi=(si&0xffff0000)|source[0];cc=unchecked(((uint)source[4]<<8)+csi);cd=unchecked(d+2);ca=unchecked(a+8);carry=ca<a;
            }
            uint destination=unchecked(cd-2);
            if(destination<0xa0000||destination>=0xbffff)break;
            _bus.WriteWord(destination,(ushort)cc);
            uint after=b-1;flags=DecFlags(b,after,flags,carry);a=ca;c=cc;d=cd;si=csi;b=after;
            completed++;Loops++;WordLoops++;nextIp=b==0?ip+7:ip-20;
            if(b==0)break;
        }
        if(completed==0)return false;
        uint[] names=[0,1,2,3,6,0x10,0x11];
        WhpxNative.RegisterValue[] values=[new(a),new(c),new(d),new(b),new(si),new(nextIp),new(flags)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition,0,names,(uint)names.Length,values),"commit gathered-pixel context");return true;
    }

    /// <summary>Executes a separately verified packed-coordinate two-pixel word loop through the real VGA word bus.</summary>
    private bool TryRunWords(byte[] exit)
    {
        if (!_hasVideo || !_bus.HasExternalRam || !_bus.A20Enabled ||
            (BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) & 3) != 1) return false;
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, Names, (uint)Names.Length, _values), "read word video batch context");
        var cs = _values[9]; var ds = _values[10]; ulong flags = _values[8].Low;
        if ((_values[11].Low & 0x80000001UL) != 1 || (flags & 0x30100) != 0 ||
            (_values[12].Low & 255) != 0 || (_values[13].Low & 3) != 0 ||
            (_values[14].Low & 1) != 0 || (_values[15].Low & 1) != 0 ||
            cs.Low != 0 || ds.Low != 0 || (uint)cs.High != uint.MaxValue || (uint)ds.High != uint.MaxValue ||
            ((cs.High >> 48) & 0xff) != 0x9b || ((cs.High >> 48) & 0x6000) != 0x4000 ||
            ((cs.High >> 32) & 3) != 0 || ((ds.High >> 48) & 0xff) != 0x93 || ((ds.High >> 32) & 3) != 0 ||
            _values.Take(8).Any(v => v.Low > uint.MaxValue)) return false;
        uint ip = (uint)_values[7].Low;
        if (ip < 22 || ip > uint.MaxValue - 42 || !_bus.TryGetReadOnlyPhysicalRamSpan(ip - 22, 64, null, out var code)) return false;
        ReadOnlySpan<int> pattern = [0x0f,0xa4,0xe9,0x16,0x8a,0x33,0x0f,0xa4,0xe9,6,0x81,0xc5,-1,-1,-1,-1,
            0x81,0xe1,0xff,0x0f,0,0,0x66,0x89,0x17,0x0f,0xa4,0xea,0x16,0x83,0xc7,2,0x0f,0xa4,0xea,6,
            0x81,0xc5,-1,-1,-1,-1,0x81,0xe2,0xff,0x0f,0,0,0x8a,4,0x0e,0x8a,0x1c,0x16,0xff,0x0d,-1,-1,-1,-1,0x8a,0x10,0x75,0xc0];
        for (int i = 0; i < pattern.Length; i++) if (pattern[i] >= 0 && code[i] != pattern[i]) return false;
        Span<byte> originalCode=stackalloc byte[64];code.CopyTo(originalCode);
        uint stepA = BinaryPrimitives.ReadUInt32LittleEndian(code[12..]), stepB = BinaryPrimitives.ReadUInt32LittleEndian(code[38..]);
        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(code[56..]);
        if ((ulong)counter < (ulong)ip + 42 && (ulong)counter + 4 > ip - 22 ||
            !_bus.TryGetReadOnlyPhysicalRamSpan(counter, 4, null, out _)) return false;
        uint a=(uint)_values[0].Low,c=(uint)_values[1].Low,d=(uint)_values[2].Low,b=(uint)_values[3].Low;
        uint bp=(uint)_values[4].Low,si=(uint)_values[5].Low,di=(uint)_values[6].Low;
        if (BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)) != di ||
            BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(32)) != ip) return false;
        uint nextIp=ip;int completed=0;
        while(completed<32)
        {
            if(!_bus.A20Enabled || !code.SequenceEqual(originalCode))break;
            uint currentC=completed==0?c:Tile(bp),currentBp=completed==0?bp:unchecked(bp+stepA),currentD=d;
            if(completed!=0)
            {
                if(!TryRam(b,counter,out byte high))break;
                currentD=(d&0xffff00ff)|((uint)high<<8);
            }
            if(di<0xa0000 || di>=0xbffff || !_bus.TryGetReadOnlyPhysicalRamSpan(counter,4,null,out var countBytes)) break;
            uint count=BinaryPrimitives.ReadUInt32LittleEndian(countBytes),index=Tile(currentBp);
            if(count is 0 or >4096 || !TryRam(unchecked(si+currentC),counter,out byte ta) || !TryRam(unchecked(si+index),counter,out byte tb)) break;
            uint lightA=(a&0xffffff00)|ta,lightB=(b&0xffffff00)|tb;
            if(!TryRam(lightA,counter,out byte pa) || !TryRam(lightB,counter,out _)) break;
            _bus.WriteWord(di,(ushort)currentD);di=unchecked(di+2);bp=unchecked(currentBp+stepB);c=currentC;
            a=lightA;b=lightB;d=(index&0xffffff00)|pa;
            uint after=count-1;_bus.WriteDword(counter,after);flags=DecFlags(count,after,flags,false);
            completed++;Loops++;WordLoops++;nextIp=after==0?ip+42:ip-22;
            if(after==0 || completed==32) break;
        }
        if(completed==0)return false;
        uint[] names=[0,1,2,3,5,7,0x10,0x11];
        WhpxNative.RegisterValue[] values=[new(a),new(c),new(d),new(b),new(bp),new(di),new(nextIp),new(flags)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition,0,names,(uint)names.Length,values),"commit word video batch context");
        return true;
    }

    /// <summary>Computes the exact low twelve bits of the two SHLD operations followed by AND 0FFFh.</summary>
    private static uint Tile(uint value) => ((value >> 4) & 0xfc0) | (value >> 26);

    /// <summary>Reads only ordinary lookup RAM which cannot alias the loop's actual counter update.</summary>
    private bool TryRam(uint address, uint counter, out byte value)
    {
        value = 0;
        if ((ulong)address >= counter && (ulong)address < (ulong)counter + 4 ||
            !_bus.TryGetReadOnlyPhysicalRamSpan(address, 1, null, out var bytes)) return false;
        value = bytes[0]; return true;
    }

    /// <summary>Commits defined DEC flags while retaining CF from the preceding actual ADD to EDI.</summary>
    private static ulong DecFlags(uint before, uint after, ulong flags, bool carry) =>
        (flags & ~0x8d5UL) | (carry ? 1UL : 0) | ((BitOperations.PopCount(after & 255) & 1) == 0 ? 4UL : 0) |
        (((before ^ after) & 16) != 0 ? 16UL : 0) | (after == 0 ? 64UL : 0) | ((after & 0x80000000) != 0 ? 128UL : 0) |
        (before == 0x80000000 ? 2048UL : 0);
}
