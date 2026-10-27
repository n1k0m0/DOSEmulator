// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Cpu.X86;

/// <summary>Independent inventory of documented Intel486 ESC encodings, excluding later instructions and undocumented aliases.
/// Numeric results, precision, exceptions and environment bytes retain their separate detailed regression families.</summary>
internal static class X87EncodingCoverageTests
{
    /// <summary>Builds the documented register-form inventory from Intel486 instruction tables, not product dispatch predicates.</summary>
    private static IEnumerable<(byte Opcode, byte Modrm)> Registers()
    {
        foreach (int code in Enumerable.Range(0xc0,64)) yield return (0xd8,(byte)code);
        foreach (int code in Enumerable.Range(0xc0,16).Concat(new[] {0xd0,0xe0,0xe1,0xe4,0xe5})
            .Concat(Enumerable.Range(0xe8,7)).Concat(Enumerable.Range(0xf0,16))) yield return (0xd9,(byte)code);
        yield return (0xda,0xe9);
        foreach (int code in Enumerable.Range(0xe0,5)) yield return (0xdb,(byte)code);
        foreach (int code in Enumerable.Range(0xc0,16).Concat(Enumerable.Range(0xe0,32))) yield return (0xdc,(byte)code);
        foreach (int code in Enumerable.Range(0xc0,8).Concat(Enumerable.Range(0xd0,32))) yield return (0xdd,(byte)code);
        foreach (int code in Enumerable.Range(0xc0,16).Concat(new[] {0xd9}).Concat(Enumerable.Range(0xe0,32))) yield return (0xde,(byte)code);
        yield return (0xdf,0xe0);
    }

    /// <summary>Executes one original encoding with a real FPU stack and checked completion in either code-size context.</summary>
    private static void Execute(byte[] code, bool code32)
    {
        var (c,m) = ProtectedModeTests.Rig(code,code32:code32);
        PagingTests.At(c,m,[0xd9,0xe8],0x2400); PagingTests.At(c,m,[0xd9,0xe8],0x2400);
        c.Eip = 0x2000; long before = c.Instructions;
        c.Step();
        if (c.Eip != 0x2000+code.Length || c.Instructions != before+1 || c.NumericErrorFaults != 0 || c.TripleFaulted)
            throw new Exception("Documented 486 x87 form did not retire at its complete encoded boundary.");
    }

    /// <summary>Registers all 252 documented register forms and 57 documented memory groups in PE16 and PE32.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool code32 in new[] {false,true})
        {
            foreach (var (opcode,modrm) in Registers())
                test($"x87 documented encoding {opcode:X2}{modrm:X2} PE{(code32?32:16)} reaches implemented family", () => Execute([opcode,modrm],code32));
            int[][] groups = [ [0,1,2,3,4,5,6,7], [0,2,3,4,5,6,7], [0,1,2,3,4,5,6,7], [0,2,3,5,7],
                [0,1,2,3,4,5,6,7], [0,2,3,4,6,7], [0,1,2,3,4,5,6,7], [0,2,3,4,5,6,7] ];
            for (int index=0;index<groups.Length;index++) foreach (int group in groups[index])
            {
                byte opcode = (byte)(0xd8+index), modrm = (byte)((group<<3)|(code32?5:6));
                byte[] code = code32 ? [opcode,modrm,0,0x30,0,0] : [opcode,modrm,0,0x30];
                test($"x87 documented memory encoding {opcode:X2}/{group} PE{(code32?32:16)} reaches implemented family", () => Execute(code,code32));
            }
        }
    }
}
