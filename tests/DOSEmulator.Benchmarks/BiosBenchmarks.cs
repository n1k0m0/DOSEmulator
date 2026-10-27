// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class BiosBenchmarks
{
    private sealed class Disk : IWritableBlockDevice
    {
        public readonly byte[] Bytes = new byte[2048 * 512];
        /// <summary>Complete sector count of the private fixed-work medium.</summary>
        public long SectorCount => Bytes.Length / 512;
        /// <summary>Identifies this synthetic medium, which never opens original files.</summary>
        public string Name => "BIOS microbenchmark";
        /// <summary>Seeds predictable payload and a genuine original project boot sector.</summary>
        public Disk() { for (int i = 0; i < Bytes.Length; i++) Bytes[i] = (byte)(i * 29 + i / 512); DevelopmentFirmware.CreateDemoDisk().CopyTo(Bytes, 0); }
        /// <summary>Loads one actual ATA sector from private memory.</summary>
        public void ReadSector(long lba, Span<byte> destination) => Bytes.AsSpan((int)lba * 512, 512).CopyTo(destination);
        /// <summary>Commits one actual ATA sector into private memory.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source) => source.CopyTo(Bytes.AsSpan((int)lba * 512, 512));
        /// <summary>Completes the no-op durability boundary of this explicitly volatile medium.</summary>
        public void Flush() { }
        /// <summary>Reopens an independent memory copy.</summary>
        public IReadOnlyBlockDevice Reopen() { var copy = new Disk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>Releases no external resources.</summary>
        public void Dispose() { }
    }
    /// <summary>Runs actual guest BIOS code to its prepared CLI/HLT return point.</summary>
    private static void Call(PcMachine m, byte vector)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 2;
        m.Memory.WriteByte(0x1000, 0xf4); m.Cpu.EnterRealModeInterrupt(vector); m.RunSteps(1000000);
        if (m.Fault is not null || !m.Cpu.Halted || m.Cpu.Position != "0000:00001001" || (m.Cpu.Eflags & 1) != 0)
            throw new Exception(m.Fault ?? "BIOS benchmark failed to return success");
    }
    /// <summary>Measures fixed real BIOS services after setup, checking exact result bytes outside the timed region.</summary>
    private static object Sample(string name)
    {
        using var disk = new Disk(); using var m = new PcMachine(new MachineProfile(), disk); m.RunSteps(100000);
        const int bytes = 65536;
        byte[] expected = Enumerable.Range(0, bytes).Select(i => (byte)(i * 31 + i / 512)).ToArray();
        bool memory = name == "int15-ah87-64k", write = name == "edd-write-128";
        int calls = memory ? 32 : 8;
        if (memory)
        {
            expected.CopyTo(m.Memory.Ram, 0x30000);
            foreach (var (address, basis) in new (uint, uint)[] { (0x20110, 0x30000), (0x20118, 0x120000) })
            { m.Memory.WriteDword(address, 0xffff | basis << 16); m.Memory.WriteDword(address + 4, (basis >> 16 & 255) | 0x9200); }
        }
        else
        {
            if (write) expected.CopyTo(m.Memory.Ram, 0x40000); else disk.Bytes.AsSpan(8 * 512, bytes).CopyTo(expected);
            m.Memory.WriteWord(0x3000, 16); m.Memory.WriteWord(0x3002, 128); m.Memory.WriteWord(0x3004, 0); m.Memory.WriteWord(0x3006, 0x4000);
            m.Memory.WriteDword(0x3008, 8); m.Memory.WriteDword(0x300c, 0);
        }
        long instructions = m.Cpu.Instructions; ulong virtualBefore = m.Scheduler.Now;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < calls; i++)
        {
            m.Cpu.SetRegister(Register.Ax, memory ? 0x8700u : write ? 0x4300u : 0x4200u);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Si, memory ? 0x100u : 0x3000u);
            m.Cpu.SetRegister(Register.Cx, memory ? 0x8000u : 0); m.Cpu.SetRegister(Register.Dx, 0x80);
            Call(m, memory ? (byte)0x15 : (byte)0x13);
        }
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        byte[] actual = memory ? m.Memory.Ram.AsSpan(0x120000, bytes).ToArray()
            : write ? disk.Bytes.AsSpan(8 * 512, bytes).ToArray() : m.Memory.Ram.AsSpan(0x40000, bytes).ToArray();
        if (!actual.SequenceEqual(expected)) throw new Exception("Independent BIOS payload mismatch");
        return new { name, calls, bytesPerCall = bytes, milliseconds, instructions = m.Cpu.Instructions - instructions,
            virtualNanoseconds = m.Scheduler.Now - virtualBefore, repBatchedElements = m.Cpu.RepBatchedElements,
            checksum = Convert.ToHexString(SHA256.HashData(actual)) };
    }
    /// <summary>Warms each service and emits repeated samples; boot and byte checking are outside stopwatch measurements.</summary>
    public static void Run(int repeats)
    {
        if (repeats is < 3 or > 31) throw new ArgumentOutOfRangeException(nameof(repeats));
        var rows = new List<object>();
        foreach (string name in new[] { "int15-ah87-64k", "edd-read-128", "edd-write-128" })
        { Sample(name); for (int i = 0; i < repeats; i++) rows.Add(Sample(name)); }
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, rows, bootExcludedFromTiming = true,
            onlyPrivateMemoryMedia = true, ordinaryDotnet = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") is null,
            historicalCpuCalibration = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
