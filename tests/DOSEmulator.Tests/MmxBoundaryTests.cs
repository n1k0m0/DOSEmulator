// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>MMX paging/fault, protected/V86 and original x87 save-area boundaries beyond arithmetic vectors.</summary>
internal static class MmxBoundaryTests
{
    /// <summary>Requires exact architectural data or an actual independently observed exception.</summary>
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>Executes controlled setup through the real scalar decoder.</summary>
    private static void At(X86Cpu cpu, MemoryBus memory, params byte[] code) => PagingTests.At(cpu, memory, code);
    /// <summary>Registers ordinary MMX execution in every existing protected/paged/V86 fixture and exact fault restart cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int mode = 0; mode <= 6; mode++)
        {
            int selected = mode;
            test($"MMX real PE16 PE32 paging V86 mode {mode}", () =>
            {
                var rig = DecodedBatchTests.Make(selected, [0x0f,0x6e,0xc0,0x0f,0xfd,0xc0,0x0f,0x7e,0xc3,0x0f,0x77], X86CpuModel.IntelP55cMmxSubset);
                rig.Cpu.SetRegister(Register.Ax, 0x7fff8000); uint flags = rig.Cpu.Eflags;
                for (int i = 0; i < 4; i++) rig.Cpu.Step();
                Check(rig.Cpu.Eip == rig.Start + 11 && rig.Cpu.GetRegister(Register.Bx) == 0xfffe0000 && rig.Cpu.FpuTagWord == 0xffff && rig.Cpu.Eflags == flags,
                    "MMX mode execution/word wrapping/flags/EMMS differs");
            });
        }
        foreach (byte opcode in new byte[] { 0x6f,0x7f,0xef }) foreach (bool readOnly in new[] { false,true })
            test($"MMX atomic second-page fault {opcode:X2} readonly {readOnly}", () =>
            {
                var (c,m) = PagingTests.Create([], model:X86CpuModel.IntelP55cMmxSubset);
                c.SetRegister(Register.Ax, 0x12345678); At(c,m,0x0f,0x6e,0xc0); ulong original = c.GetMmxRegister(0);
                m.Ram.AsSpan(0x30ffc, 8).Fill(0x5a); PagingTests.Pte(m,0x31000,0x31000,readOnly?5u:0u);
                if (readOnly && opcode != 0x7f) { PagingTests.Pte(m,0x31000,0x31000,0); }
                byte[] code = [0x0f,opcode,5,0xfc,0x0f,3,0]; code.CopyTo(m.Ram,0x2000); c.Eip=0x2000;
                uint error = (opcode==0x7f?2u:0u) | (readOnly && opcode==0x7f?1u:0u);
                ProtectedModeTests.Fault(c,m,14,error);
                Check(c.Cr2 == 0x31000 && c.GetMmxRegister(0) == original && m.Ram.AsSpan(0x30ffc,8).IndexOfAnyExcept((byte)0x5a)<0, "Partial qword commit or wrong page fault");
            });
        foreach (bool ne in new[] { false,true }) foreach (bool ignore in new[] { false,true }) foreach (byte opcode in new byte[] { 0x77,0xef })
            test($"MMX pending x87 error NE {ne} IGNNE {ignore} opcode {opcode:X2}", () =>
            {
                var (c,m) = ProtectedModeTests.Rig([],model:X86CpuModel.IntelP55cMmxSubset);
                c.SetRegister(Register.Ax, ne?c.Cr0|0x20:c.Cr0&~0x20u); At(c,m,0x0f,0x22,0xc0);
                m.WriteWord(0x30000,0x37e); At(c,m,0xd9,0x2d,0,0,3,0); At(c,m,0xd8,0xc0); Check(c.FpuErrorAsserted,"No actual pending error");
                c.IgnoreNumericError=ignore; ushort tags=c.FpuTagWord; byte? vector=null; c.InterruptEntered+=v=>vector=v;
                At(c,m,0x0f,opcode,0xc0);
                if(ne) Check(vector==16 && c.FpuTagWord==tags,"MMX did not preserve MF fault boundary");
                else if(!ignore) Check(c.WaitingForNumericError && c.Eip==0x2200 && c.FpuTagWord==tags,"External FERR wait retired MMX");
                else Check(c.Eip==0x2200+(opcode==0x77?2u:3u) && c.FpuErrorAsserted,"IGNNE did not permit MMX or cleared the error");
            });
        foreach (bool wide in new[] { false,true }) test($"MMX FSAVE FRSTOR all raw physical registers width {wide}", () =>
        {
            var (c,m) = ProtectedModeTests.Rig([],code32:wide,model:X86CpuModel.IntelP55cMmxSubset);
            // Encodes the same absolute FSAVE/FRSTOR target under the actual 16/32-bit address mode.
            byte[] Address(byte opcode,int group,uint at) => wide ? [opcode,(byte)(group*8+5),..BitConverter.GetBytes(at)] : [opcode,(byte)(group*8+6),(byte)at,(byte)(at>>8)];
            // Actual x87 pushes make TOP nonzero before MMX names physical registers.
            At(c,m,0xd9,0xe8); At(c,m,0xd9,0xe8); Check(c.FpuTop==6,"Own x87 fixture TOP differs");
            for(int i=0;i<8;i++) { c.SetRegister(Register.Ax,0x80000000u+(uint)i); At(c,m,0x0f,0x6e,(byte)(0xc0+i*8)); }
            ulong[] raw=Enumerable.Range(0,8).Select(c.GetMmxRegister).ToArray(); At(c,m,Address(0xdd,6,0x4000));
            Check(c.FpuTagWord==0xffff,"FNSAVE did not initialize the aliased stack"); At(c,m,Address(0xdd,4,0x4000));
            Check(c.FpuTop==0 && Enumerable.Range(0,8).All(i=>c.GetMmxRegister(i)==raw[i] && c.GetFpuRegister(i).SignExponent==0xffff),"FRSTOR lost MMX sign/exponent/raw order");
            StateBlock saved=c.SaveState(); var (other,_)=ProtectedModeTests.Rig([],code32:wide,model:X86CpuModel.IntelP55cMmxSubset); other.LoadState(saved);
            Check(JsonSerializer.Serialize(saved,StateValues.Json)==JsonSerializer.Serialize(other.SaveState(),StateValues.Json),"Raw restored CPU state differs");
            At(c,m,0x0f,0x77); At(c,m,0xd9,0xe8); Check(c.GetFpuRegister(0)==Float80Value.One,"EMMS did not permit original x87 after save/restore");
        });
        test("MMX LOCK rejects without packed data or tag commit", () =>
        {
            var(c,m)=ProtectedModeTests.Rig([0xf0,0x0f,0xef,0xc0],model:X86CpuModel.IntelP55cMmxSubset);
            ProtectedModeTests.Fault(c,m,6,null); Check(c.FpuTagWord==0xffff,"LOCK changed MMX occupancy");
        });
    }
}
