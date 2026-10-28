// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Looks up a descriptor for query instructions while converting eligible selector errors into a failed query.</summary>
    private bool TryQueryDescriptor(ushort selector, out Descriptor descriptor)
    {
        descriptor = default;
        if ((selector & 0xfffc) == 0) return false;
        try { descriptor = ReadDescriptor(selector); }
        catch (CpuFaultException fault) when (fault.Vector == 13) { return false; }
        var cache = descriptor.Cache;
        bool conformingCode = (cache.Access & 0x10) != 0 && cache.Conforming;
        return conformingCode || Math.Max(_cpl, selector & 3) <= cache.Privilege;
    }
    /// <summary>Implements VERR/VERW accessibility checks and publishes their result through ZF.</summary>
    private void VerifySelector(Operand operand, bool write)
    {
        bool valid = TryQueryDescriptor((ushort)Read(operand, 2), out var descriptor);
        var cache = descriptor.Cache;
        valid &= (cache.Access & 0x10) != 0;
        valid &= write ? !cache.Code && (cache.Access & 2) != 0 : !cache.Code || (cache.Access & 2) != 0;
        SetFlag(Zero, valid); // Present is deliberately not a query criterion.
    }
    /// <summary>Implements LAR/LSL descriptor queries with protected-mode and visibility checks.</summary>
    private void QueryDescriptor(bool limit)
    {
        byte modrm = FetchByte();
        if (!ProtectedMode || Virtual8086) throw new CpuFaultException(6);
        Operand operand = DecodeOperand(modrm);
        bool valid = TryQueryDescriptor((ushort)Read(operand, 2), out var descriptor);
        var cache = descriptor.Cache;
        if ((cache.Access & 0x10) == 0)
            valid &= limit ? (cache.Access & 15) is 1 or 2 or 3 or 9 or 11 : (cache.Access & 15) is 1 or 2 or 3 or 4 or 5 or 9 or 11 or 12;
        SetFlag(Zero, valid);
        if (valid)
        {
            uint value = limit ? cache.Limit : ((uint)cache.Access << 8) | ((uint)cache.Flags << 20);
            // Intel leaves LAR bits 19:16 undefined. DOSE's P54C preserves the raw
            // descriptor nibble for deterministic legacy rights round trips.
            // This compatibility policy is not a measured B5 silicon claim.
            if (!limit && Width == 4 && Model == X86CpuModel.IntelP54cB5Subset)
                value |= descriptor.High & 0x000f0000u;
            WriteRegister((modrm >> 3) & 7, Width, value);
        }
    }
    /// <summary>Implements ARPL, returning false for mode-invalid UD only after the original ModRM fetch.</summary>
    private bool AdjustRpl()
    {
        byte modrm = FetchByte();
        if (!ProtectedMode || Virtual8086) return false;
        Operand operand = DecodeOperand(modrm);
        uint value = Read(operand, 2, true), rpl = ReadRegister((modrm >> 3) & 7, 2) & 3;
        bool adjust = (value & 3) < rpl;
        if (adjust) Write(operand, 2, (value & ~3u) | rpl);
        SetFlag(Zero, adjust);
        return true;
    }
}
