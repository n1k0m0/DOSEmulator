// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Independent raw-integer/FILD oracles and scanout/CPU-register separation for the second audit.</summary>
internal static class Audit2PerformanceTests
{
    /// <summary>Requires an exact architectural, pixel or cache-work result.</summary>
    private static void Check(bool value, string message = "Second audit performance contract failed.")
    { if (!value) throw new Exception(message); }
    /// <summary>Uses an independently unbounded integer magnitude and bit length to construct the exact raw format.</summary>
    private static Float80Value Reference(long value)
    {
        BigInteger magnitude = BigInteger.Abs(new BigInteger(value));
        if (magnitude.IsZero) return default;
        int bits = checked((int)magnitude.GetBitLength());
        return new((ulong)(magnitude << (64 - bits)), (ushort)(16383 + bits - 1 | (value < 0 ? 0x8000 : 0)));
    }
    /// <summary>Writes actual indexed register ports, including width/lane handling in the production adapter.</summary>
    private static void Reg(VgaAdapter video, ushort port, int index, int value) => video.Write(port, (uint)(index | value << 8), 2);
    /// <summary>Builds a small planar fixture with physically distinct values in the four real backing planes.</summary>
    private static VgaAdapter Video(bool et, bool graphics)
    {
        var v = new VgaAdapter(et4000: et);
        Reg(v, 0x3c4, 4, 6); Reg(v, 0x3ce, 5, 0); Reg(v, 0x3ce, 6, graphics ? 5 : 4);
        for (int plane = 0; plane < 4; plane++) { Reg(v, 0x3c4, 2, 1 << plane); v.WriteByte(0, (byte)(0x11 << plane)); }
        Reg(v, 0x3c4, 2, 15); return v;
    }
    /// <summary>Compares the cached result with a newly rendered complete surface at the same guest time.</summary>
    private static void Fresh(VgaAdapter video, VgaFrameBuffer buffer)
    {
        var actual = video.Capture(0, buffer); var expected = video.Capture(0);
        Check(actual.Width == expected.Width && actual.Height == expected.Height && actual.ModeGeneration == expected.ModeGeneration &&
            actual.ArgbPixels.AsSpan().SequenceEqual(expected.ArgbPixels), "Cached VGA scanout differs from complete render.");
    }
    /// <summary>Registers mathematical/CPU loads and real VGA read/write/capture effects, without native UI or GL.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Audit2 integer conversion matches every signed16 value and independent Int64 boundaries/randoms", () =>
        {
            for (int value = short.MinValue; value <= short.MaxValue; value++) Check(Float80Math.FromInteger(value) == Reference(value));
            var values = new HashSet<long> { 0, 1, -1, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue };
            for (int bit = 0; bit < 63; bit++)
            {
                long value = 1L << bit;
                foreach (long item in new[] { value, -value, value - 1, value + 1, -value + 1, -value - 1 }) values.Add(item);
            }
            ulong seed = 0x17cc981234ad59fe;
            for (int i = 0; i < 200000; i++) { seed ^= seed << 13; seed ^= seed >> 7; seed ^= seed << 17; values.Add(unchecked((long)seed)); }
            Check(values.Count == 200373);
            foreach (long value in values) Check(Float80Math.FromInteger(value) == Reference(value), "Raw80 integer mismatch.");
        });
        foreach (int width in new[] { 16, 32, 64 })
        foreach (int precision in new[] { 0, 2, 3 })
        foreach (int rounding in new[] { 0, 1, 2, 3 })
            test($"Audit2 exact FILD width={width} PC={precision} RC={rounding}", () =>
            {
                long min = width == 64 ? long.MinValue : -(1L << (width - 1));
                long max = width == 64 ? long.MaxValue : (1L << (width - 1)) - 1;
                foreach (long value in new[] { min, min + 1, -32768, -1, 0, 1, 32767, max - 1, max })
                {
                    byte opcode = width == 32 ? (byte)0xdb : (byte)0xdf;
                    byte modrm = width == 64 ? (byte)0x2d : (byte)0x05;
                    byte[] code = [0xdb,0xe3, 0xd9,0x2d,0,0,4,0, opcode,modrm,0,0,3,0, 0xdb,0x3d,0,1,3,0];
                    var (cpu, memory) = ProtectedModeTests.Rig(code);
                    memory.WriteWord(0x40000, (ushort)(0x007f | precision << 8 | rounding << 10));
                    BinaryPrimitives.WriteInt64LittleEndian(memory.Ram.AsSpan(0x30000, 8), value);
                    memory.Ram.AsSpan(0x300f0, 32).Fill(0xa5);
                    cpu.Eflags = 0x8d7;
                    uint[] registers = Enumerable.Range(0, 8).Select(i => cpu.GetRegister((Register)i)).ToArray();
                    cpu.Step(); cpu.Step(); cpu.Step();
                    Check(cpu.GetFpuRegister(0) == Reference(value) && cpu.FpuStatusWord == 0x3800 && cpu.FpuTop == 7);
                    cpu.Step();
                    var expected = Reference(value);
                    Check(memory.ReadDword(0x30100) == (uint)expected.Significand && memory.ReadDword(0x30104) == (uint)(expected.Significand >> 32) &&
                        memory.ReadWord(0x30108) == expected.SignExponent && cpu.FpuStatusWord == 0 && cpu.FpuTagWord == 0xffff && cpu.FpuTop == 0);
                    Check(memory.Ram.AsSpan(0x300f0, 16).IndexOfAnyExcept((byte)0xa5) < 0 && memory.Ram.AsSpan(0x3010a, 6).IndexOfAnyExcept((byte)0xa5) < 0);
                    Check(cpu.Eflags == 0x8d7 && registers.SequenceEqual(Enumerable.Range(0, 8).Select(i => cpu.GetRegister((Register)i))));
                }
            });
        foreach (bool et in new[] { false, true })
        foreach (bool graphics in new[] { false, true })
        foreach (int index in new[] { 0, 1, 2, 3, 4, 7, 8 })
            test($"Audit2 VGA CPU-only GC preserves actual pixels/reuse et={et} graphics={graphics} index={index}", () =>
            {
                var v = Video(et, graphics); var a = new VgaFrameBuffer(); var b = new VgaFrameBuffer();
                var original = v.Capture(0, a).ArgbPixels.ToArray(); v.Capture(0, b); long renders = b.RenderedFrames;
                Reg(v, 0x3ce, index, index == 8 ? 0x5a : 1);
                Check(v.Read(0x3cf, 1) == (uint)(index == 8 ? 0x5a : 1));
                Fresh(v, b); Check(b.RenderedFrames == renders && a.HasSamePixels(b) && original.AsSpan().SequenceEqual(b.Frame!.ArgbPixels));
            });
        test("Audit2 VGA latched GC controls affect later real VRAM/pixels after an inert register phase", () =>
        {
            var v = Video(false, true); var buffer = new VgaFrameBuffer(); var before = v.Capture(0, buffer).ArgbPixels.ToArray();
            long renders = buffer.RenderedFrames;
            Reg(v, 0x3c4, 2, 1); Fresh(v, buffer); renders = buffer.RenderedFrames;
            Reg(v, 0x3ce, 0, 1); Reg(v, 0x3ce, 1, 1); Reg(v, 0x3ce, 8, 0x80);
            Fresh(v, buffer); Check(buffer.RenderedFrames == renders);
            Reg(v, 0x3ce, 4, 0); Check(v.ReadByte(0) == 0x11); v.WriteByte(0, 0);
            Check(v.ReadByte(0) == 0x91); Fresh(v, buffer);
            Check(buffer.RenderedFrames == renders + 1 && !before.AsSpan().SequenceEqual(buffer.Frame!.ArgbPixels));
        });
        foreach (bool et in new[] { false, true })
            test("Audit2 VGA GC4 selects distinct real CPU planes without pixel invalidation et=" + et, () =>
            {
                var v = Video(et, true); var buffer = new VgaFrameBuffer(); Fresh(v, buffer); long rendered = buffer.RenderedFrames;
                for (int plane = 0; plane < 4; plane++) { Reg(v, 0x3ce, 4, plane); Check(v.ReadByte(0) == (byte)(0x11 << plane)); Fresh(v, buffer); }
                Check(buffer.RenderedFrames == rendered);
                Reg(v, 0x3c4, 2, 1); v.WriteByte(0, 0x80); Fresh(v, buffer); Check(buffer.RenderedFrames == rendered + 1);
                Reg(v, 0x3ce, 4, 0); Check(v.ReadByte(0) == 0x80);
            });
        foreach (bool et in new[] { false, true })
            test("Audit2 VGA data-path bits preserve cache and actual display transitions invalidate et=" + et, () =>
            {
                var v = Video(et, true); var buffer = new VgaFrameBuffer(); Fresh(v, buffer); long count = buffer.RenderedFrames;
                Reg(v, 0x3ce, 5, 0x1b); Fresh(v, buffer); Check(buffer.RenderedFrames == count);
                Reg(v, 0x3ce, 6, 0x0d); Fresh(v, buffer); Check(buffer.RenderedFrames == count);
                Reg(v, 0x3ce, 5, 0x40); Fresh(v, buffer); Check(buffer.RenderedFrames == ++count);
                Reg(v, 0x3ce, 6, 0x0c); Fresh(v, buffer); Check(buffer.RenderedFrames == ++count);
            });
        foreach (bool et in new[] { false, true })
            test("Audit2 VGA protected/identical CRTC writes retain pixels and cache et=" + et, () =>
            {
                var v = Video(et, true); Reg(v, 0x3d4, 0x11, 0x80); var buffer = new VgaFrameBuffer(); Fresh(v, buffer); long count = buffer.RenderedFrames;
                Reg(v, 0x3d4, 1, 79); Reg(v, 0x3d4, 1, 12); Reg(v, 0x3d4, 7, 2); Fresh(v, buffer); Check(buffer.RenderedFrames == count);
                Reg(v, 0x3d4, 0x11, 0); Reg(v, 0x3d4, 1, 39); Fresh(v, buffer); Check(buffer.RenderedFrames == count + 1 && buffer.Frame!.Width == 320);
            });
        foreach (bool et in new[] { false, true })
            test("Audit2 VGA attribute index is inert while blanking and palette writes invalidate et=" + et, () =>
            {
                var v = Video(et, true); var buffer = new VgaFrameBuffer(); Fresh(v, buffer); long count = buffer.RenderedFrames;
                v.Read(0x3da, 1); v.Write(0x3c0, 0x23, 1); Fresh(v, buffer); Check(buffer.RenderedFrames == count);
                v.Write(0x3c0, 0x03, 1); Fresh(v, buffer); Check(buffer.RenderedFrames == count);
                v.Read(0x3da, 1); v.Write(0x3c0, 0x03, 1); Fresh(v, buffer); Check(buffer.RenderedFrames == ++count && buffer.Frame!.ArgbPixels.All(pixel => pixel == 0xff000000));
                v.Read(0x3da, 1); v.Write(0x3c0, 0x23, 1); Fresh(v, buffer); Check(buffer.RenderedFrames == ++count);
                v.Write(0x3c0, 0x04, 1); Fresh(v, buffer); Check(buffer.RenderedFrames == ++count);
            });
    }
}
