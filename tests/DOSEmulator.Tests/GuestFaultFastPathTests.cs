// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Independent architectural/restart controls for the value-based guest-fault fast path.</summary>
internal static class GuestFaultFastPathTests
{
    /// <summary>Own virtual8086 code location, deliberately separate from all descriptor/table/handler memory.</summary>
    private const uint VmBase = 0x30000, VmIp = 0x100, RingStack = 0x1a000;
    /// <summary>Rejects a concrete wrong frame, fetch order or state transition.</summary>
    private static void Check(bool condition, string reason = "Guest fault fast-path contract failed")
    { if (!condition) throw new Exception(reason); }

    /// <summary>Own CPU/bus fixture and optional stateful fetch/data observer.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory);

    /// <summary>Real mapped byte effects prove that fetching ModRM remains observable but data decoding does not occur.</summary>
    private sealed class ByteDevice(byte value) : IMemoryDevice
    {
        /// <summary>Actual mapped reads, including real CPU code fetches.</summary>
        public int Reads;
        /// <summary>Actual writes that must not be invented by a rejected instruction.</summary>
        public int Writes;
        /// <summary>Returns one controlled byte and retains the actual observation.</summary>
        public byte ReadByte(uint offset) { Reads++; return value; }
        /// <summary>Records an actual attempted mapped write.</summary>
        public void WriteByte(uint offset, byte item) { Writes++; }
    }

    /// <summary>Seeds all complete GPRs so low-word operations or restart cannot silently corrupt their high halves.</summary>
    private static uint[] Seed(X86Cpu c, uint stack)
    {
        uint[] values = [0xa1b21230, 0xc3d40003, 0xd4e50003, 0xb2c30110, stack, 0xe6f70120, 0xf7080130, 0x81920100];
        for (int i = 0; i < 8; i++) c.SetRegister((Register)i, values[i]);
        return values;
    }

    /// <summary>Checks every nonswitching GPR while the real exception frame deliberately changes ESP.</summary>
    private static void Registers(X86Cpu c, uint[] before)
    {
        for (int i = 0; i < 8; i++) if (i != 4)
            Check(c.GetRegister((Register)i) == before[i], "GPR changed across rejected ARPL: " + i);
    }

    /// <summary>Creates a real-mode guest with a genuine IVT UD handler and an untouched guard around its stack.</summary>
    private static Rig Real(byte[] code, X86CpuModel model = X86CpuModel.IntelP54cB5Subset)
    {
        var m = new MemoryBus(2 * 1024 * 1024);
        var c = model == X86CpuModel.Intel486Dx ? new X86Cpu(m, new(), model) :
            new X86Cpu(m, new(), model, new DeviceScheduler(), 100000000);
        c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0);
        c.SetSegment(Segment.Ds, 0x5000); c.SetSegment(Segment.Es, 0x6000);
        c.SetSegment(Segment.Fs, 0x7000); c.SetSegment(Segment.Gs, 0x8000);
        c.Eip = 0x1000; c.Eflags = 0x2d7; Seed(c, 0xa5a59000);
        code.CopyTo(m.Ram, 0x1000); m.WriteWord(24, 0x6000); m.WriteWord(26, 0);
        m.WriteByte(0x6000, 0xcf); m.Ram.AsSpan(0x8fe0, 64).Fill(0x5a);
        return new(c, m);
    }

    /// <summary>Programs debug comparators through actual guest MOV DR before entering virtual8086.</summary>
    private static void Debug(X86Cpu c, MemoryBus m, int number, uint value)
    {
        c.SetRegister(Register.Ax, value);
        PagingTests.At(c, m, [0x0f, 0x23, (byte)(0xc0 | number << 3)], 0x2400);
    }

    /// <summary>Enters virtual8086 via real LTR/IRET instructions and a real ring-zero exception stack.</summary>
    private static Rig Virtual(byte[] code, uint ip = VmIp, uint flags = 0x232d7, bool paging = false,
        bool executionBreakpoint = false, X86CpuModel model = X86CpuModel.IntelP54cB5Subset)
    {
        DeviceScheduler? clock = model == X86CpuModel.Intel486Dx ? null : new DeviceScheduler();
        uint frequency = clock is null ? 0u : 100000000u;
        var (c, m) = paging
            ? PagingTests.Create([0x90], model: model, timestampSource: clock, timestampFrequency: frequency)
            : ProtectedModeTests.Rig([0x90], model: model, timestampSource: clock, timestampFrequency: frequency);
        PagingTests.Tss(c, m);
        if (executionBreakpoint) { Debug(c, m, 0, VmBase + ip); Debug(c, m, 7, 1); }
        uint sp = c.GetRegister(Register.Sp);
        uint[] frame = [ip, 0x3000, flags, 0xa5a58000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
        for (int i = 0; i < frame.Length; i++) m.WriteDword(sp + (uint)i * 4, frame[i]);
        code.CopyTo(m.Ram, (int)(VmBase + ip));
        PagingTests.At(c, m, [0xcf], 0x2200);
        Check(c.Virtual8086 && c.Eip == ip && c.GetSegmentCache(Segment.Cs).Limit == 0xffff, "Fixture did not enter real V86.");
        Seed(c, 0xa5a58000); m.WriteByte(0x6000, 0xcf);
        m.Ram.AsSpan((int)RingStack - 64, 96).Fill(0x5a);
        return new(c, m);
    }

    /// <summary>Checks every actual virtual8086-to-ring-zero frame slot, including all preserved virtual segment selectors.</summary>
    private static uint VmFrame(Rig r, uint ip, uint flags, uint? error = null, bool synthesizeRf = true)
    {
        var c = r.Cpu; var m = r.Memory;
        uint sp = RingStack - 36 - (error.HasValue ? 4u : 0);
        Check(c.Eip == 0x6000 && c.GetSegment(Segment.Cs) == 8 && c.GetSegment(Segment.Ss) == 16 &&
            c.GetRegister(Register.Sp) == sp && c.CurrentPrivilegeLevel == 0 && !c.Virtual8086 && !c.TripleFaulted, "Wrong delivered V86 context.");
        uint at = sp;
        if (error.HasValue) { Check(m.ReadWord(at) == error.Value, "Wrong architectural low error word."); at += 4; }
        uint[] expected = [ip, 0x3000, flags | (synthesizeRf ? 0x10000u : 0), 0xa5a58000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
        for (int i = 0; i < expected.Length; i++) Check(m.ReadDword(at + (uint)i * 4) == expected[i], "Wrong V86 frame slot " + i);
        Check((c.Eflags & (X86Cpu.Trap | X86Cpu.InterruptEnable | 0x30000u)) == 0, "Live TF/IF/RF/VM survived interrupt-gate delivery.");
        Check(m.ReadByte(RingStack) == 0x5a && m.ReadByte(sp - 1) == 0x5a, "Exception frame escaped its exact stack extent.");
        return sp;
    }

    /// <summary>Executes one real rejected instruction and checks the complete16-bit IVT frame and step accounting.</summary>
    private static void RealUd(Rig r, int fetched)
    {
        var c = r.Cpu; var m = r.Memory; var before = Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray();
        uint flags = c.Eflags; long instructions = c.Instructions, vm = c.Virtual8086Instructions, pe = c.ProtectedInstructions;
        var entered = new List<byte>(); c.InterruptEntered += entered.Add;
        Check(c.Step() == 4 && entered.SequenceEqual(new byte[] { 6 }) && c.Eip == 0x6000 && !c.TripleFaulted);
        Check(c.GetRegister(Register.Sp) == 0xa5a58ffa && m.ReadWord(0x8ffa) == 0x1000 && m.ReadWord(0x8ffc) == 0 &&
            m.ReadWord(0x8ffe) == (ushort)flags, "Wrong real-mode UD frame.");
        Check(c.Eflags == (flags & ~(X86Cpu.Trap | X86Cpu.InterruptEnable)) && c.Instructions == instructions + 1 &&
            c.ProtectedInstructions == pe && c.Virtual8086Instructions == vm && c.SingleStepTraps == 0, "Real fault retired/trapped incorrectly.");
        Check(c.SaveState().Fields["_instructionBytes"].GetInt32() == fetched, "Wrong actual consumed instruction-byte count.");
        Registers(c, before);
    }

    /// <summary>Registers bounded new fast-fault controls; existing task/x87 matrices remain in their original suites.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        byte[][] forms = [[0x63,0xc8], [0x66,0x63,0x0d], [0x67,0x63,0x84],
            [0x2e,0x63,0x0d], [0xf3,0x63,0x0d], [0xf2,0x66,0x67,0x65,0x63,0x0d]];
        foreach (var model in new[] { X86CpuModel.Intel486Dx, X86CpuModel.IntelP54cB5Subset })
        foreach (byte[] code in forms)
        foreach (bool vm in new[] { false, true })
            test($"GuestFault ARPL {model} {(vm ? "V86" : "real")} frame/prefix {Convert.ToHexString(code)}", () =>
            {
                var r = vm ? Virtual(code, model: model) : Real(code, model);
                var data = new ByteDevice(0x5a); r.Memory.Map(0x50000, 4096, data, "own ARPL data effect guard");
                if (!vm) RealUd(r, code.Length);
                else
                {
                    var c = r.Cpu; uint flags = c.Eflags; uint[] registers = Seed(c, 0xa5a58000); long count = c.Instructions, virtualCount = c.Virtual8086Instructions;
                    var entered = new List<byte>(); c.InterruptEntered += entered.Add;
                    Check(c.Step() == 4 && entered.SequenceEqual(new byte[] { 6 })); VmFrame(r, VmIp, flags); Registers(c, registers);
                    Check(c.Instructions == count + 1 && c.Virtual8086Instructions == virtualCount + 1 && c.SingleStepTraps == 0);
                    Check(c.SaveState().Fields["_instructionBytes"].GetInt32() == code.Length);
                    c.Step(); Check(c.Virtual8086 && c.Eip == VmIp && c.GetRegister(Register.Sp) == 0xa5a58000 &&
                        c.Eflags == (flags | 0x10000), "Real IRET did not resume the faulting V86 instruction.");
                }
                Check(data.Reads == 0 && data.Writes == 0, "Illegal-mode ARPL decoded/read/wrote its operand.");
            });
        foreach (bool vm in new[] { false, true })
            test($"GuestFault {(vm ? "V86" : "real")} ARPL fetches real mapped ModRM exactly once", () =>
            {
                var r = vm ? Virtual([0x63,0xc8]) : Real([0x63,0xc8]);
                var fetch = new ByteDevice(0xc8); r.Memory.Map((vm ? VmBase + VmIp : 0x1000) + 1, 1, fetch, "own ModRM byte");
                if (!vm) RealUd(r, 2); else { uint f = r.Cpu.Eflags; r.Cpu.Step(); VmFrame(r, VmIp, f); }
                Check(fetch.Reads == 1 && fetch.Writes == 0);
            });
        foreach (bool vm in new[] { false, true })
            test($"GuestFault {(vm ? "V86" : "real")} ARPL fault suppresses TF and retains RF restart policy", () =>
            {
                var r = vm ? Virtual([0x63,0xc8], flags: 0x333d7) : Real([0x63,0xc8]);
                if (!vm) { r.Cpu.Eflags = 0x103d7; RealUd(r, 2); }
                else { uint f = r.Cpu.Eflags; r.Cpu.Step(); VmFrame(r, VmIp, f); Check(r.Cpu.SingleStepTraps == 0); }
            });
        foreach (int mode in new[] { 0, 1, 2 })
            test($"GuestFault V86 ARPL page boundary preserves original ModRM/LOCK order {mode}", () =>
            {
                uint start = mode == 0 ? 0xfffu : 0xffeu;
                byte[] code = mode == 0 ? [0x63] : mode == 1 ? [0xf0,0x63] : [0x63,0x84];
                var r = Virtual(code, start, paging: true); uint flags = r.Cpu.Eflags;
                PagingTests.Pte(r.Memory, 0x31000, 0, 0); long pages = r.Cpu.PageFaults;
                var entered = new List<byte>(); r.Cpu.InterruptEntered += entered.Add;
                r.Cpu.Step();
                if (mode == 0)
                {
                    Check(entered.SequenceEqual(new byte[] { 14 }) && r.Cpu.Cr2 == 0x31000 && r.Cpu.PageFaults == pages + 1);
                    VmFrame(r, start, flags, 4);
                }
                else
                {
                    Check(entered.SequenceEqual(new byte[] { 6 }) && r.Cpu.PageFaults == pages);
                    VmFrame(r, start, flags);
                    Check(r.Cpu.InvalidLockPrefixes == (mode == 1 ? 1 : 0));
                }
            });
        foreach (bool locked in new[] { false, true })
            test($"GuestFault V86 CS-limit check precedes missing ModRM locked={locked}", () =>
            {
                uint start = locked ? 0xfffeu : 0xffffu;
                var r = Virtual(locked ? [0xf0,0x63] : [0x63], start); uint flags = r.Cpu.Eflags;
                var entered = new List<byte>(); r.Cpu.InterruptEntered += entered.Add; r.Cpu.Step();
                Check(entered.SequenceEqual(new byte[] { (byte)(locked ? 6 : 13) }));
                VmFrame(r, start, flags, locked ? null : 0);
            });
        foreach (int form in new[] { 0, 1, 2 })
            test($"GuestFault V86 exact15-byte prefix/ModRM budget {form}", () =>
            {
                byte[] code = form == 0 ? [..Enumerable.Repeat((byte)0x66,13),0x63,0xc8] :
                    form == 1 ? [..Enumerable.Repeat((byte)0x66,14),0x63,0xc8] :
                    [..Enumerable.Repeat((byte)0x66,13),0xf0,0x63,0xc8];
                var r = Virtual(code); uint flags = r.Cpu.Eflags;
                var entered = new List<byte>(); r.Cpu.InterruptEntered += entered.Add; r.Cpu.Step();
                Check(entered.SequenceEqual(new byte[] { (byte)(form == 1 ? 13 : 6) }));
                VmFrame(r, VmIp, flags, form == 1 ? 0 : null);
                Check(r.Cpu.SaveState().Fields["_instructionBytes"].GetInt32() == (form == 1 ? 16 : 15));
            });
        foreach (bool rf in new[] { false, true })
            test($"GuestFault V86 execution comparator/RF precedence rf={rf}", () =>
            {
                var r = Virtual([0x63,0xc8], flags: rf ? 0x332d7u : 0x232d7u, executionBreakpoint: true);
                uint flags = r.Cpu.Eflags; var entered = new List<byte>(); r.Cpu.InterruptEntered += entered.Add;
                r.Cpu.Step(); Check(entered.SequenceEqual(new byte[] { (byte)(rf ? 6 : 1) }));
                VmFrame(r, VmIp, flags, synthesizeRf: rf);
                Check(r.Cpu.InstructionBreakpointFaults == (rf ? 0 : 1) && r.Cpu.SingleStepTraps == 0);
            });
        foreach (bool code32 in new[] { false, true })
        foreach (bool prefix in new[] { false, true })
        foreach (bool adjust in new[] { false, true })
            test($"GuestFault protected ARPL remains16-bit/ZF-only code32={code32} prefix66={prefix} adjust={adjust}", () =>
            {
                byte[] code = prefix ? [0x66,0x63,0xd0] : [0x63,0xd0];
                var (c,m) = ProtectedModeTests.Rig(code, code32: code32);
                c.SetRegister(Register.Ax, adjust ? 0xabcd1230u : 0xabcd1233u); c.SetRegister(Register.Dx, 0xbad00003); c.Eflags = 0x8d7;
                long count = c.Instructions; Check(c.Step() == 4 && c.Eip == 0x2000u + code.Length &&
                    c.GetRegister(Register.Ax) == 0xabcd1233 && c.Eflags == ((0x8d7u & ~0x40u) | (adjust ? 0x40u : 0)) &&
                    c.Instructions == count + 1 && !c.TripleFaulted);
            });
        test("GuestFault valid protected ARPL still commits before the real TF trap", () =>
        {
            var (c,m) = ProtectedModeTests.Rig([0x63,0xd0]); c.SetRegister(Register.Ax,0xabcd1230); c.SetRegister(Register.Dx,3); c.Eflags=0x10303;
            var entered=new List<byte>(); c.InterruptEntered+=entered.Add; c.Step(); uint sp=c.GetRegister(Register.Sp);
            Check(entered.SequenceEqual(new byte[]{1}) && c.Eip==0x6000 && c.SingleStepTraps==1 &&
                c.GetRegister(Register.Ax)==0xabcd1233 && m.ReadDword(sp)==0x2002 &&
                (m.ReadDword(sp+8)&0x10040)==0x40 && (c.Dr6&0x4000)!=0);
        });
        foreach (bool adjust in new[] { false, true })
            test($"GuestFault protected ARPL readonly destination faults before ZF/write adjust={adjust}", () =>
            {
                var (c,m)=PagingTests.Create([0x63,0x15,0,0,3,0]); c.SetRegister(Register.Dx,3);
                m.WriteWord(0x30000,adjust?(ushort)0x1230:(ushort)0x1233); PagingTests.Pte(m,0x30000,0x30000,1); c.Eflags=0x2d7;
                uint frame=Fault(c,m,14,3);Check(c.Cr2==0x30000 && m.ReadWord(0x30000)==(adjust?0x1230:0x1233) &&
                    m.ReadDword(frame+12)==0x102d7);
            });
        foreach (bool shutdown in new[] { false, true })
            test($"GuestFault ARPL UD-to-NP-to-DF escalation shutdown={shutdown}", () =>
            {
                var r=Virtual([0x63,0xc8]);uint flags=r.Cpu.Eflags;
                Gate(r.Memory,6,access:0x0e);Gate(r.Memory,11,access:0x0e);Gate(r.Memory,8,access:shutdown?(byte)0x0e:(byte)0x8e);
                var entered=new List<byte>();r.Cpu.InterruptEntered+=entered.Add;long count=r.Cpu.Instructions;
                Check(r.Cpu.Step()==4 && r.Cpu.Instructions==count+1);
                if(shutdown){Check(r.Cpu.Halted && r.Cpu.TripleFaulted && entered.Count==0);Check(r.Cpu.Step()==0 && r.Cpu.Instructions==count+1);}
                else{Check(entered.SequenceEqual(new byte[]{8}));VmFrame(r,VmIp,flags,0);}
            });
        test("GuestFault real IDT-limit delivery failure keeps the original unwrapped unsupported boundary", () =>
        {
            var r=Real([0x63,0xc8]);r.Memory.WriteWord(0x50200,0x17);r.Memory.WriteDword(0x50202,0);
            PagingTests.At(r.Cpu,r.Memory,[0x0f,0x01,0x1e,0,2],0x1200);r.Cpu.Eip=0x1000;long count=r.Cpu.Instructions;
            try{r.Cpu.Step();throw new Exception("Expected real IDT-limit delivery refusal.");}
            catch(UnsupportedInstructionException ex){Check(ex.Message.StartsWith("Real-mode IDT limit exceeded",StringComparison.Ordinal) && r.Cpu.Instructions==count);}
        });
    }
}
