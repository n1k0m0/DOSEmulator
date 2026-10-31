// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DOSEmulator.Core;

namespace DOSEmulator.Whpx;

/// <summary>
/// Isolated WHP execution lane for owned real-mode programs. It shares DOSE's port-bus
/// contract but deliberately does not claim PcMachine timing, PIC, VGA, or snapshots.
/// No user PC or media are opened and no production launcher selects this lane.
/// </summary>
public sealed unsafe class WhpxRealModeMachine : IDisposable
{
    private const int RamBytes = 1 << 20;
    private readonly IoPortBus _ports;
    private readonly byte[] _exit = new byte[4096];
    private nint _partition;
    private void* _ram;
    private bool _vp;
    private bool _running;
    private bool _disposed;

    /// <summary>Queries local WHP availability without creating a host window or an endpoint.</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
        try
        {
            int hr = WhpxNative.WHvGetCapability(0, out ulong present, 8, out uint written);
            return hr >= 0 && written >= 4 && (uint)present == 1;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Creates an isolated native CPU and its own RAM for finite primitive real-mode probes.</summary>
    public WhpxRealModeMachine(IoPortBus ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (!IsAvailable()) throw new PlatformNotSupportedException("Windows Hypervisor Platform is unavailable.");
        _ports = ports;
        try
        {
            WhpxNative.Check(WhpxNative.WHvCreatePartition(out _partition), "create partition");
            uint one = 1;
            WhpxNative.Check(WhpxNative.WHvSetPartitionProperty(_partition, 0x1fff, in one, 4), "set processor count");
            WhpxNative.Check(WhpxNative.WHvSetupPartition(_partition), "setup partition");
            _ram = NativeMemory.AlignedAlloc(RamBytes, 4096);
            if (_ram is null) throw new OutOfMemoryException();
            new Span<byte>(_ram, RamBytes).Clear();
            WhpxNative.Check(WhpxNative.WHvMapGpaRange(_partition, (nint)_ram, 0, RamBytes,
                WhpxNative.ReadWriteExecuteDirty), "map private RAM");
            WhpxNative.Check(WhpxNative.WHvCreateVirtualProcessor(_partition, 0, 0), "create virtual processor");
            _vp = true;
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Copies bounded own guest code or data into stopped native RAM.</summary>
    public void WriteRam(int address, ReadOnlySpan<byte> bytes)
    {
        EnsureStopped();
        if (address < 0 || bytes.Length > RamBytes - address) throw new ArgumentOutOfRangeException(nameof(address));
        bytes.CopyTo(new Span<byte>((byte*)_ram + address, bytes.Length));
    }

    /// <summary>Copies a bounded stopped native RAM range into an independent diagnostic buffer.</summary>
    public byte[] ReadRam(int address, int length)
    {
        EnsureStopped();
        if (address < 0 || length < 0 || length > RamBytes - address) throw new ArgumentOutOfRangeException(nameof(address));
        return new ReadOnlySpan<byte>((byte*)_ram + address, length).ToArray();
    }

    /// <summary>Initializes a flat real-mode CS and deterministic initial flags for a private program.</summary>
    public void SetEntry(ushort cs, ushort ip)
    {
        EnsureStopped();
        uint[] names = [WhpxNative.Cs, WhpxNative.Rip, WhpxNative.Rflags];
        WhpxNative.RegisterValue[] values =
        [new((ulong)cs << 4, 0xffffUL | ((ulong)cs << 32) | (0x9bUL << 48)), new(ip), new(2)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 3, values), "set real-mode entry");
    }

    /// <summary>Reads a selected authoritative native register at a stopped CPU boundary.</summary>
    public ulong GetRegister(uint name)
    {
        EnsureStopped();
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = new WhpxNative.RegisterValue[1];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, 1, values), "get register");
        return values[0].Low;
    }

    /// <summary>Returns WHP-tracked dirty 4-KiB pages, then clears its tracking window.</summary>
    public int[] TakeDirtyPages()
    {
        EnsureStopped();
        ulong[] bits = new ulong[RamBytes / 4096 / 64];
        WhpxNative.Check(WhpxNative.WHvQueryGpaRangeDirtyBitmap(_partition, 0, RamBytes, bits,
            (uint)(bits.Length * sizeof(ulong))), "query dirty pages");
        var pages = new List<int>();
        for (int page = 0; page < RamBytes / 4096; page++)
            if ((bits[page >> 6] & (1UL << (page & 63))) != 0) pages.Add(page);
        return [.. pages];
    }

    /// <summary>Runs bounded own code; only non-string IN/OUT and HLT are admitted exits.</summary>
    public WhpxRunResult RunUntilHalt(TimeSpan timeout, int maximumExits = 1024)
    {
        EnsureStopped();
        if (timeout <= TimeSpan.Zero || maximumExits <= 0) throw new ArgumentOutOfRangeException(nameof(timeout));
        int ioExits = 0;
        int cancelled = 0;
        _running = true;
        using var timer = new CancellationTokenSource(timeout);
        using var registration = timer.Token.Register(() =>
        {
            Interlocked.Exchange(ref cancelled, 1);
            WhpxNative.WHvCancelRunVirtualProcessor(_partition, 0, 0);
        });
        try
        {
            for (int exitCount = 0; exitCount < maximumExits; exitCount++)
            {
                WhpxNative.Check(WhpxNative.WHvRunVirtualProcessor(_partition, 0, _exit, (uint)_exit.Length), "run virtual processor");
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(_exit);
                if (Volatile.Read(ref cancelled) != 0 || reason == 0xffffffff)
                    throw new TimeoutException("WHP private guest execution timed out.");
                if (reason == 8) return new WhpxRunResult(ioExits, exitCount + 1, GetRegisterWhileRunning(WhpxNative.Rip));
                if (reason != 2) throw new NotSupportedException($"WHP exit 0x{reason:X} at RIP={GetRegisterWhileRunning(WhpxNative.Rip):X} is outside the admitted real-mode lane.");
                HandleIoExit();
                ioExits++;
            }
            throw new InvalidOperationException("WHP exit budget exhausted.");
        }
        finally { _running = false; }
    }

    /// <summary>Routes actual scalar ports directly and string I/O through the checked decoder.</summary>
    private void HandleIoExit()
    {
        int length = _exit[10] & 15;
        uint info = BinaryPrimitives.ReadUInt32LittleEndian(_exit.AsSpan(68));
        bool write = (info & 1) != 0;
        int width = (int)((info >> 1) & 7);
        if (length is < 1 or > 15 || (info & 0x30) != 0 || width is not (1 or 2 or 4))
            throw new NotSupportedException("WHP string/REP or malformed I/O exit is not admitted.");
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(_exit.AsSpan(72));
        ulong rax = GetRegisterWhileRunning(WhpxNative.Rax);
        uint mask = width == 4 ? uint.MaxValue : (uint)((1UL << (width * 8)) - 1);
        if (write) _ports.Write(port, (uint)rax & mask, width);
        else SetRegisterWhileRunning(WhpxNative.Rax, (rax & ~mask) | (_ports.Read(port, width) & mask));
        ulong rip = GetRegisterWhileRunning(WhpxNative.Rip);
        SetRegisterWhileRunning(WhpxNative.Rip, rip + (uint)length);
    }

    /// <summary>Reads the selected native register inside the sole execution owner's exit handler.</summary>
    private ulong GetRegisterWhileRunning(uint name)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = new WhpxNative.RegisterValue[1];
        WhpxNative.Check(WhpxNative.WHvGetVirtualProcessorRegisters(_partition, 0, names, 1, values), "get exit register");
        return values[0].Low;
    }

    /// <summary>Commits one register result inside the sole execution owner's exit handler.</summary>
    private void SetRegisterWhileRunning(uint name, ulong value)
    {
        uint[] names = [name];
        WhpxNative.RegisterValue[] values = [new(value)];
        WhpxNative.Check(WhpxNative.WHvSetVirtualProcessorRegisters(_partition, 0, names, 1, values), "set exit register");
    }

    /// <summary>Rejects register or storage access during native execution or after disposal.</summary>
    private void EnsureStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("WHP state is owner-accessible only while stopped.");
    }

    /// <summary>Releases the stopped primitive probe CPU, RAM and partition in ownership order.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_running) throw new InvalidOperationException("Cannot dispose a running WHP virtual processor.");
        _disposed = true;
        if (_vp) { WhpxNative.WHvDeleteVirtualProcessor(_partition, 0); _vp = false; }
        if (_partition != 0) { WhpxNative.WHvDeletePartition(_partition); _partition = 0; }
        if (_ram is not null) { NativeMemory.AlignedFree(_ram); _ram = null; }
    }
}

public readonly record struct WhpxRunResult(int IoExits, int TotalExits, ulong Rip);
