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

    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorCreateEmulator(in Callbacks callbacks, out nint emulator);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorDestroyEmulator(nint emulator);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorTryMmioEmulation(nint emulator, nint context, byte* vpContext,
        byte* memoryContext, out uint status);
    [DllImport(Library, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WHvEmulatorTryIoEmulation(nint emulator, nint context, byte* vpContext,
        byte* ioContext, out uint status);

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

    internal void EmulateIo(byte[] exit)
    {
        _callbackFailure = null;
        _ioOperations = 0;
        fixed (byte* pointer = exit)
        {
            WhpxNative.Check(WHvEmulatorTryIoEmulation(_emulator, 0, pointer + 8, pointer + 48, out uint status), "emulate string I/O");
            if (_callbackFailure is { } error) throw new InvalidOperationException("WHP I/O callback failed.", error);
            if ((status & 1) == 0) throw new NotSupportedException($"WHP cannot emulate this I/O instruction (status 0x{status:X8}).");
        }
    }

    internal void Emulate(byte[] exit)
    {
        _callbackFailure = null;
        fixed (byte* pointer = exit)
        {
            WhpxNative.Check(WHvEmulatorTryMmioEmulation(_emulator, 0, pointer + 8, pointer + 48, out uint status), "emulate MMIO");
            if (_callbackFailure is { } error) throw new InvalidOperationException("WHP MMIO callback failed.", error);
            if ((status & 1) == 0) throw new NotSupportedException($"WHP cannot emulate this MMIO instruction (status 0x{status:X8}, count={exit[48]}, bytes={Convert.ToHexString(exit.AsSpan(52, 16))}, access={BinaryPrimitives.ReadUInt32LittleEndian(exit.AsSpan(68)):X}, GPA={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(72)):X}, GVA={BinaryPrimitives.ReadUInt64LittleEndian(exit.AsSpan(80)):X}, translated={_lastGva:X}/{_lastGpa:X}/{_lastTranslation}).");
        }
    }

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

    private int AccessMemory(nint context, byte* access)
    {
        try
        {
            ulong address = *(ulong*)access;
            int width = access[9];
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

    private int GetRegisters(nint context, uint* names, uint count, WhpxNative.RegisterValue* values) =>
        WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, count, values);

    private int SetRegisters(nint context, uint* names, uint count, WhpxNative.RegisterValue* values) =>
        WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, count, values);

    private int Translate(nint context, ulong gva, uint flags, uint* result, ulong* gpa)
    {
        int hr = WhpxNative.WHvTranslateGva(_partition, 0, gva, flags, out ulong translation, out ulong address);
        if (hr < 0) return hr;
        *result = (uint)translation;
        *gpa = address & ~4095UL;
        _lastGva = gva; _lastGpa = address; _lastTranslation = (uint)translation;
        return 0;
    }

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
