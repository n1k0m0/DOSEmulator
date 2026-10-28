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

/// <summary>Original DOS plus original MSCDEX filesystem requests with genuine resident SYS and complete PC continuations.</summary>
internal static class MscdexGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System, string Iso, string Driver, string Mscdex, int FirstCutTimeoutSeconds = 120);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0, uint Cr3,
        bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Word(ushort Value, bool Write, ulong Nanoseconds, long Instructions, uint Eip, ushort Cs, int Phase);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Service(int Offset, byte Vector, Point Before)
    {
        /// <summary>Actual original DOS or MSCDEX client return before its first result instruction executes.</summary>
        public Point? After { get; set; }
        /// <summary>Actual bytes at a successful original DOS/MSCDEX read return, before COM buffers can be reused.</summary>
        public byte[]? Data { get; set; }
    }
    private sealed record Critical(Point Entry, byte[] Frame)
    {
        /// <summary>Actual own AL3 instruction result immediately before the original critical-error IRET.</summary>
        public Point? Commit { get; set; }
        /// <summary>Actual original DOS return immediately after that IRET completes.</summary>
        public Point? After { get; set; }
    }
    private sealed record PcmDigest(long Frames, string Sha256);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects a missing real guest effect without changing guest state.</summary>
    private static void Check(bool value, string why) { if (!value) throw new InvalidDataException(why); }
    /// <summary>Serializes every registered raw field and VRAM block without changing the machine.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only one explicitly supplied own byte extent.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes bounded text metadata rather than screenshots or recordings.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    /// <summary>Loads fixed own COM labels rather than inferring variable data from debug messages.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("MSCMETA1"u8);
        Check(at >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8, 2)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 10, 2)) == 68, "Own MSCDEX metadata absent.");
        return Enumerable.Range(0, 68).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 12 + i * 2, 2))).ToArray();
    }
    /// <summary>Captures actual CPU and integer clock at the current ordinary owner boundary.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] table)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(), m.Scheduler.ClockTicks(c.TimestampFrequency), c.TimestampCounter,
            c.Eflags, c.Cr0, c.Cr3, c.Virtual8086, c.Eip, Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(), Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(),
            basis == 0 ? 0 : m.Memory.ReadWord(basis + table[0] + 10u));
    }
    /// <summary>Reads the actual original DOS critical-error frame through unchanged classic original page tables.</summary>
    private static byte[] CriticalFrame(PcMachine m)
    {
        var c = m.Cpu; Check(c.Virtual8086 || (c.Cr0 & 1) == 0, "Own critical-error callback is not original real/V86 DOS.");
        uint linear = c.GetSegmentBase(Segment.Ss) + (c.GetRegister(Register.Sp) & 65535); byte[] frame = new byte[6];
        for (uint i = 0; i < frame.Length; i++)
        {
            uint physical = linear + i;
            if (c.PagingEnabled)
            {
                uint pde = m.Memory.ReadDword((c.Cr3 & 0xfffff000) + (physical >> 22) * 4); Check((pde & 0x81) == 1, "Critical-error original PDE missing.");
                uint pte = m.Memory.ReadDword((pde & 0xfffff000) + ((physical >> 12) & 1023) * 4); Check((pte & 1) != 0, "Critical-error original PTE missing.");
                physical = (pte & 0xfffff000) | (physical & 4095);
            }
            Check(physical < m.Memory.Ram.Length, "Critical-error original frame outside RAM."); frame[i] = m.Memory.Ram[physical];
        }
        return frame;
    }
    /// <summary>Read-only resident-service, physical port and IRQ observations for one complete PC.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint basis = 0)
    {
        /// <summary>Actual original client PSP base inferred from immutable fetched program bytes.</summary>
        public uint ProgramBase { get; private set; } = basis;
        /// <summary>Real taskfile, PIC and KBC accesses, excluding alternate-status idle polling.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Every actual cooked device data word and every CDB word.</summary>
        public List<Word> Words { get; } = [];
        /// <summary>Actual physical PIC acknowledgements, with no injected interrupt.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Original resident DOS and MSCDEX calls and exact client returns.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Actual own DOS critical-error callbacks, their real frames and completed IRET returns.</summary>
        public List<Critical> Criticals { get; } = [];
        /// <summary>Independently observed return for a real request already in flight when this observer attached.</summary>
        public Service? CarriedService { get; private set; }
        /// <summary>Returns the actual existing pending service without adding or changing an observation.</summary>
        public Service? PendingAtCut() => _pending;
        /// <summary>Carries only passive pre-call metadata; the restored CPU must still execute and publish its own actual return.</summary>
        public Service Carry(Service source)
        {
            Check(_pending is null && source.After is null, "Cannot carry an absent/completed or overlapping service.");
            _pending = new(source.Offset, source.Vector, source.Before); CarriedService = _pending; return _pending;
        }
        /// <summary>Whether retained diagnostics reached the fixed aggregate bound.</summary>
        public bool LimitReached { get; private set; }
        private Service? _pending;
        private Critical? _activeCritical, _criticalIret;
        /// <summary>Bounds all retained records before another point allocation.</summary>
        private bool Reserve()
        {
            if (LimitReached) return false;
            if ((long)Ports.Count + Words.Count + Acks.Count + Services.Count + Criticals.Count < 100000) return true;
            LimitReached = true; return false;
        }
        /// <summary>Returns one original client proof word without mutation.</summary>
        public ushort Proof(int offset) => ProgramBase == 0 ? (ushort)0 : m.Memory.ReadWord(ProgramBase + table[0] + (uint)offset);
        /// <summary>Captures actual original or restored CPU state.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Records real CD data effects and selected source/acknowledgement ports.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (ProgramBase == 0 || LimitReached) return;
            if (address == 0x170 && width == 2)
            {
                if (Reserve()) Words.Add(new((ushort)value, write, m.Scheduler.Now, m.Cpu.Instructions, m.Cpu.Eip, m.Cpu.GetSegment(Segment.Cs), Proof(10)));
                return;
            }
            if (!(address is >= 0x171 and <= 0x177 || address is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x60 or 0x64 || address == 0x376 && write)) return;
            if (address == 0x64 && !write) return;
            if (Reserve()) Ports.Add(new(address, value, width, write, Point()));
        }
        /// <summary>Records the actual physical acknowledgement before CPU interrupt delivery.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) { if (ProgramBase != 0 && Reserve()) Acks.Add(new(irq, vector, spurious, Point())); }
        /// <summary>Associates only original client INT instructions and their actual next-instruction returns.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            uint found = m.Cpu.GetSegmentBase(Segment.Cs);
            if (physical < found || physical >= found + table[2] || found + 0x110 >= m.Memory.Ram.Length ||
                !m.Memory.Ram.AsSpan((int)found + 256, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = found; int offset = (int)(physical - found);
            if (offset == table[52] && Reserve()) { _activeCritical = new(Point(), CriticalFrame(m)); Criticals.Add(_activeCritical); }
            if (offset == table[53] && _activeCritical is not null) { _activeCritical.Commit = Point(); _criticalIret = _activeCritical; }
            if (_pending is not null && offset == _pending.Offset + 2)
            {
                Service current = _pending; Point after = Point(); current.After = after; _pending = null;
                if (current.Vector == 0x21 && ((current.Before.Registers[0] >> 8) & 255) == 0x3f && (after.Flags & 1) == 0)
                {
                    int count = (int)(after.Registers[0] & 65535); uint buffer = current.Before.Bases[3] + (current.Before.Registers[2] & 65535);
                    Check(count <= 16384 && buffer <= m.Memory.Ram.Length - count, "Original DOS readback outside bounded client RAM.");
                    current.Data = m.Memory.Ram.AsSpan((int)buffer, count).ToArray();
                }
                else if (current.Offset == table[40] && (m.Memory.ReadWord(found + table[4] + 3u) & 0x8100) == 0x100)
                    current.Data = m.Memory.Ram.AsSpan((int)found + table[11], 2048).ToArray();
            }
            if (opcode != 0xcd || !Reserve()) return;
            byte vector = m.Memory.ReadByte(physical + 1);
            if (vector is not (0x21 or 0x2f)) return;
            _pending = new(offset, vector, Point()); Services.Add(_pending);
        }
        /// <summary>Records the actual next original DOS instruction boundary after the own IRET.</summary>
        public void AfterStep()
        {
            if (_criticalIret is not null) { _criticalIret.After = Point(); _criticalIret = null; }
        }
    }
    /// <summary>One actual machine with passive removable observations and published PCM digest.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>Complete original or genuinely SnapshotStore-restored PC.</summary>
        public PcMachine Machine { get; }
        /// <summary>Actual original or future side effects.</summary>
        public Ledger Ledger { get; }
        /// <summary>True only after observing the own successful original DOS exit.</summary>
        public bool Exited { get; private set; }
        private readonly IncrementalHash _audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _frames;
        /// <summary>Attaches read-only hooks to an already constructed complete PC.</summary>
        public Side(PcMachine machine, byte[] program, ushort[] table, uint basis = 0)
        {
            Machine = machine; Ledger = new(machine, program, table, basis);
            machine.Ports.Accessed += Ledger.Access; machine.Pic.InterruptAcknowledged += Ledger.Acknowledge;
            machine.Cpu.InstructionStarting += Ledger.Instruction; machine.Cpu.InstructionStarting += Exit; machine.Audio.AudioReady += Audio;
        }
        /// <summary>Recognizes a successful own AH4C instruction independently of upper EAX bits.</summary>
        private void Exit(uint physical, byte opcode)
        {
            if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && opcode == 0xcd &&
                Machine.Memory.ReadByte(physical + 1) == 0x21 && (Machine.Cpu.GetRegister(Register.Ax) & 65535) == 0x4c00) Exited = true;
        }
        /// <summary>Hashes real published PCM only; no host endpoint is opened.</summary>
        private void Audio(AudioBlock block) { _audio.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); _frames += block.Samples.Length / 2; }
        /// <summary>Separates actual future publication intervals without altering mixer state.</summary>
        public PcmDigest FinishAudio() { var result = new PcmDigest(_frames, Convert.ToHexString(_audio.GetHashAndReset())); _frames = 0; return result; }
        /// <summary>Executes ordinary CPU/scheduler work and rejects stopped machines.</summary>
        public int Step(int budget) { int count = Machine.RunSteps(budget); Ledger.AfterStep(); return count; }
        /// <summary>Removes passive hooks without disposing the machine itself.</summary>
        public void Dispose()
        {
            Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge;
            Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit; Machine.Audio.AudioReady -= Audio; _audio.Dispose();
        }
    }
    /// <summary>Persists textual CPU/proof and raw RAM/state diagnostics for a bounded failure.</summary>
    private static void Require(bool value, string root, Side side, string why)
    {
        if (value) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), optical = side.Machine.Atapi!.CapturePhase(),
            screen = side.Machine.Video.ReadText(), fault = side.Machine.Fault?.ToString(), lastPorts = side.Ledger.Ports.TakeLast(24), lastWords = side.Ledger.Words.TakeLast(24),
            lastServices = side.Ledger.Services.TakeLast(12), proofPhase = side.Ledger.Proof(10), proofError = side.Ledger.Proof(12) });
        File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine)); File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram);
        throw new InvalidDataException(why + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Reports bounded progress with no image or native action.</summary>
    private static void Progress(string root, Side side, string stage, long steps, double seconds) => Write(Path.Combine(root, "progress.json"),
        new { stage, steps, seconds, point = side.Ledger.Point(), optical = side.Machine.Atapi!.CapturePhase(), words = side.Ledger.Words.Count, ports = side.Ledger.Ports.Count, screen = side.Machine.Video.ReadText(), fault = side.Machine.Fault?.ToString() });
    /// <summary>Compares every raw component field, all VRAM and every installed RAM byte.</summary>
    private static void Equal(IEnumerable<Side> sides)
    {
        var all = sides.ToArray(); byte[] state = State(all[0].Machine); byte[] ram = all[0].Machine.Memory.Ram;
        foreach (var side in all.Skip(1)) Check(state.SequenceEqual(State(side.Machine)) && ram.SequenceEqual(side.Machine.Memory.Ram), "Entire MSCDEX graph/RAM differs.");
    }
    /// <summary>Requires the completed four-character DOS prompt and coherent BDA/CRTC cursor.</summary>
    private static bool PromptReady(PcMachine m, string drive)
    {
        string[] lines = m.Video.ReadText().Replace("\r", "", StringComparison.Ordinal).TrimEnd().Split('\n'); int row = lines.Length - 1;
        if (row < 0 || lines[row] != drive + ":\\>" || m.Memory.ReadByte(0x450) != 4 || m.Memory.ReadByte(0x451) != row) return false;
        byte[] crtc = m.Video.SaveState().Fields["_crtc"].GetBytesFromBase64(); return ((crtc[14] << 8) | crtc[15]) == row * 80 + 4;
    }
    /// <summary>Keeps exact retained list boundaries for an actual snapshot.</summary>
    private static int[] Counts(Ledger l) => [l.Ports.Count, l.Words.Count, l.Acks.Count, l.Services.Count, l.Criticals.Count];
    /// <summary>Compares every real future port/word/IRQ/service record at matching saved boundaries.</summary>
    private static void Future(Ledger a, int[] beginA, Ledger b, int[] beginB, int[] endA, int[] endB)
    {
        object[] left = [a.Ports, a.Words, a.Acks, a.Services, a.Criticals], right = [b.Ports, b.Words, b.Acks, b.Services, b.Criticals];
        for (int i = 0; i < left.Length; i++)
        {
            var x = ((System.Collections.IEnumerable)left[i]).Cast<object>().Skip(beginA[i]).Take(endA[i] - beginA[i]);
            var y = ((System.Collections.IEnumerable)right[i]).Cast<object>().Skip(beginB[i]).Take(endB[i] - beginB[i]);
            Check(JsonSerializer.Serialize(x, Json) == JsonSerializer.Serialize(y, Json), "Actual MSCDEX future ledger differs: " + i);
        }
    }
    /// <summary>Runs original DOS/MSCDEX, captures real partial request/data phases and resumes all three PCs to a fresh command.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(input.FirstCutTimeoutSeconds is >= 10 and <= 900, "First-cut timeout outside 10..900 seconds.");
        foreach (string selected in new[] { input.Machine, input.Output, input.Probe, input.Iso, input.Driver, input.Mscdex }) Check(!selected.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal overlay.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == "dose-430fx-piix-s3-atapi-dev-v1" && original.Memory.Ram.Length == 32 * 1048576 && original.Atapi is not null && original.Cpu.TimestampFrequency == 100000000, "Wrong actual CD profile.");
        using var first = new Side(original, program, table); var sides = new List<Side> { first }; long total = 0, bootProgress = 0;
        while (!(original.Video.ReadText().Contains("CDREADY", StringComparison.Ordinal) && PromptReady(original, "C")) && original.Fault is null)
        {
            Require(timer.Elapsed.TotalSeconds < 180, root, first, "Original MSCDEX boot timeout."); int n = first.Step(10000);
            Require(n > 0, root, first, "Original DOS/SYS boot stopped."); total += n;
            if (total - bootProgress >= 200000) { Progress(root, first, "original-sys-mscdex-boot", total, timer.Elapsed.TotalSeconds); bootProgress = total; }
        }
        Require(original.Fault is null && PromptReady(original, "C"), root, first, "Original DOS/MSCDEX boot not completed."); string bootScreen = original.Video.ReadText();
        byte[] keys = input.GermanKeyboard ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rMSCDTEST.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rMSCDTEST.COM\r");
        int sent = 0; long progressAt = total, checkedAt = total;
        while (!(first.Ledger.Proof(10) == 8 && original.Atapi!.Phase == AtapiPhase.PacketOut && original.Atapi.CapturePhase().PacketOffset == 10) && original.Fault is null)
        {
            Require(timer.Elapsed.TotalSeconds < input.FirstCutTimeoutSeconds && !first.Ledger.LimitReached, root, first, "Original MSCDEX partial CDB timeout/diagnostic bound.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int n = first.Step(first.Ledger.ProgramBase == 0 ? 100 : 1); Check(n > 0, "Original CD client stopped."); total += n;
            if (total - checkedAt >= 10000) { Require(first.Ledger.Proof(12) == 0 && !first.Exited && !original.Video.ReadText().Contains("MSCDEX-FAILED", StringComparison.Ordinal), root, first, "Own original CD client failed before capture."); checkedAt = total; }
            if (total - progressAt >= 200000) { Progress(root, first, "awaiting-real-cdb", total, timer.Elapsed.TotalSeconds); progressAt = total; }
        }
        Require(sent == keys.Length && original.Fault is null && first.Ledger.Proof(10) == 8 && original.Atapi!.CapturePhase().PacketOffset == 10, root, first, "Actual partial original SYS CDB missing.");
        Service pendingOriginal = first.Ledger.PendingAtCut() ?? throw new InvalidDataException("Original resident1510 request has no actual pending client call.");
        Check(pendingOriginal.Offset == table[40] && pendingOriginal.Before.Phase == 8 && pendingOriginal.After is null, "Wrong actual original pending1510 call.");
        Write(Path.Combine(root, "packet-point.json"), first.Ledger.Point()); string packetSave = Path.Combine(root, "packet-cut.dossav"); var packetManifest = SnapshotStore.Capture(original, packetSave);
        using var packetMachine = SnapshotStore.Load(packetSave, Path.Combine(root, "packet-resumed.dosebranch")); using var packet = new Side(packetMachine, program, table, first.Ledger.ProgramBase); sides.Add(packet); Service pendingPacket = packet.Ledger.Carry(pendingOriginal);
        int[] packetCounts = Counts(first.Ledger); Equal(sides); first.FinishAudio(); long steps = 0; int comparisons = 1;
        while (!(original.Atapi!.Phase == AtapiPhase.DataIn && original.Atapi.CapturePhase().Offset == 128) && original.Fault is null)
        {
            Require(timer.Elapsed.TotalSeconds < 300 && steps < 20000000 && !first.Ledger.LimitReached, root, first, "Actual resident SYS partial DataIn timeout.");
            int n = first.Step(1); Check(n > 0 && packet.Step(1) == n && packetMachine.Fault is null, "Actual CDB future differs."); steps += n;
            if (steps % 256 == 0) { Equal(sides); comparisons++; }
        }
        Require(first.Ledger.Proof(10) == 8 && original.Atapi!.CapturePhase().Offset == 128, root, first, "Actual original SYS DataIn128 missing."); Equal(sides);
        var packetAudio = first.FinishAudio(); Check(packetAudio == packet.FinishAudio(), "Actual first PCM future differs.");
        Write(Path.Combine(root, "data-point.json"), first.Ledger.Point()); string dataSave = Path.Combine(root, "data-cut.dossav"); var dataManifest = SnapshotStore.Capture(original, dataSave);
        using var dataMachine = SnapshotStore.Load(dataSave, Path.Combine(root, "data-resumed.dosebranch")); using var data = new Side(dataMachine, program, table, first.Ledger.ProgramBase); sides.Add(data); Service pendingData = data.Ledger.Carry(pendingOriginal);
        int[] dataCounts = Counts(first.Ledger), packetDataCounts = Counts(packet.Ledger); Equal(sides);
        /// <summary>Accepts only a successful own DOS exit followed by the complete original prompt.</summary>
        bool Complete(Side side) => side.Exited && PromptReady(side.Machine, "A");
        long continuationCheckAt = steps;
        while (!sides.All(Complete))
        {
            Require(timer.Elapsed.TotalSeconds < 600 && steps < 200000000 && !first.Ledger.LimitReached, root, first, "Original DOS/MSCDEX continuation timeout.");
            Require(first.Ledger.Proof(12) == 0, root, first, "Own original CD client reported failure after capture.");
            int budget = first.Ledger.Proof(10) >= 10 ? 1000 : 1; int n = first.Step(budget);
            foreach (var side in sides.Skip(1)) Check(side.Step(budget) == n && side.Machine.Fault is null, "Actual data future differs.");
            Check(n > 0 && original.Fault is null, "Original resident continuation stopped."); steps += n;
            if (steps - continuationCheckAt >= 10000)
            {
                Require(first.Ledger.Proof(12) == 0 && !original.Video.ReadText().Contains("MSCDEX-FAILED", StringComparison.Ordinal), root, first, "Own original CD client failed during continuation.");
                continuationCheckAt = steps;
            }
            if (steps % 2000 < n || sides.All(Complete)) { Equal(sides); comparisons++; }
            if (total + steps - progressAt >= 200000) { Progress(root, first, "awaiting-real-dos-return", total + steps, timer.Elapsed.TotalSeconds); progressAt = total + steps; }
        }
        string beforeEcho = original.Video.ReadText(); Check(!beforeEcho.Contains("MSCDEXCONTINUED", StringComparison.Ordinal), "Fresh echo already visible.");
        var echoAt = new { nanoseconds = original.Scheduler.Now, ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count };
        byte[] echo = GuestKeyboard.Encode("ECHO MSCDEXCONTINUED\r"); int echoed = 0;
        /// <summary>Requires the new output line and a coherently completed subsequent prompt.</summary>
        bool EchoDone(Side side) => side.Machine.Video.ReadText().Contains("\nMSCDEXCONTINUED", StringComparison.Ordinal) && PromptReady(side.Machine, "A");
        while (!sides.All(EchoDone))
        {
            Require(timer.Elapsed.TotalSeconds < 660 && steps < 220000000 && !first.Ledger.LimitReached, root, first, "Original post-CD ECHO timeout.");
            if (echoed < echo.Length)
            {
                bool accepted = GuestKeyboard.TryInjectForBios(original, echo[echoed]);
                foreach (var side in sides.Skip(1)) Check(GuestKeyboard.TryInjectForBios(side.Machine, echo[echoed]) == accepted, "Actual KBC future admission differs.");
                if (accepted) echoed++;
            }
            int n = first.Step(1000); foreach (var side in sides.Skip(1)) Check(side.Step(1000) == n && side.Machine.Fault is null, "Actual ECHO future differs."); steps += n;
        }
        Check(echoed == echo.Length, "Actual echo input unfinished."); Equal(sides); foreach (var side in sides) side.Machine.Audio.Flush(); Equal(sides);
        var audio = first.FinishAudio(); var restoredAudio = new[] { packet.FinishAudio(), data.FinishAudio() }; Check(restoredAudio.All(x => x == audio), "Actual published future PCM differs.");
        Check(pendingOriginal.After is not null && pendingOriginal.Data?.Length == 2048 &&
            JsonSerializer.Serialize(pendingOriginal, Json) == JsonSerializer.Serialize(pendingPacket, Json) &&
            JsonSerializer.Serialize(pendingOriginal, Json) == JsonSerializer.Serialize(pendingData, Json), "Actual pending1510 return/data differs across all three original/restored PCs.");
        Equal(sides); Future(first.Ledger, packetCounts, packet.Ledger, new int[5], dataCounts, packetDataCounts);
        Future(first.Ledger, dataCounts, data.Ledger, new int[5], Counts(first.Ledger), Counts(data.Ledger));
        string? mediaEncoding = null;
        for (int i = 0; i < sides.Count; i++)
        {
            var media = MediaCheckpointStore.Capture(sides[i].Machine, Path.Combine(root, "final-" + i + ".dosemedia")); string value = JsonSerializer.Serialize(media.Media, StateValues.Json);
            if (mediaEncoding is not null) Check(value == mediaEncoding, "Actual final media generations differ."); mediaEncoding = value;
        }
        byte[] finalState = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), finalState); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, original = first.Ledger, packetFuture = packet.Ledger, dataFuture = data.Ledger, packetCounts, dataCounts, packetDataCounts, carriedService = pendingOriginal, restoredCarriedServices = new[] { pendingPacket, pendingData } });
        Write(Path.Combine(root, "result.json"), new { passed = true, system = input.System, returnedToDos = true, postMscdexEcho = true, exits = sides.Select(x => x.Exited).ToArray(),
            programBase = first.Ledger.ProgramBase, programSha256 = Hash(program), isoSha256 = Hash(File.ReadAllBytes(input.Iso)), driverSha256 = Hash(File.ReadAllBytes(input.Driver)), mscdexSha256 = Hash(File.ReadAllBytes(input.Mscdex)),
            packetManifest, dataManifest, comparisons, futureSteps = steps, packetStateSha256 = Hash(File.ReadAllBytes(Path.Combine(packetSave, "state.json"))),
            packetRamSha256 = Hash(File.ReadAllBytes(Path.Combine(packetSave, "ram.bin"))), dataStateSha256 = Hash(File.ReadAllBytes(Path.Combine(dataSave, "state.json"))),
            dataRamSha256 = Hash(File.ReadAllBytes(Path.Combine(dataSave, "ram.bin"))), finalStateSha256 = Hash(finalState), finalRamSha256 = Hash(original.Memory.Ram),
            bootScreen, beforeEcho, echoAt, echoBytes = echoed, packetAudio, audioFuture = audio, restoredAudioFuture = restoredAudio,
            finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Progress(root, first, "completed", total + steps, timer.Elapsed.TotalSeconds); Console.WriteLine("Actual original MSCDEX/DOS three-PC continuation passed; independent oracle remains required."); return 0;
    }
}
