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
    public byte[] Ram { get; }
    public bool A20Enabled { get; set; } = true;

    public MemoryBus(int ramBytes)
    {
        if (ramBytes is < 1024 * 1024 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(ramBytes));
        Ram = new byte[ramBytes];
    }

    public void Map(uint start, uint length, IMemoryDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (length == 0 || (ulong)start + length > 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(length));
        if (_mappings.Any(m => (ulong)start < (ulong)m.Start + m.Length && (ulong)m.Start < (ulong)start + length))
            throw new InvalidOperationException($"Memory mapping conflict: {name}.");
        _mappings.Add(new(start, length, device, name));
    }

    private uint Translate(uint address) => A20Enabled ? address : address & ~0x0010_0000u;

    public byte ReadByte(uint address)
    {
        address = Translate(address);
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
                return map.Device.ReadByte(address - map.Start);
        return address < Ram.Length ? Ram[address] : (byte)0xff;
    }

    public void WriteByte(uint address, byte value)
    {
        address = Translate(address);
        foreach (var map in _mappings)
            if (address >= map.Start && (ulong)address < (ulong)map.Start + map.Length)
            {
                map.Device.WriteByte(address - map.Start, value);
                return;
            }
        if (address < Ram.Length) Ram[address] = value;
    }

    public ushort ReadWord(uint address) => (ushort)(ReadByte(address) | ReadByte(unchecked(address + 1)) << 8);
    public uint ReadDword(uint address) => (uint)(ReadWord(address) | (uint)ReadWord(unchecked(address + 2)) << 16);
    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)value);
        WriteByte(unchecked(address + 1), (byte)(value >> 8));
    }
    public void WriteDword(uint address, uint value)
    {
        WriteWord(address, (ushort)value);
        WriteWord(unchecked(address + 2), (ushort)(value >> 16));
    }
}

public sealed class RomDevice : IMemoryDevice
{
    private readonly byte[] _bytes;
    public RomDevice(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public byte ReadByte(uint offset) => _bytes[offset];
    public void WriteByte(uint offset, byte value) { }
}
