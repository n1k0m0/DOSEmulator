// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class Et4000Tests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool ok, string message = "ET4000 assertion failed") { if (!ok) throw new Exception(message); }
    /// <summary>Writes an actual indexed VGA register for the controlled fixture setup.</summary>
    private static void Reg(VgaAdapter v, ushort p, int i, int x) => v.Write(p, (uint)(i | x << 8), 2);
    /// <summary>Reads an actual indexed device register for the surrounding independent assertions.</summary>
    private static byte Get(VgaAdapter v, ushort p, int i) { v.Write(p, (uint)i, 1); return (byte)v.Read((ushort)(p + 1), 1); }
    /// <summary>Performs the actual ET4000 key-port sequence through the selected mono/color decoder.</summary>
    private static void Unlock(VgaAdapter v, bool mono = false) { v.Write(0x3bf, 3, 1); v.Write(mono ? (ushort)0x3b8 : (ushort)0x3d8, 0xa0, 1); }
    /// <summary>Initializes the surrounding test or benchmark fixture before measured execution begins.</summary>
    private static VgaAdapter Setup(bool chain = true)
    {
        var v = new VgaAdapter(et4000: true); Unlock(v);
        Reg(v, 0x3c4, 4, chain ? 14 : 6); Reg(v, 0x3c4, 2, 15);
        Reg(v, 0x3ce, 5, chain ? 0x40 : 0); Reg(v, 0x3ce, 6, 5); return v;
    }
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot()
    {
        var m = PcMachine.CreateDemo(new MachineProfile { VideoCard = "et4000ax-isa-1m-development" });
        m.RunSteps(200000); Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST"); return m;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(2000000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "BIOS incomplete");
    }
    /// <summary>Registers the named et4000 regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("ET4000 is explicit and the default VGA keeps its old memory and port contract", () =>
        {
            new MachineProfile { VideoCard = "et4000ax-isa-1m-development" }.Validate();
            try { new MachineProfile { VideoCard = "et4000-w32" }.Validate(); throw new Exception("profile accepted"); } catch (ArgumentException) { }
            var v = new VgaAdapter(); Check(!v.IsEt4000 && v.VideoMemoryBytes == 262144);
            Unlock(v); v.Write(0x3cd, 0xab, 1); Check(v.Read(0x3cd, 1) == 255);
            Reg(v, 0x3d4, 0x33, 0x12); Check(Get(v, 0x3d4, 0x13) == 0x12);
            var e = Setup(); Check(e.IsEt4000 && e.VideoMemoryBytes == 1048576);
        });
        test("ET4000 bank access needs the actual key after power-up and synchronous reset", () =>
        {
            var v = new VgaAdapter(et4000: true); v.Write(0x3cd, 0x9b, 1);
            Check(v.Read(0x3cd, 1) == 255 && v.BankSwitchWrites == 0);
            v.Write(0x3d8, 0xa0, 1); Check(!v.ExtendedRegistersUnlocked); v.Write(0x3bf, 3, 1);
            Check(v.ExtendedRegistersUnlocked); v.Write(0x3cd, 0x9b, 1); Check(v.Read(0x3cd, 1) == 0x9b);
            v.Write(0x3d8, 0, 1); Check(!v.ExtendedRegistersUnlocked && v.SegmentRegisterEnabled);
            v.Write(0x3cd, 0x65, 1); Check(v.Read(0x3cd, 1) == 0x65);
            Reg(v, 0x3c4, 0, 1); Check(!v.SegmentRegisterEnabled);
            v.Write(0x3cd, 0, 1); Reg(v, 0x3c4, 0, 3); Check(v.Read(0x3cd, 1) == 255);
            Unlock(v); Check(v.Read(0x3cd, 1) == 0x65 && v.BankSwitchWrites == 2);
        });
        test("ET4000 key and extended CRTC obey the selected mono or color decoder", () =>
        {
            var v = new VgaAdapter(et4000: true); v.Write(0x3c2, 0x66, 1); Unlock(v);
            Check(!v.ExtendedRegistersUnlocked); Unlock(v, true); Check(v.ExtendedRegistersUnlocked);
            Reg(v, 0x3d4, 0x36, 0x88); Check(Get(v, 0x3d4, 0x36) == 255);
            Reg(v, 0x3b4, 0x36, 0xc0); Check(Get(v, 0x3b4, 0x36) == 0xc0);
            Check(v.Read(0x3bf, 1) == 255 && (v.Read(0x3b8, 1) & 0x40) != 0);
        });
        test("ET4000 inactive mode-control writes cannot revive an old key after synchronous reset", () =>
        {
            var v = Setup(); Reg(v, 0x3c4, 0, 1); v.Write(0x3c2, 0x66, 1);
            v.Write(0x3d8, 0xa0, 1); Check(!v.ExtendedRegistersUnlocked && !v.SegmentRegisterEnabled);
            v.Write(0x3b8, 0xa0, 1); Check(v.ExtendedRegistersUnlocked && v.SegmentRegisterEnabled);
        });
        foreach (int index in new[] { 0x32, 0x34, 0x36, 0x37 })
            test($"ET4000 protected CRTC {index:X2} does not alias VGA and needs an unlocked key", () =>
            {
                var v = new VgaAdapter(et4000: true); byte before = Get(v, 0x3d4, index);
                Reg(v, 0x3d4, index, 0x69); Check(Get(v, 0x3d4, index) == before);
                Unlock(v); Reg(v, 0x3d4, index, 0x69); Check(Get(v, 0x3d4, index) == 0x69);
                Reg(v, 0x3d4, 0x29, 0xff); Check(Get(v, 0x3d4, 0x29) == 255);
            });
        test("ET4000 overflow protection and unprotected start-address register are distinct", () =>
        {
            var v = new VgaAdapter(et4000: true); Reg(v, 0x3d4, 0x33, 3); Check(Get(v, 0x3d4, 0x33) == 3);
            Reg(v, 0x3d4, 0x35, 0x6f); Reg(v, 0x3d4, 0x11, 0x80);
            Reg(v, 0x3d4, 0x35, 0x90); Check(Get(v, 0x3d4, 0x35) == 0xff);
            Reg(v, 0x3d4, 0x11, 0); Reg(v, 0x3d4, 0x35, 0); Check(Get(v, 0x3d4, 0x35) == 0);
        });
        for (int b = 0; b < 16; b++)
        {
            int bank = b;
            test($"ET4000 chain4 bank {bank} has independent read/write nibbles and boundary bytes", () =>
            {
                var v = Setup();
                for (int n = 0; n < 16; n++) { v.Write(0x3cd, (uint)n, 1); foreach (uint at in new uint[] { 0, 1, 2, 3, 65532, 65533, 65534, 65535 }) v.WriteByte(at, (byte)(n * 11 + at % 4 + 1)); }
                v.Write(0x3cd, (uint)(bank << 4 | (bank ^ 15)), 1);
                foreach (uint at in new uint[] { 0, 1, 2, 3, 65532, 65533, 65534, 65535 }) Check(v.ReadByte(at) == (byte)(bank * 11 + at % 4 + 1));
                v.WriteByte(123, 0xe7); Check(v.ReadByte(123) == 0);
                v.Write(0x3cd, (uint)((bank ^ 15) << 4), 1); Check(v.ReadByte(123) == 0xe7);
            });
            test($"ET4000 chain4 bank {bank} still enforces every plane mask", () =>
            {
                var v = Setup(); v.Write(0x3cd, (uint)(bank * 17), 1);
                for (int p = 0; p < 4; p++)
                {
                    Reg(v, 0x3c4, 2, 1 << p);
                    for (uint at = 0; at < 4; at++) v.WriteByte((uint)(4 * p) + at, 0x5a);
                    for (uint at = 0; at < 4; at++) Check(v.ReadByte((uint)(4 * p) + at) == (at == p ? 0x5a : 0));
                }
            });
        }
        for (int b = 0; b < 4; b++)
        {
            int bank = b;
            test($"ET4000 planar group {bank} covers 64 KiB in each of four independent planes", () =>
            {
                var v = Setup(false);
                for (int n = 0; n < 4; n++)
                    for (int p = 0; p < 4; p++) { v.Write(0x3cd, (uint)n, 1); Reg(v, 0x3c4, 2, 1 << p); v.WriteByte(0, (byte)(n * 4 + p + 1)); v.WriteByte(65535, (byte)(n * 4 + p + 64)); }
                v.Write(0x3cd, (uint)(bank << 4), 1);
                for (int p = 0; p < 4; p++) { Reg(v, 0x3ce, 4, p); Check(v.ReadByte(0) == bank * 4 + p + 1 && v.ReadByte(65535) == bank * 4 + p + 64); }
                v.Write(0x3cd, (uint)((bank + 4) << 4), 1); Check(v.ReadByte(0) == bank * 4 + 4);
            });
        }
        test("ET4000 read latches from one bank drive write-mode-one in a different bank", () =>
        {
            var v = Setup(false); for (int p = 0; p < 4; p++) { Reg(v, 0x3c4, 2, 1 << p); v.WriteByte(1, (byte)(0x81 + p)); }
            v.ReadByte(1); v.Write(0x3cd, 2, 1); Reg(v, 0x3c4, 2, 15); Reg(v, 0x3ce, 5, 1); v.WriteByte(2, 0);
            v.Write(0x3cd, 0x20, 1); for (int p = 0; p < 4; p++) { Reg(v, 0x3ce, 4, p); Check(v.ReadByte(2) == 0x81 + p); }
        });
        for (int b = 0; b < 8; b++)
        {
            int bank = b;
            test($"ET4000 odd-even group {bank} shares the physical chain4 storage", () =>
            {
                var v = Setup(false); Reg(v, 0x3c4, 4, 2); Reg(v, 0x3ce, 5, 0x10);
                v.Write(0x3cd, (uint)(bank * 17), 1); v.WriteByte(0, 0x41); v.WriteByte(1, 0x52);
                Reg(v, 0x3c4, 4, 14); Reg(v, 0x3ce, 5, 0x40);
                v.Write(0x3cd, (uint)(bank * 2) << 4, 1);
                Check(v.ReadByte(0) == 0x41 && v.ReadByte(1) == 0x52 && v.ReadByte(2) == 0x41 && v.ReadByte(3) == 0x52);
            });
        }
        test("ET4000 vertical overflow bit and indexed pixel width respond to actual CRTC and attribute registers", () =>
        {
            var v = Setup(); Reg(v, 0x3d4, 0x11, 0); Reg(v, 0x3d4, 0x12, 0); Reg(v, 0x3d4, 7, 0); Reg(v, 0x3d4, 9, 0);
            Check(v.Height == 1); Reg(v, 0x3d4, 0x35, 4); Check(v.Height == 1025);
            v.Read(0x3da, 1); v.Write(0x3c0, 0x10, 1); v.Write(0x3c0, 1, 1); v.Write(0x3c0, 0x20, 1); Check(v.Width == 640);
            v.Read(0x3da, 1); v.Write(0x3c0, 0x10, 1); v.Write(0x3c0, 0x41, 1); v.Write(0x3c0, 0x20, 1); Check(v.Width == 320);
        });
        test("ET4000 ISA linear aperture is not silently redirected into banked memory or DRAM", () =>
        {
            var v = Setup(); v.WriteByte(0, 9); Reg(v, 0x3d4, 0x36, 0x50);
            Check(v.ReadByte(0) == 255); v.WriteByte(0, 88); Reg(v, 0x3d4, 0x36, 0x40); Check(v.ReadByte(0) == 9);
            using var m = Boot(); m.Memory.Ram[0xb0000] = 0x55; Bios(m, 0x2e);
            m.Memory.WriteDword(0xafffe, 0x11223344); Check(m.Memory.ReadDword(0xafffe) == 0xffff3344 && m.Memory.Ram[0xb0000] == 0x55);
        });
        foreach (var mode in new[] { (Code: 0x2e, Width: 640, Height: 480), (Code: 0x30, Width: 800, Height: 600), (Code: 0x38, Width: 1024, Height: 768) })
        {
            test($"ET4000 BIOS {mode.Code:X2} has actual SVGA dimensions, banked last pixel and immutable frames", () =>
            {
                using var m = Boot(); Bios(m, (ushort)mode.Code); var f = m.CaptureFrame();
                Check(f.Width == mode.Width && f.Height == mode.Height && m.Video.Indexed256);
                Check(m.Memory.ReadByte(0x449) == mode.Code && m.Memory.ReadWord(0x44a) == mode.Width / 8);
                Check(m.Memory.ReadDword(0x43 * 4) == 0xf000c000, "SVGA point-height and INT43 font pointer must agree");
                m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Bx, 0);
                m.Memory.WriteDword(0x3000, 0x12345678); Bios(m, 0x1b00);
                Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0 && m.Memory.ReadDword(0x3000) == 0x12345678, "Unsupported IBM state format must not fabricate SVGA fields");
                int last = mode.Width * mode.Height - 1; m.Ports.Write(0x3cd, (uint)(last >> 16) * 17, 1);
                m.Memory.WriteByte((uint)(0xa0000 + (last & 65535)), 15);
                var g = m.CaptureFrame(); Check(g.ArgbPixels[^1] == 0xffffffff && f.ArgbPixels[^1] == 0xff000000);
                Bios(m, (ushort)(mode.Code | 0x80)); m.Ports.Write(0x3cd, (uint)(last >> 16) * 17, 1);
                Check(m.Memory.ReadByte((uint)(0xa0000 + (last & 65535))) == 15);
                Bios(m, (ushort)mode.Code); m.Ports.Write(0x3cd, (uint)(last >> 16) * 17, 1); Check(m.Memory.ReadByte((uint)(0xa0000 + (last & 65535))) == 0);
                Bios(m, 3); Check(m.Video.Width == 640 && m.Video.Height == 400 && m.Video.SegmentSelect == 0);
                Bios(m, 0x13); Check(m.Video.Width == 320 && m.Video.Height == 200);
                Bios(m, 0x12); Check(m.Video.Width == 640 && m.Video.Height == 480);
            });
        }
        test("ET4000 high CRTC start changes scanout independently of CPU bank selection and invalidates surfaces", () =>
        {
            using var m = Boot(); Bios(m, 0x2e); var surface = new VgaFrameBuffer(); var before = m.Video.Capture(0, surface); long renders = surface.RenderedFrames;
            m.Ports.Write(0x3cd, 4, 1); m.Memory.WriteByte(0xa0000, 15); Reg(m.Video, 0x3d4, 0x33, 1);
            var f = m.Video.Capture(0, surface); Check(f.ArgbPixels[0] == 0xffffffff && surface.RenderedFrames == renders + 1);
            m.Ports.Write(0x3cd, 0x99, 1); m.Video.Capture(0, surface); Check(surface.RenderedFrames == renders + 1);
            Reg(m.Video, 0x3d4, 0x33, 0); Check(m.Video.Capture(0).ArgbPixels[0] == 0xff000000);
        });
    }
}
