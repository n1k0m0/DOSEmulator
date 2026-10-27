// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    private readonly bool _resourceAcceptance;
    private MachineStatus? _resourceGuestStatus;

    // Test mode changes only this freshly launched private acceptance machine.
    // UI actions use the same queued power-cycle path as normal menu input.
    private void AdvanceSoundResourceAcceptance()
    {
        var status = Volatile.Read(ref _status);
        if (_elapsed.ElapsedMilliseconds < _nextSmokeTime || _captureIndex >= 0) return;
        if (_smokeStage == 0 && status?.DebugOutput.Contains(_captureAfter ?? "ADC-DOS-RETURN-OK", StringComparison.Ordinal) != true) return;
        if (_smokeStage == 0 && (_scenario?.Complete == false || status?.DebugOutput.EndsWith("\r\nC:\\>", StringComparison.Ordinal) != true)) return;
        var initial = _resourceGuestStatus?.Configuration;
        int expectedBase = initial?.SoundBlasterBasePort == 0x220 ? 0x240 : 0x220;
        int[] irqs = [3, 5, 7, 9];
        int expectedIrq = initial is null ? 0 : irqs[(Array.IndexOf(irqs, initial.SoundBlasterIrq) + irqs.Length - 1) % irqs.Length];
        switch (_smokeStage)
        {
            case 0:
                if (status?.Configuration?.SoundCard != "sb2-opl2-development") throw new InvalidOperationException("Resource acceptance requires the SB2+OPL2 profile.");
                _resourceGuestStatus = status; _captureIndex = 0; break;
            case 1: OpenPage("MACHINE / HARDWARE"); _captureIndex = 1; break;
            case 2: _selected = 6; Activate(); _captureIndex = 2; break;
            case 3: _selected = 0; Activate(); break;
            case 4:
                if (status?.Configuration?.SoundBlasterBasePort != expectedBase || status.State != MachineRunState.Paused) return;
                _captureIndex = 3; break;
            case 5: _selected = 1; Activate(-1); break;
            case 6:
                if (status?.Configuration?.SoundBlasterIrq != expectedIrq || status.State != MachineRunState.Paused) return;
                _captureIndex = 4; break;
            case 7:
                bool passed = status is { Fault: null, State: MachineRunState.Paused }
                    && status.Configuration?.SoundBlasterBasePort == expectedBase && status.Configuration.SoundBlasterIrq == expectedIrq
                    && status.FloppyAMedia == _resourceGuestStatus!.FloppyAMedia && status.MediaName == _resourceGuestStatus.MediaName
                    && status.DiskWritable == _resourceGuestStatus.DiskWritable && _smokeCaptures.Count == 5 && _scenario?.Complete != false;
                File.WriteAllText(Path.Combine(_smokeDirectory!, "resource-result.json"), JsonSerializer.Serialize(new {
                    passed, guestStatus = _resourceGuestStatus, finalStatus = status, captures = _smokeCaptures,
                    baseChangedViaMenu = status?.Configuration?.SoundBlasterBasePort == expectedBase,
                    irqChangedViaMenu = status?.Configuration?.SoundBlasterIrq == expectedIrq,
                    scenario = _scenario is null ? null : new { _scenario.Complete, _scenario.InputBytesSent, _scenario.InputBytesTotal }
                }, IpcProtocol.JsonOptions));
                Close(); break;
        }
        _captureDelay = 2; _nextSmokeTime = _elapsed.ElapsedMilliseconds + 450; _smokeStage++;
    }
}
