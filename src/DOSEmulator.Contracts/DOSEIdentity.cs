// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Official product identity shared by the host UI, CLI and default machine profiles.</summary>
public static class DOSEIdentity
{
    /// <summary>Official short product name.</summary>
    public const string Name = "DOSE";
    /// <summary>Expansion of the official acronym.</summary>
    public const string Expansion = "DosEmulator";
    /// <summary>Complete name for window titles and initial command-line help.</summary>
    public const string DisplayName = Name + " (" + Expansion + ")";
    /// <summary>Canonical identifier of the own development VGA model.</summary>
    public const string VgaModel = "dose-vga-development";
}
