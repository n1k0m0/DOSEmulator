// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using System.Numerics;

namespace DOSEmulator.Hardware;

/// <summary>Development pair of edge-triggered 8259-compatible controllers, fixed priority and normal EOI.</summary>
public sealed partial class Pic8259 : IIoPortDevice
{
    private sealed partial class Chip(byte vector)
    {
        public byte Vector = vector, Mask = 0xff, Irr, Isr, Lines;
        public int InitStep;
        public bool NeedsIcw4, Single, AutoEoi, ReadIsr;
        /// <summary>Selects the highest eligible request under the current mask and in-service priority.</summary>
        public int Pending()
        {
            int activePriority = Isr == 0 ? 8 : BitOperations.TrailingZeroCount((uint)Isr);
            uint ready = (uint)(Irr & ~Mask & ((1 << activePriority) - 1));
            return ready == 0 ? -1 : BitOperations.TrailingZeroCount(ready);
        }
        /// <summary>Updates an external interrupt input, recognizing only the model's eligible edge/level transitions.</summary>
        public void SetLine(int irq, bool high)
        {
            byte bit = (byte)(1 << irq);
            if (high && (Lines & bit) == 0) Irr |= bit;
            Lines = high ? (byte)(Lines | bit) : (byte)(Lines & ~bit);
        }
        /// <summary>Selects and acknowledges an eligible interrupt, including cascade and spurious-vector behavior.</summary>
        public int Acknowledge()
        {
            int irq = Pending();
            if (irq < 0) return -1;
            Irr &= (byte)~(1 << irq);
            if (!AutoEoi) Isr |= (byte)(1 << irq);
            return Vector + irq;
        }
        /// <summary>Submits the requested operation through the component's normal command interface.</summary>
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
        /// <summary>Consumes PIC initialization data or updates the mask register under the current initialization phase.</summary>
        public void Data(byte value)
        {
            if (InitStep == 1) { Vector = (byte)(value & 0xf8); InitStep = Single ? NeedsIcw4 ? 3 : 0 : 2; }
            else if (InitStep == 2) InitStep = NeedsIcw4 ? 3 : 0;
            else if (InitStep == 3) { AutoEoi = (value & 2) != 0; InitStep = 0; }
            else Mask = value;
        }
    }
    private readonly Chip _master = new(8), _slave = new(0x70);
    private int _pendingMaster = -1;
    /// <summary>Accepted physical IRQ, current vector and spurious-cascade flag; distinct from CPU exceptions sharing that vector.</summary>
    public event Action<int, byte, bool>? InterruptAcknowledged;
    /// <summary>Updates an external interrupt input, recognizing only the model's eligible edge/level transitions.</summary>
    public void SetLine(int irq, bool high)
    {
        if ((uint)irq >= 16) throw new ArgumentOutOfRangeException(nameof(irq));
        Chip chip = irq < 8 ? _master : _slave;
        if (irq != 2 && ((chip.Lines >> (irq & 7)) & 1) == (high ? 1 : 0)) return;
        if (irq < 8) _master.SetLine(irq, high); else _slave.SetLine(irq - 8, high);
        RefreshCascade();
    }
    /// <summary>Updates the slave-to-master PIC cascade level after priority, mask or request changes.</summary>
    private void RefreshCascade()
    { _master.SetLine(2, _slave.Pending() >= 0); _pendingMaster = _master.Pending(); }
    /// <summary>Whether the PIC has an unmasked request eligible under its current priority/service state.</summary>
    public bool HasPending => _pendingMaster >= 0;
    /// <summary>Selects and acknowledges an eligible interrupt, including cascade and spurious-vector behavior.</summary>
    public byte? Acknowledge()
    {
        int irq = _pendingMaster; int vector = _master.Acknowledge();
        if (vector < 0) return null;
        if (irq == 2)
        {
            int slaveIrq = _slave.Pending(), slaveVector = _slave.Acknowledge();
            byte result = (byte)(slaveVector < 0 ? _slave.Vector + 7 : slaveVector);
            RefreshCascade(); InterruptAcknowledged?.Invoke(slaveIrq < 0 ? 15 : slaveIrq + 8, result, slaveVector < 0); return result;
        }
        RefreshCascade(); InterruptAcknowledged?.Invoke(irq, (byte)vector, false); return (byte)vector;
    }
    /// <summary>Reads the selected PIC mask/request/service register under its current OCW selection.</summary>
    public uint Read(ushort port, int width)
    {
        Chip chip = port >= 0xa0 ? _slave : _master;
        return (port & 1) != 0 ? chip.Mask : chip.ReadIsr ? chip.Isr : chip.Irr;
    }
    /// <summary>Processes ICW/OCW commands, masks and EOIs and refreshes cascaded interrupt eligibility.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        Chip chip = port >= 0xa0 ? _slave : _master;
        if ((port & 1) != 0) chip.Data((byte)value); else chip.Command((byte)value);
        RefreshCascade();
    }
}
