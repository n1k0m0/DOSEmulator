using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static partial class Opl2Tests
{
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly PcAudioMixer Mixer;
        public readonly Opl2 Opl;
        public readonly List<AudioBlock> Audio = [];
        public Rig() { Mixer = new(Clock); Opl = new(Clock, Mixer); Mixer.AudioReady += Audio.Add; }
        public void Advance(ulong delta) { Clock.AdvanceTo(Clock.Now + delta); Mixer.AdvanceTo(Clock.Now); }
        public ulong Write(byte register, byte value, ushort port = 0x388)
        {
            Opl.Write(port, register, 1); Advance(Opl2.AddressWaitNanoseconds);
            ulong committed = Clock.Now; Opl.Write((ushort)(port + 1), value, 1); Advance(Opl2.DataWaitNanoseconds); return committed;
        }
        public void Tone(int channel = 0, int fnum = 580, int block = 4, byte control = 0x21, byte attackDecay = 0xf0, byte sustainRelease = 0x0f)
        {
            int[] offsets = [0,1,2,8,9,10,16,17,18]; int m = offsets[channel], c = m + 3;
            Write((byte)(0x20 + m), 1); Write((byte)(0x60 + m), 0); // silent modulator, still a real keyed slot
            Write((byte)(0x20 + c), control); Write((byte)(0x40 + c), 0); Write((byte)(0x60 + c), attackDecay);
            Write((byte)(0x80 + c), sustainRelease); Write((byte)(0xa0 + channel), (byte)fnum);
            Write((byte)(0xb0 + channel), (byte)(0x20 | block << 2 | fnum >> 8));
        }
        public float[] Samples() { Mixer.Flush(); return Audio.SelectMany(b => b.Samples).ToArray(); }
        public double RmsTail(int count = 4800)
        { float[] samples = Samples(); return Math.Sqrt(samples.TakeLast(count * 2).Select(x => (double)x * x).Average()); }
    }
    private static void Check(bool condition, string message = "OPL assertion failed.") { if (!condition) throw new Exception(message); }
    private static double Pitch(float[] samples, int start = 4800)
    {
        var rises = new List<int>();
        for (int i = Math.Max(2, start * 2); i < samples.Length; i += 2) if (samples[i - 2] < 0 && samples[i] >= 0) rises.Add(i / 2);
        Check(rises.Count > 10); return (rises.Count - 1) * 48000.0 / (rises[^1] - rises[0]);
    }
    public static void AddTo(Action<string, Action> test)
    {
        test("OPL cold state is silent and data port is write-only", () =>
        {
            var r = new Rig(); r.Advance(100000000); Check(r.Opl.Read(0x388, 1) == 6 && r.Opl.Read(0x389, 1) == 255);
            Check(r.Opl.Read(0x388, 2) == 255 && r.Opl.ActiveOperators == 0 && r.Samples().All(s => s == 0));
        });
        test("OPL address and data writes enforce distinct master-clock wait intervals", () =>
        {
            var r = new Rig(); r.Opl.Write(0x388, 2, 1); r.Advance(Opl2.AddressWaitNanoseconds - 1);
            r.Opl.Write(0x389, 255, 1); Check(r.Opl.RejectedWrites == 1 && r.Opl.InspectRegister(2) == 0);
            r.Advance(1); r.Opl.Write(0x389, 255, 1); r.Advance(Opl2.DataWaitNanoseconds - 1); r.Opl.Write(0x388, 3, 1);
            Check(r.Opl.RejectedWrites == 2 && r.Opl.InspectRegister(2) == 255); r.Advance(1); r.Write(3, 254);
            Check(r.Opl.InspectRegister(3) == 254);
        });
        test("OPL timer one overflows on its rational master-clock deadline and status reads preserve flags", () =>
        {
            var r = new Rig(); r.Write(2, 255); ulong start = r.Write(4, 1);
            ulong masterStart = (ulong)(((UInt128)start * Opl2.MasterFrequency + 999999999) / 1000000000);
            ulong deadline = (ulong)(((UInt128)(masterStart + 288) * 1000000000 + Opl2.MasterFrequency - 1) / Opl2.MasterFrequency);
            r.Advance(deadline - r.Clock.Now - 1); Check(r.Opl.Read(0x388, 1) == 6); r.Advance(1);
            Check(r.Opl.Read(0x388, 1) == 0xc6 && r.Opl.Read(0x388, 1) == 0xc6 && r.Opl.Timer1Overflows == 1);
        });
        test("OPL timer two has four times the timer-one tick period", () =>
        {
            var r = new Rig(); r.Write(2, 255); r.Write(3, 255); r.Write(4, 3); r.Advance(100000);
            Check(r.Opl.Timer1Overflows == 1 && r.Opl.Timer2Overflows == 0); r.Advance(250000);
            Check(r.Opl.Timer1Overflows == 4 && r.Opl.Timer2Overflows == 1 && r.Opl.Read(0x388, 1) == 0xe6);
        });
        test("OPL timer IRQ reset ignores lower control bits and keeps counters running", () =>
        {
            var r = new Rig(); r.Write(2, 255); r.Write(4, 1); r.Advance(100000); Check(r.Opl.TimerInterruptAsserted);
            r.Write(4, 0x80); Check(r.Opl.Read(0x388, 1) == 6 && r.Opl.InspectRegister(4) == 1);
            r.Advance(100000); Check(r.Opl.Timer1Overflows >= 2 && r.Opl.TimerInterruptAsserted);
            r.Write(4, 0); r.Write(4, 0x80); long count = r.Opl.Timer1Overflows; r.Advance(1000000);
            Check(!r.Opl.TimerInterruptAsserted && r.Opl.Timer1Overflows == count);
        });
        test("OPL timer masks clear flags without stopping timer overflow counts", () =>
        {
            var r = new Rig(); r.Write(2, 255); r.Write(4, 0x41); r.Advance(1000000);
            Check(r.Opl.Timer1Overflows > 10 && r.Opl.Read(0x388, 1) == 6); r.Write(4, 1); r.Advance(100000);
            Check(r.Opl.TimerInterruptAsserted); r.Write(4, 0x41); Check(!r.Opl.TimerInterruptAsserted);
        });
        test("OPL timer preload changes apply at reload rather than rewriting the active countdown", () =>
        {
            var r = new Rig(); r.Write(2, 250); r.Write(4, 1); r.Write(2, 255); r.Advance(300000);
            Check(r.Opl.Timer1Overflows == 0); r.Advance(250000); Check(r.Opl.Timer1Overflows is >= 1 and <= 2);
            r.Advance(250000); Check(r.Opl.Timer1Overflows >= 4);
        });
        test("OPL shared address latch works across the Sound Blaster FM alias", () =>
        {
            var r = new Rig(); r.Opl.Write(0x228, 2, 1); r.Advance(Opl2.AddressWaitNanoseconds);
            r.Opl.Write(0x389, 255, 1); r.Advance(Opl2.DataWaitNanoseconds); r.Write(4, 1, 0x228); r.Advance(100000);
            Check(r.Opl.InspectRegister(2) == 255 && r.Opl.Read(0x228, 1) == r.Opl.Read(0x388, 1));
        });
        test("OPL native sample count follows master divided by 72 independently of host samples", () =>
        {
            var r = new Rig(); r.Advance(1000000000);
            Check(r.Opl.GeneratedSamples == Opl2.MasterFrequency / 72 && r.Mixer.GeneratedFrames == 48000);
        });
        test("OPL melodic pitch follows F-number block and multiplier", () =>
        {
            foreach ((int fnum, int block, byte mul) in new[] { (580, 4, (byte)1), (580, 5, (byte)1), (580, 4, (byte)2), (580, 4, (byte)0) })
            {
                var r = new Rig(); r.Tone(fnum: fnum, block: block, control: (byte)(0x20 | mul)); r.Advance(300000000);
                double multiplier = mul == 0 ? 0.5 : mul;
                double expected = Opl2.MasterFrequency / 72.0 * fnum * (1 << block) / (1 << 20) * multiplier;
                Check(Math.Abs(Pitch(r.Samples()) - expected) < 0.3, "OPL frequency differs from programmed phase increment.");
            }
        });
        test("OPL all nine channels decode their own operator register pairs", () =>
        {
            for (int ch = 0; ch < 9; ch++)
            {
                var r = new Rig(); r.Tone(ch); r.Advance(50000000); Check(r.RmsTail(1000) > 0.07 && r.Opl.KeyOnCount == 2);
                for (int other = 0; other < 9; other++) Check(r.Opl.InspectOperator(other, true).Keyed == (other == ch));
            }
        });
        test("OPL rendering is independent of arbitrary host chunks with immutable published blocks", () =>
        {
            var a = new Rig(); var b = new Rig(); a.Tone(); b.Tone(); a.Advance(100000000);
            for (int i = 0; i < 1000; i++) b.Advance(100000);
            Check(a.Samples().AsSpan().SequenceEqual(b.Samples())); float[] saved = a.Audio[0].Samples.ToArray(); a.Advance(10000000);
            Check(a.Audio[0].Samples.AsSpan().SequenceEqual(saved));
        });
        test("OPL held key writes and frequency changes do not retrigger envelopes", () =>
        {
            var r = new Rig(); r.Tone(attackDecay: 0x50); r.Advance(10000000); double before = r.Opl.InspectOperator(0, true).AttenuationDb;
            r.Write(0xb0, 0x32); r.Write(0xa0, 0x50); r.Advance(10000000);
            Check(r.Opl.KeyOnCount == 2 && r.Opl.InspectOperator(0, true).AttenuationDb < before);
            r.Write(0xb0, 0x12); r.Write(0xb0, 0x32); Check(r.Opl.KeyOnCount == 4);
        });
        test("OPL zero attack rate remains silent until a nonzero rate is programmed", () =>
        {
            var r = new Rig(); r.Tone(attackDecay: 0); r.Advance(100000000); Check(r.Samples().All(s => s == 0));
            r.Write(0x63, 0xf0); r.Advance(50000000); Check(r.RmsTail(1000) > 0.07);
        });
        test("OPL sustained envelopes hold their level then release on key-off", () =>
        {
            var r = new Rig(); r.Tone(attackDecay: 0xff, sustainRelease: 0x4f); r.Advance(30000000);
            OplOperatorStatus state = r.Opl.InspectOperator(0, true); Check(state.Stage == OplEnvelopeStage.Sustain && Math.Abs(state.AttenuationDb - 12) < 0.01);
            r.Advance(100000000); Check(Math.Abs(r.Opl.InspectOperator(0, true).AttenuationDb - 12) < 0.01);
            r.Write(0xb0, 0x12); r.Advance(10000000); Check(r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Off);
        });
        test("OPL percussion envelope type decays after sustain level even while key stays high", () =>
        {
            var r = new Rig(); r.Tone(control: 1, attackDecay: 0xff, sustainRelease: 0x4f); r.Advance(30000000);
            Check(r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Off && r.Opl.InspectOperator(0, true).Keyed);
            r.Write(0xb0, 0x32); r.Advance(1000000); Check(r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Off);
        });
        test("OPL key scaling accelerates envelope rate while total level changes only output gain", () =>
        {
            var a = new Rig(); var b = new Rig(); a.Tone(control: 0x21, attackDecay: 0x50); b.Tone(control: 0x31, attackDecay: 0x50);
            a.Advance(10000000); b.Advance(10000000);
            Check(b.Opl.InspectOperator(0, true).AttenuationDb < a.Opl.InspectOperator(0, true).AttenuationDb * 0.5);
            var r = new Rig(); r.Tone(); r.Advance(100000000); double loud = r.RmsTail(); r.Write(0x43, 8); r.Advance(100000000);
            Check(Math.Abs(r.RmsTail() / loud - Math.Pow(10, -6.0 / 20)) < 0.005);
        });
        test("OPL attack duration follows the documented full-range rate-table anchor", () =>
        {
            // AR5 + key-scale offset2: Yamaha rate 5/2 = 117.76 ms at 3.6 MHz,
            // approximately 118.43 ms with this board's crystal.
            var r = new Rig(); r.Tone(attackDecay: 0x50); r.Advance(117000000);
            Check(r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Attack);
            r.Advance(3000000); Check(r.Opl.InspectOperator(0, true).Stage != OplEnvelopeStage.Attack);
        });
        test("OPL key-scale level modes apply the documented octave attenuation", () =>
        {
            var r = new Rig(); r.Tone(block: 5); r.Advance(100000000); double original = r.RmsTail();
            foreach ((byte value, double db) in new[] { ((byte)0x40,12.75), ((byte)0x80,6.375), ((byte)0xc0,25.5) })
            {
                r.Write(0x43, value); r.Advance(100000000);
                Check(Math.Abs(r.RmsTail() / original - Math.Pow(10, -db / 20)) < 0.005);
            }
        });
        test("OPL half-wave rectified and quarter-wave outputs have their documented sign and gaps", () =>
        {
            for (byte wave = 0; wave < 4; wave++)
            {
                var r = new Rig(); r.Tone(); r.Write(1, 0x20); r.Write(0xe3, wave);
                var values = new List<short>();
                for (int i = 0; i < 1200; i++) { r.Advance(20115); values.Add(r.Opl.Output); }
                if (wave == 0) Check(values.Any(v => v > 0) && values.Any(v => v < 0));
                else Check(values.All(v => v >= 0));
                double zeros = values.Count(v => v == 0) / (double)values.Count;
                Check(wave is 1 or 3 ? zeros is > 0.45 and < 0.55 : zeros < 0.02);
            }
        });
        test("OPL waveform select is gated globally and changes harmonic content when enabled", () =>
        {
            var a = new Rig(); var b = new Rig(); a.Tone(); b.Tone(); a.Write(0xe3, 2); b.Write(0xe3, 0);
            a.Advance(100000000); b.Advance(100000000); Check(a.Samples().AsSpan().SequenceEqual(b.Samples()));
            a.Write(1, 0x20); a.Advance(200000000); Check(Math.Abs(Pitch(a.Samples().TakeLast(9600).ToArray(), 100) - 880.125) < 1);
        });
        test("OPL FM connection and feedback affect the actual waveform", () =>
        {
            float[] Run(byte connection)
            {
                var r = new Rig(); r.Tone(); r.Write(0x20, 0x22); r.Write(0x60, 0xf0); r.Write(0xc0, connection);
                r.Advance(100000000); return r.Samples();
            }
            float[] fm = Run(0), additive = Run(1), feedback = Run(0x0e);
            Check(!fm.AsSpan().SequenceEqual(additive) && !fm.AsSpan().SequenceEqual(feedback));
            Check(fm.Any(s => Math.Abs(s) > 0.05) && additive.All(float.IsFinite) && feedback.All(float.IsFinite));
        });
        test("OPL AM and vibrato modulation change output without changing host sample count", () =>
        {
            var a = new Rig(); var b = new Rig(); a.Tone(control: 0xe1); b.Tone(control: 0x21); a.Advance(500000000); b.Advance(500000000);
            Check(a.Opl.GeneratedSamples == b.Opl.GeneratedSamples && !a.Samples().AsSpan().SequenceEqual(b.Samples()));
        });
        test("OPL reset silences voices and cancels pending timers without resetting machine time", () =>
        {
            var r = new Rig(); r.Tone(); r.Write(2, 255); r.Write(4, 1); r.Advance(10000000); long count = r.Opl.Timer1Overflows;
            ulong time = r.Clock.Now; r.Opl.Reset(); Check(r.Clock.Now == time && r.Opl.ActiveOperators == 0 && !r.Opl.TimerInterruptAsserted);
            r.Advance(200000000); Check(r.Opl.Timer1Overflows == count && r.RmsTail() < 1e-8);
        });
        test("OPL unsupported rhythm CSM and test programming stays explicit", () =>
        {
            var r = new Rig(); r.Tone(8); r.Write(0xbd, 0x20); r.Advance(100000000);
            Check(r.Opl.UnsupportedRhythm && r.Opl.UnsupportedProgramming && r.RmsTail(1000) < 1e-5);
            r.Write(0xbd, 0); r.Write(8, 0x80); Check(r.Opl.UnsupportedCsm); r.Write(8, 0); r.Write(1, 1); Check(r.Opl.UnsupportedTest);
        });
        test("OPL profiles map AdLib and shared SB aliases without coupling DSP reset", () =>
        {
            using var adlib = PcMachine.CreateDemo(new MachineProfile { SoundCard = "adlib-opl2-development" });
            Check(adlib.Opl is not null && adlib.SoundBlaster is null && adlib.Ports.Read(0x388, 1) == 6 && adlib.Ports.Read(0x228, 1) == 255);
            using var sb = PcMachine.CreateDemo(new MachineProfile { SoundCard = "sb2-opl2-development" });
            Check(sb.Opl is not null && sb.SoundBlaster is not null && sb.Ports.Read(0x228, 1) == 6);
            sb.Ports.Write(0x228, 2, 1); sb.Scheduler.AdvanceTo(sb.Scheduler.Now + Opl2.AddressWaitNanoseconds);
            sb.Ports.Write(0x389, 255, 1); sb.Ports.Write(0x226, 1, 1);
            Check(sb.Opl!.InspectRegister(2) == 255);
        });
    }
}
