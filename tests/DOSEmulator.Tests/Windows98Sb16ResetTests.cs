// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Reflection;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using Rig = Windows95Sb16AbsentAspTests.Rig;

/// <summary>Original DSP4.05 cold_boot bytes and bounded F9 reset RAM reads through actual CPU IN/OUT, including strict whole-PC futures.</summary>
internal static class Windows98Sb16ResetTests
{
    /// <summary>Rejects an incorrect port response, real DMA effect, reset domain or full-state future.</summary>
    private static void Check(bool value, string reason = "DSP4.05 reset-readback contract failed.")
    { if (!value) throw new InvalidOperationException(reason); }

    /// <summary>Checks exactly one expected ready byte, then empty status; FF is never confused with an empty port.</summary>
    private static void Query(Rig r, byte command, byte address, byte expected)
    { r.Send(command, address); r.Expect([expected]); }

    /// <summary>Fixed independent cold_boot/command-handler values and the actual Win98 driver's six-address table.</summary>
    private static void SelfTest(Rig r)
    {
        long unsupported = r.Card.UnsupportedCommands;
        Query(r, 0x0f, 9, 0xf8);
        Query(r, 0xf9, 0x0e, 0xff); Query(r, 0xf9, 0x0f, 7);
        foreach (byte address in new byte[] { 0x0b, 0x23, 0x24, 0x2b, 0x2c, 0x2d }) Query(r, 0xf9, address, 0);
        Query(r, 0xf9, 0x37, 0x38);
        Check(r.Card.UnsupportedCommands == unsupported && !r.Card.Active && r.Card.Dma8Grants == 0 &&
            r.Card.Dma16Grants == 0 && r.Card.InterruptSources == 0 && r.Card.TransferredSamples == 0);
    }

    /// <summary>Compares all graph fields, scheduler data and every RAM byte without normalizing diagnostics.</summary>
    private static void Equal(PcMachine first, PcMachine second)
    {
        Check(JsonSerializer.Serialize(first.CaptureHardwareState(), StateValues.Json) ==
            JsonSerializer.Serialize(second.CaptureHardwareState(), StateValues.Json), "Whole hardware/scheduler state differs.");
        Check(first.Memory.Ram.AsSpan().SequenceEqual(second.Memory.Ram), "Whole RAM differs.");
    }

    /// <summary>Calls the fixed shared capture/import validator on one own malformed graph, without publishing or adopting it.</summary>
    private static void RejectState(object state, PcMachine machine)
    {
        var type = typeof(PcMachine).Assembly.GetType("DOSEmulator.Runtime.SnapshotValidation", true)!;
        try
        {
            type.GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state]);
            type.GetMethod("CheckProfile", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [state, machine.Profile, machine.Memory.Ram.Length]);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Incoherent reset RAM state passed the shared capture/import validator.");
    }

    /// <summary>Builds a complete own board with a new tiny private disk; no original or user OS media is used.</summary>
    private static PcMachine Machine() => PcMachine.CreateWithDisks(new MachineProfile
    {
        Id = MachineProfile.Windows95ProfileId, Name = "Own DSP reset RAM continuation",
        Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId,
        SoundCard = SoundBlaster16.ProfileId
    }, [new MemoryDisk(new byte[128 * 512], "Own reset-readback disk"), null, null, null]);

    /// <summary>Checks incomplete F9/XBUS packets, a queued reply and an invalidated image through the strict public snapshot store.</summary>
    private static void Future(string cut)
    {
        string parent = Path.GetFullPath("artifacts/tests"), root = Path.Combine(parent, "win98-sb16-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = Machine(); var a = new Rig(machine: first); a.Reset();
            if (cut == "parameter") a.Send(0xf9);
            else if (cut == "queued") a.Send(0xf9, 0x37);
            else if (cut == "command-deadline") a.Write(0xc, 0xf9);
            else if (cut == "xbus-write") a.Send(0x0e, 9);
            else a.Send(0x40, 0x80);
            string saved = Path.Combine(root, "cut.dossav"); SnapshotStore.Capture(first, saved);
            using var second = SnapshotStore.Load(saved, Path.Combine(root, "restored.dosebranch"));
            var b = new Rig(machine: second, configure: false); Equal(first, second);
            if (cut == "command-deadline") { a.Advance(1000); b.Advance(1000); Equal(first, second); }
            if (cut is "parameter" or "command-deadline") { a.Send(0x37); b.Send(0x37); }
            if (cut is "parameter" or "command-deadline" or "queued") { a.Expect([0x38]); b.Expect([0x38]); }
            else if (cut == "xbus-write")
            {
                a.Send(0xe3); b.Send(0xe3); Query(a, 0x0f, 9, 0xe3); Query(b, 0x0f, 9, 0xe3);
            }
            else
            {
                a.Send(0xf9, 0x23); b.Send(0xf9, 0x23); a.Expect([]); b.Expect([]);
                Check(a.Card.UnsupportedCommands == 1 && b.Card.UnsupportedCommands == 1);
            }
            Equal(first, second); a.Reset(); b.Reset(); SelfTest(a); SelfTest(b); Equal(first, second);
        }
        finally
        {
            Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win98-sb16-reset-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }

    /// <summary>Registers bounded reset probes, meaningful state changes, packet framing and complete snapshot futures.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var pair in new (byte Address, byte Value)[] { (0x0b, 0), (0x0e, 0xff), (0x0f, 7), (0x23, 0),
            (0x24, 0), (0x2b, 0), (0x2c, 0), (0x2d, 0), (0x37, 0x38) })
            test("M4 Win98 SB16 reset RAM byte " + pair.Address.ToString("X2"), () =>
            { var r = new Rig(); r.Reset(); Query(r, 0xf9, pair.Address, pair.Value); Check(r.Card.UnsupportedCommands == 0); });
        test("M4 Win98 SB16 original complete reset self-test sequence", () => { var r = new Rig(); r.Reset(); SelfTest(r); });
        foreach (byte value in new byte[] { 0, 0x5a, 0xe3, 0xff })
            test("M4 Win98 SB16 opaque XBUS09 whole-byte read/write " + value.ToString("X2"), () =>
            { var r = new Rig(); r.Reset(); r.Send(0x0e, 9, value); Query(r, 0x0f, 9, value); Check(r.Card.UnsupportedCommands == 0); });
        foreach (byte[] command in new byte[][] { [0xd1], [0x40, 0x80], [0x48, 0x34, 0x12], [0x14, 0, 0] })
            test("M4 Win98 SB16 mutable command ends reset-only RAM scope " + command[0].ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); r.Send(command); long before = r.Card.UnsupportedCommands;
                r.Send(0xf9, 0x23); r.Expect([]); Check(r.Card.UnsupportedCommands == before + 1);
                r.Reset(); Query(r, 0xf9, 0x23, 0);
            });
        foreach (byte address in new byte[] { 0, 0xe1 })
            test("M4 Win98 SB16 F9 unsupported address remains one consumed parameter " + address.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); r.Send(0xf9, address); r.Expect([]); Check(r.Card.UnsupportedCommands == 1);
                r.Send(0xe1); r.Expect([4, 5]);
            });
        test("M4 Win98 SB16 DSP reset restores09/F9 while retaining existing05 latch", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x0e, 5, 0xa5, 0x0e, 9, 0x12, 0xd1); r.Reset();
            Query(r, 0x0f, 5, 0xa5); SelfTest(r);
        });
        test("M4 Win98 SB16 mixer reset preserves09 and reset RAM", () =>
        { var r = new Rig(); r.Reset(); r.Send(0x0e, 9, 0x5a); r.Mixer(0, 0); Query(r, 0x0f, 9, 0x5a); Query(r, 0xf9, 0x37, 0x38); });
        test("M4 Win98 SB16 legacy default auto-init block really uses2048 components", () =>
        { var r = new Rig(); r.Reset(); r.Send(0x1c); Check(r.Card.Active && r.Card.RemainingSamples == 2048); });
        test("M4 Win98 SB16 PIIX reset restores both latches and reset image", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x0e, 5, 0xa5, 0x0e, 9, 0x12, 0xd1);
            r.Card.AssertPiixReset(5, 1, 5); r.Advance(3000); r.Card.ReleasePiixReset(); r.Advance(100000); r.Expect([0xaa]);
            Query(r, 0x0f, 5, 0); SelfTest(r);
        });
        foreach (string cut in new[] { "parameter", "queued", "command-deadline", "xbus-write", "invalidated" })
            test("M4 Win98 SB16 strict whole32MiB reset-readback future " + cut, () => Future(cut));
        foreach (string field in new[] { "_legacyLength", "InputRate", "OutputRate", "SpeakerEnabled", "_auto", "_word", "RemainingSamples" })
            test("M4 Win98 SB16 snapshot validation rejects inconsistent reset image " + field, () =>
            {
                using var m = Machine(); var r = new Rig(machine: m); r.Reset(); var state = m.CaptureHardwareState();
                state.Components["sb16"].Fields[field] = field is "SpeakerEnabled" or "_auto" or "_word"
                    ? JsonSerializer.SerializeToElement(true) : JsonSerializer.SerializeToElement(1);
                RejectState(state, m);
            });
        foreach (string field in new[] { "_xbus09Control", "_resetRamReadable" })
            test("M4 Win98 SB16 held-reset snapshot validation rejects wrong reset domain " + field, () =>
            {
                using var m = Machine(); var r = new Rig(machine: m); r.Reset(); r.Output(0xcf9, 6);
                Check(m.RunSteps(1, m.Scheduler.Now) == 0 && m.CpuResetHeld);
                var state = m.CaptureHardwareState(); state.Components["sb16"].Fields[field] = field == "_resetRamReadable"
                    ? JsonSerializer.SerializeToElement(false) : JsonSerializer.SerializeToElement(0);
                RejectState(state, m);
            });
    }
}
