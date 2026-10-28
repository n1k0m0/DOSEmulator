// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Raster-unit comparisons against the preoptimization byte algorithm; these are not Windows or command-reachability acceptance.</summary>
internal static class TrioEngineReadOptimizationTests
{
    private const int PopulatedBytes = 2 * 1024 * 1024;
    /// <summary>Fails on any wrong logical pixel, revision increment or unrelated saved field.</summary>
    private static void Check(bool value, string why = "Trio engine read optimization invariant")
    { if (!value) throw new Exception(why); }
    /// <summary>Sets one explicitly owned passive input field in the existing complete device-state shape.</summary>
    private static void Field<T>(StateBlock block, string name, T value) =>
        block.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json);
    /// <summary>Finds only the named private raster-unit fixture input, never a guest or host object graph.</summary>
    private static T Live<T>(VgaAdapter video, string name) =>
        (T)typeof(VgaAdapter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(video)!;
    /// <summary>Sets only a named mutable input of this private raster probe; this is deliberately not a state import or reachable command setup.</summary>
    private static void FixtureField<T>(VgaAdapter video, string name, T value)
    {
        FieldInfo field = typeof(VgaAdapter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(field.FieldType == typeof(T) && !field.IsInitOnly, "Unexpected private raster fixture input.");
        field.SetValue(video, value);
    }

    /// <summary>One own two-MiB raster unit, passive expected state and independent byte-addressed source/destination model.</summary>
    private sealed class Fixture
    {
        public readonly VgaAdapter Video = new(null, false, true);
        public readonly ushort[] Words;
        public readonly uint[] Colors;
        private readonly StateBlock _expected;
        private readonly byte[][] _reference, _actual;
        private readonly ushort[] _actualWords;
        private readonly uint[] _actualColors;
        private readonly MethodInfo _pixel = typeof(VgaAdapter).GetMethod("TrioEnginePixel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private long _revision = 0x12345;
        private readonly int _bytes;
        /// <summary>Seeds own artwork and raw raster-unit sentinels after construction, without invoking or claiming product state import.</summary>
        public Fixture(int bytes)
        {
            _bytes = bytes;
            _reference = Video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
            _actual = Live<byte[][]>(Video, "_planes");
            for (int p = 0; p < 4; p++)
            {
                for (int i = 0; i < _reference[p].Length; i++)
                    _reference[p][i] = unchecked((byte)((i * 13 + p * 71) ^ (i >> 8)));
                _reference[p].CopyTo(_actual[p], 0);
            }
            var words = new ushort[32]; words[26] = (ushort)bytes; words[27] = 640;
            words[16] = 0x10; words[9] = words[10] = 4095;
            _actualWords = Live<ushort[]>(Video, "_trioEngineActiveWords");
            _actualColors = Live<uint[]>(Video, "_trioEngineActiveColors"); words.CopyTo(_actualWords, 0);
            FixtureField(Video, "_pixelRevision", _revision);
            new byte[] { 0x19, 0x97, 0x38, 0xad }.CopyTo(Live<byte[]>(Video, "_latch"), 0);
            // Deliberately distinct raster-unit sentinels: the pixel helper must not consume or clear transfer/read state.
            // Their aggregate is not a reachable command phase. Product validators remain responsible for rejecting it.
            FixtureField(Video, "_trioEngineActive", true); FixtureField(Video, "_trioEnginePixelIndex", 5);
            FixtureField(Video, "_trioEngineInputData", 0x3125u); FixtureField(Video, "_trioEngineInputBytes", (byte)2);
            FixtureField(Video, "_trioEngineReadAddress", (ushort)0xbee8);
            FixtureField(Video, "_trioEngineReadLatch", (ushort)0x6d37); FixtureField(Video, "_trioEngineReadValid", true);
            _expected = Video.SaveState();
            Words = (ushort[])_actualWords.Clone(); Colors = (uint[])_actualColors.Clone();
        }

        /// <summary>Uses literal populated DRAM and its pulled-up out-of-range response, without device read helpers.</summary>
        private static byte Read(byte[][] planes, int at) =>
            at >= 0 && at < PopulatedBytes ? planes[at % 4][at / 4] : (byte)255;
        /// <summary>Uses the previously implemented source/destination MiB selection and ordinary row-major little-endian pixel layout.</summary>
        private int Address(int x, int y, bool source)
        {
            int selected = source ? (Words[13] / 16) & 7 : Words[13] & 7;
            if (selected == 0) selected = source ? (Words[12] / 4) & 3 : Words[12] & 3;
            return selected * 1048576 + (((y & 4095) * Words[27]) + (x & 4095)) * _bytes;
        }
        /// <summary>Joins the exact independent source bytes, including a partial pixel at the physical DRAM end.</summary>
        private uint ReadPixel(int at)
        {
            uint value = 0;
            for (int b = 0; b < _bytes; b++) value |= (uint)Read(_reference, at + b) << (8 * b);
            return value;
        }
        /// <summary>Evaluates all 16 ROPs from an independent four-entry Boolean truth table for each physical bit.</summary>
        private static uint Mix(uint source, uint destination, int operation)
        {
            ReadOnlySpan<byte> table = [5, 0, 15, 10, 3, 6, 9, 12, 7, 11, 13, 14, 8, 4, 2, 1];
            uint value = 0;
            for (int bit = 0; bit < 32; bit++)
            {
                int entry = (int)(((source >> bit) & 1) * 2 + ((destination >> bit) & 1));
                if ((table[operation] & (1 << entry)) != 0) value |= 1u << bit;
            }
            return value;
        }
        /// <summary>Retains the former unconditional bitmap and destination reads before compare and masking.</summary>
        public uint Mixed(int x, int y, int sx, int sy, uint cpu, bool mono, out uint current)
        {
            uint limit = _bytes == 4 ? uint.MaxValue : (1u << (_bytes * 8)) - 1;
            uint bitmap = ReadPixel(Address(sx, sy, true)), readMask = Colors[3] & limit;
            int control = Words[11] & 0xc0;
            bool foreground = control == 0 || control == 0x80 && mono || control == 0xc0 && (bitmap & readMask) == readMask;
            int mix = Words[foreground ? 30 : 29];
            uint source = ((mix / 32) & 3) switch { 0 => Colors[0], 1 => Colors[1], 2 => cpu, _ => bitmap & readMask };
            current = ReadPixel(Address(x, y, false));
            return Mix(source, current, mix & 15) & limit;
        }
        /// <summary>Runs the old byte algorithm with original clipping, pre-mask comparison and per-changed-byte revision increments.</summary>
        private void Reference(int x, int y, int sx, int sy, uint cpu, bool mono)
        {
            if ((Words[16] & 0x10) == 0) return;
            bool inside = x >= Words[8] && x <= Words[10] && y >= Words[7] && y <= Words[9];
            if (inside == ((Words[12] & 0x20) != 0)) return;
            uint result = Mixed(x, y, sx, sy, cpu, mono, out uint current);
            uint limit = _bytes == 4 ? uint.MaxValue : (1u << (_bytes * 8)) - 1;
            if ((Words[12] & 0x100) != 0)
            {
                bool equal = result == (Colors[4] & limit);
                if (equal != ((Words[12] & 0x80) != 0)) return;
            }
            uint mask = Colors[2] & limit; result = (current & ~mask) | (result & mask);
            int at = Address(x, y, false);
            for (int b = 0; b < _bytes; b++)
            {
                int target = at + b; byte value = (byte)(result >> (b * 8));
                if (target >= 0 && target < PopulatedBytes && Read(_reference, target) != value)
                { _reference[target % 4][target / 4] = value; _revision++; }
            }
        }
        /// <summary>Checks every individual target byte and revision immediately, preventing later overlapping writes from hiding an error.</summary>
        public void Probe(int x, int y, int sx, int sy, uint cpu, bool mono)
        {
            Words.CopyTo(_actualWords, 0); Colors.CopyTo(_actualColors, 0);
            Reference(x, y, sx, sy, cpu, mono);
            _pixel.Invoke(Video, [x, y, sx, sy, cpu, mono]);
            int at = Address(x, y, false);
            for (int b = 0; b < _bytes; b++) Check(Read(_actual, at + b) == Read(_reference, at + b), "Individual target byte differs.");
            Check(Live<long>(Video, "_pixelRevision") == _revision, "Individual pixel revision differs.");
            Check(_actualWords.SequenceEqual(Words) && _actualColors.SequenceEqual(Colors), "Pixel changed a latched command input.");
        }
        /// <summary>Compares all two-MiB plane bytes and every saved video field, including untouched input, latch and read phases.</summary>
        public void Complete()
        {
            Field(_expected, "_planes", _reference); Field(_expected, "_pixelRevision", _revision);
            Field(_expected, "_trioEngineActiveWords", Words); Field(_expected, "_trioEngineActiveColors", Colors);
            StateBlock actual = Video.SaveState();
            Check(actual.Type == _expected.Type && actual.Version == _expected.Version &&
                actual.Fields.Keys.SequenceEqual(_expected.Fields.Keys), "Saved video shape differs.");
            foreach (string name in _expected.Fields.Keys)
                Check(actual.Fields[name].GetRawText() == _expected.Fields[name].GetRawText(), "Saved video field differs: " + name);
        }
    }

    /// <summary>Registers all ROPs, masks, source choices, mono/bitmap selectors, compare outcomes, banks and overlapping read futures.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int bytes in new[] { 1, 2, 4 })
        {
            for (int operation = 0; operation < 16; operation++)
            {
                int rop = operation;
                test($"Trio read optimization complete byte/reference state depth={bytes} rop={rop}", () =>
                {
                    var f = new Fixture(bytes); int sequence = 0;
                    uint limit = bytes == 4 ? uint.MaxValue : (1u << (bytes * 8)) - 1;
                    for (int source = 0; source < 4; source++)
                    foreach (int control in new[] { 0, 0x40, 0x80, 0xc0 })
                    foreach (uint mask in new[] { 0u, limit, 0x5a5a5a5au & limit })
                    foreach (int compare in new[] { 0, 0x100, 0x180 })
                    foreach (bool mono in new[] { false, true })
                    {
                        int x = 9 + (sequence & 3), y = 7 + ((sequence >> 2) & 3), sx = 2 + ((sequence >> 4) & 3), sy = 1 + ((sequence >> 6) & 3);
                        uint cpu = 0xc39a5713u ^ (uint)sequence * 0x10203u;
                        f.Words[11] = (ushort)control; f.Words[12] = (ushort)compare;
                        f.Words[30] = (ushort)((source << 5) | rop);
                        f.Words[29] = (ushort)((((source + 1) & 3) << 5) | ((rop + 5) & 15));
                        f.Colors[0] = 0x128f365au ^ (uint)sequence; f.Colors[1] = 0x89abcdefu ^ ((uint)sequence << 8);
                        f.Colors[2] = mask; f.Colors[3] = (sequence & 2) == 0 ? limit : 0x0f0f0f0fu;
                        f.Colors[4] = f.Mixed(x, y, sx, sy, cpu, mono, out _) ^ (uint)(sequence & 1);
                        f.Probe(x, y, sx, sy, cpu, mono); sequence++;
                    }
                    Check(sequence == 288, "The complete source/control/mask/compare/mono matrix was not run.");
                    f.Complete();
                });
            }
            test($"Trio read optimization legacy/extended MiB and unpopulated DRAM depth={bytes}", () =>
            {
                var f = new Fixture(bytes); f.Words[30] = 0x67; f.Colors[2] = f.Colors[3] = uint.MaxValue;
                foreach (int source in Enumerable.Range(0, 8))
                foreach (int destination in Enumerable.Range(0, 8))
                {
                    f.Words[12] = 6; f.Words[13] = (ushort)((source << 4) | destination);
                    f.Words[11] = (ushort)(((source + destination) & 1) == 0 ? 0 : 0xc0);
                    f.Words[29] = 0x47; f.Colors[4] = 0x9712a85c;
                    f.Probe(0, 0, 4, 0, 0xe135ab79, true);
                    f.Probe(639, 0, 638, 0, 0x79ab35e1, false);
                    f.Probe(4095, 4095, 4094, 4095, 0xa35e197b, true);
                    f.Probe(2, 1, 4095, 4095, 0x39f107b5, false);
                }
                f.Complete();
            });
            foreach (int dx in new[] { -1, 1 })
            foreach (int dy in new[] { -1, 1 })
                test($"Trio read optimization ordered overlapping bitmap future depth={bytes} dx={dx} dy={dy}", () =>
                {
                    var f = new Fixture(bytes); f.Words[30] = 0x67; f.Colors[2] = f.Colors[3] = uint.MaxValue;
                    int sx = dx > 0 ? 4 : 17, sy = dy > 0 ? 4 : 13;
                    for (int y = 0; y < 9; y++)
                        for (int x = 0; x < 14; x++)
                            f.Probe(sx + x * dx + dx, sy + y * dy + dy, sx + x * dx, sy + y * dy, 0, true);
                    f.Complete();
                });
            test($"Trio read optimization disabled drawing and inclusive/external clip retain whole state depth={bytes}", () =>
            {
                var f = new Fixture(bytes); f.Words[30] = 0x27; f.Colors[1] = 0x3579bde1; f.Colors[2] = uint.MaxValue;
                f.Words[7] = 3; f.Words[8] = 4; f.Words[9] = 7; f.Words[10] = 8;
                foreach (ushort command in new ushort[] { 0, 0x10 })
                foreach (ushort outside in new ushort[] { 0, 0x20 })
                {
                    f.Words[16] = command; f.Words[12] = outside;
                    for (int y = 2; y <= 8; y++) for (int x = 3; x <= 9; x++) f.Probe(x, y, x + 1, y, 0, true);
                }
                f.Complete();
            });
        }
    }
}
