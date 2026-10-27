// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ConstantTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="x87 constant invariant") {if(!value)throw new Exception(detail);}
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
        int initial=(kind&0xc0) switch {0x40=>0x100,0x80=>0x4000,0xc0=>0x4500,_=>0};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|((kind&32)!=0?0x200:0)|initial|((kind&16)!=0?4:0)));
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
                int kind=v.GetProperty("kind").GetInt32();int op=v.GetProperty("operation").GetInt32();
                for(int d=kind&15;d>0;d--)Load(c,m,(d&1)==0?a:b,(uint)(0x30000+((d&1)*16)));Seed(c,m,control,kind);
                c.Eflags=0x247;At(c,m,[0xd9,(byte)(0xe8+op)]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                // C0/C2/C3 are architecturally undefined for x87 constant.
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==(expectedStatus>>11&7),"Constant EFLAGS/TOP");
                if((kind&15)<8)Check(c.GetFpuRegister(0)==Float80Math.LoadConstant(op,control),"Pure constant differs");
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 constant regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        ulong[] floor=[0x8000000000000000,0xd49a784bcd1b8afe,0xb8aa3b295c17f0bb,0xc90fdaa22168c234,0x9a209a84fbcff798,0xb17217f7d1cf79ab,0];
        ushort[] exponent=[0x3fff,0x4000,0x3fff,0x4000,0x3ffd,0x3ffe,0];
        // Constructs the independently specified raw extended answer for this rounding/constant case.
        Float80Value Answer(int op,int rc)=>new(floor[op]+(rc==2&&op is >=1 and <=5||rc==0&&op is >=2 and <=5?1ul:0),exponent[op]);
        foreach(int op in Enumerable.Range(0,7))foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))
            test($"x87 constant {op}, PC={pc}, RC={rc}, preserves full precision without PE or C1 rounding",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(123));var old=c.GetFpuRegister(0);
                Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&~32),0xf0);c.Eflags=0x247;At(c,m,[0xd9,(byte)(0xe8+op)]);
                Check(c.GetFpuRegister(0)==Answer(op,rc) && c.GetFpuRegister(1)==old && c.FpuTop==6 && c.Eflags==0x247);
                Check((c.FpuStatusWord&0x23f)==4 && (c.FpuStatusWord&0x4500)==0x4500 && !c.FpuErrorAsserted);
            });
        foreach(int op in Enumerable.Range(0,7))foreach(bool masked in new[]{false,true})
            test($"x87 constant {op}, full stack IM={masked}, distinct commit and tag response",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,Float80Math.FromInteger(i+1));
                var old=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);
                At(c,m,[0xd9,(byte)(0xe8+op)]);Check((c.FpuStatusWord&0x241)==0x241 && c.FpuTop==(masked?7:0) && c.FpuErrorAsserted==!masked);
                if(masked)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
                for(int i=masked?1:0;i<8;i++)Check(c.GetFpuRegister(i)==old[masked?i-1:i]);
            });
        foreach(int op in Enumerable.Range(0,7))foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"x87 constant {op}, prefix {Convert.ToHexString(prefix)}, reuses an empty wrapped stack slot",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,Float80Math.FromInteger(i+1));
                var old=Enumerable.Range(0,7).Select(c.GetFpuRegister).ToArray();At(c,m,[0xdd,0xc7]);
                At(c,m,[..prefix,0xd9,(byte)(0xe8+op)]);Check(c.FpuTop==7 && c.GetFpuRegister(0)==Answer(op,0) && (c.FpuStatusWord&0x23f)==0);
                for(int i=0;i<7;i++)Check(c.GetFpuRegister(i+1)==old[i]);
            });
        foreach(int op in Enumerable.Range(0,7))foreach(uint bits in new uint[]{4,8,12})
            test($"x87 constant {op}, EM/TS {bits:X}, NM before recording or pushing",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(3));uint ip=c.FpuInstructionPointer;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,(byte)(0xe8+op)}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip && c.FpuTop==7 && c.GetFpuRegister(0)==Float80Math.FromInteger(3) && c.FpuStatusWord==0x3800);
            });
        foreach(int op in Enumerable.Range(0,7))
            test($"x87 constant {op}, overflow MF repairs an empty slot then retries the pending constant",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,Float80Math.FromInteger(i+1));Seed(c,m,0x37e,0);
                c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,(byte)(0xe8+op)]);uint ip=c.FpuInstructionPointer;
                Check(c.FpuErrorAsserted && c.FpuTop==0 && (c.FpuStatusWord&0x241)==0x241);
                new byte[]{0xd9,(byte)(0xe8+op)}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);Check(c.FpuInstructionPointer==ip && c.FpuTop==0);
                new byte[]{0xdb,0xe2,0xdd,0xc7,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2000);c.Step();
                Check(c.Eip==0x2002 && c.GetFpuRegister(0)==Answer(op,0) && c.FpuTop==7 && !c.FpuErrorAsserted && (c.FpuStatusWord&0x241)==0);
            });
        foreach(int op in Enumerable.Range(0,7))
            test($"x87 constant {op}, second-byte fetch PF leaves tags unchanged and retries through IRETD",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Math.FromInteger(3));uint ip=c.FpuInstructionPointer;
                m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,(byte)(0xe8+op));PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
                Check(c.FpuTop==7 && c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==Float80Math.FromInteger(3));
                PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
                Check(c.Eip==0x3001 && c.FpuTop==6 && c.GetFpuRegister(0)==Answer(op,0) && c.GetFpuRegister(1)==Float80Math.FromInteger(3));
            });
        foreach(int op in Enumerable.Range(0,7))
            test($"x87 constant {op}, TF trap observes the retired pushed value and following EIP",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);byte? vector=null;c.InterruptEntered+=v=>vector=v;
                new byte[]{0xd9,(byte)(0xe8+op)}.CopyTo(m.Ram,0x2200);c.Eip=0x2200;c.Eflags=0x347;uint sp=c.GetRegister(Register.Sp);c.Step();
                Check(vector==1 && c.Eip==0x6000 && c.GetFpuRegister(0)==Answer(op,0) && c.FpuTop==7);
                Check(m.ReadDword(sp-12)==0x2202 && (m.ReadDword(sp-4)&0x10100)==0x100);
            });
    }
}
