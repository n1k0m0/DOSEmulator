// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    // S3 DB014-B, March 1995, 86C764 PCI board with 2 MiB FPM DRAM. No 86C765, accelerator or high-color scanout.
    private byte _trioSetup, _trioPos;
    private ushort _trioAdvanced;

    /// <summary>Construction invariant selecting the 86C764 register and two-MiB storage contract.</summary>
    public bool IsTrio64 { get; }
    /// <summary>Setup-control latch, independent of normal PCI I/O and memory enables.</summary>
    public byte TrioSetup => _trioSetup;
    /// <summary>POS video-subsystem enable latch, written only during setup mode.</summary>
    public byte TrioPos => _trioPos;
    /// <summary>Implemented Advanced Function Control latch; drawing-command execution remains absent.</summary>
    public ushort TrioAdvanced => _trioAdvanced;
    /// <summary>Selected setup port; the alternate address permits explicit second-card initialization.</summary>
    public ushort TrioSetupPort => (_crtc[0x65] & 4) == 0 ? (ushort)0x46e8 : (ushort)0x3c3;
    /// <summary>Whether the original setup/POS sequence has enabled the ordinary video target decoders.</summary>
    public bool TrioVideoEnabled => IsTrio64 && (_trioSetup & 0x18) == 8 && (_trioPos & 1) != 0;
    /// <summary>Whether POS access is currently selected instead of ordinary video decoding.</summary>
    public bool TrioSetupMode => IsTrio64 && (_trioSetup & 0x10) != 0;
    /// <summary>Canonical eight-MiB PCI resource base, shared with CR59 and bit 7 of CR5A.</summary>
    public uint TrioBarBase => (uint)_crtc[0x59] << 24 | (uint)(_crtc[0x5a] & 0x80) << 16;
    /// <summary>Programmed size-aligned linear window position, including the independent low seven CR5A bits.</summary>
    public uint TrioLinearBase => (((uint)_crtc[0x59] << 24) | ((uint)_crtc[0x5a] << 16)) & ~(TrioLinearLength - 1);
    /// <summary>Programmed linear window size, independent from installed two-MiB storage and the PCI BAR resource.</summary>
    public uint TrioLinearLength => (_crtc[0x58] & 3) switch { 0 => 0x10000u, 1 => 0x100000u, 2 => 0x200000u, _ => 0x400000u };
    /// <summary>Linear decode requires both enhanced-mode prerequisites and either documented linear-enable bit.</summary>
    public bool TrioLinearEnabled => IsTrio64 && (_trioAdvanced & 1) != 0 && (_crtc[0x31] & 8) != 0 && (_misc & 2) != 0 && TrioLinearRequested;
    /// <summary>Current common 64-KiB bank, with the documented nonzero CR6A override and old-register fallback.</summary>
    public int TrioBank => (_crtc[0x31] & 1) == 0 ? 0 : (_crtc[0x6a] & 0x3f) != 0 ? _crtc[0x6a] & 0x3f :
        (_crtc[0x35] & 15) | ((_crtc[0x51] & 12) << 2);
    /// <summary>Combines the two original linear-enable latches independently from prerequisite mode and PCI gates.</summary>
    private bool TrioLinearRequested => ((_crtc[0x58] | _trioAdvanced) & 0x10) != 0;
    /// <summary>Recognizes every documented 01xx10xx key for the S3 VGA register group.</summary>
    private bool TrioVgaUnlocked => (_crtc[0x38] & 0xcc) == 0x48;
    /// <summary>Recognizes every documented 101xxxxx key for system and system-extension register access.</summary>
    private bool TrioSystemUnlocked => (_crtc[0x39] & 0xe0) == 0xa0;
    /// <summary>Selects non-planar enhanced functions only for the constructed original Trio64 model.</summary>
    private bool TrioEnhanced => IsTrio64 && (_trioAdvanced & 1) != 0;
    /// <summary>Identifies the current indexed scanout while keeping direct-color RAMDAC modes separate.</summary>
    private bool TrioEnhanced8 => TrioEnhancedColorBits == 8;
    /// <summary>Derives the supported original-Trio64 RAMDAC format without storing a second mode latch.</summary>
    private int TrioEnhancedColorBits => IsTrio64 ? TrioScanoutColorBits(_crtc, _trioAdvanced) : 0;
    /// <summary>Decodes DB014-B table8-1 after the enhanced DWORD and packed-pixel prerequisites.</summary>
    private static int TrioScanoutColorBits(ReadOnlySpan<byte> crtc, ushort advanced)
    {
        if ((advanced & 1) == 0 || (crtc[0x31] & 8) == 0 || (crtc[0x3a] & 0x10) == 0) return 0;
        // CR50's pixel length selects graphics-engine commands; it does not select the direct-color RAMDAC format.
        return (crtc[0x67] >> 4) switch
        {
            0 when (advanced & 4) == 0 && (crtc[0x50] & 0x30) == 0 => 8,
            3 => 15, 5 => 16, 13 => 24,
            _ => 0 // Packed24 mode7 belongs to Trio32; output-doubled indexed mode1 remains outside this path.
        };
    }
    /// <summary>Combines the logical-screen-width extension and its nonzero override of the older single overflow bit.</summary>
    private int TrioOffset => _crtc[0x13] | ((_crtc[0x51] & 0x30) != 0 ? (_crtc[0x51] & 0x30) << 4 : (_crtc[0x43] & 4) << 6);
    /// <summary>Combines the low start word with the nonzero CR69 override or the original CR31/CR51 extension.</summary>
    private int TrioDisplayStart => _crtc[12] << 8 | _crtc[13] | ((_crtc[0x69] & 15) != 0 ? (_crtc[0x69] & 15) << 16 :
        ((_crtc[0x31] & 0x30) << 12) | ((_crtc[0x51] & 3) << 18));
    /// <summary>Derives visible character-clock width and the implemented VGA/enhanced pixel-clock division.</summary>
    private int TrioWidth => (_crtc[1] + ((_crtc[0x5d] & 2) << 7) + 1) * (GraphicsMode ? 8 : CharacterWidth) *
        ((_crtc[0x43] & 0x80) != 0 ? 2 : 1) / (!TrioEnhanced && Indexed256 && (_attr[0x10] & 0x40) != 0 ? 2 : 1);

    /// <summary>Returns the writable latch bits; real but unimplemented blocks are not advertised as reserved hardware.</summary>
    private static byte TrioCrtcMask(int index) => index switch
    {
        0x31 => 0x7f, 0x32 => 0x50, 0x33 => 0x7a, 0x34 => 0x17, 0x35 => 0x3f,
        0x36 => 0xfc, 0x37 or 0x38 or 0x39 or 0x3b or 0x3c or 0x41 => 0xff,
        0x3a => 0xbf, 0x40 => 0x11, 0x42 => 0x20, 0x43 => 0x84,
        0x45 => 0x11, 0x46 or 0x48 => 7, 0x47 or 0x49 or 0x4a or 0x4b or 0x4d => 0xff,
        0x4c => 15, 0x4e or 0x4f => 63,
        0x50 => 0xf5, 0x51 => 0x3f, 0x52 => 0xff, 0x53 => 0x51, 0x54 => 0xf8,
        0x55 => 0x94, 0x56 => 0x1f, 0x57 or 0x59 or 0x5a or 0x5c or 0x5d or 0x60 or 0x63 => 0xff,
        0x58 => 0x9b, 0x5e => 0x57, 0x65 => 0x1c, 0x66 => 0xc0, 0x67 => 0xf1,
        0x68 => 0xff, 0x69 => 0x0f, 0x6a => 0x3f, _ => 0
    };

    /// <summary>Defines implemented sequencer latches; PLL timing, feature connectors and S3 test logic are not simulated.</summary>
    private static byte TrioSequencerMask(int index) => index switch
    {
        0 => 3, 1 => 0x3d, 2 => 15, 3 => 0x3f, 4 => 14, 8 => 15,
        9 => 0x80, 0x0a => 0xff, 0x0b => 0xf3, 0x0d => 0xf1,
        >= 0x10 and <= 0x13 => 0x7f, 0x14 or 0x15 or 0x18 => 0xff, _ => 0
    };

    /// <summary>Initializes identification and explicitly chosen board straps without changing VRAM or virtual time.</summary>
    private void ResetTrioRegisters()
    {
        _trioSetup = _trioPos = 0; _trioAdvanced = 0; ResetTrioEngine(); ResetTrioCursor();
        _misc = 0;
        Array.Clear(_seq, 0, 5);
        Array.Clear(_attr, 0x10, 5);
        _crtc[0x2d] = 0x88; _crtc[0x2e] = 0x11; _crtc[0x2f] = 0;
        _crtc[0x30] = 0xe1; _crtc[0x36] = 0x8e; _crtc[0x37] = 0x0a;
        _crtc[0x40] = 0x30; _crtc[0x68] = 0x80;
    }

    /// <summary>Checks canonical register bytes before a snapshot opens or branches media.</summary>
    public static void ValidateTrio64Registers(ReadOnlySpan<byte> crtc, ReadOnlySpan<byte> seq, ushort advanced, byte setup = 0, byte pos = 0)
    {
        if (crtc.Length != 128 || seq.Length != 32 || (advanced & ~0x35) != 0 || (setup & ~0x18) != 0 || (pos & ~1) != 0)
            throw new InvalidDataException("Invalid Trio64 register shape or control latch.");
        for (int i = 0; i < crtc.Length; i++)
        {
            if (i < 25) continue;
            int fixedValue = i switch { 0x2d => 0x88, 0x2e => 0x11, 0x30 => 0xe1, 0x36 => 2, 0x40 => 0x20, _ => 0 };
            int fixedMask = ~TrioCrtcMask(i) & 255;
            if ((crtc[i] & fixedMask) != (fixedValue & fixedMask))
                throw new InvalidDataException($"Invalid Trio64 CRTC bits at {i:X2}.");
        }
        for (int i = 0; i < seq.Length; i++)
            if ((seq[i] & ~TrioSequencerMask(i)) != 0)
                throw new InvalidDataException($"Invalid Trio64 sequencer bits at {i:X2}.");
    }

    /// <summary>Derives the exact mode cache for validated Trio64 register arrays without constructing a machine or touching media.</summary>
    public static (bool Graphics, bool Indexed, int Width, int Height) GetTrio64ModeKey(
        ReadOnlySpan<byte> crtc, ReadOnlySpan<byte> seq, ReadOnlySpan<byte> gc, ReadOnlySpan<byte> attr, ushort advanced)
    {
        if (crtc.Length != 128 || seq.Length != 32 || gc.Length != 9 || attr.Length != 21)
            throw new InvalidDataException("Invalid Trio64 mode register shape.");
        bool enhanced = (advanced & 1) != 0;
        bool graphics = (gc[6] & 1) != 0 || enhanced;
        bool indexed = enhanced ? TrioScanoutColorBits(crtc, advanced) == 8 : graphics && (gc[5] & 0x40) != 0;
        int width = (crtc[1] + ((crtc[0x5d] & 2) << 7) + 1) * (graphics ? 8 : (seq[1] & 1) != 0 ? 8 : 9) *
            ((crtc[0x43] & 0x80) != 0 ? 2 : 1) / (!enhanced && indexed && (attr[0x10] & 0x40) != 0 ? 2 : 1);
        int vertical = crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + ((crtc[0x5e] & 2) << 9) + 1;
        int repeat = ((crtc[9] & 31) + 1) * ((crtc[9] & 0x80) != 0 ? 2 : 1);
        int height = Math.Max(1, graphics ? vertical / repeat : vertical / ((crtc[9] & 0x80) != 0 ? 2 : 1));
        return (graphics, indexed, width, height);
    }

    /// <summary>Checks model-specific availability and read locks; CR2D-2F and the CR38 key remain discoverable.</summary>
    private bool ReadableTrioCrtc(byte index) => index < 25 || index is >= 0x2d and <= 0x2f || index == 0x38 ||
        TrioCrtcMask(index) != 0 && (index < 0x40 ? TrioVgaUnlocked : TrioSystemUnlocked) || index == 0x30 && TrioVgaUnlocked;

    /// <summary>Applies normal/extended write locks before changing the single canonical register store.</summary>
    private void WriteTrioCrtc(byte index, byte value)
    {
        byte mask;
        if (index < 25)
        {
            mask = 255;
            if ((_crtc[0x11] & 0x80) != 0 && index < 8)
                mask = index == 7 ? (byte)(0x10 | ((_crtc[0x33] & 2) != 0 ? 0x42 : 0)) : (byte)0;
            if ((_crtc[0x35] & 0x10) != 0)
                mask &= index switch { 6 or 0x10 or 0x15 or 0x16 => (byte)0, 7 => (byte)0x52, 9 => (byte)0xdf, 0x11 => (byte)0xf0, _ => (byte)255 };
            if ((_crtc[0x35] & 0x20) != 0)
                mask &= index switch { <= 5 => (byte)0, 0x17 => (byte)0xfb, _ => (byte)255 };
        }
        else
        {
            mask = TrioCrtcMask(index);
            if (mask == 0 || index != 0x38 && !(index < 0x40 ? TrioVgaUnlocked : TrioSystemUnlocked)) return;
            if ((index is 0x36 or 0x37 or 0x68) && _crtc[0x39] != 0xa5) return;
        }
        if (WriteTrioCursorStack(index,value)) return;
        byte next = (byte)((_crtc[index] & ~mask) | (value & mask));
        if (index == 0x48)
        {
            if (_crtc[index] != next) { _crtc[index] = next; _pixelRevision++; }
            CommitTrioCursorOrigin(); return;
        }
        if (_crtc[index] == next) return;
        if (index is 0x35 or 0x51 or 0x6a) BankSwitchWrites++;
        _crtc[index] = next; _pixelRevision++; UpdateMode();
    }

    /// <summary>Returns locked/unsupported sequencer locations as an explicitly unimplemented read.</summary>
    private byte ReadTrioSequencer() => _seqIndex < _seq.Length && TrioSequencerMask(_seqIndex) != 0 &&
        (_seqIndex <= 8 || (_seq[8] & 15) == 6) ? _seq[_seqIndex] : (byte)255;

    /// <summary>Writes implemented sequencer latches only after the original xxxx0110 unlock.</summary>
    private void WriteTrioSequencer(byte value)
    {
        if (_seqIndex >= _seq.Length || _seqIndex > 8 && (_seq[8] & 15) != 6) return;
        byte mask = TrioSequencerMask(_seqIndex);
        byte next = (byte)(value & mask);
        if (_seq[_seqIndex] != next) { _seq[_seqIndex] = next; _pixelRevision++; UpdateMode(); }
    }

    /// <summary>Updates the mirrored PCI BAR bits without touching the independent low CR5A address bits or locks.</summary>
    internal void SetTrioBarBase(uint value)
    {
        _crtc[0x59] = (byte)(value >> 24);
        _crtc[0x5a] = (byte)(((uint)_crtc[0x5a] & 0x7fu) | ((value >> 16) & 0x80u));
    }

    /// <summary>Changes only setup address decoding; ongoing scanout storage and time are retained.</summary>
    internal void WriteTrioSetup(byte value) => _trioSetup = (byte)(value & 0x18);
    /// <summary>Changes POS only during the selected setup phase.</summary>
    internal void WriteTrioPos(byte value) { if (TrioSetupMode) _trioPos = (byte)(value & 1); }
    /// <summary>Advanced function bits are accessible only through the original Enhanced-register enable.</summary>
    internal bool TrioAdvancedAccessible => (_crtc[0x40] & 1) != 0;
    /// <summary>Stores defined control bits without inventing a completed drawing engine or MMIO execution.</summary>
    internal void WriteTrioAdvanced(ushort value)
    {
        if (!TrioAdvancedAccessible) return;
        ushort next = (ushort)(value & 0x35);
        if (_trioAdvanced != next) { _trioAdvanced = next; _pixelRevision++; UpdateMode(); }
    }

    /// <summary>Decodes enhanced A0000 mapping and the documented 64-KiB linear-window exception.</summary>
    private bool TrioAperture(uint offset, out int address)
    {
        address = 0;
        if ((_misc & 2) == 0 || offset >= 0x20000) return false;
        if ((_crtc[0x31] & 8) != 0)
        {
            if (offset >= 0x10000) return false;
            address = (int)offset;
        }
        else
        {
            uint start = ((_gc[6] >> 2) & 3) switch { 2 => 0x10000u, 3 => 0x18000u, _ => 0 };
            uint length = ((_gc[6] >> 2) & 3) switch { 0 => 0x20000u, 1 => 0x10000u, _ => 0x8000u };
            if (offset < start || offset - start >= length) return false;
            address = (int)(offset - start);
        }
        return !TrioLinearRequested || offset >= 0x10000 ||
            ((_crtc[0x31] & 1) != 0 && TrioLinearLength == 0x10000 && TrioLinearBase == 0xa0000);
    }

    /// <summary>Reads a valid LFB byte through the existing VGA latches; the two-MCLK write shortcut does not suppress read effects.</summary>
    internal byte ReadTrioLinearByte(uint offset)
    {
        if (TrioLinearLength == 0x10000) offset += (uint)TrioBank << 16;
        if (offset >= VideoMemoryBytes) return 255;
        byte value = ReadVgaMemory((int)offset, false);
        return (_crtc[0x53] & 0x40) == 0 ? value : (byte)(value << 4 | value >> 4);
    }

    /// <summary>Writes only installed LFB storage; unpopulated tail bytes never alias another bank.</summary>
    internal void WriteTrioLinearByte(uint offset, byte value)
    {
        if (TrioLinearLength == 0x10000) offset += (uint)TrioBank << 16;
        if (offset >= VideoMemoryBytes) return;
        if ((_crtc[0x53] & 0x40) != 0) value = (byte)(value << 4 | value >> 4);
        if ((_seq[0x0a] & _seq[0x15] & 0x80) != 0) WriteTrioRaw((int)offset, value);
        else WriteVgaMemory((int)offset, value, false);
    }

    /// <summary>Reads one interleaved physical DRAM byte; absent two-to-four-MiB addresses use the board's explicit open-bus policy.</summary>
    private byte ReadTrioRaw(int address) => (uint)address < (uint)VideoMemoryBytes ? _planes[address & 3][address >> 2] : (byte)255;
    /// <summary>Stores one interleaved physical DRAM byte while preserving pixel revision ownership.</summary>
    private void WriteTrioRaw(int address, byte value) { if ((uint)address < (uint)VideoMemoryBytes) StorePlane(address & 3, address >> 2, value); }
}
