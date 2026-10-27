// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ArcTangentTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="FPATAN invariant") { if(!value)throw new Exception(detail); }
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    { code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step(); }
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte op,int group,uint address=0x31000)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    { m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address)); }
    /// <summary>Builds and installs the controlled initial pixels required by the graphics regression.</summary>
    private static void Seed(X86Cpu c,MemoryBus m,ushort control,int kind)
    {
        int initial=(kind&12) switch {4=>0,8=>0x100,12=>0x4000,_=>0x4500};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x200|initial|((kind&16)!=0?4:0)));
        At(c,m,Mem(0xd9,4,0x32000));
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
    /// <summary>Checks the actual numeric core/decoder against independently certified mathematical intervals.</summary>
    public static int VerifyMathVectors(string vectorsPath,string expectedPath)
    {
        using var input=JsonDocument.Parse(File.ReadAllText(vectorsPath));using var output=JsonDocument.Parse(File.ReadAllText(expectedPath));
        var vectors=input.RootElement.EnumerateArray().ToArray();var results=output.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Check(vectors.Length==results.Length);var failures=new List<object>();int checkedCases=0;
        for(int n=0;n<vectors.Length;n++)
        {
            var v=vectors[n];var e=results[n];string id=v.GetProperty("id").GetString()!;
            try
            {
                Check(id==e.GetProperty("id").GetString());var(c,m)=ProtectedModeTests.Rig([0x90]);
                var a=Value(v.GetProperty("a"));var b=Value(v.GetProperty("b"));ushort control=v.GetProperty("control").GetUInt16();
                int kind=v.GetProperty("kind").GetInt32();bool plus=v.GetProperty("operation").GetInt32()!=0;
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,control,kind);
                if((kind&1)!=0)At(c,m,[0xdd,0xc0]);if((kind&2)!=0)At(c,m,[0xdd,0xc1]);
                c.Eflags=0x247;At(c,m,[0xd9,(byte)0xf3]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16()&&c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247&&c.FpuTop==(expectedStatus>>11&7),"EFLAGS/TOP differs");
                if((kind&3)==0)
                {
                    var pure=Float80Math.ArcTangent(a,b,control);bool pop=(expectedStatus>>11&7)==7;
                    Check(pure.Commit==pop,"Pure commit/pop differs");
                    Check((pure.Commit?pure.Value:a)==Value(raw[0]),"Pure 80-bit result differs");
                    Check((expectedStatus&63)==(pure.Exceptions|((kind&16)!=0?4:0)),"Pure flags differ");
                    Check(((expectedStatus&0x200)!=0)==pure.RoundedUp,"Pure C1 differs");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 arc tangent regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool nx in new[]{false,true})foreach(bool ny in new[]{false,true})foreach(bool pm in new[]{false,true})
            test($"FPATAN quadrant x={nx}, y={ny}, PC={pc}, RC={rc}, PM={pm} has full-precision pi fractions and commits its pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,ny?(ushort)0xbfff:(ushort)0x3fff));Load(c,m,new(j,nx?(ushort)0xbfff:(ushort)0x3fff));
                Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(pm?0xffff:~32)),4);c.Eflags=0x247;At(c,m,[0xd9,0xf3]);
                bool up=rc==0||rc==(ny?1:2);var expected=new Float80Value((nx?0x96cbe3f9990e91a7:0xc90fdaa22168c234)+(up?1ul:0),(ushort)((nx?0x4000:0x3ffe)|(ny?0x8000:0)));
                Check(c.FpuTop==7&&c.GetFpuRegister(0)==expected&&c.IsFpuRegisterEmpty(7)&&c.Eflags==0x247);
                Check((c.FpuStatusWord&63)==32&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==!pm);
            });
        foreach(bool infinite in new[]{false,true})foreach(bool nx in new[]{false,true})foreach(bool ny in new[]{false,true})
            test($"FPATAN double {(infinite?"infinity":"zero")} signs {nx}/{ny} use atan2 axes without invalid or zero-divide",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(infinite?j:0,(ushort)((infinite?0x7fff:0)|(ny?0x8000:0))));Load(c,m,new(infinite?j:0,(ushort)((infinite?0x7fff:0)|(nx?0x8000:0))));At(c,m,[0xd9,0xf3]);
                Float80Value expected=infinite?new(nx?0x96cbe3f9990e91a8:0xc90fdaa22168c235,(ushort)((nx?0x4000:0x3ffe)|(ny?0x8000:0))):nx?new(0xc90fdaa22168c235,(ushort)(0x4000|(ny?0x8000:0))):new(0,ny?(ushort)0x8000:(ushort)0);
                Check(c.FpuTop==7&&c.GetFpuRegister(0)==expected&&(c.FpuStatusWord&63)==(infinite||nx?32:0));
            });
        foreach(int rc in Enumerable.Range(0,4))foreach(bool um in new[]{false,true})
            test($"FPATAN minimum-normal true angle below its source, RC={rc}, UM={um}, preserves strict rounding and tininess",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,1));Load(c,m,Float80Value.One);Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)),4);At(c,m,[0xd9,0xf3]);
                bool up=rc is 0 or 2;var answer=up?new Float80Value(j,1):um?new Float80Value(ulong.MaxValue>>1,0):new Float80Value(ulong.MaxValue,0x6000);
                Check(c.GetFpuRegister(0)==answer&&c.FpuTop==7&&(c.FpuStatusWord&63)==(up?32:48)&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==(!up&&!um));
            });
        foreach(int rc in Enumerable.Range(0,4))foreach(bool dm in new[]{false,true})
            test($"FPATAN minimum denormal RC={rc}, DM={dm} separates input fault from committed tiny angle",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(1,0));Load(c,m,Float80Value.One);Seed(c,m,(ushort)((0x37f|rc<<10)&(dm?0xffff:~2)),4);At(c,m,[0xd9,0xf3]);
                Check(c.FpuTop==(dm?7:6)&&(c.FpuStatusWord&63)==(dm?50:2)&&c.FpuErrorAsserted==!dm);
                Check(c.GetFpuRegister(0)==(dm?new Float80Value(rc is 0 or 2?1ul:0,0):Float80Value.One));
            });
        foreach(int missing in new[]{1,2,3})foreach(bool im in new[]{false,true})
            test($"FPATAN missing sources {missing}, IM={im} preserves or commits both destination and pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x4000));Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,4);
                if((missing&1)!=0)At(c,m,[0xdd,0xc0]);if((missing&2)!=0)At(c,m,[0xdd,0xc1]);At(c,m,[0xd9,0xf3]);
                Check(c.FpuTop==(im?7:6)&&(c.FpuStatusWord&0x241)==0x41&&c.FpuErrorAsserted==!im);
                if(im)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);else Check(c.GetFpuRegister(0)==new Float80Value(j,0x4000)&&c.GetFpuRegister(1)==Float80Value.One);
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FPATAN prefix {Convert.ToHexString(prefix)} on a full stack preserves six other raw registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=2;i--)Load(c,m,new(j+(ulong)i,(ushort)(0x4000+i)));Load(c,m,Float80Value.One);Load(c,m,new(j,0x4000));
                var rest=Enumerable.Range(2,6).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,0xf3]);Check(c.FpuTop==1&&c.IsFpuRegisterEmpty(7));for(int i=1;i<7;i++)Check(c.GetFpuRegister(i)==rest[i-1]);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FPATAN EM/TS={bits} faults NM before FPU pointer/status/pop changes",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x4000));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);
                new byte[]{0xd9,0xf3}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);Check(c.FpuTop==6&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        test("FPATAN split fetch PF repairs and repeats without premature pop",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xf3);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
            Check(c.FpuTop==6&&c.FpuInstructionPointer==ip);PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.FpuTop==7&&c.GetFpuRegister(0)==new Float80Value(0xc90fdaa22168c235,0x3ffe)&&c.Eip==0x3001);
        });
        test("FPATAN TF traps after the committed result and pop",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Value.One);c.Eflags|=0x100;new byte[]{0xd9,0xf3}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&c.FpuTop==7&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002);
        });
        test("LOCK FPATAN faults UD before FPU effects",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;new byte[]{0xf0,0xd9,0xf3}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuTop==6&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
        });
        foreach((Float80Value x,Float80Value y,ushort cw,int flags,int missing) in new (Float80Value,Float80Value,ushort,int,int)[]{
            (new(j,0x4000),Float80Value.One,0x35f,32,0),(Float80Value.One,new(1,0),0x36f,50,0),(new(1,0),Float80Value.One,0x37d,2,0),
            (new(j|0x123,0x7fff),Float80Value.One,0x37e,1,0),(new(j,0x4000),Float80Value.One,0x37e,1,1),(new(j,0x4000),Float80Value.One,0x37e,1,2)})
            test($"FPATAN actual MF/IRETD/WAIT pending={flags:X}, missing={missing} keeps its correct stack context",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,y);Load(c,m,x);Seed(c,m,cw,4);if(missing!=0)At(c,m,[0xdd,missing==1?(byte)0xc0:(byte)0xc1]);
                c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,0xf3]);Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags);
                int top=c.FpuTop;ushort tags=c.FpuTagWord;var regs=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);
                new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.FpuTop==top&&c.FpuTagWord==tags);for(int i=0;i<8;i++)Check(c.GetFpuRegister(i)==regs[i]);
            });
    }
}
