// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;

/// <summary>Checks real compiled settings policies and atomic PC persistence without constructing a native window or opening user media.</summary>
internal static class SettingsRevisionTests
{
    /// <summary>Fails one concrete settings contract without suppressing a product failure.</summary>
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>Loads the actual compiled GUI under the existing native-resolution guard and supplies its contracts type.</summary>
    private static void Gui(Action<Assembly, Type> action)
    {
        Type binding = typeof(HostNativeRepeatTests).GetNestedType("UiTypes", BindingFlags.NonPublic)!;
        object metadata = Activator.CreateInstance(binding)!;
        var context = (AssemblyLoadContext)binding.GetProperty("Context")!.GetValue(metadata)!;
        // Applies the same guard to dependencies explicitly loaded for pure menu policy.
        Assembly Load(string path) => (Assembly)context.GetType().GetMethod("LoadGuarded")!.Invoke(context, [path])!;
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "net10.0-windows"));
        var gui = Load(Path.Combine(directory, "DOSEmulator.dll"));
        var contracts = Load(Path.Combine(directory, "DOSEmulator.Contracts.dll"));
        action(gui, contracts.GetType("DOSEmulator.Contracts.MachineProfile", true)!);
        Check((int)context.GetType().GetProperty("NativeAttempts")!.GetValue(context)! == 0, "Settings policy attempted native code or a host window.");
    }

    /// <summary>Creates only a new bounded temporary test directory; no accepted or user PC is read.</summary>
    private static void Private(Action<string> action)
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dose-settings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            Check(Path.GetDirectoryName(directory) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(directory).StartsWith("dose-settings-", StringComparison.Ordinal), "Test cleanup escaped its own temporary directory.");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Runs one actual owner command and returns its normal success/error response.</summary>
    private static IpcResponse Command(MachineSession session, string command, object payload)
    {
        var task = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return task.GetAwaiter().GetResult();
    }

    /// <summary>Registers admission, layout, edit and persistence cases with no native input or network endpoint.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int countValue in new[] { 1, 8, 10, 11, 13, 17, 31 })
        {
            int count = countValue;
            test($"Settings visible paging and mouse rows count={count}", () => Gui((gui, _) =>
            {
                Type layout = gui.GetType("DOSEmulator.SettingsMenuLayout", true)!;
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                var first = layout.GetMethod("First", flags)!; var hit = layout.GetMethod("Hit", flags)!;
                for (int selected = 0; selected < count; selected++)
                {
                    int top = (int)first.Invoke(null, [count, selected])!;
                    Check(top <= selected && selected < top + 10, "Selected hardware/network row is outside the visible viewport.");
                    for (int row = 0; row < Math.Min(10, count - top); row++)
                        Check((int)hit.Invoke(null, [count, selected, 100, 186 + row * 33])! == top + row, "Mouse selected an offscreen or different setting.");
                    Check((int)hit.Invoke(null, [count, selected, 39, 186])! == -1 && (int)hit.Invoke(null, [count, selected, 100, 540])! == -1, "Title/footer/outside click changed a setting.");
                }
            }));
        }
        foreach (int hzValue in new[] { 30, 60, 120 })
        {
            int hz = hzValue;
            test($"Settings host presentation cadence hz={hz}", () => Gui((gui, _) =>
            {
                Type type = gui.GetType("DOSEmulator.PresentationCadence", true)!;
                object cadence = Activator.CreateInstance(type, true)!;
                var due = type.GetMethod("Due", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Check((bool)due.Invoke(cadence, [0UL, hz, false])!, "First image was not admitted.");
                Check(!(bool)due.Invoke(cadence, [1UL, hz, false])!, "Redundant sub-frame capture was admitted.");
                ulong at = 1_000_000_000UL / (uint)hz;
                Check((bool)due.Invoke(cadence, [at, hz, false])!, "A due image was suppressed.");
                Check((bool)due.Invoke(cadence, [at + 1, hz, true])!, "A new machine did not force its initial image.");
                Check(!(bool)due.Invoke(cadence, [at + 2, hz, false])!, "Forced image generated a catch-up queue.");
                Check((bool)due.Invoke(cadence, [ulong.MaxValue, hz, false])!, "Host deadline overflow failed.");
            }));
        }
        test("Settings draft stages and validates one complete original owner revision", () => Gui((gui, profileType) =>
        {
            Type type = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object original = Activator.CreateInstance(profileType)!;
            object draft = Activator.CreateInstance(type, flags, null, [original, 7L], null)!;
            var requested = type.GetProperty("Requested", flags)!; var changed = type.GetProperty("HasChanges", flags)!;
            object edited = JsonSerializer.Deserialize(JsonSerializer.Serialize(new MachineProfile { RamMiB = 16 }), profileType)!;
            type.GetMethod("Stage", flags)!.Invoke(draft, [edited]);
            Check((bool)changed.GetValue(draft)! && (int)profileType.GetProperty("RamMiB")!.GetValue(original)! == 8, "Draft mutated live original RAM.");
            _ = type.GetMethod("Plan", flags)!.Invoke(draft, [7L]);
            try { type.GetMethod("Plan", flags)!.Invoke(draft, [8L]); throw new InvalidOperationException("Stale draft accepted."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
            type.GetMethod("Stage", flags)!.Invoke(draft, [original]);
            Check(!(bool)changed.GetValue(draft)! && requested.GetValue(draft) == original, "Discard-to-original remained dirty.");
        }));
        test("Settings real Windows CPU choices preserve RAM and devices", () => Gui((gui, profileType) =>
        {
            Type type = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!;
            var cycle = type.GetMethod("CycleCpu", BindingFlags.Static | BindingFlags.NonPublic)!;
            var own = new MachineProfile { Id = MachineProfile.Windows95ProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000,
                RamMiB = 64, VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId };
            object initial = JsonSerializer.Deserialize(JsonSerializer.Serialize(own), profileType)!;
            object fast = cycle.Invoke(null, [initial, 1])!;
            using var f = JsonDocument.Parse(JsonSerializer.Serialize(fast, profileType));
            Check(f.RootElement.GetProperty("CpuHz").GetInt32() == 133_000_000 && f.RootElement.GetProperty("RamMiB").GetInt32() == 64, "CPU choice changed RAM or used an unsupported clock.");
            object slow = cycle.Invoke(null, [fast, -1])!;
            Check(JsonSerializer.Serialize(slow, profileType) == JsonSerializer.Serialize(initial, profileType), "Windows clock round trip changed devices.");
        }));
        test("Settings actual compiled hardware/network pages expose every edit and Apply action", () => Gui((gui, profileType) =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = gui.GetType("DOSEmulator.DosWindow", true)!;
            object window = RuntimeHelpers.GetUninitializedObject(type); GC.SuppressFinalize(window);
            type.GetField("_networkRefreshAt", flags)!.SetValue(window, long.MaxValue);
            var own = new MachineProfile { SoundCard = "sb16-ct1740-dsp405-development",
                NetworkAdapters = [new() { MacAddress = "02:44:4F:53:47:01" }] };
            object profile = JsonSerializer.Deserialize(JsonSerializer.Serialize(own), profileType)!;
            Type draftType = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!;
            object draft = Activator.CreateInstance(draftType, flags, null, [profile, 1L], null)!;
            type.GetField("_configurationDraft", flags)!.SetValue(window, draft);
            foreach (string page in new[] { "MACHINE / HARDWARE", "CPU / PLATFORM", "SOUND CARD RESOURCES", "NETWORK", "NETWORK / ADAPTER 1", "NETWORK HELP" })
            {
                type.GetField("_page", flags)!.SetValue(window, page);
                type.GetMethod("RebuildItems", flags)!.Invoke(window, null);
                string[] items = (string[])type.GetField("_items", flags)!.GetValue(window)!;
                Check(items.Length > 0 && items[^1] == "BACK", "Actual settings page has an unreachable or missing Back row: " + page);
                if (page == "MACHINE / HARDWARE") Check(items.Length == 17 && items[14].Contains("APPLY", StringComparison.Ordinal), "Hardware Apply action is missing.");
                if (page == "NETWORK / ADAPTER 1") Check(items.Length == 13 && items[3].Contains("EDIT", StringComparison.Ordinal) &&
                    items[7].Contains("EDIT", StringComparison.Ordinal) && items[10].Contains("APPLY", StringComparison.Ordinal), "Network fields remain read-only or lack Apply.");
            }
        }));
        test("Settings typed MAC normalization rejects multicast/global/partial identities", () => Gui((gui, _) =>
        {
            var method = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!.GetMethod("NormalizeMac", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((string)method.Invoke(null, [" 02-44-4f-53-45-ab "])! == "02:44:4F:53:45:AB", "Typed MAC normalization.");
            foreach (string bad in new[] { "03:44:4F:53:45:01", "00:44:4F:53:45:01", "02:44:4F", "02 44 4F 53 45 01", "02:44:4F:53:45:0Z" })
            {
                try { method.Invoke(null, [bad]); throw new InvalidOperationException("Invalid MAC accepted: " + bad); }
                catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
            }
        }));
        test("Settings platform presets produce valid complete CPU/video/CD/RAM tuples", () => Gui((gui, profileType) =>
        {
            var cycle = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!.GetMethod("CyclePlatform", BindingFlags.Static | BindingFlags.NonPublic)!;
            object profile = Activator.CreateInstance(profileType)!;
            foreach (string id in new[] { MachineProfile.P54cProfileId, MachineProfile.Windows95ProfileId, MachineProfile.Windows95Profile133Id, "virtual486-dev-v1" })
            {
                profile = cycle.Invoke(null, [profile, 1])!;
                profileType.GetMethod("Validate")!.Invoke(profile, null);
                Check((string)profileType.GetProperty("Id")!.GetValue(profile)! == id, "Platform selection used an unsupported or incomplete board tuple.");
            }
        }));
        test("Settings ISA platform selection retains explicit PCI-card removal requirement", () => Gui((gui, profileType) =>
        {
            var own = new MachineProfile { Id = MachineProfile.Windows95ProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000,
                RamMiB = 64, VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId,
                NetworkAdapters = [new() { Model = NetworkAdapterProfile.Rtl8029Pci, MacAddress = "02:44:4F:53:47:01", IoBase = 0x6000, Irq = 11 }] };
            object original = JsonSerializer.Deserialize(JsonSerializer.Serialize(own), profileType)!;
            var cycle = gui.GetType("DOSEmulator.MachineSettingsDraft", true)!.GetMethod("CyclePlatform", BindingFlags.Static | BindingFlags.NonPublic)!;
            try { cycle.Invoke(null, [original, -1]); throw new InvalidOperationException("ISA board silently removed a PCI NIC."); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
            Check(JsonSerializer.Serialize(original, profileType) == JsonSerializer.Serialize(own), "Rejected board change mutated the original NIC.");
        }));
        test("Settings bound PC persists hardware after one normal owner restart", () => Private(directory =>
        {
            string path = Path.Combine(directory, "own.dospc.json");
            var definition = new MachineDefinition { BuiltInTestDisk = true };
            MachineDefinitionStore.Save(path, definition);
            using var session = new MachineSession(MachineFactory.FromDefinition(definition), definitionPath: path);
            var requested = session.Machine.Profile with { RamMiB = 16, Mouse = "ps2-two-button" };
            var response = Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, requested, true));
            Check(response.Success && session.Machine.Profile == requested && session.DefinitionPath == path, "Bound owner did not apply hardware.");
            var saved = MachineDefinitionStore.Load(path);
            Check(saved.Id == definition.Id && saved.Hardware == requested && saved.BuiltInTestDisk, "Restarted hardware was lost on disk.");
            string before = File.ReadAllText(path);
            Check(!Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, requested with { RamMiB = 3 }, true)).Success &&
                File.ReadAllText(path) == before, "Invalid hardware changed the PC file.");
        }));
        test("Settings auto-save reports concurrent PC edits without clobbering them", () => Private(directory =>
        {
            string path = Path.Combine(directory, "own.dospc.json"); var definition = new MachineDefinition { BuiltInTestDisk = true };
            MachineDefinitionStore.Save(path, definition);
            using var session = new MachineSession(MachineFactory.FromDefinition(definition), definitionPath: path);
            MachineDefinitionStore.Save(path, definition with { Name = "External edit" }); string before = File.ReadAllText(path);
            var response = Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, definition.Hardware with { RamMiB = 16 }, true));
            Check(!response.Success && response.ErrorCode == "ConfigurationAppliedNotSaved" && session.Machine.Profile.RamMiB == 16 &&
                File.ReadAllText(path) == before, "Concurrent edit was overwritten or a committed VM change was hidden.");
        }));
        test("Settings explicit Save As binds later changes to the selected PC only", () => Private(directory =>
        {
            using var session = new MachineSession(MachineFactory.FromDefinition(new MachineDefinition { BuiltInTestDisk = true }));
            string path = Path.Combine(directory, "own.dospc.json");
            Check(Command(session, "SaveMachineDefinition", new { path, name = "Own settings PC" }).Success, "Save As failed.");
            var requested = session.Machine.Profile with { RamMiB = 16 };
            Check(Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, requested, true)).Success &&
                MachineDefinitionStore.Load(path).Hardware.RamMiB == 16, "Save As did not retain future changes.");
        }));
        test("Settings legacy launch remembers hardware without reopening the default configuration", () => Private(directory =>
        {
            // Supply no disk to use only the original own built-in fixture.
            string? Value(string name) => null;
            string? path;
            using (var session = new MachineSession(MachineLaunch.OpenPersistent(Value, directory, out path), definitionPath: path))
            {
                var requested = session.Machine.Profile with { RamMiB = 16, Mouse = "ps2-two-button" };
                Check(Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, requested, true)).Success, "Legacy settings did not apply.");
            }
            using var restored = MachineLaunch.OpenPersistent(Value, directory, out var same);
            Check(path == same && restored.Profile.RamMiB == 16 && restored.Profile.Mouse == "ps2-two-button", "Legacy launcher lost its saved machine settings.");
        }));
        foreach (string commandValue in new[] { "PowerCycleDemo", "PowerCycleReadOnlyDisk", "PowerCycleOverlayDisk" })
        {
            string command = commandValue;
            test("Settings temporary boot detaches original PC auto-save " + command, () => Private(directory =>
            {
                string path = Path.Combine(directory, "original.dospc.json");
                var original = new MachineDefinition { BuiltInTestDisk = true };
                MachineDefinitionStore.Save(path, original); string before = File.ReadAllText(path);
                string image = Path.Combine(directory, "own.img"); File.WriteAllBytes(image, new byte[512 * 32]);
                using var session = new MachineSession(MachineFactory.FromDefinition(original), definitionPath: path);
                Check(Command(session, command, new { path = image, overlayPath = Path.Combine(directory, "own.cow") }).Success &&
                    session.DefinitionPath is null, "Temporary boot retained the original PC destination.");
                var requested = session.Machine.Profile with { RamMiB = 16 };
                Check(Command(session, "CommitConfigurationChange", new ConfigurationPlan(session.Revision, requested, true)).Success &&
                    File.ReadAllText(path) == before && MachineDefinitionStore.Load(path).Hardware.RamMiB == 8,
                    "Editing the temporary boot replaced the original PC configuration.");
            }));
        }
    }
}
