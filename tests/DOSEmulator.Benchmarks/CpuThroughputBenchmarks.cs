// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

internal static class CpuThroughputBenchmarks
{
    /// <summary>Creates private flat PE32 memory and optionally identity paging using actual decoded control instructions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Setup(byte[] code, bool paging)
    {
        var m = new MemoryBus(4 * 1048576); var c = new X86Cpu(m, new());
        m.WriteDword(0x408, 0xffff); m.WriteDword(0x40c, 0x00cf9a00);
        m.WriteDword(0x410, 0xffff); m.WriteDword(0x414, 0x00cf9200);
        m.WriteWord(0x300, 0x17); m.WriteDword(0x302, 0x400);
        byte[] bootstrap = [0x0f, 0x01, 0x16, 0, 3, 0x66, 0xb8, 0x11, 0, 0, 0x60,
            0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0];
        bootstrap.CopyTo(m.Ram, 0x1000); code.CopyTo(m.Ram, 0x2000);
        c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000;
        for (int i = 0; i < 4; i++) c.Step();
        if (c.Eip != 0x2000 || !c.ProtectedMode) throw new Exception("PE fixture setup failed");
        c.SetSegment(Segment.Ds, 16); c.SetSegment(Segment.Ss, 16); c.SetRegister(Register.Sp, 0x18000);
        if (paging)
        {
            m.WriteDword(0x1c000, 0x1d007);
            for (uint i = 0; i < 1024; i++) m.WriteDword(0x1d000 + i * 4, i * 4096 | 7);
            new byte[] { 0x0f, 0x22, 0xd8, 0x0f, 0x20, 0xc0, 0x0d, 0, 0, 0, 0x80, 0x0f, 0x22, 0xc0 }.CopyTo(m.Ram, 0x2200);
            c.SetRegister(Register.Ax, 0x1c000); c.Eip = 0x2200;
            for (int i = 0; i < 4; i++) c.Step();
            if (!c.PagingEnabled) throw new Exception("Paging fixture setup failed");
        }
        c.Eip = 0x2000; c.Eflags = 2;
        c.SetRegister(Register.Ax, 0x98fedcba); c.SetRegister(Register.Bx, 0x13579bdf); c.SetRegister(Register.Dx, 0x87654321);
        return (c, m);
    }

    /// <summary>Measures fixed instruction counts after warmup; checksums accompany each separate ordinary-.NET process.</summary>
    public static void Run(int repeats)
    {
        if (repeats is < 3 or > 31) throw new ArgumentOutOfRangeException(nameof(repeats));
        const int instructions = 2_000_000;
        (string Name, byte[] Code)[] programs = [
            ("register-immediates", [0xb8, 0x78, 0x56, 0x34, 0x12, 0xbb, 0x21, 0x43, 0x65, 0x87, 0xba, 0xef, 0xcd, 0xab, 0x89, 0xeb, 0xef]),
            ("arithmetic-flags", [0x01, 0xd0, 0x11, 0xd8, 0x29, 0xc2, 0x31, 0xd8, 0xeb, 0xf6]),
            ("memory-and-displacements", [0xa1, 0, 0, 0x20, 0, 0x03, 0xc2, 0xa3, 0, 0, 0x20, 0, 0xeb, 0xf2]),
            ("single-byte-registers", [0x40, 0x42, 0x4b, 0xeb, 0xfb])];
        var rows = new List<object>();
        foreach (bool paging in new[] { false, true })
        foreach (var program in programs)
        {
            var warm = Setup(program.Code, paging);
            for (int i = 0; i < 500_000; i++) warm.Cpu.Step();
            var times = new List<double>(); string? expected = null;
            for (int repeat = 0; repeat < repeats; repeat++)
            {
                var (c, m) = Setup(program.Code, paging);
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < instructions; i++) c.Step();
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                string checksum = string.Join("/", c.Eip, c.Eflags, c.Instructions, c.PageWalks, c.PageFaults,
                    c.GetRegister(Register.Ax), c.GetRegister(Register.Bx), c.GetRegister(Register.Dx), m.ReadDword(0x200000));
                if (expected is not null && checksum != expected) throw new Exception("Fixed-work result changed");
                expected = checksum;
            }
            rows.Add(new { name = program.Name, paging, instructions, medianMilliseconds = times.Order().ElementAt(repeats / 2),
                milliseconds = times, checksum = expected });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { runtime = RuntimeInformation.FrameworkDescription,
            ordinaryDotnetConfiguration = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") is null,
            fixedVirtualTimingUnchanged = true, rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
