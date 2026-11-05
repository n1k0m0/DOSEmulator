// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Whpx;

/// <summary>Identifies one successfully opened host surface; the UI never discovers unrelated native windows.</summary>
public sealed record MiniGlideSurface(nint Window, long Generation);

/// <summary>Publishes only the active process-local renderer target, separate from emulated or saved machine state.</summary>
public static class MiniGlidePresentation
{
    private static MiniGlideSurface? _current;
    private static long _generation;

    /// <summary>Returns one immutable presentation generation or null when the guest has closed Glide.</summary>
    public static MiniGlideSurface? Current => Volatile.Read(ref _current);

    /// <summary>Publishes a completed WinOpen, including reopen of the same native handle at another resolution.</summary>
    internal static MiniGlideSurface Open(nint window)
    {
        if (window == 0) throw new ArgumentException("A Glide surface requires a native window.", nameof(window));
        var surface = new MiniGlideSurface(window, Interlocked.Increment(ref _generation));
        Volatile.Write(ref _current, surface);
        return surface;
    }

    /// <summary>Withdraws only this exact surface; a retired owner cannot hide a later renderer generation.</summary>
    internal static void Close(MiniGlideSurface? surface)
    {
        if (surface is not null) Interlocked.CompareExchange(ref _current, null, surface);
    }
}
