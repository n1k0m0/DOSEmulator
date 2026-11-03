// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Whpx;

/// <summary>Actual WHPX pointer translations and native ABI calls into bounded own callbacks, without a renderer, window or endpoint.</summary>
internal static class NativeGlideTransportChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Pair(ulong a, ulong b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Empty();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Triple(ulong a, ulong b, ulong c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Four(ulong a, ulong b, ulong c, ulong d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Five(ulong a, ulong b, ulong c, ulong d, ulong e);

    /// <summary>Rejects any independently observed native ABI, byte, return-value or admission discrepancy.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>Exercises exact metadata extents, packed mip levels, partial palettes and separately addressed vertices through the real transport.</summary>
    public static void Run()
    {
        const uint argumentAddress = 0x2000, infoAddress = 0x3000;
        var memory = new MemoryBus(4 << 20);
        using var native = new WhpxMappedMachine(memory, new(), new(), new());
        native.SetEntry(0, 0x1000);
        var type = typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlidePassThrough", true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(type, Hidden, null,
            [typeof(WhpxMappedMachine).GetField("_partition", Hidden)!.GetValue(native)!, memory, Path.Combine(Environment.SystemDirectory, "kernel32.dll")], null)!;
        var functions = (nint[])type.GetField("_functions", Hidden)!.GetValue(bridge)!;
        var invoke = type.GetMethod("Invoke", Hidden)!;
        var callbacks = new List<Delegate>();
        bool bad = false; int calls = 0, cases = 0;
        byte[] expectedInfo = [], expectedPixels = [];
        ulong expectedMask = 3, expectedFirst = 0, expectedLast = 0;
        // Writes one declared own little-endian guest word.
        void Word(uint at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan((int)at, 4), value);
        // Copies only a declared bounded own native callback input.
        byte[] Bytes(ulong pointer, int length) { byte[] result = new byte[length]; Marshal.Copy((nint)pointer, result, 0, length); return result; }
        // Retains each delegate while installing its actual unmanaged function pointer.
        void Install(int id, Delegate callback) { callbacks.Add(callback); functions[id] = Marshal.GetFunctionPointerForDelegate(callback); }
        // Passes a real guest argument block through the production Invoke entry.
        uint Call(uint id, params uint[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++) Word(argumentAddress + (uint)i * 4, arguments[i]);
            return (uint)invoke.Invoke(bridge, [id, argumentAddress, 0u])!;
        }
        Install(99, new Pair((mask, info) => { calls++; bad |= mask != expectedMask || !Bytes(info, 16).AsSpan().SequenceEqual(expectedInfo) || Marshal.ReadInt64((nint)info, 16) != 0; return 0x89abcdef; }));
        Install(98, new Four((tmu, address, mask, info) => { calls++; bad |= tmu != 1 || address != 0x120 || mask != 3 || !Bytes(info, 16).AsSpan().SequenceEqual(expectedInfo) || Marshal.ReadInt64((nint)info, 16) != 0; return 0; }));
        Install(85, new Four((tmu, address, mask, info) => { calls++; bad |= tmu != 1 || address != 0x120 || mask != expectedMask || !Bytes(info, 16).AsSpan().SequenceEqual(expectedInfo) || !Bytes((ulong)Marshal.ReadInt64((nint)info, 16), expectedPixels.Length).AsSpan().SequenceEqual(expectedPixels); return 0; }));
        Install(89, new Five((tmu, table, pixels, first, last) => { calls++; bad |= tmu != 1 || table != 2 || first != expectedFirst || last != expectedLast || !Bytes(pixels + first * 4, expectedPixels.Length).AsSpan().SequenceEqual(expectedPixels); return 0; }));
        Install(107, new Triple((a, b, c) => { calls++; bad |= !Bytes(a, 60).Concat(Bytes(b, 60)).Concat(Bytes(c, 60)).SequenceEqual(expectedPixels); return 0; }));
        Install(32, new Pair((a, b) => { calls++; bad |= !Bytes(a, 60).Concat(Bytes(b, 60)).SequenceEqual(expectedPixels); return 0; }));

        // An independent explicit host-image size table includes tiny 8:1 and 4:1 levels without TMU padding.
        int[][] texels = [[65536,16384,4096,1024,256,64,16,4,1], [32768,8192,2048,512,128,32,8,2,1],
            [16384,4096,1024,256,64,16,4,2,1], [8192,2048,512,128,32,8,4,2,1]];
        foreach (uint aspect in Enumerable.Range(0, 7).Select(i => (uint)i))
        foreach (uint format in new uint[] { 0, 7, 8, 10, 15 })
        foreach ((uint large, uint small) in new[] { (0u, 8u), (3u, 6u), (7u, 8u), (8u, 8u) })
        {
            expectedInfo = new uint[] { small, large, aspect, format }.SelectMany(BitConverter.GetBytes).ToArray();
            expectedInfo.CopyTo(memory.Ram, (int)infoAddress);
            foreach (uint mask in new uint[] { 1, 2, 3 })
            {
                expectedMask = mask;
                Check(Call(99, mask, infoAddress) == 0x89abcdef && !bad, "Glide texture query ABI differs");
                int length = texels[Math.Abs((int)aspect - 3)].Skip((int)large).Take((int)(small - large + 1)).Sum() * (format < 8 ? 1 : 2);
                expectedPixels = Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();
                uint pixels = (uint)memory.Ram.Length - (uint)length;
                expectedPixels.CopyTo(memory.Ram, (int)pixels); Word(infoAddress + 16, pixels);
                Call(85, 1, 0x120, mask, infoAddress); Check(!bad, "Glide packed mip payload or parity layout differs"); cases++;
            }
        }
        // Neither selection nor memory queries consume the unused data word beyond these final 16 RAM bytes.
        uint edge = (uint)memory.Ram.Length - 16;
        expectedInfo.CopyTo(memory.Ram, (int)edge); expectedMask = 3;
        Check(Call(99, 3, edge) == 0x89abcdef, "Glide metadata query read an unused data pointer");
        Call(98, 1, 0x120, 3, edge); Check(!bad, "Glide metadata selection extent differs"); cases += 2;
        foreach ((uint first, uint last) in new[] { (0u, 0u), (255u, 255u), (17u, 43u), (0u, 255u) })
        {
            expectedFirst = first; expectedLast = last;
            expectedPixels = Enumerable.Range(0, (int)(last - first + 1) * 4).Select(i => (byte)(i * 17 + 5)).ToArray();
            uint pointer = (uint)memory.Ram.Length - (last + 1) * 4;
            expectedPixels.CopyTo(memory.Ram, (int)(pointer + first * 4));
            Call(89, 1, 2, pointer, first, last); Check(!bad, "Glide partial palette index or data differs"); cases++;
        }
        foreach (uint id in new uint[] { 32, 107 })
        {
            int count = id == 32 ? 2 : 3;
            expectedPixels = Enumerable.Range(0, count * 60).Select(i => (byte)(i * 3 + 1)).ToArray();
            for (int i = 0; i < count; i++) expectedPixels.AsSpan(i * 60, 60).CopyTo(memory.Ram.AsSpan(0x5000 + i * 0x1000, 60));
            Call(id, Enumerable.Range(0, count).Select(i => (uint)(0x5000 + i * 0x1000)).ToArray());
            Check(!bad, "Glide separately addressed vertex order or full 60-byte record differs"); cases++;
            // The independent batch record owns these exact bytes even after all three caller records change.
            var record = new List<byte>(); record.AddRange(BitConverter.GetBytes(id)); record.AddRange(BitConverter.GetBytes((uint)count)); record.AddRange(expectedPixels);
            record.ToArray().CopyTo(memory.Ram, 0x10000);
            memory.Ram.AsSpan(0x5000, 0x3000).Fill(0xcd);
            invoke.Invoke(bridge, [0xfffeu, 0x10000u, (uint)record.Count / 4]);
            Check(!bad, "Glide batched vertex copy or native clipping call differs"); cases++;
            Word(0x10004, (uint)count + 1);
            bool rejected = false;
            try { invoke.Invoke(bridge, [0xfffeu, 0x10000u, (uint)record.Count / 4]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
            Check(rejected, "Glide malformed pointer batch reached the provider"); cases++;
        }
        // Invalid metadata, ranges and missing RAM must fail before any native provider call.
        foreach (Action invalid in new Action[] { () => Call(89, 1, 2, 0x4000, 7, 6), () => Call(89, 1, 2, 0x4000, 0, 256),
            () => Call(89, 1, 0, 0x4000, 1, 11), () => Call(99, 3, (uint)memory.Ram.Length - 8),
            () => { Word(infoAddress, 9); Call(99, 3, infoAddress); } })
        {
            int before = calls; bool rejected = false;
            try { invalid(); } catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
            Check(rejected && calls == before, "Invalid Glide input reached the native callback"); cases++;
        }
        // A completed own worker needs no HWND or native message. Shutdown must retire
        // its presentation even when the caller omits an explicit grSstWinClose.
        var retired = new Thread(() => { }); retired.Start(); retired.Join();
        type.GetField("_windowThread", Hidden)!.SetValue(bridge, retired);
        type.GetField("_window", Hidden)!.SetValue(bridge, (nint)123);
        var procedureType = type.GetNestedType("WindowProcedure", BindingFlags.NonPublic)!;
        var procedure = type.GetMethod("WindowCallback", Hidden)!.CreateDelegate(procedureType, bridge);
        type.GetField("_windowProcedure", Hidden)!.SetValue(bridge, procedure);
        type.GetField("_previousWindowProcedure", Hidden)!.SetValue(bridge, (nint)456);
        Install(49, new Empty(() => 0)); Call(49);
        Check(type.GetField("_windowThread", Hidden)!.GetValue(bridge) is null &&
            (nint)type.GetField("_window", Hidden)!.GetValue(bridge)! == 0 &&
            type.GetField("_windowProcedure", Hidden)!.GetValue(bridge) is null &&
            (nint)type.GetField("_previousWindowProcedure", Hidden)!.GetValue(bridge)! == 0,
            "Glide shutdown retained stale presentation or callback over the guest desktop"); cases++;
        GC.KeepAlive(callbacks);
        Console.WriteLine($"PASS: Glide transport {cases} actual WHPX/native ABI controls; packed mips, three parity masks, exact RAM edge, palette slices, distinct vertices and pre-call rejection; no renderer or host endpoint.");
    }
}
