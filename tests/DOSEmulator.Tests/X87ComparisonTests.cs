// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ComparisonTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="x87 comparison invariant") {if(!value)throw new Exception(detail);}
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    {code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step();}
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte op,int group,uint address=0x31000)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    {m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address));}
    /// <summary>Builds and installs the controlled initial pixels required by the graphics regression.</summary>
    private static void Seed(X86Cpu c,MemoryBus m,ushort control,ushort conditions=0x4500)
    {
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x0200|conditions));
        At(c,m,Mem(0xd9,4,0x32000));
    }
    /// <summary>Builds explicit guest instruction bytes for the requested numeric/control operation.</summary>
    internal static byte[] Code(int op)=>op switch
    {
        0=>[0xd8,0xd1],1=>[0xd8,0xd9],2=>[0xde,0xd9],3=>[0xdd,0xe1],4=>[0xdd,0xe9],5=>[0xda,0xe9],6=>[0xd9,0xe4],
        7=>Mem(0xd8,2),8=>Mem(0xd8,3),9=>Mem(0xdc,2),10=>Mem(0xdc,3),11=>Mem(0xda,2),12=>Mem(0xda,3),13=>Mem(0xde,2),14=>Mem(0xde,3),_=>throw new ArgumentOutOfRangeException(nameof(op))
    };
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
                int op=v.GetProperty("operation").GetInt32(),empty=v.GetProperty("kind").GetInt32();
                ushort initial=(empty&12) switch {4=>0,8=>0x100,12=>0x4000,_=>0x4500};
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,control,initial);
                if((empty&1)!=0)At(c,m,[0xdd,0xc0]);if((empty&2)!=0)At(c,m,[0xdd,0xc1]);
                ulong bits=Convert.ToUInt64(v.GetProperty("bits").GetString(),16);m.WriteDword(0x31000,(uint)bits);m.WriteDword(0x31004,(uint)(bits>>32));
                c.Eflags=0x247;At(c,m,Code(op));
                Check(c.FpuStatusWord==e.GetProperty("status").GetUInt16(),$"SW {c.FpuStatusWord:X4} vs {e.GetProperty("status").GetUInt16():X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),"Compare changed an active register");
                Check(c.Eflags==0x247,"Compare changed EFLAGS");
                if(op<7 && (empty&1)==0 && (op==6 || (empty&2)==0))
                {
                    var relation=Float80Math.Compare(a,op==6?Float80Value.Zero:b,op is >=3 and <=5,control);
                    Check((c.FpuStatusWord&0x4500)==relation.Conditions && (c.FpuStatusWord&0x3f)==relation.Exceptions,"Pure comparison differs from native state");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 comparison regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int op in Enumerable.Range(0,7))foreach(var(a,b,want) in new(long,long,int)[]{(1,2,0x100),(2,1,0),(1,1,0x4000),(-2,-1,0x100),(0,0,0x4000)})
            test($"Comparison form {op} exact relation {a}/{b} preserves values and EFLAGS",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(b));Load(c,m,Float80Math.FromInteger(a));Seed(c,m,0x37f);
                c.Eflags=0x247;At(c,m,Code(op));int relation=op==6?(a<0?0x100:a==0?0x4000:0):want;
                Check((c.FpuStatusWord&0x4700)==relation && c.Eflags==0x247 && (c.FpuStatusWord&0x3f)==0);
                int pops=op is 2 or 5?2:op is 1 or 4?1:0;Check(c.FpuTop==((6+pops)&7));
            });
        foreach(bool quiet in new[]{false,true})foreach(bool signaling in new[]{false,true})foreach(bool masked in new[]{false,true})
            test($"NaN compare quiet={quiet}, signaling={signaling}, IM={masked} has distinct commit/pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(signaling?0x8000000000000042:0xc000000000000042,0x7fff));Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0x100);
                At(c,m,Code(quiet?4:1));bool invalid=!quiet||signaling;Check((c.FpuStatusWord&1)==(invalid?1:0));
                Check(c.FpuTop==(invalid&&!masked?6:7) && (c.FpuStatusWord&0x4700)==0x4500);
            });
        foreach(int op in Enumerable.Range(0,7))foreach(bool masked in new[]{false,true})
            test($"Empty ST0 compare {op}, IM={masked} does not manufacture an indefinite register",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0x4000);At(c,m,Code(op));
                int pops=masked?(op is 2 or 5?2:op is 1 or 4?1:0):0;
                Check(c.FpuTagWord==0xffff && c.FpuTop==pops && (c.FpuStatusWord&0x241)==0x41 && (c.FpuStatusWord&0x4500)==0x4500);
            });
        foreach(int slot in Enumerable.Range(0,8))foreach(bool quiet in new[]{false,true})foreach(bool pop in new[]{false,true})
            test($"Register compare ST({slot}) snapshots aliases, quiet={quiet}, pop={pop}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=8;i>=1;i--)Load(c,m,Float80Math.FromInteger(i));
                At(c,m,[quiet?(byte)0xdd:(byte)0xd8,(byte)((quiet?0xe0:0xd0)+(pop?8:0)+slot)]);
                Check((c.FpuStatusWord&0x4700)==(slot==0?0x4000:0x100) && c.FpuTop==(pop?1:0));
            });
        foreach(int op in Enumerable.Range(7,8))
            test($"Memory compare {op} PF leaves status TOP and register bits unchanged",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);ushort status=c.FpuStatusWord;
                PagingTests.Pte(m,0x31000,0,0);Code(op).CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,14,0);
                Check(c.FpuStatusWord==status && c.GetFpuRegister(0)==Float80Value.One && c.FpuTop==7);
            });
        test("Unmasked FCOMP defers MF, preserves TOP and resumes WAIT after actual IRETD",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,new(0xc000000000000042,0x7fff));Seed(c,m,0x37e);
            c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,Code(1));Check(c.FpuTop==6 && c.FpuErrorAsserted);
            new byte[]{0x9b}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);
            new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();Check(c.Eip==0x2000 && !c.FpuErrorAsserted);
            c.Step();Check(c.Eip==0x2001 && c.FpuTop==6 && (c.FpuStatusWord&0x4500)==0x4500);
        });
    }
}
