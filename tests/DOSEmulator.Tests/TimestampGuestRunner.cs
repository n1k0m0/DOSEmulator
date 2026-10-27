// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Own P54C TSC/MSR reset guests with a coordinated CPU/RAM/PIC/PIT/scheduler cut; deliberately not a complete Pentium PC.</summary>
internal static class TimestampGuestRunner
{
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { IgnoreReadOnlyProperties = false };
    private sealed class Output : IIoPortDevice
    {
        public readonly StringBuilder Text = new();
        /// <summary>Leaves the output-only diagnostic port undriven.</summary>
        public uint Read(ushort port, int width) => uint.MaxValue;
        /// <summary>Captures actual byte output from the own guest without touching a host endpoint.</summary>
        public void Write(ushort port, uint value, int width)
        { if (port != 0xe9 || width != 1) throw new InvalidDataException("Unexpected output."); Text.Append((char)(byte)value); }
    }

    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly MemoryBus Memory = new(2 * 1024 * 1024);
        public readonly Pic8259 Pic = new();
        public readonly Pit8254 Pit;
        public readonly X86Cpu Cpu;
        public readonly Output Output = new();
        public readonly Dictionary<string, IStateObject> Graph = new(StringComparer.Ordinal);
        public readonly Dictionary<object, string> Owners = new(ReferenceEqualityComparer.Instance);
        public readonly List<object> Ledger = new();
        public readonly uint Frequency;
        private readonly bool _msrGuest;
        private byte? _entered;

        /// <summary>Constructs a fixed known local component graph with registered timer operations and immutable own ROM.</summary>
        public Rig(byte[] program, uint frequency, bool msrGuest = false)
        {
            Frequency = frequency; _msrGuest = msrGuest;
            Pit = new(Clock, () => { Ledger.Add(new { kind = "pulse", now = Clock.Now }); Pic.SetLine(0, true); Pic.SetLine(0, false); });
            var ports = new IoPortBus(); ports.Map(0x20, 2, Pic); ports.Map(0xa0, 2, Pic); ports.Map(0x40, 4, Pit); ports.Map(0xe9, 1, Output);
            ports.Accessed += (port, value, width, write) => Ledger.Add(new { kind = "port", now = Clock.Now, port, value, width, write });
            Pic.InterruptAcknowledged += (irq, vector, spurious) => Ledger.Add(new { kind = "ack", now = Clock.Now, irq, vector, spurious });
            var rom = new byte[65536]; new byte[] { 0xea, 0, 0x10, 0, 0 }.CopyTo(rom, 0xfff0);
            var reset = new RomDevice(rom); Memory.Map(0xffff0000, 65536, reset, "Own TSC reset ROM"); Memory.Map(0xffef0000, 65536, reset, "Own masked reset ROM");
            program.CopyTo(Memory.Ram, 0x1000); Cpu = new(Memory, ports, X86CpuModel.IntelP54cB5Subset, Clock, frequency);
            Cpu.InterruptEntered += vector => { _entered = vector; if (_msrGuest) Ledger.Add(new { kind = "entry", now = Clock.Now, vector }); };
            Add("cpu", Cpu); Add("pic", Pic); Add("pit", Pit);
        }

        /// <summary>Enumerates explicit owned state children without delegate/object graph activation.</summary>
        private void Add(string name, IStateObject value)
        {
            Graph.Add(name, value); Owners.Add(value, name);
            foreach (var child in value.StateChildren) Add(name + "/" + child.Name, child.Value);
        }

        /// <summary>Captures the complete selected graph and the exact pending timer queue at an owner boundary.</summary>
        public Cut Save() => new(Graph.ToDictionary(p => p.Key, p => p.Value.SaveState()), Clock.SaveState(Owners));

        /// <summary>Advances actual guest instructions or the next timer deadline, recording inputs to independent clock reconstruction.</summary>
        public void Advance()
        {
            if (Cpu.CanAcceptMaskableInterrupt && Pic.HasPending && Pic.Acknowledge() is { } vector)
            { Cpu.EnterHardwareInterrupt(vector); return; }
            if (Cpu.Halted)
            {
                ulong target = Clock.NextEventTime ?? throw new InvalidDataException("Halted without a timer deadline.");
                Ledger.Add(new { kind = "idle", now = Clock.Now, target }); Clock.AdvanceTo(target); return;
            }
            ulong now = Clock.Now; uint ip = Cpu.PhysicalInstructionAddress;
            int prefix = 0; uint opcode = ip;
            while (Memory.ReadByte(opcode) is 0x66 or 0x67) { prefix++; opcode++; }
            byte operation = Memory.ReadByte(opcode) == 0x0f ? Memory.ReadByte(opcode + 1) : (byte)0;
            if (_msrGuest && Memory.ReadByte(ip) == 0xf0) operation = Memory.ReadByte(ip + 1) == 0x0f ? Memory.ReadByte(ip + 2) : (byte)0;
            bool rdtsc = operation == 0x31, transfer = operation is 0x30 or 0x31 or 0x32;
            uint beforeAx = Cpu.GetRegister(Register.Ax), beforeDx = Cpu.GetRegister(Register.Dx), beforeCx = Cpu.GetRegister(Register.Cx);
            _entered = null; uint cycles = Cpu.Step();
            if (_msrGuest)
            {
                uint sp = Cpu.GetRegister(Register.Sp); bool fault = _entered is 6 or 13;
                Ledger.Add(new { kind = "step", now, ip, cycles, operation, prefix, beforeAx, beforeDx, beforeCx, entered = _entered,
                    eax = transfer ? Cpu.GetRegister(Register.Ax) : 0, edx = transfer ? Cpu.GetRegister(Register.Dx) : 0,
                    ecx = transfer ? Cpu.GetRegister(Register.Cx) : 0, flags = transfer ? Cpu.Eflags : 0,
                    frameSp = fault ? sp : 0, frameIp = fault ? Memory.ReadWord(sp) : 0, frameCs = fault ? Memory.ReadWord(sp + 2) : 0,
                    frameFlags = fault ? Memory.ReadWord(sp + 4) : 0, afterIp = Cpu.Eip });
            }
            else Ledger.Add(new { kind = "step", now, ip, cycles, rdtsc, prefix, eax = rdtsc ? Cpu.GetRegister(Register.Ax) : 0,
                edx = rdtsc ? Cpu.GetRegister(Register.Dx) : 0, flags = rdtsc ? Cpu.Eflags : 0 });
            Clock.AdvanceCycles(cycles, Frequency);
            if (Cpu.TripleFaulted) throw new InvalidDataException("Own guest triple faulted.");
        }
    }

    private sealed record Cut(Dictionary<string, StateBlock> Components, SchedulerState Scheduler);
    /// <summary>Encodes only explicitly declared component state and scheduler records.</summary>
    private static string Encode(Cut state) => JsonSerializer.Serialize(state, Json);
    /// <summary>Hashes exact bytes for cross-run and independent evidence checks.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Serializes a real halted boundary, restores a prepared known graph, and advances both with identical owner operations.</summary>
    public static int Run(string configPath)
    {
        using var config = JsonDocument.Parse(File.ReadAllBytes(configPath)); var input = config.RootElement;
        string programPath = Path.GetFullPath(input.GetProperty("program").GetString()!);
        string outputPath = Path.GetFullPath(input.GetProperty("output").GetString()!); uint frequency = input.GetProperty("frequency").GetUInt32();
        if (programPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) || outputPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Excluded personal media.");
        if (Directory.Exists(outputPath) || File.Exists(outputPath)) throw new IOException("Evidence target already exists.");
        if (frequency is not (33_000_000 or 90_000_000)) throw new InvalidDataException("Unexpected virtual test clock.");
        byte[] program = File.ReadAllBytes(programPath); if (program.Length is < 64 or > 8192) throw new InvalidDataException("Own program size differs.");
        bool msrGuest = input.TryGetProperty("msr", out var msr) && msr.GetBoolean();
        Directory.CreateDirectory(outputPath); var a = new Rig(program, frequency, msrGuest); a.Clock.AdvanceTo(50_000_000_000);
        int warmup = 0;
        while (!a.Cpu.Halted && warmup++ < 10000) a.Advance();
        if (!a.Cpu.Halted || a.Pit.InterruptPulses != 0 || !a.Cpu.CanAcceptMaskableInterrupt) throw new InvalidDataException("Missing first active-timer cut.");
        string saved = Encode(a.Save()); File.WriteAllText(Path.Combine(outputPath, "cut.json"), saved);
        File.WriteAllBytes(Path.Combine(outputPath, "ram-cut.bin"), a.Memory.Ram);
        var b = new Rig(program, frequency, msrGuest); File.ReadAllBytes(Path.Combine(outputPath, "ram-cut.bin")).CopyTo(b.Memory.Ram, 0);
        Cut cut = JsonSerializer.Deserialize<Cut>(File.ReadAllBytes(Path.Combine(outputPath, "cut.json")), Json)!;
        if (!cut.Components.Keys.Order().SequenceEqual(b.Graph.Keys.Order())) throw new InvalidDataException("Component topology differs.");
        foreach (var field in cut.Components) b.Graph[field.Key].LoadState(field.Value);
        b.Clock.LoadState(cut.Scheduler, b.Graph);
        if (saved != Encode(b.Save())) throw new InvalidDataException("Selected graph failed exact roundtrip.");
        int ledgerCut = a.Ledger.Count, comparisons = 0;
        while (a.Memory.Ram[0x5140] == 0 || !a.Cpu.Halted)
        {
            if (++comparisons > 10000) throw new InvalidDataException("Own guest failed bounded completion.");
            a.Advance(); b.Advance();
            if (Encode(a.Save()) != Encode(b.Save()) || !a.Memory.Ram.SequenceEqual(b.Memory.Ram)) throw new InvalidDataException("Coordinated continuation differs.");
        }
        if (!b.Cpu.Halted || a.Pit.InterruptPulses != 4 || a.Output.Text.ToString() != b.Output.Text.ToString()) throw new InvalidDataException("Wrong completion.");
        string futureA = JsonSerializer.Serialize(a.Ledger.Skip(ledgerCut), Json), futureB = JsonSerializer.Serialize(b.Ledger, Json);
        if (futureA != futureB) throw new InvalidDataException("Future timer/port/CPU publications differ.");
        File.WriteAllText(Path.Combine(outputPath, "ledger.json"), JsonSerializer.Serialize(a.Ledger, Json));
        File.WriteAllText(Path.Combine(outputPath, "future-original.json"), futureA); File.WriteAllText(Path.Combine(outputPath, "future-restored.json"), futureB);
        File.WriteAllText(Path.Combine(outputPath, "final-original.json"), Encode(a.Save())); File.WriteAllText(Path.Combine(outputPath, "final-restored.json"), Encode(b.Save()));
        File.WriteAllBytes(Path.Combine(outputPath, "ram-original.bin"), a.Memory.Ram); File.WriteAllBytes(Path.Combine(outputPath, "ram-restored.bin"), b.Memory.Ram);
        var result = new { passed = true, frequency, msrGuest, initialTime = 50_000_000_000UL, fullPcSnapshot = false, dosBoot = false,
            selectedComponents = a.Graph.Keys.ToArray(), warmup, comparisons, ledgerCut, pulses = a.Pit.InterruptPulses, output = a.Output.Text.ToString(),
            programSha256 = Hash(program), ramSha256 = Hash(a.Memory.Ram), stateSha256 = Hash(Encoding.UTF8.GetBytes(Encode(a.Save()))),
            futureSha256 = Hash(Encoding.UTF8.GetBytes(futureA)), finalTime = a.Clock.Now };
        File.WriteAllText(Path.Combine(outputPath, "result.json"), JsonSerializer.Serialize(result, Json)); Console.WriteLine(JsonSerializer.Serialize(result, Json)); return 0;
    }
}
