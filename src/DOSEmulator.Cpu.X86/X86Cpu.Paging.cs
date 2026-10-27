// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private struct Translation
    {
        public bool Valid, User, Writable, Dirty;
        public uint Page, Frame, PteAddress;
        public long Age;
    }
    // 32 entries, eight sets/four ways. Replacement is deterministic LRU;
    // no claim is made about a particular 486 stepping's replacement circuit.
    private readonly Translation[] _tlb = new Translation[32];
    private long _translationAge;
    /// <summary>Whether classic paging is enabled in the implemented control-register state.</summary>
    public bool PagingEnabled => (Cr0 & 0x80000000) != 0;
    /// <summary>Diagnostic count of page faults.</summary>
    public long PageFaults { get; private set; }
    /// <summary>Diagnostic count of page walks.</summary>
    public long PageWalks { get; private set; }
    /// <summary>Diagnostic count of tlb hits.</summary>
    public long TlbHits { get; private set; }
    /// <summary>Diagnostic count of tlb invalidations.</summary>
    public long TlbInvalidations { get; private set; }

    /// <summary>Resets translation caches and paging diagnostics without inventing retained address-space state.</summary>
    private void ResetPaging()
    {
        Array.Clear(_tlb); _translationAge = 0;
        PageFaults = PageWalks = TlbHits = TlbInvalidations = 0;
    }
    /// <summary>Invalidates cached paging translations when architectural address-space state changes.</summary>
    private void FlushTranslations() { Array.Clear(_tlb); TlbInvalidations++; }
    /// <summary>Invalidates the implemented cached translation for one linear page.</summary>
    private void InvalidatePage(uint address)
    {
        uint page = address >> 12;
        int first = (int)(page & 7) * 4;
        for (int i = first; i < first + 4; i++)
            if (_tlb[i].Valid && _tlb[i].Page == page) _tlb[i].Valid = false;
        TlbInvalidations++;
    }
    /// <summary>Records CR2 and constructs the page-fault error code for the failing access.</summary>
    private CpuFaultException PageFault(uint address, bool write, bool user, bool present)
    {
        Cr2 = address; PageFaults++;
        return new(14, (present ? 1u : 0u) | (write ? 2u : 0u) | (user ? 4u : 0u));
    }
    /// <summary>Applies user/supervisor, read/write and CR0.WP checks to a translated page.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void CheckPageRights(in Translation entry, uint address, bool write, bool user)
    {
        if ((user && !entry.User) || (write && !entry.Writable && (user || (Cr0 & 0x10000) != 0)))
            throw PageFault(address, write, user, true);
    }
    /// <summary>Translates a linear byte address through the classic 4-KiB page tables/TLB and records A/D effects.</summary>
    private uint TranslateLinear(uint address, bool write = false, bool supervisor = false)
    {
        if (!PagingEnabled) return address;
        bool user = !supervisor && CurrentPrivilegeLevel == 3;
        uint page = address >> 12;
        int first = (int)(page & 7) * 4, victim = first;
        for (int i = first; i < first + 4; i++)
        {
            ref var cached = ref _tlb[i];
            if (cached.Valid && cached.Page == page)
            {
                CheckPageRights(cached, address, write, user);
                TlbHits++; cached.Age = ++_translationAge;
                if (write && !cached.Dirty)
                {
                    EnsureDeviceCheckpoint(cached.PteAddress);
                    _memory.WriteDword(cached.PteAddress, _memory.ReadDword(cached.PteAddress) | 0x40);
                    cached.Dirty = true;
                }
                return cached.Frame | (address & 0xfff);
            }
            if (!cached.Valid || (_tlb[victim].Valid && cached.Age < _tlb[victim].Age)) victim = i;
        }
        PageWalks++;
        uint pdeAddress = (Cr3 & 0xfffff000) + ((address >> 20) & 0xffc);
        EnsureDeviceCheckpoint(pdeAddress);
        uint pde = _memory.ReadDword(pdeAddress);
        if ((pde & 1) == 0) throw PageFault(address, write, user, false);
        // A walked present directory can remain accessed after a later fault.
        if ((pde & 0x20) == 0) _memory.WriteDword(pdeAddress, pde | 0x20);
        uint pteAddress = (pde & 0xfffff000) + ((address >> 10) & 0xffc);
        EnsureDeviceCheckpoint(pteAddress);
        uint pte = _memory.ReadDword(pteAddress);
        if ((pte & 1) == 0) throw PageFault(address, write, user, false);
        var entry = new Translation { Valid = true, Page = page, Frame = pte & 0xfffff000,
            User = (pde & pte & 4) != 0, Writable = (pde & pte & 2) != 0,
            Dirty = (pte & 0x40) != 0, PteAddress = pteAddress, Age = ++_translationAge };
        CheckPageRights(entry, address, write, user);
        uint accessed = pte | 0x20 | (write ? 0x40u : 0u);
        if (accessed != pte) _memory.WriteDword(pteAddress, accessed);
        entry.Dirty |= write;
        _tlb[victim] = entry;
        return entry.Frame | (address & 0xfff);
    }

    /// <summary>Preflights a complete linear range and requested rights before a later nontransactional access.</summary>
    private void ProbeMemory(uint address, int width, bool write = false, bool supervisor = false)
    {
        if (!PagingEnabled) return;
        int remaining = width;
        while (remaining > 0)
        {
            TranslateLinear(address, write, supervisor);
            int part = Math.Min(remaining, 4096 - (int)(address & 0xfff));
            address = unchecked(address + (uint)part); remaining -= part;
        }
    }
    /// <summary>Reads a linear operand through paging, the memory bus and eligible debug-data observation.</summary>
    private uint ReadMemory(uint address, int width, bool supervisor = false, bool writeIntent = false)
    {
        CheckAlignment(address, width, supervisor);
        if (!PagingEnabled || (address & 0xfff) + width <= 4096)
        {
            uint physical = TranslateLinear(address, writeIntent, supervisor);
            EnsureDeviceCheckpoint(physical, width);
            ObserveDebugData(address, width, false, supervisor);
            return width switch { 1 => _memory.ReadByte(physical), 2 => _memory.ReadWord(physical), _ => _memory.ReadDword(physical) };
        }
        // Translate BOTH pages before the first RAM/MMIO read.
        Span<uint> physicalBytes = stackalloc uint[4];
        for (int i = 0; i < width; i++) { physicalBytes[i] = TranslateLinear(unchecked(address + (uint)i), writeIntent, supervisor); EnsureDeviceCheckpoint(physicalBytes[i]); }
        uint result = 0;
        ObserveDebugData(address, width, false, supervisor);
        for (int i = 0; i < width; i++) result |= (uint)_memory.ReadByte(physicalBytes[i]) << (8 * i);
        return result;
    }
    /// <summary>Writes a prevalidated linear operand through paging and the memory bus with debug-data observation.</summary>
    private void WriteMemory(uint address, int width, uint value, bool supervisor = false)
    {
        CheckAlignment(address, width, supervisor);
        if (!PagingEnabled || (address & 0xfff) + width <= 4096)
        {
            uint physical = TranslateLinear(address, true, supervisor);
            EnsureDeviceCheckpoint(physical, width);
            ObserveDebugData(address, width, true, supervisor);
            if (width == 1) _memory.WriteByte(physical, (byte)value);
            else if (width == 2) _memory.WriteWord(physical, (ushort)value);
            else _memory.WriteDword(physical, value);
            return;
        }
        Span<uint> physicalBytes = stackalloc uint[4];
        for (int i = 0; i < width; i++) { physicalBytes[i] = TranslateLinear(unchecked(address + (uint)i), true, supervisor); EnsureDeviceCheckpoint(physicalBytes[i]); }
        ObserveDebugData(address, width, true, supervisor);
        for (int i = 0; i < width; i++) _memory.WriteByte(physicalBytes[i], (byte)(value >> (8 * i)));
    }

    // Diagnostic lookup never fills the TLB, changes A/D, or raises a guest fault.
    private bool PeekPhysicalDword(uint address, out uint value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!_memory.TryPeekByte(unchecked(address + (uint)i), out byte part)) return false;
            value |= (uint)part << (i * 8);
        }
        return true;
    }
    /// <summary>Most recently recorded physical fetch address for diagnostics.</summary>
    public uint? MappedInstructionAddress
    {
        get
        {
            uint address = LinearInstructionAddress;
            if (!PagingEnabled) return address;
            uint page = address >> 12;
            int first = (int)(page & 7) * 4;
            for (int i = first; i < first + 4; i++)
                if (_tlb[i].Valid && _tlb[i].Page == page) return _tlb[i].Frame | (address & 0xfff);
            if (!PeekPhysicalDword((Cr3 & 0xfffff000) + ((address >> 20) & 0xffc), out uint pde)) return null;
            if ((pde & 1) == 0) return null;
            if (!PeekPhysicalDword((pde & 0xfffff000) + ((address >> 10) & 0xffc), out uint pte)) return null;
            return (pte & 1) == 0 ? null : (pte & 0xfffff000) | (address & 0xfff);
        }
    }
}
