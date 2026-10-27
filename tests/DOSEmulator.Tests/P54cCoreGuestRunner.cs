// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Runs an own reset-ROM guest and CPU-only continuation; deliberately not a full-PC snapshot or DOS boot.</summary>
internal static class P54cCoreGuestRunner
{
    private sealed class Output : IIoPortDevice
    {
        public readonly StringBuilder Text = new();
        /// <summary>Leaves this output-only diagnostic device undriven on reads.</summary>
        public uint Read(ushort port, int width) => uint.MaxValue;
        /// <summary>Records only actual byte OUT instructions from the own guest.</summary>
        public void Write(ushort port, uint value, int width)
        { if (port != 0xe9 || width != 1) throw new InvalidDataException("Unexpected guest output width/port."); Text.Append((char)(byte)value); }
    }

    /// <summary>Creates the same immutable own reset ROM and maps no external or host device.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, Output Output) Create(byte[] program)
    {
        var memory = new MemoryBus(2 * 1024 * 1024); var ports = new IoPortBus(); var output = new Output();
        ports.Map(0xe9, 1, output); var rom = new byte[65536];
        new byte[] { 0xea, 0, 0x10, 0, 0 }.CopyTo(rom, 0xfff0);
        var reset = new RomDevice(rom); memory.Map(0xffff0000, 65536, reset, "Own P54C reset ROM");
        memory.Map(0xffef0000, 65536, reset, "Own A20-masked P54C reset ROM");
        program.CopyTo(memory.Ram, 0x1000);
        return (new X86Cpu(memory, ports, X86CpuModel.IntelP54cB5Subset), memory, output);
    }

    /// <summary>Encodes the complete explicit CPU block, retaining raw 80-bit FPU and restart metadata.</summary>
    private static string State(X86Cpu cpu) => JsonSerializer.Serialize(cpu.SaveState(), StateValues.Json);

    /// <summary>Runs actual guest instructions through reset, serializes a component cut, and independently advances both sides.</summary>
    public static int Run(string configPath)
    {
        using var config = JsonDocument.Parse(File.ReadAllBytes(configPath)); var input = config.RootElement;
        string programPath = Path.GetFullPath(input.GetProperty("program").GetString()!);
        string outputPath = Path.GetFullPath(input.GetProperty("output").GetString()!);
        if (programPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) || outputPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Personal play paths are excluded.");
        if (Directory.Exists(outputPath) || File.Exists(outputPath)) throw new IOException("Guest output must be new.");
        byte[] program = File.ReadAllBytes(programPath);
        if (program.Length is < 64 or > 24576) throw new InvalidDataException("Unexpected own guest length.");
        Directory.CreateDirectory(outputPath);
        var (a, ma, oa) = Create(program);
        int before = 0;
        while (!a.Halted && before++ < 200) a.Step();
        if (a.Halted || a.TripleFaulted) throw new InvalidDataException("Guest stopped before its component continuation cut.");
        string saved = State(a);
        File.WriteAllText(Path.Combine(outputPath, "cpu-cut.json"), saved);
        File.WriteAllBytes(Path.Combine(outputPath, "ram-cut.bin"), ma.Ram);
        var (b, mb, ob) = Create(program); ma.Ram.CopyTo(mb.Ram, 0);
        b.LoadState(JsonSerializer.Deserialize<StateBlock>(File.ReadAllBytes(Path.Combine(outputPath, "cpu-cut.json")), StateValues.Json)!);
        if (saved != State(b)) throw new InvalidDataException("CPU cut did not restore exactly.");
        int comparisons = 0, ramComparisons = 0;
        while (!a.Halted && comparisons < 10000)
        {
            a.Step(); b.Step(); comparisons++;
            if (State(a) != State(b)) throw new InvalidDataException("Raw CPU continuation differs.");
            if (comparisons % 32 == 0) { if (!ma.Ram.SequenceEqual(mb.Ram)) throw new InvalidDataException("Guest RAM differs."); ramComparisons++; }
        }
        if (!a.Halted || !b.Halted || a.TripleFaulted || b.TripleFaulted || !ma.Ram.SequenceEqual(mb.Ram) || oa.Text.ToString() != ob.Text.ToString())
            throw new InvalidDataException("Guest continuation did not terminate identically.");
        File.WriteAllBytes(Path.Combine(outputPath, "ram-original.bin"), ma.Ram);
        File.WriteAllBytes(Path.Combine(outputPath, "ram-restored.bin"), mb.Ram);
        File.WriteAllText(Path.Combine(outputPath, "cpu-original.json"), State(a));
        File.WriteAllText(Path.Combine(outputPath, "cpu-restored.json"), State(b));
        File.WriteAllText(Path.Combine(outputPath, "e9.txt"), oa.Text.ToString());
        var result = new { passed = true, model = a.Model.ToString(), cpuOnly = true, fullPcSnapshot = false, dosBoot = false,
            cutInstructions = 200, comparisons, ramComparisons, finalInstructions = a.Instructions, output = oa.Text.ToString(),
            programSha256 = Convert.ToHexString(SHA256.HashData(program)),
            ramSha256 = Convert.ToHexString(SHA256.HashData(ma.Ram)) };
        File.WriteAllText(Path.Combine(outputPath, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(StateValues.Json) { IgnoreReadOnlyProperties = false }));
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(StateValues.Json) { IgnoreReadOnlyProperties = false }));
        return 0;
    }
}
