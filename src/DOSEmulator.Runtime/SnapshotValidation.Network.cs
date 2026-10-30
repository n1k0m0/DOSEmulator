// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

internal static partial class SnapshotValidation
{
    /// <summary>Checks every optional NIC, PCI peer, DMA latch, queued frame and exact callback before any media is opened.</summary>
    private static void CheckNetwork(HardwareState state, MachineProfile profile)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        bool pciInterrupt = false;
        foreach (var adapter in profile.NetworkAdapters)
        {
            string name = "nic" + adapter.Slot; expected.Add(name);
            if (!state.Components.TryGetValue(name, out var nic)) throw new InvalidDataException("Missing configured Ethernet controller.");
            Ne2000.ValidateSnapshot(nic, state.Scheduler, name, (ushort)adapter.IoBase);
            bool interrupt = nic.Fields["_interruptAsserted"].GetBoolean();
            if (adapter.Model == NetworkAdapterProfile.Rtl8029Pci)
            {
                string pciName = name + "-pci"; expected.Add(pciName);
                if (!state.Components.TryGetValue(pciName, out var pci)) throw new InvalidDataException("Missing RTL8029 PCI function.");
                Rtl8029.ValidateSnapshot(pci, state.Scheduler, pciName);
                bool dma = nic.Fields["_remoteCount"].GetUInt16() != 0 || nic.Fields["_remoteReadValidBytes"].GetByte() != 0 || nic.Fields["_remoteAutoPacket"].GetBoolean();
                int remoteMode = (nic.Fields["_command"].GetByte() >> 3) & 7;
                if (((dma && remoteMode is >= 1 and <= 3) || nic.Fields["_transmitEvent"].GetInt64() != 0) && !nic.Fields["_remotePci"].GetBoolean())
                    throw new InvalidDataException("Active RTL8029 remote DMA lacks its PCI data-port wiring.");
                byte control = StateValues.Read<byte>(pci.Fields["_eepromControl"]);
                byte power = StateValues.Read<byte>(pci.Fields["_configuration3"]);
                long autoload = pci.Fields["_autoloadEvent"].GetInt64();
                bool external = (power & 2) == 0 && (control & 0xc0) != 0x80 && autoload == 0;
                if (nic.Fields["_externalEnabled"].GetBoolean() != external)
                    throw new InvalidDataException("RTL8029 power/EEPROM gate differs from its NIC.");
                pciInterrupt |= interrupt;
            }
            else
            {
                if (nic.Fields["_remotePci"].GetBoolean() || !nic.Fields["_externalEnabled"].GetBoolean())
                    throw new InvalidDataException("ISA NE2000 contains a PCI-only transfer or power gate.");
                bool pin = profile.IsPiixIrq
                    ? (state.Components["piix-irq"].Fields["_isaLines"].GetUInt16() & (1 << adapter.Irq)) != 0
                    : (state.Components["pic/_slave"].Fields["Lines"].GetByte() & (1 << (adapter.Irq - 8))) != 0;
                if (pin != interrupt) throw new InvalidDataException("NE2000 IRQ differs from its physical board input.");
            }
        }
        if (!expected.SetEquals(state.Components.Keys.Where(n => n.StartsWith("nic", StringComparison.Ordinal))))
            throw new InvalidDataException("Ethernet snapshot topology differs from the selected card slots.");
        if (profile.NetworkAdapters.Any(n => n.Model == NetworkAdapterProfile.Rtl8029Pci) &&
            (((state.Components["piix-irq"].Fields["_pirqAssertions"].GetByte() & 1) != 0) != pciInterrupt))
            throw new InvalidDataException("Shared RTL8029 INTA requests differ from their real PIRQA wire.");
    }
}
