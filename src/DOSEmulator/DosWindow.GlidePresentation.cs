// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Whpx;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    private nint _glideWindow;
    private uint[] _glidePixels = [];
    private int _glideWidth, _glideHeight;
    private long _glideRevision;
    private long _glideGeneration;
    private string? _glideDisplayLogDirectory = Environment.GetEnvironmentVariable("DOSE_GLIDE_DISPLAY_LOG");
    private string? _glideDisplayLogPath;
    private string _glideDisplayLogStage = "";
    private long _glideDisplayLogNext;
    private int _glideDisplayLogRecords;

    /// <summary>Copies the host renderer's client image into the regular guest viewport so the overlay and input stay in one window.</summary>
    private bool TryCaptureGlideFrame()
    {
        var surface = MiniGlidePresentation.Current;
        if (!_session.UsesExternalCpu || surface is null)
        {
            _glideWindow = 0; _glideWidth = _glideHeight = 0;
            TraceGlideDisplay("desktop", false, false);
            return false;
        }
        if (_glideWindow != surface.Window || _glideGeneration != surface.Generation)
        {
            _glideWindow = surface.Window; _glideGeneration = surface.Generation;
            _glideWidth = _glideHeight = 0;
        }
        if (!IsWindow(_glideWindow)) { TraceGlideDisplay("window-closed", false, false); return false; }
        // The common overlay uses a stable last frame while the guest owner is paused.
        if (_page.Length != 0 && _glideWidth > 0 && _glideHeight > 0)
        { TraceGlideDisplay("overlay", false, false); return true; }
        if (!GetWindowRect(_glideWindow, out RectangleNative outer) || !GetClientRect(_glideWindow, out RectangleNative client))
        { TraceGlideDisplay("geometry-unavailable", false, false); return false; }
        // Some providers reposition the swap-chain window after mode changes. Keep it off the desktop each frame.
        if (outer.Left > -1000 || outer.Top > -1000)
        {
            if (!SetWindowPos(_glideWindow, 0, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010) ||
                !GetWindowRect(_glideWindow, out outer))
            { TraceGlideDisplay("offscreen-move-failed", false, false); return false; }
        }
        PointNative origin = default;
        if (!ClientToScreen(_glideWindow, ref origin)) { TraceGlideDisplay("origin-unavailable", false, false); return false; }
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        int outerWidth = outer.Right - outer.Left, outerHeight = outer.Bottom - outer.Top;
        int left = origin.X - outer.Left, top = origin.Y - outer.Top;
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || outerWidth is < 1 or > 4096 ||
            outerHeight is < 1 or > 4096 || left < 0 || top < 0 ||
            left + width > outerWidth || top + height > outerHeight)
        { TraceGlideDisplay("geometry-out-of-range", false, false); return false; }
        using var bitmap = new Bitmap(outerWidth, outerHeight, PixelFormat.Format32bppArgb);
        bool fullContent, legacyFallback = false;
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            nint dc = graphics.GetHdc();
            try
            {
                // Include GPU/composited content even though the backing HWND is offscreen.
                // The legacy print path can succeed while returning a black DirectX image.
                fullContent = PrintWindow(_glideWindow, dc, 2); // PW_RENDERFULLCONTENT (Windows 8.1+).
                if (!fullContent && !(legacyFallback = PrintWindow(_glideWindow, dc, 0)))
                { TraceGlideDisplay("capture-failed", false, false); return false; }
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        var bounds = new Rectangle(left, top, width, height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int count = checked(width * height);
            if (_glidePixels.Length != count) _glidePixels = new uint[count];
            int[] row = new int[width];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, width);
                for (int x = 0; x < width; x++) _glidePixels[y * width + x] = unchecked((uint)row[x]);
            }
        }
        finally { bitmap.UnlockBits(data); }
        _glideWidth = width; _glideHeight = height;
        unchecked { _glideRevision++; }
        TraceGlideDisplay("captured", fullContent, legacyFallback);
        return true;
    }

    /// <summary>Optionally records bounded display metadata for a reported black screen; never saves guest RAM or images.</summary>
    private void TraceGlideDisplay(string stage, bool fullContent, bool legacyFallback)
    {
        if (string.IsNullOrWhiteSpace(_glideDisplayLogDirectory) || _glideDisplayLogRecords >= 240) return;
        long now = Environment.TickCount64;
        // Waiting at the desktop must not spend the game's diagnostic budget.
        if (stage == "desktop" && stage == _glideDisplayLogStage) return;
        if (stage == _glideDisplayLogStage && now < _glideDisplayLogNext) return;
        _glideDisplayLogStage = stage; _glideDisplayLogNext = now + 1000;
        try
        {
            if (_glideDisplayLogPath is null)
            {
                Directory.CreateDirectory(_glideDisplayLogDirectory);
                _glideDisplayLogPath = Path.Combine(_glideDisplayLogDirectory, $"display-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
                using var fresh = new FileStream(_glideDisplayLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            uint rgbOr = 0; int nonBlack = 0;
            if (stage == "captured") foreach (uint pixel in _glidePixels)
            { uint rgb = pixel & 0xFFFFFF; rgbOr |= rgb; if (rgb != 0) nonBlack++; }
            File.AppendAllText(_glideDisplayLogPath, JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, stage, fullContent, legacyFallback, focused = IsFocused,
                window = (long)_glideWindow, generation = _glideGeneration,
                width = _glideWidth, height = _glideHeight, revision = _glideRevision,
                rendererVisible = _glideWindow != 0 && IsWindowVisible(_glideWindow),
                rendererMinimized = _glideWindow != 0 && IsIconic(_glideWindow), rgbOr, nonBlack
            }) + Environment.NewLine);
            _glideDisplayLogRecords++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _glideDisplayLogDirectory = null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }
    /// <summary>Checks whether the renderer handle is still live.</summary>
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    /// <summary>Reports only this own backing window's renderable state for optional local diagnostics.</summary>
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    /// <summary>Reports whether this own renderer was minimized, without activating or restoring it.</summary>
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    /// <summary>Reads outer window bounds for the provider image crop.</summary>
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectangleNative rectangle);
    /// <summary>Reads the provider's drawable client area.</summary>
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RectangleNative rectangle);
    /// <summary>Converts the provider client origin to screen coordinates.</summary>
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref PointNative point);
    /// <summary>Keeps the provider window away from the visible emulator.</summary>
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    /// <summary>Copies one provider frame into a GDI bitmap for the existing GPU display path.</summary>
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
}
