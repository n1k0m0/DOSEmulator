// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ExponentialTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="F2XM1 invariant") {if(!value)throw new Exception(detail);}
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
        int initial=(kind&12) switch {4=>0,8=>0x100,12=>0x4000,_=>0x4500};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x200|initial|((kind&16)!=0?4:0)));
        At(c,m,Mem(0xd9,4,0x32000));
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
    /// <summary>Compares the independent reference-vector file with the actual numeric core/decoder results.</summary>
    public static int VerifyNativeVectors(string vectorsPath,string expectedPath)
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
                int kind=v.GetProperty("kind").GetInt32();Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,control,kind);
                if((kind&1)!=0)At(c,m,[0xdd,0xc0]);if((kind&2)!=0)At(c,m,[0xdd,0xc1]);
                c.Eflags=0x247;At(c,m,[0xd9,0xf0]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                // C0/C2/C3 are architecturally undefined for F2XM1.
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==6,"F2XM1 changed EFLAGS/TOP");
                if((kind&1)==0)
                {
                    var pure=Float80Math.Exp2MinusOne(a,control);var expected=Value(raw[0]);
                    Check((pure.Commit?pure.Value:a)==expected,"Pure 80-bit result differs");
                    Check((expectedStatus&0x3f)==(pure.Exceptions|((kind&16)!=0?4:0)),"Pure exception flags differ");
                    Check(((expectedStatus&0x200)!=0)==pure.RoundedUp,"Pure C1 differs");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 exponential regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})foreach(bool pm in new[]{false,true})
            test($"F2XM1 endpoint sign={negative}, PC={pc}, RC={rc}, PM={pm} retains the exact mathematical endpoint and signals reference PE",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,(ushort)(0x3fff|(negative?0x8000:0))));Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(pm?0xffff:~32)),0);
                c.Eflags=0x247;At(c,m,[0xd9,0xf0]);Check(c.GetFpuRegister(0)==new Float80Value(j,negative?(ushort)0xbffe:(ushort)0x3fff)&&c.FpuTop==7&&c.Eflags==0x247);
                Check((c.FpuStatusWord&0x23f)==32&&c.FpuErrorAsserted==!pm);
            });
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})
            test($"F2XM1 half sign={negative}, PC={pc}, RC={rc} retains 64 result bits and independently known rounding",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,(ushort)(0x3ffe|(negative?0x8000:0))));Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),4);At(c,m,[0xd9,0xf0]);
                bool up=rc==(negative?1:2);ulong sig=negative?0x95f619980c4336f7:0xd413cccfe7799211;
                Check(c.GetFpuRegister(0)==new Float80Value(sig+(up?1ul:0),(ushort)(0x3ffd|(negative?0x8000:0)))&&(c.FpuStatusWord&0x3f)==32&&((c.FpuStatusWord&0x200)!=0)==up);
            });
        foreach(bool negative in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(bool um in new[]{false,true})foreach(bool dm in new[]{false,true})
            test($"F2XM1 minimum denormal sign={negative}, RC={rc}, UM={um}, DM={dm} bounds cancellation and has separate DE/UE/PE commits",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var input=new Float80Value(1,negative?(ushort)0x8000:(ushort)0);Load(c,m,input);Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)&(dm?0xffff:~2)),4);At(c,m,[0xd9,0xf0]);
                bool up=dm&&(um?(rc==0||rc==(negative?1:2)):(rc==0||rc==(negative?1:2)));
                ulong sig=um?(up?1ul:0):0xb17217f7d1cf79ab+(up?1ul:0);
                var expected=!dm?input:new Float80Value(sig,(ushort)((um?0:0x5fc1)|(negative?0x8000:0)));
                Check(c.GetFpuRegister(0)==expected&&(c.FpuStatusWord&0x3f)==(dm?50:2)&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==(!dm||!um));
            });
        foreach(bool im in new[]{false,true})foreach(var input in new[]{new Float80Value(1,1),new Float80Value(j|0x123,0x7fff),new Float80Value(j|0x4000000000000123,0xffff)})
            test($"F2XM1 special {input}, IM={im} has separate payload/invalid/commit answers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,input);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xf0]);bool invalid=input.Significand!=(j|0x4000000000000123);
                var answer=!invalid||!im?input:input.IntegerBit?input with{Significand=input.Significand|0x4000000000000000}:Float80Value.Indefinite;
                Check(c.GetFpuRegister(0)==answer&&(c.FpuStatusWord&0x23f)==(invalid?1:0)&&c.FpuErrorAsserted==(invalid&&!im));
            });
        foreach(bool masked in new[]{false,true})
            test($"F2XM1 empty source, IM={masked}, does not pop or disturb the next register",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(17));Load(c,m,Float80Value.One);At(c,m,[0xdd,0xc0]);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xf0]);
                Check(c.FpuTop==6&&c.IsFpuRegisterEmpty(0)==!masked&&c.GetFpuRegister(1)==Float80Math.FromInteger(17)&&(c.FpuStatusWord&0x241)==0x41&&c.FpuErrorAsserted==!masked);
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"F2XM1 prefix {Convert.ToHexString(prefix)} on a full stack preserves seven other raw registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=1;i--)Load(c,m,Float80Math.FromInteger(i));Load(c,m,new(j,0x3ffe));var previous=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();
                At(c,m,[..prefix,0xd9,0xf0]);Check(c.FpuTop==0&&c.GetFpuRegister(0)==new Float80Value(0xd413cccfe7799211,0x3ffd));for(int i=1;i<8;i++)Check(c.GetFpuRegister(i)==previous[i-1]);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"F2XM1 EM/TS={bits} faults NM before pointer, register or status changes",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,0x3ffe));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);
                new byte[]{0xd9,0xf0}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw&&c.GetFpuRegister(0)==new Float80Value(j,0x3ffe));
            });
        foreach((Float80Value input,ushort control,int flags,bool commit) in new (Float80Value,ushort,int,bool)[]{(new(j,0x3ffe),0x35f,32,true),(new(j,1),0x36f,48,true),(new(1,0),0x37d,2,false),(new(j|0x123,0x7fff),0x37e,1,false)})
            test($"F2XM1 pending {flags:X} gives actual MF then FNCLEX/IRETD repairs and repeats WAIT",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,input);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,0xf0]);
                Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags&&(commit||c.GetFpuRegister(0)==input));var result=c.GetFpuRegister(0);
                m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.GetFpuRegister(0)==result);
            });
        test("F2XM1 split opcode PF preserves uncomputed source and retries",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,new(j,0x3ffe));uint ip=c.FpuInstructionPointer;m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xf0);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);Check(c.FpuInstructionPointer==ip&&c.GetFpuRegister(0)==new Float80Value(j,0x3ffe));
            PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x3001&&c.GetFpuRegister(0)==new Float80Value(0xd413cccfe7799211,0x3ffd));
        });
        test("F2XM1 TF traps after the committed rounded result",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,0x3ffe));c.Eflags|=0x100;new byte[]{0xd9,0xf0}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002&&c.GetFpuRegister(0)==new Float80Value(0xd413cccfe7799211,0x3ffd));
        });
        test("LOCK F2XM1 faults UD before any FPU effects",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,0x3ffe));uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;new byte[]{0xf0,0xd9,0xf0}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
        });
        foreach(var input in new[]{new Float80Value(0xc000000000000000,0x3fff),new Float80Value(j,0x4000)})
            test($"F2XM1 out-of-range {input} has only a deterministic undefined-input diagnostic policy",()=>
            {var x=Float80Math.Exp2MinusOne(input,0x37f);Check(x.Value==input&&x.Exceptions==32&&!x.RoundedUp);});
    }
}
