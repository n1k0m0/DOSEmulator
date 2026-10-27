// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class BiosReviewTests
{
    /// <summary>Requires one independently specified firmware result.</summary>
    private static void Check(bool ok, string text = "BIOS review regression") { if (!ok) throw new Exception(text); }
    private sealed class Disk : IWritableBlockDevice
    {
        public readonly byte[] Bytes = new byte[2048 * 512];
        /// <summary>Optional real backend I/O failure at an explicitly selected sector.</summary>
        public long FailWriteAt { get; set; } = -1;
        /// <summary>Capacity of this exclusively private test medium.</summary>
        public long SectorCount => Bytes.Length / 512;
        /// <summary>Identifies the private in-memory regression fixture.</summary>
        public string Name => "own BIOS test data";
        /// <summary>Creates an actual bootable own-sector disk with independently predictable later sectors.</summary>
        public Disk() { for (int i = 0; i < Bytes.Length; i++) Bytes[i] = (byte)((i / 512) * 37 + i * 11); DevelopmentFirmware.CreateDemoDisk().CopyTo(Bytes, 0); }
        /// <summary>Reads one full private sector through the real ATA backend interface.</summary>
        public void ReadSector(long lba, Span<byte> destination) => Bytes.AsSpan((int)lba * 512, 512).CopyTo(destination);
        /// <summary>Writes one full private sector; no filesystem or original media is involved.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        {
            if (lba == FailWriteAt) throw new IOException("deliberate private ATA failure");
            source.CopyTo(Bytes.AsSpan((int)lba * 512, 512));
        }
        /// <summary>The private memory medium has no deferred host writes.</summary>
        public void Flush() { }
        /// <summary>Returns an independent copied fixture rather than sharing a writable disk.</summary>
        public IReadOnlyBlockDevice Reopen() { var copy = new Disk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>Releases no external resource for this isolated memory disk.</summary>
        public void Dispose() { }
    }
    /// <summary>Boots the reference firmware and places a stopped caller beyond the boot sector.</summary>
    private static PcMachine Machine()
    {
        var m = PcMachine.CreateDemo(); m.RunSteps(100000); Check(m.Fault is null && m.Cpu.Halted);
        Caller(m); return m;
    }
    /// <summary>Prepares a real-mode CLI/HLT caller while retaining the actual POST-installed vectors.</summary>
    private static void Caller(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.Eip = 0x1000;
        m.Cpu.Eflags = 2; m.Memory.WriteByte(0x1000, 0xf4);
    }
    /// <summary>Enters the actual guest interrupt and waits for the caller's first HLT.</summary>
    private static void Call(PcMachine m, byte vector, int limit = 100000)
    {
        m.Cpu.EnterRealModeInterrupt(vector); m.RunSteps(limit);
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001001", m.Fault ?? m.Cpu.Position);
    }
    /// <summary>Registers keyboard/clock/POST and real multi-sector ATA regressions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool caps in new[] { false, true })
        foreach (byte shift in new byte[] { 0, 0x2a, 0x36 })
            test($"BIOS Caps/Shift XOR, caps={caps}, shift={shift:X2}", () =>
            {
                using var m = Machine();
                if (caps) { m.Keyboard.InjectTranslatedSet1(0x3a); Call(m, 9); Caller(m); }
                if (shift != 0) { m.Keyboard.InjectTranslatedSet1(shift); Call(m, 9); Caller(m); }
                m.Keyboard.InjectTranslatedSet1(0x1e); Call(m, 9);
                Check(m.Memory.ReadWord(0x41e) == (0x1e00 | (caps ^ shift != 0 ? 'A' : 'a')));
            });
        test("BIOS Caps Lock leaves shifted digits intact and Ctrl-letter remains control-A", () =>
        {
            using var m = Machine();
            foreach (byte scan in new byte[] { 0x3a, 0xba, 0x2a, 0x02, 0x1d, 0x1e })
            { m.Keyboard.InjectTranslatedSet1(scan); Call(m, 9); Caller(m); }
            Check(m.Memory.ReadWord(0x41e) == 0x0221 && m.Memory.ReadWord(0x420) == 0x1e01);
        });
        foreach (ushort value in new ushort[] { 0, 1, 0xffff })
            test($"BIOS clock set clears prior midnight notification, ticks-low={value:X4}", () =>
            {
                using var m = Machine(); m.Memory.WriteByte(0x470, 7);
                m.Cpu.SetRegister(Register.Ax, 0x0100); m.Cpu.SetRegister(Register.Cx, 0x12); m.Cpu.SetRegister(Register.Dx, value);
                Call(m, 0x1a); Check(m.Memory.ReadByte(0x470) == 0 && m.Memory.ReadDword(0x46c) == (0x120000u | value));
                Caller(m); m.Cpu.SetRegister(Register.Ax, 0); Call(m, 0x1a); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0);
            });
        foreach (bool et4000 in new[] { false, true })
            test($"POST identity and configured hardware reach E9 and actual VRAM, ET4000={et4000}", () =>
            {
                using var m = new PcMachine(new MachineProfile { RamMiB = 16, VideoCard = et4000 ? "et4000ax-isa-1m-development" : "dose-vga-development" }, null);
                m.RunSteps(100000); string text = m.Video.ReadText();
                Check(m.Fault is null && m.Cpu.Halted && text.Contains(DOSEBios.BiosName) && text.Contains(DOSEBios.BiosVersion), m.Fault ?? text);
                Check(text.Contains("16384 KiB") && text.Contains("Boot target: IDE") && text.Contains("no readable"), text);
                Check(m.Board.DebugText.Contains(et4000 ? "ET4000AX ISA / 1024 KiB" : "DOSE VGA"));
                Check(m.Memory.ReadByte(0xb8001) == 0x1f && m.Memory.ReadWord(0x460) == 0x0e0f, $"attribute={m.Memory.ReadByte(0xb8001):X}, cursor={m.Memory.ReadWord(0x460):X}");
                Check(System.Text.Encoding.ASCII.GetString(DOSEBios.CreateBios(8, 1), 0xfff5, 8) == DOSEBios.BiosDate);
            });
        foreach (int count in new[] { 1, 2, 127, 128, 255, 256, 257 })
        foreach (bool write in new[] { false, true })
        foreach (ushort offset in new ushort[] { 0x120, 0xff00, 1, 0xfdff, 0xfe01, 0xfffd, 0xfffe, 0xffff })
            test($"BIOS ATA command batching count={count}, write={write}, buffer={offset:X4}", () =>
            {
                using var disk = new Disk(); using var m = new PcMachine(new MachineProfile(), disk); m.RunSteps(100000); Caller(m);
                var counts = new List<uint>(); var commands = new List<uint>(); int wordTransfers = 0;
                m.Ports.Accessed += (p, v, width, w) =>
                {
                    if (w && p == 0x1f2) counts.Add(v); if (w && p == 0x1f7) commands.Add(v);
                    if (p == 0x1f0) { Check(width == 2 && w == write, "ATA data must remain one word per grant"); wordTransfers++; }
                };
                const int lba = 8; uint destination = 0x20000u + offset;
                byte[] expected = new byte[count * 512];
                m.Memory.Ram.AsSpan((int)destination - 16, expected.Length + 32).Fill(0xcc);
                if (write) { for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i * 29 + i / 512); expected.CopyTo(m.Memory.Ram, (int)destination); }
                else disk.Bytes.AsSpan(lba * 512, expected.Length).CopyTo(expected);
                m.Memory.WriteWord(0x3000, 16); m.Memory.WriteWord(0x3002, (ushort)count); m.Memory.WriteWord(0x3004, offset); m.Memory.WriteWord(0x3006, 0x2000);
                m.Memory.WriteDword(0x3008, lba); m.Memory.WriteDword(0x300c, 0);
                m.Cpu.SetRegister(Register.Ax, write ? 0x4300u : 0x4200u); m.Cpu.SetRegister(Register.Dx, 0x80); m.Cpu.SetRegister(Register.Si, 0x3000);
                Call(m, 0x13, 2000000); Check((m.Cpu.Eflags & 1) == 0);
                Check(commands.Count == (count + 255) / 256 && commands.All(v => v == (write ? 0x30u : 0x20u)));
                Check(counts.SequenceEqual(count > 256 ? new uint[] { 0, 1 } : new uint[] { (uint)count & 255 }));
                Check(write ? disk.Bytes.AsSpan(lba * 512, expected.Length).SequenceEqual(expected) : m.Memory.Ram.AsSpan((int)destination, expected.Length).SequenceEqual(expected));
                Check(wordTransfers == count * 256 && m.Memory.Ram.AsSpan((int)destination - 16, 16).ToArray().All(v => v == 0xcc) &&
                    m.Memory.Ram.AsSpan((int)destination + expected.Length, 16).ToArray().All(v => v == 0xcc));
            });
        foreach (int count in new[] { 1, 3 })
        foreach (bool write in new[] { false, true })
        foreach (ushort offset in new ushort[] { 0xfe01, 0xfffd, 0xfffe, 0xffff })
            test($"BIOS CHS odd-boundary buffer={offset:X4}, write={write}, sectors={count}", () =>
            {
                using var disk = new Disk(); using var m = new PcMachine(new MachineProfile(), disk); m.RunSteps(100000); Caller(m);
                const int lba = 8; int address = 0x20000 + offset;
                byte[] expected = Enumerable.Range(0, count * 512).Select(i => (byte)(i * 17 + i / 251 + 3)).ToArray();
                m.Memory.Ram.AsSpan(address - 16, expected.Length + 32).Fill(0xa7);
                if (write) expected.CopyTo(m.Memory.Ram, address); else expected.CopyTo(disk.Bytes, lba * 512);
                m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bx, 0x13570000u | offset);
                m.Cpu.SetRegister(Register.Cx, 0x24680009); m.Cpu.SetRegister(Register.Dx, 0x789a0080);
                m.Cpu.SetRegister(Register.Si, 0x12344321); m.Cpu.SetRegister(Register.Di, 0x34568765);
                m.Cpu.SetRegister(Register.Bp, 0x56783210); m.Cpu.Eflags |= X86Cpu.Direction;
                uint sp = m.Cpu.GetRegister(Register.Sp);
                m.Cpu.SetRegister(Register.Ax, 0xabcd0000u | (write ? 0x0300u : 0x0200u) | (uint)count);
                int words = 0; m.Ports.Accessed += (p, _, width, w) => { if (p == 0x1f0) { Check(width == 2 && w == write); words++; } };
                Call(m, 0x13, 200000);
                Check((m.Cpu.Eflags & 1) == 0 && (m.Cpu.Eflags & X86Cpu.Direction) != 0 && words == count * 256);
                Check((m.Cpu.GetRegister(Register.Ax) & 0xffffff00) == 0xabcd0000 && m.Cpu.GetRegister(Register.Bx) == (0x13570000u | offset));
                Check(m.Cpu.GetRegister(Register.Cx) == 0x24680009 && m.Cpu.GetRegister(Register.Dx) == 0x789a0080 && m.Cpu.GetRegister(Register.Sp) == sp);
                Check(m.Cpu.GetRegister(Register.Si) == 0x12344321 && m.Cpu.GetRegister(Register.Di) == 0x34568765 && m.Cpu.GetRegister(Register.Bp) == 0x56783210 && m.Cpu.GetSegment(Segment.Es) == 0x2000);
                Check(write ? disk.Bytes.AsSpan(lba * 512, expected.Length).SequenceEqual(expected) : m.Memory.Ram.AsSpan(address, expected.Length).SequenceEqual(expected));
                Check(m.Memory.Ram.AsSpan(address - 16, 16).ToArray().All(v => v == 0xa7) && m.Memory.Ram.AsSpan(address + expected.Length, 16).ToArray().All(v => v == 0xa7));
            });
        foreach (uint lba in new uint[] { 2047, uint.MaxValue })
            test($"BIOS ATA rejects complete invalid ranges before issuing any command, LBA={lba:X}", () =>
            {
                using var disk = new Disk(); using var m = new PcMachine(new MachineProfile(), disk); m.RunSteps(100000); Caller(m);
                int commands = 0; m.Ports.Accessed += (p, _, _, w) => { if (w && p == 0x1f7) commands++; };
                m.Memory.WriteWord(0x3000, 16); m.Memory.WriteWord(0x3002, 2); m.Memory.WriteWord(0x3004, 0); m.Memory.WriteWord(0x3006, 0x2000);
                m.Memory.WriteDword(0x3008, lba); m.Memory.WriteDword(0x300c, 0);
                m.Cpu.SetRegister(Register.Ax, 0x4200); m.Cpu.SetRegister(Register.Dx, 0x80); m.Cpu.SetRegister(Register.Si, 0x3000);
                Call(m, 0x13); Check((m.Cpu.Eflags & 1) != 0 && commands == 0);
            });
        test("BIOS multi-sector WRITE returns CF after a real partial backend failure and stops later sectors", () =>
        {
            using var disk = new Disk(); using var m = new PcMachine(new MachineProfile(), disk); m.RunSteps(100000); Caller(m);
            byte[] original = disk.Bytes.ToArray(); disk.FailWriteAt = 10;
            m.Memory.Ram.AsSpan(0x20000, 4 * 512).Fill(0x98);
            m.Memory.WriteWord(0x3000, 16); m.Memory.WriteWord(0x3002, 4); m.Memory.WriteWord(0x3004, 0); m.Memory.WriteWord(0x3006, 0x2000);
            m.Memory.WriteDword(0x3008, 8); m.Memory.WriteDword(0x300c, 0);
            m.Cpu.SetRegister(Register.Ax, 0x4300); m.Cpu.SetRegister(Register.Dx, 0x80); m.Cpu.SetRegister(Register.Si, 0x3000);
            Call(m, 0x13); Check((m.Cpu.Eflags & 1) != 0 && m.Ata.WrittenSectorCount == 2);
            Check(disk.Bytes.AsSpan(8 * 512, 1024).ToArray().All(v => v == 0x98) && disk.Bytes.AsSpan(10 * 512).SequenceEqual(original.AsSpan(10 * 512)));
        });
        foreach (bool binary in new[] { false, true })
            test($"FDC stuck FIFO uses RTC timeout and preserves CMOS index/NMI, binary={binary}", () =>
            {
                var profile = new MachineProfile { FloppyDriveA = "1440k" };
                using var m = new PcMachine(profile, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "boot"), new MemoryDisk(new byte[1440 * 1024], "own floppy"));
                m.RunSteps(100000); Caller(m);
                m.Rtc.Write(0x70, 0xb, 1); m.Rtc.Write(0x71, binary ? 6u : 2u, 1); m.Rtc.Write(0x70, 0x8b, 1);
                ulong? started = null;
                m.Ports.Accessed += (p, v, _, w) => { if (w && p == 0x3f5 && v == 0x46 && started is null) { started = m.Scheduler.Now; m.Floppy.Write(0x3f2, 0, 1); } };
                m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Ax, 0x0201);
                m.Cpu.SetRegister(Register.Cx, 1); m.Cpu.SetRegister(Register.Dx, 0);
                Call(m, 0x13, 25000000);
                Check(started is not null && m.Scheduler.Now - started > 900000000 && m.Scheduler.Now - started < 2100000000);
                Check((m.Cpu.Eflags & 1) != 0 && (m.Cpu.GetRegister(Register.Ax) >> 8 & 255) == 0x80);
                Check(m.Rtc.Read(0x70, 1) == 0x8b && !m.Pit.UnsupportedProgramming);
            });
    }
}
