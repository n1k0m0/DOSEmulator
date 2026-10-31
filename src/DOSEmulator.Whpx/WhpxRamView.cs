// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers;

namespace DOSEmulator.Whpx;

/// <summary>Managed span view over the native owner's page-aligned DRAM, valid only until the owner detaches and frees it.</summary>
internal sealed unsafe class WhpxRamView(byte* pointer, int length) : MemoryManager<byte>
{
    private bool _disposed;

    /// <summary>Exposes the same physical bytes used by WHP, without a second device-side RAM copy.</summary>
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); return new(pointer, length);
    }

    /// <summary>Returns the already stable native address after checking the complete requested offset.</summary>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)elementIndex > (uint)length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new(pointer + elementIndex);
    }

    /// <summary>Requires no GC unpin because the separately owned aligned storage never moves.</summary>
    public override void Unpin() { }

    /// <summary>Invalidates views only after the bus lease is detached; the physical-memory owner frees the allocation.</summary>
    protected override void Dispose(bool disposing) => _disposed = true;
}
