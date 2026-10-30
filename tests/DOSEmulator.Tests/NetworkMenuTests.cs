// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

/// <summary>Exercises actual compiled network menu methods without a window constructor, host input or graphics context.</summary>
internal static class NetworkMenuTests
{
    /// <summary>Requires real model selection, persistent identity and live cable status, including a disconnected restored owner.</summary>
    public static void AddTo(Action<string, Action> test) => test("Network compiled menu preserves MAC and displays actual restored cable", () =>
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        Type binding = typeof(HostNativeRepeatTests).GetNestedType("UiTypes", BindingFlags.NonPublic)!;
        object metadata = Activator.CreateInstance(binding)!;
        object guarded = binding.GetProperty("Context")!.GetValue(metadata)!;
        var context = (AssemblyLoadContext)guarded;
        // Install the same GUI native-resolution guard on every explicitly loaded assembly.
        Assembly Load(string path) => (Assembly)guarded.GetType().GetMethod("LoadGuarded")!.Invoke(guarded, [path])!;
        context.Resolving += (_, name) =>
        {
            if (name.Name != "System.Private.Windows.Core") return null;
            string runtime = Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            string path = Path.GetFullPath(Path.Combine(runtime, "..", "..", "Microsoft.WindowsDesktop.App", Path.GetFileName(runtime), name.Name + ".dll"));
            return Load(path);
        };
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "net10.0-windows"));
        var gui = Load(Path.Combine(directory, "DOSEmulator.dll"));
        var contracts = Load(Path.Combine(directory, "DOSEmulator.Contracts.dll"));
        Type profileType = contracts.GetType("DOSEmulator.Contracts.MachineProfile", true)!;
        object profile = Activator.CreateInstance(profileType)!;
        Type windowType = gui.GetType("DOSEmulator.DosWindow", true)!;
        object window = RuntimeHelpers.GetUninitializedObject(windowType);
        GC.SuppressFinalize(window);
        // Fail on an actual compiled-menu mismatch instead of substituting copied policy.
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        // Execute the real private model-selection method on a managed-only receiver.
        object Model(object previous, int slot, int delta) => windowType.GetMethod("WithNetworkModel", statics)!.Invoke(null, [previous, slot, delta])!;
        // Read the actual compiled profile's public persisted values across load contexts.
        string Json(object value) => JsonSerializer.Serialize(value, profileType);
        object first = Model(profile, 0, 1);
        using var one = JsonDocument.Parse(Json(first));
        string mac = one.RootElement.GetProperty("NetworkAdapters")[0].GetProperty("MacAddress").GetString()!;
        Check(one.RootElement.GetProperty("NetworkAdapters")[0].GetProperty("Model").GetString() == "ne2000-isa", "Actual ISA model selection");
        object two = Model(first, 1, 1);
        using var both = JsonDocument.Parse(Json(two));
        var values = both.RootElement.GetProperty("NetworkAdapters");
        Check(values.GetArrayLength() == 2 && values[0].GetProperty("MacAddress").GetString() == mac && values[1].GetProperty("MacAddress").GetString() != mac, "Persistent distinct per-card MACs");
        var menu = windowType.GetMethod("NetworkAdapterItems", instance)!;
        // Supply one immutable actual-owner status, including a deliberately stale epoch.
        void Status(long generation, bool connected)
        {
            var status = JsonSerializer.SerializeToElement(new { bridgeAvailable = false, adapters = new[] { new { slot = 0, connected, description = "Own controlled peer" } } });
            Type snapshot = windowType.GetNestedType("NetworkMenuSnapshot", BindingFlags.NonPublic)!;
            windowType.GetField("_networkMenu", instance)!.SetValue(window, Activator.CreateInstance(snapshot, [generation, status]));
        }
        windowType.GetField("_uiInputGeneration", instance)!.SetValue(window, 5L);
        // The saved preference can be ON while a strict restore deliberately has no external connection.
        string saved = Json(first).Replace("\"CableConnected\":false", "\"CableConnected\":true", StringComparison.Ordinal);
        object restored = JsonSerializer.Deserialize(saved, profileType)!;
        Status(5, false);
        Check(((string[])menu.Invoke(window, [restored, 0])!)[2] == "CABLE: OFF", "Restored live cable is OFF despite saved ON preference");
        Status(5, true);
        Check(((string[])menu.Invoke(window, [restored, 0])!)[2] == "CABLE: ON", "Connected owner cable is ON");
        Status(4, true);
        Check(((string[])menu.Invoke(window, [restored, 0])!)[2] == "CABLE: OFF", "Old owner's connection cannot leak into new menu");
        Check((int)guarded.GetType().GetProperty("NativeAttempts")!.GetValue(guarded)! == 0, "No native resolution by the guarded GUI assemblies or window construction");
    });
}
