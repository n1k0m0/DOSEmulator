// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Whpx;

/// <summary>WHP's instruction decoder forwards an entire MMIO instruction through the physical DOSE bus.</summary>
internal sealed unsafe class WhpxInstructionEmulator : IDisposable
{
    private const string Library = "WinHvEmulation.dll";
    private readonly nint _partition;
    private readonly MemoryBus _bus;
    private readonly IoPortBus _ports;
    private readonly MemoryCallback _memoryCallback;
    private readonly IoCallback _ioCallback;
    private readonly GetRegistersCallback _getRegistersCallback;
    private readonly SetRegistersCallback _setRegistersCallback;
    private readonly TranslateCallback _translateCallback;
    private nint _emulator;
    private Exception? _callbackFailure;
    private int _ioOperations;
    private int _memoryOperations;
    private ulong _lastMemoryAddress;
    private int _lastMemoryWidth;
    private bool _lastMemoryWasRead;
    private string? _videoFallbackRejection;
    private ulong _lastGva, _lastGpa;
    private uint _lastTranslation;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IoCallback(nint context, byte* access);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MemoryCallback(nint context, byte* access);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetRegistersCallback(nint context, uint* names, uint count, WhpxNative.RegisterValue* values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetRegistersCallback(nint context, uint* names, uint count, WhpxNative.RegisterValue* values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TranslateCallback(nint context, ulong gva, uint flags, uint* result, ulong* gpa);

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public uint Size, Reserved;
        public nint Io, Memory, GetRegisters, SetRegisters, Translate;
    }

    /// <summary>Creates the per-partition instruction decoder with the exact DOSE callback table.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorCreateEmulator(in Callbacks callbacks, out nint emulator);
    /// <summary>Releases only the supplied stopped instruction decoder.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorDestroyEmulator(nint emulator);
    /// <summary>Decodes one actual MMIO exit and forwards its register and physical-memory effects through callbacks.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorTryMmioEmulation(nint emulator, nint context, byte* vpContext,
        byte* memoryContext, out uint status);
    /// <summary>Decodes one actual string-I/O exit while retaining its register, direction and count effects.</summary>
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorTryIoEmulation(nint emulator, nint context, byte* vpContext,
        byte* ioContext, out uint status);

    /// <summary>Binds stable decoder callbacks to this native partition and its actual DOSE buses.</summary>
    internal WhpxInstructionEmulator(nint partition, MemoryBus bus, IoPortBus ports)
    {
        _partition = partition;
        _bus = bus;
        _ports = ports;
        _ioCallback = AccessIo;
        _memoryCallback = AccessMemory;
        _getRegistersCallback = GetRegisters;
        _setRegistersCallback = SetRegisters;
        _translateCallback = Translate;
        var callbacks = new Callbacks
        {
            Size = (uint)sizeof(Callbacks),
            Io = Marshal.GetFunctionPointerForDelegate(_ioCallback),
            Memory = Marshal.GetFunctionPointerForDelegate(_memoryCallback),
            GetRegisters = Marshal.GetFunctionPointerForDelegate(_getRegistersCallback),
            SetRegisters = Marshal.GetFunctionPointerForDelegate(_setRegistersCallback),
            Translate = Marshal.GetFunctionPointerForDelegate(_translateCallback)
        };
        WhpxNative.Check(WHvEmulatorCreateEmulator(in callbacks, out _emulator), "create instruction emulator");
    }

    /// <summary>Emulates the actual string-I/O instruction and admits only preflighted fallbacks without replaying a completed port effect.</summary>
    internal void EmulateIo(byte[] exit)
    {
        _callbackFailure = null;
        _ioOperations = 0;
        _memoryOperations = 0;
        fixed (byte* pointer = exit)
        {
            WhpxNative.Check(WHvEmulatorTryIoEmulation(_emulator, 0, pointer + 8, pointer + 48, out uint status), "emulate string I/O");
            if (_callbackFailure is { } error) throw new InvalidOperationException("WHP I/O callback failed.", error);
            if ((status & 1) == 0 && !TryCompleteProtectedWordOut(exit, status) &&
                !TryCompleteProtectedWordIn(exit, status))
                throw new NotSupportedException($"WHP cannot emulate this I/O instruction (status 0x{status:X8}, " +
                    $"completedIo={_ioOperations}, memoryCalls={_memoryOperations}, " +
                    $"count={exit[48]}, bytes={Convert.ToHexString(exit.AsSpan(52, 16))}, " +
                    $"access={BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)):X}, " +
                    $"port={BinaryPrimitives.ReadUInt16LittleEndian(exit.AsSpan(72)):X4}, " +
                    $"rcx={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(88)):X}, " +
                    $"rsi={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(96)):X}, " +
                    $"rdi={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(104)):X}, " +
                    $"lastMemory={_lastMemoryAddress:X}/{_lastMemoryWidth}/{_lastMemoryWasRead}, " +
                    $"translated={_lastGva:X}/{_lastGpa:X}/{_lastTranslation}).");
        }
    }

    // Win98 can exit on a 16-bit protected-mode REP OUTSW.
    // WinHvEmulation returns InternalEmulationFailure before issuing any I/O callback
    // for this case. It may have read one source word already. Complete the instruction
    // only when that read came from ordinary RAM and no device effects occurred.
    private bool TryCompleteProtectedWordOut(byte[] exit, uint status)
    {
        bool esOverride = ((exit[52] == 0x26 && exit[53] == 0xf3) ||
            (exit[52] == 0xf3 && exit[53] == 0x26)) && exit[54] == 0x6f;
        int instructionLength = esOverride ? 3 : 2;
        if (status != 2 || _ioOperations != 0 ||
            (!esOverride && (exit[52] != 0xf3 || exit[53] != 0x6f)) ||
            (exit[10] & 15) != instructionLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) != 0x35 ||
            _memoryOperations > 1)
            return false;

        uint[] names = [6, WhpxNative.Rcx, WhpxNative.Rip, WhpxNative.Rflags, WhpxNative.Ds,
            WhpxNative.Cs, WhpxNative.Rdx, WhpxNative.Es];
        var values = new WhpxNative.RegisterValue[names.Length];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names,
            (uint)names.Length, values), "read trailing OUTSW registers");
        ulong si = values[0].Low, cx = values[1].Low, rip = values[2].Low;
        ulong dsBase = esOverride ? values[7].Low : values[4].Low;
        ulong dsDescriptor = esOverride ? values[7].High : values[4].High;
        ulong csDescriptor = values[5].High;
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(exit.AsSpan(72));
        int count = (ushort)cx;
        if (count == 0 || (values[6].Low & 0xffff) != port ||
            (dsDescriptor >> 48 & 0x4000) != 0 || (csDescriptor >> 48 & 0x4000) != 0 ||
            dsBase > uint.MaxValue)
            return false;

        int increment = (values[3].Low & 0x400) != 0 ? -2 : 2;
        var physicalBytes = new uint[count * 2];
        ushort offset = (ushort)si;
        for (int element = 0; element < count; element++, offset = unchecked((ushort)(offset + increment)))
        {
            if ((uint)offset + 1 > (dsDescriptor & 0xffff_ffff) || dsBase + offset + 1 > uint.MaxValue)
                return false;
            for (int lane = 0; lane < 2; lane++)
            {
                int hr = WhpxNative.WHvTranslateGva(_partition, 0, dsBase + offset + (uint)lane, 0x11,
                    out ulong translation, out ulong physical);
                if (hr < 0 || translation != 0 || physical > uint.MaxValue ||
                    !_bus.IsPlainRamByte((uint)physical)) return false;
                physicalBytes[element * 2 + lane] = (uint)physical;
            }
        }

        if (_memoryOperations == 1 &&
            (!_lastMemoryWasRead || _lastMemoryWidth != 2 || _lastMemoryAddress != physicalBytes[0]))
            return false;

        for (int element = 0; element < count; element++)
        {
            uint word = _bus.ReadByte(physicalBytes[element * 2]) |
                ((uint)_bus.ReadByte(physicalBytes[element * 2 + 1]) << 8);
            _ports.Write(port, word, 2);
        }
        ushort nextSi = unchecked((ushort)((ushort)si + increment * count));
        values[0] = new((si & ~0xffffUL) | nextSi);
        values[1] = new(cx & ~0xffffUL);
        values[2] = new((rip & ~0xffffUL) | (ushort)(rip + (uint)instructionLength));
        uint[] updatedNames = [names[0], names[1], names[2]];
        WhpxNative.RegisterValue[] updatedValues = [values[0], values[1], values[2]];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, updatedNames, 3,
            updatedValues), "complete protected OUTSW");
        return true;
    }

    // The Win95 BIOS also uses REP INSW under V86/paging. Validate every RAM
    // destination before consuming the first IDE word so a failed translation
    // cannot leave the device half drained.
    private bool TryCompleteProtectedWordIn(byte[] exit, uint status)
    {
        if (status != 2 || _ioOperations != 0 || _memoryOperations != 0 || exit[52] != 0xf3 ||
            exit[53] != 0x6d || (exit[10] & 15) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) != 0x34)
            return false;
        uint[] names = [7, WhpxNative.Rcx, WhpxNative.Rip, WhpxNative.Rflags,
            WhpxNative.Es, WhpxNative.Cs, WhpxNative.Rdx];
        var values = new WhpxNative.RegisterValue[names.Length];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names,
            (uint)names.Length, values), "read protected INSW registers");
        ulong di = values[0].Low, cx = values[1].Low, rip = values[2].Low;
        ulong esBase = values[4].Low, esLimit = values[4].High & 0xffff_ffff;
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(exit.AsSpan(72));
        int count = (ushort)cx;
        if (count == 0 || (values[6].Low & 0xffff) != port ||
            (values[5].High >> 48 & 0x4000) != 0 || esBase > uint.MaxValue)
            return false;
        int increment = (values[3].Low & 0x400) != 0 ? -2 : 2;
        var physicalBytes = new uint[count * 2];
        ushort offset = (ushort)di;
        for (int element = 0; element < count; element++, offset = unchecked((ushort)(offset + increment)))
        {
            if ((uint)offset + 1 > esLimit || esBase + offset + 1 > uint.MaxValue) return false;
            for (int lane = 0; lane < 2; lane++)
            {
                int hr = WhpxNative.WHvTranslateGva(_partition, 0, esBase + offset + (uint)lane, 0x12,
                    out ulong translation, out ulong physical);
                if (hr < 0 || translation != 0 || physical > uint.MaxValue ||
                    !_bus.IsPlainRamByte((uint)physical)) return false;
                physicalBytes[element * 2 + lane] = (uint)physical;
            }
        }
        for (int element = 0; element < count; element++)
        {
            uint word = _ports.Read(port, 2);
            _bus.WriteByte(physicalBytes[element * 2], (byte)word);
            _bus.WriteByte(physicalBytes[element * 2 + 1], (byte)(word >> 8));
        }
        ushort nextDi = unchecked((ushort)((ushort)di + increment * count));
        uint[] updatedNames = [names[0], names[1], names[2]];
        WhpxNative.RegisterValue[] updatedValues =
        [new((di & ~0xffffUL) | nextDi), new(cx & ~0xffffUL),
         new((rip & ~0xffffUL) | (ushort)(rip + 2))];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, updatedNames, 3,
            updatedValues), "complete protected INSW");
        return true;
    }

    /// <summary>Executes the actual MMIO instruction or the explicitly checked V86 video-copy fallback.</summary>
    internal void Emulate(byte[] exit)
    {
        _callbackFailure = null;
        _memoryOperations = 0;
        _videoFallbackRejection = null;
        fixed (byte* pointer = exit)
        {
            WhpxNative.Check(WHvEmulatorTryMmioEmulation(_emulator, 0, pointer + 8, pointer + 48, out uint status), "emulate MMIO");
            if (_callbackFailure is { } error) throw new InvalidOperationException("WHP MMIO callback failed.", error);
            if ((status & 1) == 0 && !TryCompleteRealModeVideoMovsb(exit, status))
                throw new NotSupportedException($"WHP cannot emulate this MMIO instruction (status 0x{status:X8}, fallback={_videoFallbackRejection}, length={exit[10] & 15}, memoryCalls={_memoryOperations}, lastMemory={_lastMemoryAddress:X}, count={exit[48]}, bytes={Convert.ToHexString(exit.AsSpan(52, 16))}, access={BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)):X}, GPA={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)):X}, GVA={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(80)):X}, translated={_lastGva:X}/{_lastGpa:X}/{_lastTranslation}).");
        }
    }

    // The Win95 BIOS uses unreal-mode DS/ES descriptors while copying a short
    // REP MOVSB run from RAM into VGA. WinHvEmulation can report an internal
    // failure before any memory callback. Resolve the narrowly bounded BIOS
    // copy through the physical DOSE bus, preserving byte order and 16-bit wrapping.
    private bool TryCompleteRealModeVideoMovsb(byte[] exit, uint status)
    {
        if (status != 2 || _memoryOperations != 0 || exit[52] != 0xf3 || exit[53] != 0xa4 ||
            exit[48] < 2 || BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)) != 5)
        {
            _videoFallbackRejection = "exit-shape";
            return false;
        }

        uint[] names = [6, 7, WhpxNative.Rcx, WhpxNative.Rip, WhpxNative.Rflags,
            WhpxNative.Ds, WhpxNative.Es, WhpxNative.Cs, 0x1c, 0x1e, 0x1f];
        var values = new WhpxNative.RegisterValue[names.Length];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names,
            (uint)names.Length, values), "read video MOVSB registers");
        bool pagedV86 = (values[8].Low & 0x8000_0001UL) == 0x8000_0001UL &&
            (values[4].Low & 0x20000) != 0;
        bool plainReal = (values[8].Low & 0x8000_0001UL) == 0 &&
            (values[4].Low & 0x20000) == 0;
        if ((!plainReal && !pagedV86) || (values[7].High >> 48 & 0x4000) != 0)
        {
            _videoFallbackRejection = "cpu-mode";
            return false;
        }
        int count = (ushort)values[2].Low;
        if (count == 0) { _videoFallbackRejection = "empty"; return false; }
        int direction = (values[4].Low & 0x400) != 0 ? -1 : 1;
        ulong dsBase = values[5].Low, esBase = values[6].Low;
        ulong dsLimit = values[5].High & 0xffff_ffff, esLimit = values[6].High & 0xffff_ffff;
        ushort si = (ushort)values[0].Low, di = (ushort)values[1].Low;
        if (plainReal && esBase + di != BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)))
        { _videoFallbackRejection = "first-gpa"; return false; }

        var source = new byte[count];
        var destinations = new uint[count];
        for (int element = 0; element < count; element++)
        {
            ushort sourceOffset = unchecked((ushort)(si + element * direction));
            ushort targetOffset = unchecked((ushort)(di + element * direction));
            ulong sourceAddress = dsBase + sourceOffset, targetAddress = esBase + targetOffset;
            if (sourceOffset > dsLimit || targetOffset > esLimit || sourceAddress > uint.MaxValue ||
                targetAddress > uint.MaxValue)
            { _videoFallbackRejection = $"operand-{element}-src-{sourceAddress:X}-dst-{targetAddress:X}"; return false; }
            uint sourcePhysical = (uint)sourceAddress, targetPhysical = (uint)targetAddress;
            if (pagedV86)
            {
                int readHr = WhpxNative.WHvTranslateGva(_partition, 0, sourceAddress, 0x11,
                    out ulong readResult, out ulong readPhysical);
                if (readHr < 0 || readResult != 0 || readPhysical > uint.MaxValue)
                { _videoFallbackRejection = $"source-page-{element}-{readHr:X8}-{readResult}"; return false; }
                sourcePhysical = (uint)readPhysical;
                int writeHr = WhpxNative.WHvTranslateGva(_partition, 0, targetAddress, 0x12,
                    out ulong writeResult, out ulong writePhysical);
                if (writeHr >= 0 && writeResult == 0 && writePhysical <= uint.MaxValue)
                    targetPhysical = (uint)writePhysical;
                else if (writeHr >= 0 && writeResult == 4 &&
                    TryWalkV86VideoPage(targetAddress, values[9].Low, values[10].Low, out uint walked))
                    targetPhysical = walked;
                else
                { _videoFallbackRejection = $"target-page-{element}-{writeHr:X8}-{writeResult}"; return false; }
                if (element == 0 && targetPhysical != BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)))
                { _videoFallbackRejection = "first-paged-gpa"; return false; }
            }
            if (targetPhysical is < 0xa0000 or >= 0xc0000 ||
                !_bus.TryReadRamByte(sourcePhysical, out source[element]))
            { _videoFallbackRejection = $"physical-{element}-src-{sourcePhysical:X}-dst-{targetPhysical:X}"; return false; }
            destinations[element] = targetPhysical;
        }
        for (int element = 0; element < count; element++)
            _bus.WriteByte(destinations[element], source[element]);
        WhpxNative.RegisterValue[] updatedValues =
        [new((values[0].Low & ~0xffffUL) | unchecked((ushort)(si + direction * count))),
         new((values[1].Low & ~0xffffUL) | unchecked((ushort)(di + direction * count))),
         new(values[2].Low & ~0xffffUL),
         new((values[3].Low & ~0xffffUL) | (ushort)(values[3].Low + 2))];
        uint[] updatedNames = [names[0], names[1], names[2], names[3]];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, updatedNames, 4,
            updatedValues), "complete video MOVSB");
        return true;
    }

    /// <summary>Checks the V86 paging translation and permissions needed by the bounded video-copy fallback.</summary>
    private bool TryWalkV86VideoPage(ulong linear, ulong cr3, ulong cr4, out uint physical)
    {
        physical = 0;
        if (linear > uint.MaxValue ||
            !TryReadPageEntry((cr3 & 0xffff_f000) + ((linear >> 22) & 0x3ff) * 4, out uint pde) ||
            (pde & 7) != 7) return false;
        if ((pde & 0x80) != 0)
        {
            if ((cr4 & 0x10) == 0) return false;
            physical = (pde & 0xffc0_0000) | ((uint)linear & 0x3f_ffff);
            return true;
        }
        if (!TryReadPageEntry((pde & 0xffff_f000u) + ((linear >> 12) & 0x3ff) * 4, out uint pte) ||
            (pte & 7) != 7) return false;
        physical = (pte & 0xffff_f000) | ((uint)linear & 0xfff);
        return true;
    }

    /// <summary>Reads an ordinary RAM page-table entry without invoking a device mapping.</summary>
    private bool TryReadPageEntry(ulong address, out uint value)
    {
        value = 0;
        if (address + 3 > uint.MaxValue) return false;
        for (uint lane = 0; lane < 4; lane++)
        {
            if (!_bus.IsPlainRamByte((uint)address + lane)) return false;
            value |= (uint)_bus.ReadByte((uint)address + lane) << (int)(lane * 8);
        }
        return true;
    }

    /// <summary>Forwards each completed decoder port operation to DOSE and retains any callback failure for the owner.</summary>
    private int AccessIo(nint context, byte* access)
    {
        try
        {
            if (++_ioOperations > 65536) throw new InvalidOperationException("WHP I/O instruction exceeded the bounded element budget.");
            ushort port = *(ushort*)(access + 2);
            int width = *(ushort*)(access + 4);
            if (width is not (1 or 2 or 4)) throw new NotSupportedException("Unknown WHP I/O width.");
            if (access[0] == 0) *(uint*)(access + 8) = _ports.Read(port, width);
            else if (access[0] == 1) _ports.Write(port, *(uint*)(access + 8), width);
            else throw new NotSupportedException("Unknown WHP I/O direction.");
            return 0;
        }
        catch (Exception error) { _callbackFailure = error; return unchecked((int)0x80004005); }
    }

    /// <summary>Forwards actual physical memory reads and writes through device-aware bus accesses.</summary>
    private int AccessMemory(nint context, byte* access)
    {
        try
        {
            ulong address = *(ulong*)access;
            _memoryOperations++;
            _lastMemoryAddress = address;
            int width = access[9];
            _lastMemoryWidth = width;
            _lastMemoryWasRead = access[8] == 0;
            if (address > uint.MaxValue || width is < 1 or > 8 || address + (uint)width > 0x1_0000_0000UL)
                throw new NotSupportedException("MMIO access outside the 32-bit physical bus.");
            if (access[8] == 0) // WHvEmulatorMemoryAccessRead
                for (int lane = 0; lane < width; lane++) access[10 + lane] = _bus.ReadByte((uint)address + (uint)lane);
            else if (access[8] == 1) // WHvEmulatorMemoryAccessWrite
            {
                if (width == 2) _bus.WriteWord((uint)address, *(ushort*)(access + 10));
                else for (int lane = 0; lane < width; lane++) _bus.WriteByte((uint)address + (uint)lane, access[10 + lane]);
            }
            else throw new NotSupportedException("Unknown WHP memory direction.");
            return 0;
        }
        catch (Exception error) { _callbackFailure = error; return unchecked((int)0x80004005); }
    }

    /// <summary>Returns precisely the native registers requested by the instruction decoder.</summary>
    private int GetRegisters(nint context, uint* names, uint count, WhpxNative.RegisterValue* values) =>
        WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, count, values);

    /// <summary>Commits precisely the native register results supplied by the instruction decoder.</summary>
    private int SetRegisters(nint context, uint* names, uint count, WhpxNative.RegisterValue* values) =>
        WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, count, values);

    /// <summary>Uses WHP's actual virtual-to-physical translation rather than the independent software CPU.</summary>
    private int Translate(nint context, ulong gva, uint flags, uint* result, ulong* gpa)
    {
        int hr = WhpxNative.WHvTranslateGva(_partition, 0, gva, flags, out ulong translation, out ulong address);
        if (hr < 0) return hr;
        *result = (uint)translation;
        *gpa = address & ~4095UL;
        _lastGva = gva; _lastGpa = address; _lastTranslation = (uint)translation;
        return 0;
    }

    /// <summary>Destroys the stopped partition's instruction decoder after all callbacks have returned.</summary>
    public void Dispose()
    {
        if (_emulator == 0) return;
        WHvEmulatorDestroyEmulator(_emulator);
        _emulator = 0;
        GC.KeepAlive(_memoryCallback);
        GC.KeepAlive(_ioCallback);
        GC.KeepAlive(_getRegistersCallback);
        GC.KeepAlive(_setRegistersCallback);
        GC.KeepAlive(_translateCallback);
    }
}
