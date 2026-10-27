// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Frontend capture state. Preserves fractional native deltas, converts down to PS/2 up,
/// and releases both guest buttons on every capture loss. No OS pointer access.</summary>
public sealed class RelativeMouseInput
{
    public sealed record Input(int X, int Y, int Buttons);
    private double _x, _y;
    /// <summary>Whether this relative-input adapter currently owns guest mouse capture.</summary>
    public bool Captured { get; private set; }
    /// <summary>Current emulated two-button state maintained by the local input policy.</summary>
    public int Buttons { get; private set; }
    /// <summary>Captures the component's current output into owned result storage.</summary>
    public void Capture() { Captured = true; _x = _y = 0; Buttons = 0; }
    /// <summary>Releases capture, clears accumulated fractional motion and balances the emulated buttons.</summary>
    public Input Release() { Captured = false; Buttons = 0; _x = _y = 0; return new(0, 0, 0); }
    /// <summary>Accumulates finite captured relative motion and emits bounded guest counts without losing fractional movement.</summary>
    public Input? Move(double x, double y)
    {
        if (!Captured || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        _x += Math.Clamp(x, -32767, 32767); _y -= Math.Clamp(y, -32767, 32767);
        int dx = (int)_x, dy = (int)_y; _x -= dx; _y -= dy;
        return dx == 0 && dy == 0 ? null : new(dx, dy, Buttons);
    }
    /// <summary>Updates only a valid captured button bit and emits a change when the guest state actually differs.</summary>
    public Input? Button(int bit, bool down)
    {
        if (!Captured || bit is not (1 or 2)) return null;
        int previous = Buttons; Buttons = down ? Buttons | bit : Buttons & ~bit;
        return previous == Buttons ? null : new(0, 0, Buttons);
    }
}
