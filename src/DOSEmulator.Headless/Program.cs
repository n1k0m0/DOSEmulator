// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

try
{
    // Retrieves a named launch option and rejects a missing value rather than silently consuming another option.
    string? Get(string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
        return args[index + 1];
    }
    if (args.Contains("--help"))
    {
        Console.WriteLine(DOSEIdentity.DisplayName + " development runner\n  --steps <count> --disk <raw IDE image> [--overlay <private .cow file>] --report <json> --frame <ppm>\n  --floppy-a <image> [--floppy-a-overlay <cow>] --floppy-b <image> [--floppy-b-overlay <cow>] --boot ide|floppy-a\n  --trace <jsonl> --trace-tail <json> --type-file <text> --type-after <guest text> --stop-text <guest text>\n  --swap-floppy a|b --swap-image <image> [--swap-overlay <cow>] --swap-after <guest text>\n  --write-demo <path> writes our built-in test-sector image to a NEW file.\n  --prepare-freedos / --prepare-freedos-floppy <official image> --output <new private image>\nBase images always stay read-only. Writes require an exclusive overlay. With only floppy A attached it becomes the boot device. Default boots our own test sector. FreeDOS minimal IDE/FDC paths and PE/TSS and 4-KiB paging/V86 reference guests are tested. Original local EMM386 NOEMS boots with basic VCPI memory calls; 16/32-bit call gates with privilege/parameter/RETF paths are tested. Controlled 16/32-bit hardware tasks, task gates, task-IRET, LOCK/CMPXCHG/XADD and alignment-check retries are guest-tested; MOV-DR/breakpoint/TF reference guests, raw 80-bit x87 state and elementary numeric operations are tested. Full x87, task/fault/debug/bus precision and Windows remain pending.");
        Console.WriteLine("PC files: --machine <pc.dospc.json> loads a complete hardware/media configuration. --save-machine <new.json> [--machine-name <name>] saves the selected configuration without running it. --disk1/--overlay1, --disk2/--overlay2, --disk3/--overlay3 attach the other IDE slots; --disk remains primary master. --create-disk <new.img> --size <number> --unit MiB|GiB creates a blank zeroed image (1 MiB..128 GiB), never overwriting an existing file.");
        Console.WriteLine("Audio: --audio-out <new.wav> captures mixed guest audio as 48-kHz stereo PCM16. --audio-io-trace <new.jsonl> logs DSP/FM reads/writes plus DMA1/control, speaker and diagnostic writes at access completion. Profile soundCard=sb2-dsp-development adds 220h/IRQ5/DMA1 normal/high-speed output PCM; adlib-opl2-development or sb2-opl2-development add melodic FM and development CSM keying (rhythm, CSM pipeline/TL latching and full chip accuracy pending). ADPCM/MIDI remain pending; ADC uses a virtual input without a host microphone.\nProbe tools: --prepare-test-programs <verified minimal IDE image> --program-dir <own COM probes> --output <new image>");
        Console.WriteLine("Storage diagnostics: --floppy-io-trace <new.jsonl> logs FIFO/result and DMA2/control bytes; --bios-trace <new.jsonl> logs actual INT13/INT15 entry registers. --memory-dump <new private.bin> saves RAM for private debugging and may contain guest OS data; never publish it. --stop-screen <text> stops on actual VGA text (ASCII approximation). --steps accepts 1..10000000000 interpreter steps.\n--scenario <json> replays {waitForScreen,text|scanCodes,insertFloppy:{drive,path,overlayPath},settleNanoseconds} steps through actual KBC/FDC. Screens must remain stable for 100 ms of virtual time by default. Media paths must be absolute; writes still require private overlays. Cannot combine with --type-file or --swap-floppy.");
        Console.WriteLine("Mouse: profile mouse=ps2-two-button enables ID00 Aux/IRQ12. Scenario steps may use mouse:{x,y,buttons}; choose waitForScreen or waitForDebug (guest E9 marker, including graphics). Positive Y points up; buttons 0..3. --mouse-io-trace <new.jsonl> logs KBC/PIC bytes and actual IRQ12 entries. Wheel and electrical serial-line timing are not implemented. The GUI accepts --scenario for private guest acceptance too.");
    Console.WriteLine("Gameport: gameport=ibm-game-control-development enables the standalone IBM analog adapter. Scenario gameport:{axOhms,ayOhms,bxOhms,byOhms,buttons} supplies external inputs: 0..100000 ohms or null for each open circuit; buttons 0..15. --gameport-io-trace <new.jsonl> observes decoded byte transfers and E9 markers. DOSEBios INT15/AH84 reads switches or bounded resistance counts; no host gamepad or MIDI endpoint is opened.");
    Console.WriteLine("Ethernet: up to two networkAdapters in the saved hardware profile select ne2000-isa or rtl8029-pci, each with nat/host-only/bridged, persistent MAC and explicit cable. --enable-network activates configured cold-start cables. Strict snapshot loads stay disconnected unless --enable-network --reconnect-network is supplied. Bridged requires separately installed Npcap and an exact selected adapter. No host interface is opened by the hardware factory.");
    Console.WriteLine("COM1: serialPort=pc16550d-com1-development enables the virtual National PC16550D at 3F8h/IRQ4. Scenario serial:{data:[0..255],errors:PE/FE/BI,cts,dsr,ri,dcd} queues complete timed frames and pin changes. --serial-echo explicitly connects a framed virtual echo peer with CTS/DSR/DCD asserted; --serial-io-trace <new.jsonl> observes ports, TX frames, E9 and real PIC IRQ4 acknowledgements. No host COM device or network is opened.");
    Console.WriteLine("LPT1: parallelPort=ibm-printer-adapter-development enables the IBM 6361507 adapter at 378h/IRQ7 with original aliases. --parallel-sink explicitly attaches a nominal virtual raw-byte peer; --parallel-output <new.bin> captures its accepted bytes, --parallel-io-trace <new.jsonl> observes actual IO/pins/bytes/PIC IRQ7. Scenario parallel:{busyHigh,ackHigh,paperEndHigh,selectedHigh,errorHigh} drives unconnected external lines. No host printer, EPP, ECP or bidirectional mode is opened.");
        Console.WriteLine("MIDI: midiInterface=ct1740-mpu401-uart-development explicitly enables the CT1740 MPU UART at midiBasePort=768|816 (300h|330h), sharing its DSP IRQ and mixer82 bit2. --midi-output <new.bin> captures complete outgoing 31.25-kbaud bytes; --midi-io-trace <new.jsonl> observes real ports/frames/IRQ. Scenario midi:{data:[0..255]} or SendMidiInput queues outer full frames. No host MIDI, synthesizer, Intelligent Mode or raw electrical accuracy is implied.");
        Console.WriteLine("ATAPI: --profile profiles/dose-430fx-piix-s3-atapi-dev-v1.json [--cdrom <read-only cooked.iso>] installs one SFF-8020i 1995 PIO CD-ROM target at the profile's reserved IDE slot. Empty tray is valid. ISO blocks are 2048 bytes; no COW, host optical device, DMA, overlap or CD audio playback. Saved PCs preserve opticalDiscs separately from hardDisks. Native original CD-driver/MSCDEX and Windows acceptance remain separate.");
        Console.WriteLine("Full snapshots: --save-snapshot <new.dossav> seals CPU/raw FPU/RAM/devices/scheduler and all media at the final owner boundary; --load-snapshot <existing.dossav> --snapshot-branch <new.dosebranch> resumes on independent journals. Requires the identical VM build and unchanged protected base images. No host devices or already emitted output queues are saved.");
        Console.WriteLine("Media checkpoints: --capture-media <new.dosemedia> captures all file-backed media after the run at one owner-thread boundary. --branch-media <checkpoint.dosemedia> --output <new.dosebranch> checks dependencies and creates independent writable journals plus branch.dospc.json for a cold start. CPU/RAM/devices/scheduler are not saved; this is not a running-state snapshot. Originals and existing targets are never overwritten.");
        Console.WriteLine("ADC diagnostics: --adc-test-signal midpoint|ramp chooses a deterministic virtual input (default unconnected midpoint). --adc-trace <new.jsonl> logs actual conversions and DMA results. No host microphone is opened.");
        Console.WriteLine("SB2 resources: profile soundBlasterBasePort=544|576 (220h|240h), soundBlasterIrq=3|5|7|9, soundBlasterDmaChannel=1. Hardware changes require a power cycle. --audio-irq-trace <new.jsonl> logs actual CPU entries at the selected DSP vector. The GUI offers SOUND CARD RESOURCES; the shown BLASTER value must be set in DOS by the user.");
        Console.WriteLine("Video diagnostics: --video-io-trace <new.jsonl> logs VGA port accesses and actual INT10 entry registers, including AH11 guest font pointers. Font bytes remain guest-owned; no host code-page conversion is performed.");
        Console.WriteLine("Video profile videoCard=et4000ax-isa-1m-development adds banked 1-MiB ET4000AX, legacy modes 2Eh/30h/38h and a bounded VBE 1.2-format interface for 101h/103h/105h (640x480/800x600/1024x768, 256 colors): controller/mode info, set/get and bank control. No full VBE conformance, LFB or high-color. Its optional video trace also observes originating CD 10 instructions through V86 monitors; this diagnostic disables CPU fast batches.");
        Console.WriteLine("Trace filters: --trace-min/--trace-max are inclusive hexadecimal physical addresses; --trace-tail-count is 1..65536 (default 96). Filters affect diagnostics only.\nGuest resets: KBC pulses/output-port reset and port92 reset edges restart the CPU while RAM, powered devices and virtual time remain. Reports distinguish hardware-reset count from DOSEBios POST entries; resident DOS keyboard drivers may reboot in software. Scenario minimumResetCount/minimumPost10Count gate commands after a specific boot. BIOS tracing also records INT19. Stop markers wait for complete scenario input.");
        Console.WriteLine("--graphics-capture <new private directory> samples changed graphics scanout at 100-ms guest intervals, capped at 128 PPM files plus report hashes. Sampling can show partially drawn frames; it does not assert completed application presents. No native windows or inputs are used. Captures may contain private game content; never publish test artifacts.");
        return 0;
    }
    if (Get("--create-disk") is { } newDisk)
    {
        // A German decimal comma must not silently become an invariant thousands separator (1,5 -> 15).
        decimal size = decimal.Parse(Get("--size") ?? throw new ArgumentException("--size is required."),
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture);
        long bytes = RawDiskImage.SizeInBytes(size, Get("--unit") ?? "MiB");
        RawDiskImage.Create(newDisk, bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { createdDisk = Path.GetFullPath(newDisk), bytes, sectorSize = 512 }));
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
    if (Get("--branch-media") is { } mediaCheckpoint)
    {
        string destination = Get("--output") ?? throw new ArgumentException("--output requires a new .dosebranch directory.");
        var definition = MediaCheckpointStore.CreateBranch(mediaCheckpoint, destination);
        Console.WriteLine(JsonSerializer.Serialize(new { createdMediaBranch = Path.GetFullPath(destination),
            machineFile = Path.Combine(Path.GetFullPath(destination), "branch.dospc.json"), definition, coldStartRequired = true }, IpcProtocol.JsonOptions));
        return 0;
    }
    long steps = long.Parse(Get("--steps") ?? "100000");
    if (steps is < 1 or > 10_000_000_000) throw new ArgumentException("Step count must be 1..10000000000.");
    using var machine = MachineLaunch.Open(Get);
    using var network = new DOSEmulator.Runtime.Networking.NetworkService(machine,
        enableHostNetworking: args.Contains("--enable-network"));
    if (machine.IsRestoredSnapshot && args.Contains("--reconnect-network"))
    {
        if (!args.Contains("--enable-network")) throw new ArgumentException("--reconnect-network requires --enable-network.");
        foreach (var adapter in machine.Profile.NetworkAdapters.Where(n => n.CableConnected)) network.SetConnected(adapter.Slot, true);
    }
    string? midiOutputPath = Get("--midi-output");
    if (midiOutputPath is not null && machine.Midi is null) throw new ArgumentException("MIDI capture requires an explicitly selected MPU-UART profile.");
    using FileStream? midiOutput = midiOutputPath is not null ? new(midiOutputPath, FileMode.CreateNew, FileAccess.Write) : null;
    using var midiHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    if (midiOutput is not null) machine.Midi!.Transmitted += frame =>
    {
        midiOutput.WriteByte(frame.Data); Span<byte> one = stackalloc byte[1] { frame.Data }; midiHash.AppendData(one);
    };
    bool parallelSink = args.Contains("--parallel-sink") || machine.Parallel?.Printer is not null;
    var virtualPrinter = parallelSink ? (machine.Parallel?.Printer ?? (machine.Parallel ?? throw new ArgumentException("A virtual printer requires an LPT1 profile.")).ConnectPrinter()) : null;
    string? parallelOutputPath = Get("--parallel-output");
    if (parallelOutputPath is not null && virtualPrinter is null) throw new ArgumentException("--parallel-output requires explicit --parallel-sink.");
    using FileStream? parallelOutput = parallelOutputPath is not null ? new(parallelOutputPath, FileMode.CreateNew, FileAccess.Write) : null;
    using var parallelHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    if (parallelOutput is not null) virtualPrinter!.Accepted += item =>
    {
        parallelOutput.WriteByte(item.Data); Span<byte> one = stackalloc byte[1] { item.Data }; parallelHash.AppendData(one);
    };
    bool serialEcho = args.Contains("--serial-echo");
    if (serialEcho)
    {
        machine.ConnectSerialEcho();
    }
    if (Get("--save-machine") is { } saveMachine)
    {
        MachineDefinitionStore.Save(saveMachine, machine.CaptureDefinition(Get("--machine-name")));
        Console.WriteLine(JsonSerializer.Serialize(new { savedMachine = Path.GetFullPath(saveMachine) }));
        return 0;
    }
    string? adcSignal = Get("--adc-test-signal");
    if (adcSignal is not null) machine.SetVirtualAdc(adcSignal);
    string? sb16AdcSignal = Get("--sb16-adc-test-signal");
    if (sb16AdcSignal is not null) machine.SetVirtualAdc(sb16AdcSignal, true);
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
    using BinaryWriter? opl3Native = Get("--opl3-native-out") is { } opl3Path
        ? new(new FileStream(opl3Path, FileMode.CreateNew, FileAccess.Write)) : null;
    if (opl3Native is not null)
    {
        if (machine.Opl3 is not { } synth3) throw new ArgumentException("OPL3 capture requires a CT1740 profile.");
        synth3.SampleGenerated += sample => { opl3Native.Write(sample.A); opl3Native.Write(sample.B); opl3Native.Write(sample.C); opl3Native.Write(sample.D); };
    }
    using StreamWriter? audioTrace = Get("--audio-io-trace") is { } audioTracePath
        ? new(new FileStream(audioTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    using StreamWriter? adcTrace = Get("--adc-trace") is { } adcTracePath
        ? new(new FileStream(adcTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (adcTrace is not null && machine.SoundBlaster is { } adcDsp)
        adcDsp.AdcSampled += (time, value, result) => adcTrace.WriteLine(JsonSerializer.Serialize(new { time, value, dmaResult = result?.ToString() }));
    using StreamWriter? sb16Trace = Get("--sb16-trace") is { } trace16Path
        ? new(new FileStream(trace16Path, FileMode.CreateNew, FileAccess.Write)) : null;
    if (sb16Trace is not null && machine.SoundBlaster16 is { } trace16)
    {
        trace16.TransferStarted += transfer => sb16Trace.WriteLine(JsonSerializer.Serialize(new { kind = "start", transfer }, IpcProtocol.JsonOptions));
        trace16.SampleTransferred += sample => sb16Trace.WriteLine(JsonSerializer.Serialize(new { kind = "sample", sample }, IpcProtocol.JsonOptions));
        trace16.BlockCompleted += (time, word) => sb16Trace.WriteLine(JsonSerializer.Serialize(new { kind = "block", time, word }, IpcProtocol.JsonOptions));
    }
    using StreamWriter? audioIrqTrace = Get("--audio-irq-trace") is { } audioIrqPath
        ? new(new FileStream(audioIrqPath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (audioIrqTrace is not null && machine.SoundBlaster16 is not null) machine.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
    {
        if (irq == machine.SoundBlaster16.Irq)
            audioIrqTrace.WriteLine(JsonSerializer.Serialize(new { kind = "hardware", time = machine.Scheduler.Now, irq, vector, spurious }));
    };
    if (audioIrqTrace is not null && machine.SoundBlaster is not null) machine.Cpu.InterruptEntered += vector =>
    {
        int irq = machine.SoundBlaster16?.Irq ?? machine.Profile.SoundBlasterIrq;
        if (vector == (irq < 8 ? 8 + irq : 0x70 + irq - 8))
            audioIrqTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, vector }));
    };
    if (audioTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        int sbBase = machine.Profile.SoundBlasterBasePort;
        if ((write && (port == sbBase + 6 || port is 0xe9 or 0x61 or 0x42 or 0x43 or 2 or 3 or 0x83 or 0x0a or 0x0b or 0x0c or 0x20 or 0x21 or 0xa0 or 0xa1)) ||
            (machine.SoundBlaster16 is not null && (port is >= 0xc0 and < 0xe0 or 0x89 or 0x8a or 0x8b or 0x87 or 0x82 ||
                port == sbBase + 4 || port == sbBase + 5 || port == sbBase + 0x0f)) ||
            (machine.Opl3 is not null && (port >= sbBase && port < sbBase + 4 || port is 0x38a or 0x38b)) ||
            port == sbBase + 0x0a || port == sbBase + 0x0c || port == sbBase + 0x0e ||
            port == sbBase + 8 || port == sbBase + 9 || port is 0x388 or 0x389)
            audioTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, port, value, width, write }));
    };
    using StreamWriter? gameportTrace = Get("--gameport-io-trace") is { } gameportTracePath
        ? new(new FileStream(gameportTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (gameportTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        if (AnalogGameport.Decodes(port) || port == 0xe9 && write)
            gameportTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, port, value, width, write }));
    };
    using StreamWriter? ideTrace = Get("--ide-io-trace") is { } ideTracePath
        ? new(new FileStream(ideTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (ideTrace is not null) machine.Ports.Accessed += (port, value, width, write) =>
    {
        if (port is >= 0x1f0 and <= 0x1f7 or >= 0x170 and <= 0x177 or 0x3f6 or 0x376 or 0xe9)
            ideTrace.WriteLine(JsonSerializer.Serialize(new { time = machine.Scheduler.Now, port, value, width, write }));
    };
    using StreamWriter? serialTrace = Get("--serial-io-trace") is { } serialTracePath
        ? new(new FileStream(serialTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (serialTrace is not null)
    {
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is >= 0x3f8 and <= 0x3ff or 0x20 or 0x21 or 0x70 or 0x71 || port == 0xe9 && write)
                serialTrace.WriteLine(JsonSerializer.Serialize(new { type = "io", time = machine.Scheduler.Now, port, value, width, write }));
        };
        machine.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
        {
            if (irq == 4) serialTrace.WriteLine(JsonSerializer.Serialize(new { type = "irq", time = machine.Scheduler.Now, irq, vector, spurious }));
        };
        if (machine.Serial is { } serial) serial.Transmitted += frame => serialTrace.WriteLine(JsonSerializer.Serialize(new { type = "tx", frame }));
    }
    using StreamWriter? parallelTrace = Get("--parallel-io-trace") is { } parallelTracePath
        ? new(new FileStream(parallelTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (parallelTrace is not null)
    {
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (ParallelPrinterPort.Decodes(port) || port is 0x20 or 0x21 || port == 0xe9 && write)
                parallelTrace.WriteLine(JsonSerializer.Serialize(new { type = "io", time = machine.Scheduler.Now, port, value, width, write }));
        };
        machine.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
        {
            if (irq == 7) parallelTrace.WriteLine(JsonSerializer.Serialize(new { type = "irq", time = machine.Scheduler.Now, irq, vector, spurious }));
        };
        if (machine.Parallel is { } parallel) parallel.InputsChanged += pins => parallelTrace.WriteLine(JsonSerializer.Serialize(new { type = "pins", pins }));
        if (virtualPrinter is not null) virtualPrinter.Accepted += item => parallelTrace.WriteLine(JsonSerializer.Serialize(new { type = "byte", item }));
    }
    using StreamWriter? midiTrace = Get("--midi-io-trace") is { } midiTracePath
        ? new(new FileStream(midiTracePath, FileMode.CreateNew, FileAccess.Write)) : null;
    if (midiTrace is not null && machine.Midi is { } midi)
    {
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port == midi.BasePort || port == midi.BasePort + 1 || port is 0x20 or 0x21 or 0xa0 or 0xa1 ||
                port >= machine.Profile.SoundBlasterBasePort && port <= machine.Profile.SoundBlasterBasePort + 15 || port == 0xe9 && write)
                midiTrace.WriteLine(JsonSerializer.Serialize(new { type = "io", time = machine.Scheduler.Now, port, value, width, write }));
        };
        machine.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
        {
            if (irq is 5 or 7 or 9 or 10) midiTrace.WriteLine(JsonSerializer.Serialize(new { type = "irq", time = machine.Scheduler.Now, irq, vector, spurious, sources = machine.SoundBlaster16!.InterruptSources }));
        };
        midi.Transmitted += frame => midiTrace.WriteLine(JsonSerializer.Serialize(new { type = "tx", frame }));
        midi.Received += reception => midiTrace.WriteLine(JsonSerializer.Serialize(new { type = "rx", reception }));
        midi.InputQueued += (time, data) => midiTrace.WriteLine(JsonSerializer.Serialize(new { type = "input", time, data = data.Select(v => (int)v).ToArray() }));
    }
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
        if (machine.Profile.IsEt4000)
        {
            // In V86 a monitor may reflect INT10 through GP/IRET instead of an
            // InterruptEntered(10h) notification. Observe the guest CD 10 itself.
            // Side-effect-free physical peeks; never fetch from MMIO or guess across pages.
            // This optional diagnostic observer deliberately disables CPU fast batches.
            machine.Cpu.InstructionStarting += (address, opcode) =>
            {
                if (opcode == 0xcd && (address & 4095) != 4095 && machine.Memory.TryPeekByte(address, out byte first) && first == 0xcd &&
                    machine.Memory.TryPeekByte(address + 1, out byte vector) && vector == 0x10)
                    videoTrace.WriteLine(JsonSerializer.Serialize(new { kind = "int10", time = machine.Scheduler.Now,
                        address, ax = machine.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Ax),
                        machine.Cpu.Virtual8086, machine.Cpu.CurrentPrivilegeLevel }));
            };
        }
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is >= 0x3c0 and <= 0x3cf or 0x3b4 or 0x3b5 or 0x3b8 or 0x3ba or 0x3bf or 0x3d4 or 0x3d5 or 0x3d8 or 0x3da || (port == 0xe9 && write))
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
    var graphicsSurface = new VgaFrameBuffer(); // Saved/hashed synchronously before this surface is reused.
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
        network.Pump();
        int ran = machine.RunSteps((int)Math.Min(10000, steps - executed)); executed += ran;
        if (graphicsDirectory is not null && graphicsFrames.Count < 128 && machine.Video.GraphicsMode && machine.Scheduler.Now >= nextGraphicsCapture)
        {
            nextGraphicsCapture = machine.Scheduler.Now + 100_000_000;
            var picture = machine.CaptureFrame(graphicsSurface);
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
    var mediaCheckpointResult = Get("--capture-media") is { } checkpointPath ? MediaCheckpointStore.Capture(machine, checkpointPath) : null;
    if (Get("--trace-tail") is { } tailPath) File.WriteAllText(tailPath, JsonSerializer.Serialize(recent, new JsonSerializerOptions { WriteIndented = true }));
    if (Get("--memory-dump") is { } dumpPath)
    {
        using var dump = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write);
        dump.Write(machine.Memory.Ram); // Private diagnostics may contain guest OS data; never publish them.
    }
    var snapshotResult = Get("--save-snapshot") is { } snapshotPath ? SnapshotStore.Capture(machine, snapshotPath) : null;
    machine.Audio.Flush(); audio?.Complete(); parallelOutput?.Flush(); midiOutput?.Flush();
    var status = machine.GetStatus();
    var frame = machine.CaptureFrame();
    string hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan())));
    string cpuLimitation = machine.Profile.IsTrio64
        ? "DOSE 430FX/PIIX reset board with original S3 86C764 PCI VGA/bank/LFB/8bpp subset, P54C B5 at nominal 100 MHz with 32 MiB. Acceleration, full S3 and Windows 95 acceptance remain pending. "
        : machine.Profile.IsPiixReset
        ? "DOSE 82437FX / original-PIIX IDE/busmaster and CF9/INIT reset-domain subset, P54C B5 at nominal 100 MHz with 32 MiB. Own pulse timing and external ISA wiring; complete historical chipset/Windows 95 acceptance remain pending. "
        : machine.Profile.IsPiixIde
        ? "DOSE 82437FX / original-PIIX IDE/busmaster subset, P54C B5 at nominal 100 MHz with 32 MiB. Chipset reset/CF9/INIT and complete historical 430FX/Windows 95 acceptance remain pending. "
        : machine.Profile.IsPiixIrq
        ? "DOSE 82437FX / original-PIIX IRQ subset, P54C B5 at nominal 100 MHz with 32 MiB. PIIX IDE/reset and complete historical 430FX/Windows 95 acceptance remain pending. "
        : machine.Profile.IsFxHost
        ? "DOSE 82437FX host subset / P54C B5 at nominal 100 MHz with 32 MiB. PIIX and complete historical 430FX/Windows 95 platform acceptance remain pending. "
        : machine.Profile.IsP54c
        ? "DOSE AT / P54C B5 subset at nominal 90 MHz. Own validation platform; no 430FX or Windows 95 platform acceptance. The following documented 486 reference coverage is not a universal CPU/device compatibility claim. "
        : "Development 486 subset. Documented reference coverage, not a universal CPU/device compatibility claim. ";
    var result = new { status, fullSnapshot = snapshotResult, mediaCheckpoint = mediaCheckpointResult, text = machine.Video.ReadText(), frameHash = hash, frame.Width, frame.Height, frame.ModeGeneration,
        graphicsCapture = graphicsDirectory is null ? null : new { directory = graphicsDirectory, intervalNanoseconds = 100_000_000, maximumFrames = 128, frames = graphicsFrames },
        cpu = new { machine.Cpu.Model, machine.Cpu.TimestampFrequency, machine.Cpu.Cr4, machine.Cpu.HasTimeStampCounter, machine.Cpu.TimestampCounter,
            machine.Cpu.ProtectedMode, machine.Cpu.CurrentPrivilegeLevel, machine.Cpu.Eip, machine.Cpu.Eflags, machine.Cpu.Cr0, machine.Cpu.Cr2, machine.Cpu.Cr3,
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
        video = new { model = machine.Profile.VideoCard, machine.Video.VideoMemoryBytes, machine.Video.SegmentSelect,
            machine.Video.ExtendedRegistersUnlocked, machine.Video.SegmentRegisterEnabled, machine.Video.BankSwitchWrites,
            vbe = machine.Profile.IsEt4000, vbeInterface = machine.Profile.IsEt4000 ? "1.2-format banked subset 00/01/02/03/05" : null,
            fullVbeConformance = false, linearFramebuffer = machine.Profile.IsTrio64, highColor = false, fullChip = false },
        gameport = machine.Gameport is { } gameport ? new { model = AnalogGameport.ProfileId, gameport.Input,
            gameport.ReadCount, gameport.TriggerCount, gameport.InputUpdates, gameport.ConversionStarted, gameport.TriggerTime,
            nominalTiming = "24200 + 11 * resistanceOhms nanoseconds; sampled at trigger", hostGamepad = false, midi = false, biosAh84 = true } : null,
        midi = machine.Midi is { } midiDevice ? new { model = Mpu401Uart.ProfileId, midiDevice.BasePort,
            midiDevice.UartMode, midiDevice.Status, midiDevice.ReceiveCount, midiDevice.ExternalFramesPending, midiDevice.Transmitting, midiDevice.Receiving,
            midiDevice.TransmittedBytes, midiDevice.ReceivedFrames, midiDevice.ReceivedBytes, midiDevice.IgnoredInputFrames, midiDevice.Overruns,
            midiDevice.Resets, midiDevice.Acknowledgements, midiDevice.RejectedWrites, midiDevice.IgnoredCommands, midiDevice.AbortedOutputBytes,
            midiDevice.InterruptAsserted, midiDevice.InterruptEdges, baud = 31250, dataBits = 8, stopBits = 1, parity = false,
            outputFileBytes = midiOutput?.Position, outputSha256 = midiOutput is null ? null : Convert.ToHexString(midiHash.GetHashAndReset()),
            hostMidi = false, synthesizer = false, intelligentMode = false, siliconExact = false } : null,
        parallel = machine.Parallel is { } parallelDevice ? new { model = ParallelPrinterPort.ProfileId,
            parallelDevice.Data, parallelDevice.Control, parallelDevice.ControlPins, parallelDevice.InputPins, parallelDevice.Status,
            parallelDevice.ReadCount, parallelDevice.WriteCount, parallelDevice.InterruptAsserted, parallelDevice.InterruptEdges,
            virtualSink = parallelSink, acceptedBytes = virtualPrinter?.AcceptedBytes, rejectedStrobes = virtualPrinter?.RejectedStrobes,
            initializations = virtualPrinter?.Initializations, outputFileBytes = parallelOutput?.Position,
            outputSha256 = parallelOutput is null ? null : Convert.ToHexString(parallelHash.GetHashAndReset()),
            hostPrinter = false, epp = false, ecp = false, bidirectional = false, fullPrinter = false } : null,
        serial = machine.Serial is { } serialDevice ? new { model = Uart16550.ProfileId, serialDevice.Divisor,
            serialDevice.LineControl, serialDevice.ModemControl, serialDevice.InterruptEnable, serialDevice.FifoControl,
            serialDevice.LineStatus, serialDevice.InterruptIdentification, serialDevice.ReceivedCount, serialDevice.TransmitCount,
            serialDevice.Transmitting, serialDevice.InterruptAsserted, serialDevice.InterruptEdges, serialDevice.TransmittedCharacters,
            serialDevice.ReceivedCharacters, serialDevice.Overruns, serialDevice.RejectedTransmitWrites,
            serialDevice.ExternalCharactersQueued, serialDevice.ExternalCharactersIgnored, serialDevice.PhysicalModem,
            virtualEcho = serialEcho, hostSerialPort = false, fullChip = false } : null,
        graphicsMode = machine.Video.GraphicsMode, indexed256 = machine.Video.Indexed256, hardwareResetCount = machine.HardwareResetCount, ataSectorsRead = machine.Ata.ReadSectorCount + machine.SecondaryAta.ReadSectorCount,
        opticalDisc = machine.GetStatus().OpticalDisc, ataSectorsWritten = machine.Ata.WrittenSectorCount + machine.SecondaryAta.WrittenSectorCount, diskWritable = machine.GetStatus().HardDisks?.Any(d => d.Writable) == true,
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
        sb16 = machine.SoundBlaster16 is { } sb16 ? new { model = SoundBlaster16.Model, sb16.IoBasePort, sb16.Irq, sb16.Dma8Channel, sb16.Dma16Channel,
            sb16.TransferredSamples, sb16.RecordedSamples, sb16.ConvertedSamples, sb16.Dma8Grants, sb16.Dma16Grants,
            sb16.CompletedBlocks, sb16.Irq8Requests, sb16.Irq16Requests, sb16.SoftwareIrq8Requests, sb16.SoftwareIrq16Requests,
            sb16.InterruptSources, sb16.OutputRate, sb16.InputRate,
            sb16.Active, sb16.Paused, sb16.RemainingSamples, sb16.SpeakerEnabled, sb16.DacLeft, sb16.DacRight,
            sb16.LeftAnalogGain, sb16.RightAnalogGain, sb16.ResetCount, sb16.RejectedWrites, sb16.RejectedParameters,
            sb16.UnsupportedCommands, sb16.LastUnsupportedCommand, sb16.UnsupportedMixerAccesses, sb16.LegacyOutputControlAccesses, sb16.RejectedResourceWrites,
            adcInput = sb16AdcSignal ?? "disconnected-silence", hostMicrophone = false, opl3 = machine.Opl3 is not null, mpu401 = machine.Midi is not null, csp = false,
            fifoBusTiming = false, fullCardCompatibility = false } : null,
        opl3 = machine.Opl3 is { } synth3State ? new { model = Opl3.Model, masterFrequency = Opl3.MasterFrequency, clocksPerSample = Opl3.ClocksPerSample,
            synth3State.GeneratedSamples, synth3State.RegisterWrites, synth3State.RejectedWrites, synth3State.UnsupportedWrites,
            synth3State.IgnoredBankWrites, synth3State.CompatibilityAliases, synth3State.KeyOnCount, synth3State.NewMode, synth3State.FourOperatorMask,
            synth3State.RhythmEnabled, synth3State.ActiveOperators, synth3State.NoiseState,
            synth3State.Timer1Overflows, synth3State.Timer2Overflows, synth3State.TimerInterruptAsserted,
            synth3State.LeftMixerGain, synth3State.RightMixerGain, csm = false, siliconExact = false,
            physicalDacModel = false, quadOutputs = true, boardConnectedOutputs = "A=left B=right; C/D unconnected",
            nativeCaptureFrames = opl3Native is null ? 0 : synth3State.GeneratedSamples } : null,
        opl = machine.Opl is { } opl ? new { opl.GeneratedSamples, opl.RegisterWrites, opl.RejectedWrites, opl.KeyOnCount, opl.CsmKeyPulses,
            opl.Timer1Overflows, opl.Timer2Overflows, opl.TimerInterruptAsserted, opl.ActiveOperators, opl.UnsupportedProgramming,
            opl.UnsupportedRhythm, opl.UnsupportedCsm, opl.UnsupportedTest, opl.CsmEnabled, opl.CsmPulseActive, opl.RhythmEnabled,
            melodic = true, rhythm = true, csmKeying = true, csmTotalLevelLatch = true, csm = true, siliconExact = false,
            ioTiming = machine.FmIoTiming is { } timing ? new { frequency = Isa8BitIoTiming.Frequency, clocksPerByte = Isa8BitIoTiming.ClocksPerByte,
                timing.Accesses, timing.ByteCycles, timing.ElapsedNanoseconds, scope = "FM ports only; normal 8-bit path" } : null } : null,
        bootMarker = machine.Memory.ReadWord(0x500), postCode = machine.Board.PostCode,
        inputBytesSent = sent, inputBytesTotal = typing.Length,
        ps2Mouse = machine.Mouse is null ? null : new { machine.Mouse.CommandCount, machine.Mouse.PacketCount, machine.Mouse.Enabled, machine.Mouse.SampleRate, machine.Mouse.Resolution },
        scenario = scenario is null ? null : new { scenario.CompletedSteps, scenario.TotalSteps, scenario.InputBytesSent, scenario.InputBytesTotal, scenario.MediaChanges, scenario.MouseInputsSent, scenario.GameportInputsSent, scenario.SerialInputsSent, scenario.ParallelInputsSent, scenario.MidiInputsSent, scenario.Complete },
        limitation = cpuLimitation + "FreeDOS 1.4 minimal and local German MS-DOS 6.22 setup/HDD profile tested. Guest-loaded CP850 text and graphics verified at 8/14/16 pixels per character. Built-in CP437 fonts are original DOSE platform artwork, not IBM ROM replicas. PE16/PE32, descriptor caches and original HIMEM.SYS/XMS on the local German MS-DOS 6.22 profile are tested. LLDT/LTR, TSS interrupt stack changes and outer IRET/RETF are implemented. 4-KiB paging/TLB, V86 monitor/bitmap paths and original local EMM386 NOEMS with basic VCPI memory calls are tested. 16/32-bit call gates with TSS stacks, parameters and outer RETF are tested. Controlled 16/32-bit hardware tasks, task gates and task-IRET are guest-tested. LOCK/CMPXCHG/XADD, alignment-check retries and the 486 word/dword pseudo-descriptor path are tested on controlled guests. Debug-register/TF reference guests, raw 80-bit x87 save/restore, elementary numeric operations and selected external FERR/IGNNE/IRQ13 and native MF return paths are implemented. General x87, task/fault/debug/bus precision, DOS-extender/EMS coverage and Windows remain pending." };
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
