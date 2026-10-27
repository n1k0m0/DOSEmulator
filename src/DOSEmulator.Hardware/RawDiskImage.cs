// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DOSEmulator.Hardware;

/// <summary>Creates empty sector images atomically, using sparse zero ranges where supported.</summary>
public static class RawDiskImage
{
    public const long MaximumBytes = 128L * 1024 * 1024 * 1024;
    public const long MinimumBytes = 1024 * 1024;

    /// <summary>Converts explicitly binary MiB/GiB to whole sectors and rejects unsupported LBA28 sizes.</summary>
    public static long SizeInBytes(decimal size, string unit)
    {
        decimal factor = unit switch { "MiB" => 1024m * 1024, "GiB" => 1024m * 1024 * 1024, _ => throw new ArgumentException("Size unit must be MiB or GiB.") };
        decimal bytes = size * factor;
        if (bytes < MinimumBytes || bytes > MaximumBytes || bytes != decimal.Truncate(bytes) || bytes % 512 != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Use a whole number of 512-byte sectors between 1 MiB and 128 GiB.");
        return (long)bytes;
    }

    /// <summary>Publishes a fully zeroed new image; cancellation/failure removes only its own temporary file.</summary>
    public static void Create(string path, long bytes, CancellationToken cancellation = default)
    {
        if (bytes < MinimumBytes || bytes > MaximumBytes || bytes % 512 != 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new IOException("The disk image already exists. Choose a new filename.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                cancellation.ThrowIfCancellationRequested();
                bool sparse = OperatingSystem.IsWindows() && SetSparse(file.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                file.SetLength(bytes);
                var range = new ZeroRange { End = bytes };
                bool zeroed = sparse && ZeroData(file.SafeFileHandle, 0x980c8, ref range, 16, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!zeroed)
                {
                    byte[] zeros = new byte[1024 * 1024];
                    for (long offset = 0; offset < bytes; offset += zeros.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        file.Write(zeros.AsSpan(0, (int)Math.Min(zeros.Length, bytes - offset)));
                    }
                }
                cancellation.ThrowIfCancellationRequested(); file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ZeroRange { public long Start, End; }
    /// <summary>Marks this newly created Windows file sparse without touching an existing user image.</summary>
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSparse(SafeFileHandle file, uint code, IntPtr input, uint inputBytes, IntPtr output, uint outputBytes, out uint returned, IntPtr overlapped);
    /// <summary>Requests a logically zero range; callers fall back to explicit zero writes if unsupported.</summary>
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ZeroData(SafeFileHandle file, uint code, ref ZeroRange input, uint inputBytes, IntPtr output, uint outputBytes, out uint returned, IntPtr overlapped);
}
