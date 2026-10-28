// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Architectural fault delivery for the two-byte reserved encoding used by the original Windows setup DPMI probe.</summary>
internal static class Windows95UndefinedOpcodeTests
{
    /// <summary>Reports the independently expected fault, frame, or memory invariant.</summary>
    private static void Check(bool value, string detail)
    { if (!value) throw new Exception(detail); }

    /// <summary>Checks restartable #UD without an error word or a speculative ModR/M/data access.</summary>
    private static void Protected(X86Cpu cpu, MemoryBus memory, uint ip, bool user = false, int length = 2)
    {
        uint[] registers = Enum.GetValues<Register>().Select(cpu.GetRegister).ToArray();
        memory.WriteDword(0x28000, 0xc0dec0de);
        ushort cs = cpu.GetSegment(Segment.Cs);
        uint previousSp = cpu.GetRegister(Register.Sp) & (cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 65535);
        byte? entered = null; cpu.InterruptEntered += vector => entered = vector; cpu.Step();
        uint frame = cpu.GetRegister(Register.Sp) & (cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 65535);
        Check(entered == 6 && cpu.Eip == 0x6000 && memory.ReadDword(frame) == ip &&
            (memory.ReadDword(frame + 8) & 0x10000) != 0, "#UD lost its fault vector, restart IP, or saved RF.");
        Check(cpu.CurrentPrivilegeLevel == 0 && memory.ReadDword(frame + 4) == cs, "#UD did not retain the actual caller CS.");
        Check(frame == (user ? 0x1a000u - 20 : previousSp - 12), "#UD pushed an unexpected error word or privilege frame.");
        if (user) Check(memory.ReadDword(frame + 12) == previousSp && memory.ReadDword(frame + 16) == 0x33, "#UD lost the old ring-three stack.");
        foreach (Register register in Enum.GetValues<Register>())
            if (register != Register.Sp) Check(cpu.GetRegister(register) == registers[(int)register], "Reserved encoding modified a GPR.");
        Check(memory.ReadDword(0x28000) == 0xc0dec0de, "Reserved encoding performed a data write.");
        Check(!cpu.TripleFaulted && cpu.Cr2 == 0, "Reserved encoding produced an unexpected page or nested fault.");
        memory.WriteDword(frame, ip + (uint)length);
        memory.WriteByte(0x6000, 0xcf);
        cpu.Step();
        Check(cpu.GetSegment(Segment.Cs) == cs && cpu.Eip == ip + length && cpu.CurrentPrivilegeLevel == (user ? 3 : 0),
            "The actual IRET did not return through the fault frame.");
    }

    /// <summary>Registers real, protected, paged, segment-edge and ring-three architectural controls on both CPU models.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        {
            foreach (bool code32 in new[] { false, true }) foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67, 0xf3], [0xf0] })
                test($"M4 Win95 reserved0FFF protected frame model={model} code32={code32} prefix={Convert.ToHexString(prefix)}", () =>
                {
                    var (cpu, memory) = ProtectedModeTests.Rig([.. prefix, 0x0f, 0xff, 0x55, 0x8b, 0xec], code32: code32, model: model);
                    cpu.SetRegister(Register.Ax, 0x81234567); cpu.SetRegister(Register.Di, 0x28000); cpu.Eflags |= X86Cpu.Direction;
                    Protected(cpu, memory, 0x2000, length: prefix.Length + 2);
                });
            foreach (bool prefix in new[] { false, true })
                test($"M4 Win95 reserved0FFF real frame model={model} prefix66={prefix}", () =>
                {
                    var memory = new MemoryBus(2 * 1048576); var cpu = new X86Cpu(memory, new(), model);
                    cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0); cpu.Eip = 0x2000; cpu.SetRegister(Register.Sp, 0x9000);
                    byte[] code = prefix ? [0x66, 0x0f, 0xff] : [0x0f, 0xff]; code.CopyTo(memory.Ram, 0x2000);
                    memory.WriteWord(24, 0x6000); memory.WriteWord(26, 0); memory.WriteByte(0x6000, 0xcf);
                    byte? entered = null; cpu.InterruptEntered += vector => entered = vector; cpu.Step();
                    Check(entered == 6 && cpu.Eip == 0x6000 && cpu.GetRegister(Register.Sp) == 0x8ffa, "Real #UD has the wrong vector or six-byte frame.");
                    Check(memory.ReadWord(0x8ffa) == 0x2000 && memory.ReadWord(0x8ffc) == 0 && !cpu.TripleFaulted, "Real #UD did not save the instruction start.");
                    memory.WriteWord(0x8ffa, (ushort)(0x2000 + code.Length)); cpu.Step();
                    Check(cpu.Eip == 0x2000 + code.Length && cpu.GetRegister(Register.Sp) == 0x9000, "Real IRET did not resume after the reserved encoding.");
                });
            test($"M4 Win95 reserved0FFF last code-page bytes do not fetch a ModRM model={model}", () =>
            {
                var (cpu, memory) = PagingTests.Create([0x90], model: model);
                memory.WriteByte(0x2ffe, 0x0f); memory.WriteByte(0x2fff, 0xff); PagingTests.Pte(memory, 0x3000, 0x3000, 0);
                cpu.Eip = 0x2ffe; Protected(cpu, memory, 0x2ffe);
            });
            test($"M4 Win95 reserved0FFF last CS bytes do not consume a ModRM model={model}", () =>
            {
                var (cpu, memory) = ProtectedModeTests.Rig([0x0f, 0xff], code32: false, target: 0xfffe, model: model);
                // Do not return beyond the 16-bit limit in this boundary control.
                uint frame = ProtectedModeTests.Fault(cpu, memory, 6, null, 0xfffe);
                Check(memory.ReadDword(frame) == 0xfffe && !cpu.TripleFaulted, "Code limit won over a complete two-byte reserved opcode.");
            });
            test($"M4 Win95 reserved0FFF second opcode-byte page fault precedes UD model={model}", () =>
            {
                var (cpu, memory) = PagingTests.Create([0x90], model: model);
                memory.WriteByte(0x2fff, 0x0f); PagingTests.Pte(memory, 0x3000, 0x3000, 0); cpu.Eip = 0x2fff;
                ProtectedModeTests.Fault(cpu, memory, 14, 0, 0x2fff);
                Check(cpu.Cr2 == 0x3000, "Missing second opcode byte did not report its actual address.");
            });
            test($"M4 Win95 reserved0FFF ring-three frame uses actual ring-zero TSS stack model={model}", () =>
            {
                var (cpu, memory) = ProtectedModeTests.Rig([0x0f, 0xff, 0x55, 0x8b, 0xec], model: model);
                PagingTests.Tss(cpu, memory); PagingTests.User(cpu, memory);
                Protected(cpu, memory, 0x2000, true);
            });
            test($"M4 Win95 reserved0FFF fix retains explicit unsupported diagnostic for unclaimed neighboring encoding model={model}", () =>
            {
                var (cpu, _) = ProtectedModeTests.Rig([0x0f, 0xfe], model: model);
                try { cpu.Step(); } catch (UnsupportedInstructionException) { return; }
                throw new Exception("The reserved0FFF fix silently broadened neighboring opcode support.");
            });
        }
    }
}
