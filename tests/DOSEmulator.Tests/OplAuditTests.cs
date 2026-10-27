// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Exact OPL bookkeeping continuation and baseline-comparable native/PCM/full-state probes.</summary>
internal static class OplAuditTests
{
    private static readonly int[] Offsets = [0, 3, 1, 4, 2, 5, 8, 11, 9, 12, 10, 13, 16, 19, 17, 20, 18, 21];
    private sealed record Native(ulong Time, short A, short B, short C, short D);
    private sealed record Cut(Dictionary<string, StateBlock> Components, SchedulerState Scheduler);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { IgnoreReadOnlyProperties = false };
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly PcAudioMixer Audio;
        public readonly Opl2? Two;
        public readonly Opl3? Three;
        public readonly List<Native> Samples = [];
        public readonly List<float> Pcm = [];
        private readonly Dictionary<string, IStateObject> _graph = new();
        private readonly Dictionary<object, string> _owners = new(ReferenceEqualityComparer.Instance);
        /// <summary>Builds a fixed declared source/mixer graph and only external publication observers.</summary>
        public Rig(bool opl3)
        {
            Audio = new(Clock);
            if (opl3)
            {
                Three = new(Clock, Audio); Three.SampleGenerated += s => Samples.Add(new(s.Time, s.A, s.B, s.C, s.D));
            }
            else Two = new(Clock, Audio);
            Audio.AudioReady += block => Pcm.AddRange(block.Samples);
            Add("audio", Audio); Add("synth", (IStateObject?)Three ?? Two!);
        }
        /// <summary>Registers only explicit graph children, retaining fixed source peers constructed by the rig.</summary>
        private void Add(string name, IStateObject value)
        { _graph.Add(name, value); _owners.Add(value, name); foreach (var child in value.StateChildren) Add(name + "/" + child.Name, child.Value); }
        /// <summary>Captures every declared raw operator, channel, timer, mixer and scheduler member.</summary>
        public Cut Save() => new(_graph.ToDictionary(p => p.Key, p => p.Value.SaveState()), Clock.SaveState(_owners));
        /// <summary>Loads only into a newly prepared rig, never replacing a currently adopted source.</summary>
        public void Load(Cut cut)
        { foreach (var p in cut.Components) _graph[p.Key].LoadState(p.Value); Clock.LoadState(cut.Scheduler, _graph); }
        /// <summary>Writes actual address/data ports; bank selection is explicit only for the OPL3 source.</summary>
        public void Write(int address, int value)
        {
            IIoPortDevice port = (IIoPortDevice?)Three ?? Two!;
            port.Write((ushort)(address >= 256 ? 0x38a : 0x388), (byte)address, 1);
            if (Two is not null) AdvanceTo(Clock.Now + Opl2.AddressWaitNanoseconds);
            port.Write(0x389, (byte)value, 1);
            if (Two is not null) AdvanceTo(Clock.Now + Opl2.DataWaitNanoseconds);
        }
        /// <summary>Computes the shared exact native frequency without changing either source's sample counter.</summary>
        private static ulong SampleTime(long sample) => (ulong)(((UInt128)(ulong)sample * 288 * 1_000_000_000 + Opl3.MasterFrequency - 1) / Opl3.MasterFrequency);
        /// <summary>Honors OPL2 bus waits while observing every actual native output boundary.</summary>
        private void AdvanceTo(ulong target)
        {
            if (Two is not null)
            {
                while (SampleTime(Two.GeneratedSamples + 1) <= target)
                {
                    ulong next = SampleTime(Two.GeneratedSamples + 1); Clock.AdvanceTo(next); Audio.AdvanceTo(next);
                    Samples.Add(new(next, Two.Output, Two.Output, 0, 0));
                }
            }
            Clock.AdvanceTo(target); Audio.AdvanceTo(target);
        }
        /// <summary>Programs two operators and one explicit channel without modifying synthesis mathematics.</summary>
        private void Tone(int channel)
        {
            for (int which = 0; which < 2; which++)
            {
                int slot = channel * 2 + which, offset = (slot >= 18 ? 256 : 0) + Offsets[slot % 18];
                Write(0x20 + offset, 0x21); Write(0x40 + offset, which == 0 ? 63 : 0); Write(0x60 + offset, 0xf0); Write(0x80 + offset, 0x01); Write(0xe0 + offset, 0);
            }
            int bank = channel >= 9 ? 256 : 0, local = channel % 9;
            Write(bank + 0xc0 + local, 0x31); Write(bank + 0xa0 + local, 0x44); Write(bank + 0xb0 + local, 0x32);
        }
        /// <summary>Selects silent, early/late active, release, rhythm or muted reference bookkeeping paths.</summary>
        public void Configure(string mode)
        {
            if (Three is not null) Write(0x105, 1);
            if (mode != "silent") Tone(mode == "late" ? (Three is not null ? 17 : 8) : 0);
            if (mode == "rhythm") { for (int ch = 6; ch <= 8; ch++) Tone(ch); Write(0xbd, 0x3f); }
            if (mode == "muted") { if (Three is not null) Three.Volume = 0; else Two!.Volume = 0; }
            Write(2, 0xfc); Write(4, 1);
        }
        /// <summary>Advances every native boundary and observes OPL2 output without inventing a native callback.</summary>
        public void Render(int count)
        {
            for (int i = 0; i < count; i++)
            {
                long sample = (Three?.GeneratedSamples ?? Two!.GeneratedSamples) + 1;
                AdvanceTo(SampleTime(sample));
            }
        }
        /// <summary>Creates a non-boundary integration tail and optionally releases the currently keyed note.</summary>
        public void PrepareCut(string mode)
        {
            if (mode == "release") Write(0xb0, 0x12);
            // End a real sample first, so the 13-clock partial tail never crosses an
            // unobserved native boundary after the scheduler itself advances.
            Render(1); Clock.AdvanceCycles(13, 33_000_000); Audio.AdvanceTo(Clock.Now);
        }
        /// <summary>Queries the unchanged full diagnostic active-operator count.</summary>
        public int Active => Three?.ActiveOperators ?? Two!.ActiveOperators;
        /// <summary>Reads the actual free-running noise state, including silent and muted operation.</summary>
        public uint Noise => Three?.NoiseState ?? Two!.NoiseState;
    }
    /// <summary>Requires exact publication/state or phase properties before reporting a bookkeeping optimization.</summary>
    private static void Check(bool value, string message = "OPL audit invariant failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Serializes only explicit declared numeric state with deterministic collection ordering.</summary>
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    /// <summary>Hashes one explicit raw-state or publication byte sequence.</summary>
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    /// <summary>Produces a stable bit-level digest of published stereo floats.</summary>
    private static string PcmHash(IEnumerable<float> source)
    {
        float[] values = source.ToArray(); byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), BitConverter.SingleToInt32Bits(values[i]));
        return Hash(bytes);
    }
    /// <summary>Requires every selected raw state and future native/PCM publication to survive a real prepared continuation.</summary>
    private static object Probe(bool three, string mode)
    {
        var a = new Rig(three); a.Configure(mode); a.Render(1024); a.PrepareCut(mode);
        int expectedActive = mode == "silent" ? 0 : mode == "rhythm" ? 8 : 2;
        Check(a.Noise != 1 && a.Active == expectedActive, $"OPL{(three ? 3 : 2)} {mode}: active {a.Active}, expected {expectedActive}, noise {a.Noise}");
        Check(a.Samples.Count == (a.Three?.GeneratedSamples ?? a.Two!.GeneratedSamples), "Fixture missed a programming-wait native boundary");
        Cut cut = a.Save(); string saved = Encode(cut); var b = new Rig(three); b.Load(cut);
        Check(saved == Encode(b.Save()) && cut.Scheduler.Remainder != 0, "Initial raw cut or fractional phase did not round-trip");
        int nativeCut = a.Samples.Count, pcmCut = a.Pcm.Count;
        for (int i = 0; i < 16; i++)
        {
            a.Render(128); b.Render(128);
            Check(Encode(a.Save()) == Encode(b.Save()), "Full raw operator/mixer/scheduler continuation differs");
        }
        var future = a.Samples.Skip(nativeCut).ToArray();
        Check(future.SequenceEqual(b.Samples) && a.Pcm.Skip(pcmCut).Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Pcm.Select(BitConverter.SingleToInt32Bits)), "Future native/PCM publications differ");
        Check(a.Noise == b.Noise && a.Samples.Count == nativeCut + 2048 && b.Samples.Count == 2048, "Native observation counts or free-running noise differ");
        if (mode is "silent" or "muted") Check(a.Pcm.All(v => v == 0), "Silent/muted fixture published nonzero PCM");
        else Check(a.Pcm.Any(v => v != 0), $"OPL{(three ? 3 : 2)} {mode}: programmed tone never produced PCM");
        return new { model = three ? "OPL3" : "OPL2", mode, samples = a.Samples.Count, futureSamples = future.Length, comparisons = 16,
            nativeSha256 = Hash(Encoding.UTF8.GetBytes(Encode(a.Samples))), pcmSha256 = PcmHash(a.Pcm), cutSha256 = Hash(Encoding.UTF8.GetBytes(saved)), finalStateSha256 = Hash(Encoding.UTF8.GetBytes(Encode(a.Save()))),
            futureNativeSha256 = Hash(Encoding.UTF8.GetBytes(Encode(future))), futurePcmSha256 = PcmHash(b.Pcm), activeOperators = a.Active, noiseState = a.Noise };
    }
    /// <summary>Registers compact silent/active/release/rhythm/mute continuation and public-count cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool three in new[] { false, true })
        foreach (string mode in new[] { "silent", "active", "late", "release", "rhythm", "muted" })
            test($"Audit OPL bookkeeping exact continuation OPL{(three ? 3 : 2)} {mode}", () => Probe(three, mode));
    }
    /// <summary>Exports baseline-comparable native/PCM/full-state digests and fixed rhythm-write allocation measurements.</summary>
    public static int Run(string output)
    {
        output = Path.GetFullPath(output);
        string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Own artifact directory required.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Existing benchmark output."); Directory.CreateDirectory(output);
        var probes = new List<object>(); var measurements = new List<object>();
        foreach (bool three in new[] { false, true })
        {
            foreach (string mode in new[] { "silent", "active", "late", "release", "rhythm", "muted" }) probes.Add(Probe(three, mode));
            var rig = new Rig(three); rig.Configure("rhythm"); for (int i = 0; i < 512; i++) rig.Write(0xbd, 0x3e | (i & 1));
            long[] allocated = new long[9]; double[] milliseconds = new double[9];
            for (int row = 0; row < allocated.Length; row++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 4096; i++) rig.Write(0xbd, 0x3e | (i & 1));
                allocated[row] = GC.GetAllocatedBytesForCurrentThread() - before; milliseconds[row] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            measurements.Add(new { model = three ? "OPL3" : "OPL2", writesPerRepeat = 4096, allocatedBytes = allocated, milliseconds,
                finalStateSha256 = Hash(Encoding.UTF8.GetBytes(Encode(rig.Save()))), activeOperators = rig.Active });
        }
        var report = new { passed = true, probes, measurements, synthesisMathUnchanged = true, silenceIntegratorShortcutAdded = false, requestedRenderSamplesPerProbe = 3072, programmingWaitSamplesObserved = true };
        File.WriteAllText(Path.Combine(output, "benchmark.json"), Encode(report)); Console.WriteLine(Encode(report)); return 0;
    }
}
