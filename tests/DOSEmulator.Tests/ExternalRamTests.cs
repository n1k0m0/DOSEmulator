// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Runtime;

/// <summary>Independent storage, decode and owner controls for the temporary shared native DRAM lease.</summary>
internal static class ExternalRamTests
{
    /// <summary>Rejects a violated storage or decode oracle with a focused description.</summary>
    private static void Need(bool value, string reason) { if (!value) throw new Exception(reason); }

    /// <summary>Requires the precise rejection without accepting an unrelated failure.</summary>
    private static void Reject<T>(Action operation) where T : Exception
    {
        try { operation(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    /// <summary>Checks coherent bus APIs and a complete stopped mirror for both supported access origins.</summary>
    private static void Coherence(bool externalWritesFirst)
    {
        var memory = new MemoryBus(2 << 20); var backing = new byte[memory.Ram.Length];
        memory.Ram[0x321] = 0x47; memory.AttachExternalRam(backing);
        Need(backing[0x321] == 0x47 && memory.HasExternalRam, "Attach omitted the stopped RAM image.");
        if (externalWritesFirst) backing[0x1234] = 0x91;
        else memory.WriteByte(0x1234, 0x91);
        Need(memory.ReadByte(0x1234) == 0x91 && memory.Ram[0x1234] == 0, "Active storage was copied or read from the stale mirror.");
        memory.WriteWord(0x2222, 0x7351); memory.WriteDword(0x3333, 0xd29f4e61);
        Need(BitConverter.ToUInt16(backing, 0x2222) == 0x7351 && memory.ReadWord(0x2222) == 0x7351 &&
            BitConverter.ToUInt32(backing, 0x3333) == 0xd29f4e61 && memory.ReadDword(0x3333) == 0xd29f4e61,
            "Word/dword bus accesses used another RAM image.");
        Need(memory.TryGetRamSpan(0x4444, 16, out var cpu) && memory.TryGetPhysicalRamSpan(0x5555, 16, null, out _), "Valid RAM span refused.");
        cpu.Fill(0x62); memory.PhysicalRam[0x5555] = 0xac;
        Need(memory.ReadByte(0x4444) == 0x62 && backing[0x5555] == 0xac, "Span writes were invisible to the shared bus.");
        memory.DetachExternalRam(backing);
        Need(!memory.HasExternalRam && memory.Ram.AsSpan().SequenceEqual(backing), "Detach lost any installed RAM byte.");
        memory.WriteByte(0x1234, 0x28);
        Need(memory.Ram[0x1234] == 0x28 && backing[0x1234] == 0x91, "Detached native storage was still in use.");
    }

    /// <summary>Registers semantic controls without a hypervisor, aligned allocator or native endpoint.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool externalFirstValue in new[] { false, true })
        {
            bool externalFirst = externalFirstValue;
            test("External RAM complete bus/span coherence " + externalFirst, () => Coherence(externalFirst));
        }
        test("External RAM owner and capacity refusals preserve the live lease", () =>
        {
            var memory = new MemoryBus(1 << 20); var a = new byte[memory.Ram.Length]; var b = new byte[a.Length];
            Reject<ArgumentException>(() => memory.AttachExternalRam(a.AsMemory(1)));
            memory.AttachExternalRam(a); a[7] = 0x61;
            Reject<InvalidOperationException>(() => memory.AttachExternalRam(b));
            Reject<InvalidOperationException>(() => memory.DetachExternalRam(b));
            Need(memory.HasExternalRam && memory.ReadByte(7) == 0x61, "Rejected lease replaced live bytes.");
            memory.DetachExternalRam(a); Reject<InvalidOperationException>(() => memory.DetachExternalRam(a));
            Need(memory.Ram[7] == 0x61, "Rejected detach lost stopped data.");
        });
        test("External RAM A20 and static mapped-device precedence stay on the bus", () =>
        {
            var memory = new MemoryBus(2 << 20); var backing = new byte[memory.Ram.Length];
            memory.Map(0xf0000, 65536, new RomDevice(Enumerable.Repeat((byte)0x6b, 65536).ToArray()), "own ROM");
            memory.AttachExternalRam(backing); memory.A20Enabled = false; memory.WriteByte(0x123456, 0x59);
            Need(backing[0x23456] == 0x59 && backing[0x123456] == 0 && memory.ReadByte(0xf0000) == 0x6b,
                "External RAM bypassed A20 or mapped ROM.");
            memory.WriteByte(0xf0000, 0x31); Need(backing[0xf0000] == 0, "A ROM store leaked into external DRAM.");
            Need(!memory.TryGetReadOnlyPhysicalRamSpan(0xf0000, 1, null, out _), "ROM was granted as physical RAM.");
            memory.DetachExternalRam(backing);
        });
        test("External RAM readonly classification has no write notifications", () =>
        {
            var memory = new MemoryBus(1 << 20); var backing = new byte[memory.Ram.Length]; int writes = 0;
            memory.PhysicalRamWrite += (_, _) => writes++; memory.AttachExternalRam(backing);
            Need(memory.TryGetReadOnlyPhysicalRamSpan(0x2000, 4096, null, out var span) && span.Length == 4096 && writes == 0,
                "A pure physical classification pretended to write RAM.");
            Need(memory.TryGetPhysicalRamSpan(0x2000, 1, null, out var writable) && writes == 1, "Writable DMA grant lost its notification.");
            writable[0] = 0xd5; Need(memory.ReadByte(0x2000) == 0xd5, "DMA grant did not expose live native storage.");
            memory.DetachExternalRam(backing);
        });
        test("External RAM hidden DRAM remains preserved after a decode change", () =>
        {
            var memory = new MemoryBus(2 << 20); var backing = new byte[memory.Ram.Length]; memory.AttachExternalRam(backing);
            backing[0x180000] = 0x53; memory.SetDecodedRamBytes(1 << 20);
            Need(memory.ReadByte(0x180000) == 255 && !memory.TryGetPhysicalRamSpan(0x180000, 1, null, out _), "Native storage bypassed DRB decode.");
            memory.DetachExternalRam(backing); Need(memory.Ram[0x180000] == 0x53, "Stopped mirror discarded hidden installed storage.");
        });
        test("External RAM refuses software CPU execution until its stopped mirror returns", () =>
        {
            using var pc = PcMachine.CreateDemo(); var backing = new byte[pc.Memory.Ram.Length]; pc.Memory.AttachExternalRam(backing);
            Reject<InvalidOperationException>(() => pc.RunSteps(1));
            Reject<InvalidOperationException>(() => pc.CaptureHardwareState());
            pc.Memory.DetachExternalRam(backing); Need(pc.RunSteps(1) == 1, "A data-only stopped lease changed CPU execution ownership.");
        });
    }
}
