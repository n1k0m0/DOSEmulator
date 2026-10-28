// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Complete owned hardware data at an instruction boundary; host observers are deliberately outside the PC.</summary>
public sealed record HardwareState(Dictionary<string, StateBlock> Components, SchedulerState Scheduler,
    string MemoryMap, string PortMap, bool ParallelSink, bool SerialEcho, string? AdcSource, string? Adc16Source);

public sealed partial class PcMachine
{
    private bool _executing, _serialEcho;
    private readonly int _factoryPortCount, _factoryMemoryCount;
    /// <summary>Host startup policy only; a restored pause state must not be unconditionally resumed by the GUI constructor.</summary>
    public bool IsRestoredSnapshot { get; private set; }
    private string? _adcSource, _adc16Source;
    private Func<ulong, byte>? _snapshotAdc;
    private Func<ulong, bool, short>? _snapshotAdc16;

    /// <summary>Connects only the explicit virtual framed COM peer; no host serial endpoint is opened.</summary>
    public void ConnectSerialEcho()
    {
        var serial = Serial ?? throw new InvalidOperationException("Virtual echo requires COM1.");
        if (_serialEcho) throw new InvalidOperationException("A virtual serial echo is already connected.");
        _serialEcho = true; serial.SetModemInputs(true, true, false, true);
        serial.Transmitted += EchoSerial;
    }

    /// <summary>Feeds completed virtual frames back through the timed external receive line.</summary>
    private void EchoSerial(UartFrame frame) => Serial!.QueueReceive([frame.Data], frame.Errors);

    /// <summary>Selects a reproducible guest-time ADC diagnostic source that can be reconstructed after load.</summary>
    public void SetVirtualAdc(string source, bool sb16 = false)
    {
        if (sb16)
        {
            var dsp = SoundBlaster16 ?? throw new InvalidOperationException("SB16 ADC source requires SB16.");
            if (source is not ("silence" or "ramp")) throw new ArgumentException("Unknown virtual SB16 ADC source.");
            _adc16Source = source;
            _snapshotAdc16 = source == "ramp" ? (time, right) => unchecked((short)(time / 125000 * 17 + (right ? 1000UL : 0))) : null;
            dsp.AdcInput = _snapshotAdc16;
        }
        else
        {
            var dsp = SoundBlaster ?? throw new InvalidOperationException("SB2 ADC source requires SB2.");
            if (source is not ("midpoint" or "ramp")) throw new ArgumentException("Unknown virtual SB2 ADC source.");
            _adcSource = source;
            _snapshotAdc = source == "ramp" ? now => (byte)((now / 125000) & 255) : null;
            dsp.AdcInput = _snapshotAdc;
        }
    }

    /// <summary>Rejects reentrant capture during CPU execution, device callbacks, or after media disposal.</summary>
    internal void RequireSnapshotBoundary()
    {
        if (_disposed || _executing || Scheduler.IsDispatching) throw new InvalidOperationException("Snapshots require a stopped owner instruction boundary.");
        if (Profile.IsP54c
            ? Cpu.Model != X86CpuModel.IntelP54cB5Subset || !Cpu.UsesTimestampClock(Scheduler, (uint)Profile.CpuHz)
            : Cpu.Model != X86CpuModel.Intel486Dx || Cpu.HasTimeStampCounter || Cpu.TimestampFrequency != 0)
            throw new InvalidOperationException("The CPU must retain its selected model and owning PC clock.");
        if (Profile.IsFxHost
            ? HostBridge is null || !HostBridge.HasMemoryBinding(Memory) || Pci is null ||
                !(Profile.IsPiixIrq
                    ? PiixIrq is not null && PiixIrq.SupportsMirq == Profile.IsPiixIde && PiixIrq.HasPicBinding(Pic) &&
                      Pci.FunctionCount == (Profile.IsTrio64 ? 4 : Profile.IsPiixIde ? 3 : 2) && Pci.HasFunction(0, 0, 0, HostBridge) && Pci.HasFunction(0, 7, 0, PiixIrq)
                    : PiixIrq is null && !Pic.SupportsLevelInputs && Pci.HasOnlyFunction(0, 0, 0, HostBridge))
            : HostBridge is not null || Pci is not null || PiixIrq is not null || Pic.SupportsLevelInputs ||
                Memory.DecodedRamBytes != Memory.Ram.Length || Memory.RamHoleLength != 0 || Memory.RamHoleStart != 0)
            throw new InvalidOperationException("The board must retain its fixed PCI peers and configured DRAM decoding.");
        if (Ata.DmaAvailable != Profile.IsPiixIde || SecondaryAta.DmaAvailable != Profile.IsPiixIde ||
            (Profile.IsPiixIde
                ? PiixIde is null || HostBridge is null || PiixIrq is null || Pci is null || !Pci.HasFunction(0, 7, 1, PiixIde) ||
                  !Ports.HasOnlyDynamicDecoder(PiixIde) || !PiixIde.HasBindings(Scheduler, HostBridge, Ata, SecondaryAta, Ports) ||
                  ((PiixIrq.IsaLines & 0x4000) != 0) != Ata.InterruptAsserted || ((PiixIrq.MirqAssertions & 1) != 0) != SecondaryAta.InterruptAsserted
                : PiixIde is not null || Ports.DynamicDecoderCount != 0))
            throw new InvalidOperationException("The selected board must retain its fixed IDE, MIRQ, DMA and dynamic I/O peers.");
        if (Profile.IsAtapi
            ? Atapi is null || !Atapi.HasBindings(Scheduler, OpticalDisc) ||
              !Ata.HasPacketBinding(Profile.AtapiSlot < 2 ? Atapi : null, Profile.AtapiSlot < 2 ? Profile.AtapiSlot & 1 : -1) ||
              !SecondaryAta.HasPacketBinding(Profile.AtapiSlot >= 2 ? Atapi : null, Profile.AtapiSlot >= 2 ? Profile.AtapiSlot & 1 : -1)
            : Atapi is not null || OpticalDisc is not null || !Ata.HasPacketBinding(null, -1) || !SecondaryAta.HasPacketBinding(null, -1))
            throw new InvalidOperationException("The installed packet target must retain its fixed controller, clock and optical lease.");
        if (Profile.IsPiixReset
            ? PiixReset is null || Pci is null || !PiixReset.HasBindings(Scheduler, Pci, this)
            : PiixReset is not null)
            throw new InvalidOperationException("The board must retain its fixed CF9 reset owner and clock.");
        if (Video.IsEt4000 != Profile.IsEt4000 || Video.IsTrio64 != Profile.IsTrio64 ||
            (Profile.IsTrio64
                ? S3 is null || Pci is null || !Pci.HasFunction(0, 3, 0, S3) ||
                  !S3.HasBindings(Memory, Video) || !Memory.HasOnlyDynamicDecoder(S3)
                : S3 is not null || Memory.DynamicDecoderCount != 0))
            throw new InvalidOperationException("The board must retain its fixed video, PCI and physical memory decoder peers.");
        if (Memory.StateMappingCount != _factoryMemoryCount || Ports.StatePortCount != _factoryPortCount)
            throw new InvalidOperationException("Custom bus mappings are outside the product snapshot topology.");
        int inputs = 1 + (SoundBlaster is null ? 0 : 1) + (SoundBlaster16 is null ? 0 : 2) + (Opl is null ? 0 : 1) + (Opl3 is null ? 0 : 2);
        if (Audio.StateChildren.Count() != inputs || Mouse is not null && !Mouse.HasStateOutputOwner(Keyboard) ||
            SoundBlaster16 is not null && !SoundBlaster16.HasStatePeers(Opl3, Midi))
            throw new InvalidOperationException("External audio or peripheral wiring cannot be silently omitted from a snapshot.");
        if (SoundBlaster?.AdcInput != _snapshotAdc || SoundBlaster16?.AdcInput != _snapshotAdc16)
            throw new InvalidOperationException("An opaque ADC callback cannot be persisted; select a virtual ADC source.");
    }

    /// <summary>Enumerates the fixed product graph once, preserving shared references and owned nested identities.</summary>
    private Dictionary<string, IStateObject> StateGraph()
    {
        var graph = new Dictionary<string, IStateObject>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        // Register only uniquely owned graph nodes; shared factory links are validated separately.
        void Add(string name, IStateObject? item)
        {
            if (item is null) return;
            if (!seen.Add(item)) throw new InvalidOperationException("Duplicate state ownership: " + name);
            graph.Add(name, item);
            foreach (var child in item.StateChildren) Add(name + "/" + child.Name, child.Value);
        }
        Add("machine", this); Add("memory", Memory); Add("cpu", Cpu); Add("board", Board);
        Add("pic", Pic); Add("pit", Pit); Add("audio", Audio); Add("speaker", Speaker); Add("rtc", Rtc);
        Add("dma", Dma); Add("ata0", Ata); Add("ata1", SecondaryAta); Add("floppy", Floppy);
        Add("video", Video); Add("keyboard", Keyboard); Add("fpu-line", FpuError);
        Add("mouse", Mouse); Add("gameport", Gameport); Add("serial", Serial); Add("parallel", Parallel);
        Add("midi", Midi); Add("opl2", Opl); Add("opl3", Opl3); Add("fm-timing", FmIoTiming);
        Add("sb2", SoundBlaster); Add("sb16", SoundBlaster16);
        Add("pci", Pci); Add("hostbridge", HostBridge); Add("piix-irq", PiixIrq); Add("piix-ide", PiixIde); Add("piix-reset", PiixReset); Add("s3", S3);
        return graph;
    }

    /// <summary>Copies all component fields and event descriptors without advancing guest time or flushing PCM.</summary>
    public HardwareState CaptureHardwareState()
    {
        RequireSnapshotBoundary();
        var graph = StateGraph();
        var owners = graph.ToDictionary(v => (object)v.Value, v => v.Key, ReferenceEqualityComparer.Instance);
        // Resolve only registered stateful bus devices, rejecting custom callbacks or mappings.
        string Name(object item) => owners.TryGetValue(item, out var name) ? name : throw new InvalidOperationException("Unregistered external bus mapping.");
        var state = new HardwareState(graph.ToDictionary(v => v.Key, v => v.Value.SaveState(), StringComparer.Ordinal), Scheduler.SaveState(owners),
            Memory.StateMap(Name), Ports.StateMap(Name), Parallel?.Printer is not null, _serialEcho, _adcSource, _adc16Source);
        SnapshotValidation.CheckProfile(state, Profile, Memory.Ram.Length);
        return state;
    }

    /// <summary>Rehydrates only a freshly constructed PC; callers adopt it after the complete import succeeds.</summary>
    internal void RestoreHardwareState(HardwareState state, byte[] ram)
    {
        RequireSnapshotBoundary();
        if (state.ParallelSink) (Parallel ?? throw new InvalidDataException("Missing snapshot LPT1.")).ConnectPrinter();
        if (state.SerialEcho) ConnectSerialEcho();
        if (state.AdcSource is { } adc) SetVirtualAdc(adc);
        if (state.Adc16Source is { } adc16) SetVirtualAdc(adc16, true);
        var graph = StateGraph();
        if (state.Components is null || state.Components.Count != graph.Count || ram.Length != Memory.Ram.Length ||
            graph.Keys.Any(k => !state.Components.ContainsKey(k))) throw new InvalidDataException("Snapshot hardware topology or RAM capacity differs.");
        var owners = graph.ToDictionary(v => (object)v.Value, v => v.Key, ReferenceEqualityComparer.Instance);
        // Resolve only registered stateful bus devices, rejecting custom callbacks or mappings.
        string Name(object item) => owners.TryGetValue(item, out var name) ? name : throw new InvalidDataException("Unknown bus mapping.");
        if (Memory.StateMap(Name) != state.MemoryMap || Ports.StateMap(Name) != state.PortMap)
            throw new InvalidDataException("Snapshot firmware or bus mappings differ.");
        SnapshotValidation.Check(state);
        SnapshotValidation.CheckProfile(state, Profile, ram.Length);
        foreach (var (name, component) in graph) component.LoadState(state.Components[name]);
        ram.CopyTo(Memory.Ram, 0);
        Scheduler.LoadState(state.Scheduler, graph);
        // Re-encoding also detects lossy converters, normalized fields and ignored nested values before adoption.
        var roundTrip = CaptureHardwareState();
        if (JsonSerializer.Serialize(roundTrip, StateValues.Json) != JsonSerializer.Serialize(state, StateValues.Json))
            throw new InvalidDataException("Snapshot state did not round-trip exactly.");
        IsRestoredSnapshot = true;
    }
}
