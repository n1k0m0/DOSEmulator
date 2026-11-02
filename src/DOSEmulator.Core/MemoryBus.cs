// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DOSEmulator.Core;

public interface IMemoryDevice
{
    /// <summary>Reads one device-relative byte, including that mapping's documented register/latch effects.</summary>
    byte ReadByte(uint offset);
    /// <summary>Writes one device-relative byte under the mapped device's storage/register contract.</summary>
    void WriteByte(uint offset, byte value);
}

/// <summary>Optional fixed-mapping peer that distinguishes an aligned physical word store from two byte stores.</summary>
public interface IMemoryWordWriteDevice : IMemoryDevice
{
    /// <summary>Consumes one little-endian word, or returns false without any effect so the bus can perform its ordered byte fallback.</summary>
    bool TryWriteWord(uint offset, ushort value);
}

/// <summary>Physical byte-addressed bus. Mapped devices take precedence over installed RAM.</summary>
public sealed partial class MemoryBus
{
    private sealed record Mapping(uint Start, uint Length, IMemoryDevice Device, string Name);
    private readonly List<Mapping> _mappings = [];
    // One conservative flag per installed 4-KiB RAM page. Partial device pages
    // remain on the byte bus; high aliases do not allocate a 4-GiB lookup table.
    private readonly bool[] _mappedRamPages;
    private readonly Mapping?[] _devicePages;
    /// <summary>Installed RAM storage; normal bus accesses still give mapped devices precedence over these bytes.</summary>
    public byte[] Ram { get; }
    /// <summary>Optional owner-side notification for RAM that a device may have changed while an external CPU is stopped.</summary>
    public event Action<uint, int>? PhysicalRamWrite;
    /// <summary>Marks a direct RAM store by a mapped device such as PAM shadow memory.</summary>
    public void NotifyPhysicalRamWrite(uint address, int length)
    {
        if (length <= 0 || (ulong)address + (uint)length > (ulong)Ram.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        PhysicalRamWrite?.Invoke(address, length);
    }
    private uint _ramHoleStart, _ramHoleLength;
    /// <summary>Decoded DRAM extent, bounded by installed storage; mapped devices retain their normal precedence.</summary>
    public uint DecodedRamBytes { get; private set; }
    /// <summary>First byte of the optional forwarded RAM hole, independent from installed backing storage.</summary>
    public uint RamHoleStart => _ramHoleStart;
    /// <summary>Length of the optional forwarded RAM hole; zero means no hole.</summary>
    public uint RamHoleLength => _ramHoleLength;
    /// <summary>Whether the reference physical bus preserves address bit 20, including its documented DMA policy.</summary>
    public bool A20Enabled { get; set; } = true;

    /// <summary>Initializes MemoryBus and connects the supplied dependencies before the component is used.</summary>
    public MemoryBus(int ramBytes)
    {
        if (ramBytes is < 1024 * 1024 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(ramBytes));
        Ram = new byte[ramBytes]; DecodedRamBytes = (uint)ramBytes;
        _mappedRamPages = new bool[(ramBytes + 4095) >> 12];
        _devicePages = new Mapping?[_mappedRamPages.Length];
    }

    /// <summary>Registers a nonoverlapping physical device range and conservatively updates page classifications.</summary>
    public void Map(uint start, uint length, IMemoryDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (length == 0 || (ulong)start + length > 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(length));
        if (_mappings.Any(m => (ulong)start < (ulong)m.Start + m.Length && (ulong)m.Start < (ulong)start + length))
            throw new InvalidOperationException($"Memory mapping conflict: {name}.");
        var mapping = new Mapping(start, length, device, name);
        _mappings.Add(mapping);
        RebuildMemoryClassification();
    }

    /// <summary>Changes only decoded DRAM visibility, retaining all hidden installed bytes for later re-enabling.</summary>
    public void SetDecodedRamBytes(uint bytes)
    {
        if (bytes > Ram.Length) throw new ArgumentOutOfRangeException(nameof(bytes));
        DecodedRamBytes = bytes;
    }

    /// <summary>Forwards one page-aligned hole while retaining MMIO precedence and conservative page fastpaths.</summary>
    public void SetRamHole(uint start, uint length)
    {
        if (length == 0 && start != 0 || (start & 4095) != 0 || (length & 4095) != 0 || (ulong)start + length > 0x1_0000_0000UL)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (_ramHoleStart == start && _ramHoleLength == length) return;
        _ramHoleStart = start; _ramHoleLength = length;
        RebuildMemoryClassification();
    }

    /// <summary>Validates restored visibility before rebuilding the derived classification cache.</summary>
    private void ValidateRamDecode()
    {
        if (DecodedRamBytes > Ram.Length || _ramHoleLength == 0 && _ramHoleStart != 0 ||
            (_ramHoleStart & 4095) != 0 || (_ramHoleLength & 4095) != 0 ||
            (ulong)_ramHoleStart + _ramHoleLength > 0x1_0000_0000UL)
            throw new InvalidDataException("Invalid decoded DRAM extent or forwarded hole.");
        RebuildMemoryClassification();
    }

    /// <summary>Reconstructs conservative RAM/device pages from fixed mappings and the current forwarded hole.</summary>
    private void RebuildMemoryClassification()
    {
        Array.Clear(_mappedRamPages); Array.Clear(_devicePages);
        foreach (var mapping in _mappings)
        {
            uint start = mapping.Start, length = mapping.Length;
            if (start >= Ram.Length) continue;
            int last = (int)((Math.Min((ulong)start + length, (ulong)Ram.Length) - 1) >> 12);
            Array.Fill(_mappedRamPages, true, (int)(start >> 12), last - (int)(start >> 12) + 1);
            int firstFull = (int)(((ulong)start + 4095) >> 12);
            int afterFull = (int)(Math.Min((ulong)start + length, (ulong)Ram.Length) >> 12);
            if (afterFull > firstFull) Array.Fill(_devicePages, mapping, firstFull, afterFull - firstFull);
        }
        if (_ramHoleLength == 0 || _ramHoleStart >= Ram.Length) return;
        var forwarded = new Mapping(_ramHoleStart, _ramHoleLength, new OpenBusMemory(), "Forwarded DRAM hole");
        int first = (int)(_ramHoleStart >> 12), end = (int)((Math.Min((ulong)_ramHoleStart + _ramHoleLength, (ulong)Ram.Length) + 4095) >> 12);
        for (int page = first; page < end; page++)
        {
            if (!_mappedRamPages[page]) _devicePages[page] = forwarded;
            _mappedRamPages[page] = true;
        }
    }

    /// <summary>Checks only the slow unmapped fallback; ordinary RAM bytes use their existing page classification.</summary>
    private bool InRamHole(uint address) => _ramHoleLength != 0 && address >= _ramHoleStart && (ulong)address < (ulong)_ramHoleStart + _ramHoleLength;

    /// <summary>Number of installed ranges, allowing the PC to reject extra custom mappings before capture.</summary>
    public int StateMappingCount => _mappings.Count;

    /// <summary>Fixed physical windows for an external execution engine. The returned view does not read devices.</summary>
    public IReadOnlyList<(uint Start, uint Length, IMemoryDevice Device)> PhysicalMappings =>
        _mappings.Select(m => (m.Start, m.Length, m.Device)).ToArray();

    /// <summary>Describes the factory memory map and immutable ROM bytes without bus reads or side effects.</summary>
    public string StateMap(Func<object, string> owner) => string.Join(";", _mappings.Select(m =>
        $"{m.Start:X8}:{m.Length:X8}:" + (m.Device is RomDevice rom ? "rom/" + rom.StateIdentity :
            m.Device is OpenBusMemory ? "open" : owner(m.Device)))) + DynamicStateMap(owner);

    /// <summary>Applies the reference A20 bit-folding policy to a physical bus address.</summary>
    private uint Translate(uint address) => A20Enabled ? address : address & ~0x0010_0000u;

    /// <summary>Reads one physical byte after A20 folding, giving mapped devices precedence over installed RAM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        address = Translate(address);
        if (address < DecodedRamBytes && !_mappedRamPages[address >> 12]) return ReadRamByte(address);
        return ReadMappedByte(address);
    }

    /// <summary>Finds the mapped memory device or open bus after the ordinary RAM fast path has been excluded.</summary>
    private byte ReadMappedByte(uint address)
    {
        if (address < Ram.Length && _devicePages[address >> 12] is { } page)
            return page.Device.ReadByte(address - page.Start);
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
                return map.Device.ReadByte(address - map.Start);
        if (ResolveDynamic(address) is { } dynamic) return dynamic.ReadDecodedByte(address);
        return address < DecodedRamBytes && !InRamHole(address) ? ReadRamByte(address) : (byte)0xff;
    }

    /// <summary>Writes one physical byte after A20 folding without writing through a device or open-bus mapping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        if (address < DecodedRamBytes && !_mappedRamPages[address >> 12]) { WriteRamByte(address, value); PhysicalRamWrite?.Invoke(address, 1); return; }
        WriteMappedByte(address, value);
    }

    /// <summary>Routes a byte to its mapped device or remaining RAM without writing through an open-bus range.</summary>
    private void WriteMappedByte(uint address, byte value)
    {
        if (address < Ram.Length && _devicePages[address >> 12] is { } page)
        { page.Device.WriteByte(address - page.Start, value); return; }
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
            {
                map.Device.WriteByte(address - map.Start, value);
                return;
            }
        if (ResolveDynamic(address) is { } dynamic) { dynamic.WriteDecodedByte(address, value); return; }
        if (address < DecodedRamBytes && !InRamHole(address)) { WriteRamByte(address, value); PhysicalRamWrite?.Invoke(address, 1); }
    }

    /// <summary>Checks a contiguous plain-RAM range including A20 folding, device boundaries and address wrap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryRamRange(uint address, int width, out int offset)
    {
        uint physical = Translate(address);
        offset = (int)physical;
        // A20 must not fold any interior byte; uint address wrap and RAM ends
        // also remain on the original byte path. At most two pages are touched.
        return (ulong)physical + (uint)width <= DecodedRamBytes &&
            (A20Enabled || ((address ^ unchecked(address + (uint)width - 1)) & 0x0010_0000u) == 0) &&
            !_mappedRamPages[physical >> 12] && !_mappedRamPages[(physical + (uint)width - 1) >> 12];
    }

    /// <summary>Owner-thread RAM access only; refuses devices, A20 folds and range boundaries.</summary>
    public bool TryGetRamSpan(uint address, int length, out Span<byte> bytes)
    {
        bytes = default;
        if (length <= 0) return false;
        uint physical = Translate(address);
        if ((ulong)physical + (uint)length > DecodedRamBytes ||
            !A20Enabled && (address >> 20) != (unchecked(address + (uint)length - 1) >> 20)) return false;
        for (uint page = physical >> 12; page <= (physical + (uint)length - 1) >> 12; page++)
            if (_mappedRamPages[page]) return false;
        bytes = PhysicalRam.Slice((int)physical, length); PhysicalRamWrite?.Invoke(physical, length); return true;
    }

    /// <summary>Grants owner-thread physical DRAM without CPU A20 folding or device callbacks; only the named mapping may expose its own RAM backing.</summary>
    public bool TryGetPhysicalRamSpan(uint address, int length, IMemoryDevice? permittedMapping, out Span<byte> bytes)
    {
        bytes = default;
        if (!TryGetReadOnlyPhysicalRamSpan(address, length, permittedMapping, out _)) return false;
        bytes = PhysicalRam.Slice((int)address, length); PhysicalRamWrite?.Invoke(address, length); return true;
    }

    /// <summary>Classifies physical DRAM without claiming a device write or changing any RAM/observer state.</summary>
    public bool TryGetReadOnlyPhysicalRamSpan(uint address, int length, IMemoryDevice? permittedMapping, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (length <= 0) return false;
        ulong end = (ulong)address + (uint)length;
        if (end > 0x1_0000_0000UL || end > DecodedRamBytes) return false;
        if (_ramHoleLength != 0 && (ulong)address < (ulong)_ramHoleStart + _ramHoleLength && _ramHoleStart < end)
            return false;
        foreach (var mapping in _mappings)
            if ((ulong)address < (ulong)mapping.Start + mapping.Length && mapping.Start < end &&
                !ReferenceEquals(mapping.Device, permittedMapping))
                return false;
        bytes = PhysicalRam.Slice((int)address, length); return true;
    }

    /// <summary>Inspects contiguous RAM or immutable ROM code without contacting devices; never grants writable ROM storage.</summary>
    public bool TryGetCodeSpan(uint address, int length, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (TryGetRamSpan(address, length, out var ram)) { bytes = ram; return true; }
        if (length <= 0 || (ulong)address + (uint)length > 0x1_0000_0000UL) return false;
        uint physical = Translate(address);
        if (!A20Enabled && (address >> 20) != ((address + (uint)length - 1) >> 20)) return false;
        foreach (var map in _mappings)
            if (physical >= map.Start && (ulong)physical + (uint)length <= (ulong)map.Start + map.Length && map.Device is RomDevice rom)
                return rom.TryGetSpan(physical - map.Start, length, out bytes);
        return false;
    }

    /// <summary>Classifies the translated byte as ordinary installed RAM without contacting a mapped device.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPlainRamByte(uint address)
    {
        address = Translate(address);
        return address < DecodedRamBytes && !_mappedRamPages[address >> 12];
    }

    /// <summary>Reads only ordinary RAM after A20 folding; false leaves mapped devices entirely untouched.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadRamByte(uint address, out byte value)
    {
        address = Translate(address);
        if (address < DecodedRamBytes && !_mappedRamPages[address >> 12])
        {
            value = ReadRamByte(address);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Reads a little-endian word through a checked contiguous-RAM path or the ordered byte bus.</summary>
    public ushort ReadWord(uint address) => TryRamRange(address, 2, out int offset)
        ? BinaryPrimitives.ReadUInt16LittleEndian(PhysicalRam.Slice(offset, 2))
        : TransactionDecoder(address, 2, out uint physical) is { } peer ? (ushort)peer.ReadDecoded(physical, 2)
        : (ushort)(ReadByte(address) | ReadByte(unchecked(address + 1)) << 8);
    /// <summary>Non-invasive RAM/ROM inspection; refuses devices with read side effects.</summary>
    public bool TryPeekByte(uint address, out byte value)
    {
        address = Translate(address);
        if (address < DecodedRamBytes && !_mappedRamPages[address >> 12]) { value = ReadRamByte(address); return true; }
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
            {
                if (map.Device is RomDevice rom) { value = rom.ReadByte(address - map.Start); return true; }
                if (map.Device is OpenBusMemory) { value = 0xff; return true; }
                value = 0; return false;
            }
        if (ResolveDynamic(address) is not null) { value = 0; return false; }
        value = address < DecodedRamBytes && !InRamHole(address) ? ReadRamByte(address) : (byte)0xff;
        return true;
    }
    /// <summary>Reads a little-endian doubleword without bypassing A20, mapping or range boundaries.</summary>
    public uint ReadDword(uint address) => TryRamRange(address, 4, out int offset)
        ? BinaryPrimitives.ReadUInt32LittleEndian(PhysicalRam.Slice(offset, 4))
        : TransactionDecoder(address, 4, out uint physical) is { } peer ? peer.ReadDecoded(physical, 4)
        : (uint)ReadByte(address) | (uint)ReadByte(unchecked(address + 1)) << 8 |
          (uint)ReadByte(unchecked(address + 2)) << 16 | (uint)ReadByte(unchecked(address + 3)) << 24;
    /// <summary>Offers an aligned word only to one complete fixed mapping, retaining ordered byte effects on decline or at boundaries.</summary>
    private bool TryWriteMappedWord(uint address, ushort value)
    {
        uint physical = Translate(address);
        if ((physical & 1) != 0 || address == uint.MaxValue ||
            Translate(unchecked(address + 1)) != physical + 1) return false;
        Mapping? mapping = physical < Ram.Length ? _devicePages[physical >> 12] : null;
        if (mapping is null)
            foreach (var candidate in _mappings)
                if (physical >= candidate.Start && (ulong)physical < (ulong)candidate.Start + candidate.Length)
                { mapping = candidate; break; }
        return mapping is not null && (ulong)physical + 2 <= (ulong)mapping.Start + mapping.Length &&
            mapping.Device is IMemoryWordWriteDevice word && word.TryWriteWord(physical - mapping.Start, value);
    }

    /// <summary>Writes a little-endian word through checked RAM, an accepting fixed device, or the original ordered byte bus.</summary>
    public void WriteWord(uint address, ushort value)
    {
        if (TryRamRange(address, 2, out int offset)) { BinaryPrimitives.WriteUInt16LittleEndian(PhysicalRam.Slice(offset, 2), value); PhysicalRamWrite?.Invoke((uint)offset, 2); return; }
        if (TryWriteMappedWord(address, value)) return;
        if (TransactionDecoder(address, 2, out uint physical) is { } peer) { peer.WriteDecoded(physical, value, 2); return; }
        WriteByte(address, (byte)value);
        WriteByte(unchecked(address + 1), (byte)(value >> 8));
    }
    /// <summary>Writes a little-endian doubleword while retaining ordered device-byte effects at any boundary.</summary>
    public void WriteDword(uint address, uint value)
    {
        if (TryRamRange(address, 4, out int offset)) { BinaryPrimitives.WriteUInt32LittleEndian(PhysicalRam.Slice(offset, 4), value); PhysicalRamWrite?.Invoke((uint)offset, 4); return; }
        if (TransactionDecoder(address, 4, out uint physical) is { } peer) { peer.WriteDecoded(physical, value, 4); return; }
        WriteByte(address, (byte)value);
        WriteByte(unchecked(address + 1), (byte)(value >> 8));
        WriteByte(unchecked(address + 2), (byte)(value >> 16));
        WriteByte(unchecked(address + 3), (byte)(value >> 24));
    }
}

/// <summary>Undecoded DOSE board address range: pull-up reads, no write target.</summary>
public sealed class OpenBusMemory : IMemoryDevice
{
    /// <summary>Returns the reference unclaimed-memory bus value without allocating backing RAM.</summary>
    public byte ReadByte(uint offset) => 0xff;
    /// <summary>Discards a write to the unclaimed ISA window.</summary>
    public void WriteByte(uint offset, byte value) { }
}

public sealed class RomDevice : IMemoryDevice
{
    private readonly byte[] _bytes;
    /// <summary>Identifies the immutable firmware by bytes, never by its filename or a mutable host label.</summary>
    public string StateIdentity => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_bytes));
    /// <summary>Initializes RomDevice and connects the supplied dependencies before the component is used.</summary>
    public RomDevice(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    /// <summary>Exposes only checked read-only bytes from the private copied ROM image for non-invasive instruction validation.</summary>
    internal bool TryGetSpan(uint offset, int length, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (length <= 0 || (ulong)offset + (uint)length > (ulong)_bytes.Length) return false;
        bytes = _bytes.AsSpan((int)offset, length); return true;
    }
    /// <summary>Reads one ROM byte, returning open bus outside the supplied immutable image.</summary>
    public byte ReadByte(uint offset) => _bytes[offset];
    /// <summary>Ignores a ROM write so guest stores cannot modify the firmware image.</summary>
    public void WriteByte(uint offset, byte value) { }
}
