using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static partial class SoundBlasterTests
{
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly MemoryBus Memory = new(1024 * 1024);
        public readonly Dma8237 Dma;
        public readonly PcAudioMixer Mixer;
        public readonly SoundBlasterDsp Dsp;
        public readonly List<AudioBlock> Audio = [];
        public readonly List<(ulong Time, bool High)> Irqs = [];
        public Rig()
        {
            Dma = new(Memory); Mixer = new(Clock); Mixer.AudioReady += Audio.Add;
            Dsp = new(Clock, Dma, Mixer, high => Irqs.Add((Clock.Now, high)));
        }
        public void Reset()
        {
            Dsp.Write(0x226, 1, 1); Advance(3000); Dsp.Write(0x226, 0, 1); Advance(100000);
            Check(Dsp.Read(0x22e, 1) == 0x80 && Dsp.Read(0x22a, 1) == 0xaa);
        }
        public void Send(params byte[] bytes)
        {
            foreach (byte value in bytes)
            {
                Check(Dsp.Read(0x22c, 1) == 0, "DSP must be ready before each write.");
                Dsp.Write(0x22c, value, 1); Advance(SoundBlasterDsp.CommandNanoseconds);
            }
        }
        public void Advance(ulong nanoseconds) { Clock.AdvanceTo(Clock.Now + nanoseconds); Mixer.AdvanceTo(Clock.Now); }
        public void ProgramDma(byte[] data, byte mode = 0x49, uint address = 0x12000)
        {
            for (int i = 0; i < data.Length; i++) Memory.WriteByte((address & 0xf0000) | ((address + (uint)i) & 65535), data[i]);
            Dma.Write(0x0a, 5, 1); Dma.Write(0x0c, 0, 1);
            Dma.Write(2, (byte)address, 1); Dma.Write(2, (byte)(address >> 8), 1); Dma.Write(0x83, address >> 16, 1);
            Dma.Write(3, (byte)(data.Length - 1), 1); Dma.Write(3, (byte)((data.Length - 1) >> 8), 1);
            Dma.Write(0x0b, mode, 1); Dma.Write(0x0a, 1, 1);
        }
        public float[] Samples() { Mixer.Flush(); return Audio.SelectMany(b => b.Samples).ToArray(); }
    }
    private static void Check(bool condition, string message = "Sound Blaster assertion failed.") { if (!condition) throw new Exception(message); }
    public static void AddTo(Action<string, Action> test)
    {
        test("SB DSP reset observes high-pulse and virtual initialization delays", () =>
        {
            var r = new Rig(); Check(r.Dsp.Read(0x22c, 1) == 0x80 && r.Dsp.Read(0x22e, 1) == 0);
            r.Dsp.Write(0x226, 1, 1); r.Advance(2999); r.Dsp.Write(0x226, 0, 1); r.Advance(200000);
            Check(r.Dsp.Read(0x22e, 1) == 0 && r.Dsp.Read(0x22c, 1) == 0x80);
            r.Dsp.Write(0x226, 1, 1); r.Advance(3000); r.Dsp.Write(0x226, 0, 1); r.Advance(99999);
            Check(r.Dsp.Read(0x22e, 1) == 0); r.Advance(1);
            Check(r.Dsp.Read(0x22e, 1) == 0x80 && r.Dsp.Read(0x22a, 1) == 0xaa && r.Dsp.Read(0x22e, 1) == 0);
        });
        test("SB command busy rejects premature data and returns DSP 2.01 in byte order", () =>
        {
            var r = new Rig(); r.Reset(); r.Dsp.Write(0x22c, 0xe1, 1);
            Check(r.Dsp.Read(0x22c, 1) == 0x80); r.Dsp.Write(0x22c, 0x40, 1); r.Advance(999);
            Check(r.Dsp.Read(0x22e, 1) == 0); r.Advance(1);
            Check(r.Dsp.RejectedWrites == 1 && r.Dsp.Read(0x22a, 1) == 2 && r.Dsp.Read(0x22a, 1) == 1 && r.Dsp.Read(0x22a, 1) == 255);
        });
        test("SB direct DAC honors unsigned midpoint and speaker gate", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x10, 255); r.Advance(10_000_000);
            Check(r.Samples().All(s => s == 0)); r.Send(0xd8); Check(r.Dsp.Read(0x22a, 1) == 0);
            r.Send(0xd1, 0xd8); Check(r.Dsp.Read(0x22a, 1) == 255); r.Advance(10_000_000);
            Check(r.Samples().Any(s => s > 0.45)); r.Send(0x10, 0); r.Advance(100000);
            Check(r.Samples().TakeLast(8).All(s => s < -0.4));
        });
        test("SB single DMA takes one byte per guest sample period and raises IRQ at DSP length", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0x20, 0x80, 0xe0]); r.Send(0xd1, 0x40, 131, 0x14, 2, 0);
            ulong start = r.Clock.Now; r.Advance(124999); Check(r.Dsp.TransferredSamples == 0);
            r.Advance(1); Check(r.Dsp.DacValue == 0x20 && r.Dsp.RemainingSamples == 2 && !r.Dsp.InterruptAsserted);
            r.Advance(125000); Check(r.Dsp.DacValue == 0x80); r.Advance(125000);
            Check(r.Dsp.DacValue == 0xe0 && r.Dsp.TransferredSamples == 3 && !r.Dsp.Active && r.Dma.IsTerminalCount(1));
            Check(r.Irqs.SequenceEqual(new[] { (start + 375000, true) }) && r.Dsp.CompletedBlocks == 1);
            Check(r.Dsp.Read(0x22a, 1) == 255 && r.Dsp.InterruptAsserted); r.Dsp.Read(0x22e, 1);
            Check(!r.Dsp.InterruptAsserted && r.Irqs[^1] == (r.Clock.Now, false));
        });
        test("SB DMA page wrap remains on the 8237 page", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0x11, 0x22, 0x33], address: 0x1ffff); r.Send(0x40, 131, 0x14, 2, 0);
            r.Advance(125000); Check(r.Dsp.DacValue == 0x11); r.Advance(125000); Check(r.Dsp.DacValue == 0x22);
            r.Advance(125000); Check(r.Dsp.DacValue == 0x33 && r.Dma.IsTerminalCount(1));
        });
        test("SB DSP block size and DMA auto-init buffer length are independent", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([10, 20, 30, 40], 0x59); r.Send(0x40, 131, 0x48, 1, 0, 0x1c);
            r.Advance(250000); Check(r.Dsp.DacValue == 20 && r.Dsp.CompletedBlocks == 1 && !r.Dma.IsTerminalCount(1));
            r.Dsp.Read(0x22e, 1); r.Advance(250000); Check(r.Dsp.DacValue == 40 && r.Dsp.CompletedBlocks == 2 && r.Dma.IsTerminalCount(1));
            r.Dsp.Read(0x22e, 1); r.Advance(250000); Check(r.Dsp.DacValue == 20 && r.Dsp.CompletedBlocks == 3 && r.Dsp.Active);
            Check(r.Irqs.Count(i => i.High) == 3);
        });
        test("SB auto-init exit completes the current block before stopping", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([10, 20, 30, 40], 0x59); r.Send(0x40, 131, 0x48, 3, 0, 0x1c);
            r.Advance(125000); r.Send(0xda); Check(r.Dsp.Active); r.Advance(374000);
            Check(!r.Dsp.Active && r.Dsp.TransferredSamples == 4 && r.Dsp.CompletedBlocks == 1);
            r.Advance(1000000); Check(r.Dsp.TransferredSamples == 4);
        });
        test("SB queued single-cycle command follows the current auto-init block", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([1, 2, 3, 4, 5, 6, 7, 8], 0x59); r.Send(0x40, 131, 0x48, 3, 0, 0x1c);
            r.Advance(125000); r.Send(0x14, 1, 0); r.Advance(372000);
            Check(r.Dsp.TransferredSamples == 4 && r.Dsp.RemainingSamples == 2 && r.Dsp.Active);
            r.Dsp.Read(0x22e, 1); r.Advance(250000);
            Check(!r.Dsp.Active && r.Dsp.TransferredSamples == 6 && r.Dsp.CompletedBlocks == 2);
        });
        test("SB unacknowledged block IRQs do not invent new PIC edges", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128], 0x59); r.Send(0x40, 131, 0x48, 0, 0, 0x1c);
            r.Advance(500000); Check(r.Dsp.InterruptRequests == 4 && r.Irqs.Count == 1 && r.Dsp.InterruptAsserted);
            r.Dsp.Read(0x22e, 1); r.Advance(125000); Check(r.Irqs.Count(i => i.High) == 2);
        });
        test("SB pause preserves remaining byte count and partial sample deadline", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([1, 2, 3]); r.Send(0x40, 131, 0x14, 2, 0);
            r.Advance(150000); r.Send(0xd0); Check(r.Dsp.Paused && r.Dsp.TransferredSamples == 1);
            r.Advance(10000000); Check(r.Dsp.RemainingSamples == 2); r.Send(0xd4);
            r.Advance(98999); Check(r.Dsp.TransferredSamples == 1); r.Advance(1); Check(r.Dsp.TransferredSamples == 2);
            r.Advance(125000); Check(r.Dsp.TransferredSamples == 3 && !r.Dsp.Active);
        });
        test("SB speaker-off gates sound while DMA and block interrupts continue", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0, 255], 0x59); r.Send(0xd1, 0x40, 131, 0x48, 1, 0, 0x1c);
            r.Advance(10000000); r.Send(0xd3); long before = r.Dsp.TransferredSamples; r.Advance(200000000);
            Check(r.Dsp.TransferredSamples == before + 1600 && r.Dsp.Active && !r.Dsp.SpeakerEnabled);
            Check(r.Samples().TakeLast(200).All(s => Math.Abs(s) < 1e-9));
        });
        test("SB masked DMA stalls without consuming DSP samples then resumes", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([42]); r.Dma.Write(0x0a, 5, 1); r.Send(0x40, 131, 0x14, 0, 0);
            r.Advance(1000000); Check(r.Dsp.RemainingSamples == 1 && !r.Dsp.InterruptAsserted && r.Dsp.LastDmaResult == DmaReadResult.Blocked);
            r.Dma.Write(0x0a, 1, 1); r.Advance(125000); Check(r.Dsp.DacValue == 42 && !r.Dsp.Active);
        });
        test("SB wrong-direction and verify DMA do not fabricate audio or change guest memory", () =>
        {
            foreach (byte mode in new byte[] { 0x45, 0x41, 0xc9 })
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([42], mode); r.Send(0x40, 131, 0x14, 0, 0); r.Advance(1000000);
                Check(r.Dsp.LastDmaResult == DmaReadResult.InvalidMode && r.Dsp.TransferredSamples == 0 && r.Memory.ReadByte(0x12000) == 42 && !r.Dma.IsTerminalCount(1));
            }
        });
        test("SB early DMA terminal count cannot falsely finish a larger DSP block", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([42]); r.Send(0x40, 131, 0x14, 2, 0); r.Advance(1000000);
            Check(r.Dsp.RemainingSamples == 2 && r.Dsp.TransferredSamples == 1 && r.Dsp.CompletedBlocks == 0);
        });
        test("SB reset cancels queued commands samples and stale initialization callbacks", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0, 255], 0x59); r.Send(0xd1, 0x40, 131, 0x48, 1, 0, 0x1c);
            r.Advance(250000); Check(r.Dsp.InterruptAsserted); r.Dsp.Write(0x22c, 0xd1, 1); r.Dsp.Write(0x226, 1, 1);
            long count = r.Dsp.TransferredSamples; r.Advance(3000); r.Dsp.Write(0x226, 0, 1); r.Advance(50000);
            r.Dsp.Write(0x226, 1, 1); r.Advance(3000); r.Dsp.Write(0x226, 0, 1); r.Advance(99999);
            Check(r.Dsp.Read(0x22e, 1) == 0); r.Advance(1); Check(r.Dsp.Read(0x22a, 1) == 0xaa && r.Dsp.Read(0x22e, 1) == 0);
            Check(!r.Dsp.Active && !r.Dsp.InterruptAsserted && !r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 128 && r.Dsp.TransferredSamples == count);
        });
        test("SB timed DAC pause counts sample periods without reading DMA", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x40, 131, 0x80, 2, 0); r.Advance(374999);
            Check(r.Dsp.Active && !r.Dsp.InterruptAsserted); r.Advance(1);
            Check(!r.Dsp.Active && r.Dsp.InterruptAsserted && r.Dsp.TransferredSamples == 0 && r.Dsp.CompletedBlocks == 1);
        });
        test("SB time constant uses microsecond periods independently of 48-kHz host output", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0, 255], 0x59); r.Send(0x40, 165, 0x48, 255, 255, 0x1c);
            ulong start = r.Clock.Now; r.Advance(1_000_000_000);
            Check(r.Dsp.TransferredSamples == 10989 && r.Dsp.SamplePeriodNanoseconds == 91000);
            Check(r.Mixer.GeneratedFrames == (long)((UInt128)(start + 1_000_000_000) * 48000 / 1_000_000_000));
        });
        test("SB length FFFF means 65536 samples rather than an empty block", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma(new byte[65536]); r.Send(0x40, 131, 0x14, 255, 255);
            r.Advance(125000); Check(r.Dsp.TransferredSamples == 1 && r.Dsp.RemainingSamples == 65535 && !r.Dsp.InterruptAsserted);
        });
        test("SB missing MIDI commands are explicitly diagnosed without corrupting DSP framing", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x38, 0x10, 0x30);
            Check(r.Dsp.UnsupportedCommands == 2 && r.Dsp.LastUnsupportedCommand == 0x30 && !r.Dsp.Active);
            r.Send(0xe1); Check(r.Dsp.Read(0x22a, 1) == 2 && r.Dsp.Read(0x22a, 1) == 1);
        });
        test("mixed speaker and PCM equal independently integrated sources across chunk sizes", () =>
        {
            float[] Run(bool speaker, bool digital, bool chunks)
            {
                var r = new Rig(); var pit = new Pit8254(r.Clock, () => { }); var pc = new PcSpeaker(r.Clock, pit.SpeakerCounter, r.Mixer);
                r.Reset(); r.ProgramDma([0, 64, 128, 255], 0x59); r.Send(0x40, 131, 0x48, 3, 0, 0x1c);
                if (digital) r.Send(0xd1); else r.Send(0xd3);
                if (speaker) { pit.Write(0x43, 0xb6, 1); pit.Write(0x42, 0x98, 1); pit.Write(0x42, 0x0a, 1); pc.Write(0x61, 3, 1); }
                if (chunks) for (int i = 0; i < 100; i++) r.Advance(1000000); else r.Advance(100000000);
                return r.Samples();
            }
            float[] both = Run(true, true, false), chunked = Run(true, true, true), speaker = Run(true, false, false), digital = Run(false, true, false);
            Check(both.AsSpan().SequenceEqual(chunked));
            Check(both.Length == speaker.Length && both.Length == digital.Length);
            for (int i = 0; i < both.Length; i++) Check(Math.Abs(both[i] - speaker[i] - digital[i]) < 1e-7);
        });
        test("mixer source levels remain independent and hidden sources keep their device clocks", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0, 255], 0x59); r.Send(0xd1, 0x40, 131, 0x48, 1, 0, 0x1c);
            r.Dsp.Volume = 0; r.Advance(10000000); Check(r.Samples().All(s => s == 0) && r.Dsp.TransferredSamples == 80);
            r.Dsp.Volume = 0.5; r.Advance(10000000); Check(r.Samples().TakeLast(500).Any(s => Math.Abs(s) > 0.2));
            try { r.Dsp.Volume = double.NaN; throw new Exception("Invalid volume accepted."); } catch (ArgumentOutOfRangeException) { }
        });
        test("SB profile attachment uses configuration preflight and restart while rejecting unsupported cards", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo()); Check(session.Machine.SoundBlaster is null);
            var profile = session.Machine.Profile with { SoundCard = "sb2-dsp-development" };
            var prepare = session.Enqueue(new(1, "sb-plan", "PrepareConfigurationChange", JsonSerializer.SerializeToElement(profile, IpcProtocol.JsonOptions)));
            session.PumpCommands(); Check(prepare.Result.Success && session.Machine.SoundBlaster is null);
            var plan = new ConfigurationPlan(session.Revision, profile, true);
            var commit = session.Enqueue(new(1, "sb-commit", "CommitConfigurationChange", JsonSerializer.SerializeToElement(plan, IpcProtocol.JsonOptions)));
            session.PumpCommands(); Check(commit.Result.Success && session.Machine.SoundBlaster is not null && session.Machine.Ports.Read(0x22c, 1) == 0x80);
            try { (profile with { SoundCard = "sb16" }).Validate(); throw new Exception("Unsupported sound card accepted."); } catch (ArgumentException) { }
        });
        test("SB IRQ5 reaches the PIC and requires DSP acknowledgement plus PIC EOI", () =>
        {
            var clock = new DeviceScheduler(); var dma = new Dma8237(new(1024 * 1024)); var audio = new PcAudioMixer(clock); var pic = new Pic8259();
            var dsp = new SoundBlasterDsp(clock, dma, audio, high => pic.SetLine(5, high));
            dsp.Write(0x226, 1, 1); clock.AdvanceTo(3000); dsp.Write(0x226, 0, 1); clock.AdvanceTo(103000); dsp.Read(0x22a, 1);
            foreach (byte value in new byte[] { 0x40, 131, 0x80, 0, 0 }) { dsp.Write(0x22c, value, 1); clock.AdvanceTo(clock.Now + 1000); }
            pic.Write(0x21, 0xdf, 1); clock.AdvanceTo(clock.Now + 125000);
            Check(pic.HasPending && pic.Acknowledge() == 0x0d && dsp.InterruptAsserted); dsp.Read(0x22e, 1);
            Check(!dsp.InterruptAsserted && !pic.HasPending); pic.Write(0x20, 0x20, 1);
        });
    }
}
