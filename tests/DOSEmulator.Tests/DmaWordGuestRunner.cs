// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Headless DOS acceptance harness. The synthetic DMA card exists only in the test executable.</summary>
internal static class DmaWordGuestRunner
{
    private sealed record Options(string Disk, string? Overlay, string Floppy, string System, string Prefix);
    /// <summary>Executes the component's explicit runner with the supplied bounds and reports its resulting evidence.</summary>
    public static int Run(string path)
    {
        var options = JsonSerializer.Deserialize<Options>(File.ReadAllText(path), IpcProtocol.JsonOptions)
            ?? throw new ArgumentException("Empty DMA guest options.");
        foreach (string file in new[] { options.Disk, options.Overlay, options.Floppy, options.Prefix }.OfType<string>())
            if (Path.GetFullPath(file).Contains("doom2-play", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("User play media are excluded from acceptance.");
        using var machine = MachineFactory.FromImages(new MachineProfile(), options.Disk, options.Overlay, options.Floppy, bootDevice: "ide");
        var fixture = new DmaWordFixture(machine.Dma, machine.Scheduler);
        machine.Ports.Map(0x300, 16, fixture);
        bool free = options.System == "freedos";
        var steps = new List<GuestTestScenario.Step>();
        if (!free) steps.Add(new(WaitForScreen: "C:\\>", Text: "kezb us\r\n"));
        steps.Add(new(WaitForScreen: free ? "C:\\>" : "C:\\>keyb us", Text: "a:dma16\r\nver\r\necho DMA16-END\r\n"));
        var scenario = new GuestTestScenario(steps.ToArray());
        var accesses = new List<object>();
        var freeRequests = new List<object>();
        var vdsRequests = new List<object>();
        machine.Cpu.InterruptEntered += vector =>
        {
            if (vector == 0x4b)
                vdsRequests.Add(new { ax = machine.Cpu.GetRegister(Register.Ax) & 0xffff, bx = machine.Cpu.GetRegister(Register.Bx) & 0xffff,
                    dx = machine.Cpu.GetRegister(Register.Dx) & 0xffff, es = machine.Cpu.GetSegment(Segment.Es), di = machine.Cpu.GetRegister(Register.Di) & 0xffff });
            if (vector != 0x21 || (machine.Cpu.GetRegister(Register.Ax) & 0xff00) != 0x4900) return;
            ushort segment = machine.Cpu.GetSegment(Segment.Es);
            if (segment == 0) return;
            uint mcb = (uint)segment * 16 - 16;
            freeRequests.Add(new { segment, owner = machine.Memory.ReadWord(mcb + 1), paragraphs = machine.Memory.ReadWord(mcb + 3),
                marker = machine.Memory.ReadByte(mcb), time = machine.Scheduler.Now });
        };
        machine.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is >= 0xc0 and < 0xe0 or 0x89 or 0x8a or 0x8b or >= 0x300 and < 0x310)
                // Polling is deliberately omitted; the guest's actual register reads and all configuration writes remain.
                if (port != 0x308) accesses.Add(new { time = machine.Scheduler.Now, port, value, width, write });
        };
        machine.Running = true;
        long executed = 0;
        string stop = free ? "\r\nDMA16-END\r\nC:\\>" : "\r\nDMA16-END\r\n\r\nC:\\>";
        while (executed < 150_000_000 && machine.Fault is null)
        {
            scenario.Pump(machine);
            int ran = machine.RunSteps(10000); executed += ran;
            if (scenario.Complete && machine.Board.DebugText.Contains(stop, StringComparison.Ordinal)) break;
            if (ran == 0) break;
        }
        using (var file = new FileStream(options.Prefix + ".private-ram.bin", FileMode.CreateNew, FileAccess.Write)) file.Write(machine.Memory.Ram);
        var frame = machine.CaptureFrame();
        string frameHash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan())));
        var report = new
        {
            system = options.System, status = machine.GetStatus(), text = machine.Video.ReadText(), executed, frameHash,
            machine.HardwareResetCount, ataSectorsWritten = machine.Ata.WrittenSectorCount,
            cpu = new { machine.Cpu.ProtectedMode, machine.Cpu.Virtual8086, machine.Cpu.CurrentPrivilegeLevel, machine.Cpu.Cr0, machine.Cpu.Cr3,
                machine.Cpu.Virtual8086Entries, machine.Cpu.Virtual8086Exits, machine.Cpu.Virtual8086Instructions },
            scenario = new { scenario.Complete, scenario.CompletedSteps, scenario.InputBytesSent, scenario.InputBytesTotal },
            fixture = new { testOnly = true, periodNanoseconds = DmaWordFixture.Period, batches = fixture.Batches, beats = fixture.Beats },
            io = accesses, freeRequests, vdsRequests
        };
        using var output = new FileStream(options.Prefix + ".json", FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, report, new JsonSerializerOptions(IpcProtocol.JsonOptions) { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(new { passed = machine.Fault is null && scenario.Complete && machine.Board.DebugText.Contains(stop), options.Prefix, executed }));
        return machine.Fault is null && scenario.Complete && machine.Board.DebugText.Contains(stop) ? 0 : 1;
    }
}
