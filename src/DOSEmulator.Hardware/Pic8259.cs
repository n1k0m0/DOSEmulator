// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using System.Numerics;

namespace DOSEmulator.Hardware;

/// <summary>Fixed-priority 8259-compatible pair. The default preserves legacy edges; an explicit board may supply per-input levels.</summary>
public sealed partial class Pic8259 : IIoPortDevice
{
    private sealed partial class Chip(byte vector)
    {
        public byte Vector = vector, Mask = 0xff, Irr, Isr, Lines, LevelMask;
        public int InitStep;
        public bool NeedsIcw4, Single, AutoEoi, ReadIsr;
        /// <summary>Selects the highest eligible request under masks, in-service priority and the optional initialization gate.</summary>
        public int Pending(bool suppressDuringInitialization)
        {
            if (suppressDuringInitialization && InitStep != 0) return -1;
            int activePriority = Isr == 0 ? 8 : BitOperations.TrailingZeroCount((uint)Isr);
            uint ready = (uint)(Irr & ~Mask & ((1 << activePriority) - 1));
            return ready == 0 ? -1 : BitOperations.TrailingZeroCount(ready);
        }
        /// <summary>Updates one already-combined physical input without treating duplicate high calls as new edges.</summary>
        public void SetLine(int irq, bool high)
        {
            byte bit = (byte)(1 << irq);
            if ((LevelMask & bit) != 0) Irr = high ? (byte)(Irr | bit) : (byte)(Irr & ~bit);
            else if (high && (Lines & bit) == 0) Irr |= bit;
            Lines = high ? (byte)(Lines | bit) : (byte)(Lines & ~bit);
        }
        /// <summary>Samples newly level-sensitive inputs; returning to edge mode retains latched requests without inventing an edge.</summary>
        public void SetLevelMask(byte value)
        {
            int newlyLevel = value & ~LevelMask;
            Irr &= (byte)~(newlyLevel & ~Lines);
            Irr |= (byte)(newlyLevel & Lines & ~Isr);
            LevelMask = value;
        }
        /// <summary>Requests held levels after their service completes, without clearing already latched edge requests.</summary>
        private void RepublishLevels() => Irr |= (byte)(Lines & LevelMask & ~Isr);
        /// <summary>Acknowledges the selected request; auto-EOI immediately republishes a still asserted level.</summary>
        public int Acknowledge(bool suppressDuringInitialization)
        {
            int irq = Pending(suppressDuringInitialization);
            if (irq < 0) return -1;
            Irr &= (byte)~(1 << irq);
            if (!AutoEoi) Isr |= (byte)(1 << irq);
            else RepublishLevels();
            return Vector + irq;
        }
        /// <summary>Processes ICW/OCW while an explicitly capable board retains its physical pins and external level selection.</summary>
        public void Command(byte value, bool preservePhysicalInputs)
        {
            if ((value & 0x10) != 0)
            {
                InitStep = 1; NeedsIcw4 = (value & 1) != 0; Single = (value & 2) != 0;
                Irr = Isr = Mask = 0;
                if (preservePhysicalInputs) RepublishLevels(); else Lines = 0;
                AutoEoi = false; ReadIsr = false; return;
            }
            if ((value & 0x18) == 8) { if ((value & 2) != 0) ReadIsr = (value & 1) != 0; return; }
            if ((value & 0x20) != 0)
            {
                if ((value & 0x40) != 0) Isr &= (byte)~(1 << (value & 7));
                else for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { Isr &= (byte)~(1 << i); break; }
                RepublishLevels();
            }
        }
        /// <summary>Consumes initialization bytes or updates the ordinary interrupt mask.</summary>
        public void Data(byte value)
        {
            if (InitStep == 1) { Vector = (byte)(value & 0xf8); InitStep = Single ? NeedsIcw4 ? 3 : 0 : 2; }
            else if (InitStep == 2) InitStep = NeedsIcw4 ? 3 : 0;
            else if (InitStep == 3) { AutoEoi = (value & 2) != 0; InitStep = 0; }
            else Mask = value;
        }
    }
    private readonly Chip _master = new(8), _slave = new(0x70);
    private readonly bool _supportsLevelInputs;
    private int _pendingMaster = -1;
    /// <summary>Preserves the original public constructor and edge-only behavior.</summary>
    public Pic8259() : this(false) { }
    /// <summary>Enables only the explicitly requested board-level input mask; no I/O ports or PCI sources are implied.</summary>
    public Pic8259(bool supportsLevelInputs) => _supportsLevelInputs = supportsLevelInputs;
    /// <summary>Whether this constructor-fixed pair permits external per-input level selection.</summary>
    public bool SupportsLevelInputs => _supportsLevelInputs;
    /// <summary>Current per-IRQ level selection, with physical IRQn represented by bit n.</summary>
    public ushort LevelMask => (ushort)(_master.LevelMask | _slave.LevelMask << 8);
    /// <summary>Current physical pins including the internal slave-to-master cascade at IRQ2.</summary>
    public ushort InputLines => (ushort)(_master.Lines | _slave.Lines << 8);
    /// <summary>Changes the capable board's level selection atomically; ELCR0=F8h/ELCR1=DEh keep IRQ0/1/2/8/13 edge-only.</summary>
    public void SetLevelMask(ushort mask)
    {
        if (!_supportsLevelInputs) throw new InvalidOperationException("This PIC pair retains its edge-only board wiring.");
        if ((mask & ~0xdef8) != 0) throw new ArgumentOutOfRangeException(nameof(mask), "Level inputs are limited to physical IRQ bits DEF8h.");
        _master.SetLevelMask((byte)mask); _slave.SetLevelMask((byte)(mask >> 8));
        RefreshCascade();
    }
    /// <summary>Accepted physical IRQ, current vector and spurious-cascade flag; distinct from CPU exceptions sharing that vector.</summary>
    public event Action<int, byte, bool>? InterruptAcknowledged;
    /// <summary>Updates one external pin, already combined by the board's source-routing policy.</summary>
    public void SetLine(int irq, bool high)
    {
        if ((uint)irq >= 16) throw new ArgumentOutOfRangeException(nameof(irq));
        Chip chip = irq < 8 ? _master : _slave;
        if (irq != 2 && ((chip.Lines >> (irq & 7)) & 1) == (high ? 1 : 0)) return;
        if (irq < 8) _master.SetLine(irq, high); else _slave.SetLine(irq - 8, high);
        RefreshCascade();
    }
    /// <summary>Updates the slave INT output and fixed edge-sensitive master cascade without clearing latched requests.</summary>
    private void RefreshCascade()
    { _master.SetLine(2, _slave.Pending(_supportsLevelInputs) >= 0); _pendingMaster = _master.Pending(_supportsLevelInputs); }
    /// <summary>Whether a request is eligible under the current initialization, mask and in-service state.</summary>
    public bool HasPending => _pendingMaster >= 0;
    /// <summary>Acknowledges both PICs when needed and republishes the slave INT output after an actual cascade acknowledge.</summary>
    public byte? Acknowledge()
    {
        int irq = _pendingMaster; int vector = _master.Acknowledge(_supportsLevelInputs);
        if (vector < 0) return null;
        if (irq == 2)
        {
            if (_supportsLevelInputs) _master.SetLine(2, false);
            int slaveIrq = _slave.Pending(_supportsLevelInputs), slaveVector = _slave.Acknowledge(_supportsLevelInputs);
            byte result = (byte)(slaveVector < 0 ? _slave.Vector + 7 : slaveVector);
            RefreshCascade(); InterruptAcknowledged?.Invoke(slaveIrq < 0 ? 15 : slaveIrq + 8, result, slaveVector < 0); return result;
        }
        RefreshCascade(); InterruptAcknowledged?.Invoke(irq, (byte)vector, false); return (byte)vector;
    }
    /// <summary>Reads the selected mask/request/service register without sampling new pins or changing requests.</summary>
    public uint Read(ushort port, int width)
    {
        Chip chip = port >= 0xa0 ? _slave : _master;
        return (port & 1) != 0 ? chip.Mask : chip.ReadIsr ? chip.Isr : chip.Irr;
    }
    /// <summary>Processes ICW/OCW and refreshes cascade eligibility without changing legacy default initialization behavior.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        Chip chip = port >= 0xa0 ? _slave : _master;
        int previousInitStep = chip.InitStep;
        if ((port & 1) != 0) chip.Data((byte)value); else chip.Command((byte)value, _supportsLevelInputs);
        // A master-only reinitialization must not lose a slave INT that stayed high throughout its ICW sequence.
        if (_supportsLevelInputs && ReferenceEquals(chip, _master) && previousInitStep != 0 && chip.InitStep == 0)
            _master.SetLine(2, false);
        RefreshCascade();
    }
}
