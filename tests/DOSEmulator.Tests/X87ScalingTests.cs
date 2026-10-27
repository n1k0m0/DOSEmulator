// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ScalingTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string detail="FSCALE invariant") {if(!value)throw new Exception(detail);}
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
                c.Eflags=0x247;At(c,m,[0xd9,0xfd]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                // C0/C2/C3 are architecturally undefined for FSCALE.
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==6,"FSCALE changed EFLAGS/TOP");
                if((kind&3)==0)
                {
                    var pure=Float80Math.ScaleByPowerOfTwo(a,b,control);var expected=Value(raw[0]);
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
    /// <summary>Registers the named x87 scaling regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        const ulong j=0x8000000000000000;
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(int numerator in new[]{-7,-3,-1,0,1,3,7})
            test($"FSCALE PC={pc}, RC={rc}, power={numerator}/2 keeps all 64 significand bits and truncates toward zero",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j+1,0x3fff);var b=Float80Math.FromInteger(numerator);
                if(numerator!=0)b=b with{SignExponent=(ushort)(b.SignExponent-1)};
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),16);c.Eflags=0x247;At(c,m,[0xd9,0xfd]);
                Check(c.GetFpuRegister(0)==a with{SignExponent=(ushort)(0x3fff+numerator/2)}&&c.GetFpuRegister(1)==b);
                Check(c.FpuTop==6&&c.Eflags==0x247&&(c.FpuStatusWord&0x23f)==4&&!c.FpuErrorAsserted);
            });
        foreach(bool masked in new[]{false,true})foreach(bool negative in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))
            test($"FSCALE overflow OM={masked}, negative={negative}, RC={rc} keeps ST1 and applies the exponent response",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j+1,(ushort)(0x7ffe|(negative?0x8000:0)));var b=Float80Value.One;
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,(ushort)((0x37f|rc<<10)&(masked?0xffff:~8)),0);At(c,m,[0xd9,0xfd]);
                bool infinite=masked&&(rc==0||rc==1&&negative||rc==2&&!negative);
                var expected=!masked?new Float80Value(j+1,(ushort)(0x1fff|(negative?0x8000:0))):infinite?new Float80Value(j,(ushort)(0x7fff|(negative?0x8000:0))):new Float80Value(ulong.MaxValue,(ushort)(0x7ffe|(negative?0x8000:0)));
                Check(c.GetFpuRegister(0)==expected&&c.GetFpuRegister(1)==b&&c.FpuTop==6);
                Check((c.FpuStatusWord&0x3f)==(masked?0x28:8)&&((c.FpuStatusWord&0x200)!=0)==infinite&&c.FpuErrorAsserted==!masked);
            });
        foreach(bool um in new[]{false,true})foreach(bool pm in new[]{false,true})foreach(bool negative in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))
            test($"FSCALE tiny halfway UM={um}, PM={pm}, negative={negative}, RC={rc} rounds or delivers an adjusted exponent",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j+1,(ushort)(1|(negative?0x8000:0)));var b=Float80Math.FromInteger(-1);
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,(ushort)((0x37f|rc<<10)&(um?0xffff:~16)&(pm?0xffff:~32)),0);At(c,m,[0xd9,0xfd]);
                bool up=um&&(rc==1&&negative||rc==2&&!negative);
                var expected=um?new Float80Value((j>>1)+(up?1ul:0),(ushort)(negative?0x8000:0)):new Float80Value(j+1,(ushort)(0x6000|(negative?0x8000:0)));
                Check(c.GetFpuRegister(0)==expected&&c.GetFpuRegister(1)==b&&c.FpuTop==6);
                Check((c.FpuStatusWord&0x3f)==(um?0x30:16)&&((c.FpuStatusWord&0x200)!=0)==up&&c.FpuErrorAsserted==(!um||!pm));
            });
        foreach(int power in new[]{-65536,-40960,-40959,-40958,40958,40959,40960,65536})foreach(int rc in Enumerable.Range(0,4))
            test($"FSCALE extreme finite power {power}, RC={rc}, checks the range after the 24576 bias",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var a=new Float80Value(j+3,0x3fff);var b=Float80Math.FromInteger(power);bool overflow=power>0;
                Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,(ushort)((0x37f|rc<<10)&~(overflow?8:16)),0);At(c,m,[0xd9,0xfd]);
                int adjusted=power+(overflow?-24576:24576);bool outside=adjusted is < -16382 or > 16383;
                var answer=outside?(overflow?new Float80Value(j,0x7fff):Float80Value.Zero):new Float80Value(j+3,(ushort)(adjusted+16383));
                Check(c.GetFpuRegister(0)==answer&&c.GetFpuRegister(1)==b&&c.FpuTop==6);
                Check((c.FpuStatusWord&0x3f)==((overflow?8:16)|(outside?32:0))&&((c.FpuStatusWord&0x200)!=0)==(outside&&overflow)&&c.FpuErrorAsserted);
            });
        foreach(bool dm in new[]{false,true})foreach((Float80Value a,Float80Value b,Float80Value expected) in new (Float80Value,Float80Value,Float80Value)[]{
            (new(1,0),Float80Value.One,new(2,0)),(new(j,0),Float80Value.Zero,new(j,1)),
            (Float80Value.One,new(1,0),Float80Value.One),(Float80Value.One,new(j,0),Float80Value.One)})
            test($"FSCALE denormal sources {a}/{b}, DM={dm}, preserve the original source on an unmasked exception",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,dm?(ushort)0x37f:(ushort)0x37d,0);At(c,m,[0xd9,0xfd]);
                Check(c.GetFpuRegister(0)==(dm?expected:a)&&c.GetFpuRegister(1)==b&&(c.FpuStatusWord&0x23f)==2&&c.FpuErrorAsserted==!dm);
            });
        foreach(bool im in new[]{false,true})foreach((Float80Value a,Float80Value b,Float80Value expected,int flag) in new (Float80Value,Float80Value,Float80Value,int)[]{
            (Float80Value.Zero,new(j,0x7fff),Float80Value.Indefinite,1),
            (new(j,0xffff),new(j,0xffff),Float80Value.Indefinite,1),
            (new(0,0x8000),new(j,0xffff),new(0,0x8000),0),
            (Float80Value.One,new(j,0x7fff),new(j,0x7fff),0),
            (Float80Value.One,new(j,0xffff),Float80Value.Zero,0),
            (Float80Value.One,new(j|0x123,0x7fff),new(j|0x4000000000000123,0x7fff),1),
            (Float80Value.One,new(j|0x4000000000000123,0xffff),new(j|0x4000000000000123,0xffff),0),
            (Float80Value.One,new(1,1),Float80Value.Indefinite,1)})
            test($"FSCALE special {a}/{b}, IM={im}, has a defined separate commit and preserves ST1",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xfd]);
                Check(c.GetFpuRegister(0)==(im||flag==0?expected:a)&&c.GetFpuRegister(1)==b&&c.FpuTop==6);
                Check((c.FpuStatusWord&0x23f)==flag&&c.FpuErrorAsserted==(flag!=0&&!im));
            });
        foreach(bool im in new[]{false,true})foreach(int empty in new[]{1,2,3})
            test($"FSCALE missing source mask={empty}, IM={im}, fills only ST0 when masked and never pops",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Math.FromInteger(3),0x30010);Seed(c,m,im?(ushort)0x37f:(ushort)0x37e,0);
                if((empty&1)!=0)At(c,m,[0xdd,0xc0]);if((empty&2)!=0)At(c,m,[0xdd,0xc1]);At(c,m,[0xd9,0xfd]);
                Check(c.FpuTop==6&&(c.FpuStatusWord&0x241)==0x41&&c.IsFpuRegisterEmpty(1)==((empty&2)!=0));
                Check(c.IsFpuRegisterEmpty(0)==(!im&&(empty&1)!=0));if(im)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FSCALE prefix {Convert.ToHexString(prefix)} preserves all seven other full-stack registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=1;i--)Load(c,m,Float80Math.FromInteger(i==1?3:100+i));Load(c,m,new(j+1,0x3fff));
                var old=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,0xfd]);
                Check(c.FpuTop==0&&c.GetFpuRegister(0)==new Float80Value(j+1,0x4002));for(int i=1;i<8;i++)Check(c.GetFpuRegister(i)==old[i-1]);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FSCALE EM/TS={bits:X} gives actual NM before state and pointers change",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);Load(c,m,Float80Math.FromInteger(3),0x30010);uint ip=c.FpuInstructionPointer;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,0xfd}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip&&c.GetFpuRegister(0)==Float80Math.FromInteger(3)&&c.FpuStatusWord==0x3000);
            });
        foreach((Float80Value a,Float80Value b,ushort control,ushort flags,Float80Value answer) in new (Float80Value,Float80Value,ushort,ushort,Float80Value)[]{
            (new(j,0x7ffe),Float80Value.One,0x377,8,new(j,0x1fff)),
            (new(j+1,1),Float80Math.FromInteger(-1),0x36f,16,new(j+1,0x6000)),
            (new(j+1,1),Float80Math.FromInteger(-1),0x35f,0x30,new(j>>1,0)),
            (Float80Value.One,new(1,0),0x37d,2,Float80Value.One),
            (Float80Value.Zero,new(j,0x7fff),0x37e,1,Float80Value.Zero)})
            test($"FSCALE pending flags={flags:X} gives a real MF frame and FNCLEX/IRETD retries WAIT",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,b);Load(c,m,a,0x30010);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);At(c,m,[0xd9,0xfd]);
                Check(c.FpuErrorAsserted&&(c.FpuStatusWord&0x3f)==flags&&c.GetFpuRegister(0)==answer&&c.GetFpuRegister(1)==b);
                m.WriteByte(0x2000,0x9b);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();Check(c.Eip==0x2000);
                c.Step();Check(c.Eip==0x2001&&!c.FpuErrorAsserted&&c.GetFpuRegister(0)==answer&&c.GetFpuRegister(1)==b);
            });
        test("FSCALE split opcode fetch PF preserves both sources and retries after PTE repair",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Math.FromInteger(2));Load(c,m,new(j+1,0x3fff),0x30010);uint ip=c.FpuInstructionPointer;
            m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xfd);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
            Check(c.FpuInstructionPointer==ip&&c.GetFpuRegister(0)==new Float80Value(j+1,0x3fff)&&c.GetFpuRegister(1)==Float80Math.FromInteger(2));
            PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
            Check(c.Eip==0x3001&&c.GetFpuRegister(0)==new Float80Value(j+1,0x4001)&&(c.FpuStatusWord&0x3f)==0);
        });
        test("FSCALE TF trap observes the completed 80-bit result and unchanged ST1",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(2));Load(c,m,new(j+1,0x3fff),0x30010);
            c.Eflags|=0x100;new byte[]{0xd9,0xfd}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
            Check(c.Eip==0x6000&&m.ReadDword(c.GetRegister(Register.Sp))==0x2002&&c.GetFpuRegister(0)==new Float80Value(j+1,0x4001)&&c.GetFpuRegister(1)==Float80Math.FromInteger(2));
        });
        test("LOCK FSCALE raises UD before FPU pointer, flags or operands change",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(2));Load(c,m,Float80Value.One,0x30010);uint ip=c.FpuInstructionPointer;ushort sw=c.FpuStatusWord;
            new byte[]{0xf0,0xd9,0xfd}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,6,null);
            Check(c.FpuInstructionPointer==ip&&c.FpuStatusWord==sw&&c.GetFpuRegister(0)==Float80Value.One&&c.GetFpuRegister(1)==Float80Math.FromInteger(2));
        });
    }
}
