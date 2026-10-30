// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Own RAM-only Windows board controls: real firmware OUTs/INT15, CMOS and strict saved-profile bindings, without OS media.</summary>
internal static class Windows95RamProfileTests
{
    /// <summary>One actual completed guest port write, including its width and value.</summary>
    private readonly record struct Port(ushort Address, uint Value, int Width, bool Write);
    /// <summary>Managed entry to the existing own ROM-configuration helper; it does not execute guest code.</summary>
    private delegate void RamPatch(Span<byte> rom, int ramMiB);
    /// <summary>Requires an independently specified profile, physical byte or guest firmware result.</summary>
    private static void Check(bool value, string why = "Windows95 RAM invariant failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Requires rejection of an unsupported cold hardware definition.</summary>
    private static void Invalid(Action action)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, "Unsupported RAM definition accepted."); }
    /// <summary>Builds only the two existing Windows board identities, retaining their clock and all required peers.</summary>
    private static MachineProfile Profile(bool fast, int ramMiB) => new()
    {
        Id = fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
        Name = "Own RAM board", Cpu = MachineProfile.P54cCpuId,
        CpuHz = fast ? MachineProfile.Windows95Cpu133Hz : MachineProfile.FxHostCpuHz, RamMiB = ramMiB,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId
    };
    /// <summary>Reads one real guest CMOS byte and restores the exact previous selector/NMI latch.</summary>
    private static byte Cmos(PcMachine machine, byte index)
    {
        uint previous = machine.Ports.Read(0x70, 1);
        try { machine.Ports.Write(0x70, (uint)(index | 0x80), 1); return (byte)machine.Ports.Read(0x71, 1); }
        finally { machine.Ports.Write(0x70, previous, 1); }
    }
    /// <summary>Runs a genuine INT15 through the own POST-installed vector and returns complete architectural register values.</summary>
    private static uint[] Int15(PcMachine machine, ushort ax)
    {
        var cpu = machine.Cpu; cpu.Reset(); cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0);
        cpu.SetSegment(Segment.Ds, 0x2345); cpu.SetSegment(Segment.Es, 0x3456);
        cpu.SetRegister(Register.Sp, 0x9000); cpu.Eip = 0x1000; cpu.Eflags = X86Cpu.Carry | 2;
        uint[] before = [0x12340000u | ax,0x23456789,0x3456789a,0x456789ab,0x9000,0x56789abc,0x6789abcd,0x789abcde];
        for (int i = 0; i < before.Length; i++) cpu.SetRegister((Register)i, before[i]);
        new byte[] { 0xcd,0x15,0xfa,0xf4 }.CopyTo(machine.Memory.Ram, 0x1000);
        int steps = 0;
        while (!cpu.Halted && machine.Fault is null && steps < 10000) steps += machine.RunSteps(32);
        Check(cpu.Halted && machine.Fault is null && (cpu.Eflags & X86Cpu.Carry) == 0,
            machine.Fault ?? "Own INT15 did not return with success.");
        uint[] after = Enumerable.Range(0, 8).Select(i => cpu.GetRegister((Register)i)).ToArray();
        for (int i = 0; i < before.Length; i++) Check((after[i] & 0xffff0000) == (before[i] & 0xffff0000), "INT15 changed upper register bits.");
        Check(cpu.GetSegment(Segment.Ds) == 0x2345 && cpu.GetSegment(Segment.Es) == 0x3456 && after[4] == 0x9000,
            "INT15 changed the caller segment or stack contract.");
        return after;
    }
    /// <summary>Calls the actual pre-branch validator without constructing any snapshot files or media branches.</summary>
    private static void CheckSavedProfile(PcMachine machine, MachineProfile profile)
    {
        var method = typeof(PcMachine).Assembly.GetType("DOSEmulator.Runtime.SnapshotValidation")!
            .GetMethod("CheckProfile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var validate = method.CreateDelegate<Action<HardwareState, MachineProfile, int>>();
        HardwareState state = machine.CaptureHardwareState();
        string before = JsonSerializer.Serialize(state, StateValues.Json);
        validate(state, profile, machine.Memory.Ram.Length);
        Check(JsonSerializer.Serialize(state, StateValues.Json) == before, "Saved-profile validation changed hardware data.");
        int other = profile.RamMiB == 16 ? 64 : 16;
        bool rejected = false;
        try { validate(state, profile with { RamMiB = other }, machine.Memory.Ram.Length); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "A supported but differently sized snapshot definition was accepted.");
    }
    /// <summary>Checks one complete own PC from physical reset through actual DRB programming and BIOS memory services.</summary>
    private static void Firmware(bool fast, int ramMiB)
    {
        MachineProfile profile = Profile(fast, ramMiB);
        var definition = new MachineDefinition { Name = "Own RAM PC", Hardware = profile };
        using var machine = MachineFactory.FromDefinition(definition);
        var host = machine.HostBridge ?? throw new InvalidOperationException("Own Windows RAM PC has no host bridge.");
        Check(machine.Memory.Ram.Length == ramMiB * 1048576 && host.DecodedRamBytes == 8 * 1048576,
            "Factory storage or physical host reset decode differs.");
        Check(machine.Cpu.TimestampFrequency == (uint)profile.CpuHz && machine.Cpu.UsesTimestampClock(machine.Scheduler, (uint)profile.CpuHz),
            "RAM selection changed the fixed CPU clock source.");
        var writes = new List<Port>();
        /// <summary>Records only completed real guest port transactions, without injecting BIOS results.</summary>
        void Access(ushort address, uint value, int width, bool write)
        { if (write && address is 0xcf8 or 0xcfc) writes.Add(new(address, value, width, write)); }
        machine.Ports.Accessed += Access;
        int count = 0;
        uint boundary = (uint)ramMiB / 4;
        while (count++ < 2000 && (host.ReadConfiguration(0x64, 1) != boundary || machine.Pci!.ConfigurationAddress != 0))
            Check(machine.RunSteps(1) == 1 && machine.Fault is null, machine.Fault ?? "Early DRB firmware stopped.");
        machine.Ports.Accessed -= Access;
        Check(count < 2000 && machine.Memory.DecodedRamBytes == ramMiB * 1048576,
            "Real early firmware did not expose the selected installed RAM.");
        Port[] expected = [new(0xcf8,0x80000060,4,true),new(0xcfc,boundary * 0x01010101u,4,true),
            new(0xcf8,0x80000064,4,true),new(0xcfc,boundary,1,true),new(0xcf8,0,4,true)];
        Check(writes.Count >= 5 && writes.TakeLast(5).SequenceEqual(expected), "DRB settings were not produced by the original real OUT sequence.");
        for (int i = 0; i < 5; i++) Check(host.ReadConfiguration(0x60 + i, 1) == boundary, "A DRB boundary retained 32 MiB.");
        uint last = (uint)machine.Memory.Ram.Length - 1;
        machine.Ports.Write(0x92, 2, 1); machine.Memory.WriteByte(last, 0xa7);
        Check(machine.Memory.ReadByte(last) == 0xa7, "Selected RAM top is not physically writable/readable.");
        machine.Memory.WriteByte(last, 0);
        machine.RunSteps(300000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "Own media-free POST did not finish.");
        int extended = Math.Min((ramMiB - 1) * 1024, 0xfc00);
        Check((Cmos(machine, 0x17) | (Cmos(machine, 0x18) << 8)) == extended &&
            (Cmos(machine, 0x30) | (Cmos(machine, 0x31) << 8)) == extended,
            "CMOS extended memory differs from the bounded legacy memory report.");
        machine.Ports.Write(0x21, 255, 1); machine.Ports.Write(0xa1, 255, 1);
        uint[] ah88 = Int15(machine, 0x8800); Check((ah88[0] & 65535) == extended, "Actual AH88 RAM report differs.");
        uint[] e801 = Int15(machine, 0xe801);
        uint lower = (uint)(ramMiB <= 32 ? (ramMiB - 1) * 1024 : 15 * 1024);
        uint upper = (uint)(ramMiB <= 32 ? 0 : (ramMiB - 16) * 16);
        Check((e801[0] & 65535) == lower && (e801[1] & 65535) == lower &&
            (e801[3] & 65535) == upper && (e801[2] & 65535) == upper,
            "Actual E801 AX/CX/BX/DX split differs.");
        Check(1048576UL + lower * 1024UL + upper * 65536UL == (ulong)machine.Memory.Ram.Length,
            "INT15 cannot describe the complete selected RAM.");
        CheckSavedProfile(machine, profile);
        Check(machine.CaptureDefinition().Hardware.RamMiB == ramMiB, "Cold PC definition lost selected RAM.");
    }
    /// <summary>Checks the default patch against every full original generated FX ROM, without using a regenerated expected ROM.</summary>
    private static void DefaultRomIdentity()
    {
        var patch = typeof(DOSEBios).GetMethod("ConfigureFxHostRam", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<RamPatch>();
        foreach (string field in new[] { "FxHostCompressedImage", "PiixIdeCompressedImage", "PiixResetCompressedImage",
            "Trio64CompressedImage", "PciBiosCompressedImage", "Windows95CompressedImage" })
        {
            string encoded = (string)typeof(DOSEBios).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            using var source = new MemoryStream(Convert.FromBase64String(encoded)); using var gzip = new GZipStream(source, CompressionMode.Decompress);
            byte[] original = new byte[65536]; gzip.ReadExactly(original); byte[] actual = original.ToArray();
            patch(actual, 32); Check(actual.AsSpan().SequenceEqual(original), "32-MiB patch changed original ROM bytes: " + field);
        }
    }
    /// <summary>Registers only the new RAM choices and concrete no-media component/firmware contracts; no Windows boot is implied.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool fastValue in new[] { false, true })
        {
            bool fast = fastValue;
            foreach (int ramValue in new[] { 16,32,64,128 })
            {
                int ram = ramValue;
                test($"Windows95 RAM profile/firmware/CMOS/INT15/strict definition fast={fast} MiB={ram}", () => Firmware(fast, ram));
                test($"Windows95 RAM exact JSON hardware tuple fast={fast} MiB={ram}", () =>
                {
                    var p = Profile(fast, ram); p.Validate();
                    Check(p.GetSupportedRamMiB().SequenceEqual(new[] { 16,32,64,128 }), "Windows RAM menu choices differ from validation.");
                    var d = new MachineDefinition { Name = "Own RAM persisted PC", Hardware = p };
                    var restored = JsonSerializer.Deserialize<MachineDefinition>(JsonSerializer.Serialize(d, IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
                    restored.Validate(); Check(restored.Hardware == p && restored.Hardware.RamMiB == ram, "Hardware JSON lost exact selected RAM.");
                });
            }
            test("Windows95 RAM rejects capacities outside selected 430FX choices fast=" + fast, () =>
            {
                foreach (int ram in new[] { int.MinValue,0,4,8,24,48,96,129,256,int.MaxValue })
                    Invalid(() => (Profile(fast, ram)).ValidateCpuSelection());
            });
        }
        foreach (string idValue in new[] { MachineProfile.FxHostProfileId, MachineProfile.PiixIrqProfileId,
            MachineProfile.PiixIdeProfileId, MachineProfile.PiixResetProfileId, MachineProfile.S3Trio64ProfileId,
            MachineProfile.AtapiProfileId, MachineProfile.PciBiosProfileId })
        {
            string id = idValue;
            test("Windows95 RAM preserves prior fixed FX profile " + id, () =>
            {
                var p = Profile(false, 32) with { Id = id, OpticalDrive = id is MachineProfile.AtapiProfileId or MachineProfile.PciBiosProfileId ? MachineProfile.AtapiCdRomId : "none",
                    VideoCard = id is MachineProfile.S3Trio64ProfileId or MachineProfile.AtapiProfileId or MachineProfile.PciBiosProfileId ? MachineProfile.S3Trio64VideoId : DOSEIdentity.VgaModel };
                p.Validate(); Check(p.GetSupportedRamMiB().SequenceEqual(new[] { 32 }), "An earlier FX board acquired Windows RAM choices.");
                foreach (int ram in new[] { 16,64,128 }) Invalid(() => (p with { RamMiB = ram }).ValidateCpuSelection());
            });
        }
        test("Windows95 RAM preserves original AT capacity choices", () =>
        {
            foreach (bool p54c in new[] { false,true })
            {
                var p = new MachineProfile().WithDevelopmentCpu(p54c);
                Check(p.GetSupportedRamMiB().SequenceEqual(new[] { 4,8,16 }), "An earlier AT board acquired Windows RAM choices.");
                foreach (int ram in new[] { 4,8,16 }) (p with { RamMiB = ram }).Validate();
                foreach (int ram in new[] { 32,64,128 }) Invalid(() => (p with { RamMiB = ram }).ValidateCpuSelection());
            }
        });
        test("Windows95 RAM 32 MiB retains every original generated FX ROM byte", DefaultRomIdentity);
    }
}
