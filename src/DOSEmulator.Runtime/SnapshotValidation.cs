// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Checks bounds used by indexes, buffers and virtual clocks before untrusted data reaches a prepared PC.</summary>
internal static class SnapshotValidation
{
    /// <summary>Validates graph-wide resource limits and component-specific mutable index contracts.</summary>
    internal static void Check(HardwareState state)
    {
        if (state.Components is null || state.Scheduler is null || state.Components.Count > 2048)
            throw new InvalidDataException("Incomplete snapshot graph.");
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
        if (state.Components.TryGetValue("cpu", out var cpuState)) CheckRepContinuation(cpuState);
        Range("audio", "_count", 0, 958);
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
        // Remaining fields still pass the complete typed loader and exact round-trip check.
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
