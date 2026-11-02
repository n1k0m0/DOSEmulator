// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Actual host-native MMX integer and raw x87-alias comparisons against the independently vector-tested software path.</summary>
internal static class NativeMmxChecks
{
    /// <summary>Encodes fixed own real-mode code with normal original MMX forms, without later mandatory SSE prefixes.</summary>
    private static byte[] Program(byte opcode, bool memory)
    {
        var b = new List<byte> { 0x0f,0x20,0xc0, 0x66,0x83,0xe0,0xf3, 0x0f,0x22,0xc0, 0xdb,0xe3 };
        for (int i = 0; i < 8; i++) b.AddRange([0x0f,0xef,(byte)(0xc0 | i << 3 | i)]);
        b.AddRange([0x0f,0x6f,6,0,0x40, 0x0f,0x6f,0x0e,8,0x40, 0x0f,opcode]);
        b.AddRange(memory ? [6,8,0x40] : new byte[] { 0xc1 });
        b.AddRange([0x0f,0x7f,6,0,0x50, 0xdd,0x36,0,0x51, 0x0f,0x77, 0xf4]);
        return b.ToArray();
    }

    /// <summary>Runs all original binary packed forms on actual WHP hardware and compares defined integer/FPU data with scalar execution.</summary>
    public static void Run()
    {
        byte[] opcodes = [0x60,0x61,0x62,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6a,0x6b,0x74,0x75,0x76,
            0xd1,0xd2,0xd3,0xd5,0xd8,0xd9,0xdb,0xdc,0xdd,0xdf,0xe1,0xe2,0xe5,0xe8,0xe9,0xeb,0xec,0xed,0xef,0xf1,0xf2,0xf3,0xf5,0xf8,0xf9,0xfa,0xfc,0xfd,0xfe];
        ulong[] values = [0,1,7,8,15,16,31,32,63,64,65,255,256,ulong.MaxValue,0x8000800080008000,0x7fff7fff7fff7fff,
            0x8080808080808080,0x7f7f7f7f7f7f7f7f,0x8000000080000000,0x7fffffff7fffffff,0x00ff0100ffff8000,0x0123456789abcdef];
        var memory = new MemoryBus(2 * 1024 * 1024); var clock = new DeviceScheduler();
        using var native = new WhpxMappedMachine(memory, new(), new(), clock, WhpxCpuContract.IntelP55cMmxSubset, 166_000_000);
        int cases = 0;
        foreach (byte opcode in opcodes) foreach (int i in Enumerable.Range(0, values.Length)) foreach (bool fromMemory in new[] { false, true })
        {
            var referenceMemory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(referenceMemory, new(), X86CpuModel.IntelP55cMmxSubset);
            byte[] code = Program(opcode, fromMemory); code.CopyTo(memory.Ram, 0x1000); code.CopyTo(referenceMemory.Ram, 0x1000);
            ulong a = values[(i * 7 + 3) % values.Length], b = values[i];
            BinaryPrimitives.WriteUInt64LittleEndian(memory.Ram.AsSpan(0x4000, 8), a); BinaryPrimitives.WriteUInt64LittleEndian(memory.Ram.AsSpan(0x4008, 8), b);
            memory.Ram.AsSpan(0x4000, 16).CopyTo(referenceMemory.Ram.AsSpan(0x4000));
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ds, 0); cpu.SetSegment(Segment.Ss, 0); cpu.SetRegister(Register.Sp, 0x8000); cpu.Eip = 0x1000;
            for (int step = 0; !cpu.Halted && step < 40; step++) cpu.Step();
            if (!cpu.Halted) throw new Exception("Software MMX reference did not halt.");
            native.SetEntry(0, 0x1000); native.RunUntilHalt(TimeSpan.FromSeconds(3));
            if (!memory.Ram.AsSpan(0x5000, 8).SequenceEqual(referenceMemory.Ram.AsSpan(0x5000, 8)) ||
                !memory.Ram.AsSpan(0x510e, 80).SequenceEqual(referenceMemory.Ram.AsSpan(0x510e, 80)))
                throw new Exception($"Native MMX {opcode:X2} vector {i} memory={fromMemory} differs: {Convert.ToHexString(memory.Ram.AsSpan(0x5000, 8))} versus {Convert.ToHexString(referenceMemory.Ram.AsSpan(0x5000, 8))}.");
            cases++;
        }
        Console.WriteLine($"PASS: {cases} actual native MMX integer and complete 80-byte aliased register comparisons.");
        byte[] identify = [0x66,0xb8,1,0,0,0, 0x0f,0xa2, 0x66,0xa3,0,0x60, 0x66,0x89,0x16,4,0x60, 0xf4];
        identify.CopyTo(memory.Ram, 0x1000); native.SetEntry(0, 0x1000); native.RunUntilHalt(TimeSpan.FromSeconds(3));
        if (memory.ReadDword(0x6000) != 0x543 || memory.ReadDword(0x6004) != 0x00800135) throw new Exception("Native P55C advertised identity differs.");
        var saved = native.CaptureCpuState(); native.RestoreCpuState(saved);
        if (saved.Contract != WhpxCpuContract.IntelP55cMmxSubset || saved.NominalCpuHz != 166_000_000) throw new Exception("Native MMX checkpoint lost model or nominal clock.");
        Console.WriteLine("PASS: native P55C MMX CPUID and distinct stopped checkpoint contract.");
    }
}
