using System.Text;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed class DevelopmentBoardPorts(MemoryBus memory) : IIoPortDevice
{
    private readonly StringBuilder _debug = new();
    private byte _port92 = 2;
    public string DebugText => _debug.ToString();
    public byte PostCode { get; private set; }
    public bool ResetRequested { get; set; }
    public uint Read(ushort port, int width) => port == 0x92 ? _port92 : 0xffu;
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == 0xe9)
        {
            if (_debug.Length < 8192) _debug.Append((char)(value & 255));
        }
        else if (port == 0x80) PostCode = (byte)value;
        else if (port == 0x92)
        {
            _port92 = (byte)(value & 0xfe);
            memory.A20Enabled = (value & 2) != 0;
            if ((value & 1) != 0) ResetRequested = true;
        }
    }
}
