// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using System.Text;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Explicitly selected Npcap adapter, named for the host UI without selecting a default interface.</summary>
public sealed record HostEthernetAdapter(string Name, string Description);

/// <summary>Windows Npcap-only binding, never a PATH/WinPcap/remote-capture fallback or driver installer.</summary>
internal sealed class NativePcap : IDisposable
{
    private nint _library;
    private static readonly object InitializationGate = new();
    internal const int ErrorBytes = 256;

    /// <summary>Npcap's linked device list uses four pointers and a32-bit flags field under Win64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Device
    {
        internal nint Next, Name, Description, Addresses;
        internal uint Flags;
    }

    /// <summary>Windows timeval has two32-bit longs even in a64-bit process.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PacketHeader
    {
        internal int Seconds, Microseconds;
        internal uint CapturedBytes, OriginalBytes;
    }

    /// <summary>The kernel BPF program is compiled only from a fixed Ethernet filter and validated MAC bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FilterProgram
    {
        internal uint Instructions;
        internal nint Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate(uint options, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindDelegate(out nint devices, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDevicesDelegate(nint devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint OpenDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int snapshot, int promiscuous, int timeout, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LinkDelegate(nint capture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NonblockDelegate(nint capture, int enabled, [Out] byte[] error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NextDelegate(nint capture, out nint header, out nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SendDelegate(nint capture, byte[] data, int bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseDelegate(nint capture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CompileDelegate(nint capture, ref FilterProgram program, [MarshalAs(UnmanagedType.LPUTF8Str)] string filter, int optimize, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetFilterDelegate(nint capture, ref FilterProgram program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeFilterDelegate(ref FilterProgram program);

    private readonly FindDelegate _find;
    private readonly FreeDevicesDelegate _freeDevices;
    internal readonly OpenDelegate Open;
    internal readonly LinkDelegate Link;
    internal readonly NonblockDelegate Nonblock;
    internal readonly NextDelegate Next;
    internal readonly SendDelegate Send;
    internal readonly CloseDelegate Close;
    internal readonly CompileDelegate Compile;
    internal readonly SetFilterDelegate SetFilter;
    internal readonly FreeFilterDelegate FreeFilter;

    /// <summary>Checks only the fixed Npcap installation location; no device is opened during availability checks.</summary>
    internal static string LibraryPath()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Bridged networking requires a64-bit Windows DOSE process.");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("Npcap is not installed. Bridged networking is unavailable; DOSE does not install a driver or substitute NAT.");
        return path;
    }

    /// <summary>Loads installed Npcap and resolves its API only after explicit adapter enumeration or Connect.</summary>
    internal NativePcap()
    {
        _library = NativeNetworkLibrary.Load(LibraryPath());
        try
        {
            var initialize = Export<InitDelegate>("pcap_init");
            byte[] error = new byte[ErrorBytes];
            lock (InitializationGate)
                if (initialize(1, error) != 0) throw new InvalidOperationException("Npcap could not initialize its UTF-8 API.");
            _find = Export<FindDelegate>("pcap_findalldevs"); _freeDevices = Export<FreeDevicesDelegate>("pcap_freealldevs");
            Open = Export<OpenDelegate>("pcap_open_live"); Link = Export<LinkDelegate>("pcap_datalink");
            Nonblock = Export<NonblockDelegate>("pcap_setnonblock"); Next = Export<NextDelegate>("pcap_next_ex");
            Send = Export<SendDelegate>("pcap_sendpacket"); Close = Export<CloseDelegate>("pcap_close");
            Compile = Export<CompileDelegate>("pcap_compile"); SetFilter = Export<SetFilterDelegate>("pcap_setfilter");
            FreeFilter = Export<FreeFilterDelegate>("pcap_freecode");
            if (Marshal.SizeOf<PacketHeader>() != 16 || Marshal.SizeOf<FilterProgram>() != 16 || Marshal.SizeOf<Device>() != 40)
                throw new InvalidOperationException("The Npcap Win64 ABI layout is unsupported.");
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Returns a named C symbol from the exact selected installed library.</summary>
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    /// <summary>Enumerates at most256 local GUID adapters without opening one, interpreting addresses or choosing the first.</summary>
    internal IReadOnlyList<HostEthernetAdapter> Adapters()
    {
        byte[] error = new byte[ErrorBytes];
        if (_find(out nint first, error) != 0) throw new InvalidOperationException("Npcap could not enumerate local adapters. Check that its driver is available and permitted.");
        try
        {
            var result = new List<HostEthernetAdapter>(); var seen = new HashSet<nint>();
            for (nint at = first; at != 0;)
            {
                if (seen.Count >= 256 || !seen.Add(at)) throw new InvalidOperationException("Npcap returned an invalid or oversized adapter list.");
                var device = Marshal.PtrToStructure<Device>(at);
                string name = Text(device.Name, 256), description = Text(device.Description, 1024);
                if (ValidAdapterName(name)) result.Add(new(name, string.IsNullOrWhiteSpace(description) ? name : description));
                at = device.Next;
            }
            return result;
        }
        finally { if (first != 0) _freeDevices(first); }
    }

    /// <summary>Only a local NPF GUID interface is admitted; rpcap, arbitrary devices and implicit defaults are excluded.</summary>
    internal static bool ValidAdapterName(string? name) => name is not null && name.StartsWith("\\Device\\NPF_", StringComparison.Ordinal) &&
        name.Length == 50 && Guid.TryParseExact(name[12..], "B", out _);

    /// <summary>Reads one native adapter label through a finite byte bound instead of an unbounded C-string scan.</summary>
    private static string Text(nint pointer, int maximum)
    {
        if (pointer == 0) return "";
        var bytes = new List<byte>();
        for (int i = 0; i < maximum; i++)
        {
            byte value = Marshal.ReadByte(pointer, i);
            if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(value);
        }
        throw new InvalidOperationException("Npcap returned an unterminated adapter label.");
    }

    /// <summary>Releases this reference only after its capture has been closed by its joined owner worker.</summary>
    public void Dispose()
    {
        nint library = Interlocked.Exchange(ref _library, 0);
        if (library != 0) NativeLibrary.Free(library);
    }
}
