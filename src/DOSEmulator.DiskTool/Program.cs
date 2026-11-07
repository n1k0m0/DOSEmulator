// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.DiskTools;

/// <summary>Scriptable offline disk commands sharing the graphical explorer's checked storage operations.</summary>
internal static class Program
{
    /// <summary>Parses explicit media/volume bindings and never creates a VM, mount, network connection or native window.</summary>
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("""
                DOSE Disk Tool
                Commands: list, import, export, mkdir, merge
                Media: --vm <machine.dospc.json> [--disk 0] OR --image <disk.img> [--cow <changes.cow>]
                Options: --volume 0 --codepage 437|850
                list   --path <guest-directory>
                import --from <host-file-or-folder> [--from <another>] --to <guest-directory> [--overwrite]
                export --path <guest-file-or-folder> [--path <another>] --output <new-host-directory>
                mkdir  --path <new-guest-directory>
                merge  --output <new-complete.img>
                Guest paths have no drive letter. Close DOSE before opening its writable disks.
                Imports update only a COW file and keep a backup; standalone IMGs use an adjacent .cow.
                """);
            return 0;
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try
        {
            Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
            for (int n = 1; n < args.Length; n++)
            {
                string key = args[n];
                if (!key.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected an option: " + key);
                string value = key == "--overwrite" ? "true" :
                    ++n < args.Length ? args[n] : throw new ArgumentException("Missing value: " + key);
                if (!options.TryGetValue(key, out var values)) options[key] = values = [];
                values.Add(value);
            }
            // Returns the last explicit value for one console option.
            string? Get(string key) => options.TryGetValue(key, out var values) ? values[^1] : null;
            // Requires an explicit value before performing a disk operation.
            string Required(string key) => Get(key) ?? throw new ArgumentException("Missing option: " + key);
            // Retains every repeated source/path option in command-line order.
            string[] All(string key) => options.TryGetValue(key, out var values) ? values.ToArray() : [];
            DiskSelection selection;
            if (Get("--vm") is { } machine)
            {
                var disks = DiskSelection.FromMachine(machine);
                int disk = int.Parse(Get("--disk") ?? "0");
                if ((uint)disk >= disks.Length) throw new ArgumentException("Disk index is out of range.");
                selection = disks[disk];
            }
            else selection = DiskSelection.FromImage(Required("--image"), Get("--cow"));
            using var session = new DiskSession(selection, int.Parse(Get("--codepage") ?? "437"));
            int index = int.Parse(Get("--volume") ?? "0");
            if ((uint)index >= session.Volumes.Count) throw new ArgumentException("Volume index is out of range.");
            var volume = session.Volumes[index];
            var progress = new InlineProgress(p => Console.Error.WriteLine(p.Message));
            switch (args[0])
            {
                case "list":
                    Console.WriteLine(JsonSerializer.Serialize(new { selection, volumes = session.Volumes,
                        entries = session.List(volume, Get("--path") ?? "") }, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case "import":
                    session.Import(volume, All("--from"), Get("--to") ?? "", Get("--overwrite") is not null, cancel.Token, progress);
                    Console.WriteLine("Import saved to " + selection.CowPath);
                    break;
                case "export":
                    string[] paths = All("--path");
                    if (paths.Length == 0) throw new ArgumentException("Select at least one --path.");
                    Console.WriteLine(session.Export(volume, paths, Required("--output"), cancel.Token, progress));
                    break;
                case "mkdir":
                    session.CreateDirectory(volume, Required("--path"), cancel.Token);
                    Console.WriteLine("Directory created.");
                    break;
                case "merge":
                    session.SaveMergedImage(Required("--output"), cancel.Token, progress);
                    Console.WriteLine("Complete image saved.");
                    break;
                default: throw new ArgumentException("Unknown command: " + args[0]);
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; unpublished changes were discarded."); return 2; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    /// <summary>Reports synchronously so CLI progress cannot outlive its completed transaction.</summary>
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        /// <summary>Emits progress immediately on the calling console thread.</summary>
        public void Report(TransferProgress value) => report(value);
    }
}
