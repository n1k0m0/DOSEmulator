// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;

namespace DOSEmulator.Whpx;

internal static unsafe class WhpxNative
{
    private const string Library = "WinHvPlatform.dll";
    internal const uint Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rip = 0x10, Rflags = 0x11;
    internal const uint Es = 0x12, Cs = 0x13, Ss = 0x14, Ds = 0x15;
    internal const uint PendingInterruption = 0x80000000, PendingEvent = 0x80000002;
    internal const uint ReadWriteExecuteDirty = 0xf;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct RegisterValue(ulong low, ulong high = 0)
    {
        public ulong Low = low;
        public ulong High = high;
    }

    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetCapability(uint code, out ulong value, uint size, out uint written);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCreatePartition(out nint partition);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetPartitionProperty(nint partition, uint property, in uint value, uint size);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetPartitionProperty(nint partition, uint property, in ulong value, uint size);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetupPartition(nint partition);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvDeletePartition(nint partition);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCreateVirtualProcessor(nint partition, uint index, uint flags);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvDeleteVirtualProcessor(nint partition, uint index);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvMapGpaRange(nint partition, nint memory, ulong guest, ulong bytes, uint flags);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvUnmapGpaRange(nint partition, ulong guest, ulong bytes);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetVirtualProcessorRegisters(nint partition, uint index, [In] uint[] names, uint count, [In] RegisterValue[] values);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetVirtualProcessorRegisters(nint partition, uint index, uint* names, uint count, RegisterValue* values);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetVirtualProcessorRegisters(nint partition, uint index, [In] uint[] names, uint count, [Out] RegisterValue[] values);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetVirtualProcessorRegisters(nint partition, uint index, uint* names, uint count, RegisterValue* values);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvRunVirtualProcessor(nint partition, uint index, [Out] byte[] exit, uint size);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCancelRunVirtualProcessor(nint partition, uint index, uint flags);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvQueryGpaRangeDirtyBitmap(nint partition, ulong guest, ulong bytes, [Out] ulong[] bitmap, uint bitmapBytes);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvTranslateGva(nint partition, uint index, ulong gva, uint flags, out ulong result, out ulong gpa);

    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"WHP {operation} failed: 0x{result:X8}.");
    }
}
