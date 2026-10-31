// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

public sealed partial class PcMachine
{
    private bool _externalCpuOwner;

    /// <summary>Reserves this device graph against software CPU steps while a separate engine runs it.</summary>
    internal void AcquireExternalCpuOwner()
    {
        if (_disposed || _executing || _externalCpuOwner)
            throw new InvalidOperationException("The PC has no free CPU owner boundary.");
        _externalCpuOwner = true;
    }

    internal void ReleaseExternalCpuOwner()
    {
        if (!_externalCpuOwner) throw new InvalidOperationException("The PC has no external CPU owner.");
        _externalCpuOwner = false;
    }
}
