// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Whpx;

/// <summary>Uses real hidden Win32 backing surfaces and a finite own provider to check publication, confinement and joined lifetime.</summary>
internal static class NativeGlidePresentationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate ulong OpenCall(nint function, Span<ulong> arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong Provider(ulong window, ulong b, ulong c, ulong d, ulong e, ulong f, ulong g);
    private static int _checks;

    /// <summary>Counts independent presentation oracles and reports the first unexpected native effect.</summary>
    private static void Check(bool value, string message)
    { _checks++; if (!value) throw new InvalidOperationException(message); }

    /// <summary>Checks inactive/open/reopen/close/failure plus stale publication without constructing a GUI or sending native input.</summary>
    internal static void Run()
    {
        _checks = 0;
        Check(MiniGlidePresentation.Current is null, "Unexpected active renderer at entry.");
        var type = typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlidePassThrough", true)!;
        using var bridge = (IDisposable)Activator.CreateInstance(type, Private, null,
            [nint.Zero, new MemoryBus(2 << 20), Path.Combine(Environment.SystemDirectory, "kernel32.dll")], null)!;
        var open = type.GetMethod("OpenWindow", Private)!.CreateDelegate<OpenCall>(bridge);
        var close = type.GetMethod("CloseWindow", Private)!.CreateDelegate<Action>(bridge);
        bool initialRenderable = false, initialOffscreen = false, dimensionsKept = false;
        Provider provider = (handle, b, c, d, e, f, g) =>
        {
            nint window = (nint)handle;
            initialRenderable = IsWindowVisible(window);
            GetWindowRect(window, out var before); initialOffscreen = before.Left < -1000 && before.Top < -1000;
            SetWindowLongPtrW(window, -20, 0x00040000); // An unwanted provider APPWINDOW request.
            SetWindowPos(window, 0, 50, 60, 900, 700, 0x14); // NOACTIVATE + NOZORDER; no visible show.
            GetWindowRect(window, out var after); dimensionsKept = after.Right - after.Left == 900 && after.Bottom - after.Top == 700;
            return 1;
        };
        Span<ulong> arguments = stackalloc ulong[7];
        Check(open(Marshal.GetFunctionPointerForDelegate(provider), arguments) == 1, "Own provider was rejected.");
        var first = MiniGlidePresentation.Current!;
        Check(first is not null && first.Window == (nint)arguments[0], "WinOpen did not publish its exact substituted HWND.");
        Check(initialRenderable && initialOffscreen, "Backing window was not renderable and offscreen before provider initialization.");
        Check(dimensionsKept, "Confinement changed renderer-requested dimensions.");
        GetWindowRect(first!.Window, out var rectangle);
        Check(rectangle.Left < -1000 && rectangle.Top < -1000, "Provider moved backing surface onto the desktop.");
        long styles = GetWindowLongPtrW(first.Window, -20);
        Check((styles & 0x08000080) == 0x08000080 && (styles & 0x40000) == 0,
            "Provider removed NOACTIVATE/TOOLWINDOW or enabled APPWINDOW.");
        Check(IsWindowVisible(first.Window), "Backing window lost its renderable style.");
        SetWindowPos(first.Window, 0, 70, 80, 0, 0, 0x415); // Provider bypasses WM_WINDOWPOSCHANGING.
        GetWindowRect(first.Window, out rectangle);
        Check(rectangle.Left < -1000 && rectangle.Top < -1000, "NOSENDCHANGING exposed the backing surface.");
        Check(SendMessageW(first.Window, 0x21, 0, 0) == 3, "Backing surface accepted WM_MOUSEACTIVATE.");
        SendMessageW(first.Window, 0x10, 0, 0);
        Check(IsWindow(first.Window), "WM_CLOSE destroyed an active guest renderer.");
        Check(open(Marshal.GetFunctionPointerForDelegate(provider), arguments) == 1, "Reopen failed.");
        var second = MiniGlidePresentation.Current!;
        Check(second.Window == first.Window && second.Generation > first.Generation,
            "Reopening the same handle did not publish a fresh mode generation.");
        typeof(MiniGlidePresentation).GetMethod("Close", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [first]);
        Check(ReferenceEquals(MiniGlidePresentation.Current, second), "Retired surface cleared a newer generation.");
        close();
        Check(MiniGlidePresentation.Current is null && !IsWindow(first.Window), "Explicit close did not withdraw and join its HWND.");
        Provider failed = (a, b, c, d, e, f, g) => 0;
        Check(open(Marshal.GetFunctionPointerForDelegate(failed), arguments) == 0, "Failed provider result changed.");
        Check(MiniGlidePresentation.Current is null && !IsWindow((nint)arguments[0]), "Failed WinOpen left a published/live backing surface.");
        Check(type.GetField("_windowThread", Private)!.GetValue(bridge) is null, "Backing thread was not joined.");
        GC.KeepAlive(provider); GC.KeepAlive(failed);
        Console.WriteLine($"PASS: MiniGlide presentation {_checks} independent controls; offscreen surfaces, no host input or endpoint.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    /// <summary>Reads actual backing geometry after the provider's attempted move.</summary>
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rectangle rectangle);
    /// <summary>Checks actual HWND lifetime after the joined close.</summary>
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    /// <summary>Checks that the own finite provider never exposes a visible window.</summary>
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    /// <summary>Attempts a provider geometry change without activating or showing the hidden target.</summary>
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    /// <summary>Attempts a provider extended-style change on its own backing window.</summary>
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    /// <summary>Reads resulting native styles independently from the product callback.</summary>
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    /// <summary>Sends only own lifecycle/mouse-activation control messages; no keyboard or pointer input is injected.</summary>
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
