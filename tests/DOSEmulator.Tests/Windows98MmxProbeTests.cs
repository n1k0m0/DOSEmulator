// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Non-MMX CPU exception semantics required by the original Windows 98 guarded EMMS probe.</summary>
internal static class Windows98MmxProbeTests
{
    /// <summary>Reports an architectural invariant independently of the decoder's implementation.</summary>
    private static void Check(bool value, string detail)
    { if (!value) throw new Exception(detail); }

    /// <summary>Observes all public raw x87 registers, tags and environment without executing another FPU instruction.</summary>
    private static string Fpu(X86Cpu c) => string.Join("/", c.FpuControlWord, c.FpuStatusWord, c.FpuTagWord,
        c.FpuInstructionPointer, c.FpuDataPointer, c.FpuCodeSelector, c.FpuDataSelector, c.FpuOpcode,
        c.FpuErrorAsserted, string.Join(",", Enumerable.Range(0, 8).Select(i => c.GetFpuRegister(i).ToString())));

    /// <summary>Creates nonempty, unequal x87 stack values through actual instructions before attempting EMMS.</summary>
    private static void Seed(X86Cpu c, MemoryBus m)
    {
        foreach (byte[] code in new byte[][] { [0xdb, 0xe3], [0xd9, 0xeb], [0xd9, 0xee] })
        { code.CopyTo(m.Ram, 0x2200); c.Eip = 0x2200; c.Step(); }
        Check(c.FpuTagWord != 0xffff && c.GetFpuRegister(0) != c.GetFpuRegister(1), "x87 seed did not produce distinct live values.");
    }

    /// <summary>Requires restartable #UD with unchanged raw x87/GPR state and a working architectural IRET.</summary>
    private static void Protected(X86Cpu c, MemoryBus m, uint ip, int length = 2, bool user = false)
    {
        string fpu = Fpu(c);
        uint[] registers = Enum.GetValues<Register>().Select(c.GetRegister).ToArray();
        ushort cs = c.GetSegment(Segment.Cs);
        uint stack = c.GetRegister(Register.Sp);
        byte? vector = null; c.InterruptEntered += v => vector = v;
        c.Step(); uint frame = c.GetRegister(Register.Sp);
        Check(vector == 6 && c.Eip == 0x6000 && !c.TripleFaulted, "EMMS did not enter the guest #UD handler.");
        Check(frame == (user ? 0x1a000u - 20 : stack - 12) && m.ReadDword(frame) == ip && m.ReadDword(frame + 4) == cs,
            "EMMS #UD lost restart CS:IP or pushed an error word.");
        Check((m.ReadDword(frame + 8) & 0x10000) != 0 && c.CurrentPrivilegeLevel == 0, "EMMS #UD lost RF or ring-zero delivery.");
        if (user) Check(m.ReadDword(frame + 12) == stack && m.ReadDword(frame + 16) == 0x33, "EMMS lost the user stack.");
        foreach (Register r in Enum.GetValues<Register>())
            if (r != Register.Sp) Check(c.GetRegister(r) == registers[(int)r], "Rejected EMMS changed a GPR.");
        Check(Fpu(c) == fpu, "Rejected EMMS emptied tags or changed raw x87/environment state.");
        m.WriteDword(frame, ip + (uint)length); m.WriteByte(0x6000, 0xcf); c.Step();
        Check(c.Eip == ip + length && c.GetSegment(Segment.Cs) == cs && c.GetRegister(Register.Sp) == stack &&
            c.CurrentPrivilegeLevel == (user ? 3 : 0) && Fpu(c) == fpu, "Actual IRET did not resume the guarded probe.");
    }

    /// <summary>Registers modes, exception precedence, exact instruction boundaries and feature-advertisement controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        {
            foreach (bool code32 in new[] { false, true })
            foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67, 0xf3], [0xf0] })
                test($"M4 Win98 EMMS UD protected model={model} code32={code32} prefix={Convert.ToHexString(prefix)}", () =>
                {
                    var (c, m) = ProtectedModeTests.Rig([.. prefix, 0x0f, 0x77], code32: code32, model: model);
                    Seed(c, m); c.Eip = 0x2000; c.SetRegister(Register.Ax, 0x81234567); c.Eflags |= X86Cpu.Direction;
                    Protected(c, m, 0x2000, prefix.Length + 2);
                });
            foreach (uint emTs in new uint[] { 0, 4, 8, 12 })
                test($"M4 Win98 EMMS absent MMX precedes NM model={model} EMTS={emTs}", () =>
                {
                    var (c, m) = ProtectedModeTests.Rig([0x0f, 0x77], model: model); Seed(c, m);
                    c.SetRegister(Register.Ax, c.Cr0 | emTs); PagingTests.At(c, m, [0x0f, 0x22, 0xc0]); c.Eip = 0x2000;
                    Protected(c, m, 0x2000);
                });
            foreach (bool prefixed in new[] { false, true })
                test($"M4 Win98 EMMS UD real six-byte frame model={model} prefix66={prefixed}", () =>
                {
                    var m = new MemoryBus(2 * 1048576); var c = new X86Cpu(m, new(), model);
                    c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0); c.SetRegister(Register.Sp, 0x9000);
                    Seed(c, m); string fpu = Fpu(c);
                    byte[] code = prefixed ? [0x66, 0x0f, 0x77] : [0x0f, 0x77]; code.CopyTo(m.Ram, 0x2000); c.Eip = 0x2000;
                    m.WriteWord(24, 0x6000); m.WriteWord(26, 0); m.WriteByte(0x6000, 0xcf);
                    byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                    Check(vector == 6 && c.Eip == 0x6000 && c.GetRegister(Register.Sp) == 0x8ffa &&
                        m.ReadWord(0x8ffa) == 0x2000 && m.ReadWord(0x8ffc) == 0 && Fpu(c) == fpu, "Real EMMS lost its #UD frame or x87 state.");
                    m.WriteWord(0x8ffa, (ushort)(0x2000 + code.Length)); c.Step();
                    Check(c.Eip == 0x2000 + code.Length && c.GetRegister(Register.Sp) == 0x9000 && Fpu(c) == fpu, "Real EMMS IRET failed.");
                });
            test($"M4 Win98 EMMS UD ring-three TSS stack model={model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f, 0x77], model: model); Seed(c, m);
                PagingTests.Tss(c, m); PagingTests.User(c, m); Protected(c, m, 0x2000, user: true);
            });
            test($"M4 Win98 EMMS UD virtual8086 full monitor frame model={model}", () =>
            {
                var (c, m) = PagingTests.Create([0x90], model: model); Seed(c, m); string fpu = Fpu(c); PagingTests.Tss(c, m);
                uint[] image = [0x100, 0x3000, 0x20202, 0xabcd8000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
                for (int i = 0; i < image.Length; i++) m.WriteDword((uint)(0x18000 + i * 4), image[i]);
                new byte[] { 0x0f, 0x77 }.CopyTo(m.Ram, 0x30100); PagingTests.At(c, m, [0xcf]);
                Check(c.Virtual8086 && c.Eip == 0x100, "Fixture did not enter V86.");
                byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                uint frame = c.GetRegister(Register.Sp);
                Check(vector == 6 && frame == 0x19fdc && !c.Virtual8086 && c.CurrentPrivilegeLevel == 0, "V86 EMMS did not use a no-error monitor frame.");
                for (int i = 0; i < image.Length; i++) Check(m.ReadDword(frame + (uint)i * 4) == (i == 2 ? image[i] | 0x10000u : image[i]), "V86 EMMS lost a saved context field.");
                Check(Fpu(c) == fpu && !c.TripleFaulted, "V86 EMMS changed x87 state or triple faulted.");
                m.WriteDword(frame, 0x102); m.WriteByte(0x6000, 0xcf); c.Step();
                Check(c.Virtual8086 && c.Eip == 0x102 && c.GetRegister(Register.Sp) == 0xabcd8000 && Fpu(c) == fpu, "V86 EMMS IRETD failed.");
            });
            test($"M4 Win98 EMMS complete last-page opcode never fetches operand model={model}", () =>
            {
                var (c, m) = PagingTests.Create([0x90], model: model); Seed(c, m);
                m.WriteByte(0x2ffe, 0x0f); m.WriteByte(0x2fff, 0x77); PagingTests.Pte(m, 0x3000, 0x3000, 0); c.Eip = 0x2ffe;
                Protected(c, m, 0x2ffe); Check(c.Cr2 == 0, "Complete EMMS fetched nonexistent operand bytes.");
            });
            test($"M4 Win98 EMMS missing second byte faults before UD model={model}", () =>
            {
                var (c, m) = PagingTests.Create([0x90], model: model); m.WriteByte(0x2fff, 0x0f);
                PagingTests.Pte(m, 0x3000, 0x3000, 0); c.Eip = 0x2fff; ProtectedModeTests.Fault(c, m, 14, 0, 0x2fff);
                Check(c.Cr2 == 0x3000, "Opcode fetch #PF did not retain CR2.");
            });
            test($"M4 Win98 EMMS complete last-CS bytes have no operand model={model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f, 0x77], code32: false, target: 0xfffe, model: model);
                ProtectedModeTests.Fault(c, m, 6, null, 0xfffe);
            });
            test($"M4 Win98 EMMS missing second byte respects CS limit model={model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f], code32: false, target: 0xffff, model: model);
                ProtectedModeTests.Fault(c, m, 13, 0, 0xffff);
            });
            test($"M4 Win98 EMMS correction retains CPU feature contract model={model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f, 0xa2], model: model); c.SetRegister(Register.Ax, 1);
                if (model == X86CpuModel.Intel486Dx) ProtectedModeTests.Fault(c, m, 6, null);
                else { c.Step(); Check((c.GetRegister(Register.Dx) & (1u << 23)) == 0, "P54C falsely advertised MMX."); }
            });
            test($"M4 Win98 EMMS correction retains unclaimed neighboring diagnostic model={model}", () =>
            {
                var (c, _) = ProtectedModeTests.Rig([0x0f, 0x76], model: model);
                try { c.Step(); } catch (UnsupportedInstructionException) { return; }
                throw new Exception("EMMS correction broadened an unclaimed neighboring opcode.");
            });
        }
    }
}
