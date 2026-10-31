// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections.Concurrent;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime.Networking;

namespace DOSEmulator.Runtime;

/// <summary>UI and IPC enqueue work; only the emulation owner pumps and mutates the machine.</summary>
public sealed class MachineSession : IDisposable
{
    private readonly ConcurrentQueue<(IpcRequest Request, TaskCompletionSource<IpcResponse> Result, long Generation, long NetworkGeneration)> _commands = new();
    private bool _disposed;
    private readonly object _queueGate = new();
    private long _revision = 1;
    private long _inputGeneration = 1;
    private long _resumeGeneration;
    private long _networkGeneration = 1;
    private string _definitionId = Guid.NewGuid().ToString("N");
    private string? _definitionName;
    private MachineDefinitionBinding? _definitionBinding;
    private readonly bool _enableHostNetworking;
    private NetworkService _network;
    private IMachineExecutionFactory? _executionFactory;
    private IMachineExecution? _execution;
    private readonly Func<string, IMachineExecutionFactory?>? _executionResolver;
    /// <summary>Explicit CPU backend, independent of the PC's nominal hardware clock.</summary>
    public string CpuEngine => _executionFactory?.Engine ?? "software";
    /// <summary>Whether the current graph has a separate native register owner.</summary>
    public bool UsesExternalCpu => _execution is not null;
    /// <summary>Reads status from the active CPU owner at a stopped emulation boundary.</summary>
    public MachineStatus GetStatus() => _execution?.GetStatus() ?? Machine.GetStatus();
    /// <summary>Runs a native slice and its joined presentation; software pacing remains with its established frontend.</summary>
    public void RunRealtime(TimeSpan duration, Action observe, CancellationToken cancellation = default) =>
        (_execution ?? throw new InvalidOperationException("No native CPU owner.")).RunRealtime(duration, observe, cancellation);
    /// <summary>Routes explicit frontend calendar input through the active CPU owner's stopped device boundary.</summary>
    public void SetCalendar(DateTime calendar)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_execution is { } execution) execution.SetCalendar(calendar);
        else Machine.SetCalendar(calendar);
    }
    // External input awaiting admission is session-owned, like _commands, not saved guest state.
    private readonly Queue<RelativeMouseInput.Input> _pendingMouse = new();
    private const int PendingMouseLimit = 128;
    /// <summary>Current PC owned by this session; only queued owner-thread commands may replace it.</summary>
    public PcMachine Machine { get; private set; }
    /// <summary>Configuration/reset revision used to reject stale prepared changes.</summary>
    public long Revision => _revision;
    /// <summary>Current input ownership epoch; frontends acknowledge it only after discarding their retired held input.</summary>
    public long InputGeneration => Interlocked.Read(ref _inputGeneration);
    /// <summary>Successful paused-to-running Resume commands, allowing frontend policy to observe transitions coalesced in one pump.</summary>
    public long ResumeGeneration => Interlocked.Read(ref _resumeGeneration);
    /// <summary>Explicit frontend configuration destination; snapshots and ordinary headless sessions remain unbound.</summary>
    public string? DefinitionPath => _definitionBinding?.Path;

    /// <summary>Initializes MachineSession and connects the supplied dependencies before the component is used.</summary>
    public MachineSession(PcMachine machine, bool enableHostNetworking = false, string? definitionPath = null, IMachineExecutionFactory? executionFactory = null, Func<string, IMachineExecutionFactory?>? executionResolver = null)
    {
        executionFactory?.ValidateProfile(machine.Profile);
        var execution = executionFactory?.Create(machine);
        execution?.Activate();
        _executionFactory = executionFactory; _execution = execution; _executionResolver = executionResolver;
        try
        {
            var definition = machine.CaptureDefinition();
            if (definitionPath is not null) _definitionBinding = new(definitionPath, definition.Id);
            _enableHostNetworking = enableHostNetworking;
            _network = new(machine, enableHostNetworking);
            Machine = machine; Machine.HardwareResetOccurred += TrackReset;
            _definitionId = definition.Id; _definitionName = definition.Name;
        }
        catch { execution?.Dispose(); throw; }
    }
    /// <summary>Invalidates the configuration revision after a real machine reset event.</summary>
    private void TrackReset()
    {
        _revision++;
        if (Machine.LastResetSource == "cf9-hard")
        {
            Interlocked.Increment(ref _networkGeneration);
            string? failure = null;
            try { _network.Dispose(); } catch (Exception error) { failure = error.Message; }
            foreach (var adapter in Machine.Profile.NetworkAdapters.ToArray()) Machine.SetNetworkCablePreference(adapter.Slot, false);
            _network = new(Machine, _enableHostNetworking);
            if (failure is not null) _network.RecordFailure(failure);
        }
    }
    /// <summary>Transfers battery state, releases the old machine and adopts an already prepared replacement.</summary>
    private void ReplaceMachine(PcMachine replacement, bool carryBattery = true, IMachineExecution? prepared = null, IMachineExecutionFactory? factory = null, bool changeEngine = false)
    {
        var selected = changeEngine ? factory : _executionFactory;
        IMachineExecution? next;
        try { selected?.ValidateProfile(replacement.Profile); next = prepared ?? selected?.Create(replacement); }
        catch { prepared?.Dispose(); replacement.Dispose(); throw; }
        if (carryBattery) Machine.Rtc.CarryBatteryStateTo(replacement.Rtc);
        try { _network.Dispose(); }
        catch (Exception error)
        {
            Interlocked.Increment(ref _networkGeneration);
            _revision++;
            foreach (var adapter in Machine.Profile.NetworkAdapters.ToArray()) Machine.SetNetworkCablePreference(adapter.Slot, false);
            _network = new(Machine, _enableHostNetworking);
            _network.RecordFailure(error.Message);
            next?.Dispose(); replacement.Dispose();
            throw new IOException("The previous network connection could not close. The original PC is retained with networking disconnected.", error);
        }
        try
        {
            _execution?.Suspend();
            next?.Activate();
        }
        catch
        {
            next?.Dispose(); replacement.Dispose(); _execution?.Activate();
            _network = new(Machine, _enableHostNetworking); throw;
        }
        _pendingMouse.Clear();
        Machine.HardwareResetOccurred -= TrackReset;
        _execution?.Dispose(); Machine.Dispose(); Machine = replacement;
        _execution = next; _executionFactory = selected;
        Machine.HardwareResetOccurred += TrackReset; _revision++; Interlocked.Increment(ref _inputGeneration);
        Interlocked.Increment(ref _networkGeneration);
        _network = new(Machine, _enableHostNetworking);
    }

    /// <summary>Queues a bounded UI/IPC request for the emulation owner; completion never runs controller code on the caller.</summary>
    public Task<IpcResponse> Enqueue(IpcRequest request) => Enqueue(request, InputGeneration);

    /// <summary>Queues an input source's explicitly acknowledged epoch without restamping its retired held state as new input.</summary>
    public Task<IpcResponse> Enqueue(IpcRequest request, long observedInputGeneration)
    {
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueGate)
        {
            if (_disposed) completion.SetResult(new(1, request.RequestId, false, "SessionStopped", null));
            else if (_commands.Count >= 256) completion.SetResult(new(1, request.RequestId, false, "QueueFull", null));
            else _commands.Enqueue((request, completion, observedInputGeneration, Interlocked.Read(ref _networkGeneration)));
        }
        return completion.Task;
    }

    /// <summary>Executes a bounded number of queued UI/IPC requests on the sole emulation owner.</summary>
    public void PumpCommands()
    {
        PumpMouseInput();
        int count = 0;
        while (count++ < 256 && _commands.TryDequeue(out var command))
        {
            try
            {
                bool input = command.Request.Command is "SendScanCode" or "SendMouseInput" or "SendCtrlAltDel" or "SetGameportInput" or "SendSerialInput" or "SetParallelInput" or "SendMidiInput" or "SetNetworkConnection";
                if (command.Request.Command == "SetNetworkConnection" && command.NetworkGeneration != Interlocked.Read(ref _networkGeneration))
                    command.Result.TrySetResult(new(1, command.Request.RequestId, false, "StaleNetworkGeneration", null));
                else if (input && command.Generation != Interlocked.Read(ref _inputGeneration))
                    command.Result.TrySetResult(new(1, command.Request.RequestId, false, "StaleInputGeneration", null));
                else command.Result.TrySetResult(Handle(command.Request));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
            {
                command.Result.TrySetResult(new(1, command.Request.RequestId, false, "InvalidRequest", new { message = ex.Message }));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException)
            {
                command.Result.TrySetResult(new(1, command.Request.RequestId, false, "ResourceUnavailable", new { message = ex.Message }));
            }
        }
        PumpMouseInput();
        _network.Pump(Machine.Running);
    }

    /// <summary>Preserves host button edges until an actual PS/2 report accepts the preceding state.</summary>
    private void PumpMouseInput()
    {
        var mouse = Machine.Mouse;
        if (mouse is null) { _pendingMouse.Clear(); return; }
        while (_pendingMouse.TryPeek(out var input))
        {
            // Movement with the same buttons can accumulate normally. A later edge must not
            // erase an unreported press/release when both host events arrive in one owner pump.
            // Paused or disabled devices retain their previous latest-sensor-state policy.
            if (Machine.Running && (mouse.Enabled || mouse.Remote) &&
                input.Buttons != mouse.Buttons && mouse.Buttons != mouse.ReportedButtons) break;
            _pendingMouse.Dequeue();
            mouse.Inject(input.X, input.Y, input.Buttons);
        }
    }

    /// <summary>Rejects a missing, nonintegral, overflowing or out-of-domain numeric payload before any device/media effects.</summary>
    private static int ReadInt32(JsonElement payload, string name, int minimum, int maximum)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value) || value < minimum || value > maximum)
            throw new ArgumentException($"Property '{name}' must be a JSON integer in {minimum}..{maximum}.", name);
        return value;
    }

    /// <summary>Validates and executes one versioned session command and returns its stable success/error response.</summary>
    private IpcResponse Handle(IpcRequest request)
    {
        if (request.ProtocolVersion != IpcProtocol.Version) return new(1, request.RequestId, false, "ProtocolMismatch", null);
        object? result;
        switch (request.Command)
        {
            case "Ping": result = new { pong = true }; break;
            case "GetStatus": result = GetStatus(); break;
            case "GetNetworkStatus": result = _network.GetStatus(); break;
            case "SetNetworkConnection":
            {
                int slot = ReadInt32(request.Payload, "slot", 0, 1);
                if (!request.Payload.TryGetProperty("connected", out var connected) || connected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Property 'connected' must be a JSON boolean.");
                try { _network.SetConnected(slot, connected.GetBoolean()); }
                finally { _revision++; }
                if (connected.GetBoolean() && !_network.IsConnected(slot))
                    return new(1, request.RequestId, false, "NetworkUnavailable", new { message = _network.ConnectionError(slot) ?? "The network cable could not connect.", network = _network.GetStatus() });
                result = _network.GetStatus(); break;
            }
            case "GetMachineDefinition": result = Machine.CaptureDefinition(_definitionName, _definitionId); break;
            case "SaveMachineDefinition":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing machine file.");
                string? name = request.Payload.TryGetProperty("name", out var property) ? property.GetString() : null;
                var definition = Machine.CaptureDefinition(name ?? _definitionName, _definitionId);
                MachineDefinitionStore.Save(path, definition); _definitionName = definition.Name;
                Machine.SetDefinitionIdentity(definition.Id, definition.Name);
                _definitionBinding = new(path, definition.Id);
                result = new { path = Path.GetFullPath(path), definition }; break;
            }
            case "LoadMachineDefinition":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing machine file.");
                var definition = MachineDefinitionStore.Load(path);
                var replacement = MachineFactory.FromDefinition(definition, Machine);
                ReplaceMachine(replacement); _definitionId = definition.Id; _definitionName = definition.Name;
                _definitionBinding = new(path, definition.Id);
                Machine.Running = true; result = GetStatus(); break;
            }
            case "SetHardDisk":
            {
                int slot = ReadInt32(request.Payload, "slot", 0, 3);
                string? image = request.Payload.TryGetProperty("path", out var imageProperty) ? imageProperty.GetString() : null;
                string? overlay = request.Payload.TryGetProperty("overlayPath", out var overlayProperty) ? overlayProperty.GetString() : null;
                if (image is null && overlay is not null) throw new ArgumentException("An overlay requires its base image.");
                var definition = Machine.CaptureDefinition(_definitionName, _definitionId);
                var media = definition.HardDisks.Where(d => d.Slot != slot).ToList();
                if (image is not null) media.Add(new(slot, image, overlay));
                definition = definition with { HardDisks = media.OrderBy(d => d.Slot).ToArray(), BuiltInTestDisk = false };
                var replacement = MachineFactory.FromDefinition(definition, Machine);
                ReplaceMachine(replacement); Machine.Running = true;
                result = GetStatus(); break;
            }
            case "SetOpticalDisc":
            {
                if (!Machine.Profile.IsAtapi) throw new ArgumentException("No installed packet target.");
                string? image = request.Payload.TryGetProperty("path", out var property) ? property.GetString() : null;
                if (image is not null && string.IsNullOrWhiteSpace(image)) throw new ArgumentException("A cooked ISO path is required.");
                var definition = Machine.CaptureDefinition(_definitionName, _definitionId) with
                { OpticalDiscs = image is null ? [] : [new(Machine.Profile.AtapiSlot, image)] };
                var replacement = MachineFactory.FromDefinition(definition, Machine);
                ReplaceMachine(replacement); Machine.Running = true;
                result = GetStatus(); break;
            }
            case "CreateSnapshot":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing new snapshot directory.");
                object snapshot = _execution?.CaptureSnapshot(path) ?? (object)SnapshotStore.Capture(Machine, path);
                result = new { path = Path.GetFullPath(path), snapshot, runningStateSnapshot = true }; break;
            }
            case "LoadSnapshot":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing snapshot directory.");
                string destination = request.Payload.GetProperty("destination").GetString() ?? throw new ArgumentException("Missing new branch directory.");
                var loaded = _executionFactory?.Load(path, destination);
                var replacement = loaded?.Machine ?? SnapshotStore.Load(path, destination);
                ReplaceMachine(replacement, false, loaded);
                var identity = replacement.CaptureDefinition(); _definitionId = identity.Id; _definitionName = identity.Name;
                _definitionBinding = null;
                result = GetStatus(); break;
            }
            case "CreateMediaCheckpoint":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing checkpoint directory.");
                var checkpoint = MediaCheckpointStore.Capture(Machine, path);
                result = new { path = Path.GetFullPath(path), checkpoint, runningStateSnapshot = false }; break;
            }
            case "CreateMediaBranch":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing checkpoint directory.");
                string destination = request.Payload.GetProperty("destination").GetString() ?? throw new ArgumentException("Missing new branch directory.");
                var definition = MediaCheckpointStore.CreateBranch(path, destination);
                result = new { path = Path.Combine(Path.GetFullPath(destination), "branch.dospc.json"), definition, coldStartRequired = true }; break;
            }
            case "GetCapabilities":
                result = new { development = true, freeDosMinimalBoot = true, msDos622InstalledRealMode = true, biosFloppyFormatNativeGeometry = true, biosFixedDiskChsVerify = true,
                    privateDiskOverlays = true, floppyDma = true, dosBoot = "FreeDOS 1.4 minimal; local German MS-DOS 6.22 installed real-mode profile", protectedMode = true, atomicKeyboardPackets = true, keyboardInputBackpressure = true, sound = true, pcSpeaker = true, sb2Dsp = true, sb2HighSpeedPcm = true,
                    sb2DirectAdc = true, sb2InputPcm = true, sb2HighSpeedInput = true, sb2Adpcm = true, hostAudioInput = false,
                    sb2ConfigurableResources = true, sb2BasePorts = new[] { 0x220, 0x240 }, sb2Irqs = new[] { 3, 5, 7, 9 }, sb2DmaChannels = new[] { 1 },
                    sb16Pcm = true, sb16Model = SoundBlaster16.Model, sb16Profile = SoundBlaster16.ProfileId,
                    sb16Opl3 = true, sb16Mpu401 = true, sb16MidiModel = Mpu401Uart.ProfileId, midiIntelligentMode = false, midiSynthesizer = false, hostMidi = false,
                    sb16FullCard = false, opl3Stereo = true, opl3FourOperator = true,
                    opl2Timers = true, opl2Melodic = true, opl2Rhythm = true, opl2CsmKeying = true, opl2CsmTotalLevelLatch = true, soundBlaster = false, opl = false, guestNetwork = false,
                    ps2Mouse = true, mouseWheel = false, analogGameport = true, gameportModel = AnalogGameport.ProfileId, hostGamepad = false,
                    gameportBiosAh84 = true, vgaTextFontBlocks = 8, biosVgaUserFontLoad = true,
                    serialPort = true, serialModel = Uart16550.ProfileId, serialBiosFunctions = new[] { 0, 1, 2, 3 }, hostSerialPort = false,
                    parallelPort = true, parallelModel = ParallelPrinterPort.ProfileId, parallelBiosFunctions = new[] { 0, 1, 2 }, hostPrinter = false,
                    et4000AxBanked256 = true, et4000VideoMemoryBytes = 1048576, et4000LegacyModes = new[] { 0x2e, 0x30, 0x38 },
                    vbe = true, vbeFunctions = new[] { 0, 1, 2, 3, 5 }, fullVbeConformance = false,
                    trio64Pci = true, trio64Model = MachineProfile.S3Trio64VideoId, trio64VideoMemoryBytes = 2097152,
                    trio64Linear8bpp = true, trio64Acceleration = true, trio64Acceleration2dSubset = true,
                    trio64AccelerationScope = "Synchronous line, rectangle, BitBLT, PatBLT and CPU-image subset; no FIFO/bus-time calibration",
                    trio64HighColor = true, trio64PixelFormats = new[] { "indexed8", "RGB555", "RGB565", "BGRx32" },
                    trio64HardwareCursor = true, trio64HardwareCursorPolicy = "86C764: 64x64, left storage, Windows/X11, enhanced scanout",
                    trio64Active = Machine.Profile.IsTrio64, trio64FullChip = false,
                    trio64Vbe12Subset = true, trio64VbeVersion = 0x0102, trio64VbeOwnFirmware = true,
                    trio64VbeBanked = true, trio64VbeFunctions = new[] { 0, 1, 2, 3, 5 }, trio64VbeLfbInterface = false,
                    trio64VbeActive = Machine.Profile.IsWindows95Board,
                    windows95CandidateProfiles = new[] { MachineProfile.Windows95ProfileId, MachineProfile.Windows95Profile133Id },
                    atapiPio = true, atapiModel = MachineProfile.AtapiCdRomId, atapiBlockBytes = 2048,
                    atapiDma = false, atapiAudioPlayback = false, hostOpticalDrive = false,
                    pciBios21Subset = true, bios32ServiceDirectory = true, pciBiosProfile = MachineProfile.PciBiosProfileId,
                    pciBiosActive = Machine.Profile.IsPciBiosBoard, pciSpecialCycles = false, fullPciBiosConformance = false,
                    et4000FullChip = false, ideChannels = 2, ideSlots = 4, networkAdapterSlots = 2, networkModels = new[] { "ne2000-isa", "rtl8029-pci" }, networkModes = new[] { "nat", "host-only", "bridged" }, networkSnapshotReconnectRequired = true, machineDefinitions = true, blankDiskImages = true, mediaCheckpoints = true, runningStateSnapshots = true,
                    biosVgaTextScanlines = true, msDos622Cp850Text = true, guestCpuHotReset = true,
                    resetScope = "CPU only; RAM, devices, virtual time and attached media retained",
                    commands = new[] { "CreateSnapshot", "LoadSnapshot", "CreateMediaCheckpoint", "CreateMediaBranch", "GetMachineDefinition", "SaveMachineDefinition", "LoadMachineDefinition", "SetHardDisk", "SetOpticalDisc", "Ping", "GetStatus", "GetCapabilities", "GetNetworkStatus", "SetNetworkConnection", "Pause", "Resume", "HardwareReset", "SendCtrlAltDel", "PowerCycleCurrent", "PowerCycleDemo", "PowerCycleReadOnlyDisk", "PowerCycleOverlayDisk", "InsertFloppy", "EjectFloppy", "PrepareConfigurationChange", "CommitConfigurationChange", "SendScanCode", "SendMouseInput", "SetGameportInput", "SendSerialInput", "SetParallelInput", "SendMidiInput" } }; break;
            case "Pause": _execution?.Pause(); Machine.Running = false; result = GetStatus(); break;
            case "Resume":
            {
                if (!Machine.Running)
                {
                    long next = checked(_resumeGeneration + 1);
                    Machine.Running = true; Interlocked.Exchange(ref _resumeGeneration, next);
                }
                result = GetStatus(); break;
            }
            case "SetCpuEngine":
            {
                string engine = request.Payload.GetProperty("engine").GetString() ?? throw new ArgumentException("Missing engine.");
                IMachineExecutionFactory? selected = null;
                if (engine != "software") selected = _executionResolver?.Invoke(engine) ?? throw new ArgumentException("Unknown or unavailable CPU engine.");
                selected?.ValidateProfile(Machine.Profile);
                var replacement = Machine.CreatePowerCycle(Machine.Profile);
                ReplaceMachine(replacement, factory: selected, changeEngine: true);
                Machine.Running = false; result = new { engine = CpuEngine, status = GetStatus() }; break;
            }
            case "HardwareReset": Machine.HardwareReset(); result = GetStatus(); break;
            case "SendCtrlAltDel":
                if (!Machine.Keyboard.TryInjectTranslatedSet1([0x1d, 0x38, 0x53, 0xd3, 0xb8, 0x9d]))
                    return new(1, request.RequestId, false, "InputNotAccepted", null);
                result = GetStatus(); break;
            case "PowerCycleCurrent":
            {
                var replacement = Machine.CreatePowerCycle(Machine.Profile);
                ReplaceMachine(replacement); Machine.Running = true;
                result = GetStatus(); break;
            }
            case "PowerCycleDemo":
            {
                // This explicit operation always boots the built-in test disk, never swaps an attached user image silently.
                var replacement = PcMachine.CreateDemo(Machine.Profile);
                ReplaceMachine(replacement); Machine.Running = true; _definitionBinding = null;
                result = GetStatus(); break;
            }
            case "PowerCycleReadOnlyDisk":
            case "PowerCycleOverlayDisk":
            {
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing image path.");
                string extension = Path.GetExtension(path);
                if (!Path.IsPathFullyQualified(path) || (!extension.Equals(".img", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ima", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute path to a raw IDE sector image (.img/.raw/.ima). This is not a floppy or ISO controller.");
                string? overlay = request.Command == "PowerCycleOverlayDisk"
                    ? request.Payload.GetProperty("overlayPath").GetString() ?? throw new ArgumentException("Missing overlay path.") : null;
                if (overlay is not null && (!Path.IsPathFullyQualified(overlay) || !Path.GetExtension(overlay).Equals(".cow", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute path to a private .cow overlay file.");
                var definition = Machine.CaptureDefinition(_definitionName, _definitionId);
                definition = definition with { BuiltInTestDisk = false, HardDisks = definition.HardDisks.Where(d => d.Slot != 0).Append(new DiskAttachment(0, path, overlay)).ToArray() };
                var replacement = MachineFactory.FromDefinition(definition, Machine);
                ReplaceMachine(replacement); Machine.Running = true; _definitionBinding = null;
                result = GetStatus(); break;
            }
            case "InsertFloppy":
            {
                int drive = ReadInt32(request.Payload, "drive", 0, 1);
                string path = request.Payload.GetProperty("path").GetString() ?? throw new ArgumentException("Missing floppy path.");
                if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".img" or ".ima" or ".raw"))
                    throw new ArgumentException("Use an absolute raw floppy image path.");
                string? overlay = request.Payload.TryGetProperty("overlayPath", out var overlayProperty) ? overlayProperty.GetString() : null;
                if (overlay is not null && (!Path.IsPathFullyQualified(overlay) || !Path.GetExtension(overlay).Equals(".cow", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use an absolute private .cow overlay path.");
                IReadOnlyBlockDevice disk = overlay is not null ? new CopyOnWriteDisk(path, overlay) : new ReadOnlyRawDisk(path);
                try { Machine.Floppy.ReplaceMedia(drive, disk); } catch { disk.Dispose(); throw; }
                _revision++; result = GetStatus(); break;
            }
            case "EjectFloppy":
                Machine.Floppy.ReplaceMedia(ReadInt32(request.Payload, "drive", 0, 1), null);
                _revision++; result = GetStatus(); break;
            case "PrepareConfigurationChange":
            {
                var requested = request.Payload.Deserialize<MachineProfile>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing profile.");
                requested.Validate(); _executionFactory?.ValidateProfile(requested); result = new ConfigurationPlan(_revision, requested,
                    JsonSerializer.Serialize(requested, IpcProtocol.JsonOptions) != JsonSerializer.Serialize(Machine.Profile, IpcProtocol.JsonOptions)); break;
            }
            case "CommitConfigurationChange":
            {
                var plan = request.Payload.Deserialize<ConfigurationPlan>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing change plan.");
                if (plan.ExpectedRevision != _revision) return new(1, request.RequestId, false, "StaleConfiguration", null);
                plan.Requested.Validate(); _executionFactory?.ValidateProfile(plan.Requested);
                var replacement = Machine.CreatePowerCycle(plan.Requested);
                ReplaceMachine(replacement); Machine.Running = true;
                result = new { revision = _revision, status = GetStatus() }; break;
            }
            case "SendScanCode":
            {
                bool accepted;
                if (request.Payload.TryGetProperty("scanCodes", out var packet))
                {
                    if (request.Payload.TryGetProperty("scanCode", out _) || packet.ValueKind != JsonValueKind.Array || packet.GetArrayLength() is < 1 or > 16)
                        throw new ArgumentException("Supply one scanCode or a complete scanCodes packet of 1..16 bytes.");
                    Span<byte> codes = stackalloc byte[16]; int length = 0;
                    foreach (var value in packet.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetByte(out byte code)) throw new ArgumentException("Scan bytes must be in 0..255.");
                        codes[length++] = code;
                    }
                    accepted = Machine.Keyboard.TryInjectTranslatedSet1(codes[..length]);
                }
                else
                {
                    byte code = (byte)ReadInt32(request.Payload, "scanCode", 0, 255);
                    accepted = Machine.Keyboard.TryInjectTranslatedSet1(code);
                }
                if (!accepted) return new(1, request.RequestId, false, "InputNotAccepted", null);
                result = null; break;
            }
            case "SendMouseInput":
            {
                int x = ReadInt32(request.Payload, "x", -32767, 32767), y = ReadInt32(request.Payload, "y", -32767, 32767);
                int buttons = ReadInt32(request.Payload, "buttons", 0, 3);
                if (Machine.Mouse is null) throw new InvalidOperationException("No PS/2 mouse configured.");
                PumpMouseInput();
                if (_pendingMouse.Count == PendingMouseLimit)
                    return new(1, request.RequestId, false, "InputNotAccepted", null);
                _pendingMouse.Enqueue(new(x, y, buttons));
                PumpMouseInput(); result = null; break;
            }
            case "SetGameportInput":
            {
                var input = request.Payload.Deserialize<GameportInput>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing analog input.");
                if (Machine.Gameport is null) throw new InvalidOperationException("No analog gameport configured.");
                Machine.Gameport.SetInput(input); result = null; break;
            }
            case "SendSerialInput":
                Machine.SendSerialInput(request.Payload.Deserialize<SerialInput>(IpcProtocol.JsonOptions)
                    ?? throw new ArgumentException("Missing virtual serial input.")); result = null; break;
            case "SendMidiInput":
                (Machine.Midi ?? throw new InvalidOperationException("No MPU-UART configured.")).QueueReceive(
                    request.Payload.Deserialize<MidiInput>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing virtual MIDI input."));
                result = null; break;
            case "SetParallelInput":
                (Machine.Parallel ?? throw new InvalidOperationException("No LPT1 adapter configured.")).SetInput(
                    request.Payload.Deserialize<ParallelInput>(IpcProtocol.JsonOptions) ?? throw new ArgumentException("Missing printer pin levels."));
                result = null; break;
            default: return new(1, request.RequestId, false, "UnsupportedCommand", null);
        }
        if (_definitionBinding is not null && request.Command is "CommitConfigurationChange" or "SetNetworkConnection" or
            "SetHardDisk" or "SetOpticalDisc" or "InsertFloppy" or "EjectFloppy")
        {
            try { _definitionBinding.Save(Machine.CaptureDefinition(_definitionName, _definitionId)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new(1, request.RequestId, false, "ConfigurationAppliedNotSaved", new
                { applied = true, message = "The change is active, but the PC file was not saved: " + error.Message });
            }
        }
        return new(1, request.RequestId, true, null, result);
    }

    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
    public void Dispose()
    {
        lock (_queueGate)
        {
            _disposed = true;
            _pendingMouse.Clear();
            while (_commands.TryDequeue(out var item)) item.Result.TrySetResult(new(1, item.Request.RequestId, false, "SessionStopped", null));
        }
        Machine.HardwareResetOccurred -= TrackReset;
        try { _network.Dispose(); }
        finally { try { _execution?.Dispose(); } finally { Machine.Dispose(); } }
    }
}
