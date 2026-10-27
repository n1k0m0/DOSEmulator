// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class GuestScenarioTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition) { if (!condition) throw new Exception("Scenario assertion failed"); }
    /// <summary>Requires the supplied operation to throw the selected exception type.</summary>
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid scenario"); }
    /// <summary>Installs only the scenario fixture's controlled text screen before exercising its input policy.</summary>
    private static void Screen(PcMachine machine, string text)
    {
        for (int i = 0; i < 80; i++) machine.Memory.WriteByte((uint)(0xb8000 + i * 2), i < text.Length ? (byte)text[i] : (byte)' ');
    }
    /// <summary>Registers the named guest scenario regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("guest scenario gates actual KBC bytes on VGA text and controller backpressure", () =>
        {
            using var m = PcMachine.CreateDemo(); m.RunSteps(100000); long instructions = m.Cpu.Instructions;
            var scenario = new GuestTestScenario([new("READY", Text: "a", SettleNanoseconds: 0), new("NEXT", ScanCodes: [0x3d, 0xbd], SettleNanoseconds: 0)]);
            Screen(m, "WAIT"); scenario.Pump(m); Check(m.Keyboard.OutputByteCount == 0 && scenario.InputBytesSent == 0);
            Screen(m, "READY"); scenario.Pump(m); scenario.Pump(m);
            Check(scenario.InputBytesSent == 1 && m.Keyboard.Read(0x60, 1) == 0x1e);
            scenario.Pump(m); Check(scenario.CompletedSteps == 1 && m.Keyboard.Read(0x60, 1) == 0x9e);
            scenario.Pump(m); Check(scenario.InputBytesSent == 2);
            Screen(m, "NEXT"); scenario.Pump(m); Check(m.Keyboard.Read(0x60, 1) == 0x3d);
            scenario.Pump(m); Check(m.Keyboard.Read(0x60, 1) == 0xbd && scenario.Complete);
            scenario.Pump(m); Check(scenario.InputBytesSent == 4 && scenario.InputBytesTotal == 4 && m.Cpu.Instructions == instructions);
        });
        test("guest scenario defers media insertion until the FDC execution and result phases end", () =>
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "scenario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "next.img"); File.WriteAllBytes(path, new byte[1474560]);
            using var m = new PcMachine(new() { FloppyDriveA = "1440k" }, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "boot"), new MemoryDisk(new byte[1474560], "old"));
            m.RunSteps(100000); Screen(m, "INSERT DISK 2");
            var scenario = new GuestTestScenario([new("INSERT DISK 2", InsertFloppy: new(0, path), SettleNanoseconds: 0)]);
            m.Floppy.Write(0x3f2, 0x1c, 1);
            foreach (byte value in new byte[] { 0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff }) m.Floppy.Write(0x3f5, value, 1);
            scenario.Pump(m); Check(scenario.MediaChanges == 0 && m.Floppy.MediaName(0) == "old");
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 400_000_000); scenario.Pump(m); Check(scenario.MediaChanges == 0);
            for (int i = 0; i < 7; i++) m.Floppy.Read(0x3f5, 1);
            long instructions = m.Cpu.Instructions; scenario.Pump(m);
            Check(scenario.MediaChanges == 1 && scenario.Complete && m.Floppy.MediaName(0) == "next.img" && m.Cpu.Instructions == instructions);
            Check(m.Floppy.Read(0x3f7, 1) == 0x80);
        });
        test("guest scenario releases rejected media and retains the old floppy", () =>
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "scenario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "wrong.img"); File.WriteAllBytes(path, new byte[512]);
            using var m = new PcMachine(new() { FloppyDriveA = "1440k" }, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "boot"), new MemoryDisk(new byte[1474560], "old"));
            m.RunSteps(100000); Screen(m, "SWAP"); var scenario = new GuestTestScenario([new("SWAP", InsertFloppy: new(0, path), SettleNanoseconds: 0)]);
            Reject(() => scenario.Pump(m)); Check(m.Floppy.MediaName(0) == "old" && scenario.MediaChanges == 0 && !scenario.Complete);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        });
        test("guest scenario rejects ungated ambiguous and invalid inputs before any action", () =>
        {
            Reject(() => new GuestTestScenario([])); Reject(() => new GuestTestScenario([new("")]));
            Reject(() => new GuestTestScenario([new("READY", Text: "a", ScanCodes: [1])]));
            Reject(() => new GuestTestScenario([new("READY", ScanCodes: [256])]));
            Reject(() => new GuestTestScenario([new("READY", InsertFloppy: new(0, "relative.img"))]));
            Reject(() => new GuestTestScenario([new("READY", InsertFloppy: new(2, Path.GetFullPath("fixture.img")))]));
        });
        test("guest scenario JSON accepts integer scancodes and owns its validated input", () =>
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "scenario-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "plan.json"); File.WriteAllText(path, "[{\"waitForScreen\":\"READY\",\"scanCodes\":[61,189]}]");
            var loaded = GuestTestScenario.Load(path); Check(loaded.TotalSteps == 1 && loaded.InputBytesTotal == 2);
            int[] bytes = [0x1e]; var scenario = new GuestTestScenario([new("READY", ScanCodes: bytes, SettleNanoseconds: 0)]); bytes[0] = 0x3d;
            using var m = PcMachine.CreateDemo(); m.RunSteps(100000); Screen(m, "READY"); scenario.Pump(m);
            Check(m.Keyboard.Read(0x60, 1) == 0x1e && scenario.Complete);
        });
        test("guest scenario settles the whole screen in virtual time before typing into an installer", () =>
        {
            using var m = PcMachine.CreateDemo(); m.RunSteps(100000);
            var scenario = new GuestTestScenario([new("READY", Text: "\r")]);
            Screen(m, "READY / DRAWING"); scenario.Pump(m);
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 90_000_000); scenario.Pump(m); Check(scenario.InputBytesSent == 0);
            Screen(m, "READY / FINISHED"); scenario.Pump(m);
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 99_999_999); scenario.Pump(m); Check(scenario.InputBytesSent == 0);
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 1); scenario.Pump(m); Check(m.Keyboard.Read(0x60, 1) == 0x1c);
        });
    }
}
