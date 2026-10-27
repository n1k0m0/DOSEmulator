// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;

internal static partial class SoundBlasterTests
{
    /// <summary>Registers virtual ADC and actual device-paced recording/playback regressions.</summary>
    public static void AddRecordingTo(Action<string, Action> test)
    {
        test("SB direct ADC returns unconnected midpoint or the virtual source at command completion without DMA", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0x20);
            Check(r.Dsp.Read(0x22a, 1) == 128 && r.Dsp.ConvertedSamples == 1 && r.Dsp.RecordedSamples == 0);
            ulong sampled = 0; r.Dsp.AdcInput = now => { sampled = now; return 0x39; };
            r.Send(0x20); Check(sampled == r.Clock.Now && r.Dsp.Read(0x22a, 1) == 0x39);
            Check(r.Dsp.DacValue == 128 && !r.Dsp.InterruptAsserted && !r.Dsp.Active);
        });
        test("SB input DMA takes one ADC sample per real guest sample period and writes only its target bytes", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc,0xcc], 0x45);
            var times = new List<ulong>(); r.Dsp.AdcInput = now => { times.Add(now); return (byte)(now / 125000); };
            r.Send(0x40,131,0x24,2,0); ulong start = r.Clock.Now;
            r.Advance(124999); Check(r.Dsp.RecordedSamples == 0 && r.Memory.ReadByte(0x12000) == 0xcc);
            r.Advance(1); Check(r.Dsp.RecordedSamples == 1 && r.Dsp.Recording && !r.Dsp.InterruptAsserted);
            r.Advance(250000);
            Check(times.SequenceEqual(new[] { start+125000,start+250000,start+375000 }));
            for (int i = 0; i < 3; i++) Check(r.Memory.ReadByte((uint)(0x12000+i)) == (byte)(times[i]/125000));
            Check(r.Dsp.RecordedSamples == 3 && r.Dsp.TransferredSamples == 0 && !r.Dsp.Recording && !r.Dsp.Active);
            Check(r.Dsp.CompletedBlocks == 1 && r.Dsp.InterruptAsserted && r.Dma.IsTerminalCount(1));
            Check(r.Memory.ReadByte(0x11fff) == 0 && r.Memory.ReadByte(0x12003) == 0);
        });
        test("SB ADC never routes captured input into the DAC or changes its held output", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc,0xcc], 0x45);
            r.Dsp.AdcInput = _ => 0; r.Send(0xd1,0x10,0xe0,0x40,131,0x24,2,0); r.Advance(500000);
            Check(r.Dsp.DacValue == 0xe0 && r.Dsp.AdcValue == 0 && r.Dsp.RecordedSamples == 3 && r.Dsp.TransferredSamples == 0);
            Check(r.Samples().TakeLast(8).All(value => value > 0.35));
        });
        test("SB masked and controller-disabled recording stalls without memory writes or invented completion", () =>
        {
            foreach (bool mask in new[] { true,false })
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc,0xcc], 0x45);
                if (mask) r.Dma.Write(0x0a,5,1); else r.Dma.Write(8,4,1);
                r.Send(0x40,131,0x24,2,0); r.Advance(500000);
                Check(r.Dsp.ConvertedSamples == 4 && r.Dsp.RecordedSamples == 0 && r.Dsp.RemainingSamples == 3);
                Check(r.Memory.ReadByte(0x12000) == 0xcc && !r.Dsp.InterruptAsserted && r.Dsp.LastDmaResult == DmaReadResult.Blocked);
                if (mask) r.Dma.Write(0x0a,1,1); else r.Dma.Write(8,0,1);
                r.Advance(125000); Check(r.Dsp.RecordedSamples == 1 && r.Memory.ReadByte(0x12000) == 128);
            }
        });
        test("SB recording rejects verify/read/illegal/cascade DMA without consuming the buffer", () =>
        {
            foreach (byte mode in new byte[] {0x41,0x49,0x4d,0xc5})
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc], mode);
                r.Send(0x40,131,0x24,1,0); r.Advance(500000);
                Check(r.Dsp.LastDmaResult == DmaReadResult.InvalidMode && r.Dsp.RecordedSamples == 0 && r.Dsp.RemainingSamples == 2);
                Check(r.Memory.ReadByte(0x12000) == 0xcc && r.Memory.ReadByte(0x12001) == 0xcc && !r.Dsp.InterruptAsserted && !r.Dma.IsTerminalCount(1));
            }
        });
        test("SB recording honors 8237 page wrap and decrement addressing", () =>
        {
            foreach (bool decrement in new[] {false,true})
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc,0xcc], (byte)(decrement ? 0x65 : 0x45), 0x1ffff);
                byte value = 0; r.Dsp.AdcInput = _ => ++value; r.Memory.WriteByte(0x20000,0x5a);
                r.Send(0x40,131,0x24,2,0); r.Advance(375000);
                Check(r.Memory.ReadByte(0x1ffff) == 1);
                Check(r.Memory.ReadByte(decrement ? 0x1fffeu : 0x10000u) == 2 && r.Memory.ReadByte(decrement ? 0x1fffdu : 0x10001u) == 3);
                Check(r.Memory.ReadByte(0x20000) == 0x5a && r.Dsp.RecordedSamples == 3);
            }
        });
        test("SB short input DMA buffer stalls the remaining DSP count after terminal count", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc], 0x45); r.Send(0x40,131,0x24,3,0); r.Advance(750000);
            Check(r.Dsp.RecordedSamples == 2 && r.Dsp.RemainingSamples == 2 && !r.Dsp.InterruptAsserted && r.Dma.IsTerminalCount(1));
        });
        test("SB recording auto-init separates DSP blocks from DMA buffers and needs IRQ acknowledgement", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma(new byte[6], 0x55); byte value = 0; r.Dsp.AdcInput = _ => ++value;
            r.Send(0x40,131,0x48,2,0,0x2c); r.Advance(750000);
            Check(r.Dsp.RecordedSamples == 6 && r.Dsp.CompletedBlocks == 2 && r.Irqs.Count(e => e.High) == 1);
            r.Dsp.Read(0x22e,1); r.Advance(375000);
            Check(r.Irqs.Count(e => e.High) == 2 && r.Dsp.InterruptRequests == 3);
            Check(r.Memory.Ram.AsSpan(0x12000,6).SequenceEqual(new byte[] {7,8,9,4,5,6}));
            r.Send(0xda); r.Advance(375000); Check(r.Dsp.RecordedSamples == 12 && r.Dsp.CompletedBlocks == 4 && !r.Dsp.Active);
        });
        test("SB recording pause/resume retains the fractional time to the next ADC transfer", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc], 0x45); r.Send(0x40,131,0x24,1,0);
            r.Advance(50000); r.Send(0xd0); r.Advance(10000000); Check(r.Dsp.Paused && r.Dsp.RecordedSamples == 0);
            r.Send(0xd4); r.Advance(73999); Check(r.Dsp.RecordedSamples == 0); r.Advance(1); Check(r.Dsp.RecordedSamples == 1);
        });
        test("SB normal reset aborts queued ADC parameters and pending sample writes", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc], 0x45); r.Send(0x40,131,0x24,1,0);
            r.Advance(1000); r.Reset(); r.Advance(500000);
            Check(r.Dsp.RecordedSamples == 0 && !r.Dsp.Recording && r.Memory.ReadByte(0x12000) == 0xcc);
            r.Send(0x24,0); r.Reset(); r.Send(0x20); Check(r.Dsp.Read(0x22a,1) == 128 && r.Dsp.RecordedSamples == 0);
        });
        test("SB high-speed input single-cycle uses the input rate and exits after exactly one block", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([0xcc,0xcc,0xcc], 0x45);
            r.Send(0x40,188,0x48,2,0,0x99); Check(r.Dsp.HighSpeed && r.Dsp.Recording && r.Dsp.Read(0x22c,1) == 128);
            r.Advance(67999); Check(r.Dsp.RecordedSamples == 0); r.Advance(1); r.Advance(136000);
            Check(r.Dsp.RecordedSamples == 3 && r.Dsp.HighSpeedRecordedSamples == 3 && r.Dsp.HighSpeedSamples == 0 && !r.Dsp.Active && !r.Dsp.HighSpeed);
            Check(r.Dsp.InterruptAsserted && r.Dsp.CompletedBlocks == 1 && r.Dsp.Read(0x22c,1) == 0);
            r.Send(0xe1); Check(r.Dsp.Read(0x22a,1) == 2 && r.Dsp.Read(0x22a,1) == 1);
        });
        test("SB high-speed recording ignores commands and reset preserves TC/block/speaker/DAC", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma(new byte[6], 0x55); r.Send(0xd1,0x10,170,0x40,188,0x48,2,0,0x98);
            foreach (byte value in new byte[] {0xd0,0xd4,0xda,0xd3,0x20,0x24,0,0}) r.Dsp.Write(0x22c,value,1);
            r.Advance(408000); Check(r.Dsp.RecordedSamples == 6 && r.Dsp.CompletedBlocks == 2 && r.Dsp.Recording);
            r.Reset(); Check(r.Dsp.HighSpeedResets == 1 && r.Dsp.TimeConstant == 188 && r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 170 && !r.Dsp.Active);
            Check(r.Dsp.IgnoredHighSpeedWrites == 8 && r.Dsp.UnsupportedCommands == 0);
            r.Send(0x2c); r.Advance(204000); Check(r.Dsp.RecordedSamples == 9 && r.Dsp.CompletedBlocks == 3);
            r.Reset(); Check(r.Dsp.TimeConstant == 0 && !r.Dsp.SpeakerEnabled && r.Dsp.DacValue == 128);
        });
        test("SB queued single-cycle requests retain the recording/playback direction at block boundaries", () =>
        {
            foreach (bool recordFirst in new[] {false,true})
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([10,20], (byte)(recordFirst ? 0x55 : 0x59));
                r.Send(0x40,131,0x48,1,0,(byte)(recordFirst ? 0x2c : 0x1c)); r.Advance(125000);
                r.Send((byte)(recordFirst ? 0x14 : 0x24),2,0); r.Advance(122000);
                Check(r.Dsp.CompletedBlocks == 1 && r.Dsp.RemainingSamples == 3 && r.Dsp.Recording != recordFirst);
                r.ProgramDma([30,40,50], (byte)(recordFirst ? 0x49 : 0x45)); r.Advance(375000);
                Check(!r.Dsp.Active && r.Dsp.CompletedBlocks == 2);
                Check(r.Dsp.RecordedSamples == (recordFirst ? 2 : 3) && r.Dsp.TransferredSamples == (recordFirst ? 3 : 2));
            }
        });
        test("SB input accepts the full FFFF-encoded 65536-byte block without wrapping its DSP counter", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma(new byte[65536], 0x45, 0x10000); r.Send(0x40,131,0x24,255,255);
            r.Advance(8192000000); Check(r.Dsp.RecordedSamples == 65536 && r.Dsp.CompletedBlocks == 1 && !r.Dsp.Active && r.Dma.IsTerminalCount(1));
            Check(r.Memory.Ram.AsSpan(0x10000,65536).ToArray().All(value => value == 128));
        });
        test("SB ADC traces distinguish direct conversion, successful DMA and blocked conversion", () =>
        {
            var r = new Rig(); r.Reset(); var conversions = new List<(ulong Time,byte Value,DmaReadResult? Result)>();
            r.Dsp.AdcSampled += (time,value,result) => conversions.Add((time,value,result));
            r.Send(0x20); r.Dsp.Read(0x22a,1); r.ProgramDma([0xcc],0x45); r.Send(0x40,131,0x24,1,0); r.Advance(250000);
            Check(conversions.Count == 3 && conversions[0].Result is null && conversions[1].Result == DmaReadResult.Transferred && conversions[2].Result == DmaReadResult.Blocked);
            Check(r.Dsp.ConvertedSamples == 3 && r.Dsp.RecordedSamples == 1);
        });
    }
}
