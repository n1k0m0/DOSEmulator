// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Transfers only the implemented P54C TSC MSR; privilege/address failures precede every clock or register effect.</summary>
    private void TransferTimestampMsr(bool write)
    {
        if (!HasTimeStampCounter) throw new CpuFaultException(6);
        RequirePrivilegeZero();
        // Other documented P54C MSRs require their own cache/performance/MCE contracts.
        if (GetRegister(Register.Cx) != 0x10) throw Gp();
        if (write)
        {
            ulong ticks = _timestampSource!.ClockTicks(TimestampFrequency);
            ulong value = (ulong)GetRegister(Register.Dx) << 32 | GetRegister(Register.Ax);
            // A modulo epoch preserves all 64 written P54C bits, future wrap, and the unchanged scheduler phase.
            _tscEpochTicks = unchecked(ticks - value);
        }
        else
        {
            ulong value = TimestampCounter;
            SetRegister(Register.Ax, (uint)value); SetRegister(Register.Dx, (uint)(value >> 32));
        }
    }
}
