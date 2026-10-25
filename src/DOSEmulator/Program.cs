using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

namespace DOSEmulator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? Value(string name)
            {
                int index = Array.IndexOf(args, name);
                if (index < 0) return null;
                return index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {name} value.");
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
            MachineProfile profile = Value("--profile") is { } profilePath
                ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(profilePath), IpcProtocol.JsonOptions) ?? throw new ArgumentException("Empty profile.") : new();
            profile.Validate();
            PcMachine machine = MachineFactory.FromImages(profile, Value("--disk"), Value("--overlay"),
                Value("--floppy-a"), Value("--floppy-a-overlay"), Value("--floppy-b"), Value("--floppy-b-overlay"), Value("--boot"));
            string? acceptance = Value("--acceptance-test");
            using var window = new DosWindow(machine, settings, settingsFile, acceptance ?? Value("--smoke-test"),
                acceptance is not null, Value("--type-file") is { } typePath ? File.ReadAllText(typePath) : null, Value("--capture-after"),
                Value("--audio-out"), args.Contains("--audio-acceptance"), Value("--scenario") is { } scenarioPath ? GuestTestScenario.Load(scenarioPath) : null,
                args.Contains("--sb2-resource-acceptance"), args.Contains("--mouse-native-acceptance"));
            window.Run();
            return 0;
        }
        catch (Exception ex)
        {
            string errorPath = Path.Combine(AppContext.BaseDirectory, "startup-error.txt");
            try { File.WriteAllText(errorPath, ex.ToString()); } catch (IOException) { }
            MessageBox.Show(ex.Message, "DOSEmulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
