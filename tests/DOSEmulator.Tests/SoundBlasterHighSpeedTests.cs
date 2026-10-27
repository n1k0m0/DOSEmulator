// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;

internal static partial class SoundBlasterTests
{
    /// <summary>Registers legacy high-speed DSP transfer/reset regressions.</summary>
    public static void AddHighSpeedTo(Action<string, Action> test)
    {
        test("SB high-speed single uses the 48h block size and exact programmed sample period", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0x20, 0x80, 0xe0]); r.Send(0xd1, 0x40, 233, 0x48, 2, 0, 0x91);
            ulong start = r.Clock.Now; Check(r.Dsp.HighSpeed && r.Dsp.Read(0x22c, 1) == 0x80);
            r.Advance(22999); Check(r.Dsp.TransferredSamples == 0); r.Advance(1);
            Check(r.Dsp.DacValue == 0x20 && r.Dsp.RemainingSamples == 2 && !r.Dsp.InterruptAsserted);
            r.Advance(46000); Check(r.Dsp.HighSpeedSamples == 3 && r.Dsp.CompletedBlocks == 1 && !r.Dsp.Active && !r.Dsp.HighSpeed);
            Check(r.Irqs.SequenceEqual(new[] { (start + 69000, true) }) && r.Dma.IsTerminalCount(1));
        });
        test("SB 91h leaves high speed at block completion even with DMA auto-init", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([1, 2, 3, 4], 0x59); r.Send(0x40, 224, 0x48, 1, 0, 0x91);
            r.Advance(64000); Check(!r.Dsp.HighSpeed && r.Dsp.Read(0x22c, 1) == 0 && r.Dsp.TransferredSamples == 2);
            r.Send(0xe1); Check(r.Dsp.Read(0x22a, 1) == 2 && r.Dsp.Read(0x22a, 1) == 1);
            r.Advance(1000000); Check(r.Dsp.TransferredSamples == 2 && r.Dsp.CompletedBlocks == 1);
        });
        test("SB high-speed auto-init keeps DSP blocks separate from DMA buffers", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([10, 20, 30, 40], 0x59); r.Send(0x40, 224, 0x48, 1, 0, 0x90);
            for (int i = 0; i < 3; i++)
            {
                r.Advance(64000); Check(r.Dsp.HighSpeed && r.Dsp.CompletedBlocks == i + 1);
                Check(r.Dsp.DacValue == (i == 1 ? 40 : 20)); r.Dsp.Read(0x22e, 1);
            }
            Check(r.Dsp.TransferredSamples == 6 && r.Dsp.HighSpeedSamples == 6 && r.Irqs.Count(i => i.High) == 3);
        });
        test("SB high-speed mode ignores every command byte without parser or output side effects", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([64, 192], 0x59); r.Send(0xd1, 0x40, 233, 0x48, 255, 255, 0x90);
            byte[] ignored = [0xd0, 0xd4, 0xd3, 0xd8, 0xda, 0x40, 131, 0x48, 0, 0, 0x10, 0, 0xe1, 0x91, 0x99];
            foreach (byte value in ignored) { r.Dsp.Write(0x22c, value, 1); r.Advance(1000); }
            Check(r.Dsp.IgnoredHighSpeedWrites == ignored.Length && r.Dsp.RejectedWrites == 0 && r.Dsp.UnsupportedCommands == 0);
            Check(r.Dsp.HighSpeed && !r.Dsp.Paused && r.Dsp.SpeakerEnabled && r.Dsp.TimeConstant == 233 && r.Dsp.RemainingSamples == 65536);
            Check(r.Dsp.Read(0x22a, 1) == 0xff && r.Dsp.Read(0x22e, 1) == 0);
            r.Advance(23000); Check(r.Dsp.HighSpeedSamples == 1);
        });
        test("SB high-speed reset stops DMA while preserving time constant speaker block and DAC", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([30, 90, 160, 220], 0x59); r.Send(0xd1, 0x40, 233, 0x48, 1, 0, 0x90);
            r.Advance(69000); Check(r.Dsp.InterruptAsserted && r.Dsp.DacValue == 160);
            r.Reset(); Check(!r.Dsp.HighSpeed && !r.Dsp.Active && !r.Dsp.InterruptAsserted && r.Dsp.HighSpeedResets == 1);
            Check(r.Dsp.TimeConstant == 233 && r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 160 && r.Dsp.HighSpeedSamples == 3);
            // Normal 8-kHz auto-init must still use the retained two-byte DSP block.
            r.Send(0x40, 131, 0x1c); r.Advance(250000);
            Check(r.Dsp.CompletedBlocks == 2 && r.Dsp.TransferredSamples == 5 && r.Dsp.HighSpeedSamples == 3);
        });
        test("SB reset of active high-speed single also preserves configuration and cancels future samples", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([10, 20, 30, 40]); r.Send(0xd1, 0x40, 224, 0x48, 3, 0, 0x91);
            r.Advance(48000); r.Reset(); r.Advance(1000000);
            Check(r.Dsp.HighSpeedSamples == 1 && r.Dsp.TransferredSamples == 1 && r.Dsp.CompletedBlocks == 0 && r.Dsp.InterruptRequests == 0);
            Check(r.Dsp.HighSpeedResets == 1 && r.Dsp.TimeConstant == 224 && r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 10);
        });
        test("SB a second normal reset restores defaults after a high-speed escape", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([255], 0x59); r.Send(0xd1, 0x40, 233, 0x48, 0, 0, 0x90);
            r.Advance(23000); r.Reset(); r.Reset();
            Check(!r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 128 && r.Dsp.TimeConstant == 0 && r.Dsp.HighSpeedResets == 1 && r.Dsp.ResetCount == 3);
            r.Send(0x40, 131, 0x1c); r.Advance(125000); Check(r.Dsp.RemainingSamples == 65535);
        });
        test("SB repeated reset-high writes do not turn high-speed escape into a second full reset", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([64], 0x59); r.Send(0xd1, 0x40, 233, 0x48, 0, 0, 0x90);
            r.Dsp.Write(0x226, 1, 1); r.Advance(3000); r.Dsp.Write(0x226, 1, 1); r.Advance(3000); r.Dsp.Write(0x226, 0, 1); r.Advance(100000);
            Check(r.Dsp.Read(0x22a, 1) == 0xaa && r.Dsp.SpeakerEnabled && r.Dsp.TimeConstant == 233 && r.Dsp.ResetCount == 2 && r.Dsp.HighSpeedResets == 1);
        });
        test("SB high-speed abort of an incomplete block does not invent completion or IRQ", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([11, 22, 33, 44], 0x59); r.Send(0x40, 233, 0x48, 255, 255, 0x90);
            r.Advance(100000); r.Reset(); r.Advance(1000000);
            Check(r.Dsp.HighSpeedSamples == 4 && r.Dsp.CompletedBlocks == 0 && r.Dsp.InterruptRequests == 0 && !r.Dsp.InterruptAsserted);
        });
        test("SB high-speed stays busy across DMA masking and resumes on a sample deadline", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([42, 84]); r.Dma.Write(0x0a, 5, 1); r.Send(0x40, 233, 0x48, 1, 0, 0x91);
            r.Advance(1000000); Check(r.Dsp.HighSpeed && r.Dsp.RemainingSamples == 2 && r.Dsp.CompletedBlocks == 0 && r.Dsp.Read(0x22c, 1) == 0x80);
            r.Dma.Write(0x0a, 1, 1); r.Advance(46000); Check(!r.Dsp.HighSpeed && r.Dsp.HighSpeedSamples == 2 && r.Dsp.CompletedBlocks == 1);
        });
        test("SB early DMA terminal count cannot exit high speed or finish a larger DSP block", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([42]); r.Send(0x40, 233, 0x48, 2, 0, 0x91); r.Advance(1000000);
            Check(r.Dsp.HighSpeed && r.Dsp.RemainingSamples == 2 && r.Dsp.HighSpeedSamples == 1 && r.Dsp.CompletedBlocks == 0);
        });
        test("SB wrong DMA direction in high speed leaves memory and block counts unchanged", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0x42], 0x45); r.Send(0x40, 233, 0x48, 0, 0, 0x91); r.Advance(1000000);
            Check(r.Dsp.HighSpeed && r.Dsp.HighSpeedSamples == 0 && r.Dsp.CompletedBlocks == 0 && r.Memory.ReadByte(0x12000) == 0x42);
        });
        test("SB high-speed unacknowledged block IRQs do not invent PIC edges", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128], 0x59); r.Send(0x40, 233, 0x48, 0, 0, 0x90); r.Advance(230000);
            Check(r.Dsp.InterruptRequests == 10 && r.Irqs.Count == 1 && r.Dsp.HighSpeed);
            r.Dsp.Read(0x22e, 1); r.Advance(23000); Check(r.Irqs.Count(i => i.High) == 2 && r.Dsp.HighSpeed);
        });
        test("SB high-speed FFFF block means 65536 bytes", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128], 0x59); r.Send(0x40, 233, 0x48, 255, 255, 0x91); r.Advance(23000);
            Check(r.Dsp.RemainingSamples == 65535 && r.Dsp.HighSpeed && r.Dsp.CompletedBlocks == 0);
        });
        test("SB high-speed 43-kHz device clock remains independent of 48-kHz host output", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0, 255], 0x59); r.Send(0x40, 233, 0x48, 255, 255, 0x90);
            ulong start = r.Clock.Now; r.Advance(1000000000);
            Check(r.Dsp.HighSpeedSamples == 43478 && r.Dsp.SamplePeriodNanoseconds == 23000);
            Check(r.Mixer.GeneratedFrames == (long)((UInt128)(start + 1000000000) * 48000 / 1000000000));
        });
        test("SB high-speed with speaker disabled still consumes DMA and signals its block IRQ", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([64, 192]); r.Send(0x40, 233, 0x48, 1, 0, 0x91); r.Advance(10000000);
            Check(r.Dsp.HighSpeedSamples == 2 && r.Dsp.CompletedBlocks == 1 && r.Dsp.InterruptAsserted && r.Samples().All(s => s == 0));
        });
        test("SB high-speed waveform is identical across arbitrary host chunk sizes", () =>
        {
            // Renders the current guest text/graphics state into a complete caller-owned pixel surface.
            float[] Render(bool chunked)
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([0, 64, 128, 255], 0x59); r.Send(0xd1, 0x40, 233, 0x48, 3, 0, 0x90);
                if (chunked) for (int i = 0; i < 100; i++) r.Advance(100000); else r.Advance(10000000);
                return r.Samples();
            }
            Check(Render(true).AsSpan().SequenceEqual(Render(false)));
        });
        test("SB high-speed ignored parameters cannot corrupt subsequent normal commands", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128], 0x59); r.Send(0x40, 233, 0x48, 255, 255, 0x90);
            r.Dsp.Write(0x22c, 0x14, 1); r.Dsp.Write(0x22c, 255, 1); r.Reset();
            r.Send(0xe1, 0xd8); Check(r.Dsp.Read(0x22a, 1) == 2 && r.Dsp.Read(0x22a, 1) == 1 && r.Dsp.Read(0x22a, 1) == 0);
            Check(r.Dsp.UnsupportedCommands == 0 && r.Dsp.IgnoredHighSpeedWrites == 2);
        });
    }
}
