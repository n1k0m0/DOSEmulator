// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Independent pin/EOI/priority/cascade and raw-state oracles for the explicitly capable PIC pair.</summary>
internal static class PiixPicTests
{
    /// <summary>Requires a concrete vector, register, pin or complete explicit-state result.</summary>
    private static void Check(bool value, string why = "Optional PIC level contract failed.") { if (!value) throw new Exception(why); }
    /// <summary>Initializes both actual command/data interfaces with distinct conventional protected-mode vectors.</summary>
    private static Pic8259 Pic(bool capable = true, bool masterAuto = false, bool slaveAuto = false)
    {
        var p = new Pic8259(capable);
        p.Write(0x20, 0x11, 1); p.Write(0xa0, 0x11, 1); p.Write(0x21, 0x20, 1); p.Write(0xa1, 0x28, 1);
        p.Write(0x21, 4, 1); p.Write(0xa1, 2, 1); p.Write(0x21, masterAuto ? 3u : 1, 1); p.Write(0xa1, slaveAuto ? 3u : 1, 1);
        p.Write(0x21, 0, 1); p.Write(0xa1, 0, 1); return p;
    }
    /// <summary>Reads a selected hardware IRR or ISR through the ordinary OCW3 protocol.</summary>
    private static byte Register(Pic8259 p, bool slave, bool isr)
    { ushort port = slave ? (ushort)0xa0 : (ushort)0x20; p.Write(port, isr ? 0x0bu : 0x0a, 1); return (byte)p.Read(port, 1); }
    /// <summary>Delivers the ordinary nonspecific EOI to one controller.</summary>
    private static void Eoi(Pic8259 p, bool slave = false) => p.Write(slave ? (ushort)0xa0 : (ushort)0x20, 0x20, 1);
    /// <summary>Computes the expected remapped vector from the physical IRQ number rather than PIC private state.</summary>
    private static byte Vector(int irq) => (byte)(irq < 8 ? 0x20 + irq : 0x28 + irq - 8);
    /// <summary>Captures every parent and child field without calling a mutating device API.</summary>
    private static Dictionary<string, StateBlock> Capture(Pic8259 p)
    {
        var state = new Dictionary<string, StateBlock> { ["pic"] = p.SaveState() };
        foreach (var (name, child) in p.StateChildren) state[name] = child.SaveState(); return state;
    }
    /// <summary>Compares the complete explicit pair state, including cache, pins and initialization progress.</summary>
    private static string State(Pic8259 p) => JsonSerializer.Serialize(Capture(p), StateValues.Json);
    /// <summary>Restores the pair using the same parent-before-children order as the production state graph.</summary>
    private static Pic8259 Restore(Pic8259 p)
    {
        var state = Capture(p); var restored = new Pic8259(p.SupportsLevelInputs); restored.LoadState(state["pic"]);
        foreach (var (name, child) in restored.StateChildren) child.LoadState(state[name]);
        Check(State(p) == State(restored), "Raw PIC state changed during restore."); return restored;
    }
    /// <summary>Requires exact atomic API rejection.</summary>
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    /// <summary>Registers narrow capable-PIC cases without claiming a full PIIX board or broad 8259 silicon precision.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("PIIX PIC original parameterless ABI and explicit legacy constructor retain edges", () =>
        {
            Check(typeof(Pic8259).GetConstructor(Type.EmptyTypes) is not null);
            var original = new Pic8259(); Check(!original.SupportsLevelInputs && original.LevelMask == 0 && original.InputLines == 0);
            Reject<InvalidOperationException>(() => original.SetLevelMask(0)); Reject<InvalidOperationException>(() => original.SetLevelMask(8));
            foreach (var p in new[] { Pic(false), Pic() })
            {
                p.SetLine(3, true); Check(p.Acknowledge() == 0x23); Eoi(p); p.SetLine(3, true); Check(!p.HasPending);
                p.SetLine(3, false); p.SetLine(3, true); p.SetLine(3, false); Check(p.HasPending && p.Acknowledge() == 0x23); Eoi(p); Check(!p.HasPending);
            }
        });
        foreach (int fixedIrq in new[] { 0, 1, 2, 8, 13 })
            test("PIIX PIC fixed edge bit rejects atomically IRQ" + fixedIrq, () =>
            {
                var p = Pic(); p.SetLevelMask(0x0808); p.SetLine(3, true); string before = State(p);
                Reject<ArgumentOutOfRangeException>(() => p.SetLevelMask((ushort)(0x0808 | 1 << fixedIrq)));
                Check(State(p) == before && p.LevelMask == 0x0808 && p.HasPending);
            });
        test("PIIX PIC accepts exactly physical IRQ mask DEF8 without byte reversal", () =>
        {
            var p = Pic(); p.SetLevelMask(0xdef8); Check(p.SupportsLevelInputs && p.LevelMask == 0xdef8);
            Reject<ArgumentOutOfRangeException>(() => p.SetLevelMask(0xf8de)); Check(p.LevelMask == 0xdef8);
        });
        foreach (int irq in new[] { 3, 4, 5, 6, 7, 9, 10, 11, 12, 14, 15 })
        foreach (bool masterAuto in new[] { false, true }) foreach (bool slaveAuto in new[] { false, true }) foreach (bool masterFirst in new[] { false, true })
            test($"PIIX PIC held level IRQ{irq} masterAuto={masterAuto} slaveAuto={slaveAuto} masterFirst={masterFirst}", () =>
            {
                var p = Pic(masterAuto: masterAuto, slaveAuto: slaveAuto); p.SetLevelMask((ushort)(1 << irq)); var observed = new List<(int Irq, byte Vector, bool Spurious)>();
                p.InterruptAcknowledged += (physical, vector, spurious) => observed.Add((physical, vector, spurious)); p.SetLine(irq, true);
                for (int i = 0; i < 3; i++)
                {
                    Check(p.HasPending && p.Acknowledge() == Vector(irq));
                    Check(Register(p, irq >= 8, true) == (irq < 8 ? masterAuto ? 0 : 1 << irq : slaveAuto ? 0 : 1 << (irq - 8)));
                    if (irq >= 8) Check(Register(p, false, true) == (masterAuto ? 0 : 4));
                    Check(p.HasPending == (irq < 8 ? masterAuto : masterAuto && slaveAuto));
                    if (irq < 8) Eoi(p);
                    else if (masterFirst) { Eoi(p); Eoi(p, true); } else { Eoi(p, true); Eoi(p); }
                    Check(p.HasPending, "A held level vanished at EOI/cascade publication.");
                }
                p.SetLine(irq, false);
                if (irq >= 8)
                {
                    Check(p.Acknowledge() == 0x2f, "A latched cascade edge must remain a spurious slave acknowledge.");
                    Check(Register(p, true, true) == 0 && Register(p, false, true) == (masterAuto ? 0 : 4)); Eoi(p);
                }
                Check(!p.HasPending && p.Acknowledge() is null);
                Check(observed.Take(3).All(row => row == (irq, Vector(irq), false)));
                Check(irq < 8 ? observed.Count == 3 : observed.Count == 4 && observed[^1] == (15, (byte)0x2f, true));
            });
        foreach (int irq in new[] { 3, 11 })
            test("PIIX PIC deassert clears masked level IRR but preserves latched edge IRQ" + irq, () =>
            {
                var level = Pic(); level.SetLevelMask((ushort)(1 << irq)); level.Write(irq < 8 ? (ushort)0x21 : (ushort)0xa1, 255, 1);
                level.SetLine(irq, true); Check((Register(level, irq >= 8, false) & (1 << (irq & 7))) != 0 && !level.HasPending);
                level.SetLine(irq, false); Check((Register(level, irq >= 8, false) & (1 << (irq & 7))) == 0);
                level.Write(irq < 8 ? (ushort)0x21 : (ushort)0xa1, 0, 1); Check(!level.HasPending);
                var edge = Pic(); edge.Write(irq < 8 ? (ushort)0x21 : (ushort)0xa1, 255, 1); edge.SetLine(irq, true); edge.SetLine(irq, false);
                Check((Register(edge, irq >= 8, false) & (1 << (irq & 7))) != 0); edge.Write(irq < 8 ? (ushort)0x21 : (ushort)0xa1, 0, 1); Check(edge.Acknowledge() == Vector(irq));
            });
        foreach (int irq in new[] { 3, 11 })
            test("PIIX PIC low during ISR never clears service IRQ" + irq, () =>
            {
                var p = Pic(); p.SetLevelMask((ushort)(1 << irq)); p.SetLine(irq, true); Check(p.Acknowledge() == Vector(irq)); p.SetLine(irq, false);
                Check(Register(p, irq >= 8, true) == 1 << (irq & 7) && !p.HasPending);
                if (irq >= 8) Eoi(p, true); Eoi(p); Check(!p.HasPending);
            });
        test("PIIX PIC fixed priorities and specific EOI retain lower active ISR", () =>
        {
            var p = Pic(); p.SetLevelMask(0x28); p.SetLine(5, true); Check(p.Acknowledge() == 0x25); p.SetLine(3, true); Check(p.Acknowledge() == 0x23);
            Check(Register(p, false, true) == 0x28 && !p.HasPending); p.SetLine(3, false); p.Write(0x20, 0x63, 1);
            Check(Register(p, false, true) == 0x20 && !p.HasPending); p.Write(0x20, 0x65, 1); Check(p.HasPending && p.Acknowledge() == 0x25);
            p.SetLine(5, false); Eoi(p); Check(!p.HasPending);
        });
        foreach (bool service in new[] { false, true }) foreach (bool high in new[] { false, true })
            test($"PIIX PIC mode switch samples pins without new edge service={service} high={high}", () =>
            {
                var p = Pic(); p.SetLine(3, true); if (service) Check(p.Acknowledge() == 0x23); if (!high) p.SetLine(3, false);
                p.SetLevelMask(8); Check(p.LevelMask == 8 && ((p.InputLines & 8) != 0) == high);
                Check((Register(p, false, false) & 8) == (high && !service ? 8 : 0));
                p.SetLevelMask(0); if (service) Eoi(p);
                Check(p.HasPending == (high && !service));
                if (p.HasPending) { Check(p.Acknowledge() == 0x23); Eoi(p); }
                p.SetLine(3, high); Check(!p.HasPending); p.SetLine(3, false); p.SetLine(3, true); Check(p.Acknowledge() == 0x23);
            });
        test("PIIX PIC capable ICW1 retains physical high level and waits for complete ICWs", () =>
        {
            var p = Pic(); p.SetLevelMask(8); p.SetLine(3, true); p.Write(0x20, 0x11, 1);
            Check(p.LevelMask == 8 && (p.InputLines & 8) != 0 && !p.HasPending && p.Acknowledge() is null);
            p.Write(0x21, 0x30, 1); Check(!p.HasPending); p.Write(0x21, 4, 1); Check(!p.HasPending); p.Write(0x21, 1, 1); Check(p.Acknowledge() == 0x33);
        });
        test("PIIX PIC legacy ICW1 retains original pin-detector reset", () =>
        {
            var p = Pic(false); p.SetLine(3, true); p.Write(0x20, 0x11, 1); Check((p.InputLines & 8) == 0);
            p.Write(0x21, 0x20, 1); p.Write(0x21, 4, 1); p.Write(0x21, 1, 1); p.SetLine(3, true); Check(p.Acknowledge() == 0x23);
        });
        test("PIIX PIC slave init gates fresh requests and master-only init republishes held cascade", () =>
        {
            var p = Pic(); p.SetLevelMask(0x800); p.Write(0xa0, 0x11, 1); p.SetLine(11, true); Check(!p.HasPending);
            p.Write(0xa1, 0x28, 1); Check(!p.HasPending); p.Write(0xa1, 2, 1); Check(!p.HasPending); p.Write(0xa1, 1, 1); Check(p.HasPending);
            p.Write(0x20, 0x11, 1); Check(!p.HasPending && (p.InputLines & 0x804) == 0x804);
            p.Write(0x21, 0x20, 1); p.Write(0x21, 4, 1); p.Write(0x21, 1, 1); Check(p.Acknowledge() == 0x2b);
        });
        test("PIIX PIC enabling level samples an already consumed high edge without a new callback", () =>
        {
            var p = Pic(); p.SetLine(3, true); Check(p.Acknowledge() == 0x23); Eoi(p); Check(!p.HasPending && (p.InputLines & 8) != 0);
            p.SetLevelMask(8); Check(p.HasPending && p.Acknowledge() == 0x23);
            Check(Register(p, false, false) == 0 && Register(p, false, true) == 8);
            string before = State(p); p.SetLevelMask(8); Check(State(p) == before, "Identical level mask republished an in-service request.");
            Eoi(p); Check(p.HasPending && p.Acknowledge() == 0x23);
        });
        test("PIIX PIC real level reassert during service queues IRR and low removes only that request", () =>
        {
            var p = Pic(); p.SetLevelMask(8); p.SetLine(3, true); Check(p.Acknowledge() == 0x23);
            p.SetLine(3, false); p.SetLine(3, true); Check(Register(p, false, false) == 8 && Register(p, false, true) == 8 && !p.HasPending);
            p.SetLine(3, false); Check(Register(p, false, false) == 0 && Register(p, false, true) == 8); Eoi(p); Check(!p.HasPending);
        });
        test("PIIX PIC cascade priority blocks lower master level until both service states complete", () =>
        {
            var p = Pic(); p.SetLevelMask(0x808); p.SetLine(3, true); p.SetLine(11, true); Check(p.Acknowledge() == 0x2b);
            p.SetLine(1, true); p.SetLine(1, false); Check(p.Acknowledge() == 0x21 && Register(p, false, true) == 6 && !p.HasPending);
            Eoi(p); Check(Register(p, false, true) == 4 && !p.HasPending); p.SetLine(11, false); Eoi(p, true); Check(!p.HasPending);
            Eoi(p); Check(p.Acknowledge() == 0x23); p.SetLine(3, false); Eoi(p); Check(!p.HasPending);
        });
        test("PIIX PIC capable init preserves high edge pins without manufacturing a fresh request", () =>
        {
            var p = Pic(); p.SetLine(3, true); Check(p.Acknowledge() == 0x23); Eoi(p);
            p.Write(0x20, 0x11, 1); Check((p.InputLines & 8) != 0); p.Write(0x21, 0x20, 1); p.Write(0x21, 4, 1); p.Write(0x21, 1, 1);
            Check(!p.HasPending); p.SetLine(3, true); Check(!p.HasPending); p.SetLine(3, false); p.SetLine(3, true); Check(p.Acknowledge() == 0x23);
        });
        foreach (string point in new[] { "edge-low", "masked", "pending", "service", "reassert-service", "auto", "master-init", "spurious", "transition" })
            test("PIIX PIC raw parent/children continuation " + point, () =>
            {
                var p = Pic(masterAuto: point == "auto", slaveAuto: point == "auto"); p.SetLevelMask(point == "edge-low" ? (ushort)0 : (ushort)0x808);
                if (point == "edge-low") { p.SetLine(1, true); p.SetLine(1, false); }
                else if (point == "master-init") { p.SetLine(3, true); p.Write(0x20, 0x11, 1); }
                else
                {
                    if (point == "masked") p.Write(0xa1, 255, 1); p.SetLine(11, true);
                    if (point is "service" or "reassert-service" or "auto" or "transition") Check(p.Acknowledge() == 0x2b);
                    if (point == "reassert-service") { p.SetLine(11, false); p.SetLine(11, true); }
                    if (point == "spurious") p.SetLine(11, false);
                    if (point == "transition") p.SetLevelMask(8);
                }
                var r = Restore(p); var first = new List<(int, byte, bool)>(); var second = new List<(int, byte, bool)>();
                p.InterruptAcknowledged += (irq, vector, spurious) => first.Add((irq, vector, spurious)); r.InterruptAcknowledged += (irq, vector, spurious) => second.Add((irq, vector, spurious));
                if (point == "master-init") foreach (var x in new[] { (0x21, 0x20), (0x21, 4), (0x21, 1) }) { p.Write((ushort)x.Item1, (uint)x.Item2, 1); r.Write((ushort)x.Item1, (uint)x.Item2, 1); Check(State(p) == State(r)); }
                for (int i = 0; i < 4; i++)
                {
                    Check(p.HasPending == r.HasPending && p.Acknowledge() == r.Acknowledge()); Eoi(p, true); Eoi(r, true); Eoi(p); Eoi(r);
                    p.Write(0xa1, 0, 1); r.Write(0xa1, 0, 1); Check(State(p) == State(r));
                }
                p.SetLine(11, false); r.SetLine(11, false); p.SetLine(3, true); r.SetLine(3, true); Check(State(p) == State(r));
                Check(first.SequenceEqual(second), "Future physical IRQ/vector/spurious publications differ.");
            });
        foreach (int irq in new[] { 3, 11 })
            test("PIIX PIC actual protected CPU OUT EOI republishes held IRQ" + irq, () =>
            {
                var p = Pic(); p.SetLevelMask((ushort)(1 << irq)); p.SetLine(irq, true); Check(p.Acknowledge() == Vector(irq));
                var ports = new IoPortBus(); ports.Map(0x20, 2, p); ports.Map(0xa0, 2, p);
                byte command = irq < 8 ? (byte)0x20 : (byte)0xa0;
                byte[] code = [0xb0,0x20,0xe6,command, ..(irq < 8 ? Array.Empty<byte>() : new byte[] { 0xe6,0x20 }),
                    0xb0,0x0a,0xe6,command,0xe4,command,0xa2,0,0,3,0,0xf4];
                var (cpu, memory) = ProtectedModeTests.Rig(code, io: ports); for (int i = 0; i < 20 && !cpu.Halted; i++) cpu.Step();
                Check(cpu.Halted && !cpu.TripleFaulted && memory.ReadByte(0x30000) == 1 << (irq & 7) && p.HasPending);
            });
    }
}
