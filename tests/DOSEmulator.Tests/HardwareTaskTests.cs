using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class HardwareTaskTests
{
    private static void Check(bool condition, string detail = "Hardware task invariant failed")
    { if (!condition) throw new Exception(detail); }
    private static void At(X86Cpu c, MemoryBus m, byte[] code, uint ip = 0x2200)
    { code.CopyTo(m.Ram, (int)ip); c.Eip = ip; c.Step(); }
    private static byte[] Transfer(ushort selector = 0x48, bool jump = false, bool operand32 = true)
    {
        var code = new List<byte> { (byte)(jump ? 0xea : 0x9a) };
        code.AddRange(operand32 ? new byte[] { 0xef, 0xbe, 0xad, 0xde } : new byte[] { 0xef, 0xbe });
        code.Add((byte)selector); code.Add((byte)(selector >> 8)); return code.ToArray();
    }
    private static void Task(MemoryBus m, ushort selector, uint basis, bool wide, uint ip = 0x5000, uint pointer = 0x1a000,
        ushort? cs = null, ushort? ss = null, uint flags = 2, byte? access = null)
    {
        Descriptor(m, selector, basis, (uint)(wide ? 0x67 : 0x2b), access ?? (byte)(wide ? 0x89 : 0x81));
        m.Ram.AsSpan((int)basis, wide ? 104 : 44).Fill(0xa5);
        m.WriteWord(basis, 0);
        uint a = basis + (wide ? 0x20u : 0x0eu); int w = wide ? 4 : 2;
        Write(m, a, w, ip); Write(m, a + (uint)w, w, flags);
        for (int i = 0; i < 8; i++) Write(m, a + (uint)((i + 2) * w), w, i == 4 ? pointer : Pattern(i));
        ushort[] segments = [wide ? (ushort)16 : (ushort)32, cs ?? (wide ? (ushort)8 : (ushort)24), ss ?? (wide ? (ushort)16 : (ushort)32), wide ? (ushort)16 : (ushort)32, 0, 0];
        uint s = basis + (wide ? 0x48u : 0x22u);
        for (int i = 0; i < (wide ? 6 : 4); i++) m.WriteWord(s + (uint)(i * w), segments[i]);
        m.WriteWord(basis + (wide ? 0x60u : 0x2au), 0);
        if (wide) { m.WriteDword(basis + 0x1c, 0x1c000u); m.WriteWord(basis + 0x64, 0); m.WriteWord(basis + 0x66, 0x68); }
    }
    private static uint Pattern(int i) => 0x12345670u + (uint)i;
    private static uint Read(MemoryBus m, uint a, int w) => w == 4 ? m.ReadDword(a) : m.ReadWord(a);
    private static void Write(MemoryBus m, uint a, int w, uint value)
    { if (w == 4) m.WriteDword(a, value); else m.WriteWord(a, (ushort)value); }
    private static void TaskGate(MemoryBus m, ushort selector, ushort task = 0x48, byte access = 0x85, uint? address = null)
    {
        uint a = address ?? (0x400u + ((uint)selector & 0xfff8));
        m.WriteDword(a, ((uint)task << 16) | 0xbeef); m.WriteDword(a + 4, 0xdead0000 | ((uint)access << 8) | 0x1f);
    }
    private static (X86Cpu Cpu, MemoryBus Memory) Create(bool old32 = true, bool new32 = true, bool paging = false)
    {
        var (c, m) = paging ? PagingTests.Create([0xf4]) : ProtectedModeTests.Rig([0xf4]);
        Task(m, 0x40, 0x9000, old32); Task(m, 0x48, 0xa000, new32, pointer: new32 ? 0x1a000u : 0x7000);
        Task(m, 0x50, 0xb000, true, ip: 0x6000, pointer: 0x1c000);
        c.SetRegister(Register.Ax, 0x40); At(c, m, [0x0f, 0x00, 0xd8]);
        for (int i = 0; i < 8; i++) c.SetRegister((Register)i, 0x87654320u + (uint)i);
        c.SetRegister(Register.Sp, 0x18000); c.Eip = 0x2000; c.Eflags = 0x40203;
        m.WriteByte(0x5000, 0xcf); return (c, m);
    }
    private static void OldFault(X86Cpu c, MemoryBus m, byte vector, uint error, uint ip = 0x2000)
    {
        Fault(c, m, vector, error, ip);
        Check(c.Tr == 0x40 && c.HardwareTaskSwitches == 0 && (c.Cr0 & 8) == 0 && m.ReadByte(0x445) == 0x8b);
    }

    public static void AddTo(Action<string, Action> test)
    {
        foreach(bool wide in new[]{false,true})
            test($"TSS{(wide?32:16)} switch clears local debug enables and LE but retains globals and addresses",()=>
            {
                var(c,m)=Create(new32:wide);c.SetRegister(Register.Ax,0x12345678);At(c,m,[0x0f,0x23,0xc0]);
                c.SetRegister(Register.Ax,0x3ff);At(c,m,[0x0f,0x23,0xf8]);
                At(c,m,Transfer(),0x2000);Check(c.Tr==0x48 && c.Dr7==0x6aa && c.Dr0==0x12345678);
            });
        foreach(bool global in new[]{false,true})
            test($"hardware task first instruction breakpoint global={global} follows new task context",()=>
            {
                var(c,m)=Create();c.SetRegister(Register.Ax,0x5000);At(c,m,[0x0f,0x23,0xc0]);
                c.SetRegister(Register.Ax,global?2u:1);At(c,m,[0x0f,0x23,0xf8]);At(c,m,Transfer(),0x2000);
                Check(c.Tr==0x48 && c.Eip==0x5000 && c.InstructionBreakpointFaults==0);c.Step();
                if(global)Check(c.Tr==0x48 && c.Eip==0x6000 && m.ReadDword(c.GetRegister(Register.Sp))==0x5000 && c.InstructionBreakpointFaults==1);
                else Check(c.Tr==0x40 && c.InstructionBreakpointFaults==0 && c.HardwareTaskReturns==1);
            });
        test("TF loaded from a new TSS traps after its first instruction rather than the old task transfer",()=>
        {
            var(c,m)=Create();m.WriteDword(0xa024,0x102);m.WriteByte(0x5000,0x90);At(c,m,Transfer(),0x2000);
            Check(c.Tr==0x48 && c.SingleStepTraps==0 && c.Eip==0x5000);c.Step();
            Check(c.Tr==0x48 && c.Eip==0x6000 && c.SingleStepTraps==1 && m.ReadDword(c.GetRegister(Register.Sp))==0x5001 && (c.Dr6&0x4000)!=0);
        });
        foreach (bool old32 in new[] { false, true }) foreach (bool new32 in new[] { false, true })
        foreach (bool caller32 in new[] { false, true }) foreach (bool jump in new[] { false, true })
            test($"hardware {(jump ? "JMP" : "CALL/IRET")} TSS{(old32 ? 32 : 16)} to TSS{(new32 ? 32 : 16)} operand{(caller32 ? 32 : 16)} saves actual formats", () =>
            {
                var (c, m) = Create(old32, new32); uint oldFlags = c.Eflags;
                byte[] code = Transfer(jump: jump, operand32: caller32);
                if (!caller32) code = [0x66, .. code];
                code.CopyTo(m.Ram, 0x2000); uint next = 0x2000u + (uint)code.Length;
                c.Step(); int w = old32 ? 4 : 2;
                Check(c.Tr == 0x48 && c.Eip == 0x5000 && c.HardwareTaskSwitches == 1 && (c.Cr0 & 8) != 0);
                Check(c.GetSegment(Segment.Cs) == (new32 ? 8 : 24) && c.GetSegment(Segment.Ss) == (new32 ? 16 : 32));
                Check(c.GetRegister(Register.Sp) == (new32 ? 0x1a000u : 0xffff7000u));
                for (int i = 0; i < 8; i++) if (i != 4) Check(c.GetRegister((Register)i) == (new32 ? Pattern(i) : 0xffff0000 | (Pattern(i) & 65535)));
                Check(Read(m, 0x9000u + (old32 ? 0x20u : 0x0eu), w) == next);
                Check(Read(m, 0x9000u + (old32 ? 0x24u : 0x10u), w) == (oldFlags & (old32 ? uint.MaxValue : 65535)));
                Check(m.ReadByte(0x445) == (old32 ? (jump ? 0x89 : 0x8b) : (jump ? 0x81 : 0x83)));
                Check(m.ReadByte(0x44d) == (new32 ? 0x8b : 0x83));
                Check(m.ReadWord(0xa000) == (jump ? 0 : 0x40) && (c.Eflags & 0x4000) == (jump ? 0u : 0x4000u));
                if (old32) Check(m.ReadWord(0x904a) == 0xa5a5 && m.ReadWord(0x9062) == 0xa5a5 && m.ReadDword(0x901c) == 0x1c000u);
                if (!jump)
                {
                    c.Step(); Check(c.Tr == 0x40 && c.Eip == next && c.HardwareTaskSwitches == 2 && c.HardwareTaskReturns == 1);
                    Check(c.Eflags == (oldFlags & (old32 ? uint.MaxValue : 65535)) && m.ReadByte(0x44d) == (new32 ? 0x89 : 0x81));
                    Check((Read(m, 0xa000u + (new32 ? 0x24u : 0x10u), new32 ? 4 : 2) & 0x4000) == 0, "IRET saved NT into terminated task");
                }
            });
        foreach (bool ldt in new[] { false, true }) foreach (bool jump in new[] { false, true })
            test($"{(ldt ? "LDT index zero" : "GDT")} task gate {(jump ? "JMP" : "CALL")} ignores reserved offsets and target TSS DPL", () =>
            {
                var (c, m) = Create(); ushort selector = 0x5b;
                if (ldt)
                {
                    Descriptor(m, 0x58, 0xc000, 7, 0x82); c.SetRegister(Register.Ax, 0x58); At(c, m, [0x0f, 0x00, 0xd0]);
                    TaskGate(m, 4, access: 0xe5, address: 0xc000); selector = 7;
                }
                else TaskGate(m, 0x58, access: 0xe5);
                At(c, m, Transfer(selector, jump), 0x2000);
                Check(c.Tr == 0x48 && c.TaskGateTransfers == 1 && c.Eip == 0x5000 && c.Ldtr == 0);
            });
        foreach (bool software in new[] { false, true })
            test($"IDT task gate {(software ? "INT" : "hardware IRQ")} nests and IRET resumes correct instruction", () =>
            {
                var (c, m) = Create(); TaskGate(m, 0, address: 0x8000 + 0x60 * 8);
                if (software) At(c, m, [0xcd, 0x60], 0x2000); else c.EnterHardwareInterrupt(0x60);
                Check(c.Tr == 0x48 && c.TaskGateTransfers == 1 && c.Eip == 0x5000 && m.ReadWord(0xa000) == 0x40);
                c.Step(); Check(c.Tr == 0x40 && c.Eip == (software ? 0x2002u : 0x2000u));
            });
        test("three nested hardware tasks return through actual backlink chain", () =>
        {
            var (c, m) = Create(); At(c, m, Transfer(), 0x2000);
            At(c, m, Transfer(0x50), 0x5000); Check(c.Tr == 0x50 && m.ReadWord(0xb000) == 0x48 && (c.Eflags & 0x4000) != 0);
            m.WriteByte(0x6000, 0xcf); c.Step(); Check(c.Tr == 0x48 && c.Eip == 0x5007 && (c.Eflags & 0x4000) != 0);
            m.WriteByte(0x5007, 0xcf); c.Step(); Check(c.Tr == 0x40 && c.Eip == 0x2007 && c.HardwareTaskSwitches == 4 && c.HardwareTaskReturns == 2);
        });
        test("JMP loads incoming NT while preserving outgoing NT and both backlinks", () =>
        {
            var (c, m) = Create(); c.Eflags |= 0x4000; m.WriteDword(0xa024, 0x4002); m.WriteWord(0xa000, 0x50); m.WriteWord(0x9000, 0x77);
            At(c, m, Transfer(jump: true), 0x2000); Check((c.Eflags & 0x4000) != 0 && (m.ReadDword(0x9024) & 0x4000) != 0);
            Check(m.ReadWord(0xa000) == 0x50 && m.ReadWord(0x9000) == 0x77);
        });
        foreach (ushort selector in new ushort[] { 0x4b, 0x4f, 0x100 })
            test($"direct TSS invalid or overprivileged selector {selector:X} fails in old task", () =>
            {
                var (c, m) = Create(); Transfer(selector).CopyTo(m.Ram, 0x2000); OldFault(c, m, 13, (uint)selector & 0xfffc);
            });
        foreach (byte access in new byte[] { 0x8b, 0x83, 0x92, 0x09 })
            test($"direct target TSS type/present {access:X2} fails before saving old task", () =>
            {
                var (c, m) = Create(); Descriptor(m, 0x48, 0xa000, 0x67, access); Transfer().CopyTo(m.Ram, 0x2000);
                byte[] before = m.Ram.AsSpan(0x9000, 104).ToArray(); OldFault(c, m, access == 9 ? (byte)11 : (byte)13, 0x48);
                Check(m.Ram.AsSpan(0x9000, 104).SequenceEqual(before));
            });
        foreach (bool wide in new[] { false, true })
            test($"short TSS{(wide ? 32 : 16)} raises TS in outgoing context", () =>
            {
                var (c, m) = Create(new32: wide); Descriptor(m, 0x48, 0xa000, (uint)(wide ? 0x66 : 0x2a), (byte)(wide ? 0x89 : 0x81));
                Transfer().CopyTo(m.Ram, 0x2000); OldFault(c, m, 10, 0x48);
            });
        foreach (ushort backlink in new ushort[] { 0, 4, 0x48, 0x100 })
            test($"NT IRET rejects invalid/not-busy backlink {backlink:X} without popping current stack", () =>
            {
                var (c, m) = Create(); m.WriteWord(0x9000, backlink); c.Eflags |= 0x4000; m.WriteByte(0x2000, 0xcf);
                OldFault(c, m, 10, (uint)backlink & 0xfffc);
            });
        test("task gate DPL rejection precedes not-present and does not touch TSS", () =>
        {
            var (c, m) = Create(); TaskGate(m, 0x58, access: 5); Transfer(0x5b).CopyTo(m.Ram, 0x2000); OldFault(c, m, 13, 0x58);
        });
        test("absent task gate gives NP of gate rather than target TSS", () =>
        {
            var (c, m) = Create(); TaskGate(m, 0x58, task: 4, access: 5); Transfer(0x58).CopyTo(m.Ram, 0x2000); OldFault(c, m, 11, 0x58);
        });
        foreach (ushort target in new ushort[] { 0, 4, 0x100 })
            test($"task gate target {target:X} must name global TSS", () =>
            {
                var (c, m) = Create(); TaskGate(m, 0x58, target); Transfer(0x58).CopyTo(m.Ram, 0x2000); OldFault(c, m, 13, (uint)target & 0xfffc);
            });
        foreach (bool hardware in new[] { false, true })
            test($"post-commit invalid DS keeps new TR/registers in {(hardware ? "external task gate" : "CALL")} fault delivery", () =>
            {
                var (c, m) = Create(); m.WriteWord(0xa054, 0x100); byte? seen = null; c.InterruptEntered += v => seen = v;
                if (hardware) { TaskGate(m, 0, address: 0x8000 + 0x60 * 8); c.EnterHardwareInterrupt(0x60); }
                else At(c, m, Transfer(), 0x2000);
                uint frame = c.GetRegister(Register.Sp);
                Check(c.Tr == 0x48 && c.HardwareTaskSwitches == 1 && c.Eip == 0x6000 && !c.TripleFaulted && seen == 10);
                Check(c.GetRegister(Register.Bx) == Pattern(3) && c.GetSegment(Segment.Ds) == 0x100 && !c.GetSegmentCache(Segment.Ds).Usable);
                Check(frame == 0x19ff0 && m.ReadWord(frame) == (hardware ? 0x101 : 0x100) && m.ReadDword(frame + 4) == 0x5000);
                Check(m.ReadDword(0x9020) == (hardware ? 0x2000u : 0x2007u) && m.ReadWord(0xa000) == 0x40);
            });
        foreach ((uint offset, ushort value, byte vector, uint error) in new (uint, ushort, byte, uint)[] {
            (0x60,4,10,4),(0x60,0x58,10,0x58),(0x4c,0,10,0),(0x4c,16,10,16),(0x50,0,10,0),
            (0x50,8,10,8),(0x54,0x70,10,0x70),(0x54,0x68,11,0x68) })
            test($"committed task selector field {offset:X}/{value:X} faults via a separate handler task", () =>
            {
                var (c, m) = Create(); Descriptor(m, 0x68, 0, 0xffff, 0x12); Descriptor(m, 0x70, 0, 0xffff, 0x98); m.WriteWord(0xa000 + offset, value);
                TaskGate(m, 0, 0x50, address: 0x8000u + (uint)vector * 8); At(c, m, Transfer(), 0x2000);
                Check(c.Tr == 0x50 && c.HardwareTaskSwitches == 2 && c.TaskGateTransfers == 1 && c.Eip == 0x6000 && !c.TripleFaulted);
                Check(m.ReadWord(0xb000) == 0x48 && m.ReadDword(0xa020) == 0x5000 && m.ReadDword(0xa034) == Pattern(3));
                Check(c.GetRegister(Register.Sp) == 0x1bffc && m.ReadWord(0x1bffc) == error);
            });
        test("invalid incoming EIP faults after task commit and after segment reload", () =>
        {
            var (c, m) = Create(); Descriptor(m, 8, 0, 0xffff, 0x9a, 4); m.WriteDword(0xa020, 0x10000);
            At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x48 && c.Eip == 0x6000 && m.ReadDword(0x19ff4) == 0x10000 && m.ReadWord(0x19ff0) == 0);
        });
        test("new task loads its LDT before local CS/SS/data descriptors", () =>
        {
            var (c, m) = Create(); Descriptor(m, 0x58, 0xc000, 0x17, 0x82);
            m.Ram.AsSpan(0x408, 8).CopyTo(m.Ram.AsSpan(0xc000, 8)); m.Ram.AsSpan(0x410, 8).CopyTo(m.Ram.AsSpan(0xc008, 8));
            m.WriteWord(0xa060, 0x58); m.WriteWord(0xa04c, 4); m.WriteWord(0xa050, 12); m.WriteWord(0xa048, 12); m.WriteWord(0xa054, 12);
            At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x48 && c.Ldtr == 0x58 && c.GetSegment(Segment.Cs) == 4 && c.GetSegment(Segment.Ss) == 12 && c.Eip == 0x5000);
        });
        foreach (bool new32 in new[] { false, true })
            test($"TSS{(new32 ? 32 : 16)} load uses cached outgoing TSS base despite changed GDT", () =>
            {
                var (c, m) = Create(new32: new32); Descriptor(m, 0x40, 0xd000, 0, 0x0b);
                At(c, m, Transfer(), 0x2000); Check(m.ReadDword(0x9020) == 0x2007 && m.ReadDword(0xd020) == 0 && c.Tr == 0x48);
            });
        foreach (bool new32 in new[] { false, true })
            test($"paging task to TSS{(new32 ? 32 : 16)} {(new32 ? "loads CR3 and flushes cached mapping" : "keeps CR3 and mapping")}", () =>
            {
                var (c, m) = Create(new32: new32, paging: true);
                const uint directory = 0x30000, table = 0x31000;
                m.WriteDword(directory, table | 7); for (uint i = 0; i < 512; i++) m.WriteDword(table + i * 4, i * 4096 | 7);
                m.WriteDword(table + 0x14, 0x40007); m.WriteByte(0x40000, 0xcf); m.WriteDword(0xa01c, directory);
                At(c, m, [0xa1, 0, 0x50, 0, 0]); long flushes = c.TlbInvalidations;
                At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x48 && c.Cr3 == (new32 ? directory : 0x1c000u));
                Check(c.PhysicalInstructionAddress == (new32 ? 0x40000u : 0x5000u) && c.TlbInvalidations == flushes + (new32 ? 1 : 0));
                c.Step(); Check(c.Tr == 0x40 && c.Cr3 == 0x1c000u && c.Eip == 0x2007);
            });
        foreach (bool outgoing in new[] { false, true })
            test($"pre-commit {(outgoing ? "outgoing TSS write" : "incoming TSS read")} page fault restarts old instruction", () =>
            {
                var (c, m) = Create(paging: true); PagingTests.Pte(m, outgoing ? 0x9000u : 0xa000u, 0, 0);
                Transfer().CopyTo(m.Ram, 0x2000); OldFault(c, m, 14, outgoing ? 2u : 0u);
                Check(c.Cr2 == (outgoing ? 0x9020u : 0xa01cu));
            });
        test("post-commit descriptor page fault uses new CR3 and new task in handler backlink", () =>
        {
            var (c, m) = Create(paging: true); const uint directory = 0x30000, table = 0x31000;
            m.WriteDword(directory, table | 7); for (uint i = 0; i < 512; i++) m.WriteDword(table + i * 4, i * 4096 | 7);
            Descriptor(m, 0x58, 0xc000, 0x17, 0x82); m.WriteWord(0xa060, 0x58); m.WriteWord(0xa054, 4);
            m.WriteDword(table + 12 * 4, 0); m.WriteDword(0xa01c, directory);
            TaskGate(m, 0, 0x50, address: 0x8000 + 14 * 8); At(c, m, Transfer(), 0x2000);
            Check(c.Tr == 0x50 && c.HardwareTaskSwitches == 2 && c.Cr2 == 0xc000 && c.Cr3 == 0x1c000u && !c.TripleFaulted);
            Check(m.ReadWord(0xb000) == 0x48 && m.ReadDword(0xa020) == 0x5000 && m.ReadWord(0x1bffc) == 0);
        });
        test("task switch can enter V86 and an IDT task gate returns to protected task", () =>
        {
            var (c, m) = Create(); m.WriteDword(0xa020, 0x100); m.WriteDword(0xa024, 0x23002);
            ushort[] selectors = [0x4000, 0x3000, 0x5000, 0x6000, 0x7000, 0x8000];
            for (int i = 0; i < 6; i++) m.WriteWord((uint)(0xa048 + i * 4), selectors[i]);
            At(c, m, Transfer(), 0x2000); Check(c.Virtual8086 && c.Tr == 0x48 && c.CurrentPrivilegeLevel == 3 && c.Virtual8086Entries == 1);
            for (int i = 0; i < 6; i++) Check(c.GetSegmentBase((Segment)i) == (uint)selectors[i] << 4 && c.GetSegmentCache((Segment)i).Limit == 65535);
            TaskGate(m, 0, 0x50, address: 0x8000 + 0x60 * 8); c.EnterHardwareInterrupt(0x60);
            Check(!c.Virtual8086 && c.Tr == 0x50 && c.Virtual8086Exits == 1 && m.ReadDword(0xa024) == 0x27002);
            m.WriteByte(0x6000, 0xcf); c.Step(); Check(c.Virtual8086 && c.Tr == 0x48 && c.Virtual8086Entries == 2 && c.Eip == 0x100);
        });
        test("TSS debug T-bit traps before first incoming instruction and records DR6.BT without RF", () =>
        {
            var (c, m) = Create(); m.WriteWord(0xa064, 1); uint before = c.Dr6; At(c, m, Transfer(), 0x2000);
            Check(c.Tr == 0x48 && c.TaskDebugTraps == 1 && c.Dr6 == (before | 0x8000) && c.Eip == 0x6000);
            Check(c.GetRegister(Register.Sp) == 0x19ff4 && m.ReadDword(0x19ff4) == 0x5000 && (m.ReadDword(0x19ffc) & 0x10000) == 0);
        });
        test("reset invalidates task counters/cache without changing saved TSS or busy descriptors", () =>
        {
            var (c, m) = Create(); At(c, m, Transfer(), 0x2000); c.Reset();
            Check(c.HardwareTaskSwitches == 0 && c.HardwareTaskReturns == 0 && c.TaskGateTransfers == 0 && c.TaskDebugTraps == 0 && c.Tr == 0 && !c.TrCache.Usable);
            Check(c.Dr6 == 0xffff0ff0 && m.ReadByte(0x44d) == 0x8b && m.ReadDword(0x9020) == 0x2007);
        });
        foreach (bool useGate in new[] { false, true })
            test($"ring-three {(useGate ? "public task gate enters private kernel TSS" : "direct private TSS is denied")} with supervisor TSS access", () =>
            {
                var (c, m) = Create(paging: true); PagingTests.User(c, m);
                if (useGate) TaskGate(m, 0x58, access: 0xe5);
                ushort selector = useGate ? (ushort)0x5b : (ushort)0x4b;
                Transfer(selector).CopyTo(m.Ram, 0x2000);
                if (!useGate)
                {
                    Gate(m, 13, 0x2b); c.Step(); Check(c.Tr == 0x40 && c.HardwareTaskSwitches == 0 && m.ReadWord(0x1aff0) == 0x48);
                }
                else
                {
                    PagingTests.Pte(m, 0x9000, 0x9000, 3); PagingTests.Pte(m, 0xa000, 0xa000, 3);
                    c.Step(); Check(c.Tr == 0x48 && c.CurrentPrivilegeLevel == 0 && c.Eip == 0x5000);
                    c.Step(); Check(c.Tr == 0x40 && c.CurrentPrivilegeLevel == 3 && c.Eip == 0x2007 && c.GetRegister(Register.Sp) == 0x1b000);
                }
            });
        foreach (int ring in new[] { 1, 2, 3 }) foreach (bool conforming in new[] { false, true })
            test($"TSS CS.RPL chooses ring {ring} {(conforming ? "conforming" : "ordinary")} independently of old CPL", () =>
            {
                var (c, m) = Create(); byte codeAccess = (byte)(conforming ? 0x9e : 0x9a | (ring << 5));
                Descriptor(m, 0x60, 0, 0xfffff, codeAccess, 12); Descriptor(m, 0x68, 0, 0xfffff, (byte)(0x92 | (ring << 5)), 12);
                m.WriteWord(0xa04c, (ushort)(0x60 | ring)); m.WriteWord(0xa050, (ushort)(0x68 | ring)); m.WriteWord(0xa048, 0); m.WriteWord(0xa054, 0);
                At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x48 && c.CurrentPrivilegeLevel == ring && c.Eip == 0x5000);
                // Task IRET requires no access to the current user stack or CPL0 privilege.
                c.SetRegister(Register.Sp, 0xffffffff); c.Step(); Check(c.Tr == 0x40 && c.CurrentPrivilegeLevel == 0 && c.Eip == 0x2007);
            });
        foreach (uint field in new uint[] { 0x48, 0x54, 0x58, 0x5c })
            test($"incoming null data selector at {field:X} invalidates its old cache", () =>
            {
                var (c, m) = Create(); c.SetSegment(Segment.Fs, 16); c.SetSegment(Segment.Gs, 16);
                m.WriteWord(0xa000 + field, 3); At(c, m, Transfer(), 0x2000);
                Segment segment = field == 0x48 ? Segment.Es : field == 0x54 ? Segment.Ds : field == 0x58 ? Segment.Fs : Segment.Gs;
                Check(c.Tr == 0x48 && c.GetSegment(segment) == 3 && !c.GetSegmentCache(segment).Usable);
            });
        foreach (bool absentStack in new[] { false, true })
            test($"post-commit absent {(absentStack ? "SS gives SS" : "CS gives NP")} and handler task saves adopted selectors", () =>
            {
                var (c, m) = Create(); Descriptor(m, 0x68, 0, 0xfffff, (byte)(absentStack ? 0x12 : 0x1a), 12);
                m.WriteWord(absentStack ? 0xa050u : 0xa04cu, 0x68);
                byte vector = absentStack ? (byte)12 : (byte)11; TaskGate(m, 0, 0x50, address: 0x8000u + (uint)vector * 8);
                At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x50 && c.HardwareTaskSwitches == 2 && m.ReadWord(0x1bffc) == 0x68 && m.ReadWord(0xb000) == 0x48);
                Check(m.ReadWord(absentStack ? 0xa050u : 0xa04cu) == 0x68 && m.ReadDword(0xa020) == 0x5000);
            });
        test("fault delivery through task gate keeps committed task when a second task faults and DF uses third task", () =>
        {
            var (c, m) = Create(); m.WriteByte(0x2000, 0x8e); m.WriteByte(0x2001, 0xd8); c.SetRegister(Register.Ax, 0x100);
            m.WriteWord(0xa054, 0x100); TaskGate(m, 0, address: 0x8000 + 13 * 8);
            TaskGate(m, 0, 0x50, address: 0x8000 + 8 * 8); c.Step();
            Check(c.Tr == 0x50 && c.HardwareTaskSwitches == 2 && c.TaskGateTransfers == 2 && m.ReadWord(0xb000) == 0x48 && m.ReadDword(0xa020) == 0x5000);
            Check(m.ReadDword(0xa034) == Pattern(3) && m.ReadWord(0x1bffc) == 0 && !c.TripleFaulted);
        });
        foreach (bool wide in new[] { false, true })
            test($"exception task gate pushes {(wide ? "dword" : "word")} error slot from TSS type", () =>
            {
                var (c, m) = Create(new32: wide); m.WriteByte(0x2000, 0x8e); m.WriteByte(0x2001, 0xd8); c.SetRegister(Register.Ax, 0x100);
                TaskGate(m, 0, address: 0x8000 + 13 * 8); c.Step(); uint sp = c.GetRegister(Register.Sp) & (wide ? uint.MaxValue : 65535);
                Check(c.Tr == 0x48 && sp == (wide ? 0x19ffcu : 0x6ffeu) && m.ReadWord(sp) == 0x100);
                Check(m.ReadDword(0x9020) == 0x2000 && (m.ReadDword(0x9024) & 0x10000) != 0);
            });
        foreach (bool wp in new[] { false, true })
            test($"task-state writes respect supervisor WP={wp}", () =>
            {
                var (c, m) = Create(paging: true); PagingTests.Pte(m, 0x9000, 0x9000, 1);
                if (!wp) { c.SetRegister(Register.Ax, c.Cr0 & ~0x10000u); At(c, m, [0x0f, 0x22, 0xc0]); }
                Transfer().CopyTo(m.Ram, 0x2000); c.Eip = 0x2000;
                if (wp) { OldFault(c, m, 14, 3); Check(c.Cr2 == 0x9020 && m.ReadDword(0x9020) == 0x5000); }
                else { c.Step(); Check(c.Tr == 0x48 && m.ReadDword(0x9020) == 0x2007); }
            });
        test("nonpaged hardware task leaves CR3 static, even with a different incoming PDBR", () =>
        {
            var (c, m) = Create(); m.WriteDword(0xa01c, 0x12345000); At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x48 && c.Cr3 == 0);
        });
        test("fault after task IRET commits resumed task instead of restoring terminated task", () =>
        {
            var (c, m) = Create(); At(c, m, Transfer(), 0x2000); m.WriteWord(0x9054, 0x100); c.Step();
            Check(c.Tr == 0x40 && c.HardwareTaskSwitches == 2 && c.HardwareTaskReturns == 1 && c.Eip == 0x6000);
            Check(c.GetRegister(Register.Sp) == 0x17ff0 && m.ReadWord(0x17ff0) == 0x100 && m.ReadDword(0x17ff4) == 0x2007);
            Check(m.ReadByte(0x445) == 0x8b && m.ReadByte(0x44d) == 0x89 && m.ReadDword(0xa020) == 0x5001);
        });
        test("a task-switch TS fault is repairable through a separate task and real IRET", () =>
        {
            var (c, m) = Create(); m.WriteWord(0xa054, 0x100); TaskGate(m, 0, 0x50, address: 0x8000 + 10 * 8);
            At(c, m, Transfer(), 0x2000); Check(c.Tr == 0x50 && m.ReadWord(0xb000) == 0x48);
            m.WriteWord(0xa054, 16); m.WriteByte(0x6000, 0xcf); c.SetRegister(Register.Sp, 0x1c000); c.Step();
            Check(c.Tr == 0x48 && c.Eip == 0x5000 && c.GetSegment(Segment.Ds) == 16 && c.GetRegister(Register.Bx) == Pattern(3));
            c.Step(); Check(c.Tr == 0x40 && c.Eip == 0x2007 && c.HardwareTaskSwitches == 4 && c.HardwareTaskReturns == 2);
        });
        test("every hardware task switch sets TS so first x87 operation raises NM at new EIP", () =>
        {
            var (c, m) = Create(); m.WriteByte(0x5000, 0xdb); m.WriteByte(0x5001, 0xe3); At(c, m, Transfer(), 0x2000);
            c.Step(); Check(c.Tr == 0x48 && c.Eip == 0x6000 && c.X87Instructions == 0 && m.ReadDword(0x19ff4) == 0x5000);
            Check(c.FpuControlWord == 0x37f && (c.Cr0 & 8) != 0);
        });
        test("hardware task switch without usable current TR gives TS zero before state saving", () =>
        {
            var (c, m) = ProtectedModeTests.Rig(Transfer()); Task(m, 0x48, 0xa000, true);
            Fault(c, m, 10, 0); Check(c.Tr == 0 && c.HardwareTaskSwitches == 0 && m.ReadByte(0x44d) == 0x89);
        });
        test("NT IRET with absent busy target gives NP before clearing either busy bit", () =>
        {
            var (c, m) = Create(); c.Eflags |= 0x4000; m.WriteWord(0x9000, 0x48); Descriptor(m, 0x48, 0xa000, 0x67, 0x0b);
            m.WriteByte(0x2000, 0xcf); OldFault(c, m, 11, 0x48); Check(m.ReadByte(0x44d) == 0x0b);
        });
        test("outgoing short cached TSS faults even after its GDT entry was repaired", () =>
        {
            var (c, m) = ProtectedModeTests.Rig(Transfer()); Task(m, 0x48, 0xa000, true);
            Descriptor(m, 0x40, 0x9000, 0x66, 0x89); c.SetRegister(Register.Ax, 0x40); At(c, m, [0x0f, 0x00, 0xd8]);
            Descriptor(m, 0x40, 0x9000, 0x67, 0x8b); c.Eip = 0x2000; OldFault(c, m, 10, 0x40);
        });
    }
}
