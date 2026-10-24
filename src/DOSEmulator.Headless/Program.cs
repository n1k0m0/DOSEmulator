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
        Console.WriteLine("DOSEmulator development runner\n  --steps <count> --disk <raw IDE image> [--overlay <private .cow file>] --report <json> --frame <ppm>\n  --floppy-a <image> [--floppy-a-overlay <cow>] --floppy-b <image> [--floppy-b-overlay <cow>] --boot ide|floppy-a\n  --trace <jsonl> --trace-tail <json> --type-file <text> --type-after <guest text> --stop-text <guest text>\n  --swap-floppy a|b --swap-image <image> [--swap-overlay <cow>] --swap-after <guest text>\n  --write-demo <path> writes our built-in test-sector image to a NEW file.\n  --prepare-freedos / --prepare-freedos-floppy <official image> --output <new private image>\nBase images always stay read-only. Writes require an exclusive overlay. With only floppy A attached it becomes the boot device. Default boots our own test sector. FreeDOS minimal IDE/FDC paths are tested; protected mode and Windows remain unimplemented.");
        Console.WriteLine("Audio: --audio-out <new.wav> captures mixed guest audio as 48-kHz stereo PCM16. --audio-io-trace <new.jsonl> logs DSP/FM reads/writes plus DMA1/control, speaker and diagnostic writes at access completion. Profile soundCard=sb2-dsp-development adds 220h/IRQ5/DMA1 normal/high-speed output PCM; adlib-opl2-development or sb2-opl2-development add melodic FM and development CSM keying (rhythm, CSM pipeline/TL latching and full chip accuracy pending). ADPCM/recording/MIDI remain pending.\nProbe tools: --prepare-test-programs <verified minimal IDE image> --program-dir <own COM probes> --output <new image>");
        Console.WriteLine("Storage diagnostics: --floppy-io-trace <new.jsonl> logs FIFO/result and DMA2/control bytes; --bios-trace <new.jsonl> logs actual INT13/INT15 entry registers. --memory-dump <new private.bin> saves RAM for private debugging and may contain guest OS data; never publish it. --stop-screen <text> stops on actual VGA text (ASCII approximation). --steps accepts 1..10000000000 interpreter steps.\n--scenario <json> replays {waitForScreen,text|scanCodes,insertFloppy:{drive,path,overlayPath},settleNanoseconds} steps through actual KBC/FDC. Screens must remain stable for 100 ms of virtual time by default. Media paths must be absolute; writes still require private overlays. Cannot combine with --type-file or --swap-floppy.");
        Console.WriteLine("Mouse: profile mouse=ps2-two-button enables ID00 Aux/IRQ12. Scenario steps may use mouse:{x,y,buttons}; choose waitForScreen or waitForDebug (guest E9 marker, including graphics). Positive Y points up; buttons 0..3. --mouse-io-trace <new.jsonl> logs KBC/PIC bytes and actual IRQ12 entries. Wheel and electrical serial-line timing are not implemented. The GUI accepts --scenario for private guest acceptance too.");
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
    using StreamWriter? trace = Get("--trace") is { } tracePath ? new StreamWriter(tracePath, false) : null;
    if (trace is not null)
        machine.Cpu.InstructionStarting += (physical, opcode) => trace.WriteLine(JsonSerializer.Serialize(new { physical, opcode, time = machine.Scheduler.Now, position = machine.Cpu.Position }));
    var recent = new Queue<object>();
    if (Get("--trace-tail") is not null) machine.Cpu.InstructionStarting += (physical, opcode) =>
    {
        if (recent.Count >= 96) recent.Dequeue();
        recent.Enqueue(new { physical, opcode, position = machine.Cpu.Position,
            ax = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Ax), cx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Cx),
            dx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Dx), bx = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx),
            sp = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Sp), bp = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bp),
            si = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Si), di = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Di) });
    };
    machine.Running = true;
    using var audio = Get("--audio-out") is { } audioPath ? new PcmWaveRecorder(audioPath) : null;
    if (audio is not null) machine.Audio.AudioReady += audio.Write;
    using StreamWriter? audioTrace = Get("--audio-io-trace") is { } audioTracePath
        ? new(new FileStream(audioTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (audioTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        if ((write && port is 0x226 or 0xe9 or 0x61 or 0x42 or 0x43 or 2 or 3 or 0x83 or 0x0a or 0x0b or 0x0c) ||
            port is 0x22a or 0x22c or 0x22e or 0x388 or 0x389 or 0x228 or 0x229)
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
        if (vector is 0x13 or 0x15)
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
    byte[] typing = Get("--type-file") is { } typePath ? GuestKeyboard.Encode(File.ReadAllText(typePath)) : [];
    GuestTestScenario? scenario = Get("--scenario") is { } scenarioPath ? GuestTestScenario.Load(scenarioPath) : null;
    if (scenario is not null && (Get("--type-file") is not null || Get("--swap-floppy") is not null))
        throw new ArgumentException("Scenario cannot be combined with --type-file or --swap-floppy.");
    string typeAfter = Get("--type-after") ?? "C:\\>";
    string? stopText = Get("--stop-text");
    string? stopScreen = Get("--stop-screen");
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
        if (stopText is not null && machine.Board.DebugText.Contains(stopText, StringComparison.Ordinal)) break;
        if (stopScreen is not null && machine.Video.ReadText().Contains(stopScreen, StringComparison.Ordinal)) break;
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
        graphicsMode = machine.Video.GraphicsMode, indexed256 = machine.Video.Indexed256, ataSectorsRead = machine.Ata.ReadSectorCount,
        ataSectorsWritten = machine.Ata.WrittenSectorCount, diskWritable = overlayPath is not null,
        floppySectorsRead = machine.Floppy.ReadSectorCount, floppySectorsWritten = machine.Floppy.WrittenSectorCount,
        mediaSwapApplied,
        audio = new { sampleRate = 48000, channels = 2, generatedFrames = machine.Audio.GeneratedFrames,
            recordedFrames = audio?.Frames, dataSha256 = audio?.DataSha256, control = machine.Speaker.Control,
            divisor = machine.Pit.SpeakerCounter.Reload, unsupportedPitProgramming = machine.Pit.UnsupportedProgramming },
        soundBlaster = machine.SoundBlaster is { } sb ? new { sb.TransferredSamples, sb.CompletedBlocks, sb.InterruptRequests,
            sb.RemainingSamples, sb.Active, sb.Paused, sb.InterruptAsserted, sb.SpeakerEnabled, sb.HighSpeed, sb.HighSpeedSamples,
            sb.HighSpeedTransfers, sb.HighSpeedResets, sb.IgnoredHighSpeedWrites, sb.DacValue, sb.TimeConstant,
            sb.SamplePeriodNanoseconds, sb.ResetCount, sb.RejectedWrites, sb.UnsupportedCommands, sb.LastUnsupportedCommand,
            dmaResult = sb.LastDmaResult.ToString(), highSpeedOutput = true, highSpeedInput = false,
            fmImplemented = false, fmMelodicImplemented = machine.Opl is not null } : null,
        opl = machine.Opl is { } opl ? new { opl.GeneratedSamples, opl.RegisterWrites, opl.RejectedWrites, opl.KeyOnCount, opl.CsmKeyPulses,
            opl.Timer1Overflows, opl.Timer2Overflows, opl.TimerInterruptAsserted, opl.ActiveOperators, opl.UnsupportedProgramming,
            opl.UnsupportedRhythm, opl.UnsupportedCsm, opl.UnsupportedTest, opl.CsmEnabled, opl.CsmPulseActive,
            melodic = true, rhythm = false, csmKeying = true, csm = false, siliconExact = false,
            ioTiming = machine.FmIoTiming is { } timing ? new { frequency = Isa8BitIoTiming.Frequency, clocksPerByte = Isa8BitIoTiming.ClocksPerByte,
                timing.Accesses, timing.ByteCycles, timing.ElapsedNanoseconds, scope = "FM ports only; normal 8-bit path" } : null } : null,
        bootMarker = machine.Memory.ReadWord(0x500), postCode = machine.Board.PostCode,
        inputBytesSent = sent, inputBytesTotal = typing.Length,
        ps2Mouse = machine.Mouse is null ? null : new { machine.Mouse.CommandCount, machine.Mouse.PacketCount, machine.Mouse.Enabled, machine.Mouse.SampleRate, machine.Mouse.Resolution },
        scenario = scenario is null ? null : new { scenario.CompletedSteps, scenario.TotalSteps, scenario.InputBytesSent, scenario.InputBytesTotal, scenario.MediaChanges, scenario.MouseInputsSent, scenario.Complete },
        limitation = "Development real-mode subset. FreeDOS 1.4 minimal and local German MS-DOS 6.22 setup/HDD profile tested. HIMEM/EMM386 and full code-page font loading unverified; protected mode and Windows remain unimplemented." };
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
