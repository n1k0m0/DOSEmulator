// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;

namespace DOSEmulator;

/// <summary>Opt-in output-only diagnostic with no window, microphone, MIDI device or native input.</summary>
internal static class HostAudioCheck
{
    /// <summary>Exercises repeated actual playback/stop/dispose and saves failures without a modal error dialog.</summary>
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory); var results = new List<object>(); bool passed = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var audio = new HostAudioOutput(); audio.SetLevels(false, 2);
            var samples = new float[9600];
            for (int i = 0; i < samples.Length; i += 2) samples[i] = samples[i + 1] = (float)Math.Sin(i * Math.PI * 220 / 48000);
            audio.Write(new AudioBlock(0, 48000, 2, samples));
            SpinWait.SpinUntil(() => audio.NonSilentFrames > 1000 || audio.Fault is not null, 3000);
            object before = audio.Snapshot(); audio.Dispose();
            bool accepted = audio.NonSilentFrames > 1000 && audio.ShutdownFault is null && audio.Fault is null;
            passed &= accepted; results.Add(new { attempt, accepted, before, after = audio.Snapshot() });
        }
        string path = Path.Combine(directory, "host-audio-check.json");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, new { passed, outputOnly = true, nativeInputSent = false, results });
        return passed ? 0 : 1;
    }
}
