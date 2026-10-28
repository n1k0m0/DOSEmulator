// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Own P54C LAR compatibility policy for architecturally undefined bits, including the observed original Windows rights-round-trip form.</summary>
internal static class Windows95LarCompatibilityTests
{
    private const X86CpuModel P54c = X86CpuModel.IntelP54cB5Subset;

    /// <summary>Requires a concrete architectural, memory or mapped-device result; undefined bits name a DOSE policy only.</summary>
    private static void Check(bool value, string why = "P54C LAR compatibility invariant failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Writes explicit descriptor fields into the own GDT or LDT, independent from the CPU descriptor decoder.</summary>
    private static byte[] Descriptor(MemoryBus memory, uint address, uint basis, uint rawLimit, byte access, byte flags)
    {
        byte[] bytes = [(byte)rawLimit, (byte)(rawLimit >> 8), (byte)basis, (byte)(basis >> 8),
            (byte)(basis >> 16), access, (byte)((rawLimit >> 16) | (uint)flags << 4), (byte)(basis >> 24)];
        bytes.CopyTo(memory.Ram, checked((int)address)); return bytes;
    }

    /// <summary>Computes defined access rights from their logical input fields, without masking or querying raw descriptor bytes.</summary>
    private static uint Rights(byte access, bool available, bool wide, bool granular) =>
        (uint)access * 256 + (available ? 0x100000u : 0) + (wide ? 0x400000u : 0) + (granular ? 0x800000u : 0);

    /// <summary>Encodes the actual register form LAR/LSL (E)CX,BX in the independently selected code/operand sizes.</summary>
    private static byte[] Query(bool code32, bool destination32, bool limit = false)
    {
        byte[] instruction = [0x0f, (byte)(limit ? 3 : 2), 0xcb];
        return code32 == destination32 ? instruction : [0x66, .. instruction];
    }

    /// <summary>Enters a real own protected-mode fixture through guest LGDT/CR0/far-jump instructions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Rig(byte[] code, bool code32 = true, X86CpuModel model = P54c) =>
        ProtectedModeTests.Rig(code, code32: code32, model: model);

    /// <summary>Executes exactly one real query and checks unchanged source/full GPRs, ZF and descriptor storage.</summary>
    private static void Successful(X86Cpu cpu, MemoryBus memory, byte[] instruction, ushort selector,
        uint address, uint expected, bool destination32 = true)
    {
        cpu.SetRegister(Register.Bx, 0xa6b70000u | selector); cpu.SetRegister(Register.Cx, 0xdeadbeef);
        cpu.Eflags = 0x897;
        uint[] before = Enum.GetValues<Register>().Select(cpu.GetRegister).ToArray();
        byte[] descriptor = memory.Ram.AsSpan((int)address, 8).ToArray();
        cpu.Step();
        Check(cpu.Eip == 0x2000 + instruction.Length && !cpu.TripleFaulted && cpu.Eflags == 0x8d7,
            "Successful LAR/LSL changed a flag other than ZF or did not return from the actual instruction.");
        Check(cpu.GetRegister(Register.Cx) == (destination32 ? expected : 0xdead0000u | expected & 0xffff),
            "Actual query result differs from the logical expected rights/limit.");
        for (int i = 0; i < before.Length; i++)
            if (i != (int)Register.Cx) Check(cpu.GetRegister((Register)i) == before[i], "Query changed unrelated GPR " + i);
        Check(descriptor.AsSpan().SequenceEqual(memory.Ram.AsSpan((int)address, 8)),
            "Descriptor query modified table storage or marked its accessed bit.");
    }

    /// <summary>Installs an actual LDT through LLDT; descriptor queries still use the ordinary GDT/LDT selector rules.</summary>
    private static void LocalTable(X86Cpu cpu, MemoryBus memory)
    {
        Descriptor(memory, 0x448, 0xa000, 0x1f, 0x82, 0);
        cpu.SetRegister(Register.Ax, 0x48); PagingTests.At(cpu, memory, [0x0f, 0x00, 0xd0]);
        Check(cpu.Ldtr == 0x48 && cpu.LdtrCache.Base == 0xa000); cpu.Eip = 0x2000;
    }

    /// <summary>Own side-effect memory permits independent byte-read/write counts at selector and copy boundaries.</summary>
    private sealed class Device : IMemoryDevice
    {
        /// <summary>Explicit bytes of the own fake physical endpoint, independent from backing RAM.</summary>
        public readonly byte[] Bytes;
        /// <summary>Actual endpoint byte reads, excluding CPU preflight and ordinary RAM/table reads.</summary>
        public int Reads { get; private set; }
        /// <summary>Actual endpoint byte writes.</summary>
        public int Writes { get; private set; }

        /// <summary>Initializes a fixed independent byte pattern, or a caller-specified selector source.</summary>
        public Device(int size)
        {
            Bytes = new byte[size];
            for (int i = 0; i < size; i++) Bytes[i] = (byte)(0x5b ^ i ^ (i >> 3));
        }

        /// <summary>Reads one real mapped byte and records only an actually consumed endpoint value.</summary>
        public byte ReadByte(uint offset) { Reads++; return Bytes[checked((int)offset)]; }
        /// <summary>Records an unexpected or expected actual mapped-byte write.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; Bytes[checked((int)offset)] = value; }
    }

    /// <summary>Requires a genuine ring-zero or ring-three page-fault frame and an unchanged destination/flags input.</summary>
    private static void PageFault(X86Cpu cpu, MemoryBus memory, bool user, uint address, uint error)
    {
        if (!user)
        {
            uint frame = ProtectedModeTests.Fault(cpu, memory, 14, error);
            Check(memory.ReadDword(frame + 12) == 0x108d7, "Fault lost the pre-query ZF/flags input.");
        }
        else
        {
            byte? vector = null;
            cpu.InterruptEntered += value => vector = value;
            cpu.Step(); uint frame = cpu.GetRegister(Register.Sp);
            Check(vector == 14 && cpu.CurrentPrivilegeLevel == 0 && cpu.Eip == 0x6000 && frame == 0x19fe8 &&
                !cpu.TripleFaulted, "User query did not enter the real supervisor page-fault handler.");
            Check(memory.ReadDword(frame) == error && memory.ReadDword(frame + 4) == 0x2000 &&
                memory.ReadDword(frame + 8) == 0x2b && memory.ReadDword(frame + 12) == 0x108d7 &&
                memory.ReadDword(frame + 16) == 0x1b000 && memory.ReadDword(frame + 20) == 0x33,
                "Actual user page-fault error/restart/outer-stack frame differs.");
        }
        Check(cpu.Cr2 == address && cpu.GetRegister(Register.Cx) == 0xdeadbeef,
            "A faulting query changed its destination or reported the wrong linear fault address.");
    }

    /// <summary>Recreates the original 16-bit-code LAR ECX/BX, SHR ECX8, BTS CX12, MOV[BX+5]/CX instruction form.</summary>
    private static void RoundTrip(X86Cpu cpu, MemoryBus memory, ushort selector)
    {
        byte[] code = [0x66, 0x0f, 0x02, 0xcb, 0x66, 0xc1, 0xe9, 8, 0x0f, 0xba, 0xe9, 12, 0x89, 0x4f, 5];
        code.CopyTo(memory.Ram, 0x2000); cpu.Eip = 0x2000; cpu.SetRegister(Register.Bx, selector);
        for (int i = 0; i < 4; i++) cpu.Step();
        Check(cpu.Eip == 0x200f && !cpu.TripleFaulted, "The actual original-form rights round trip faulted.");
    }

    /// <summary>Executes word transfers with an actual address-size override and rejects a fault instead of treating handler entry as success.</summary>
    private static void HugeCopy(X86Cpu cpu, MemoryBus memory, uint source, uint destination, int words)
    {
        new byte[] { 0x67, 0xf3, 0xa5 }.CopyTo(memory.Ram, 0x2400); cpu.Eip = 0x2400;
        cpu.Eflags = 2; cpu.SetRegister(Register.Si, source); cpu.SetRegister(Register.Di, destination); cpu.SetRegister(Register.Cx, (uint)words);
        byte? fault = null; cpu.InterruptEntered += vector => fault = vector;
        for (int i = 0; i < words && fault is null; i++) cpu.Step();
        Check(fault is null && cpu.Eip == 0x2403 && cpu.GetRegister(Register.Cx) == 0 &&
            cpu.GetRegister(Register.Si) == source + words * 2 && cpu.GetRegister(Register.Di) == destination + words * 2,
            "Real huge-alias REP MOVSW faulted or lost continuation registers: vector=" + fault + " at " + cpu.Position);
    }

    /// <summary>Registers bounded raw/expanded-limit, model, privilege, mapped-memory and concrete original-sequence checks.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // The Intel-defined mask excludes bits19:16. Only their deterministic P54C answer below is an own compatibility policy.
        for (int nibble = 0; nibble < 16; nibble++)
        foreach (bool granular in new[] { false, true })
        foreach (bool code32 in new[] { false, true })
        {
            int rawNibble = nibble;
            test($"M4 Win95 P54C LAR raw nibble {rawNibble:X} G={granular} code32={code32}", () =>
            {
                byte[] instruction = Query(code32, true); var (cpu, memory) = Rig(instruction, code32);
                Descriptor(memory, 0x450, 0xa7b4c5d6, (uint)rawNibble * 65536 + 0x2468, 0xf2, granular ? (byte)13 : (byte)5);
                uint expected = Rights(0xf2, true, true, granular) + (uint)rawNibble * 65536;
                Successful(cpu, memory, instruction, 0x50, 0x450, expected);
            });
        }

        foreach (bool granular in new[] { false, true })
        foreach (bool code32 in new[] { false, true })
            test($"M4 Win95 LAR unchanged486 zero-nibble policy G={granular} code32={code32}", () =>
            {
                byte[] instruction = Query(code32, true); var (cpu, memory) = Rig(instruction, code32, X86CpuModel.Intel486Dx);
                Descriptor(memory, 0x450, 0xa7b4c5d6, 0xf2468, 0xf2, granular ? (byte)13 : (byte)5);
                Successful(cpu, memory, instruction, 0x50, 0x450, Rights(0xf2, true, true, granular));
            });

        foreach (X86CpuModel model in new[] { P54c, X86CpuModel.Intel486Dx })
        foreach (bool code32 in new[] { false, true })
            test($"M4 Win95 LAR16 preserves destination upper half model={model} code32={code32}", () =>
            {
                byte[] instruction = Query(code32, false); var (cpu, memory) = Rig(instruction, code32, model);
                Descriptor(memory, 0x450, 0x8157d28c, 0xf2468, 0xf3, 13);
                Successful(cpu, memory, instruction, 0x50, 0x450, 0xf300, false);
            });

        foreach (X86CpuModel model in new[] { P54c, X86CpuModel.Intel486Dx })
        foreach (bool granular in new[] { false, true })
        foreach (bool code32 in new[] { false, true })
            test($"M4 Win95 LSL keeps expanded limit model={model} G={granular} code32={code32}", () =>
            {
                byte[] instruction = Query(code32, true, true); var (cpu, memory) = Rig(instruction, code32, model);
                Descriptor(memory, 0x450, 0x8157d28c, 0xb2468, 0x73, granular ? (byte)13 : (byte)5);
                Successful(cpu, memory, instruction, 0x50, 0x450, granular ? 0xb2468fffu : 0xb2468);
            });

        foreach (bool user in new[] { false, true })
        foreach (bool local in new[] { false, true })
        foreach (byte access in new byte[] { 0xf3, 0x73, 0x93, 0x9f })
            test($"M4 Win95 P54C LAR rights visibility user={user} LDT={local} access={access:X2}", () =>
            {
                byte[] instruction = Query(true, true); var (cpu, memory) = Rig(instruction);
                if (local) LocalTable(cpu, memory);
                uint address = local ? 0xa008u : 0x450u;
                Descriptor(memory, address, 0x8157d28c, 0xa2468, access, 5);
                if (user) { PagingTests.Tss(cpu, memory); PagingTests.User(cpu, memory, true); }
                ushort selector = (ushort)((local ? 0x0c : 0x50) | (user ? 3 : 0));
                if (!user || access != 0x93)
                    Successful(cpu, memory, instruction, selector, address, Rights(access, true, true, false) + 0xa0000);
                else
                {
                    cpu.SetRegister(Register.Bx, selector); cpu.SetRegister(Register.Cx, 0xdeadbeef); cpu.Eflags = 0x8d7; cpu.Step();
                    Check(cpu.Eip == 0x2003 && cpu.GetRegister(Register.Cx) == 0xdeadbeef && cpu.Eflags == 0x897,
                        "An inaccessible DPL0 selector did not retain the failed-query contract.");
                }
            });

        foreach (bool destination32 in new[] { false, true })
        foreach (string reason in new[] { "null", "null-RPL3", "GDT-end", "no-LDT", "reserved-type", "RPL3-DPL0" })
            test($"M4 Win95 P54C failed LAR preserves full destination size32={destination32} {reason}", () =>
            {
                byte[] instruction = Query(true, destination32); var (cpu, memory) = Rig(instruction);
                Descriptor(memory, 0x450, 0x8157d28c, 0xa2468, reason == "reserved-type" ? (byte)0x80 : (byte)0x93, 5);
                ushort selector = reason switch { "null" => 0, "null-RPL3" => 3, "GDT-end" => 0x100,
                    "no-LDT" => 4, "RPL3-DPL0" => 0x53, _ => 0x50 };
                cpu.SetRegister(Register.Bx, selector); cpu.SetRegister(Register.Cx, 0xdeadbeef); cpu.Eflags = 0x8d7;
                byte[] before = memory.Ram.AsSpan(0x450, 8).ToArray(); cpu.Step();
                Check(cpu.Eip == 0x2000 + instruction.Length && cpu.GetRegister(Register.Cx) == 0xdeadbeef && cpu.Eflags == 0x897 &&
                    before.AsSpan().SequenceEqual(memory.Ram.AsSpan(0x450, 8)), "Rejected query changed destination, flags or descriptor.");
            });

        test("M4 Win95 P54C LAR successful memory selector consumes only two MMIO bytes", () =>
        {
            byte[] instruction = [0x0f, 0x02, 0x0d, 0, 0, 3, 0]; var (cpu, memory) = Rig(instruction);
            Descriptor(memory, 0x450, 0x8157d28c, 0xa2468, 0xf2, 13);
            var device = new Device(4096); device.Bytes[0] = 0x50; device.Bytes[1] = 0;
            memory.Map(0x30000, 4096, device, "Own LAR selector endpoint");
            Successful(cpu, memory, instruction, 0x50, 0x450, Rights(0xf2, true, true, true) + 0xa0000);
            Check(device.Reads == 2 && device.Writes == 0, "LAR introduced an extra source read or endpoint write.");
        });
        test("M4 Win95 P54C LAR cross-page selector fault precedes first MMIO read", () =>
        {
            var (cpu, memory) = PagingTests.Create([0x0f, 0x02, 0x0d, 0xff, 0x0f, 3, 0], model: P54c);
            var device = new Device(4096); memory.Map(0x40000, 4096, device, "Own selector preflight");
            PagingTests.Pte(memory, 0x30000, 0x40000); PagingTests.Pte(memory, 0x31000, 0, 0);
            cpu.SetRegister(Register.Cx, 0xdeadbeef); cpu.Eflags = 0x8d7; PageFault(cpu, memory, false, 0x31000, 0);
            Check(device.Reads == 0 && device.Writes == 0);
        });
        foreach (bool user in new[] { false, true })
            test("M4 Win95 P54C LAR supervisor descriptor preflight protects MMIO user=" + user, () =>
            {
                var (cpu, memory) = PagingTests.Create([0x0f, 0x02, 0xcb], model: P54c);
                if (user)
                {
                    PagingTests.Tss(cpu, memory);
                    // User uses fixed bootstrap GDT addresses, so populate both entries before copying the actual relocated table.
                    Descriptor(memory, 0x428, 0, 0xfffff, 0xfb, 12); Descriptor(memory, 0x430, 0, 0xfffff, 0xf3, 12);
                }
                memory.Ram.AsSpan(0x400, 256).CopyTo(memory.Ram.AsSpan(0x30004));
                memory.WriteWord(0x350, 0x2000); memory.WriteDword(0x352, 0x30004);
                PagingTests.At(cpu, memory, [0x0f, 0x01, 0x15, 0x50, 3, 0, 0]);
                var device = new Device(4); memory.Map(0x30ffc, 4, device, "Own descriptor preflight");
                PagingTests.Pte(memory, 0x30000, 0x30000, 3); PagingTests.Pte(memory, 0x31000, 0, 0);
                if (user) PagingTests.User(cpu, memory, true);
                cpu.Eip = 0x2000; cpu.SetRegister(Register.Bx, 0xff8); cpu.SetRegister(Register.Cx, 0xdeadbeef); cpu.Eflags = 0x8d7;
                PageFault(cpu, memory, user, 0x31000, 0);
                Check(device.Reads == 0 && device.Writes == 0, "Descriptor preflight consumed first-page MMIO before the later PF.");
            });
        test("M4 Win95 P54C LAR ring3 descriptor lookup uses supervisor paging", () =>
        {
            var (cpu, memory) = PagingTests.Create(Query(true, true), model: P54c);
            Descriptor(memory, 0x450, 0x8157d28c, 0xa2468, 0xf2, 5);
            PagingTests.Tss(cpu, memory); PagingTests.Pte(memory, 0, 0, 3);
            // Remove the earlier supervisor bootstrap translation before testing a genuinely supervisor-only GDT page.
            PagingTests.At(cpu, memory, [0x0f, 0x01, 0x3d, 0, 0, 0, 0]); PagingTests.User(cpu, memory, true);
            Successful(cpu, memory, Query(true, true), 0x53, 0x450, Rights(0xf2, true, true, false) + 0xa0000);
        });
        test("M4 Win95 P54C LAR ring3 operand still obeys user paging before MMIO", () =>
        {
            var (cpu, memory) = PagingTests.Create([0x0f, 0x02, 0x0d, 0, 0, 3, 0], model: P54c);
            var device = new Device(4096); memory.Map(0x40000, 4096, device, "Own user selector protection");
            PagingTests.Tss(cpu, memory); PagingTests.Pte(memory, 0x30000, 0x40000, 3);
            PagingTests.User(cpu, memory, true); cpu.SetSegment(Segment.Ds, 0x33);
            cpu.SetRegister(Register.Cx, 0xdeadbeef); cpu.Eflags = 0x8d7; PageFault(cpu, memory, true, 0x30000, 5);
            Check(device.Reads == 0 && device.Writes == 0);
        });

        test("M4 Win95 original-form LAR rights round trip retains first147DF second47DF and actual huge copy", () =>
        {
            var (cpu, memory) = Rig([0x90], false);
            Descriptor(memory, 0x450, 0x50000, 0x147df, 0xf3, 0);
            Descriptor(memory, 0x458, 0x60000, 0x47df, 0xf3, 0);
            Descriptor(memory, 0x460, 0x400, 0xfff, 0x93, 0);
            cpu.SetSegment(Segment.Es, 0x50); var originalCache = cpu.GetSegmentCache(Segment.Es);
            cpu.SetSegment(Segment.Ds, 0x60); RoundTrip(cpu, memory, 0x50); RoundTrip(cpu, memory, 0x58);
            Check(cpu.GetSegmentCache(Segment.Es) == originalCache, "Descriptor stores silently changed an already loaded hidden cache.");
            byte[] source = Enumerable.Range(0, 256).Select(i => (byte)(0x31 + i * 17)).ToArray();
            source.CopyTo(memory.Ram, 0x30000); memory.Ram.AsSpan(0x5ffe8, 70).Fill(0xa7);
            cpu.SetSegment(Segment.Ds, 16); cpu.SetSegment(Segment.Es, 0x50);
            HugeCopy(cpu, memory, 0x30000, 0xfff0, 27);
            Check(memory.Ram.AsSpan(0x5fff0, 54).SequenceEqual(source.AsSpan(0, 54)) &&
                memory.Ram.AsSpan(0x5ffe8, 8).IndexOfAnyExcept((byte)0xa7) < 0 &&
                memory.Ram.AsSpan(0x60026, 8).IndexOfAnyExcept((byte)0xa7) < 0,
                "The actual copy did not cross64KiB inside the retained first alias with exact guards.");
            Check(cpu.GetSegmentCache(Segment.Es).Limit == 0x147df &&
                memory.ReadWord(0x450) == 0x47df && memory.ReadByte(0x456) == 0x11 &&
                memory.ReadWord(0x458) == 0x47df && memory.ReadByte(0x45e) == 0x10);
            memory.Ram.AsSpan(0x60038, 32).Fill(0xa7); cpu.SetSegment(Segment.Es, 0x58);
            HugeCopy(cpu, memory, 0x30040, 0x40, 8);
            Check(cpu.GetSegmentCache(Segment.Es).Limit == 0x47df &&
                memory.Ram.AsSpan(0x60040, 16).SequenceEqual(source.AsSpan(64, 16)) &&
                memory.Ram.AsSpan(0x60038, 8).IndexOfAnyExcept((byte)0xa7) < 0 &&
                memory.Ram.AsSpan(0x60050, 8).IndexOfAnyExcept((byte)0xa7) < 0 &&
                source.AsSpan().SequenceEqual(memory.Ram.AsSpan(0x30000, 256)));
        });

        foreach (bool lastValid in new[] { false, true })
            test("M4 Win95 LAR policy preserves actual D16 REP segment fault and prior15words lastValid=" + lastValid, () =>
            {
                var (cpu, memory) = Rig([0xf3, 0xa5], false);
                Descriptor(memory, 0x458, 0x50000, 0x47df, 0xf3, 0);
                Descriptor(memory, 0x460, 0xa0000, 0xffff, 0xf3, 0);
                var device = new Device(65536); memory.Map(0xa0000, 65536, device, "Own MMIO copy source");
                cpu.SetSegment(Segment.Ds, 0x60); cpu.SetSegment(Segment.Es, 0x58); cpu.Eflags = 2;
                memory.Ram.AsSpan(0x547b0, 80).Fill(0xa7);
                cpu.SetRegister(Register.Si, 0x000230d4); cpu.SetRegister(Register.Di, lastValid ? 0x000247deu : 0x000247c1u);
                cpu.SetRegister(Register.Cx, lastValid ? 0x20560001u : 0x2056001b);
                int words = lastValid ? 1 : 15;
                for (int i = 0; i < words; i++) cpu.Step();
                if (!lastValid) ProtectedModeTests.Fault(cpu, memory, 13, 0);
                Check(device.Reads == words * 2 && device.Writes == 0 &&
                    memory.Ram.AsSpan(lastValid ? 0x547de : 0x547c1, words * 2).SequenceEqual(device.Bytes.AsSpan(0x30d4, words * 2)) &&
                    memory.Ram.AsSpan(lastValid ? 0x547e0 : 0x547df, 16).IndexOfAnyExcept((byte)0xa7) < 0,
                    "A segment fault consumed forbidden MMIO or wrote beyond the last legal complete word.");
                Check(cpu.GetRegister(Register.Si) == (lastValid ? 0x000230d6u : 0x000230f2u) &&
                    cpu.GetRegister(Register.Di) == (lastValid ? 0x000247e0u : 0x000247dfu) &&
                    cpu.GetRegister(Register.Cx) == (lastValid ? 0x20560000u : 0x2056000cu) &&
                    cpu.GetSegmentCache(Segment.Es).Limit == 0x47df &&
                    (!lastValid || cpu.Eip == 0x2002), "REP lost its a16 upper halves, precise boundary or continuation.");
            });
    }
}
