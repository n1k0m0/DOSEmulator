// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    // Reference power-on state after initialization. A later CPU RESET without
    // BIST leaves the 486 FPU unchanged; FNINIT explicitly initializes it.
    public ushort FpuControlWord { get; private set; } = 0x037f;
    /// <summary>Raw x87 status, including TOP, condition codes and sticky exception/summary bits.</summary>
    public ushort FpuStatusWord { get; private set; }
    /// <summary>Diagnostic count of x87 instructions.</summary>
    public long X87Instructions { get; private set; }
    // Pin levels are expressed as asserted=true, despite the active-low 486 pins.
    public bool FpuErrorAsserted => (FpuStatusWord & ~FpuControlWord & 0x3f) != 0;
    /// <summary>External IGNNE input level, independent from FERR status and CR0.NE.</summary>
    public bool IgnoreNumericError { get; set; }
    private bool _publishedFpuError, _waitingForNumericError;
    /// <summary>Whether an external-error waiting instruction is suspended without architectural completion.</summary>
    public bool WaitingForNumericError => _waitingForNumericError && HoldForNumericError;
    /// <summary>Whether the current external numeric-error contract requires holding a waiting instruction.</summary>
    private bool HoldForNumericError => FpuErrorAsserted && (Cr0 & 0x20) == 0 && !IgnoreNumericError;
    /// <summary>Count of recorded external numeric-error waits under the implemented FERR/IGNNE contract.</summary>
    public long NumericErrorWaits { get; private set; }
    /// <summary>Diagnostic count of numeric error faults.</summary>
    public long NumericErrorFaults { get; private set; }
    public event Action<bool>? FpuErrorChanged;
    private sealed class NumericErrorWaitException : Exception;

    /// <summary>Publishes an actual FERR level transition independently from the CR0.NE delivery policy.</summary>
    private void PublishFpuError()
    {
        bool asserted = FpuErrorAsserted;
        if (asserted == _publishedFpuError) return;
        _publishedFpuError = asserted;
        FpuErrorChanged?.Invoke(asserted);
    }

    /// <summary>Checks EM/TS and, for waiting forms, the pending numeric-error delivery contract.</summary>
    private void RequireFpu(bool waiting)
    {
        if ((Cr0 & 0x0c) != 0) throw new CpuFaultException(7);
        if (waiting) CheckFpuPending();
    }
    /// <summary>Raises native MF or suspends an external-error waiting instruction until IGNNE permits completion.</summary>
    private void CheckFpuPending()
    {
        if (FpuErrorAsserted)
        {
            if ((Cr0 & 0x20) != 0) { NumericErrorFaults++; throw new CpuFaultException(16); }
            if (!IgnoreNumericError) throw new NumericErrorWaitException();
        }
    }

    /// <summary>Decodes ESC forms and separates nonwaiting controls from instructions that must observe pending errors.</summary>
    private void ExecuteX87(byte opcode)
    {
        byte modrm = FetchByte();
        bool nonwaiting = opcode == 0xdb && modrm is >= 0xe0 and <= 0xe4 ||
                          opcode == 0xdf && modrm == 0xe0 ||
                          modrm < 0xc0 && (opcode == 0xd9 && (modrm >> 3 & 7) is 6 or 7 || opcode == 0xdd && (modrm >> 3 & 7) is 6 or 7);
        RequireFpu(!nonwaiting);
        if (opcode == 0xdb && modrm == 0xe3)
        {
            InitializeFpu();
            return;
        }
        if (opcode == 0xdb && modrm == 0xe2)
        {
            FpuStatusWord &= 0x7f00; // Exceptions/SF/ES/B cleared, TOP/conditions kept.
            PublishFpuError();
            return;
        }
        if (opcode == 0xdf && modrm == 0xe0) { WriteRegister(0, 2, FpuStatusWord); return; }
        if (opcode == 0xdb && modrm is 0xe0 or 0xe1 or 0xe4) return; // Legacy FNENI/FNDISI/FNSETPM: ignored on 486.
        if (opcode == 0xd9 && modrm == 0xd0) { RecordFpuInstruction(opcode, modrm); return; }
        if (modrm < 0xc0)
        {
            int operation = modrm >> 3 & 7;
            if (opcode == 0xd9 && operation is 5 or 7 || opcode == 0xdd && operation == 7)
            {
                Operand operand = DecodeOperand(modrm);
                if (opcode == 0xd9 && operation == 5)
                {
                    FpuControlWord = (ushort)Read(operand, 2);
                    FpuSummary();
                }
                else Write(operand, 2, opcode == 0xd9 ? FpuControlWord : FpuStatusWord);
                return;
            }
        }
        if (ExecuteFpuExponential(opcode, modrm)) return;
        if (ExecuteFpuLogarithm(opcode, modrm)) return;
        if (ExecuteFpuArcTangent(opcode, modrm)) return;
        if (ExecuteFpuTangent(opcode, modrm)) return;
        if (ExecuteFpuSineCosine(opcode, modrm)) return;
        if (ExecuteFpuSinCosPair(opcode, modrm)) return;
        if (ExecuteFpuRemainder(opcode, modrm)) return;
        if (ExecuteFpuExtraction(opcode, modrm)) return;
        if (ExecuteFpuScaling(opcode, modrm)) return;
        if (ExecuteFpuRounding(opcode, modrm)) return;
        if (ExecuteFpuComparison(opcode, modrm)) return;
        if (ExecuteFpuPackedBcd(opcode, modrm)) return;
        if (ExecuteFpuState(opcode, modrm)) return;
        if (ExecuteFpuNumeric(opcode, modrm)) return;
        throw Unsupported($"x87 opcode {opcode:X2} {modrm:X2} is not implemented; remaining numeric instructions are pending.");
    }
}
