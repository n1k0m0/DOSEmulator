// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Original DSP4.05 E3 response through actual CPU I/O and preserved FIFO/reset/snapshot boundaries; no sound-driver acceptance.</summary>
internal static class Windows95Sb16CopyrightTests
{
    /// <summary>Independent original45-byte reply fixture, including its single final NUL; not obtained from product constants.</summary>
    private static readonly byte[] Notice = Convert.FromHexString(
        "434F505952494748542028432920435245415449564520544543484E4F4C4F4759204C54442C20313939322E00");

    /// <summary>Fails on an incorrect actual CPU, device, byte, deadline or continuation result.</summary>
    private static void Check(bool value, string why = "DSP4.05 E3 invariant failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Private real CPU/DMA/mixer graph, optionally borrowing a complete factory Windows board.</summary>
    private sealed class Rig
    {
        /// <summary>Optional complete owner used for actual full-container instruction boundaries.</summary>
        public readonly PcMachine? Machine;
        /// <summary>Actual CPU executing byte/word/dword IN and OUT, without replacing any device handler.</summary>
        public readonly X86Cpu Cpu;
        /// <summary>Physical memory of the same CPU and DMA peers.</summary>
        public readonly MemoryBus Memory;
        /// <summary>Actual virtual scheduler supplying existing DSP command/reset deadlines.</summary>
        public readonly DeviceScheduler Clock;
        /// <summary>Mapped physical I/O bus for all instructions in this fixture.</summary>
        public readonly IoPortBus Ports;
        /// <summary>Real ISA DMA controller, whose absence of transfer is independently checked.</summary>
        public readonly Dma8237 Dma;
        /// <summary>Real integrated audio owner; E3 must not create a sample transfer.</summary>
        public readonly PcAudioMixer Audio;
        /// <summary>Actual unchanged CT1740/DSP4.05 instance.</summary>
        public readonly SoundBlaster16 Card;
        /// <summary>Optional virtual-only MPU peer for the independent IRQ-source control.</summary>
        public readonly Mpu401Uart? Midi;
        /// <summary>External physical card IRQ transitions, distinct from its three source bits.</summary>
        public readonly List<(int Irq, bool High)> Irqs = [];

        /// <summary>Builds only own components or binds the peers of a newly imported complete PC.</summary>
        public Rig(ushort port = 0x220, int irq = 5, bool midi = false, PcMachine? machine = null, bool configure = true)
        {
            Machine = machine;
            if (machine is null)
            {
                Ports = new(); (Cpu, Memory) = ProtectedModeTests.Rig([0x90], io: Ports, model: X86CpuModel.IntelP54cB5Subset);
                Clock = new(); Dma = new(Memory); Audio = new(Clock);
                Card = new(Clock, Dma, Audio, (line, high) => Irqs.Add((line, high)), port, irq);
                Ports.Map(port, 16, Card);
                if (midi) { Midi = new(Clock); Card.AttachMidi(Midi); Ports.Map(0x330, 2, Midi); }
            }
            else
            {
                Cpu = machine.Cpu; Memory = machine.Memory; Clock = machine.Scheduler; Ports = machine.Ports;
                Dma = machine.Dma; Audio = machine.Audio; Card = machine.SoundBlaster16!; Midi = machine.Midi;
                if (configure) EnterProtected(machine);
            }
        }

        /// <summary>Executes exactly one own instruction through the full owner where present.</summary>
        private void Step()
        {
            if (Machine is null) Cpu.Step();
            else Check(Machine.RunSteps(1) == 1 && Machine.Fault is null, Machine.Fault ?? "Owner stopped.");
            Check(!Cpu.TripleFaulted && !Cpu.Halted, "Actual CPU I/O caller faulted or halted.");
        }

        /// <summary>Performs an actual port transaction with independently seeded upper register bits.</summary>
        private uint Io(ushort port, bool write, uint value, int width)
        {
            Check(width is 1 or 2 or 4);
            var code = new List<byte> { 0xba };
            code.AddRange(BitConverter.GetBytes((uint)port)); code.Add(0xb8);
            code.AddRange(BitConverter.GetBytes(write ? value : 0xa5c36912u));
            if (width == 2) code.Add(0x66);
            code.Add(write ? (width == 1 ? (byte)0xee : (byte)0xef) : (width == 1 ? (byte)0xec : (byte)0xed));
            code.CopyTo(Memory.Ram, 0x2000); Cpu.Eip = 0x2000;
            for (int i = 0; i < 3; i++) Step();
            Check(Cpu.Eip == 0x2000u + code.Count && Cpu.GetSegment(Segment.Cs) == 8, "IN/OUT left the own caller.");
            uint result = Cpu.GetRegister(Register.Ax);
            if (!write && width != 4)
                Check((result & (width == 1 ? 0xffffff00u : 0xffff0000u)) ==
                    (width == 1 ? 0xa5c36900u : 0xa5c30000u), "Narrow IN changed unrelated register bits.");
            return result & (width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue);
        }

        /// <summary>Reads a relative CT1740 port through a real CPU IN.</summary>
        public uint Read(int offset, int width = 1) => Io((ushort)(Card.IoBasePort + offset), false, 0, width);
        /// <summary>Writes a relative CT1740 port through a real CPU OUT.</summary>
        public void Write(int offset, uint value, int width = 1) => Io((ushort)(Card.IoBasePort + offset), true, value, width);
        /// <summary>Reads a separately mapped peer through the same real CPU decoder.</summary>
        public uint Input(ushort port) => Io(port, false, 0, 1);
        /// <summary>Writes a separately mapped peer through the same real CPU decoder.</summary>
        public void Output(ushort port, byte value) => Io(port, true, value, 1);

        /// <summary>Advances existing virtual deadlines and the integrated mixer without introducing timers.</summary>
        public void Advance(ulong delta) { Clock.AdvanceTo(checked(Clock.Now + delta)); Audio.AdvanceTo(Clock.Now); }
        /// <summary>Uses the existing nominal DSP reset contract and consumes exactly its AA reply.</summary>
        public void Reset()
        {
            Write(6, 1); Advance(3000); Write(6, 0); Advance(100000);
            Check(Read(0xe) == 0x80 && Read(0xa) == 0xaa && Read(0xe) == 0, "Reset AA/status differs.");
        }
        /// <summary>Sends actual accepted command/parameter bytes through the existing one-microsecond functional path.</summary>
        public void Send(params byte[] data)
        {
            foreach (byte value in data)
            { Check(Read(0xc) == 0, "DSP was not command-ready."); Write(0xc, value); Advance(1000); }
        }
        /// <summary>Reads exactly the externally specified response bytes and checks destructive FIFO progress.</summary>
        public void Expect(ReadOnlySpan<byte> expected, bool emptyAfter = true)
        {
            foreach (byte value in expected)
            {
                Check(Read(0xe) == 0x80, "Read-ready disappeared before an expected response byte.");
                uint actual = Read(0xa); Check(actual == value, "Actual DSP response byte " + actual.ToString("X2") + " differs from " + value.ToString("X2"));
            }
            if (emptyAfter) Check(Read(0xe) == 0 && Read(0xa) == 0xff && Read(0xe) == 0, "FIFO gained an extra byte or lost empty status.");
        }
        /// <summary>Reads one indexed mixer byte without acknowledging any DSP or MPU source.</summary>
        public uint Mixer(byte index) { Write(4, index); return Read(5); }
        /// <summary>Writes one actual indexed mixer byte.</summary>
        public void Mixer(byte index, byte value) { Write(4, index); Write(5, value); }
        /// <summary>Captures only this rig's named schedulable component without opaque host callbacks.</summary>
        public SchedulerState Scheduled() => Clock.SaveState(new Dictionary<object, string> { [Card] = "sb16" });
    }

    /// <summary>Enters real PE32 on the complete own PC; no CPU replacement, firmware callback or host framebuffer is used.</summary>
    private static void EnterProtected(PcMachine machine)
    {
        var c = machine.Cpu; var m = machine.Memory; c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000; c.Eflags = 2;
        c.SetRegister(Register.Sp, 0x9000);
        ProtectedModeTests.Descriptor(m, 8, 0, 0xfffff, 0x9a, 12); ProtectedModeTests.Descriptor(m, 16, 0, 0xfffff, 0x92, 12);
        for (int i = 0; i < 256; i++) ProtectedModeTests.Gate(m, (byte)i);
        m.WriteByte(0x6000, 0xf4); m.WriteWord(0x300, 0xff); m.WriteDword(0x302, 0x400);
        m.WriteWord(0x310, 0x7ff); m.WriteDword(0x312, 0x8000);
        new byte[] { 0x0f, 0x01, 0x16, 0, 3, 0x66, 0x0f, 0x01, 0x1e, 0x10, 3,
            0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0 }.CopyTo(m.Ram, 0x1000);
        Check(machine.RunSteps(5) == 5 && c.ProtectedMode && c.Eip == 0x2000 && machine.Fault is null);
        c.SetSegment(Segment.Ss, 16); c.SetSegment(Segment.Ds, 16); c.SetSegment(Segment.Es, 16); c.SetRegister(Register.Sp, 0x18000);
    }

    /// <summary>Requires an informational command to leave the physical DMA engines and all IRQ/sample sources idle.</summary>
    private static void NoTransfer(Rig rig, string dmaBefore)
    {
        Check(!rig.Card.Active && rig.Card.TransferredSamples == 0 && rig.Card.RecordedSamples == 0 &&
            rig.Card.Dma8Grants == 0 && rig.Card.Dma16Grants == 0 && rig.Card.CompletedBlocks == 0 &&
            rig.Card.InterruptSources == 0 && rig.Card.Irq8Requests == 0 && rig.Card.Irq16Requests == 0 &&
            rig.Card.DacLeft == 0 && rig.Card.DacRight == 0, "E3 invented PCM, DMA or an IRQ source.");
        Check(dmaBefore == JsonSerializer.Serialize(rig.Dma.SaveState(), StateValues.Json), "E3 changed DMA programming.");
    }

    /// <summary>Recursively copies explicit reviewed component state, retaining the new rig's original peer bindings.</summary>
    private static void CopyState(IStateObject source, IStateObject target)
    {
        target.LoadState(source.SaveState());
        var children = target.StateChildren.ToDictionary(v => v.Name, v => v.Value);
        foreach (var child in source.StateChildren) CopyState(child.Value, children[child.Name]);
    }

    /// <summary>Restores an independently constructed raw graph, including CPU/RAM/audio inputs and the actual scheduled command token.</summary>
    private static Rig Copy(Rig source)
    {
        var target = new Rig(source.Card.IoBasePort, source.Card.Irq); source.Memory.Ram.CopyTo(target.Memory.Ram, 0);
        CopyState(source.Memory, target.Memory); CopyState(source.Dma, target.Dma); CopyState(source.Card, target.Card);
        CopyState(source.Audio, target.Audio); CopyState(source.Cpu, target.Cpu);
        target.Clock.LoadState(source.Scheduled(), new Dictionary<string, IStateObject> { ["sb16"] = target.Card });
        return target;
    }

    /// <summary>Includes every raw owned member and nested audio input in the continuation comparison.</summary>
    private static string Tree(IStateObject value)
    {
        var nodes = new Dictionary<string, StateBlock>();
        var pending = new Queue<(string Name, IStateObject Value)>(); pending.Enqueue(("", value));
        while (pending.TryDequeue(out var next))
        {
            nodes.Add(next.Name, next.Value.SaveState());
            foreach (var child in next.Value.StateChildren) pending.Enqueue((next.Name + "/" + child.Name, child.Value));
        }
        return JsonSerializer.Serialize(nodes, StateValues.Json);
    }

    /// <summary>Compares complete raw CPU/RAM/DMA/card/audio/scheduler futures after each actual port effect.</summary>
    private static void Equal(Rig source, Rig target)
    {
        Check(source.Memory.Ram.AsSpan().SequenceEqual(target.Memory.Ram), "Raw full physical RAM differs.");
        Check(Tree(source.Memory) == Tree(target.Memory) && Tree(source.Cpu) == Tree(target.Cpu) &&
            Tree(source.Dma) == Tree(target.Dma) && Tree(source.Card) == Tree(target.Card) && Tree(source.Audio) == Tree(target.Audio) &&
            JsonSerializer.Serialize(source.Scheduled(), StateValues.Json) == JsonSerializer.Serialize(target.Scheduled(), StateValues.Json),
            "Raw CPU/card/DMA/audio or scheduler continuation differs.");
    }

    /// <summary>Continues a genuinely partial FIFO or an undispatched E3 command with each subsequent real CPU read compared.</summary>
    private static void RawFuture(int prefix, bool pending)
    {
        var source = new Rig(); source.Reset();
        if (pending) source.Write(0xc, 0xe3); else { source.Send(0xe3); source.Expect(Notice.AsSpan(0, prefix), false); }
        var restored = Copy(source); Equal(source, restored);
        if (pending)
        {
            ulong deadline = source.Clock.NextEventTime!.Value;
            source.Advance(deadline - source.Clock.Now - 1); restored.Advance(deadline - restored.Clock.Now - 1); Equal(source, restored);
            Check(source.Read(0xe) == 0 && restored.Read(0xe) == 0); Equal(source, restored);
            source.Advance(1); restored.Advance(1); Equal(source, restored);
        }
        for (int i = prefix; i < Notice.Length; i++)
        {
            Check(source.Read(0xe) == 0x80 && restored.Read(0xe) == 0x80); Equal(source, restored);
            Check(source.Read(0xa) == Notice[i] && restored.Read(0xa) == Notice[i], "Restored actual FIFO byte differs."); Equal(source, restored);
        }
        source.Expect([]); restored.Expect([]); Equal(source, restored);
    }

    /// <summary>Captures a full own32-MiB Windows board with an open command or partially consumed FIFO and compares all later hardware/RAM effects.</summary>
    private static void FullFuture(bool pending)
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
        string root = Path.GetFullPath(Path.Combine(parent, "win95-sb16-e3-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new MachineProfile { Id = MachineProfile.Windows95ProfileId, Name = "Own E3 full-state continuation",
                Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId,
                OpticalDrive = MachineProfile.AtapiCdRomId, SoundCard = SoundBlaster16.ProfileId };
            using var machine = PcMachine.CreateWithDisks(profile, [new MemoryDisk(new byte[128 * 512], "Own E3 disk"), null, null, null]);
            var source = new Rig(machine: machine); source.Reset(); int prefix = pending ? 0 : 13;
            if (pending) source.Write(0xc, 0xe3); else { source.Send(0xe3); source.Expect(Notice.AsSpan(0, prefix), false); }
            string saved = Path.Combine(root, "active.dossav"); SnapshotStore.Capture(machine, saved);
            using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
            var restored = new Rig(machine: resumed, configure: false);
            // Captures include every implemented device and scheduler field; installed RAM is compared separately in full.
            Check(JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json) ==
                JsonSerializer.Serialize(resumed.CaptureHardwareState(), StateValues.Json) && machine.Memory.Ram.AsSpan().SequenceEqual(resumed.Memory.Ram));
            if (pending) { source.Advance(1000); restored.Advance(1000); }
            for (int i = prefix; i < Notice.Length; i++)
            {
                Check(source.Read(0xe) == 0x80 && restored.Read(0xe) == 0x80);
                Check(source.Read(0xa) == Notice[i] && restored.Read(0xa) == Notice[i], "Full restored FIFO byte differs.");
                Check(JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json) ==
                    JsonSerializer.Serialize(resumed.CaptureHardwareState(), StateValues.Json) &&
                    machine.Memory.Ram.AsSpan().SequenceEqual(resumed.Memory.Ram), "Full actual PC future differs after data IN.");
            }
            source.Expect([]); restored.Expect([]);
            source.Send(0xe1); restored.Send(0xe1); source.Expect([4, 5]); restored.Expect([4, 5]);
            Check(JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json) ==
                JsonSerializer.Serialize(resumed.CaptureHardwareState(), StateValues.Json) && machine.Memory.Ram.AsSpan().SequenceEqual(resumed.Memory.Ram));
        }
        finally
        {
            Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win95-sb16-e3-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }

    /// <summary>Registers bounded independent E3, parser/FIFO/reset, source-separation and genuine continuation controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (ushort port in new ushort[] { 0x220, 0x280 })
            test("M4 Win95 DSP4.05 E3 real CPU exact45-byte NUL reply A" + port.ToString("X3"), () =>
            {
                var rig = new Rig(port, port == 0x220 ? 5 : 9); rig.Reset();
                string dma = JsonSerializer.Serialize(rig.Dma.SaveState(), StateValues.Json);
                rig.Send(0xe3); Check(Notice.Length == 45 && Notice[44] == 0); rig.Expect(Notice);
                NoTransfer(rig, dma); Check(rig.Card.UnsupportedCommands == 0 && rig.Card.LastUnsupportedCommand is null && rig.Irqs.Count == 0);
            });
        test("M4 Win95 DSP4.05 E3 existing command busy deadline rejects a competing E1", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Write(0xc, 0xe3); ulong deadline = rig.Clock.NextEventTime!.Value;
            Check(deadline == rig.Clock.Now + 1000 && rig.Read(0xc) == 0x80 && rig.Read(0xe) == 0);
            rig.Write(0xc, 0xe1); Check(rig.Card.RejectedWrites == 1);
            rig.Advance(999); Check(rig.Read(0xe) == 0 && rig.Read(0xc) == 0x80);
            rig.Advance(1); Check(rig.Read(0xc) == 0); rig.Expect(Notice);
            rig.Send(0xe1); rig.Expect([4, 5]); Check(rig.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 DSP4.05 E3 remains parameter data while direct-DAC command waits", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Send(0x10, 0xe3);
            Check(rig.Card.DacLeft == 25344 && rig.Card.DacRight == 25344 && rig.Read(0xe) == 0 && rig.Card.UnsupportedCommands == 0);
            rig.Send(0xe3); rig.Expect(Notice); Check(rig.Card.DacLeft == 25344 && rig.Card.DacRight == 25344);
        });
        test("M4 Win95 DSP4.05 E3 appends behind E1 and ahead of following speaker-status byte", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Send(0xe1, 0xe3, 0xd8);
            rig.Expect(new byte[] { 4, 5 }.Concat(Notice).Append((byte)0).ToArray()); Check(rig.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 DSP4.05 E3 retains existing bounded64-byte drop-new FIFO policy", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Send(0xe3, 0xe3);
            rig.Expect(Notice.Concat(Notice.Take(19)).ToArray()); rig.Send(0xe3); rig.Expect(Notice);
        });
        test("M4 Win95 DSP4.05 E3 wide reads and writes do not consume or invent byte replies", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Write(0xc, 0xe3, 2); rig.Write(0xc, 0xe3, 4); rig.Advance(2000);
            Check(rig.Read(0xe) == 0 && rig.Card.RejectedWrites == 0); rig.Send(0xe3);
            Check(rig.Read(0xa, 2) == 0xffff && rig.Read(0xa, 4) == uint.MaxValue &&
                rig.Read(0xe, 2) == 0xffff && rig.Read(0xe, 4) == uint.MaxValue && rig.Read(9) == 0xff);
            rig.Expect(Notice); Check(rig.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 DSP4.05 reset cancels accepted pending E3 before its original callback", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Write(0xc, 0xe3); rig.Advance(999); rig.Write(6, 1);
            rig.Advance(3000); Check(rig.Read(0xe) == 0 && rig.Read(0xc) == 0x80);
            rig.Write(6, 0); rig.Advance(99999); Check(rig.Read(0xe) == 0);
            rig.Advance(1); rig.Expect([0xaa]); rig.Send(0xe1, 0xe3);
            rig.Expect(new byte[] { 4, 5 }.Concat(Notice).ToArray()); Check(rig.Card.ResetCount == 2 && rig.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 DSP4.05 reset discards a genuine partial E3 FIFO without an old suffix", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Send(0xe3); rig.Expect(Notice.AsSpan(0, 17), false);
            rig.Reset(); rig.Advance(10000); rig.Expect([]); rig.Send(0xe3); rig.Expect(Notice);
        });
        foreach (bool wordFirst in new[] { false, true })
            test("M4 Win95 DSP4.05 E3 separates actual DSP8/DSP16 IRQ acknowledgements wordFirst=" + wordFirst, () =>
            {
                var rig = new Rig(); rig.Reset(); rig.Send(0xf2, 0xf3);
                Check(rig.Card.InterruptSources == 3 && rig.Mixer(0x82) == 3 && rig.Irqs.SequenceEqual(new[] { (5, true) }));
                rig.Send(0xe3); Check(rig.Card.InterruptSources == 3 && rig.Read(0xa) == Notice[0] && rig.Card.InterruptSources == 3);
                if (wordFirst)
                { Check(rig.Read(0xf) == 0xff && rig.Card.InterruptSources == 1); Check(rig.Read(0xe) == 0x80 && rig.Card.InterruptSources == 0); }
                else
                { Check(rig.Read(0xe) == 0x80 && rig.Card.InterruptSources == 2); Check(rig.Read(0xf) == 0xff && rig.Card.InterruptSources == 0); }
                rig.Expect(Notice.AsSpan(1));
                Check(rig.Irqs.SequenceEqual(new[] { (5, true), (5, false) }) && rig.Card.Irq8Requests == 1 &&
                    rig.Card.Irq16Requests == 1 && rig.Card.SoftwareIrq8Requests == 1 && rig.Card.SoftwareIrq16Requests == 1);
            });
        test("M4 Win95 DSP4.05 E3 leaves reserved CT1745 replies and mixer-reset FIFO ownership unchanged", () =>
        {
            var rig = new Rig(); rig.Reset(); rig.Mixer(0x30, 0xf8); rig.Send(0xe3); rig.Expect(Notice.AsSpan(0, 7), false);
            rig.Mixer(0x0e, 0); rig.Mixer(0x3c, 0); Check(rig.Mixer(0x0e) == 0xff && rig.Mixer(0x3c) == 0xff);
            Check(rig.Mixer(0x80) == 2 && rig.Mixer(0x81) == 0x22 && rig.Mixer(0x82) == 0);
            rig.Mixer(0, 0); Check(rig.Mixer(0x30) == 0xc0); rig.Expect(Notice.AsSpan(7));
            Check(rig.Card.UnsupportedCommands == 0 && rig.Card.InterruptSources == 0);
        });
        foreach (int prefix in new[] { 1, 23 })
            test("M4 Win95 DSP4.05 E3 actual partial FIFO raw continuation prefix=" + prefix, () => RawFuture(prefix, false));
        test("M4 Win95 DSP4.05 E3 actual pending-command raw callback continuation", () => RawFuture(0, true));
        foreach (bool pending in new[] { false, true })
            test("M4 Win95 DSP4.05 E3 full32MiB SnapshotStore actual future pending=" + pending, () => FullFuture(pending));
        test("M4 Win95 DSP4.05 E3 and DSP reset preserve the independently pending MPU source", () =>
        {
            var rig = new Rig(midi: true); rig.Reset(); rig.Output(0x331, 0x3f); rig.Advance(10000);
            Check(rig.Midi!.InterruptAsserted && rig.Card.InterruptSources == 4 && rig.Midi.ReceiveCount == 1);
            rig.Send(0xe3); rig.Expect(Notice.AsSpan(0, 9), false);
            Check(rig.Midi.InterruptAsserted && rig.Card.InterruptSources == 4);
            rig.Reset(); Check(rig.Card.InterruptSources == 4 && rig.Midi.ReceiveCount == 1);
            Check(rig.Input(0x330) == 0xfe && rig.Card.InterruptSources == 0 && !rig.Midi.InterruptAsserted);
            rig.Send(0xe3); rig.Expect(Notice); Check(rig.Midi.Resets == 0 && rig.Midi.Acknowledgements == 1);
        });
    }
}
