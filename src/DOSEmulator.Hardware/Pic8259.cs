using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Development pair of edge-triggered 8259-compatible controllers, fixed priority and normal EOI.</summary>
public sealed class Pic8259 : IIoPortDevice
{
    private sealed class Chip(byte vector)
    {
        public byte Vector = vector, Mask = 0xff, Irr, Isr, Lines;
        public int InitStep;
        public bool NeedsIcw4, Single, AutoEoi, ReadIsr;
        public int Pending()
        {
            int activePriority = 8;
            for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { activePriority = i; break; }
            for (int i = 0; i < activePriority; i++) if ((Irr & ~Mask & (1 << i)) != 0) return i;
            return -1;
        }
        public void SetLine(int irq, bool high)
        {
            byte bit = (byte)(1 << irq);
            if (high && (Lines & bit) == 0) Irr |= bit;
            Lines = high ? (byte)(Lines | bit) : (byte)(Lines & ~bit);
        }
        public int Acknowledge()
        {
            int irq = Pending();
            if (irq < 0) return -1;
            Irr &= (byte)~(1 << irq);
            if (!AutoEoi) Isr |= (byte)(1 << irq);
            return Vector + irq;
        }
        public void Command(byte value)
        {
            if ((value & 0x10) != 0)
            {
                InitStep = 1; NeedsIcw4 = (value & 1) != 0; Single = (value & 2) != 0;
                Irr = Isr = Mask = Lines = 0; AutoEoi = false; ReadIsr = false; return;
            }
            if ((value & 0x18) == 8) { if ((value & 2) != 0) ReadIsr = (value & 1) != 0; return; }
            if ((value & 0x20) != 0)
            {
                if ((value & 0x40) != 0) Isr &= (byte)~(1 << (value & 7));
                else for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { Isr &= (byte)~(1 << i); break; }
            }
        }
        public void Data(byte value)
        {
            if (InitStep == 1) { Vector = (byte)(value & 0xf8); InitStep = Single ? NeedsIcw4 ? 3 : 0 : 2; }
            else if (InitStep == 2) InitStep = NeedsIcw4 ? 3 : 0;
            else if (InitStep == 3) { AutoEoi = (value & 2) != 0; InitStep = 0; }
            else Mask = value;
        }
    }
    private readonly Chip _master = new(8), _slave = new(0x70);
    public void SetLine(int irq, bool high)
    {
        if ((uint)irq >= 16) throw new ArgumentOutOfRangeException(nameof(irq));
        if (irq < 8) _master.SetLine(irq, high); else _slave.SetLine(irq - 8, high);
        RefreshCascade();
    }
    private void RefreshCascade() => _master.SetLine(2, _slave.Pending() >= 0);
    public bool HasPending { get { RefreshCascade(); return _master.Pending() >= 0; } }
    public byte? Acknowledge()
    {
        RefreshCascade(); int irq = _master.Pending(); int vector = _master.Acknowledge();
        if (vector < 0) return null;
        if (irq == 2) { int slaveVector = _slave.Acknowledge(); RefreshCascade(); return (byte)(slaveVector < 0 ? _slave.Vector + 7 : slaveVector); }
        return (byte)vector;
    }
    public uint Read(ushort port, int width)
    {
        Chip chip = port >= 0xa0 ? _slave : _master;
        return (port & 1) != 0 ? chip.Mask : chip.ReadIsr ? chip.Isr : chip.Irr;
    }
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        Chip chip = port >= 0xa0 ? _slave : _master;
        if ((port & 1) != 0) chip.Data((byte)value); else chip.Command((byte)value);
        RefreshCascade();
    }
}
