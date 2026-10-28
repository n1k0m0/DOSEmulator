// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    // ET4000AX ISA, 1 MiB DRAM, 6-bit VGA DAC. No W32, accelerator, high-color DAC or option-ROM backend.
    public bool IsEt4000 { get; }
    /// <summary>Installed adapter DRAM bytes, distinct from the CPU aperture size.</summary>
    public int VideoMemoryBytes => _planes[0].Length * 4;
    /// <summary>Raw ET4000 read/write bank latch with independent high/low nibbles.</summary>
    public byte SegmentSelect => _segmentSelect;
    /// <summary>Whether the ET4000 key sequence currently permits protected extension writes.</summary>
    public bool ExtendedRegistersUnlocked => _etKey;
    /// <summary>Whether the ET4000 bank port has been enabled since the relevant power/reset boundary.</summary>
    public bool SegmentRegisterEnabled => _etSegmentEnabled;
    /// <summary>Diagnostic count of bank switch writes.</summary>
    public long BankSwitchWrites { get; private set; }
    private byte _segmentSelect, _hercules, _modeControl;
    private bool _etKey, _etSegmentEnabled;
    /// <summary>Address mask within one installed VGA/ET4000 backing plane.</summary>
    private int PlaneAddressMask => _planes[0].Length - 1;

    /// <summary>Combines the active ET4000 read/write bank with the memory addressing mode's per-plane offset.</summary>
    private int BankAddress(int address, bool write)
    {
        if (IsTrio64) return address + (TrioBank << 14);
        if (!IsEt4000) return address & 65535;
        int bank = write ? _segmentSelect & 15 : _segmentSelect >> 4;
        // Tseng Data Book 1990, table 4.3-4: PL starts at MD16 in LG1,
        // MD17 in OE1 and MD18 in PG. Each stored plane omits MD0/MD1.
        bool oddEven = write ? (_seq[4] & 4) == 0 : (_gc[5] & 0x10) != 0;
        int shift = (_seq[4] & 8) != 0 ? 14 : oddEven ? 15 : 16;
        return (address | (bank << shift)) & PlaneAddressMask;
    }

    /// <summary>Classifies the CRTC index as an implemented ET4000 extension.</summary>
    private bool ExtendedCrtc(byte index) => IsEt4000 && index is >= 0x32 and <= 0x37;
    /// <summary>Checks CRTC register availability and the model's protected-write mask.</summary>
    private bool WritableCrtc(byte index) => index < 25 || ExtendedCrtc(index) && (_etKey || index is 0x33 or 0x35);
    /// <summary>Checks whether the selected CRTC register is implemented and readable in the active adapter model.</summary>
    private bool ReadableCrtc(byte index) => IsTrio64 ? ReadableTrioCrtc(index) : index < 25 || ExtendedCrtc(index);
    /// <summary>Processes the ET4000 key ports without allowing an inactive mono/color decoder to revive a stale unlock.</summary>
    private void WriteEtKey(ushort port, byte value)
    {
        if (port == 0x3bf) _hercules = value;
        else if (ActiveCrtc(port)) _modeControl = value;
        else return;
        _etKey = (_hercules & 3) == 3 && (_modeControl & 0xa0) == 0xa0;
        if (_etKey) _etSegmentEnabled = true;
    }
}
