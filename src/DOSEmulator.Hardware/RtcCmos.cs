using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Deterministic MC146818-compatible calendar/NVRAM subset. Periodic/alarm IRQs follow later.</summary>
public sealed class RtcCmos(DeviceScheduler scheduler, int ramMiB, byte floppyTypes = 0) : IIoPortDevice
{
    private readonly byte[] _nvram = new byte[128];
    private readonly DateTime _epoch = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private byte _index;
    private byte _statusB = 2;
    public uint Read(ushort port, int width)
    {
        if (port == 0x70) return _index;
        if (port != 0x71 || width != 1) return 0xff;
        DateTime now = _epoch.AddSeconds(scheduler.Now / 1_000_000_000);
        byte Encode(int value) => (_statusB & 4) != 0 ? (byte)value : (byte)((value / 10 << 4) | value % 10);
        return (_index & 127) switch
        {
            0 => Encode(now.Second), 2 => Encode(now.Minute), 4 => Encode(now.Hour),
            6 => Encode((int)now.DayOfWeek + 1), 7 => Encode(now.Day), 8 => Encode(now.Month),
            9 => Encode(now.Year % 100), 0x32 => Encode(now.Year / 100),
            0x0a => 0x26, 0x0b => _statusB, 0x0c => 0, 0x0d => 0x80,
            0x10 => floppyTypes, 0x14 => floppyTypes == 0 ? 0x20u : 0x21u, 0x15 => 0x7f, 0x16 => 2,
            0x17 or 0x30 => (byte)((ramMiB - 1) * 1024),
            0x18 or 0x31 => (byte)(((ramMiB - 1) * 1024) >> 8),
            _ => _nvram[_index & 127]
        };
    }
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == 0x70) _index = (byte)value;
        else if (port == 0x71)
        {
            if ((_index & 127) == 0x0b) _statusB = (byte)value;
            else _nvram[_index & 127] = (byte)value;
        }
    }
}
