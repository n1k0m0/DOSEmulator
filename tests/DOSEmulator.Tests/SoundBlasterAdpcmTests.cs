using DOSEmulator.Hardware;

internal static partial class SoundBlasterTests
{
    public static void AddAdpcmTo(Action<string, Action> test)
    {
        byte[] encoded = [0x77,0x77,0xff,0xf0,0x00,0xe3,0x55,0xaa,0xff,0x00,0x99,0x66];
        // Independently expanded DOSLIB hardware-observation vectors, including
        // saturation, sign, decreasing step and the special last 3/3/2 code.
        byte[][] expected = [
            [129,126,132,120,144,96,144,96,48,0,0,0,0,0,16,24,28,30,31,31,30,29,29,28,31,37,49,73,57,49,45,43,40,34,22,0,16,24,28,30,29,30,29,30,33,31,34,32],
            [131,128,123,130,124,114,100,72,47,12,7,11,13,14,14,11,12,10,12,11,13,12,14,14,11,4,0,2,3,3,3,1,3,6,9,8],
            [135,150,180,240,180,120,60,64,66,67,61,68,79,101,81,61,1,0,4,6,3,0,13,39]
        ];
        foreach (int bits in new[] { 2, 3, 4 })
        {
            int count = CreativeAdpcmDecoder.SamplesPerByte(bits);
            byte single = bits == 2 ? (byte)0x17 : bits == 3 ? (byte)0x77 : (byte)0x75;
            byte auto = bits == 2 ? (byte)0x1f : bits == 3 ? (byte)0x7f : (byte)0x7d;
            test($"SB ADPCM {bits}-bit independent decoded vector and compressed-byte accounting", () =>
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([128, .. encoded]);
                r.Send(0x40,131,single,(byte)encoded.Length,0);
                r.Advance(125000); Check(r.Dsp.DacValue == 128 && r.Dsp.AdpcmDecodedSamples == 1);
                foreach (byte value in expected[bits - 2]) { r.Advance(125000); Check(r.Dsp.DacValue == value); }
                Check(!r.Dsp.Active && r.Dsp.CompletedBlocks == 1 && r.Dsp.AdpcmDmaBytes == 13
                    && r.Dsp.TransferredSamples == 13 && r.Dma.IsTerminalCount(1));
                r.Advance(1000000); Check(r.Dsp.AdpcmDecodedSamples == 1 + count * encoded.Length);
            });
            test($"SB ADPCM {bits}-bit buffered codes survive DMA mask and TC without premature IRQ", () =>
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([128,0xff]); r.Send(0x40,131,single,1,0);
                r.Advance(250000); Check(r.Dma.IsTerminalCount(1) && r.Dsp.Active && r.Dsp.CompletedBlocks == 0);
                r.Dma.Write(0x0a,5,1); r.Advance((ulong)(count - 1) * 125000 - 1);
                Check(!r.Dsp.InterruptAsserted); r.Advance(1);
                Check(r.Dsp.InterruptAsserted && !r.Dsp.Active && r.Dsp.AdpcmDecodedSamples == count + 1);
            });
            test($"SB ADPCM {bits}-bit auto-init has one reference and resets adaptation at block boundaries", () =>
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([128,0],0x59); r.Send(0x40,131,0x48,1,0,auto);
                r.Advance((ulong)(1 + count) * 125000); Check(r.Dsp.CompletedBlocks == 1 && r.Dsp.DacValue == 128);
                r.Dsp.Read(0x22e,1); r.Send(0xda); r.Advance((ulong)(2 * count) * 125000 - 1000);
                Check(r.Dsp.CompletedBlocks == 2 && !r.Dsp.Active && r.Dsp.AdpcmDmaBytes == 4
                    && r.Dsp.AdpcmDecodedSamples == 1 + 3 * count && r.Dsp.DacValue != 128);
            });
            test($"SB ADPCM {bits}-bit no-reference retains predictor but restarts step", () =>
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([192]); r.Send(0x40,131,single,0,0); r.Advance(125000);
                r.Dsp.Read(0x22e,1); r.ProgramDma([0]); r.Send((byte)(single-1),0,0); r.Advance(125000);
                Check(r.Dsp.DacValue == (bits == 2 ? 208 : bits == 3 ? 197 : 196));
            });
        }
        test("SB ADPCM pause preserves buffered code and fractional DAC deadline", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128,0x77]); r.Send(0x40,131,0x75,1,0);
            r.Advance(275000); r.Send(0xd0); Check(r.Dsp.DacValue == 135);
            r.Advance(10000000); Check(r.Dsp.AdpcmDecodedSamples == 2); r.Send(0xd4);
            r.Advance(98999); Check(r.Dsp.DacValue == 135); r.Advance(1); Check(r.Dsp.DacValue == 150 && !r.Dsp.Active);
        });
        test("SB ADPCM reset discards packed tail and pending IRQ without stale DAC output", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128,0x55]); r.Send(0x40,131,0x17,1,0); r.Advance(250000);
            r.Reset(); long decoded = r.Dsp.AdpcmDecodedSamples; r.Advance(1000000);
            Check(!r.Dsp.Active && r.Dsp.DacValue == 128 && r.Dsp.AdpcmDecodedSamples == decoded && !r.Dsp.InterruptAsserted);
        });
        test("SB ADPCM queued PCM follows final decoded code, and queued ADPCM follows PCM", () =>
        {
            var r = new Rig(); r.Reset(); r.ProgramDma([128,0x77,0x11,0x22],0x59);
            r.Send(0x40,131,0x48,1,0,0x7d); r.Advance(125000); r.Send(0x14,0,0);
            r.Advance(247000); Check(r.Dsp.CompletedBlocks == 1 && r.Dsp.AdpcmDecodedSamples == 3 && r.Dsp.DacValue == 150);
            r.Dsp.Read(0x22e,1); r.Advance(125000); Check(r.Dsp.DacValue == 0x11 && !r.Dsp.Active);
            r.ProgramDma([128,0,0,0],0x59); r.Send(0x1c); r.Send(0x75,0,0); r.Advance(247000);
            Check(r.Dsp.AdpcmBits == 4); r.Advance(125000); Check(!r.Dsp.Active && r.Dsp.DacValue == 0);
        });
        test("SB ADPCM invalid DMA directions do not consume reference or produce decoded samples", () =>
        {
            foreach (byte mode in new byte[] { 0x45,0x41,0xc9 })
            {
                var r = new Rig(); r.Reset(); r.ProgramDma([192],mode); r.Send(0x75,0,0); r.Advance(1000000);
                Check(r.Dsp.Active && r.Dsp.AdpcmDmaBytes == 0 && r.Dsp.AdpcmDecodedSamples == 0 && r.Dsp.DacValue == 128);
            }
        });
    }
}
