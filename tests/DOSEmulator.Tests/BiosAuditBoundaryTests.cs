// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual INT13 DAP/AH48 boundary returns with independent memory, register and port oracles.</summary>
internal static class BiosAuditBoundaryTests
{
    private sealed class Disk : IWritableBlockDevice
    {
        public readonly byte[] Bytes = new byte[2048 * 512];
        /// <summary>Fixed capacity of the private in-memory fixture.</summary>
        public long SectorCount => Bytes.Length / 512;
        /// <summary>Identifies exclusively synthetic bytes with no original media source.</summary>
        public string Name => "own INT13 metadata test disk";
        /// <summary>Creates only an independent in-memory boot medium and deterministic later sectors.</summary>
        public Disk()
        { for (int i = 0; i < Bytes.Length; i++) Bytes[i] = (byte)(i * 13 + i / 512 * 7); DevelopmentFirmware.CreateDemoDisk().CopyTo(Bytes, 0); }
        /// <summary>Returns one actual sector through the private ATA backend.</summary>
        public void ReadSector(long lba, Span<byte> destination) => Bytes.AsSpan((int)lba * 512, 512).CopyTo(destination);
        /// <summary>Commits only one complete private in-memory sector.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source) => source.CopyTo(Bytes.AsSpan((int)lba * 512, 512));
        /// <summary>Has no pending host I/O or external endpoint.</summary>
        public void Flush() { }
        /// <summary>Returns a separately owned reopened fixture without sharing writable data.</summary>
        public IReadOnlyBlockDevice Reopen() { var copy = new Disk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>Releases no external resource.</summary>
        public void Dispose() { }
    }
    private sealed class MetadataDevice : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Counts any forbidden metadata read before rejection without throwing a host exception.</summary>
        public byte ReadByte(uint offset) { Reads++; return offset == 0 ? (byte)26 : (byte)0; }
        /// <summary>Counts a forbidden partial metadata write.</summary>
        public void WriteByte(uint offset, byte value) => Writes++;
    }
    /// <summary>Requires a real guest return or independently specified memory/port/register effect.</summary>
    private static void Check(bool value, string message = "INT13 metadata boundary invariant")
    { if (!value) throw new Exception(message); }
    /// <summary>Boots the real DOSEBios and installs a bounded caller with distinct full registers.</summary>
    private static PcMachine Create(Disk disk, byte function, ushort offset, bool carry = false, byte drive = 0x80)
    {
        var machine = new PcMachine(new MachineProfile(), disk); machine.RunSteps(100000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? machine.Cpu.Position);
        var c = machine.Cpu; c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0); c.SetSegment(Segment.Ds, 0x2000);
        c.SetSegment(Segment.Es, 0x4444); c.SetSegment(Segment.Fs, 0x1111); c.SetSegment(Segment.Gs, 0x3333);
        c.Eip = 0x1000; machine.Memory.WriteByte(0x1000, 0xf4); c.Eflags = 0xcd6u | (carry ? 1u : 0);
        c.SetRegister(Register.Ax, 0xabcd0000u | (uint)function << 8 | 0xa5);
        c.SetRegister(Register.Bx, 0x13572468); c.SetRegister(Register.Cx, 0x2468aced); c.SetRegister(Register.Dx, 0x789a0000u | drive);
        c.SetRegister(Register.Si, 0xdead0000u | offset); c.SetRegister(Register.Di, 0x34568765); c.SetRegister(Register.Bp, 0x56783210); c.SetRegister(Register.Sp, 0xbeef9000);
        machine.Memory.WriteByte(0x474, 0xa9); return machine;
    }
    /// <summary>Executes the genuine firmware interrupt until the caller's own HLT or a strict instruction bound.</summary>
    private static void Call(PcMachine machine)
    {
        machine.Cpu.EnterRealModeInterrupt(0x13); machine.RunSteps(100000);
        Check(machine.Fault is null && machine.Cpu.Halted && machine.Cpu.Position == "0000:00001001", machine.Fault ?? machine.Cpu.Position);
    }
    /// <summary>Writes a physically contiguous private request without hiding segment-limit checks in the guest.</summary>
    private static void Request(PcMachine machine, byte function, ushort offset, ushort length = 0)
    {
        uint at = 0x20000u + offset; machine.Memory.Ram.AsSpan((int)at - 8, 48).Fill(0x7d);
        if (function == 0x48) machine.Memory.WriteWord(at, length == 0 ? (ushort)26 : length);
        else
        {
            machine.Memory.WriteByte(at, length == 0 ? (byte)16 : (byte)length); machine.Memory.WriteByte(at + 1, 0);
            machine.Memory.WriteWord(at + 2, 1); machine.Memory.WriteWord(at + 4, 0); machine.Memory.WriteWord(at + 6, 0x6000);
            machine.Memory.WriteDword(at + 8, 8); machine.Memory.WriteDword(at + 12, 0);
        }
    }
    /// <summary>Requires all original full registers/segments and flags except the documented AH/CF result.</summary>
    private static void Preserved(PcMachine machine, uint[] registers, uint flags, byte ah)
    {
        var c = machine.Cpu;
        foreach (Register register in Enum.GetValues<Register>())
            Check(c.GetRegister(register) == (register == Register.Ax ? (registers[0] & 0xffff00ff) | (uint)ah << 8 : registers[(int)register]), "Register changed: " + register);
        Check((c.Eflags & ~1u) == (flags & ~1u) && c.GetSegment(Segment.Cs) == 0 && c.GetSegment(Segment.Ss) == 0 && c.GetSegment(Segment.Ds) == 0x2000 &&
            c.GetSegment(Segment.Es) == 0x4444 && c.GetSegment(Segment.Fs) == 0x1111 && c.GetSegment(Segment.Gs) == 0x3333, "Flags/segment preservation");
    }
    /// <summary>Independently specifies the exact 26-byte returned HDD parameter block.</summary>
    private static byte[] Parameters()
    {
        byte[] expected = new byte[26]; BinaryPrimitives.WriteUInt16LittleEndian(expected, 26); BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(12), 63); BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(16), 2048);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(24), 512); return expected;
    }
    /// <summary>Checks a real successful or rejected metadata request against exact independent bytes and ATA grants.</summary>
    private static void Boundary(byte function, ushort offset, bool carry, ushort declared = 0)
    {
        using var disk = new Disk(); using var machine = Create(disk, function, offset, carry); Request(machine, function, offset, declared);
        uint at = 0x20000u + offset; byte[] before = machine.Memory.Ram.AsSpan((int)at - 8, 48).ToArray(); byte[] source = disk.Bytes.AsSpan(8 * 512, 512).ToArray();
        byte[] data = Enumerable.Range(0, 512).Select(i => (byte)(i * 19 + 3)).ToArray(); machine.Memory.Ram.AsSpan(0x5fff0, 544).Fill(0xa7);
        if (function == 0x43) data.CopyTo(machine.Memory.Ram, 0x60000);
        uint[] registers = Enumerable.Range(0, 8).Select(i => machine.Cpu.GetRegister((Register)i)).ToArray(); uint flags = machine.Cpu.Eflags;
        int accesses = 0, commands = 0, words = 0;
        machine.Ports.Accessed += (port, value, width, write) =>
        { if (port is >= 0x1f0 and <= 0x1f7) { accesses++; if (port == 0x1f7 && write) { commands++; Check(value == (function == 0x43 ? 0x30u : 0x20u)); } if (port == 0x1f0) { words++; Check(width == 2 && write == (function == 0x43)); } } };
        bool accepted = offset <= (function == 0x48 ? 0xffe6 : 0xfff0) && (declared == 0 || declared >= (function == 0x48 ? 26 : 16));
        Call(machine); Preserved(machine, registers, flags, accepted ? (byte)0 : (byte)1);
        Check((machine.Cpu.Eflags & 1) == (accepted ? 0u : 1u) && machine.Memory.ReadByte(0x474) == (accepted ? 0 : 1), "AH/CF/BDA status");
        byte[] after = machine.Memory.Ram.AsSpan((int)at - 8, 48).ToArray();
        if (accepted && function == 0x48) { byte[] expected = before.ToArray(); Parameters().CopyTo(expected, 8); Check(after.SequenceEqual(expected), "Full parameter block/guards"); }
        else Check(after.SequenceEqual(before), "Request packet or metadata guards changed");
        Check(commands == (accepted && function != 0x48 ? 1 : 0) && words == (accepted && function != 0x48 ? 256 : 0));
        if (!accepted || function == 0x48) Check(accesses == 0, "Rejected/metadata-only call reached ATA");
        Check(machine.Memory.Ram.AsSpan(0x5fff0, 16).ToArray().All(v => v == 0xa7) && machine.Memory.Ram.AsSpan(0x60200, 16).ToArray().All(v => v == 0xa7), "Data guards changed");
        Check(disk.Bytes.AsSpan(8 * 512, 512).SequenceEqual(accepted && function == 0x43 ? data : source), "Private disk effects");
        Check(machine.Memory.Ram.AsSpan(0x60000, 512).SequenceEqual(accepted && function == 0x42 ? source : function == 0x43 ? data : Enumerable.Repeat((byte)0xa7, 512).ToArray()), "Private data-buffer effects");
    }
    /// <summary>Registers actual last-valid/first-invalid metadata and zero-access rejection controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (byte function in new byte[] { 0x42, 0x43 })
        foreach (ushort offset in new ushort[] { 0x100, 0xffe0, 0xfff0, 0xfff1, 0xfff2, 0xfffd, 0xfffe, 0xffff })
        foreach (bool carry in new[] { false, true })
            test($"Audit BIOS metadata AH{function:X2} offset{offset:X4} CF{carry}", () => Boundary(function, offset, carry));
        foreach (ushort offset in new ushort[] { 0x100, 0xffe6, 0xffe7, 0xfff0, 0xfffd, 0xfffe, 0xffff })
        foreach (bool carry in new[] { false, true })
            test($"Audit BIOS metadata AH48 offset{offset:X4} CF{carry}", () => Boundary(0x48, offset, carry));
        foreach (byte function in new byte[] { 0x42, 0x43, 0x48 })
            test($"Audit BIOS metadata rejects before even first buffer read AH{function:X2}", () =>
            {
                using var disk = new Disk(); ushort offset = function == 0x48 ? (ushort)0xffe7 : (ushort)0xfff1;
                using var machine = Create(disk, function, offset); var device = new MetadataDevice(); machine.Memory.Map(0x20000u + offset, 32, device, "Invalid metadata must not be read");
                int ports = 0; machine.Ports.Accessed += (p, _, _, _) => { if (p is >= 0x1f0 and <= 0x1f7) ports++; }; Call(machine);
                Check(device.Reads == 0 && device.Writes == 0 && ports == 0 && (machine.Cpu.Eflags & 1) != 0 && (machine.Cpu.GetRegister(Register.Ax) & 0xff00) == 0x100);
            });
        foreach (byte function in new byte[] { 0x42, 0x43, 0x48 })
            test($"Audit BIOS metadata absent drive rejects before invalid packet AH{function:X2}", () =>
            {
                using var disk = new Disk(); using var machine = Create(disk, function, 0xffff, drive: 0x84); var device = new MetadataDevice(); machine.Memory.Map(0x2ffff, 32, device, "Absent drive metadata");
                int ports = 0; machine.Ports.Accessed += (p, _, _, _) => { if (p is >= 0x1f0 and <= 0x1f7) ports++; }; Call(machine);
                Check(device.Reads == 0 && device.Writes == 0 && ports == 0 && machine.Memory.ReadByte(0x474) == 1 && (machine.Cpu.Eflags & 1) != 0);
            });
        foreach (byte function in new byte[] { 0x42, 0x43, 0x48 })
            test($"Audit BIOS metadata short declared size preserves packet AH{function:X2}", () => Boundary(function, function == 0x48 ? (ushort)0xffe6 : (ushort)0xfff0, false, function == 0x48 ? (ushort)25 : (ushort)15));
        foreach (byte function in new byte[] { 0x42, 0x43 })
            test($"Audit BIOS metadata larger DAP retains implemented16-byte prefix AH{function:X2}", () => Boundary(function, 0xfff0, false, 24));
        test("Audit BIOS metadata AH48 larger size writes only26 bytes at last valid offset", () => Boundary(0x48, 0xffe6, false, ushort.MaxValue));
    }
}
