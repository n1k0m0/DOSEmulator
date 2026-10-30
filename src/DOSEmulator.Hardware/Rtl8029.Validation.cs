// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class Rtl8029
{
    /// <summary>Checks all serialized PCI/power/serial fields and its exact auto-load callback before any media opens.</summary>
    public static void ValidateSnapshot(StateBlock state, SchedulerState scheduler, string node)
    {
        DeviceScheduler.ValidateState(scheduler); var r = new StateReader(state, "DOSEmulator.Hardware.Rtl8029");
        byte[] config = r.Value<byte[]>("_config"); byte eeprom = r.Value<byte>("_eepromControl"), c2 = r.Value<byte>("_configuration2"),
            c3 = r.Value<byte>("_configuration3"), halt = r.Value<byte>("_haltClock");
        ushort shift = r.Value<ushort>("_eepromShift"); r.Value<ushort>("_eepromOutput");
        int inputBits = r.Value<int>("_eepromBits"), outputBits = r.Value<int>("_eepromOutputBits");
        long token = r.Value<long>("_autoloadEvent"); ulong deadline = r.Value<ulong>("_autoloadDeadline"); r.Finish();
        if (config is not { Length: 256 } || (c2 & 0x3f) != 0 || (c3 & ~6) != 0 || (eeprom & 0x31) != 0 ||
            (halt != 0x48 && halt != 0x52) || (halt == 0x48 && (c3 & 2) == 0) ||
            inputBits < 0 || inputBits > 8 || outputBits < 0 || outputBits > 17 ||
            (inputBits == 0 ? shift != 0 : shift >= 1 << inputBits) || (inputBits != 0 && outputBits != 0) ||
            (token != 0 && (eeprom & 0xc0) != 0x40))
            throw new InvalidDataException("Invalid RTL8029 configuration or serial state.");
        for (int i = 0; i < 256; i++)
        {
            byte mask = i switch { 4 => 3, 0x10 => 0xe0, 0x11 or 0x12 or 0x13 => 0xff, 0x3c => 0x0f, _ => 0 };
            if ((config[i] & ~mask) != ResetByte(i)) throw new InvalidDataException("Invalid RTL8029 fixed configuration lane.");
        }
        Ne2000.ValidateNetworkEvent(scheduler, node, "autoload", token, deadline, 2_000_000);
        if (scheduler.Events.Any(e => e.Owner == node && !scheduler.Cancelled.Contains(e.Id) && e.Operation != "autoload"))
            throw new InvalidDataException("Unknown active RTL8029 callback.");
    }
}
