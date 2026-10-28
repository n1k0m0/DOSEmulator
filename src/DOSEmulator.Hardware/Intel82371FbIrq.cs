// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Original 82371FB A1 function-zero IRQ subset: four active-low PCI pins, ISA selection and ELCR.
/// Optional active-high MIRQ peers belong to the IDE board; ISA/DMA controls and CF9 reset remain separate.</summary>
public sealed partial class Intel82371FbIrq : IPciConfigurationFunction, IIoPortDevice
{
    private readonly Pic8259 _pic;
    private readonly bool _supportsMirq;
    private readonly byte[] _config = new byte[256];
    private ushort _elcr, _isaLines;
    private byte _pirqAssertions, _mirqAssertions;

    /// <summary>Binds the powered bridge to the board's optional level-capable PIC pair before any guest access.</summary>
    public Intel82371FbIrq(Pic8259 pic) : this(pic, false) { }

    /// <summary>Fixes the optional motherboard-input capability at construction; old IRQ-only boards retain inert MBIRQ latches.</summary>
    public Intel82371FbIrq(Pic8259 pic, bool supportsMirq)
    {
        ArgumentNullException.ThrowIfNull(pic);
        if (!pic.SupportsLevelInputs) throw new ArgumentException("PIIX IRQ routing requires the board's level-capable PIC.", nameof(pic));
        _pic = pic; _supportsMirq = supportsMirq;
        for (int i = 0; i < _config.Length; i++) _config[i] = ResetValue(i);
        _pic.SetLevelMask(0);
    }

    /// <summary>Logical assertions of physical active-low PIRQA through PIRQD; bit zero names PIRQA.</summary>
    public byte PirqAssertions => _pirqAssertions;
    /// <summary>Whether this fixed board binds original-PIIX MIRQ0 and MIRQ1 inputs.</summary>
    public bool SupportsMirq => _supportsMirq;
    /// <summary>Saved high-active motherboard input pins, before MBIRQ routing and PIC sharing.</summary>
    public byte MirqAssertions => _mirqAssertions;
    /// <summary>Saved ordinary high-active ISA input pins, including inputs currently masked by a PCI route.</summary>
    public ushort IsaLines => _isaLines;
    /// <summary>IRQ-bit-numbered level selection: low-port writable F8h and high-port writable DEh.</summary>
    public ushort Elcr => _elcr;

    /// <summary>Returns documented subset reset bytes; unimplemented real control registers do not imply hardware effects.</summary>
    private static byte ResetValue(int offset) => offset switch
    {
        0x00 => 0x86, 0x01 => 0x80, 0x02 => 0x2e, 0x03 => 0x12,
        0x04 => 0x07, 0x07 => 0x02, 0x08 => 0x02, 0x0a => 0x01, 0x0b => 0x06,
        0x0e => 0x80, 0x4e => 0x03, >= 0x60 and <= 0x63 => 0x80,
        0x6a => 0x04, 0x70 or 0x71 => 0x80, _ => 0
    };

    /// <summary>Separates functional interrupt routes from the explicitly inert XBCS latch; MBIRQ is functional only on capable boards.</summary>
    private static byte WritableMask(int offset) => offset switch
    {
        0x04 => 0x08, 0x07 => 0x38, 0x4e => 0xf7,
        >= 0x60 and <= 0x63 => 0x8f, 0x6a => 0x04,
        0x70 or 0x71 => 0xcf, _ => 0
    };

    /// <summary>Checks a complete configuration transaction before any lane is consumed or published.</summary>
    private static void ValidateTransaction(int offset, int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    /// <summary>Reads the exact fixed identification and implemented configuration lanes without changing pins or time.</summary>
    public uint ReadConfiguration(int offset, int width)
    {
        ValidateTransaction(offset, width);
        uint value = 0;
        for (int lane = 0; lane < width; lane++) value |= (uint)_config[offset + lane] << (lane * 8);
        return value;
    }

    /// <summary>Commits masked lanes and then republishes the combined routing once, preserving each source independently.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        ValidateTransaction(offset, width);
        bool route = false;
        for (int lane = 0; lane < width; lane++)
        {
            int address = offset + lane;
            byte incoming = (byte)(value >> (lane * 8)), mask = WritableMask(address);
            if (address == 0x07) _config[address] &= (byte)~(incoming & mask);
            else _config[address] = (byte)((_config[address] & ~mask) | (incoming & mask));
            route |= address is >= 0x60 and <= 0x63 || _supportsMirq && (address is 0x70 or 0x71);
        }
        _config[0x0e] = (byte)((_config[0x6a] & 4) << 5);
        if (route) PublishPins();
    }

    /// <summary>Resolves only documented routable IRQ numbers; reserved nibbles latch but remain disconnected in this subset.</summary>
    private static int Route(byte value) => (value & 0x80) != 0 ? -1 : (value & 15) switch
    { 3 or 4 or 5 or 6 or 7 or 9 or 10 or 11 or 12 or 14 or 15 => value & 15, _ => -1 };

    /// <summary>Preserves the IRQ-only board ABI, where MBIRQ configuration latches have no connected peer.</summary>
    public static ushort ComputePicInputs(ReadOnlySpan<byte> configuration, ushort isaLines, byte assertions) =>
        ComputePicInputs(configuration, isaLines, assertions, false, 0);

    /// <summary>Combines independent PCI and motherboard pins; exclusive routes mask ISA even when idle, and sharing never overrides a PIRQ mask.</summary>
    public static ushort ComputePicInputs(ReadOnlySpan<byte> configuration, ushort isaLines, byte assertions,
        bool supportsMirq, byte mirqAssertions)
    {
        if (configuration.Length != 256) throw new ArgumentException("Expected 256 configuration bytes.", nameof(configuration));
        ushort routed = 0, active = 0;
        for (int pin = 0; pin < 4; pin++)
        {
            int irq = Route(configuration[0x60 + pin]);
            if (irq < 0) continue;
            ushort bit = (ushort)(1 << irq);
            routed |= bit;
            if ((assertions & (1 << pin)) != 0) active |= bit;
        }
        if (supportsMirq)
            for (int pin = 0; pin < 2; pin++)
            {
                byte control = configuration[0x70 + pin];
                int irq = Route(control);
                if (irq < 0) continue;
                ushort bit = (ushort)(1 << irq);
                if ((control & 0x40) == 0) routed |= bit;
                if ((mirqAssertions & (1 << pin)) != 0) active |= bit;
            }
        return (ushort)((isaLines & ~routed) | active);
    }

    /// <summary>Publishes each merged high-active PIC input; the cascade remains wholly owned by the PIC pair.</summary>
    private void PublishPins()
    {
        ushort pins = ComputePicInputs(_config, _isaLines, _pirqAssertions, _supportsMirq, _mirqAssertions);
        for (int irq = 0; irq < 16; irq++) if (irq != 2) _pic.SetLine(irq, (pins & (1 << irq)) != 0);
    }

    /// <summary>Sets one controlled virtual PCI source pin on the PC owner; no host interrupt or endpoint is involved.</summary>
    public void SetPirq(int pin, bool asserted)
    {
        if ((uint)pin >= 4) throw new ArgumentOutOfRangeException(nameof(pin));
        byte bit = (byte)(1 << pin);
        byte next = asserted ? (byte)(_pirqAssertions | bit) : (byte)(_pirqAssertions & ~bit);
        if (next == _pirqAssertions) return;
        _pirqAssertions = next; PublishPins();
    }

    /// <summary>Sets one fixed-board high-active motherboard peer; unavailable pins are rejected without storing or publishing a request.</summary>
    public void SetMirq(int pin, bool high)
    {
        if ((uint)pin >= 2) throw new ArgumentOutOfRangeException(nameof(pin));
        if (!_supportsMirq) throw new InvalidOperationException("This board has no bound MIRQ inputs.");
        byte bit = (byte)(1 << pin);
        byte next = high ? (byte)(_mirqAssertions | bit) : (byte)(_mirqAssertions & ~bit);
        if (next == _mirqAssertions) return;
        _mirqAssertions = next; PublishPins();
    }

    /// <summary>Retains one ordinary ISA input independently from any route that currently masks it.</summary>
    public void SetIsaLine(int irq, bool high)
    {
        if ((uint)irq >= 16 || irq == 2) throw new ArgumentOutOfRangeException(nameof(irq));
        ushort bit = (ushort)(1 << irq);
        ushort next = high ? (ushort)(_isaLines | bit) : (ushort)(_isaLines & ~bit);
        if (next == _isaLines) return;
        _isaLines = next; PublishPins();
    }

    /// <summary>Returns only the two byte ELCR ports; wider or unrelated reads float high with no hidden byte operations.</summary>
    public uint Read(ushort port, int width) => width switch
    {
        1 => port switch { 0x4d0 => (byte)_elcr, 0x4d1 => (byte)(_elcr >> 8), _ => 0xffu },
        2 => 0xffff, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width))
    };

    /// <summary>Writes one masked ELCR byte and samples level pins without advancing time or creating an edge on level-to-edge changes.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (width != 1) return;
        ushort next = port switch
        {
            0x4d0 => (ushort)(((uint)_elcr & 0xff00u) | (value & 0xf8u)),
            0x4d1 => (ushort)(((uint)_elcr & 0x00ffu) | ((value & 0xdeu) << 8)),
            _ => _elcr
        };
        if (next == _elcr) return;
        _elcr = next; _pic.SetLevelMask(next);
    }

    /// <summary>Checks the complete fixed-bit contract before accepting a saved bridge configuration.</summary>
    public static void ValidateConfigurationBytes(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != 256) throw new InvalidDataException("Invalid PIIX IRQ configuration size.");
        for (int i = 0; i < configuration.Length; i++)
        {
            if (i == 0x0e) continue;
            int fixedMask = ~WritableMask(i) & 0xff;
            if ((configuration[i] & fixedMask) != (ResetValue(i) & fixedMask))
                throw new InvalidDataException($"Invalid PIIX IRQ fixed configuration bits at {i:X2}.");
        }
        if (configuration[0x0e] != (configuration[0x6a] & 4) << 5)
            throw new InvalidDataException("PIIX multifunction header differs from its MSTAT latch.");
    }

    /// <summary>Checks scalar bounds only; loading must never republish pins or synthesize interrupt edges.</summary>
    private void ValidateState()
    {
        ValidateConfigurationBytes(_config);
        if ((_elcr & ~0xdef8) != 0 || (_pirqAssertions & ~15) != 0 || (_isaLines & 4) != 0 ||
            (_mirqAssertions & ~3) != 0 || !_supportsMirq && _mirqAssertions != 0)
            throw new InvalidDataException("Invalid PIIX IRQ pins or ELCR bits.");
    }

    /// <summary>Checks the owned PIC peer and derived physical pins at a stopped PC boundary.</summary>
    public bool HasPicBinding(Pic8259 pic) => ReferenceEquals(_pic, pic) && pic.SupportsLevelInputs && pic.LevelMask == _elcr &&
        (pic.InputLines & ~4) == (ComputePicInputs(_config, _isaLines, _pirqAssertions, _supportsMirq, _mirqAssertions) & ~4);
}
