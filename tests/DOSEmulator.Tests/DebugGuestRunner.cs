// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Own P54C I/O-debug guest on a fixed selected component graph, without a product PC or DOS claim.</summary>
internal static class DebugGuestRunner
{
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { IgnoreReadOnlyProperties = false };
    private sealed record View(uint Ip, uint[] Registers, ushort Cs, ushort Ss, int Cpl, uint Flags, uint Dr6, uint Dr7, uint Cr4);
    private sealed record Frame(uint Physical, string Bytes);
    private sealed record Cut(Dictionary<string, StateBlock> Components, SchedulerState Scheduler);
    private sealed class FixedPort : IIoPortDevice
    {
        /// <summary>Returns deterministic virtual data without a host endpoint or mutable peer state.</summary>
        public uint Read(ushort port, int width) => width switch { 1 => 0xef, 2 => 0xcdef, 4 => 0x89abcdef, _ => throw new InvalidDataException("Fixture read width.") };
        /// <summary>Accepts actual own guest writes; their complete effects are retained in the port ledger.</summary>
        public void Write(ushort port, uint value, int width) { }
    }
    private sealed class Output : IIoPortDevice
    {
        public readonly StringBuilder Text = new();
        /// <summary>Leaves the diagnostic output device undriven on reads.</summary>
        public uint Read(ushort port, int width) => uint.MaxValue;
        /// <summary>Records only byte output from the actual own guest.</summary>
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
        private readonly uint _frequency;
        private readonly uint _db, _ud, _gp;

        /// <summary>Constructs explicitly known local devices, immutable reset ROM and a deterministic I/O peer.</summary>
        public Rig(byte[] program, uint frequency)
        {
            _frequency = frequency; _db = BitConverter.ToUInt16(program, 17); _ud = BitConverter.ToUInt16(program, 19); _gp = BitConverter.ToUInt16(program, 21);
            Pit = new(Clock, () => { Ledger.Add(new { kind = "pulse", now = Clock.Now }); Pic.SetLine(0, true); Pic.SetLine(0, false); });
            var ports = new IoPortBus(); ports.Map(0x20, 2, Pic); ports.Map(0xa0, 2, Pic); ports.Map(0x40, 4, Pit); ports.Map(0xe9, 1, Output); ports.Map(0x2e0, 4, new FixedPort());
            ports.Accessed += (port, value, width, write) => Ledger.Add(new { kind = "port", now = Clock.Now, port, value, width, write });
            Pic.InterruptAcknowledged += (irq, vector, spurious) => Ledger.Add(new { kind = "ack", now = Clock.Now, irq, vector, spurious });
            var rom = new byte[65536]; new byte[] { 0xea, 0, 0x10, 0, 0 }.CopyTo(rom, 0xfff0); var reset = new RomDevice(rom);
            Memory.Map(0xffff0000, 65536, reset, "Own debug reset ROM"); Memory.Map(0xffef0000, 65536, reset, "Own masked reset ROM");
            program.CopyTo(Memory.Ram, 0x1000);
            Cpu = new(Memory, ports, X86CpuModel.IntelP54cB5Subset, Clock, frequency);
            Cpu.InterruptEntered += vector => Ledger.Add(new { kind = "entry", now = Clock.Now, vector });
            Add("cpu", Cpu); Add("pic", Pic); Add("pit", Pit);
        }
        /// <summary>Registers each explicit owned state object and stable scheduler owner name.</summary>
        private void Add(string name, IStateObject value)
        { Graph.Add(name, value); Owners.Add(value, name); foreach (var child in value.StateChildren) Add(name + "/" + child.Name, child.Value); }
        /// <summary>Captures exact architectural boundary values, including comparator controls and REP progress.</summary>
        private View Position() => new(Cpu.Eip, Enumerable.Range(0, 8).Select(i => Cpu.GetRegister((Register)i)).ToArray(),
            Cpu.GetSegment(Segment.Cs), Cpu.GetSegment(Segment.Ss), Cpu.CurrentPrivilegeLevel, Cpu.Eflags, Cpu.Dr6, Cpu.Dr7, Cpu.Cr4);
        /// <summary>Reads only committed own RAM frame bytes without emulated speculative accesses.</summary>
        private Frame StackFrame()
        {
            uint address = Cpu.GetSegmentBase(Segment.Ss) + (Cpu.GetRegister(Register.Sp) & (Cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff));
            if (address > Memory.Ram.Length - 24) throw new InvalidDataException("Own frame outside RAM.");
            return new(address, Convert.ToHexString(Memory.Ram.AsSpan((int)address, 24)));
        }
        /// <summary>Captures all selected component records and exact pending typed scheduler events.</summary>
        public Cut Save() => new(Graph.ToDictionary(p => p.Key, p => p.Value.SaveState()), Clock.SaveState(Owners));
        /// <summary>Executes only a real guest step, physical interrupt acceptance or next virtual deadline.</summary>
        public void Advance()
        {
            View before = Position();
            if (Cpu.CanAcceptMaskableInterrupt && Pic.HasPending && Pic.Acknowledge() is { } vector)
            {
                Cpu.EnterHardwareInterrupt(vector); Ledger.Add(new { kind = "hardware", now = Clock.Now, before, after = Position(), frame = StackFrame() }); return;
            }
            if (Cpu.Halted)
            {
                ulong target = Clock.NextEventTime ?? throw new InvalidDataException("Halted without a deadline.");
                Ledger.Add(new { kind = "idle", now = Clock.Now, target }); Clock.AdvanceTo(target); return;
            }
            ulong now = Clock.Now; uint physical = Cpu.PhysicalInstructionAddress; uint cycles = Cpu.Step(); View after = Position();
            Ledger.Add(new { kind = "step", now, physical, cycles, before, after,
                frame = after.Ip == _db || after.Ip == _ud || after.Ip == _gp ? StackFrame() : null, phase = Memory.ReadDword(0x5104) });
            Clock.AdvanceCycles(cycles, _frequency);
            if (Cpu.TripleFaulted) throw new InvalidDataException("Own guest triple faulted.");
        }
    }
    /// <summary>Serializes only declared component records and raw numeric values.</summary>
    private static string Encode(Cut cut) => JsonSerializer.Serialize(cut, Json);
    /// <summary>Hashes exact evidence bytes for repeat identity checks.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Restores a real active DE/GD/comparator/timer cut on a new fixed selected graph.</summary>
    public static int Run(string configPath)
    {
        using var config = JsonDocument.Parse(File.ReadAllBytes(configPath)); var input = config.RootElement;
        string programPath = Path.GetFullPath(input.GetProperty("program").GetString()!); string outputPath = Path.GetFullPath(input.GetProperty("output").GetString()!);
        uint frequency = input.GetProperty("frequency").GetUInt32(); if (frequency is not (33_000_000 or 90_000_000)) throw new InvalidDataException("Unknown own fixed frequency.");
        if (programPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) || outputPath.Contains("doom2-play", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Excluded personal paths.");
        if (Directory.Exists(outputPath) || File.Exists(outputPath)) throw new IOException("Evidence target exists.");
        byte[] program = File.ReadAllBytes(programPath); if (program.Length is < 64 or > 12288) throw new InvalidDataException("Own program size differs.");
        Directory.CreateDirectory(outputPath); var a = new Rig(program, frequency); a.Clock.AdvanceTo(50_000_000_000); int warmup = 0;
        while (!a.Cpu.Halted && warmup++ < 10000) a.Advance();
        if (!a.Cpu.Halted || a.Pit.InterruptPulses != 0 || !a.Cpu.CanAcceptMaskableInterrupt || a.Cpu.CurrentPrivilegeLevel != 0 || a.Cpu.Cr4 != 8 || a.Cpu.Dr7 != 0x22401)
            throw new InvalidDataException("Missing first active debug/timer cut.");
        string saved = Encode(a.Save()); File.WriteAllText(Path.Combine(outputPath, "cut.json"), saved); File.WriteAllBytes(Path.Combine(outputPath, "ram-cut.bin"), a.Memory.Ram);
        var b = new Rig(program, frequency); File.ReadAllBytes(Path.Combine(outputPath, "ram-cut.bin")).CopyTo(b.Memory.Ram, 0);
        Cut cut = JsonSerializer.Deserialize<Cut>(File.ReadAllBytes(Path.Combine(outputPath, "cut.json")), Json)!;
        if (!cut.Components.Keys.Order().SequenceEqual(b.Graph.Keys.Order())) throw new InvalidDataException("Graph differs.");
        foreach (var field in cut.Components) b.Graph[field.Key].LoadState(field.Value); b.Clock.LoadState(cut.Scheduler, b.Graph);
        if (saved != Encode(b.Save())) throw new InvalidDataException("Cut failed exact roundtrip.");
        int ledgerCut = a.Ledger.Count, comparisons = 0;
        while (a.Memory.Ram[0x5114] == 0 || !a.Cpu.Halted)
        {
            if (++comparisons > 10000) throw new InvalidDataException("Own guest failed bounded completion."); a.Advance(); b.Advance();
            if (Encode(a.Save()) != Encode(b.Save()) || !a.Memory.Ram.SequenceEqual(b.Memory.Ram)) throw new InvalidDataException("Selected debug continuation differs.");
        }
        if (!b.Cpu.Halted || a.Pit.InterruptPulses != 4 || a.Output.Text.ToString() != b.Output.Text.ToString()) throw new InvalidDataException("Wrong completion.");
        string futureA = JsonSerializer.Serialize(a.Ledger.Skip(ledgerCut), Json), futureB = JsonSerializer.Serialize(b.Ledger, Json);
        if (futureA != futureB) throw new InvalidDataException("Future publications differ.");
        File.WriteAllText(Path.Combine(outputPath, "ledger.json"), JsonSerializer.Serialize(a.Ledger, Json));
        File.WriteAllText(Path.Combine(outputPath, "future-original.json"), futureA); File.WriteAllText(Path.Combine(outputPath, "future-restored.json"), futureB);
        File.WriteAllText(Path.Combine(outputPath, "final-original.json"), Encode(a.Save())); File.WriteAllText(Path.Combine(outputPath, "final-restored.json"), Encode(b.Save()));
        File.WriteAllBytes(Path.Combine(outputPath, "ram-original.bin"), a.Memory.Ram); File.WriteAllBytes(Path.Combine(outputPath, "ram-restored.bin"), b.Memory.Ram);
        var result = new { passed = true, model = "IntelP54cB5Subset", fullPcSnapshot = false, dosBoot = false, frequency, initialTime = 50_000_000_000UL,
            warmup, comparisons, ledgerCut, pulses = a.Pit.InterruptPulses, output = a.Output.Text.ToString(), programSha256 = Hash(program),
            ramSha256 = Hash(a.Memory.Ram), stateSha256 = Hash(Encoding.UTF8.GetBytes(Encode(a.Save()))), futureSha256 = Hash(Encoding.UTF8.GetBytes(futureA)), finalTime = a.Clock.Now };
        File.WriteAllText(Path.Combine(outputPath, "result.json"), JsonSerializer.Serialize(result, Json)); Console.WriteLine(JsonSerializer.Serialize(result, Json)); return 0;
    }
}
