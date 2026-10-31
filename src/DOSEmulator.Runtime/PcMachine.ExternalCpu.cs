// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

public sealed partial class PcMachine
{
    private bool _externalCpuOwner;
    private bool _externalCpuImage;
    /// <summary>Indicates an owner reservation, never an authoritative software-CPU register image.</summary>
    internal bool HasExternalCpuOwner => _externalCpuOwner;

    /// <summary>Prevents an independent native register file from being mistaken for the stale software CPU.</summary>
    internal void MarkExternalCpuImage() => _externalCpuImage = true;

    /// <summary>Requires a fresh power cycle after native ownership; disposing a VP does not migrate its registers.</summary>
    internal void RequireSoftwareCpu()
    {
        if (_externalCpuOwner || _externalCpuImage || Memory.HasExternalRam)
            throw new InvalidOperationException("This PC requires its external CPU engine or a fresh power cycle.");
    }

    /// <summary>Reserves this device graph against software CPU steps while a separate engine runs it.</summary>
    internal void AcquireExternalCpuOwner()
    {
        if (_disposed || _executing || _externalCpuOwner)
            throw new InvalidOperationException("The PC has no free CPU owner boundary.");
        _externalCpuOwner = true;
    }

    /// <summary>Releases only the live host reservation after the native engine has stopped.</summary>
    internal void ReleaseExternalCpuOwner()
    {
        if (!_externalCpuOwner) throw new InvalidOperationException("The PC has no external CPU owner.");
        _externalCpuOwner = false;
    }
}
