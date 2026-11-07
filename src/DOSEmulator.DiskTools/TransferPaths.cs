// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.DiskTools;

/// <summary>Separates guest names from host paths and rejects traversal, device names and reparse-point targets.</summary>
internal static class TransferPaths
{
    /// <summary>Validates one interoperable FAT/Windows filename, preserving case and Unicode long names.</summary>
    public static string Leaf(string value)
    {
        if (value.Length is 0 or > 255 || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.') ||
            value.Any(c => c < 32 || "\\/:*?\"<>|".Contains(c)) || value.IndexOf('\0') >= 0)
            throw new IOException("Invalid filename: " + value);
        string stem = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))
            throw new IOException("Reserved DOS device filename: " + value);
        return value;
    }

    /// <summary>Returns a normalized guest-relative path and forbids drive/host path interpretation.</summary>
    public static string Guest(string path)
    {
        string[] parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts) Leaf(part);
        return string.Join('\\', parts);
    }

    /// <summary>Checks the complete existing host ancestry before creating a local export or reading an import.</summary>
    public static void NoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\?\", StringComparison.Ordinal) && !(full.Length > 6 && full[5] == ':' || full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Device namespaces and physical disks are not supported.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links and junctions are not followed: " + current);
    }

    /// <summary>Builds a host child only below a newly created private staging tree.</summary>
    public static string Child(string parent, string name)
    {
        string result = Path.GetFullPath(Path.Combine(parent, Leaf(name)));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export path escaped its selected directory.");
        return result;
    }
}
