// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

/// <summary>A 486 six-byte pseudo-descriptor: a byte limit and a linear base.</summary>
public readonly record struct DescriptorTableRegister(ushort Limit, uint Base);

public sealed partial class X86Cpu
{
    /// <summary>Global descriptor-table limit and linear base.</summary>
    public DescriptorTableRegister Gdtr { get; private set; }
    /// <summary>Interrupt descriptor-table limit and linear base.</summary>
    public DescriptorTableRegister Idtr { get; private set; }
    /// <summary>Linear address recorded for the most recent page fault.</summary>
    public uint Cr2 { get; private set; }
    /// <summary>Current page-directory base/control value under the implemented classic paging contract.</summary>
    public uint Cr3 { get; private set; }
    /// <summary>Diagnostic count of protected mode entries.</summary>
    public long ProtectedModeEntries { get; private set; }
    /// <summary>Diagnostic count of protected mode exits.</summary>
    public long ProtectedModeExits { get; private set; }
    /// <summary>Diagnostic count of protected instructions.</summary>
    public long ProtectedInstructions { get; private set; }
    /// <summary>Diagnostic count of privilege stack switches.</summary>
    public long PrivilegeStackSwitches { get; private set; }
    /// <summary>Diagnostic count of outer privilege returns.</summary>
    public long OuterPrivilegeReturns { get; private set; }

    /// <summary>Initializes stable development-model values for system registers with undefined reset contents.</summary>
    private void ResetSystemRegisters()
    {
        // GDTR/CR2/CR3 are undefined after 486 reset. These are stable model defaults.
        Gdtr = new(0xffff, 0);
        Idtr = new(0x03ff, 0);
        Cr2 = Cr3 = 0;
        X87Instructions = 0;
        TripleFaulted = false;
        ProtectedModeEntries = ProtectedModeExits = ProtectedInstructions = 0;
        PrivilegeStackSwitches = OuterPrivilegeReturns = CallGateTransfers = 0;
        Virtual8086Entries = Virtual8086Exits = Virtual8086Instructions = 0;
        _taskSwitchEpoch = HardwareTaskSwitches = HardwareTaskReturns = TaskGateTransfers = TaskDebugTraps = 0;
        ResetDebug();
        LockedMemoryInstructions = InvalidLockPrefixes = AlignmentFaults = 0;
    }

    /// <summary>Executes descriptor-table/control system instructions with operand-form and privilege checks.</summary>
    private void ExecuteSystemGroup()
    {
        byte modrm = FetchByte();
        int operation = (modrm >> 3) & 7;
        if ((operation <= 3 || operation == 7) && modrm >= 0xc0) throw new CpuFaultException(6);
        if (operation == 5) throw new CpuFaultException(6); // Reserved on the reference 486.
        if (operation is 2 or 3 or 6 or 7) RequirePrivilegeZero();
        Operand operand = DecodeOperand(modrm);
        if (operation <= 3)
        {
            CheckOperand(operand, 6, operation <= 1);
            // 486 SGDT/SIDT use an aligned word followed by a dword. Their
            // fault-free user address is 2 MOD 4, not a modern blanket rule.
            CheckAlignment(operand.Address, 2);
            CheckAlignment(unchecked(operand.Address + 2), 4);
            if (operation <= 1)
            {
                var table = operation == 0 ? Gdtr : Idtr;
                ProbeMemory(operand.Address, 6, true);
                WriteMemory(operand.Address, 2, table.Limit);
                // Intel486 SGDT/SIDT's 16-bit compatibility form clears byte 5.
                WriteMemory(unchecked(operand.Address + 2), 4, _operand32 ? table.Base : table.Base & 0x00ffffff);
            }
            else
            {
                ProbeMemory(operand.Address, 6);
                ushort limit = (ushort)ReadMemory(operand.Address, 2);
                uint address = ReadMemory(unchecked(operand.Address + 2), 4);
                var table = new DescriptorTableRegister(limit, _operand32 ? address : address & 0x00ffffff);
                if (operation == 2) Gdtr = table; else Idtr = table;
            }
            return;
        }
        if (operation is 4 or 6)
        {
            CheckOperand(operand, 2, operation == 4);
            if (operation == 4) Write(operand, operand.IsRegister ? Width : 2, Cr0 & 0xffff);
            else
            {
                uint value = Read(operand, 2);
                // LMSW changes only MP/EM/TS and may set (never clear) PE; ET is fixed.
                WriteCr0((Cr0 & ~0x0eu) | (value & 0x0f));
            }
            return;
        }
        InvalidatePage(operand.Address); // No read of the addressed page.
    }

    /// <summary>Validates CR0 mode combinations and invalidates translations when paging mode changes.</summary>
    private void WriteCr0(uint value)
    {
        if ((value & 0x80000001) == 0x80000000) throw Gp();
        if (((Cr0 ^ value) & 0x80000000) != 0) FlushTranslations();
        // PE changes mode, not the visible selectors or their hidden caches.
        if (((Cr0 ^ value) & 1) != 0)
        {
            _cpl = 0;
            if ((value & 1) != 0) ProtectedModeEntries++; else ProtectedModeExits++;
        }
        Cr0 = value | 0x10; // ET is hardwired on the reference 486DX.
    }

    /// <summary>Executes MOV CR for the implemented control registers with register-form and CPL checks.</summary>
    private void MoveControlRegister(byte opcode)
    {
        byte modrm = FetchByte();
        int control = (modrm >> 3) & 7, register = modrm & 7;
        if (modrm < 0xc0 || control is not (0 or 2 or 3) && !(control == 4 && HasTimeStampCounter)) throw new CpuFaultException(6);
        RequirePrivilegeZero();
        // Operand/address-size prefixes do not change the 32-bit register transfer.
        if (opcode == 0x20)
            WriteRegister(register, 4, control switch { 0 => Cr0, 2 => Cr2, 3 => Cr3, _ => Cr4 });
        else
        {
            uint value = ReadRegister(register, 4);
            if (control == 0) WriteCr0(value);
            else if (control == 2) Cr2 = value;
            else if (control == 4) { if ((value & ~12u) != 0) throw Gp(); Cr4 = value; }
            else { Cr3 = value; FlushTranslations(); }
        }
    }
}
