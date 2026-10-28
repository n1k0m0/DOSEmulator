// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Cpu.X86;

namespace DOSEmulator.Runtime;

internal static partial class SnapshotValidation
{
    /// <summary>Validates the original CF9 edge latch, phase deadlines, exactly owned callbacks and held domains before opening any media.</summary>
    private static void CheckPiixReset(HardwareState state, MachineProfile profile)
    {
        var cancelled = state.Scheduler.Cancelled.ToHashSet();
        var live = state.Scheduler.Events.Where(e => !cancelled.Contains(e.Id) && e.Owner == "piix-reset").ToArray();
        if (!profile.IsPiixReset)
        {
            if (state.Components.ContainsKey("piix-reset") || live.Length != 0) throw new InvalidDataException("The earlier board has no CF9 reset sequencer.");
            return;
        }
        // All lookups are typed data reads against the sealed graph, without allocating a new PC.
        StateReader Node(string name, string type) => state.Components.TryGetValue(name, out var row)
            ? new StateReader(row, type) : throw new InvalidDataException("Missing reset domain: " + name);
        var reset = Node("piix-reset", "DOSEmulator.Hardware.PiixResetController");
        byte control = reset.Value<byte>("_control");
        var pending = reset.Value<PiixResetRequest>("_pending"); var phase = reset.Value<PiixResetPhase>("_phase");
        long pci = reset.Value<long>("_pciEvent"), cpu = reset.Value<long>("_cpuEvent");
        bool ready = reset.Value<bool>("_cpuReleaseReady");
        ulong at = reset.Value<ulong>("_assertedAt"), pciAt = reset.Value<ulong>("_pciReleaseAt"), cpuAt = reset.Value<ulong>("_cpuReleaseAt");
        ulong now = state.Scheduler.Now; var expected = new HashSet<long>();
        if ((control & ~6) != 0 || (uint)pending > 2 || (uint)phase > 2 || pci < 0 || cpu < 0 ||
            reset.Value<long>("SoftEdges") < 0 || reset.Value<long>("HardEdges") < 0)
            throw new InvalidDataException("Invalid CF9 latch, phase, edge counter or token.");
        bool hasPulse = pciAt != 0 || cpuAt != 0;
        if (hasPulse && (at > ulong.MaxValue - PiixResetController.CpuHoldNanoseconds ||
            pciAt != at + PiixResetController.PciIsaHoldNanoseconds || cpuAt != at + PiixResetController.CpuHoldNanoseconds))
            throw new InvalidDataException("Reset release deadlines differ from the own nominal pulse contract.");
        // Every live token must identify exactly the future callback that its current phase can execute.
        void Event(long token, string operation, ulong deadline)
        {
            var rows = live.Where(e => e.Id == token).ToArray();
            if (token <= 0 || rows.Length != 1 || rows[0].Operation != operation || rows[0].Time != deadline ||
                deadline <= now || rows[0].Payload is null || rows[0].Payload.Length != 0 || !expected.Add(token))
                throw new InvalidDataException("Reset event differs from its exact future phase.");
        }
        if (phase == PiixResetPhase.Idle)
        {
            if (pci != 0 || cpu != 0 || ready || !hasPulse && at != 0 || hasPulse && now < cpuAt)
                throw new InvalidDataException("Idle reset controller contains an unfinished pulse.");
        }
        else
        {
            if (!hasPulse || at > now || pending != PiixResetRequest.None || control != 0)
                throw new InvalidDataException("Held reset has a missing pulse or queued programming.");
            if (phase == PiixResetPhase.AllAsserted)
            {
                if (ready || now >= pciAt) throw new InvalidDataException("PCI/ISA reset outlived its callback.");
                Event(pci, "release-pci-isa", pciAt); Event(cpu, "release-cpu-ready", cpuAt);
            }
            else
            {
                if (pci != 0 || now < pciAt || ready != (now >= cpuAt) || ready && cpu != 0)
                    throw new InvalidDataException("CPU-only reset has inconsistent release readiness.");
                if (!ready) Event(cpu, "release-cpu-ready", cpuAt);
            }
            var c = Node("cpu", "DOSEmulator.Cpu.X86.X86Cpu");
            X86Cpu.ValidateP54cResetImage(state.Components["cpu"]);
            UInt128 epoch = (UInt128)at * (uint)profile.CpuHz;
            if (state.Scheduler.CachedFrequency != 0)
                epoch += (UInt128)state.Scheduler.Remainder * (uint)profile.CpuHz / state.Scheduler.CachedFrequency;
            if (c.Value<ulong>("_tscEpochTicks") != unchecked((ulong)(epoch / 1_000_000_000)))
                throw new InvalidDataException("Held CPURST TSC epoch differs from the exact assertion clock phase.");
            var host = Node("hostbridge", "DOSEmulator.Hardware.Intel82437Fx");
            var irq = Node("piix-irq", "DOSEmulator.Hardware.Intel82371FbIrq");
            var ide = Node("piix-ide", "DOSEmulator.Hardware.Intel82371FbIde");
            if (!Intel82437Fx.IsHardResetConfiguration(host.Value<byte[]>("_config")) ||
                !Intel82371FbIrq.IsHardResetConfiguration(irq.Value<byte[]>("_config")) || irq.Value<ushort>("_elcr") != 0 ||
                !Intel82371FbIde.IsHardResetConfiguration(ide.Value<byte[]>("_config")) ||
                Node("pci", "DOSEmulator.Hardware.PciConfigurationBus").Value<uint>("_address") != 0)
                throw new InvalidDataException("Held PCI domain differs from its hard-reset registers.");
            var pit = Node("pit", "DOSEmulator.Hardware.Pit8254");
            if (!pit.Value<bool>("_resetOutputLow") || pit.Value<bool>("_running") || pit.Value<long>("_event") != 0)
                throw new InvalidDataException("Held ISA timer has already been reprogrammed or retained old timer work.");
            if (phase == PiixResetPhase.AllAsserted)
            {
                var keyboard = Node("keyboard", "DOSEmulator.Hardware.Keyboard8042");
                var floppy = Node("floppy", "DOSEmulator.Hardware.Fdc82077");
                if (keyboard.Value<bool>("_enabled") || keyboard.Value<bool>("_scanning") ||
                    floppy.Value<byte>("_dor") != 0 || floppy.Value<long>("_event") != 0)
                    throw new InvalidDataException("ISA controller reset was released before its deadline.");
            }
            for (int channel = 0; channel < 2; channel++) for (int drive = 0; drive < 2; drive++)
            {
                var ata = Node("ata" + channel + "/_drives/" + drive, "DOSEmulator.Hardware.AtaPioDrive");
                bool asserted = phase == PiixResetPhase.AllAsserted;
                if (ata.Value<bool>("_resetActive") != asserted || ata.Value<byte>("_control") != (asserted ? 4 : 0) ||
                    ata.Value<long>("_event") != 0 || ata.Value<AtaDmaDirection>("_dmaDirection") != AtaDmaDirection.None ||
                    ata.Value<bool>("_writing") || ata.Value<int>("_offset") != 0 || ata.Value<int>("_remaining") != 0)
                    throw new InvalidDataException("Held board lost an ATA reset pin or retained an old transfer.");
            }
            foreach (var pair in new[] { ("sb2", "DOSEmulator.Hardware.SoundBlasterDsp"), ("sb16", "DOSEmulator.Hardware.SoundBlaster16") })
            {
                if (!state.Components.ContainsKey(pair.Item1)) continue;
                var dsp = Node(pair.Item1, pair.Item2);
                if (dsp.Value<bool>("_resetHigh") != (phase == PiixResetPhase.AllAsserted) ||
                    dsp.Value<long>("_commandEvent") != 0 || dsp.Value<long>("_sampleEvent") != 0 ||
                    pair.Item1 == "sb16" && dsp.Value<byte>("_xbus05Control") != 0)
                    throw new InvalidDataException("Held board lost its DSP reset pin or retained old DSP work.");
            }

        }
        if (live.Any(e => !expected.Contains(e.Id))) throw new InvalidDataException("Unreferenced live reset callback.");
    }
}
