// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87RemainderTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="FPREM invariant") {if(!value)throw new Exception(detail);}
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
                int operation=v.GetProperty("operation").GetInt32(),iterations=0;byte modrm=(byte)((operation&1)!=0?0xf5:0xf8);
                c.Eflags=0x247;
                do{At(c,m,[0xd9,modrm]);iterations++;Check(iterations<2048,"Remainder loop bound");}
                while((operation&2)!=0&&!c.FpuErrorAsserted&&(c.FpuStatusWord&0x401)==0x400);
                ushort expectedStatus=e.GetProperty("status").GetUInt16();
                Check(c.FpuStatusWord==expectedStatus,$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16()&&c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247&&c.FpuTop==6,"Remainder changed EFLAGS/TOP");
                Check(iterations==BitConverter.ToInt32(Convert.FromHexString(e.GetProperty("memory").GetString()!),0),"Iteration count differs");
                if((kind&3)==0)
                {
                    var current=a;ushort flags=(ushort)((kind&16)!=0?4:0);int steps=0;
                    do{
                        var pure=Float80Math.Remainder(current,b,(operation&1)!=0,control);flags|=pure.Exceptions;
                        if(pure.Commit)current=pure.Value;steps++;
                        if((operation&2)==0||(flags&~control&63)!=0||(flags&1)!=0||(pure.Conditions&0x400)==0)break;
                        Check(steps<2048,"Pure iteration bound");
                    }while(true);
                    Check(current==Value(raw[0])&&steps==iterations,"Pure result/iterations differ");
                    Check((expectedStatus&63)==flags,"Pure exception flags differ");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }
    /// <summary>Registers the named x87 remainder regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(bool nearest in new[]{false,true})foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(int numerator in new[]{-15,-7,-5,-3,-1,1,3,7,15})
            test($"FPREM nearest={nearest}, PC={pc}, RC={rc}, numerator={numerator}/2 ignores PC/RC and uses magnitude quotient bits",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(2));Load(c,m,Float80Math.FromInteger(numerator),0x30010);Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),0);
                int q=Math.Abs(numerator)/2;if(nearest&&(q&1)!=0)q++;int remainder=numerator-Math.Sign(numerator)*q*2;
                At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);ushort cc=(ushort)(((q&4)<<6)|((q&2)<<13)|((q&1)<<9));
                Check(c.GetFpuRegister(0)==Float80Math.FromInteger(remainder)&&c.GetFpuRegister(1)==Float80Math.FromInteger(2)&&c.FpuTop==6);
                Check((c.FpuStatusWord&0x473f)==cc&&!c.FpuErrorAsserted);
            });
        foreach(bool nearest in new[]{false,true})foreach(int gap in new[]{63,64,65,95,96,127,256,4096,16383})foreach(bool negative in new[]{false,true})
            test($"FPREM nearest={nearest}, gap={gap}, negative={negative} exposes C2 on each partial instruction and converges",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(0xabcdef0123456789,(ushort)((0x3fff+gap)|(negative?0x8000:0)));var b=new Float80Value(0xc123456789abcdef,0x3fff);
                Load(c,m,b);Load(c,m,a,0x30010);int steps=0;var expected=a;
                do{
                    var p=Float80Math.Remainder(expected,b,nearest,0x37f);At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);steps++;
                    Check(c.GetFpuRegister(0)==p.Value&&c.GetFpuRegister(1)==b&&c.FpuTop==6&&(c.FpuStatusWord&0x473f)==p.Conditions);
                    if(steps==1)Check(((c.FpuStatusWord&0x400)!=0)==(gap>=64));
                    if((c.FpuStatusWord&0x400)==0)break;
                    // A separate save/restore between real instructions retains
                    // a resumable partial result and the unchanged divisor.
                    At(c,m,Mem(0xdd,6,0x33000));At(c,m,Mem(0xdd,4,0x33000));expected=p.Value;Check(steps<1024);
                }while(true);
                Check(steps<1024&&!c.FpuErrorAsserted);
            });
        foreach(bool nearest in new[]{false,true})foreach(bool negative in new[]{false,true})foreach(bool um in new[]{false,true})
            test($"FPREM nearest={nearest}, negative={negative}, UM={um} has exact tiny remainder or a wrapped exponent without PE",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(j,1));Load(c,m,new(j+1,(ushort)(1|(negative?0x8000:0))),0x30010);Seed(c,m,um?(ushort)0x37f:(ushort)0x36f,0);At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);
                var answer=um?new Float80Value(1,(ushort)(negative?0x8000:0)):new Float80Value(j,(ushort)(0x5fc2|(negative?0x8000:0)));
                Check(c.GetFpuRegister(0)==answer&&(c.FpuStatusWord&63)==(um?0:16)&&c.FpuErrorAsserted==!um&&(c.FpuStatusWord&0x4700)==0x200);
            });
        foreach(bool nearest in new[]{false,true})foreach(bool im in new[]{false,true})foreach(int empty in new[]{1,2,3})
            test($"FPREM nearest={nearest}, IM={im}, empty={empty} preserves missing sources on unmasked stack faults",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Math.FromInteger(3),0x30010);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,0);
                if((empty&1)!=0)At(c,m,[0xdd,0xc0]);if((empty&2)!=0)At(c,m,[0xdd,0xc1]);At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);
                Check(c.FpuTop==6&&(c.FpuStatusWord&0x641)==0x41&&c.IsFpuRegisterEmpty(0)==(!im&&(empty&1)!=0)&&c.IsFpuRegisterEmpty(1)==((empty&2)!=0));
                if(im)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);Check(c.FpuErrorAsserted==!im);
            });
        foreach(bool nearest in new[]{false,true})foreach(bool negative in new[]{false,true})foreach(int quotient in Enumerable.Range(0,8))
            test($"FPREM nearest={nearest}, sign={negative}, exact quotient={quotient} sets all three bits and retains signed zero",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);var a=Float80Math.FromInteger(negative?-quotient:quotient);if(negative&&quotient==0)a=new(0,0x8000);Load(c,m,a,0x30010);At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);
                Check(c.GetFpuRegister(0)==new Float80Value(0,(ushort)(negative?0x8000:0))&&(c.FpuStatusWord&0x4700)==((quotient&4)<<6|((quotient&2)<<13)|((quotient&1)<<9)));
            });
        foreach(bool nearest in new[]{false,true})foreach((Float80Value a,Float80Value b,ushort control,int flags,bool commit) in new (Float80Value,Float80Value,ushort,int,bool)[]{
            (new(j,0x7fff),Float80Value.One,0x37e,1,false),
            (new(1,0),Float80Value.One,0x37d,2,false),
            (new(j+1,1),new(j,1),0x36f,16,true)})
            test($"FPREM nearest={nearest}, pending={flags} gives an actual MF frame and WAIT returns through IRETD",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,(byte)(nearest?0xf5:0xf8)]);
                Check(c.FpuErrorAsserted&&(c.FpuStatusWord&63)==flags&&c.GetFpuRegister(0)==(commit?new Float80Value(j,0x5fc2):a));
                m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted);
            });
        foreach(byte modrm in new byte[]{0xf8,0xf5})foreach(uint bits in new uint[]{4,8,12})
            test($"FPREM opcode={modrm:X}, EM/TS={bits} gives real NM before pointers or operands change",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Math.FromInteger(3),0x30010);uint ip=c.FpuInstructionPointer;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,modrm}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);Check(c.FpuInstructionPointer==ip&&c.GetFpuRegister(0)==Float80Math.FromInteger(3));
            });
        foreach(byte modrm in new byte[]{0xf8,0xf5})
        {
            foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
                test($"FPREM opcode={modrm:X}, prefix={Convert.ToHexString(prefix)} preserves seven other full-stack registers and low precision bits",()=>
                {
                    var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=1;i--)Load(c,m,Float80Math.FromInteger(i));Load(c,m,new(j+1,0x3fff),0x30010);Seed(c,m,0x7f,4);
                    var original=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();c.Eflags=0x247;At(c,m,[..prefix,0xd9,modrm]);
                    Check(c.FpuTop==0&&c.Eflags==0x247&&c.GetFpuRegister(0)==new Float80Value(j,0x3fc0));for(int k=1;k<8;k++)Check(c.GetFpuRegister(k)==original[k-1]);
                });
            test($"FPREM opcode={modrm:X} split fetch PF preserves dividend and retries",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Math.FromInteger(2));Load(c,m,Float80Math.FromInteger(7),0x30010);uint ip=c.FpuInstructionPointer;
                m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,modrm);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);Check(c.FpuInstructionPointer==ip&&c.GetFpuRegister(0)==Float80Math.FromInteger(7));
                PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x3001&&c.GetFpuRegister(0)==Float80Math.FromInteger(modrm==0xf8?1:-1));
            });
            test($"FPREM opcode={modrm:X} TF traps after one partial reduction instead of running an internal completion loop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var b=new Float80Value(0xc123456789abcdef,0x3fff);var a=new Float80Value(0xabcdef0123456789,0x403f);Load(c,m,b);Load(c,m,a,0x30010);
                c.Eflags|=0x100;new byte[]{0xd9,modrm}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();Check(c.Eip==0x6000&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002&&(c.FpuStatusWord&0x400)!=0&&c.GetFpuRegister(0)==Float80Math.Remainder(a,b,modrm==0xf5,0x37f).Value);
            });
            test($"LOCK FPREM opcode={modrm:X} faults UD before any FPU effects",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;
                new byte[]{0xf0,0xd9,modrm}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw);
            });
        }
    }
}
