// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Deterministic acceptance input: actual VGA text gates KBC bytes and safe-point media changes.
/// Does not invoke guest OS/BIOS services or edit guest memory/files.</summary>
public sealed class GuestTestScenario
{
    public sealed record FloppyInsertion(int Drive, string Path, string? OverlayPath = null);
    public sealed record MouseMotion(int X, int Y, int Buttons);
    public sealed record Step(string WaitForScreen = "", string? Text = null, int[]? ScanCodes = null,
        FloppyInsertion? InsertFloppy = null, ulong SettleNanoseconds = 100_000_000, MouseMotion? Mouse = null, string? WaitForDebug = null,
        long MinimumResetCount = 0, long MinimumPost10Count = 0, GameportInput? Gameport = null, SerialInput? Serial = null,
        ParallelInput? Parallel = null, MidiInput? Midi = null);
    private readonly Step[] _steps;
    private readonly byte[][] _keys;
    private int _keyIndex;
    private bool _started;
    private string? _candidateScreen;
    private ulong _candidateAt;
    /// <summary>Diagnostic count of completed steps.</summary>
    public int CompletedSteps { get; private set; }
    /// <summary>Diagnostic count of total steps.</summary>
    public int TotalSteps => _steps.Length;
    /// <summary>Diagnostic count of input bytes sent.</summary>
    public int InputBytesSent { get; private set; }
    /// <summary>Diagnostic count of input bytes total.</summary>
    public int InputBytesTotal => _keys.Sum(bytes => bytes.Length);
    /// <summary>Diagnostic count of media changes.</summary>
    public int MediaChanges { get; private set; }
    /// <summary>Diagnostic count of mouse inputs sent.</summary>
    public int MouseInputsSent { get; private set; }
    /// <summary>External analog resistance/button replacements, not guest port triggers or register injection.</summary>
    public int GameportInputsSent { get; private set; }
    /// <summary>Explicit framed-character and modem-pin input requests delivered to the virtual COM adapter.</summary>
    public int SerialInputsSent { get; private set; }
    /// <summary>Explicit replacements of outer printer status pins, not adapter registers or guest data.</summary>
    public int ParallelInputsSent { get; private set; }
    /// <summary>Whole outer MIDI packets queued through the virtual serial transport.</summary>
    public int MidiInputsSent { get; private set; }
    /// <summary>Whether every scenario step has completed its actual guest input/media conditions.</summary>
    public bool Complete => CompletedSteps == TotalSteps;
    /// <summary>Initializes GuestTestScenario and connects the supplied dependencies before the component is used.</summary>
    public GuestTestScenario(Step[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Any(step => step is null)) throw new ArgumentException("Scenario steps cannot be null.");
        if (steps.Length is < 1 or > 64) throw new ArgumentException("Scenario requires 1..64 steps.");
        // Own the arrays: callers cannot change a validated scenario while it runs.
        _steps = steps.Select(step => step with { ScanCodes = step.ScanCodes?.ToArray(), Serial = step.Serial?.ValidatedCopy(), Midi = step.Midi?.ValidatedCopy() }).ToArray();
        _keys = _steps.Select(step =>
        {
            if ((string.IsNullOrWhiteSpace(step.WaitForScreen) == string.IsNullOrWhiteSpace(step.WaitForDebug)) ||
                step.WaitForScreen?.Length > 1000 || step.WaitForDebug?.Length > 1000)
                throw new ArgumentException("Each scenario step requires either actual VGA text or a guest debug-port marker.");
            if (step.SettleNanoseconds > 5_000_000_000) throw new ArgumentException("Screen settling must be at most five virtual seconds.");
            if (step.MinimumResetCount is < 0 or > 1_000_000) throw new ArgumentException("Invalid minimum hardware reset count.");
            if (step.MinimumPost10Count is < 0 or > 1_000_000) throw new ArgumentException("Invalid minimum POST count.");
            if (step.Text is not null && step.ScanCodes is not null) throw new ArgumentException("Choose text or scanCodes.");
            if (step.InsertFloppy is { } media && (media.Drive is < 0 or > 1 || !System.IO.Path.IsPathFullyQualified(media.Path) ||
                (media.OverlayPath is not null && !System.IO.Path.IsPathFullyQualified(media.OverlayPath))))
                throw new ArgumentException("Scenario media require drive 0/1 and absolute image/overlay paths.");
            if (step.ScanCodes?.Any(value => value is < 0 or > 255) == true) throw new ArgumentException("Invalid set-1 byte.");
            if (step.Mouse is { } mouse && (mouse.X is < -32767 or > 32767 || mouse.Y is < -32767 or > 32767 || (uint)mouse.Buttons > 3))
                throw new ArgumentException("Invalid relative two-button mouse input.");
            step.Gameport?.Validate();
            byte[] keys = step.ScanCodes?.Select(value => (byte)value).ToArray() ?? GuestKeyboard.Encode(step.Text ?? "");
            if (keys.Length > 8192) throw new ArgumentException("Scenario input is too long.");
            return keys;
        }).ToArray();
    }
    /// <summary>Loads and validates an explicit guest-input/media scenario before execution.</summary>
    public static GuestTestScenario Load(string path) => new(JsonSerializer.Deserialize<Step[]>(File.ReadAllText(path),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Empty scenario."));
    /// <summary>Call only on the emulation owner, between RunSteps batches.</summary>
    public void Pump(PcMachine machine)
    {
        if (Complete) return;
        Step step = _steps[CompletedSteps];
        if (machine.HardwareResetCount < step.MinimumResetCount || machine.Board.Post10Count < step.MinimumPost10Count) { _candidateScreen = null; return; }
        if (!_started)
        {
            string screen = step.WaitForDebug is null ? machine.Video.ReadText() : machine.Board.DebugText;
            if (!screen.Contains(step.WaitForDebug ?? step.WaitForScreen, StringComparison.Ordinal)) { _candidateScreen = null; return; }
            if (screen != _candidateScreen) { _candidateScreen = screen; _candidateAt = machine.Scheduler.Now; }
            // DOS installers may draw a prompt before flushing type-ahead. Wait
            // for the actual screen to settle, measured only in virtual time.
            if (machine.Scheduler.Now - _candidateAt < step.SettleNanoseconds) return;
            if (step.InsertFloppy is { } media)
            {
                if (machine.Floppy.Busy) return;
                IReadOnlyBlockDevice disk = media.OverlayPath is null ? new ReadOnlyRawDisk(media.Path) : new CopyOnWriteDisk(media.Path, media.OverlayPath);
                try { machine.Floppy.ReplaceMedia(media.Drive, disk); }
                catch { disk.Dispose(); throw; }
                MediaChanges++;
            }
            if (step.Mouse is { } mouse)
            {
                if (machine.Mouse is null) throw new InvalidOperationException("Scenario requires a configured PS/2 mouse.");
                machine.Mouse.Inject(mouse.X, mouse.Y, mouse.Buttons); MouseInputsSent++;
            }
            if (step.Gameport is { } input)
            {
                if (machine.Gameport is null) throw new InvalidOperationException("Scenario requires a configured analog gameport.");
                machine.Gameport.SetInput(input); GameportInputsSent++;
            }
            if (step.Serial is { } serial) { machine.SendSerialInput(serial); SerialInputsSent++; }
            if (step.Midi is { } midi)
            {
                (machine.Midi ?? throw new InvalidOperationException("No MPU-UART configured.")).QueueReceive(midi); MidiInputsSent++;
            }
            if (step.Parallel is { } parallel)
            {
                (machine.Parallel ?? throw new InvalidOperationException("No LPT1 adapter configured.")).SetInput(parallel);
                ParallelInputsSent++;
            }
            _started = true;
        }
        byte[] keys = _keys[CompletedSteps];
        if (_keyIndex < keys.Length && GuestKeyboard.TryInjectForBios(machine, keys[_keyIndex])) { _keyIndex++; InputBytesSent++; }
        if (_keyIndex == keys.Length) { CompletedSteps++; _keyIndex = 0; _started = false; _candidateScreen = null; }
    }
}
