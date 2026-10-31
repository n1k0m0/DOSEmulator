// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;

namespace DOSEmulator.Whpx;

internal static unsafe class WhpxNative
{
    private const string Library = "WinHvPlatform.dll";
    internal const uint Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rip = 0x10, Rflags = 0x11;
    internal const uint Es = 0x12, Cs = 0x13, Ss = 0x14, Ds = 0x15;
    internal const uint PendingInterruption = 0x80000000, InterruptState = 0x80000001,
        PendingEvent = 0x80000002, DeliverabilityNotifications = 0x80000004;
    internal const uint ReadWriteExecuteDirty = 0xf;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct RegisterValue(ulong low, ulong high = 0)
    {
        public ulong Low = low;
        public ulong High = high;
    }

    /// <summary>Queries the installed hypervisor API capability without changing a guest or a host endpoint.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetCapability(uint code, out ulong value, uint size, out uint written);
    /// <summary>Creates one independent native partition handle.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCreatePartition(out nint partition);
    /// <summary>Sets one explicit native partition property before setup.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetPartitionProperty(nint partition, uint property, in uint value, uint size);
    /// <summary>Sets one explicit native partition property before setup.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetPartitionProperty(nint partition, uint property, in ulong value, uint size);
    /// <summary>Finalizes the partition properties before guest memory and CPU creation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetupPartition(nint partition);
    /// <summary>Releases the stopped partition after its CPU and memory owners have ended.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvDeletePartition(nint partition);
    /// <summary>Creates the sole virtual processor in this partition.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCreateVirtualProcessor(nint partition, uint index, uint flags);
    /// <summary>Deletes the stopped virtual processor before its partition is released.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvDeleteVirtualProcessor(nint partition, uint index);
    /// <summary>Maps aligned owner storage with the exact requested guest physical permissions.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvMapGpaRange(nint partition, nint memory, ulong guest, ulong bytes, uint flags);
    /// <summary>Removes only the specified stopped guest physical range.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvUnmapGpaRange(nint partition, ulong guest, ulong bytes);
    /// <summary>Writes the specified complete native register values in one API operation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetVirtualProcessorRegisters(nint partition, uint index, [In] uint[] names, uint count, [In] RegisterValue[] values);
    /// <summary>Writes the specified complete native register values in one API operation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvSetVirtualProcessorRegisters(nint partition, uint index, uint* names, uint count, RegisterValue* values);
    /// <summary>Reads the specified complete native register values in one API operation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetVirtualProcessorRegisters(nint partition, uint index, [In] uint[] names, uint count, [Out] RegisterValue[] values);
    /// <summary>Reads the specified complete native register values in one API operation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvGetVirtualProcessorRegisters(nint partition, uint index, uint* names, uint count, RegisterValue* values);
    /// <summary>Executes the sole native CPU until a guest exit or owner cancellation.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvRunVirtualProcessor(nint partition, uint index, [Out] byte[] exit, uint size);
    /// <summary>Requests return from the sole native CPU without changing guest architectural state.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvCancelRunVirtualProcessor(nint partition, uint index, uint flags);
    /// <summary>Queries dirty tracking for the isolated primitive probe's explicitly mapped pages.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvQueryGpaRangeDirtyBitmap(nint partition, ulong guest, ulong bytes, [Out] ulong[] bitmap, uint bitmapBytes);
    /// <summary>Obtains the native CPU's actual guest translation and translation result flags.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WHvTranslateGva(nint partition, uint index, ulong gva, uint flags, out ulong result, out ulong gpa);

    /// <summary>Turns a failing hypervisor HRESULT into a named owner-side exception.</summary>
    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"WHP {operation} failed: 0x{result:X8}.");
    }
}
