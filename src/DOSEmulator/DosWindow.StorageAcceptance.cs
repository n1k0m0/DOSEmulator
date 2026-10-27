// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    private readonly bool _storageAcceptance;
    private Task<IpcResponse>? _storageCommand;
    private MachineDefinition? _storageSaved;
    private object? _storageDialogProof;

    /// <summary>Exercises the public session queue and actual storage overlays internally; never sends Windows keyboard/mouse input.</summary>
    private void AdvanceStorageAcceptance()
    {
        if (_elapsed.ElapsedMilliseconds < _nextSmokeTime || _captureIndex >= 0) return;
        if (_elapsed.ElapsedMilliseconds > 35000) { FinishStorageAcceptance("Storage GUI acceptance timed out."); return; }
        if (_storageCommand is not null && !_storageCommand.IsCompleted) return;
        try
        {
            if (_storageCommand is not null && !_storageCommand.GetAwaiter().GetResult().Success)
                throw new InvalidOperationException("Storage command failed: " + JsonSerializer.Serialize(_storageCommand.Result));
            _storageCommand = null;
            string path = Path.Combine(_smokeDirectory!, "saved-pc.dospc.json");
            switch (_smokeStage)
            {
                case 0:
                    if (Volatile.Read(ref _status)?.DebugOutput.Contains("C:\\>", StringComparison.Ordinal) != true) return;
                    if (Volatile.Read(ref _status)?.HardDisks?.Count != 4) throw new InvalidOperationException("Expected four initial GUI disks.");
                    _captureIndex = 0; break;
                case 1: OpenPage("HARD DISKS"); _captureIndex = 1; break;
                case 2: OpenPage("IDE 3"); _captureIndex = 2; break;
                case 3: OpenPage("SAVED MACHINES"); _captureIndex = 3; break;
                case 4:
                    _storageDialogProof = StorageDialogs.CapturePreview(_smokeDirectory!);
                    _storageCommand = _session.Enqueue(IpcProtocol.CreateRequest("SaveMachineDefinition", new { path, name = "Storage review PC" })); break;
                case 5:
                    _storageSaved = MachineDefinitionStore.Load(path);
                    if (_storageSaved.Name != "Storage review PC" || _storageSaved.HardDisks.Length != 4) throw new InvalidOperationException("Saved GUI PC is incomplete.");
                    _storageCommand = _session.Enqueue(IpcProtocol.CreateRequest("SetHardDisk", new { slot = 1 })); break;
                case 6:
                    if (Volatile.Read(ref _status)?.HardDisks?.Count != 3) return;
                    OpenPage("HARD DISKS"); _captureIndex = 4; break;
                case 7:
                    _storageCommand = _session.Enqueue(IpcProtocol.CreateRequest("LoadMachineDefinition", new { path })); break;
                case 8:
                    if (Volatile.Read(ref _status)?.HardDisks?.Count != 4) return;
                    lock (_gate)
                    {
                        var restored = _session.Machine.CaptureDefinition();
                        if (restored.Id != _storageSaved!.Id || restored.Name != _storageSaved.Name || !restored.HardDisks.SequenceEqual(_storageSaved.HardDisks))
                            throw new InvalidOperationException("GUI cold load changed the saved PC bindings/identity.");
                    }
                    OpenPage("HARD DISKS"); _captureIndex = 5; break;
                default: FinishStorageAcceptance(null); return;
            }
            _captureDelay = 2; _nextSmokeTime = _elapsed.ElapsedMilliseconds + 450; _smokeStage++;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
        { FinishStorageAcceptance(ex.Message); }
    }

    /// <summary>Writes private GUI evidence and requests normal shutdown, keeping native input and running-state snapshots explicitly unclaimed.</summary>
    private void FinishStorageAcceptance(string? failure)
    {
        File.WriteAllText(Path.Combine(_smokeDirectory!, "storage-result.json"), JsonSerializer.Serialize(new
        {
            passed = failure is null && _smokeCaptures.Count == 6, failure, status = Volatile.Read(ref _status),
            captures = _smokeCaptures, dialogs = _storageDialogProof, savedDefinition = _storageSaved,
            nativeInputSent = false, runningStateSnapshotImplemented = false, audio = _audio?.Snapshot()
        }, IpcProtocol.JsonOptions));
        Close();
    }
}
