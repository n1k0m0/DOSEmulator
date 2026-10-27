// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87SineCosineTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="FSIN/FCOS invariant") { if(!value)throw new Exception(detail); }
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    {code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step();}
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte op,int group,uint address=0x31000)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    {m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address));}
    /// <summary>Builds and installs the controlled initial pixels required by the graphics regression.</summary>
    private static void Seed(X86Cpu c,MemoryBus m,ushort control,int kind)
    {
        if((kind&32)!=0){At(c,m,[0xd9,0xee]);At(c,m,[0xd9,0xf7]);}
        int cc=(kind&12) switch{4=>0,8=>0x100,12=>0x4000,_=>0x4500};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x200|cc|((kind&16)!=0?4:0)));At(c,m,Mem(0xd9,4,0x32000));
        if((kind&1)!=0)At(c,m,[0xdd,0xc0]);if((kind&2)!=0)At(c,m,[0xdd,0xc1]);
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
    /// <summary>Checks the actual numeric core/decoder against independently certified mathematical intervals.</summary>
    public static int VerifyMathVectors(string vectorsPath,string expectedPath)
    {
        using var input=JsonDocument.Parse(File.ReadAllText(vectorsPath));using var expected=JsonDocument.Parse(File.ReadAllText(expectedPath));
        var vs=input.RootElement.EnumerateArray().ToArray();var es=expected.RootElement.GetProperty("results").EnumerateArray().ToArray();Check(vs.Length==es.Length);var failures=new List<object>();int checkedCases=0;
        for(int n=0;n<vs.Length;n++)
        {
            var v=vs[n];var e=es[n];string id=v.GetProperty("id").GetString()!;
            try
            {
                Check(id==e.GetProperty("id").GetString());var(c,m)=ProtectedModeTests.Rig([0x90]);var a=Value(v.GetProperty("a"));var b=Value(v.GetProperty("b"));ushort cw=v.GetProperty("control").GetUInt16();int kind=v.GetProperty("kind").GetInt32();bool cos=v.GetProperty("operation").GetInt32()!=0;
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,cw,kind);c.Eflags=0x247;At(c,m,[0xd9,cos?(byte)0xff:(byte)0xfe]);
                ushort sw=e.GetProperty("status").GetUInt16();var regs=e.GetProperty("registers").EnumerateArray().ToArray();
                Check((c.FpuStatusWord&~0x4100)==(sw&~0x4100),$"SW {c.FpuStatusWord:X4}/{sw:X4}");Check(c.FpuControlWord==cw&&c.FpuTagWord==e.GetProperty("tags").GetUInt16(),$"CW/tags {c.FpuTagWord:X4}/{e.GetProperty("tags").GetUInt16():X4}");
                for(int j=0;j<8;j++)if(!c.IsFpuRegisterEmpty(j))Check(c.GetFpuRegister(j)==Value(regs[j]),$"Raw ST({j}) {c.GetFpuRegister(j)}/{Value(regs[j])}");
                Check(c.FpuTop==6&&c.Eflags==0x247,"TOP/EFLAGS");
                if((kind&1)==0)
                {
                    var pure=Float80Math.SineCosine(a,cos,cw);Check(pure.OutOfRange==((sw&0x400)!=0));
                    Check((pure.Number.Exceptions|((kind&16)!=0?4:0))==(sw&63)&&pure.Number.RoundedUp==((sw&0x200)!=0),"Pure flags/C1");
                    Check((pure.Number.Commit?pure.Number.Value:a)==Value(regs[0]),"Pure value/commit");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vs.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 sine cosine regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(bool cosine in new[]{false,true})foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})foreach(bool pm in new[]{false,true})
            test($"{(cosine?"FCOS":"FSIN")} half radian PC={pc} RC={rc} negative={negative} PM={pm} retains full precision, TOP and other registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j+7,0x4000));Load(c,m,new(j,(ushort)(0x3ffe|(negative?0x8000:0))));Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(pm?0xffff:~32)),0);c.Eflags=0x247;At(c,m,[0xd9,cosine?(byte)0xff:(byte)0xfe]);
                bool neg=negative&&!cosine,up=rc==0||rc==(neg?1:2);var expected=new Float80Value((cosine?0xe0a94032dbea7ced:0xf57743a2582f7f43)+(up?1ul:0),(ushort)((cosine?0x3ffe:0x3ffd)|(neg?0x8000:0)));
                Check(c.FpuTop==6&&c.GetFpuRegister(0)==expected&&c.GetFpuRegister(1)==new Float80Value(j+7,0x4000)&&c.Eflags==0x247&&(c.FpuStatusWord&0x47f)==32&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==!pm);
            });
        foreach(bool cosine in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(bool um in new[]{false,true})
            test($"{(cosine?"FCOS":"FSIN")} minimum normal RC={rc} UM={um} distinguishes strict rounding below the binary boundary",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,1));Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)),4);At(c,m,[0xd9,cosine?(byte)0xff:(byte)0xfe]);bool up=rc is 0 or 2;
                var answer=cosine?(up?Float80Value.One:new Float80Value(ulong.MaxValue,0x3ffe)):(up?new Float80Value(j,1):um?new Float80Value(j-1,0):new Float80Value(ulong.MaxValue,0x6000));
                Check(c.GetFpuRegister(0)==answer&&c.FpuTop==7&&(c.FpuStatusWord&63)==(cosine||up?32:48)&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==(!cosine&&!up&&!um));
            });
        foreach(bool cosine in new[]{false,true})foreach(bool negative in new[]{false,true})foreach(bool dm in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))
            test($"{(cosine?"FCOS":"FSIN")} minimum denormal negative={negative} DM={dm} RC={rc} separates input from result exceptions",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(1,negative?(ushort)0x8000:(ushort)0);Load(c,m,a);Seed(c,m,(ushort)((0x37f|rc<<10)&(dm?0xffff:~2)),4);At(c,m,[0xd9,cosine?(byte)0xff:(byte)0xfe]);
                bool up=rc==0||rc==(!cosine&&negative?1:2);var expected=!dm?a:cosine?(up?Float80Value.One:new Float80Value(ulong.MaxValue,0x3ffe)):new Float80Value(up?1ul:0,(ushort)(negative?0x8000:0));
                Check(c.GetFpuRegister(0)==expected&&c.FpuTop==7&&(c.FpuStatusWord&63)==(!dm?2:cosine?34:50)&&c.FpuErrorAsserted==!dm);
            });
        foreach(bool cosine in new[]{false,true})foreach(ushort exp in new ushort[]{0x403e,0x403f,0x7ffe})foreach(bool missing in new[]{false,true})foreach(bool im in new[]{false,true})
            test($"{(cosine?"FCOS":"FSIN")} range {exp:X} missing={missing} IM={im} prioritizes source fault without a stack movement",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j,exp);Load(c,m,a);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,missing?1:0);At(c,m,[0xd9,cosine?(byte)0xff:(byte)0xfe]);
                Check(c.FpuTop==7&&(c.FpuStatusWord&0x67f)==(missing?0x41:0x400));Check(c.GetFpuRegister(0)==(missing&&im?Float80Value.Indefinite:a)&&c.FpuErrorAsserted==(missing&&!im));
            });
        foreach(bool cosine in new[]{false,true})foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"{(cosine?"FCOS":"FSIN")} prefix {Convert.ToHexString(prefix)} with a full stack changes only the source",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int k=7;k>=1;k--)Load(c,m,new(j+(ulong)k,(ushort)(0x4000+k)));Load(c,m,new(0,0x8000));Seed(c,m,0x37f,0);var old=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,cosine?(byte)0xff:(byte)0xfe]);
                Check(c.FpuTop==0&&c.GetFpuRegister(0)==(cosine?Float80Value.One:new Float80Value(0,0x8000))&&(c.FpuStatusWord&0x67f)==0);for(int k=1;k<8;k++)Check(c.GetFpuRegister(k)==old[k-1]);
            });
        foreach(bool cosine in new[]{false,true})foreach(uint bits in new uint[]{4,8,12})
            test($"{(cosine?"FCOS":"FSIN")} EM/TS={bits} faults NM before pointer/status/register changes",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,cosine?(byte)0xff:(byte)0xfe}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);Check(c.GetFpuRegister(0)==Float80Value.One&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        foreach(bool cosine in new[]{false,true})
        {
            test($"{(cosine?"FCOS":"FSIN")} split fetch PF repairs and repeats without an early register change",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.Zero);uint ip=c.FpuInstructionPointer;m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,cosine?(byte)0xff:(byte)0xfe);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);Check(c.FpuTop==7&&c.GetFpuRegister(0)==Float80Value.Zero&&c.FpuInstructionPointer==ip);PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x3001&&c.GetFpuRegister(0)==(cosine?Float80Value.One:Float80Value.Zero));
            });
            test($"{(cosine?"FCOS":"FSIN")} TF traps after its committed result",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.Zero);c.Eflags|=0x100;new byte[]{0xd9,cosine?(byte)0xff:(byte)0xfe}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&c.GetFpuRegister(0)==(cosine?Float80Value.One:Float80Value.Zero)&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002);
            });
            test($"LOCK {(cosine?"FCOS":"FSIN")} faults UD before effects",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;new byte[]{0xf0,0xd9,cosine?(byte)0xff:(byte)0xfe}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.GetFpuRegister(0)==Float80Value.One&&c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        }
        foreach((bool cos,Float80Value x,ushort cw,int kind,int flags) in new (bool,Float80Value,ushort,int,int)[]{(false,new(j,0x3ffe),0x35f,4,32),(true,new(j,0x3ffe),0x35f,4,32),(false,new(1,0),0x36f,4,50),(false,new(1,0),0x37d,4,2),(true,new(1,0),0x37d,4,2),(true,new(j,0x7fff),0x37e,4,1),(false,Float80Value.One,0x37e,5,1),(true,Float80Value.One,0x37e,5,1)})
            test($"{(cos?"FCOS":"FSIN")} actual MF/IRETD/WAIT flags={flags:X} kind={kind} keeps committed or suppressed result",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,x);Seed(c,m,cw,kind);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,cos?(byte)0xff:(byte)0xfe]);Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags);int top=c.FpuTop;ushort tags=c.FpuTagWord;var regs=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.FpuTop==top&&c.FpuTagWord==tags);for(int k=0;k<8;k++)Check(c.GetFpuRegister(k)==regs[k]);
            });
    }
}
