// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual CPU PCI cycles, independent PAM/DRAM effects and complete local host-controller PC state.</summary>
internal static class FxHostPcTests
{
    /// <summary>Selects the explicit limited host-controller board with its constructor-fixed CPU and memory tuple.</summary>
    private static MachineProfile Profile() => new()
    {
        Id = MachineProfile.FxHostProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = MachineProfile.FxHostCpuHz, RamMiB = MachineProfile.FxHostRamMiB, Name = "Own 430FX host fixture"
    };
    /// <summary>Requires an observable guest, byte, file or owner-state result.</summary>
    private static void Check(bool value, string why = "430FX host PC contract failed.") { if (!value) throw new Exception(why); }
    /// <summary>Accepts only the expected validation families.</summary>
    private static void Invalid(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("Invalid host configuration was accepted.");
    }
    /// <summary>Executes one real-mode instruction through the production PC and its own scheduler.</summary>
    private static void One(PcMachine m, byte[] bytes)
    {
        Check(!m.Cpu.Halted, "Cannot reactivate a halted test CPU.");
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetSegment(Segment.Ss, 0);
        m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; bytes.CopyTo(m.Memory.Ram, 0x1000);
        Check(m.RunSteps(1) == 1 && m.Fault is null, m.Fault ?? "Guest instruction did not execute.");
    }
    /// <summary>Performs a genuine byte/word/dword OUT instruction without directly calling the PCI implementation.</summary>
    private static void Out(PcMachine m, ushort port, uint value, int width)
    {
        m.Cpu.SetRegister(Register.Dx, port); m.Cpu.SetRegister(Register.Ax, value);
        One(m, width == 1 ? [0xee] : width == 2 ? [0xef] : [0x66, 0xef]);
    }
    /// <summary>Performs the corresponding IN instruction and observes only its architectural operand width.</summary>
    private static uint In(PcMachine m, ushort port, int width)
    {
        m.Cpu.SetRegister(Register.Dx, port); m.Cpu.SetRegister(Register.Ax, 0x5a5a5a5a);
        One(m, width == 1 ? [0xec] : width == 2 ? [0xed] : [0x66, 0xed]);
        uint mask = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
        Check((m.Cpu.GetRegister(Register.Ax) & ~mask) == (0x5a5a5a5a & ~mask), "IN changed a nonoperand register half.");
        return m.Cpu.GetRegister(Register.Ax) & mask;
    }
    /// <summary>Selects BDF zero and the requested register, then writes the original CPU-selected lane.</summary>
    private static void Write(PcMachine m, int offset, uint value, int width = 1)
    { Out(m, 0xcf8, 0x80000000u | (uint)(offset & 0xfc), 4); Out(m, (ushort)(0xcfc + (offset & 3)), value, width); }
    /// <summary>Reads an actual CPU-selected configuration lane using independently assembled CF8/CFC addresses.</summary>
    private static uint Read(PcMachine m, int offset, int width = 1)
    { Out(m, 0xcf8, 0x80000000u | (uint)(offset & 0xfc), 4); return In(m, (ushort)(0xcfc + (offset & 3)), width); }
    /// <summary>Captures the entire explicit component graph and the scheduler's future operations.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares every installed DRAM byte, including currently undecoded rows and PAM backing storage.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        Check(State(a) == State(b), "Full CPU/device/PCI/scheduler state differs.");
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Full 32-MiB physical RAM differs.");
        Check(a.Cpu.TimestampCounter == b.Cpu.TimestampCounter && b.Cpu.UsesTimestampClock(b.Scheduler, 100_000_000), "Virtual timestamp owner or epoch differs.");
    }
    /// <summary>Places an irreversible HLT only after all instruction-based setup has completed.</summary>
    private static void Halt(PcMachine m)
    { One(m, [0xfa]); One(m, [0xdb, 0xe3]); One(m, [0xd9, 0xeb]); One(m, [0xf4]); Check(m.Cpu.Halted); }
    /// <summary>Writes the modular epoch through WRMSR, preserving all scheduler and timer ownership.</summary>
    private static void WriteTsc(PcMachine m, ulong value)
    {
        m.Cpu.SetRegister(Register.Cx, 0x10); m.Cpu.SetRegister(Register.Dx, (uint)(value >> 32)); m.Cpu.SetRegister(Register.Ax, (uint)value); One(m, [0x0f, 0x30]);
    }
    /// <summary>Finds the concrete persisted type without making its graph owner label part of the hardware oracle.</summary>
    private static JsonObject Fields(JsonObject state, string type) => state["components"]!.AsObject()
        .Single(item => item.Value!["type"]!.GetValue<string>() == type).Value!["fields"]!.AsObject();
    /// <summary>Edits a private snapshot and repairs all enclosing hashes so the typed semantic checks must reject it.</summary>
    private static void EditState(string saved, Action<JsonObject> edit)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Changes only sealed private hardware metadata and repairs its enclosing snapshot hash.</summary>
    private static void EditProfile(string saved, Action<JsonObject> edit)
    {
        string path = Path.Combine(saved, "media.dosemedia", "media.json"); var media = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(media["machine"]!["hardware"]!.AsObject());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(media, IpcProtocol.JsonOptions); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Changes a raw byte array under the same typed JSON representation used by StateWriter.</summary>
    private static void Config(JsonObject state, Action<byte[]> edit)
    {
        var fields = Fields(state, "DOSEmulator.Hardware.Intel82437Fx"); byte[] bytes = fields["_config"]!.Deserialize<byte[]>(StateValues.Json)!; edit(bytes);
        fields["_config"] = JsonSerializer.SerializeToNode(bytes, StateValues.Json);
    }
    /// <summary>Pumps the real owner command so rejection must preserve the currently adopted PC and input generation.</summary>
    private static IpcResponse Request(MachineSession session, string command, object payload)
    { var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return pending.GetAwaiter().GetResult(); }
    private sealed class AlienFunction : IPciConfigurationFunction
    {
        /// <summary>Identifies only an explicitly synthetic additional function, never a claimed commercial card.</summary>
        public uint ReadConfiguration(int offset, int width) => 0;
        /// <summary>Leaves all existing hardware untouched.</summary>
        public void WriteConfiguration(int offset, uint value, int width) { }
    }
    /// <summary>Registers actual host transactions and complete PC continuations, without claiming a full chipset or Windows platform.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 430FX host factory binds model clock 32-MiB storage and only BDF zero", () =>
        {
            var profile = Profile(); profile.Validate(); using var m = PcMachine.CreateDemo(profile);
            Check(profile.IsFxHost && profile.IsP54c && m.HostBridge is not null && m.Pci is not null);
            Check(m.Cpu.Model == X86CpuModel.IntelP54cB5Subset && m.Cpu.TimestampFrequency == 100_000_000 && m.Cpu.UsesTimestampClock(m.Scheduler, 100_000_000));
            Check(m.Memory.Ram.Length == 32 * 1048576 && m.Memory.DecodedRamBytes == 8 * 1048576 && m.Memory.RamHoleLength == 0);
            Check(m.HostBridge!.HasMemoryBinding(m.Memory) && m.Pci!.HasOnlyFunction(0, 0, 0, m.HostBridge) && m.Pci!.ConfigurationAddress == 0);
            m.Cpu.SetRegister(Register.Ax, 1); One(m, [0x0f, 0xa2]); Check(m.Cpu.GetRegister(Register.Ax) == 0x524 && m.Cpu.GetRegister(Register.Dx) == 0x135);
            Check(m.Cpu.TimestampCounter == 4 && m.Cpu.Cr4 == 0);
        });
        foreach (bool p54c in new[] { false, true })
            test("M4 430FX host leaves prior factory profiles without PCI p54c=" + p54c, () =>
            {
                using var m = PcMachine.CreateDemo(new MachineProfile().WithDevelopmentCpu(p54c));
                Check(m.HostBridge is null && m.Pci is null && m.Memory.DecodedRamBytes == m.Memory.Ram.Length && m.Memory.RamHoleLength == 0);
                Check(m.Ports.Read(0xcf8, 4) == uint.MaxValue && m.Ports.Read(0xcfc, 4) == uint.MaxValue);
                Check(m.Cpu.TimestampFrequency == (p54c ? 90_000_000u : 0) && m.Cpu.Model == (p54c ? X86CpuModel.IntelP54cB5Subset : X86CpuModel.Intel486Dx));
            });
        foreach (string invalid in new[] { "cpu", "frequency90", "frequency0", "ram8", "ram16", "ram64", "unknown-id" })
            test("M4 430FX host profile rejects unmatched tuple before media open " + invalid, () =>
            {
                var p = Profile(); p = invalid switch
                {
                    "cpu" => p with { Cpu = "i486dx-development" }, "frequency90" => p with { CpuHz = 90_000_000 }, "frequency0" => p with { CpuHz = 0 },
                    "ram8" => p with { RamMiB = 8 }, "ram16" => p with { RamMiB = 16 }, "ram64" => p with { RamMiB = 64 }, _ => p with { Id = "unimplemented-430fx-board" }
                };
                Invalid(p.Validate);
                using var own = new SnapshotTests.Fixture(); var definition = new MachineDefinition { Hardware = p, HardDisks = [new(0, own.PathFor("absent.img"), own.PathFor("not-created.cow"))] };
                Invalid(() => MachineFactory.FromDefinition(definition)); Check(!File.Exists(own.PathFor("not-created.cow")));
            });
        test("M4 430FX host firmware programs real DRB lanes before announcing installed RAM", () =>
        {
            using var m = new PcMachine(Profile(), null); Check(m.Memory.DecodedRamBytes == 8 * 1048576); m.RunSteps(150000);
            Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not finish at the absent boot target.");
            Check(m.Memory.DecodedRamBytes == 32 * 1048576 && m.HostBridge!.HasMemoryBinding(m.Memory));
            for (int i = 0; i < 5; i++) Check(m.HostBridge!.ReadConfiguration(0x60 + i, 1) == 8, "Firmware did not write every actual DRB row boundary.");
            Check(m.Board.DebugText.Contains("430FX", StringComparison.Ordinal) && m.Video.ReadText().Contains("430FX", StringComparison.Ordinal), "Actual firmware inventory lacks host board identity.");
            Check(m.Cpu.Model == X86CpuModel.IntelP54cB5Subset && m.Cpu.TimestampFrequency == 100_000_000);
        });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 430FX host actual CPU identity and readonly header widths " + width, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); byte[] header = [0x86, 0x80, 0x2d, 0x12, 6, 0, 0, 2, 2, 0, 0, 6, 0, 0, 0, 0];
                for (int offset = 0; offset + width <= 16; offset += width)
                {
                    uint expected = 0; for (int i = 0; i < width; i++) expected |= (uint)header[offset + i] << (8 * i);
                    Check(Read(m, offset, width) == expected, "Independent A2 header differs.");
                }
                Write(m, 0, uint.MaxValue, width); Write(m, 8, uint.MaxValue, width); Write(m, 0x10, uint.MaxValue, width); Write(m, 0xfc, uint.MaxValue, width);
                Check(Read(m, 0, 4) == 0x122d8086 && Read(m, 8, 4) == 0x06000002 && Read(m, 0x10, 4) == 0 && Read(m, 0xfc, 4) == 0);
            });
        foreach (var (offset, mask, fixedBits) in new (int, byte, byte)[] { (4, 2, 4), (0x0d, 0xf8, 0), (0x50, 0xef, 0), (0x52, 0xfb, 0), (0x57, 0xcf, 0), (0x58, 0x7f, 0), (0x59, 0x70, 0), (0x5a, 0x77, 0), (0x5f, 0x77, 0), (0x60, 0x3f, 0), (0x64, 0x3f, 0), (0x68, 0x1f, 0), (0x72, 0, 2) })
            test($"M4 430FX host actual byte lane mask {offset:X2}", () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Write(m, offset, 255); Check(Read(m, offset) == (uint)(mask | fixedBits));
                Write(m, offset, 0); Check((offset == 0x64 ? m.HostBridge!.ReadConfiguration(offset, 1) : Read(m, offset)) == fixedBits);
            });
        test("M4 430FX host MAE and cache latches do not hide CPU DRAM or advance virtual time themselves", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); m.Memory.WriteDword(0x20000, 0x98765432); Write(m, 4, 0);
            Check(!m.HostBridge!.MemoryAccessEnabled && m.Memory.ReadDword(0x20000) == 0x98765432); m.Memory.WriteDword(0x20000, 0x12345678);
            Write(m, 0x52, 0xff); Check(m.Memory.ReadDword(0x20000) == 0x12345678 && Read(m, 4, 2) == 4);
            Write(m, 4, 2); Check(m.HostBridge!.MemoryAccessEnabled && Read(m, 4, 2) == 6);
        });
        foreach (uint bdf in new uint[] { 0x100, 0x800, 0x10000, 0xffff00 })
            test("M4 430FX host unknown BDF causes real config master abort " + bdf.ToString("X6"), () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Out(m, 0xcf8, 0x80000000 | bdf, 4); Check(In(m, 0xcfc, 4) == uint.MaxValue);
                Check(Read(m, 6, 2) == 0x2200); Write(m, 7, 0); Check(Read(m, 6, 2) == 0x2200); Write(m, 7, 0x20); Check(Read(m, 6, 2) == 0x0200);
                Out(m, 0xcf8, 0x80000000 | bdf, 4); Out(m, 0xcfc, 0x12345678, 4); Check(Read(m, 6, 2) == 0x2200 && Read(m, 0, 4) == 0x122d8086);
            });
        test("M4 430FX host disabled and unsupported PCI lanes have no endpoint or abort effects", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Out(m, 0xcf8, 0x80000, 4); Check(In(m, 0xcfc, 4) == uint.MaxValue); Out(m, 0xcfc, 0, 4);
            Out(m, 0xcf8, 0x80000800, 4); Check(In(m, 0xcff, 2) == 65535); Out(m, 0xcff, 0, 2);
            Check(Read(m, 6, 2) == 0x0200 && Read(m, 0, 4) == 0x122d8086);
        });
        for (int window = 0; window < 13; window++)
        {
            int selected = window;
            test("M4 430FX host independent PAM RE WE CE window " + selected, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); uint start = selected == 12 ? 0xf0000u : 0xc0000u + (uint)selected * 0x4000;
                uint length = selected == 12 ? 0x10000u : 0x4000u; int register = selected == 12 ? 0x59 : 0x5a + selected / 2, shift = selected == 12 || selected % 2 != 0 ? 4 : 0;
                uint[] addresses = [start, start + 1, start + 0x123, start + length / 2, start + length - 2, start + length - 1];
                foreach (uint attributes in Enumerable.Range(0, 8).Select(i => (uint)i))
                {
                    Write(m, register, attributes << shift);
                    for (int i = 0; i < addresses.Length; i++)
                    {
                        uint at = addresses[i]; byte initial = (byte)(0x31 + i * 17 + attributes * 3), stored = (byte)(0xa9 ^ i ^ attributes);
                        m.Memory.Ram[at] = initial;
                        byte external = selected == 12 ? m.Memory.ReadByte(0xffff0000u + at - 0xf0000) : (byte)255;
                        Check(m.Memory.ReadByte(at) == ((attributes & 1) != 0 ? initial : external), "PAM read selection differs.");
                        m.Memory.WriteByte(at, stored); byte expectedRam = (attributes & 2) != 0 ? stored : initial;
                        Check(m.Memory.Ram[at] == expectedRam && m.Memory.ReadByte(at) == ((attributes & 1) != 0 ? expectedRam : external), "PAM writes bypassed their independent enable.");
                    }
                    Check(Read(m, register) == attributes << shift, "Cache-enable latch was lost or leaked into another PAM field.");
                    if (selected < 12 && selected % 2 == 0) Check(m.Memory.ReadByte(start + length) == 255, "Lower nibble changed the following PAM window.");
                    if (selected < 12 && selected % 2 != 0) Check(m.Memory.ReadByte(start - 1) == 255, "Upper nibble changed the preceding PAM window.");
                }
            });
        }
        foreach (int width in new[] { 2, 4 })
            test("M4 430FX host wide PAM transactions preserve field lanes width " + width, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Write(m, 0x5c, 0x76543210, width);
                byte[] encoded = [0x10, 0x32, 0x54, 0x76];
                for (int lane = 0; lane < width; lane++) Check(Read(m, 0x5c + lane) == (encoded[lane] & 0x77));
                for (int lane = width; lane < 4; lane++) Check(Read(m, 0x5c + lane) == 0);
            });
        foreach (byte row in new byte[] { 0, 1, 2, 3, 7, 8, 9, 31, 32, 63 })
            test("M4 430FX host actual DRB4 changes ordinary RAM without row alias " + row, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); uint expected = Math.Min((uint)row * 0x400000, 0x2000000u);
                m.Memory.Ram[0x1ffffff] = 0x79; Write(m, 0x64, row); Check(m.Memory.DecodedRamBytes == expected);
                Check(m.Memory.ReadByte(expected) == 255 && !m.Memory.TryGetRamSpan(expected, 1, out _));
                if (expected != 0) { m.Memory.WriteByte(expected - 1, 0x4d); Check(m.Memory.ReadByte(expected - 1) == 0x4d); }
                if (expected < 0x2000000) { m.Memory.WriteByte(0x1ffffff, 0); Check(m.Memory.Ram[0x1ffffff] == 0x79); }
                // With DRB4=0 the guest has intentionally removed its own instruction RAM; restore wiring without pretending another RAM instruction ran.
                if (row == 0) m.HostBridge!.WriteConfiguration(0x64, 8, 1); else Write(m, 0x64, 8);
                Check(m.Memory.ReadByte(0x1ffffff) == (expected == 0x2000000 ? 0x4d : 0x79));
            });
        foreach (byte value in new byte[] { 1, 0x41, 0x81, 0xc1 })
            test("M4 430FX host DRAMC CPU holes keep storage and do not remap " + value.ToString("X2"), () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Write(m, 0x64, 8); m.Memory.Ram[0x80000] = 0x31; m.Memory.Ram[0xf00000] = 0x52; m.Memory.Ram[0x1000000] = 0x73;
                Write(m, 0x57, value); uint start = value == 0x41 ? 0x80000u : value == 0x81 ? 0xf00000u : 0;
                uint length = value == 0x41 ? 0x20000u : value == 0x81 ? 0x100000u : 0;
                Check(m.Memory.RamHoleStart == start && m.Memory.RamHoleLength == length && m.Memory.DecodedRamBytes == 0x2000000);
                if (length != 0) { byte initial = m.Memory.Ram[start]; Check(m.Memory.ReadByte(start) == 255); m.Memory.WriteByte(start, 0); Check(m.Memory.Ram[start] == initial); }
                Check(m.Memory.ReadByte(0x1000000) == 0x73); Write(m, 0x57, 1); Check(m.Memory.ReadByte(0x80000) == 0x31 && m.Memory.ReadByte(0xf00000) == 0x52);
            });
        test("M4 430FX host DRB0 through DRB3 retain row latches without inventing alias banks", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Write(m, 0x60, 0x01030507, 4);
            Check(Read(m, 0x60, 4) == 0x01030507 && m.Memory.DecodedRamBytes == 8 * 1048576 && Read(m, 0x64) == 2);
        });
        test("M4 430FX host invalid direct configuration transactions have no partial side effects", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); string before = JsonSerializer.Serialize(m.HostBridge!.SaveState(), StateValues.Json);
            foreach (var (offset, width) in new[] { (-1, 1), (255, 2), (254, 4), (256, 1), (0, 3), (0, 0), (0, 8) })
            { Invalid(() => m.HostBridge!.ReadConfiguration(offset, width)); Invalid(() => m.HostBridge!.WriteConfiguration(offset, uint.MaxValue, width)); }
            Check(before == JsonSerializer.Serialize(m.HostBridge!.SaveState(), StateValues.Json) && m.Memory.DecodedRamBytes == 8 * 1048576);
        });
        test("M4 430FX host rejects added PCI topology before saving and retains usable PC", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine; m.Pci!.RegisterFunction(0, 1, 0, new AlienFunction());
            uint ip = m.Cpu.Eip; long instructions = m.Cpu.Instructions; Invalid(() => SnapshotStore.Capture(m, fixture.PathFor("topology.dossav")));
            Check(!Directory.Exists(fixture.PathFor("topology.dossav")) && m.Cpu.Eip == ip && m.Cpu.Instructions == instructions);
            One(m, [0x90]); Check(m.Fault is null);
        });
        test("M4 430FX host rejects externally detached memory wiring before snapshot capture", () =>
        {
            using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine; m.Memory.SetDecodedRamBytes(0x400000);
            Invalid(() => SnapshotStore.Capture(m, fixture.PathFor("detached.dossav"))); Check(!Directory.Exists(fixture.PathFor("detached.dossav")));
            Write(m, 0x64, 2); Check(m.HostBridge!.HasMemoryBinding(m.Memory)); One(m, [0x90]);
        });
        foreach (byte attributes in new byte[] { 0, 1, 2, 3, 7 })
            test("M4 430FX host full snapshot retains PAM DRB PCI epoch hidden RAM and future devices " + attributes, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile() with { SerialPort = Uart16550.ProfileId }, false); var m = fixture.Machine;
                Write(m, 0x64, 3); Write(m, 0x60, 0x03020100, 4); Write(m, 0x59, (uint)attributes << 4); Write(m, 0x5a, (uint)(attributes | (attributes ^ 3) << 4)); Write(m, 0x57, 0x81);
                m.Memory.Ram[0xc0123] = 0x35; m.Memory.Ram[0xf0234] = 0x46; m.Memory.Ram[0x1800000] = 0x57; m.Memory.Ram[^1] = 0x68;
                m.Serial!.Write(0x3fb, 0x80, 1); m.Serial.Write(0x3f8, 12, 1); m.Serial.Write(0x3f9, 0, 1); m.Serial.Write(0x3fb, 3, 1); m.Serial.Write(0x3f8, 0x69, 1);
                Out(m, 0x43, 0x34, 1); Out(m, 0x40, 0x34, 1); Out(m, 0x40, 0x12, 1);
                Out(m, 0x43, 0xb6, 1); Out(m, 0x42, 0x80, 1); Out(m, 0x42, 1, 1); Out(m, 0x61, 3, 1);
                WriteTsc(m, 0xfedcba9876543210); Out(m, 0xcf8, 0x80000058, 4); Halt(m); m.RunSteps(10, m.Scheduler.Now + 123457);
                Check(m.Pci!.ConfigurationAddress == 0x80000058 && m.CaptureHardwareState().Scheduler.Events.Any(e => e.Owner == "pit"));
                using var restored = fixture.Restore(); Same(m, restored);
                Check(restored.HostBridge!.HasMemoryBinding(restored.Memory) && restored.Pci!.HasOnlyFunction(0, 0, 0, restored.HostBridge));
                Check(restored.Memory.ReadByte(0x1800000) == 255 && restored.Memory.Ram[0x1800000] == 0x57 && restored.Memory.Ram[^1] == 0x68);
                for (int i = 0; i < 8; i++) { ulong limit = m.Scheduler.Now + 100003; m.RunSteps(10, limit); restored.RunSteps(10, limit); Same(m, restored); }
            });
        foreach (string change in new[] { "vendor", "pam-fixed-bit", "config-length", "firmware", "drb-mismatch", "decode-top", "decode-mismatch", "hole-alignment", "hole-mismatch", "pci-reserved", "missing-host", "extra-host", "profile90", "profile-ram", "profile-cpu", "profile-frequency" })
            test("M4 430FX host rehashed semantic import rejection and owner rollback " + change, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine; WriteTsc(m, 0x9876543210abcdef); Write(m, 0x5a, 0x73); Halt(m);
                using var session = new MachineSession(m); string saved = fixture.PathFor("bad-host.dossav"), branch = fixture.PathFor("bad-host.dosebranch"); SnapshotStore.Capture(m, saved);
                if (change.StartsWith("profile", StringComparison.Ordinal)) EditProfile(saved, profile =>
                {
                    if (change == "profile90") { profile["id"] = MachineProfile.P54cProfileId; profile["cpuHz"] = 90_000_000; profile["ramMiB"] = 8; }
                    else if (change == "profile-ram") profile["ramMiB"] = 16;
                    else if (change == "profile-cpu") profile["cpu"] = "i486dx-development";
                    else profile["cpuHz"] = 90_000_000;
                });
                else EditState(saved, state =>
                {
                    var memory = Fields(state, "DOSEmulator.Core.MemoryBus");
                    switch (change)
                    {
                        case "vendor": Config(state, bytes => bytes[0] ^= 1); break;
                        case "pam-fixed-bit": Config(state, bytes => bytes[0x5a] |= 8); break;
                        case "drb-mismatch": Config(state, bytes => bytes[0x64] = 3); break;
                        case "config-length": Fields(state, "DOSEmulator.Hardware.Intel82437Fx")["_config"] = JsonSerializer.SerializeToNode(new byte[255], StateValues.Json); break;
                        case "firmware": Fields(state, "DOSEmulator.Hardware.Intel82437Fx")["_firmwareSha256"] = new string('0', 64); break;
                        case "decode-top": memory["DecodedRamBytes"] = 33 * 1048576; break;
                        case "decode-mismatch": memory["DecodedRamBytes"] = 4 * 1048576; break;
                        case "hole-alignment": memory["_ramHoleStart"] = 1; memory["_ramHoleLength"] = 4096; break;
                        case "hole-mismatch": memory["_ramHoleStart"] = 0xf00000; memory["_ramHoleLength"] = 0x100000; break;
                        case "pci-reserved": Fields(state, "DOSEmulator.Hardware.PciConfigurationBus")["_address"] = 0x80000001u; break;
                        case "missing-host":
                            string key = state["components"]!.AsObject().Single(item => item.Value!["type"]!.GetValue<string>() == "DOSEmulator.Hardware.Intel82437Fx").Key;
                            state["components"]!.AsObject().Remove(key); break;
                        case "extra-host": state["components"]!["foreign-host"] = state["components"]!["cpu"]!.DeepClone(); break;
                    }
                });
                string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); long revision = session.Revision;
                var answer = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
                Check(!answer.Success && ReferenceEquals(m, session.Machine) && session.Revision == revision && !Directory.Exists(branch));
                Check(before == State(m) && ram.AsSpan().SequenceEqual(m.Memory.Ram)); m.RunSteps(10, m.Scheduler.Now + 123457); Check(m.Fault is null);
            });
        test("M4 430FX host CPU hot reset retains powered PCI PAM DRB hole status and hidden RAM", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Write(m, 0x64, 3); Write(m, 0x60, 0x03020100, 4); Write(m, 0x57, 0x41);
            Write(m, 0x59, 0x70); Write(m, 0x5a, 0x73); m.Memory.Ram[0xc0000] = 0x59; m.Memory.Ram[0xf0000] = 0x6a; m.Memory.Ram[0x1800000] = 0x7b;
            Out(m, 0xcf8, 0x80000800, 4); Check(In(m, 0xcfc, 4) == uint.MaxValue); Out(m, 0xcf8, 0x8000005c, 4);
            WriteTsc(m, 0x76543210fedcba98); m.Cpu.SetRegister(Register.Ax, 12); One(m, [0x0f, 0x22, 0xe0]);
            m.Memory.WriteWord(0x472, 0x1234); var before = m.CaptureHardwareState(); byte[] ram = m.Memory.Ram.ToArray();
            var cpu = m.Cpu; var host = m.HostBridge; var pci = m.Pci; ulong now = m.Scheduler.Now; Check(m.Cpu.Cr4 == 12 && m.Cpu.TimestampCounter != 0);
            m.HardwareReset(); var after = m.CaptureHardwareState();
            Check(ReferenceEquals(cpu, m.Cpu) && ReferenceEquals(host, m.HostBridge) && ReferenceEquals(pci, m.Pci));
            Check(m.Cpu.TimestampCounter == 0 && m.Cpu.Cr4 == 0 && m.Cpu.TimestampFrequency == 100_000_000 && m.Cpu.GetRegister(Register.Dx) == 0x524);
            Check(m.Cpu.PhysicalInstructionAddress == 0xfffffff0 && m.Cpu.Instructions == 0 && m.Scheduler.Now == now);
            Check(m.HardwareResetCount == 1 && m.LastWarmBootFlag == 0x1234 && m.LastResetTime == now && ram.AsSpan().SequenceEqual(m.Memory.Ram));
            Check(m.Pci!.ConfigurationAddress == 0x8000005c && m.HostBridge!.ReadConfiguration(6, 2) == 0x2200);
            foreach (string node in before.Components.Keys.Where(key => key is not ("cpu" or "machine")))
                Check(JsonSerializer.Serialize(before.Components[node], StateValues.Json) == JsonSerializer.Serialize(after.Components[node], StateValues.Json), "CPU reset changed powered node " + node);
            Check(JsonSerializer.Serialize(before.Scheduler, StateValues.Json) == JsonSerializer.Serialize(after.Scheduler, StateValues.Json));
            Check(m.Memory.DecodedRamBytes == 12 * 1048576 && m.Memory.RamHoleStart == 0x80000 && m.Memory.RamHoleLength == 0x20000);
        });
        test("M4 430FX host actual power cycle creates reset controller peers then firmware restores 32 MiB", () =>
        {
            using var old = new PcMachine(Profile(), null); Write(old, 0x64, 3); Write(old, 0x57, 0x41); Write(old, 0x59, 0x70); Write(old, 0x5a, 0x73);
            old.Memory.Ram[0xc0000] = 0x59; old.Memory.Ram[0xf0000] = 0x6a; old.Memory.Ram[^1] = 0x7b;
            Out(old, 0xcf8, 0x80000800, 4); In(old, 0xcfc, 4); WriteTsc(old, 0x87654321fedcba98); string before = State(old);
            using var cold = old.CreatePowerCycle(old.Profile);
            Check(!ReferenceEquals(old.HostBridge, cold.HostBridge) && !ReferenceEquals(old.Pci, cold.Pci) && !ReferenceEquals(old.Cpu, cold.Cpu) && !ReferenceEquals(old.Memory, cold.Memory));
            Check(cold.Memory.Ram.Length == 32 * 1048576 && cold.Memory.DecodedRamBytes == 8 * 1048576 && cold.Memory.RamHoleStart == 0 && cold.Memory.RamHoleLength == 0);
            Check(cold.Pci!.ConfigurationAddress == 0 && cold.HostBridge!.ReadConfiguration(6, 2) == 0x0200 && cold.Cpu.TimestampCounter == 0 && cold.Cpu.Cr4 == 0 && cold.Scheduler.Now == 0);
            for (int reg = 0x59; reg <= 0x5f; reg++) Check(cold.HostBridge!.ReadConfiguration(reg, 1) == 0);
            for (int reg = 0x60; reg <= 0x64; reg++) Check(cold.HostBridge!.ReadConfiguration(reg, 1) == 2);
            Check(cold.Memory.Ram[0xc0000] == 0 && cold.Memory.Ram[0xf0000] == 0 && cold.Memory.Ram[^1] == 0 && cold.Memory.ReadByte(0xf0000) == cold.Memory.ReadByte(0xffff0000));
            cold.RunSteps(150000); Check(cold.Fault is null && cold.Cpu.Halted && cold.Memory.DecodedRamBytes == 32 * 1048576);
            for (int reg = 0x60; reg <= 0x64; reg++) Check(cold.HostBridge!.ReadConfiguration(reg, 1) == 8);
            Check(cold.HostBridge!.HasMemoryBinding(cold.Memory) && State(old) == before, "A new cold board changed the still-owned old hardware.");
        });
        test("M4 430FX host actual PE32 far calls fetch guest-written PAM shadow and return under RO", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Write(m, 0x5a, 2); Check(m.Memory.ReadDword(0xc0000) == uint.MaxValue);
            // Flat caller/data and a separate 16-KiB 32-bit executable shadow segment, all descriptor storage below 640 KiB.
            m.Memory.WriteDword(0x408, 0xffff); m.Memory.WriteDword(0x40c, 0x00cf9a00);
            m.Memory.WriteDword(0x410, 0xffff); m.Memory.WriteDword(0x414, 0x00cf9200);
            m.Memory.WriteDword(0x418, 0x3fff); m.Memory.WriteDword(0x41c, 0x00409a0c);
            m.Memory.WriteWord(0x300, 0x1f); m.Memory.WriteDword(0x302, 0x400);
            byte[] boot = [0x0f, 0x01, 0x16, 0, 3, 0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0];
            byte[] caller = [0xb8, 0x10, 0, 0, 0, 0x8e, 0xd8, 0x8e, 0xc0, 0x8e, 0xd0, 0xbc, 0, 0x90, 0, 0,
                0xc7, 0x05, 0, 0, 0x0c, 0, 0xb8, 0x21, 0x43, 0x65,
                0xc7, 0x05, 4, 0, 0x0c, 0, 0x87, 0xa3, 0, 0x10,
                0xc7, 0x05, 8, 0, 0x0c, 0, 3, 0, 0xcb, 0x90,
                0xba, 0xf8, 0x0c, 0, 0, 0xb8, 0x58, 0, 0, 0x80, 0xef, 0xba, 0xfe, 0x0c, 0, 0, 0xb0, 3, 0xee,
                0x9a, 0, 0, 0, 0, 0x18, 0, 0xa3, 4, 0x10, 3, 0, 0xb0, 1, 0xee,
                0xc7, 0x05, 0, 0, 0x0c, 0, 0xff, 0xff, 0xff, 0xff,
                0x9a, 0, 0, 0, 0, 0x18, 0, 0xa3, 8, 0x10, 3, 0, 0xf4];
            boot.CopyTo(m.Memory.Ram, 0x1000); caller.CopyTo(m.Memory.Ram, 0x2000); m.Cpu.Eip = 0x1000; m.RunSteps(4);
            Check(m.Cpu.ProtectedMode && m.Cpu.GetSegment(Segment.Cs) == 8 && m.Cpu.Eip == 0x2000 && m.Fault is null);
            int calls = 0, returns = 0, fetched = 0; uint expectedReturn = 0;
            for (int steps = 0; steps < 80 && !m.Cpu.Halted; steps++)
            {
                bool call = m.Cpu.GetSegment(Segment.Cs) == 8 && m.Memory.Ram[m.Cpu.Eip] == 0x9a;
                bool returning = m.Cpu.GetSegment(Segment.Cs) == 0x18 && m.Cpu.Eip == 10;
                if (call) expectedReturn = m.Cpu.Eip + 7;
                if (m.Cpu.GetSegment(Segment.Cs) == 0x18) { Check(m.Cpu.PhysicalInstructionAddress == 0xc0000 + m.Cpu.Eip); fetched++; }
                m.RunSteps(1); Check(m.Fault is null, m.Fault ?? "PAM guest fault.");
                if (call)
                {
                    calls++; Check(m.Cpu.GetSegment(Segment.Cs) == 0x18 && m.Cpu.Eip == 0 && m.Cpu.GetRegister(Register.Sp) == 0x8ff8);
                    Check(m.Memory.ReadDword(0x8ff8) == expectedReturn && m.Memory.ReadDword(0x8ffc) == 8, "Actual far-call stack frame differs.");
                }
                if (returning) { returns++; Check(m.Cpu.GetSegment(Segment.Cs) == 8 && m.Cpu.Eip == expectedReturn && m.Cpu.GetRegister(Register.Sp) == 0x9000); }
            }
            Check(m.Cpu.Halted && calls == 2 && returns == 2 && fetched == 6);
            Check(m.Memory.ReadDword(0x31000) == 0x87654321 && m.Memory.ReadDword(0x31004) == 0x87654321 && m.Memory.ReadDword(0x31008) == 0x87654321);
            Check(m.HostBridge!.ReadConfiguration(0x5a, 1) == 1 && m.Memory.ReadDword(0xc0000) == 0x654321b8 && m.Memory.Ram[0xc000a] == 0xcb, "RO PAM write changed executable bytes.");
        });
        foreach (var (type, field) in new[] { ("DOSEmulator.Core.MemoryBus", "DecodedRamBytes"), ("DOSEmulator.Core.MemoryBus", "_ramHoleStart"),
            ("DOSEmulator.Core.MemoryBus", "_ramHoleLength"), ("DOSEmulator.Hardware.Intel82437Fx", "_config"), ("DOSEmulator.Hardware.PciConfigurationBus", "_address") })
            test("M4 430FX host missing state field rejects before branch and media open " + field, () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(), false); var m = fixture.Machine; Halt(m); string saved = fixture.PathFor("missing.dossav"), branch = fixture.PathFor("missing.dosebranch");
                SnapshotStore.Capture(m, saved); EditState(saved, state => Check(Fields(state, type).Remove(field), "Missing-field fixture removed nothing."));
                // This is the fixture's freshly exported own MemoryDisk, not an original or a user's attached image.
                using var lockedBase = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
                string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); bool invalidData = false;
                try { using var unexpected = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { invalidData = true; }
                Check(invalidData && !Directory.Exists(branch) && State(m) == before && ram.AsSpan().SequenceEqual(m.Memory.Ram), "Missing scalar reached media opening, branch publication or old hardware.");
                using var session = new MachineSession(m); long revision = session.Revision;
                var result = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
                Check(!result.Success && ReferenceEquals(session.Machine, m) && session.Revision == revision && !Directory.Exists(branch));
                Check(State(m) == before && ram.AsSpan().SequenceEqual(m.Memory.Ram));
            });
        test("M4 430FX host failed full import preserves actual active COW generation and old owner", () =>
        {
            using var directory = new SnapshotTests.Fixture(); string basis = directory.PathFor("own-blank.img"), journal = directory.PathFor("own-active.cow");
            using (var file = new FileStream(basis, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.SetLength(2 * 1048576);
            var disk = new CopyOnWriteDisk(basis, journal); using var m = PcMachine.CreateWithDisks(Profile(), [disk, null, null, null]);
            byte[] sector = Enumerable.Range(0, 512).Select(i => (byte)(i * 17 + 3)).ToArray(); disk.WriteSector(7, sector); Write(m, 0x64, 8); Halt(m);
            using var session = new MachineSession(m); string saved = directory.PathFor("cow-state.dossav"), branch = directory.PathFor("cow-state.dosebranch"); SnapshotStore.Capture(m, saved);
            // The active COW is intentionally FileShare.None. Read only complete copies published by the real snapshot path.
            byte[] originalJournal = File.ReadAllBytes(Path.Combine(saved, "media.dosemedia", "ide0.cow"));
            string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); EditState(saved, state => Config(state, bytes => bytes[0x5f] |= 0x80));
            var answer = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
            Check(!answer.Success && !Directory.Exists(branch) && ReferenceEquals(session.Machine, m));
            Check(State(m) == before && ram.AsSpan().SequenceEqual(m.Memory.Ram));
            string after = directory.PathFor("after-rejected-import.dossav"); SnapshotStore.Capture(m, after);
            Check(originalJournal.SequenceEqual(File.ReadAllBytes(Path.Combine(after, "media.dosemedia", "ide0.cow"))) && State(m) == before);
            byte[] actual = new byte[512]; disk.ReadSector(7, actual); Check(actual.SequenceEqual(sector));
            disk.WriteSector(8, sector); disk.ReadSector(8, actual); Check(actual.SequenceEqual(sector)); m.RunSteps(10, m.Scheduler.Now + 100003); Check(m.Fault is null);
        });
    }
}
