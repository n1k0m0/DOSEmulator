// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent original-PIIX byte contracts and real merged pin/level/ACK/EOI effects; no commercial PCI card or guest acceptance is implied.</summary>
internal static class PiixIrqBridgeTests
{
    private static readonly int[] LegalIrqs = [3, 4, 5, 6, 7, 9, 10, 11, 12, 14, 15];

    /// <summary>Requires an observable byte, IRQ vector, source or no-mutation invariant.</summary>
    private static void Check(bool condition, string why = "PIIX IRQ bridge contract failed.")
    { if (!condition) throw new Exception(why); }

    /// <summary>Requires the caller validation family without swallowing an unrelated execution failure.</summary>
    private static void Invalid(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid PIIX IRQ caller input was accepted.");
    }

    /// <summary>Initializes the actual capable PIC pair through ICWs and unmasks both banks before bridge pin traffic.</summary>
    private sealed class Rig
    {
        public readonly Pic8259 Pic = new(true);
        public readonly Intel82371FbIrq Bridge;
        public readonly List<(int Irq, byte Vector, bool Spurious)> Acknowledgements = [];

        /// <summary>Connects a fresh bridge and initializes both controller banks before any input is asserted.</summary>
        public Rig()
        {
            Bridge = new(Pic);
            Pic.InterruptAcknowledged += (irq, vector, spurious) => Acknowledgements.Add((irq, vector, spurious));
            Pic.Write(0x20, 0x11, 1); Pic.Write(0xa0, 0x11, 1);
            Pic.Write(0x21, 0x20, 1); Pic.Write(0xa1, 0x28, 1);
            Pic.Write(0x21, 4, 1); Pic.Write(0xa1, 2, 1);
            Pic.Write(0x21, 1, 1); Pic.Write(0xa1, 1, 1);
            Pic.Write(0x21, 0, 1); Pic.Write(0xa1, 0, 1);
        }
    }

    /// <summary>Reads all configuration bytes without relying on the implementation's internal mask table.</summary>
    private static byte[] Config(Intel82371FbIrq bridge) =>
        Enumerable.Range(0, 256).Select(offset => (byte)bridge.ReadConfiguration(offset, 1)).ToArray();

    /// <summary>Captures every explicit bridge field plus the actual PIC child state for no-side-effect checks.</summary>
    private static string State(Rig rig) => JsonSerializer.Serialize(
        new[] { rig.Bridge.SaveState(), rig.Pic.SaveState() }.Concat(rig.Pic.StateChildren.Select(v => v.Value.SaveState())).ToArray(), StateValues.Json);

    /// <summary>Selects a real ELCR bit through its byte port while retaining the other seven settings.</summary>
    private static void Level(Rig rig, int irq)
    {
        ushort port = irq < 8 ? (ushort)0x4d0 : (ushort)0x4d1;
        rig.Bridge.Write(port, rig.Bridge.Read(port, 1) | (uint)(1 << (irq & 7)), 1);
        Check(rig.Bridge.Elcr == rig.Pic.LevelMask && (rig.Bridge.Elcr & (1 << irq)) != 0);
    }

    /// <summary>Programs one independent source route through its actual configuration byte.</summary>
    private static void Route(Rig rig, int pin, int irq) => rig.Bridge.WriteConfiguration(0x60 + pin, (uint)irq, 1);

    /// <summary>Acknowledges the actual physical request and checks the independently programmed PIC vectors.</summary>
    private static void Ack(Rig rig, int irq)
    {
        int before = rig.Acknowledgements.Count;
        byte expected = (byte)(irq < 8 ? 0x20 + irq : 0x28 + irq - 8);
        Check(rig.Pic.HasPending && rig.Pic.Acknowledge() == expected, "Wrong physical IRQ/vector.");
        Check(rig.Acknowledgements.Count == before + 1 && rig.Acknowledgements[^1] == (irq, expected, false),
            "Physical PIC acknowledgement evidence differs.");
    }

    /// <summary>Issues the slave EOI first where needed, followed by the real cascade/master EOI.</summary>
    private static void Eoi(Rig rig, int irq)
    { if (irq >= 8) rig.Pic.Write(0xa0, 0x20, 1); rig.Pic.Write(0x20, 0x20, 1); }

    /// <summary>Seeds only permitted PCI error status in a newly prepared component; ordinary writes must then perform W1C.</summary>
    private static void SeedStatus(Rig rig, ushort status)
    {
        var state = rig.Bridge.SaveState();
        byte[] bytes = state.Fields["_config"].Deserialize<byte[]>(StateValues.Json)!;
        bytes[6] = (byte)status; bytes[7] = (byte)(status >> 8);
        state.Fields["_config"] = JsonSerializer.SerializeToElement(bytes, StateValues.Json);
        rig.Bridge.LoadState(state);
        Check(rig.Bridge.ReadConfiguration(6, 2) == status);
    }

    /// <summary>Registers complete device transactions and independent routing, source retention and no-hidden-access checks.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PIIX IRQ bridge rejects null and legacy PIC before configuration use", () =>
        {
            Invalid(() => new Intel82371FbIrq(null!));
            var old = new Pic8259(); Invalid(() => new Intel82371FbIrq(old));
            Check(!old.HasPending && !old.SupportsLevelInputs && old.LevelMask == 0);
        });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 PIIX IRQ exact A1 header and reserved bytes at width " + width, () =>
            {
                var rig = new Rig();
                byte[] header = [0x86, 0x80, 0x2e, 0x12, 7, 0, 0, 2, 2, 0, 1, 6, 0, 0, 0x80, 0];
                for (int offset = 0; offset + width <= header.Length; offset++)
                {
                    uint expected = 0;
                    for (int lane = 0; lane < width; lane++) expected |= (uint)header[offset + lane] << (8 * lane);
                    Check(rig.Bridge.ReadConfiguration(offset, width) == expected, "Independent A1 header byte order differs.");
                }
                foreach (int offset in new[] { 0x0f, 0x3c, 0x44, 0x50, 0x64, 0x72, 0x78, 0x80, 0x82, 0xa0, 0xfc })
                    if (offset + width <= 256) Check(rig.Bridge.ReadConfiguration(offset, width) == 0,
                        "An unimplemented control acquired an invented answer.");
                Check(rig.Bridge.ReadConfiguration(0x4e, 1) == 3 && rig.Bridge.ReadConfiguration(0x6a, 2) == 4);
                Check(rig.Bridge.PirqAssertions == 0 && rig.Bridge.IsaLines == 0 && rig.Bridge.Elcr == 0 && rig.Pic.InputLines == 0);
            });
        foreach (uint incoming in new uint[] { 0, 7, 8, 0xffff })
            test("M4 PIIX IRQ command fixed bits and special-cycle latch " + incoming, () =>
            {
                var rig = new Rig(); rig.Bridge.WriteConfiguration(4, incoming, 2);
                Check(rig.Bridge.ReadConfiguration(4, 2) == (7 | (incoming & 8)));
                Check(!rig.Pic.HasPending && rig.Bridge.ReadConfiguration(6, 2) == 0x200);
            });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 PIIX IRQ readonly identity lanes reject writes width " + width, () =>
            {
                var rig = new Rig(); byte[] before = Config(rig.Bridge);
                foreach (int offset in new[] { 0, 1, 2, 8, 9, 10, 11, 12, 14, 0xfc })
                {
                    if (offset < 4 && offset + width > 4 || offset >= 8 && offset < 16 && offset + width > 16) continue;
                    rig.Bridge.WriteConfiguration(offset, uint.MaxValue, width);
                }
                Check(Config(rig.Bridge).AsSpan().SequenceEqual(before), "Fixed/reserved byte changed.");
            });
        foreach (int value in new[] { 0, 1, 3, 4, 7, 0xfb, 0xff })
            test("M4 PIIX IRQ MSTAT controls only the multifunction header bit " + value, () =>
            {
                var rig = new Rig(); rig.Bridge.WriteConfiguration(0x6a, (uint)value, 2);
                Check(rig.Bridge.ReadConfiguration(0x6a, 2) == (value & 4));
                Check(rig.Bridge.ReadConfiguration(0x0e, 1) == ((value & 4) << 5));
                rig.Bridge.WriteConfiguration(0x0e, (uint)~value, 1);
                Check(rig.Bridge.ReadConfiguration(0x0e, 1) == ((value & 4) << 5), "HEDT acquired independent writability.");
                Check(!rig.Pic.HasPending && rig.Bridge.Elcr == 0);
            });
        test("M4 PIIX IRQ inert XBCS and MIRQ latches retain only original PIIX bits", () =>
        {
            var rig = new Rig();
            foreach (int value in Enumerable.Range(0, 256))
            {
                rig.Bridge.WriteConfiguration(0x4e, (uint)value, 1);
                rig.Bridge.WriteConfiguration(0x70, (uint)value | ((uint)(255 - value) << 8), 2);
                Check(rig.Bridge.ReadConfiguration(0x4e, 1) == (value & 0xf7));
                Check(rig.Bridge.ReadConfiguration(0x70, 1) == (value & 0xcf));
                Check(rig.Bridge.ReadConfiguration(0x71, 1) == ((255 - value) & 0xcf));
                Check(!rig.Pic.HasPending && rig.Pic.InputLines == 0 && rig.Bridge.PirqAssertions == 0,
                    "Unwired motherboard controls invented an interrupt.");
            }
        });
        foreach (var transfer in new (int Offset, int Width, uint Value)[]
        {
            (0x60, 4, 0x0f0e0b0a), (0x60, 2, 0x0a03), (0x61, 2, 0x0e07),
            (0x62, 2, 0x0f09), (0x60, 1, 3), (0x63, 1, 15), (0x5f, 4, 0x0b070355)
        })
            test($"M4 PIIX IRQ complete route lanes {transfer.Offset:X2}/{transfer.Width}", () =>
            {
                var rig = new Rig(); rig.Bridge.Write(0x4d0, 0xf8, 1); rig.Bridge.Write(0x4d1, 0xde, 1);
                rig.Bridge.WriteConfiguration(transfer.Offset, transfer.Value, transfer.Width);
                int expectedPins = 0;
                for (int pin = 0; pin < 4; pin++)
                {
                    int lane = 0x60 + pin - transfer.Offset;
                    int expectedByte = lane >= 0 && lane < transfer.Width ? (int)((transfer.Value >> (8 * lane)) & 0x8f) : 0x80;
                    Check(rig.Bridge.ReadConfiguration(0x60 + pin, 1) == expectedByte, "Wrong selected route byte.");
                    rig.Bridge.SetPirq(pin, true);
                    if ((expectedByte & 0x80) == 0) expectedPins |= 1 << (expectedByte & 15);
                }
                Check((rig.Pic.InputLines & ~4) == expectedPins && rig.Bridge.PirqAssertions == 15,
                    "Original byte lanes selected the wrong physical IRQs.");
                Check(rig.Bridge.Elcr == 0xdef8);
            });
        foreach (int pin in Enumerable.Range(0, 4)) foreach (int irq in LegalIrqs)
            test($"M4 PIIX IRQ pin {pin} routes to physical IRQ{irq} and returns after deassert/EOI", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, pin, irq);
                rig.Bridge.SetPirq(pin, true);
                Check(rig.Bridge.PirqAssertions == 1 << pin && (rig.Pic.InputLines & (1 << irq)) != 0);
                Ack(rig, irq); Check(!rig.Pic.HasPending, "In-service request reentered before EOI.");
                rig.Bridge.SetPirq(pin, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Bridge.PirqAssertions == 0 && (rig.Pic.InputLines & (1 << irq)) == 0);
                rig.Bridge.SetPirq(pin, true); Ack(rig, irq); rig.Bridge.SetPirq(pin, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Acknowledgements.Count == 2);
            });
        foreach (int pin in Enumerable.Range(0, 4)) foreach (int code in new[] { 0, 1, 2, 8, 13 })
            test($"M4 PIIX IRQ reserved route {code} on pin {pin} remains disconnected", () =>
            {
                var rig = new Rig(); rig.Bridge.WriteConfiguration(0x60 + pin, (uint)(code | 0x70), 1);
                rig.Bridge.SetPirq(pin, true);
                Check(rig.Bridge.ReadConfiguration(0x60 + pin, 1) == code && !rig.Pic.HasPending && rig.Pic.InputLines == 0,
                    "Reserved nibble was coerced to a real IRQ.");
                rig.Bridge.SetIsaLine(5, true); Ack(rig, 5);
                rig.Bridge.SetIsaLine(5, false); Eoi(rig, 5); Check(!rig.Pic.HasPending);
            });
        foreach (int irq in LegalIrqs)
            test("M4 PIIX IRQ an idle enabled route replaces an already high ISA input IRQ" + irq, () =>
            {
                var rig = new Rig(); Level(rig, irq);
                ushort maskPort = irq < 8 ? (ushort)0x21 : (ushort)0xa1;
                rig.Pic.Write(maskPort, (uint)(1 << (irq & 7)), 1); rig.Bridge.SetIsaLine(irq, true);
                Check(!rig.Pic.HasPending && (rig.Pic.InputLines & (1 << irq)) != 0); Route(rig, 0, irq);
                rig.Pic.Write(maskPort, 0, 1);
                Check(rig.Bridge.IsaLines == 1 << irq && !rig.Pic.HasPending && (rig.Pic.InputLines & (1 << irq)) == 0,
                    "An idle route failed to mask the ordinary ISA pin.");
                rig.Bridge.SetPirq(0, true); Ack(rig, irq); rig.Bridge.SetPirq(0, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending, "Masked ISA source leaked after PCI EOI.");
                rig.Bridge.WriteConfiguration(0x60, (uint)(0x80 | irq), 1);
                Check(rig.Pic.HasPending && (rig.Pic.InputLines & (1 << irq)) != 0, "Disabling the route lost retained ISA level.");
                Ack(rig, irq); rig.Bridge.SetIsaLine(irq, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
            });
        foreach (int irq in new[] { 3, 7, 9, 15 })
            test("M4 PIIX IRQ a held PCI level reasserts only after the real EOI IRQ" + irq, () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, 0, irq); rig.Bridge.SetPirq(0, true);
                Ack(rig, irq); rig.Bridge.SetPirq(0, true); Check(!rig.Pic.HasPending);
                Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetPirq(0, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Acknowledgements.Count == 2);
            });
        foreach (int first in Enumerable.Range(0, 4)) foreach (int second in Enumerable.Range(0, 4).Where(v => v > first))
        foreach (int irq in new[] { 5, 11 })
            test($"M4 PIIX IRQ shared IRQ{irq} retains pin {second} after pin {first} releases", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, first, irq); Route(rig, second, irq);
                rig.Bridge.SetPirq(first, true); rig.Bridge.SetPirq(second, true); Ack(rig, irq);
                rig.Bridge.SetPirq(first, false);
                Check((rig.Pic.InputLines & (1 << irq)) != 0 && rig.Bridge.PirqAssertions == 1 << second);
                Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetPirq(second, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
            });
        foreach (var change in new (int Before, int After)[] { (3, 9), (7, 15), (5, 11) })
            test($"M4 PIIX IRQ rerouting an asserted pin moves the real level {change.Before}->{change.After}", () =>
            {
                var rig = new Rig(); Level(rig, change.Before); Level(rig, change.After);
                Route(rig, 1, change.Before); rig.Bridge.SetPirq(1, true); Route(rig, 1, change.After);
                Check((rig.Pic.InputLines & ~4) == 1 << change.After, "Old route retained a physical level.");
                Ack(rig, change.After); rig.Bridge.SetPirq(1, false); Eoi(rig, change.After);
                Check(!rig.Pic.HasPending && rig.Acknowledgements.Count == 1, "Old level left a false pending request.");
            });
        test("M4 PIIX IRQ withdrawing a slave route before ACK preserves the real spurious cascade", () =>
        {
            var rig = new Rig(); Level(rig, 15); Level(rig, 7); Route(rig, 1, 15); rig.Bridge.SetPirq(1, true);
            Route(rig, 1, 7); Check((rig.Pic.InputLines & ~4) == 1 << 7);
            Check(rig.Pic.HasPending && rig.Pic.Acknowledge() == 0x2f &&
                rig.Acknowledgements[^1] == (15, (byte)0x2f, true), "Withdrawn slave input lost its latched master cascade.");
            rig.Pic.Write(0x20, 0x20, 1); Ack(rig, 7);
            rig.Bridge.SetPirq(1, false); Eoi(rig, 7); Check(!rig.Pic.HasPending && rig.Acknowledgements.Count == 2);
        });
        test("M4 PIIX IRQ a masked asserted level survives until PIC unmask without a new edge", () =>
        {
            var rig = new Rig(); Level(rig, 11); Route(rig, 2, 11); rig.Pic.Write(0xa1, 8, 1);
            rig.Bridge.SetPirq(2, true); Check(!rig.Pic.HasPending && (rig.Pic.InputLines & (1 << 11)) != 0);
            rig.Pic.Write(0xa1, 0, 1); Ack(rig, 11);
            rig.Bridge.SetPirq(2, false); Eoi(rig, 11); Check(!rig.Pic.HasPending);
        });
        foreach (int irq in new[] { 0, 1, 3, 7, 8, 13, 15 })
            test("M4 PIIX IRQ ordinary ISA pulse retains default edge semantics IRQ" + irq, () =>
            {
                var rig = new Rig(); rig.Bridge.SetIsaLine(irq, true); rig.Bridge.SetIsaLine(irq, false);
                Check(rig.Bridge.IsaLines == 0 && rig.Bridge.Elcr == 0);
                Ack(rig, irq); Eoi(rig, irq); Check(!rig.Pic.HasPending);
            });
        foreach (ushort port in new ushort[] { 0x4d0, 0x4d1 })
            test("M4 PIIX IRQ every ELCR byte value obeys hardwired IRQ restrictions port " + port.ToString("X4"), () =>
            {
                var rig = new Rig(); int mask = port == 0x4d0 ? 0xf8 : 0xde;
                foreach (uint incoming in Enumerable.Range(0, 256).Select(v => (uint)v))
                {
                    rig.Bridge.Write(port, incoming | 0xa500, 1);
                    Check(rig.Bridge.Read(port, 1) == (incoming & mask));
                    Check(rig.Bridge.Elcr == rig.Pic.LevelMask && (rig.Bridge.Elcr & ~0xdef8) == 0 && !rig.Pic.HasPending);
                }
            });
        test("M4 PIIX IRQ switching an already high source to level takes the actual level", () =>
        {
            var rig = new Rig(); Route(rig, 0, 5); rig.Bridge.SetPirq(0, true);
            Ack(rig, 5); Eoi(rig, 5); Check(!rig.Pic.HasPending);
            Level(rig, 5); Ack(rig, 5); rig.Bridge.SetPirq(0, false); Eoi(rig, 5); Check(!rig.Pic.HasPending);
        });
        test("M4 PIIX IRQ switching level to edge does not manufacture a second rising edge", () =>
        {
            var rig = new Rig(); Level(rig, 5); Route(rig, 0, 5); rig.Bridge.SetPirq(0, true); Ack(rig, 5);
            rig.Bridge.Write(0x4d0, 0, 1); Eoi(rig, 5); Check(!rig.Pic.HasPending);
            rig.Bridge.SetPirq(0, false); rig.Bridge.SetPirq(0, true); Ack(rig, 5);
            rig.Bridge.SetPirq(0, false); Eoi(rig, 5); Check(!rig.Pic.HasPending);
        });
        foreach (ushort port in new ushort[] { 0x4cf, 0x4d0, 0x4d1, 0x4d2 }) foreach (int width in new[] { 2, 4 })
            test($"M4 PIIX IRQ wide ELCR access {port:X4}/{width} has no hidden byte accesses", () =>
            {
                var rig = new Rig(); Level(rig, 11); Route(rig, 0, 11); rig.Bridge.SetPirq(0, true);
                string before = State(rig); uint mask = width == 2 ? 65535u : uint.MaxValue;
                Check(rig.Bridge.Read(port, width) == mask); rig.Bridge.Write(port, uint.MaxValue, width);
                Check(State(rig) == before && rig.Pic.HasPending);
            });
        test("M4 PIIX IRQ all valid register reads preserve pending source and PIC state", () =>
        {
            var rig = new Rig(); Level(rig, 10); Route(rig, 3, 10); rig.Bridge.SetIsaLine(7, true); rig.Bridge.SetPirq(3, true);
            string before = State(rig);
            foreach (int width in new[] { 1, 2, 4 }) for (int offset = 0; offset + width <= 256; offset++)
                rig.Bridge.ReadConfiguration(offset, width);
            foreach (ushort port in new ushort[] { 0x4cf, 0x4d0, 0x4d1, 0x4d2 }) rig.Bridge.Read(port, 1);
            Check(State(rig) == before && rig.Pic.HasPending && rig.Acknowledgements.Count == 0,
                "A pure read consumed/replayed input or changed a register.");
        });
        foreach (int width in new[] { -1, 0, 3, 8 })
            test("M4 PIIX IRQ invalid API width is rejected atomically " + width, () =>
            {
                var rig = new Rig(); Level(rig, 5); Route(rig, 0, 5); rig.Bridge.SetPirq(0, true); string before = State(rig);
                Invalid(() => rig.Bridge.ReadConfiguration(0x60, width)); Invalid(() => rig.Bridge.WriteConfiguration(0x60, 0, width));
                Invalid(() => rig.Bridge.Read(0x4d0, width)); Invalid(() => rig.Bridge.Write(0x4d0, 0, width));
                Check(State(rig) == before && rig.Pic.HasPending);
            });
        foreach (var transaction in new (int Offset, int Width)[] { (-1, 1), (256, 1), (255, 2), (253, 4), (int.MaxValue, 4) })
            test($"M4 PIIX IRQ incomplete config range {transaction.Offset}/{transaction.Width} has no partial commit", () =>
            {
                var rig = new Rig(); string before = State(rig);
                Invalid(() => rig.Bridge.ReadConfiguration(transaction.Offset, transaction.Width));
                Invalid(() => rig.Bridge.WriteConfiguration(transaction.Offset, uint.MaxValue, transaction.Width));
                Check(State(rig) == before);
            });
        foreach (int irq in new[] { -1, 2, 16, int.MaxValue })
            test("M4 PIIX IRQ invalid ordinary ISA pin does not take over the cascade " + irq, () =>
            {
                var rig = new Rig(); string before = State(rig); Invalid(() => rig.Bridge.SetIsaLine(irq, true)); Check(State(rig) == before);
            });
        foreach (int pin in new[] { -1, 4, int.MaxValue })
            test("M4 PIIX IRQ invalid PCI pin has no source or line effect " + pin, () =>
            {
                var rig = new Rig(); string before = State(rig); Invalid(() => rig.Bridge.SetPirq(pin, true)); Check(State(rig) == before);
            });
        foreach (int statusBit in new[] { 0x800, 0x1000, 0x2000 }) foreach (int width in new[] { 1, 2, 4 })
            test($"M4 PIIX IRQ actual W1C bit {statusBit:X4} through width {width}", () =>
            {
                var rig = new Rig(); SeedStatus(rig, 0x3a00);
                int offset = width == 1 ? 7 : width == 2 ? 6 : 4;
                rig.Bridge.WriteConfiguration(offset, 0, width);
                Check(rig.Bridge.ReadConfiguration(6, 2) == 0x3a00, "Writing zero cleared a W1C latch.");
                uint incoming = width == 1 ? (uint)(statusBit >> 8) : width == 2 ? (uint)statusBit : (uint)statusBit << 16;
                rig.Bridge.WriteConfiguration(offset, incoming, width);
                Check(rig.Bridge.ReadConfiguration(6, 2) == (0x3a00 & ~statusBit), "Wrong W1C byte/latch cleared.");
                Check(rig.Bridge.ReadConfiguration(4, 2) == 7 && !rig.Pic.HasPending);
                rig.Bridge.WriteConfiguration(offset, incoming, width);
                Check(rig.Bridge.ReadConfiguration(6, 2) == (0x3a00 & ~statusBit), "Second W1C write recreated an error.");
            });
    }
}
