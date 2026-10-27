// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Historical P54C DE aliases, port comparators and committed I/O/debug restart boundaries.</summary>
internal static class DebugExtensionTests
{
    private const X86CpuModel Model = X86CpuModel.IntelP54cB5Subset;
    private static readonly Dictionary<object, string> NoOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, IStateObject> NoObjects = new();

    private sealed class Port : IIoPortDevice
    {
        public readonly List<(ushort Address, int Width, bool Write, uint Value)> Calls = [];
        /// <summary>Records the single actual device read independently of CPU debug bookkeeping.</summary>
        public uint Read(ushort port, int width)
        { Calls.Add((port, width, false, 0x89abcdef)); return 0x89abcdef; }
        /// <summary>Records the already width-masked device write independently of CPU debug bookkeeping.</summary>
        public void Write(ushort port, uint value, int width) => Calls.Add((port, width, true, value));
    }

    /// <summary>Requires an independently observable guest frame, operand or device effect.</summary>
    private static void Check(bool value, string message = "P54C debug-extension contract failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Serializes selected explicit CPU fields without invoking host callbacks.</summary>
    private static string State(X86Cpu cpu) => JsonSerializer.Serialize(cpu.SaveState(), StateValues.Json);
    /// <summary>Creates a clock-bound P54C and enters the requested actual guest execution mode.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, DeviceScheduler Clock, Port Device) Rig(byte[] code, int mode = 2)
    {
        var clock = new DeviceScheduler(); var bus = new IoPortBus(); var port = new Port(); bus.Map(0, 65536, port);
        if (mode == 3)
        {
            var (c, m) = PagingTests.Create(code, io: bus, model: Model, timestampSource: clock, timestampFrequency: 90_000_000);
            return (c, m, clock, port);
        }
        if (mode != 0)
        {
            var (c, m) = ProtectedModeTests.Rig(code, code32: mode == 2, io: bus, model: Model, timestampSource: clock, timestampFrequency: 90_000_000);
            return (c, m, clock, port);
        }
        var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, bus, Model, clock, 90_000_000);
        cpu.SetSegment(Segment.Cs, 0); cpu.SetRegister(Register.Sp, 0x9000); cpu.Eip = 0x2000; code.CopyTo(memory.Ram, 0x2000);
        return (cpu, memory, clock, port);
    }
    /// <summary>Writes CR4 using the guest instruction and preserves the pending test address.</summary>
    private static void Cr4(X86Cpu cpu, MemoryBus memory, uint value)
    {
        uint ip = cpu.Eip; cpu.SetRegister(Register.Ax, value); PagingTests.At(cpu, memory, [0x0f, 0x22, 0xe0], 0x2500); cpu.Eip = ip;
    }
    /// <summary>Writes one debug register through the actual always-32-bit MOV DR form.</summary>
    private static void Dr(X86Cpu cpu, MemoryBus memory, int register, uint value)
    {
        uint ip = cpu.Eip; cpu.SetRegister(Register.Ax, value);
        PagingTests.At(cpu, memory, [0x0f, 0x23, (byte)(0xc0 | register << 3)], 0x2400); cpu.Eip = ip;
    }
    /// <summary>Arms one explicit address comparator with the selected local/global enable, kind and length.</summary>
    private static void Arm(X86Cpu cpu, MemoryBus memory, uint address, int length = 1, int slot = 0, bool global = false, int kind = 2)
    {
        Dr(cpu, memory, slot, address); uint len = length == 4 ? 3u : length == 2 ? 1u : length == 1 ? 0u : 2u;
        Dr(cpu, memory, 7, 1u << (slot * 2 + (global ? 1 : 0)) | ((uint)kind | len << 2) << (16 + slot * 4));
    }
    /// <summary>Encodes a scalar byte/word/dword port instruction for the fixture's default code size.</summary>
    private static byte[] Scalar(bool write, int width, bool immediate = false, bool code32 = true)
    {
        byte opcode = (byte)((immediate ? 0xe4 : 0xec) + (write ? 2 : 0) + (width == 1 ? 0 : 1));
        byte[] prefix = width != 1 && (width == 4) != code32 ? [0x66] : [];
        return immediate ? [.. prefix, opcode, 0x80] : [.. prefix, opcode];
    }
    /// <summary>Encodes actual port string instructions without introducing synthetic memory transfers.</summary>
    private static byte[] StringIo(bool write, int width, bool repeat = false, bool address16 = false)
    {
        byte opcode = (byte)((write ? 0x6e : 0x6c) + (width == 1 ? 0 : 1));
        return [.. (repeat ? new byte[] { 0xf3 } : []), .. (width == 2 ? new byte[] { 0x66 } : []), .. (address16 ? new byte[] { 0x67 } : []), opcode];
    }
    /// <summary>Checks the actual protected-mode debug trap frame after a completed instruction.</summary>
    private static uint Trap(X86Cpu cpu, MemoryBus memory, uint ip, uint status = 1)
    {
        byte? entered = null; cpu.InterruptEntered += v => entered = v; cpu.Step(); uint sp = cpu.GetRegister(Register.Sp);
        Check(entered == 1 && cpu.Eip == 0x6000 && !cpu.TripleFaulted, $"Expected #DB, got {entered} at {cpu.Position}");
        Check(memory.ReadDword(sp) == ip, $"Saved IP {memory.ReadDword(sp):X}, expected {ip:X}");
        Check((cpu.Dr6 & 0x400f) == status && (cpu.Eflags & X86Cpu.Trap) == 0);
        return sp;
    }
    /// <summary>Checks a delivered protected fault independently of any pre-fault stack-switch width.</summary>
    private static uint Error(X86Cpu cpu, MemoryBus memory, byte vector, uint ip = 0x2000, uint? error = null)
    {
        byte? entered = null; cpu.InterruptEntered += v => entered = v; cpu.Step(); uint sp = cpu.GetRegister(Register.Sp);
        Check(entered == vector && cpu.Eip == 0x6000 && !cpu.TripleFaulted, $"Expected fault {vector}, got {entered} at {cpu.Position}");
        if (error.HasValue) { Check(memory.ReadDword(sp) == error.Value); sp += 4; }
        Check(memory.ReadDword(sp) == ip && (memory.ReadDword(sp + 8) & 0x10000) != 0);
        return sp;
    }
    /// <summary>Returns through the actual IRETD instruction with optional explicit stacked-flag repair.</summary>
    private static void Return(X86Cpu cpu, MemoryBus memory, uint? flags = null)
    {
        uint sp = cpu.GetRegister(Register.Sp); if (flags.HasValue) memory.WriteDword(sp + 8, flags.Value);
        memory.WriteByte(0x6000, 0xcf); cpu.Step();
    }
    /// <summary>Enters the requested lower ring through a private TSS and an actual IRETD frame.</summary>
    private static void User(X86Cpu cpu, MemoryBus memory, int ring = 3, uint flags = 0x202)
    {
        PagingTests.Tss(cpu, memory); memory.WriteWord(0x9066, 0x68);
        Descriptor(memory, 0x28, 0, 0xfffff, (byte)(0x9a | ring << 5), 12);
        Descriptor(memory, 0x30, 0, 0xfffff, (byte)(0x92 | ring << 5), 12);
        uint[] frame = [0x2000, (uint)(0x28 | ring), flags, 0x1b000, (uint)(0x30 | ring)];
        for (uint i = 0; i < frame.Length; i++) memory.WriteDword(0x18000 + i * 4, frame[i]);
        PagingTests.At(cpu, memory, [0xcf], 0x2100);
    }
    /// <summary>Enters V86 through a real IRETD extended frame and installs the supplied 16-bit instruction.</summary>
    private static void V86(X86Cpu cpu, MemoryBus memory, byte[] code, uint flags = 0x23202)
    {
        PagingTests.Tss(cpu, memory);
        Descriptor(memory, 0x48, 0x9000, 0x2068, 0x89); memory.WriteWord(0x9066, 0x68); memory.Ram.AsSpan(0x9068, 8193).Fill(0xff);
        memory.WriteByte(0x9068 + 0x330 / 8, 0xfe); cpu.SetRegister(Register.Ax, 0x48); PagingTests.At(cpu, memory, [0x0f, 0x00, 0xd8], 0x2600);
        uint[] frame = [0x100, 0x3000, flags, 0x8000, 0x4000, 0, 0, 0, 0];
        for (uint i = 0; i < frame.Length; i++) memory.WriteDword(0x18000 + i * 4, frame[i]);
        code.CopyTo(memory.Ram, 0x30100); PagingTests.At(cpu, memory, [0xcf], 0x2100);
    }

    /// <summary>Registers CPU, I/O, privilege, fault, REP and raw-state debug-extension cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (uint value in new uint[] { 0, 4, 8, 12 })
            test($"M4 DE CR4 coexistence mode {mode} bits {value:X}", () =>
            {
                var (c, m, clock, _) = Rig([0x0f, 0x20, 0xe1], mode); clock.AdvanceCycles(137, 90_000_000);
                long invalidations = c.TlbInvalidations; Cr4(c, m, value); c.SetRegister(Register.Cx, uint.MaxValue); c.Eflags = 0x200cd7; c.Step();
                Check(c.Cr4 == value && c.GetRegister(Register.Cx) == value && c.Eflags == 0x200cd7);
                Check(c.TlbInvalidations == invalidations && c.TimestampCounter == 137);
            });
        foreach (int debug in new[] { 4, 5 })
        foreach (bool write in new[] { false, true })
        foreach (int mod in new[] { 0, 1, 2, 3 })
            test($"M4 DE clear DR{debug} alias write {write} Mod {mod}", () =>
            {
                byte mr = (byte)(mod << 6 | debug << 3 | 3);
                var (c, m, _, _) = Rig([0x66, 0x67, 0x0f, (byte)(write ? 0x23 : 0x21), mr]);
                uint value = debug == 4 ? 0xe00fu : 0x1234u; Dr(c, m, debug + 2, value); c.SetRegister(Register.Bx, write ? value : 0xdeadbeef); c.Step();
                Check(c.Eip == 0x2005 && c.Cr4 == 0);
                uint expected = debug == 4 ? 0xffffefffu : 0x634u;
                Check(write ? (debug == 4 ? c.Dr6 : c.Dr7) == expected : c.GetRegister(Register.Bx) == expected);
            });
        foreach (int debug in new[] { 4, 5 })
        foreach (bool write in new[] { false, true })
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
            test($"M4 DE set DR{debug} UD write {write} prefix {Convert.ToHexString(prefix)}", () =>
            {
                var (c, m, _, _) = Rig([.. prefix, 0x0f, (byte)(write ? 0x23 : 0x21), (byte)(0xc0 | debug << 3)]);
                Cr4(c, m, 8); c.SetRegister(Register.Ax, 0x89abcdef); Error(c, m, 6);
                Check(c.GetRegister(Register.Ax) == 0x89abcdef && c.Dr6 == 0xffff0ff0 && c.Dr7 == 0x400 && c.GeneralDetectFaults == 0);
            });
        foreach (int ring in new[] { 0, 1, 2, 3 })
        foreach (int debug in new[] { 4, 5 })
        foreach (bool write in new[] { false, true })
            test($"M4 DE invalid DR{debug} precedes GD and CPL {ring} write {write}", () =>
            {
                var (c, m, _, _) = Rig([0x0f, (byte)(write ? 0x23 : 0x21), (byte)(0xc0 | debug << 3)]);
                Cr4(c, m, 8); Dr(c, m, 7, 0x2000); if (ring != 0) User(c, m, ring); c.Eip = 0x2000;
                c.SetRegister(Register.Ax, 0x89abcdef); Error(c, m, 6);
                Check(c.Dr7 == 0x2400 && c.Dr6 == 0xffff0ff0 && c.GeneralDetectFaults == 0 && c.GetRegister(Register.Ax) == 0x89abcdef);
            });
        foreach (int debug in new[] { 4, 5 })
        foreach (bool write in new[] { false, true })
            test($"M4 DE invalid DR{debug} V86 UD before GP write {write}", () =>
            {
                var (c, m, _, _) = Rig([0x90], 3); Cr4(c, m, 8); Dr(c, m, 7, 0x2000);
                V86(c, m, [0x0f, (byte)(write ? 0x23 : 0x21), (byte)(0xc0 | debug << 3)]); c.SetRegister(Register.Ax, 0x76543210);
                Error(c, m, 6, 0x100); Check(c.Dr7 == 0x2400 && c.GeneralDetectFaults == 0 && c.GetRegister(Register.Ax) == 0x76543210);
            });
        foreach (bool clockBound in new[] { false, true })
            test($"M4 P54C DR7 reserved bit12 fixed zero clock {clockBound}", () =>
            {
                var clock = clockBound ? new DeviceScheduler() : null;
                var (c, m) = ProtectedModeTests.Rig([0x90], model: Model, timestampSource: clock, timestampFrequency: clockBound ? 90_000_000u : 0);
                Dr(c, m, 7, 0xffffdfff); Check(c.Dr7 == 0xffff07ff); Dr(c, m, 7, 0); Check(c.Dr7 == 0x400);
            });
        foreach (bool write in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
        foreach (bool immediate in new[] { false, true })
        foreach (int mode in new[] { 0, 1, 2, 3 })
            test($"M4 DE scalar actual I/O write {write} width {width} immediate {immediate} mode {mode}", () =>
            {
                byte[] code = Scalar(write, width, immediate, mode == 2 || mode == 3); var (c, m, _, port) = Rig(code, mode);
                Cr4(c, m, 8); Arm(c, m, immediate ? 0x80u : 0x330u, width); c.SetRegister(Register.Dx, 0xfeed0330); c.SetRegister(Register.Ax, 0x76543210);
                uint ip = 0x2000u + (uint)code.Length;
                if (mode == 0)
                {
                    m.WriteWord(4, 0x6000); m.WriteWord(6, 0); c.Step();
                    Check(c.Eip == 0x6000 && c.GetRegister(Register.Sp) == 0x8ffa && m.ReadWord(0x8ffa) == ip);
                }
                else Trap(c, m, ip);
                Check(port.Calls.Count == 1 && port.Calls[0].Address == (immediate ? 0x80 : 0x330) && port.Calls[0].Width == width && port.Calls[0].Write == write);
                uint mask = width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
                Check(write ? port.Calls[0].Value == (0x76543210 & mask) : c.GetRegister(Register.Ax) == ((0x76543210 & ~mask) | (0x89abcdef & mask)));
                Check(c.DataBreakpointTraps == 1 && (c.Dr6 & 15) == 1);
            });
        foreach (int slot in new[] { 0, 1, 2, 3 })
        foreach (bool global in new[] { false, true })
            test($"M4 DE I/O comparator slot {slot} global {global}", () =>
            {
                var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Arm(c, m, 0x330, slot: slot, global: global); c.SetRegister(Register.Dx, 0x330);
                Trap(c, m, 0x2001, 1u << slot); Check(port.Calls.Count == 1 && c.DataBreakpointTraps == 1);
            });
        foreach (int length in new[] { 1, 2, 4 })
        foreach (int delta in Enumerable.Range(-3, 10))
            test($"M4 DE I/O aligned LEN {length} offset {delta}", () =>
            {
                var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Arm(c, m, 0x330u + (uint)(length - 1), length); c.SetRegister(Register.Dx, (uint)(0x330 + delta));
                if (delta >= 0 && delta < length) Trap(c, m, 0x2001);
                else { c.Step(); Check(c.Eip == 0x2001 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0); }
                Check(port.Calls.Count == 1);
            });
        foreach (bool write in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
            test($"M4 DE I/O wide access overlaps last byte write {write} width {width}", () =>
            {
                byte[] code = Scalar(write, width); var (c, m, _, port) = Rig(code); Cr4(c, m, 8); Arm(c, m, 0x330u + (uint)width - 1); c.SetRegister(Register.Dx, 0x330);
                Trap(c, m, 0x2000u + (uint)code.Length); Check(port.Calls.Count == 1 && port.Calls[0].Width == width);
            });
        foreach (uint address in new uint[] { 0x10330, 0x80000330, 0xffff0330 })
            test($"M4 DE I/O zero extends port before full32 comparator {address:X8}", () =>
            {
                var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Arm(c, m, address); c.SetRegister(Register.Dx, 0xffff0330); c.Step();
                Check(c.Eip == 0x2001 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0 && port.Calls.Count == 1);
            });
        foreach (uint de in new uint[] { 0, 8 })
        foreach (int kind in new[] { 0, 1, 2, 3 })
            test($"M4 DE port operation versus RW {kind} DE {de}", () =>
            {
                var (c, m, _, port) = Rig([0xee]); Cr4(c, m, de); Arm(c, m, 0x330, kind: kind); c.SetRegister(Register.Dx, 0x330);
                if (de != 0 && kind == 2) Trap(c, m, 0x2001); else { c.Step(); Check(c.Eip == 0x2001 && c.DataBreakpointTraps == 0); }
                Check(port.Calls.Count == 1);
            });
        test("M4 DE unsupported LEN10 does not invent an eight-byte port comparison", () =>
        {
            var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Arm(c, m, 0x330, 8); c.SetRegister(Register.Dx, 0x330); c.Step();
            Check(c.Eip == 0x2001 && c.DataBreakpointTraps == 0 && port.Calls.Count == 1);
        });
        test("M4 DE clear comparator does not observe CPU memory writes as port operations", () =>
        {
            var (c, m, _, port) = Rig([0xa3, 0x30, 3, 0, 0]); Cr4(c, m, 8); Arm(c, m, 0x330); c.SetRegister(Register.Ax, 0x12345678); c.Step();
            Check(c.Eip == 0x2005 && m.ReadDword(0x330) == 0x12345678 && port.Calls.Count == 0 && c.DataBreakpointTraps == 0);
        });
        test("M4 DE direct external port bus operation is not an executing CPU access", () =>
        {
            var clock = new DeviceScheduler(); var bus = new IoPortBus(); var port = new Port(); bus.Map(0x330, 4, port);
            var (c, m) = ProtectedModeTests.Rig([0x90], io: bus, model: Model, timestampSource: clock, timestampFrequency: 90_000_000);
            Cr4(c, m, 8); Arm(c, m, 0x330); bus.Write(0x330, 0x5a, 1); c.Step();
            Check(c.Eip == 0x2001 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0 && port.Calls.Count == 1);
        });
        foreach (bool write in new[] { false, true })
        foreach (bool allow in new[] { false, true })
            test($"M4 DE CPL3 IOPL permission write {write} allowed {allow}", () =>
            {
                var (c, m, _, port) = Rig(Scalar(write, 1), 3); Cr4(c, m, 8); Arm(c, m, 0x330); User(c, m, flags: allow ? 0x3202u : 0x202);
                c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Ax, 0x76543210);
                if (allow) { uint sp = Trap(c, m, 0x2001); Check(m.ReadDword(sp + 12) == 0x1b000 && m.ReadDword(sp + 16) == 0x33 && port.Calls.Count == 1); }
                else { Error(c, m, 13, error: 0); Check(port.Calls.Count == 0 && c.GetRegister(Register.Ax) == 0x76543210 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0); }
            });
        foreach (bool write in new[] { false, true })
        foreach (bool allow in new[] { false, true })
            test($"M4 DE CPL3 bitmap full dword permission write {write} allowed {allow}", () =>
            {
                var (c, m, _, port) = Rig(Scalar(write, 4), 3); Cr4(c, m, 8); Arm(c, m, 0x333);
                User(c, m); c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Ax, 0x76543210);
                // Reload the same private TSS with a valid bitmap before returning to CPL3.
                // Its descriptor was cached by User, so establish the larger limit through a new LTR at CPL0.
                Gate(m, 0x60, access: 0xee); PagingTests.At(c, m, [0xcd, 0x60], 0x2300);
                Descriptor(m, 0x48, 0x9000, 0x2068, 0x89); m.WriteWord(0x9066, 0x68); m.Ram.AsSpan(0x9068, 8193).Fill(0xff);
                m.WriteByte(0x9068 + 0x330 / 8, (byte)(allow ? 0 : 8)); c.SetRegister(Register.Ax, 0x48); PagingTests.At(c, m, [0x0f, 0x00, 0xd8], 0x2600);
                uint sp = c.GetRegister(Register.Sp); m.WriteDword(sp, 0x2000); m.WriteDword(sp + 8, 0x202); c.Eip = 0x6000; Return(c, m); c.SetRegister(Register.Ax, 0x76543210);
                if (allow) { Trap(c, m, 0x2001); Check(port.Calls.Count == 1); }
                else { Error(c, m, 13, error: 0); Check(port.Calls.Count == 0 && c.DataBreakpointTraps == 0 && c.GetRegister(Register.Ax) == 0x76543210); }
            });
        foreach (bool write in new[] { false, true })
            test($"M4 DE V86 permitted bitmap port trap after actual operation write {write}", () =>
            {
                var (c, m, _, port) = Rig([0x90], 3); Cr4(c, m, 8); Arm(c, m, 0x330); V86(c, m, Scalar(write, 1)); c.SetRegister(Register.Dx, 0x330);
                uint sp = Trap(c, m, 0x101); Check(port.Calls.Count == 1 && (m.ReadDword(sp + 8) & 0x20000) != 0 && !c.Virtual8086);
            });
        foreach (bool write in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
        foreach (bool down in new[] { false, true })
            test($"M4 DE string committed transfer write {write} width {width} DF {down}", () =>
            {
                byte[] code = StringIo(write, width); var (c, m, _, port) = Rig(code); Cr4(c, m, 8); Arm(c, m, 0x330, width);
                c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Si, 0x30000); c.SetRegister(Register.Di, 0x31000); c.Eflags = down ? 0x402u : 2;
                m.WriteDword(0x30000, 0x76543210); Trap(c, m, 0x2000u + (uint)code.Length);
                Check(port.Calls.Count == 1 && port.Calls[0].Width == width && port.Calls[0].Write == write);
                uint next = (uint)((write ? 0x30000 : 0x31000) + (down ? -width : width)); Check(c.GetRegister(write ? Register.Si : Register.Di) == next);
                uint mask = width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
                Check(write ? port.Calls[0].Value == (0x76543210 & mask) : (m.ReadDword(0x31000) & mask) == (0x89abcdef & mask));
            });
        foreach (bool write in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
            test($"M4 DE REP string traps each committed element and resumes write {write} width {width}", () =>
            {
                byte[] code = StringIo(write, width, repeat: true); var (c, m, _, port) = Rig(code); Cr4(c, m, 8); Arm(c, m, 0x330, width);
                c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Cx, 2); c.SetRegister(Register.Si, 0x30000); c.SetRegister(Register.Di, 0x31000); m.WriteDword(0x30000, 0x76543210);
                uint sp = Trap(c, m, 0x2000); Check(c.GetRegister(Register.Cx) == 1 && port.Calls.Count == 1 && (m.ReadDword(sp + 8) & 0x10000) != 0);
                Check(c.TryRunRepBatch(1) == 0); Return(c, m); Trap(c, m, 0x2000u + (uint)code.Length);
                Check(c.GetRegister(Register.Cx) == 0 && port.Calls.Count == 2 && c.DataBreakpointTraps == 2);
            });
        foreach (bool write in new[] { false, true })
            test($"M4 DE REP zero count performs neither port access nor trap write {write}", () =>
            {
                var (c, m, _, port) = Rig(StringIo(write, 4, repeat: true)); Cr4(c, m, 8); Arm(c, m, 0x330, 4); c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Cx, 0); c.Step();
                Check(c.Eip == 0x2002 && port.Calls.Count == 0 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0);
            });
        foreach (bool write in new[] { false, true })
            test($"M4 DE string page fault precedes port effect and debug trap write {write}", () =>
            {
                var (c, m, _, port) = Rig(StringIo(write, 4, repeat: true), 3); Cr4(c, m, 8); Arm(c, m, 0x330, 4);
                c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Cx, 2); c.SetRegister(Register.Si, 0x30000); c.SetRegister(Register.Di, 0x31000);
                PagingTests.Pte(m, write ? 0x30000u : 0x31000u, 0, 0); Error(c, m, 14, error: write ? 0u : 2u);
                Check(port.Calls.Count == 0 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0 && c.GetRegister(Register.Cx) == 2);
                Check(c.GetRegister(Register.Si) == 0x30000 && c.GetRegister(Register.Di) == 0x31000);
            });
        test("M4 DE RF does not suppress committed I/O trap and TF combines with its comparator", () =>
        {
            var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Arm(c, m, 0x330); c.SetRegister(Register.Dx, 0x330); c.Eflags = 0x10102;
            Trap(c, m, 0x2001, 0x4001); Check(c.SingleStepTraps == 1 && c.DataBreakpointTraps == 1 && port.Calls.Count == 1);
        });
        test("M4 DE disabled equal I/O slot joins enabled slot status", () =>
        {
            var (c, m, _, port) = Rig([0xec]); Cr4(c, m, 8); Dr(c, m, 0, 0x330); Dr(c, m, 2, 0x330); Dr(c, m, 7, 0x02020001); c.SetRegister(Register.Dx, 0x330);
            Trap(c, m, 0x2001, 5); Check(c.DataBreakpointTraps == 1 && port.Calls.Count == 1);
        });
        test("M4 DE MOV SS boundary uses the installed stack for following I/O trap", () =>
        {
            var (c, m, _, port) = Rig([0x8e, 0xd0, 0xec]); Cr4(c, m, 8); Arm(c, m, 0x330); c.SetRegister(Register.Ax, 16); c.SetRegister(Register.Dx, 0x330);
            c.Step(); Check(c.Eip == 0x2002 && c.DataBreakpointTraps == 0); Trap(c, m, 0x2003); Check(port.Calls.Count == 1 && c.GetRegister(Register.Sp) == 0x17ff4);
        });
        test("M4 DE current comparator decision and pending SS deferral survive raw CPU continuation", () =>
        {
            byte[] code = [0x17, 0xec]; var (a, ma, ca, pa) = Rig(code); Cr4(a, ma, 12); Dr(a, ma, 0, 0x330); Dr(a, ma, 1, 0x18000); Dr(a, ma, 7, 0x00f20005);
            a.SetRegister(Register.Dx, 0x330); ma.WriteDword(0x18000, 16); a.Step(); ca.AdvanceCycles(137, 90_000_000); Check(a.DataBreakpointTraps == 0);
            var (b, mb, cb, pb) = Rig(code); ma.Ram.CopyTo(mb.Ram, 0); b.LoadState(a.SaveState()); cb.LoadState(ca.SaveState(NoOwners), NoObjects); Check(State(a) == State(b));
            Trap(a, ma, 0x2002, 3); Trap(b, mb, 0x2002, 3);
            Check(State(a) == State(b) && ma.Ram.SequenceEqual(mb.Ram) && a.Cr4 == 12 && a.TimestampCounter == b.TimestampCounter && pa.Calls.SequenceEqual(pb.Calls));
        });
        foreach (bool write in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
            test($"M4 DE address16 string advances only low pointer write {write} width {width}", () =>
            {
                byte[] code = StringIo(write, width, address16: true); var (c, m, _, port) = Rig(code); Cr4(c, m, 8); Arm(c, m, 0x330, width);
                c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Si, 0x12346000); c.SetRegister(Register.Di, 0x56787000); m.WriteDword(0x6000, 0x76543210);
                Trap(c, m, 0x2000u + (uint)code.Length); uint pointer = write ? 0x12346000u : 0x56787000u;
                Check(c.GetRegister(write ? Register.Si : Register.Di) == pointer + width && port.Calls.Count == 1);
            });
        foreach (byte[] code in new byte[][] { [0xf0, 0xec], [0xf0, 0xee], [0xf0, 0x6d], [0xf0, 0xf3, 0x6f] })
            test("M4 DE LOCK I/O rejected before device or comparator effect " + Convert.ToHexString(code), () =>
            {
                var (c, m, _, port) = Rig(code); Cr4(c, m, 8); Arm(c, m, 0x330); c.SetRegister(Register.Dx, 0x330); c.SetRegister(Register.Cx, 2); Error(c, m, 6);
                Check(port.Calls.Count == 0 && c.DataBreakpointTraps == 0 && (c.Dr6 & 15) == 0 && c.GetRegister(Register.Cx) == 2);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("M4 DE unavailable no-clock port comparator preserves legacy core " + model, () =>
            {
                var bus = new IoPortBus(); var port = new Port(); bus.Map(0x330, 1, port);
                var (c, m) = ProtectedModeTests.Rig([0xec], io: bus, model: model); Arm(c, m, 0x330); c.SetRegister(Register.Dx, 0x330); c.Step();
                Check(c.Eip == 0x2001 && c.Cr4 == 0 && c.DataBreakpointTraps == 0 && port.Calls.Count == 1);
            });
        foreach ((string field, uint value) in new (string, uint)[] { ("Cr4", 1), ("Cr4", 0x10), ("Cr4", uint.MaxValue), ("Dr7", 0), ("Dr7", 0xc00), ("Dr7", 0x1400), ("Dr7", 0x4400), ("Dr7", 0x8400) })
            test($"M4 DE rejects corrupt prepared CPU state {field} {value:X}", () =>
            {
                var (source, memory, clock, _) = Rig([0x90]); Cr4(source, memory, 12); Arm(source, memory, 0x330, 4); string before = State(source);
                var state = source.SaveState(); state.Fields[field] = JsonSerializer.SerializeToElement(value, StateValues.Json);
                var (prepared, _, preparedClock, _) = Rig([0x90]); bool rejected = false;
                try { prepared.LoadState(state); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && State(source) == before && clock.Now == 0 && preparedClock.Now == 0);
            });
        test("M4 DE REP cut continues through real debug handler with identical CPU RAM and ports", () =>
        {
            byte[] code = StringIo(false, 4, repeat: true); var (a, ma, ca, _) = Rig(code); Cr4(a, ma, 8); Arm(a, ma, 0x330, 4); a.SetRegister(Register.Dx, 0x330); a.SetRegister(Register.Di, 0x31000); a.SetRegister(Register.Cx, 2);
            Trap(a, ma, 0x2000); ca.AdvanceCycles(137, 90_000_000); var (b, mb, cb, pb) = Rig(code); ma.Ram.CopyTo(mb.Ram, 0); b.LoadState(a.SaveState()); cb.LoadState(ca.SaveState(NoOwners), NoObjects);
            Return(a, ma); Return(b, mb); Trap(a, ma, 0x2002); Trap(b, mb, 0x2002);
            Check(State(a) == State(b) && ma.Ram.SequenceEqual(mb.Ram) && pb.Calls.Count == 1 && a.GetRegister(Register.Cx) == 0);
        });
        test("M4 DE reset clears CR4 comparators deferral and counters while retaining clock binding", () =>
        {
            var (c, m, clock, _) = Rig([0xec]); Cr4(c, m, 12); Arm(c, m, 0x330); c.SetRegister(Register.Dx, 0x330); Trap(c, m, 0x2001); clock.AdvanceCycles(137, 90_000_000); c.Reset();
            Check(c.Cr4 == 0 && c.Dr0 == 0 && c.Dr1 == 0 && c.Dr2 == 0 && c.Dr3 == 0 && c.Dr6 == 0xffff0ff0 && c.Dr7 == 0x400);
            Check(c.DataBreakpointTraps == 0 && c.SingleStepTraps == 0 && c.GeneralDetectFaults == 0 && c.HasTimeStampCounter && c.TimestampCounter == 0);
        });
    }
}
