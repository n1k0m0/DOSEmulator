// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;

/// <summary>Pure network resource/identity checks and private configuration persistence; no guest, card backend or host endpoint is constructed.</summary>
internal static class NetworkProfileTests
{
    /// <summary>Requires a separately specified resource, canonical identity or persisted byte result.</summary>
    private static void Check(bool condition, string message = "Network profile invariant failed.")
    { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Requires a controlled profile rejection rather than an unrelated runtime failure.</summary>
    private static void Invalid(Action action)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unsupported or conflicting network configuration was accepted.");
    }

    /// <summary>Explicit stable ISA fixture with no connection and no generated identity.</summary>
    private static NetworkAdapterProfile Isa(int slot = 0) => new()
    { Slot = slot, MacAddress = slot == 0 ? "02:44:4F:53:45:10" : "02:44:4F:53:45:11", IoBase = slot == 0 ? 0x300 : 0x320, Irq = slot == 0 ? 10 : 9 };

    /// <summary>Explicit PCI fixture using a distinct 32-port window but the shared fixed PIRQA route.</summary>
    private static NetworkAdapterProfile Pci(int slot = 0) => Isa(slot) with
    { Model = NetworkAdapterProfile.Rtl8029Pci, IoBase = slot == 0 ? 0x6000 : 0x6020, Irq = 11 };

    /// <summary>Constructs only the existing complete PCI-firmware board record; no emulator or backend is started.</summary>
    private static MachineProfile Board(bool fast = false) => new()
    {
        Id = fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
        Cpu = MachineProfile.P54cCpuId, CpuHz = fast ? 133_000_000 : 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId
    };

    /// <summary>Chooses one distinct scalar value to verify every persisted init property remains part of record equality.</summary>
    private static object DifferentScalar(object? value, Type type) => type == typeof(string) ? (value as string ?? "") + "-different"
        : type == typeof(int) ? checked((int)value! + 1) : type == typeof(bool) ? !(bool)value!
        : throw new InvalidOperationException("Add an explicit equality control for this new profile property type.");

    /// <summary>Checks value equality through typed, object, operator and hashed collection APIs without relying on array identity.</summary>
    private static void EqualProfiles(MachineProfile expected, MachineProfile actual)
    {
        Check(expected == actual && actual == expected && expected.Equals(actual) && actual.Equals(expected) && ((object)expected).Equals(actual),
            "Structurally identical hardware profiles must compare equal.");
        Check(expected.GetHashCode() == actual.GetHashCode(), "Equal hardware profiles must have equal hash codes.");
        Check(new HashSet<MachineProfile> { expected }.Contains(actual), "An equal round-trip profile must be a hashed collection key.");
        Check(new Dictionary<MachineProfile, int> { [expected] = 17 }.TryGetValue(actual, out int value) && value == 17,
            "An equal round-trip profile must locate the original dictionary value.");
    }

    /// <summary>Registers independent model, MAC, board, alias, IRQ, bridge, LAN and saved-PC positive/negative controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Network old profile JSON retains zero adapters", () =>
        {
            var profile = JsonSerializer.Deserialize<MachineProfile>("{\"name\":\"Own old PC\",\"ramMiB\":8}", IpcProtocol.JsonOptions)!;
            profile.Validate(); Check(profile.NetworkAdapters.Length == 0 && profile.Id == "virtual486-dev-v1" && profile.CpuHz == 33_000_000);
        });
        test("Network new adapter cable defaults OFF", () => Check(!Isa().CableConnected && !Pci().CableConnected));
        test("Network empty arrays retain old profile record equality", () =>
        {
            var expected = new MachineProfile { NetworkAdapters = new NetworkAdapterProfile[0] };
            var actual = JsonSerializer.Deserialize<MachineProfile>("{\"ramMiB\":8}", IpcProtocol.JsonOptions)!;
            expected.Validate(); actual.Validate(); EqualProfiles(expected, actual);
            EqualProfiles(expected, expected with { NetworkAdapters = [] });
        });
        foreach (bool fast in new[] { false, true })
            foreach (int count in new[] { 0, 1, 2 })
                test("Network structural profile JSON and copied array equality fast=" + fast + " cards=" + count, () =>
                {
                    NetworkAdapterProfile[] adapters = count switch { 0 => [], 1 => [Isa()], _ => [Isa(), Pci(1)] };
                    var expected = Board(fast) with { NetworkAdapters = adapters };
                    var actual = JsonSerializer.Deserialize<MachineProfile>(JsonSerializer.Serialize(expected, IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
                    expected.Validate(); actual.Validate(); EqualProfiles(expected, actual);
                    var copiedAdapters = new NetworkAdapterProfile[adapters.Length];
                    for (int i = 0; i < adapters.Length; i++) copiedAdapters[i] = adapters[i] with { };
                    var copied = expected with { NetworkAdapters = copiedAdapters };
                    Check(!ReferenceEquals(expected.NetworkAdapters, copied.NetworkAdapters), "The control must use a different array.");
                    EqualProfiles(expected, copied);
                });
        test("Network adapter order remains part of the exact persisted profile value", () =>
        {
            var expected = Board() with { NetworkAdapters = [Isa(), Pci(1)] };
            var reversed = expected with { NetworkAdapters = expected.NetworkAdapters.Reverse().ToArray() };
            expected.Validate(); reversed.Validate(); Check(expected != reversed && !expected.Equals(reversed), "Equality must not silently reorder slots.");
            var actual = JsonSerializer.Deserialize<MachineProfile>(JsonSerializer.Serialize(reversed, IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
            EqualProfiles(reversed, actual); Check(actual.NetworkAdapters[0].Slot == 1 && actual.NetworkAdapters[1].Slot == 0, "JSON must retain explicit array order.");
        });
        test("Network different array lengths remain distinct profile values", () =>
        {
            var empty = Board(); var one = empty with { NetworkAdapters = [Isa()] }; var two = empty with { NetworkAdapters = [Isa(), Pci(1)] };
            Check(empty != one && empty != two && one != two && one != (one with { NetworkAdapters = [Isa(1)] }));
        });
        test("Network invalid null arrays retain controlled equality and validation", () =>
        {
            var missing = Board() with { NetworkAdapters = null! }; var another = Board() with { NetworkAdapters = null! };
            EqualProfiles(missing, another); Check(missing != Board()); Invalid(missing.Validate);
        });
        foreach (var property in typeof(NetworkAdapterProfile).GetProperties().Where(property => property.SetMethod?.IsPublic == true))
            test("Network every adapter field participates in profile equality " + property.Name, () =>
            {
                var first = Isa(); var changed = first with { }; property.SetValue(changed, DifferentScalar(property.GetValue(first), property.PropertyType));
                var expected = Board() with { NetworkAdapters = [first] }; var actual = expected with { NetworkAdapters = [changed] };
                Check(first != changed && expected != actual && !((object)expected).Equals(actual), "A changed adapter field must change the profile value.");
                EqualProfiles(actual, actual with { NetworkAdapters = [changed with { }] });
            });
        foreach (var property in typeof(MachineProfile).GetProperties().Where(property => property.SetMethod?.IsPublic == true && property.Name != nameof(MachineProfile.NetworkAdapters)))
            test("Network equality preserves earlier machine field " + property.Name, () =>
            {
                var expected = Board() with { NetworkAdapters = [Isa(), Pci(1)] }; var changed = expected with { };
                property.SetValue(changed, DifferentScalar(property.GetValue(expected), property.PropertyType));
                Check(expected != changed && !((object)expected).Equals(changed), "An existing hardware field must remain part of record equality.");
                EqualProfiles(changed, changed with { NetworkAdapters = [Isa(), Pci(1)] });
            });
        foreach (string model in new[] { NetworkAdapterProfile.Ne2000Isa, NetworkAdapterProfile.Rtl8029Pci })
            foreach (string mode in new[] { "nat", "host-only", "bridged" })
                foreach (bool connected in new[] { false, true })
                    test("Network explicit mode/resource positive " + model + "/" + mode + "/" + connected, () =>
                    {
                        var adapter = (model == NetworkAdapterProfile.Ne2000Isa ? Isa() : Pci()) with
                        { Mode = mode, CableConnected = connected, HostAdapter = mode == "bridged" && connected ? "Own test bridge ID" : null };
                        adapter.Validate(true); (Board() with { NetworkAdapters = [adapter] }).Validate();
                        Check(adapter.GetMacBytes().SequenceEqual(new byte[] { 2, 0x44, 0x4f, 0x53, 0x45, 0x10 }));
                    });
        foreach (int basis in new[] { 0x300, 0x320 }) foreach (int irq in new[] { 9, 10 }) foreach (int slot in new[] { 0, 1 })
            test("Network ISA explicit resource matrix " + basis.ToString("X") + "/" + irq + "/" + slot, () =>
                (new MachineProfile { NetworkAdapters = [Isa(slot) with { IoBase = basis, Irq = irq }] }).Validate());
        foreach (bool fast in new[] { false, true })
            foreach (int basis in new[] { 0x6000, 0x6020 })
                test("Network RTL validated PCI firmware board " + fast + "/" + basis.ToString("X"), () =>
                    (Board(fast) with { NetworkAdapters = [Pci() with { IoBase = basis }] }).Validate());
        test("Network PCI BIOS predecessor supports the same resource contract", () =>
            (Board() with { Id = MachineProfile.PciBiosProfileId, NetworkAdapters = [Pci()] }).Validate());
        foreach (string id in new[] { MachineProfile.FxHostProfileId, MachineProfile.PiixIrqProfileId, MachineProfile.PiixIdeProfileId,
            MachineProfile.PiixResetProfileId, MachineProfile.S3Trio64ProfileId, MachineProfile.AtapiProfileId })
            test("Network prior PCI board cannot silently acquire RTL firmware contract " + id, () =>
            {
                var profile = Board() with { Id = id, VideoCard = id is MachineProfile.S3Trio64ProfileId or MachineProfile.AtapiProfileId ? MachineProfile.S3Trio64VideoId : DOSEIdentity.VgaModel,
                    OpticalDrive = id == MachineProfile.AtapiProfileId ? MachineProfile.AtapiCdRomId : "none" };
                profile.Validate(); (profile with { NetworkAdapters = [Isa()] }).Validate();
                Invalid(() => (profile with { NetworkAdapters = [Pci()] }).ValidateCpuSelection());
            });
        test("Network ISA works on old own P54C AT tuple", () =>
            (new MachineProfile { Id = MachineProfile.P54cProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 90_000_000, NetworkAdapters = [Isa()] }).Validate());
        test("Network two ISA cards have distinct ports/MACs/IRQs", () => (new MachineProfile { NetworkAdapters = [Isa(), Isa(1)] }).Validate());
        test("Network two RTL cards share only the fixed PCI IRQ11 route", () => (Board() with { NetworkAdapters = [Pci(), Pci(1)] }).Validate());
        test("Network mixed ISA and PCI resources remain independent", () => (Board() with { NetworkAdapters = [Isa(), Pci(1)] }).Validate());
        foreach (int first in Enumerable.Range(0, 256).Where(value => (value & 3) == 2))
            test("Network every locally administered unicast first byte " + first.ToString("X2"), () =>
            {
                var adapter = Isa() with { MacAddress = first.ToString("X2") + ":44:4F:53:45:10" };
                adapter.Validate(false); Check(adapter.GetMacBytes()[0] == first);
            });
        foreach (string? mac in new string?[] { null, "", "02:44:4F:53:45", "02:44:4F:53:45:10:00", "02-44-4F-53-45-10", "02:44:4f:53:45:10",
            "00:44:4F:53:45:10", "01:44:4F:53:45:10", "03:44:4F:53:45:10", "FF:FF:FF:FF:FF:FF", "02:44:4F:53:45:GG", " 2:44:4F:53:45:10" })
            test("Network malformed/global/multicast MAC rejected " + (mac ?? "null"), () => Invalid(() => (Isa() with { MacAddress = mac! }).Validate(false)));
        foreach (int slot in new[] { -1, 2, int.MaxValue })
            test("Network invalid slot rejected " + slot, () => Invalid(() => (Isa() with { Slot = slot }).Validate(false)));
        foreach (string? model in new string?[] { null, "", "NE2000-ISA", "rtl8029", "ne2000", "none" })
            test("Network unknown model rejected " + (model ?? "null"), () => Invalid(() => (Isa() with { Model = model! }).Validate(true)));
        foreach (string? mode in new string?[] { null, "", "NAT", "bridge", "automatic", "hostonly" })
            test("Network unknown backend mode rejected " + (mode ?? "null"), () => Invalid(() => (Isa() with { Mode = mode! }).Validate(false)));
        foreach (int basis in new[] { -1, 0, 0x20, 0x170, 0x1f0, 0x201, 0x220, 0x240, 0x260, 0x280, 0x301, 0x310, 0x321, 0x330, 0x378, 0x3b0, 0x3f0, 0x3f8, 0xcf8, 0xffff, 0x10000 })
            test("Network ISA unsupported/reserved base rejected " + basis.ToString("X"), () => Invalid(() => (Isa() with { IoBase = basis }).Validate(false)));
        foreach (int irq in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16 })
            test("Network ISA reserved/unwired IRQ rejected " + irq, () => Invalid(() => (Isa() with { Irq = irq }).Validate(false)));
        foreach (int basis in new[] { 0x300, 0x5fe0, 0x6001, 0x6010, 0x6040, 0xffe0, 0x10000 })
            test("Network RTL unsupported base rejected " + basis.ToString("X"), () => Invalid(() => (Pci() with { IoBase = basis }).Validate(true)));
        foreach (int irq in new[] { 0, 9, 10, 12, 15 })
            test("Network RTL fixed PCI route rejected " + irq, () => Invalid(() => (Pci() with { Irq = irq }).Validate(true)));
        test("Network RTL rejected without PCI capability", () => Invalid(() => Pci().Validate(false)));
        test("Network null adapter array rejected in preflight", () => Invalid(() => (Board() with { NetworkAdapters = null! }).ValidateCpuSelection()));
        test("Network null adapter entry rejected in preflight", () => Invalid(() => (Board() with { NetworkAdapters = [null!] }).ValidateCpuSelection()));
        test("Network more than two adapters rejected in preflight", () => Invalid(() => (Board() with { NetworkAdapters = [Isa(), Isa(1), Pci()] }).ValidateCpuSelection()));
        test("Network duplicate slot rejected", () => Invalid(() => (Board() with { NetworkAdapters = [Isa(), Pci() with { MacAddress = "02:44:4F:53:45:12" }] }).Validate()));
        test("Network duplicate MAC rejected", () => Invalid(() => (Board() with { NetworkAdapters = [Isa(), Pci(1) with { MacAddress = Isa().MacAddress }] }).Validate()));
        test("Network overlapping ISA I/O rejected", () => Invalid(() => (new MachineProfile { NetworkAdapters = [Isa(), Isa(1) with { IoBase = 0x300 }] }).Validate()));
        test("Network overlapping PCI I/O rejected", () => Invalid(() => (Board() with { NetworkAdapters = [Pci(), Pci(1) with { IoBase = 0x6000 }] }).Validate()));
        test("Network shared ISA IRQ rejected", () => Invalid(() => (new MachineProfile { NetworkAdapters = [Isa(), Isa(1) with { Irq = 10 }] }).Validate()));
        foreach (int irq in new[] { 9, 10 })
            test("Network active SB16 IRQ conflict rejected " + irq, () => Invalid(() => (Board() with
                { SoundCard = "sb16-ct1740-dsp405-development", SoundBlasterIrq = irq, NetworkAdapters = [Isa() with { Irq = irq }] }).ValidateCpuSelection()));
        test("Network active SB2 IRQ conflict rejected", () => Invalid(() => (new MachineProfile
            { SoundCard = "sb2-dsp-development", SoundBlasterIrq = 9, NetworkAdapters = [Isa() with { Irq = 9 }] }).Validate()));
        test("Network inactive sound IRQ does not reserve resources", () => (new MachineProfile { SoundBlasterIrq = 9, NetworkAdapters = [Isa() with { Irq = 9 }] }).Validate());
        foreach ((int basis, int midi) in new[] { (0x300, 0x300), (0x320, 0x330) })
            test("Network MPU port overlap rejected " + basis.ToString("X") + "/" + midi.ToString("X"), () => Invalid(() => (Board() with
                { SoundCard = "sb16-ct1740-dsp405-development", MidiInterface = "ct1740-mpu401-uart-development", MidiBasePort = midi,
                    NetworkAdapters = [Isa() with { IoBase = basis }] }).ValidateCpuSelection()));
        test("Network nonoverlapping MPU and ISA remain valid", () => (Board() with
            { SoundCard = "sb16-ct1740-dsp405-development", MidiInterface = "ct1740-mpu401-uart-development", MidiBasePort = 0x330,
                NetworkAdapters = [Isa()] }).Validate());
        test("Network COM/LPT/gameport and separate sound resources remain valid", () => (new MachineProfile
            { SerialPort = "pc16550d-com1-development", ParallelPort = "ibm-printer-adapter-development", Gameport = "ibm-game-control-development",
                SoundCard = "sb2-opl2-development", SoundBlasterIrq = 5, NetworkAdapters = [Isa()] }).Validate());
        int hostCase = 0;
        foreach (string? host in new string?[] { "", " own", "own ", "own\0bridge", new('x', 513) })
            test("Network invalid host adapter ID rejected case=" + hostCase++, () => Invalid(() => (Isa() with { Mode = "bridged", HostAdapter = host }).Validate(false)));
        test("Network selected host adapter cannot leak into another mode", () => Invalid(() => (Isa() with { HostAdapter = "Own test ID" }).Validate(false)));
        test("Network disconnected bridge can be configured without a backend", () => (Isa() with { Mode = "bridged" }).Validate(false));
        test("Network connected bridge requires an explicit selected adapter", () => Invalid(() => (Isa() with { Mode = "bridged", CableConnected = true }).Validate(false)));
        int lanCase = 0;
        foreach (string? name in new string?[] { null, "", " ", " DOSE", "DOSE ", "own\nlan", "own/lan", "\u00e4", new('x', 65) })
            test("Network invalid virtual LAN name rejected case=" + lanCase++, () => Invalid(() => (Isa() with { VirtualLan = name! }).Validate(false)));
        test("Network bounded virtual LAN names retain exact spelling", () =>
        {
            var adapter = Isa() with { Mode = "host-only", VirtualLan = "Own LAN_1.2-" + new string('x', 52) };
            Check(adapter.VirtualLan.Length == 64); adapter.Validate(false);
        });
        test("Network repeated profile/JSON validation never changes the persisted MAC", () =>
        {
            var profile = Board() with { NetworkAdapters = [Isa(), Pci(1)] };
            string original = JsonSerializer.Serialize(profile, IpcProtocol.JsonOptions);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                profile.Validate(); profile = JsonSerializer.Deserialize<MachineProfile>(JsonSerializer.Serialize(profile, IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
                Check(JsonSerializer.Serialize(profile, IpcProtocol.JsonOptions) == original);
            }
            Check(profile.NetworkAdapters[0].MacAddress == "02:44:4F:53:45:10" && profile.NetworkAdapters[1].MacAddress == "02:44:4F:53:45:11");
        });
        test("Network own saved PC retains both cards/modes/resources/MACs without media", SavedPc);
    }

    /// <summary>Writes and rereads only one new own definition; this never constructs a PC or opens a network backend.</summary>
    private static void SavedPc()
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string root = Path.Combine(parent, "dose-own-network-profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var profile = Board() with { NetworkAdapters = [Isa() with { Mode = "host-only", VirtualLan = "Own LAN" },
                Pci(1) with { Mode = "bridged", HostAdapter = "Own unconnected test ID" }] };
            var definition = new MachineDefinition { Name = "Own network configuration only", Hardware = profile };
            string path = Path.Combine(root, "own-network.dospc.json"); Check(!File.Exists(path));
            MachineDefinitionStore.Save(path, definition); var actual = MachineDefinitionStore.Load(path);
            Check(JsonSerializer.Serialize(actual.Hardware, IpcProtocol.JsonOptions) == JsonSerializer.Serialize(profile, IpcProtocol.JsonOptions));
            EqualProfiles(profile, actual.Hardware);
            Check(actual.HardDisks.Length == 0 && actual.Floppies.Length == 0 && actual.OpticalDiscs.Length == 0 && !actual.BuiltInTestDisk);
            Check(actual.Hardware.NetworkAdapters.All(adapter => !adapter.CableConnected));
        }
        finally
        {
            string full = Path.GetFullPath(root);
            Check(string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("dose-own-network-profile-", StringComparison.Ordinal),
                "Refuse cleaning outside the own profile-only fixture.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
