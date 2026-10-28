// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Unpopulated ASP command framing and opaque XBUS05 persistence through real CPU I/O and complete PC futures; no CSP processing or native-audio acceptance.</summary>
internal static class Windows95Sb16AbsentAspTests
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
        /// <summary>Real integrated audio owner; absent ASP probes must not create a sample transfer.</summary>
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
        Check(!rig.Card.Active && rig.Card.TransferredSamples == 0 && rig.Card.RecordedSamples == 0 && rig.Card.ConvertedSamples == 0 &&
            rig.Card.Dma8Grants == 0 && rig.Card.Dma16Grants == 0 && rig.Card.CompletedBlocks == 0 &&
            rig.Card.InterruptSources == 0 && rig.Card.Irq8Requests == 0 && rig.Card.Irq16Requests == 0 &&
            rig.Card.DacLeft == 0 && rig.Card.DacRight == 0, "Absent ASP framing invented PCM, DMA or an IRQ source.");
        Check(dmaBefore == JsonSerializer.Serialize(rig.Dma.SaveState(), StateValues.Json), "Absent ASP framing changed DMA programming.");
    }

    /// <summary>Reads supplemental explicitly persisted state; expected values are fixed by the independent command/reset contract.</summary>
    private static T Field<T>(Rig r, string name) => r.Card.SaveState().Fields[name].Deserialize<T>(StateValues.Json)!;

    /// <summary>Writes one whole opaque05 byte through the real two-parameter command, without assigning meaning to its bits.</summary>
    private static void WriteControl(Rig r, byte value) => r.Send(0x0e, 5, value);

    /// <summary>Reads one actual05 response and distinguishes a queued FF from an empty pulled-up data port.</summary>
    private static byte ReadControl(Rig r)
    {
        r.Send(0x0f, 5); Check(r.Read(0xe) == 0x80); byte value = (byte)r.Read(0xa);
        Check(r.Read(0xe) == 0 && r.Read(0xa) == 0xff); return value;
    }

    /// <summary>Compares every explicit PC state block, the actual scheduler and whole32MiB after future real port effects.</summary>
    private static void Equal(PcMachine a, PcMachine b)
    {
        var first = a.CaptureHardwareState(); var second = b.CaptureHardwareState();
        Check(JsonSerializer.Serialize(first.Components, StateValues.Json) == JsonSerializer.Serialize(second.Components, StateValues.Json) &&
            JsonSerializer.Serialize(first.Scheduler, StateValues.Json) == JsonSerializer.Serialize(second.Scheduler, StateValues.Json) &&
            first.MemoryMap == second.MemoryMap && first.PortMap == second.PortMap && first.ParallelSink == second.ParallelSink &&
            first.SerialEcho == second.SerialEcho && first.AdcSource == second.AdcSource && first.Adc16Source == second.Adc16Source &&
            a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Full actual ASP-absence state/CPU/RAM/scheduler future differs.");
    }

    /// <summary>Performs the fixed independent response-byte oracle at each real CPU read on original and restored owners.</summary>
    private static void ExpectBoth(Rig a, Rig b, ReadOnlySpan<byte> expected)
    {
        foreach (byte value in expected)
        {
            Check(a.Read(0xe) == 0x80 && b.Read(0xe) == 0x80);
            Check(a.Read(0xa) == value && b.Read(0xa) == value); Equal(a.Machine!, b.Machine!);
        }
        a.Expect([]); b.Expect([]); Equal(a.Machine!, b.Machine!);
    }

    /// <summary>Proves whole-PC persistence of an opaque byte, live FIFO, and both real incomplete XBUS command shapes.</summary>
    private static void FullFuture(string cut)
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
        string root = Path.GetFullPath(Path.Combine(parent, "win95-sb16-absent-asp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new MachineProfile { Id = MachineProfile.Windows95ProfileId, Name = "Own unpopulated ASP continuation",
                Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId,
                OpticalDrive = MachineProfile.AtapiCdRomId, SoundCard = SoundBlaster16.ProfileId };
            using var machine = PcMachine.CreateWithDisks(profile, [new MemoryDisk(new byte[128 * 512], "Own ASP-absence disk"), null, null, null]);
            var source = new Rig(machine: machine); source.Reset(); WriteControl(source, 0xa5);
            if (cut == "fifo") { source.Send(0xe3, 0x0f, 5, 0xe1); source.Expect(Notice.AsSpan(0, 11), false); }
            else if (cut == "write-address") source.Send(0x0e, 5);
            else if (cut == "read-command") source.Send(0x0f);
            else
            {
                long before = machine.HardwareResetCount;
                source.Output(0xcf9, 4); Check(machine.RunSteps(1, machine.Scheduler.Now) == 0);
                Check(machine.LastResetSource == "cf9-init" && Field<byte>(source, "_xbus05Control") == 0xa5);
                EnterProtected(machine); Check(ReadControl(source) == 0xa5, "Actual INIT changed the powered opaque byte.");
                source.Output(0xcf9, 0); source.Output(0xcf9, 6); Check(machine.RunSteps(1, machine.Scheduler.Now) == 0);
                Check(machine.HardwareResetCount == before + 2 && machine.LastResetSource == "cf9-hard" &&
                    machine.PiixReset!.Phase == PiixResetPhase.AllAsserted && machine.CpuResetHeld &&
                    Field<byte>(source, "_xbus05Control") == 0 && source.Card.InterruptSources == 0,
                    "The actual CF9 hard-reset domain did not clear only its reset-connected opaque byte.");
            }
            string saved = Path.Combine(root, "active.dossav"); SnapshotStore.Capture(machine, saved);
            using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
            var restored = new Rig(machine: resumed, configure: false); Equal(machine, resumed);
            if (cut == "fifo") ExpectBoth(source, restored, Notice[11..].Concat(new byte[] { 0xa5, 4, 5 }).ToArray());
            else if (cut == "write-address")
            {
                source.Send(0x3c); restored.Send(0x3c); Equal(machine, resumed);
                source.Send(0x0f, 5, 0xe1, 0xe3); restored.Send(0x0f, 5, 0xe1, 0xe3);
                ExpectBoth(source, restored, new byte[] { 0x3c, 4, 5 }.Concat(Notice).ToArray());
            }
            else if (cut == "read-command")
            {
                source.Send(5, 0xe1, 0xe3); restored.Send(5, 0xe1, 0xe3);
                ExpectBoth(source, restored, new byte[] { 0xa5, 4, 5 }.Concat(Notice).ToArray());
            }
            else
            {
                ulong deadline = machine.PiixReset!.CpuReleaseAt;
                source.Advance(deadline - source.Clock.Now); restored.Advance(deadline - restored.Clock.Now); Equal(machine, resumed);
                Check(machine.PiixReset.CpuReleaseReady && resumed.PiixReset!.CpuReleaseReady);
                Check(machine.RunSteps(1, machine.Scheduler.Now) == 0 && resumed.RunSteps(1, resumed.Scheduler.Now) == 0);
                Check(!machine.CpuResetHeld && !resumed.CpuResetHeld); Equal(machine, resumed);
                EnterProtected(machine); EnterProtected(resumed); Equal(machine, resumed);
                ExpectBoth(source, restored, [0xaa]); Check(ReadControl(source) == 0 && ReadControl(restored) == 0);
                Equal(machine, resumed);
            }
            byte final = cut == "write-address" ? (byte)0x3c : cut == "hard" ? (byte)0 : (byte)0xa5;
            Check(ReadControl(source) == final && ReadControl(restored) == final &&
                source.Card.UnsupportedCommands == 0 && restored.Card.UnsupportedCommands == 0);
            Equal(machine, resumed);
        }
        finally
        {
            Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win95-sb16-absent-asp-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }

    /// <summary>Registers framed real-port absent-ASP controls and opaque persistence without claiming physical ASIC bit functions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 Win95 SB16 absentASP power05zero and actual one-byte CMD03 pull-up response", () =>
        {
            var r = new Rig(); Check(Field<byte>(r, "_xbus05Control") == 0); r.Reset();
            string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
            Check(ReadControl(r) == 0); r.Send(3); r.Expect([0xff]); NoTransfer(r, dma);
            Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
        });
        test("M4 Win95 SB16 absentASP original four-byte83 complement probe stays explicitly negative", () =>
        {
            var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
            WriteControl(r, 0x5a);
            // Actual native setup: CMD05 two zeros, CMD03 status, two CMD04 mode bytes, and XBUS83 read.
            r.Send(5, 0, 0, 3, 4, 0xfc, 4, 0xfa, 0x0f, 0x83); r.Expect([0xff, 0xff]);
            byte[] original = new byte[4];
            for (int i = 0; i < original.Length; i++)
            {
                r.Send(0x0f, 0x83); Check(r.Read(0xe) == 0x80); original[i] = (byte)r.Read(0xa);
                Check(original[i] == 0xff && r.Read(0xe) == 0);
            }
            bool complementMatched = true;
            foreach (byte value in original)
            {
                byte complement = (byte)(value ^ 0xff); Check(complement == 0);
                r.Send(0x0e, 0x83, complement); r.Expect([]); r.Send(0x0f, 0x83); Check(r.Read(0xe) == 0x80);
                byte actual = (byte)r.Read(0xa); Check(actual == 0xff && r.Read(0xe) == 0);
                complementMatched &= actual == complement;
            }
            Check(!complementMatched && ReadControl(r) == 0x5a && r.Card.UnsupportedCommands == 0); NoTransfer(r, dma);
        });
        foreach (byte value in new byte[] { 0, 0x7f, 0x80, 0xff })
            test("M4 Win95 SB16 own opaque05 whole-byte readback and DSP-reset retention value=" + value.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, value); Check(ReadControl(r) == value && Field<byte>(r, "_xbus05Control") == value);
                r.Reset(); Check(ReadControl(r) == value); r.Send(3); r.Expect([0xff]);
                NoTransfer(r, dma); Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
            });
        foreach (byte address in new byte[] { 0x80, 0x81, 0x82, 0x83 })
            test("M4 Win95 SB16 unpopulated externalXBUS address FF/drop " + address.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, 0xa5); r.Send(0x0f, address); r.Expect([0xff]);
                r.Send(0x0e, address, 0); r.Expect([]); r.Send(0x0f, address); r.Expect([0xff]);
                Check(ReadControl(r) == 0xa5); r.Send(0xe1); r.Expect([4, 5]);
                NoTransfer(r, dma); Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
            });
        foreach (byte parameter in new byte[] { 0, 0xe3, 0xf2 })
            test("M4 Win95 SB16 CMD04 consumes exactly one absentASP parameter " + parameter.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, 0x5a); r.Send(4, parameter); r.Expect([]); Check(ReadControl(r) == 0x5a);
                r.Send(0xe1); r.Expect([4, 5]); NoTransfer(r, dma); Check(r.Card.UnsupportedCommands == 0);
            });
        foreach (var parameters in new (byte First, byte Second)[] { (0, 0), (0xe1, 0xe3), (0xf2, 0xf3) })
            test("M4 Win95 SB16 CMD05 consumes both absentASP parameters " + parameters.First.ToString("X2") + "/" + parameters.Second.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, 0xa5); r.Send(5, parameters.First, parameters.Second); r.Expect([]);
                Check(ReadControl(r) == 0xa5, "CMD05 was confused with XBUS register05.");
                r.Send(3); r.Expect([0xff]); r.Send(0xe1); r.Expect([4, 5]); NoTransfer(r, dma);
                Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
            });
        foreach (byte address in new byte[] { 0, 6, 0x7f, 0x84 })
            test("M4 Win95 SB16 unknownXBUS address remains unsupported without a fake response " + address.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, 0x5a); r.Send(0x0e, address, 0xe3); r.Expect([]);
                Check(r.Card.UnsupportedCommands == 1 && r.Card.LastUnsupportedCommand == 0x0e && r.Card.RejectedParameters == 0);
                r.Send(0x0f, address); r.Expect([]);
                Check(r.Card.UnsupportedCommands == 2 && r.Card.LastUnsupportedCommand == 0x0f && ReadControl(r) == 0x5a);
                r.Send(0xe1, 0xe3); r.Expect(new byte[] { 4, 5 }.Concat(Notice).ToArray()); NoTransfer(r, dma);
            });
        foreach (byte command in new byte[] { 8, 0xde, 0xdf })
            test("M4 Win95 SB16 no ASPversion or CSP service is advertised by unsupported command " + command.ToString("X2"), () =>
            {
                var r = new Rig(); r.Reset(); string dma = JsonSerializer.Serialize(r.Dma.SaveState(), StateValues.Json);
                WriteControl(r, 0x80); r.Send(command); r.Expect([]);
                Check(r.Card.UnsupportedCommands == 1 && r.Card.LastUnsupportedCommand == command && r.Card.RejectedParameters == 0);
                Check(ReadControl(r) == 0x80); r.Send(0xe1, 0xe3); r.Expect(new byte[] { 4, 5 }.Concat(Notice).ToArray());
                NoTransfer(r, dma);
            });
        foreach (bool write in new[] { false, true })
            test("M4 Win95 SB16 partial XBUS command waits for actual address/value write=" + write, () =>
            {
                var r = new Rig(); r.Reset(); WriteControl(r, 0xa5); r.Send(write ? (byte)0x0e : (byte)0x0f); r.Expect([]);
                Check(Field<int>(r, "_expected") == (write ? 2 : 1) && Field<int>(r, "_received") == 0 &&
                    Field<byte>(r, "_xbus05Control") == 0xa5);
                r.Send(5);
                if (write)
                {
                    r.Expect([]); Check(Field<int>(r, "_received") == 1 && Field<byte>(r, "_xbus05Control") == 0xa5);
                    r.Send(0); r.Expect([]); Check(ReadControl(r) == 0);
                }
                else r.Expect([0xa5]);
                Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
            });
        test("M4 Win95 SB16 existing busy command rejects competing E3 before accepting XBUS framing", () =>
        {
            var r = new Rig(); r.Reset(); r.Write(0xc, 0x0e); r.Write(0xc, 0xe3);
            Check(r.Card.RejectedWrites == 1 && r.Read(0xc) == 0x80); r.Advance(1000);
            r.Send(5, 0x77); r.Expect([]); Check(ReadControl(r) == 0x77 && r.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 SB16 opaque05 commits only at the unchanged final-parameter callback", () =>
        {
            var r = new Rig(); r.Reset(); WriteControl(r, 0x31); r.Send(0x0e, 5); r.Write(0xc, 0x3c); r.Advance(999);
            Check(Field<byte>(r, "_xbus05Control") == 0x31 && r.Read(0xc) == 0x80 && r.Read(0xe) == 0);
            r.Advance(1); Check(Field<byte>(r, "_xbus05Control") == 0x3c && ReadControl(r) == 0x3c);
        });
        foreach (int phase in new[] { 0, 1, 2 })
            test("M4 Win95 SB16 DSP reset aborts only partial parser and retains opaque05 phase=" + phase, () =>
            {
                var r = new Rig(); r.Reset(); WriteControl(r, 0x6d);
                r.Send(phase == 2 ? (byte)0x0f : (byte)0x0e); if (phase == 1) r.Send(5);
                r.Reset(); Check(ReadControl(r) == 0x6d);
                r.Send(0xe1, 0xe3); r.Expect(new byte[] { 4, 5 }.Concat(Notice).ToArray());
                Check(r.Card.UnsupportedCommands == 0 && r.Card.RejectedParameters == 0);
            });
        test("M4 Win95 SB16 DSP reset cancels pending opaque05 value without applying it later", () =>
        {
            var r = new Rig(); r.Reset(); WriteControl(r, 0xa5); r.Send(0x0e, 5); r.Write(0xc, 0x3c); r.Advance(999);
            r.Reset(); r.Advance(10000); Check(ReadControl(r) == 0xa5 && Field<int>(r, "_expected") == 0 &&
                Field<int>(r, "_received") == 0 && r.Card.UnsupportedCommands == 0);
        });
        test("M4 Win95 SB16 absentASP frames do not merge or acknowledge actual DSP8 DSP16 MPU sources", () =>
        {
            var r = new Rig(midi: true); r.Reset(); r.Output(0x331, 0x3f); r.Advance(10000); r.Send(0xf2, 0xf3);
            Check(r.Card.InterruptSources == 7 && r.Midi!.ReceiveCount == 1 && r.Irqs.SequenceEqual(new[] { (5, true) }));
            WriteControl(r, 0xf3); r.Send(4, 0xe3, 5, 0xf2, 0xf3, 3, 0x0f, 5, 0xe1);
            Check(r.Card.InterruptSources == 7 && r.Mixer(0x82) == 7 && r.Read(0xa) == 0xff && r.Card.InterruptSources == 7);
            Check(r.Read(0xa) == 0xf3 && r.Card.InterruptSources == 7);
            Check(r.Read(0xe) == 0x80 && r.Card.InterruptSources == 6); Check(r.Read(0xf) == 0xff && r.Card.InterruptSources == 4);
            Check(r.Input(0x330) == 0xfe && r.Card.InterruptSources == 0); r.Expect([4, 5]);
            Check(r.Card.SoftwareIrq8Requests == 1 && r.Card.SoftwareIrq16Requests == 1 && r.Card.Dma8Grants == 0 &&
                r.Card.Dma16Grants == 0 && r.Card.CompletedBlocks == 0 && r.Card.UnsupportedCommands == 0 &&
                r.Irqs.SequenceEqual(new[] { (5, true), (5, false) }));
        });
        test("M4 Win95 SB16 mixer reset and reserved mixer probes preserve opaque05 and absentASP replies", () =>
        {
            var r = new Rig(); r.Reset(); WriteControl(r, 0xa5); r.Mixer(0x0e, 0); r.Mixer(0x3c, 0);
            Check(r.Mixer(0x0e) == 0xff && r.Mixer(0x3c) == 0xff && r.Mixer(0x80) == 2 && r.Mixer(0x81) == 0x22);
            r.Mixer(0, 0); Check(ReadControl(r) == 0xa5); r.Send(3); r.Expect([0xff]);
            Check(r.Card.UnsupportedCommands == 0 && r.Card.InterruptSources == 0);
        });
        foreach (string cut in new[] { "fifo", "write-address", "read-command", "hard" })
            test("M4 Win95 SB16 absentASP full32MiB SnapshotStore actual future " + cut, () => FullFuture(cut));
    }
}
