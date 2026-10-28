// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent original-PIIX motherboard routing, shared levels, old capability and exact state-boundary cases.</summary>
internal static class PiixMirqTests
{
    private static readonly int[] LegalIrqs = [3, 4, 5, 6, 7, 9, 10, 11, 12, 14, 15];

    /// <summary>Requires a real source, vector, line or no-side-effect observation.</summary>
    private static void Check(bool value, string why = "PIIX MIRQ contract failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Requires a caller or imported-state rejection without accepting an unrelated failure.</summary>
    private static void Invalid(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Invalid MIRQ request or state was accepted.");
    }

    /// <summary>Connects a fixed-capability bridge to the actual level-capable, normally initialized PIC pair.</summary>
    private sealed class Rig
    {
        public readonly Pic8259 Pic = new(true);
        public readonly Intel82371FbIrq Bridge;
        public readonly List<(int Irq, byte Vector, bool Spurious)> Acks = [];
        /// <summary>Initializes real ICWs and unmasks both banks; no synthetic acknowledgement bypass is used.</summary>
        public Rig(bool mirq = true)
        {
            Bridge = new(Pic, mirq);
            Pic.InterruptAcknowledged += (irq, vector, spurious) => Acks.Add((irq, vector, spurious));
            Pic.Write(0x20, 0x11, 1); Pic.Write(0xa0, 0x11, 1);
            Pic.Write(0x21, 0x20, 1); Pic.Write(0xa1, 0x28, 1);
            Pic.Write(0x21, 4, 1); Pic.Write(0xa1, 2, 1);
            Pic.Write(0x21, 1, 1); Pic.Write(0xa1, 1, 1);
            Pic.Write(0x21, 0, 1); Pic.Write(0xa1, 0, 1);
        }
    }

    /// <summary>Reads complete configuration independently from private mask helpers.</summary>
    private static byte[] Configuration(Intel82371FbIrq bridge) =>
        Enumerable.Range(0, 256).Select(offset => (byte)bridge.ReadConfiguration(offset, 1)).ToArray();

    /// <summary>Captures all actual bridge and nested PIC state, including ISR/IRR and physical input history.</summary>
    private static string State(Rig rig) => JsonSerializer.Serialize(
        new[] { rig.Bridge.SaveState(), rig.Pic.SaveState() }.Concat(rig.Pic.StateChildren.Select(v => v.Value.SaveState())).ToArray(), StateValues.Json);

    /// <summary>Selects the real ELCR byte without altering another input's mode.</summary>
    private static void Level(Rig rig, int irq)
    {
        ushort port = irq < 8 ? (ushort)0x4d0 : (ushort)0x4d1;
        rig.Bridge.Write(port, rig.Bridge.Read(port, 1) | (uint)(1 << (irq & 7)), 1);
    }

    /// <summary>Programs only one actual MBIRQ route byte with an independently chosen sharing bit.</summary>
    private static void Route(Rig rig, int pin, int irq, bool share = false) =>
        rig.Bridge.WriteConfiguration(0x70 + pin, (uint)(irq | (share ? 0x40 : 0)), 1);

    /// <summary>Confirms that the PIC pair accepted the actual expected physical request and vector.</summary>
    private static void Ack(Rig rig, int irq)
    {
        byte expected = (byte)(irq < 8 ? 0x20 + irq : 0x28 + irq - 8);
        int before = rig.Acks.Count;
        Check(rig.Pic.HasPending && rig.Pic.Acknowledge() == expected, "Wrong physical MIRQ acknowledgement.");
        Check(rig.Acks.Count == before + 1 && rig.Acks[^1] == (irq, expected, false));
    }

    /// <summary>Returns from the actual slave and master in-service state in the documented order.</summary>
    private static void Eoi(Rig rig, int irq)
    { if (irq >= 8) rig.Pic.Write(0xa0, 0x20, 1); rig.Pic.Write(0x20, 0x20, 1); }

    /// <summary>Prepares a private component block mutation for semantic imported-state checks.</summary>
    private static StateBlock Edit<T>(StateBlock original, string name, T value)
    {
        var fields = new Dictionary<string, JsonElement>(original.Fields, StringComparer.Ordinal)
        { [name] = JsonSerializer.SerializeToElement(value, StateValues.Json) };
        return original with { Fields = fields };
    }

    /// <summary>Registers exact route masks, source retention and real ACK/EOI observations with bounded import failures.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PIIX MIRQ capability is constructor-fixed and the old one-argument ABI remains disconnected", () =>
        {
            var pic = new Pic8259(true); var old = new Intel82371FbIrq(pic);
            Check(!old.SupportsMirq && old.MirqAssertions == 0);
            string before = JsonSerializer.Serialize(old.SaveState(), StateValues.Json);
            Invalid(() => old.SetMirq(0, true)); Invalid(() => old.SetMirq(1, false));
            Check(JsonSerializer.Serialize(old.SaveState(), StateValues.Json) == before && pic.InputLines == 0);
            var enabled = new Rig(); Check(enabled.Bridge.SupportsMirq && enabled.Bridge.MirqAssertions == 0);
        });
        foreach (int pin in new[] { -1, 2, 3, int.MinValue, int.MaxValue }) foreach (bool capability in new[] { false, true })
            test($"M4 PIIX MIRQ rejects invalid pin before side effects pin={pin} capability={capability}", () =>
            {
                var rig = new Rig(capability); string before = State(rig);
                Invalid(() => rig.Bridge.SetMirq(pin, true)); Invalid(() => rig.Bridge.SetMirq(pin, false));
                Check(State(rig) == before && rig.Pic.InputLines == 0);
            });
        foreach (int pin in new[] { 0, 1 }) foreach (int irq in LegalIrqs) foreach (bool share in new[] { false, true })
            test($"M4 PIIX MIRQ{pin} routes to IRQ{irq} share={share} with real level ACK and EOI", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, pin, irq, share);
                rig.Bridge.SetMirq(pin, true);
                Check(rig.Bridge.MirqAssertions == 1 << pin && (rig.Pic.InputLines & (1 << irq)) != 0);
                Ack(rig, irq); Check(!rig.Pic.HasPending);
                Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetMirq(pin, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Acks.Count == 2 && rig.Bridge.MirqAssertions == 0);
                Check(rig.Bridge.HasPicBinding(rig.Pic));
            });
        foreach (int pin in new[] { 0, 1 }) foreach (int code in new[] { 0, 1, 2, 8, 13 })
            test($"M4 PIIX MIRQ{pin} reserved route nibble {code} is retained without masking an ISA pin", () =>
            {
                var rig = new Rig(); rig.Bridge.WriteConfiguration(0x70 + pin, (uint)(code | 0x30), 1);
                rig.Bridge.SetMirq(pin, true);
                Check(rig.Bridge.ReadConfiguration(0x70 + pin, 1) == code && !rig.Pic.HasPending && rig.Pic.InputLines == 0);
                rig.Bridge.SetIsaLine(5, true); Ack(rig, 5);
                rig.Bridge.SetIsaLine(5, false); Eoi(rig, 5); Check(!rig.Pic.HasPending);
            });
        foreach (int pin in new[] { 0, 1 }) foreach (int irq in LegalIrqs)
            test($"M4 PIIX MIRQ{pin} exclusive idle selection masks and later restores retained ISA IRQ{irq}", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, pin, irq);
                rig.Bridge.SetIsaLine(irq, true);
                Check(rig.Bridge.IsaLines == 1 << irq && (rig.Pic.InputLines & (1 << irq)) == 0 && !rig.Pic.HasPending);
                rig.Bridge.SetMirq(pin, true); Ack(rig, irq);
                rig.Bridge.SetMirq(pin, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Bridge.IsaLines == 1 << irq);
                rig.Bridge.WriteConfiguration(0x70 + pin, (uint)(0x80 | irq), 1);
                Ack(rig, irq); rig.Bridge.SetIsaLine(irq, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
            });
        foreach (int pin in new[] { 0, 1 }) foreach (int irq in new[] { 5, 10, 15 })
            test($"M4 PIIX MIRQ{pin} shared ISA IRQ{irq} OR retains either high source", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, pin, irq, true);
                rig.Bridge.SetIsaLine(irq, true); Ack(rig, irq);
                rig.Bridge.SetMirq(pin, true); rig.Bridge.SetIsaLine(irq, false); Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetIsaLine(irq, true); rig.Bridge.SetMirq(pin, false); Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetIsaLine(irq, false); Eoi(rig, irq);
                Check(!rig.Pic.HasPending && rig.Acks.Count == 3 && rig.Bridge.MirqAssertions == 0 && rig.Bridge.IsaLines == 0);
            });
        foreach (int irq in new[] { 3, 10, 15 }) foreach (bool firstShare in new[] { false, true }) foreach (bool secondShare in new[] { false, true })
            test($"M4 PIIX two MIRQs IRQ{irq} OR independently share0={firstShare} share1={secondShare}", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, 0, irq, firstShare); Route(rig, 1, irq, secondShare);
                rig.Bridge.SetMirq(0, true); rig.Bridge.SetMirq(1, true); Ack(rig, irq);
                rig.Bridge.SetMirq(0, false); Eoi(rig, irq); Ack(rig, irq);
                rig.Bridge.SetMirq(1, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
                rig.Bridge.SetIsaLine(irq, true);
                Check(((rig.Pic.InputLines & (1 << irq)) != 0) == (firstShare && secondShare),
                    "An exclusive MIRQ route was incorrectly overridden by another sharing bit.");
            });
        foreach (int irq in new[] { 5, 10, 15 }) foreach (int pin in new[] { 0, 1 })
            test($"M4 PIIX MIRQ{pin} sharing misprogrammed beside PIRQ retains PIRQ ISA exclusion IRQ{irq}", () =>
            {
                var rig = new Rig(); Level(rig, irq); Route(rig, pin, irq, true);
                rig.Bridge.WriteConfiguration(0x60, (uint)irq, 1); rig.Bridge.SetIsaLine(irq, true);
                Check((rig.Pic.InputLines & (1 << irq)) == 0 && !rig.Pic.HasPending);
                rig.Bridge.SetMirq(pin, true); Ack(rig, irq);
                rig.Bridge.SetMirq(pin, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
                rig.Bridge.SetPirq(0, true); Ack(rig, irq);
                rig.Bridge.SetPirq(0, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
                rig.Bridge.WriteConfiguration(0x60, 0x80, 1);
                Ack(rig, irq); rig.Bridge.SetIsaLine(irq, false); Eoi(rig, irq); Check(!rig.Pic.HasPending);
            });
        foreach (int pin in new[] { 0, 1 })
            test("M4 PIIX MIRQ disabled routes retain high peers without reset or synthesized events pin=" + pin, () =>
            {
                var rig = new Rig(); Level(rig, 10); rig.Bridge.SetMirq(pin, true);
                Check(rig.Bridge.MirqAssertions == 1 << pin && rig.Pic.InputLines == 0);
                Route(rig, pin, 10); Ack(rig, 10);
                rig.Bridge.WriteConfiguration(0x70 + pin, 0x8a, 1); Eoi(rig, 10); Check(!rig.Pic.HasPending);
                Check(rig.Bridge.MirqAssertions == 1 << pin);
                Route(rig, pin, 10); Ack(rig, 10);
                rig.Bridge.SetMirq(pin, false); Eoi(rig, 10); Check(!rig.Pic.HasPending);
            });
        test("M4 PIIX MIRQ primary ISA14 and secondary MIRQ0 remain independent physical inputs", () =>
        {
            var rig = new Rig(); Level(rig, 14); Level(rig, 15); Route(rig, 0, 15);
            rig.Bridge.SetIsaLine(14, true); rig.Bridge.SetMirq(0, true);
            Check(rig.Bridge.IsaLines == 0x4000 && rig.Bridge.MirqAssertions == 1);
            Ack(rig, 14); rig.Bridge.SetIsaLine(14, false); Eoi(rig, 14);
            Ack(rig, 15); rig.Bridge.SetMirq(0, false); Eoi(rig, 15);
            Check(!rig.Pic.HasPending && rig.Acks.Count == 2 && rig.Bridge.HasPicBinding(rig.Pic));
        });
        foreach (int value in new[] { 0, 0x0a, 0x4a, 0x80, 0xcf, 0xff })
            test("M4 PIIX old board keeps MBIRQ writes inert value=" + value, () =>
            {
                var rig = new Rig(false); Level(rig, 10);
                rig.Bridge.SetIsaLine(10, true); Ack(rig, 10);
                rig.Bridge.WriteConfiguration(0x70, (uint)value | ((uint)value << 8), 2);
                Check(rig.Bridge.ReadConfiguration(0x70, 2) == (uint)((value & 0xcf) * 0x101));
                Check((rig.Pic.InputLines & (1 << 10)) != 0 && rig.Bridge.MirqAssertions == 0);
                Eoi(rig, 10); Ack(rig, 10); rig.Bridge.SetIsaLine(10, false); Eoi(rig, 10); Check(!rig.Pic.HasPending);
                byte[] configuration = Configuration(rig.Bridge);
                Check(Intel82371FbIrq.ComputePicInputs(configuration, 1 << 10, 0) == 1 << 10);
                Check(Intel82371FbIrq.ComputePicInputs(configuration, 1 << 10, 0, false, 3) == 1 << 10);
            });
        test("M4 PIIX MIRQ repeated reads and same-level calls do not alter any PIC or bridge field", () =>
        {
            var rig = new Rig(); Level(rig, 10); Route(rig, 0, 10); rig.Bridge.SetMirq(0, true); Ack(rig, 10);
            string before = State(rig);
            for (int i = 0; i < 32; i++)
            {
                _ = Configuration(rig.Bridge); _ = rig.Bridge.Read(0x4d0, 1); _ = rig.Bridge.Read(0x4d1, 1);
                rig.Bridge.SetMirq(0, true); rig.Bridge.SetMirq(1, false);
                Check(rig.Bridge.HasPicBinding(rig.Pic));
            }
            Check(State(rig) == before);
            rig.Bridge.SetMirq(0, false); Eoi(rig, 10); Check(!rig.Pic.HasPending);
        });
        test("M4 PIIX MIRQ load preserves capability and peer levels without republishing a PIC edge", () =>
        {
            var source = new Rig(); Level(source, 10); Route(source, 0, 10);
            source.Bridge.SetMirq(0, true); Ack(source, 10); source.Bridge.SetMirq(1, true);
            var target = new Rig(); string picBefore = JsonSerializer.Serialize(target.Pic.SaveState(), StateValues.Json);
            string childrenBefore = JsonSerializer.Serialize(target.Pic.StateChildren.Select(v => v.Value.SaveState()).ToArray(), StateValues.Json);
            target.Bridge.LoadState(source.Bridge.SaveState());
            Check(target.Bridge.MirqAssertions == 3 && target.Bridge.SupportsMirq && target.Pic.InputLines == 0);
            Check(!target.Bridge.HasPicBinding(target.Pic));
            Check(JsonSerializer.Serialize(target.Pic.SaveState(), StateValues.Json) == picBefore);
            Check(JsonSerializer.Serialize(target.Pic.StateChildren.Select(v => v.Value.SaveState()).ToArray(), StateValues.Json) == childrenBefore);
            target.Pic.LoadState(source.Pic.SaveState());
            var childStates = source.Pic.StateChildren.ToDictionary(v => v.Name, v => v.Value.SaveState(), StringComparer.Ordinal);
            foreach (var child in target.Pic.StateChildren) child.Value.LoadState(childStates[child.Name]);
            Check(State(target) == State(source) && target.Bridge.HasPicBinding(target.Pic));
            source.Bridge.SetMirq(0, false); target.Bridge.SetMirq(0, false); Eoi(source, 10); Eoi(target, 10);
            Check(State(target) == State(source) && !target.Pic.HasPending && target.Bridge.MirqAssertions == 2);
        });
        foreach (byte invalid in new byte[] { 4, 8, 0x80, 0xff })
            test("M4 PIIX MIRQ import rejects out-of-range assertion bits " + invalid, () =>
            {
                var rig = new Rig(); var state = Edit(rig.Bridge.SaveState(), "_mirqAssertions", invalid);
                Invalid(() => rig.Bridge.LoadState(state)); Check(rig.Pic.InputLines == 0 && !rig.Pic.HasPending);
            });
        test("M4 PIIX MIRQ import rejects capability substitution and active input on the old board", () =>
        {
            var capable = new Rig(); var old = new Rig(false);
            Invalid(() => capable.Bridge.LoadState(Edit(capable.Bridge.SaveState(), "_supportsMirq", false)));
            Invalid(() => old.Bridge.LoadState(capable.Bridge.SaveState()));
            Invalid(() => old.Bridge.LoadState(Edit(old.Bridge.SaveState(), "_mirqAssertions", (byte)1)));
            Check(old.Pic.InputLines == 0 && capable.Pic.InputLines == 0);
        });
        test("M4 PIIX MIRQ pure route calculation validates complete configuration size", () =>
        {
            foreach (int count in new[] { 0, 255, 257 })
            {
                Invalid(() => Intel82371FbIrq.ComputePicInputs(new byte[count], 0, 0));
                Invalid(() => Intel82371FbIrq.ComputePicInputs(new byte[count], 0, 0, true, 3));
            }
        });
    }
}

