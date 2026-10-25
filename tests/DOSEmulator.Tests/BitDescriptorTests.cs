using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class BitDescriptorTests
{
    private static void Check(bool value, string message = "486 bit/selector invariant failed") { if (!value) throw new Exception(message); }
    public static void AddTo(Action<string,Action> test)
    {
        foreach (byte opcode in new byte[] { 0xa3, 0xab, 0xb3, 0xbb })
        foreach (bool set in new[] { false, true })
            test($"BT family {opcode:X2} reports original carry and updates only the selected bit, initially {set}", () =>
            {
                var(c,_)=ProtectedModeTests.Rig([0x0f,opcode,0xc8]);c.SetRegister(Register.Ax,set?0x80000001u:1);c.SetRegister(Register.Cx,63);c.Eflags=0x8d6;
                c.Step();uint expected=opcode switch{0xab=>0x80000001,0xb3=>1,0xbb=>set?1u:0x80000001,_=>set?0x80000001u:1};
                Check(c.GetRegister(Register.Ax)==expected && (c.Eflags&1)==(set?1u:0) && (c.Eflags&0x8d6)==0x8d6);
            });
        foreach (bool word in new[] { false,true })
            test($"register bit index is signed for a memory bit string of {(word?16:32)}-bit units", () =>
            {
                var(c,m)=ProtectedModeTests.Rig(word?[0x66,0x0f,0xab,0x0d,0,0,3,0]:[0x0f,0xab,0x0d,0,0,3,0]);
                c.SetRegister(Register.Cx,0xffffffff);c.Step();
                Check(word?m.ReadWord(0x2fffe)==0x8000:m.ReadDword(0x2fffc)==0x80000000);Check(m.ReadDword(0x30000)==0 && (c.Eflags&1)==0);
            });
        test("memory BT immediate masks the high index bits within its operand",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x0f,0xba,0x2d,0,0,3,0,0xff]);c.Step();Check(m.ReadDword(0x30000)==0x80000000 && m.ReadDword(0x3001c)==0);
        });
        test("memory BT signed displacement wraps with a sixteen-bit address override",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x67,0x0f,0xab,0x0e,0,0]);c.SetRegister(Register.Cx,0xffffffff);c.Step();Check(m.ReadDword(0xfffc)==0x80000000);
        });
        foreach(byte group in new byte[]{0,1,2,3})
            test($"reserved 0F BA group {group} raises UD",()=>{var(c,m)=ProtectedModeTests.Rig([0x0f,0xba,(byte)(0xc0+group*8),1]);Fault(c,m,6,null);});
        test("bit modifying page fault preserves carry and does not modify the bit string",()=>
        {
            var(c,m)=PagingTests.Create([0x0f,0xba,0x2d,0,0,3,0,1]);PagingTests.Pte(m,0x30000,0x40000,1);c.Eflags=0x203;
            uint frame=Fault(c,m,14,3);Check(m.ReadDword(0x40000)==0 && (m.ReadDword(frame+12)&1)==1 && c.Cr2==0x30000);
        });
        foreach(bool right in new[]{false,true})
        foreach(bool word in new[]{false,true})
            test($"{(right?"SHRD":"SHLD")} {(word?16:32)}-bit merges source and reports last shifted bit",()=>
            {
                byte[] instruction=[0x0f,(byte)(right?0xac:0xa4),0xd0,4];
                var(c,_)=ProtectedModeTests.Rig(word?[0x66,..instruction]:instruction);c.SetRegister(Register.Ax,0x81234569);c.SetRegister(Register.Dx,0xabcdef32);c.Step();
                uint expected=word?(right?0x81232456u:0x8123569e):(right?0x28123456u:0x1234569a);
                Check(c.GetRegister(Register.Ax)==expected && (c.Eflags&1)==(right?1u:0));
            });
        test("SHLD CL masks count and reads overlapping source before its destination changes",()=>
        {
            var(c,_)=ProtectedModeTests.Rig([0x0f,0xa5,0xc0]);c.SetRegister(Register.Ax,0x80000001);c.SetRegister(Register.Cx,33);c.Step();
            Check(c.GetRegister(Register.Ax)==3 && (c.Eflags&0x801)==0x801);
        });
        test("zero-count register double shift preserves the destination and flags",()=>
        {
            var(c,_)=ProtectedModeTests.Rig([0x0f,0xac,0xd0,32]);c.SetRegister(Register.Ax,0xdeadbeef);c.Eflags=0x8d7;
            c.Step();Check(c.Eip==0x2004 && c.Eflags==0x8d7 && c.GetRegister(Register.Ax)==0xdeadbeef);
        });
        foreach(byte[] instruction in new byte[][]{[0x0f,0xa4,0x15,0,0,3,0,0],[0x0f,0xac,0x15,0,0,3,0,32],[0xc1,0x25,0,0,3,0,0],[0xc1,0x05,0,0,3,0,32]})
        test($"zero-count memory shift {Convert.ToHexString(instruction)} still checks read/modify/write access",()=>
        {
            var(c,m)=PagingTests.Create(instruction);PagingTests.Pte(m,0x30000,0,0);c.Eflags=0x8d7;
            uint frame=Fault(c,m,14,2);Check(c.Cr2==0x30000 && m.ReadDword(frame+12)==0x108d7);
        });
        foreach(bool reverse in new[]{false,true})
        foreach(bool zero in new[]{false,true})
            test($"{(reverse?"BSR":"BSF")} {(zero?"zero":"nonzero")} flag and destination",()=>
            {
                var(c,_)=ProtectedModeTests.Rig([0x0f,(byte)(reverse?0xbd:0xbc),0xc2]);c.SetRegister(Register.Dx,zero?0u:0x81000100);c.SetRegister(Register.Ax,0xdeadbeef);c.Eflags=0x203;
                c.Step();Check((c.Eflags&0x40)==(zero?0x40u:0) && c.GetRegister(Register.Ax)==(zero?0xdeadbeefu:reverse?31u:8));
            });
        foreach(bool limit in new[]{false,true})
            test($"{(limit?"LSL":"LAR")} returns a granular descriptor without requiring present or setting accessed",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x0f,(byte)(limit?3:2),0xc2]);Descriptor(m,0x28,0x12345678,0x12345,0x12,12);c.SetRegister(Register.Dx,0x28);c.Step();
                Check((c.Eflags&0x40)!=0 && c.GetRegister(Register.Ax)==(limit?0x12345fffu:0xc01200) && m.ReadByte(0x42d)==0x12);
            });
        foreach(byte type in new byte[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15})
            test($"LAR and LSL use the Intel486 system descriptor type table {type:X}",()=>
            {
                foreach(bool limit in new[]{false,true})
                {
                    var(c,m)=ProtectedModeTests.Rig([0x0f,(byte)(limit?3:2),0xc2]);Descriptor(m,0x28,0,0x1234,(byte)(0x80|type));c.SetRegister(Register.Dx,0x28);c.SetRegister(Register.Ax,0xdeadbeef);c.Step();
                    bool valid=limit?type is 1 or 2 or 3 or 9 or 11:type is 1 or 2 or 3 or 4 or 5 or 9 or 11 or 12;
                    Check(((c.Eflags&0x40)!=0)==valid && (valid || c.GetRegister(Register.Ax)==0xdeadbeef));
                }
            });
        foreach(byte access in new byte[]{0x90,0x92,0x98,0x9a,0x9e,0x89})
            test($"VERR and VERW apply readable/writable segment rules for access {access:X2}",()=>
            {
                foreach(bool write in new[]{false,true})
                {
                    var(c,m)=ProtectedModeTests.Rig([0x0f,0x00,(byte)(write?0xea:0xe2)]);Descriptor(m,0x28,0,0xffff,access);c.SetRegister(Register.Dx,0x28);c.Step();
                    bool valid=write?access==0x92:access is 0x90 or 0x92 or 0x9a or 0x9e;Check(((c.Eflags&0x40)!=0)==valid);
                }
            });
        foreach(ushort selector in new ushort[]{0,3,0x100,0x104,0x2b})
            test($"invisible selector query {selector:X4} clears ZF rather than raising GP",()=>
            {
                var(c,_)=ProtectedModeTests.Rig([0x0f,0x02,0xc2]);c.SetRegister(Register.Dx,selector);c.SetRegister(Register.Ax,0xdeadbeef);c.Eflags=0x242;c.Step();
                Check(c.Eip==0x2003 && (c.Eflags&0x40)==0 && c.GetRegister(Register.Ax)==0xdeadbeef);
            });
        test("selector query propagates supervisor GDT page faults and keeps its destination",()=>
        {
            var(c,m)=PagingTests.Create([0x0f,0x02,0xc2]);m.WriteWord(0x350,0xff);m.WriteDword(0x352,0x30000);
            m.Ram.AsSpan(0x400,256).CopyTo(m.Ram.AsSpan(0x30000));PagingTests.At(c,m,[0x0f,0x01,0x15,0x50,3,0,0]);
            // Fault delivery uses the new GDT. Pre-cache its code/stack descriptors,
            // then arrange a page fault on a separate descriptor page instead.
            m.WriteWord(0x350,0x20ff);PagingTests.At(c,m,[0x0f,0x01,0x15,0x50,3,0,0]);
            PagingTests.Pte(m,0x31000,0,0);c.SetRegister(Register.Dx,0x1000);c.SetRegister(Register.Ax,0xdeadbeef);c.Eip=0x2000;
            Fault(c,m,14,0);Check(c.Cr2==0x31000 && c.GetRegister(Register.Ax)==0xdeadbeef);
        });
        foreach(bool adjust in new[]{false,true})
            test($"ARPL {(adjust?"raises":"retains")} only the destination RPL and reports ZF",()=>
            {
                var(c,_)=ProtectedModeTests.Rig([0x63,0xd0]);c.SetRegister(Register.Ax,adjust?0xabcd1230u:0xabcd1233);c.SetRegister(Register.Dx,3);c.Step();
                Check(c.GetRegister(Register.Ax)==0xabcd1233 && ((c.Eflags&0x40)!=0)==adjust);
            });
    }
}
