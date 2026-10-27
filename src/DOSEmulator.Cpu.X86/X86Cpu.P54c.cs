// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

/// <summary>Explicit core-development boundaries; the P54C subset is not a complete Pentium platform.</summary>
public enum X86CpuModel { Intel486Dx, IntelP54cB5Subset }

public sealed partial class X86Cpu
{
    /// <summary>Constructor-fixed instruction-set boundary, persisted as a checked snapshot invariant.</summary>
    public X86CpuModel Model { get; }

    /// <summary>Reports only implemented features; no host CPUID or unimplemented P54C facilities are exposed.</summary>
    private void IdentifyCpu()
    {
        if (Model == X86CpuModel.Intel486Dx) throw new CpuFaultException(6);
        uint leaf = GetRegister(Register.Ax);
        uint a = 0, b = 0, c = 0, d = 0;
        if (leaf == 0)
        {
            a = 1; b = 0x756e6547; d = 0x49656e69; c = 0x6c65746e;
        }
        else if (leaf == 1)
        {
            // P54C B5 signature. TSC, MSR and DE require this explicitly clock-bound development subset.
            a = 0x524; d = HasTimeStampCounter ? 0x135u : 0x101u;
        }
        // Unsupported leaves use an explicit all-zero DOSE policy, not a stepping-accuracy claim.
        SetRegister(Register.Ax, a); SetRegister(Register.Bx, b);
        SetRegister(Register.Cx, c); SetRegister(Register.Dx, d);
    }

    /// <summary>Performs a fixed eight-byte writable compare/exchange after checking the entire operand.</summary>
    private void CompareExchangeEightBytes()
    {
        if (Model == X86CpuModel.Intel486Dx) throw new CpuFaultException(6);
        byte modrm = FetchByte();
        if (((modrm >> 3) & 7) != 1 || modrm >= 0xc0) throw new CpuFaultException(6);
        ValidateLockOperand(modrm);
        Operand destination = DecodeOperand(modrm);
        CheckOperand(destination, 8, true);
        CheckAlignment(destination.Address, 8);
        // A failing comparison still writes the original destination back. Preflight BOTH pages
        // for write access before any RAM/MMIO data read, not merely the selected outcome.
        ProbeMemory(destination.Address, 8, true);
        uint lowCompare = GetRegister(Register.Ax), highCompare = GetRegister(Register.Dx);
        uint lowInput = GetRegister(Register.Bx), highInput = GetRegister(Register.Cx);
        uint low = ReadMemory(destination.Address, 4, writeIntent: true);
        uint highAddress = unchecked(destination.Address + 4);
        uint high = ReadMemory(highAddress, 4, writeIntent: true);
        bool equal = low == lowCompare && high == highCompare;
        WriteMemory(destination.Address, 4, equal ? lowInput : low);
        WriteMemory(highAddress, 4, equal ? highInput : high);
        if (!equal) { SetRegister(Register.Ax, low); SetRegister(Register.Dx, high); }
        SetFlag(Zero, equal);
    }
}
