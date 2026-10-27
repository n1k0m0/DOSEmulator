// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ExtractionTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool ok,string message="FXTRACT invariant") { if(!ok)throw new Exception(message); }
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte op,int group,uint address)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    {code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step();}
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    {m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address));}
    /// <summary>Builds and installs the controlled initial pixels required by the graphics regression.</summary>
    private static void Seed(X86Cpu c,MemoryBus m,ushort control,int kind)
    {
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);
        int cc=(kind&0xc0) switch{0x40=>0x100,0x80=>0x4000,0xc0=>0x4500,_=>0};
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|cc|((kind&32)!=0?0x200:0)|((kind&16)!=0?8:0)));
        At(c,m,Mem(0xd9,4,0x32000));
    }
    /// <summary>Initializes the controlled x87 stack shape and raw source operands used by the numeric regression.</summary>
    private static void Fill(X86Cpu c,MemoryBus m,Float80Value a,Float80Value b,int kind,ushort control)
    {
        for(int left=kind&15;left>0;left--)Load(c,m,left==1?a:b,left==1?0x30010u:0x30000u);
        Seed(c,m,control,kind);
        if((kind&0x100)!=0)At(c,m,[0xdd,0xc0]);if((kind&0x200)!=0)At(c,m,[0xdd,0xc7]);if((kind&0x400)!=0)At(c,m,[0xdd,0xc3]);
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
    /// <summary>Compares the independent reference-vector file with the actual numeric core/decoder results.</summary>
    public static int VerifyNativeVectors(string inputPath,string outputPath)
    {
        using var input=JsonDocument.Parse(File.ReadAllText(inputPath));using var reference=JsonDocument.Parse(File.ReadAllText(outputPath));
        var vs=input.RootElement.EnumerateArray().ToArray();var rs=reference.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Check(vs.Length==rs.Length);var failures=new List<object>();int checkedCases=0;
        for(int n=0;n<vs.Length;n++)try
        {
            var v=vs[n];var r=rs[n];Check(v.GetProperty("id").GetString()==r.GetProperty("id").GetString());
            var(c,m)=ProtectedModeTests.Rig([0x90]);var a=Value(v.GetProperty("a"));var b=Value(v.GetProperty("b"));int kind=v.GetProperty("kind").GetInt32();ushort cw=v.GetProperty("control").GetUInt16();
            Fill(c,m,a,b,kind,cw);bool normal=!c.IsFpuRegisterEmpty(0)&&c.IsFpuRegisterEmpty(7);int oldTop=c.FpuTop;
            c.Eflags=0x247;At(c,m,[0xd9,0xf4]);ushort sw=r.GetProperty("status").GetUInt16();
            Check((c.FpuStatusWord&~0x4500)==(sw&~0x4500),$"SW {c.FpuStatusWord:X4} vs {sw:X4}");
            Check(c.FpuControlWord==r.GetProperty("control").GetUInt16()&&c.FpuTagWord==r.GetProperty("tags").GetUInt16(),"CW/tags differ");
            var raw=r.GetProperty("registers").EnumerateArray().ToArray();
            for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
            Check(c.Eflags==0x247,"Changed EFLAGS");
            if(normal)
            {
                var pure=Float80Math.Extract(a,cw);
                Check((sw&63)==(pure.Exceptions|((kind&16)!=0?8:0)),"Pure flags differ");
                Check(c.FpuTop==(pure.Commit?(oldTop-1)&7:oldTop),"Pure commit/TOP differs");
                Check(c.GetFpuRegister(0)==(pure.Commit?pure.Significand:a),"Pure significand/abort differs");
                if(pure.Commit)Check(c.GetFpuRegister(1)==pure.Exponent,"Pure exponent differs");
            }
            checkedCases++;
        }
        catch(Exception ex){failures.Add(new{id=vs[n].GetProperty("id").GetString(),error=ex.Message});}
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vs.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 extraction regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(int power in new[]{-16382,-1,0,1,16383})
            test($"FXTRACT PC={pc}, RC={rc}, exponent={power} returns exact full-width pair",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j+1,(ushort)(power+16383));Load(c,m,a);Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),0xf0);c.Eflags=0x247;At(c,m,[0xd9,0xf4]);
                Check(c.GetFpuRegister(0)==new Float80Value(j+1,0x3fff)&&c.GetFpuRegister(1)==Float80Math.FromInteger(power));
                Check(c.FpuTop==6&&c.Eflags==0x247&&(c.FpuStatusWord&0x23f)==8&&!c.FpuErrorAsserted);
            });
        foreach(int depth in Enumerable.Range(0,9))foreach(int holes in new[]{0,0x100,0x200,0x300,0x400})foreach(bool im in new[]{false,true})
            test($"FXTRACT depth={depth}, holes={holes:X}, IM={im} preflights both results and prioritizes source underflow",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Fill(c,m,Float80Math.FromInteger(8),Float80Math.FromInteger(17),depth|holes|0x20,im?(ushort)0x37f:(ushort)0x37e);
                int oldTop=c.FpuTop;bool under=c.IsFpuRegisterEmpty(0),over=!c.IsFpuRegisterEmpty(7);ushort tw=c.FpuTagWord;
                var raw=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();At(c,m,[0xd9,0xf4]);
                if(under||over)
                {
                    Check((c.FpuStatusWord&0x241)==(under?0x41:0x241)&&c.FpuErrorAsserted==!im);
                    Check(c.FpuTop==(im?(oldTop-1)&7:oldTop));
                    if(im)Check(c.GetFpuRegister(0)==Float80Value.Indefinite&&c.GetFpuRegister(1)==Float80Value.Indefinite);
                    else{Check(c.FpuTagWord==tw);for(int k=0;k<8;k++)Check(c.GetFpuRegister(k)==raw[k]);}
                }
                else Check(c.FpuTop==((oldTop-1)&7)&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==Float80Math.FromInteger(3));
            });
        foreach(bool masked in new[]{false,true})foreach((Float80Value source,Float80Value sig,Float80Value exp,int flag) in new (Float80Value,Float80Value,Float80Value,int)[]{
            (new(0,0x8000),new(0,0x8000),new(j,0xffff),4),
            (new(1,0),Float80Value.One,Float80Math.FromInteger(-16445),2),
            (new(j+1,0),new(j+1,0x3fff),Float80Math.FromInteger(-16382),2),
            (new(j,0xffff),new(j,0xffff),new(j,0x7fff),0),
            (new(j|0x123,0x7fff),new(j|0x4000000000000123,0x7fff),new(j|0x4000000000000123,0x7fff),1),
            (new(j|0x4000000000000123,0xffff),new(j|0x4000000000000123,0xffff),new(j|0x4000000000000123,0xffff),0),
            (new(1,1),Float80Value.Indefinite,Float80Value.Indefinite,1)})
            test($"FXTRACT source={source}, masked={masked} separates special responses from push commit",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,source);Seed(c,m,(ushort)(0x37f&(masked?0xffff:~flag)),0x20);At(c,m,[0xd9,0xf4]);bool commit=masked||flag==0;
                Check(c.FpuTop==(commit?6:7)&&c.GetFpuRegister(0)==(commit?sig:source));if(commit)Check(c.GetFpuRegister(1)==exp);
                Check((c.FpuStatusWord&0x23f)==flag&&c.FpuErrorAsserted==(!masked&&flag!=0));
            });
        foreach(int bit in new[]{0,1,31,62,63})foreach(bool negative in new[]{false,true})
            test($"FXTRACT denormal leading bit={bit}, negative={negative} normalizes with an exact exponent",()=>
            {
                var a=new Float80Value(1ul<<bit,negative?(ushort)0x8000:(ushort)0);var x=Float80Math.Extract(a,0x37f);
                Check(x.Significand==new Float80Value(j,(ushort)(0x3fff|(negative?0x8000:0)))&&x.Exponent==Float80Math.FromInteger(bit-16445)&&x.Exceptions==2&&x.Commit);
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FXTRACT prefix {Convert.ToHexString(prefix)} preserves all six lower registers and has a lossless FSCALE round trip",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=6;i>=1;i--)Load(c,m,Float80Math.FromInteger(100+i));var a=new Float80Value(j+3,0xbffe);Load(c,m,a);
                var before=Enumerable.Range(1,6).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,0xf4]);Check(c.FpuTop==0);
                for(int i=0;i<6;i++)Check(c.GetFpuRegister(i+2)==before[i]);At(c,m,[0xd9,0xfd]);At(c,m,[0xdd,0xd9]);
                Check(c.FpuTop==1&&c.GetFpuRegister(0)==a);for(int i=0;i<6;i++)Check(c.GetFpuRegister(i+1)==before[i]);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FXTRACT EM/TS={bits} gives actual NM before state or pointer changes",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(3));uint ip=c.FpuInstructionPointer;ushort tw=c.FpuTagWord;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,0xf4}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip&&c.FpuTagWord==tw&&c.GetFpuRegister(0)==Float80Math.FromInteger(3));
            });
        foreach((Float80Value a,ushort control,int flags) in new (Float80Value,ushort,int)[]{(Float80Value.Zero,0x37b,4),(new(1,0),0x37d,2),(new(j|1,0x7fff),0x37e,1)})
            test($"FXTRACT pending flags={flags} actually faults WAIT then FNCLEX/IRETD repeats without a partial push",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,a);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,0xf4]);
                Check(c.FpuTop==7&&c.GetFpuRegister(0)==a&&c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags);
                m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
                Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.FpuTop==7&&c.GetFpuRegister(0)==a);
            });
        test("FXTRACT split opcode PF preserves the source then retries with exactly one push",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,new(j+1,0x4003));uint ip=c.FpuInstructionPointer;
            m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xf4);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
            Check(c.FpuInstructionPointer==ip&&c.FpuTop==7&&c.GetFpuRegister(0)==new Float80Value(j+1,0x4003));
            PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
            Check(c.Eip==0x3001&&c.FpuTop==6&&c.GetFpuRegister(0)==new Float80Value(j+1,0x3fff)&&c.GetFpuRegister(1)==Float80Math.FromInteger(4));
        });
        test("FXTRACT TF trap observes both committed outputs after the instruction",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j+1,0x4003));c.Eflags|=0x100;new byte[]{0xd9,0xf4}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
            Check(c.Eip==0x6000&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002&&c.FpuTop==6&&c.GetFpuRegister(0)==new Float80Value(j+1,0x3fff)&&c.GetFpuRegister(1)==Float80Math.FromInteger(4));
        });
        test("LOCK FXTRACT gives UD without any FPU state or pointer change",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(3));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;
            new byte[]{0xf0,0xd9,0xf4}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw&&c.GetFpuRegister(0)==Float80Math.FromInteger(3));
        });
    }
}
