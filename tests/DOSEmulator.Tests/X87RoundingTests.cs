// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87RoundingTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="FRNDINT invariant") {if(!value)throw new Exception(detail);}
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
                c.Eflags=0x247;At(c,m,[0xd9,0xfc]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                // C0/C2/C3 are architecturally undefined for FRNDINT.
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==6,"FRNDINT changed EFLAGS/TOP");
                if((kind&1)==0)
                {
                    var pure=Float80Math.RoundToInteger(a,control);var expected=Value(raw[0]);
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
    /// <summary>Registers the named x87 rounding regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})foreach(int n in new[]{1,3,5})
            test($"FRNDINT dyadic {n}/2 sign={negative} RC={rc} ignores PC={pc}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var input=new Float80Value(n==1?0x8000000000000000:n==3?0xc000000000000000:0xa000000000000000,(ushort)((n==1?0x3ffe:n==3?0x3fff:0x4000)|(negative?0x8000:0)));
                long[] nearest=n==1?[0,0,1,0]:n==3?[2,1,2,1]:[2,2,3,2];
                long answer=negative?(rc switch{1=>(n+1)/2,2=>n/2,0=>nearest[0],_=>n/2}):nearest[rc];
                var expected=answer==0?new Float80Value(0,negative?(ushort)0x8000:(ushort)0):Float80Math.FromInteger(negative?-answer:answer);
                Load(c,m,input);Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),0);c.Eflags=0x247;At(c,m,[0xd9,0xfc]);
                Check(c.GetFpuRegister(0)==expected && c.FpuTop==7 && c.Eflags==0x247);
                Check((c.FpuStatusWord&0x3f)==32 && ((c.FpuStatusWord&0x200)!=0)==(answer*2>n));
                Check((c.FpuStatusWord&0x4500)==0x4500,"Stable reference C0/C2/C3 contract");
            });
        foreach(int pc in new[]{0,2,3})foreach(bool negative in new[]{false,true})
            test($"FRNDINT preserves the low bit of a 64-bit integer beyond signed range, PC={pc}, negative={negative}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var input=new Float80Value(0x8000000000000001,(ushort)(0x403e|(negative?0x8000:0)));
                Load(c,m,input);Seed(c,m,(ushort)(0x7f|pc<<8),0);At(c,m,[0xd9,0xfc]);
                Check(c.GetFpuRegister(0)==input && (c.FpuStatusWord&0x23f)==0 && c.FpuTop==7);
            });
        foreach(bool masked in new[]{false,true})foreach(var value in new Float80Value[]{new(0xc000000000000042,0x7fff),new(0x8000000000000042,0x7fff),new(1,0x1234)})
            test($"FRNDINT NaN/unsupported {value.Significand:X16}/{value.SignExponent:X4} IM={masked}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,4);
                At(c,m,[0xd9,0xfc]);bool invalid=value.Significand!=0xc000000000000042;
                var expected=!invalid||!masked?value:value.IntegerBit?value with{Significand=value.Significand|0x4000000000000000}:Float80Value.Indefinite;
                Check(c.GetFpuRegister(0)==expected && (c.FpuStatusWord&0x23f)==(invalid?1:0) && c.FpuTop==7);
                Check(c.FpuErrorAsserted==(invalid&&!masked));
            });
        foreach(bool masked in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})
            test($"FRNDINT denormal DM={masked} RC={rc} sign={negative} commits only when DM allows it",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var input=new Float80Value(1,negative?(ushort)0x8000:(ushort)0);Load(c,m,input);
                Seed(c,m,(ushort)((0x37f|rc<<10)&(masked?0xffff:~2)),0);At(c,m,[0xd9,0xfc]);
                bool one=masked&&(rc==1&&negative||rc==2&&!negative);
                var expected=!masked?input:one?Float80Math.FromInteger(negative?-1:1):new Float80Value(0,input.SignExponent);
                Check(c.GetFpuRegister(0)==expected && (c.FpuStatusWord&0x3f)==(masked?0x22:2) && ((c.FpuStatusWord&0x200)!=0)==one && c.FpuTop==7);
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FRNDINT register-only prefix {Convert.ToHexString(prefix)} preserves a full eight-register stack",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=1;i--)Load(c,m,Float80Math.FromInteger(i+100));
                Load(c,m,new(0xc000000000000000,0x3fff));var old=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();
                At(c,m,[..prefix,0xd9,0xfc]);Check(c.FpuTop==0 && c.GetFpuRegister(0)==Float80Math.FromInteger(2));
                for(int i=1;i<8;i++)Check(c.GetFpuRegister(i)==old[i-1]);
            });
        foreach(bool masked in new[]{false,true})
            test($"FRNDINT empty ST0 IM={masked} has a distinct indefinite/unchanged response",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xfc]);
                Check(c.FpuTop==0 && (c.FpuStatusWord&0x241)==0x41 && c.IsFpuRegisterEmpty(0)==!masked);
                if(masked)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FRNDINT CR0 {bits:X} gives NM before recording or changing FPU state",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0xc000000000000000,0x3fff));uint ip=c.FpuInstructionPointer;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,0xfc}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==new Float80Value(0xc000000000000000,0x3fff) && c.FpuStatusWord==0x3800);
            });
        foreach((Float80Value value,ushort control,ushort flag,Float80Value answer) in new (Float80Value,ushort,ushort,Float80Value)[]{
            (new(0xc000000000000000,0x3fff),0x35f,32,new(0x8000000000000000,0x4000)),
            (new(1,0),0x37d,2,new(1,0)),(new(0x8000000000000042,0x7fff),0x37e,1,new(0x8000000000000042,0x7fff))})
            test($"FRNDINT unmasked {flag:X} reaches real MF and resumes WAIT through FNCLEX/IRETD",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);
                At(c,m,[0xd9,0xfc]);Check(c.FpuErrorAsserted && (c.FpuStatusWord&0x3f)==flag && c.GetFpuRegister(0)==answer);
                new byte[]{0x9b}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);
                c.Step();c.Step();Check(c.Eip==0x2000);c.Step();Check(c.Eip==0x2001 && !c.FpuErrorAsserted && c.GetFpuRegister(0)==answer);
            });
        test("FRNDINT second-byte fetch PF leaves the unrounded operand and retries through IRETD",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,new(0xc000000000000000,0x3fff));uint ip=c.FpuInstructionPointer;
            m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xfc);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
            Check(c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==new Float80Value(0xc000000000000000,0x3fff) && c.FpuStatusWord==0x3800);
            // #PF leaves a separate error dword before the IRETD frame.
            PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
            Check(c.Eip==0x3001 && c.GetFpuRegister(0)==Float80Math.FromInteger(2) && (c.FpuStatusWord&0x3f)==32);
        });
    }
}
