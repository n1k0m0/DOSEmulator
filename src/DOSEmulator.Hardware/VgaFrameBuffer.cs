// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Reusable owner-thread surface. A reader must hold its exchange lease through GPU upload.</summary>
public sealed class VgaFrameBuffer
{
    internal VgaAdapter? Owner;
    internal long Revision;
    internal ulong Phase;
    internal int Width, Height;
    internal uint[] Pixels = [];
    /// <summary>Compares the complete existing render-cache identity without reading or copying leased pixels.</summary>
    public bool HasSamePixels(VgaFrameBuffer other) => Owner is not null && ReferenceEquals(Owner, other.Owner) &&
        Revision == other.Revision && Phase == other.Phase && Width == other.Width && Height == other.Height &&
        Frame?.ModeGeneration == other.Frame?.ModeGeneration;
    /// <summary>Most recently completed frame owned by this reusable scanout surface.</summary>
    public VideoFrame? Frame { get; internal set; }
    /// <summary>Diagnostic count of rendered frames.</summary>
    public long RenderedFrames { get; internal set; }
}
