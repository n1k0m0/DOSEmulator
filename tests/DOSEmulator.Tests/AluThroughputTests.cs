// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

internal static class AluThroughputTests
{
    /// <summary>Computes flags from signed mathematical bounds, nibble arithmetic and a separate eight-bit parity count.</summary>
    private static (uint Value, uint Flags) Expected(uint a, uint b, int op, int bits, uint seed)
    {
        uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1, sign = 1u << (bits - 1);
        int carry = (seed & X86Cpu.Carry) != 0 && op is 2 or 3 ? 1 : 0;
        bool add = op is 0 or 2, subtract = op is 3 or 5 or 7;
        long mathematical = add ? (long)a + b + carry : subtract ? (long)a - b - carry : 0;
        uint result = add || subtract ? (uint)mathematical & mask : op switch { 1 => a | b, 4 => a & b, _ => a ^ b };
        uint flags = seed & ~(X86Cpu.Carry | X86Cpu.Parity | X86Cpu.Auxiliary | X86Cpu.Zero | X86Cpu.Sign | X86Cpu.Overflow);
        if (add || subtract)
        {
            long signedA = (a & sign) == 0 ? a : (long)a - (1L << bits);
            long signedB = (b & sign) == 0 ? b : (long)b - (1L << bits);
            long signedResult = add ? signedA + signedB + carry : signedA - signedB - carry;
            if (mathematical < 0 || mathematical > mask) flags |= X86Cpu.Carry;
            if (signedResult < -(1L << (bits - 1)) || signedResult > (1L << (bits - 1)) - 1) flags |= X86Cpu.Overflow;
            if (add ? (a & 15) + (b & 15) + carry > 15 : (a & 15) < (b & 15) + carry) flags |= X86Cpu.Auxiliary;
        }
        if (result == 0) flags |= X86Cpu.Zero;
        if ((result & sign) != 0) flags |= X86Cpu.Sign;
        int ones = 0;
        for (int bit = 0; bit < 8; bit++) ones += (int)(result >> bit & 1);
        if ((ones & 1) == 0) flags |= X86Cpu.Parity;
        return (result, flags | 2);
    }
    /// <summary>Registers exhaustive byte and deterministic wider arithmetic/logical decoder cases with both incoming carries.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int width in new[] { 1, 2, 4 })
        foreach (int op in Enumerable.Range(0, 8))
            test($"batched arithmetic flags retain independent mathematical results, width={width}, op={op}", () =>
            {
                var m = new MemoryBus(2 * 1048576); var c = new X86Cpu(m, new()); c.SetSegment(Segment.Cs, 0);
                int address = 0x1000, immediate = address + 1;
                if (width == 4) { m.Ram[address++] = 0x66; immediate++; }
                m.Ram[address] = (byte)(op * 8 + 4 + (width == 1 ? 0 : 1));
                uint mask = width == 4 ? uint.MaxValue : (1u << (width * 8)) - 1, random = 0x12345678;
                uint[] edges = [0, 1, 7, 15, 16, mask / 2, mask / 2 + 1, mask - 1, mask];
                int count = width == 1 ? 131072 : 8192;
                for (int i = 0; i < count; i++)
                {
                    uint a, b;
                    if (width == 1) { a = (uint)(i >> 9) & 255; b = (uint)(i >> 1) & 255; }
                    else if (i < edges.Length * edges.Length * 2) { a = edges[i / 2 / edges.Length]; b = edges[i / 2 % edges.Length]; }
                    else { random = unchecked(random * 1664525 + 1013904223); a = random & mask; random = unchecked(random * 1664525 + 1013904223); b = random & mask; }
                    uint seed = 2 | X86Cpu.InterruptEnable | X86Cpu.Direction | 0x40000 | X86Cpu.Overflow | X86Cpu.Sign | X86Cpu.Zero | X86Cpu.Auxiliary | X86Cpu.Parity | (uint)(i & 1);
                    var expected = Expected(a, b, op, width * 8, seed);
                    uint initial = 0xa55a0000 & ~mask | a;
                    c.SetRegister(Register.Ax, initial); c.Eflags = seed; c.Eip = 0x1000;
                    for (int k = 0; k < width; k++) m.Ram[immediate + k] = (byte)(b >> (k * 8));
                    c.Step(); uint value = op == 7 ? initial : (initial & ~mask) | expected.Value;
                    if (c.GetRegister(Register.Ax) != value || c.Eflags != expected.Flags)
                        throw new Exception($"ALU width {width} op {op} input {a:X}/{b:X}/{i & 1}: value {c.GetRegister(Register.Ax):X}/{value:X}, flags {c.Eflags:X}/{expected.Flags:X}");
                }
            });
    }
}
