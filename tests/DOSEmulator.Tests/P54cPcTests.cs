// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Production P54C-PC clock ownership, real instruction effects and complete local snapshot boundaries.</summary>
internal static class P54cPcTests
{
    /// <summary>Creates only the explicitly limited own AT validation profile, without implying a historical PCI board.</summary>
    private static MachineProfile Profile() => new()
    {
        Id = MachineProfile.P54cProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = MachineProfile.P54cCpuHz, Name = "P54C test hardware"
    };

    /// <summary>Requires a concrete register, memory, deadline or production-loader result.</summary>
    private static void Check(bool value, string detail = "P54C PC contract failed.")
    { if (!value) throw new Exception(detail); }

    /// <summary>Places actual real-mode guest bytes without replacing the CPU or adding an unregistered device.</summary>
    private static void Code(PcMachine m, byte[] code, uint address = 0x1000)
    {
        Check(!m.Cpu.Halted, "A halted CPU cannot be silently reactivated by the test harness.");
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0);
        m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0);
        m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = address; code.CopyTo(m.Memory.Ram, address);
    }

    /// <summary>Executes one real guest instruction under the PC owner and its normal cycle conversion.</summary>
    private static void One(PcMachine m, byte[] code, uint address = 0x1000)
    { Code(m, code, address); Check(m.RunSteps(1) == 1 && m.Fault is null, m.Fault ?? "Instruction did not run."); }

    /// <summary>Combines the actual architectural timestamp result registers.</summary>
    private static ulong Result(PcMachine m) => (ulong)m.Cpu.GetRegister(Register.Dx) << 32 | m.Cpu.GetRegister(Register.Ax);

    /// <summary>Writes the full virtual TSC using the privileged architectural interface, not a state edit.</summary>
    private static void WriteTsc(PcMachine m, ulong value)
    {
        m.Cpu.SetRegister(Register.Cx, 0x10); m.Cpu.SetRegister(Register.Ax, (uint)value);
        m.Cpu.SetRegister(Register.Dx, (uint)(value >> 32)); One(m, [0x0f, 0x30]);
    }

    /// <summary>Encodes the entire explicit CPU/device/scheduler graph for comparison.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);

    /// <summary>Reads the immutable own firmware through the actual physical bus, without reading private guest media.</summary>
    private static byte[] Rom(PcMachine m) => Enumerable.Range(0, 65536).Select(i => m.Memory.ReadByte(0xf0000u + (uint)i)).ToArray();

    /// <summary>Checks every RAM byte and every explicit state field, including future device operations.</summary>
    private static void Same(PcMachine first, PcMachine second)
    {
        Check(State(first) == State(second), "Complete hardware state differs.");
        Check(first.Memory.Ram.SequenceEqual(second.Memory.Ram), "Whole physical RAM differs.");
        Check(first.Cpu.TimestampCounter == second.Cpu.TimestampCounter, "Rebound virtual clock differs.");
    }

    /// <summary>Advances only the actual bound scheduler, preserving the mixer's unfinished integration.</summary>
    private static void Advance(PcMachine m, ulong delta)
    { m.Scheduler.AdvanceTo(checked(m.Scheduler.Now + delta)); m.Audio.AdvanceTo(m.Scheduler.Now); }

    /// <summary>Edits a private test document while updating its enclosing integrity hash.</summary>
    private static void EditState(string directory, Action<JsonObject> edit)
    {
        string path = Path.Combine(directory, "state.json"); var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(document);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(directory, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }

    /// <summary>Rehashes deliberately malformed profile metadata so validation cannot rely on a checksum mismatch alone.</summary>
    private static void EditProfile(string directory, Action<JsonObject> edit)
    {
        string path = Path.Combine(directory, "media.dosemedia", "media.json"); var media = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        edit(media["machine"]!["hardware"]!.AsObject());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(media, IpcProtocol.JsonOptions); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(directory, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }

    /// <summary>Pumps one real owner command instead of calling a private import helper.</summary>
    private static IpcResponse Request(MachineSession session, string command, object payload)
    { var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return pending.GetAwaiter().GetResult(); }

    /// <summary>Registers narrowly justified complete-PC tests without asserting native input or Windows compatibility.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 P54C PC factory owns the selected model and exact nominal clock", () =>
        {
            using var machine = PcMachine.CreateDemo(Profile());
            Check(machine.Cpu.Model == X86CpuModel.IntelP54cB5Subset && machine.Cpu.HasTimeStampCounter);
            Check(machine.Cpu.TimestampFrequency == 90_000_000 && machine.Cpu.GetRegister(Register.Dx) == 0x524);
            Check(machine.Cpu.TimestampCounter == 0 && machine.Scheduler.Now == 0);
            machine.Scheduler.AdvanceCycles(137, 90_000_000);
            Check(machine.Cpu.TimestampCounter == 137 && machine.Scheduler.Now == 1522);
            var cpu = machine.Cpu.SaveState();
            Check(cpu.Fields["Model"].GetInt32() == 1 && cpu.Fields["TimestampFrequency"].GetUInt32() == 90_000_000);
        });
        test("M4 P54C PC legacy constructor and default PC retain the 486 clock contract", () =>
        {
            using var machine = PcMachine.CreateDemo(); var legacy = new X86Cpu(new(1048576), new());
            Check(machine.Cpu.Model == X86CpuModel.Intel486Dx && !machine.Cpu.HasTimeStampCounter && machine.Cpu.TimestampFrequency == 0);
            Check(legacy.Model == X86CpuModel.Intel486Dx && legacy.GetRegister(Register.Dx) == 0x400);
            machine.Scheduler.AdvanceCycles(17, 33_000_000); Check(machine.Cpu.TimestampCounter == 0);
        });
        test("M4 P54C PC actual firmware POST identifies P54C in E9 and VGA", () =>
        {
            using var machine = new PcMachine(Profile(), null); machine.RunSteps(100000);
            string text = machine.Video.ReadText(), mirror = machine.Board.DebugText;
            Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "POST did not halt at the absent boot target.");
            foreach (string output in new[] { text, mirror })
            {
                Check(output.Contains("P54C / AT", StringComparison.Ordinal) && output.Contains("Configured CPU: P54C B5 / DOSE AT subset", StringComparison.Ordinal), "Actual POST lacks the configured P54C inventory.");
                Check(!output.Contains("Configured CPU: 486DX", StringComparison.Ordinal) && !output.Contains("486 / AT", StringComparison.Ordinal), "Actual P54C POST still advertises the 486 configuration.");
            }
        });
        test("M4 P54C PC firmware patch changes only two original inventory capacities", () =>
        {
            using var legacy = new PcMachine(new(), null); using var selected = new PcMachine(Profile(), null);
            byte[] before = Rom(legacy), after = Rom(selected); var allowed = new bool[65536];
            foreach (string label in new[] { "  |  486 / AT   |   Original firmware - 64 KiB ROM", "Configured CPU: 486DX / AT development profile" })
            {
                byte[] old = System.Text.Encoding.ASCII.GetBytes(label); int position = before.AsSpan().IndexOf(old);
                Check(position >= 0 && before[position + old.Length] == 0, "Known original inventory capacity was not found.");
                Check(after[position + old.Length] == 0, "Inventory terminator moved.");
                for (int i = 0; i < old.Length; i++) allowed[position + i] = true;
            }
            int changed = 0;
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i]) { Check(allowed[i], "P54C patch changed a byte outside the two inventory capacities at " + i.ToString("X4")); changed++; }
            Check(changed > 0, "The P54C inventory patch did not change any ROM bytes.");
            Check(System.Text.Encoding.ASCII.GetString(after).Contains("Configured CPU: P54C B5 / DOSE AT subset", StringComparison.Ordinal));
        });
        test("M4 P54C PC legacy public BIOS API and actual 486 ROM retain the frozen 8870 identity", () =>
        {
            // Independently rendered from five hash-verified frozen own sources: image, BIOS builder, palette, font algorithm and original artwork.
            const string expected = "DCF095E737BFA441F45BF7260617E26BD8C9320DBCCF0D716B7C3529E63D199D";
            byte[] generated = DOSEBios.CreateBios(8, 1);
            Check(Convert.ToHexString(SHA256.HashData(generated)) == expected, "The legacy generated 486 ROM differs from the frozen 8870 source oracle.");
            using var machine = new PcMachine(new(), new MemoryDisk(new byte[512], "Own one-sector ROM fixture"));
            Check(Convert.ToHexString(SHA256.HashData(Rom(machine))) == expected, "The actual 486 ROM mapping differs from the unchanged public generator.");
            machine.RunSteps(100000); string text = machine.Video.ReadText();
            Check(machine.Fault is null && text.Contains("Configured CPU: 486DX / AT development profile", StringComparison.Ordinal), machine.Fault ?? "Legacy 486 POST inventory changed.");
        });
        test("M4 P54C PC constructor-fixed timestamp owner rejects alien clocks and frequencies", () =>
        {
            using var machine = PcMachine.CreateDemo(Profile()); var alien = new DeviceScheduler();
            Check(machine.Cpu.UsesTimestampClock(machine.Scheduler, 90_000_000));
            Check(!machine.Cpu.UsesTimestampClock(alien, 90_000_000) && !machine.Cpu.UsesTimestampClock(machine.Scheduler, 33_000_000));
            alien.AdvanceCycles(123, 90_000_000); Check(machine.Cpu.TimestampCounter == 0);
            machine.Scheduler.AdvanceCycles(23, 90_000_000); Check(machine.Cpu.TimestampCounter == 23);
            using var legacy = PcMachine.CreateDemo(); Check(!legacy.Cpu.UsesTimestampClock(legacy.Scheduler, 33_000_000));
        });
        test("M4 P54C PC clock samples and reset reject active callbacks before CPU mutation", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
            WriteTsc(m, 0x123456789abcdef0); string before = JsonSerializer.Serialize(m.Cpu.SaveState(), StateValues.Json);
            long resets = m.HardwareResetCount; bool sampleRejected = false, resetRejected = false;
            m.Scheduler.ScheduleAfter(1, () =>
            {
                try { _ = m.Cpu.TimestampCounter; } catch (InvalidOperationException) { sampleRejected = true; }
                try { m.HardwareReset(); } catch (InvalidOperationException) { resetRejected = true; }
            });
            Advance(m, 1);
            Check(sampleRejected && resetRejected && m.HardwareResetCount == resets);
            Check(before == JsonSerializer.Serialize(m.Cpu.SaveState(), StateValues.Json));
            Check(m.Cpu.TimestampCounter != 0 && m.Fault is null);
        });
        foreach (uint leaf in new uint[] { 0, 1 })
            test("M4 P54C PC actual CPUID advertises only implemented facilities leaf " + leaf, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
                m.Cpu.SetRegister(Register.Ax, leaf); One(m, [0x0f, 0xa2]);
                if (leaf == 0)
                    Check(m.Cpu.GetRegister(Register.Ax) == 1 && m.Cpu.GetRegister(Register.Bx) == 0x756e6547 &&
                        m.Cpu.GetRegister(Register.Dx) == 0x49656e69 && m.Cpu.GetRegister(Register.Cx) == 0x6c65746e);
                else Check(m.Cpu.GetRegister(Register.Ax) == 0x524 && m.Cpu.GetRegister(Register.Bx) == 0 &&
                    m.Cpu.GetRegister(Register.Cx) == 0 && m.Cpu.GetRegister(Register.Dx) == 0x135);
                Check(m.Cpu.TimestampCounter == 4 && m.Fault is null);
            });
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
            test("M4 P54C PC actual RDTSC preserves the scheduler phase prefix " + Convert.ToHexString(prefix), () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
                m.Scheduler.AdvanceCycles(4097, 90_000_000); ulong time = m.Scheduler.Now;
                m.Cpu.Eflags = 0x0cd7; m.Cpu.SetRegister(Register.Bx, 0x12345678); m.Cpu.SetRegister(Register.Cx, 0xdeadbeef);
                One(m, [.. prefix, 0x0f, 0x31]);
                Check(Result(m) == 4097 && m.Cpu.TimestampCounter == 4101 && m.Scheduler.Now >= time);
                Check(m.Cpu.Eflags == 0x0cd7 && m.Cpu.GetRegister(Register.Bx) == 0x12345678 && m.Cpu.GetRegister(Register.Cx) == 0xdeadbeef);
                Check(m.Cpu.Eip == 0x1002 + prefix.Length);
            });
        foreach (ulong value in new ulong[] { 0, 1, 0xfedcba9876543210, ulong.MaxValue - 1 })
            test("M4 P54C PC actual WRMSR10 retains every bit through full restoration value " + value, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
                m.Scheduler.AdvanceCycles(37, 90_000_000); WriteTsc(m, value);
                Check(m.Cpu.TimestampCounter == unchecked(value + 4));
                m.Cpu.SetRegister(Register.Ax, 12); One(m, [0x0f, 0x22, 0xe0]);
                Check(m.Cpu.Cr4 == 12 && m.Cpu.TimestampCounter == unchecked(value + 8));
                Code(m, [0xfa, 0xf4]); m.RunSteps(2); m.Running = false;
                Check(m.Cpu.Halted && m.Cpu.TimestampCounter == unchecked(value + 16));
                using var resumed = fixture.Restore(); Same(m, resumed);
                ulong before = m.Cpu.TimestampCounter; Advance(m, 12345); Advance(resumed, 12345); Same(m, resumed);
                Check(m.Cpu.TimestampCounter != before && m.Cpu.Model == resumed.Cpu.Model && resumed.Cpu.Cr4 == 12);
            });
        foreach (bool match in new[] { false, true })
            test("M4 P54C PC actual CMPXCHG8B reaches its own RAM and clock match " + match, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
                m.Memory.WriteDword(0x3000, 0x01234567); m.Memory.WriteDword(0x3004, 0x89abcdef);
                m.Cpu.SetRegister(Register.Ax, match ? 0x01234567u : 0x11234567u); m.Cpu.SetRegister(Register.Dx, 0x89abcdef);
                m.Cpu.SetRegister(Register.Bx, 0xfedcba98); m.Cpu.SetRegister(Register.Cx, 0x76543210);
                One(m, [0x0f, 0xc7, 0x0e, 0x00, 0x30]);
                Check(m.Memory.ReadDword(0x3000) == (match ? 0xfedcba98u : 0x01234567u));
                Check(m.Memory.ReadDword(0x3004) == (match ? 0x76543210u : 0x89abcdefu));
                Check(((m.Cpu.Eflags & 0x40) != 0) == match && m.Cpu.TimestampCounter == 4);
            });
        test("M4 P54C PC hardware reset reanchors TSC without resetting powered devices or RAM", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile() with { SerialPort = Uart16550.ProfileId }, false); var m = fixture.Machine;
            m.Serial!.Write(0x3fb, 0x80, 1); m.Serial.Write(0x3f8, 12, 1); m.Serial.Write(0x3f9, 0, 1);
            m.Serial.Write(0x3fb, 3, 1); m.Serial.Write(0x3f8, 0x5a, 1);
            WriteTsc(m, 0x76543210fedcba98); m.Cpu.SetRegister(Register.Ax, 12); One(m, [0x0f, 0x22, 0xe0]);
            m.Memory.WriteWord(0x472, 0x1234); m.Memory.Ram[0x80000] = 0xa7;
            var before = m.CaptureHardwareState(); string serial = JsonSerializer.Serialize(before.Components["serial"], StateValues.Json);
            string pit = JsonSerializer.Serialize(before.Components["pit"], StateValues.Json);
            ulong time = m.Scheduler.Now; ulong? next = m.Scheduler.NextEventTime; var cpu = m.Cpu;
            m.HardwareReset();
            Check(ReferenceEquals(cpu, m.Cpu) && m.Cpu.Model == X86CpuModel.IntelP54cB5Subset && m.Cpu.TimestampFrequency == 90_000_000);
            Check(m.Cpu.TimestampCounter == 0 && m.Cpu.Cr4 == 0 && m.Cpu.GetRegister(Register.Dx) == 0x524);
            Check(m.Scheduler.Now == time && m.Scheduler.NextEventTime == next && m.Memory.Ram[0x80000] == 0xa7);
            Check(m.HardwareResetCount == 1 && m.LastWarmBootFlag == 0x1234 && m.LastResetTime == time);
            var after = m.CaptureHardwareState();
            Check(serial == JsonSerializer.Serialize(after.Components["serial"], StateValues.Json) && pit == JsonSerializer.Serialize(after.Components["pit"], StateValues.Json));
            m.Scheduler.AdvanceCycles(29, 90_000_000); Check(m.Cpu.TimestampCounter == 29);
        });
        test("M4 P54C PC HLT continues exact virtual TSC with live PIT and partial audio", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
            m.Ports.Write(0x43, 0x34, 1); m.Ports.Write(0x40, 0x34, 1); m.Ports.Write(0x40, 0x12, 1);
            m.Ports.Write(0x43, 0xb6, 1); m.Ports.Write(0x42, 0x80, 1); m.Ports.Write(0x42, 1, 1); m.Ports.Write(0x61, 3, 1);
            Code(m, [0xfa, 0xdb, 0xe3, 0xd9, 0xeb, 0xf4]); m.RunSteps(4);
            Check(m.Cpu.Halted && m.Cpu.TimestampCounter == 16, $"HLT prelude: halted={m.Cpu.Halted}, TSC={m.Cpu.TimestampCounter}, fault={m.Fault}.");
            m.RunSteps(10, 1234567);
            Check(m.Cpu.Halted && m.Scheduler.Now == 1234567 && m.Cpu.TimestampCounter == 111111,
                $"HLT idle: halted={m.Cpu.Halted}, now={m.Scheduler.Now}, TSC={m.Cpu.TimestampCounter}, fault={m.Fault}.");
            var state = m.CaptureHardwareState();
            Check(state.Scheduler.Events.Any(v => v.Owner == "pit"), "Programmed periodic PIT channel0 has no future event.");
            Check(m.Pit.SpeakerCounter.Gate && !m.Pit.SpeakerCounter.NullCount && m.Speaker.Enabled, "Speaker counter was not loaded and running.");
            Check(state.Components["audio"].Fields["_count"].GetInt32() > 0, "Mixer does not contain a partial PCM block.");
            using var resumed = fixture.Restore(); Same(m, resumed);
            for (int i = 0; i < 12; i++) { ulong deadline = m.Scheduler.Now + 27183; m.RunSteps(10, deadline); resumed.RunSteps(10, deadline); Same(m, resumed); }
            Check(m.Cpu.SaveState().Fields["_fpuRegisters"].GetArrayLength() == 8, "Raw FPU register set is incomplete.");
        });
        test("M4 P54C PC REP caller deadline preserves batch state and raw continuation", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
            Code(m, [0xfa, 0xfc, 0xf3, 0xa4, 0xf4]); m.Cpu.SetRegister(Register.Cx, 4096); m.Cpu.SetRegister(Register.Si, 0x3000); m.Cpu.SetRegister(Register.Di, 0x5000);
            for (int i = 0; i < 4096; i++) m.Memory.Ram[0x3000 + i] = (byte)(i * 37 + 11);
            m.RunSteps(10000, 20000);
            Check(m.Cpu.HasRepContinuation && m.Cpu.GetRegister(Register.Cx) > 0 && m.Cpu.GetRegister(Register.Cx) < 4096);
            Check(m.Scheduler.Now is >= 20000 and < 20045 && m.Cpu.TimestampCounter % 4 == 0);
            using var resumed = fixture.Restore(); Same(m, resumed);
            for (int i = 0; i < 20 && !m.Cpu.Halted; i++) { m.RunSteps(247); resumed.RunSteps(247); Same(m, resumed); }
            Check(m.Cpu.Halted && m.Cpu.GetRegister(Register.Cx) == 0);
            Check(m.Memory.Ram.AsSpan(0x3000, 4096).SequenceEqual(m.Memory.Ram.AsSpan(0x5000, 4096)));
        });
        foreach (string operation in new[] { "serial", "midi-input", "midi-output", "dsp-reset", "opl-timer" })
            test("M4 P54C PC complete peripheral clock rebinding " + operation, () =>
            {
                var profile = Profile() with { SerialPort = Uart16550.ProfileId, SoundCard = SoundBlaster16.ProfileId, MidiInterface = Mpu401Uart.ProfileId };
                using var fixture = new SnapshotTests.Fixture(profile, false); var m = fixture.Machine;
                WriteTsc(m, 0x1122334455667788);
                if (operation == "serial")
                { m.Serial!.Write(0x3fb, 0x80, 1); m.Serial.Write(0x3f8, 12, 1); m.Serial.Write(0x3f9, 0, 1); m.Serial.Write(0x3fb, 3, 1); m.Serial.Write(0x3f8, 0x69, 1); }
                else if (operation.StartsWith("midi-", StringComparison.Ordinal))
                { m.Midi!.Write(0x331, 0x3f, 1); Advance(m, 10000); m.Midi.Read(0x330, 1); if (operation == "midi-input") m.Midi.QueueReceive(new MidiInput([0x90, 60, 100])); else m.Midi.Write(0x330, 0x91, 1); }
                else if (operation == "dsp-reset") { m.Ports.Write(0x226, 1, 1); Advance(m, 4000); m.Ports.Write(0x226, 0, 1); }
                else { m.Ports.Write(0x388, 2, 1); Advance(m, 50000); m.Ports.Write(0x389, 200, 1); Advance(m, 50000); m.Ports.Write(0x388, 4, 1); Advance(m, 50000); m.Ports.Write(0x389, 1, 1); }
                Code(m, [0xfa, 0xdb, 0xe3, 0xd9, 0xeb, 0xf4]); m.RunSteps(4);
                string owner = operation == "serial" ? "serial" : operation.StartsWith("midi-", StringComparison.Ordinal) ? "midi" : operation == "dsp-reset" ? "sb16" : "opl3";
                Check(m.CaptureHardwareState().Scheduler.Events.Any(v => v.Owner == owner));
                using var resumed = fixture.Restore(); Same(m, resumed);
                for (int i = 0; i < 12; i++) { m.RunSteps(10, m.Scheduler.Now + 135791); resumed.RunSteps(10, resumed.Scheduler.Now + 135791); Same(m, resumed); }
            });
        foreach (string change in new[] { "model486", "model-unknown", "frequency0", "frequency33", "cr4", "epoch-negative", "epoch-source", "scheduler-frequency", "profile486", "profile-frequency", "profile-cpu", "profile-unknown" })
            test("M4 P54C PC semantic import rejection and active-machine rollback " + change, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine;
                WriteTsc(m, 0xfedcba9876543210); Code(m, [0xfa, 0xf4]); m.RunSteps(2);
                using var session = new MachineSession(m); string saved = fixture.PathFor("bad-p54c.dossav"), branch = fixture.PathFor("bad-p54c.dosebranch"); SnapshotStore.Capture(m, saved);
                if (change.StartsWith("profile", StringComparison.Ordinal)) EditProfile(saved, profile =>
                {
                    if (change == "profile486") { profile["id"] = "virtual486-dev-v1"; profile["cpu"] = "i486dx-development"; profile["cpuHz"] = 33000000; }
                    else if (change == "profile-frequency") profile["cpuHz"] = 33000000;
                    else if (change == "profile-cpu") profile["cpu"] = "i486dx-development";
                    else profile["id"] = "invented-p54c-board";
                });
                else EditState(saved, state =>
                {
                    var cpu = state["components"]!["cpu"]!["fields"]!.AsObject();
                    switch (change)
                    {
                        case "model486": cpu["Model"] = 0; break;
                        case "model-unknown": cpu["Model"] = 99; break;
                        case "frequency0": cpu["TimestampFrequency"] = 0; break;
                        case "frequency33": cpu["TimestampFrequency"] = 33000000; break;
                        case "cr4": cpu["Cr4"] = 0x10; break;
                        case "epoch-negative": cpu["_tscEpochTicks"] = -1; break;
                        case "epoch-source": cpu["_timestampSource"] = "host-clock"; break;
                        case "scheduler-frequency":
                            state["scheduler"]!["cachedFrequency"] = 33000000;
                            state["scheduler"]!["remainder"] = 0;
                            state["scheduler"]!["whole"] = 121;
                            state["scheduler"]!["fraction"] = 7000000;
                            break;
                    }
                });
                string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); long revision = session.Revision;
                var answer = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
                Check(!answer.Success && ReferenceEquals(m, session.Machine) && session.Revision == revision);
                Check(!Directory.Exists(branch) && before == State(m) && ram.SequenceEqual(m.Memory.Ram));
                m.RunSteps(10, m.Scheduler.Now + 123456); Check(m.Fault is null && m.Cpu.TimestampCounter != 0);
            });
        test("M4 P54C PC snapshot keeps custom PC name separate from hardware name", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile()); var m = fixture.Machine; using var session = new MachineSession(m);
            Check(Request(session, "SaveMachineDefinition", new { path = fixture.PathFor("named-pc.dospc.json"), name = "My P54C PC" }).Success);
            using var resumed = fixture.Restore(); Same(m, resumed);
            Check(resumed.Profile == m.Profile && resumed.CaptureDefinition().Name == "My P54C PC");
            Check(resumed.Profile.Name == "P54C test hardware" && resumed.Cpu.Model == X86CpuModel.IntelP54cB5Subset);
        });
        test("M4 P54C PC full container retains all four IDE and both floppy generations", () =>
        {
            using var directory = new SnapshotTests.Fixture(); var disks = new CopyOnWriteDisk?[6]; PcMachine? m = null;
            var basis = new string[6]; var baseHashes = new byte[6][];
            try
            {
                for (int slot = 0; slot < 6; slot++)
                {
                    basis[slot] = directory.PathFor("own-base-" + slot + ".img");
                    using (var file = new FileStream(basis[slot], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.SetLength(slot < 4 ? 2 * 1048576 : 1440 * 1024); file.WriteByte((byte)(0x30 + slot)); }
                    baseHashes[slot] = SHA256.HashData(File.ReadAllBytes(basis[slot]));
                    disks[slot] = new(basis[slot], directory.PathFor("own-journal-" + slot + ".cow"));
                    foreach (bool last in new[] { false, true })
                    {
                        byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(slot * 29 + i * 7 + (last ? 113 : 3))).ToArray();
                        disks[slot]!.WriteSector(last ? disks[slot]!.SectorCount - 1 : 0, payload);
                    }
                }
                m = PcMachine.CreateWithDisks(Profile() with { FloppyDriveA = "1440k", FloppyDriveB = "1440k" }, disks.Take(4).Cast<IReadOnlyBlockDevice?>().ToArray(), disks[4], disks[5]);
                WriteTsc(m, 0x1234fedcba987654); Code(m, [0xfa, 0xdb, 0xe3, 0xd9, 0xeb, 0xf4]); m.RunSteps(4);
                string saved = directory.PathFor("six-media.dossav"), again = directory.PathFor("six-after.dossav");
                SnapshotStore.Capture(m, saved); using var resumed = SnapshotStore.Load(saved, directory.PathFor("six-media.dosebranch")); Same(m, resumed);
                Check(resumed.CaptureDefinition().HardDisks.Select(v => v.Slot).SequenceEqual(new[] { 0, 1, 2, 3 }), "Four IDE positions did not survive load.");
                Check(resumed.CaptureDefinition().Floppies.Select(v => v.Slot).SequenceEqual(new[] { 0, 1 }), "Both floppy positions did not survive load.");
                m.RunSteps(10, m.Scheduler.Now + 200003); resumed.RunSteps(10, resumed.Scheduler.Now + 200003); Same(m, resumed);
                SnapshotStore.Capture(resumed, again);
                for (int slot = 0; slot < 6; slot++)
                {
                    string name = slot < 4 ? "ide" + slot + ".cow" : "floppy" + (slot - 4) + ".cow";
                    byte[] first = File.ReadAllBytes(Path.Combine(saved, "media.dosemedia", name));
                    byte[] after = File.ReadAllBytes(Path.Combine(again, "media.dosemedia", name));
                    Check(first.SequenceEqual(after) && first.Length == 48 + 2 * 552, "A complete sealed journal changed at media slot " + slot);
                    Check(BitConverter.ToInt64(after, 48) == 0 && BitConverter.ToInt64(after, 48 + 552) == disks[slot]!.SectorCount - 1, "A sealed record moved to another LBA.");
                    for (int i = 0; i < 512; i++)
                        Check(after[56 + i] == (byte)(slot * 29 + i * 7 + 3) && after[608 + i] == (byte)(slot * 29 + i * 7 + 113), "Independent first/last sector bytes differ.");
                    Check(baseHashes[slot].SequenceEqual(SHA256.HashData(File.ReadAllBytes(basis[slot]))), "Private immutable base was modified.");
                }
            }
            finally
            {
                if (m is not null) m.Dispose();
                else foreach (var disk in disks) disk?.Dispose();
            }
        });
    }
}
