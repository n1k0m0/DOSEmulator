// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Checks bounds used by indexes, buffers and virtual clocks before untrusted data reaches a prepared PC.</summary>
internal static partial class SnapshotValidation
{
    /// <summary>Validates graph-wide resource limits and component-specific mutable index contracts.</summary>
    internal static void Check(HardwareState state)
    {
        if (state.Components is null || state.Scheduler is null || state.Components.Count > 2048)
            throw new InvalidDataException("Incomplete snapshot graph.");
        DeviceScheduler.ValidateState(state.Scheduler);
        foreach (var block in state.Components.Values)
        {
            if (block is null || block.Fields is null || block.Fields.Count > 256) throw new InvalidDataException("Invalid state block.");
            foreach (var field in block.Fields.Values) CheckSize(field);
        }
        // Enforce each index bound before any restored code can use the value as an array offset.
        void Range(string node, string field, long first, long last)
        {
            if (state.Components.TryGetValue(node, out var b) && (!b.Fields.TryGetValue(field, out var v) || v.GetInt64() < first || v.GetInt64() > last))
                throw new InvalidDataException("Snapshot index out of range: " + node + "/" + field);
        }
        Range("cpu", "_cpl", 0, 3); Range("cpu", "_instructionBytes", 0, 15);
        Range("cpu", "_interruptShadow", 0, 2); Range("cpu", "_debugShadow", 0, 2);
        Range("cpu", "_repLength", 0, 15); Range("cpu", "_repSize", 0, 4);
        if (state.Components.TryGetValue("rtc", out var rtc))
        {
            var reader = new StateReader(rtc, "DOSEmulator.Hardware.RtcCmos");
            DateTime epoch = reader.Value<DateTime>("_epoch");
            ulong origin = reader.Value<ulong>("_calendarOriginNanoseconds");
            if (epoch.Kind == DateTimeKind.Local || origin > state.Scheduler.Now)
                throw new InvalidDataException("Invalid RTC calendar origin.");
            UInt128 elapsed = (UInt128)reader.Value<ulong>("_batteryNanoseconds") + state.Scheduler.Now - origin;
            if (elapsed / 100 > (ulong)(DateTime.MaxValue.Ticks - epoch.Ticks))
                throw new InvalidDataException("RTC calendar exceeds the supported civil date range.");
        }
        if (state.Components.TryGetValue("cpu", out var cpuState)) CheckRepContinuation(cpuState);
        Range("audio", "_count", 0, 958);
        Range("sb16", "_xbus05Control", 0, 255);
        if (state.Components.TryGetValue("audio", out var audio))
        {
            if (audio.Fields["_samples"].GetArrayLength() != 960 || audio.Fields["_count"].GetInt32() % 2 != 0 ||
                audio.Fields["_position"].GetUInt64() > state.Scheduler.Now ||
                audio.Fields["_sampleStart"].GetUInt64() > audio.Fields["_position"].GetUInt64() ||
                audio.Fields["_nextSample"].GetUInt64() <= audio.Fields["_position"].GetUInt64())
                throw new InvalidDataException("Invalid partial PCM integration state.");
        }
        Range("serial", "_rxHead", 0, 15); Range("serial", "_rxCount", 0, 16);
        Range("floppy", "_received", 0, 16); Range("floppy", "_expected", 0, 16); Range("floppy", "_drive", 0, 3);
        foreach (string node in state.Components.Keys.Where(n => n.StartsWith("ata", StringComparison.Ordinal) && n.Contains("_drives/", StringComparison.Ordinal)))
        {
            Range(node, "_offset", 0, 512); Range(node, "_remaining", 0, 256);
            var ata = new StateReader(state.Components[node], "DOSEmulator.Hardware.AtaPioDrive");
            int offset = ata.Value<int>("_offset"); byte status = ata.Value<byte>("Status");
            if ((offset & 1) != 0 || (status & 8) != 0 && offset > 510)
                throw new InvalidDataException("ATA data phase has no complete aligned word: " + node);
        }
        if (state.Components.TryGetValue("gameport", out var gameport))
        {
            (StateValues.Read<GameportInput>(gameport.Fields["Input"]) ?? throw new InvalidDataException("Null gameport input.")).Validate();
            (StateValues.Read<GameportInput>(gameport.Fields["_sampled"]) ?? throw new InvalidDataException("Null sampled gameport input.")).Validate();
        }
    }

    /// <summary>Checks only an active decoded REP cache; stale inactive bytes remain harmless and retain their exact saved representation.</summary>
    private static void CheckRepContinuation(StateBlock state)
    {
        var reader = new StateReader(state, "DOSEmulator.Cpu.X86.X86Cpu");
        if (!reader.Value<bool>("_repValid")) return;
        int length = reader.Value<int>("_repLength"), size = reader.Value<int>("_repSize");
        byte opcode = reader.Value<byte>("_repOpcode"); byte[] code = reader.Value<byte[]>("_repCode");
        if (length is < 2 or > 15 || size is not (1 or 2 or 4) || opcode is not (0xa4 or 0xa5 or 0xaa or 0xab) ||
            code is null || code.Length != 15 || code[length - 1] != opcode)
            throw new InvalidDataException("Invalid active REP opcode, size or instruction extent.");
        bool code32 = reader.Value<bool>("_repCode32"), operand32 = code32, address32 = code32, repeat = false;
        Segment source = Segment.Ds;
        for (int i = 0; i < length - 1; i++)
        {
            switch (code[i])
            {
                // Repeated size prefixes select the complement of the code default, rather than toggling it repeatedly.
                case 0x66: operand32 = !code32; break;
                case 0x67: address32 = !code32; break;
                case 0xf2: case 0xf3: repeat = true; break;
                case 0x26: source = Segment.Es; break;
                case 0x2e: source = Segment.Cs; break;
                case 0x36: source = Segment.Ss; break;
                case 0x3e: source = Segment.Ds; break;
                case 0x64: source = Segment.Fs; break;
                case 0x65: source = Segment.Gs; break;
                default: throw new InvalidDataException("Invalid active REP prefix byte.");
            }
        }
        int expectedSize = (opcode & 1) == 0 ? 1 : operand32 ? 4 : 2;
        if (!repeat || size != expectedSize || address32 != reader.Value<bool>("_repAddress32") ||
            source != reader.Value<Segment>("_repSource") ||
            reader.Value<uint>("_repEnd") != unchecked(reader.Value<uint>("_repStart") + (uint)length))
            throw new InvalidDataException("Active REP cache differs from its saved instruction encoding.");
        // Do not require the cached CS/EIP/task to be current: an intervening IRQ may safely make a cache stale.
        // TryRunRepBatch already falls back when the live context or live instruction bytes no longer match.
    }
    /// <summary>Checks the constructor-fixed CPU and clock against the sealed PC definition before a branch is published.</summary>
    internal static void CheckProfile(HardwareState state, MachineProfile profile, int ramBytes)
    {
        profile.Validate();
        if (ramBytes != profile.RamMiB * 1048576 || !state.Components.TryGetValue("cpu", out var cpu) ||
            state.Scheduler.CachedFrequency != 0 && state.Scheduler.CachedFrequency != (uint)profile.CpuHz)
            throw new InvalidDataException("Snapshot RAM or clock differs from its PC profile.");
        var reader = new StateReader(cpu, "DOSEmulator.Cpu.X86.X86Cpu");
        reader.Invariant("Model", profile.IsP54c ? X86CpuModel.IntelP54cB5Subset : X86CpuModel.Intel486Dx);
        reader.Invariant("TimestampFrequency", profile.IsP54c ? (uint)profile.CpuHz : 0u);
        if (!state.Components.TryGetValue("memory", out var memory)) throw new InvalidDataException("Snapshot memory decoder is absent.");
        var memoryReader = new StateReader(memory, "DOSEmulator.Core.MemoryBus");
        uint top = memoryReader.Value<uint>("DecodedRamBytes");
        uint hole = memoryReader.Value<uint>("_ramHoleStart");
        uint length = memoryReader.Value<uint>("_ramHoleLength");
        if (profile.IsFxHost)
        {
            if (!state.Components.TryGetValue("hostbridge", out var host) || !state.Components.TryGetValue("pci", out var pci))
                throw new InvalidDataException("The 430FX-host board requires both fixed PCI nodes.");
            var hostReader = new StateReader(host, "DOSEmulator.Hardware.Intel82437Fx");
            var pciReader = new StateReader(pci, "DOSEmulator.Hardware.PciConfigurationBus");
            byte[] config = hostReader.Value<byte[]>("_config") ?? throw new InvalidDataException("Null host configuration.");
            Intel82437Fx.ValidateConfigurationBytes(config);
            uint expectedTop = Math.Min(Math.Min((uint)config[0x64] * 0x400000u, 0x08000000u), (uint)ramBytes);
            (uint expectedHole, uint expectedLength) = (config[0x57] >> 6) switch
            {
                1 => (0x80000u, 0x20000u), 2 => (0xf00000u, 0x100000u), _ => (0u, 0u)
            };
            if (top != expectedTop || hole != expectedHole || length != expectedLength ||
                (pciReader.Value<uint>("_address") & ~0x80fffffcu) != 0)
                throw new InvalidDataException("Host configuration differs from the saved memory decoder.");
        }
        else if (state.Components.ContainsKey("pci") || state.Components.ContainsKey("hostbridge") || top != ramBytes || hole != 0 || length != 0)
            throw new InvalidDataException("The selected AT board has no programmable DRAM or PCI host controller.");
        CheckPicRouting(state, profile);
        CheckAtapi(state, profile);
        CheckIdeDma(state, profile);
        CheckPiixReset(state, profile);
        CheckTrio64(state, profile);
        // Remaining fields still pass the complete typed loader and exact round-trip check.
    }

    /// <summary>Checks constructor capability, level semantics, cascade priority and the bridge's saved physical pin selection before media opening.</summary>
    private static void CheckPicRouting(HardwareState state, MachineProfile profile)
    {
        // Resolves one required typed node without preparing media or mutating a peer.
        StateReader Node(string name, string type) => state.Components.TryGetValue(name, out var block)
            ? new StateReader(block, type) : throw new InvalidDataException("Missing PIC routing node: " + name);
        var pic = Node("pic", "DOSEmulator.Hardware.Pic8259");
        var master = Node("pic/_master", "DOSEmulator.Hardware.Pic8259.Chip");
        var slave = Node("pic/_slave", "DOSEmulator.Hardware.Pic8259.Chip");
        pic.Invariant("_supportsLevelInputs", profile.IsPiixIrq);
        // Reads each field exactly once, retaining ordinary edge IRR history independently from current pins.
        static (byte Pins, byte Irr, byte Isr, byte Mask, byte Level, int Init, byte Vector) Chip(StateReader reader) =>
            (reader.Value<byte>("Lines"), reader.Value<byte>("Irr"), reader.Value<byte>("Isr"), reader.Value<byte>("Mask"),
             reader.Value<byte>("LevelMask"), reader.Value<int>("InitStep"), reader.Value<byte>("Vector"));
        var m = Chip(master); var s = Chip(slave);
        // Reconstructs fixed PIC priority solely from saved IRR, ISR, masks and initialization.
        int Pending((byte Pins, byte Irr, byte Isr, byte Mask, byte Level, int Init, byte Vector) chip)
        {
            if (chip.Init is < 0 or > 3 || (chip.Vector & 7) != 0)
                throw new InvalidDataException("Invalid PIC initialization phase or vector alignment.");
            if (profile.IsPiixIrq && chip.Init != 0) return -1;
            int priority = chip.Isr == 0 ? 8 : System.Numerics.BitOperations.TrailingZeroCount((uint)chip.Isr);
            uint ready = (uint)(chip.Irr & ~chip.Mask & ((1 << priority) - 1));
            return ready == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(ready);
        }
        if (pic.Value<int>("_pendingMaster") != Pending(m))
            throw new InvalidDataException("PIC pending-priority cache differs from its request state.");
        if (!profile.IsPiixIrq)
        {
            if (state.Components.ContainsKey("piix-irq") || m.Level != 0 || s.Level != 0)
                throw new InvalidDataException("The selected earlier board has no PIIX IRQ routing or level selection.");
            return;
        }
        var bridge = Node("piix-irq", "DOSEmulator.Hardware.Intel82371FbIrq");
        byte[] config = bridge.Value<byte[]>("_config") ?? throw new InvalidDataException("Null PIIX IRQ configuration.");
        Intel82371FbIrq.ValidateConfigurationBytes(config);
        bridge.Invariant("_supportsMirq", profile.IsPiixIde);
        ushort elcr = bridge.Value<ushort>("_elcr"), isa = bridge.Value<ushort>("_isaLines");
        byte assertions = bridge.Value<byte>("_pirqAssertions"), mirqs = bridge.Value<byte>("_mirqAssertions");
        if ((mirqs & ~3) != 0 || !profile.IsPiixIde && mirqs != 0) throw new InvalidDataException("Invalid board MIRQ capability or sources.");
        if ((elcr & ~0xdef8) != 0 || (isa & 4) != 0 || (assertions & ~15) != 0 || (m.Level | s.Level << 8) != elcr)
            throw new InvalidDataException("PIIX ELCR/pins differ from the selected PIC capability.");
        ushort lines = (ushort)(m.Pins | s.Pins << 8);
        ushort expected = Intel82371FbIrq.ComputePicInputs(config, isa, assertions, profile.IsPiixIde, mirqs);
        if ((lines & ~4) != expected || ((lines & 4) != 0) != (Pending(s) >= 0))
            throw new InvalidDataException("PIIX sources, physical PIC pins or slave cascade differ.");
        foreach (var chip in new[] { m, s })
            if ((chip.Irr & chip.Level & ~chip.Pins) != 0 || (chip.Pins & chip.Level & ~chip.Isr & ~chip.Irr) != 0)
                throw new InvalidDataException("Saved level request contradicts its physical input or service state.");
    }

    /// <summary>Checks fixed ATA DMA capability, real device phases and all live PCI/ATA event relationships before media branching.</summary>
    private static void CheckIdeDma(HardwareState state, MachineProfile profile)
    {
        var cancelled = state.Scheduler.Cancelled.ToHashSet();
        // Resolve one required node without constructing or opening any media peer.
        StateReader Node(string name, string type) => state.Components.TryGetValue(name, out var block)
            ? new StateReader(block, type) : throw new InvalidDataException("Missing IDE state node: " + name);
        var expectedNewEvents = new HashSet<long>();
        // Require one exact future live callback; canceled historical queue entries deliberately remain valid.
        void Event(long token, string owner, string operation)
        {
            var rows = state.Scheduler.Events.Where(e => e.Id == token).ToArray();
            if (token <= 0 || cancelled.Contains(token) || rows.Length != 1 || rows[0].Owner != owner ||
                rows[0].Operation != operation || rows[0].Time <= state.Scheduler.Now || rows[0].Payload.Length != 0)
                throw new InvalidDataException("IDE live event differs from its device phase.");
            expectedNewEvents.Add(token);
        }
        var direction = new AtaDmaDirection[2]; var ready = new bool[2]; var irq = new bool[2];
        for (int channel = 0; channel < 2; channel++)
        {
            string name = "ata" + channel;
            var ata = Node(name, "DOSEmulator.Hardware.AtaPioController");
            ata.Invariant("_dmaAvailable", profile.IsPiixIde); int selected = ata.Value<int>("_dmaDriveIndex");
            if (selected is < -1 or > 1 || !profile.IsPiixIde && selected != -1) throw new InvalidDataException("Invalid DMA drive latch.");
            irq[channel] = ata.Value<bool>("_interrupt");
            bool deviceIrq = AtapiInterrupt(state, profile, channel); int activeDrive = -1;
            for (int drive = 0; drive < 2; drive++)
            {
                string owner = name + "/_drives/" + drive; var d = Node(owner, "DOSEmulator.Hardware.AtaPioDrive");
                d.Invariant("_dmaAvailable", profile.IsPiixIde);
                var mode = d.Value<AtaDmaDirection>("_dmaDirection"); bool dmaSelected = d.Value<bool>("_dmaSelected");
                if ((uint)mode > 2 || !profile.IsPiixIde && (mode != AtaDmaDirection.None || dmaSelected))
                    throw new InvalidDataException("Invalid ATA DMA mode/capability.");
                bool pending = d.Value<bool>("_pendingInterrupt"); byte control = d.Value<byte>("_control");
                deviceIrq |= pending && (control & 2) == 0;
                if (mode == AtaDmaDirection.None) continue;
                if (activeDrive >= 0) throw new InvalidDataException("Two drives cannot own one DMA handshake."); activeDrive = drive;
                int position = d.Value<int>("_offset"), remaining = d.Value<int>("_remaining"); byte status = d.Value<byte>("Status");
                bool reset = d.Value<bool>("_resetActive"), writing = d.Value<bool>("_writing"), identify = d.Value<bool>("_identify");
                long token = d.Value<long>("_event"), lba = d.Value<long>("_lba");
                if (reset || identify || writing != (mode == AtaDmaDirection.MemoryToDevice) || remaining is < 1 or > 256 ||
                    lba is < 0 or > 0x10000000 || position is < 0 or > 512 || (position & 1) != 0 || (status & 0x88) is not (8 or 0x80))
                    throw new InvalidDataException("Invalid active ATA DMA phase.");
                bool isReady = (status & 0x88) == 8;
                if (isReady ? position > 510 || token != 0 : token <= 0) throw new InvalidDataException("ATA DMA buffer/event extent differs.");
                if (!isReady)
                {
                    string operation = mode == AtaDmaDirection.DeviceToMemory ? "dma-read-ready" : position == 512 ? "dma-write-commit" : "dma-write-ready";
                    if (mode == AtaDmaDirection.DeviceToMemory && position != 0 || mode == AtaDmaDirection.MemoryToDevice && position is not (0 or 512))
                        throw new InvalidDataException("Invalid busy DMA sector extent.");
                    Event(token, owner, operation);
                }
                direction[channel] = mode; ready[channel] = isReady;
            }
            if (selected != activeDrive || irq[channel] != deviceIrq) throw new InvalidDataException("ATA DMA latch or combined IRQ differs from its drives.");
        }
        // Every live new callback must also be referenced by exactly the authoritative device phase.
        void RejectUnreferencedNewEvents()
        {
            foreach (var row in state.Scheduler.Events)
                if (!cancelled.Contains(row.Id) &&
                    (row.Owner == "piix-ide" && row.Operation is "primary-burst" or "secondary-burst" ||
                     row.Owner.StartsWith("ata", StringComparison.Ordinal) && row.Operation is "dma-read-ready" or "dma-write-ready" or "dma-write-commit") &&
                    !expectedNewEvents.Contains(row.Id))
                    throw new InvalidDataException("Unreferenced live IDE/DMA callback.");
        }
        if (!profile.IsPiixIde)
        {
            RejectUnreferencedNewEvents();
            if (state.Components.Keys.Any(k => k == "piix-ide" || k.StartsWith("piix-ide/", StringComparison.Ordinal)))
                throw new InvalidDataException("The selected earlier profile has no IDE PCI function.");
            return;
        }
        var ide = Node("piix-ide", "DOSEmulator.Hardware.Intel82371FbIde");
        byte[] config = ide.Value<byte[]>("_config") ?? throw new InvalidDataException("Null PIIX IDE configuration.");
        Intel82371FbIde.ValidateConfigurationBytes(config); long[] timers = ide.Value<long[]>("_events");
        if (timers is null || timers.Length != 2) throw new InvalidDataException("Invalid BM event array.");
        var bridge = Node("piix-irq", "DOSEmulator.Hardware.Intel82371FbIrq");
        ushort isa = bridge.Value<ushort>("_isaLines"); byte mirq = bridge.Value<byte>("_mirqAssertions");
        if (((isa & 0x4000) != 0) != irq[0] || ((mirq & 1) != 0) != irq[1]) throw new InvalidDataException("PIIX IDE IRQ14/MIRQ0 sources differ.");
        for (int channel = 0; channel < 2; channel++)
        {
            var c = Node("piix-ide/_channels/" + channel, "DOSEmulator.Hardware.Intel82371FbIde.Channel");
            byte command = c.Value<byte>("Command"), status = c.Value<byte>("Status");
            uint table = c.Value<uint>("Prdt"), descriptor = c.Value<uint>("DescriptorAddress"), region = c.Value<uint>("RegionAddress");
            int length = c.Value<int>("RegionLength"), position = c.Value<int>("RegionOffset"), index = c.Value<int>("DescriptorIndex");
            bool loaded = c.Value<bool>("Loaded"), eot = c.Value<bool>("Eot"), high = c.Value<bool>("IrqHigh");
            long transferred = c.Value<long>("BytesTransferred"); bool active = (status & 1) != 0;
            if ((command & ~9) != 0 || (status & ~0x67) != 0 || (table & 3) != 0 || (region & 1) != 0 ||
                index is < 0 or > 512 || length is < 0 or > 65536 || position < 0 || position > length || ((length | position) & 1) != 0 ||
                transferred is < 0 or > 33554432 || (transferred & 1) != 0 || high != irq[channel] ||
                active && (command & 1) == 0 || loaded && (length == 0 || index >= 512 || (region & 65535) + length > 65536) ||
                active && loaded && ((descriptor & 4095) > 4088 || (descriptor & 0xfffff000) != (table & 0xfffff000)))
                throw new InvalidDataException("Invalid BM command/status/cursor/source bounds.");
            if (active && descriptor != unchecked(table + (uint)index * 8)) throw new InvalidDataException("Active BM descriptor differs from its immutable table cursor.");
            bool matches = direction[channel] == ((command & 8) != 0 ? AtaDmaDirection.DeviceToMemory : AtaDmaDirection.MemoryToDevice);
            if (active && loaded && position == length || active && (config[4] & 4) != 0 && !loaded &&
                (index == 0 && transferred == 0 || ready[channel] && matches))
                throw new InvalidDataException("Active BM region should already have advanced or scheduled its next grant.");
            bool needsTimer = active && (config[4] & 4) != 0 && loaded && position < length && ready[channel] && matches;
            if ((timers[channel] != 0) != needsTimer) throw new InvalidDataException("BM grant timer differs from actual permission/device readiness.");
            if (needsTimer) Event(timers[channel], "piix-ide", channel == 0 ? "primary-burst" : "secondary-burst");
            // IRQ=1/Active=1, EOT before ATA end, stopped partial sectors and lazy next descriptors are valid states.
        }
        RejectUnreferencedNewEvents();
    }

    /// <summary>Checks IDE phases against sealed slot/capacity/writability metadata before any media file is opened.</summary>
    internal static void CheckIdeMedia(HardwareState state, MachineDefinition definition, MediaGeneration[] media)
    {
        CheckAtapiMedia(state, definition, media);
        var cancelled = state.Scheduler.Cancelled.ToHashSet();
        var live = state.Scheduler.Events.Where(row => !cancelled.Contains(row.Id)).ToArray();
        for (int channel = 0; channel < 2; channel++) for (int drive = 0; drive < 2; drive++)
        {
            int slot = channel * 2 + drive; string owner = "ata" + channel + "/_drives/" + drive;
            var reader = new StateReader(state.Components[owner], "DOSEmulator.Hardware.AtaPioDrive");
            var mode = reader.Value<AtaDmaDirection>("_dmaDirection"); bool writing = reader.Value<bool>("_writing");
            bool identify = reader.Value<bool>("_identify"), reset = reader.Value<bool>("_resetActive");
            byte status = reader.Value<byte>("Status"); int offset = reader.Value<int>("_offset"), remaining = reader.Value<int>("_remaining");
            long lba = reader.Value<long>("_lba"), token = reader.Value<long>("_event");
            var callbacks = live.Where(row => row.Owner == owner && row.Operation is "event0" or "event1" or "event2" or "event3").ToArray();
            bool pioData = mode == AtaDmaDirection.None && (status & 8) != 0;
            if (identify && writing || pioData && (reset || (status & 0x80) != 0 || token != 0 || callbacks.Length != 0))
                throw new InvalidDataException("PIO data phase differs from its command or pending callback.");
            if (pioData && !identify && remaining < 1)
                throw new InvalidDataException("PIO sector data requires a remaining current sector.");
            if (callbacks.Length != 0)
            {
                if (mode != AtaDmaDirection.None || reset || callbacks.Length != 1 || callbacks[0].Id != token ||
                    callbacks[0].Time <= state.Scheduler.Now || callbacks[0].Time - state.Scheduler.Now > 5_000 ||
                    callbacks[0].Payload.Length != 0 || (status & 0x88) != 0x80)
                    throw new InvalidDataException("PIO callback differs from its real pending phase.");
                bool validPhase = callbacks[0].Operation switch
                {
                    "event0" => writing && !identify && offset == 512 && remaining >= 1,
                    "event1" => identify && !writing && offset == 0,
                    "event2" => writing && !identify && offset == 0 && remaining >= 1,
                    "event3" => !writing && !identify && offset == 0 && remaining >= 1,
                    _ => false
                };
                if (!validPhase) throw new InvalidDataException("PIO callback differs from its command, buffer or remaining sectors.");
            }
            if (mode == AtaDmaDirection.None && !writing && !pioData && callbacks.Length == 0) continue;
            var attachment = definition.HardDisks.SingleOrDefault(row => row.Slot == slot);
            var medium = media.SingleOrDefault(row => row.Device == "ide" && row.Slot == slot);
            if (attachment is null || medium is null) throw new InvalidDataException("Active IDE operation has no sealed medium.");
            bool writable = attachment.OverlayPath is not null;
            if (writing && (!writable || lba < 0 || remaining < 0 || lba > medium.Sectors - remaining))
                throw new InvalidDataException("IDE write phase differs from its writable medium extent.");
            if (pioData && !identify && (lba < 0 || lba >= medium.Sectors))
                throw new InvalidDataException("Ready PIO sector buffer has no valid current medium sector.");
            if (mode == AtaDmaDirection.DeviceToMemory && (status & 8) != 0 && (lba < 0 || lba >= medium.Sectors))
                throw new InvalidDataException("Ready DMA read buffer has no valid current source sector.");
            foreach (var callback in callbacks.Where(row => row.Operation == "event0"))
                if (callback.Id != token || mode != AtaDmaDirection.None || !writing || offset != 512 ||
                    (status & 0x88) != 0x80 || remaining < 1 || !writable || lba < 0 || lba >= medium.Sectors)
                    throw new InvalidDataException("PIO write commit differs from its real complete writable sector.");
            // A busy READ can legitimately reach IDNF at its next callback. Its entire remaining range is not preflighted.
            // Completed mode=None writes may retain a one-past LBA with zero remaining sectors.
        }
    }

    /// <summary>Bounds every variable collection independently of its containing JSON file's byte limit.</summary>
    private static void CheckSize(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 262144) throw new InvalidDataException("Snapshot collection exceeds its resource limit.");
            foreach (var child in value.EnumerateArray()) CheckSize(child);
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var child in value.EnumerateObject()) CheckSize(child.Value);
        else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Snapshot string exceeds its resource limit.");
    }
}
