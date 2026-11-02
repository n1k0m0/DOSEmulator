// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent actual-bus serial DAC, clock RAM and snapshot cursor boundaries required by original SST-1 initialization.</summary>
internal static class VoodooDacTests
{
    private const uint Base = 0xd0000000;
    /// <summary>Rejects any serial byte, cursor or remap discrepancy.</summary>
    private static void Check(bool value) { if (!value) throw new Exception("SST-1 DAC serial contract differs"); }
    private sealed class Rig
    {
        public readonly MemoryBus Memory = new(1 << 20);
        public readonly DeviceScheduler Clock = new();
        public readonly VoodooGraphics Card;
        /// <summary>Constructs one actual width-preserving bus target, with the original serial readback remap enabled.</summary>
        public Rig()
        {
            Card = new(Memory, Clock); Memory.MapDynamic(Card);
            Card.WriteConfiguration(0x10, Base, 4); Card.WriteConfiguration(4, 2, 2); Card.WriteConfiguration(0x40, 5, 4);
        }
        /// <summary>Issues one original external-DAC write using complete DWORD MMIO.</summary>
        public void Write(int address, byte data) => Memory.WriteDword(Base + 0x22c, (uint)(address << 8) | data);
        /// <summary>Separates the DAC-read request and FBI-remap read exactly as the original hardware interface requires.</summary>
        public byte Read(int address)
        { Memory.WriteDword(Base + 0x22c, (uint)(address << 8) | 0x800); return (byte)Memory.ReadDword(Base + 0x218); }
    }

    /// <summary>Registers serial transfers independently from driver identification and future rendering policies.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var pair in new[] { (Index: 1, Low: 0x55, High: 0x49), (Index: 7, Low: 0x71, High: 0x29), (Index: 11, Low: 0x79, High: 0x2e) })
            test($"Voodoo DAC original writable PLL defaults index {pair.Index}", () =>
            {
                var r = new Rig(); r.Write(7, (byte)pair.Index);
                Check(r.Read(5) == pair.Low && r.Read(5) == pair.High && r.Read(7) == (pair.Index + 1 & 15));
                r.Write(4, (byte)pair.Index); r.Write(5, 0x23); r.Write(5, 0x45); r.Write(7, (byte)pair.Index);
                Check(r.Read(5) == 0x23 && r.Read(5) == 0x45 && r.Clock.Now == 0 && r.Clock.NextEventTime is null);
            });
        test("Voodoo DAC readback latch and PCI remap do not change on a write", () =>
        {
            var r = new Rig(); r.Write(6, 0x50); Check(r.Read(6) == 0x50); r.Write(6, 0x70);
            Check(r.Memory.ReadDword(Base + 0x218) == 0x50);
            r.Card.WriteConfiguration(0x40, 1, 4); Check(r.Memory.ReadDword(Base + 0x218) == 0x80000040);
            r.Card.WriteConfiguration(0x40, 5, 4); Check(r.Memory.ReadDword(Base + 0x218) == 0x50 && r.Read(6) == 0x70);
        });
        foreach (int width in new[] { 1, 2, 4 }) test($"Voodoo DAC rejects narrow serial side effects width {width}", () =>
        {
            var r = new Rig(); r.Write(6, 0x50);
            if (width == 1) r.Memory.WriteByte(Base + 0x22c, 0x70);
            else if (width == 2) r.Memory.WriteWord(Base + 0x22c, 0x670);
            else r.Memory.WriteDword(Base + 0x22c, 0x670);
            Check(r.Read(6) == (width == 4 ? 0x70 : 0x50));
        });
        test("Voodoo DAC independent read/write cursors and single-byte control increment", () =>
        {
            var r = new Rig(); r.Write(4, 0); r.Write(5, 0x12); r.Write(7, 1); Check(r.Read(5) == 0x55);
            r.Write(5, 0x34); Check(r.Read(5) == 0x49); r.Write(7, 0); Check(r.Read(5) == 0x12 && r.Read(5) == 0x34);
            r.Write(4, 14); r.Write(5, 0xff); Check(r.Read(4) == 15); r.Write(7, 14);
            Check(r.Read(5) == 0x37 && r.Read(7) == 15); Check(r.Read(5) == 0 && r.Read(5) == 0 && r.Read(7) == 0);
        });
        test("Voodoo DAC actual clock words and selector determine nominal clock", () =>
        {
            var r = new Rig(); r.Write(4, 0); r.Write(5, 3); r.Write(5, 5);
            Check(r.Card.NominalVideoClockHz == 14_318_000UL * 5 / 7);
            r.Write(4, 1); r.Write(5, 5); r.Write(5, 6); r.Write(4, 14); r.Write(5, 0x21);
            Check(r.Card.NominalVideoClockHz == 14_318_000UL * 7 / 8 && r.Clock.Now == 0);
        });
        test("Voodoo DAC palette triples wrap and mask does not hide serial RAM", () =>
        {
            var r = new Rig(); Check(r.Read(2) == 255); r.Write(2, 0); r.Write(0, 255);
            foreach (byte b in new byte[] { 0x7f, 0x42, 0x23, 4, 5, 6 }) r.Write(1, b);
            Check(r.Read(0) == 1); r.Write(3, 255);
            foreach (byte b in new byte[] { 63, 2, 35, 4, 5, 6 }) Check(r.Read(1) == b);
            Check(r.Read(3) == 1 && r.Read(2) == 0);
        });
        test("Voodoo DAC PCI hard reset retains external powered clock RAM and serial phases", () =>
        {
            var r = new Rig(); r.Write(4, 1); r.Write(5, 0x23); r.Card.ApplyPiixHardReset();
            r.Card.WriteConfiguration(0x10, Base, 4); r.Card.WriteConfiguration(4, 2, 2); r.Card.WriteConfiguration(0x40, 5, 4);
            r.Write(5, 0x45); r.Write(7, 1); Check(r.Read(5) == 0x23 && r.Read(5) == 0x45);
            var fresh = new Rig(); fresh.Write(7, 1); Check(fresh.Read(5) == 0x55);
        });
        foreach (int phase in new[] { 0, 1 }) test($"Voodoo DAC full raw state continues independent PLL and palette cursors phase {phase}", () =>
        {
            var a = new Rig(); a.Write(4, 1); a.Write(5, 0x23); a.Write(7, 7); if (phase == 1) Check(a.Read(5) == 0x71);
            a.Write(0, 255); a.Write(1, 63); a.Write(3, 255); _ = a.Read(1);
            var b = new Rig(); b.Card.LoadState(a.Card.SaveState());
            a.Write(5, 0x45); b.Write(5, 0x45); a.Write(1, 2); b.Write(1, 2);
            foreach (int reg in new[] { 5, 5, 5, 1, 1, 2, 4, 6, 7 }) Check(a.Read(reg) == b.Read(reg));
            Check(JsonSerializer.Serialize(a.Card.SaveState(), StateValues.Json) == JsonSerializer.Serialize(b.Card.SaveState(), StateValues.Json));
        });
        foreach (string field in new[] { "_dacReadIndex", "_dacWriteIndex", "_dacReadPhase", "_dacWritePhase", "_dacPaletteReadPhase", "_dacPaletteWritePhase" })
            test($"Voodoo DAC saved serial state rejects invalid cursor {field}", () =>
            {
                var r = new Rig(); StateBlock state = r.Card.SaveState(); state.Fields[field] = JsonSerializer.SerializeToElement((byte)255);
                bool rejected = false; try { VoodooGraphics.ValidateSavedState(state); } catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            });
        foreach (int width in new[] { 1, 2, 4 }) test($"Voodoo PCI cfgStatus is the actual engine status alias width {width}", () =>
        {
            var r = new Rig(); uint actual = r.Memory.ReadDword(Base);
            Check(r.Card.ReadConfiguration(0x4c, width) == (actual & (width == 4 ? uint.MaxValue : (1u << (width * 8)) - 1)));
        });
        foreach (int offset in new[] { 0, 16, 18, 24, 26, 28, 29, 30 })
            test($"Voodoo DAC saved clock RAM rejects unreachable bits offset {offset}", () =>
            {
                var r = new Rig(); StateBlock state = r.Card.SaveState();
                byte[] data = state.Fields["_dacPll"].Deserialize<byte[]>()!; data[offset] = 0x80;
                state.Fields["_dacPll"] = JsonSerializer.SerializeToElement(data);
                bool rejected = false; try { VoodooGraphics.ValidateSavedState(state); } catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            });
    }
}
