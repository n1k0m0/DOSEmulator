using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DOSEmulator.Core;

public interface IMemoryDevice
{
    byte ReadByte(uint offset);
    void WriteByte(uint offset, byte value);
}

/// <summary>Physical byte-addressed bus. Mapped devices take precedence over installed RAM.</summary>
public sealed class MemoryBus
{
    private sealed record Mapping(uint Start, uint Length, IMemoryDevice Device, string Name);
    private readonly List<Mapping> _mappings = [];
    // One conservative flag per installed 4-KiB RAM page. Partial device pages
    // remain on the byte bus; high aliases do not allocate a 4-GiB lookup table.
    private readonly bool[] _mappedRamPages;
    public byte[] Ram { get; }
    public bool A20Enabled { get; set; } = true;

    public MemoryBus(int ramBytes)
    {
        if (ramBytes is < 1024 * 1024 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(ramBytes));
        Ram = new byte[ramBytes];
        _mappedRamPages = new bool[(ramBytes + 4095) >> 12];
    }

    public void Map(uint start, uint length, IMemoryDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (length == 0 || (ulong)start + length > 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(length));
        if (_mappings.Any(m => (ulong)start < (ulong)m.Start + m.Length && (ulong)m.Start < (ulong)start + length))
            throw new InvalidOperationException($"Memory mapping conflict: {name}.");
        _mappings.Add(new(start, length, device, name));
        if (start < Ram.Length)
        {
            int last = (int)((Math.Min((ulong)start + length, (ulong)Ram.Length) - 1) >> 12);
            Array.Fill(_mappedRamPages, true, (int)(start >> 12), last - (int)(start >> 12) + 1);
        }
    }

    private uint Translate(uint address) => A20Enabled ? address : address & ~0x0010_0000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        address = Translate(address);
        if (address < Ram.Length && !_mappedRamPages[address >> 12]) return Ram[address];
        return ReadMappedByte(address);
    }

    private byte ReadMappedByte(uint address)
    {
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
                return map.Device.ReadByte(address - map.Start);
        return address < Ram.Length ? Ram[address] : (byte)0xff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        if (address < Ram.Length && !_mappedRamPages[address >> 12]) { Ram[address] = value; return; }
        WriteMappedByte(address, value);
    }

    private void WriteMappedByte(uint address, byte value)
    {
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
            {
                map.Device.WriteByte(address - map.Start, value);
                return;
            }
        if (address < Ram.Length) Ram[address] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryRamRange(uint address, int width, out int offset)
    {
        uint physical = Translate(address);
        offset = (int)physical;
        // A20 must not fold any interior byte; uint address wrap and RAM ends
        // also remain on the original byte path. At most two pages are touched.
        return physical <= (uint)(Ram.Length - width) &&
            (A20Enabled || ((address ^ unchecked(address + (uint)width - 1)) & 0x0010_0000u) == 0) &&
            !_mappedRamPages[physical >> 12] && !_mappedRamPages[(physical + (uint)width - 1) >> 12];
    }

    public ushort ReadWord(uint address) => TryRamRange(address, 2, out int offset)
        ? BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan(offset, 2))
        : (ushort)(ReadByte(address) | ReadByte(unchecked(address + 1)) << 8);
    /// <summary>Non-invasive RAM/ROM inspection; refuses devices with read side effects.</summary>
    public bool TryPeekByte(uint address, out byte value)
    {
        address = Translate(address);
        if (address < Ram.Length && !_mappedRamPages[address >> 12]) { value = Ram[address]; return true; }
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
            {
                if (map.Device is RomDevice rom) { value = rom.ReadByte(address - map.Start); return true; }
                if (map.Device is OpenBusMemory) { value = 0xff; return true; }
                value = 0; return false;
            }
        value = address < Ram.Length ? Ram[address] : (byte)0xff;
        return true;
    }
    public uint ReadDword(uint address) => TryRamRange(address, 4, out int offset)
        ? BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan(offset, 4))
        : (uint)ReadByte(address) | (uint)ReadByte(unchecked(address + 1)) << 8 |
          (uint)ReadByte(unchecked(address + 2)) << 16 | (uint)ReadByte(unchecked(address + 3)) << 24;
    public void WriteWord(uint address, ushort value)
    {
        if (TryRamRange(address, 2, out int offset)) { BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(offset, 2), value); return; }
        WriteByte(address, (byte)value);
        WriteByte(unchecked(address + 1), (byte)(value >> 8));
    }
    public void WriteDword(uint address, uint value)
    {
        if (TryRamRange(address, 4, out int offset)) { BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan(offset, 4), value); return; }
        WriteByte(address, (byte)value);
        WriteByte(unchecked(address + 1), (byte)(value >> 8));
        WriteByte(unchecked(address + 2), (byte)(value >> 16));
        WriteByte(unchecked(address + 3), (byte)(value >> 24));
    }
}

/// <summary>Undecoded reference-board address range: pull-up reads, no write target.</summary>
public sealed class OpenBusMemory : IMemoryDevice
{
    public byte ReadByte(uint offset) => 0xff;
    public void WriteByte(uint offset, byte value) { }
}

public sealed class RomDevice : IMemoryDevice
{
    private readonly byte[] _bytes;
    public RomDevice(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public byte ReadByte(uint offset) => _bytes[offset];
    public void WriteByte(uint offset, byte value) { }
}
