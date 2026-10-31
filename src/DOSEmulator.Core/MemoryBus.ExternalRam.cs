// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

public sealed partial class MemoryBus
{
    private Memory<byte> _externalRam;

    /// <summary>Whether one stopped-owner lease currently binds physical RAM to an external CPU's aligned storage.</summary>
    public bool HasExternalRam => !_externalRam.IsEmpty;

    /// <summary>Authoritative owner-thread DRAM bytes; all RAM mappings, holes and A20 policy still belong to the bus.</summary>
    public Span<byte> PhysicalRam => _externalRam.IsEmpty ? Ram.AsSpan() : _externalRam.Span;

    /// <summary>Copies the coherent stopped image before sharing external aligned storage with devices and a native CPU.</summary>
    public void AttachExternalRam(Memory<byte> backing)
    {
        if (HasExternalRam) throw new InvalidOperationException("Physical RAM already has an external owner.");
        if (backing.Length != Ram.Length) throw new ArgumentException("External RAM must retain the complete installed capacity.", nameof(backing));
        Ram.AsSpan().CopyTo(backing.Span); _externalRam = backing;
    }

    /// <summary>Returns a coherent managed image at an actual owner pause before releasing that exact external storage lease.</summary>
    public void DetachExternalRam(Memory<byte> backing)
    {
        if (!HasExternalRam || !_externalRam.Equals(backing)) throw new InvalidOperationException("The external RAM owner differs.");
        _externalRam.Span.CopyTo(Ram); _externalRam = default;
    }

    /// <summary>Retains the ordinary byte-array hot path while external native windows use their shared authoritative storage.</summary>
    private byte ReadRamByte(uint address) => _externalRam.IsEmpty ? Ram[address] : _externalRam.Span[(int)address];

    /// <summary>Writes the selected owner storage; bus callers separately preserve device precedence and write notifications.</summary>
    private void WriteRamByte(uint address, byte value)
    {
        if (_externalRam.IsEmpty) Ram[address] = value; else _externalRam.Span[(int)address] = value;
    }
}
