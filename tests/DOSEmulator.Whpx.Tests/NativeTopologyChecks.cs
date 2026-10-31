// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Whole-RAM oracles for repeated actual CPU A20 transitions across large native mapping plans.</summary>
internal static class NativeTopologyChecks
{
    /// <summary>Warms both transition directions and checks all bytes, including untouched even and odd high-MiB pages.</summary>
    internal static void Run()
    {
        foreach (int mib in new[] { 8, 64, 128 })
        {
            var memory = new MemoryBus(mib << 20); var ports = new IoPortBus();
            ports.Map(0x92, 1, new DevelopmentBoardPorts(memory));
            var code = new List<byte> { 0xb8, 0xff, 0xff, 0x8e, 0xc0 };
            for (int n = 0; n < 64; n++)
                code.AddRange(new byte[] { 0xb0, 2, 0xe6, 0x92, 0x26, 0xc6, 0x06, 0x10, 0, 0x66,
                    0xb0, 0, 0xe6, 0x92, 0x26, 0xc6, 0x06, 0x10, 0, 0x55 });
            code.Add(0xf4); code.ToArray().CopyTo(memory.Ram.AsSpan(0x1000));
            for (int i = 0x200000; i < memory.Ram.Length; i += 4096) memory.Ram[i] = (byte)(i >> 20);
            byte[] expected = (byte[])memory.Ram.Clone(); expected[0] = 0x55; expected[0x100000] = 0x66;
            using var cpu = new WhpxMappedMachine(memory, ports, new Pic8259(), new DeviceScheduler());
            cpu.SetEntry(0, 0x1000); var result = cpu.RunUntilHalt(TimeSpan.FromSeconds(15));
            if (result.IoExits != 128 || !memory.Ram.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"Repeated native A20 topology corrupted {mib}-MiB RAM.");
            Console.WriteLine($"PASS: native A20 {mib} MiB, 128 real OUTs, whole RAM and every high-page guard unchanged.");
        }
    }
}
