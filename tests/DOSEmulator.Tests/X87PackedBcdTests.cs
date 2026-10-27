// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87PackedBcdTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="x87 BCD invariant") {if(!value)throw new Exception(detail);}
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
                byte[] packed=Convert.FromHexString(v.GetProperty("packed").GetString()!);packed.CopyTo(m.Ram,0x31000);
                m.Ram[0x30fff]=0x5a;m.Ram[0x3100a]=0xa5;
                c.Eflags=0x247;At(c,m,Mem(0xdf,op==0?4:6,0x31000));ushort expectedStatus=e.GetProperty("status").GetUInt16();
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==(expectedStatus>>11&7),"BCD EFLAGS/TOP");
                Check(Convert.ToHexString(m.Ram.AsSpan(0x31000,10))==e.GetProperty("memory80").GetString(),"Ten BCD memory bytes differ");
                Check(m.Ram[0x30fff]==0x5a && m.Ram[0x3100a]==0xa5,"BCD bounds");
                if(op==0 && (kind&15)<8)Check(c.GetFpuRegister(0)==Float80Math.LoadPackedBcd(packed),"Pure BCD load differs");
                if(op==1 && (kind&15)>0)
                {
                    var numeric=Float80Math.StorePackedBcd(b,control);byte[] destination=(byte[])packed.Clone();
                    if(numeric.Commit){BitConverter.GetBytes(numeric.Low).CopyTo(destination,0);BitConverter.GetBytes(numeric.High).CopyTo(destination,8);}
                    Check(destination.AsSpan().SequenceEqual(m.Ram.AsSpan(0x31000,10)),"Pure BCD store differs");
                }
                checkedCases++;
            }
            catch(Exception ex){failures.Add(new{id,error=ex.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{passed=failures.Count==0,vectors=vectors.Length,checkedCases,failed=failures.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
        return failures.Count==0?0:1;
    }

    /// <summary>Builds an explicit packed-BCD test operand from decimal digits, sign and optional undefined padding.</summary>
    private static byte[] Decimal(long absolute,bool negative=false,byte padding=0)
    {
        byte[] bytes=new byte[10];for(int i=0;i<18;i++){bytes[i/2]|=(byte)((absolute%10)<<(4*(i%2)));absolute/=10;}
        bytes[9]=(byte)(padding|(negative?128:0));return bytes;
    }
    /// <summary>Publishes the device command's result bytes and interrupt state.</summary>
    private static byte[] Result(PackedBcdResult result)=>[..BitConverter.GetBytes(result.Low),..BitConverter.GetBytes(result.High)];
    /// <summary>Registers the named x87 packed bcd regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})
            test($"FBLD exact 18 digits, PC={pc}, RC={rc}, sign={negative}, including negative zero and ignored sign-byte bits",()=>
            {
                foreach(long n in new long[]{0,1,9,10,99,100,123456789012345678,999999999999999999})foreach(byte padding in new byte[]{0,0x7f})
                {
                    var(c,m)=ProtectedModeTests.Rig([0x90]);Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&~32),0xf0);
                    Decimal(n,negative,padding).CopyTo(m.Ram,0x31000);c.Eflags=0x247;At(c,m,Mem(0xdf,4,0x31000));
                    var expected=Float80Math.FromInteger(n) with{SignExponent=(ushort)(Float80Math.FromInteger(n).SignExponent|(negative?0x8000:0))};
                    Check(c.GetFpuRegister(0)==expected && c.FpuTop==7 && (c.FpuStatusWord&0x23f)==4 && c.Eflags==0x247);
                    Check(m.Ram.AsSpan(0x31000,10).SequenceEqual(Decimal(n,negative,padding)));
                }
            });
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})
            test($"FBSTP half rounding PC={pc}, RC={rc}, sign={negative}, commits memory and pop even when precision is unmasked",()=>
            {
                foreach(bool masked in new[]{false,true})foreach(long n in new long[]{0,1,2,3})
                {
                    var(c,m)=ProtectedModeTests.Rig([0x90]);var value=Float80Math.Calculate(Float80Math.FromInteger(n),new(0x8000000000000000,0x3ffe),Float80Operation.Add,0x37f).Value;
                    if(negative)value=value with{SignExponent=(ushort)(value.SignExponent|0x8000)};
                    Load(c,m,value);Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&~(masked?0:32)),0);
                    m.Ram.AsSpan(0x31000,12).Fill(0xa5);At(c,m,Mem(0xdf,6,0x31000));
                    bool up=rc==0?(n&1)!=0:rc==1?negative:rc==2&&!negative;
                    Check(m.Ram.AsSpan(0x31000,10).SequenceEqual(Decimal(n+(up?1:0),negative)));
                    Check(c.FpuTop==0 && c.FpuTagWord==0xffff && (c.FpuStatusWord&0x23f)==(32|(up?512:0)) && c.FpuErrorAsserted==!masked);
                    Check(m.ReadWord(0x3100a)==0xa5a5);
                }
            });
        foreach(int rc in Enumerable.Range(0,4))foreach(bool negative in new[]{false,true})foreach(bool masked in new[]{false,true})
            test($"FBSTP range test follows rounding RC={rc}, sign={negative}, IM={masked}",()=>
            {
                // 10^18 - 1/2: exactly represented at full extended precision.
                var value=new Float80Value(((ulong)1_000_000_000_000_000_000<<4)-8,(ushort)(0x403a|(negative?0x8000:0)));
                var result=Float80Math.StorePackedBcd(value,(ushort)((0x37f|rc<<10)&~(masked?0:1)));
                bool overflow=rc==0||rc==1&&negative||rc==2&&!negative;
                Check(result.Exceptions==(overflow?1:32) && result.Commit==(!overflow||masked) && !result.RoundedUp);
                if(!overflow)Check(Result(result).SequenceEqual(Decimal(999999999999999999,negative)));
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,(ushort)((0x37f|rc<<10)&~(masked?0:1)),0);
                m.Ram.AsSpan(0x31000,10).Fill(0xa5);At(c,m,Mem(0xdf,6,0x31000));
                Check(c.FpuTop==(result.Commit?0:7) && m.Ram.AsSpan(0x31000,10).SequenceEqual(result.Commit?Result(result):Enumerable.Repeat((byte)0xa5,10).ToArray()));
            });
        foreach(Float80Value value in new Float80Value[]{new(0xc000000000001234,0x7fff),new(0x8000000000001234,0x7fff),new(0x8000000000000000,0x7fff),new(0x8000000000000000,0xffff),new(1,0x4000),new(0,0x7fff),new(ulong.MaxValue,0x7ffe)})
        foreach(bool masked in new[]{false,true})
            test($"FBSTP invalid {value}, IM={masked}, canonical indefinite versus preserved bytes and raw source",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0x20);
                m.Ram.AsSpan(0x31000,10).Fill(0xa5);At(c,m,Mem(0xdf,6,0x31000));
                Check((c.FpuStatusWord&0x23f)==1 && c.FpuTop==(masked?0:7) && c.FpuErrorAsserted==!masked);
                Check(Convert.ToHexString(m.Ram.AsSpan(0x31000,10))==(masked?"00000000000000C0FFFF":"A5A5A5A5A5A5A5A5A5A5"));
                if(!masked)Check(c.GetFpuRegister(0)==value);
            });
        foreach(bool load in new[]{false,true})foreach(bool masked in new[]{false,true})
            test($"BCD {(load?"full push":"empty pop")}, IM={masked}, stack fault and commit rules",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);if(load)for(int i=0;i<8;i++)Load(c,m,Float80Math.FromInteger(i+1));
                Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);Decimal(999999999999999999).CopyTo(m.Ram,0x31000);At(c,m,Mem(0xdf,load?4:6,0x31000));
                Check((c.FpuStatusWord&0x241)==(load?0x241:0x41) && c.FpuTop==(masked?(load?7:1):0));
                if(load&&masked)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
                if(!load)Check(Convert.ToHexString(m.Ram.AsSpan(0x31000,10))==(masked?"00000000000000C0FFFF":"99999999999999999900"));
            });
        foreach(bool store in new[]{false,true})foreach(int firstBytes in Enumerable.Range(1,9))
            test($"BCD {(store?"store":"load")} after {firstBytes} bytes hits PF before all FPU/data effects; repaired IRETD retries once",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Math.FromInteger(123456789012345678));
                uint address=0x31000u-(uint)firstBytes;byte[] old=Decimal(999999999999999999);
                old.CopyTo(m.Ram,0x40000+(int)(address&4095));PagingTests.Pte(m,0x30000,0x40000);PagingTests.Pte(m,0x31000,0,0);
                c.SetRegister(Register.Ax,c.Cr3);At(c,m,[0x0f,0x22,0xd8]); // The prior FLD warmed this page's old TLB mapping.
                uint ip=c.FpuInstructionPointer;Mem(0xdf,store?6:4,address).CopyTo(m.Ram,0x2000);c.Eip=0x2000;
                Fault(c,m,14,store?2u:0);Check(c.FpuTop==7 && c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==Float80Math.FromInteger(123456789012345678),"PF FPU preflight");
                Check(m.Ram.AsSpan(0x40000+(int)(address&4095),firstBytes).SequenceEqual(old.AsSpan(0,firstBytes)),"PF memory preflight");
                PagingTests.Pte(m,0x31000,0x41000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();Check(c.Eip==0x2000,$"IRET target {c.Eip:X}");c.Step();
                Check(c.Eip==0x2006 && c.FpuTop==(store?0:6),$"Retry IP={c.Eip:X} TOP={c.FpuTop} PF={c.PageFaults} CR2={c.Cr2:X}");if(!store)Check(c.GetFpuRegister(0)==Float80Math.FromInteger(999999999999999999));
                else Check(m.Ram.AsSpan(0x40000+(int)(address&4095),10).SequenceEqual(Decimal(123456789012345678)));
            });
        foreach(int group in new[]{4,6})foreach(uint bits in new uint[]{4,8,12})
            test($"BCD /{group}, EM/TS {bits}, NM before even a missing operand page",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);uint ip=c.FpuInstructionPointer;PagingTests.Pte(m,0x31000,0,0);
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);Mem(0xdf,group,0x31000).CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.PageFaults==0 && c.FpuTop==7 && c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==Float80Value.One);
            });
        foreach(int group in new[]{4,6})foreach(bool stack in new[]{false,true})
            test($"BCD /{group}, {(stack?"SS":"DS")} segment limit covers the tenth byte before numeric effects",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Descriptor(m,40,0,0x31007,0x92);c.SetSegment(stack?Segment.Ss:Segment.Ds,40);
                byte[] prefix=stack?[0x36]:[];byte[] instruction=[..prefix,..Mem(0xdf,group,0x31000)];instruction.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,stack?(byte)12:(byte)13,0);
                Check(c.FpuTop==7 && c.GetFpuRegister(0)==Float80Value.One);
            });
        foreach(int group in new[]{4,6})foreach(uint address in new uint[]{0x30000,0x30001,0x30004})
            test($"BCD /{group}, user alignment {address:X}, reference 8-byte operand alignment",()=>
            {
                var(c,m)=PagingTests.Create(Mem(0xdf,group,address));Load(c,m,Float80Value.One);PagingTests.Tss(c,m);c.SetRegister(Register.Ax,c.Cr0|0x40000);At(c,m,[0x0f,0x22,0xc0]);
                PagingTests.User(c,m);c.SetSegment(Segment.Ds,0x33);c.Eflags|=0x40000;Decimal(1).CopyTo(m.Ram,(int)address);byte? entered=null;c.InterruptEntered+=v=>entered=v;c.Step();
                if((address&7)==0)Check(c.FpuTop==(group==4?6:0));else Check(entered==17 && c.AlignmentFaults==1 && c.FpuTop==7);
            });
        foreach(int group in new[]{4,6})
            test($"BCD /{group}, pending MF precedes missing operand, FNCLEX/IRETD and retry succeed",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);Seed(c,m,0x37e,0);c.SetRegister(Register.Ax,c.Cr0|32);At(c,m,[0x0f,0x22,0xc0]);
                var bad=Float80Value.Indefinite;Load(c,m,bad);At(c,m,Mem(0xdf,6,0x31000));Check(c.FpuErrorAsserted && c.FpuTop==6);
                PagingTests.Pte(m,0x33000,0,0);Mem(0xdf,group,0x33000).CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);Check(c.PageFaults==0);
                PagingTests.Pte(m,0x33000,0x33000);Decimal(42).CopyTo(m.Ram,0x33000);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();Check(c.Eip==0x2006);
            });
        foreach(int group in new[]{4,6})
            test($"BCD /{group}, TF trap sees completed push or store/pop",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(42));Decimal(17).CopyTo(m.Ram,0x31000);
                c.Eflags|=0x100;Mem(0xdf,group,0x31000).CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
                Check(c.Eip==0x6000 && m.ReadDword(c.GetRegister(Register.Sp))==0x2006 && c.FpuTop==(group==4?6:0));
                if(group==6)Check(m.Ram.AsSpan(0x31000,10).SequenceEqual(Decimal(42)));
            });
        test("FBLD undefined digit fallback is deterministic without claiming architecture validity",()=>
        {
            var value=Float80Math.LoadPackedBcd(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFF"));
            Check(value==Float80Math.FromInteger(-1666666666666666665));
        });
    }
}
