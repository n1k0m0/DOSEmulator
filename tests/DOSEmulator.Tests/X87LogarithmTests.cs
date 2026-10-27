// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87LogarithmTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="Logarithm invariant") { if(!value)throw new Exception(detail); }
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
                c.Eflags=0x247;At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16()&&c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247&&c.FpuTop==(expectedStatus>>11&7),"EFLAGS/TOP differs");
                if((kind&3)==0)
                {
                    var pure=Float80Math.Log2Multiply(a,b,plus,control);bool pop=(expectedStatus>>11&7)==7;
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
    /// <summary>Registers the named x87 logarithm regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(bool plus in new[]{false,true})foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool pm in new[]{false,true})
            test($"Logarithm plus={plus}, PC={pc}, RC={rc}, PM={pm} computes a fused full-precision result and commits its pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j+0x1234,0x4003));Load(c,m,new(j,0x3ffd));Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(pm?0xffff:~32)),4);
                c.Eflags=0x247;At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);
                var answer=plus?new Float80Value(0xa4d3c25e68dc6f63+(rc==2?1ul:0),0x4001):new Float80Value(j+0x1234,0xc004);
                Check(c.FpuTop==7&&c.GetFpuRegister(0)==answer&&c.IsFpuRegisterEmpty(7)&&c.Eflags==0x247);
                Check((c.FpuStatusWord&63)==32&&((c.FpuStatusWord&0x200)!=0)==(plus&&rc==2)&&c.FpuErrorAsserted==!pm);
            });
        foreach(bool plus in new[]{false,true})foreach(bool negativeX in new[]{false,true})foreach(bool negativeY in new[]{false,true})
            test($"Logarithm signed zero plus={plus}, xsign={negativeX}, ysign={negativeY} follows the defined sign table",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0,negativeY?(ushort)0x8000:(ushort)0));
                Load(c,m,plus?new(0,negativeX?(ushort)0x8000:(ushort)0):new(j,negativeX?(ushort)0x3ffe:(ushort)0x4000));At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);
                Check(c.FpuTop==7&&c.GetFpuRegister(0)==new Float80Value(0,negativeX^negativeY?(ushort)0x8000:(ushort)0)&&(c.FpuStatusWord&63)==0);
            });
        foreach(bool plus in new[]{false,true})foreach(int missing in new[]{1,2,3})foreach(bool masked in new[]{false,true})
            test($"Logarithm plus={plus}, missing={missing}, IM={masked} suppresses or commits both destination and pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var y=new Float80Value(j|0x1234,0x4003);var x=new Float80Value(j,0x3ffd);Load(c,m,y);Load(c,m,x);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,4);
                if((missing&1)!=0)At(c,m,[0xdd,0xc0]);if((missing&2)!=0)At(c,m,[0xdd,0xc1]);At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);
                Check(c.FpuTop==(masked?7:6)&&(c.FpuStatusWord&0x241)==0x41&&c.FpuErrorAsserted==!masked);
                if(masked)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
                else Check(c.GetFpuRegister(0)==x&&c.GetFpuRegister(1)==y&&c.IsFpuRegisterEmpty(0)==((missing&1)!=0)&&c.IsFpuRegisterEmpty(1)==((missing&2)!=0));
            });
        foreach(bool plus in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(bool um in new[]{false,true})foreach(bool dm in new[]{false,true})
            test($"Logarithm plus={plus}, RC={rc}, UM={um}, DM={dm} separates tiny/denormal state and pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var y=new Float80Value(1,0);var x=new Float80Value(j,plus?(ushort)0x3ffd:(ushort)0x3ffe);Load(c,m,y);Load(c,m,x);
                Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)&(dm?0xffff:~2)),4);At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);
                Check(c.FpuTop==(dm?7:6)&&(c.FpuStatusWord&63)==(dm?50:2)&&c.FpuErrorAsserted==(!um||!dm));
                if(!dm)Check(c.GetFpuRegister(0)==x&&c.GetFpuRegister(1)==y);
                else if(um)Check(c.GetFpuRegister(0)==new Float80Value(plus?(rc==2?1ul:0):1,plus?(ushort)0:(ushort)0x8000));
                else Check(c.GetFpuRegister(0)==(plus?new Float80Value(0xa4d3c25e68dc57f2+(rc==2?1ul:0),0x5fc0):new Float80Value(j,0xdfc2)));
            });
        foreach(bool plus in new[]{false,true})foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"Logarithm plus={plus}, prefix {Convert.ToHexString(prefix)} on a full stack keeps six other registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=2;i--)Load(c,m,new(j+(ulong)i,(ushort)(0x4000+i)));Load(c,m,Float80Value.One);Load(c,m,new(j,0x3ffd));
                var rest=Enumerable.Range(2,6).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,plus?(byte)0xf9:(byte)0xf1]);Check(c.FpuTop==1&&c.IsFpuRegisterEmpty(7));for(int i=1;i<7;i++)Check(c.GetFpuRegister(i)==rest[i-1]);
            });
        foreach(bool plus in new[]{false,true})foreach(uint bits in new uint[]{4,8,12})
            test($"Logarithm plus={plus}, EM/TS={bits} gives actual NM before pointer/pop/status effects",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x3ffd));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,plus?(byte)0xf9:(byte)0xf1}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw&&c.FpuTop==6&&c.GetFpuRegister(0)==new Float80Value(j,0x3ffd));
            });
        foreach(bool plus in new[]{false,true})
        {
            test($"Logarithm plus={plus} split opcode PF does not precompute, pop or update pointers; repaired fetch retries",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x3ffd));uint ip=c.FpuInstructionPointer;
                m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,plus?(byte)0xf9:(byte)0xf1);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
                Check(c.FpuInstructionPointer==ip&&c.FpuTop==6);PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
                Check(c.Eip==0x3001&&c.FpuTop==7&&c.GetFpuRegister(0)==(plus?new Float80Value(0xa4d3c25e68dc57f2,0x3ffd):Float80Math.FromInteger(-2)));
            });
            test($"Logarithm plus={plus} TF traps after the result and pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x3ffd));c.Eflags|=0x100;
                new byte[]{0xd9,plus?(byte)0xf9:(byte)0xf1}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002&&c.FpuTop==7);
            });
            test($"LOCK logarithm plus={plus} gives actual UD without FPU side effects",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(j,0x3ffd));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;
                new byte[]{0xf0,0xd9,plus?(byte)0xf9:(byte)0xf1}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuTop==6&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        }
        foreach((bool plus,Float80Value x,Float80Value y,ushort cw,int flags,int missing) in new (bool,Float80Value,Float80Value,ushort,int,int)[]{
            (false,new(0xc000000000000000,0x3fff),Float80Value.One,0x35f,32,0),(true,new(j,0x3ffd),Float80Value.One,0x35f,32,0),
            (false,new(j,0x4001),new(ulong.MaxValue,0x7ffe),0x377,40,0),(true,new(j,0x3ffd),new(j,1),0x36f,48,0),
            (true,new(1,0),Float80Value.One,0x37d,2,0),(false,Float80Value.Zero,Float80Value.One,0x37b,4,0),
            (false,new(j,0xbfff),Float80Value.One,0x37e,1,0),(true,new(j,0x3ffd),Float80Value.One,0x37e,1,1),(false,new(j,0x3ffd),Float80Value.One,0x37e,1,2)})
            test($"Logarithm actual MF/IRETD/WAIT plus={plus}, pending={flags:X}, missing={missing} preserves its committed or uncommitted stack",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,y);Load(c,m,x);Seed(c,m,cw,4);
                if(missing!=0)At(c,m,[0xdd,missing==1?(byte)0xc0:(byte)0xc1]);
                c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,plus?(byte)0xf9:(byte)0xf1]);
                Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags);int top=c.FpuTop;ushort tags=c.FpuTagWord;var values=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();
                m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
                Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.FpuTop==top&&c.FpuTagWord==tags);for(int i=0;i<8;i++)Check(c.GetFpuRegister(i)==values[i]);
            });
    }
}
