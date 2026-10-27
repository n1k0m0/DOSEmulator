// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class SoundBlaster16
{
    private readonly byte[] _mixer = new byte[256];
    private Opl3? _fm;
    /// <summary>Connects the board's FM source to its independent mixer gains and output routing.</summary>
    public void AttachFm(Opl3 fm)
    {
        if (_fm is not null) throw new InvalidOperationException("CT1740 FM is already connected.");
        _fm = fm; UpdateVolume();
    }
    /// <summary>Diagnostic count of unsupported mixer accesses.</summary>
    public long UnsupportedMixerAccesses { get; private set; }
    /// <summary>Counts harmless CT1345 output-control probes, a register absent from the CT1745 register map.</summary>
    public long LegacyOutputControlAccesses { get; private set; }
    /// <summary>Diagnostic count of rejected resource writes.</summary>
    public long RejectedResourceWrites { get; private set; }
    /// <summary>Derived left guest analog gain under the limited CT1745 mixer contract.</summary>
    public double LeftAnalogGain => _left.DeviceGain;
    /// <summary>Derived right guest analog gain under the limited CT1745 mixer contract.</summary>
    public double RightAnalogGain => _right.DeviceGain;
    /// <summary>Restores the concrete card's reference mixer registers independently from DSP stream reset.</summary>
    private void ResetMixer()
    {
        Array.Clear(_mixer);
        for (int i = 0x30; i <= 0x35; i++) _mixer[i] = 24 << 3;
        UpdateVolume(); // DSP state and board resource registers are independent of the CT1745 reset.
    }
    /// <summary>Encodes a supported IRQ as its card-resource register bit.</summary>
    private static byte IrqBit(int irq) => irq switch { 9 => 1, 5 => 2, 7 => 4, 10 => 8, _ => 0 };
    /// <summary>Encodes a supported DMA resource as its card-resource register bit.</summary>
    private static byte DmaBit(int channel) => channel < 0 ? (byte)0 : (byte)(1 << channel);
    /// <summary>Converts a valid mixer DMA selection into the corresponding controller channel.</summary>
    private static int SingleChannel(int value) => value == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount((uint)value);
    /// <summary>Recognizes a legal one-resource-or-disabled bit selection before accepting a mixer resource write.</summary>
    private static bool SingleBitOrZero(int value) => (value & (value - 1)) == 0;
    /// <summary>Returns a readable mixer register without accidentally acknowledging a DSP interrupt source.</summary>
    private uint ReadMixer()
    {
        switch (_mixerIndex)
        {
            case 0x80: return IrqBit(Irq);
            case 0x81: return (uint)(DmaBit(Dma8Channel) | DmaBit(Dma16Channel));
            case 0x82: return _interruptSources;
            case 0: return 0;
            // Creative lists 0Eh only for CT1345, not CT1745. Keep the existing
            // stable FFh reserved-register response without inventing an SB Pro
            // stereo switch or analog filter in this DSP4.xx card.
            case 0x0e: LegacyOutputControlAccesses++; return 0xff;
            case 0x04: return (uint)((_mixer[0x32] & 0xf0) | _mixer[0x33] >> 4);
            case 0x22: return (uint)((_mixer[0x30] & 0xf0) | _mixer[0x31] >> 4);
            case 0x26: return (uint)((_mixer[0x34] & 0xf0) | _mixer[0x35] >> 4);
            case >= 0x30 and <= 0x35: case 0x41: case 0x42: return _mixer[_mixerIndex];
            default: UnsupportedMixerAccesses++; return 0xff;
        }
    }
    /// <summary>Applies a guest mixer write through the model's register/resource/volume contract.</summary>
    private void WriteMixer(byte value)
    {
        switch (_mixerIndex)
        {
            case 0: ResetMixer(); return;
            case 0x0e: LegacyOutputControlAccesses++; return;
            case 0x82: return; // Read-only IRQ-source status, never an acknowledgment.
            case 0x80:
                int bits = value & 15;
                if (!SingleBitOrZero(bits)) { RejectedResourceWrites++; return; }
                int next = bits switch { 1 => 9, 2 => 5, 4 => 7, 8 => 10, _ => -1 };
                if (next == Irq) return;
                if (_lineHigh && Irq >= 0) _irq(Irq, false);
                _lineHigh = false; Irq = next; UpdateIrq(); return;
            case 0x81:
                int low = value & 0x0b, high = value & 0xe0;
                if (!SingleBitOrZero(low) || !SingleBitOrZero(high)) { RejectedResourceWrites++; return; }
                Dma8Channel = SingleChannel(low); Dma16Channel = SingleChannel(high); return;
            case 0x04: case 0x22: case 0x26:
                int register = _mixerIndex == 4 ? 0x32 : _mixerIndex == 0x26 ? 0x34 : 0x30;
                // CT1345's 16 nominal levels span -60..0 dB; CT1745 uses -62..0 in 2-dB steps.
                _audio.AdvanceTo(_clock.Now);
                _mixer[register] = (byte)((value & 0xf0) | 8);
                _mixer[register + 1] = (byte)((value << 4 & 0xf0) | 8);
                UpdateVolume(); return;
            case >= 0x30 and <= 0x35:
                _audio.AdvanceTo(_clock.Now); _mixer[_mixerIndex] = (byte)(value & 0xf8); UpdateVolume(); return;
            case 0x41: case 0x42:
                _audio.AdvanceTo(_clock.Now); _mixer[_mixerIndex] = (byte)(value & 0xc0); UpdateVolume(); return;
            default: UnsupportedMixerAccesses++; return;
        }
    }
    /// <summary>Recomputes reference left/right guest analog gains at the actual register-write boundary.</summary>
    private void UpdateVolume()
    {
        // Converts the concrete mixer's programmed level into the documented nominal analog gain.
        static double Gain(byte master, byte voice, byte output) => Math.Pow(10,
            (2 * ((master >> 3) - 31) + 2 * ((voice >> 3) - 31) + 6 * (output >> 6)) / 20.0);
        _left.DeviceGain = Gain(_mixer[0x30], _mixer[0x32], _mixer[0x41]);
        _right.DeviceGain = Gain(_mixer[0x31], _mixer[0x33], _mixer[0x42]);
        _fm?.SetMixerGains(Gain(_mixer[0x30], _mixer[0x34], _mixer[0x41]), Gain(_mixer[0x31], _mixer[0x35], _mixer[0x42]));
    }
}
