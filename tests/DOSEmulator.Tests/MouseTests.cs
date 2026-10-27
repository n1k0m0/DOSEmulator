// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using System.Text.Json;
using DOSEmulator.Contracts;

internal static class MouseTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "Mouse assertion failed") { if (!condition) throw new Exception(message); }
    /// <summary>Executes only the continuations explicitly queued by this controlled test context.</summary>
    private static byte[] Drain(Keyboard8042 k)
    {
        var bytes = new List<byte>(); while (k.OutputByteCount > 0) bytes.Add((byte)k.Read(0x60, 1)); return bytes.ToArray();
    }
    /// <summary>Submits the requested operation through the component's normal command interface.</summary>
    private static void Command(Keyboard8042 k, params byte[] values)
    { foreach (byte b in values) { k.Write(0x64, 0xd4, 1); k.Write(0x60, b, 1); } }
    /// <summary>Builds an isolated CPU/bus fixture with caller-supplied guest instruction bytes.</summary>
    private static (DeviceScheduler Time, Ps2Mouse Mouse, Keyboard8042 Kbc) Rig()
    {
        var time = new DeviceScheduler(); var mouse = new Ps2Mouse(time);
        var kbc = new Keyboard8042(new(1048576), _ => { }, mouse); kbc.Write(0x64, 0xa8, 1); return (time, mouse, kbc);
    }
    /// <summary>Builds an isolated machine fixture with the specified guest code and optional identity paging.</summary>
    private static PcMachine Machine(bool mouse = true)
    {
        var m = PcMachine.CreateDemo(new() { Mouse = mouse ? "ps2-two-button" : "none" });
        m.RunSteps(100000); Check(m.Fault is null); return m;
    }
    /// <summary>Executes the controlled guest service/instruction path used by the surrounding regression.</summary>
    private static byte Call(PcMachine m, byte al, ushort bx = 0)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteWord(0x1000, 0xf4fa);
        m.Cpu.SetRegister(Register.Ax, (uint)(0xc200 | al)); m.Cpu.SetRegister(Register.Bx, bx);
        m.Cpu.EnterRealModeInterrupt(0x15); m.RunSteps(500000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "BIOS did not return");
        byte status = (byte)(m.Cpu.GetRegister(Register.Ax) >> 8);
        Check(((m.Cpu.Eflags & X86Cpu.Carry) != 0) == (status != 0), "BIOS carry/status mismatch"); return status;
    }
    /// <summary>Registers the named mouse regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("PS2 reset defaults and ID-00 acknowledgements use Aux output", () =>
        {
            var (_, mouse, k) = Rig(); Command(k, 0xff);
            Check(k.Read(0x64, 1) == 0x25); Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0xaa, 0 }));
            Check(!mouse.Enabled && !mouse.Remote && mouse.Resolution == 2 && mouse.SampleRate == 100);
            Command(k, 0xf2); Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0 }));
        });
        test("KBC FIFO routes each source to IRQ1 or IRQ12 and reasserts successive bytes", () =>
        {
            bool one = false, twelve = false; var mouse = new Ps2Mouse(new());
            var k = new Keyboard8042(new(1048576), x => one = x, mouse, x => twelve = x);
            k.Write(0x64, 0x60, 1); k.Write(0x60, 0x47, 1); k.InjectTranslatedSet1(0x1e); Command(k, 0xf2);
            Check(one && !twelve && k.Read(0x64, 1) == 5); Check(k.Read(0x60, 1) == 0x1e);
            Check(!one && twelve && k.Read(0x64, 1) == 0x25); Check(k.Read(0x60, 1) == 0xfa);
            Check(twelve); Check(k.Read(0x60, 1) == 0 && !twelve && k.Read(0x64, 1) == 4);
        });
        test("PS2 sample clock is virtual and emits changed movement and button-only packets", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xf4); Drain(k); mouse.Inject(200, -5, 1);
            time.AdvanceTo(9_999_999); Check(k.OutputByteCount == 0); time.AdvanceTo(10_000_000);
            Check(Drain(k).SequenceEqual(new byte[] { 0x29, 200, 251 })); time.AdvanceTo(20_000_000); Check(k.OutputByteCount == 0);
            mouse.Inject(0, 0, 0); time.AdvanceTo(30_000_000); Check(Drain(k).SequenceEqual(new byte[] { 8, 0, 0 }));
            Check(mouse.PacketCount == 2);
        });
        test("PS2 clock inhibit retains movement and IRQ mask does not consume packets", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xf4); Drain(k); k.Write(0x64, 0xa7, 1);
            mouse.Inject(-2, 3, 2); time.AdvanceTo(20_000_000); Check(k.OutputByteCount == 0);
            k.Write(0x64, 0xa8, 1); time.AdvanceTo(30_000_000); Check(Drain(k).SequenceEqual(new byte[] { 0x1a, 254, 3 }));
        });
        test("PS2 remote read-data reports one unscaled packet without spontaneous stream", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xe7, 0xf0); Drain(k); mouse.Inject(4, -5, 3);
            time.AdvanceTo(1_000_000_000); Check(k.OutputByteCount == 0); Command(k, 0xeb);
            Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0x2b, 4, 251 })); Command(k, 0xeb);
            Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0x0b, 0, 0 }));
        });
        test("PS2 rate and resolution reject invalid parameters and accept retry", () =>
        {
            var (_, mouse, k) = Rig(); Command(k, 0xf3, 99, 200, 0xe8, 4, 3);
            Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0xfe, 0xfa, 0xfa, 0xfe, 0xfa }));
            Check(mouse.SampleRate == 200 && mouse.Resolution == 3); Command(k, 0xe9);
            Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0, 3, 200 }));
        });
        test("PS2 two-to-one scaling follows small-count table and signed overflow flags", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xe7, 0xf4); Drain(k);
            int[] magnitudes = [0, 1, 1, 3, 6, 9, 12];
            for (int i = 1; i <= 6; i++) { mouse.Inject(i, -i, 0); time.AdvanceTo(time.Now + 10_000_000); Check(Drain(k).SequenceEqual(new byte[] { 0x28, (byte)magnitudes[i], (byte)(256 - magnitudes[i]) })); }
            Command(k, 0xe6); Drain(k); mouse.Inject(256, -256, 0); time.AdvanceTo(time.Now + 10_000_000);
            Check(Drain(k).SequenceEqual(new byte[] { 0xe8, 255, 1 }));
        });
        test("PS2 full KBC applies whole-packet backpressure without dropping pending motion", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xf4); Drain(k);
            for (int i = 0; i < 63; i++) k.InjectTranslatedSet1(0x1e);
            mouse.Inject(7, 8, 0); time.AdvanceTo(10_000_000); Check(k.OutputByteCount == 63 && mouse.PacketCount == 0);
            Drain(k); time.AdvanceTo(20_000_000); Check(Drain(k).SequenceEqual(new byte[] { 8, 7, 8 }));
        });
        test("PS2 wrap echo exits without losing configuration and resend repeats reply", () =>
        {
            var (_, _, k) = Rig(); Command(k, 0xee); Drain(k); Command(k, 0xf2); Check(Drain(k).SequenceEqual(new byte[] { 0xf2 }));
            Command(k, 0xec, 0xf2); Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0xfa, 0 }));
            Command(k, 0xfe); Check(Drain(k).SequenceEqual(new byte[] { 0xfa, 0 }));
        });
        test("PS2 sixty-Hz sample clock carries fractional nanoseconds without drift", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xf3, 60, 0xf4); Drain(k);
            for (int i = 1; i <= 60; i++)
            {
                mouse.Inject(1, 0, 0); time.AdvanceTo((ulong)i * 1_000_000_000 / 60);
                Check(mouse.PacketCount == i && Drain(k).SequenceEqual(new byte[] { 8, 1, 0 }));
            }
            Check(time.Now == 1_000_000_000);
        });
        test("PS2 disable and defaults stop events and discard prior movement", () =>
        {
            var (time, mouse, k) = Rig(); Command(k, 0xf4); Drain(k); mouse.Inject(6, 4, 0);
            Command(k, 0xf5); Drain(k); time.AdvanceTo(20_000_000); Check(k.OutputByteCount == 0);
            Command(k, 0xf4); Drain(k); time.AdvanceTo(30_000_000); Check(k.OutputByteCount == 0);
            Command(k, 0xe8, 3, 0xf3, 200, 0xe7, 0xf6); Drain(k);
            Check(!mouse.Enabled && !mouse.Remote && !mouse.Scaling2To1 && mouse.Resolution == 2 && mouse.SampleRate == 100);
        });
        test("BIOS PS2 absence is honest and installed profile advertises pointing equipment", () =>
        {
            using var absent = Machine(false); Check(Call(absent, 5, 0x300) == 0x86 && (absent.Memory.ReadWord(0x410) & 4) == 0);
            using var present = Machine(); Check((present.Memory.ReadWord(0x410) & 4) != 0 && Call(present, 5, 0x300) == 0);
            Check(Call(present, 0, 0x100) == 5 && !present.Mouse!.Enabled);
            Check(Call(present, 5, 0x400) == 2 && Call(present, 8) == 1);
        });
        test("BIOS PS2 rate resolution identify reset and status execute real Aux I/O", () =>
        {
            using var m = Machine(); Check(Call(m, 5, 0x300) == 0); Check(Call(m, 2, 0x600) == 0 && m.Mouse!.SampleRate == 200);
            Check(Call(m, 2, 0x700) == 2); Check(Call(m, 3, 0x300) == 0 && m.Mouse!.Resolution == 3);
            Check(Call(m, 6, 0x200) == 0 && m.Mouse!.Scaling2To1); Check(Call(m, 6) == 0);
            Check((m.Cpu.GetRegister(Register.Bx) & 255) == 0x10 && (m.Cpu.GetRegister(Register.Cx) & 255) == 3 && (m.Cpu.GetRegister(Register.Dx) & 255) == 200);
            Check(Call(m, 4) == 0 && (m.Cpu.GetRegister(Register.Bx) >> 8) == 0);
            Check(Call(m, 1) == 0 && m.Cpu.GetRegister(Register.Bx) == 0xaa && m.Mouse!.SampleRate == 100 && m.Mouse.Resolution == 2);
        });
        foreach(bool relocated in new[]{false,true})
        test($"BIOS IRQ12 far callback preserves guest registers with {(relocated?"relocated":"initial")} EBDA", () =>
        {
            using var m = Machine();
            if(relocated){m.Memory.Ram.AsSpan(0x9fc00,1024).CopyTo(m.Memory.Ram.AsSpan(0xe920));m.Memory.Ram.AsSpan(0x9fc00,1024).Fill(0xa5);m.Memory.WriteWord(0x40e,0x0e92);}
            Check(Call(m, 5, 0x300) == 0);
            // Original far callback: copy status/X/Y/Z from its real stack to DS=0 test RAM.
            byte[] callback = [0x55,0x89,0xe5,0x50,0x1e,0x31,0xc0,0x8e,0xd8,
                0x8b,0x46,12,0xa3,0,0x30,0x8b,0x46,10,0xa3,2,0x30,0x8b,0x46,8,0xa3,4,0x30,
                0x8b,0x46,6,0xa3,6,0x30,0xff,0x06,8,0x30,0x1f,0x58,0x5d,0xcb];
            callback.CopyTo(m.Memory.Ram, 0x2000); m.Cpu.SetSegment(Segment.Es, 0); Check(Call(m, 7, 0x2000) == 0);
            Check(Call(m, 0, 0x100) == 0); Check((m.Pic.Read(0x21, 1) & 4) == 0 && (m.Pic.Read(0xa1, 1) & 0x10) == 0);
            m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; new byte[] { 0xfb, 0xf4, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000);
            m.Cpu.SetRegister(Register.Ax, 0x12345678); m.Cpu.SetSegment(Segment.Ds, 0x1234); uint sp = m.Cpu.GetRegister(Register.Sp);
            m.Cpu.EnterRealModeInterrupt(0x60); // leave the previous test's HLT through an actual IRET
            m.Mouse!.Inject(200, -5, 1); m.RunSteps(100000, m.Scheduler.Now + 20_000_000);
            Check(m.Fault is null, m.Fault ?? ""); Check(m.Memory.ReadWord(0x3008) == 1, $"Callback count {m.Memory.ReadWord(0x3008)}, packets {m.Mouse.PacketCount}, FIFO {m.Keyboard.OutputByteCount}, {m.Cpu.Position}");
            Check(m.Memory.ReadWord(0x3000) == 0x29 && m.Memory.ReadWord(0x3002) == 200 && m.Memory.ReadWord(0x3004) == 251 && m.Memory.ReadWord(0x3006) == 0, $"Callback words {Convert.ToHexString(m.Memory.Ram.AsSpan(0x3000, 8))}");
            Check(m.Cpu.GetRegister(Register.Ax) == 0x12345678 && m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetRegister(Register.Sp) == sp, $"Registers AX={m.Cpu.GetRegister(Register.Ax):X} DS={m.Cpu.GetSegment(Segment.Ds):X} SP={m.Cpu.GetRegister(Register.Sp):X}, expected {sp:X}");
            Check(Call(m, 0) == 0 && !m.Mouse.Enabled && (m.Pic.Read(0xa1, 1) & 0x10) != 0);
            if(relocated)Check(m.Memory.Ram.AsSpan(0x9fc00,1024).ToArray().All(b=>b==0xa5));
        });
        test("BIOS Aux commands preserve pending keyboard characters and ignore stale IRQ1", () =>
        {
            using var m = Machine(); m.Keyboard.InjectTranslatedSet1(0x1e); Check(Call(m, 5, 0x300) == 0);
            Check(m.Memory.ReadWord(0x41e) == 0x1e61 && m.Keyboard.OutputByteCount == 0);
            Check(Call(m, 4) == 0 && m.Keyboard.OutputByteCount == 0);
        });
        test("native relative input keeps fractions Y direction buttons and release boundaries", () =>
        {
            var input = new RelativeMouseInput(); Check(input.Move(1, 2) is null && input.Button(1, true) is null);
            input.Capture(); Check(input.Move(0.4, 0.2) is null); Check(input.Move(0.7, 0.9) == new RelativeMouseInput.Input(1, -1, 0));
            Check(input.Button(1, true) == new RelativeMouseInput.Input(0, 0, 1)); Check(input.Button(2, true)!.Buttons == 3);
            Check(input.Button(1, false)!.Buttons == 2 && input.Release().Buttons == 0 && !input.Captured);
            Check(input.Move(100, 100) is null); input.Capture(); Check(input.Move(double.NaN, 1) is null && input.Move(0.9, 0) is null);
        });
        test("mouse IPC validates counts buttons and absent hardware before injection", () =>
        {
            using var session = new MachineSession(Machine());
            // Sends the controlled test data through the real device-facing transfer interface.
            bool Send(object input)
            {
                var result = session.Enqueue(new(1, "mouse", "SendMouseInput", JsonSerializer.SerializeToElement(input)));
                session.PumpCommands(); return result.GetAwaiter().GetResult().Success;
            }
            Check(Send(new { x = 4, y = -3, buttons = 1 }));
            Check(!Send(new { x = 32768, y = 0, buttons = 0 }) && !Send(new { x = 0, y = 0, buttons = 4 }));
            using var none = new MachineSession(Machine(false));
            var absent = none.Enqueue(new(1, "mouse", "SendMouseInput", JsonSerializer.SerializeToElement(new { x = 0, y = 0, buttons = 0 })));
            none.PumpCommands(); Check(!absent.Result.Success);
        });
        test("GUI record payloads reach real PS2 packets using the canonical IPC naming policy", () =>
        {
            using var session = new MachineSession(Machine());
            var m = session.Machine;
            m.Keyboard.Write(0x64,0xa8,1); m.Mouse!.Write(0xf4); Drain(m.Keyboard);
            var input = new RelativeMouseInput(); input.Capture();
            foreach (var eventData in new[] { input.Button(1,true)!,input.Move(40,-25)! })
            {
                var request = IpcProtocol.CreateRequest("SendMouseInput",eventData);
                Check(request.Payload.GetProperty("x").GetInt32()==eventData.X);
                var response=session.Enqueue(request); session.PumpCommands(); Check(response.Result.Success);
            }
            m.Scheduler.AdvanceTo(m.Scheduler.Now+10000000);
            Check(Drain(m.Keyboard).AsSpan().SequenceEqual(new byte[] {9,40,25}));
            var release=session.Enqueue(IpcProtocol.CreateRequest("SendMouseInput",input.Release()));
            session.PumpCommands(); Check(release.Result.Success); m.Scheduler.AdvanceTo(m.Scheduler.Now+10000000);
            Check(Drain(m.Keyboard).AsSpan().SequenceEqual(new byte[] {8,0,0}));
        });
        test("mouse scenarios use guest debug markers in graphics and validate bounded input", () =>
        {
            using var m = Machine(); m.Mouse!.Write(0xf4); Drain(m.Keyboard);
            var scenario = new GuestTestScenario([new(WaitForDebug: "GRAPHICS", Mouse: new(1, -2, 1), SettleNanoseconds: 0)]);
            scenario.Pump(m); Check(!scenario.Complete); foreach (char c in "GRAPHICS") m.Ports.Write(0xe9, (byte)c, 1);
            scenario.Pump(m); Check(scenario.Complete && scenario.MouseInputsSent == 1);
            bool rejected = false;
            try { _ = new GuestTestScenario([new("READY", Mouse: new(0, 0, 4))]); } catch (ArgumentException) { rejected = true; }
            Check(rejected);
            rejected = false;
            try { _ = new GuestTestScenario([new("READY", WaitForDebug: "ALSO")]); } catch (ArgumentException) { rejected = true; }
            Check(rejected);
        });
    }
}
