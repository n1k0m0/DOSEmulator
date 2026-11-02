// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

namespace DOSEmulator.Whpx;

/// <summary>Executes bounded original-style gradient instructions while WHP is stopped; every arithmetic operation and isolated SST write remains real.</summary>
internal sealed unsafe class WhpxGlideBatch(nint partition,MemoryBus bus,Func<ulong> observeClock)
{
    private static readonly uint[] Names=[0,1,2,3,4,5,6,7,0x10,0x11,19,21,0x1c,0x26,
        WhpxNative.InterruptState,WhpxNative.PendingInterruption,WhpxNative.PendingEvent,
        0x1010,0x1011,0x1012,0x1013,0x1014,0x1015,0x1016,0x1017,0x1018,0x1019];
    private readonly WhpxNative.RegisterValue[] _values=new WhpxNative.RegisterValue[Names.Length];
    /// <summary>Derived last committed context, seeded at each stopped entry and never serialized.</summary>
    private readonly WhpxNative.RegisterValue[] _committedValues=new WhpxNative.RegisterValue[Names.Length];
    private readonly ulong[] _pages=new ulong[12],_physicalPages=new ulong[12];
    private readonly uint[] _pageFlags=new uint[12];
    private int _pageCount;
    private uint _dataLimit;
    private bool _dataExpandDown;
    /// <summary>Derived counts of actual interpreted instructions and parameter writes, not general native retirement counters.</summary>
    internal long Instructions {get;private set;}
    /// <summary>Counts every real isolated parameter DWORD performed by this helper.</summary>
    internal long Stores {get;private set;}
    /// <summary>Counts committed boundaries at which the owner serviced due devices or stopped this block.</summary>
    internal long BoundaryYields {get;private set;}
    /// <summary>Counts actual register-set API calls, excluding repeated identical commits.</summary>
    internal long RegisterCommitCalls {get;private set;}
    /// <summary>Counts actual register values, preserving all nine raw x87 values whenever that group changes.</summary>
    internal long RegistersCommitted {get;private set;}
    /// <summary>The trusted stopped owner supplies a derived RAM/firmware/GPA mutation epoch; absent observers always clear translations.</summary>
    internal Func<ulong>? ReadBoundaryMemoryVersion {get;set;}
    /// <summary>Private control for independently comparing the conservative clearing policy on the same implementation.</summary>
    internal bool RetainReadOnlyBoundaryTranslations {get;set;}=true;
    /// <summary>Counts actual native permission-specific page-walk calls, not guest memory operations.</summary>
    internal long TranslationQueries {get;private set;}
    /// <summary>Counts committed serviced boundaries with a proven unchanged memory epoch inside this stopped block.</summary>
    internal long ReadOnlyBoundaryKeeps {get;private set;}

    /// <summary>Reuses permission-specific translations only inside this stopped block, clearing them after every observed memory mutation.</summary>
    private bool Translate(uint linear,uint flags,out uint physical)
    {
        ulong page=(ulong)linear>>12;
        for(int i=0;i<_pageCount;i++)if(_pages[i]==page&&_pageFlags[i]==flags)
        {physical=(uint)(_physicalPages[i]+(linear&4095));return true;}
        TranslationQueries++;
        int hr=WhpxNative.WHvTranslateGva(partition,0,linear,flags,out ulong result,out ulong translated);
        if(hr<0||result!=0||translated>uint.MaxValue||(translated&4095)!=(linear&4095)){physical=0;return false;}
        physical=(uint)translated;
        if(_pageCount<12){_pages[_pageCount]=page;_pageFlags[_pageCount]=flags;_physicalPages[_pageCount++]=translated&~4095UL;}
        return true;
    }
    /// <summary>Reads only ordinary current RAM, with independent permission translations across every page boundary.</summary>
    private bool Read(uint address,Span<byte> bytes,uint flags=0x11)
    {
        if((ulong)address+(uint)bytes.Length>0x1_0000_0000UL)return false;
        int copied=0;
        while(copied<bytes.Length)
        {
            uint current=address+(uint)copied;int count=Math.Min(bytes.Length-copied,4096-(int)(current&4095));
            if(!Translate(current,flags,out uint physical)||!bus.TryGetReadOnlyPhysicalRamSpan(physical,count,null,out var source))return false;
            source.CopyTo(bytes.Slice(copied,count));copied+=count;
        }
        return true;
    }
    /// <summary>Reads a complete plain-RAM DWORD without exposing MMIO reads or partial host device effects.</summary>
    private bool ReadDword(uint address,out uint value)
    {
        Span<byte> bytes=stackalloc byte[4];bool ok=DataExtent(address)&&Read(address,bytes);value=ok?BinaryPrimitives.ReadUInt32LittleEndian(bytes):0;return ok;
    }
    /// <summary>Preserves the actual hidden normal/expand-down DS limit before any complete 32-bit operand access.</summary>
    private bool DataExtent(uint address)=>address<=uint.MaxValue-3&&(_dataExpandDown?address>_dataLimit:address+3<=_dataLimit);
    /// <summary>Admits only an aligned complete parameter latch, excluding commands, memory writes, uploads, palettes and opaque devices.</summary>
    private bool WriteAddress(uint address,out uint physical)
    {
        physical=0;
        if(!DataExtent(address)||(address&3)!=0||!Translate(address,0x12,out uint first)||!bus.IsImmediateDwordWrite(first))return false;
        // Four-byte alignment keeps all lanes inside this same translated 4-KiB page; the bus query checked every actual peer/mapping lane.
        physical=first;return true;
    }
    /// <summary>Retains finite raw extended operands; unsupported encodings and special x87 cases return to actual native execution.</summary>
    private static bool Finite(WhpxNative.RegisterValue value)=>value.High<=0xffff&&
        ((value.High&0x7fff)==0?value.Low==0:(value.High&0x7fff)!=0x7fff&&(value.Low>>63)!=0);
    /// <summary>Computes defined arithmetic flags and the actual host's qualified zero AF for TEST.</summary>
    private static ulong LogicFlags(ulong flags,uint result)=>(flags&~0x8d5UL)|
        ((BitOperations.PopCount(result&255)&1)==0?4UL:0)|(result==0?64UL:0)|((result&0x80000000)!=0?128UL:0);
    /// <summary>Computes all native ADD flags without changing unrelated architectural flag bits.</summary>
    private static ulong AddFlags(ulong flags,uint before,uint add,uint result)=>LogicFlags(flags,result)|
        (result<before?1UL:0)|(((before^add^result)&16)!=0?16UL:0)|((~(before^add)&(before^result)&0x80000000)!=0?2048UL:0);

    /// <summary>Checks each live instruction, permission and clock boundary; no translation survives a native run, observed memory mutation or snapshot.</summary>
    internal bool TryRun(byte[] exit,ulong stopAt,Func<ulong>? serviceBoundary=null)
    {
        bool gradient=exit[48]>=2&&exit[52]==0x89&&exit[53]==0x2f;
        bool vertex=exit[48]>=6&&exit[52]==0x89&&exit[53]==0x97&&BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(54))==0x88;
        bool zeroEntry=exit[48]>=6&&exit[52]==0xc7&&exit[53]==7&&BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(54))==0;
        if(!bus.HasExternalRam||!bus.A20Enabled||!(gradient||vertex||zeroEntry)||(BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68))&3)!=1)return false;
        // Opaque peers and disabled private controls keep the ordinary scalar path without paying native context/code-translation preflight first.
        ulong exitedAddress=BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72));
        if(exitedAddress>uint.MaxValue||!bus.IsImmediateDwordWrite((uint)exitedAddress))return false;
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(partition,0,Names,(uint)Names.Length,_values),"read isolated gradient context");
        _values.CopyTo(_committedValues,0);
        var v=_values;var cs=v[10];var ds=v[11];ulong flags=v[9].Low,control=v[25].Low;
        if((v[12].Low&13)!=1||(flags&0x30100)!=0||(v[13].Low&255)!=0||(v[14].Low&3)!=0||
            (v[15].Low&1)!=0||(v[16].Low&1)!=0||cs.Low!=0||ds.Low!=0||(uint)cs.High!=uint.MaxValue||
            ((cs.High>>48)&0x6098)!=0x4098||((ds.High>>48)&0x409a)!=0x4092||
            (ds.High>>32&0xfffc)==0||Math.Max((int)(cs.High>>32&3),(int)(ds.High>>32&3))>(int)(ds.High>>53&3)||
            (control&63)!=63||(control>>8&3)==1||(control>>16&0x80)!=0||control>>48!=0||v[25].High!=0||v[26].Low!=0||
            v[8].Low>uint.MaxValue||((cs.High>>32&3)==3&&(v[12].Low&flags&0x40000)!=0))return false;
        for(int i=0;i<8;i++)if(v[i].Low>uint.MaxValue)return false;
        _dataLimit=(uint)ds.High;_dataExpandDown=(ds.High>>50&1)!=0;
        uint start=(uint)v[8].Low;
        if(BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(32))!=start)return false;
        _pageCount=0;
        // Opcode/operand forms, not a guest address or API signature. Unknown code retains scalar/native execution.
        ReadOnlySpan<int> pattern=gradient?[0x89,0x2f,0xd8,0x0d,-1,-1,-1,-1,0xd9,0xca,0xd8,0x0d,-1,-1,-1,-1,
            0xd9,0xcb,0xde,0xe9,0xd9,0xca,0xde,0xe1,0xd9,0xc9,0x8b,0x56,8,0x83,0xc6,8,0xd9,0x5f,4,0xd9,0x5f,8,0x85,0xd2,0x0f,0x85]:
            vertex?[0x89,0x97,0x88,0,0,0,0x89,0xaf,0x8c,0,0,0,0x8b,0x13,0x8b,0x6b,4,0x89,0x97,0x90,0,0,0,
                0x89,0xaf,0x94,0,0,0,0x8b,0x11,0x8b,0x69,4,0x89,0x97,0x98,0,0,0,0x89,0xaf,0x9c,0,0,0]:
                [0xc7,7,0,0,0,0,0x74,6,0x87,5,-1,-1,-1,-1,0x8b,0x56,8];
        Span<byte> initial=stackalloc byte[46];
        if((start&4095)>4096-pattern.Length||!Read(start,initial[..pattern.Length],0x14))return false;
        for(int i=0;i<pattern.Length;i++)if(pattern[i]>=0&&initial[i]!=pattern[i])return false;
        uint firstAddress=unchecked((uint)v[7].Low+(vertex?0x88u:0));
        if(!WriteAddress(firstAddress,out uint target)||target!=BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)))return false;
        Span<byte> code=stackalloc byte[15],again=stackalloc byte[15];
        uint ip=start;ushort cw=(ushort)control,sw=(ushort)(control>>16);byte tags=(byte)(control>>32);
        int completed=0;
        while(completed<256)
        {
            if(completed!=0&&observeClock()>=stopAt)
            {
                // Devices observe a committed native instruction boundary. A reset/PIC/deadline stops this block without restoring stale registers.
                ulong memoryVersion=ReadBoundaryMemoryVersion?.Invoke()??0;
                Commit(v,ip,flags,control,sw,tags);BoundaryYields++;
                if(serviceBoundary is null||(stopAt=serviceBoundary())==0)return true;
                if(!RetainReadOnlyBoundaryTranslations||ReadBoundaryMemoryVersion is null||ReadBoundaryMemoryVersion()!=memoryVersion)_pageCount=0;
                else ReadOnlyBoundaryKeeps++;
                if(!bus.HasExternalRam||!bus.A20Enabled)return true;
            }
            // Branches stay in the same bounded code page. No unsupported instruction is retired on rejection.
            if((ip>>12)!=(start>>12)||(ip&4095)>4081||ip+15UL>0x1_0000_0000UL||ip<(long)start-128||ip>start+128UL||!Read(ip,code,0x14))break;
            int length=0,destination=-1,fpu=-1,exchange=-1,push=-1,pop=-1;
            uint integer=0,write=0,physical=0;bool store=false,branch=false;ulong nextFlags=flags;
            uint nextIp=ip;Float80Result numeric=default;Float80Encoding encoded=default;
            int top=sw>>11&7;
            // TOP maps the abridged physical tag to a finite current logical raw register.
            bool Occupied(int logical)=>(tags&(1<<((top+logical)&7)))!=0&&Finite(v[17+logical]);
            if(code[0]==0x89&&code[1]==0x2f)
            {length=2;write=(uint)v[5].Low;store=true;if(!WriteAddress((uint)v[7].Low,out physical))break;}
            else if(code[0]==0x89&&code[1] is 0x97 or 0xaf)
            {
                length=6;write=(uint)v[code[1]==0x97?2:5].Low;store=true;
                if(!WriteAddress(unchecked((uint)v[7].Low+BinaryPrimitives.ReadUInt32LittleEndian(code[2..])),out physical))break;
            }
            else if(code[0]==0xc7&&code[1]==7)
            {length=6;write=BinaryPrimitives.ReadUInt32LittleEndian(code[2..]);store=true;if(!WriteAddress((uint)v[7].Low,out physical))break;}
            else if(code[0]==0x8b&&code[1] is 0x10 or 0x11 or 0x13)
            {length=2;destination=2;if(!ReadDword((uint)v[code[1]&7].Low,out integer))break;}
            else if(code[0]==0x8b&&code[1] is 0x68 or 0x69 or 0x6b)
            {length=3;destination=5;if(!ReadDword(unchecked((uint)v[code[1]&7].Low+(uint)(sbyte)code[2]),out integer))break;}
            else if(code[0]==0x8b&&code[1] is 0x7e or 0x56)
            {length=3;destination=code[1]==0x7e?7:2;if(!ReadDword(unchecked((uint)v[6].Low+(uint)(sbyte)code[2]),out integer))break;}
            else if(code[0]==0x8b&&code[1]==0x2c&&code[2]==2)
            {length=3;destination=5;if(!ReadDword(unchecked((uint)v[2].Low+(uint)v[0].Low),out integer))break;}
            else if(code[0]==0x85&&code[1]==0xd2)
            {length=2;nextFlags=LogicFlags(flags,(uint)v[2].Low);}
            else if(code[0]==0xf7&&code[1]==0xc2)
            {length=6;nextFlags=LogicFlags(flags,(uint)v[2].Low&BinaryPrimitives.ReadUInt32LittleEndian(code[2..]));}
            else if(code[0]==0x83&&code[1]==0xc6)
            {length=3;destination=6;uint add=unchecked((uint)(sbyte)code[2]);integer=unchecked((uint)v[6].Low+add);nextFlags=AddFlags(flags,(uint)v[6].Low,add,integer);}
            else if(code[0] is 0x74 or 0x75)
            {length=2;branch=true;bool zero=(flags&64)!=0;nextIp=unchecked(ip+2+(uint)((code[0]==0x74?zero:!zero)?(sbyte)code[1]:0));}
            else if(code[0]==0x0f&&code[1] is 0x84 or 0x85)
            {length=6;branch=true;bool take=(flags&64)==(code[1]==0x84?64UL:0);nextIp=unchecked(ip+6+(take?BinaryPrimitives.ReadUInt32LittleEndian(code[2..]):0));}
            else if(code[0]==0xd9&&code[1] is >=0xc8 and <=0xcb)
            {length=2;exchange=code[1]-0xc8;if(!Occupied(0)||!Occupied(exchange))break;}
            else if(code[0]==0xd9&&code[1]==0xc1)
            {length=2;push=1;if(!Occupied(1)||(tags&(1<<((top+7)&7)))!=0)break;numeric=new(new(v[18].Low,(ushort)v[18].High));}
            else if(code[0]==0xd9&&code[1]==4&&code[2] is 2 or 0x1a)
            {
                length=3;push=0;if((tags&(1<<((top+7)&7)))!=0||!ReadDword(unchecked((uint)v[2].Low+(uint)v[code[2]==2?0:3].Low),out uint bits)||
                    (bits&0x7f800000)==0x7f800000||(bits&0x7f800000)==0&&(bits&0x7fffff)!=0)break;
                numeric=Float80Math.LoadBinary(bits,32,cw);
            }
            else if(code[0]==0xd8&&(code[1]==0x0d||code[1]==0x24&&code[2] is 0x1a or 0x0a))
            {
                if(!Occupied(0))break;uint address;
                if(code[1]==0x0d){length=6;address=BinaryPrimitives.ReadUInt32LittleEndian(code[2..]);}
                else{length=3;address=unchecked((uint)v[2].Low+(uint)v[code[2]==0x1a?3:1].Low);}
                if(!ReadDword(address,out uint bits)||(bits&0x7f800000)==0x7f800000||(bits&0x7f800000)==0&&(bits&0x7fffff)!=0)break;
                fpu=0;numeric=Float80Math.CalculateBinaryMemory(new(v[17].Low,(ushort)v[17].High),bits,32,
                    code[1]==0x0d?Float80Operation.Multiply:Float80Operation.Subtract,cw);
            }
            else if(code[0]==0xde&&code[1] is 0xe9 or 0xe1)
            {
                length=2;if(!Occupied(0)||!Occupied(1))break;fpu=1;pop=1;
                var a=new Float80Value(v[18].Low,(ushort)v[18].High);var b=new Float80Value(v[17].Low,(ushort)v[17].High);
                numeric=code[1]==0xe9?Float80Math.Calculate(a,b,Float80Operation.Subtract,cw):Float80Math.Calculate(b,a,Float80Operation.Subtract,cw);
            }
            else if(code[0]==0xd9&&code[1]==0x5f)
            {
                length=3;if(!Occupied(0)||!WriteAddress(unchecked((uint)v[7].Low+(uint)(sbyte)code[2]),out physical))break;
                encoded=Float80Math.StoreBinary(new(v[17].Low,(ushort)v[17].High),32,cw);
                if(!encoded.Commit||(encoded.Exceptions&31)!=0)break;store=true;write=(uint)encoded.Bits;pop=0;
            }
            else break;
            if((fpu>=0||push>=0)&&(!numeric.Commit||(numeric.Exceptions&31)!=0||!Finite(new(numeric.Value.Significand,numeric.Value.SignExponent))))break;
            // Translation may set page-table A/D bits in aliased RAM. Recheck every instruction byte after all data preflights.
            if(!Read(ip,again[..length],0x14)||!code[..length].SequenceEqual(again[..length]))break;
            if(store){bus.WriteDword(physical,write);Stores++;}
            if(destination>=0)v[destination]=new(integer);
            if(exchange>=0){(v[17],v[17+exchange])=(v[17+exchange],v[17]);sw=(ushort)(sw&~0x200);}
            if(fpu>=0){v[17+fpu]=new(numeric.Value.Significand,numeric.Value.SignExponent);sw=(ushort)((sw&~0x200)|numeric.Exceptions|(numeric.RoundedUp?0x200:0));}
            if(push>=0)
            {
                for(int i=7;i>0;i--)v[17+i]=v[16+i];v[17]=new(numeric.Value.Significand,numeric.Value.SignExponent);
                top=(top+7)&7;tags|=(byte)(1<<top);sw=(ushort)((sw&~0x3a00)|(top<<11)|numeric.Exceptions);
            }
            if(pop>=0)
            {
                if(pop==0)sw=(ushort)((sw&~0x200)|encoded.Exceptions|(encoded.RoundedUp?0x200:0));
                var first=v[17];for(int i=0;i<7;i++)v[17+i]=v[18+i];v[24]=first;
                tags&=(byte)~(1<<top);top=(top+1)&7;sw=(ushort)((sw&~0x3800)|(top<<11));
            }
            flags=nextFlags;ip=branch?nextIp:ip+(uint)length;completed++;Instructions++;
        }
        if(completed==0)return false;
        Commit(v,ip,flags,control,sw,tags);return true;
    }
    /// <summary>Publishes complete partial integer/raw x87 progress before a device event or return, using the qualified aligned native ABI.</summary>
    private void Commit(WhpxNative.RegisterValue[] v,uint ip,ulong flags,ulong control,ushort sw,byte tags)
    {
        v[8]=new(ip);v[9]=new(flags);v[25]=new((control&~(0xffffffUL<<16))|(ulong)sw<<16|(ulong)tags<<32,v[25].High);
        // WHP's SIMD register-value ABI requires 16-byte alignment; a pinned CLR array need not have that alignment.
        bool floatingChanged=false;
        for(int i=17;i<=25;i++)if(v[i].Low!=_committedValues[i].Low||v[i].High!=_committedValues[i].High){floatingChanged=true;break;}
        uint* updatedNames=stackalloc uint[19];WhpxNative.RegisterValue* updated=stackalloc WhpxNative.RegisterValue[19];uint count=0;
        for(int i=0;i<19;i++)
        {
            int source=i<10?i:i+7;
            if(source>=17?floatingChanged:v[source].Low!=_committedValues[source].Low||v[source].High!=_committedValues[source].High)
            {updatedNames[count]=Names[source];updated[count++]=v[source];}
        }
        if(count==0)return;
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(partition,0,updatedNames,count,updated),"commit changed integer and complete changed raw x87 context");
        v.CopyTo(_committedValues,0);RegisterCommitCalls++;RegistersCommitted+=count;
    }
}
