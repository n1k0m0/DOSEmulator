// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using DOSEmulator.Hardware;

/// <summary>Checks the real Windows durability mode and uncached journal bytes before any explicit guest flush or close.</summary>
internal static class CowDurabilityTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status; public nuint Information; }

    /// <summary>Queries the actual opened kernel file mode without changing it or accessing any user file.</summary>
    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status,
        out uint mode, uint size, int informationClass);

    /// <summary>Reads FILE_MODE_INFORMATION, whose independent kernel flag identifies synchronous write-through durability.</summary>
    private static uint Mode(FileStream file)
    {
        int result = NtQueryInformationFile(file.SafeFileHandle, out _, out uint mode, sizeof(uint), 16);
        if (result < 0) throw new IOException($"Own file-mode query failed: {result:X8}.");
        return mode;
    }

    /// <summary>Registers the Windows file-handle and journal-format checks; non-Windows hosts retain their explicit-flush backend.</summary>
    internal static void AddTo(Action<string, Action> test)
    {
        test("COW Windows durable ACK uses actual write-through and valid unchanged records before explicit flush", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string directory = Path.GetFullPath(Path.Combine(temporaryRoot, "dose-own-durable-" + Guid.NewGuid().ToString("N")));
            if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Own durability fixture escaped its temporary root.");
            Directory.CreateDirectory(directory);
            try
            {
                string basis = Path.Combine(directory, "own.img"), overlay = Path.Combine(directory, "own.cow");
                File.WriteAllBytes(basis, new byte[8 * 512]);
                using (var control = new FileStream(Path.Combine(directory, "buffered-control.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    if ((Mode(control) & 2) != 0) throw new Exception("The independent buffered control unexpectedly uses write-through.");
                byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(i * 37 + 11)).ToArray();
                for (int reopen = 0; reopen < 2; reopen++)
                {
                    using var disk = new CopyOnWriteDisk(basis, overlay);
                    var store = typeof(CopyOnWriteDisk).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disk)!;
                    var file = (FileStream)store.GetType().GetField("File")!.GetValue(store)!;
                    if ((Mode(file) & 0x22) != 0x22) throw new Exception("The actual COW handle lacks synchronous WRITE_THROUGH before sector ACK.");
                    payload[0] = (byte)reopen; disk.WriteSector(3, payload);
                    byte[] record = new byte[552]; long offset = 48 + reopen * 552;
                    if (RandomAccess.Read(file.SafeFileHandle, record, offset) != record.Length ||
                        BinaryPrimitives.ReadInt64LittleEndian(record) != 3 || !record.AsSpan(8, 512).SequenceEqual(payload) ||
                        !SHA256.HashData(record.AsSpan(0, 520)).AsSpan().SequenceEqual(record.AsSpan(520)))
                        throw new Exception("Acknowledged bytes or original COW record checksum differ before explicit flush.");
                    byte[] actual = new byte[512]; disk.ReadSector(3, actual);
                    if (!actual.AsSpan().SequenceEqual(payload)) throw new Exception("Reopened durable sector differs.");
                }
                if (new FileInfo(overlay).Length != 48 + 2 * 552) throw new Exception("Write-through changed the journal format or reserved a visible tail.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }
}
