// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Complete differential checks for newly admitted register shifts, rotates and signed multiplication.</summary>
internal static class DecodedMathBatchTests
{
    /// <summary>Requires an actual retirement, denial or complete state invariant.</summary>
    private static void Check(bool value, string detail)
    { if (!value) throw new Exception(detail); }

    /// <summary>Reuses private CPU/RAM fixtures while changing every current source operand and code encoding.</summary>
    private sealed class Pair
    {
        private readonly DecodedBatchTests.Rig _scalar, _decoded;
        private readonly StateBlock _left, _right;
        private readonly uint _address;
        private readonly Random _random;
        public readonly bool Wide;

        /// <summary>Builds both CPUs through genuine mode/paging/V86 instructions, without instruction observers.</summary>
        public Pair(int mode, X86CpuModel model)
        {
            _scalar = DecodedBatchTests.Make(mode, [], model); _decoded = DecodedBatchTests.Make(mode, [], model);
            _left = _scalar.Cpu.SaveState(); _right = _decoded.Cpu.SaveState();
            _address = _scalar.Cpu.LinearInstructionAddress; Wide = mode is 2 or 4;
            _random = new Random(0x51c1 + mode + (int)model * 113);
        }

        /// <summary>Checks all CPU fields and RAM against the exact returned number of ordinary scalar instructions.</summary>
        public void Run(byte[] code, int cl = 1, int maximum = 64, int expected = 3, ulong? deadline = null,
            Action<uint, byte>? observer = null)
        {
            var a = _scalar.Cpu; var b = _decoded.Cpu;
            a.LoadState(_left); b.LoadState(_right);
            for (int r = 0; r < 8; r++)
            { uint value = (uint)_random.NextInt64(0, 1L << 32); a.SetRegister((Register)r, value); b.SetRegister((Register)r, value); }
            a.SetRegister(Register.Cx, (a.GetRegister(Register.Cx) & 0xffffff00u) | (uint)cl);
            b.SetRegister(Register.Cx, a.GetRegister(Register.Cx));
            a.Eflags = b.Eflags = (a.Eflags & ~0x8d5u) | ((uint)_random.Next(4096) & 0x8d5u);
            byte[] program = [..code, 0x90, 0x90, 0xf4];
            program.CopyTo(_scalar.Memory.Ram, (int)_address); program.CopyTo(_decoded.Memory.Ram, (int)_address);
            var clock = new DeviceScheduler(); var owners = new Dictionary<object, string>();
            string before = JsonSerializer.Serialize(clock.SaveState(owners), StateValues.Json);
            b.InstructionStarting += observer;
            int count;
            try { count = deadline is ulong end ? b.TryRunDecodedBatch(maximum, clock, 100000000, end) : b.TryRunDecodedBatch(maximum); }
            finally { b.InstructionStarting -= observer; }
            Check(count == expected, $"Expected {expected} actual retirements, got {count}: {Convert.ToHexString(code)}");
            for (int i = 0; i < count; i++) Check(a.Step() == 4, "Scalar clocks changed.");
            DecodedBatchTests.Equal(_scalar, _decoded);
            Check(before == JsonSerializer.Serialize(clock.SaveState(owners), StateValues.Json), "Admission advanced or changed scheduler state.");
        }
    }

    /// <summary>Registers independent modes/models and bounded vector matrices, preserving reserved/undefined flag policy.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var modelValue in new[] { X86CpuModel.Intel486Dx, X86CpuModel.IntelP54cB5Subset })
        for (int modeValue = 0; modeValue < 7; modeValue++)
        {
            var model = modelValue; int mode = modeValue;
            test($"DecodedMath shifts/rotates full state {model} mode{mode}", () =>
            {
                var pair = new Pair(mode, model);
                foreach (byte opcode in new byte[] { 0xc0, 0xc1, 0xd0, 0xd1, 0xd2, 0xd3 })
                for (int operation = 0; operation < 8; operation++)
                foreach (int count in new[] { 0,1,7,8,9,15,16,17,31,32,33,255 })
                {
                    int register = (operation + count) % 8;
                    byte modrm = (byte)(0xc0 | operation << 3 | register);
                    pair.Run(opcode is 0xc0 or 0xc1 ? [opcode,modrm,(byte)count] : [opcode,modrm], count);
                }
            });
            test($"DecodedMath signed IMUL full state {model} mode{mode}", () =>
            {
                var pair = new Pair(mode, model); var random = new Random(0x6941 + mode);
                for (int vector = 0; vector < 300; vector++)
                {
                    int destination = vector % 8, source = (vector / 8) % 8;
                    byte modrm = (byte)(0xc0 | destination << 3 | source);
                    uint immediate = (vector % 5) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u,
                        3 => 0x7fffffffu, _ => (uint)random.NextInt64(0, 1L << 32) };
                    byte[] operand = pair.Wide ? BitConverter.GetBytes(immediate) : BitConverter.GetBytes((ushort)immediate);
                    byte[] code = (vector % 3) switch { 0 => [0x69,modrm,..operand], 1 => [0x6b,modrm,(byte)immediate], _ => [0x0f,0xaf,modrm] };
                    pair.Run(code, vector & 255);
                }
            });
            test($"DecodedMath deadlines and current code/CL {model} mode{mode}", () =>
            {
                var pair = new Pair(mode, model);
                foreach (int budget in new[] { 0,1,2,3,7 })
                {
                    int expected = budget < 2 ? 0 : Math.Min(3, budget);
                    pair.Run([0xd3,0xe1], 33, expected: expected, deadline: (ulong)budget * 40);
                    pair.Run([0xc1,0xe8,255], expected: expected, deadline: (ulong)budget * 40);
                }
                pair.Run([0xc0,0xec,1]); pair.Run([0xc0,0xec,0]); pair.Run([0xc0,0xec,255]);
                pair.Run([0xd3,0xe1], 0); pair.Run([0xd3,0xe1], 255);
            });
            test($"DecodedMath current size prefix and remaining memory LOCK REP observer guards {model} mode{mode}", () =>
            {
                var pair = new Pair(mode, model);
                foreach (byte[] code in new byte[][] { [0xc1,0x20,1], [0xd3,0x20], [0x69,0x00,1,0,0,0],
                    [0x6b,0x00,255], [0x0f,0xaf,0x00], [0xf0,0xd1,0xe0], [0xf3,0xd1,0xe0] }) pair.Run(code, expected: 0);
                pair.Run([0x66,0xd1,0xe0]);
                bool observed = false; pair.Run([0xc1,0xe0,1], expected: 0, observer: (_,_) => observed = true);
                Check(!observed, "An instruction observer was bypassed by batching.");
            });
        }
    }
}
