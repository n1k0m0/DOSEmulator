using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87SquareRootTests
{
    private static void Check(bool value,string detail="FSQRT invariant") {if(!value)throw new Exception(detail);}
    private static void At(X86Cpu c,MemoryBus m,byte[] code,uint address=0x2200)
    {code.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step();}
    private static byte[] Mem(byte op,int group,uint address=0x31000)=>[op,(byte)(group*8+5),..BitConverter.GetBytes(address)];
    private static void Load(X86Cpu c,MemoryBus m,Float80Value v,uint address=0x30000)
    {m.WriteDword(address,(uint)v.Significand);m.WriteDword(address+4,(uint)(v.Significand>>32));m.WriteWord(address+8,v.SignExponent);At(c,m,Mem(0xdb,5,address));}
    private static void Seed(X86Cpu c,MemoryBus m,ushort control,int kind)
    {
        int initial=(kind&12) switch {4=>0,8=>0x100,12=>0x4000,_=>0x4500};
        At(c,m,Mem(0xd9,6,0x32000));m.WriteWord(0x32000,control);
        m.WriteWord(0x32004,(ushort)((c.FpuStatusWord&~0x4700)|0x200|initial|((kind&16)!=0?4:0)));
        At(c,m,Mem(0xd9,4,0x32000));
    }
    private static Float80Value Value(JsonElement v)=>new(Convert.ToUInt64(v.GetProperty("significand").GetString(),16),Convert.ToUInt16(v.GetProperty("exponent").GetString(),16));
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
                c.Eflags=0x247;At(c,m,[0xd9,0xfa]);ushort expectedStatus=e.GetProperty("status").GetUInt16();
                // C0/C2/C3 are architecturally undefined for FSQRT.
                Check((c.FpuStatusWord&~0x4500)==(expectedStatus&~0x4500),$"SW {c.FpuStatusWord:X4} vs {expectedStatus:X4}");
                Check(c.FpuControlWord==e.GetProperty("control").GetUInt16() && c.FpuTagWord==e.GetProperty("tags").GetUInt16(),"CW/tags differ");
                var raw=e.GetProperty("registers").EnumerateArray().ToArray();
                for(int i=0;i<8;i++)if(!c.IsFpuRegisterEmpty(i))Check(c.GetFpuRegister(i)==Value(raw[i]),$"Raw ST({i}) differs");
                Check(c.Eflags==0x247 && c.FpuTop==6,"FSQRT changed EFLAGS/TOP");
                if((kind&1)==0)
                {
                    var pure=Float80Math.SquareRoot(a,control);var expected=Value(raw[0]);
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
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(bool masked in new[]{false,true})
            test($"FSQRT sqrt(2), PC={pc}, RC={rc}, PM={masked}, retains exact precision bits",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(2));
                Seed(c,m,(ushort)((0x7f|pc<<8|rc<<10)&(masked?0xffff:~32)),0);
                ulong floor=pc==0?0xb504f30000000000:pc==2?0xb504f333f9de6000:0xb504f333f9de6484;
                bool up=rc==2||rc==0&&pc==2;
                ulong answer=floor+(up?(pc==0?1ul<<40:pc==2?1ul<<11:1):0);
                c.Eflags=0x247;At(c,m,[0xd9,0xfa]);
                Check(c.GetFpuRegister(0)==new Float80Value(answer,0x3fff) && c.FpuTop==7 && c.Eflags==0x247);
                Check((c.FpuStatusWord&0x3f)==32 && ((c.FpuStatusWord&0x200)!=0)==up && c.FpuErrorAsserted==!masked);
            });
        foreach(int pc in new[]{0,2,3})foreach(int rc in Enumerable.Range(0,4))foreach(long root in new long[]{0,1,2,3,7,9,100})
            test($"FSQRT perfect square {root}^2, PC={pc}, RC={rc}, has no precision exception",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(root*root));Seed(c,m,(ushort)(0x7f|pc<<8|rc<<10),0);
                At(c,m,[0xd9,0xfa]);Check(c.GetFpuRegister(0)==Float80Math.FromInteger(root) && (c.FpuStatusWord&0x23f)==0 && c.FpuTop==7);
            });
        foreach(bool masked in new[]{false,true})foreach(var value in new Float80Value[]{
            new(0,0x8000),new(0x8000000000000000,0x7fff),new(0x8000000000000000,0xffff),new(0x8000000000000000,0xbfff),
            new(1,0x8000),new(0xc000000000000042,0xffff),new(0x8000000000000042,0x7fff),new(1,0x1234)})
            test($"FSQRT special {value.Significand:X16}/{value.SignExponent:X4}, IM={masked}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xfa]);
                bool nan=value.Exponent==0x7fff&&value.Significand>0x8000000000000000;
                bool invalid=value.Significand==1||value.Negative&&value.Significand!=0&&!nan||nan&&(value.Significand&0x4000000000000000)==0;
                var answer=!invalid||!masked?value:nan?value with{Significand=value.Significand|0x4000000000000000}:Float80Value.Indefinite;
                Check(c.GetFpuRegister(0)==answer && (c.FpuStatusWord&0x23f)==(invalid?1:0) && c.FpuErrorAsserted==(invalid&&!masked));
            });
        foreach(bool masked in new[]{false,true})foreach(int rc in Enumerable.Range(0,4))foreach(ulong sig in new ulong[]{1,2,0x8000000000000000})
            test($"FSQRT tiny/pseudo-denormal {sig:X}, DM={masked}, RC={rc}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var value=new Float80Value(sig,0);Load(c,m,value);
                Seed(c,m,(ushort)((0x37f|rc<<10)&(masked?0xffff:~2)),0);At(c,m,[0xd9,0xfa]);
                var answer=!masked?value:sig==2?new Float80Value(0x8000000000000000,0x1fe1):sig==0x8000000000000000?new Float80Value(sig,0x2000):new Float80Value(0xb504f333f9de6484+(rc==2?1ul:0),0x1fe0);
                Check(c.GetFpuRegister(0)==answer && (c.FpuStatusWord&0x3f)==(!masked?2:sig==1?0x22:2));
                Check(((c.FpuStatusWord&0x200)!=0)==(masked&&sig==1&&rc==2));
            });
        foreach(byte[] prefix in new byte[][]{[],[0x66],[0x67],[0x66,0x67]})
            test($"FSQRT register prefix {Convert.ToHexString(prefix)} preserves seven other full-stack registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=7;i>=1;i--)Load(c,m,Float80Math.FromInteger(i+100));Load(c,m,Float80Math.FromInteger(9));
                var old=Enumerable.Range(1,7).Select(c.GetFpuRegister).ToArray();At(c,m,[..prefix,0xd9,0xfa]);
                Check(c.FpuTop==0 && c.GetFpuRegister(0)==Float80Math.FromInteger(3));for(int i=1;i<8;i++)Check(c.GetFpuRegister(i)==old[i-1]);
            });
        foreach(bool masked in new[]{false,true})
            test($"FSQRT empty ST0, IM={masked}, preserves TOP",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Seed(c,m,masked?(ushort)0x37f:(ushort)0x37e,0);At(c,m,[0xd9,0xfa]);
                Check(c.FpuTop==0 && (c.FpuStatusWord&0x241)==0x41 && c.IsFpuRegisterEmpty(0)==!masked);
                if(masked)Check(c.GetFpuRegister(0)==Float80Value.Indefinite);
            });
        foreach(uint bits in new uint[]{4,8,12})
            test($"FSQRT CR0 EM/TS={bits:X} faults before any FPU mutation",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Math.FromInteger(2));uint ip=c.FpuInstructionPointer;
                c.SetRegister(Register.Ax,c.Cr0|bits);At(c,m,[0x0f,0x22,0xc0]);new byte[]{0xd9,0xfa}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==Float80Math.FromInteger(2) && c.FpuStatusWord==0x3800);
            });
        foreach((Float80Value value,ushort control,ushort flag,Float80Value answer) in new (Float80Value,ushort,ushort,Float80Value)[]{
            (Float80Math.FromInteger(2),0x35f,32,new(0xb504f333f9de6484,0x3fff)),
            (new(1,0),0x37d,2,new(1,0)),(Float80Math.FromInteger(-1),0x37e,1,Float80Math.FromInteger(-1))})
            test($"FSQRT pending {flag:X} gives actual MF and FNCLEX/IRETD retries WAIT",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,value);Seed(c,m,control,0);c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);
                At(c,m,[0xd9,0xfa]);Check(c.FpuErrorAsserted && (c.FpuStatusWord&0x3f)==flag && c.GetFpuRegister(0)==answer);
                new byte[]{0x9b}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);
                c.Step();c.Step();Check(c.Eip==0x2000);c.Step();Check(c.Eip==0x2001 && !c.FpuErrorAsserted && c.GetFpuRegister(0)==answer);
            });
        test("FSQRT split opcode fetch PF preserves the original operand and retries after repairing the PTE",()=>
        {
            var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Math.FromInteger(9));uint ip=c.FpuInstructionPointer;
            m.WriteByte(0x2fff,0xd9);m.WriteByte(0x3000,0xfa);PagingTests.Pte(m,0x3000,0,0);c.Eip=0x2fff;Fault(c,m,14,0,0x2fff);
            Check(c.FpuInstructionPointer==ip && c.GetFpuRegister(0)==Float80Math.FromInteger(9) && c.FpuStatusWord==0x3800);
            PagingTests.Pte(m,0x3000,0x3000);new byte[]{0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);c.Step();c.Step();c.Step();
            Check(c.Eip==0x3001 && c.GetFpuRegister(0)==Float80Math.FromInteger(3) && (c.FpuStatusWord&0x3f)==0);
        });
    }
}
