// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual original-DOS PCI BIOS, BIOS32 FAR calls, original VCPI and two complete active firmware continuations.</summary>
internal static class PciBiosGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System, string Iso, string Firmware, string ExpectedFirmware, int FirstCutTimeoutSeconds = 180);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0, uint Cr3, uint Cr4,
        bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase, int Case);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record RomPoint(string Kind, Point At, uint StackLinear, byte[] StackBytes);
    private sealed record Service(string Kind, int Offset, int ReturnOffset, Point Before)
    {
        /// <summary>Actual first caller instruction after the original firmware/server/DOS service returns.</summary>
        public Point? After { get; set; }
        /// <summary>Actual routing data or CD bytes at that real return, before buffers can be reused.</summary>
        public byte[]? Data { get; set; }
        /// <summary>Actual returned routing-envelope size, distinct from copied table bytes.</summary>
        public int? Size { get; set; }
    }
    private sealed record PcmDigest(long Frames, string Sha256);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects missing guest effects rather than repairing architectural or device state.</summary>
    private static void Check(bool value, string why) { if (!value) throw new InvalidDataException(why); }
    /// <summary>Hashes one explicitly selected own byte extent.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Serializes every registered raw component field and all VRAM without mutation.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Writes bounded textual evidence only, never a screenshot or sound recording.</summary>
    private static void Write(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, Json));
    /// <summary>Reads exact immutable own COM labels.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("PCIBMET1"u8);
        Check(at >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 10)) == 40, "Own PCI guest metadata missing.");
        return Enumerable.Range(0, 40).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 12 + i * 2))).ToArray();
    }
    /// <summary>Reads the own raw assembler's passive ROM-offset manifest, not a CPU entry supplied by the host.</summary>
    private static uint[] RomMetadata(byte[] firmware)
    {
        int at = firmware.AsSpan().IndexOf("PCIMETA1"u8);
        Check(firmware.Length == 65536 && at >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(firmware.AsSpan(at + 8)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(firmware.AsSpan(at + 10)) == 14, "Own original PCI ROM manifest missing.");
        return Enumerable.Range(0, 14).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(firmware.AsSpan(at + 12 + i * 4))).ToArray();
    }
    /// <summary>Observes actual CPU, caller case and integer scheduler phase at an owner boundary.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] table)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(), m.Scheduler.ClockTicks(c.TimestampFrequency), c.TimestampCounter,
            c.Eflags, c.Cr0, c.Cr3, c.Cr4, c.Virtual8086, c.Eip, Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(), Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(),
            basis == 0 ? 0 : m.Memory.ReadWord(basis + table[0] + 10u), basis == 0 ? 0 : m.Memory.ReadWord(basis + table[0] + 14u));
    }
    /// <summary>Reads saved original classic-page-table bytes without setting A/D bits or altering monitor mappings.</summary>
    private static byte[] Linear(PcMachine m, uint address, int count)
    {
        byte[] bytes = new byte[count]; var c = m.Cpu;
        for (uint i = 0; i < bytes.Length; i++)
        {
            uint physical = address + i;
            if (c.PagingEnabled)
            {
                uint pde = m.Memory.ReadDword((c.Cr3 & 0xfffff000) + (physical >> 22) * 4); Check((pde & 0x81) == 1, "Actual original ROM-stack PDE missing.");
                uint pte = m.Memory.ReadDword((pde & 0xfffff000) + ((physical >> 12) & 1023) * 4); Check((pte & 1) != 0, "Actual original ROM-stack PTE missing.");
                physical = (pte & 0xfffff000) | (physical & 4095);
            }
            bytes[i] = m.Memory.ReadByte(physical);
        }
        return bytes;
    }
    /// <summary>Passive original client/firmware/port observations, with a bounded aggregate record count.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint[] rom, uint initialBasis = 0)
    {
        /// <summary>Actual original client PSP base discovered from its fetched immutable bytes.</summary>
        public uint ProgramBase { get; private set; } = initialBasis;
        /// <summary>Actual mechanism1, route, VGA and KBC accesses.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Actual physical PIC acknowledgements, never injected interrupts.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Actual original firmware, VCPI and DOS call/return pairs.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Actual named firmware entries and pre-OUT original stack windows.</summary>
        public List<RomPoint> Firmware { get; } = [];
        /// <summary>Passive pre-call metadata for a service already executing when observation attached.</summary>
        public Service? CarriedService { get; private set; }
        /// <summary>Whether the original first requested CF8 transaction actually happened.</summary>
        public bool Cut16 { get; private set; }
        /// <summary>Whether the original protected32-bit CF8 transaction actually happened.</summary>
        public bool Cut32 { get; private set; }
        /// <summary>Actual original firmware origin of the first requested mechanism transaction.</summary>
        public RomPoint? Origin16 { get; private set; }
        /// <summary>Actual original firmware origin of the protected transaction.</summary>
        public RomPoint? Origin32 { get; private set; }
        /// <summary>Whether passive diagnostics reached the absolute100000-record bound.</summary>
        public bool LimitReached { get; private set; }
        private Service? _pending;
        private RomPoint? _out16, _out32;
        /// <summary>Bounds all retained observations before further allocation.</summary>
        private bool Reserve()
        {
            if (LimitReached) return false;
            if ((long)Ports.Count + Acks.Count + Services.Count + Firmware.Count < 100000) return true;
            LimitReached = true; return false;
        }
        /// <summary>Reads one committed own proof word, without guest writes.</summary>
        public ushort Proof(int offset) => ProgramBase == 0 ? (ushort)0 : m.Memory.ReadWord(ProgramBase + table[0] + (uint)offset);
        /// <summary>Returns the real current architectural point.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Returns the existing pending service without adding an artificial ledger row.</summary>
        public Service? PendingAtCut() => _pending;
        /// <summary>Carries passive original invocation metadata, while the restored CPU still executes its own return.</summary>
        public Service Carry(Service source)
        {
            Check(_pending is null && source.After is null, "Cannot carry an overlapping/completed original service.");
            var copy = new Service(source.Kind, source.Offset, source.ReturnOffset, source.Before); _pending = copy; CarriedService = copy; return copy;
        }
        /// <summary>Records actual atomic port effects and the observed pre-OUT firmware origin.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (ProgramBase == 0 || LimitReached) return;
            if (!(address is >= 0xcf8 and <= 0xcff || address is 0x3cc or 0x4d0 or 0x4d1 or 0x60 or 0x21 or 0xa1 or 0x20 or 0xa0 or 0x70)) return;
            if (Reserve()) Ports.Add(new(address, value, width, write, Point()));
            if (address != 0xcf8 || width != 4 || !write || _pending is null) return;
            if (Proof(10) == 2 && Proof(14) == 99 && value == 0x80000000 && _pending.Kind == "pci16") { Cut16 = true; Origin16 = _out16; }
            if (Proof(10) == 6 && Proof(14) == 199 && value == 0x80003900 && _pending.Kind == "pci32") { Cut32 = true; Origin32 = _out32; }
        }
        /// <summary>Records only real PIC acknowledgements.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) { if (ProgramBase != 0 && Reserve()) Acks.Add(new(irq, vector, spurious, Point())); }
        /// <summary>Records real named firmware instruction points and their untouched caller-stack windows.</summary>
        private void RomInstruction(uint physical)
        {
            if (ProgramBase == 0 || physical < 0xf0000 || physical >= 0x100000) return;
            uint offset = physical - 0xf0000;
            bool out16 = offset == rom[4], out32 = offset == rom[6];
            if (!out16 && !out32 && offset != rom[2] && offset != rom[3] && offset != rom[1]) return;
            if (!Reserve()) return;
            uint pointer = m.Cpu.GetRegister(Register.Bp); int length = 0; uint start = 0;
            if (out16) { start = m.Cpu.GetSegmentBase(Segment.Ss) + (pointer & 65535) - 44; length = 52; }
            if (out32) { start = m.Cpu.GetSegmentBase(Segment.Ss) + pointer - 48; length = 64; }
            var row = new RomPoint(out16 ? "out16" : out32 ? "out32" : offset == rom[2] ? "entry16" : offset == rom[3] ? "entry32" : "directory", Point(), start,
                length == 0 ? [] : Linear(m, start, length));
            Firmware.Add(row); if (out16) _out16 = row; if (out32) _out32 = row;
        }
        /// <summary>Binds only fetched own client sites, genuine original firmware and actual subsequent returns.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            RomInstruction(physical);
            uint basis = m.Cpu.GetSegmentBase(Segment.Cs);
            if (physical < basis || physical >= basis + table[2] || basis + 0x110 >= m.Memory.Ram.Length ||
                !m.Memory.Ram.AsSpan((int)basis + 256, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis; int offset = (int)(physical - basis);
            if (_pending is not null && offset == _pending.ReturnOffset)
            {
                Service current = _pending; current.After = Point(); _pending = null;
                if (current.Kind is "pci16" or "pci32" && (current.Before.Registers[0] & 65535) == 0xb10e)
                {
                    current.Data = m.Memory.Ram.AsSpan((int)basis + table[34], 64).ToArray();
                    current.Size = m.Memory.ReadWord(basis + table[current.Kind == "pci16" ? 32 : 33]);
                }
                if (current.Kind == "cd-read" && (current.After.Flags & 1) == 0)
                { int count = (int)(current.After.Registers[0] & 65535); Check(count <= 256, "Actual CD read exceeds own bounded buffer."); current.Data = m.Memory.Ram.AsSpan((int)basis + (int)(current.Before.Registers[2] & 65535), count).ToArray(); }
            }
            int[] sites = [4, 6, 8, 10, 12, 14, 15, 16, 17, 18, 19, 20, 39];
            string[] kinds = ["pci16", "pci32", "directory", "missing-directory", "reserved-directory", "vcpi-presence", "vcpi-pd", "vcpi-pt", "vcpi-interface", "vcpi-identity", "vcpi-enter", "vcpi-return", "cd-read"];
            int index = Array.FindIndex(sites, item => table[item] == offset);
            if (index < 0 || !Reserve()) return;
            int after = index < 5 ? table[sites[index] + 1] : index == 10 ? table[21] : index == 11 ? table[22] : offset + 2;
            _pending = new(kinds[index], offset, after, Point()); Services.Add(_pending);
        }
    }
    /// <summary>One complete actual PC and only passive removable observation hooks.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>Original or genuine SnapshotStore-restored machine.</summary>
        public PcMachine Machine { get; }
        /// <summary>Actual original or future observed effects.</summary>
        public Ledger Ledger { get; }
        /// <summary>Actual successful original DOS AH4C attempt.</summary>
        public bool Exited { get; private set; }
        private readonly IncrementalHash _audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _frames;
        /// <summary>Attaches observers to an already constructed complete machine.</summary>
        public Side(PcMachine machine, byte[] program, ushort[] table, uint[] rom, uint basis = 0)
        {
            Machine = machine; Ledger = new(machine, program, table, rom, basis); machine.Ports.Accessed += Ledger.Access;
            machine.Pic.InterruptAcknowledged += Ledger.Acknowledge; machine.Cpu.InstructionStarting += Ledger.Instruction; machine.Cpu.InstructionStarting += Exit; machine.Audio.AudioReady += Audio;
        }
        /// <summary>Observes own AH4C00 independently from upper EAX bits.</summary>
        private void Exit(uint physical, byte opcode)
        { if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && opcode == 0xcd && Machine.Memory.ReadByte(physical + 1) == 0x21 && (Machine.Cpu.GetRegister(Register.Ax) & 65535) == 0x4c00) Exited = true; }
        /// <summary>Hashes actual published PCM, without opening a host endpoint.</summary>
        private void Audio(AudioBlock block) { _audio.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); _frames += block.Samples.Length / 2; }
        /// <summary>Starts another actual publication interval without altering mixer/device state.</summary>
        public PcmDigest FinishAudio() { var value = new PcmDigest(_frames, Convert.ToHexString(_audio.GetHashAndReset())); _frames = 0; return value; }
        /// <summary>Executes only normal owner work.</summary>
        public int Step(int budget) => Machine.RunSteps(budget);
        /// <summary>Detaches passive observers without owning machine lifetime.</summary>
        public void Dispose()
        { Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge; Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit; Machine.Audio.AudioReady -= Audio; _audio.Dispose(); }
    }
    /// <summary>Persists only textual observations and raw selected state/RAM on a bounded genuine failure.</summary>
    private static void Require(bool value, string root, Side side, string why)
    {
        if (value) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), screen = side.Machine.Video.ReadText(), fault = side.Machine.Fault?.ToString(),
            lastPorts = side.Ledger.Ports.TakeLast(24), lastServices = side.Ledger.Services.TakeLast(12), lastFirmware = side.Ledger.Firmware.TakeLast(8), error = side.Ledger.Proof(12) });
        File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine)); File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram); throw new InvalidDataException(why);
    }
    /// <summary>Reports bounded real progress without screen recording or native UI.</summary>
    private static void Progress(string root, Side side, string stage, long steps, double seconds) => Write(Path.Combine(root, "progress.json"),
        new { stage, steps, seconds, point = side.Ledger.Point(), screen = side.Machine.Video.ReadText(), fault = side.Machine.Fault?.ToString(), ports = side.Ledger.Ports.Count });
    /// <summary>Compares every raw CPU/device/VRAM field and all installed RAM.</summary>
    private static void Equal(IEnumerable<Side> sides)
    { var array = sides.ToArray(); byte[] state = State(array[0].Machine), ram = array[0].Machine.Memory.Ram; foreach (var side in array.Skip(1)) Check(state.SequenceEqual(State(side.Machine)) && ram.SequenceEqual(side.Machine.Memory.Ram), "Entire PCI BIOS graph/RAM differs."); }
    /// <summary>Requires the complete original four-character prompt and coherent BDA/CRTC cursor.</summary>
    private static bool Prompt(PcMachine m, string drive)
    {
        string[] lines = m.Video.ReadText().Replace("\r", "", StringComparison.Ordinal).TrimEnd().Split('\n'); int row = lines.Length - 1;
        if (row < 0 || lines[row] != drive + ":\\>" || m.Memory.ReadByte(0x450) != 4 || m.Memory.ReadByte(0x451) != row) return false;
        byte[] crtc = m.Video.SaveState().Fields["_crtc"].GetBytesFromBase64(); return ((crtc[14] << 8) | crtc[15]) == row * 80 + 4;
    }
    /// <summary>Keeps exact original observation-list slice boundaries.</summary>
    private static int[] Counts(Ledger l) => [l.Ports.Count, l.Acks.Count, l.Services.Count, l.Firmware.Count];
    /// <summary>Compares each actual subsequent effect, including original firmware stack observations.</summary>
    private static void Future(Ledger a, int[] beginA, Ledger b, int[] beginB, int[] endA, int[] endB)
    {
        object[] x = [a.Ports, a.Acks, a.Services, a.Firmware], y = [b.Ports, b.Acks, b.Services, b.Firmware];
        for (int i = 0; i < x.Length; i++) Check(JsonSerializer.Serialize(((System.Collections.IEnumerable)x[i]).Cast<object>().Skip(beginA[i]).Take(endA[i] - beginA[i]), Json) ==
            JsonSerializer.Serialize(((System.Collections.IEnumerable)y[i]).Cast<object>().Skip(beginB[i]).Take(endB[i] - beginB[i]), Json), "Actual PCI BIOS future differs: " + i);
    }
    /// <summary>Runs original DOS, real16/32-bit firmware, two real imports and three actual DOS/CD/ECHO futures.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(input.FirstCutTimeoutSeconds is >= 10 and <= 900, "PCI first-cut timeout outside10..900 seconds.");
        foreach (string p in new[] { input.Machine, input.Output, input.Probe, input.Iso, input.Firmware, input.ExpectedFirmware }) Check(!p.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal overlay.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe), expected = File.ReadAllBytes(input.ExpectedFirmware), raw = File.ReadAllBytes(input.Firmware); ushort[] table = Metadata(program); uint[] rom = RomMetadata(raw);
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == "dose-430fx-piix-s3-atapi-pcibios-dev-v1" && original.Memory.Ram.Length == 32 * 1048576 && original.Cpu.TimestampFrequency == 100000000, "Wrong actual PCI BIOS profile.");
        byte[] actual = Enumerable.Range(0, 65536).Select(i => original.Memory.ReadByte(0xf0000u + (uint)i)).ToArray(); Check(actual.SequenceEqual(expected), "Actual entire firmware differs from separately prepared expected own ROM."); File.WriteAllBytes(Path.Combine(root, "firmware.bin"), actual);
        using var first = new Side(original, program, table, rom); var sides = new List<Side> { first }; var timer = Stopwatch.StartNew(); long total = 0, progress = 0;
        while (!(original.Video.ReadText().Contains("CDREADY", StringComparison.Ordinal) && Prompt(original, "C")) && original.Fault is null)
        { Require(timer.Elapsed.TotalSeconds < 180, root, first, "Original PCI BIOS DOS/CD boot timeout."); int n = first.Step(10000); Require(n > 0, root, first, "Original boot stopped."); total += n; if (total - progress >= 200000) { Progress(root, first, "boot", total, timer.Elapsed.TotalSeconds); progress = total; } }
        Require(original.Fault is null, root, first, "Original PCI BIOS boot fault."); string bootScreen = original.Video.ReadText();
        byte[] keys = input.GermanKeyboard ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rPCIBTEST.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rPCIBTEST.COM\r"); int sent = 0;
        while (!first.Ledger.Cut16)
        {
            Require(timer.Elapsed.TotalSeconds < input.FirstCutTimeoutSeconds && !first.Ledger.LimitReached && first.Ledger.Proof(12) == 0 && original.Fault is null,
                root, first, "Actual original16-bit firmware cut timeout/failure.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int n = first.Step(first.Ledger.ProgramBase == 0 ? 100 : 1); Require(n > 0, root, first, "Actual original BIOS16 guest stopped."); total += n;
            if (total - progress >= 200000) { Progress(root, first, "awaiting-bios16", total, timer.Elapsed.TotalSeconds); progress = total; }
        }
        Require(sent == keys.Length && first.Ledger.Origin16 is not null, root, first, "Genuine original firmware OUT origin absent.");
        Service pending16 = first.Ledger.PendingAtCut() ?? throw new InvalidDataException("Actual BIOS16 invocation missing.");
        Write(Path.Combine(root, "bios16-point.json"), first.Ledger.Point()); Write(Path.Combine(root, "bios16-origin.json"), first.Ledger.Origin16!);
        string save16 = Path.Combine(root, "bios16-cut.dossav"); var bios16Manifest = SnapshotStore.Capture(original, save16);
        using var restored16 = SnapshotStore.Load(save16, Path.Combine(root, "bios16-resumed.dosebranch")); using var second = new Side(restored16, program, table, rom, first.Ledger.ProgramBase); sides.Add(second);
        Service return16 = second.Ledger.Carry(pending16); int[] counts16 = Counts(first.Ledger); Equal(sides); first.FinishAudio(); long steps = 0; int comparisons = 1;
        while (!first.Ledger.Cut32)
        {
            Require(timer.Elapsed.TotalSeconds < 600 && steps < 100000000 && !first.Ledger.LimitReached && first.Ledger.Proof(12) == 0 && first.Ledger.Proof(200) == 0,
                root, first, "Actual original VCPI/BIOS32 cut timeout/failure.");
            int n = first.Step(1); Check(n > 0 && second.Step(1) == n && original.Fault is null && restored16.Fault is null, "Original BIOS16/VCPI future differs."); steps += n;
            if (steps % 2000 == 0) { Equal(sides); comparisons++; }
            if (total + steps - progress >= 200000) { Progress(root, first, "awaiting-bios32", total + steps, timer.Elapsed.TotalSeconds); progress = total + steps; }
        }
        Require(first.Ledger.Origin32 is not null && (original.Cpu.Eflags & X86Cpu.InterruptEnable) == 0, root, first, "Actual direct32-bit firmware IF0 cut absent."); Equal(sides);
        Check(pending16.After is not null && JsonSerializer.Serialize(pending16, Json) == JsonSerializer.Serialize(return16, Json), "Actual pending BIOS16 return differs.");
        var firstAudio = first.FinishAudio(); Check(firstAudio == second.FinishAudio(), "Actual first published future PCM differs.");
        Service pending32 = first.Ledger.PendingAtCut() ?? throw new InvalidDataException("Actual BIOS32 invocation missing."); Service second32 = second.Ledger.PendingAtCut() ?? throw new InvalidDataException("Restored actual BIOS32 invocation missing.");
        Write(Path.Combine(root, "bios32-point.json"), first.Ledger.Point()); Write(Path.Combine(root, "bios32-origin.json"), first.Ledger.Origin32!);
        string save32 = Path.Combine(root, "bios32-cut.dossav"); var bios32Manifest = SnapshotStore.Capture(original, save32);
        using var restored32 = SnapshotStore.Load(save32, Path.Combine(root, "bios32-resumed.dosebranch")); using var third = new Side(restored32, program, table, rom, first.Ledger.ProgramBase); sides.Add(third);
        Service return32 = third.Ledger.Carry(pending32); int[] counts32 = Counts(first.Ledger), secondCounts32 = Counts(second.Ledger); Equal(sides);
        /// <summary>Accepts only an actual successful own exit and completed original DOS prompt.</summary>
        bool Complete(Side side) => side.Exited && Prompt(side.Machine, "A");
        while (!sides.All(Complete))
        {
            Require(timer.Elapsed.TotalSeconds < 900 && steps < 200000000 && !first.Ledger.LimitReached && first.Ledger.Proof(12) == 0 && first.Ledger.Proof(200) == 0,
                root, first, "Actual original three-PC PCI BIOS/DOS return timeout/failure.");
            int budget = first.Ledger.Proof(10) >= 10 ? 1000 : 1; int n = first.Step(budget);
            foreach (var side in sides.Skip(1)) Check(side.Step(budget) == n && side.Machine.Fault is null, "Actual BIOS32/DOS future differs."); Check(n > 0 && original.Fault is null, "Original future stopped."); steps += n;
            if (steps % 2000 < n || sides.All(Complete)) { Equal(sides); comparisons++; }
            if (total + steps - progress >= 200000) { Progress(root, first, "awaiting-original-dos", total + steps, timer.Elapsed.TotalSeconds); progress = total + steps; }
        }
        string beforeEcho = original.Video.ReadText(); Check(!beforeEcho.Contains("PCIBCONTINUED", StringComparison.Ordinal), "Fresh PCI echo already visible."); var echoAt = new { ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count, nanoseconds = original.Scheduler.Now };
        byte[] echo = GuestKeyboard.Encode("ECHO PCIBCONTINUED\r"); int echoed = 0;
        /// <summary>Requires the new executed original ECHO line and coherent following cursor.</summary>
        bool EchoDone(Side side) => side.Machine.Video.ReadText().Contains("\nPCIBCONTINUED", StringComparison.Ordinal) && Prompt(side.Machine, "A");
        while (!sides.All(EchoDone))
        {
            Require(timer.Elapsed.TotalSeconds < 960 && steps < 220000000, root, first, "Actual new post-PCI BIOS original ECHO timeout.");
            if (echoed < echo.Length) { bool accepted = GuestKeyboard.TryInjectForBios(original, echo[echoed]); foreach (var side in sides.Skip(1)) Check(GuestKeyboard.TryInjectForBios(side.Machine, echo[echoed]) == accepted, "Original KBC admission differs."); if (accepted) echoed++; }
            int n = first.Step(1000); foreach (var side in sides.Skip(1)) Check(side.Step(1000) == n && side.Machine.Fault is null, "Actual original echo future differs."); steps += n;
        }
        Check(echoed == echo.Length, "Original echo input unfinished."); Equal(sides); foreach (var side in sides) side.Machine.Audio.Flush(); Equal(sides);
        var audio = first.FinishAudio(); var restoredAudio = new[] { second.FinishAudio(), third.FinishAudio() }; Check(restoredAudio.All(x => x == audio), "Actual future published PCM differs.");
        Check(pending32.After is not null && JsonSerializer.Serialize(pending32, Json) == JsonSerializer.Serialize(second32, Json) && JsonSerializer.Serialize(pending32, Json) == JsonSerializer.Serialize(return32, Json), "Actual pending BIOS32 return differs on all three PCs.");
        Future(first.Ledger, counts16, second.Ledger, new int[4], counts32, secondCounts32); Future(first.Ledger, counts32, third.Ledger, new int[4], Counts(first.Ledger), Counts(third.Ledger)); Equal(sides);
        string? encodedMedia = null;
        for (int i = 0; i < sides.Count; i++) { var media = MediaCheckpointStore.Capture(sides[i].Machine, Path.Combine(root, "final-" + i + ".dosemedia")); string encoded = JsonSerializer.Serialize(media.Media, StateValues.Json); if (encodedMedia is not null) Check(encoded == encodedMedia, "Actual complete media generations differ."); encodedMedia = encoded; }
        byte[] final = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), final); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, original = first.Ledger, bios16Future = second.Ledger, bios32Future = third.Ledger, counts16, counts32, secondCounts32,
            carried16 = new[] { pending16, return16 }, carried32 = new[] { pending32, second32, return32 } });
        Write(Path.Combine(root, "result.json"), new { passed = true, system = input.System, returnedToDos = true, postPciBiosEcho = true, exits = sides.Select(x => x.Exited).ToArray(), programBase = first.Ledger.ProgramBase,
            programSha256 = Hash(program), firmwareSha256 = Hash(actual), rawFirmwareSha256 = Hash(raw), isoSha256 = Hash(File.ReadAllBytes(input.Iso)), bios16Manifest, bios32Manifest, comparisons, futureSteps = steps,
            bios16StateSha256 = Hash(File.ReadAllBytes(Path.Combine(save16, "state.json"))), bios16RamSha256 = Hash(File.ReadAllBytes(Path.Combine(save16, "ram.bin"))),
            bios32StateSha256 = Hash(File.ReadAllBytes(Path.Combine(save32, "state.json"))), bios32RamSha256 = Hash(File.ReadAllBytes(Path.Combine(save32, "ram.bin"))), finalStateSha256 = Hash(final), finalRamSha256 = Hash(original.Memory.Ram),
            bootScreen, beforeEcho, echoAt, echoBytes = echoed, firstAudio, audioFuture = audio, restoredAudioFuture = restoredAudio, finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Console.WriteLine("Actual original DOS PCI BIOS/BIOS32 three-PC continuation passed; independent oracle remains required."); return 0;
    }
}
