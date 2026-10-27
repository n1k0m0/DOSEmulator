// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Optional physical high/low levels of the five printer input pins; null leaves that external line unchanged.</summary>
public sealed record ParallelInput(bool? BusyHigh = null, bool? AckHigh = null, bool? PaperEndHigh = null,
    bool? SelectedHigh = null, bool? ErrorHigh = null);
