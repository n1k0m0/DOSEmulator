// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>One explicitly registered, local PCI function's 256-byte configuration interface.
/// Each function owns its header, writable masks, BARs and status side effects.</summary>
public interface IPciConfigurationFunction
{
    /// <summary>Reads one in-range byte/word/dword configuration transaction.</summary>
    uint ReadConfiguration(int offset, int width);
    /// <summary>Accepts one masked, in-range byte/word/dword configuration transaction.</summary>
    void WriteConfiguration(int offset, uint value, int width);
}

/// <summary>Optional fixed host-controller sink for actual unclaimed configuration transactions.</summary>
public interface IPciConfigurationAbortSink
{
    /// <summary>Records an unexpected host-to-PCI master abort without inventing a missing function.</summary>
    void RecordMasterAbort();
}

/// <summary>Initial M4 configuration-mechanism-1 transport, using Intel 82437FX CONFADD/CONFDATA fields.
/// Explicit local functions retain their own state; no host passthrough or implied peripheral is created.</summary>
public sealed partial class PciConfigurationBus : IIoPortDevice
{
    private readonly Dictionary<ushort, IPciConfigurationFunction> _functions = new();
    private uint _address;
    /// <summary>Current configuration address latch, with reserved bits reading zero in this DOSE contract.</summary>
    public uint ConfigurationAddress => _address;
    /// <summary>Number of explicit function addresses; absent functions do not acquire synthetic identities.</summary>
    public int FunctionCount => _functions.Count;

    /// <summary>Adds a validated bus/device/function address before any guest access; duplicates leave the bus unchanged.</summary>
    public void RegisterFunction(byte bus, int device, int function, IPciConfigurationFunction target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if ((uint)device > 31) throw new ArgumentOutOfRangeException(nameof(device));
        if ((uint)function > 7) throw new ArgumentOutOfRangeException(nameof(function));
        ushort key = (ushort)((bus << 8) | (device << 3) | function);
        if (!_functions.TryAdd(key, target)) throw new InvalidOperationException("PCI function address is already registered.");
    }

    /// <summary>Checks exact fixed single-function topology by BDF and peer identity before snapshot acceptance.</summary>
    public bool HasOnlyFunction(byte bus, int device, int function, IPciConfigurationFunction target) =>
        target is not null && (uint)device <= 31 && (uint)function <= 7 && _functions.Count == 1 &&
        _functions.TryGetValue((ushort)((bus << 8) | (device << 3) | function), out var actual) && ReferenceEquals(actual, target);

    /// <summary>Checks one fixed BDF and peer identity while a board separately checks the complete function count.</summary>
    public bool HasFunction(byte bus, int device, int function, IPciConfigurationFunction target) =>
        target is not null && (uint)device <= 31 && (uint)function <= 7 &&
        _functions.TryGetValue((ushort)((bus << 8) | (device << 3) | function), out var actual) && ReferenceEquals(actual, target);

    /// <summary>Resets only the configuration address latch; individual function reset is a chipset owner's separate operation.</summary>
    public void ResetAddress() => _address = 0;

    /// <summary>Validates the supported transfer widths before invoking any registered function.</summary>
    private static uint Mask(int width) => width switch
    { 1 => 0xff, 2 => 0xffff, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };

    /// <summary>Recognizes an enabled complete transaction inside the four-byte data window without wrapping lanes.</summary>
    private bool IsDataTransaction(ushort port, int width) => port >= 0xcfc && port <= 0xcff &&
        port - 0xcfc + width <= 4 && (_address & 0x80000000) != 0;

    /// <summary>Resolves one accepted data transaction to its explicitly registered function and exact byte offset.</summary>
    private bool Resolve(ushort port, out IPciConfigurationFunction? function, out int offset)
    {
        offset = (int)(_address & 0xfc) + port - 0xcfc;
        return _functions.TryGetValue((ushort)(_address >> 8), out function);
    }

    /// <summary>Reports an actual unclaimed configuration cycle to an explicitly registered local host controller.</summary>
    private void RecordMasterAbort()
    {
        if (_functions.TryGetValue(0, out var host) && host is IPciConfigurationAbortSink sink) sink.RecordMasterAbort();
    }

    /// <summary>Reads CONFADD only as a dword or forwards one data transaction; invalid or missing accesses float high.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width);
        if (port == 0xcf8 && width == 4) return _address;
        if (!IsDataTransaction(port, width)) return mask;
        if (Resolve(port, out var function, out int offset)) return function!.ReadConfiguration(offset, width) & mask;
        RecordMasterAbort();
        return mask;
    }

    /// <summary>Forwards one completely validated masked write; only actual unclaimed data cycles cause master abort.</summary>
    public void Write(ushort port, uint value, int width)
    {
        uint mask = Mask(width);
        if (port == 0xcf8 && width == 4) { _address = value & 0x80fffffc; return; }
        if (!IsDataTransaction(port, width)) return;
        if (Resolve(port, out var function, out int offset)) function!.WriteConfiguration(offset, value & mask, width);
        else RecordMasterAbort();
    }

    /// <summary>Rejects persisted reserved CONFADD bits while preserving the fixed, separately validated function peers.</summary>
    private void ValidateState()
    {
        if ((_address & ~0x80fffffcu) != 0) throw new InvalidDataException("Invalid PCI configuration address bits.");
    }
}
