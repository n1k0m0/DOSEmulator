using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Standard ID-00, two-button PS/2 mouse. Byte protocol and virtual sample clock;
/// no IntelliMouse extensions or electrical serial-line emulation.</summary>
public sealed class Ps2Mouse(DeviceScheduler scheduler)
{
    private Func<byte[], bool>? _output;
    private long _sampleEvent;
    private ulong _sampleRemainder;
    private byte _parameter, _buttons, _reportedButtons;
    private int _x, _y;
    private bool _wrap, _clockEnabled;
    private byte[] _lastReply = [0];
    public bool Enabled { get; private set; }
    public bool Remote { get; private set; }
    public bool Scaling2To1 { get; private set; }
    public byte Resolution { get; private set; } = 2;
    public byte SampleRate { get; private set; } = 100;
    public long PacketCount { get; private set; }
    public long CommandCount { get; private set; }
    public void ConnectOutput(Func<byte[], bool> output) => _output = output;
    public void SetClockEnabled(bool enabled) => _clockEnabled = enabled;

    /// <summary>Relative counts, positive X right, positive Y up; buttons bit 0 left/1 right.
    /// Host pixels are mapped to counts by the frontend; resolution describes the virtual sensor.</summary>
    public void Inject(int x, int y, int buttons)
    {
        if ((uint)buttons > 3) throw new ArgumentOutOfRangeException(nameof(buttons));
        _buttons = (byte)buttons;
        if (!Enabled && !Remote) return;
        _x = (int)Math.Clamp((long)_x + x, -1_000_000, 1_000_000);
        _y = (int)Math.Clamp((long)_y + y, -1_000_000, 1_000_000);
    }
    public void Write(byte value)
    {
        CommandCount++;
        if (_wrap && value is not (0xec or 0xff)) { Reply([value]); return; }
        if (_parameter != 0)
        {
            bool valid = _parameter == 0xe8 ? value <= 3 : value is 10 or 20 or 40 or 60 or 80 or 100 or 200;
            if (!valid) { Reply([0xfe]); return; }
            if (_parameter == 0xe8) Resolution = value; else { SampleRate = value; RestartSampling(); }
            _parameter = 0; Reply([0xfa]); return;
        }
        switch (value)
        {
            case 0xff: Defaults(); _wrap = false; Reply([0xfa, 0xaa, 0]); break;
            case 0xf6: Defaults(); Reply([0xfa]); break;
            case 0xf5: Enabled = false; StopSampling(); ClearMotion(); Reply([0xfa]); break;
            case 0xf4: Enabled = true; ClearMotion(); RestartSampling(); Reply([0xfa]); break;
            case 0xf3: case 0xe8: _parameter = value; Reply([0xfa]); break;
            case 0xf2: Reply([0xfa, 0]); break;
            case 0xf0: Remote = true; StopSampling(); ClearMotion(); Reply([0xfa]); break;
            case 0xea: Remote = false; ClearMotion(); RestartSampling(); Reply([0xfa]); break;
            case 0xee: _wrap = true; StopSampling(); Reply([0xfa]); break;
            case 0xec: _wrap = false; RestartSampling(); Reply([0xfa]); break;
            case 0xeb:
                byte[] packet = Packet();
                if (Reply([0xfa, .. packet])) { ClearMotion(); PacketCount++; }
                break;
            case 0xe9:
                // E9 button ordering differs from movement packets (IBM Mouse TR).
                byte status = (byte)((Remote ? 0x40 : 0) | (Enabled ? 0x20 : 0) | (Scaling2To1 ? 0x10 : 0) |
                    ((_buttons & 1) << 2) | ((_buttons >> 1) & 1));
                Reply([0xfa, status, Resolution, SampleRate]); break;
            case 0xe7: Scaling2To1 = true; Reply([0xfa]); break;
            case 0xe6: Scaling2To1 = false; Reply([0xfa]); break;
            case 0xfe: _output?.Invoke(_lastReply.ToArray()); break;
            default: Reply([0xfe]); break;
        }
    }
    private bool Reply(byte[] bytes)
    {
        if (_output?.Invoke(bytes) != true) return false;
        if (bytes[0] != 0xfe) _lastReply = bytes;
        return true;
    }
    private void Defaults()
    {
        Enabled = Remote = Scaling2To1 = false; Resolution = 2; SampleRate = 100; _parameter = 0;
        StopSampling(); ClearMotion();
    }
    private void ClearMotion() { _x = _y = 0; _reportedButtons = _buttons; }
    private void StopSampling() { if (_sampleEvent != 0) scheduler.Cancel(_sampleEvent); _sampleEvent = 0; }
    private void RestartSampling()
    {
        StopSampling(); _sampleRemainder = 0; ScheduleSample();
    }
    private void ScheduleSample()
    {
        if (!Enabled || Remote || _wrap) return;
        _sampleRemainder += 1_000_000_000UL;
        ulong delay = _sampleRemainder / SampleRate; _sampleRemainder %= SampleRate;
        _sampleEvent = scheduler.ScheduleAfter(delay, Sample);
    }
    private void Sample()
    {
        _sampleEvent = 0;
        if (_clockEnabled && (_x != 0 || _y != 0 || _buttons != _reportedButtons) && Reply(Packet()))
        { ClearMotion(); PacketCount++; }
        ScheduleSample();
    }
    private static int Scale(int value)
    {
        int magnitude = Math.Abs(value);
        return Math.Sign(value) * (magnitude switch { 0 => 0, 1 => 1, 2 => 1, 3 => 3, 4 => 6, 5 => 9, _ => magnitude * 2 });
    }
    private byte[] Packet()
    {
        int x = Math.Clamp(_x, -255, 255), y = Math.Clamp(_y, -255, 255);
        if (Scaling2To1 && !Remote) { x = Scale(x); y = Scale(y); }
        byte status = (byte)(8 | _buttons | (x < 0 ? 0x10 : 0) | (y < 0 ? 0x20 : 0) |
            (Math.Abs(_x) > 255 || Math.Abs(x) > 255 ? 0x40 : 0) | (Math.Abs(_y) > 255 || Math.Abs(y) > 255 ? 0x80 : 0));
        return [status, unchecked((byte)x), unchecked((byte)y)];
    }
}
