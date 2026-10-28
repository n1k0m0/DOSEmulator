// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent hard-reset domain effects on the existing PIIX peers; no cold PC replacement, original media or host endpoint is used.</summary>
internal static class PiixResetDomainTests
{
    /// <summary>Requires a physical byte, pin, future event or explicit retained-state result.</summary>
    private static void Check(bool value, string why = "PIIX reset domain contract failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Captures all actual owned child states without contacting memory or ports.</summary>
    private static string State(IStateObject item) => JsonSerializer.Serialize(new[] { item.SaveState() }
        .Concat(item.StateChildren.Select(child => child.Value.SaveState())).ToArray(), StateValues.Json);

    /// <summary>Own memory media and real scheduler/bus peers; every transferred sector has independently specified bytes.</summary>
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly MemoryBus Memory = new(32 * 1048576);
        public readonly IoPortBus Ports = new();
        public readonly Pic8259 Pic = new(true);
        public readonly Intel82371FbIrq Irq;
        public readonly Intel82437Fx Host;
        public readonly AtaPioController Primary, Secondary;
        public readonly Intel82371FbIde Ide;
        public readonly Pit8254 Pit;
        public readonly Dma8237 Dma;
        /// <summary>Builds fixed peers once, including real physical ATA interrupt callbacks and separately timed PIT IRQ pulses.</summary>
        public Rig()
        {
            Irq = new(Pic, true);
            Host = new(Memory, new byte[65536]); Host.WriteConfiguration(0x64, 8, 1);
            Memory.Map(0xc0000, 0x40000, Host, "Own PAM");
            byte[] first = Enumerable.Range(0, 4096).Select(i => unchecked((byte)(i * 13 + 0x29))).ToArray();
            byte[] second = first.Select(value => (byte)(value ^ 0xa5)).ToArray();
            Primary = new(Clock, new MemoryDisk(first, "Own primary reset fixture"), null, 0x1f0, 0x3f6, true);
            Secondary = new(Clock, new MemoryDisk(second, "Own secondary reset fixture"), null, 0x170, 0x376, true);
            Primary.InterruptChanged += high => Irq.SetIsaLine(14, high);
            Secondary.InterruptChanged += high => Irq.SetMirq(0, high);
            Ide = new(Clock, Host, Primary, Secondary, Ports); Ports.MapDynamic(Ide);
            Ports.Map(0x1f0, 8, Ide); Ports.Map(0x3f6, 1, Ide); Ports.Map(0x170, 8, Ide); Ports.Map(0x376, 1, Ide);
            Pit = new(Clock, () => { Irq.SetIsaLine(0, true); Irq.SetIsaLine(0, false); });
            Dma = new(Memory);
        }
        /// <summary>Enables both target channels and actual DMA master permission.</summary>
        public void Enable()
        { Ide.WriteConfiguration(0x20, 0xc001, 4); Ide.WriteConfiguration(0x40, 0x80008000, 4); Ide.WriteConfiguration(4, 7, 2); }
        /// <summary>Issues one real read command to the chosen owned channel and actual physical PRD.</summary>
        public void ReadDma(int channel)
        {
            ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
            uint table = 0x10000u + (uint)channel * 0x1000, target = 0x20000u + (uint)channel * 0x10000;
            BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan((int)table, 4), target);
            BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan((int)table + 4, 4), 0x80000200);
            Ports.Write((ushort)(port + 6), 0xe0, 1); Ports.Write((ushort)(port + 2), 1, 1);
            Ports.Write((ushort)(port + 3), 0, 1); Ports.Write((ushort)(port + 4), 0, 1); Ports.Write((ushort)(port + 5), 0, 1);
            Ports.Write((ushort)(port + 7), 0xc8, 1);
            Ports.Write((ushort)(0xc004 + channel * 8), table, 4); Ports.Write((ushort)(0xc000 + channel * 8), 9, 1);
        }
        /// <summary>Initializes both real PIC banks with a nondefault vector pair and unmasks inputs.</summary>
        public void InitPic()
        {
            Pic.Write(0x20, 0x11, 1); Pic.Write(0xa0, 0x11, 1);
            Pic.Write(0x21, 0x20, 1); Pic.Write(0xa1, 0x28, 1);
            Pic.Write(0x21, 4, 1); Pic.Write(0xa1, 2, 1);
            Pic.Write(0x21, 1, 1); Pic.Write(0xa1, 1, 1);
            Pic.Write(0x21, 0, 1); Pic.Write(0xa1, 0, 1);
        }
        /// <summary>Programs an actual binary PIT0 mode and reload without touching another component.</summary>
        public void ProgramPit(int mode, ushort divisor)
        { Pit.Write(0x43, (uint)(0x30 | mode << 1), 1); Pit.Write(0x40, (byte)divisor, 1); Pit.Write(0x40, (byte)(divisor >> 8), 1); }
    }

    /// <summary>Seeds only legal sticky error bits so a reset must actually restore them rather than merely preserving fixed IDs.</summary>
    private static void Status(IStateObject target, ushort value)
    {
        var state = target.SaveState(); byte[] config = state.Fields["_config"].Deserialize<byte[]>(StateValues.Json)!;
        config[6] = (byte)value; config[7] = (byte)(value >> 8);
        state.Fields["_config"] = JsonSerializer.SerializeToElement(config, StateValues.Json); target.LoadState(state);
    }

    /// <summary>Registers actual per-domain reset effects, old ABI isolation and later hardware progress.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PIIX reset bridge restores configuration but retains all independent external input pins", () =>
        {
            var r = new Rig(); r.InitPic(); r.Irq.Write(0x4d0, 0xf8, 1); r.Irq.Write(0x4d1, 0xde, 1);
            r.Irq.WriteConfiguration(0x60, 0x0f0e0b0a, 4); r.Irq.WriteConfiguration(0x70, 0x4b0a, 2);
            r.Irq.WriteConfiguration(0x4e, 0xf7, 1); r.Irq.WriteConfiguration(4, 15, 2); r.Irq.WriteConfiguration(0x6a, 0, 2);
            r.Irq.SetIsaLine(10, true); r.Irq.SetIsaLine(14, true); r.Irq.SetPirq(0, true); r.Irq.SetPirq(3, true);
            r.Irq.SetMirq(0, true); r.Irq.SetMirq(1, true); Status(r.Irq, 0x3a00);
            ushort isa = r.Irq.IsaLines; byte pirq = r.Irq.PirqAssertions, mirq = r.Irq.MirqAssertions;
            r.Irq.ApplyPiixHardReset(); r.Pic.ApplyPiixHardReset();
            Check(r.Irq.IsaLines == isa && r.Irq.PirqAssertions == pirq && r.Irq.MirqAssertions == mirq && r.Irq.SupportsMirq);
            Check(r.Irq.ReadConfiguration(0, 4) == 0x122e8086 && r.Irq.ReadConfiguration(4, 4) == 0x02000007);
            Check(r.Irq.ReadConfiguration(0x60, 4) == 0x80808080 && r.Irq.ReadConfiguration(0x70, 2) == 0x8080);
            Check(r.Irq.ReadConfiguration(0x4e, 1) == 3 && r.Irq.ReadConfiguration(0x6a, 2) == 4 && r.Irq.ReadConfiguration(0x0e, 1) == 0x80);
            Check(r.Irq.Elcr == 0 && r.Pic.LevelMask == 0 && (r.Pic.InputLines & ~4) == isa && r.Irq.HasPicBinding(r.Pic) && !r.Pic.HasPending);
        });
        test("M4 PIIX reset IRQ-only bridge retains absent MIRQ capability and inert motherboard latches", () =>
        {
            var pic = new Pic8259(true); var bridge = new Intel82371FbIrq(pic);
            bridge.WriteConfiguration(0x70, 0x0a4b, 2); bridge.SetPirq(2, true); bridge.SetIsaLine(5, true);
            bridge.ApplyPiixHardReset(); pic.ApplyPiixHardReset();
            Check(!bridge.SupportsMirq && bridge.MirqAssertions == 0 && bridge.PirqAssertions == 4 && bridge.IsaLines == 32);
            Check(bridge.ReadConfiguration(0x70, 2) == 0x8080 && bridge.HasPicBinding(pic));
        });
        foreach (int irq in new[] { 0, 1, 3, 5, 7, 8, 9, 10, 12, 13, 14, 15 })
            test("M4 PIIX reset PIC clears service state and keeps held physical IRQ" + irq, () =>
            {
                var r = new Rig(); r.InitPic();
                if ((0xdef8 & (1 << irq)) != 0) r.Pic.SetLevelMask((ushort)(1 << irq));
                r.Pic.SetLine(irq, true); Check(r.Pic.Acknowledge() == (byte)(irq < 8 ? 0x20 + irq : 0x28 + irq - 8));
                ushort physical = r.Pic.InputLines; int notifications = 0; r.Pic.InterruptAcknowledged += (_, _, _) => notifications++;
                r.Pic.ApplyPiixHardReset();
                Check((r.Pic.InputLines & ~4) == (physical & ~4) && r.Pic.LevelMask == 0 && !r.Pic.HasPending);
                Check(r.Pic.Read(0x21, 1) == 255 && r.Pic.Read(0xa1, 1) == 255 && r.Pic.Read(0x20, 1) == 0 && r.Pic.Read(0xa0, 1) == 0);
                Check(notifications == 0, "PIC reset fabricated an interrupt acknowledgement.");
                r.Pic.Write(0x21, 0, 1); r.Pic.Write(0xa1, 0, 1); Check(!r.Pic.HasPending, "A retained high edge pin was replayed.");
                r.Pic.SetLine(irq, false); r.Pic.SetLine(irq, true);
                Check(r.Pic.Acknowledge() == (byte)(irq < 8 ? 8 + irq : 0x70 + irq - 8) && notifications == 1);
            });
        test("M4 PIIX reset rejects accidental use on the legacy edge-only PIC without mutation", () =>
        {
            var pic = new Pic8259(); pic.SetLine(5, true); string before = State(pic); bool rejected = false;
            try { pic.ApplyPiixHardReset(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && State(pic) == before);
        });
        foreach (int channel in new[] { 0, 1 }) foreach (bool progressed in new[] { false, true })
            test($"M4 PIIX reset IDE cancels only its grants channel={channel} progressed={progressed}", () =>
            {
                var r = new Rig(); r.Enable(); r.ReadDma(channel); r.ProgramPit(2, 64);
                r.Clock.AdvanceTo(5000);
                if (progressed) r.Clock.AdvanceTo(5000 + Intel82371FbIde.BurstDelayNanoseconds);
                var drive = channel == 0 ? r.Primary : r.Secondary;
                int offset = drive.DmaOffset; string ata = State(drive), pit = State(r.Pit), host = State(r.Host);
                byte[] ram = r.Memory.Ram.ToArray(); ulong now = r.Clock.Now;
                Check(r.Ide.GetChannelState(channel).EventToken != 0 && drive.DmaReady);
                Status(r.Ide, 0x3a80); r.Ide.ApplyPiixHardReset();
                Check(r.Clock.Now == now && State(drive) == ata && State(r.Pit) == pit && State(r.Host) == host && r.Memory.Ram.AsSpan().SequenceEqual(ram));
                Check(r.Ide.ReadConfiguration(0, 4) == 0x12308086 && r.Ide.ReadConfiguration(4, 4) == 0x02800002 &&
                    r.Ide.ReadConfiguration(0x20, 4) == 1 && r.Ide.ReadConfiguration(0x40, 4) == 0);
                foreach (int number in new[] { 0, 1 })
                {
                    var state = r.Ide.GetChannelState(number);
                    Check(state.Command == 0 && state.Status == 0 && state.DescriptorTablePointer == 0 && state.DescriptorAddress == 0 &&
                        state.DescriptorRemaining == 0 && state.BytesTransferred == 0 && state.EventToken == 0 && !state.Active);
                }
                r.Clock.AdvanceTo(now + 200000);
                Check(drive.DmaReady && drive.DmaOffset == offset && drive.ReadSectorCount == 0 && r.Memory.Ram.AsSpan().SequenceEqual(ram));
                Check(r.Pit.InterruptPulses > 0, "IDE reset removed an unrelated actual PIT deadline.");
            });
        foreach (int channel in new[] { 0, 1 })
            test("M4 PIIX reset IDE does not relatch a continuously asserted raw drive IRQ channel=" + channel, () =>
            {
                var r = new Rig(); r.Enable(); var drive = channel == 0 ? r.Primary : r.Secondary;
                ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
                r.Ports.Write((ushort)(port + 7), 0xec, 1); r.Clock.AdvanceTo(5000);
                Check(drive.InterruptAsserted && (r.Ide.GetChannelState(channel).Status & 4) != 0);
                string before = State(drive); r.Ide.ApplyPiixHardReset();
                Check(drive.InterruptAsserted && State(drive) == before && r.Ide.GetChannelState(channel).Status == 0);
                drive.Read((ushort)(port + 7), 1); Check(!drive.InterruptAsserted);
                // Complete the original IDENTIFY data phase before issuing a legal new command.
                for (int word = 0; word < 256; word++) drive.Read(port, 2);
                drive.Write((ushort)(port + 7), 0xec, 1); r.Clock.AdvanceTo(r.Clock.Now + 5000);
                Check(drive.InterruptAsserted && (r.Ide.GetChannelState(channel).Status & 4) != 0);
            });
        foreach (int mode in new[] { 2, 3 })
            test("M4 PIIX reset PIT stops old real IRQ deadlines and preserves pulse history mode=" + mode, () =>
            {
                var r = new Rig(); r.ProgramPit(mode, 32); r.Clock.AdvanceTo(100000);
                long published = r.Pit.InterruptPulses; Check(published > 0);
                r.Primary.Write(0x1f7, 0xec, 1);
                ulong now = r.Clock.Now; r.Pit.ApplyPiixHardReset();
                Check(r.Clock.Now == now && r.Pit.InterruptPulses == published);
                r.Pit.Write(0x43, 0xe2, 1); Check((r.Pit.Read(0x40, 1) & 0x80) == 0, "Reset PIT0 OUT is not low.");
                r.Clock.AdvanceTo(now + 200000);
                Check(r.Pit.InterruptPulses == published && r.Primary.InterruptAsserted, "PIT reset failed to isolate its old callback from a real ATA deadline.");
                r.ProgramPit(mode, 64); r.Clock.AdvanceTo(r.Clock.Now + 100000);
                Check(r.Pit.InterruptPulses > published, "A newly programmed PIT could not resume after reset.");
            });
        foreach (byte control in new byte[] { 0x00, 0xe2, 0xc2, 0xb6, 0x74 })
            test("M4 PIIX reset PIT0 latch or another channel cannot clear reset output guard " + control.ToString("X2"), () =>
            {
                var r = new Rig(); r.Pit.ApplyPiixHardReset(); r.Pit.Write(0x43, control, 1);
                Check(r.Pit.SaveState().Fields["_resetOutputLow"].GetBoolean());
            });
        foreach (int mode in new[] { 2, 3 })
            test("M4 PIIX reset PIT0 real mode programming clears output guard mode=" + mode, () =>
            {
                var r = new Rig(); r.Pit.ApplyPiixHardReset(); r.Pit.Write(0x43, (uint)(0x30 | mode << 1), 1);
                Check(!r.Pit.SaveState().Fields["_resetOutputLow"].GetBoolean());
                r.Pit.Write(0x43, 0xe2, 1); Check((r.Pit.Read(0x40, 1) & 0x80) != 0);
            });
        test("M4 PIIX reset PIT0 waits for the full count write before clearing output guard", () =>
        {
            var r = new Rig(); r.Pit.ApplyPiixHardReset();
            r.Pit.Write(0x40, 64, 1); Check(r.Pit.SaveState().Fields["_resetOutputLow"].GetBoolean());
            r.Pit.Write(0x40, 0, 1); Check(!r.Pit.SaveState().Fields["_resetOutputLow"].GetBoolean());
            r.Clock.AdvanceTo(100000); Check(r.Pit.InterruptPulses == 1);
        });
        foreach (int mode in new[] { 0, 2, 3 }) foreach (bool loaded in new[] { false, true })
            test($"M4 PIIX reset counter2 integrates before clearing pending or live wave mode={mode} loaded={loaded}", () =>
            {
                var clock = new DeviceScheduler(); var counter = new PitSpeakerCounter(clock);
                counter.Control((byte)(0x30 | mode << 1)); counter.SetGate(true); counter.Write(5); counter.Write(0);
                if (loaded) clock.AdvanceTo(1000);
                bool output = counter.Output, gate = counter.Gate, nullCount = counter.NullCount;
                bool observed = false; ulong now = clock.Now;
                counter.BeforeChange += () => { Check(clock.Now == now && counter.Output == output && counter.Gate == gate && counter.NullCount == nullCount); observed = true; };
                counter.ApplyPiixHardReset();
                Check(observed && !counter.Output && !counter.Gate && counter.NullCount && counter.Reload == 65536 && clock.Now == now);
                clock.AdvanceTo(now + 100000); Check(!counter.Output && !counter.Gate && counter.NullCount && counter.HighArea(now, clock.Now) == 0);
            });
        foreach (int channel in new[] { 0, 1, 2, 3, 5, 6, 7 })
            test("M4 PIIX reset ISA DMA masks requests and clears address byte sequencing channel=" + channel, () =>
            {
                var r = new Rig(); int local = channel & 3, chip = channel / 4;
                ushort basePort = channel < 4 ? (ushort)(channel * 2) : (ushort)(0xc0 + local * 4);
                ushort flip = chip == 0 ? (ushort)0x0c : (ushort)0xd8, mode = chip == 0 ? (ushort)0x0b : (ushort)0xd6;
                ushort mask = chip == 0 ? (ushort)0x0a : (ushort)0xd4, request = chip == 0 ? (ushort)9 : (ushort)0xd2;
                ushort[] pages = [0x87, 0x83, 0x81, 0x82, 0x8f, 0x8b, 0x89, 0x8a];
                r.Dma.Write(flip, 0, 1); r.Dma.Write(basePort, 0x21, 1); r.Dma.Write(basePort, 0x43, 1);
                r.Dma.Write((ushort)(basePort + (chip == 0 ? 1 : 2)), 7, 1); r.Dma.Write((ushort)(basePort + (chip == 0 ? 1 : 2)), 0, 1);
                r.Dma.Write(pages[channel], 2, 1); r.Dma.Write(mode, (uint)(0x48 | local), 1);
                r.Dma.Write(mask, (uint)local, 1); r.Dma.Write(request, (uint)(local | 4), 1);
                r.Dma.Write(basePort, 0x99, 1);
                r.Memory.Ram[0] = 0x57; r.Memory.Ram[1] = 0x13; byte[] ram = r.Memory.Ram.ToArray();
                r.Dma.ApplyPiixHardReset();
                Check(r.Dma.Read(0x0f, 1) == 15 && r.Dma.Read(0xde, 1) == 15 && r.Dma.Read(8, 1) == 0 && r.Dma.Read(0xd0, 1) == 0);
                for (int index = 0; index < 8; index++) Check(r.Dma.Read(pages[index], 1) == 0 && !r.Dma.IsTerminalCount(index));
                if (channel < 4) Check(r.Dma.TryReadByte(channel, out _) == DmaReadResult.Blocked);
                else Check(r.Dma.TryReadWord(channel, out _) == DmaReadResult.Blocked);
                r.Dma.Write(mode, (uint)(0x48 | local), 1); r.Dma.Write(mask, (uint)local, 1);
                if (channel < 4) Check(r.Dma.TryReadByte(channel, out byte byteValue) == DmaReadResult.Transferred && byteValue == 0x57);
                else Check(r.Dma.TryReadWord(channel, out ushort wordValue) == DmaReadResult.Transferred && wordValue == 0x1357);
                Check(r.Dma.IsTerminalCount(channel) && r.Memory.Ram.AsSpan().SequenceEqual(ram));
            });
        test("M4 PIIX reset methods preserve fixed peer and child object identities", () =>
        {
            var r = new Rig(); var counter = r.Pit.SpeakerCounter;
            var picChildren = r.Pic.StateChildren.Select(item => item.Value).ToArray();
            var dmaChildren = r.Dma.StateChildren.Select(item => item.Value).ToArray();
            var ideChildren = r.Ide.StateChildren.Select(item => item.Value).ToArray();
            r.Ide.ApplyPiixHardReset(); r.Pit.ApplyPiixHardReset(); r.Dma.ApplyPiixHardReset(); r.Irq.ApplyPiixHardReset(); r.Pic.ApplyPiixHardReset();
            Check(ReferenceEquals(counter, r.Pit.SpeakerCounter) && r.Pic.StateChildren.Select(item => item.Value).SequenceEqual(picChildren) &&
                r.Dma.StateChildren.Select(item => item.Value).SequenceEqual(dmaChildren) && r.Ide.StateChildren.Select(item => item.Value).SequenceEqual(ideChildren));
            Check(r.Irq.HasPicBinding(r.Pic) && r.Ide.HasBindings(r.Clock, r.Host, r.Primary, r.Secondary, r.Ports) && r.Host.HasMemoryBinding(r.Memory));
        });
    }
}

