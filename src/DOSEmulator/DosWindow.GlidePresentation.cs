// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DOSEmulator;

public sealed partial class DosWindow
{
    private nint _glideWindow;
    private uint[] _glidePixels = [];
    private int _glideWidth, _glideHeight;
    private long _glideRevision;

    /// <summary>Copies the host renderer's client image into the regular guest viewport so the overlay and input stay in one window.</summary>
    private bool TryCaptureGlideFrame()
    {
        if (!_session.UsesExternalCpu || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOSE_GLIDE_HOST_DLL"))) return false;
        if (_glideWindow == 0 || !IsWindow(_glideWindow))
        {
            _glideWindow = FindOwnGlideWindow();
            if (_glideWindow == 0)
            { _glideWindow = 0; return false; }
        }
        if (!GetWindowRect(_glideWindow, out RectangleNative outer) || !GetClientRect(_glideWindow, out RectangleNative client)) return false;
        // Some providers reposition the swap-chain window after mode changes. Keep it off the desktop each frame.
        if (outer.Left > -1000 || outer.Top > -1000)
        {
            if (!SetWindowPos(_glideWindow, 0, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010) ||
                !GetWindowRect(_glideWindow, out outer)) return false;
        }
        PointNative origin = default;
        if (!ClientToScreen(_glideWindow, ref origin)) return false;
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        int outerWidth = outer.Right - outer.Left, outerHeight = outer.Bottom - outer.Top;
        int left = origin.X - outer.Left, top = origin.Y - outer.Top;
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || outerWidth is < 1 or > 4096 ||
            outerHeight is < 1 or > 4096 || left < 0 || top < 0 ||
            left + width > outerWidth || top + height > outerHeight) return false;
        using var bitmap = new Bitmap(outerWidth, outerHeight, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            nint dc = graphics.GetHdc();
            try { if (!PrintWindow(_glideWindow, dc, 0)) return false; }
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
        return true;
    }

    /// <summary>Finds only the renderer window owned by this emulator process.</summary>
    private static nint FindOwnGlideWindow()
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out uint processId) == 0 || processId != Environment.ProcessId) return true;
            var title = new StringBuilder(32);
            var klass = new StringBuilder(32);
            if (GetWindowTextW(window, title, title.Capacity) > 0 &&
                GetClassNameW(window, klass, klass.Capacity) > 0 &&
                title.ToString() == "MiniGlide" &&
                string.Equals(klass.ToString(), "Static", StringComparison.OrdinalIgnoreCase))
            { found = window; return false; }
            return true;
        }, 0);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    /// <summary>Enumerates top-level windows to find this process's renderer.</summary>
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    /// <summary>Reads the class name of a possible renderer window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder buffer, int length);
    /// <summary>Reads the title of a possible renderer window.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder buffer, int length);
    /// <summary>Checks whether the renderer handle is still live.</summary>
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    /// <summary>Matches a candidate renderer to the current emulator process.</summary>
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
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
