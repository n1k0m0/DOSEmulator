// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual fifth-board POST, CPU PCI/VGA OUTs, DOS BIOS legacy transitions and physical reset retention.</summary>
internal static class S3Trio64PcTests
{
    /// <summary>Selects only the fixed own S3 board; older profiles retain their existing ROM and adapter.</summary>
    private static MachineProfile Profile() => new() { Id = MachineProfile.S3Trio64ProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId };
    /// <summary>Requires an actual byte, instruction, firmware text or peer retention result.</summary>
    private static void Check(bool value, string reason = "S3 full PC invariant") { if (!value) throw new Exception(reason); }
    /// <summary>Accepts only a deliberately invalid fixed profile or capture topology.</summary>
    private static void Invalid(Action action) { bool invalid = false; try { action(); } catch (ArgumentException) { invalid = true; } catch (InvalidOperationException) { invalid = true; } Check(invalid); }
    /// <summary>Executes one actual real-mode opcode in own RAM through the full owner and clock.</summary>
    private static void One(PcMachine m, byte[] code)
    {
        Check(!m.Cpu.Halted, "The own CPU instruction fixture must be active"); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; code.CopyTo(m.Memory.Ram, 0x1000);
        Check(m.RunSteps(1) == 1 && m.Fault is null);
    }
    /// <summary>Performs one real OUT instruction with the selected architectural operand width.</summary>
    private static void Out(PcMachine m, ushort port, uint value, int width = 1)
    { m.Cpu.SetRegister(Register.Dx, port); m.Cpu.SetRegister(Register.Ax, value); One(m, width == 1 ? [0xee] : width == 2 ? [0xef] : [0x66, 0xef]); }
    /// <summary>Performs one real IN and returns only its selected operand bits.</summary>
    private static uint In(PcMachine m, ushort port, int width = 1)
    { m.Cpu.SetRegister(Register.Dx, port); One(m, width == 1 ? [0xec] : width == 2 ? [0xed] : [0x66, 0xed]); return m.Cpu.GetRegister(Register.Ax) & (width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue); }
    /// <summary>Writes one S3 configuration lane through actual CPU CF8/CFC OUTs.</summary>
    private static void Config(PcMachine m, int offset, uint value, int width = 4)
    { Out(m, 0xcf8, 0x80001800u | (uint)(offset & ~3), 4); Out(m, (ushort)(0xcfc + (offset & 3)), value, width); }
    /// <summary>Writes a genuine word index/data CRTC transfer.</summary>
    private static void Crtc(PcMachine m, byte index, byte value) => Out(m, 0x3d4, (uint)(index | value << 8), 2);
    /// <summary>Boots the own development sector through actual ROM setup and POST, never a DOS installation.</summary>
    private static PcMachine Boot()
    { var m = PcMachine.CreateDemo(Profile()); m.RunSteps(1000000); Check(m.Cpu.Halted && m.Fault is null && m.Memory.ReadWord(0x500) == 0xbeef, m.Fault ?? "Own S3 POST/demo did not complete"); return m; }
    /// <summary>Invokes actual ROM INT10 with a sentinel IRET/HLT and the guest's current caller registers.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4); m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(200000);
        Check(m.Cpu.Halted && m.Cpu.Eip == 0x1001 && m.Fault is null, m.Fault ?? "Actual INT10 did not return to own sentinel");
    }
    /// <summary>Obtains only one explicitly named compiled raw ROM variant for frozen byte identity.</summary>
    private static byte[] Rom(string name)
    {
        var field = typeof(DOSEBios).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Check(field?.GetValue(null) is string); using var encoded = new MemoryStream(Convert.FromBase64String((string)field!.GetValue(null)!)); using var gzip = new GZipStream(encoded, CompressionMode.Decompress); using var result = new MemoryStream(); gzip.CopyTo(result); return result.ToArray();
    }
    /// <summary>Captures every installed VRAM byte regardless of active decoder or bank.</summary>
    private static string Vram(PcMachine m) => m.Video.SaveState().Fields["_planes"].GetRawText();
    /// <summary>Creates actual enhanced geometry and high linear addressing that a later ROM call must leave safely.</summary>
    private static void Enhanced(PcMachine m)
    {
        if (m.Cpu.Halted) m.Cpu.Reset();
        Crtc(m, 0x38, 0x48); Crtc(m, 0x39, 0xa5); Crtc(m, 0x40, 1); Crtc(m, 0x31, 0x38); Crtc(m, 0x3a, 0x10); Crtc(m, 0x58, 0x12);
        Crtc(m, 0x51, 0x3f); Crtc(m, 0x69, 5); Crtc(m, 0x5d, 2); Crtc(m, 0x5e, 2); Crtc(m, 0x43, 0x80);
        Out(m, 0x3c4, 0x0608, 2); Out(m, 0x3c4, 0x800a, 2); Out(m, 0x3c4, 0x8015, 2); Out(m, 0x3c4, 0x0e04, 2); Out(m, 0x3ce, 0x0005, 2); Out(m, 0x4ae8, 1, 2);
        Check(m.Video.GraphicsMode && m.Video.Indexed256 && m.S3!.LinearEnabled);
    }
    /// <summary>Seeds distinguishable real caller registers without changing the valid private stack.</summary>
    private static uint[] Caller(PcMachine m, ushort ax)
    {
        for (int i = 0; i < 8; i++) if (i != (int)Register.Sp) m.Cpu.SetRegister((Register)i, 0x10203040u + (uint)i * 0x01010101);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.Eflags = 0xcd7;
        return Enumerable.Range(0, 8).Select(i => m.Cpu.GetRegister((Register)i)).ToArray();
    }
    /// <summary>Requires exact full-width caller registers and the real ROM return point after a mode operation.</summary>
    private static void Returned(PcMachine m, uint[] expected)
    {
        Check(m.Cpu.Halted && m.Cpu.Eip == 0x1001 && m.Cpu.Eflags == 0xcd7 && Enumerable.Range(0, 8).All(i => m.Cpu.GetRegister((Register)i) == expected[i]), "ROM mode call damaged its caller register/flags image");
    }
    /// <summary>Registers actual fixed factory, old firmware, guest BIOS and distinct INIT/CPURST behavior.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 S3 factory binds one shared two MiB video four BDFs and fixed high memory decoder", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Check(m.Profile.IsTrio64 && m.Profile.IsS3Trio64Board && m.Profile.IsPiixReset && m.Pci!.FunctionCount == 4);
            Check(m.S3!.HasBindings(m.Memory, m.Video) && m.Video.IsTrio64 && !m.Video.IsEt4000 && m.Video.VideoMemoryBytes == 0x200000);
            Check(m.Pci!.HasFunction(0, 3, 0, m.S3!) && m.Memory.DynamicDecoderCount == 1 && m.Memory.HasOnlyDynamicDecoder(m.S3!));
            Check(m.Cpu.TimestampFrequency == 100_000_000 && m.Memory.Ram.Length == 32 * 1048576 && !m.S3!.IoSpaceEnabled && !m.S3!.MemorySpaceEnabled); m.CaptureHardwareState();
        });
        foreach (int change in new[] { 0, 1, 2, 3, 4, 5 })
            test("M4 S3 fixed profile rejects incomplete or mismatched resource tuple " + change, () =>
            {
                var profile = Profile(); profile = change switch { 0 => profile with { Cpu = "i486dx-development" }, 1 => profile with { CpuHz = 90_000_000 }, 2 => profile with { RamMiB = 16 }, 3 => profile with { VideoCard = DOSEIdentity.VgaModel }, 4 => profile with { Id = MachineProfile.PiixResetProfileId }, _ => profile with { VideoCard = "et4000ax-isa-1m-development" } };
                Invalid(() => { using var rejected = PcMachine.CreateDemo(profile); });
            });
        foreach ((string field, string expected) in new[]
        {
            ("CompressedImage", "353CFA9B957CF6433F8F2B92EFA1BD33F90CDE2FA1C3BC4D63D60AA87A56FC67"),
            ("FxHostCompressedImage", "E974CA88856E5F9CD0481BA27E7517E3D6D094571DDD6C724C9816B0AEBECA84"),
            ("PiixIdeCompressedImage", "E74780B5B2463FC9E2EDBACC39ED342252C0A853195BB2C0A10D18758751C6EA"),
            ("PiixResetCompressedImage", "A708D4AA984487CDF2709550FEA46E2205C422C200C70C34D5D4E9B497831F69")
        })
            test("M4 S3 fifth ROM leaves frozen11148 raw firmware byte stream " + field, () =>
            { byte[] bytes = Rom(field); Check(bytes.Length == 65536 && Convert.ToHexString(SHA256.HashData(bytes)) == expected, "The named older firmware changed"); });
        test("M4 S3 own firmware performs actual PCI setup wake and genuine text POST before own boot", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); var writes = new List<(ushort Port, uint Value, int Width)>(); m.Ports.Accessed += (p, v, w, write) => { if (write && p is 0xcf8 or 0xcfc or 0x46e8 or 0x102 or 0x3d4) writes.Add((p, v, w)); };
            m.RunSteps(1000000); Check(m.Fault is null && m.Cpu.Halted && m.Memory.ReadWord(0x500) == 0xbeef);
            Check(writes.Contains((0xcf8, 0x80001810, 4)) && writes.Contains((0xcfc, 0xe0000000, 4)) && writes.Contains((0xcf8, 0x80001804, 4)) && writes.Contains((0xcfc, 3, 2)));
            Check(writes.Where(v => v.Port is 0x46e8 or 0x102).SequenceEqual(new[] { ((ushort)0x46e8, 0x10u, 1), ((ushort)0x102, 1u, 1), ((ushort)0x46e8, 8u, 1) }));
            Check(m.Board.Post10Count == 1 && m.Board.DebugText.Contains("FX/PIIX/S3", StringComparison.Ordinal) && m.Board.DebugText.Contains("P54C B5 / FX + PIIX + S3", StringComparison.Ordinal));
            Check(m.Video.ReadText().Length > 0 && m.S3!.BarBase == 0xe0000000 && m.S3!.IoSpaceEnabled && m.S3!.MemorySpaceEnabled && !m.S3!.LinearEnabled);
        });
        test("M4 S3 actual CPU PCI CRTC and wake outputs preserve width and canonical BAR aliases", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Out(m, 0xcf8, 0x80001800, 4); Check(In(m, 0xcfc, 4) == 0x88115333);
            Config(m, 4, 3, 2); Out(m, 0x46e8, 0x10); Out(m, 0x102, 1); Out(m, 0x46e8, 8); Out(m, 0x3c2, 0x67);
            Crtc(m, 0x38, 0x48); Crtc(m, 0x39, 0xa5); Crtc(m, 0x5a, 0x21); Config(m, 0x10, 0xe0000000);
            Check(m.Video.TrioBarBase == 0xe0000000 && m.Video.TrioLinearBase == 0xe0210000); Out(m, 0x3d4, 0x2e); Check(In(m, 0x3d5) == 0x11);
            Out(m, 0xcf8, 0x80001810, 4); Check(In(m, 0xcfc, 4) == 0xe0000000);
        });
        test("M4 S3 actual INT10 mode13 EGA pixel operations and text return retain standard VGA semantics", () =>
        {
            using var m = Boot(); Bios(m, 0x13); var indexed = m.CaptureFrame(); Check(indexed.Width == 320 && indexed.Height == 200);
            m.Memory.WriteByte(0xa0000, 201); Bios(m, 0x93); Check(m.Memory.ReadByte(0xa0000) == 201); Bios(m, 0x13); Check(m.Memory.ReadByte(0xa0000) == 0);
            Bios(m, 0x12); var planar = m.CaptureFrame(); Check(planar.Width == 640 && planar.Height == 480);
            m.Cpu.SetRegister(Register.Cx, 639); m.Cpu.SetRegister(Register.Dx, 479); Bios(m, 0x0c0b); Bios(m, 0x0d00); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 11);
            Bios(m, 3); Bios(m, 0x0e41); var text = m.CaptureFrame(); Check(m.Video.ReadText() == "A" && text.Width == 640 && text.Height == 400 && text.ArgbPixels.Take(640 * 16).Distinct().Count() > 1);
            Check(indexed.ArgbPixels.Length == 64000 && !m.S3!.LinearEnabled);
        });
        foreach (ushort mode in new ushort[] { 3, 0x12, 0x13 })
            test("M4 S3 actual INT10 returns from enhanced geometry to legacy mode " + mode, () =>
            {
                using var m = Boot(); m.Cpu.Reset(); Crtc(m, 0x38, 0x48); Crtc(m, 0x39, 0xa5); Crtc(m, 0x40, 1); Crtc(m, 0x31, 0x38); Crtc(m, 0x3a, 0x10); Crtc(m, 0x58, 0x12);
                Crtc(m, 0x51, 0x3f); Crtc(m, 0x69, 5); Crtc(m, 0x5d, 2); Crtc(m, 0x5e, 2); Crtc(m, 0x43, 0x80); Out(m, 0x4ae8, 1, 2); Check(m.Video.GraphicsMode && m.Video.Indexed256);
                Bios(m, mode); var frame = m.CaptureFrame(); Check(!m.S3!.LinearEnabled && frame.Width == (mode == 0x13 ? 320 : 640) && frame.Height == (mode == 0x13 ? 200 : mode == 0x12 ? 480 : 400));
                Check(m.Video.GraphicsMode == (mode != 3) && m.Video.Indexed256 == (mode == 0x13));
            });
        foreach (bool hard in new[] { false, true })
            test("M4 S3 actual CF9 preserves VRAM and distinguishes INIT from PCI reset gates " + hard, () =>
            {
                using var m = Boot(); m.Cpu.Reset(); Crtc(m, 0x38, 0x48); Crtc(m, 0x39, 0xa5); Crtc(m, 0x40, 1); Crtc(m, 0x31, 8); Crtc(m, 0x58, 0x12);
                Out(m, 0x3c4, 0x0608, 2); Out(m, 0x3c4, 0x800a, 2); Out(m, 0x3c4, 0x8015, 2); Out(m, 0x4ae8, 1, 2); m.Memory.WriteByte(0xe01fffff, 0xa7);
                Out(m, 0x3c8, 7); Out(m, 0x3c9, 17); Out(m, 0xcf9, hard ? 6u : 4); string before = Vram(m), video = JsonSerializer.Serialize(m.Video.SaveState(), StateValues.Json);
                ulong now = m.Scheduler.Now; Check(m.RunSteps(1, now) == 0 && Vram(m) == before && m.HardwareResetCount == 1);
                if (hard) Check(m.PiixReset!.CpuResetHeld && m.S3!.BarBase == 0 && !m.S3!.IoSpaceEnabled && !m.S3!.MemorySpaceEnabled && m.Video.TrioSetup == 0 && m.Video.TrioAdvanced == 0);
                else Check(!m.PiixReset!.CpuResetHeld && video == JsonSerializer.Serialize(m.Video.SaveState(), StateValues.Json) && m.S3!.LinearEnabled);
            });
        test("M4 S3 real power cycle constructs new cold card and VRAM while retaining battery CMOS", () =>
        {
            using var m = Boot(); m.Rtc.Write(0x70, 0x38, 1); m.Rtc.Write(0x71, 0x69, 1); using var cold = m.CreatePowerCycle(m.Profile);
            Check(!ReferenceEquals(m.S3, cold.S3) && !ReferenceEquals(m.Video, cold.Video) && cold.S3!.BarBase == 0 && !cold.Video.TrioVideoEnabled && cold.Scheduler.Now == 0 && cold.Cpu.TimestampCounter == 0);
            cold.Rtc.Write(0x70, 0x38, 1); Check(cold.Rtc.Read(0x71, 1) == 0x69); cold.CaptureHardwareState();
        });
        test("M4 S3 unsupported INT10 mode returns without video PCI or VRAM effects", () =>
        {
            using var m = Boot(); Enhanced(m); m.Ports.Write(0x3c8, 9, 1); m.Ports.Write(0x3c9, 17, 1);
            m.Memory.WriteByte(m.S3!.LinearBase + 0x1fff01, 0xa7);
            string video = JsonSerializer.Serialize(m.Video.SaveState(), StateValues.Json), pci = JsonSerializer.Serialize(m.S3!.SaveState(), StateValues.Json), bytes = Vram(m);
            byte bdaMode = m.Memory.ReadByte(0x449); var accesses = new List<ushort>();
            m.Ports.Accessed += (port, _, _, _) => { if (port is >= 0x3b0 and <= 0x3df or 0x46e8 or 0x102 or 0x4ae8 or 0x4ae9 or 0xcf8 or >= 0xcfc and <= 0xcff) accesses.Add(port); };
            uint[] caller = Caller(m, 0x007f); Bios(m, 0x007f); Returned(m, caller);
            Check(accesses.Count == 0 && video == JsonSerializer.Serialize(m.Video.SaveState(), StateValues.Json) && pci == JsonSerializer.Serialize(m.S3!.SaveState(), StateValues.Json) && bytes == Vram(m) && m.Memory.ReadByte(0x449) == bdaMode,
                "Unsupported mode touched an actual video target or memory byte");
        });
        test("M4 S3 genuine enhanced to Mode83 preserves PCI resource locked keys and high VRAM", () =>
        {
            using var m = Boot(); Enhanced(m); Config(m, 4, 0x23, 2); Crtc(m, 0x5a, 0x61);
            uint first = 0x1f0001, second = 0x1ffffc; m.Memory.WriteByte(m.S3!.LinearBase + first, 0xa7); m.Memory.WriteDword(m.S3!.LinearBase + second, 0x13579bdf);
            Out(m, 0x3c4, 0x0308, 2); Crtc(m, 0x39, 0); Crtc(m, 0x38, 0);
            var before = m.Video.SaveState(); byte[] crtc = before.Fields["_crtc"].Deserialize<byte[]>(StateValues.Json)!, seq = before.Fields["_seq"].Deserialize<byte[]>(StateValues.Json)!;
            string pci = JsonSerializer.Serialize(m.S3!.SaveState(), StateValues.Json); uint bar = m.S3!.BarBase;
            uint[] caller = Caller(m, 0x0083); Bios(m, 0x0083); Returned(m, caller);
            var after = m.Video.SaveState(); byte[] nextCrtc = after.Fields["_crtc"].Deserialize<byte[]>(StateValues.Json)!, nextSeq = after.Fields["_seq"].Deserialize<byte[]>(StateValues.Json)!;
            byte[][] planes = after.Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
            Check(pci == JsonSerializer.Serialize(m.S3!.SaveState(), StateValues.Json) && m.S3!.BarBase == bar && nextCrtc[0x59] == crtc[0x59] && nextCrtc[0x5a] == crtc[0x5a]);
            Check(nextCrtc[0x38] == crtc[0x38] && nextCrtc[0x39] == crtc[0x39] && nextSeq[8] == seq[8], "The ROM failed to restore the actual initially locked key latches");
            Check(planes[first & 3][first >> 2] == 0xa7);
            for (int i = 0; i < 4; i++) { uint at = second + (uint)i; Check(planes[at & 3][at >> 2] == (byte)(0x13579bdfu >> (8 * i)), "High preserved VRAM sentinel " + i); }
            Check(!m.S3!.LinearEnabled && !m.Video.GraphicsMode && m.Video.TrioAdvanced == 0 && m.Video.Width == 640 && m.Video.Height == 400 && m.Memory.ReadByte(0x449) == 3);
        });
        test("M4 S3 ROM uses current mono decoder before restoring actual color VGA text", () =>
        {
            using var m = Boot(); Enhanced(m); Out(m, 0x3c2, 0x66); Out(m, 0x3b4, 0x31);
            Check(In(m, 0x3cc) == 0x66 && In(m, 0x3b5) == 0x38 && In(m, 0x3d5) == 255, "Own fixture never selected the mono CRTC");
            var writes = new List<(ushort Port, uint Value, int Width)>(); m.Ports.Accessed += (p, value, width, write) => { if (write && p is 0x3b4 or 0x3d4) writes.Add((p, value, width)); };
            uint[] caller = Caller(m, 3); Bios(m, 3); Returned(m, caller);
            int mono = writes.FindIndex(v => v.Port == 0x3b4 && v.Width == 2 && v.Value == 0x4838), color = writes.FindIndex(v => v.Port == 0x3d4);
            Check(mono >= 0 && color > mono && m.S3!.IoSpaceEnabled && !m.S3!.LinearEnabled && !m.Video.GraphicsMode && m.Video.Width == 640 && m.Video.Height == 400,
                "ROM made a false fixed-color decoder assumption");
            Check(m.Ports.Read(0x3cc, 1) == 0x67 && m.Ports.Read(0x3b5, 1) == 255);
            m.Cpu.SetRegister(Register.Bx, 7); Bios(m, 0x0e5a); Check(m.Video.ReadText() == "Z", "The returned color text path did not execute the real BIOS character write");
        });
    }
}
