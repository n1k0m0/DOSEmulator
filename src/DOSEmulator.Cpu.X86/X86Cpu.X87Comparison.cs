namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private bool ExecuteFpuComparison(byte opcode, byte modrm)
    {
        int group = modrm >> 3 & 7, logical = modrm & 7;
        bool memory = modrm < 0xc0;
        bool test = opcode == 0xd9 && modrm == 0xe4;
        bool twice = opcode == 0xde && modrm == 0xd9 || opcode == 0xda && modrm == 0xe9;
        bool quiet = opcode == 0xdd && modrm is >= 0xe0 and <= 0xef || opcode == 0xda && modrm == 0xe9;
        if (!(test || twice || quiet || opcode == 0xd8 && group is 2 or 3 ||
            memory && opcode is 0xda or 0xdc or 0xde && group is 2 or 3)) return false;
        int pops = twice ? 2 : test ? 0 : quiet ? (modrm >> 3 & 1) : group - 2;
        bool empty = EmptyFpu(FpuTop);
        bool denormal = false;
        Float80Value right;
        if (memory)
        {
            int size = opcode == 0xdc ? 8 : opcode == 0xde ? 2 : 4;
            Operand operand = DecodeOperand(modrm);
            uint[] physical = FpuMemory(operand, size, false, size);
            ulong bits = FpuBits(FpuReadBytes(operand, physical));
            RecordFpuInstruction(opcode, modrm, operand);
            if (opcode is 0xda or 0xde) right = FpuInteger(bits, size);
            else
            {
                var loaded = Float80Math.LoadBinary(bits, size * 8, 0x37f);
                right = loaded.Value; denormal = (loaded.Exceptions & 2) != 0;
            }
        }
        else
        {
            RecordFpuInstruction(opcode, modrm);
            int source = (FpuTop + (twice ? 1 : logical)) & 7;
            right = test ? Float80Value.Zero : _fpuRegisters[source];
            empty |= !test && EmptyFpu(source);
        }
        if (empty)
        {
            bool pop = FpuStackFault(false);
            FpuStatusWord = (ushort)((FpuStatusWord & ~0x4500) | 0x4500);
            if (!pop) return true;
        }
        else
        {
            var result = Float80Math.Compare(_fpuRegisters[FpuTop], right, quiet, FpuControlWord, denormal);
            FpuNumericStatus(result.Exceptions, false);
            FpuStatusWord = (ushort)((FpuStatusWord & ~0x4500) | result.Conditions);
            if (!result.Commit) return true;
        }
        for (int i = 0; i < pops; i++) FpuPop();
        return true;
    }
}
