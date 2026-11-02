// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Original MMX integer vectors, aliased raw x87 state, historical prefixes and fault-atomic operands.</summary>
internal static class MmxTests
{
    /// <summary>Requires an independently computed architectural value.</summary>
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>Own RAM and CPU fixture with ordinary scalar instruction execution.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory);
    /// <summary>Creates a model-specific real-mode CPU and explicit fault handler targets.</summary>
    private static Rig Make(X86CpuModel model = X86CpuModel.IntelP55cMmxSubset)
    {
        var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, new(), model);
        cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ds, 0); cpu.SetSegment(Segment.Ss, 0);
        cpu.SetRegister(Register.Sp, 0x9000); cpu.Eip = 0x1000;
        foreach (int vector in new[] { 6, 7, 12, 13, 16, 17 }) { memory.WriteWord((uint)(vector * 4), (ushort)(0x2000 + vector * 16)); memory.WriteWord((uint)(vector * 4 + 2), 0); }
        return new(cpu, memory);
    }
    /// <summary>Executes one exact own instruction after installing it at a fixed isolated address.</summary>
    private static void Execute(Rig rig, params byte[] bytes)
    { rig.Cpu.Eip = 0x1000; bytes.CopyTo(rig.Memory.Ram, 0x1000); rig.Cpu.Step(); }
    /// <summary>Seeds a physical MMX slot with an actual MOVQ memory instruction rather than a diagnostic setter.</summary>
    private static void Seed(Rig rig, int reg, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(rig.Memory.Ram.AsSpan(0x4000, 8), value);
        Execute(rig, 0x0f, 0x6f, (byte)(reg << 3 | 6), 0, 0x40);
    }
    /// <summary>Extracts mathematically signed/unsigned lanes through BigInteger arithmetic, independently from the packed implementation.</summary>
    private static BigInteger Lane(ulong value, int lane, int bits, bool signed = false)
    {
        BigInteger modulus = BigInteger.One << bits;
        BigInteger result = (new BigInteger(value) / BigInteger.Pow(modulus, lane)) % modulus;
        return signed && result >= modulus / 2 ? result - modulus : result;
    }
    /// <summary>Concatenates arbitrary-precision lane values with explicit modular wrapping.</summary>
    private static ulong Join(IEnumerable<BigInteger> lanes, int bits)
    {
        BigInteger result = 0, modulus = BigInteger.One << bits; int i = 0;
        foreach (var value in lanes) { result += ((value % modulus + modulus) % modulus) << (bits * i); i++; }
        return (ulong)result;
    }
    /// <summary>Computes packed arithmetic and ordering from independently represented integer lane arrays.</summary>
    private static ulong Expected(byte opcode, ulong a, ulong b)
    {
        if (opcode is 0xdb or 0xdf or 0xeb or 0xef) return opcode switch { 0xdb => a & b, 0xdf => ~a & b, 0xeb => a | b, _ => a ^ b };
        if (opcode is >= 0x60 and <= 0x62 or >= 0x68 and <= 0x6a)
        {
            int bits = 8 * (1 << (opcode & 3)), half = 32 / bits, first = opcode >= 0x68 ? half : 0;
            return Join(Enumerable.Range(first, half).SelectMany(i => new[] { Lane(a, i, bits), Lane(b, i, bits) }), bits);
        }
        if (opcode is 0x63 or 0x67 or 0x6b)
        {
            int input = opcode == 0x6b ? 32 : 16, output = input / 2;
            BigInteger low = opcode == 0x67 ? 0 : -(BigInteger.One << (output - 1));
            BigInteger high = opcode == 0x67 ? 255 : (BigInteger.One << (output - 1)) - 1;
            return Join(new[] { a, b }.SelectMany(v => Enumerable.Range(0, 64 / input).Select(i => BigInteger.Clamp(Lane(v, i, input, true), low, high))), output);
        }
        if (opcode == 0xf5) return Join(Enumerable.Range(0, 2).Select(i =>
            Lane(a, i * 2, 16, true) * Lane(b, i * 2, 16, true) + Lane(a, i * 2 + 1, 16, true) * Lane(b, i * 2 + 1, 16, true)), 32);
        if (opcode is 0xd1 or 0xd2 or 0xd3 or 0xe1 or 0xe2 or 0xf1 or 0xf2 or 0xf3)
        {
            int bits = 16 << ((opcode & 15) - 1); bool left = opcode >= 0xf1, arithmetic = opcode is 0xe1 or 0xe2;
            int count = (int)Math.Min(b, 128UL);
            return Join(Enumerable.Range(0, 64 / bits).Select(i => left ? Lane(a, i, bits) << count : Lane(a, i, bits, arithmetic) >> count), bits);
        }
        int size = opcode is 0x64 or 0x74 or 0xd8 or 0xdc or 0xe8 or 0xec or 0xf8 or 0xfc ? 8 : opcode is 0x66 or 0x76 or 0xfa or 0xfe ? 32 : 16;
        BigInteger mask = (BigInteger.One << size) - 1;
        return Join(Enumerable.Range(0, 64 / size).Select(i =>
        {
            BigInteger x = Lane(a, i, size), y = Lane(b, i, size), sx = Lane(a, i, size, true), sy = Lane(b, i, size, true);
            if (opcode is >= 0x64 and <= 0x66) return sx > sy ? mask : 0;
            if (opcode is >= 0x74 and <= 0x76) return x == y ? mask : 0;
            if (opcode is 0xd5 or 0xe5) return opcode == 0xd5 ? sx * sy : sx * sy >> 16;
            if (opcode is 0xdc or 0xdd) return BigInteger.Min(x + y, mask);
            if (opcode is 0xd8 or 0xd9) return BigInteger.Max(x - y, 0);
            if (opcode is 0xec or 0xed or 0xe8 or 0xe9) return BigInteger.Clamp(opcode is 0xec or 0xed ? sx + sy : sx - sy,
                -(BigInteger.One << (size - 1)), (BigInteger.One << (size - 1)) - 1);
            return opcode is >= 0xfc and <= 0xfe ? x + y : x - y;
        }), size);
    }
    /// <summary>Compares a register or full memory-source form and its untouched flags/source lanes.</summary>
    private static void Vector(byte opcode, ulong a, ulong b, bool memory)
    {
        var r = Make(); Seed(r, 0, a); Seed(r, 1, b); uint flags = r.Cpu.Eflags = 0x8d7;
        if (memory)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(r.Memory.Ram.AsSpan(0x4800, 8), b);
            Execute(r, 0x0f, opcode, 0x06, 0, 0x48);
        }
        else Execute(r, 0x0f, opcode, 0xc1);
        Check(r.Cpu.Eip == 0x1000 + (memory ? 5 : 3), "MMX instruction faulted");
        Check(r.Cpu.GetMmxRegister(0) == Expected(opcode, a, b), $"MMX {opcode:X2}: {r.Cpu.GetMmxRegister(0):X16} expected {Expected(opcode, a, b):X16}");
        Check(r.Cpu.GetMmxRegister(1) == b && r.Cpu.Eflags == flags, "MMX source or EFLAGS changed");
        Check(r.Cpu.GetFpuRegister(0).SignExponent == 0xffff && r.Cpu.FpuTop == 0 && !r.Cpu.IsFpuRegisterEmpty(7), "MMX alias/TOP/tags differ");
    }
    /// <summary>Registers deterministic edge patterns and actual fault/restart/state checks for the new instruction set.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        byte[] opcodes = [0x60,0x61,0x62,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6a,0x6b,0x74,0x75,0x76,
            0xd1,0xd2,0xd3,0xd5,0xd8,0xd9,0xdb,0xdc,0xdd,0xdf,0xe1,0xe2,0xe5,0xe8,0xe9,0xeb,0xec,0xed,0xef,0xf1,0xf2,0xf3,0xf5,0xf8,0xf9,0xfa,0xfc,0xfd,0xfe];
        ulong[] values = [0,1,7,8,15,16,31,32,63,64,65,255,256,ulong.MaxValue,0x8000800080008000,0x7fff7fff7fff7fff,
            0x8080808080808080,0x7f7f7f7f7f7f7f7f,0x8000000080000000,0x7fffffff7fffffff,0x00ff0100ffff8000,0x0123456789abcdef];
        foreach (byte opcode in opcodes) foreach (int i in Enumerable.Range(0, values.Length)) foreach (bool memory in new[] { false, true })
        {
            ulong a = values[(i * 7 + 3) % values.Length], b = values[i];
            test($"MMX {opcode:X2} {(memory ? "memory" : "register")} edge {i}", () => Vector(opcode, a, b, memory));
        }
        foreach (byte opcode in new byte[] { 0x71, 0x72, 0x73 }) foreach (int operation in new[] { 2, 4, 6 }) foreach (byte count in new byte[] { 0,1,15,16,31,32,63,64,65,255 })
        {
            if (opcode == 0x73 && operation == 4) continue;
            test($"MMX immediate {opcode:X2}/{operation} count {count}", () =>
            {
                var r = Make(); ulong a = 0x800000007fff8001; Seed(r, 3, a);
                Execute(r, 0x0f, opcode, (byte)(0xc3 | operation << 3), count);
                byte variable = (byte)((operation == 2 ? 0xd1 : operation == 4 ? 0xe1 : 0xf1) + opcode - 0x71);
                Check(r.Cpu.GetMmxRegister(3) == Expected(variable, a, count), "Immediate count/result differs");
            });
        }
        foreach (int reg in Enumerable.Range(0, 8)) test($"MMX MOVD MOVQ EMMS physical register {reg}", () =>
        {
            var r = Make(); r.Cpu.SetRegister(Register.Ax, 0xf1234567);
            Execute(r, 0x0f, 0x6e, (byte)(0xc0 | reg << 3)); Check(r.Cpu.GetMmxRegister(reg) == 0xf1234567, "MOVD did not zero-extend");
            Seed(r, reg, 0xfedcba9876543210); Execute(r, 0x0f, 0x7e, (byte)(0xc3 | reg << 3));
            Check(r.Cpu.GetRegister(Register.Bx) == 0x76543210, "MOVD r32 width differs");
            Execute(r, 0x0f, 0x7f, (byte)(reg << 3 | 6), 0, 0x48);
            Check(BinaryPrimitives.ReadUInt64LittleEndian(r.Memory.Ram.AsSpan(0x4800, 8)) == 0xfedcba9876543210, "MOVQ store differs");
            Execute(r, 0x0f, 0x77); Check(r.Cpu.FpuTagWord == 0xffff && r.Cpu.GetMmxRegister(reg) == 0xfedcba9876543210 && r.Cpu.FpuTop == 0, "EMMS changed retained raw bits");
        });
        foreach (byte[] prefix in new byte[][] { [0x66], [0xf2], [0xf3], [0x66,0xf3] }) test($"MMX historical ignored prefix {Convert.ToHexString(prefix)}", () =>
        {
            var r = Make(); Seed(r, 0, 0x80000000ffffffff); Execute(r, [..prefix,0x0f,0xef,0xc0]);
            Check(r.Cpu.GetMmxRegister(0) == 0 && r.Cpu.Eip == 0x1003 + prefix.Length, "Historical prefix altered MMX width");
        });
        foreach (X86CpuModel model in new[] { X86CpuModel.Intel486Dx, X86CpuModel.IntelP54cB5Subset }) foreach (byte opcode in opcodes.Concat(new byte[] { 0x77,0x6e,0x6f,0x7e,0x7f }))
            test($"MMX unsupported {model} {opcode:X2} raises UD", () =>
            {
                var r = Make(model); byte? fault = null; r.Cpu.InterruptEntered += v => fault = v;
                Execute(r, 0x0f, opcode, 0xc1); Check(fault == 6 && r.Cpu.FpuTagWord == 0xffff, "Old CPU accepted MMX");
            });
        foreach (uint cr0 in new uint[] { 4,8,12 }) foreach (byte opcode in new byte[] { 0x77,0x6f,0xef }) test($"MMX CR0 {cr0:X} opcode {opcode:X2} priority/purity", () =>
        {
            var r = Make(); Seed(r, 0, 0x123456789abcdef); var s = r.Cpu.SaveState(); s.Fields["Cr0"] = JsonSerializer.SerializeToElement(r.Cpu.Cr0 | cr0, StateValues.Json); r.Cpu.LoadState(s);
            byte? fault = null; r.Cpu.InterruptEntered += v => fault = v; Execute(r, 0x0f, opcode, 0xc1);
            Check(fault == ((cr0 & 4) != 0 ? 6 : 7) && r.Cpu.GetMmxRegister(0) == 0x123456789abcdef, "EM/TS priority or source purity differs");
        });
        foreach (byte opcode in new byte[] { 0x6f,0x7f,0xef }) test($"MMX full qword segment fault {opcode:X2} leaves data untouched", () =>
        {
            var r = Make(); Seed(r, 0, 0x102030405060708); r.Memory.Ram.AsSpan(0xfffc, 8).Fill(0x5a); byte? fault = null; r.Cpu.InterruptEntered += v => fault = v;
            Execute(r, 0x0f, opcode, 6, 0xfc, 0xff);
            Check(fault == 13 && r.Cpu.GetMmxRegister(0) == 0x102030405060708 && r.Memory.Ram.AsSpan(0xfffc, 8).IndexOfAnyExcept((byte)0x5a) < 0, "Partial qword access occurred");
        });
        test("MMX active raw x87 snapshot and EMMS continuation", () =>
        {
            var a = Make(); Seed(a, 0, 0x123456789abcdef); Seed(a, 7, 0xfedcba9876543210); StateBlock state = a.Cpu.SaveState();
            var b = Make(); a.Memory.Ram.CopyTo(b.Memory.Ram, 0); b.Cpu.LoadState(state);
            Execute(a, 0x0f, 0xfd, 0xc7); Execute(b, 0x0f, 0xfd, 0xc7);
            Check(JsonSerializer.Serialize(a.Cpu.SaveState(), StateValues.Json) == JsonSerializer.Serialize(b.Cpu.SaveState(), StateValues.Json), "Active MMX CPU snapshot differs");
            Execute(a, 0x0f, 0x77); Execute(b, 0x0f, 0x77); Execute(a, 0xd9, 0xe8); Execute(b, 0xd9, 0xe8);
            Check(a.Cpu.GetFpuRegister(0) == Float80Value.One && a.Cpu.FpuTagWord == b.Cpu.FpuTagWord, "EMMS did not permit ordinary x87 push");
        });
    }
}
