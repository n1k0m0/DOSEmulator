// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;

namespace DOSEmulator.DiskTools;

/// <summary>Explicit disk and optional changes-file binding selected from a VM or raw image.</summary>
public sealed record DiskSelection(string Name, string ImagePath, string? CowPath)
{
    /// <summary>Loads attached hard disks and floppies without instantiating a VM or changing its definition.</summary>
    public static DiskSelection[] FromMachine(string path)
    {
        path = Path.GetFullPath(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unknown machine-definition version.");
        string parent = Path.GetDirectoryName(path)!;
        List<DiskSelection> result = [];
        foreach (string group in new[] { "hardDisks", "floppies" })
        {
            if (!document.RootElement.TryGetProperty(group, out var disks)) continue;
            foreach (var disk in disks.EnumerateArray())
            {
                int slot = disk.GetProperty("slot").GetInt32();
                string image = Path.GetFullPath(disk.GetProperty("imagePath").GetString()!, parent);
                string? cow = disk.TryGetProperty("overlayPath", out var overlay) && overlay.ValueKind == JsonValueKind.String
                    ? Path.GetFullPath(overlay.GetString()!, parent) : null;
                string name = group == "hardDisks" ? $"IDE {slot}" : $"Floppy {(char)('A' + slot)}:";
                result.Add(new(name + " — " + Path.GetFileName(image), image, cow));
            }
        }
        return result.ToArray();
    }

    /// <summary>Pairs a standalone raw image with its adjacent journal, creating that journal only upon import.</summary>
    public static DiskSelection FromImage(string image, string? cow = null) =>
        new(Path.GetFileName(image), Path.GetFullPath(image), Path.GetFullPath(cow ?? Path.ChangeExtension(image, ".cow")));

    /// <summary>Displays the selected controller slot and image filename.</summary>
    public override string ToString() => Name;
}
