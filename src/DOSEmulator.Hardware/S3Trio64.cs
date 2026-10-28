// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>86C764 PCI target with one canonical VGA/DRAM peer and movable linear-window decoding.
/// This own two-MiB board does not expose a drawing engine, option-ROM image or interrupt source.</summary>
public sealed partial class S3Trio64 : IPciConfigurationFunction, IMemoryWordWriteDevice, IMemoryDecoder, IIoPortDevice
{
    private readonly MemoryBus _memory;
    private readonly VgaAdapter _video;
    private readonly byte[] _config = new byte[256];

    /// <summary>Binds the fixed PCI target to its sole two-MiB 86C764 video and physical resource peers.</summary>
    public S3Trio64(MemoryBus memory, VgaAdapter video)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(video);
        if (!video.IsTrio64 || video.IsEt4000 || video.VideoMemoryBytes != 0x200000)
            throw new ArgumentException("The PCI target requires its constructed two-MiB Trio64 video peer.", nameof(video));
        _memory = memory; _video = video;
        ResetConfiguration();
    }

    /// <summary>PCI I/O enable; DAC-snoop readback never bypasses this target gate.</summary>
    public bool IoSpaceEnabled => (_config[4] & 1) != 0;
    /// <summary>PCI memory enable for both legacy and movable memory targets.</summary>
    public bool MemorySpaceEnabled => (_config[4] & 2) != 0;
    /// <summary>Canonical eight-MiB resource base shared with the video's CR59/CR5A high address bits.</summary>
    public uint BarBase => _video.TrioBarBase;
    /// <summary>Actual programmed linear-window base, distinct from the BAR's resource alignment.</summary>
    public uint LinearBase => _video.TrioLinearBase;
    /// <summary>Actual linear-window extent: 64 KiB, one, two or four MiB.</summary>
    public uint LinearLength => _video.TrioLinearLength;
    /// <summary>Whether all prerequisite video/PCI gates and the complete board resource grant currently succeed.</summary>
    public bool LinearEnabled => MemorySpaceEnabled && _video.TrioVideoEnabled && _video.TrioLinearEnabled &&
        _memory.IsFixedWindowAvailable(BarBase, 0x800000);

    /// <summary>Checks construction peers without normalizing register values or changing address decoders.</summary>
    public bool HasBindings(MemoryBus memory, VgaAdapter video) => ReferenceEquals(_memory, memory) &&
        ReferenceEquals(_video, video) && video.IsTrio64 && !video.IsEt4000 && video.VideoMemoryBytes == 0x200000;

    /// <summary>Returns original PCI header defaults; revision 00 is the explicitly named DB014-B subset, not a measured stepping.</summary>
    private static byte ResetValue(int offset) => offset switch
    {
        0 => 0x33, 1 => 0x53, 2 => 0x11, 3 => 0x88, 7 => 0x02, 0x0b => 3,
        0x32 => 0x0c, 0x3d => 1, _ => 0
    };

    /// <summary>Defines implemented PCI latches; there is no PCI-master engine or attached expansion-ROM image.</summary>
    private static byte WritableMask(int offset) => offset switch
    {
        4 => 0x23, 0x30 => 1, 0x32 or 0x33 or 0x3c => 0xff, _ => 0
    };

    /// <summary>Resets PCI control latches; the canonical BAR is reset by the single video reset boundary.</summary>
    private void ResetConfiguration() { for (int i = 0; i < _config.Length; i++) _config[i] = ResetValue(i); }

    /// <summary>Applies one PCI reset to both sides of the target without clearing display DRAM or rewinding time.</summary>
    public void ApplyPiixHardReset() { ResetConfiguration(); _video.ApplyPiixHardReset(); }

    /// <summary>Rejects bad widths and incomplete requests before any byte-lane access.</summary>
    private static void ValidateTransaction(int offset, int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    /// <summary>Reads the original PCI header with its BAR derived from the one authoritative CRTC address store.</summary>
    public uint ReadConfiguration(int offset, int width)
    {
        ValidateTransaction(offset, width);
        uint result = 0, bar = BarBase;
        for (int lane = 0; lane < width; lane++)
        {
            int index = offset + lane;
            byte value = index is >= 0x10 and <= 0x13 ? (byte)(bar >> ((index - 0x10) * 8)) : _config[index];
            result |= (uint)value << (lane * 8);
        }
        return result;
    }

    /// <summary>Writes only requested lanes; BAR probes and VGA-side relocation always observe the same nine bits.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        ValidateTransaction(offset, width);
        uint bar = BarBase;
        bool changedBar = false;
        for (int lane = 0; lane < width; lane++)
        {
            int index = offset + lane;
            byte incoming = (byte)(value >> (lane * 8));
            if (index is >= 0x10 and <= 0x13)
            {
                int shift = (index - 0x10) * 8;
                bar = (bar & ~(255u << shift)) | (uint)incoming << shift;
                changedBar = true;
            }
            else
            {
                byte mask = WritableMask(index);
                _config[index] = (byte)((_config[index] & ~mask) | (incoming & mask));
            }
        }
        if (changedBar) _video.SetTrioBarBase(bar & 0xff800000u);
    }

    /// <summary>Checks every fixed configuration bit, including zero BAR placeholders rather than a second address shadow.</summary>
    public static void ValidateConfigurationBytes(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != 256) throw new InvalidDataException("Invalid S3 Trio64 PCI configuration size.");
        for (int i = 0; i < configuration.Length; i++)
        {
            int fixedMask = ~WritableMask(i) & 255;
            if ((configuration[i] & fixedMask) != (ResetValue(i) & fixedMask))
                throw new InvalidDataException($"Invalid S3 Trio64 PCI configuration bits at {i:X2}.");
        }
    }

    /// <summary>Checks the complete reset configuration without allocating or creating a replacement component.</summary>
    public static bool IsHardResetConfiguration(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != 256) return false;
        for (int i = 0; i < configuration.Length; i++) if (configuration[i] != ResetValue(i)) return false;
        return true;
    }

    /// <summary>Checks restoration without writing the derived BAR, changing peers or repairing malformed saved state.</summary>
    public void AfterStateRestore()
    {
        ValidateConfigurationBytes(_config);
        if (!_video.IsTrio64 || _video.IsEt4000 || _video.VideoMemoryBytes != 0x200000)
            throw new InvalidDataException("Invalid S3 Trio64 restored video binding.");
    }

    /// <summary>Accepts only the actual LAW inside a completely available eight-MiB BAR resource; fixed RAM/ROM always wins.</summary>
    public bool Decodes(uint address) => LinearEnabled && address >= LinearBase && (ulong)address - LinearBase < LinearLength;

    /// <summary>Reads a movable target using an absolute physical address, never a legacy aperture offset.</summary>
    public byte ReadDecodedByte(uint address) => Decodes(address) ? _video.ReadTrioLinearByte(address - LinearBase) : (byte)255;
    /// <summary>Writes a movable target only while the complete resource and mode gates remain valid.</summary>
    public void WriteDecodedByte(uint address, byte value) { if (Decodes(address)) _video.WriteTrioLinearByte(address - LinearBase, value); }
    /// <summary>Reads the fixed A0000-BFFFF mapping with its PCI and setup/POS target gates.</summary>
    public byte ReadByte(uint offset)
    {
        if(!MemorySpaceEnabled||!_video.TrioVideoEnabled)return 255;
        return _video.TryReadTrioEngineMmio(offset,out byte value)?value:_video.ReadByte(offset);
    }
    /// <summary>Writes the fixed A0000-BFFFF mapping without allowing an asleep or PCI-disabled video target to respond.</summary>
    public void WriteByte(uint offset, byte value)
    {
        if(MemorySpaceEnabled&&_video.TrioVideoEnabled&&!_video.TryWriteTrioEngineMmio(offset,value))_video.WriteByte(offset,value);
    }

    /// <summary>Accepts only the bounded lower-lane mono WORD-tail policy through the actual enabled fixed aperture; false has no effects.</summary>
    public bool TryWriteWord(uint offset, ushort value) => MemorySpaceEnabled && _video.TrioVideoEnabled &&
        _video.TryWriteTrioEnginePixelWord(offset, value);

    /// <summary>Returns the full un-driven transfer value for unsupported I/O widths.</summary>
    private static uint Float(int width) => width switch { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };

    /// <summary>Reads POS or the implemented normal VGA/advanced registers after the complete PCI/setup target decision.</summary>
    public uint Read(ushort port, int width)
    {
        uint floating = Float(width);
        if (!IoSpaceEnabled) return floating;
        if (port == _video.TrioSetupPort) return floating; // Setup control is write-only.
        if (port == 0x102) return width == 1 && _video.TrioSetupMode ? _video.TrioPos : floating;
        if (!_video.TrioVideoEnabled) return floating;
        if (VgaAdapter.IsTrioEnginePort(port,width)) return _video.ReadTrioEnginePort(port,width);
        if (port is 0x4ae8 or 0x4ae9)
        {
            if (!_video.TrioAdvancedAccessible || width > 2 || width == 2 && port != 0x4ae8) return floating;
            return (uint)(_video.TrioAdvanced >> ((port - 0x4ae8) * 8)) & floating;
        }
        if (port == 0x3c3 || port < 0x3b0 || port + width > 0x3e0) return floating;
        return _video.Read(port, width);
    }

    /// <summary>Processes the original setup rendezvous before routing normal VGA I/O; bad control widths have no partial effects.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Float(width);
        if (!IoSpaceEnabled) return;
        if (port == _video.TrioSetupPort) { if (width == 1) _video.WriteTrioSetup((byte)value); return; }
        if (port == 0x102) { if (width == 1) _video.WriteTrioPos((byte)value); return; }
        if (!_video.TrioVideoEnabled) return;
        if (VgaAdapter.IsTrioEnginePort(port,width)) { _video.WriteTrioEnginePort(port,value,width); return; }
        if (port is 0x4ae8 or 0x4ae9)
        {
            if (width > 2 || width == 2 && port != 0x4ae8) return;
            ushort next = width == 2 ? (ushort)value : port == 0x4ae8 ?
                (ushort)(((uint)_video.TrioAdvanced & 0xff00u) | (value & 255u)) :
                (ushort)(((uint)_video.TrioAdvanced & 255u) | ((value & 255u) << 8));
            _video.WriteTrioAdvanced(next); return;
        }
        if (port == 0x3c3 || port < 0x3b0 || port + width > 0x3e0) return;
        _video.Write(port, value, width);
    }
}
