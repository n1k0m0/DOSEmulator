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
    private const uint BeginSwapOperation = 0xFFFD, PollSwapOperation = 0xFFFC;
    private const uint CooperativeSwapV1 = 0x4D475331;
    private readonly WhpxGlideSwapWorker _swapWorker = new();
    private const int MaxTransfer = 4 * 1024 * 1024;
    private readonly nint _partition;
    private readonly MemoryBus _bus;
    private readonly nint _library;
    private readonly nint[] _functions = new nint[Glide2Exports.Names.Length];
    private readonly long[] _counts = new long[Glide2Exports.Names.Length];
    private long _transportExits, _batchExits;
    private readonly byte[] _batchBytes = new byte[4096 * 4];
    private byte[] _lfbPixels = [];
    private byte[] _texturePixels = [];
    private readonly string? _tracePath;
    private readonly ManualResetEventSlim _windowReady = new(false);
    private Thread? _windowThread;
    private uint _windowThreadId;
    private Exception? _windowError;
    private nint _window;
    private MiniGlideSurface? _presentation;
    private WindowProcedure? _baseWindowProcedure;
    private nint _defaultWindowProcedure;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transportExits++;
        if (operation == PollSwapOperation) return _swapWorker.Poll(guestArguments);
        if (operation == BeginSwapOperation)
        {
            Span<byte> interval = stackalloc byte[4];
            ReadGuest(guestArguments, interval);
            uint token = _swapWorker.Begin(Function(16), BinaryPrimitives.ReadUInt32LittleEndian(interval));
            _counts[16]++;
            return token;
        }
        if (_swapWorker.HasPending) _swapWorker.Drain();
        if (operation == BatchOperation) { _batchExits++; return InvokeBatch(guestArguments, guestLfbBuffer); }
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
        if (id is 49 or 78) MiniGlidePresentation.Close(_presentation);
        ulong result = id switch
        {
            32 or 38 or 107 => DrawPointers(function, args),
            34 or 37 => DrawVertexList(function, args),
            35 => CallWithBuffer(function, args, 0, ReadGuest((uint)args[0], 60)),
            39 => CallScalar(function, [0]), // A guest function pointer cannot be called by the host.
            42 => CallWithBuffer(function, args, 0, ReadGuest((uint)args[0], 64)),
            43 => Gamma(function, args),
            44 => OutputBuffer(function, args, 312),
            47 => CallWithBuffer(function, args, 0, ReadGuest((uint)args[0], 312)),
            53 => LfbLock(function, args, guestLfbBuffer),
            54 => LfbReadRegion(function, args),
            55 => LfbUnlock(function, args),
            68 or 69 => QueryHardware(function, args),
            67 => OutputBuffer(function, args, 20),
            79 => OpenWindow(function, args),
            85 => DownloadMipMap(function, args),
            86 or 87 => DownloadLevel(function, args),
            88 or 89 => DownloadTable(function, args),
            91 => LodBias(function, args),
            98 => TextureSource(function, args),
            99 => TextureMemoryRequired(function, args),
            116 => FogTableIndexToW(function, args),
            113 or 114 or 115 => OutputBuffer(function, args, 64, fog: true),
            9 or 10 or 12 or 13 or 14 or 15 or 16 or 18 or 19 or 20 or 21 or 22 or 24 or 25 or 26 or 27 or 28 or 29
                or 31 or 40 or 41 or 46 or 49 or 50 or 59 or 63 or 64 or 66 or 70 or 71 or 72 or 73 or 78 or 80 or 81 or 82
                or 90 or 92 or 93 or 94 or 104 or 105 or 126 => CallScalar(function, args),
            _ => throw new NotSupportedException($"Glide 2 {Glide2Exports.Names[id]} requires a bridge implementation.")
        };
        if (id is 49 or 78) CloseWindow();
        return (uint)result;
    }

    /// <summary>Validates and executes ordered scalar, texture-source and vertex-list batch records.</summary>
    private uint InvokeBatch(uint guestBuffer, uint words)
    {
        if (words is 0 or > 4096) throw new InvalidDataException("Invalid Glide state batch length.");
        byte[] raw = _batchBytes;
        ReadGuest(guestBuffer, raw.AsSpan(0, checked((int)words * 4)));
        Span<ulong> arguments = stackalloc ulong[16];
        Span<byte> nativeTextureInfo = stackalloc byte[24];
        int at = 0;
        while (at < words)
        {
            int id = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at * 4)));
            if (id is 32 or 34 or 37 or 38 or 107)
            {
                if (at + 2 > words) throw new InvalidDataException("Truncated batched Glide draw header.");
                uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((at + 1) * 4));
                if (vertexCount is 0 or > 270 || at + 2 + vertexCount * 15 > words)
                    throw new InvalidDataException("Invalid batched Glide vertex list.");
                bool pointers = id is 32 or 38 or 107;
                if (pointers && vertexCount != (id == 32 ? 2u : 3u))
                    throw new InvalidDataException("Invalid batched Glide pointer-vertex count.");
                fixed (byte* vertices = &raw[(at + 2) * 4])
                {
                    if (pointers)
                    {
                        for (int i = 0; i < vertexCount; i++) arguments[i] = (ulong)(vertices + i * 60);
                        CallScalar(Function(id), arguments.Slice(0, (int)vertexCount));
                    }
                    else
                    {
                        arguments[0] = vertexCount; arguments[1] = (ulong)vertices;
                        CallScalar(Function(id), arguments.Slice(0, 2));
                    }
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

    /// <summary>Preflights the complete guest destination before a provider writes an opaque state/statistics block or byte fog table.</summary>
    private ulong OutputBuffer(nint function, ReadOnlySpan<ulong> args, int length, bool fog = false)
    {
        uint address = (uint)args[0];
        int checkedBytes = 0;
        while (checkedBytes < length)
        {
            uint current = checked(address + (uint)checkedBytes);
            int amount = Math.Min(length - checkedBytes, 4096 - (int)(current & 4095));
            uint physical = Translate(current, true);
            if (!_bus.TryGetPhysicalRamSpan(physical, amount, null, out _))
                throw new InvalidDataException("Glide output does not refer to writable guest RAM.");
            checkedBytes += amount;
        }
        byte[] buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            if (!fog) ((delegate* unmanaged<byte*, void>)function)(pointer);
            else if (args.Length == 2)
                ((delegate* unmanaged<byte*, float, void>)function)(pointer, BitConverter.Int32BitsToSingle((int)args[1]));
            else
                ((delegate* unmanaged<byte*, float, float, void>)function)(pointer,
                    BitConverter.Int32BitsToSingle((int)args[1]), BitConverter.Int32BitsToSingle((int)args[2]));
        }
        WriteGuest(address, buffer);
        return 0;
    }

    /// <summary>Copies the unchanged version buffer and reports the own transport extension through this void API's private return word.</summary>
    private uint Version(ReadOnlySpan<ulong> args)
    {
        byte[] version = new byte[80];
        "2.43 MiniGlide 1.0"u8.CopyTo(version);
        WriteGuest((uint)args[0], version);
        return CooperativeSwapV1;
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

    /// <summary>Copies each separately addressed line or triangle vertex, preserving the provider's clipping operation.</summary>
    private ulong DrawPointers(nint function, Span<ulong> args)
    {
        Span<byte> vertices = stackalloc byte[180];
        for (int i = 0; i < args.Length; i++)
            ReadGuest((uint)args[i], vertices.Slice(i * 60, 60));
        fixed (byte* first = vertices)
        {
            for (int i = 0; i < args.Length; i++) args[i] = (ulong)(first + i * 60);
            return CallScalar(function, args);
        }
    }

    /// <summary>Expands only the four metadata words needed by texture queries and selections; the data pointer is not consumed.</summary>
    private byte[] TextureInfo(uint address)
    {
        byte[] info = new byte[24];
        ReadGuest(address, info.AsSpan(0, 16));
        uint small = BinaryPrimitives.ReadUInt32LittleEndian(info), large = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4));
        uint aspect = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8)), format = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(12));
        if (small > 8 || large > small || aspect > 6 || format > 15)
            throw new InvalidDataException("Unsupported Glide texture metadata.");
        return info;
    }

    /// <summary>Passes the pointer-sized host texture record to the provider's original TMU allocation-size query.</summary>
    private ulong TextureMemoryRequired(nint function, Span<ulong> args) =>
        CallWithBuffer(function, args, 1, TextureInfo((uint)args[1]));

    /// <summary>Transfers tightly packed host-image levels, including levels skipped by the TMU parity mask, without reading TMU alignment padding.</summary>
    private ulong DownloadMipMap(nint function, Span<ulong> args)
    {
        uint address = (uint)args[3];
        byte[] info = TextureInfo(address);
        int small = (int)BinaryPrimitives.ReadUInt32LittleEndian(info), large = (int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4));
        int aspect = (int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8));
        int bytesPerTexel = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(12)) >= 8 ? 2 : 1;
        int length = 0;
        for (int lod = large; lod <= small; lod++)
        {
            int dimension = 256 >> lod;
            length += Math.Max(1, dimension >> Math.Max(0, aspect - 3)) *
                Math.Max(1, dimension >> Math.Max(0, 3 - aspect)) * bytesPerTexel;
        }
        byte[] pixels = ReadGuest(BinaryPrimitives.ReadUInt32LittleEndian(ReadGuest(checked(address + 16), 4)), length);
        fixed (byte* data = pixels)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(16), (ulong)data);
            return CallWithBuffer(function, args, 3, info);
        }
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
        if (_texturePixels.Length < bytes) _texturePixels = new byte[bytes];
        try { ReadGuest((uint)args[pointerIndex], _texturePixels.AsSpan(0, bytes)); }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException(
                $"Glide texture upload {args.Length} args, {width}x{rows}, {bytes} bytes, " +
                $"operands=[{string.Join(",", args.ToArray().Select(value => value.ToString("X")))}]",
                error);
        }
        return CallWithBuffer(function, args, pointerIndex, _texturePixels);
    }

    /// <summary>Copies the selected palette or conversion table into native-accessible storage.</summary>
    private ulong DownloadTable(nint function, Span<ulong> args)
    {
        if (args[1] > 2) throw new InvalidDataException("Unsupported Glide texture table.");
        int bytes = args[1] == 2 ? 1024 : 112;
        byte[] table;
        if (args.Length == 5 && args[1] == 2)
        {
            uint first = checked((uint)args[3]), last = checked((uint)args[4]);
            if (first > last || last > 255) throw new InvalidDataException("Invalid partial Glide palette range.");
            table = new byte[bytes];
            ReadGuest(checked((uint)args[2] + first * 4), table.AsSpan((int)first * 4, checked((int)(last - first + 1) * 4)));
        }
        else
        {
            if (args.Length == 5 && (args[3] != 0 || args[4] != 11))
                throw new InvalidDataException("Invalid partial Glide NCC range.");
            table = ReadGuest((uint)args[2], bytes);
        }
        return CallWithBuffer(function, args, 2, table);
    }

    /// <summary>Expands guest texture metadata into the native pointer-sized structure.</summary>
    private ulong TextureSource(nint function, Span<ulong> args)
    {
        return CallWithBuffer(function, args, 3, TextureInfo((uint)args[3]));
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
        if (_lfbPixels.Length < _lfbLength) _lfbPixels = new byte[_lfbLength];
        if ((_lfbLockType & 1) == 0)
        {
            Marshal.Copy(_nativeLfb, _lfbPixels, 0, _lfbLength);
            WriteGuest(_guestLfb, _lfbPixels.AsSpan(0, _lfbLength));
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
            ReadGuest(_guestLfb, _lfbPixels.AsSpan(0, _lfbLength));
            Marshal.Copy(_lfbPixels, 0, _nativeLfb, _lfbLength);
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
        if (opened != 0)
        {
            SetWindowPos(_window, 0, -32000, -32000, 0, 0, 0x415);
            _presentation = MiniGlidePresentation.Open(_window);
        }
        else CloseWindow();
        return opened;
    }

    /// <summary>Owns the MiniGlide presentation window and pumps its native message queue.</summary>
    private void WindowMessageLoop()
    {
        try
        {
            _windowThreadId = GetCurrentThreadId();
            // This surface is a renderer backing store, never a second user-facing window.
            _window = CreateWindowExW(0x08000080, "STATIC", "MiniGlide", 0x00CF0000,
                -32000, -32000, 700, 550, 0, 0, 0, 0);
            if (_window == 0) throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastPInvokeError()}.");
            // Install before WinOpen: provider initialization can itself move/show the HWND.
            _baseWindowProcedure = BaseWindowCallback;
            _defaultWindowProcedure = SetWindowLongPtrW(_window, -4,
                Marshal.GetFunctionPointerForDelegate(_baseWindowProcedure));
            if (_defaultWindowProcedure == 0)
                throw new InvalidOperationException($"Could not protect Glide backing surface: {Marshal.GetLastPInvokeError()}.");
            // The provider needs a renderable HWND; show only after offscreen guards are installed.
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE; never a user-facing/taskbar surface.
            _windowReady.Set();
            while (GetMessageW(out NativeMessage message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        catch (Exception error)
        {
            _windowError = error;
            _windowReady.Set();
        }
        finally
        {
            MiniGlidePresentation.Close(_presentation);
            if (IsWindow(_window))
            {
                if (_previousWindowProcedure != 0) SetWindowLongPtrW(_window, -4, _previousWindowProcedure);
                DestroyWindow(_window);
            }
            _window = 0; _windowProcedure = null; _previousWindowProcedure = 0;
            _baseWindowProcedure = null; _defaultWindowProcedure = 0;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Keeps renderer close requests from destroying a still-open guest presentation target.</summary>
    private nint WindowCallback(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x21) return 3;
        if (message == 0x10) // WM_CLOSE; only grSstWinClose owns this window's lifetime.
        {
            Interlocked.Increment(ref _windowCloseMessages);
            return 0;
        }
        nint result = CallWindowProcW(_previousWindowProcedure, window, message, wParam, lParam);
        ConstrainPresentation(window, message, wParam, lParam);
        return result;
    }

    /// <summary>Retains offscreen, nonactivating behavior even during the provider's first WinOpen.</summary>
    private nint BaseWindowCallback(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x21) return 3;
        if (message == 0x10) { Interlocked.Increment(ref _windowCloseMessages); return 0; }
        nint result = CallWindowProcW(_defaultWindowProcedure, window, message, wParam, lParam);
        ConstrainPresentation(window, message, wParam, lParam);
        return result;
    }

    /// <summary>Constrains only presentation position/extended styles; renderer dimensions and guest input remain untouched.</summary>
    private static void ConstrainPresentation(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x46 && lParam != 0) // WM_WINDOWPOSCHANGING.
        {
            var position = (NativeWindowPosition*)lParam;
            position->X = -32000; position->Y = -32000; position->Flags &= ~2u; // Keep provider size, reject onscreen moves.
        }
        else if (message == 0x47 && lParam != 0) // WM_WINDOWPOSCHANGED, including SWP_NOSENDCHANGING.
        {
            var position = (NativeWindowPosition*)lParam;
            if ((position->Flags & 2) == 0 && (position->X > -1000 || position->Y > -1000))
                // Suppress another CHANGING callback so a provider that rewrites it cannot recurse.
                SetWindowPos(window, 0, -32000, -32000, 0, 0, 0x415);
        }
        else if (message == 0x7C && unchecked((int)wParam) == -20 && lParam != 0) // WM_STYLECHANGING / GWL_EXSTYLE.
        {
            var styles = (uint*)lParam;
            styles[1] = (styles[1] | 0x08000080u) & ~0x00040000u; // NOACTIVATE + TOOLWINDOW, never APPWINDOW.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
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
        MiniGlidePresentation.Close(_presentation); _presentation = null;
        if (_windowThread is null) return;
        if (_windowThreadId != 0) PostThreadMessageW(_windowThreadId, 0x12, 0, 0); // WM_QUIT
        if (!_windowThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Glide host window did not close.");
        _windowThread = null;
        _windowThreadId = 0;
        _window = 0;
        _windowProcedure = null;
        _previousWindowProcedure = 0;
    }

    /// <summary>Invalidates old completion tokens only after their native work has finished at a CPU reset boundary.</summary>
    internal void ResetGeneration() => _swapWorker.Reset();

    /// <summary>Rejects an active external renderer or outstanding swap rather than saving an incomplete machine.</summary>
    internal void RequireSnapshotSupport()
    {
        if (_window != 0 || _swapWorker.HasPending)
            throw new InvalidOperationException("Close the active MiniGlide program before saving a full snapshot; host renderer state cannot be restored.");
    }

    /// <summary>Joins native presents, closes presentation, writes optional call counts and releases the host library once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _swapWorker.Dispose();
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
    /// <summary>Restores backing-only placement without resending a provider's changing-position callback.</summary>
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
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
