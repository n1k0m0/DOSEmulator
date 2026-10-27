// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

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
            using PcMachine machine = MachineLaunch.Open(Value);
            string? acceptance = Value("--acceptance-test");
            string? storage = Value("--storage-acceptance");
            using var window = new DosWindow(machine, settings, settingsFile, storage ?? acceptance ?? Value("--smoke-test"),
                acceptance is not null, Value("--type-file") is { } typePath ? File.ReadAllText(typePath) : null, Value("--capture-after"),
                Value("--audio-out"), args.Contains("--audio-acceptance"), Value("--scenario") is { } scenarioPath ? GuestTestScenario.Load(scenarioPath) : null,
                args.Contains("--sb2-resource-acceptance"), args.Contains("--mouse-native-acceptance"), storage is not null);
            window.Run();
            return 0;
        }
        catch (Exception ex)
        {
            string errorPath = Path.Combine(AppContext.BaseDirectory, "startup-error.txt");
            try { File.WriteAllText(errorPath, ex.ToString()); } catch (IOException) { }
            MessageBox.Show(ex.Message, DOSEIdentity.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
