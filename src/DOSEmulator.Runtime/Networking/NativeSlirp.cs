// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Explicit Win64 libslirp4.9.3 binding. Loading this class never opens a guest or host listening port.</summary>
internal sealed class NativeSlirp : IDisposable
{
    /// <summary>libslirp's process-global initialization, clock and resolver caches require serialized native calls.</summary>
    internal static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private nint _library;
    private bool _winsock;

    /// <summary>Exact LLP64 layout from the pinned libslirp4.9.3 Windows header; all unlisted fields remain zero.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct Config
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal int Restricted;
        [FieldOffset(8)] internal byte Ipv4;
        [FieldOffset(12)] internal uint Network;
        [FieldOffset(16)] internal uint Netmask;
        [FieldOffset(20)] internal uint Host;
        [FieldOffset(24)] internal byte Ipv6;
        [FieldOffset(96)] internal uint DhcpStart;
        [FieldOffset(100)] internal uint Nameserver;
        [FieldOffset(136)] internal nuint Mtu;
        [FieldOffset(144)] internal nuint Mru;
        [FieldOffset(152)] internal byte DisableHostLoopback;
        [FieldOffset(153)] internal byte EnableUnsafeEmulation;
        [FieldOffset(176)] internal byte DisableDns;
        [FieldOffset(177)] internal byte DisableDhcp;
    }

    /// <summary>All thirteen version6 callback pointers, kept in unmanaged storage until cleanup completes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal nint SendPacket, GuestError, ClockGetNs, TimerNew, TimerFree, TimerMod;
        internal nint RegisterFd, UnregisterFd, Notify, InitCompleted, TimerNewOpaque, RegisterSocket, UnregisterSocket;
    }

    /// <summary>A Windows WSAPOLLFD uses a pointer-sized SOCKET and two16-bit event fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        internal nuint Socket;
        internal short Events, Returned;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint WriteCallback(nint buffer, nuint length, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ErrorCallback(nint message, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long ClockCallback(nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TimerFreeCallback(nint timer, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TimerModCallback(nint timer, long expiresMilliseconds, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint TimerNewCallback(int timerId, nint callbackOpaque, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FdCallback(int socket, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SocketCallback(nuint socket, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NotifyCallback(nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void InitCallback(nint slirp, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AddPollCallback(nuint socket, int events, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetEventsCallback(int index, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint NewDelegate(ref Config config, nint callbacks, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CleanupDelegate(nint slirp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void InputDelegate(nint slirp, byte[] bytes, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FillDelegate(nint slirp, ref uint timeout, AddPollCallback callback, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PollDelegate(nint slirp, int error, GetEventsCallback callback, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TimerDelegate(nint slirp, int timerId, nint opaque);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AddForwardDelegate(nint slirp, int udp, uint hostAddress, int hostPort, uint guestAddress, int guestPort);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RemoveForwardDelegate(nint slirp, int udp, uint hostAddress, int hostPort);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VersionDelegate();

    internal readonly NewDelegate New;
    internal readonly CleanupDelegate Cleanup;
    internal readonly InputDelegate Input;
    internal readonly FillDelegate Fill;
    internal readonly PollDelegate Poll;
    internal readonly TimerDelegate HandleTimer;
    internal readonly AddForwardDelegate AddForward;
    internal readonly RemoveForwardDelegate RemoveForward;

    /// <summary>Loads only the prepared, hash-bound local DLL directory and the pointer-sized polling exports.</summary>
    internal NativeSlirp(string? directory)
    {
        string path = VerifyRuntime(directory);
        _library = NativeNetworkLibrary.Load(Path.Combine(path, "libslirp-0.dll"));
        try
        {
            if (WSAStartup(0x0202, new byte[512]) != 0) throw new InvalidOperationException("Windows sockets could not initialize for the explicit network connection.");
            _winsock = true;
            New = Export<NewDelegate>("slirp_new"); Cleanup = Export<CleanupDelegate>("slirp_cleanup");
            Input = Export<InputDelegate>("slirp_input"); Fill = Export<FillDelegate>("slirp_pollfds_fill_socket");
            Poll = Export<PollDelegate>("slirp_pollfds_poll"); HandleTimer = Export<TimerDelegate>("slirp_handle_timer");
            AddForward = Export<AddForwardDelegate>("slirp_add_hostfwd"); RemoveForward = Export<RemoveForwardDelegate>("slirp_remove_hostfwd");
            string version = Marshal.PtrToStringUTF8(Export<VersionDelegate>("slirp_version_string")()) ?? "";
            if (version != "4.9.3") throw new InvalidOperationException("DOSE requires the prepared libslirp4.9.3 runtime.");
            if (Marshal.SizeOf<Config>() != 192 || Marshal.SizeOf<Callbacks>() != 104 || Marshal.SizeOf<PollFd>() != 16)
                throw new InvalidOperationException("The libslirp Win64 ABI layout does not match this process.");
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Returns a single native symbol with its explicit C ABI.</summary>
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    /// <summary>Checks the prepared DLLs without loading native code, opening sockets or consulting PATH.</summary>
    internal static string VerifyRuntime(string? directory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("NAT and host-only networking require a64-bit Windows DOSE process.");
        string root = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "native", "win-x64"));
        string manifest = Path.Combine(root, "network-runtime.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException("The libslirp runtime is missing. Prepare it with scripts/prepare-network-runtime.py.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var value = document.RootElement;
        if (value.GetProperty("format").GetString() != "DOSE-NETWORK-RUNTIME-1" || value.GetProperty("slirpVersion").GetString() != "4.9.3")
            throw new InvalidDataException("The selected native networking manifest is unsupported.");
        var files = value.GetProperty("dlls");
        if (files.GetArrayLength() is < 2 or > 16) throw new InvalidDataException("The native networking DLL set is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? "";
            if (name != Path.GetFileName(name) || !(name.StartsWith("lib", StringComparison.Ordinal) || name == "zlib1.dll") || !name.EndsWith(".dll", StringComparison.Ordinal) || !names.Add(name))
                throw new InvalidDataException("A native networking DLL name is invalid or repeated.");
            using var input = File.OpenRead(Path.Combine(root, name));
            if (input.Length != item.GetProperty("bytes").GetInt64() || Convert.ToHexString(SHA256.HashData(input)) != item.GetProperty("sha256").GetString())
                throw new InvalidDataException("A prepared native networking DLL failed its complete identity check.");
        }
        if (!names.Contains("libslirp-0.dll") || !names.Contains("libglib-2.0-0.dll"))
            throw new InvalidDataException("The native networking runtime lacks libslirp or GLib.");
        return root;
    }

    /// <summary>One common host clock for all external stacks; never part of the deterministic PC clock or snapshots.</summary>
    internal static long Nanoseconds()
    {
        UInt128 value = (UInt128)(ulong)Clock.ElapsedTicks * 1_000_000_000 / (ulong)Stopwatch.Frequency;
        return value > (UInt128)long.MaxValue ? long.MaxValue : (long)value;
    }

    /// <summary>Polls only libslirp-owned SOCKET handles, outside the shared native-library gate.</summary>
    [DllImport("ws2_32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int WSAPoll([In, Out] PollFd[] sockets, uint count, int timeoutMilliseconds);

    /// <summary>Acquires a Windows socket reference independently from managed Socket initialization.</summary>
    [DllImport("ws2_32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WSAStartup(ushort version, [Out] byte[] data);

    /// <summary>Releases only this explicit backend's socket reference after native stack cleanup.</summary>
    [DllImport("ws2_32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WSACleanup();

    /// <summary>Reports a failed Winsock poll instead of silently spinning a connected but unusable backend.</summary>
    [DllImport("ws2_32.dll", EntryPoint = "WSAGetLastError", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int LastSocketError();

    /// <summary>Releases this library reference only after its stack has been cleaned up on its owning worker.</summary>
    public void Dispose()
    {
        nint library = Interlocked.Exchange(ref _library, 0);
        if (library != 0) NativeLibrary.Free(library);
        if (_winsock) { _winsock = false; _ = WSACleanup(); }
    }
}

/// <summary>Loads an absolute DLL and its adjacent dependencies without changing any process-wide search path.</summary>
internal static class NativeNetworkLibrary
{
    /// <summary>Use only the selected DLL directory and Windows System32 for dependencies.</summary>
    internal static nint Load(string absolutePath)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native networking requires64-bit Windows.");
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("A native networking DLL requires an absolute path.");
        nint library = LoadLibraryExW(absolutePath, 0, 0x00000100 | 0x00000800);
        if (library == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected native networking DLL or a required adjacent dependency could not be loaded.");
        return library;
    }

    /// <summary>Safe absolute loader; no SetDllDirectory, PATH lookup or current-directory fallback.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
}
