using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

try
{
    string? Get(string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
        return args[index + 1];
    }
    if (args.Contains("--help"))
    {
        Console.WriteLine("DOSEmulator development runner\n  --steps <count> --disk <raw IDE image> [--overlay <private .cow file>] --report <json> --frame <ppm>\n  --floppy-a <image> [--floppy-a-overlay <cow>] --floppy-b <image> [--floppy-b-overlay <cow>] --boot ide|floppy-a\n  --trace <jsonl> --trace-tail <json> --type-file <text> --type-after <guest text> --stop-text <guest text>\n  --swap-floppy a|b --swap-image <image> [--swap-overlay <cow>] --swap-after <guest text>\n  --write-demo <path> writes our built-in test-sector image to a NEW file.\n  --prepare-freedos / --prepare-freedos-floppy <official image> --output <new private image>\nBase images always stay read-only. Writes require an exclusive overlay. With only floppy A attached it becomes the boot device. Default boots our own test sector. FreeDOS minimal IDE/FDC paths and PE/TSS and 4-KiB paging/V86 reference guests are tested. Original local EMM386 NOEMS boots with basic VCPI memory calls; 16/32-bit call gates with privilege/parameter/RETF paths are tested. Controlled 16/32-bit hardware tasks, task gates, task-IRET, LOCK/CMPXCHG/XADD and alignment-check retries are guest-tested; MOV-DR/breakpoint/TF reference guests, raw 80-bit x87 state and elementary numeric operations are tested. Full x87, task/fault/debug/bus precision and Windows remain pending.");
        Console.WriteLine("Audio: --audio-out <new.wav> captures mixed guest audio as 48-kHz stereo PCM16. --audio-io-trace <new.jsonl> logs DSP/FM reads/writes plus DMA1/control, speaker and diagnostic writes at access completion. Profile soundCard=sb2-dsp-development adds 220h/IRQ5/DMA1 normal/high-speed output PCM; adlib-opl2-development or sb2-opl2-development add melodic FM and development CSM keying (rhythm, CSM pipeline/TL latching and full chip accuracy pending). ADPCM/MIDI remain pending; ADC uses a virtual input without a host microphone.\nProbe tools: --prepare-test-programs <verified minimal IDE image> --program-dir <own COM probes> --output <new image>");
        Console.WriteLine("Storage diagnostics: --floppy-io-trace <new.jsonl> logs FIFO/result and DMA2/control bytes; --bios-trace <new.jsonl> logs actual INT13/INT15 entry registers. --memory-dump <new private.bin> saves RAM for private debugging and may contain guest OS data; never publish it. --stop-screen <text> stops on actual VGA text (ASCII approximation). --steps accepts 1..10000000000 interpreter steps.\n--scenario <json> replays {waitForScreen,text|scanCodes,insertFloppy:{drive,path,overlayPath},settleNanoseconds} steps through actual KBC/FDC. Screens must remain stable for 100 ms of virtual time by default. Media paths must be absolute; writes still require private overlays. Cannot combine with --type-file or --swap-floppy.");
        Console.WriteLine("Mouse: profile mouse=ps2-two-button enables ID00 Aux/IRQ12. Scenario steps may use mouse:{x,y,buttons}; choose waitForScreen or waitForDebug (guest E9 marker, including graphics). Positive Y points up; buttons 0..3. --mouse-io-trace <new.jsonl> logs KBC/PIC bytes and actual IRQ12 entries. Wheel and electrical serial-line timing are not implemented. The GUI accepts --scenario for private guest acceptance too.");
        Console.WriteLine("ADC diagnostics: --adc-test-signal midpoint|ramp chooses a deterministic virtual input (default unconnected midpoint). --adc-trace <new.jsonl> logs actual conversions and DMA results. No host microphone is opened.");
        Console.WriteLine("SB2 resources: profile soundBlasterBasePort=544|576 (220h|240h), soundBlasterIrq=3|5|7|9, soundBlasterDmaChannel=1. Hardware changes require a power cycle. --audio-irq-trace <new.jsonl> logs actual CPU entries at the selected DSP vector. The GUI offers SOUND CARD RESOURCES; the shown BLASTER value must be set in DOS by the user.");
        Console.WriteLine("Video diagnostics: --video-io-trace <new.jsonl> logs VGA port accesses and actual INT10 entry registers, including AH11 guest font pointers. Font bytes remain guest-owned; no host code-page conversion is performed.");
        Console.WriteLine("Trace filters: --trace-min/--trace-max are inclusive hexadecimal physical addresses; --trace-tail-count is 1..65536 (default 96). Filters affect diagnostics only.\nGuest resets: KBC pulses/output-port reset and port92 reset edges restart the CPU while RAM, powered devices and virtual time remain. Reports distinguish hardware-reset count from reference-firmware POST entries; resident DOS keyboard drivers may reboot in software. Scenario minimumResetCount/minimumPost10Count gate commands after a specific boot. BIOS tracing also records INT19. Stop markers wait for complete scenario input.");
        Console.WriteLine("--graphics-capture <new private directory> samples changed graphics scanout at 100-ms guest intervals, capped at 128 PPM files plus report hashes. Sampling can show partially drawn frames; it does not assert completed application presents. No native windows or inputs are used. Captures may contain private game content; never publish test artifacts.");
        return 0;
    }
    if (Get("--write-demo") is { } demoPath)
    {
        using var file = new FileStream(demoPath, FileMode.CreateNew, FileAccess.Write);
        file.Write(DevelopmentFirmware.CreateDemoDisk());
        Console.WriteLine($"Created test-sector image: {Path.GetFullPath(demoPath)}");
        return 0;
    }
    if (Get("--prepare-freedos") is { } originalPath)
    {
        string destination = Get("--output") ?? throw new ArgumentException("--output is required.");
        object metadata = FreeDosTestImage.Create(originalPath, destination);
        string metadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destination + ".json", metadataJson); Console.WriteLine(metadataJson); return 0;
    }
    if (Get("--prepare-freedos-floppy") is { } floppyOriginal)
    {
        string destination = Get("--output") ?? throw new ArgumentException("--output is required.");
        string floppyMetadata = JsonSerializer.Serialize(FreeDosFloppyTestImage.Create(floppyOriginal, destination), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destination + ".json", floppyMetadata); Console.WriteLine(floppyMetadata); return 0;
    }
    if (Get("--prepare-test-programs") is { } programBase)
    {
        string destination = Get("--output") ?? throw new ArgumentException("--output is required.");
        string programs = Get("--program-dir") ?? throw new ArgumentException("--program-dir is required.");
        string metadata = JsonSerializer.Serialize(GuestProgramTestImage.Create(programBase, programs, destination), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(destination + ".json", metadata); Console.WriteLine(metadata); return 0;
    }
    long steps = long.Parse(Get("--steps") ?? "100000");
    if (steps is < 1 or > 10_000_000_000) throw new ArgumentException("Step count must be 1..10000000000.");
    MachineProfile profile = Get("--profile") is { } profilePath
        ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(profilePath), IpcProtocol.JsonOptions) ?? throw new ArgumentException("Empty profile.") : new();
    profile.Validate();
    string? overlayPath = Get("--overlay");
    using var machine = MachineFactory.FromImages(profile, Get("--disk"), overlayPath,
        Get("--floppy-a"), Get("--floppy-a-overlay"), Get("--floppy-b"), Get("--floppy-b-overlay"), Get("--boot"));
    string? adcSignal = Get("--adc-test-signal");
    if (adcSignal is not null)
    {
        if (machine.SoundBlaster is not { } inputDsp) throw new ArgumentException("An ADC test signal requires an SB2 DSP profile.");
        if (adcSignal == "ramp") inputDsp.AdcInput = now => (byte)((now / 125000) & 255);
        else if (adcSignal != "midpoint") throw new ArgumentException("ADC test signal must be midpoint or ramp.");
    }
    uint traceFirst = Convert.ToUInt32(Get("--trace-min") ?? "0", 16), traceLast = Convert.ToUInt32(Get("--trace-max") ?? "FFFFFFFF", 16);
    if (traceFirst > traceLast) throw new ArgumentException("Invalid hexadecimal physical trace range.");
    int traceTailCount = int.Parse(Get("--trace-tail-count") ?? "96");
    if (traceTailCount is < 1 or > 65536) throw new ArgumentException("Trace tail count must be 1..65536.");
    using StreamWriter? trace = Get("--trace") is { } tracePath ? new StreamWriter(tracePath, false) : null;
    if (trace is not null)
        machine.Cpu.InstructionStarting += (physical, opcode) => { if (physical >= traceFirst && physical <= traceLast) trace.WriteLine(JsonSerializer.Serialize(new { physical, opcode, time = machine.Scheduler.Now, position = machine.Cpu.Position })); };
    var recent = new Queue<object>();
    if (Get("--trace-tail") is not null) machine.Cpu.InstructionStarting += (physical, opcode) =>
    {
        if (physical < traceFirst || physical > traceLast) return;
        if (recent.Count >= traceTailCount) recent.Dequeue();
        recent.Enqueue(new { physical, opcode, position = machine.Cpu.Position,
            ax = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Ax), cx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Cx),
            dx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Dx), bx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx),
            sp = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Sp), bp = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bp),
            si = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Si), di = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Di),
            flags = machine.Cpu.Eflags, ds = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Ds), es = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Es),
            ss = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Ss), fs = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Fs), gs = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Gs) });
    };
    machine.Running = true;
    using var audio = Get("--audio-out") is { } audioPath ? new PcmWaveRecorder(audioPath) : null;
    if (audio is not null) machine.Audio.AudioReady += audio.Write;
    using StreamWriter? audioTrace = Get("--audio-io-trace") is { } audioTracePath
        ? new(new FileStream(audioTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    using StreamWriter? adcTrace = Get("--adc-trace") is { } adcTracePath
        ? new(new FileStream(adcTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (adcTrace is not null && machine.SoundBlaster is { } adcDsp)
        adcDsp.AdcSampled += (time, value, result) => adcTrace.WriteLine(JsonSerializer.Serialize(new { time, value, dmaResult = result?.ToString() }));
    using StreamWriter? audioIrqTrace = Get("--audio-irq-trace") is { } audioIrqPath
        ? new(new FileStream(audioIrqPath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (audioIrqTrace is not null && machine.SoundBlaster is not null) machine.Cpu.InterruptEntered += vector =>
    {
        int irq = machine.Profile.SoundBlasterIrq;
        if (vector == (irq < 8 ? 8 + irq : 0x70 + irq - 8))
            audioIrqTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, vector }));
    };
    if (audioTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        int sbBase = machine.Profile.SoundBlasterBasePort;
        if ((write && (port == sbBase + 6 || port is 0xe9 or 0x61 or 0x42 or 0x43 or 2 or 3 or 0x83 or 0x0a or 0x0b or 0x0c or 0x20 or 0x21 or 0xa0 or 0xa1)) ||
            port == sbBase + 0x0a || port == sbBase + 0x0c || port == sbBase + 0x0e ||
            port == sbBase + 8 || port == sbBase + 9 || port is 0x388 or 0x389)
            audioTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, port, value, width, write }));
    };
    using StreamWriter? floppyTrace = Get("--floppy-io-trace") is { } floppyTracePath
        ? new(new FileStream(floppyTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (floppyTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        if ((write && port is 4 or 5 or 0x81 or 0x0a or 0x0b or 0x0c or 0x3f2 or 0x3f7 or 0xe9) || port == 0x3f5)
            floppyTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, port, value, width, write }));
    };
    using StreamWriter? biosTrace = Get("--bios-trace") is { } biosTracePath
        ? new(new FileStream(biosTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (biosTrace is not null) machine.Cpu.InterruptEntered += vector =>
    {
        if (vector is 0x13 or 0x15 or 0x19)
            biosTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, vector, position = machine.Cpu.Position,
                ax = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Ax), bx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx),
                cx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Cx), dx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Dx) }));
    };
    using StreamWriter? mouseTrace = Get("--mouse-io-trace") is { } mouseTracePath
        ? new(new FileStream(mouseTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (mouseTrace is not null)
    {
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is 0x60 or 0x64 || (write && port is 0x20 or 0x21 or 0xa0 or 0xa1))
                mouseTrace.WriteLine(JsonSerializer.Serialize(new { kind = "io", time = machine.Scheduler.Now, port, value, width, write }));
        };
        machine.Cpu.InterruptEntered += vector =>
        {
            if (vector == 0x74) mouseTrace.WriteLine(JsonSerializer.Serialize(new { kind = "interrupt", time = machine.Scheduler.Now, vector }));
        };
    }
    using StreamWriter? videoTrace = Get("--video-io-trace") is { } videoTracePath
        ? new(new FileStream(videoTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (videoTrace is not null)
    {
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is >= 0x3c0 and <= 0x3cf or 0x3b4 or 0x3b5 or 0x3ba or 0x3d4 or 0x3d5 or 0x3da || (port == 0xe9 && write))
                videoTrace.WriteLine(JsonSerializer.Serialize(new { kind = "io", time = machine.Scheduler.Now, port, value, width, write }));
        };
        machine.Cpu.InterruptEntered += vector =>
        {
            uint ax = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Ax);
            if (vector == 0x10)
                videoTrace.WriteLine(JsonSerializer.Serialize(new { kind = ((ax >> 8) & 255) == 0x11 ? "font" : "bios", time = machine.Scheduler.Now, ax,
                    bx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx),
                    cx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Cx),
                    dx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Dx),
                    di = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Di),
                    bp = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bp),
                    es = machine.Cpu.GetSegment(DOSEmulator.Cpu.X86.Segment.Es) }));
        };
    }
    byte[] typing = Get("--type-file") is { } typePath ? GuestKeyboard.Encode(File.ReadAllText(typePath)) : [];
    GuestTestScenario? scenario = Get("--scenario") is { } scenarioPath ? GuestTestScenario.Load(scenarioPath) : null;
    if (scenario is not null && (Get("--type-file") is not null || Get("--swap-floppy") is not null))
        throw new ArgumentException("Scenario cannot be combined with --type-file or --swap-floppy.");
    string typeAfter = Get("--type-after") ?? "C:\\>";
    string? stopText = Get("--stop-text");
    string? stopScreen = Get("--stop-screen");
    string? graphicsDirectory = Get("--graphics-capture");
    if (graphicsDirectory is not null)
    {
        graphicsDirectory = Path.GetFullPath(graphicsDirectory);
        if (Directory.Exists(graphicsDirectory) || File.Exists(graphicsDirectory)) throw new IOException("Graphics capture needs a NEW private directory.");
        Directory.CreateDirectory(graphicsDirectory);
    }
    var graphicsFrames = new List<object>();
    string? lastGraphicsHash = null;
    ulong nextGraphicsCapture = 0;
    string? swapDrive = Get("--swap-floppy"), swapImage = Get("--swap-image"), swapOverlay = Get("--swap-overlay"), swapAfter = Get("--swap-after");
    if (swapDrive is not null && (swapDrive is not ("a" or "b") || swapImage is null || swapAfter is null))
        throw new ArgumentException("--swap-floppy a|b requires --swap-image and --swap-after; --swap-overlay is optional.");
    bool mediaSwapApplied = false;
    int sent = 0; long executed = 0; bool inputStarted = false;
    while (executed < steps && machine.Fault is null)
    {
        scenario?.Pump(machine);
        if (!inputStarted && typing.Length > 0 && machine.Board.DebugText.Contains(typeAfter, StringComparison.Ordinal)) inputStarted = true;
        if (swapDrive is not null && !mediaSwapApplied && !machine.Floppy.Busy && machine.Board.DebugText.Contains(swapAfter!, StringComparison.Ordinal))
        {
            IReadOnlyBlockDevice replacement = swapOverlay is null ? new ReadOnlyRawDisk(swapImage!) : new CopyOnWriteDisk(swapImage!, swapOverlay);
            try { machine.Floppy.ReplaceMedia(swapDrive == "a" ? 0 : 1, replacement); } catch { replacement.Dispose(); throw; }
            mediaSwapApplied = true;
        }
        if (inputStarted && sent < typing.Length && GuestKeyboard.TryInjectForBios(machine, typing[sent])) sent++;
        int ran = machine.RunSteps((int)Math.Min(10000, steps - executed)); executed += ran;
        if (graphicsDirectory is not null && graphicsFrames.Count < 128 && machine.Video.GraphicsMode && machine.Scheduler.Now >= nextGraphicsCapture)
        {
            nextGraphicsCapture = machine.Scheduler.Now + 100_000_000;
            var picture = machine.CaptureFrame();
            string pictureHash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(picture.ArgbPixels.AsSpan())));
            if (pictureHash != lastGraphicsHash)
            {
                lastGraphicsHash = pictureHash;
                string filename = $"graphics-{graphicsFrames.Count:D3}.ppm";
                using var file = new FileStream(Path.Combine(graphicsDirectory, filename), FileMode.CreateNew, FileAccess.Write);
                file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{picture.Width} {picture.Height}\n255\n"));
                byte[] pixels = new byte[picture.ArgbPixels.Length * 3];
                for (int pixel = 0; pixel < picture.ArgbPixels.Length; pixel++)
                {
                    uint color = picture.ArgbPixels[pixel];
                    pixels[pixel * 3] = (byte)(color >> 16); pixels[pixel * 3 + 1] = (byte)(color >> 8); pixels[pixel * 3 + 2] = (byte)color;
                }
                file.Write(pixels);
                graphicsFrames.Add(new { filename, picture.Width, picture.Height, virtualNanoseconds = machine.Scheduler.Now,
                    frameHash = pictureHash, machine.Cpu.ProtectedMode, machine.Cpu.Virtual8086, machine.Cpu.CurrentPrivilegeLevel });
            }
        }
        // A typed command can contain the stop marker before Enter or its final key-up is sent.
        if (scenario?.Complete != false && stopText is not null && machine.Board.DebugText.Contains(stopText, StringComparison.Ordinal)) break;
        if (scenario?.Complete != false && stopScreen is not null && machine.Video.ReadText().Contains(stopScreen, StringComparison.Ordinal)) break;
        if (ran == 0) break;
    }
    if (Get("--trace-tail") is { } tailPath) File.WriteAllText(tailPath, JsonSerializer.Serialize(recent, new JsonSerializerOptions { WriteIndented = true }));
    if (Get("--memory-dump") is { } dumpPath)
    {
        using var dump = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write);
        dump.Write(machine.Memory.Ram); // Private diagnostics may contain guest OS data; never publish them.
    }
    machine.Audio.Flush(); audio?.Complete();
    var status = machine.GetStatus();
    var frame = machine.CaptureFrame();
    string hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan())));
    var result = new { status, text = machine.Video.ReadText(), frameHash = hash, frame.Width, frame.Height, frame.ModeGeneration,
        graphicsCapture = graphicsDirectory is null ? null : new { directory = graphicsDirectory, intervalNanoseconds = 100_000_000, maximumFrames = 128, frames = graphicsFrames },
        cpu = new { machine.Cpu.ProtectedMode, machine.Cpu.CurrentPrivilegeLevel, machine.Cpu.Eip, machine.Cpu.Eflags, machine.Cpu.Cr0, machine.Cpu.Cr2, machine.Cpu.Cr3,
            machine.Cpu.ProtectedModeEntries, machine.Cpu.ProtectedModeExits, machine.Cpu.ProtectedInstructions,
            machine.Cpu.FpuControlWord, machine.Cpu.FpuStatusWord, machine.Cpu.FpuTagWord, machine.Cpu.X87Instructions,
            machine.Cpu.FpuTop, machine.Cpu.FpuOpcode, machine.Cpu.FpuInstructionPointer, machine.Cpu.FpuDataPointer,
            machine.Cpu.FpuCodeSelector, machine.Cpu.FpuDataSelector,
            machine.Cpu.FpuErrorAsserted, machine.Cpu.IgnoreNumericError, machine.Cpu.WaitingForNumericError,
            machine.Cpu.NumericErrorWaits, machine.Cpu.NumericErrorFaults,
            fpuRegisters = Enumerable.Range(0, 8).Select(i => new { logical = i, empty = machine.Cpu.IsFpuRegisterEmpty(i),
                significand = machine.Cpu.GetFpuRegister(i).Significand.ToString("X16"),
                signExponent = machine.Cpu.GetFpuRegister(i).SignExponent.ToString("X4") }).ToArray(),
            machine.Cpu.PrivilegeStackSwitches, machine.Cpu.OuterPrivilegeReturns, machine.Cpu.CallGateTransfers,
            machine.Cpu.HardwareTaskSwitches, machine.Cpu.HardwareTaskReturns, machine.Cpu.TaskGateTransfers, machine.Cpu.TaskDebugTraps, machine.Cpu.Dr6,
            machine.Cpu.Dr0, machine.Cpu.Dr1, machine.Cpu.Dr2, machine.Cpu.Dr3, machine.Cpu.Dr7,
            machine.Cpu.InstructionBreakpointFaults, machine.Cpu.DataBreakpointTraps, machine.Cpu.SingleStepTraps, machine.Cpu.GeneralDetectFaults,
            machine.Cpu.LockedMemoryInstructions, machine.Cpu.InvalidLockPrefixes, machine.Cpu.AlignmentCheckEnabled, machine.Cpu.AlignmentFaults,
            machine.Cpu.Ldtr, machine.Cpu.Tr, machine.Cpu.LdtrCache, machine.Cpu.TrCache,
            machine.Cpu.PagingEnabled, machine.Cpu.Virtual8086, machine.Cpu.PageFaults, machine.Cpu.PageWalks,
            machine.Cpu.TlbHits, machine.Cpu.TlbInvalidations, machine.Cpu.Virtual8086Entries, machine.Cpu.Virtual8086Exits, machine.Cpu.Virtual8086Instructions,
            machine.Cpu.Gdtr, machine.Cpu.Idtr, machine.Cpu.TripleFaulted },
        numericError = new { machine.FpuError.FerrAsserted, machine.FpuError.InterruptAsserted,
            machine.FpuError.IgnoreNumericError, machine.FpuError.ErrorEdges, machine.FpuError.Acknowledgements },
        graphicsMode = machine.Video.GraphicsMode, indexed256 = machine.Video.Indexed256, hardwareResetCount = machine.HardwareResetCount, ataSectorsRead = machine.Ata.ReadSectorCount,
        ataSectorsWritten = machine.Ata.WrittenSectorCount, diskWritable = overlayPath is not null,
        floppySectorsRead = machine.Floppy.ReadSectorCount, floppySectorsWritten = machine.Floppy.WrittenSectorCount,
        mediaSwapApplied,
        audio = new { sampleRate = 48000, channels = 2, generatedFrames = machine.Audio.GeneratedFrames,
            recordedFrames = audio?.Frames, dataSha256 = audio?.DataSha256, control = machine.Speaker.Control,
            divisor = machine.Pit.SpeakerCounter.Reload, unsupportedPitProgramming = machine.Pit.UnsupportedProgramming },
        soundBlaster = machine.SoundBlaster is { } sb ? new { sb.TransferredSamples, sb.CompletedBlocks, sb.InterruptRequests,
            basePort = sb.IoBasePort, irq = machine.Profile.SoundBlasterIrq, dmaChannel = machine.Profile.SoundBlasterDmaChannel,
            blasterEnvironment = machine.Profile.GetBlasterEnvironment(),
            sb.RecordedSamples, sb.ConvertedSamples, sb.HighSpeedRecordedSamples, sb.Recording, sb.AdcValue,
            sb.AdpcmBits, sb.AdpcmDmaBytes, sb.AdpcmDecodedSamples,
            sb.RemainingSamples, sb.Active, sb.Paused, sb.InterruptAsserted, sb.SpeakerEnabled, sb.HighSpeed, sb.HighSpeedSamples,
            sb.HighSpeedTransfers, sb.HighSpeedResets, sb.IgnoredHighSpeedWrites, sb.DacValue, sb.TimeConstant,
            sb.SamplePeriodNanoseconds, sb.ResetCount, sb.RejectedWrites, sb.UnsupportedCommands, sb.LastUnsupportedCommand,
            dmaResult = sb.LastDmaResult.ToString(), highSpeedOutput = true, highSpeedInput = true, inputPcm = true,
            adcInput = adcSignal ?? "unconnected-midpoint", hostMicrophone = false,
            fmImplemented = false, fmMelodicImplemented = machine.Opl is not null } : null,
        opl = machine.Opl is { } opl ? new { opl.GeneratedSamples, opl.RegisterWrites, opl.RejectedWrites, opl.KeyOnCount, opl.CsmKeyPulses,
            opl.Timer1Overflows, opl.Timer2Overflows, opl.TimerInterruptAsserted, opl.ActiveOperators, opl.UnsupportedProgramming,
            opl.UnsupportedRhythm, opl.UnsupportedCsm, opl.UnsupportedTest, opl.CsmEnabled, opl.CsmPulseActive, opl.RhythmEnabled,
            melodic = true, rhythm = true, csmKeying = true, csmTotalLevelLatch = true, csm = true, siliconExact = false,
            ioTiming = machine.FmIoTiming is { } timing ? new { frequency = Isa8BitIoTiming.Frequency, clocksPerByte = Isa8BitIoTiming.ClocksPerByte,
                timing.Accesses, timing.ByteCycles, timing.ElapsedNanoseconds, scope = "FM ports only; normal 8-bit path" } : null } : null,
        bootMarker = machine.Memory.ReadWord(0x500), postCode = machine.Board.PostCode,
        inputBytesSent = sent, inputBytesTotal = typing.Length,
        ps2Mouse = machine.Mouse is null ? null : new { machine.Mouse.CommandCount, machine.Mouse.PacketCount, machine.Mouse.Enabled, machine.Mouse.SampleRate, machine.Mouse.Resolution },
        scenario = scenario is null ? null : new { scenario.CompletedSteps, scenario.TotalSteps, scenario.InputBytesSent, scenario.InputBytesTotal, scenario.MediaChanges, scenario.MouseInputsSent, scenario.Complete },
        limitation = "Development 486 subset. FreeDOS 1.4 minimal and local German MS-DOS 6.22 setup/HDD profile tested. Guest-loaded CP850 text and graphics verified at 8/14/16 pixels per character. Built-in CP437 fonts are original reference-platform artwork, not IBM ROM replicas. PE16/PE32, descriptor caches and original HIMEM.SYS/XMS on the local German MS-DOS 6.22 profile are tested. LLDT/LTR, TSS interrupt stack changes and outer IRET/RETF are implemented. 4-KiB paging/TLB, V86 monitor/bitmap paths and original local EMM386 NOEMS with basic VCPI memory calls are tested. 16/32-bit call gates with TSS stacks, parameters and outer RETF are tested. Controlled 16/32-bit hardware tasks, task gates and task-IRET are guest-tested. LOCK/CMPXCHG/XADD, alignment-check retries and the 486 word/dword pseudo-descriptor path are tested on controlled guests. Debug-register/TF reference guests, raw 80-bit x87 save/restore, elementary numeric operations and selected external FERR/IGNNE/IRQ13 and native MF return paths are implemented. General x87, task/fault/debug/bus precision, DOS-extender/EMS coverage and Windows remain pending." };
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions(IpcProtocol.JsonOptions) { WriteIndented = true });
    Console.WriteLine(json);
    if (Get("--report") is { } reportPath) File.WriteAllText(reportPath, json);
    if (Get("--frame") is { } framePath)
    {
        using var file = File.Create(framePath);
        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
        foreach (uint pixel in frame.ArgbPixels) file.Write([(byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel]);
    }
    return machine.Fault is null ? 0 : 1;
}
catch (Exception ex) when (ex is ArgumentException or IOException or FormatException or OverflowException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
