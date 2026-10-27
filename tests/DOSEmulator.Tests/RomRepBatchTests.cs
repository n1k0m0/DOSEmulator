// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class RomRepBatchTests
{
    /// <summary>Requires equivalent architectural/device observations from scalar and batched execution.</summary>
    private static void Check(bool ok, string text = "ROM REP mismatch") { if (!ok) throw new Exception(text); }
    private sealed class Device : IMemoryDevice
    {
        public int Reads;
        /// <summary>Counts any prohibited speculative instruction inspection.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x90; }
        /// <summary>Ignores writes to this controlled read-only MMIO fixture.</summary>
        public void WriteByte(uint offset, byte value) { }
    }
    /// <summary>Compares complete RAM, registers, segment caches, clocks and paging statistics.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        Check(a.Fault == b.Fault && a.Scheduler.Now == b.Scheduler.Now);
        Check(a.Cpu.Eip == b.Cpu.Eip && a.Cpu.Eflags == b.Cpu.Eflags && a.Cpu.Instructions == b.Cpu.Instructions);
        foreach (Register r in Enum.GetValues<Register>()) Check(a.Cpu.GetRegister(r) == b.Cpu.GetRegister(r), r.ToString());
        foreach (Segment s in Enum.GetValues<Segment>()) Check(a.Cpu.GetSegmentCache(s) == b.Cpu.GetSegmentCache(s));
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "RAM/A-D effects");
        Check(a.Cpu.PageWalks == b.Cpu.PageWalks && a.Cpu.TlbHits == b.Cpu.TlbHits && a.Cpu.PageFaults == b.Cpu.PageFaults);
    }
    /// <summary>Registers safe ROM inspection plus real decoded REP/page/deadline comparisons.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("read-only code spans accept copied ROM while excluding MMIO, gaps, backing-image ends and A20 boundaries", () =>
        {
            var m = new MemoryBus(2 * 1048576); byte[] source = [0xf3, 0xa4, 0x90];
            m.Map(0xf0000, 4096, new RomDevice(source), "short ROM"); source[0] = 0;
            Check(m.TryGetCodeSpan(0xf0000, 2, out var bytes) && bytes.SequenceEqual(new byte[] { 0xf3, 0xa4 }));
            m.WriteByte(0xf0000, 0); Check(m.TryGetCodeSpan(0xf0000, 1, out bytes) && bytes[0] == 0xf3);
            Check(!m.TryGetRamSpan(0xf0000, 1, out _) && !m.TryGetCodeSpan(0xf0001, 3, out _));
            var d = new Device(); m.Map(0x4000, 2, d, "MMIO");
            Check(!m.TryGetCodeSpan(0x4000, 2, out _) && !m.TryGetCodeSpan(0x3fff, 2, out _) && d.Reads == 0);
            Check(!m.TryGetCodeSpan(uint.MaxValue, 2, out _) && !m.TryGetCodeSpan(0, 0, out _));
            m.A20Enabled = false;
            Check(m.TryGetCodeSpan(0x1f0000, 2, out bytes) && bytes[0] == 0xf3);
            Check(!m.TryGetCodeSpan(0xfffff, 2, out _));
        });
        foreach (bool paging in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
        foreach (bool reverse in new[] { false, true })
        foreach (bool store in new[] { false, true })
            test($"ROM REP vs scalar width={width}, paging={paging}, DF={reverse}, store={store}", () =>
            {
                byte op = (byte)(store ? width == 1 ? 0xaa : 0xab : width == 1 ? 0xa4 : 0xa5);
                byte[] code = [.. (width == 2 ? new byte[] { 0x66 } : Array.Empty<byte>()), 0xf3, op, 0xf4];
                using var a = RepBatchTests.Machine(code, paging); using var b = RepBatchTests.Machine(code, paging);
                foreach (var m in new[] { a, b })
                {
                    m.Memory.Map(0x2000, (uint)code.Length, new RomDevice(code), "own ROM instructions");
                    for (int i = 0; i < 16384; i++) m.Memory.Ram[0x30000 + i] = (byte)(i * 31 + 7);
                    uint back = reverse ? (uint)(2050 * width) : 0;
                    m.Cpu.SetRegister(Register.Si, 0x30000 + back); m.Cpu.SetRegister(Register.Di, 0x40000 + back);
                    m.Cpu.SetRegister(Register.Cx, 2051); m.Cpu.SetRegister(Register.Ax, 0x9876abcd);
                    if (reverse) m.Cpu.Eflags |= X86Cpu.Direction;
                }
                for (int i = 0; i < 2052; i++) a.RunSteps(1);
                b.RunSteps(2052); Same(a, b); Check(b.Cpu.RepBatchedElements > 1500);
            });
        foreach (bool observer in new[] { false, true })
            test($"ROM REP honors device deadlines and observer fallback, observe={observer}", () =>
            {
                byte[] code = [0xf3, 0xa4, 0xf4];
                using var a = RepBatchTests.Machine(code); using var b = RepBatchTests.Machine(code);
                var logs = new List<string>[] { [], [] };
                foreach (var (m, index) in new[] { (a, 0), (b, 1) })
                {
                    m.Memory.Map(0x2000, 3, new RomDevice(code), "deadline ROM");
                    m.Cpu.SetRegister(Register.Si, 0x30000); m.Cpu.SetRegister(Register.Di, 0x40000); m.Cpu.SetRegister(Register.Cx, 2000);
                    if (observer) m.Cpu.InstructionStarting += (_, _) => { };
                    m.Scheduler.ScheduleAfter(10000, () => { logs[index].Add($"{m.Cpu.GetRegister(Register.Cx)}:{m.Memory.ReadByte(0x40000)}"); m.Memory.WriteByte(0x30100, 0x98); });
                }
                for (int i = 0; i < 2001; i++) a.RunSteps(1);
                b.RunSteps(2001); Same(a, b); Check(logs[0].SequenceEqual(logs[1]));
                Check(observer ? b.Cpu.RepBatchedElements == 0 : b.Cpu.RepBatchedElements > 0);
            });
        test("ROM REP code crossing a linear page retains the scalar fetch path", () =>
        {
            using var m = RepBatchTests.Machine([], true);
            m.Memory.Map(0x2fff, 3, new RomDevice(new byte[] { 0xf3, 0xaa, 0xf4 }), "page-crossing ROM");
            m.Cpu.Eip = 0x2fff; m.Cpu.SetRegister(Register.Cx, 128); m.Cpu.SetRegister(Register.Di, 0x40000);
            m.RunSteps(129); Check(m.Cpu.Halted && m.Cpu.Eip == 0x3002 && m.Cpu.RepBatchedElements == 0);
        });
        test("ROM REP reaches the missing data page after the exact successful element count", () =>
        {
            byte[] code = [0xf3, 0xaa]; using var a = RepBatchTests.Machine(code, true); using var b = RepBatchTests.Machine(code, true);
            foreach (var m in new[] { a, b })
            {
                m.Memory.Map(0x2000, 2, new RomDevice(code), "faulting ROM REP");
                m.Cpu.SetRegister(Register.Di, 0x30f80); m.Cpu.SetRegister(Register.Cx, 8192);
                m.Memory.WriteDword(0x1d000 + 0x31 * 4, 0); m.Memory.WriteWord(0x330, 0x7ff); m.Memory.WriteDword(0x332, 0x8000);
                ProtectedModeTests.Gate(m.Memory, 14); m.Memory.WriteByte(0x6000, 0xf4);
                new byte[] { 0x0f, 0x01, 0x1d, 0x30, 3, 0, 0 }.CopyTo(m.Memory.Ram, 0x2200);
                m.Cpu.Eip = 0x2200; m.RunSteps(1); m.Cpu.Eip = 0x2000;
            }
            for (int i = 0; i < 8200; i++) a.RunSteps(1);
            b.RunSteps(8200); Same(a, b);
            Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x31000 && b.Cpu.GetRegister(Register.Cx) == 8192 - 128);
        });
    }
}
