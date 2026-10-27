// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent DOSCOW01 data/checksum cases and fixed-record allocation probes on fresh private journals.</summary>
internal static class CowAuditPerformanceTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root, Basis, Journal;
        public readonly byte[] Last = new byte[512];
        private readonly bool _keep;
        /// <summary>Builds a completely own raw basis and independent repeated-LBA checksummed journal.</summary>
        public Fixture(int records, string? directory = null, bool keep = false)
        {
            Root = Path.GetFullPath(directory ?? Path.Combine(Environment.CurrentDirectory, "artifacts/tests/cow-audit-" + Guid.NewGuid().ToString("N")));
            string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || Root.Contains("doom2-play", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Private fixture path required.");
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Own fixture target already exists.");
            Directory.CreateDirectory(Root); _keep = keep; Basis = Path.Combine(Root, "basis.img"); Journal = Path.Combine(Root, "journal.cow");
            File.WriteAllBytes(Basis, new byte[512 * 1024]);
            byte[] header = new byte[48]; "DOSCOW01"u8.CopyTo(header); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), 1024);
            using (var original = File.OpenRead(Basis)) SHA256.HashData(original).CopyTo(header, 16);
            using var stream = new FileStream(Journal, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(header);
            byte[] record = new byte[552]; BinaryPrimitives.WriteInt64LittleEndian(record, 9);
            for (int i = 0; i < records; i++)
            {
                for (int n = 0; n < 512; n++) record[8 + n] = unchecked((byte)(n * 37 + i * 11));
                SHA256.HashData(record.AsSpan(0, 520), record.AsSpan(520)); stream.Write(record);
                record.AsSpan(8, 512).CopyTo(Last);
            }
            stream.Flush(true);
        }
        /// <summary>Disposes only this fresh fixture, preserving explicit benchmark artifacts when requested.</summary>
        public void Dispose() { if (!_keep) Directory.Delete(Root, true); }
    }
    /// <summary>Requires an exact generation, sector, failure or allocation measurement invariant.</summary>
    private static void Check(bool value, string message = "COW audit invariant failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Hashes an explicitly known own file independently of the journal implementation.</summary>
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    /// <summary>Requires a checksum/format failure rather than successful partial journal acceptance.</summary>
    private static void Invalid(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Corrupt COW journal accepted.");
    }
    /// <summary>Injects a test-only byte fault through the already owned private file handle, bypassing sharing only for the fixture.</summary>
    private static void FaultByte(CopyOnWriteDisk disk, long offset, byte value)
    {
        object store = typeof(CopyOnWriteDisk).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disk)!;
        var file = (FileStream)store.GetType().GetField("File", BindingFlags.Instance | BindingFlags.Public)!.GetValue(store)!;
        RandomAccess.Write(file.SafeFileHandle, new byte[] { value }, offset); file.Flush(true);
    }
    /// <summary>Registers independent valid/corrupt/truncated/late-failure cases without altering durable-write policy.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int records in new[] { 0, 1, 2, 4097 })
            test("Audit COW exact generation and last repeated LBA " + records, () =>
            {
                using var f = new Fixture(records); byte[] initial = File.ReadAllBytes(f.Journal); string journalHash = Hash(f.Journal), basisHash = Hash(f.Basis);
                using var disk = new CopyOnWriteDisk(f.Basis, f.Journal); Span<byte> actual = stackalloc byte[512]; disk.ReadSector(9, actual);
                Check(actual.SequenceEqual(f.Last) && disk.OverlaySectorCount == (records == 0 ? 0 : 1));
                string exported = Path.Combine(f.Root, "sealed.cow"); var generation = disk.ExportGeneration(exported);
                Check(generation.Bytes == 48 + records * 552L && generation.Sha256 == journalHash && Hash(exported) == journalHash && Hash(f.Basis) == basisHash);
                Check(File.ReadAllBytes(exported).SequenceEqual(initial));
                byte[] next = Enumerable.Repeat((byte)0x69, 512).ToArray(); disk.WriteSector(10, next); disk.ReadSector(10, actual);
                Check(actual.SequenceEqual(next) && Hash(exported) == journalHash && Hash(f.Basis) == basisHash);
                Check(new FileInfo(f.Journal).Length == generation.Bytes + 552 && disk.OverlaySectorCount == (records == 0 ? 1 : 2));
            });
        foreach (string kind in new[] { "magic", "capacity", "identity", "negative-lba", "large-lba", "first-checksum", "last-checksum" })
            test("Audit COW cold open keeps exact corrupt record rejection " + kind, () =>
            {
                using var f = new Fixture(4); byte[] data = File.ReadAllBytes(f.Journal);
                if (kind == "magic") data[0] ^= 1;
                else if (kind == "capacity") data[8] ^= 1;
                else if (kind == "identity") data[16] ^= 1;
                else if (kind == "negative-lba") BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(48), -1);
                else if (kind == "large-lba") BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(48), 1024);
                else data[48 + (kind == "last-checksum" ? 3 * 552 : 0) + 520] ^= 1;
                File.WriteAllBytes(f.Journal, data); string hash = Hash(f.Journal);
                Invalid(() => { using var disk = new CopyOnWriteDisk(f.Basis, f.Journal); }); Check(Hash(f.Journal) == hash);
            });
        foreach (int tail in new[] { 1, 17, 551 })
            test("Audit COW incomplete last record preserves all valid committed bytes " + tail, () =>
            {
                using var f = new Fixture(4); byte[] valid = File.ReadAllBytes(f.Journal);
                using (var append = new FileStream(f.Journal, FileMode.Append, FileAccess.Write)) { append.Write(new byte[tail]); append.Flush(true); }
                using (var disk = new CopyOnWriteDisk(f.Basis, f.Journal))
                {
                    Span<byte> actual = stackalloc byte[512]; disk.ReadSector(9, actual); Check(actual.SequenceEqual(f.Last));
                    Check(new FileInfo(f.Journal).Length == valid.Length);
                    var generation = disk.ExportGeneration(Path.Combine(f.Root, "sealed.cow")); Check(generation.Sha256 == Convert.ToHexString(SHA256.HashData(valid)));
                }
                Check(File.ReadAllBytes(f.Journal).SequenceEqual(valid));
            });
        foreach (bool last in new[] { false, true })
            test("Audit COW late export checksum error removes only new partial target last " + last, () =>
            {
                using var f = new Fixture(4); byte[] pristine = File.ReadAllBytes(f.Journal); using var disk = new CopyOnWriteDisk(f.Basis, f.Journal);
                long offset = 48 + (last ? 3 * 552 : 0) + 520; byte original = pristine[(int)offset]; FaultByte(disk, offset, (byte)(original ^ 1));
                string failed = Path.Combine(f.Root, "failed.cow"); Invalid(() => disk.ExportGeneration(failed)); Check(!File.Exists(failed));
                FaultByte(disk, offset, original); string restored = Path.Combine(f.Root, "restored.cow"); var generation = disk.ExportGeneration(restored);
                Check(generation.Sha256 == Convert.ToHexString(SHA256.HashData(pristine)) && File.ReadAllBytes(restored).SequenceEqual(pristine));
                Span<byte> actual = stackalloc byte[512]; disk.ReadSector(9, actual); Check(actual.SequenceEqual(f.Last));
            });
        test("Audit COW export cannot overwrite an existing target or change active append cursor", () =>
        {
            using var f = new Fixture(2); using var disk = new CopyOnWriteDisk(f.Basis, f.Journal);
            string target = Path.Combine(f.Root, "keep.cow"); File.WriteAllBytes(target, [1, 2, 3, 4]); bool rejected = false;
            try { disk.ExportGeneration(target); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            disk.WriteSector(11, Enumerable.Repeat((byte)0x7d, 512).ToArray());
            Check(new FileInfo(f.Journal).Length == 48 + 3 * 552); Span<byte> actual = stackalloc byte[512]; disk.ReadSector(11, actual); Check(actual.IndexOfAnyExcept((byte)0x7d) < 0);
        });
    }

    /// <summary>Measures fixed cold-open/export work and allocations separately on fresh private repeated-LBA records.</summary>
    public static int Run(string output)
    {
        output = Path.GetFullPath(output);
        string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Private artifact output required.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Benchmark destination exists."); Directory.CreateDirectory(output);
        const int records = 8192, repeats = 9; using var f = new Fixture(records, Path.Combine(output, "media"), true);
        string expectedHash = Hash(f.Journal); long[] openBytes = new long[repeats], exportBytes = new long[repeats]; double[] openMs = new double[repeats], exportMs = new double[repeats];
        Span<byte> actual = stackalloc byte[512];
        for (int i = 0; i < 3; i++) { using var warm = new CopyOnWriteDisk(f.Basis, f.Journal); warm.ReadSector(9, actual); Check(actual.SequenceEqual(f.Last)); warm.ExportGeneration(Path.Combine(output, $"warm-{i}.cow")); }
        for (int i = 0; i < repeats; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            using (var disk = new CopyOnWriteDisk(f.Basis, f.Journal)) { disk.ReadSector(9, actual); Check(actual.SequenceEqual(f.Last)); }
            openBytes[i] = GC.GetAllocatedBytesForCurrentThread() - before; openMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            using var active = new CopyOnWriteDisk(f.Basis, f.Journal); string path = Path.Combine(output, $"export-{i}.cow");
            before = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp(); OverlayGeneration generation = active.ExportGeneration(path);
            exportBytes[i] = GC.GetAllocatedBytesForCurrentThread() - before; exportMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(generation.Bytes == 48 + records * 552L && generation.Sha256 == expectedHash && Hash(path) == expectedHash);
        }
        var report = new { passed = true, records, repeats, recordBytes = 552, openAllocatedBytes = openBytes, exportAllocatedBytes = exportBytes,
            openMilliseconds = openMs, exportMilliseconds = exportMs, generationSha256 = expectedHash, finalSectorSha256 = Convert.ToHexString(SHA256.HashData(f.Last)),
            sectorDurabilityUnchanged = true, baseHashStillValidated = true, readOrderingUnchanged = true, blockReadBatching = false };
        var options = new JsonSerializerOptions(StateValues.Json) { IgnoreReadOnlyProperties = false };
        File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, options)); Console.WriteLine(JsonSerializer.Serialize(report, options)); return 0;
    }
}
