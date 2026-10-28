// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual DOS IRQ/EOI/IRET and full-PC continuation with an explicitly controlled virtual PIRQ/ISA peer.</summary>
internal static class PiixIrqGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string? System = null);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0, uint Cr3,
        bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, byte Pirqs, ushort Isa, ushort Elcr, long PitPulses);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Phase(int Number, Point At);
    private sealed record Pin(string Kind, int Index, bool High, Point Before, Point After);
    private sealed record Boundary(string Kind, ushort Offset, Point Before, byte[] Frame)
    {
        /// <summary>Exact owner boundary after an actual own IRET, before the next PIC recognition.</summary>
        public Point? After { get; set; }
    }
    /// <summary>Requires actual guest effects and leaves any bounded failure visible.</summary>
    private static void Check(bool value, string why) { if (!value) throw new InvalidDataException(why); }
    /// <summary>Returns the required immutable board binding rather than adding a device after construction.</summary>
    private static Intel82371FbIrq Bridge(PcMachine machine) => machine.PiixIrq ?? throw new InvalidDataException("PIIX IRQ board absent.");
    /// <summary>Encodes all registered raw CPU/device/scheduler fields at a legal owner boundary.</summary>
    private static byte[] State(PcMachine machine) => JsonSerializer.SerializeToUtf8Bytes(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only explicitly passed owned data.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes complete read-only observations without suppressing record properties.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value,
        new JsonSerializerOptions(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false }));
    /// <summary>Finds only the unique own NASM label table.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("PIXMETA1"u8); Check(at >= 0 && at + 46 <= program.Length, "Own IRQ metadata absent.");
        return Enumerable.Range(0, 19).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8 + i * 2, 2))).ToArray();
    }
    /// <summary>Observes actual CPU and bridge pins without advancing clocks or entering an interrupt.</summary>
    private static Point Observe(PcMachine machine)
    {
        var cpu = machine.Cpu; var bridge = Bridge(machine);
        return new(machine.Scheduler.Now, cpu.Instructions, cpu.SaveState().Fields["_tscEpochTicks"].GetUInt64(),
            machine.Scheduler.ClockTicks(cpu.TimestampFrequency), cpu.TimestampCounter, cpu.Eflags, cpu.Cr0, cpu.Cr3,
            cpu.Virtual8086, cpu.Eip, Enumerable.Range(0, 8).Select(i => cpu.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => cpu.GetSegment((Segment)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => cpu.GetSegmentBase((Segment)i)).ToArray(),
            bridge.PirqAssertions, bridge.IsaLines, bridge.Elcr, machine.Pit.InterruptPulses);
    }
    /// <summary>Reads the actual small guest return frame through a nonmutating classic page-table walk.</summary>
    private static byte[] Frame(PcMachine machine)
    {
        var c = machine.Cpu; uint sp = c.GetRegister(Register.Sp) & (c.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff);
        uint linear = c.GetSegmentBase(Segment.Ss) + sp; byte[] result = new byte[6];
        for (uint i = 0; i < 6; i++)
        {
            uint address = linear + i;
            if (c.PagingEnabled)
            {
                uint pde = machine.Memory.ReadDword((c.Cr3 & 0xfffff000) + (address >> 22) * 4);
                Check((pde & 1) != 0 && (pde & 0x80) == 0, "Actual IRQ stack PDE unavailable.");
                uint pte = machine.Memory.ReadDword((pde & 0xfffff000) + ((address >> 12) & 1023) * 4);
                Check((pte & 1) != 0, "Actual IRQ stack PTE unavailable."); address = (pte & 0xfffff000) | (address & 4095);
            }
            result[i] = machine.Memory.ReadByte(address);
        }
        return result;
    }
    /// <summary>Records only actual own CPU instructions, actual ports and real PIC acknowledgements.</summary>
    private sealed class Ledger(PcMachine machine, byte[] program, ushort[] table)
    {
        /// <summary>Actual own DOS code base proven by unchanged loaded instruction bytes.</summary>
        public uint ProgramBase { get; private set; }
        /// <summary>Last actual own phase OUT completed by the CPU.</summary>
        public int Marker { get; private set; }
        /// <summary>Actual bus transfers including PIC EOIs and routing/ELCR writes.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Actual hardware PIC acknowledgements, distinct from software INTs or synthetic CPU entry.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Every completed own ready OUT, independently tied to its unchanged label.</summary>
        public List<Phase> Phases { get; } = [];
        /// <summary>Actual own handler entries, commits and IRET effects.</summary>
        public List<Boundary> Boundaries { get; } = [];
        private Boundary? _return;
        /// <summary>Reads one already committed own proof word without changing the guest.</summary>
        public ushort Word(int offset) => ProgramBase == 0 ? (ushort)0 : machine.Memory.ReadWord(ProgramBase + table[0] + (uint)offset);
        /// <summary>Captures actual PIC recognition after the real controller has accepted a source.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) => Acks.Add(new(irq, vector, spurious, Observe(machine)));
        /// <summary>Captures actual accesses; only the own phase label can control the virtual pin peer.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (address is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x4d0 or 0x4d1 || address is >= 0xcf8 and <= 0xcff)
                Ports.Add(new(address, value, width, write, Observe(machine)));
            if (address == 0xe9 && write && width == 1 && value is >= 0xa1 and <= 0xa7 && ProgramBase != 0 &&
                machine.Cpu.GetSegmentBase(Segment.Cs) == ProgramBase && machine.Cpu.Eip == table[4] + 2)
            { Marker = (int)value - 0xa0; Check(Word(10) == Marker, "Own phase output/proof differs."); Phases.Add(new(Marker, Observe(machine))); }
        }
        /// <summary>Observes only the own program's exact known handler boundaries.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            uint basis = machine.Cpu.GetSegmentBase(Segment.Cs);
            if (basis + 0x110 >= machine.Memory.Ram.Length || physical < basis || physical >= basis + table[3] ||
                !machine.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis; ushort offset = (ushort)(physical - basis);
            int index = Array.IndexOf(table, offset, 5, 6); if (index < 5) return;
            string kind = new[] { "entry10", "commit10", "iret10", "entry11", "commit11", "iret11" }[index - 5];
            var row = new Boundary(kind, offset, Observe(machine), kind.StartsWith("entry", StringComparison.Ordinal) || kind.StartsWith("iret", StringComparison.Ordinal) ? Frame(machine) : []);
            Boundaries.Add(row); if (kind.StartsWith("iret", StringComparison.Ordinal)) _return = row;
        }
        /// <summary>Samples the completed actual IRET before any following owner interrupt recognition.</summary>
        public void AfterStep() { if (_return is not null) { _return.After = Observe(machine); _return = null; } }
    }
    /// <summary>Finite controlled virtual peer. Its state is copied explicitly at the same cut as the bridge; no opaque scheduler callbacks.</summary>
    private sealed class Peer
    {
        /// <summary>Last own ready marker already applied at a stopped owner boundary.</summary>
        public int Stage { get; private set; }
        private int _actions;
        /// <summary>Whether the first shared IRQ has committed and only the second provider remains high.</summary>
        public bool Cut => Stage == 1 && (_actions & 1) != 0;
        /// <summary>Actual owner-boundary pin publications.</summary>
        public List<Pin> Pins { get; } = [];
        /// <summary>Copies only this deterministic peer's phase, never synthesizing the machine's pin state.</summary>
        public Peer Clone() => new() { Stage = Stage, _actions = _actions };
        /// <summary>Publishes one controlled pin and records before/after actual bridge/PIC state.</summary>
        private void Pin(PcMachine machine, string kind, int index, bool high)
        {
            var before = Observe(machine);
            if (kind == "pirq") Bridge(machine).SetPirq(index, high); else Bridge(machine).SetIsaLine(index, high);
            Pins.Add(new(kind, index, high, before, Observe(machine)));
        }
        /// <summary>Responds only to actual ready OUTs or actual committed handler counts.</summary>
        public bool Apply(PcMachine machine, Ledger ledger)
        {
            int before = Pins.Count;
            if (ledger.Marker > Stage)
            {
                Check(ledger.Marker == Stage + 1, "Own peer marker skipped a phase."); Stage = ledger.Marker;
                switch (Stage)
                {
                    case 1: Pin(machine, "pirq", 0, true); Pin(machine, "pirq", 1, true); break;
                    case 2: Pin(machine, "pirq", 0, true); break;
                    case 3: Pin(machine, "isa", 10, true); break;
                    case 5: Pin(machine, "pirq", 2, true); break;
                    case 6: Pin(machine, "pirq", 3, true); break;
                }
            }
            int count10 = ledger.Word(12), count11 = ledger.Word(14);
            if (Stage == 1 && count10 == 1 && (_actions & 1) == 0) { Pin(machine, "pirq", 0, false); _actions |= 1; }
            if (Stage == 1 && count10 == 2 && (_actions & 2) == 0) { Pin(machine, "pirq", 1, false); _actions |= 2; }
            if (Stage == 2 && count10 == 4 && (_actions & 4) == 0) { Pin(machine, "pirq", 0, false); _actions |= 4; }
            if (Stage == 4 && count10 == 5 && (_actions & 8) == 0) { Pin(machine, "isa", 10, false); _actions |= 8; }
            if (Stage == 5 && count11 == 1 && (_actions & 16) == 0) { Pin(machine, "pirq", 2, false); _actions |= 16; }
            if (Stage == 6 && count11 == 2 && (_actions & 32) == 0) { Pin(machine, "pirq", 3, false); _actions |= 32; }
            return Pins.Count != before;
        }
    }
    /// <summary>Attaches only observation events; no callback may mutate a guest pin or enter the CPU.</summary>
    private static void Attach(PcMachine machine, Ledger ledger)
    { machine.Ports.Accessed += ledger.Access; machine.Pic.InterruptAcknowledged += ledger.Acknowledge; machine.Cpu.InstructionStarting += ledger.Instruction; }
    /// <summary>Removes observations before moving to another explicitly measured phase.</summary>
    private static void Detach(PcMachine machine, Ledger ledger)
    { machine.Ports.Accessed -= ledger.Access; machine.Pic.InterruptAcknowledged -= ledger.Acknowledge; machine.Cpu.InstructionStarting -= ledger.Instruction; }
    /// <summary>Encodes only internal virtual guest input, preserving each current controller admission response.</summary>
    private static byte[] Command(bool german) => german ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rPIRQIRQ.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rPIRQIRQ.COM\r");
    /// <summary>Preserves bounded actual failure data without changing guest inputs or attempting recovery.</summary>
    private static void Require(bool condition, string root, PcMachine machine, Ledger? ledger, Peer? peer, string reason)
    {
        if (condition) return;
        Write(Path.Combine(root, "failed-result.json"), new { reason, at = Observe(machine), fault = machine.Fault?.ToString(),
            screen = machine.Video.ReadText(), debug = machine.Board.DebugText, marker = ledger?.Marker, peerStage = peer?.Stage,
            programBase = ledger?.ProgramBase, irq10 = ledger?.Word(12), irq11 = ledger?.Word(14), frames = ledger?.Word(16),
            lastPorts = ledger?.Ports.TakeLast(24), lastAcks = ledger?.Acks.TakeLast(16), lastBoundaries = ledger?.Boundaries.TakeLast(12),
            phases = ledger?.Phases, pins = peer?.Pins });
        File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), machine.Memory.Ram);
        File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(machine));
        throw new InvalidDataException(reason + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Boots original DOS, drives the own actual guest and requires exact whole-PC future plus a real following DOS prompt.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(!input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program);
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Existing output target."); Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew(); using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == "dose-430fx-piix-irq-dev-v1" && original.Cpu.TimestampFrequency == 100_000_000 && original.Memory.Ram.Length == 32 * 1048576, "Wrong complete PIIX IRQ board."); Bridge(original);
        long steps = 0;
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && steps < 500000000)
        { Require(timer.Elapsed.TotalSeconds < 240, root, original, null, null, "PIIX original DOS boot timeout."); int count = original.RunSteps(10000); Check(count > 0, "PIIX DOS boot stopped."); steps += count; }
        Require(original.Fault is null && original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), root, original, null, null, "Actual original DOS prompt missing.");
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter(); original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        var first = new Ledger(original, program, table); var peer = new Peer(); Attach(original, first);
        byte[] keys = Command(input.GermanKeyboard); int sent = 0;
        while (!peer.Cut && original.Fault is null && steps < 700000000)
        {
            Require(timer.Elapsed.TotalSeconds < 300, root, original, first, peer, "PIIX own first IRQ/cut timeout.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int count = original.RunSteps(first.ProgramBase == 0 ? 100 : 1); steps += count; first.AfterStep(); peer.Apply(original, first); Check(count > 0, "PIIX own guest stopped before cut.");
        }
        Detach(original, first);
        Require(original.Fault is null && sent == keys.Length && peer.Cut && first.Word(12) == 1 && first.Word(16) == 1 &&
            Bridge(original).PirqAssertions == 2 && original.SoundBlaster16!.Active, root, original, first, peer, "Actual asserted shared-provider/active-PCM cut missing.");
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved); byte[] captured = State(original), ram = original.Memory.Ram.ToArray();
        using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch")); Check(captured.SequenceEqual(State(resumed)) && ram.SequenceEqual(resumed.Memory.Ram), "Initial full PIIX graph differs.");
        var a = new Ledger(original, program, table); var b = new Ledger(resumed, program, table); var peerA = peer.Clone(); var peerB = peer.Clone(); Attach(original, a); Attach(resumed, b);
        bool exitA = false, exitB = false;
        // An actual following DOS prompt is also required; decoding AH4C alone is never completion.
        void ExitA(uint at, byte op) { if (op == 0xcd && original.Memory.ReadByte(at + 1) == 0x21 && ((original.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) exitA = true; }
        // The restored guest must independently execute the same original DOS return path.
        void ExitB(uint at, byte op) { if (op == 0xcd && resumed.Memory.ReadByte(at + 1) == 0x21 && ((resumed.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) exitB = true; }
        original.Cpu.InstructionStarting += ExitA; resumed.Cpu.InstructionStarting += ExitB;
        using var pcmA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var pcmB = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long framesA = 0, framesB = 0;
        original.Audio.AudioReady += block => { pcmA.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesA += block.Samples.Length / 2; };
        resumed.Audio.AudioReady += block => { pcmB.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesB += block.Samples.Length / 2; };
        int comparisons = 0, pending = 0; long futureSteps = 0;
        while (!(exitA && exitB && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal) && resumed.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal)) && futureSteps < 100000000)
        {
            Require(timer.Elapsed.TotalSeconds < 900, root, original, a, peerA, "PIIX full DOS continuation timeout.");
            int budget = peerA.Stage >= 7 ? 10000 : 1;
            int ca = original.RunSteps(budget), cb = resumed.RunSteps(budget); a.AfterStep(); b.AfterStep(); futureSteps += ca;
            bool changedA = peerA.Apply(original, a), changedB = peerB.Apply(resumed, b);
            Check(ca == cb && ca > 0 && changedA == changedB && peerA.Stage == peerB.Stage && original.Fault is null && resumed.Fault is null, "PIIX actual future count/fault/pin differs.");
            pending += ca;
            if (changedA || pending >= 1000 || peerA.Stage >= 7)
            { Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Entire PIIX CPU/RAM/device/scheduler future differs."); comparisons++; pending = 0; }
        }
        Require(exitA && exitB && original.Board.DebugText.Contains("PIIX-IRQ-DONE", StringComparison.Ordinal) && !original.Board.DebugText.Contains("PIIX-IRQ-FAILED", StringComparison.Ordinal) && a.Word(12) == 6 && a.Word(14) == 2 && a.Word(16) == 8 &&
            original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal) && resumed.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal),
            root, original, a, peerA, "Actual eight IRQ/EOI/IRET paths and following DOS prompt missing.");
        original.Audio.Flush(); resumed.Audio.Flush();
        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final entire PIIX graph differs.");
        Check(JsonSerializer.Serialize(a.Ports, StateValues.Json) == JsonSerializer.Serialize(b.Ports, StateValues.Json) && JsonSerializer.Serialize(a.Acks, StateValues.Json) == JsonSerializer.Serialize(b.Acks, StateValues.Json) &&
            JsonSerializer.Serialize(a.Boundaries, StateValues.Json) == JsonSerializer.Serialize(b.Boundaries, StateValues.Json) && JsonSerializer.Serialize(peerA.Pins, StateValues.Json) == JsonSerializer.Serialize(peerB.Pins, StateValues.Json), "Actual future IRQ/pin publications differ.");
        string audioA = Convert.ToHexString(pcmA.GetHashAndReset()), audioB = Convert.ToHexString(pcmB.GetHashAndReset()); Check(audioA == audioB && framesA == framesB && framesA > 480 && original.SoundBlaster16!.TransferredSamples == 512, "Actual future PCM/DMA differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia")); var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "Actual future DOS journals differ.");
        File.WriteAllBytes(Path.Combine(root, "final-state.json"), State(original)); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.ProgramBase, ports = first.Ports.Concat(a.Ports), acks = first.Acks.Concat(a.Acks), boundaries = first.Boundaries.Concat(a.Boundaries), pins = peer.Pins.Concat(peerA.Pins), phases = first.Phases.Concat(a.Phases),
            beforeCutPorts = first.Ports.Count, beforeCutAcks = first.Acks.Count, beforeCutBoundaries = first.Boundaries.Count, beforeCutPins = peer.Pins.Count,
            futurePorts = a.Ports, restoredPorts = b.Ports, futureAcks = a.Acks, restoredAcks = b.Acks, futureBoundaries = a.Boundaries, restoredBoundaries = b.Boundaries, futurePins = peerA.Pins, restoredPins = peerB.Pins, futurePhases = a.Phases, restoredPhases = b.Phases });
        Write(Path.Combine(root, "result.json"), new { passed = true, returnedToDos = true, manifest, comparisons, futureSteps, exitA, exitB, system = input.System,
            capturedStateSha256 = Hash(captured), capturedRamSha256 = Hash(ram), finalStateSha256 = Hash(State(original)), finalRamSha256 = Hash(original.Memory.Ram),
            pcmSha256 = audioA, pcmFrames = framesA, dma = original.SoundBlaster16!.TransferredSamples, programBase = first.ProgramBase, programSha256 = Hash(program), finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Console.WriteLine("PASS actual PIIX IRQ DOS/full-PC continuation " + root); return 0;
    }
}
