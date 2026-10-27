// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87TangentTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool v,string detail="FPTAN invariant") { if(!v)throw new Exception(detail); }
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    { code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step(); }
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte op,int group,uint address=0x31000)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    { m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address)); }
    /// <summary>Builds and installs the controlled initial pixels required by the graphics regression.</summary>
    private static void Seed(X86Cpu c,MemoryBus m,ushort cw,int kind)
    {
        if((kind&32)!=0){At(c,m,[0xd9,0xee]);At(c,m,[0xd9,0xf7]);}
        int cc=(kind&12) switch {4=>0,8=>0x100,12=>0x4000,_=>0x4500};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,cw);
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x200|cc|((kind&16)!=0?4:0)));
        At(c,m,Mem(0xd9,4,0x32000));
        if((kind&1)!=0)At(c,m,[0xdd,0xc0]);if((kind&2)!=0)At(c,m,[0xdd,0xc1]);
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
    /// <summary>Checks the actual numeric core/decoder against independently certified mathematical intervals.</summary>
    public static int VerifyMathVectors(string inputPath,string expectedPath)
    {
        using var input=JsonDocument.Parse(File.ReadAllText(inputPath));using var expected=JsonDocument.Parse(File.ReadAllText(expectedPath));
        var vs=input.RootElement.EnumerateArray().ToArray();var es=expected.RootElement.GetProperty("results").EnumerateArray().ToArray();Check(vs.Length==es.Length);
        var failures=new List<object>();int checkedCases=0;
        for(int n=0;n<vs.Length;n++)
        {
            var v=vs[n];var e=es[n];string id=v.GetProperty("id").GetString()!;
            try
            {
                Check(id==e.GetProperty("id").GetString());var(c,m)=ProtectedModeTests.Rig([0x90]);
                var a=Value(v.GetProperty("a"));var b=Value(v.GetProperty("b"));ushort cw=v.GetProperty("control").GetUInt16();int kind=v.GetProperty("kind").GetInt32();
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,cw,kind);c.Eflags=0x247;At(c,m,[0xd9,0xf2]);
                ushort sw=e.GetProperty("status").GetUInt16();var regs=e.GetProperty("registers").EnumerateArray().ToArray();
                Check((c.FpuStatusWord&~0x4100)==(sw&~0x4100),$"SW {c.FpuStatusWord:X4}/{sw:X4}");
                Check(c.FpuControlWord==cw&&c.FpuTagWord==e.GetProperty("tags").GetUInt16(),$"CW/tags {c.FpuTagWord:X4}");
                for(int j=0;j<8;j++)if(!c.IsFpuRegisterEmpty(j))Check(c.GetFpuRegister(j)==Value(regs[j]),$"Raw ST({j}) {c.GetFpuRegister(j)} / {Value(regs[j])}");
                Check(c.Eflags==0x247&&c.FpuTop==(sw>>11&7));
                if((kind&33)==0)
                {
                    var pure=Float80Math.Tangent(a,cw);Check(pure.Number.Commit==(c.FpuTop==5),"Pure commit/push");
                    Check(pure.OutOfRange==((sw&0x400)!=0),"Pure C2");
                    Check((pure.Number.Exceptions|((kind&16)!=0?4:0))==(sw&63),"Pure flags");
                    Check(pure.Number.RoundedUp==((sw&0x200)!=0),"Pure C1");
                    if(pure.Number.Commit)Check(pure.Pushed==Value(regs[0])&&pure.Number.Value==Value(regs[1]),"Pure two results");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vs.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 tangent regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})foreach(bool pm in new[]{false,true})
            test($"FPTAN half-radian PC={pc}, RC={rc}, negative={negative}, PM={pm} pushes one and retains full precision",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j+7,0x4000));Load(c,m,new(j,negative?(ushort)0xbffe:(ushort)0x3ffe));
                Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(pm?0xffff:~32)),0);c.Eflags=0x247;At(c,m,[0xd9,0xf2]);
                bool up=rc==0||rc==(negative?1:2);Check(c.FpuTop==5&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==new Float80Value(0x8bda7adf9a3a5218+(up?1ul:0),(ushort)(0x3ffe|(negative?0x8000:0))));
                Check(c.GetFpuRegister(2)==new Float80Value(j+7,0x4000)&&c.Eflags==0x247&&(c.FpuStatusWord&0x47f)==32&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==!pm);
            });
        foreach(bool negative in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(bool um in new[]{false,true})
            test($"FPTAN minimum denormal negative={negative}, RC={rc}, UM={um} has strict upward magnitude and a real push",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(1,negative?(ushort)0x8000:(ushort)0));Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)),4);At(c,m,[0xd9,0xf2]);
                bool up=rc==(negative?1:2);var answer=um?new Float80Value(up?2ul:1,(ushort)(negative?0x8000:0)):new Float80Value(j+(up?1ul:0),(ushort)(0x5fc2|(negative?0x8000:0)));
                Check(c.FpuTop==6&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==answer&&(c.FpuStatusWord&63)==50&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==!um);
            });
        foreach(bool negative in new[]{false,true})foreach(ushort exponent in new ushort[]{0x403e,0x403f,0x7ffe})foreach(bool occupied in new[]{false,true})foreach(bool im in new[]{false,true})
            test($"FPTAN range {exponent:X} negative={negative}, occupied={occupied}, IM={im} orders stack before C2",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j,(ushort)(exponent|(negative?0x8000:0)));Load(c,m,a);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,occupied?32:0);var before=c.GetFpuRegister(0);int top=c.FpuTop;At(c,m,[0xd9,0xf2]);
                if(!occupied)Check(c.FpuTop==top&&c.GetFpuRegister(0)==before&&(c.FpuStatusWord&0x67f)==0x400);
                else {Check((c.FpuStatusWord&0x67f)==0x241&&c.FpuTop==((top+(im?-1:0))&7)&&c.FpuErrorAsserted==!im);if(im)Check(c.GetFpuRegister(0)==Float80Value.Indefinite&&c.GetFpuRegister(1)==Float80Value.Indefinite);}
            });
        foreach(bool missing in new[]{false,true})foreach(bool im in new[]{false,true})
            test($"FPTAN full stack missing={missing}, IM={im} preflights both physical destinations",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=0;i--)Load(c,m,new(j+(ulong)i,(ushort)(0x3ffe+i)));Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,missing?1:0);var before=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();ushort tags=c.FpuTagWord;At(c,m,[0xd9,0xf2]);
                Check((c.FpuStatusWord&0x67f)==(missing?0x41:0x241)&&c.FpuErrorAsserted==!im);
                if(im){Check(c.FpuTop==7&&c.GetFpuRegister(0)==Float80Value.Indefinite&&c.GetFpuRegister(1)==Float80Value.Indefinite);for(int k=2;k<8;k++)Check(c.GetFpuRegister(k)==before[k-1]);}
                else {Check(c.FpuTop==0&&c.FpuTagWord==tags);for(int k=0;k<8;k++)Check(c.GetFpuRegister(k)==before[k]);}
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FPTAN prefix {Convert.ToHexString(prefix)} signed zero clears C2 and pushes an exact one",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0,0x8000));Seed(c,m,0x37f,0);At(c,m,[..prefix,0xd9,0xf2]);Check(c.FpuTop==6&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==new Float80Value(0,0x8000)&&(c.FpuStatusWord&0x67f)==0);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FPTAN EM/TS={bits} faults NM before FPU pointer/status/push changes",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,0xf2}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);Check(c.FpuTop==7&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        test("FPTAN split fetch PF repairs and repeats without a premature push",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.Zero);uint ip=c.FpuInstructionPointer;m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xf2);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);Check(c.FpuTop==7&&c.FpuInstructionPointer==ip);PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.FpuTop==6&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==Float80Value.Zero&&c.Eip==0x3001);
        });
        test("FPTAN TF traps after both committed stack results",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.Zero);c.Eflags|=0x100;new byte[]{0xd9,0xf2}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&c.FpuTop==6&&c.GetFpuRegister(0)==Float80Value.One&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002);
        });
        test("LOCK FPTAN faults UD before FPU effects",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;new byte[]{0xf0,0xd9,0xf2}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuTop==7&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
        });
        foreach((Float80Value x,ushort cw,int kind,int flags) in new (Float80Value,ushort,int,int)[]{(new(j,0x3ffe),0x35f,4,32),(new(1,0),0x36f,4,50),(new(1,0),0x37d,4,2),(new(j,0x7fff),0x37e,4,1),(Float80Value.One,0x37e,5,1),(new(j,0x403e),0x37e,36,1)})
            test($"FPTAN actual MF/IRETD/WAIT flags={flags:X}, kind={kind} retains committed or faulted destinations",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,x);Seed(c,m,cw,kind);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,0xf2]);Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags);
                int top=c.FpuTop;ushort tags=c.FpuTagWord;var regs=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.FpuTop==top&&c.FpuTagWord==tags);for(int k=0;k<8;k++)Check(c.GetFpuRegister(k)==regs[k]);
            });
    }
}
