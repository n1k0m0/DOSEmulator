// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Reference boundaries for synchronous GD faults, consecutive SS loads and their bounded deferral.</summary>
internal static class SsContractTests
{
    /// <summary>Requires a specific frame, register or recognition boundary.</summary>
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>Writes DR7 by an actual instruction, without modifying private CPU storage.</summary>
    private static void Arm(X86Cpu c, MemoryBus m, uint value)
    {
        uint saved = c.Eip; c.SetRegister(Register.Ax, value);
        PagingTests.At(c, m, [0x0f, 0x23, 0xf8], 0x2400); c.Eip = saved;
    }
    /// <summary>Builds real or actual PE16/PE32/paged contexts with an independently based replacement stack.</summary>
    private static (X86Cpu C, MemoryBus M, ushort Ss) Rig(int mode, byte[] bytes)
    {
        if (mode == 0)
        {
            var m = new MemoryBus(2097152); var c = new X86Cpu(m, new());
            c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0); c.Eip = 0x2000; c.SetRegister(Register.Sp, 0x9000);
            bytes.CopyTo(m.Ram, 0x2000); m.WriteWord(4, 0x6000); m.WriteWord(6, 0);
            return (c,m,0x1000);
        }
        var pair = mode == 3 ? PagingTests.Create(bytes) : ProtectedModeTests.Rig(bytes, code32: mode == 2);
        ProtectedModeTests.Descriptor(pair.Item2, 40, 0x10000, 0xfffff, 0x92, 12);
        return (pair.Item1,pair.Item2,40);
    }
    /// <summary>Checks fault-before-transfer and real IRET/retry, rather than converting GD into a postcommit trap.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int mode = 0; mode < 4; mode++) foreach (bool pop in new[] { false, true })
        foreach (bool write in new[] { false, true }) foreach (int dr in new[] { 0,4,6,7 })
        {
            int context = mode;
            test($"SS contract GD mode{context} pop={pop} write={write} DR{dr} faults before transfer and retries", () =>
            {
                byte[] ss = pop ? [0x17] : [0x8e,0xd2];
                byte[] mov = [0x0f,(byte)(write ? 0x23 : 0x21),(byte)(0xc0 | dr << 3)];
                var (c,m,selector) = Rig(context, [..ss,..mov,0x90]); Arm(c,m,0x2000);
                c.SetRegister(Register.Ax,0x12345678); c.SetRegister(Register.Dx,selector); c.Eflags = 0x202;
                uint originalSp = c.GetRegister(Register.Sp); m.WriteDword(originalSp,selector);
                c.Step(); uint afterSs = c.Eip, afterSp = c.GetRegister(Register.Sp);
                Check(!c.CanAcceptMaskableInterrupt && c.GetSegment(Segment.Ss) == selector, "SS committed with IRQ shadow");
                Check(afterSp == originalSp + (pop ? (context is 2 or 3 ? 4u : 2u) : 0u), "Actual POP width");
                c.Step(); uint frame = c.GetRegister(Register.Sp), physical = 0x10000 + frame;
                Check(c.Eip == 0x6000 && c.GeneralDetectFaults == 1 && (c.Dr6 & 0x2000) != 0 && (c.Dr7 & 0x2000) == 0, "GD is synchronous and clears its own guard");
                Check(c.GetRegister(Register.Ax) == 0x12345678 && c.GetSegment(Segment.Ss) == selector && c.Dr0 == 0, "No premature target or old-SS rollback");
                Check(context == 0 ? m.ReadWord(physical) == afterSs : m.ReadDword(physical) == afterSs, "Fault frame points at original MOV DR");
                m.WriteByte(0x6000,0xcf); c.Step();
                Check(c.Eip == afterSs && c.GetRegister(Register.Sp) == afterSp && c.CanAcceptMaskableInterrupt, "Real IRET restores committed SS with expired shadow");
                c.Step(); Check(c.Eip == afterSs+3 && c.GeneralDetectFaults == 1, "Exactly one actual GD fault, successful retry");
            });
        }
        foreach (bool firstPop in new[] { false,true }) foreach (bool secondPop in new[] { false,true })
        foreach (bool trap in new[] { false,true })
        {
            test($"SS contract consecutive pop={firstPop}/{secondPop} bounded reference deferral TF={trap}", () =>
            {
                byte[] a = firstPop ? [0x17] : [0x8e,0xd0]; byte[] b = secondPop ? [0x17] : [0x8e,0xd0];
                var (c,m) = ProtectedModeTests.Rig([..a,..b,0xbc,0x00,0x70,0x01,0x00,0x90]);
                c.SetRegister(Register.Ax,16); m.WriteDword(0x18000,16); m.WriteDword(0x18004,16);
                c.Eflags = trap ? 0x302u : 0x202;
                c.Step(); Check(!c.CanAcceptMaskableInterrupt && c.SingleStepTraps == 0,"First SS defers");
                c.Step(); Check(!c.CanAcceptMaskableInterrupt && c.SingleStepTraps == 0,"DOSE consecutive-SS policy, not a portable chip guarantee");
                c.Step();
                if (trap) Check(c.Eip == 0x6000 && c.SingleStepTraps == 1 && m.ReadDword(c.GetRegister(Register.Sp)) == 0x2000 + a.Length + b.Length + 5,"One merged TF trap on fully committed ESP");
                else Check(c.CanAcceptMaskableInterrupt && c.GetRegister(Register.Sp) == 0x17000,"First ordinary instruction ends inhibition");
            });
        }
    }
}
