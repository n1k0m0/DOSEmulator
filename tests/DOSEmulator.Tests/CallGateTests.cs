using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class CallGateTests
{
    private static void Check(bool condition, string message="Call gate invariant failed")
    { if(!condition)throw new Exception(message); }
    private static void At(X86Cpu c,MemoryBus m,byte[] bytes,uint address=0x2200)
    { bytes.CopyTo(m.Ram,(int)address);c.Eip=address;c.Step(); }
    private static void SetGate(MemoryBus m,int width=4,byte count=2,ushort targetSelector=0x50,uint target=0x5000,byte? access=null,uint address=0x438)
    {
        m.WriteDword(address,(target&65535)|((uint)targetSelector<<16));
        m.WriteDword(address+4,(target&0xffff0000)|((uint)(access??(width==4?0xec:0xe4))<<8)|count);
    }
    private static byte[] Transfer(bool operand32,bool jump=false,ushort selector=0x3b)
    {
        var bytes=new List<byte>{(byte)(jump?0xea:0x9a)};
        bytes.AddRange(operand32?new byte[]{0xef,0xbe,0xad,0xde}:new byte[]{0xef,0xbe});
        bytes.Add((byte)selector);bytes.Add((byte)(selector>>8));return bytes.ToArray();
    }
    private static (X86Cpu Cpu,MemoryBus Memory,uint OldPointer,uint NewPointer) Create(int width=4,byte count=2,
        bool old32=true,bool new32=true,bool tss32=true,bool paging=false,bool wp=true,bool? caller32=null,Action<MemoryBus>? beforeUser=null)
    {
        var(c,m)=paging?PagingTests.Create([0xf4],wp:wp):ProtectedModeTests.Rig([0xf4]);
        bool code32=caller32??width==2;
        Descriptor(m,0x28,0,code32?0xfffffu:0xffff,0xfa,(byte)(code32?12:0));
        Descriptor(m,0x30,0x30000,old32?0xfffffu:0xffff,0xf2,(byte)(old32?12:0));
        Descriptor(m,0x50,0,width==4?0xfffffu:0xffff,0x9a,(byte)(width==4?12:0));
        Descriptor(m,0x58,0x60000,new32?0xfffffu:0xffff,0x92,(byte)(new32?12:0));
        Descriptor(m,0x40,0x9000,(uint)(tss32?0x67:0x2b),(byte)(tss32?0x89:0x81));
        uint pointer=tss32?(new32?0x1a000u:0x13578000u):0x8000;
        if(tss32){m.WriteDword(0x9004,pointer);m.WriteWord(0x9008,0x58);}
        else{m.WriteWord(0x9002,(ushort)pointer);m.WriteWord(0x9004,0x58);}
        c.SetRegister(Register.Ax,0x40);At(c,m,[0x0f,0x00,0xd8]);
        uint old=old32?0x1b000u:0xabcd8000;
        if(beforeUser is not null)
        {
            beforeUser(m);c.SetRegister(Register.Ax,c.Cr3);At(c,m,[0x0f,0x22,0xd8]);
        }
        uint sp=c.GetRegister(Register.Sp);
        m.WriteDword(sp,0x2000);m.WriteDword(sp+4,0x2b);m.WriteDword(sp+8,2);
        m.WriteDword(sp+12,old);m.WriteDword(sp+16,0x33);
        At(c,m,[0xcf]);Check(c.CurrentPrivilegeLevel==3 && c.GetSegment(Segment.Ss)==0x33);old=c.GetRegister(Register.Sp);
        c.SetSegment(Segment.Ds,0x33);SetGate(m,width,count);
        byte[] code=Transfer(code32);code.CopyTo(m.Ram,0x2000);c.Eip=0x2000;
        for(int i=0;i<(count&31);i++)Write(m,0x30000+(old&(old32?uint.MaxValue:65535))+(uint)(i*width),width,Pattern(i));
        m.Ram.AsSpan(0x67000,0x14000).Fill(0xa5);
        return(c,m,old,pointer);
    }
    private static uint Pattern(int i)=>0x87654321u+(uint)i*0x0103070b;
    private static uint Read(MemoryBus m,uint a,int width)=>width==4?m.ReadDword(a):m.ReadWord(a);
    private static void Write(MemoryBus m,uint a,int width,uint value)
    { if(width==4)m.WriteDword(a,value);else m.WriteWord(a,(ushort)value); }
    private static uint Mask(int width)=>width==4?uint.MaxValue:65535;
    private static uint Fault(X86Cpu c,MemoryBus m,byte vector,uint error,uint cr2=0)
    {
        Gate(m,vector,0x2b);uint old=c.GetRegister(Register.Sp);var ss=c.GetSegmentCache(Segment.Ss);byte? seen=null;
        c.InterruptEntered+=v=>seen=v;c.Step();uint frame=(old-16)&(ss.Default32?uint.MaxValue:65535);
        Check(seen==vector && c.Eip==0x6000 && !c.TripleFaulted,$"Expected #{vector}/{error:X}, got {seen} at {c.Position}");
        Check(c.CurrentPrivilegeLevel==3 && c.GetSegment(Segment.Ss)==0x33 && (c.GetRegister(Register.Sp)&(ss.Default32?uint.MaxValue:65535))==frame);
        Check(m.ReadWord(ss.Base+frame)==error && m.ReadDword(ss.Base+frame+4)==0x2000,"Wrong error or restart IP");
        Check((m.ReadDword(ss.Base+frame+12)&0x10000)!=0 && c.CallGateTransfers==0 && c.PrivilegeStackSwitches==0);
        Check(m.Ram.AsSpan(0x67000,0x14000).ToArray().All(b=>b==0xa5),"Failed gate partially wrote the kernel frame");
        if(vector==14)Check(c.Cr2==cr2,$"CR2 {c.Cr2:X} != {cr2:X}");
        return frame;
    }

    public static void AddTo(Action<string,Action> test)
    {
        foreach(int width in new[]{2,4})foreach(bool old32 in new[]{false,true})foreach(bool new32 in new[]{false,true})
        foreach(bool tss32 in new[]{false,true})foreach(byte count in new byte[]{0,2,31})
            test($"call gate {width*8}-bit count {count} caller SS{(old32?32:16)} target SS{(new32?32:16)} TSS{(tss32?32:16)} copies and RETF cleans both stacks",()=>
            {
                var(c,m,old,pointer)=Create(width,count,old32,new32,tss32);uint flags=0x40ed7;c.Eflags=flags;
                bool caller32=width==2;uint next=(uint)(0x2000+(caller32?7:5));
                c.Step();uint sp=(pointer-(uint)((count+4)*width))&(new32?uint.MaxValue:65535);
                Check(c.CurrentPrivilegeLevel==0 && c.GetSegment(Segment.Cs)==0x50 && c.Eip==0x5000);
                Check(c.GetSegment(Segment.Ss)==0x58 && (c.GetRegister(Register.Sp)&(new32?uint.MaxValue:65535))==sp && c.Eflags==flags);
                uint physical=0x60000+sp;
                Check(Read(m,physical,width)==next && Read(m,physical+(uint)width,width)==0x2b);
                for(int i=0;i<count;i++)Check(Read(m,physical+(uint)((i+2)*width),width)==(Pattern(i)&Mask(width)),"Parameter order/value");
                Check(Read(m,physical+(uint)((count+2)*width),width)==(old&Mask(width)) && Read(m,physical+(uint)((count+3)*width),width)==0x33);
                Check(m.ReadByte(physical-1)==0xa5 && m.ReadByte(physical+(uint)((count+4)*width))==0xa5);
                Check(c.CallGateTransfers==1 && c.PrivilegeStackSwitches==1 && m.ReadByte(0x43d)==(width==4?0xec:0xe4));
                long outerBefore=c.OuterPrivilegeReturns;
                new byte[]{0xca,(byte)(count*width),0}.CopyTo(m.Ram,0x5000);c.Step();
                uint expected=old;
                if(width==2 && old32)expected=0x00330000|(old&65535); // documented 486 16-bit interlevel-return quirk
                expected=unchecked(expected+(uint)(count*width))&(old32?uint.MaxValue:65535);
                Check(c.Eip==next && c.CurrentPrivilegeLevel==3 && c.GetSegment(Segment.Cs)==0x2b && c.GetSegment(Segment.Ss)==0x33);
                Check((c.GetRegister(Register.Sp)&(old32?uint.MaxValue:65535))==expected && c.Eflags==flags && c.OuterPrivilegeReturns==outerBefore+1);
            });
        foreach(int width in new[]{2,4})foreach(bool call in new[]{true,false})foreach(bool conforming in new[]{false,true})
            test($"{(call?"CALL":"JMP")} gate {width*8}-bit {(conforming?"conforming inner DPL":"same CPL")} ignores count and target RPL",()=>
            {
                var(c,m,old,_)=Create(width,31);Descriptor(m,0x50,0,0xffff,conforming?(byte)0x9e:(byte)0xfa);
                SetGate(m,width,31,0x53,0x5000);Transfer(width==2,!call).CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
                Check(c.CurrentPrivilegeLevel==3 && c.GetSegment(Segment.Cs)==0x53 && c.Eip==0x5000 && c.PrivilegeStackSwitches==0);
                Check(c.GetRegister(Register.Sp)==old-(uint)(call?width*2:0) && c.CallGateTransfers==1);
                if(call){byte[] ret=width==4?new byte[]{0x66,0xcb}:new byte[]{0xcb};ret.CopyTo(m.Ram,0x5000);c.Step();Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==old);}
            });
        foreach(bool operand32 in new[]{false,true})foreach(int width in new[]{2,4})
            test($"indirect FF/3 with {(operand32?32:16)}-bit operand uses {width*8}-bit gate frame",()=>
            {
                var(c,m,_,_)=Create(width,2,caller32:true);
                var code=new List<byte>();if(!operand32)code.Add(0x66);code.AddRange(new byte[]{0xff,0x1d,0,0x10,0,0});
                Write(m,0x31000,operand32?4:2,0xdeadbeef);m.WriteWord((uint)(0x31000+(operand32?4:2)),0x3b);
                code.ToArray().CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();
                Check(c.CurrentPrivilegeLevel==0 && c.Eip==0x5000 && Read(m,0x60000+c.GetRegister(Register.Sp),width)==0x2000+code.Count);
            });
        test("16-bit gate ignores upper target offset; 32-bit gate reaches above 64 KiB from a 16-bit caller",()=>
        {
            foreach(int width in new[]{2,4})
            {
                var(c,m,_,_)=Create(width,0);uint target=width==2?0xdead5000u:0x125000;
                SetGate(m,width,0,0x53,target);c.Step();Check(c.Eip==(width==2?0x5000:target) && c.GetSegment(Segment.Cs)==0x50);
            }
        });
        test("call gate index zero in an LDT is valid and never acquires an accessed bit",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xf4]);Descriptor(m,0x48,0xb000,7,0x82);
            c.SetRegister(Register.Ax,0x48);At(c,m,[0x0f,0x00,0xd0]);SetGate(m,4,31,8,0x5000,0x8c,0xb000);
            At(c,m,Transfer(true,selector:4),0x2000);Check(c.Eip==0x5000 && c.CurrentPrivilegeLevel==0 && m.ReadByte(0xb005)==0x8c);
        });
        test("call gate parameter count uses all five count bits and ignores reserved high bits",()=>
        {
            var(c,m,_,_)=Create(4,31);m.WriteByte(0x43c,0xff);c.Step();
            Check(c.GetRegister(Register.Sp)==0x1a000-35*4 && m.ReadDword(0x60000+c.GetRegister(Register.Sp)+8+30*4)==Pattern(30));
        });
        foreach(byte access in new byte[]{0x8c,0xac,0xcc,0x6c})
            test($"call gate CPL check precedes presence for access {access:X2}",()=>{var(c,m,_,_)=Create();m.WriteByte(0x43d,access);Fault(c,m,access==0x6c?(byte)11:(byte)13,0x38);});
        test("gate RPL check can deny a ring-zero caller before gate presence",()=>
        {
            var(c,m)=ProtectedModeTests.Rig(Transfer(true,selector:0x3b));SetGate(m,access:0x0c);ProtectedModeTests.Fault(c,m,13,0x38);
        });
        foreach(ushort selector in new ushort[]{0,3,0x100,0x54})
            test($"call gate rejects invalid target selector {selector:X4}",()=>
            {var(c,m,_,_)=Create();SetGate(m,targetSelector:selector);Fault(c,m,13,(uint)selector&0xfffc);});
        foreach(byte access in new byte[]{0x92,0xf2,0x89,0x8c,0xe2})
            test($"call gate rejects target descriptor type {access:X2}",()=>
            {var(c,m,_,_)=Create();Descriptor(m,0x50,0,0xffff,access);Fault(c,m,13,0x50);});
        test("call gate target presence precedes TSS validity",()=>
        {var(c,m,_,_)=Create();Descriptor(m,0x50,0,0xffff,0x1a);m.WriteWord(0x9008,0);Fault(c,m,11,0x50);});
        test("JMP through a gate cannot enter an inner nonconforming level even if target is absent",()=>
        {var(c,m,_,_)=Create();Descriptor(m,0x50,0,0xffff,0x1a);Transfer(false,true).CopyTo(m.Ram,0x2000);Fault(c,m,13,0x50);});
        foreach(ushort selector in new ushort[]{0,3,0x100,0x54,0x5b})
            test($"call gate validates TSS target SS {selector:X4} with TS",()=>
            {var(c,m,_,_)=Create();m.WriteWord(0x9008,selector);Fault(c,m,10,(uint)selector&0xfffc);});
        foreach(byte access in new byte[]{0x90,0x9a,0xb2,0x89})
            test($"call gate rejects invalid target stack access {access:X2}",()=>
            {var(c,m,_,_)=Create();Descriptor(m,0x58,0x60000,0xffff,access);Fault(c,m,10,0x58);});
        test("absent call gate target SS raises SS with its selector",()=>
        {var(c,m,_,_)=Create();Descriptor(m,0x58,0x60000,0xffff,0x12);Fault(c,m,12,0x58);});
        test("short cached TSS faults with its selector without switching stacks",()=>
        {
            // LTR itself has no minimum size check; the later stack read does.
            var(d,n)=ProtectedModeTests.Rig([0xf4]);Descriptor(n,0x40,0x9000,8,0x89);d.SetRegister(Register.Ax,0x40);At(d,n,[0x0f,0x00,0xd8]);
            Descriptor(n,0x28,0,0xfffff,0xfa,12);Descriptor(n,0x30,0x30000,0xfffff,0xf2,12);Descriptor(n,0x50,0,0xfffff,0x9a,12);
            uint sp=d.GetRegister(Register.Sp);new uint[]{0x2000,0x2b,2,0x1b000,0x33}.Select((v,i)=>(v,i)).ToList().ForEach(x=>n.WriteDword(sp+(uint)x.i*4,x.v));
            At(d,n,[0xcf]);SetGate(n);Transfer(true).CopyTo(n.Ram,0x2000);d.Eip=0x2000;n.Ram.AsSpan(0x67000,0x14000).Fill(0xa5);Fault(d,n,10,0x40);
        });
        foreach(int width in new[]{2,4})
            test($"call gate {width*8}-bit transition stack overflow precedes target offset GP and reports SS selector",()=>
            {
                var(c,m,_,_)=Create(width);Descriptor(m,0x58,0x60000,15,0x92);m.WriteDword(0x9004,(uint)(width*4));
                Descriptor(m,0x50,0,0xff,0x9a);Fault(c,m,12,0x58);
            });
        test("call gate source parameter segment limit produces SS zero before any destination write",()=>
        {
            var(c,m,_,_)=Create(4,2);Descriptor(m,0x30,0x30000,0x1b003,0xf2,4);c.SetSegment(Segment.Ss,0x33);Fault(c,m,12,0);
        });
        test("zero-parameter gate does not read an out-of-limit caller stack",()=>
        {
            var(c,m,_,_)=Create(4,0);Descriptor(m,0x30,0x30000,0xff,0xf2);c.SetSegment(Segment.Ss,0x33);c.Step();
            Check(c.CurrentPrivilegeLevel==0 && c.CallGateTransfers==1);
        });
        test("gate new-stack supervisor pages and nonidentity caller parameter pages work with paging",()=>
        {
            var(c,m,_,_)=Create(paging:true);PagingTests.Pte(m,0x79000,0xa0000,3);PagingTests.Pte(m,0x4b000,0x90000,7);
            m.WriteDword(0x90000,Pattern(0));m.WriteDword(0x90004,Pattern(1));c.Step();
            Check(c.CurrentPrivilegeLevel==0 && m.ReadDword(0xa0ff0)==Pattern(0) && m.ReadDword(0xa0ff4)==Pattern(1));
        });
        test("gate parameter page permissions use old CPL three, not destination CPL zero",()=>
        {var(c,m,_,_)=Create(paging:true);PagingTests.Pte(m,0x4b000,0x4b000,3);Fault(c,m,14,5,0x4b004);});
        test("gate late parameter page fault keeps both hidden stacks and all destination bytes intact",()=>
        {
            var(c,m,_,_)=Create(paging:true);c.SetRegister(Register.Sp,0x1affc);m.WriteDword(0x4affc,Pattern(0));PagingTests.Pte(m,0x4b000,0,0);
            Fault(c,m,14,4,0x4b000);
        });
        test("gate destination second-page fault precedes parameter reads and leaves no partial saved SS",()=>
        {
            var(c,m,_,_)=Create(paging:true);m.WriteDword(0x9004,0x1a004);PagingTests.Pte(m,0x79000,0,0);
            PagingTests.Pte(m,0x4b000,0x4b000,3);Fault(c,m,14,2,0x79ffc);
        });
        test("gate descriptor accessed-bit write fault is a supervisor write and remains restartable",()=>
        {
            var(c,m,_,_)=Create(paging:true,beforeUser:n=>{n.WriteByte(0x42d,0xfb);n.WriteByte(0x435,0xf3);PagingTests.Pte(n,0,0,5);});
            Fault(c,m,14,3,0x455);Check(m.ReadByte(0x455)==0x9a);
        });
        test("gate destination respects supervisor WP without treating kernel stack as a user write",()=>
        {var(c,m,_,_)=Create(paging:true);PagingTests.Pte(m,0x79000,0x79000,1);Fault(c,m,14,3,0x79ffc);});
        test("gate transfer diagnostic counter resets with CPU reset",()=>
        {var(c,_,_,_)=Create();c.Step();Check(c.CallGateTransfers==1);c.Reset();Check(c.CallGateTransfers==0);});
        foreach(int privilege in new[]{1,2})
            test($"nested call gates enter ring {privilege} then zero and return through both parameter frames",()=>
            {
                var(c,m,old,_)=Create(4,2,caller32:true);
                Descriptor(m,0x60,0,0xfffff,(byte)(0x9a|(privilege<<5)),12);
                Descriptor(m,0x68,0x90000,0xfffff,(byte)(0x92|(privilege<<5)),12);
                m.WriteDword((uint)(0x9004+privilege*8),0x20000);m.WriteWord((uint)(0x9008+privilege*8),(ushort)(0x68|privilege));
                SetGate(m,targetSelector:0x63);SetGate(m,4,1,0x53,0x5400,(byte)(0x8c|(privilege<<5)),0x470);
                new byte[]{0x68,0x78,0x56,0x34,0x12}.Concat(Transfer(true,selector:(ushort)(0x70|privilege))).Concat(new byte[]{0xca,8,0}).ToArray().CopyTo(m.Ram,0x5000);
                new byte[]{0xca,4,0}.CopyTo(m.Ram,0x5400);
                c.Step();Check(c.CurrentPrivilegeLevel==privilege && c.GetSegment(Segment.Ss)==(0x68|privilege));
                c.Step();c.Step();Check(c.CurrentPrivilegeLevel==0 && c.Eip==0x5400 && m.ReadDword(0x60000+c.GetRegister(Register.Sp)+8)==0x12345678);
                c.Step();Check(c.CurrentPrivilegeLevel==privilege && c.Eip==0x500c);
                c.Step();Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x2007 && c.GetRegister(Register.Sp)==old+8);
                Check(c.CallGateTransfers==2 && c.PrivilegeStackSwitches==2 && c.OuterPrivilegeReturns==3);
            });
        foreach(bool valid in new[]{false,true})
            test($"call gate validates expand-down destination stack with {(valid?"enough":"insufficient")} space",()=>
            {
                var(c,m,_,_)=Create(4,2,new32:false);m.WriteDword(0x9004,0x8000);
                Descriptor(m,0x58,0x60000,valid?0x7000u:0x7ff0u,0x96);
                if(valid){c.Step();Check(c.CurrentPrivilegeLevel==0 && (c.GetRegister(Register.Sp)&65535)==0x7fe8);}
                else Fault(c,m,12,0x58);
            });
        test("gate target offset limit fault does not commit stack/cache state",()=>
        {var(c,m,_,_)=Create();Descriptor(m,0x50,0,0xff,0x9a);Fault(c,m,13,0);});
        test("same-level gate stack overflow reports SS zero and fault delivery can use the TSS stack",()=>
        {
            var(c,m,_,_)=Create();Descriptor(m,0x50,0,0xffff,0xfa);Descriptor(m,0x30,0x30000,0x1aff8,0xf6,4);c.SetSegment(Segment.Ss,0x33);
            c.Step();Check(c.CurrentPrivilegeLevel==0 && c.Eip==0x6000 && !c.TripleFaulted && c.CallGateTransfers==0);
            uint frame=0x60000+c.GetRegister(Register.Sp);Check(m.ReadWord(frame)==0 && m.ReadDword(frame+4)==0x2000 && m.ReadDword(frame+8)==0x2b);
        });
    }
}
