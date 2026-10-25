using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87NumericTests
{
    private static void Check(bool value, string message = "x87 numerical invariant failed") { if (!value) throw new Exception(message); }
    private static byte[] Mem(byte opcode, int group, uint address = 0x30000) => [opcode, (byte)(group * 8 + 5), .. BitConverter.GetBytes(address)];
    private static void At(X86Cpu c, MemoryBus m, byte[] code) { code.CopyTo(m.Ram, 0x2200); c.Eip = 0x2200; c.Step(); }
    private static void Raw(MemoryBus m, uint at, Float80Value value)
    { m.WriteDword(at, (uint)value.Significand); m.WriteDword(at + 4, (uint)(value.Significand >> 32)); m.WriteWord(at + 8, value.SignExponent); }
    private static void Load(X86Cpu c, MemoryBus m, Float80Value value, uint address = 0x30000) { Raw(m, address, value); At(c, m, Mem(0xdb, 5, address)); }
    private static Float80Value Parse(JsonElement v) => new(Convert.ToUInt64(v.GetProperty("significand").GetString(), 16), Convert.ToUInt16(v.GetProperty("exponent").GetString(), 16));

    public static int VerifyNativeVectors(string vectorPath, string nativePath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(vectorPath));
        using var oracle = JsonDocument.Parse(File.ReadAllText(nativePath));
        var vectors = source.RootElement.EnumerateArray().ToArray();
        var expected = oracle.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Check(vectors.Length == expected.Length && oracle.RootElement.GetProperty("referenceOnly").GetBoolean());
        var (cpu, memory) = ProtectedModeTests.Rig([0x90]);
        var failures = new List<object>();
        int checkedCases = 0;
        for (int i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i]; var e = expected[i]; string id = v.GetProperty("id").GetString()!;
            Check(id == e.GetProperty("id").GetString());
            var a = Parse(v.GetProperty("a")); var b = Parse(v.GetProperty("b"));
            ushort control = v.GetProperty("control").GetUInt16(); int kind = v.GetProperty("kind").GetInt32(), op = v.GetProperty("operation").GetInt32();
            ulong bits = Convert.ToUInt64(v.GetProperty("bits").GetString(), 16);
            ushort status = e.GetProperty("status").GetUInt16(); int top = status >> 11 & 7;
            var nativeValue = Parse(e.GetProperty("registers")[0]);
            try
            {
                if (kind is 0 or 1 or 2 or 3 or 9 or 10 or >= 11)
                {
                    var operation = (Float80Operation)(op == 4 ? 1 : op == 5 ? 3 : op);
                    Float80Value memoryInteger = Float80Math.FromInteger(kind == 14 ? (short)bits : (int)bits);
                    Float80Result actual = kind switch
                    {
                        0 => Float80Math.Calculate(a, b, (Float80Operation)op, control),
                        1 or 2 => Float80Math.LoadBinary(bits, kind == 1 ? 32 : 64, control),
                        11 or 12 => Float80Math.CalculateBinaryMemory(a, bits, kind == 11 ? 32 : 64, operation, control, op >= 4),
                        13 or 14 => op >= 4 ? Float80Math.Calculate(memoryInteger, a, operation, control) : Float80Math.Calculate(a, memoryInteger, operation, control),
                        _ => new(Float80Math.FromInteger(kind == 10 ? (short)bits : kind == 9 ? (int)bits : (long)bits))
                    };
                    Check(actual.Exceptions == (status & 0x3f) && actual.RoundedUp == ((status & 0x200) != 0), $"math flags {actual.Exceptions:X}/{actual.RoundedUp} versus native {status:X}");
                    if (kind < 11) Check(actual.Commit == (top == 7), "math commit differs");
                    if (actual.Commit) Check(actual.Value == nativeValue, $"math {actual.Value} versus native {nativeValue}");
                }
                else
                {
                    int width = kind is 4 or 7 ? 32 : kind == 8 ? 16 : 64;
                    var actual = kind is 4 or 5 ? Float80Math.StoreBinary(a, width, control) : Float80Math.StoreInteger(a, width, control);
                    Check(actual.Exceptions == (status & 0x3f) && actual.RoundedUp == ((status & 0x200) != 0), $"store flags {actual.Exceptions:X}/{actual.RoundedUp} versus native {status:X}");
                    Check(actual.Commit == (top == 0), "store commit differs");
                    if (actual.Commit)
                    {
                        byte[] encoded = BitConverter.GetBytes(actual.Bits), native = Convert.FromHexString(e.GetProperty("memory").GetString()!);
                        Check(encoded.AsSpan(0, width / 8).SequenceEqual(native.AsSpan(0, width / 8)), "store result differs");
                    }
                }
                // Independently execute the guest decoder/memory/stack path for every native vector.
                At(cpu, memory, [0xdb, 0xe3]); memory.WriteWord(0x30200, control); At(cpu, memory, Mem(0xd9, 5, 0x30200));
                memory.Ram.AsSpan(0x30400, 8).Fill(0xa5); memory.WriteDword(0x30300, (uint)bits); memory.WriteDword(0x30304, (uint)(bits >> 32));
                if (kind == 0)
                {
                    Load(cpu, memory, a); Load(cpu, memory, b, 0x30010);
                    At(cpu, memory, [0xde, (byte)(op switch { 0 => 0xc1, 1 => 0xe9, 2 => 0xc9, _ => 0xf9 })]);
                }
                else if (kind is 1 or 2 or 3 or 9 or 10)
                    At(cpu, memory, Mem(kind switch { 1 => 0xd9, 2 => 0xdd, 9 => 0xdb, _ => 0xdf }, kind == 3 ? 5 : 0, 0x30300));
                else if (kind >= 11)
                {
                    Load(cpu, memory, a);
                    At(cpu, memory, Mem(kind switch { 11 => 0xd8, 12 => 0xdc, 13 => 0xda, _ => 0xde }, op switch { 0 => 0, 1 => 4, 2 => 1, 3 => 6, 4 => 5, _ => 7 }, 0x30300));
                }
                else
                {
                    Load(cpu, memory, a);
                    At(cpu, memory, Mem(kind switch { 4 => 0xd9, 5 => 0xdd, 7 => 0xdb, _ => 0xdf }, kind == 6 ? 7 : 3, 0x30400));
                }
                Check(cpu.FpuStatusWord == status && cpu.FpuControlWord == control && cpu.FpuTagWord == e.GetProperty("tags").GetUInt16(),
                    $"CPU SW/TW {cpu.FpuStatusWord:X}/{cpu.FpuTagWord:X} versus {status:X}/{e.GetProperty("tags").GetUInt16():X}");
                if (!cpu.IsFpuRegisterEmpty(0)) Check(cpu.GetFpuRegister(0) == nativeValue, "CPU raw ST0 differs");
                if (kind is >= 4 and <= 8) Check(Convert.ToHexString(memory.Ram.AsSpan(0x30400, 8)) == e.GetProperty("memory").GetString(), "CPU memory differs");
                checkedCases++;
            }
            catch (Exception ex) { failures.Add(new { id, error = ex.Message }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed = failures.Count == 0, vectors = vectors.Length, checkedCases, failed = failures.Count, failures }, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }

    public static void AddTo(Action<string, Action> test)
    {
        test("x87 tininess before denormalization retains UE when final rounding reaches minimum-normal", () =>
        {
            var r = Float80Math.Calculate(new(0x8000000000000000, 1), new(ulong.MaxValue, 0x3ffe), Float80Operation.Multiply, 0x37f);
            Check(r.Value == new Float80Value(0x8000000000000000, 1) && r.Exceptions == 0x30 && r.RoundedUp);
            var reduced = Float80Math.Calculate(new(0x8000000000000000, 1), new(ulong.MaxValue, 0x3ffe), Float80Operation.Multiply, 0x27f);
            Check(reduced.Value == r.Value && reduced.Exceptions == 0x20 && reduced.RoundedUp);
        });
        foreach (int pc in new[] { 0, 2, 3 }) foreach (int rc in Enumerable.Range(0, 4)) foreach (bool negative in new[] { false, true })
            test($"80-bit tie rounding obeys PC={pc} RC={rc} negative={negative}", () =>
            {
                int p = pc == 0 ? 24 : pc == 2 ? 53 : 64;
                var a = Float80Value.One with { SignExponent = (ushort)(negative ? 0xbfff : 0x3fff) };
                var half = new Float80Value(0x8000000000000000, (ushort)(0x3fff - p | (negative ? 0x8000 : 0)));
                var r = Float80Math.Calculate(a, half, Float80Operation.Add, (ushort)(0x7f | pc << 8 | rc << 10));
                bool up = rc == 1 && negative || rc == 2 && !negative;
                Check(r.Value == new Float80Value(0x8000000000000000 + (up ? 1ul << (64 - p) : 0), a.SignExponent));
                Check(r.Exceptions == 32 && r.RoundedUp == up && r.Commit);
            });
        foreach (long integer in new long[] { 0, 1, -1, 32767, -32768, int.MaxValue, int.MinValue, long.MaxValue, long.MinValue })
            test($"FILD/FISTP exact signed 64-bit roundtrip {integer}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x90]); m.WriteDword(0x30000, (uint)integer); m.WriteDword(0x30004, (uint)(integer >> 32));
                At(c, m, Mem(0xdf, 5)); Check(c.GetFpuRegister(0) == Float80Math.FromInteger(integer)); At(c, m, Mem(0xdf, 7, 0x30100));
                Check(m.ReadDword(0x30100) == (uint)integer && m.ReadDword(0x30104) == (uint)(integer >> 32) && c.FpuStatusWord == 0 && c.FpuTagWord == 0xffff);
            });
        foreach (byte opcode in new byte[] { 0xd8, 0xdc, 0xde }) foreach (int group in new[] { 0, 1, 4, 5, 6, 7 }) foreach (int slot in new[] { 0, 1, 7 })
            test($"x87 register arithmetic {opcode:X2} group={group} ST({slot}) snapshots aliases and pop destination", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x90]); for (int i = 8; i >= 1; i--) Load(c, m, Float80Math.FromInteger(i));
                int destination = opcode == 0xd8 ? 0 : slot;
                long left = destination + 1, right = opcode == 0xd8 ? slot + 1 : 1;
                bool reverse = opcode == 0xd8 ? group is 5 or 7 : group is 4 or 6;
                var operation = group switch { 0 => Float80Operation.Add, 1 => Float80Operation.Multiply, 4 or 5 => Float80Operation.Subtract, _ => Float80Operation.Divide };
                var expected = Float80Math.Calculate(Float80Math.FromInteger(reverse ? right : left), Float80Math.FromInteger(reverse ? left : right), operation, 0x37f);
                At(c, m, [opcode, (byte)(0xc0 + group * 8 + slot)]);
                Check(c.FpuTop == (opcode == 0xde ? 1 : 0));
                if (opcode != 0xde || slot != 0) Check(c.GetFpuRegister((destination - c.FpuTop + 8) & 7) == expected.Value);
                Check((c.FpuStatusWord & 0x3f) == expected.Exceptions);
            });
        foreach (bool store in new[] { false, true }) foreach (int size in new[] { 2, 4, 8 })
            test($"numeric {(store ? "store" : "load")} width={size} PF preflight preserves FPU and first byte", () =>
            {
                var (c, m) = PagingTests.Create([0x90]); Load(c, m, Float80Value.One);
                PagingTests.Pte(m, 0x30000, 0x40000); PagingTests.Pte(m, 0x31000, 0, 0); m.WriteByte(0x40fff, 0xa5);
                byte opcode = size == 2 ? (byte)0xdf : size == 4 ? (byte)0xd9 : (byte)0xdd;
                Mem(opcode, store ? 3 : 0, 0x30fff).CopyTo(m.Ram, 0x2000); c.Eip = 0x2000; Fault(c, m, 14, store ? 2u : 0);
                Check(c.FpuTop == 7 && c.GetFpuRegister(0) == Float80Value.One && m.ReadByte(0x40fff) == 0xa5 && (c.FpuStatusWord & 0x3f) == 0);
            });
        foreach (byte opcode in new byte[] { 0xd8, 0xda, 0xdc, 0xde })
            test($"numeric memory arithmetic {opcode:X2} missing page does not consume ST0", () =>
            {
                var (c, m) = PagingTests.Create([0x90]); Load(c, m, Float80Value.One, 0x32000); PagingTests.Pte(m, 0x30000, 0, 0);
                Mem(opcode, 0).CopyTo(m.Ram, 0x2000); c.Eip = 0x2000; Fault(c, m, 14, 0); Check(c.FpuTop == 7 && c.GetFpuRegister(0) == Float80Value.One);
            });
        foreach (ushort mask in new ushort[] { 1, 4, 8, 16, 32 })
            test($"unmasked numerical exception {mask:X} defers actual MF until next waiting instruction", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x90]); c.SetRegister(Register.Ax, c.Cr0 | 0x20); At(c, m, [0x0f, 0x22, 0xc0]);
                m.WriteWord(0x30200, (ushort)(0x37f & ~mask)); At(c, m, Mem(0xd9, 5, 0x30200));
                var a = mask == 8 ? new Float80Value(ulong.MaxValue, 0x7ffe) : mask == 16 ? new Float80Value(0x8000000000000000, 1) : Float80Value.One;
                var b = mask is 1 or 4 ? Float80Value.Zero : Float80Math.FromInteger(mask == 8 ? 2 : 3);
                if (mask == 1) a = Float80Value.Zero;
                Load(c, m, a); Load(c, m, b, 0x30010); At(c, m, [0xde, mask == 8 ? (byte)0xc9 : (byte)0xf9]);
                Check((c.FpuStatusWord & mask) != 0 && (c.FpuStatusWord & 0x8080) == 0x8080);
                new byte[] { 0x9b }.CopyTo(m.Ram, 0x2000); c.Eip = 0x2000; Fault(c, m, 16, null);
                At(c, m, [0xdb, 0xe2]); Check((c.FpuStatusWord & 0x80ff) == 0);
            });
    }
}
