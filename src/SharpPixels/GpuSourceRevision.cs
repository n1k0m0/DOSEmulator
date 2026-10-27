// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
#nullable enable
namespace SharpPixels;

/// <summary>UI-only identity of an actually uploaded source; null revisions preserve the legacy always-upload API.</summary>
internal sealed class GpuSourceRevision
{
    private object? _pixels;
    private long? _revision;
    private int _width, _height;
    private bool _valid;
    /// <summary>Determines whether content, source ownership, dimensions or missing caller revision requires an upload.</summary>
    public bool NeedsUpload(object pixels, int width, int height, long? revision) => !revision.HasValue || !_valid ||
        !object.ReferenceEquals(_pixels, pixels) || _revision != revision || _width != width || _height != height;
    /// <summary>Commits only after the caller's actual upload has completed without throwing.</summary>
    public void Uploaded(object pixels, int width, int height, long? revision)
    { _pixels = pixels; _width = width; _height = height; _revision = revision; _valid = revision.HasValue; }
    /// <summary>Admits only a confirmed upload; any failure also invalidates the previous texture-content assumption.</summary>
    public bool CompleteUpload(object pixels, int width, int height, long? revision, bool successful)
    {
        if (!successful) { Invalidate(); return false; }
        Uploaded(pixels, width, height, revision); return true;
    }
    /// <summary>Invalidates a retired GPU-context/source identity without retaining its leased array.</summary>
    public void Invalidate() { _valid = false; _pixels = null; _revision = null; }
}
