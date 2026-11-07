// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.DiskTools;
using DOSEmulator.Hardware;

/// <summary>Storage, rollback, interoperability and host-path controls on fresh own filesystems only.</summary>
internal static class Program
{
    private static int _passed, _failed;
    private static readonly List<object> Results = [];
    /// <summary>Requires an exact storage invariant or fails the current named control.</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Hashes one explicitly named private fixture independently of the editor.</summary>
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    /// <summary>Requires a storage or validation failure instead of successful acceptance.</summary>
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException) { return; }
        throw new Exception("Expected failure was accepted.");
    }
    /// <summary>Records each named control and continues collecting independent failure evidence.</summary>
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Results.Add(new { name, passed = true }); Console.WriteLine("PASS: " + name); }
        catch (Exception error) { _failed++; Results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL: " + name + " — " + error.Message); }
    }

    /// <summary>Runs only named own fixtures and writes an auditable result separate from emulator regression counts.</summary>
    private static int Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "verify-copy")
        {
            string directory = Path.GetFullPath(args[1]);
            string expectedRoot = Path.GetFullPath("VMs/Tools/DiskExplorer/_work") + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Only a private integration copy is allowed.");
            using var oracle = new FatOracle(new Fixture(directory), int.Parse(args[2]));
            Check(oracle.ReadFile(args[3]).AsSpan().SequenceEqual(File.ReadAllBytes(args[4])), "Independent real-copy FAT/LFN payload differs.");
            oracle.CheckFreeHints();
            Console.WriteLine("PASS: independent FAT/LFN/COW readback on the private integration copy.");
            return 0;
        }
        string work = Path.GetFullPath(args.Length > 0 ? args[0] : "VMs/Tools/DiskExplorer/_work/tests-" + Guid.NewGuid().ToString("N"));
        string allowed = Path.GetFullPath("VMs/Tools/DiskExplorer/_work") + Path.DirectorySeparatorChar;
        if (!work.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(work)) throw new IOException("A new private test directory is required.");
        Directory.CreateDirectory(work);
        foreach (int bits in new[] { 12, 16, 32 })
        {
            int format = bits;
            Test($"FAT{bits} long-name tree import/export, overwrite, complete IMG, independent FAT/LFN/COW oracle", () => RoundTrip(work, format));
        }
        var f = new Fixture(work, "guard-controls", 12);
        Test("Open refuses an emulator-owned journal", () =>
        {
            using var owned = new CopyOnWriteDisk(f.Image, f.Cow);
            Reject(() => { using var session = new DiskSession(f.Selection); });
        });
        Test("Open reader excludes guest writers and original IMG writes", () =>
        {
            using var session = new DiskSession(f.Selection);
            Reject(() => { using var writer = new FileStream(f.Image, FileMode.Open, FileAccess.ReadWrite); });
            Reject(() => { using var guest = new CopyOnWriteDisk(f.Image, f.Cow); });
        });
        foreach (string name in new[] { "../escape", @"..\escape", "C:\\escape", "CON", "NUL.txt", "COM1", "COM¹", "LPT².txt", "CONIN$", "CONOUT$", "CON .txt", "trailing.", "trailing ", "bad:name", "a?b", "." })
        {
            string invalid = name;
            Test("Reject guest path/name " + name, () =>
            {
                string before = Hash(f.Cow);
                using var session = new DiskSession(f.Selection);
                Reject(() => session.CreateDirectory(session.Volumes[0], invalid));
                Check(Hash(f.Cow) == before, "A rejected path changed the journal.");
            });
        }
        Test("Existing host export is preserved", () =>
        {
            string target = Path.Combine(f.Root, "existing"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
            using var session = new DiskSession(f.Selection);
            Reject(() => session.Export(session.Volumes[0], ["KEEP.DAT"], target));
            Check(File.ReadAllText(Path.Combine(target, "keep.txt")) == "keep", "Existing host data changed.");
        });
        foreach (string reserved in new[] { "CON.img", "COM¹.img", "CONOUT$.img" })
        {
            string name = reserved;
            Test("Reject reserved host export filename " + name, () =>
            {
                using var session = new DiskSession(f.Selection);
                Reject(() => session.SaveMergedImage(Path.Combine(f.Root, name)));
            });
        }
        Test("Reject device namespaces before opening an endpoint", () =>
        {
            Reject(() => TransferPaths.NoLinks(@"\\.\PhysicalDrive0"));
            Reject(() => TransferPaths.NoLinks(@"\\?\GLOBALROOT\Device\Harddisk0"));
            Reject(() => TransferPaths.NoLinks(@"\\.\pipe\not-a-file"));
        });
        Test("Cancel after candidate writes leaves exact old journal and no temporary file", () =>
        {
            string input = Path.Combine(f.Root, "CANCEL.BIN"); File.WriteAllBytes(input, new byte[262157]);
            string before = Hash(f.Cow);
            using var session = new DiskSession(f.Selection);
            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress(p => { if (p.Message.StartsWith("Verifying", StringComparison.Ordinal)) cancel.Cancel(); });
            try { session.Import(session.Volumes[0], [input], cancel: cancel.Token, progress: progress); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Check(Hash(f.Cow) == before, "Cancellation published a partial filesystem.");
            Check(!Directory.EnumerateFiles(f.Root, ".doseedit-*").Any(), "Private candidate leaked.");
        });
        Test("Disk full rolls back all FAT and directory changes", () =>
        {
            string input = Path.Combine(f.Root, "TOOBIG.BIN"); File.WriteAllBytes(input, new byte[2 * 1024 * 1024]);
            string before = Hash(f.Cow);
            using var session = new DiskSession(f.Selection);
            Reject(() => session.Import(session.Volumes[0], [input]));
            Check(Hash(f.Cow) == before, "Disk-full error changed the source journal.");
            Check(!session.List(session.Volumes[0]).Any(x => x.Name == "TOOBIG.BIN"), "Disk-full import became visible.");
        });
        Test("Standalone IMG gains a local COW without changing any image bytes", () =>
        {
            string cow = Path.Combine(f.Root, "new.cow"), before = Hash(f.Image);
            using var session = new DiskSession(DiskSelection.FromImage(f.Image, cow));
            session.CreateDirectory(session.Volumes[0], "NEW");
            Check(File.Exists(cow) && Hash(f.Image) == before, "Standalone import changed the IMG.");
        });
        Test("Read-only VM attachment cannot import", () =>
        {
            using var session = new DiskSession(new("Read-only", f.Image, null));
            Reject(() => session.CreateDirectory(session.Volumes[0], "NO"));
        });
        foreach (string kind in new[] { "magic", "identity", "capacity", "checksum", "negative-lba", "large-lba" })
        {
            string corrupt = kind;
            Test("Reject corrupt COW " + kind + " without modifying its bytes", () =>
            {
                byte[] bytes = File.ReadAllBytes(f.Cow);
                if (corrupt == "magic") bytes[0] ^= 1;
                else if (corrupt == "identity") bytes[16] ^= 1;
                else if (corrupt == "capacity") bytes[8] ^= 1;
                else
                {
                    Array.Resize(ref bytes, 600);
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), corrupt == "negative-lba" ? -1 : corrupt == "large-lba" ? 2880 : 1);
                    SHA256.HashData(bytes.AsSpan(48, 520)).CopyTo(bytes, 568);
                    if (corrupt == "checksum") bytes[599] ^= 1;
                }
                string path = Path.Combine(f.Root, corrupt + ".cow"); File.WriteAllBytes(path, bytes);
                string before = Hash(path);
                Reject(() => { using var session = new DiskSession(new("Bad", f.Image, path)); });
                Check(Hash(path) == before, "Corrupt source was altered.");
            });
        }
        Test("Incomplete journal tail is read without truncating the original", () =>
        {
            string cow = Path.Combine(f.Root, "tail.cow");
            File.WriteAllBytes(cow, [.. File.ReadAllBytes(f.Cow), 1, 2, 3]);
            string before = Hash(cow);
            using (var session = new DiskSession(new("Tail", f.Image, cow))) Check(session.List(session.Volumes[0]).Any(), "Lost root files.");
            Check(Hash(cow) == before, "Read-only open truncated the source.");
        });
        Test("Merged-image export never overwrites an existing host file", () =>
        {
            string target = Path.Combine(f.Root, "occupied.img"); File.WriteAllText(target, "keep");
            using var session = new DiskSession(f.Selection);
            Reject(() => session.SaveMergedImage(target));
            Check(File.ReadAllText(target) == "keep", "Existing output changed.");
        });
        Test("Actual journal lock prevents concurrent tool publication", () =>
        {
            using var first = new DiskSession(f.Selection);
            using var second = new DiskSession(f.Selection);
            string before = Hash(f.Cow);
            Reject(() => first.CreateDirectory(first.Volumes[0], "LOCKED"));
            Check(Hash(f.Cow) == before, "Competing reader permitted publication.");
        });
        Test("VM binding resolves relative paths and retains every disk slot", () =>
        {
            string machine = Path.Combine(f.Root, "machine.dospc.json");
            File.WriteAllText(machine, """{"schemaVersion":1,"hardDisks":[{"slot":0,"imagePath":"own.img","overlayPath":"own.cow"},{"slot":1,"imagePath":"own.img","overlayPath":null}],"floppies":[]}""");
            var disks = DiskSelection.FromMachine(machine);
            Check(disks.Length == 2 && disks[0].CowPath == f.Cow && disks[1].CowPath is null, "Incorrect VM media bindings.");
        });
        var report = new { passed = _failed == 0, passedTests = _passed, failedTests = _failed,
            scope = "Fresh own FAT12/FAT16/FAT32 media only; no existing VMs, guest instructions, windows or endpoints",
            tests = Results };
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{_passed} passed; {_failed} failed. Results: {work}");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Checks full payloads, Unicode LFNs, binary/empty files, independent raw FAT structures and untouched base bytes.</summary>
    private static void RoundTrip(string work, int bits)
    {
        var f = new Fixture(work, "roundtrip-" + bits, bits);
        string imageBefore = Hash(f.Image), cowBefore = Hash(f.Cow);
        string source = Path.Combine(f.Root, "Source"), tree = Path.Combine(source, "Folder with spaces");
        Directory.CreateDirectory(Path.Combine(tree, "Inner"));
        Directory.CreateDirectory(Path.Combine(tree, "Empty folder"));
        byte[] payload = Enumerable.Range(0, 262157).Select(n => (byte)(n * 43 + 7)).ToArray();
        string longName = "A long file name Grüße.bin";
        File.WriteAllBytes(Path.Combine(tree, "Inner", longName), payload);
        File.WriteAllBytes(Path.Combine(tree, "EMPTY.TXT"), []);
        File.WriteAllBytes(Path.Combine(tree, "A matching prefix alpha.bin"), [1, 3, 5]);
        File.WriteAllBytes(Path.Combine(tree, "A matching prefix beta.bin"), [2, 4, 6]);
        using (var session = new DiskSession(f.Selection))
        {
            Check(session.Volumes.Count == 1, "Wrong volume count.");
            var volume = session.Volumes[0];
            Check(session.List(volume).Any(e => e.Name == "KEEP.DAT"), "Original root sentinel is missing.");
            session.CreateDirectory(volume, "Share");
            session.Import(volume, [tree], "Share");
            Check(session.List(volume, "Share\\Folder with spaces\\Inner").Any(e => e.Name == longName), "Long name was lost.");
            string export = Path.Combine(f.Root, "Export");
            session.Export(volume, ["Share"], export);
            Check(File.ReadAllBytes(Path.Combine(export, "Share", "Folder with spaces", "Inner", longName)).AsSpan().SequenceEqual(payload), "Export payload differs.");
            Check(new FileInfo(Path.Combine(export, "Share", "Folder with spaces", "EMPTY.TXT")).Length == 0, "Empty file differs.");
            Check(Directory.Exists(Path.Combine(export, "Share", "Folder with spaces", "Empty folder")), "Empty folder was lost.");
            string before = Hash(f.Cow);
            Reject(() => session.Import(volume, [tree], "Share"));
            Check(Hash(f.Cow) == before, "Rejected overwrite changed the journal.");
            File.WriteAllBytes(Path.Combine(tree, "Inner", longName), [9, 7, 5]);
            session.Import(volume, [tree], "Share", overwrite: true);
            string merged = Path.Combine(f.Root, "complete.img");
            session.SaveMergedImage(merged);
            using var reopened = new DiskSession(DiskSelection.FromImage(merged));
            Check(reopened.List(reopened.Volumes[0], "Share").Any(e => e.Name == "Folder with spaces"), "Complete IMG lost the imported tree.");
        }
        Check(Hash(f.Image) == imageBefore, "Original IMG bytes changed.");
        Check(Directory.EnumerateFiles(f.Root, "*.backup-*").Any(p => Hash(p) == cowBefore), "Original journal generation was not retained.");
        using var oracle = new FatOracle(f, bits);
        Check(oracle.ReadFile("KEEP.DAT").AsSpan().SequenceEqual(f.Sentinel), "Independent sentinel content differs.");
        Check(oracle.ReadFile("Share\\Folder with spaces\\Inner\\" + longName).AsSpan().SequenceEqual(new byte[] { 9, 7, 5 }), "Independent FAT/LFN payload differs.");
        Check(oracle.ReadFile("Share\\Folder with spaces\\EMPTY.TXT").Length == 0, "Independent empty file differs.");
        Check(oracle.ReadFile("Share\\Folder with spaces\\A matching prefix alpha.bin").AsSpan().SequenceEqual(new byte[] { 1, 3, 5 }), "First alias collision case differs.");
        Check(oracle.ReadFile("Share\\Folder with spaces\\A matching prefix beta.bin").AsSpan().SequenceEqual(new byte[] { 2, 4, 6 }), "Second alias collision case differs.");
        oracle.CheckFreeHints();
    }

    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        /// <summary>Runs test progress synchronously so cancellation injection happens at an exact stage.</summary>
        public void Report(TransferProgress value) => report(value);
    }
}
