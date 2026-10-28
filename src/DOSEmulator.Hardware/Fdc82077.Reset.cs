// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Fdc82077
{
    /// <summary>Aborts old controller commands/seeks on RSTDRV without changing physical cylinders, medium leases or committed sectors.</summary>
    public void AssertPiixReset()
    {
        _dor = 0; _locked = false; Reset(false);
        _rate = _tape = _specify1 = _specify2 = _configure = _precomp = 0;
        _expected = _drive = _c = _h = _r = _eot = 0; _write = _multiTrack = false;
        Array.Clear(_command); Array.Clear(_sector); Array.Clear(_motorReadyAt);
    }
    /// <summary>Releases the own reset input through the real delayed four-drive reset-result path.</summary>
    public void ReleasePiixReset() { _dor = 0x0c; Reset(true); }
}
