// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Core;

namespace DOSEmulator.Whpx;

/// <summary>
/// Experimental Glide 2 API transport for a Win9x guest shim. One port exit
/// carries a call or an ordered batch of state, texture, and bounded draws. Pointer
/// operands are copied through checked guest RAM. The original SST-1 device
/// remains available for unmodified guests.
/// </summary>
internal sealed unsafe class WhpxGlidePassThrough : IDisposable
{
    internal const ushort Port = 0xF110;
    private const uint BatchOperation = 0xFFFE;
    private const int MaxTransfer = 4 * 1024 * 1024;
    private readonly nint _partition;
    private readonly MemoryBus _bus;
    private readonly nint _library;
    private readonly nint[] _functions = new nint[Glide2Exports.Names.Length];
    private readonly long[] _counts = new long[Glide2Exports.Names.Length];
    private readonly string? _tracePath;
    private readonly ManualResetEventSlim _windowReady = new(false);
    private Thread? _windowThread;
    private uint _windowThreadId;
    private Exception? _windowError;
    private nint _window;
    private WindowProcedure? _windowProcedure;
    private nint _previousWindowProcedure;
    private long _windowCloseMessages;
    private nint _nativeLfb;
    private uint _guestLfb;
    private int _lfbLength;
    private uint _lfbLockType;
    private bool _disposed;

    /// <summary>Loads the selected host renderer and optional call-trace destination.</summary>
    internal WhpxGlidePassThrough(nint partition, MemoryBus bus, string libraryPath)
    {
        _partition = partition;
        _bus = bus;
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        _tracePath = Environment.GetEnvironmentVariable("DOSE_GLIDE_TRACE");
    }

    /// <summary>Translates one guest ABI call or ordered batch into bounded native renderer operations.</summary>
    internal uint Invoke(uint operation, uint guestArguments, uint guestLfbBuffer)
    {
        if (operation == BatchOperation) return InvokeBatch(guestArguments, guestLfbBuffer);
        if (operation == 0 || operation >= Glide2Exports.Names.Length)
            throw new NotSupportedException($"Unknown Glide 2 operation {operation}.");
        int id = (int)operation;
        _counts[id]++;
        // Old shims can still request provider branding. The current guest DLL
        // draws its own MiniGlide artwork and handles these exports locally.
        if (id is 48 or 61) return 0;
        int count = Glide2Exports.ArgumentWords[id];
        Span<ulong> args = stackalloc ulong[count];
        if (count != 0)
        {
            Span<byte> raw = stackalloc byte[count * 4];
            ReadGuest(guestArguments, raw);
            for (int i = 0; i < count; i++) args[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(i * 4));
        }
        if (id == 45) return Version(args);
        nint function = Function(id);
        ulong result = id switch
        {
            34 or 37 => DrawVertexList(function, args),
            39 => CallScalar(function, [0]), // A guest function pointer cannot be called by the host.
            42 => CallWithBuffer(function, args, 0, ReadGuest((uint)args[0], 64)),
            43 => Gamma(function, args),
            53 => LfbLock(function, args, guestLfbBuffer),
            54 => LfbReadRegion(function, args),
            55 => LfbUnlock(function, args),
            68 or 69 => QueryHardware(function, args),
            79 => OpenWindow(function, args),
            86 or 87 => DownloadLevel(function, args),
            88 => DownloadTable(function, args),
            91 => LodBias(function, args),
            98 => TextureSource(function, args),
            116 => FogTableIndexToW(function, args),
            9 or 10 or 12 or 13 or 14 or 15 or 16 or 18 or 19 or 21 or 22 or 24 or 26 or 27 or 28 or 29
                or 31 or 40 or 41 or 46 or 49 or 50 or 71 or 72 or 73 or 78 or 80 or 81 or 82
                or 90 or 92 or 93 or 94 or 104 or 105 or 126 => CallScalar(function, args),
            _ => throw new NotSupportedException($"Glide 2 {Glide2Exports.Names[id]} requires a bridge implementation.")
        };
        if (id == 78) CloseWindow();
        return (uint)result;
    }

    /// <summary>Validates and executes ordered scalar, texture-source and vertex-list batch records.</summary>
    private uint InvokeBatch(uint guestBuffer, uint words)
    {
        if (words is 0 or > 4096) throw new InvalidDataException("Invalid Glide state batch length.");
        byte[] raw = ReadGuest(guestBuffer, checked((int)words * 4));
        Span<ulong> arguments = stackalloc ulong[16];
        Span<byte> nativeTextureInfo = stackalloc byte[24];
        int at = 0;
        while (at < words)
        {
            int id = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at * 4)));
            if (id is 34 or 37)
            {
                if (at + 2 > words) throw new InvalidDataException("Truncated batched Glide draw header.");
                uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((at + 1) * 4));
                if (vertexCount is 0 or > 270 || at + 2 + vertexCount * 15 > words)
                    throw new InvalidDataException("Invalid batched Glide vertex list.");
                arguments[0] = vertexCount;
                fixed (byte* vertices = &raw[(at + 2) * 4])
                {
                    arguments[1] = (ulong)vertices;
                    CallScalar(Function(id), arguments.Slice(0, 2));
                }
                _counts[id]++;
                at += checked((int)(2 + vertexCount * 15));
                continue;
            }
            if (id == 98)
            {
                if (at + 8 > words) throw new InvalidDataException("Truncated batched Glide texture source.");
                for (int i = 0; i < 3; i++)
                    arguments[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((at + 1 + i) * 4));
                nativeTextureInfo.Clear();
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(nativeTextureInfo.Slice(i * 4),
                        BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((at + 4 + i) * 4)));
                fixed (byte* info = nativeTextureInfo)
                {
                    arguments[3] = (ulong)info;
                    CallScalar(Function(id), arguments.Slice(0, 4));
                }
                _counts[id]++;
                at += 8;
                continue;
            }
            if (!IsBatchedState(id)) throw new InvalidDataException($"Invalid batched Glide state operation {id}.");
            int count = Glide2Exports.ArgumentWords[id];
            if (count > arguments.Length || at + 1 + count > words)
                throw new InvalidDataException("Truncated Glide state batch.");
            for (int i = 0; i < count; i++)
                arguments[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((at + 1 + i) * 4));
            _counts[id]++;
            CallScalar(Function(id), arguments.Slice(0, count));
            at += 1 + count;
        }
        return 0;
    }

    /// <summary>Admits only scalar state setters supported by the guest batch protocol.</summary>
    private static bool IsBatchedState(int id) => id is
        9 or 12 or 13 or 18 or 19 or 21 or 22 or 24 or 26 or 27 or 28 or 29 or 31
        or 40 or 41 or 50 or 81 or 82 or 90 or 94 or 104 or 105 or 126;

    /// <summary>Resolves and caches the native renderer export for one checked ABI ordinal.</summary>
    private nint Function(int id)
    {
        if (_functions[id] != 0) return _functions[id];
        string name = Glide2Exports.Names[id] ?? throw new NotSupportedException($"Glide 2 operation {id} has no export.");
        if (!NativeLibrary.TryGetExport(_library, name, out nint function))
            throw new NotSupportedException($"Host Glide library does not export {name}.");
        return _functions[id] = function;
    }

    /// <summary>Dispatches up to ten scalar ABI words using the native calling convention.</summary>
    private static ulong CallScalar(nint function, ReadOnlySpan<ulong> args) => args.Length switch
    {
        0 => ((delegate* unmanaged<ulong>)function)(),
        1 => ((delegate* unmanaged<ulong, ulong>)function)(args[0]),
        2 => ((delegate* unmanaged<ulong, ulong, ulong>)function)(args[0], args[1]),
        3 => ((delegate* unmanaged<ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2]),
        4 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3]),
        5 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4]),
        6 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4], args[5]),
        7 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
        8 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
        9 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
        10 => ((delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>)function)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
        _ => throw new NotSupportedException("Glide call exceeds the transport's ten argument words.")
    };

    /// <summary>Pins a checked transfer buffer while replacing the selected guest pointer.</summary>
    private ulong CallWithBuffer(nint function, Span<ulong> args, int index, byte[] buffer)
    {
        if (buffer.Length is 0 or > MaxTransfer) throw new InvalidDataException("Invalid Glide transfer length.");
        fixed (byte* pointer = buffer)
        {
            args[index] = (ulong)pointer;
            return CallScalar(function, args);
        }
    }

    /// <summary>Copies the MiniGlide version into the guest ABI buffer without provider branding.</summary>
    private uint Version(ReadOnlySpan<ulong> args)
    {
        byte[] version = new byte[80];
        "2.43 MiniGlide 1.0"u8.CopyTo(version);
        WriteGuest((uint)args[0], version);
        return 0;
    }

    /// <summary>Returns the supported hardware-query record in the guest layout.</summary>
    private ulong QueryHardware(nint function, Span<ulong> args)
    {
        uint guest = (uint)args[0];
        byte[] configuration = new byte[256];
        ulong result = CallWithBuffer(function, args, 0, configuration);
        if (result != 0) WriteGuest(guest, configuration.AsSpan(0, 148));
        return result;
    }

    /// <summary>Copies a bounded contiguous guest vertex list before native drawing.</summary>
    private ulong DrawVertexList(nint function, Span<ulong> args)
    {
        int count = checked((int)args[0]);
        if (count is < 0 or > 4096) throw new InvalidDataException("Invalid Glide vertex count.");
        byte[] vertices = ReadGuest((uint)args[1], checked(count * 60));
        return CallWithBuffer(function, args, 1, vertices);
    }

    /// <summary>Validates dimensions and partial rows before transferring a texture level.</summary>
    private ulong DownloadLevel(nint function, Span<ulong> args)
    {
        int lod = checked((int)args[2]);
        int aspect = checked((int)args[4]);
        int format = checked((int)args[5]);
        if (lod is < 0 or > 8 || aspect is < 0 or > 6 || format is < 0 or > 15)
            throw new InvalidDataException("Unsupported Glide texture level.");
        int dimension = 256 >> lod;
        int width = aspect <= 3 ? dimension : Math.Max(1, dimension >> (aspect - 3));
        int height = aspect >= 3 ? dimension : Math.Max(1, dimension >> (3 - aspect));
        int rows = height;
        if (args.Length == 10)
        {
            int first = checked((int)args[8]), last = checked((int)args[9]);
            if (first < 0 || last < first || last >= height)
                throw new InvalidDataException("Invalid partial Glide texture row range.");
            rows = last - first + 1;
        }
        int bytes = checked(width * rows * (format >= 8 ? 2 : 1));
        const int pointerIndex = 7;
        byte[] pixels;
        try { pixels = ReadGuest((uint)args[pointerIndex], bytes); }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException(
                $"Glide texture upload {args.Length} args, {width}x{rows}, {bytes} bytes, " +
                $"operands=[{string.Join(",", args.ToArray().Select(value => value.ToString("X")))}]",
                error);
        }
        return CallWithBuffer(function, args, pointerIndex, pixels);
    }

    /// <summary>Copies the selected palette or conversion table into native-accessible storage.</summary>
    private ulong DownloadTable(nint function, Span<ulong> args)
    {
        int bytes = args[1] == 2 ? 1024 : 112;
        byte[] table = ReadGuest((uint)args[2], bytes);
        return CallWithBuffer(function, args, 2, table);
    }

    /// <summary>Expands guest texture metadata into the native pointer-sized structure.</summary>
    private ulong TextureSource(nint function, Span<ulong> args)
    {
        byte[] guestInfo = ReadGuest((uint)args[3], 20);
        byte[] nativeInfo = new byte[24];
        guestInfo.AsSpan(0, 16).CopyTo(nativeInfo);
        return CallWithBuffer(function, args, 3, nativeInfo);
    }

    /// <summary>Reads a bounded native framebuffer region back into checked guest RAM.</summary>
    private ulong LfbReadRegion(nint function, Span<ulong> args)
    {
        int height = checked((int)args[4]), stride = checked((int)args[5]);
        int size = checked(height * stride);
        if (size is <= 0 or > MaxTransfer) throw new InvalidDataException("Invalid Glide LFB read size.");
        uint guest = (uint)args[6];
        byte[] pixels = new byte[size];
        ulong result = CallWithBuffer(function, args, 6, pixels);
        if (result != 0) WriteGuest(guest, pixels);
        return result;
    }

    /// <summary>Stages a framebuffer lock and translates native lock metadata for the guest.</summary>
    private ulong LfbLock(nint function, Span<ulong> args, uint guestBuffer)
    {
        if (guestBuffer == 0 || _nativeLfb != 0) throw new InvalidOperationException("Invalid nested Glide LFB lock.");
        uint guestInfoPointer = (uint)args[5];
        byte[] info = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(info, 32);
        ulong result = CallWithBuffer(function, args, 5, info);
        if (result == 0) return 0;
        _nativeLfb = (nint)BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(8));
        _guestLfb = guestBuffer;
        _lfbLockType = (uint)args[0];
        int stride = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16)));
        int height = checked((int)CallScalar(Function(71), []));
        _lfbLength = checked(stride * height);
        if (_nativeLfb == 0 || _lfbLength is <= 0 or > MaxTransfer)
            throw new InvalidDataException("Host Glide returned an invalid LFB allocation.");
        if ((_lfbLockType & 1) == 0)
        {
            byte[] pixels = new byte[_lfbLength];
            Marshal.Copy(_nativeLfb, pixels, 0, pixels.Length);
            WriteGuest(_guestLfb, pixels);
        }
        byte[] guestInfo = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(guestInfo, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(guestInfo.AsSpan(4), _guestLfb);
        info.AsSpan(16, 12).CopyTo(guestInfo.AsSpan(8));
        WriteGuest(guestInfoPointer, guestInfo);
        return result;
    }

    /// <summary>Copies guest writes back to a locked framebuffer and releases its staging state.</summary>
    private ulong LfbUnlock(nint function, Span<ulong> args)
    {
        if (_nativeLfb == 0) return CallScalar(function, args);
        if ((_lfbLockType & 1) != 0)
        {
            byte[] pixels = ReadGuest(_guestLfb, _lfbLength);
            Marshal.Copy(pixels, 0, _nativeLfb, pixels.Length);
        }
        ulong result = CallScalar(function, args);
        _nativeLfb = 0;
        _guestLfb = 0;
        _lfbLength = 0;
        return result;
    }

    /// <summary>Creates the presentation window and substitutes its handle for the guest window.</summary>
    private ulong OpenWindow(nint function, Span<ulong> args)
    {
        if (_windowThread is null)
        {
            _windowReady.Reset();
            _windowError = null;
            _windowThread = new Thread(WindowMessageLoop) { IsBackground = true, Name = "MiniGlide host window" };
            _windowThread.Start();
            if (!_windowReady.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The Glide host window did not initialize.");
            if (_windowError is not null) throw new InvalidOperationException("Could not create host Glide window.", _windowError);
        }
        args[0] = (ulong)_window;
        ulong opened = CallScalar(function, args);
        // Some Glide renderers post WM_CLOSE during a guest map transition even
        // though the guest has not called grSstWinClose. Keep our HWND alive so
        // subsequent swaps still have a visible presentation target.
        if (opened != 0 && _windowProcedure is null && IsWindow(_window))
        {
            _windowProcedure = WindowCallback;
            _previousWindowProcedure = SetWindowLongPtrW(_window, -4,
                Marshal.GetFunctionPointerForDelegate(_windowProcedure));
            if (_previousWindowProcedure == 0)
                throw new InvalidOperationException($"Could not protect Glide host window: {Marshal.GetLastPInvokeError()}.");
        }
        return opened;
    }

    /// <summary>Owns the MiniGlide presentation window and pumps its native message queue.</summary>
    private void WindowMessageLoop()
    {
        try
        {
            _windowThreadId = GetCurrentThreadId();
            _window = CreateWindowExW(0, "STATIC", "MiniGlide", 0x10CF0000,
                90, 90, 700, 550, 0, 0, 0, 0);
            if (_window == 0) throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastPInvokeError()}.");
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
            _windowReady.Set();
            while (GetMessageW(out NativeMessage message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            if (IsWindow(_window))
            {
                if (_previousWindowProcedure != 0)
                    SetWindowLongPtrW(_window, -4, _previousWindowProcedure);
                DestroyWindow(_window);
            }
        }
        catch (Exception error)
        {
            _windowError = error;
            _windowReady.Set();
        }
        finally { _window = 0; _windowProcedure = null; _previousWindowProcedure = 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Keeps renderer close requests from destroying a still-open guest presentation target.</summary>
    private nint WindowCallback(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x10) // WM_CLOSE; only grSstWinClose owns this window's lifetime.
        {
            Interlocked.Increment(ref _windowCloseMessages);
            return 0;
        }
        return CallWindowProcW(_previousWindowProcedure, window, message, wParam, lParam);
    }

    /// <summary>Reinterprets the ABI float bits for the native gamma call.</summary>
    private static ulong Gamma(nint function, ReadOnlySpan<ulong> args)
    {
        ((delegate* unmanaged<float, void>)function)(BitConverter.Int32BitsToSingle((int)args[0]));
        return 0;
    }

    /// <summary>Reinterprets the texture LOD bias float for the native renderer.</summary>
    private static ulong LodBias(nint function, ReadOnlySpan<ulong> args)
    {
        ((delegate* unmanaged<ulong, float, void>)function)(args[0], BitConverter.Int32BitsToSingle((int)args[1]));
        return 0;
    }

    /// <summary>Returns a native fog-table float using the guest transport bit representation.</summary>
    private static ulong FogTableIndexToW(nint function, ReadOnlySpan<ulong> args)
    {
        float value = ((delegate* unmanaged<ulong, float>)function)(args[0]);
        return (uint)BitConverter.SingleToInt32Bits(value);
    }

    /// <summary>Copies a bounded guest virtual range through checked RAM page translations.</summary>
    private byte[] ReadGuest(uint address, int count)
    {
        if (count < 0 || count > MaxTransfer) throw new InvalidDataException("Invalid Glide guest read size.");
        byte[] result = new byte[count];
        ReadGuest(address, result);
        return result;
    }

    /// <summary>Copies a bounded guest virtual range through checked RAM page translations.</summary>
    private void ReadGuest(uint address, Span<byte> result)
    {
        int copied = 0;
        while (copied < result.Length)
        {
            uint current = checked(address + (uint)copied);
            int amount = Math.Min(result.Length - copied, 4096 - (int)(current & 4095));
            uint physical = Translate(current, false);
            if (!_bus.TryGetReadOnlyPhysicalRamSpan(physical, amount, null, out var source))
                throw new InvalidDataException($"Glide input does not refer to guest RAM: {current:X8}.");
            source.CopyTo(result.Slice(copied, amount));
            copied += amount;
        }
    }

    /// <summary>Copies native output into checked writable guest RAM pages.</summary>
    private void WriteGuest(uint address, ReadOnlySpan<byte> bytes)
    {
        int copied = 0;
        while (copied < bytes.Length)
        {
            uint current = checked(address + (uint)copied);
            int amount = Math.Min(bytes.Length - copied, 4096 - (int)(current & 4095));
            uint physical = Translate(current, true);
            if (!_bus.TryGetPhysicalRamSpan(physical, amount, null, out var target))
                throw new InvalidDataException($"Glide output does not refer to guest RAM: {current:X8}.");
            bytes.Slice(copied, amount).CopyTo(target);
            copied += amount;
        }
    }

    /// <summary>Translates one guest virtual address with the requested native access checks.</summary>
    private uint Translate(uint virtualAddress, bool write)
    {
        int hr = WhpxNative.WHvTranslateGva(_partition, 0, virtualAddress, write ? 0x12u : 0x11u,
            out ulong result, out ulong baseAddress);
        if (hr < 0 || result != 0 || baseAddress > uint.MaxValue)
            throw new InvalidDataException($"Glide guest pointer is not mapped: {virtualAddress:X8} (0x{result:X}, 0x{hr:X8}).");
        return checked((uint)(baseAddress & ~4095UL) + (virtualAddress & 4095));
    }

    /// <summary>Stops and joins the dedicated presentation thread at an explicit guest close.</summary>
    private void CloseWindow()
    {
        if (_windowThread is null) return;
        if (_windowThreadId != 0) PostThreadMessageW(_windowThreadId, 0x12, 0, 0); // WM_QUIT
        if (!_windowThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Glide host window did not close.");
        _windowThread = null;
        _windowThreadId = 0;
        _window = 0;
    }

    /// <summary>Closes presentation, writes optional call counts and releases the host library once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseWindow();
        if (_tracePath is { Length: > 0 })
        {
            var calls = _counts.Select((count, id) => new { id, name = Glide2Exports.Names[id], count })
                .Where(row => row.count != 0).ToArray();
            File.WriteAllText(_tracePath, JsonSerializer.Serialize(calls, new JsonSerializerOptions { WriteIndented = true }));
        }
        NativeLibrary.Free(_library);
    }

    /// <summary>Creates the native window used for renderer presentation.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    /// <summary>Shows the presentation window without taking guest input focus.</summary>
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    /// <summary>Destroys the native presentation window on its owner thread.</summary>
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    /// <summary>Checks that the presentation handle still names a live native window.</summary>
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    /// <summary>Installs or restores the presentation window procedure.</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint procedure);
    /// <summary>Forwards unhandled messages to the previous native window procedure.</summary>
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint procedure, nint window, uint message, nint wParam, nint lParam);
    /// <summary>Obtains the window thread identifier for orderly message-queue shutdown.</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    /// <summary>Waits for the next native presentation-thread message.</summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out NativeMessage message, nint window, uint min, uint max);
    /// <summary>Translates keyboard messages in the native window queue.</summary>
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);
    /// <summary>Dispatches one queued message to its native window procedure.</summary>
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessageW(ref NativeMessage message);
    /// <summary>Posts the explicit quit request to the presentation owner thread.</summary>
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }
}
