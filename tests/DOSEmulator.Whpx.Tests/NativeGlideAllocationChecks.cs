// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Whpx;

/// <summary>Full native LFB payload and batch controls bound hot-path allocations without a renderer, window or host endpoint.</summary>
internal static class NativeGlideAllocationChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Empty();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Pair(ulong a, ulong b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Triple(ulong a, ulong b, ulong c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Six(ulong a, ulong b, ulong c, ulong d, ulong e, ulong f);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Ten(ulong a, ulong b, ulong c, ulong d, ulong e, ulong f, ulong g, ulong h, ulong i, ulong j);
    /// <summary>Rejects a copied-byte, ABI or bounded-allocation discrepancy.</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>Binds own unmanaged callbacks to the actual transport and real native guest-address translation.</summary>
    private sealed class Harness : IDisposable
    {
        internal readonly MemoryBus Memory = new(8 << 20);
        private readonly WhpxMappedMachine _native;
        private readonly IDisposable _bridge;
        private readonly MethodInfo _invoke;
        private readonly nint[] _functions;
        private readonly List<Delegate> _callbacks = [];
        /// <summary>Creates only a WHPX CPU and a reference to the already installed kernel module, never a graphics provider.</summary>
        internal Harness()
        {
            _native = new(Memory, new(), new(), new()); _native.SetEntry(0, 0x1000);
            var type = typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlidePassThrough", true)!;
            _bridge = (IDisposable)Activator.CreateInstance(type, Hidden, null,
                [typeof(WhpxMappedMachine).GetField("_partition", Hidden)!.GetValue(_native)!, Memory, Path.Combine(Environment.SystemDirectory, "kernel32.dll")], null)!;
            _invoke = type.GetMethod("Invoke", Hidden)!;
            _functions = (nint[])type.GetField("_functions", Hidden)!.GetValue(_bridge)!;
        }
        /// <summary>Retains one own native callback for the whole actual transport lifetime.</summary>
        internal void Install(int id, Delegate callback) { _callbacks.Add(callback); _functions[id] = Marshal.GetFunctionPointerForDelegate(callback); }
        /// <summary>Dispatches one real supported ABI argument block from own guest RAM.</summary>
        internal uint Call(uint id, params uint[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan(0x2000 + i * 4, 4), arguments[i]);
            return (uint)_invoke.Invoke(_bridge, [id, 0x2000u, 0x20000u])!;
        }
        /// <summary>Dispatches a checked own batch of exactly the supplied word count.</summary>
        internal void Batch(uint words) => _invoke.Invoke(_bridge, [0xfffeu, 0x10000u, words]);
        /// <summary>Retires callback references only after the transport and native CPU have stopped.</summary>
        public void Dispose() { _bridge.Dispose(); _native.Dispose(); GC.KeepAlive(_callbacks); }
    }

    /// <summary>Checks full large and smaller LFB reads/writes and limits allocations after the initial scratch allocation.</summary>
    private static void Lfb()
    {
        using var h = new Harness(); const int maximum = 640 * 480 * 2;
        nint nativePixels = Marshal.AllocHGlobal(maximum);
        byte[] expected = new byte[maximum], observed = new byte[maximum];
        int height = 480, stride = 1280, calls = 0;
        h.Install(71, new Empty(() => (ulong)height));
        h.Install(53, new Six((a, b, c, d, e, info) =>
        {
            Marshal.WriteInt32((nint)info, 32); Marshal.WriteInt64((nint)info, 8, nativePixels.ToInt64());
            Marshal.WriteInt32((nint)info, 16, stride); Marshal.WriteInt32((nint)info, 20, 0); Marshal.WriteInt32((nint)info, 24, 0); calls++; return 1;
        }));
        h.Install(55, new Pair((a, b) => 1));
        try
        {
            Marshal.Copy(expected, 0, nativePixels, maximum);
            h.Call(53, 0, 1, 0, 0, 0, 0x3000); h.Call(55, 0, 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++)
            {
                height = i % 3 == 0 ? 1 : 480; stride = i % 3 == 0 ? 64 : 1280; int length = height * stride;
                expected.AsSpan(0, length).Fill((byte)(i * 7 + 1)); Marshal.Copy(expected, 0, nativePixels, length);
                h.Memory.Ram.AsSpan(0x20000, length).Fill(0xcc);
                h.Call(53, 0, 1, 0, 0, 0, 0x3000);
                Check(h.Memory.Ram.AsSpan(0x20000, length).SequenceEqual(expected.AsSpan(0, length)), "Glide LFB full read payload differs");
                Check(BitConverter.ToUInt32(h.Memory.Ram, 0x3000) == 20 && BitConverter.ToUInt32(h.Memory.Ram, 0x3004) == 0x20000 &&
                    BitConverter.ToUInt32(h.Memory.Ram, 0x3008) == stride, "Glide guest LFB record differs");
                h.Call(55, 0, 1);
                h.Call(53, 1, 1, 0, 0, 0, 0x3000);
                expected.AsSpan(0, length).Fill((byte)(i * 11 + 3)); expected.AsSpan(0, length).CopyTo(h.Memory.Ram.AsSpan(0x20000, length));
                h.Call(55, 1, 1); Marshal.Copy(nativePixels, observed, 0, length);
                Check(observed.AsSpan(0, length).SequenceEqual(expected.AsSpan(0, length)), "Glide LFB full write payload or resized extent differs");
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(allocated < 512 * 1024 && calls == 65, $"Glide LFB hot path allocated {allocated} bytes for repeated large payloads");
            Console.WriteLine($"PASS: Glide LFB 64 complete large/small read/write payloads and guest records; {allocated} hot-path allocated bytes, no renderer.");
        }
        finally { Marshal.FreeHGlobal(nativePixels); }
    }

    /// <summary>Checks whole maximum-size repeated batches without allocating another transfer array for each exit.</summary>
    private static void Batch()
    {
        using var h = new Harness(); int draws = 0;
        h.Install(107, new Triple((a, b, c) => { draws++; return 0; }));
        for (int i = 0; i < 87; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.Memory.Ram.AsSpan(0x10000 + i * 188, 4), 107);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Memory.Ram.AsSpan(0x10004 + i * 188, 4), 3);
        }
        h.Batch(4089); long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) h.Batch(4089);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(draws == 87087 && allocated < 512 * 1024, $"Glide batch hot path allocated {allocated} bytes or lost triangle records");
        Console.WriteLine($"PASS: Glide 1000 maximum-size repeated batches, 87087 native callback draws; {allocated} hot-path allocated bytes, no renderer.");
    }

    /// <summary>Checks exact large/small partial level bytes and original row metadata without per-upload pixel-array allocation.</summary>
    private static void Texture()
    {
        using var h = new Harness(); const int maximum = 256 * 256 * 2, source = 0x100000;
        byte[] expected = new byte[maximum], observed = new byte[maximum];
        int length = maximum, first = 0, last = 255, calls = 0; bool bad = false;
        h.Install(87, new Ten((tmu, address, lod, large, aspect, format, mask, pixels, row0, row1) =>
        {
            calls++; Marshal.Copy((nint)pixels, observed, 0, length);
            bad |= tmu != 1 || address != 0x12340 || lod != 0 || large != 0 || aspect != 3 || format != 15 || mask != 3 ||
                row0 != (uint)first || row1 != (uint)last || !observed.AsSpan(0, length).SequenceEqual(expected.AsSpan(0, length));
            return 1;
        }));
        h.Memory.Ram.AsSpan(source, maximum).CopyTo(expected);
        h.Call(87, 1, 0x12340, 0, 0, 3, 15, 3, source, 0, 255);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++)
        {
            int rows = i % 3 == 0 ? 256 : i % 3 == 1 ? 1 : 7; first = rows == 256 ? 0 : 3; last = first + rows - 1; length = rows * 512;
            expected.AsSpan(0, length).Fill((byte)(i * 13 + 1)); expected.AsSpan(0, length).CopyTo(h.Memory.Ram.AsSpan(source, length));
            h.Memory.Ram.AsSpan(source - 16, 16).Fill(0xcd); h.Memory.Ram.AsSpan(source + length, 16).Fill(0xce);
            Check(h.Call(87, 1, 0x12340, 0, 0, 3, 15, 3, source, (uint)first, (uint)last) == 1 && !bad,
                "Glide reused texture staging changed complete bytes, partial rows or native return");
            Check(h.Memory.Ram.AsSpan(source - 16, 16).ToArray().All(x => x == 0xcd) &&
                h.Memory.Ram.AsSpan(source + length, 16).ToArray().All(x => x == 0xce), "Glide texture input staging changed guest guards");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(calls == 513 && allocated < 512 * 1024, $"Glide texture level hot path allocated {allocated} bytes or lost actual uploads");
        Console.WriteLine($"PASS: Glide texture 512 complete large/small partial level uploads, exact bytes/metadata/returns and guest guards; {allocated} hot-path allocated bytes, no renderer.");
    }

    /// <summary>Runs the independent allocation oracles, or one named control for a frozen old-module negative comparison.</summary>
    public static void Run(string focus = "all")
    {
        if (focus is not ("all" or "lfb" or "batch" or "texture")) throw new ArgumentException("Unknown Glide allocation control");
        if (focus is "all" or "lfb") Lfb();
        if (focus is "all" or "batch") Batch();
        if (focus is "all" or "texture") Texture();
    }
}
