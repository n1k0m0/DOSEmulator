// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

namespace DOSEmulator;

internal static class Program
{
    /// <summary>Runs the executable entry point and reports startup/argument failures through its defined exit/error path.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Retrieves one explicitly named launch-option value or rejects a missing required argument.
            string? Value(string name)
            {
                int index = Array.IndexOf(args, name);
                if (index < 0) return null;
                return index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {name} value.");
            }
            if (Value("--gui-engine-check") is { } engineCheck) return GuiEngineCheck.Run(engineCheck);
            if (Value("--host-audio-check") is { } audioCheck) return HostAudioCheck.Run(audioCheck);
            if (Value("--storage-dialog-check") is { } dialogCheck)
            {
                Directory.CreateDirectory(dialogCheck);
                File.WriteAllText(Path.Combine(dialogCheck, "dialogs.json"), JsonSerializer.Serialize(StorageDialogs.CapturePreview(dialogCheck)));
                return 0;
            }
            string dataDirectory = Value("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DOSEmulator");
            Directory.CreateDirectory(dataDirectory);
            HostSettings settings = new();
            string settingsFile = Path.Combine(dataDirectory, "settings.json");
            if (File.Exists(settingsFile))
            {
                try { settings = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(settingsFile), IpcProtocol.JsonOptions) ?? new(); }
                catch (Exception ex) when (ex is JsonException or IOException) { /* Keep recoverable defaults. */ }
            }
            bool savedEngine = false;
            if (File.Exists(settingsFile))
            {
                try { using var saved = JsonDocument.Parse(File.ReadAllText(settingsFile)); savedEngine = saved.RootElement.TryGetProperty("cpuEngine", out _); }
                catch (Exception error) when (error is JsonException or IOException) { }
            }
            string engine = Value("--engine") ?? (savedEngine ? settings.CpuEngine : Value("--default-engine") ?? settings.CpuEngine);
            if (engine is not ("software" or "whpx")) throw new ArgumentException("--engine requires software or whpx.");
            settings = settings with { CpuEngine = engine };
            if (engine == "whpx" && new[] { "--acceptance-test", "--storage-acceptance", "--mouse-native-acceptance", "--audio-acceptance", "--sb2-resource-acceptance", "--scenario", "--type-file" }.Any(args.Contains))
                throw new ArgumentException("Legacy instruction-based acceptance modes require --engine software. Use --gui-engine-check for the native owner checks.");
            string? machinePath = Value("--machine");
            using PcMachine opened = Value("--remember-configuration") is { } rememberedDirectory
                ? MachineLaunch.OpenPersistent(Value, rememberedDirectory, out machinePath) : MachineLaunch.Open(Value);
            using PcMachine? upgraded = engine == "whpx" && !opened.Profile.IsPentium
                ? UpgradeStartupMachine(opened) : null;
            var machine = upgraded ?? opened;
            if (upgraded is not null) machinePath = null; // Retain the original 486 definition; only an explicit Save PC persists this native profile.
            if (engine == "whpx") new WhpxExecutionFactory().ValidateProfile(machine.Profile);
            string? acceptance = Value("--acceptance-test");
            string? storage = Value("--storage-acceptance");
            using var window = new DosWindow(machine, settings, settingsFile, storage ?? acceptance ?? Value("--smoke-test"),
                acceptance is not null, Value("--type-file") is { } typePath ? File.ReadAllText(typePath) : null, Value("--capture-after"),
                Value("--audio-out"), args.Contains("--audio-acceptance"), Value("--scenario") is { } scenarioPath ? GuestTestScenario.Load(scenarioPath) : null,
                args.Contains("--sb2-resource-acceptance"), args.Contains("--mouse-native-acceptance"), storage is not null, machinePath, engine, upgraded is null ? null : "WHPX: PENTIUM 90 MHZ / ORIGINAL 486 CONFIGURATION RETAINED", enableHostAudio: !args.Contains("--no-host-audio"));
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "dose-installed.txt")) &&
                !args.Any(arg => arg.Contains("acceptance", StringComparison.OrdinalIgnoreCase) || arg is "--smoke-test" or "--scenario"))
                Updates.StartupUpdateChecker.CheckForUpdatesOnStartup(window.RequestClose);
            window.Run();
            return 0;
        }
        catch (Exception ex)
        {
            string errorPath = Path.Combine(AppContext.BaseDirectory, "startup-error.txt");
            try { File.WriteAllText(errorPath, ex.ToString()); } catch (IOException) { }
            if (args.Contains("--gui-engine-check")) { Console.Error.WriteLine(ex); return 1; }
            MessageBox.Show(ex.Message, DOSEIdentity.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Prepares the native startup profile, then releases the old PC so its pending writable-media leases become active.</summary>
    internal static PcMachine UpgradeStartupMachine(PcMachine opened)
    {
        var replacement = opened.CreatePowerCycle(opened.Profile.WithDevelopmentCpu(true));
        try { opened.Dispose(); return replacement; }
        catch { replacement.Dispose(); throw; }
    }
}
