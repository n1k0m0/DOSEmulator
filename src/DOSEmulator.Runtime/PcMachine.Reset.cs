// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public sealed partial class PcMachine
{
    /// <summary>Consumes physical reset work only between instructions, after completed OUT effects and before any new opcode.</summary>
    private void ServicePiixResetBoundary()
    {
        if (PiixReset is not { } reset) return;
        if (reset.ConsumeCpuRelease()) Cpu.ResetSignalReleased();
        var request = reset.ConsumePending();
        if (request != PiixResetRequest.None) ApplyPiixRequest(request, request == PiixResetRequest.Init ? "cf9-init" : "cf9-hard");
    }

    /// <summary>Applies distinct INIT or CPURST/PCIRST/RSTDRV effects without replacing the CPU, any powered peer, RAM, clock or medium lease.</summary>
    private void ApplyPiixRequest(PiixResetRequest request, string source)
    {
        LastWarmBootFlag = Memory.ReadWord(0x472);
        if (request == PiixResetRequest.Init) Cpu.Init();
        else
        {
            PiixReset!.BeginHardReset();
            Cpu.ResetSignal();
            // Integrate the previous signals to this instant before stopping their producers.
            Speaker.Write(0x61, 0, 1); Midi?.ApplyPiixHardReset();
            SoundBlaster16?.AssertPiixReset(Profile.SoundBlasterIrq, Profile.SoundBlasterDmaChannel, Profile.SoundBlasterDma16Channel);
            SoundBlaster?.AssertPiixReset(); Opl?.Reset(); Opl3?.Reset();
            Serial?.ApplyPiixHardReset(); Parallel?.ApplyPiixHardReset();
            Ata.AssertPiixReset(); SecondaryAta.AssertPiixReset();
            PiixIde!.ApplyPiixHardReset();
            Floppy.AssertPiixReset(); Keyboard.AssertPiixReset(); Board.ApplyPiixHardReset(); FpuError.ApplyPiixHardReset();
            Dma.ApplyPiixHardReset(); Pit.ApplyPiixHardReset();
            if (S3 is not null) S3.ApplyPiixHardReset(); else Video.ApplyPiixHardReset();
            HostBridge!.ResetConfiguration(); Pci!.ResetAddress(); PiixIrq!.ApplyPiixHardReset(); Pic.ApplyPiixHardReset();
            _keyboardResetWasAsserted = false; _resetPulseUntil = 0;
            // RTC/battery, external PS/2/gameport pins and already emitted output remain powered.
        }
        Fault = null; HardwareResetCount++; LastResetSource = source; LastResetTime = Scheduler.Now;
        HardwareResetOccurred?.Invoke();
    }

    /// <summary>Deasserts the own PCI/ISA domain at its scheduler deadline while CPURST remains held for a further nominal millisecond.</summary>
    public void ReleasePiixPciIsaReset()
    {
        if (!Scheduler.IsDispatching || PiixReset?.Phase != PiixResetPhase.CpuOnly || Scheduler.Now != PiixReset.PciIsaReleaseAt) throw new InvalidOperationException("PCI/ISA release requires its fixed reset sequence.");
        Ata.Write(0x3f6, 0, 1); SecondaryAta.Write(0x376, 0, 1);
        Keyboard.ReleasePiixReset(); Floppy.ReleasePiixReset();
        SoundBlaster?.ReleasePiixReset(); SoundBlaster16?.ReleasePiixReset();
    }
}
