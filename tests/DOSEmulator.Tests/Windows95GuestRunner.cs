// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Persistent headless owner controller using only real guest execution and virtual KBC/PS2 input.</summary>
internal static class Windows95GuestRunner
{
    private sealed record Input(string? Machine, string? Snapshot, string? Branch, string Output, int TraceCapacity = 0, int ProgressMilliseconds = 1000);
    private sealed record Point(ulong Nanoseconds, long Instructions, string Position, uint LinearInstructionAddress, uint PhysicalInstructionAddress,
        uint Flags, uint Cr0, uint Cr2, uint Cr3, uint Cr4, bool V86, bool Halted, bool TripleFaulted, uint[] Registers, ushort[] Segments, uint[] Bases);
    private sealed record TraceRow(long Sequence, string Kind, ulong Nanoseconds, long Instructions, ushort Cs, uint Eip, ushort Ss, uint Esp,
        uint Flags, uint Cr0, uint Cr2, uint Cr3, int Address, uint Value, int Width, bool Write, int Irq, bool Spurious, uint? ExceptionCode = null, string? ExceptionCodeEvidence = null);
    private static readonly JsonSerializerOptions Json = new(IpcProtocol.JsonOptions) { IncludeFields = true };
    private const long MaximumSteps = 10_000_000_000;
    private const int MaximumLineCharacters = 65536;

    /// <summary>Rejects malformed controller requests before hardware or artifact effects.</summary>
    private static void Check(bool value, string why) { if (!value) throw new ArgumentException(why); }

    /// <summary>Reads one required or defaulted integral field without accepting floats, strings or overflowing numbers.</summary>
    private static long Number(JsonElement request, string name, long minimum, long maximum, long? fallback = null)
    {
        if (!request.TryGetProperty(name, out var value))
        {
            if (fallback.HasValue) return fallback.Value;
            throw new ArgumentException("Missing integer property: " + name);
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < minimum || result > maximum)
            throw new ArgumentException($"Property '{name}' must be an integer in {minimum}..{maximum}.");
        return result;
    }

    /// <summary>Reads one optional bounded textual property, retaining null as absence.</summary>
    private static string? OptionalText(JsonElement request, string name, int maximum = 4096)
    {
        if (!request.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Check(value.ValueKind == JsonValueKind.String, "Property must be text: " + name);
        string result = value.GetString()!;
        Check(result.Length is > 0 && result.Length <= maximum, "Empty or oversized text property: " + name);
        return result;
    }

    /// <summary>Captures only architectural diagnostics at the current owner boundary.</summary>
    private static Point Observe(PcMachine machine)
    {
        var cpu = machine.Cpu;
        return new(machine.Scheduler.Now, cpu.Instructions, cpu.Position, cpu.LinearInstructionAddress, cpu.PhysicalInstructionAddress,
            cpu.Eflags, cpu.Cr0, cpu.Cr2, cpu.Cr3, cpu.Cr4, cpu.Virtual8086, cpu.Halted, cpu.TripleFaulted,
            Enumerable.Range(0, 8).Select(i => cpu.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => cpu.GetSegment((Segment)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => cpu.GetSegmentBase((Segment)i)).ToArray());
    }

    /// <summary>Optional bounded passive event tail; capacity zero attaches no CPU, PIC or port callbacks.</summary>
    private sealed class Trace : IDisposable
    {
        private readonly PcMachine _machine;
        private readonly TraceRow?[] _rows;
        private long _sequence, _ports, _interrupts, _acknowledgements, _lastPageFaults;
        private int _pendingException = -1, _pendingExceptionWidth;
        private string? _pendingExceptionEvidence;

        /// <summary>Allocates at most the explicitly requested ring and attaches no instruction-start observer.</summary>
        public Trace(PcMachine machine, int capacity)
        {
            _machine = machine; _rows = new TraceRow?[capacity]; _lastPageFaults = machine.Cpu.PageFaults;
            if (capacity == 0) return;
            machine.Ports.Accessed += Port;
            machine.Cpu.InterruptEntered += Interrupt;
            machine.Pic.InterruptAcknowledged += Ack;
        }

        /// <summary>Stores a compact actual event in a fixed ring without accumulating full CPU-state graphs.</summary>
        private void Add(string kind, int address, uint value, int width, bool write, int irq = -1, bool spurious = false, uint? exceptionCode = null, string? evidence = null)
        {
            var cpu = _machine.Cpu; long sequence = ++_sequence;
            _rows[(int)((sequence - 1) % _rows.Length)] = new(sequence, kind, _machine.Scheduler.Now, cpu.Instructions,
                cpu.GetSegment(Segment.Cs), cpu.Eip, cpu.GetSegment(Segment.Ss), cpu.GetRegister(Register.Sp), cpu.Eflags,
                cpu.Cr0, cpu.Cr2, cpu.Cr3, address, value, width, write, irq, spurious, exceptionCode, evidence);
        }

        /// <summary>Records actual completed guest IN/OUT effects only when tracing was explicitly enabled.</summary>
        private void Port(ushort address, uint value, int width, bool write)
        { _ports++; Add("port", address, value, width, write); }

        /// <summary>Records the CPU delivery observer, which may precede installation of the new handler context.</summary>
        private void Interrupt(byte vector)
        {
            _interrupts++; Add("interrupt-delivery", vector, vector, 0, false);
            _machine.Cpu.InstructionStarting -= FirstHandlerInstruction; _pendingException = -1;
            var cpu = _machine.Cpu;
            if (cpu.ProtectedMode && vector is 13 or 14 && (uint)vector * 8 + 7 <= cpu.Idtr.Limit &&
                TryLinear(unchecked(cpu.Idtr.Base + (uint)vector * 8), 8, out var gate))
            {
                int kind = gate[5] & 15;
                if (kind is 6 or 7 or 14 or 15)
                {
                    _pendingException = vector; _pendingExceptionWidth = kind >= 14 ? 4 : 2;
                    _pendingExceptionEvidence = vector == 14 && cpu.PageFaults > _lastPageFaults
                        ? "actual-page-fault-frame" : "handler-stack-top-candidate; delivery hook does not expose software/fault origin";
                    // The additional instruction callback exists for exactly the next fetched handler instruction.
                    cpu.InstructionStarting += FirstHandlerInstruction;
                }
            }
            _lastPageFaults = cpu.PageFaults;
        }

        /// <summary>Inspects mapped RAM/ROM without changing paging A/D bits or reading device ports/MMIO.</summary>
        private bool TryLinear(uint address, int count, out byte[] bytes)
        {
            bytes = new byte[count]; var cpu = _machine.Cpu; var memory = _machine.Memory;
            for (int i = 0; i < count; i++)
            {
                uint physical = unchecked(address + (uint)i);
                if (cpu.PagingEnabled)
                {
                    uint directoryAddress = (cpu.Cr3 & 0xfffff000) + (physical >> 22) * 4;
                    if (!memory.TryGetRamSpan(directoryAddress, 4, out var directoryBytes)) return false;
                    uint pde = BinaryPrimitives.ReadUInt32LittleEndian(directoryBytes);
                    if ((pde & 0x81) != 1) return false;
                    uint entryAddress = (pde & 0xfffff000) + ((physical >> 12) & 1023) * 4;
                    if (!memory.TryGetRamSpan(entryAddress, 4, out var entryBytes)) return false;
                    uint pte = BinaryPrimitives.ReadUInt32LittleEndian(entryBytes);
                    if ((pte & 1) == 0) return false;
                    physical = (pte & 0xfffff000) | (physical & 4095);
                }
                if (!memory.TryPeekByte(physical, out bytes[i])) return false;
            }
            return true;
        }

        /// <summary>Reads the real new handler's stack top after entry, with explicit evidence limits for ambiguous GP vectors.</summary>
        private void FirstHandlerInstruction(uint physical, byte opcode)
        {
            _machine.Cpu.InstructionStarting -= FirstHandlerInstruction;
            int vector = _pendingException; _pendingException = -1;
            if (vector < 0) return;
            var cpu = _machine.Cpu; var stack = cpu.GetSegmentCache(Segment.Ss);
            uint offset = cpu.GetRegister(Register.Sp) & (stack.Default32 ? uint.MaxValue : 65535u);
            uint? code = null;
            if ((ulong)offset + (uint)_pendingExceptionWidth - 1 <= stack.Limit &&
                TryLinear(unchecked(stack.Base + offset), _pendingExceptionWidth, out var bytes))
                code = _pendingExceptionWidth == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            Add("exception-handler-stack", vector, code ?? 0, _pendingExceptionWidth, false, exceptionCode: code, evidence: _pendingExceptionEvidence);
        }

        /// <summary>Records real PIC acknowledgements without injecting vectors or altering interrupt lines.</summary>
        private void Ack(int irq, byte vector, bool spurious)
        { _acknowledgements++; Add("pic-acknowledge", vector, vector, 0, false, irq, spurious); }

        /// <summary>Whether passive CPU/PIC/port callbacks are attached; complete continuation requires this to be false.</summary>
        public bool Enabled => _rows.Length != 0;

        /// <summary>Returns the newest bounded observations in chronological sequence order.</summary>
        public object Tail(int count)
        {
            int retained = (int)Math.Min(_sequence, _rows.Length), take = Math.Min(count, retained);
            var rows = new TraceRow[take];
            for (int i = 0; i < take; i++) rows[i] = _rows[(int)((_sequence - take + i) % _rows.Length)]!;
            return new { enabled = _rows.Length != 0, capacity = _rows.Length, total = _sequence, overwritten = Math.Max(0, _sequence - _rows.Length),
                ports = _ports, interruptDeliveries = _interrupts, picAcknowledgements = _acknowledgements, records = rows };
        }

        /// <summary>Detaches only this controller's passive observations.</summary>
        public void Dispose()
        {
            if (_rows.Length == 0) return;
            _machine.Ports.Accessed -= Port;
            _machine.Cpu.InterruptEntered -= Interrupt;
            _machine.Cpu.InstructionStarting -= FirstHandlerInstruction;
            _machine.Pic.InterruptAcknowledged -= Ack;
        }
    }

    /// <summary>Owns new private evidence destinations and one already constructed regular product PC.</summary>
    private sealed class Controller(PcMachine machine, string output, int progressMilliseconds, Trace trace)
    {
        private readonly VgaFrameBuffer _frameBuffer = new();
        private long _stepElements;
        private int _stepRequests;

        /// <summary>Resolves a new artifact strictly inside this controller's private output directory.</summary>
        private string Destination(string path)
        {
            string full = Path.GetFullPath(path, output), relative = Path.GetRelativePath(output, full);
            Check(relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative), "Artifacts must be new paths inside this controller output directory.");
            Check(!File.Exists(full) && !Directory.Exists(full), "Artifact destination already exists.");
            string? parent = Path.GetDirectoryName(full);
            Check(parent is not null && Directory.Exists(parent), "Artifact parent directory must already exist.");
            return full;
        }

        /// <summary>Resolves an optional new report or explicitly requested PPM before advancing the guest.</summary>
        private string? OptionalDestination(JsonElement request, string name)
        { string? path = OptionalText(request, name); return path is null ? null : Destination(path); }

        /// <summary>Writes one new JSON artifact, never overwriting a prior result.</summary>
        private static void WriteNew(string path, object data)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, data, Json);
        }

        /// <summary>Recognizes only the documented Windows access, sharing and lock HRESULTs for dispensable progress metadata.</summary>
        private static bool ProgressSharingConflict(Exception error) => OperatingSystem.IsWindows() &&
            error is IOException or UnauthorizedAccessException &&
            error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);

        /// <summary>Retries bounded Windows metadata sharing conflicts while retaining the last complete progress and the live guest.</summary>
        private void Progress(object data)
        {
            string target = Path.Combine(output, "progress.json"), temporary = Path.Combine(output, ".progress-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                try
                {
                    using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(file, data, Json);
                }
                catch (Exception error) when (ProgressSharingConflict(error)) { return; }
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { File.Move(temporary, target, true); return; }
                    catch (Exception error) when (ProgressSharingConflict(error))
                    {
                        if (attempt == 4) return;
                        Thread.Sleep(5 << attempt);
                    }
                }
            }
            finally
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try { File.Delete(temporary); break; }
                    catch (Exception error) when (ProgressSharingConflict(error))
                    {
                        if (attempt == 3) break;
                        Thread.Sleep(5 << attempt);
                    }
                }
            }
        }

        /// <summary>Describes actual guest text, native scanout dimensions and complete rendered ARGB bytes.</summary>
        private object Display(string? ppm)
        {
            VideoFrame frame = machine.CaptureFrame(_frameBuffer);
            string digest = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan())));
            if (ppm is not null)
            {
                using var file = new FileStream(ppm, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.Write(Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
                byte[] row = new byte[checked(frame.Width * 3)];
                for (int y = 0; y < frame.Height; y++)
                {
                    for (int x = 0; x < frame.Width; x++)
                    { uint pixel = frame.ArgbPixels[y * frame.Width + x]; row[x * 3] = (byte)(pixel >> 16); row[x * 3 + 1] = (byte)(pixel >> 8); row[x * 3 + 2] = (byte)pixel; }
                    file.Write(row);
                }
            }
            return new { graphics = machine.Video.GraphicsMode, text = machine.Video.ReadText(), frame.Width, frame.Height, frame.ModeGeneration,
                frame.FrameId, frame.VirtualNanoseconds, pixelEncoding = BitConverter.IsLittleEndian ? "ARGB32-little-endian" : "ARGB32-big-endian", pixelsSha256 = digest, ppm };
        }

        /// <summary>Returns architectural/device diagnostics and a real guest scanout, never an emulated API success marker.</summary>
        private object Status(string? ppm = null) => new { status = machine.GetStatus(), point = Observe(machine),
            keyboardOutputBytes = machine.Keyboard.OutputByteCount,
            mouse = machine.Mouse is null ? null : new { machine.Mouse.Enabled, machine.Mouse.Remote, machine.Mouse.SampleRate, machine.Mouse.PacketCount },
            atapi = machine.Atapi?.CapturePhase(), nextEventTime = machine.Scheduler.NextEventTime,
            pageFaults = machine.Cpu.PageFaults, machine.Cpu.ProtectedModeEntries, machine.Cpu.PrivilegeStackSwitches,
            sessionStepElements = _stepElements, stepRequests = _stepRequests, display = Display(ppm) };
        private byte[]? _pendingKeys;

        /// <summary>Advances bounded regular owner batches and returns partial progress on a host deadline.</summary>
        private object Step(JsonElement request)
        {
            long requested = Number(request, "steps", 0, MaximumSteps), executed = 0;
            int maximumMilliseconds = (int)Number(request, "maxMilliseconds", 100, 3_600_000, 60_000);
            string? stopText = OptionalText(request, "stopText"), report = OptionalDestination(request, "report"), ppm = OptionalDestination(request, "ppm");
            Check(report is null || !string.Equals(report, ppm, StringComparison.OrdinalIgnoreCase), "Report and PPM require distinct new paths.");
            var timer = Stopwatch.StartNew(); long nextProgress = 0; string reason = "step-budget"; int batches = 0;
            _stepRequests++;
            while (executed < requested)
            {
                if (machine.Fault is not null) { reason = "guest-fault"; break; }
                if (stopText is not null && machine.Video.ReadText().Contains(stopText, StringComparison.Ordinal)) { reason = "stop-text"; break; }
                if (timer.ElapsedMilliseconds >= maximumMilliseconds) { reason = "host-deadline"; break; }
                if (timer.ElapsedMilliseconds >= nextProgress)
                {
                    Progress(new { stage = "stepping", stepRequest = _stepRequests, requestedSteps = requested, executedSteps = executed,
                        sessionStepElements = _stepElements, hostMilliseconds = timer.ElapsedMilliseconds, point = Observe(machine),
                        fault = machine.Fault, graphics = machine.Video.GraphicsMode, text = machine.Video.ReadText(), keyboardOutputBytes = machine.Keyboard.OutputByteCount });
                    nextProgress = timer.ElapsedMilliseconds + progressMilliseconds;
                }
                int amount = machine.RunSteps((int)Math.Min(10000, requested - executed));
                executed += amount; _stepElements += amount; batches++;
                if (amount == 0) { reason = machine.Fault is null ? "no-executable-progress" : "guest-fault"; break; }
            }
            if (machine.Fault is not null) reason = "guest-fault";
            else if (stopText is not null && machine.Video.ReadText().Contains(stopText, StringComparison.Ordinal)) reason = "stop-text";
            var result = new { requestedSteps = requested, executedSteps = executed, stopReason = reason, completedBudget = executed == requested,
                stopTextMatched = stopText is not null && machine.Video.ReadText().Contains(stopText, StringComparison.Ordinal),
                hostMilliseconds = timer.Elapsed.TotalMilliseconds, batches, status = Status(ppm), trace = trace.Tail(16) };
            Progress(new { stage = "idle", stepRequest = _stepRequests, result });
            if (report is not null) WriteNew(report, result);
            return result;
        }

        /// <summary>Compares this actual running PC with exactly one strict full snapshot import, using bounded normal execution and no input.</summary>
        private object CompareContinuation(JsonElement request)
        {
            Check(!trace.Enabled, "CompareContinuation requires traceCapacity=0 at controller initialization.");
            Check(_pendingKeys is null, "Retry the retained rejected KBC packet before comparing continuation.");
            string path = Destination(OptionalText(request, "path") ?? throw new ArgumentException("CompareContinuation requires a new output directory path."));
            int slices = (int)Number(request, "slices", 1, 64, 16), stepsPerSlice = (int)Number(request, "stepsPerSlice", 1, 500000, 20000);
            int maximumMilliseconds = (int)Number(request, "maxMilliseconds", 100, 60000, 30000);
            ulong maximumVirtualNanoseconds = (ulong)Number(request, "maxVirtualNanoseconds", 1000000, 1000000000, 1000000000);
            Check((long)slices * stepsPerSlice * 2 <= 1_000_000, "Both PCs combined must fit the 1,000,000-step budget.");
            return Windows95Continuation.Run(machine, path, slices, stepsPerSlice, maximumMilliseconds, maximumVirtualNanoseconds,
                Progress, count => _stepElements += count);
        }

        /// <summary>Admits one complete raw translated-set1 packet atomically, retaining rejected packets for explicit retry.</summary>
        private object Keys(JsonElement request)
        {
            bool retry = request.TryGetProperty("retry", out var retryProperty) && retryProperty.ValueKind == JsonValueKind.True;
            if (request.TryGetProperty("retry", out retryProperty)) Check(retryProperty.ValueKind is JsonValueKind.True or JsonValueKind.False, "retry must be boolean.");
            byte[] packet;
            if (retry)
            {
                Check(!request.TryGetProperty("scanCodes", out _) && _pendingKeys is not null, "Retry requires one stored rejected packet and no replacement scanCodes.");
                packet = _pendingKeys!;
            }
            else
            {
                Check(request.TryGetProperty("scanCodes", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() is >= 1 and <= 16,
                    "scanCodes must contain 1..16 integral bytes.");
                packet = new byte[values.GetArrayLength()]; int at = 0;
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetByte(out byte scanCode)) throw new ArgumentException("scanCodes must be integers in 0..255.");
                    packet[at++] = scanCode;
                }
                Check(_pendingKeys is null || _pendingKeys.AsSpan().SequenceEqual(packet), "A different rejected packet is still pending; retry it explicitly first.");
            }
            bool accepted = machine.Keyboard.TryInjectTranslatedSet1(packet);
            _pendingKeys = accepted ? null : packet;
            return new { accepted, errorCode = accepted ? null : "InputNotAccepted", scanCodes = packet.Select(value => (int)value).ToArray(),
                pending = _pendingKeys is not null, keyboardOutputBytes = machine.Keyboard.OutputByteCount, point = Observe(machine) };
        }

        /// <summary>Applies only virtual relative sensor counts and two-button levels through the actual PS2 device.</summary>
        private object Mouse(JsonElement request)
        {
            int x = (int)Number(request, "x", -32767, 32767), y = (int)Number(request, "y", -32767, 32767), buttons = (int)Number(request, "buttons", 0, 3);
            var mouse = machine.Mouse ?? throw new InvalidOperationException("No PS2 mouse is installed.");
            mouse.Inject(x, y, buttons);
            return new { applied = true, x, y, buttons, mouse.Enabled, mouse.Remote, mouse.SampleRate, mouse.PacketCount,
                note = "Virtual counts only; stream delivery follows the guest-selected mouse/controller clock.", point = Observe(machine) };
        }

        /// <summary>Executes one complete controller request on the same owner that executes every CPU batch.</summary>
        public (object Result, bool Exit) Handle(JsonElement request, string command)
        {
            switch (command.ToLowerInvariant())
            {
                case "step": return (Step(request), false);
                case "comparecontinuation": return (CompareContinuation(request), false);
                case "keys": return (Keys(request), false);
                case "mouse": return (Mouse(request), false);
                case "insertfloppy":
                {
                    string path = Path.GetFullPath(OptionalText(request, "path") ?? throw new ArgumentException("InsertFloppy requires a raw own test image."));
                    Check(path.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Floppy fixtures must be inside this controller's private output directory.");
                    int drive = (int)Number(request, "drive", 0, 1, 0);
                    var disc = new ReadOnlyRawDisk(path);
                    try { machine.Floppy.ReplaceMedia(drive, disc); } catch { disc.Dispose(); throw; }
                    return (new { inserted = true, drive, path, point = Observe(machine) }, false);
                }
                case "setcalendar":
                {
                    string value = OptionalText(request, "localTime", 64) ?? throw new ArgumentException("SetCalendar requires explicit local wall fields.");
                    DateTime calendar = DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
                    machine.SetCalendar(calendar);
                    return (new { calendar = machine.Rtc.Calendar, virtualNanoseconds = machine.Scheduler.Now }, false);
                }
                case "status":
                {
                    string? report = OptionalDestination(request, "report"), ppm = OptionalDestination(request, "ppm");
                    Check(report is null || !string.Equals(report, ppm, StringComparison.OrdinalIgnoreCase), "Report and PPM require distinct new paths.");
                    var result = new { status = Status(ppm), pendingScanCodes = _pendingKeys?.Select(value => (int)value).ToArray() ?? [], trace = trace.Tail(16) };
                    if (report is not null) WriteNew(report, result);
                    return (result, false);
                }
                case "snapshot":
                {
                    string path = Destination(OptionalText(request, "path") ?? throw new ArgumentException("Snapshot requires a new path."));
                    var manifest = SnapshotStore.Capture(machine, path);
                    return (new { path, manifest, point = Observe(machine) }, false);
                }
                case "savemedia":
                {
                    string path = Destination(OptionalText(request, "path") ?? throw new ArgumentException("SaveMedia requires a new path."));
                    var manifest = MediaCheckpointStore.Capture(machine, path);
                    return (new { path, manifest, point = Observe(machine) }, false);
                }
                case "tracetail": return (trace.Tail((int)Number(request, "count", 0, 4096, 64)), false);
                case "exit": return (new { exited = true, point = Observe(machine), fault = machine.Fault,
                    note = "Controller exit does not certify a guest operating-system shutdown." }, true);
                default: throw new ArgumentException("Unsupported controller command: " + command);
            }
        }
    }

    /// <summary>Identifies actual loaded VM modules and separately labels hashes of their current protected files.</summary>
    private static object BuildModules() => new[] { typeof(PcMachine).Assembly, typeof(X86Cpu).Assembly, typeof(DeviceScheduler).Assembly,
        typeof(VgaAdapter).Assembly, typeof(MachineProfile).Assembly, typeof(Windows95GuestRunner).Assembly }
        .Distinct().Select(assembly => new { name = assembly.GetName().Name, path = assembly.Location,
            moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N"), sha256OfAssemblyFile = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray();

    /// <summary>Emits one flushed JSON-line response and never writes informal logs to stdout.</summary>
    private static void Reply(object value)
    { Console.WriteLine(JsonSerializer.Serialize(value, Json)); Console.Out.Flush(); }

    /// <summary>Runs the persistent controller on its calling thread, without a UI, worker, host-input API or listener.</summary>
    private static int RunChecked(string path)
    {
        string inputPath = Path.GetFullPath(path), inputDirectory = Path.GetDirectoryName(inputPath)!;
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(inputPath), Json) ?? throw new ArgumentException("Empty controller initialization.");
        bool cold = !string.IsNullOrWhiteSpace(input.Machine), resume = !string.IsNullOrWhiteSpace(input.Snapshot);
        Check(cold != resume && (resume ? !string.IsNullOrWhiteSpace(input.Branch) : input.Branch is null), "Supply machine or snapshot plus a new branch, without overrides.");
        Check(!string.IsNullOrWhiteSpace(input.Output) && input.TraceCapacity is >= 0 and <= 65536 && input.ProgressMilliseconds is >= 100 and <= 10000,
            "Output, trace capacity or progress interval is invalid.");
        string output = Path.GetFullPath(input.Output, inputDirectory);
        Check(!Directory.Exists(output) && !File.Exists(output), "Controller output must be a new private directory.");
        Directory.CreateDirectory(output);
        using var machine = cold ? MachineFactory.FromDefinition(MachineDefinitionStore.Load(Path.GetFullPath(input.Machine!, inputDirectory)))
            : SnapshotStore.Load(Path.GetFullPath(input.Snapshot!, inputDirectory), Path.GetFullPath(input.Branch!, inputDirectory));
        using var trace = new Trace(machine, input.TraceCapacity);
        var controller = new Controller(machine, output, input.ProgressMilliseconds, trace);
        Reply(new { protocol = 1, ready = true, ownerThread = Environment.CurrentManagedThreadId, output, actualProfile = machine.Profile,
            buildModules = BuildModules(), point = Observe(machine), inputMode = "atomic-kbc-translated-set1", traceCapacity = input.TraceCapacity });
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            object? id = null; string? command = null;
            try
            {
                Check(line.Length is > 0 and <= MaximumLineCharacters, "Controller JSON line must contain 1..65536 characters.");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                var request = document.RootElement;
                Check(request.ValueKind == JsonValueKind.Object, "Controller request must be an object.");
                if (request.TryGetProperty("id", out var requestId)) id = requestId.Clone();
                command = OptionalText(request, "command", 32) ?? throw new ArgumentException("Missing command.");
                var result = controller.Handle(request, command);
                Reply(new { id, ok = true, command, result = result.Result });
                if (result.Exit) return 0;
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
            { Reply(new { id, ok = false, command, error = "InvalidRequest", message = error.Message, point = Observe(machine) }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or KeyNotFoundException)
            { Reply(new { id, ok = false, command, error = "ResourceUnavailable", message = error.Message, point = Observe(machine) }); }
        }
        return 0;
    }

    /// <summary>Reports controlled initialization failures as one JSON response without starting another execution owner.</summary>
    public static int Run(string path)
    {
        try { return RunChecked(path); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Reply(new { protocol = 1, ready = false, error = "InitializationFailed", message = error.Message }); return 1; }
    }
}
