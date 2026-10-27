// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87StateTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value,string message="x87 state invariant failed") {if(!value)throw new Exception(message);}
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte opcode,int group,uint address=0x30000,bool address32=true) => address32 ?
        [opcode,(byte)(group*8+5),..BitConverter.GetBytes(address)] : [opcode,(byte)(group*8+6),(byte)address,(byte)(address>>8)];
    /// <summary>Writes the explicitly specified ten-byte raw extended operand into private test memory.</summary>
    private static void Raw(MemoryBus m,uint address,Float80Value value)
    {m.WriteDword(address,(uint)value.Significand);m.WriteDword(address+4,(uint)(value.Significand>>32));m.WriteWord(address+8,value.SignExponent);}
    /// <summary>Writes the explicitly specified ten-byte raw extended operand into private test memory.</summary>
    private static Float80Value Raw(MemoryBus m,uint address) => new(m.ReadDword(address)|((ulong)m.ReadDword(address+4)<<32),m.ReadWord(address+8));
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c,MemoryBus m,byte[] code)
    {code.CopyTo(m.Ram,0x2200);c.Eip=0x2200;c.Step();}
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(X86Cpu c,MemoryBus m,Float80Value value)
    {Raw(m,0x30000,value);At(c,m,Mem(0xdb,5));}
    private static readonly (Float80Value Value,int Tag,int Class)[] Values =
    [
        (new(0,0),1,0x4000),(new(0,0x8000),1,0x4200),
        (new(0x8000000000000000,0x3fff),0,0x400),(new(0x8000000000000001,0x3fff),0,0x400),
        (new(0xffffffffffffffff,0x7ffe),0,0x400),(new(1,0),2,0x4400),
        (new(0x8000000000000000,0x7fff),2,0x500),(new(0xc000000000000042,0xffff),2,0x300),
        (new(0x8000000000000042,0x7fff),2,0x100),(new(0x4000000000000000,1),2,0)
    ];
    /// <summary>Registers the named x87 state regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(var row in Values)
            test($"80-bit FLD/FSTP retains exact {row.Value.SignExponent:X4}:{row.Value.Significand:X16}, tag {row.Tag}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,row.Value);
                Check(c.FpuTop==7 && c.GetFpuRegister(0)==row.Value && c.FpuTagWord==(0x3fff|row.Tag<<14));
                At(c,m,Mem(0xdb,7,0x31000));Check(Raw(m,0x31000)==row.Value && c.FpuTop==0 && c.FpuTagWord==0xffff && c.FpuStatusWord==0);
            });
        foreach(var row in Values)
            test($"FXAM classifies exact 80-bit {row.Value.SignExponent:X4}:{row.Value.Significand:X16}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,row.Value);At(c,m,[0xd9,0xe5]);
                Check((c.FpuStatusWord&0x4700)==row.Class && c.GetFpuRegister(0)==row.Value && c.FpuTop==7);
            });
        test("FXAM reports empty without raising invalid-operation or filling a register",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xd9,0xe5]);c.Step();Check(c.FpuStatusWord==0x4100 && c.FpuTagWord==0xffff);
        });
        foreach(int slot in Enumerable.Range(0,8))
            test($"FLD ST({slot}) snapshots its source before decrementing TOP",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,new(0x8000000000000000u+(ulong)i,0x3fff));
                At(c,m,[0xdd,0xc7]); // The next push targets physical R7, now explicitly empty.
                Float80Value old=c.GetFpuRegister(slot);
                At(c,m,[0xd9,(byte)(0xc0+slot)]);
                if(slot==7)Check(c.GetFpuRegister(0)==Float80Value.Indefinite && (c.FpuStatusWord&0x41)==0x41);
                else Check(c.GetFpuRegister(0)==old && c.FpuTop==7 && (c.FpuStatusWord&0x41)==0);
            });
        foreach(int slot in Enumerable.Range(0,8))
            test($"FXCH ST({slot}) exchanges 80 bits without changing physical tag emptiness",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,new(0x8000000000000000u+(ulong)i,0x3fff));
                var first=c.GetFpuRegister(0);var second=c.GetFpuRegister(slot);At(c,m,[0xd9,(byte)(0xc8+slot)]);
                Check(c.GetFpuRegister(0)==second && c.GetFpuRegister(slot)==first && c.FpuTagWord==0 && c.FpuTop==0);
            });
        foreach(bool pop in new[]{false,true}) foreach(int slot in Enumerable.Range(0,8))
            test($"FST{(pop?"P":"")} ST({slot}) copies all significant bits and handles destination alias",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);var value=new Float80Value(0x8000000000000001,0x4000);Load(c,m,value);
                At(c,m,[0xdd,(byte)((pop?0xd8:0xd0)+slot)]);
                if(!pop)Check(c.GetFpuRegister(slot)==value && !c.IsFpuRegisterEmpty(slot));
                else Check(c.FpuTop==0 && (slot==0 || c.GetFpuRegister((slot+7)&7)==value));
            });
        test("FCHS/FABS preserve low significand bits including negative zero",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0x8000000000000001,0x3fff));At(c,m,[0xd9,0xe0]);
            Check(c.GetFpuRegister(0)==new Float80Value(0x8000000000000001,0xbfff));At(c,m,[0xd9,0xe1]);
            Check(c.GetFpuRegister(0)==new Float80Value(0x8000000000000001,0x3fff));At(c,m,[0xd9,0xee]);At(c,m,[0xd9,0xe0]);
            Check(c.GetFpuRegister(0)==new Float80Value(0,0x8000) && (c.FpuTagWord>>12&3)==1);
        });
        test("FINCSTP/FDECSTP rotate TOP without modifying register bits or physical tags",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xd9,0xe8]);c.Step();ushort tags=c.FpuTagWord;At(c,m,[0xd9,0xf7]);
            Check(c.FpuTop==0 && c.FpuTagWord==tags && c.IsFpuRegisterEmpty(0));At(c,m,[0xd9,0xf6]);Check(c.FpuTop==7 && c.GetFpuRegister(0)==Float80Value.One);
        });
        foreach(bool wide in new[]{false,true})
            test($"FSAVE{(wide?108:94)} and FRSTOR roundtrip all eight logical 80-bit registers",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<8;i++)Load(c,m,Values[i].Value);
                At(c,m,[0xd9,0xf6]); // Nonzero TOP is essential to detect physical/logical confusion.
                var before=Enumerable.Range(0,8).Select(c.GetFpuRegister).ToArray();ushort tags=c.FpuTagWord,sw=c.FpuStatusWord;
                byte[] save=Mem(0xdd,6,0x31000);byte[] restore=Mem(0xdd,4,0x31000);if(!wide){save=[0x66,..save];restore=[0x66,..restore];}
                At(c,m,save);Check(c.FpuTop==0 && c.FpuControlWord==0x37f && c.FpuTagWord==0xffff && c.FpuInstructionPointer==0);
                int env=wide?28:14;for(int i=0;i<8;i++)Check(Raw(m,0x31000u+(uint)env+(uint)i*10)==before[i]);
                Check(m.ReadWord(0x31000)==0x37f && m.ReadWord(wide?0x31004u:0x31002)==sw && m.ReadWord(wide?0x31008u:0x31004)==tags);
                At(c,m,restore);Check(c.FpuStatusWord==sw && c.FpuTagWord==tags && Enumerable.Range(0,8).All(i=>c.GetFpuRegister(i)==before[i]));
            });
        foreach(bool real in new[]{false,true}) foreach(bool wide in new[]{false,true})
            test($"{(real?"real":"protected")} {(wide?32:16)} FSTENV stores prior opcode and pointer fields",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x90]);uint basis=0;
                if(real){c=new X86Cpu(m,new());c.SetSegment(Segment.Cs,0x1234);c.SetSegment(Segment.Ds,0x2000);basis=0x12340;}
                uint address=real?0x23000u:0x3000;Raw(m,address,new(0x8000000000000001,0x3fff));
                byte[] load=Mem(0xdb,5,0x3000,!real);if(real)load=[0x66,..load];load.CopyTo(m.Ram,(int)(basis+0x2000));c.Eip=0x2000;c.Step();
                byte[] store=Mem(0xd9,6,0x3100,!real);if(wide==real)store=[0x66,..store];store.CopyTo(m.Ram,(int)(basis+0x2100));c.Eip=0x2100;c.Step();
                uint output=real?0x23100u:0x3100;Check(m.ReadWord(output)==0x37f && c.FpuTop==7);
                if(real)
                {
                    uint linear=0x14340,dat=0x23000;
                    Check(m.ReadWord(output+(wide?12u:6))==(ushort)linear);
                    Check(m.ReadWord(output+(wide?20u:10))==(ushort)dat);
                    uint packed=wide?m.ReadDword(output+16):m.ReadWord(output+8);
                    Check((packed&0x7ff)==0x32e && (wide?packed>>28:packed>>12)==(linear>>16));
                    uint high=wide?m.ReadDword(output+24)>>28:(uint)m.ReadWord(output+12)>>12;Check(high==dat>>16);
                }
                else
                {
                    Check(m.ReadWord(output+(wide?12u:6))==0x2000 && m.ReadWord(output+(wide?16u:8))==8);
                    Check(m.ReadWord(output+(wide?20u:10))==0x3000 && m.ReadWord(output+(wide?24u:12))==16);
                    if(wide)Check(m.ReadWord(output+18)==0x32d && m.ReadWord(output+2)==0xffff && m.ReadWord(output+26)==0xffff);
                }
            });
        test("FLDENV changes TOP/empty tags while preserving raw register contents",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.One);At(c,m,Mem(0xd9,6,0x31000));
            m.WriteWord(0x31004,0);m.WriteWord(0x31008,0xffff);At(c,m,Mem(0xd9,4,0x31000));
            Check(c.FpuTop==0 && c.FpuTagWord==0xffff && c.GetFpuRegister(7)==Float80Value.One);
        });
        test("FLDENV nonempty tag types are recomputed from actual physical register values",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,Float80Value.Zero);At(c,m,Mem(0xd9,6,0x31000));
            m.WriteWord(0x31008,0x3fff);At(c,m,Mem(0xd9,4,0x31000));Check(c.FpuTagWord==0x7fff);
        });
        test("masked stack overflow writes extended indefinite and wraps TOP",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);for(int i=0;i<9;i++)At(c,m,[0xd9,0xe8]);
            Check(c.FpuTop==7 && c.GetFpuRegister(0)==Float80Value.Indefinite && (c.FpuStatusWord&0x2c1)==0x241 && c.FpuTagWord==0x8000);
        });
        test("masked empty FSTP writes all ten indefinite bytes and pops once",()=>
        {
            var(c,m)=ProtectedModeTests.Rig(Mem(0xdb,7));c.Step();Check(Raw(m,0x30000)==Float80Value.Indefinite && c.FpuTop==1 && c.FpuTagWord==0xffff && (c.FpuStatusWord&0x241)==0x41);
        });
        test("unmasked empty store changes no data or TOP and defers MF until FWAIT",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);m.WriteWord(0x32000,0x37e);At(c,m,Mem(0xd9,5,0x32000));
            c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);m.Ram.AsSpan(0x30000,10).Fill(0xa5);At(c,m,Mem(0xdb,7));
            Check(c.FpuTop==0 && c.FpuStatusWord==0x80c1 && m.Ram.AsSpan(0x30000,10).ToArray().All(b=>b==0xa5));
            new byte[]{0x9b}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,16,null);Check(c.FpuStatusWord==0x80c1);
            At(c,m,[0xdb,0xe2]);Check(c.FpuStatusWord==0 && c.FpuControlWord==0x37e);
        });
        foreach(bool write in new[]{false,true}) foreach(bool environment in new[]{false,true}) foreach(bool wide in new[]{false,true})
            test($"{(write?"save":"restore")} {(environment?"environment":"full registers")} wide={wide} PF leaves FPU and first page unchanged",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,new(0x8000000000000001,0x3fff));var value=c.GetFpuRegister(0);ushort sw=c.FpuStatusWord,tags=c.FpuTagWord;
                PagingTests.Pte(m,0x30000,0x40000);PagingTests.Pte(m,0x31000,0,0);m.WriteByte(0x40fff,0xa5);
                byte[] code=Mem(environment?(byte)0xd9:(byte)0xdd,write?6:4,0x30fff);if(!wide)code=[0x66,..code];
                code.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,14,write?2u:0);
                Check(c.FpuStatusWord==sw && c.FpuTagWord==tags && c.GetFpuRegister(0)==value && m.ReadByte(0x40fff)==0xa5);
            });
        foreach(bool write in new[]{false,true})
            test($"80-bit {(write?"pop-store":"push-load")} crossing a missing page preserves TOP/raw state",()=>
            {
                var(c,m)=PagingTests.Create([0x90]);Load(c,m,Float80Value.One);PagingTests.Pte(m,0x30000,0x40000);PagingTests.Pte(m,0x31000,0,0);m.WriteByte(0x40ffc,0xa5);
                Mem(0xdb,write?7:5,0x30ffc).CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,14,write?2u:0);
                Check(c.FpuTop==7 && c.GetFpuRegister(0)==Float80Value.One && c.FpuTagWord==0x3fff && m.ReadByte(0x40ffc)==0xa5);
            });
        foreach(byte[] instruction in new byte[][]{[0xd9,0xe8],[0xd9,0xc0],[0xd9,0xe5],Mem(0xdb,5),Mem(0xdb,7),Mem(0xd9,6),Mem(0xdd,6),Mem(0xdd,4)})
            test($"new x87 state operation {Convert.ToHexString(instruction)} respects TS before any effect",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(instruction);c.SetRegister(Register.Ax,c.Cr0|8);At(c,m,[0x0f,0x22,0xc0]);c.Eip=0x2000;
                Fault(c,m,7,null);Check(c.FpuTop==0 && c.FpuTagWord==0xffff && c.FpuStatusWord==0);
            });
        foreach(uint address in new uint[]{0x30000,0x30004})
            test($"486 user TEMPREAL alignment at {address:X} requires eight bytes",()=>
            {
                var(c,m)=PagingTests.Create(Mem(0xdb,5,address));PagingTests.Tss(c,m);c.SetRegister(Register.Ax,c.Cr0|0x40000);At(c,m,[0x0f,0x22,0xc0]);
                PagingTests.User(c,m);c.SetSegment(Segment.Ds,0x33);c.Eflags|=0x40000;Raw(m,address,Float80Value.One);
                byte? entered=null;c.InterruptEntered+=v=>entered=v;c.Step();
                if((address&7)==0)Check(c.FpuTop==7 && c.GetFpuRegister(0)==Float80Value.One);
                else Check(entered==17 && c.FpuTop==0 && c.FpuTagWord==0xffff && c.AlignmentFaults==1);
            });
        test("FNSAVE data breakpoint traps after memory commit and FPU initialization",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0x8000000000000001,0x3fff));
            c.SetRegister(Register.Ax,0x3101c);At(c,m,[0x0f,0x23,0xc0]);c.SetRegister(Register.Ax,0xd0001);At(c,m,[0x0f,0x23,0xf8]);
            Mem(0xdd,6,0x31000).CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
            Check(c.DataBreakpointTraps==1 && c.Eip==0x6000 && m.ReadDword(c.GetRegister(Register.Sp))==0x2006);
            Check(c.FpuTop==0 && c.FpuTagWord==0xffff && Raw(m,0x3101c)==new Float80Value(0x8000000000000001,0x3fff));
        });
        test("CPU RESET without BIST retains all x87 register bits and tags",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Load(c,m,new(0x8000000000000001,0x3fff));c.Reset();
            Check(c.FpuTop==7 && c.FpuTagWord==0x3fff && c.GetFpuRegister(0)==new Float80Value(0x8000000000000001,0x3fff));
        });
        test("FNOP records its prefixed instruction address while control stores retain that pointer",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x66,0xd9,0xd0]);c.Step();Check(c.FpuInstructionPointer==0x2000 && c.FpuOpcode==0x1d0);
            At(c,m,Mem(0xd9,7,0x30000));Check(c.FpuInstructionPointer==0x2000 && c.FpuOpcode==0x1d0);
        });
        test("FNSTENV saves the old unmasked exception environment before masking live control",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);m.WriteWord(0x32000,0x37e);At(c,m,Mem(0xd9,5,0x32000));At(c,m,Mem(0xdb,7));
            At(c,m,Mem(0xd9,6,0x31000));Check(m.ReadWord(0x31000)==0x37e && m.ReadWord(0x31004)==0x80c1);
            Check(c.FpuControlWord==0x37f && c.FpuStatusWord==0x41);
        });
    }
}
