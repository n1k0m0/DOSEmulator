// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

/// <summary>Independent malformed-container and replaced-module checks using exclusively new own files.</summary>
internal static class NativeSnapshotAuditChecks
{
    /// <summary>Runs the focused snapshot checks, collecting independent failures for the unchanged-baseline grader.</summary>
    internal static void Run(string selection = "all")
    {
        var failures = new List<Exception>();
        foreach (var (name, action) in new (string, Action)[]
        {
            ("identity", CheckIdentity), ("json", CheckJson), ("rollback", CheckRollback)
        })
        {
            if (selection != "all" && selection != name) continue;
            try { action(); Console.WriteLine("PASS: native snapshot audit " + name + "."); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: native snapshot audit " + name + ": " + error.Message); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    /// <summary>Runs in an isolated child whose loaded backend copy is intentionally replaced; product binaries remain untouched.</summary>
    internal static void IdentityChild(string root)
    {
        string owned = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var assembly = typeof(WhpxSnapshotStore).Assembly;
        string path = Path.GetFullPath(assembly.Location);
        if (!path.StartsWith(owned, StringComparison.OrdinalIgnoreCase)) throw new IOException("Refusing to replace a non-test assembly.");
        var method = typeof(WhpxSnapshotStore).GetMethod("RuntimeHash", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] original = File.ReadAllBytes(path);
        string normal = (string)method.Invoke(null, null)!;
        if (normal != Convert.ToHexString(SHA256.HashData(original))) throw new Exception("The initial backend identity differs.");
        Guid running = assembly.ManifestModule.ModuleVersionId;
        int offset = original.AsSpan().IndexOf(running.ToByteArray());
        if (offset < 0) throw new Exception("Own MVID not found.");
        byte[] changed = (byte[])original.Clone(); Guid.NewGuid().ToByteArray().CopyTo(changed, offset);
        File.WriteAllBytes(path + ".replacement", changed);
        File.Move(path, path + ".loaded"); File.Move(path + ".replacement", path);
        bool rejected = false;
        try { _ = method.Invoke(null, null); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("The old loaded backend accepted the identity of its replacement DLL.");
        File.WriteAllBytes(path + ".invalid", new byte[] { 1, 2, 3 });
        File.Move(path, path + ".changed"); File.Move(path + ".invalid", path);
        bool invalidRejected = false;
        try { _ = method.Invoke(null, null); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { invalidRejected = true; }
        File.WriteAllText(Path.Combine(root, "identity-result.json"), JsonSerializer.Serialize(new { rejected, invalidRejected, runningMvid = running }));
        if (!invalidRejected) throw new Exception("A corrupt replacement backend was not rejected as an owner error.");
    }

    /// <summary>Copies the actual test runtime, invokes a bounded child and requires rejection of a different on-disk MVID.</summary>
    private static void CheckIdentity()
    {
        string root = NewRoot();
        try
        {
            string runner = typeof(NativeSnapshotAuditChecks).Assembly.Location;
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(runner)!))
                if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, Path.GetFileName(runner)));
            start.ArgumentList.Add("--audit-identity-child"); start.ArgumentList.Add(root);
            using var child = Process.Start(start) ?? throw new IOException("Own identity child did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000)) { child.Kill(true); child.WaitForExit(); throw new TimeoutException("Own identity child timed out."); }
            _ = stdout.GetAwaiter().GetResult(); string errors = stderr.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new Exception("Own identity child failed: " + errors);
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "identity-result.json")));
            if (!report.RootElement.GetProperty("rejected").GetBoolean()) throw new Exception("Replaced backend was accepted.");
        }
        finally { RemoveRoot(root); }
    }

    /// <summary>Rejects duplicate outer fields and root/nested CPU fields before a media branch exists; the intact control must still load.</summary>
    private static void CheckJson()
    {
        string root = NewRoot();
        try
        {
            string image = Path.Combine(root, "own.img"); byte[] disk = new byte[1 << 20];
            disk[0] = 0xeb; disk[1] = 0xfe; disk[510] = 0x55; disk[511] = 0xaa; File.WriteAllBytes(image, disk);
            string saved = Path.Combine(root, "own.whpxsav");
            using (var pc = new PcMachine(new MachineProfile().WithDevelopmentCpu(true), new CopyOnWriteDisk(image, Path.Combine(root, "own.cow"))))
            using (var native = WhpxPcSession.Start(pc)) WhpxSnapshotStore.Capture(native, saved);
            string outerPath = Path.Combine(saved, "whpx.json"), cpuPath = Path.Combine(saved, "cpu.json");
            string outer = File.ReadAllText(outerPath), cpu = File.ReadAllText(cpuPath);
            var manifest = JsonSerializer.Deserialize<WhpxSnapshotManifest>(outer)!;
            var accepted = new List<string>(); int variant = 0;
            foreach (string malformed in new[] { "outer", "outer-case", "cpu-root", "cpu-register", "cpu-case", "syntax" })
            {
                File.WriteAllText(outerPath, outer); File.WriteAllText(cpuPath, cpu);
                if (malformed == "outer") File.WriteAllText(outerPath, "{\"Version\":-1," + outer[1..]);
                else if (malformed == "outer-case") File.WriteAllText(outerPath, "{\"version\":-1," + outer[1..]);
                else if (malformed == "syntax") File.WriteAllText(outerPath, "{invalid}");
                else
                {
                    string altered = malformed == "cpu-root" ? "{\"ClockPolicy\":\"invalid\"," + cpu[1..] :
                        cpu.Replace("\"Name\":0,", (malformed == "cpu-case" ? "\"name\":4294967295," : "\"Name\":4294967295,") + "\"Name\":0,");
                    if (altered == cpu) throw new Exception("The nested mutation did not match its own CPU fixture.");
                    File.WriteAllText(cpuPath, altered);
                    File.WriteAllText(outerPath, JsonSerializer.Serialize(manifest with { CpuSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cpuPath))) }));
                }
                string branch = Path.Combine(root, "bad" + variant++ + ".dosebranch"); bool rejected = false;
                try { using var unexpected = WhpxSnapshotStore.Load(saved, branch); }
                catch (InvalidDataException) { rejected = true; }
                catch (JsonException) { accepted.Add(malformed + " (unclassified JSON error)"); rejected = true; }
                if (!rejected || Directory.Exists(branch)) accepted.Add(malformed);
            }
            File.WriteAllText(outerPath, outer); File.WriteAllText(cpuPath, cpu);
            using (var control = WhpxSnapshotStore.Load(saved, Path.Combine(root, "control.dosebranch")))
                if (!control.Pc.Profile.IsP54c) throw new Exception("Intact native control did not load.");
            if (accepted.Count != 0) throw new Exception("Malformed native fields accepted: " + string.Join(", ", accepted));
        }
        finally { RemoveRoot(root); }
    }

    /// <summary>Exercises an actual hypervisor register rejection after preparation, requiring the old owner and no failed destination to remain.</summary>
    private static void CheckRollback()
    {
        string root = NewRoot();
        try
        {
            string image = Path.Combine(root, "own.img"); File.WriteAllBytes(image, new byte[1 << 20]);
            using var session = new MachineSession(new PcMachine(new MachineProfile().WithDevelopmentCpu(true),
                new CopyOnWriteDisk(image, Path.Combine(root, "own.cow"))), executionFactory: new WhpxExecutionFactory());
            // Executes the real queued owner API so activation failure must restore its previous native CPU.
            IpcResponse Command(string name, object payload)
            {
                var request = session.Enqueue(IpcProtocol.CreateRequest(name, payload)); session.PumpCommands(); return request.GetAwaiter().GetResult();
            }
            string saved = Path.Combine(root, "own.whpxsav");
            if (!Command("CreateSnapshot", new { path = saved }).Success) throw new Exception("Own native capture failed.");
            string cpuPath = Path.Combine(saved, "cpu.json"), manifestPath = Path.Combine(saved, "whpx.json");
            var cpu = JsonSerializer.Deserialize<WhpxCpuState>(File.ReadAllBytes(cpuPath))!;
            byte[] expectedCpu = File.ReadAllBytes(cpuPath);
            byte[] expectedHardware = File.ReadAllBytes(Path.Combine(saved, "hardware.dossav", "state.json"));
            var invalid = cpu with { Registers = cpu.Registers.Select(r => r.Name == 0x20 ? r with { Low = ulong.MaxValue, High = ulong.MaxValue } : r).ToArray() };
            File.WriteAllBytes(cpuPath, JsonSerializer.SerializeToUtf8Bytes(invalid));
            var manifest = JsonSerializer.Deserialize<WhpxSnapshotManifest>(File.ReadAllBytes(manifestPath))!;
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { CpuSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cpuPath))) }));
            var old = session.Machine; var before = session.GetStatus(); byte[] ram = (byte[])old.Memory.Ram.Clone();
            string branch = Path.Combine(root, "rejected.dosebranch");
            var response = Command("LoadSnapshot", new { path = saved, destination = branch });
            var after = session.GetStatus();
            if (response.Success || !ReferenceEquals(old, session.Machine) || before.Position != after.Position ||
                before.VirtualNanoseconds != after.VirtualNanoseconds || !ram.AsSpan().SequenceEqual(old.Memory.Ram))
                throw new Exception("Rejected native CPU replaced or corrupted the previous owner.");
            if (Directory.Exists(branch)) throw new Exception("A rejected native CPU left its prepared media branch behind.");
            string continued = Path.Combine(root, "continued.whpxsav");
            if (!Command("CreateSnapshot", new { path = continued }).Success ||
                !File.ReadAllBytes(Path.Combine(continued, "cpu.json")).AsSpan().SequenceEqual(expectedCpu) ||
                !File.ReadAllBytes(Path.Combine(continued, "hardware.dossav", "state.json")).AsSpan().SequenceEqual(expectedHardware))
                throw new Exception("Failed native activation changed the old complete CPU or serialized hardware state.");
        }
        finally { RemoveRoot(root); }
    }

    /// <summary>Creates one explicitly owned private test folder.</summary>
    private static string NewRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "whpx-audit-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); return root;
    }

    /// <summary>Checks the resolved absolute test boundary before recursively deleting only the own folder.</summary>
    private static void RemoveRoot(string root)
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "tests")) + Path.DirectorySeparatorChar;
        string absolute = Path.GetFullPath(root);
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("whpx-audit-", StringComparison.Ordinal))
            throw new IOException("Refusing to delete a non-test path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
