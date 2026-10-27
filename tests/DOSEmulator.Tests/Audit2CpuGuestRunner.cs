// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Reset-booted own PIT/STI/ENTER/restart kernel and complete real PC snapshot future.</summary>
internal static class Audit2CpuGuestRunner
{
    private sealed record Input(string Profile, string Image, string Payload, string Output);
    private sealed record Irq(ulong Time, long Instructions, ushort SavedIp, ushort SavedCs, ushort SavedFlags, uint Stack,
        int Isr, int Irr, ulong? NextDeadline);
    private sealed record Returned(ulong Time, ushort Ip, ushort Cs);
    private sealed record Eoi(ulong Time, uint Value, int Width, int Isr);
    /// <summary>Rejects a missing real guest effect, full state identity or bounded execution condition.</summary>
    private static void Check(bool value, string why) { if (!value) throw new InvalidDataException(why); }
    /// <summary>Records every registered field plus the exact future scheduler descriptors.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only owned byte arrays.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes private observations with their ordinary record properties retained.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value,
        new JsonSerializerOptions(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false }));
    /// <summary>Observes real interrupt entry, physical EOI and subsequent IRET boundary without driving any input.</summary>
    private sealed class Ledger(PcMachine machine, ushort proof)
    {
        public readonly List<Irq> Irqs = [];
        public readonly List<Returned> Returns = [];
        public readonly List<Eoi> Eois = [];
        private Irq? _active;
        private bool _pending;
        private uint _frameBase, _framePointer;
        private ulong _entryTime;
        private long _entryInstructions;
        /// <summary>Reads only the fixed own phase byte from installed RAM.</summary>
        private uint Phase => BinaryPrimitives.ReadUInt32LittleEndian(machine.Memory.Ram.AsSpan(0x8000 + proof + 8, 4));
        /// <summary>Reads actual PIC data through its pure explicit state accessor, never through an I/O command.</summary>
        private StateBlock Pic => machine.Pic.StateChildren.Single(v => v.Name == "_master").Value.SaveState();
        /// <summary>Marks the early entry event without treating its still-unwritten stack as a delivered frame.</summary>
        public void Interrupt(byte vector)
        {
            if (vector != 8 || Phase != 1) return;
            _framePointer = unchecked(machine.Cpu.GetRegister(Register.Sp) - 6) & 65535;
            _frameBase = machine.Cpu.GetSegmentBase(Segment.Ss);
            _entryTime = machine.Scheduler.Now; _entryInstructions = machine.Cpu.Instructions; _pending = true;
        }
        /// <summary>Reads the completed frame at the BIOS handler's first instruction, before any handler memory effect.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            if (!_pending) return;
            _pending = false;
            var ram = machine.Memory.Ram.AsSpan(checked((int)(_frameBase + _framePointer)), 6); var pic = Pic;
            _active = new(_entryTime, _entryInstructions,
                BinaryPrimitives.ReadUInt16LittleEndian(ram), BinaryPrimitives.ReadUInt16LittleEndian(ram[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(ram[4..]), _framePointer, pic.Fields["Isr"].GetInt32(), pic.Fields["Irr"].GetInt32(), machine.Scheduler.NextEventTime);
            Irqs.Add(_active);
        }
        /// <summary>Records only an actual nonspecific EOI written by the active BIOS timer handler.</summary>
        public void Port(ushort port, uint value, int width, bool write)
        {
            if (_active is not null && write && port == 0x20 && value == 0x20)
                Eois.Add(new(machine.Scheduler.Now, value, width, Pic.Fields["Isr"].GetInt32()));
        }
        /// <summary>Confirms the actual CPU return position immediately after the scalar production step.</summary>
        public void Boundary()
        {
            if (_active is not { } irq || machine.Cpu.GetSegment(Segment.Cs) != irq.SavedCs || machine.Cpu.Eip != irq.SavedIp) return;
            Returns.Add(new(machine.Scheduler.Now, irq.SavedIp, irq.SavedCs)); _active = null;
        }
    }
    /// <summary>Boots one validated PC from an exclusively own read-only image, then compares the complete resumed future.</summary>
    public static int Run(string inputPath)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(inputPath), StateValues.Json)!;
        Check(!input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        string output = Path.GetFullPath(input.Output); Check(!Directory.Exists(output) && !File.Exists(output), "Existing output."); Directory.CreateDirectory(output);
        byte[] payload = File.ReadAllBytes(input.Payload); int meta = payload.AsSpan().IndexOf("AU2META1"u8);
        Check(meta > 0 && meta + 26 <= payload.Length, "Own static metadata missing.");
        ushort[] labels = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(meta + 8 + i * 2, 2))).ToArray();
        var profile = JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(input.Profile), IpcProtocol.JsonOptions)!;
        using var original = MachineFactory.FromImages(profile, input.Image);
        var before = new Ledger(original, labels[0]); original.Cpu.InterruptEntered += before.Interrupt; original.Cpu.InstructionStarting += before.Instruction; original.Ports.Accessed += before.Port;
        long bootSteps = 0; var timer = Stopwatch.StartNew(); uint cutOffset = (uint)labels[2] + 4096;
        while (!(original.Cpu.GetSegment(Segment.Cs) == 0x800 && original.Cpu.Eip == cutOffset) && bootSteps < 2000000 && original.Fault is null)
        { Check(timer.Elapsed.TotalSeconds < 120, "Own CPU kernel boot timeout."); Check(original.RunSteps(1) == 1, "Own CPU guest stopped before cut."); before.Boundary(); bootSteps++; }
        Check(original.Fault is null && original.Cpu.GetSegment(Segment.Cs) == 0x800 && original.Cpu.Eip == cutOffset, "Actual STI-stream cut missing: " + original.Fault);
        Check(original.Cpu.CanAcceptMaskableInterrupt && original.Scheduler.NextEventTime is { } deadline && deadline > original.Scheduler.Now,
            "Already-enabled STI must not create another shadow at the real cut.");
        original.Cpu.InterruptEntered -= before.Interrupt; original.Cpu.InstructionStarting -= before.Instruction; original.Ports.Accessed -= before.Port;
        byte[] cutState = State(original); byte[] cutRam = original.Memory.Ram.ToArray();
        var manifest = SnapshotStore.Capture(original, Path.Combine(output, "cut.dossav"));
        using var restored = SnapshotStore.Load(Path.Combine(output, "cut.dossav"), Path.Combine(output, "resumed.dosebranch"));
        Check(cutState.AsSpan().SequenceEqual(State(restored)) && cutRam.AsSpan().SequenceEqual(restored.Memory.Ram), "Entire cut differs.");
        var a = new Ledger(original, labels[0]); var b = new Ledger(restored, labels[0]);
        original.Cpu.InterruptEntered += a.Interrupt; restored.Cpu.InterruptEntered += b.Interrupt;
        original.Cpu.InstructionStarting += a.Instruction; restored.Cpu.InstructionStarting += b.Instruction;
        original.Ports.Accessed += a.Port; restored.Ports.Accessed += b.Port;
        long futureSteps = 0; int comparisons = 0;
        while (!(original.Cpu.Halted && restored.Cpu.Halted) && futureSteps < 1000000)
        {
            Check(timer.Elapsed.TotalSeconds < 180, "Own CPU kernel future timeout.");
            int ca = original.RunSteps(1), cb = restored.RunSteps(1); a.Boundary(); b.Boundary(); futureSteps++;
            Check(ca == cb && ca > 0 && original.Fault is null && restored.Fault is null, "Actual future counts/faults differ.");
            if (futureSteps % 257 == 0)
            { Check(State(original).AsSpan().SequenceEqual(State(restored)) && original.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram), "Full CPU/RAM/devices/scheduler future differs."); comparisons++; }
        }
        Check(original.Cpu.Halted && restored.Cpu.Halted && original.Board.DebugText.Contains("AUDIT2-CPU-DONE", StringComparison.Ordinal) &&
            !original.Board.DebugText.Contains("FAILED", StringComparison.Ordinal), "Actual own CPU completion missing: " + original.Board.DebugText);
        Check(State(original).AsSpan().SequenceEqual(State(restored)) && original.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram), "Final entire PC differs.");
        Check(JsonSerializer.Serialize(a.Irqs, StateValues.Json) == JsonSerializer.Serialize(b.Irqs, StateValues.Json) &&
            JsonSerializer.Serialize(a.Returns, StateValues.Json) == JsonSerializer.Serialize(b.Returns, StateValues.Json) &&
            JsonSerializer.Serialize(a.Eois, StateValues.Json) == JsonSerializer.Serialize(b.Eois, StateValues.Json), "Future IRQ/EOI/IRET publications differ.");
        byte[] finalState = State(original); File.WriteAllBytes(Path.Combine(output, "final-state.json"), finalState);
        File.WriteAllBytes(Path.Combine(output, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(output, "ledger.json"), new { irqs = before.Irqs.Concat(a.Irqs), returns = before.Returns.Concat(a.Returns), eois = before.Eois.Concat(a.Eois),
            futureIrqs = a.Irqs, restoredIrqs = b.Irqs, futureReturns = a.Returns, restoredReturns = b.Returns, futureEois = a.Eois, restoredEois = b.Eois });
        Write(Path.Combine(output, "result.json"), new { passed = true, notDosAcceptance = true, profile, manifest, bootSteps, futureSteps, comparisons,
            stateSha256 = Hash(finalState), ramSha256 = Hash(original.Memory.Ram), imageSha256 = Hash(File.ReadAllBytes(input.Image)), payloadSha256 = Hash(payload),
            proof = original.Memory.Ram.AsSpan(0x8000 + labels[0], labels[1] - labels[0]).ToArray(), interruptEntries = before.Irqs.Count + a.Irqs.Count,
            interruptReturns = before.Returns.Count + a.Returns.Count, actualEois = before.Eois.Count + a.Eois.Count,
            finalPosition = original.Cpu.Position, finalDebug = original.Board.DebugText, finalTime = original.Scheduler.Now });
        Console.WriteLine("PASS own reset CPU/PIT/ENTER/PC continuation " + output); return 0;
    }
}
