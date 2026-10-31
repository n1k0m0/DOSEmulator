// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;
public sealed partial class X86Cpu
{
    /// <summary>Commits equal multiply carry/overflow results once, preserving every other flag.</summary>
    private void SetCarryOverflow(bool value) => Eflags = value ? Eflags | (Carry | Overflow) : Eflags & ~(Carry | Overflow);
}
