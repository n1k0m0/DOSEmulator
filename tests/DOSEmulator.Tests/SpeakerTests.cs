using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DOSEmulator.Cpu.X86;

internal static class SpeakerTests
{
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly Pit8254 Pit;
        public readonly PcSpeaker Speaker;
        public readonly List<AudioBlock> Audio = [];
        public int Irqs;
        public Rig() { Pit = new(Clock, () => Irqs++); Speaker = new(Clock, Pit.SpeakerCounter); Speaker.AudioReady += Audio.Add; }
        public void Program(int mode, ushort count, int access = 3)
        {
            Pit.Write(0x43, (uint)(0x80 | access << 4 | mode << 1), 1);
            if (access != 2) Pit.Write(0x42, (byte)count, 1);
            if (access != 1) Pit.Write(0x42, (byte)(count >> 8), 1);
        }
        public void Tick(ulong tick) => Clock.AdvanceTo((ulong)(((UInt128)tick * 1_000_000_000 + PitSpeakerCounter.Frequency - 1) / PitSpeakerCounter.Frequency));
        public void To(ulong time) { Clock.AdvanceTo(time); Speaker.AdvanceTo(time); }
        public float[] Samples() { Speaker.Flush(); return Audio.SelectMany(b => b.Samples).ToArray(); }
    }
    private static void Check(bool condition, string message = "Speaker assertion failed.") { if (!condition) throw new Exception(message); }
    private static ushort Count(Rig r)
    {
        r.Pit.Write(0x43, 0x80, 1); uint lo = r.Pit.Read(0x42, 1), hi = r.Pit.Read(0x42, 1); return (ushort)(lo | hi << 8);
    }
    public static void AddTo(Action<string, Action> test)
    {
        test("PIT2 mode three emits even and odd duty cycles on input-clock boundaries", () =>
        {
            foreach (ushort n in new ushort[] { 4, 5 })
            {
                var r = new Rig(); r.Program(3, n); r.Speaker.Write(0x61, 3, 1);
                for (ulong tick = 1; tick <= 3UL * n; tick++)
                {
                    r.Tick(tick); ulong phase = (tick - 1) % n;
                    Check(r.Pit.SpeakerCounter.Output == (phase < (uint)(n + 1) / 2));
                    int inHalf = phase < (uint)(n + 1) / 2 ? (int)phase : (int)phase - (n + 1) / 2;
                    Check(Count(r) == (n & ~1) - inHalf * 2, "Mode 3 count must decrement by two, including the odd-count zero interval.");
                }
            }
        });
        test("PIT2 read back latches status before count and exposes null count", () =>
        {
            var r = new Rig(); r.Program(2, 4); r.Speaker.Write(0x61, 1, 1);
            r.Pit.Write(0x43, 0xe8, 1); Check(r.Pit.Read(0x42, 1) == 0xf4);
            r.Tick(1); r.Pit.Write(0x43, 0xc8, 1); r.Tick(4);
            Check(r.Pit.Read(0x42, 1) == 0xb4 && r.Pit.Read(0x42, 1) == 4 && r.Pit.Read(0x42, 1) == 0);
            Check(!r.Pit.SpeakerCounter.Output && Count(r) == 1); r.Tick(5); Check(r.Pit.SpeakerCounter.Output && Count(r) == 4);
        });
        test("PIT2 repeated count latch does not overwrite unread bytes", () =>
        {
            var r = new Rig(); r.Program(2, 0x1234); r.Speaker.Write(0x61, 1, 1); r.Tick(1);
            r.Pit.Write(0x43, 0x80, 1); Check(r.Pit.Read(0x42, 1) == 0x34); r.Tick(0x200);
            r.Pit.Write(0x43, 0x80, 1); Check(r.Pit.Read(0x42, 1) == 0x12 && Count(r) != 0x1234);
        });
        test("PIT2 low-only high-only and zero reload use the selected byte format", () =>
        {
            var r = new Rig(); r.Program(2, 0x1200, 2); r.Tick(1); Check(r.Pit.Read(0x42, 1) == 0x12);
            r.Program(2, 0x34, 1); r.Tick(2); Check(r.Pit.Read(0x42, 1) == 0x34);
            r.Program(3, 0); r.Speaker.Write(0x61, 1, 1); r.Tick(3); Check(r.Pit.SpeakerCounter.Reload == 65536 && Count(r) == 0);
        });
        test("port 61 speaker gate and output readback are independent of writable control bits", () =>
        {
            var r = new Rig(); r.Program(3, 4); r.Speaker.Write(0x61, 0xff, 1); r.Tick(3);
            Check((r.Speaker.Read(0x61, 1) & 0x2f) == 15 && r.Pit.SpeakerCounter.Gate);
            r.Speaker.Write(0x61, 14, 1); Check((r.Speaker.Read(0x61, 1) & 0x2f) == 46);
            r.Tick(20); r.Speaker.Write(0x61, 15, 1); r.Tick(21); Check(r.Pit.SpeakerCounter.Output); r.Tick(23); Check(!r.Pit.SpeakerCounter.Output);
        });
        test("PIT2 mode zero pauses under low gate then resumes to terminal output", () =>
        {
            var r = new Rig(); r.Program(0, 5); r.Speaker.Write(0x61, 1, 1); r.Tick(3); Check(Count(r) == 3 && !r.Pit.SpeakerCounter.Output);
            r.Speaker.Write(0x61, 0, 1); r.Tick(20); Check(Count(r) == 3 && !r.Pit.SpeakerCounter.Output);
            r.Speaker.Write(0x61, 1, 1); r.Tick(21); Check(Count(r) == 3); r.Tick(23); Check(!r.Pit.SpeakerCounter.Output);
            r.Tick(24); Check(r.Pit.SpeakerCounter.Output && Count(r) == 0);
        });
        test("PIT2 count rewrite takes effect at the next mode-three half cycle", () =>
        {
            var r = new Rig(); r.Program(3, 8); r.Speaker.Write(0x61, 1, 1); r.Tick(2);
            r.Pit.Write(0x42, 4, 1); r.Pit.Write(0x42, 0, 1); r.Tick(4); Check(r.Pit.SpeakerCounter.Output && Count(r) == 2);
            r.Tick(5); Check(!r.Pit.SpeakerCounter.Output && Count(r) == 4); r.Tick(7); Check(r.Pit.SpeakerCounter.Output && Count(r) == 4);
        });
        test("PIT2 unsupported BCD and modes remain explicit without stopping channel zero", () =>
        {
            var r = new Rig(); r.Pit.Write(0x43, 0xb7, 1); Check(r.Pit.UnsupportedProgramming);
            r.Program(1, 100); Check(r.Pit.UnsupportedProgramming); r.Pit.Write(0x43, 0x34, 1); r.Pit.Write(0x40, 0xa9, 1); r.Pit.Write(0x40, 4, 1);
            r.To(1_000_000_000); Check(r.Irqs == 1000); r.Program(3, 4); Check(!r.Pit.UnsupportedProgramming);
        });
        test("speaker generates exactly 48000 stereo frames per virtual second", () =>
        {
            var r = new Rig(); r.Program(3, 2712); r.Speaker.Write(0x61, 3, 1); r.To(1_000_000_000);
            float[] samples = r.Samples(); Check(r.Speaker.GeneratedFrames == 48000 && samples.Length == 96000);
            for (int i = 0; i < samples.Length; i += 2) Check(float.IsFinite(samples[i]) && samples[i] == samples[i + 1] && Math.Abs(samples[i]) <= 0.251);
            int rises = 0; for (int i = 4802; i < samples.Length; i += 2) if (samples[i - 2] < 0 && samples[i] >= 0) rises++;
            Check(rises is >= 416 and <= 420, "Measured speaker pitch must match the actual PIT divisor.");
            Check(r.Audio[0].VirtualNanoseconds == 0 && r.Audio[^1].VirtualNanoseconds == 990_000_000);
        });
        test("speaker PCM is independent of rendering chunk size and retains published blocks", () =>
        {
            var a = new Rig(); var b = new Rig(); a.Program(3, 1193); b.Program(3, 1193); a.Speaker.Write(0x61, 3, 1); b.Speaker.Write(0x61, 3, 1);
            a.To(25_000_000); for (ulong ns = 1_000_000; ns <= 25_000_000; ns += 1_000_000) b.To(ns);
            float[] first = a.Audio[0].Samples.ToArray(); Check(a.Samples().AsSpan().SequenceEqual(b.Samples()));
            a.To(30_000_000); Check(a.Audio[0].Samples.AsSpan().SequenceEqual(first));
        });
        test("speaker integrates direct port pulses shorter than one output sample", () =>
        {
            var r = new Rig(); r.To(500); r.Speaker.Write(0x61, 2, 1); r.To(600); r.Speaker.Write(0x61, 0, 1); r.To(1_000_000);
            float[] samples = r.Samples(); double expected = Math.Exp(-2 * Math.PI * 20 / 48000) * 0.25 * 100 / 20834;
            Check(Math.Abs(samples[0] - expected) < 1e-8 && samples[2] < 0, "A sub-sample pulse must retain its time-integrated energy.");
        });
        test("speaker digital DC decays and disabled speaker becomes silent", () =>
        {
            var r = new Rig(); r.Speaker.Write(0x61, 2, 1); r.To(200_000_000); r.Speaker.Write(0x61, 0, 1); r.To(400_000_000);
            float[] samples = r.Samples(); Check(samples[0] > 0.24 && Math.Abs(samples[18000]) < 1e-9 && samples[19200] < -0.24 && Math.Abs(samples[^1]) < 1e-9);
        });
        test("speaker ultrasonic square wave is integrated without scheduling every input edge", () =>
        {
            var r = new Rig(); r.Program(3, 2); r.Speaker.Write(0x61, 3, 1); r.To(100_000_000);
            Check(r.Clock.NextEventTime is null && r.Speaker.GeneratedFrames == 4800);
            Check(r.Samples().TakeLast(200).All(x => Math.Abs(x) < 0.01));
        });
        test("host PCM clear discards queued sound without changing guest time", () =>
        {
            var buffer = new AudioRingBuffer(48000, 2, 4800); buffer.Write(new(0, 48000, 2, [0.2f, 0.2f])); buffer.Clear();
            float[] samples = new float[2]; buffer.Read(samples); Check(buffer.BufferedFrames == 0 && samples.All(x => x == 0));
        });
        test("PIT read back can select both implemented channels without replacing unread latches", () =>
        {
            var r = new Rig(); r.Pit.Write(0x43, 0x34, 1); r.Pit.Write(0x40, 0x34, 1); r.Pit.Write(0x40, 0x12, 1);
            r.Program(3, 4); r.Speaker.Write(0x61, 1, 1); r.Tick(1); r.Pit.Write(0x43, 0xca, 1);
            Check(r.Pit.Read(0x40, 1) == 0xb4 && r.Pit.Read(0x42, 1) == 0xb6); Check(r.Pit.Read(0x40, 1) == 0x33);
            r.Tick(0x200); r.Pit.Write(0x43, 0xda, 1); Check(r.Pit.Read(0x40, 1) == 0x12 && r.Pit.Read(0x42, 1) == 4 && r.Pit.Read(0x42, 1) == 0);
        });
        test("PIT mode zero partial write inhibits an already pending load until the high byte", () =>
        {
            var r = new Rig(); r.Program(0, 4); r.Speaker.Write(0x61, 1, 1); r.Pit.Write(0x42, 8, 1);
            r.Tick(20); Check(!r.Pit.SpeakerCounter.Output); r.Pit.Write(0x42, 0, 1); r.Tick(21); Check(Count(r) == 8);
            r.Tick(29); Check(r.Pit.SpeakerCounter.Output);
        });
        test("private WAV recorder creates standard stereo PCM and never overwrites an existing file", () =>
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "speaker-" + Guid.NewGuid().ToString("N") + ".wav");
            string? hash;
            using (var recording = new PcmWaveRecorder(path))
            {
                recording.Write(new(0, 48000, 2, [-1, 1, 0, 0.5f])); recording.Complete(); Check(recording.Frames == 2);
                hash = recording.DataSha256;
            }
            byte[] bytes = File.ReadAllBytes(path); Check(bytes.Length == 52 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) == 48000 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)) == 2);
            Check(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)) == -32767 && BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50)) == 16384);
            Check(hash == Convert.ToHexString(SHA256.HashData(bytes.AsSpan(44))));
            byte[] original = File.ReadAllBytes(path);
            try { using var duplicate = new PcmWaveRecorder(path); throw new Exception("Existing recording was overwritten."); } catch (IOException) { }
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
        });
        test("guest BIOS BEL drives the real PIT speaker and preserves the text cursor and port control", () =>
        {
            using var machine = PcMachine.CreateDemo(); machine.RunSteps(100000);
            var blocks = new List<AudioBlock>(); machine.Speaker.AudioReady += blocks.Add;
            ushort cursor = machine.Memory.ReadWord(0x450); ulong before = machine.Scheduler.Now;
            machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.Eip = 0x1000; machine.Memory.WriteByte(0x1000, 0xf4);
            machine.Cpu.SetRegister(Register.Ax, 0x0e07); machine.Cpu.EnterRealModeInterrupt(0x10); machine.RunSteps(1_000_000);
            machine.Speaker.Flush();
            Check(machine.Fault is null && machine.Cpu.Halted && machine.Memory.ReadWord(0x450) == cursor && machine.Speaker.Control == 0);
            Check(machine.Scheduler.Now - before is >= 39_000_000 and <= 41_000_000);
            Check(blocks.SelectMany(b => b.Samples).Any(s => Math.Abs(s) > 0.1) && machine.Pit.SpeakerCounter.Reload == 1193);
        });
        test("timed machine slices keep PIT and speaker running while CLI HLT stops CPU instructions", () =>
        {
            using var machine = PcMachine.CreateDemo(); machine.RunSteps(100000);
            Check(machine.Cpu.Halted && !machine.Cpu.CanAcceptMaskableInterrupt);
            var blocks = new List<AudioBlock>(); machine.Speaker.AudioReady += blocks.Add;
            machine.Ports.Write(0x43, 0xb6, 1); machine.Ports.Write(0x42, 0x98, 1); machine.Ports.Write(0x42, 0x0a, 1); machine.Ports.Write(0x61, 3, 1);
            long instructions = machine.Cpu.Instructions; ulong target = machine.Scheduler.Now + 100_000_000;
            machine.RunSteps(10000, target); machine.Speaker.Flush();
            Check(machine.Scheduler.Now == target && machine.Cpu.Instructions == instructions && machine.Cpu.Halted);
            Check(blocks.SelectMany(b => b.Samples).Any(s => Math.Abs(s) > 0.1) && machine.Fault is null);
        });
    }
}
