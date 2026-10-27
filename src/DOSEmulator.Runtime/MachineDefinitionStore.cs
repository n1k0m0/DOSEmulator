// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Text.Json.Serialization;
using DOSEmulator.Contracts;

namespace DOSEmulator.Runtime;

/// <summary>Versioned, atomic configuration files with media paths relative to the saved PC.</summary>
public static class MachineDefinitionStore
{
    private static readonly JsonSerializerOptions Options = new(IpcProtocol.JsonOptions)
    { WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Reads a bounded document and resolves relative images against its directory, never the process working directory.</summary>
    public static MachineDefinition Load(string path)
    {
        path = Path.GetFullPath(path);
        RequireConfigurationExtension(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Machine definition exceeds 1 MiB.");
        using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var definition = document.RootElement.Deserialize<MachineDefinition>(Options) ?? throw new InvalidDataException("Empty machine definition.");
        return Resolve(definition, Path.GetDirectoryName(path)!);
    }

    /// <summary>Writes a complete document beside its destination, flushes it and atomically replaces only that configuration file.</summary>
    public static void Save(string path, MachineDefinition definition)
    {
        path = Path.GetFullPath(path);
        RequireConfigurationExtension(path);
        string directory = Path.GetDirectoryName(path)!;
        var absolute = Resolve(definition, directory);
        // Expresses a canonical media binding relative to the saved PC's directory when possible.
        DiskAttachment Relative(DiskAttachment m) => m with { ImagePath = Path.GetRelativePath(directory, m.ImagePath),
            OverlayPath = m.OverlayPath is null ? null : Path.GetRelativePath(directory, m.OverlayPath) };
        var portable = absolute with { HardDisks = absolute.HardDisks.Select(Relative).ToArray(), Floppies = absolute.Floppies.Select(Relative).ToArray() };
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        if (absolute.HardDisks.Concat(absolute.Floppies).Any(m => Same(path, m.ImagePath) || m.OverlayPath is not null && Same(path, m.OverlayPath)))
            throw new ArgumentException("A machine configuration cannot replace one of its media files.");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, portable, Options); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Normalizes paths and rejects journals shared by two drives or used as any drive's protected base.</summary>
    public static MachineDefinition Resolve(MachineDefinition definition, string directory)
    {
        definition.Validate(); directory = Path.GetFullPath(directory);
        // Resolves a persisted media binding against its PC file's directory.
        DiskAttachment Absolute(DiskAttachment m) => m with { ImagePath = Path.GetFullPath(m.ImagePath, directory),
            OverlayPath = m.OverlayPath is null ? null : Path.GetFullPath(m.OverlayPath, directory) };
        var resolved = definition with { HardDisks = definition.HardDisks.Select(Absolute).ToArray(), Floppies = definition.Floppies.Select(Absolute).ToArray() };
        var all = resolved.HardDisks.Concat(resolved.Floppies).ToArray();
        var overlays = all.Where(m => m.OverlayPath is not null).Select(m => m.OverlayPath!).ToArray();
        if (overlays.Distinct(StringComparer.OrdinalIgnoreCase).Count() != overlays.Length || overlays.Any(o => all.Any(m => Same(o, m.ImagePath))))
            throw new ArgumentException("Every writable drive needs a distinct private overlay, separate from all base images.");
        return resolved;
    }

    /// <summary>Compares canonical Windows media paths without changing their on-disk case.</summary>
    internal static bool Same(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Prevents a configuration save from replacing an unrelated disk, ROM or journal chosen by mistake.</summary>
    private static void RequireConfigurationExtension(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".dospc"))
            throw new ArgumentException("Machine configurations use .dospc.json, .json or .dospc files.");
    }

    /// <summary>Rejects ambiguous JSON keys, including case aliases that the wire naming policy would otherwise merge.</summary>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate machine property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
