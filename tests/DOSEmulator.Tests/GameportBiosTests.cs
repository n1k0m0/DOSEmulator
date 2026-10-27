// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual INT15 firmware, independent RC arithmetic and observed-port measurement oracles.</summary>
internal static class GameportBiosTests
{
    /// <summary>Requires a specific return, pin or isolation invariant.</summary>
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    /// <summary>Boots the real firmware and runs one INT15 using a private stack with guards.</summary>
    private static PcMachine Invoke(bool installed, ushort subfunction, GameportInput input,
        out List<(ulong Time, uint Value, int Width, bool Write)> ports)
    {
        var m = PcMachine.CreateDemo(new() { Gameport = installed ? AnalogGameport.ProfileId : "none" });
        m.RunSteps(200000); Check(m.Fault is null && m.Cpu.Halted, "Own firmware boot");
        m.Gameport?.SetInput(input);
        var observations = new List<(ulong, uint, int, bool)>(); ports = observations;
        m.Ports.Accessed += (p, v, w, wr) => { if (AnalogGameport.Decodes(p)) observations.Add((m.Scheduler.Now, v, w, wr)); };
        m.Memory.WriteByte(0x2000, 0xf4);
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetSegment(Segment.Ds, 0x1234); m.Cpu.SetSegment(Segment.Es, 0x4321);
        m.Cpu.Eip = 0x2000; m.Cpu.SetRegister(Register.Sp, 0x9000);
        m.Cpu.SetRegister(Register.Ax, 0xa5a5845a); m.Cpu.SetRegister(Register.Bx, 0xb6b61234);
        m.Cpu.SetRegister(Register.Cx, 0xc7c75678); m.Cpu.SetRegister(Register.Dx, 0xd8d80000u | subfunction);
        m.Cpu.SetRegister(Register.Si, 0x11223344); m.Cpu.SetRegister(Register.Di, 0x55667788); m.Cpu.SetRegister(Register.Bp, 0x99aabbcc);
        m.Memory.WriteDword(0x8f00, 0x89abcdef); m.Memory.WriteDword(0x9000, 0x76543210);
        m.Cpu.Eflags = 0x647; // IF/DF and ordinary flags survive; only CF is a service result.
        m.Cpu.EnterRealModeInterrupt(0x15);
        for (int step = 0; step < 120000 && !(m.Cpu.Halted && m.Cpu.Eip == 0x2001 && m.Cpu.GetSegment(Segment.Cs) == 0); step++) m.RunSteps(1);
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Eip == 0x2001, "Actual firmware return to HLT");
        Check(m.Cpu.GetRegister(Register.Sp) == 0x9000 && m.Cpu.GetRegister(Register.Si) == 0x11223344 &&
            m.Cpu.GetRegister(Register.Di) == 0x55667788 && m.Cpu.GetRegister(Register.Bp) == 0x99aabbcc, "Stack and nonresult GPRs");
        Check(m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetSegment(Segment.Es) == 0x4321 &&
            (m.Cpu.Eflags & ~1u) == 0x646, "Segments and flags");
        Check(m.Memory.ReadDword(0x8f00) == 0x89abcdef && m.Memory.ReadDword(0x9000) == 0x76543210, "Stack guards");
        return m;
    }

    /// <summary>Registers switch, all-axis/open-axis, bounded absence and argument rejection contracts.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int buttons = 0; buttons < 16; buttons++)
        {
            int value = buttons;
            test($"Gameport BIOS switches {value:X} preserve high nibble and do not trigger", () =>
            {
                using var m = Invoke(true, 0, new() { Buttons = value }, out var ports);
                Check((m.Cpu.GetRegister(Register.Ax) & 0xffff) == (uint)((~value & 15) << 4) && (m.Cpu.Eflags & 1) == 0, "IBM switch layout");
                Check(ports.Count == 1 && !ports[0].Write && ports[0].Width == 1 && m.Gameport!.TriggerCount == 0, "Exactly one actual byte read");
                Check(m.Cpu.GetRegister(Register.Bx) == 0xb6b61234 && m.Cpu.GetRegister(Register.Cx) == 0xc7c75678 && m.Cpu.GetRegister(Register.Dx) == 0xd8d80000, "Switch-only results");
            });
        }
        foreach (int?[] axes in new int?[][] { [0,0,0,0], [100000,100000,100000,100000], [0,25000,50000,100000],
            [100000,50000,25000,0], [null,0,100000,null], [0,null,50000,100000], [null,null,null,null] })
        {
            string name = string.Join(',', axes.Select(x => x?.ToString() ?? "open"));
            test("Gameport BIOS resistance counts and bounded open axes " + name, () =>
            {
                using var m = Invoke(true, 1, new() { AxOhms = axes[0], AyOhms = axes[1], BxOhms = axes[2], ByOhms = axes[3], Buttons = 10 }, out var ports);
                Check(ports.Count(p => p.Write) == 1 && ports[0].Write && ports.All(p => p.Width == 1), "One actual trigger, only byte I/O");
                ulong trigger = ports[0].Time; int[] counts = new int[4]; uint last = 15; int reads = 0;
                foreach (var p in ports.Where(p => !p.Write))
                {
                    uint expected = 0x50;
                    for (int i = 0; i < 4; i++) if (axes[i] is null || p.Time - trigger < 24200UL + (ulong)axes[i]!.Value * 11) expected |= 1u << i;
                    Check(p.Value == expected, "Independent nominal RC transition at actual IN time");
                    last = p.Value & 15; reads++;
                    for (int i = 0; i < 4; i++) if ((last & (1u << i)) != 0) counts[i]++;
                }
                Check(reads > 0 && reads <= 4096 && ports[^1].Time - trigger < 12000000UL, "Bounded real poll loop");
                Register[] results = [Register.Ax, Register.Bx, Register.Cx, Register.Dx];
                for (int i = 0; i < 4; i++) Check((m.Cpu.GetRegister(results[i]) & 0xffff) == ((last & (1u << i)) != 0 ? 65535u : (uint)counts[i]), "Independent complete result count");
                Check((m.Cpu.Eflags & 1) == 0 && m.Gameport!.TriggerCount == 1, "Valid call even with open axes");
                Check((m.Cpu.GetRegister(Register.Ax) >> 16) == 0xa5a5 && (m.Cpu.GetRegister(Register.Bx) >> 16) == 0xb6b6 &&
                    (m.Cpu.GetRegister(Register.Cx) >> 16) == 0xc7c7 && (m.Cpu.GetRegister(Register.Dx) >> 16) == 0xd8d8, "High GPR halves preserved");
            });
        }
        foreach (bool installed in new[] { false, true }) foreach (ushort function in new ushort[] { 0,1,2,0x100,0xffff })
        {
            if (installed && function < 2) continue;
            test($"Gameport BIOS rejects absence or subfunction installed={installed} dx={function:X4} before I/O", () =>
            {
                using var m = Invoke(installed, function, new(), out var ports);
                Check(ports.Count == 0 && (m.Cpu.GetRegister(Register.Ax) & 0xffff) == 0x865a && (m.Cpu.Eflags & 1) != 0, "AH86/CF without guessed inputs");
                Check(m.Cpu.GetRegister(Register.Bx) == 0xb6b61234 && m.Cpu.GetRegister(Register.Cx) == 0xc7c75678 &&
                    m.Cpu.GetRegister(Register.Dx) == (0xd8d80000u | function), "Rejected call preserves outputs");
            });
        }
    }
}
