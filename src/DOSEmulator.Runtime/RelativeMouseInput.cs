namespace DOSEmulator.Runtime;

/// <summary>Frontend capture state. Preserves fractional native deltas, converts down to PS/2 up,
/// and releases both guest buttons on every capture loss. No OS pointer access.</summary>
public sealed class RelativeMouseInput
{
    public sealed record Input(int X, int Y, int Buttons);
    private double _x, _y;
    public bool Captured { get; private set; }
    public int Buttons { get; private set; }
    public void Capture() { Captured = true; _x = _y = 0; Buttons = 0; }
    public Input Release() { Captured = false; Buttons = 0; _x = _y = 0; return new(0, 0, 0); }
    public Input? Move(double x, double y)
    {
        if (!Captured || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        _x += Math.Clamp(x, -32767, 32767); _y -= Math.Clamp(y, -32767, 32767);
        int dx = (int)_x, dy = (int)_y; _x -= dx; _y -= dy;
        return dx == 0 && dy == 0 ? null : new(dx, dy, Buttons);
    }
    public Input? Button(int bit, bool down)
    {
        if (!Captured || bit is not (1 or 2)) return null;
        int previous = Buttons; Buttons = down ? Buttons | bit : Buttons & ~bit;
        return previous == Buttons ? null : new(0, 0, Buttons);
    }
}
