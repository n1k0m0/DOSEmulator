// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;
using DOSEmulator.Contracts;

namespace DOSEmulator.Hardware;

/// <summary>Own bounded SST-1 PCI/MMIO implementation, with 2-MiB FBI and one 2-MiB TMU beside the primary VGA.
/// Register/transfer layout follows the published SST-1 specification; command latency is an explicit DOSE development policy.</summary>
public sealed partial class VoodooGraphics : IPciConfigurationFunction, IMemoryTransactionReadiness, IImmediateMemoryWrite
{
    /// <summary>Optional additional card; it never replaces the machine's VGA/S3 boot adapter.</summary>
    public const string ProfileId = MachineProfile.VoodooGraphicsId;
    private readonly MemoryBus _memory;
    private readonly DeviceScheduler _clock;
    private readonly byte[] _config = new byte[256];
    private readonly uint[] _registers = new uint[256];
    private const int DramBytes = 2 * 1024 * 1024, BankShift = 19, BankBytes = 1 << BankShift;
    private readonly byte[][] _fbi = Enumerable.Range(0, 4).Select(_ => new byte[BankBytes]).ToArray();
    private readonly byte[][] _texture = Enumerable.Range(0, 4).Select(_ => new byte[BankBytes]).ToArray();
    private readonly uint[] _clut = new uint[33];
    private readonly uint[] _texturePalette = new uint[256];
    private readonly Queue<uint[]> _commands = new();
    private int _front;
    private long _event, _frameId;
    private ulong _deadline;
    private long _revision;
    private uint _nonfiniteParameters;
    private int _fifoWords;
    private int _pendingSwaps;
    private readonly uint[] _commandScratch=new uint[256];

    /// <summary>Constructs an unselected pass-through board with no PCI interrupt generator or host graphics endpoint.</summary>
    public VoodooGraphics(MemoryBus memory, DeviceScheduler clock)
    { _memory = memory; _clock = clock; PowerOnDac(); ApplyPiixHardReset(); }
    /// <summary>Single sixteen-MiB BAR, independent from the installed four MiB of physical graphics DRAM.</summary>
    public uint BarBase => BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan(0x10, 4));
    /// <summary>Complete resource gate; fixed RAM/ROM and constructor-prior peers retain precedence.</summary>
    public bool MemoryEnabled => (_config[4] & 2) != 0 && _memory.IsFixedWindowAvailable(BarBase, 0x1000000);
    /// <summary>Blocks LFB read admission behind prior commands and applies bounded FIFO backpressure without touching CPU, pixels or queue state.</summary>
    public ulong? TransactionReadyAt(uint address, bool write)
    {
        if (!Decodes(address) || !Busy) return null;
        uint offset=address-BarBase;
        if (!write) return offset is >=0x400000 and <0x800000 && (_registers[0x214/4]&8)!=0 ? _deadline : null;
        if ((_config[0x40]&2)==0 || (_registers[0x210/4]&0x16)!=0x10) return null;
        int chips=(int)(offset>>10&15),reg=(int)(offset&0x3fc);
        if(offset<0x400000&&chips!=0&&(reg<0x300?(chips&1)==0:(chips&2)==0))return null;
        int words=offset>=0x800000 ? ((offset&0x600000)==0?8:0) : offset>=0x400000?7 : (offset&0x3fc) switch
        {0x80 or 0x100 or 0x124=>259,0x120 or 0x128=>2,>=0x334 and <=0x350=>2,_=>0};
        return words!=0 && (MemoryFifoWords==0?_commands.Count>=63:_fifoWords+words>MemoryFifoWords) ? _deadline : null;
    }
    /// <summary>Stable pixel dimensions; the original horizontal timing field stores the final pixel index.</summary>
    public int Width => (int)(_registers[0x20c / 4] & 1023) + 1;
    /// <summary>Programmed active vertical line count, independent from the VGA mode.</summary>
    public int Height => (int)(_registers[0x20c / 4] >> 16 & 1023);
    /// <summary>Admits only enabled setup latches; commands, uploads, palettes, initialization and all RAM-affecting writes remain separate instructions.</summary>
    public bool IsImmediateDwordWrite(uint address)
    {
        if(!Decodes(address)||(address&3)!=0||address-BarBase>=0x400000||(_config[0x40]&2)==0||(_registers[0x210/4]&6)!=0)return false;
        int register=Remap((int)((address-BarBase)&0x3fc),address-BarBase);
        return register is >=0x20 and <=0x7c or >=0x88 and <=0xfc;
    }
    /// <summary>The VGA cable path remains selected until software releases video reset/blanking and enables Voodoo output.</summary>
    public bool OutputEnabled => (_registers[0x210 / 4] & 1) == 0 && (_registers[0x214 / 4] & 0x3100) == 0x2000 && Height > 0;
    /// <summary>Whether at least one real draw/clear/swap operation still has a future completion.</summary>
    public bool Busy => _commands.Count != 0;
    /// <summary>Next virtual completion, used only for diagnostics and exact continuation tests.</summary>
    public ulong CompletionNanoseconds => _deadline;
    /// <summary>Verifies fixed peers without changing decode or pending operations.</summary>
    public bool HasBindings(MemoryBus memory, DeviceScheduler clock) => ReferenceEquals(_memory, memory) && ReferenceEquals(_clock, clock);

    /// <summary>Applies actual PCI/engine reset while retaining powered DRAM and scheduler time.</summary>
    public void ApplyPiixHardReset()
    {
        if (_event != 0) _clock.Cancel(_event);
        _event = 0; _deadline = 0; _commands.Clear(); _fifoWords=0; _pendingSwaps=0; _front = 0;
        Array.Clear(_config); _config[0] = 0x1a; _config[1] = 0x12; _config[2] = 1; _config[8] = 2;
        _config[0x13] = 0xff; _config[0x3c] = 5; _config[0x3d] = 1;
        Array.Clear(_registers); _nonfiniteParameters=0; _registers[0x200 / 4] = 1; _registers[0x210 / 4] = 0x411;
        _registers[0x214 / 4] = 0x201102; _registers[0x218 / 4] = 0x80000040; _registers[0x21c / 4] = 0x1e5000;
        // The qualified revision-two board reports VGA_PASS power-on polarity in bit 12, as used by the original SST-1 init library.
        // Own strap selects VGA pass-through at reset. Identity gamma is deterministic power-on SRAM policy.
        for (int i = 0; i < _clut.Length; i++) { uint c = (uint)Math.Min(255, i * 8); _clut[i] = c * 0x010101; }
        _revision++;
    }
    /// <summary>Rejects bad PCI transaction shapes before touching any byte lane.</summary>
    private static void CheckConfig(int offset, int width)
    { if (width is not (1 or 2 or 4) || offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset)); }
    /// <summary>Exposes the original vendor/device/revision header and current writable PCI latches.</summary>
    public uint ReadConfiguration(int offset, int width)
    {
        CheckConfig(offset, width); uint result = 0;
        for (int i = 0; i < width; i++)
        {
            int at = offset + i;
            result |= (uint)(at is >= 0x4c and <= 0x4f ? (byte)(ReadRegister(0) >> ((at - 0x4c) * 8)) : _config[at]) << (i * 8);
        }
        return result;
    }
    /// <summary>Implements byte-enabled command, BAR and initialization gates; absent bus mastering never invents DMA.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        CheckConfig(offset, width);
        for (int i = 0; i < width; i++)
        {
            int at = offset + i; byte b = (byte)(value >> (i * 8));
            byte mask = at switch { 4 => 2, 0x13 or 0x3c => 255, 0x40 => 0xf7, 0x41 => 15, _ => 0 };
            _config[at] = (byte)((_config[at] & ~mask) | (b & mask));
        }
    }
    /// <summary>Tests a complete physical byte against the programmed, resource-validated BAR.</summary>
    public bool Decodes(uint address) => MemoryEnabled && address >= BarBase && (ulong)address - BarBase < 0x1000000;
    /// <summary>SST-1 does not accept byte transactions, including register, LFB and texture accesses.</summary>
    public byte ReadDecodedByte(uint address) => 255;
    /// <summary>Ignores unsupported byte writes without accidentally executing a command four times.</summary>
    public void WriteDecodedByte(uint address, byte value) { }
    /// <summary>Reads only aligned complete register DWORDs or enabled RGB/depth LFB words/DWORDs.</summary>
    public uint ReadDecoded(uint address, int width)
    {
        if (width is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        uint floating = width == 2 ? 65535u : uint.MaxValue;
        if (!Decodes(address) || (address & (width - 1)) != 0 || (ulong)address + (uint)width > (ulong)BarBase + 0x1000000) return floating;
        uint offset = address - BarBase;
        if (offset < 0x400000) return width == 4 ? ReadRegister((int)(offset & 0x3fc)) : floating;
        if (offset >= 0x800000 || (_registers[0x214 / 4] & 8) == 0) return floating;
        return ReadLfb(offset - 0x400000, width);
    }
    /// <summary>Preserves original DWORD register/texture and WORD/DWORD LFB transfer requirements.</summary>
    public void WriteDecoded(uint address, uint value, int width)
    {
        if (width is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!Decodes(address) || (address & (width - 1)) != 0 || (ulong)address + (uint)width > (ulong)BarBase + 0x1000000) return;
        uint offset = address - BarBase;
        if (offset < 0x400000)
        {
            if (width != 4) return;
            int chips = (int)(offset >> 10 & 15), reg = (int)(offset & 0x3fc);
            // S/T setup belongs to the selected TREX even though its addresses precede the texture-control block.
            int setup=Remap(reg,offset);bool textureSetup=setup is 0x34 or 0x38 or 0x54 or 0x58 or 0x74 or 0x78 or 0xb4 or 0xb8 or 0xd4 or 0xd8 or 0xf4 or 0xf8;
            if ((chips == 0 || (chips & (textureSetup||reg>=0x300?2:1)) != 0))
                WriteRegister(reg, value, offset);
        }
        else if (offset < 0x800000) WriteLfb(offset - 0x400000, value, width);
        else if (width == 4) WriteTexture(offset - 0x800000, value);
    }
    /// <summary>Reports actual FIFO/engine/front-buffer state and the bounded virtual 60-Hz development scan period.</summary>
    private uint ReadRegister(int reg) => reg switch
    {
        0 => (uint)(MemoryFifoWords==0?Math.Max(0,63-_commands.Count):Math.Min(63,Math.Max(0,MemoryFifoWords-_fifoWords)/2)) |
            (_clock.Now % 16_666_667 < 1_000_000 ? 0u : 64u) | BusyStatus() | (uint)_front << 10 |
            (uint)(MemoryFifoWords==0?65535:Math.Max(0,MemoryFifoWords-_fifoWords))<<12 |
            (uint)Math.Min(7, _pendingSwaps) << 28,
        0x204 => (uint)(_clock.Now % 16_666_667 * (ulong)Math.Max(1, Height) / 16_666_667),
        0x218 when (_config[0x40] & 4) != 0 => _dacReadLatch,
        >= 0x14c and <= 0x15c => _registers[reg / 4] & 0xffffff,
        _ => _registers[reg / 4]
    };
    /// <summary>Separates an active FBI/TMU operation from a FIFO waiting only for vertical retrace.</summary>
    private uint BusyStatus()
    {
        if (!Busy) return 0;
        uint[] c = _commands.Peek();uint operation=CommandOperation(c); bool drawing = operation is 0x80 or 0x100;
        return 0x200u | (drawing || operation == 0x124 ? 0x80u : 0) |
            ((operation & 0x800000) != 0 || IsPaletteRegister(operation) || drawing && (c[0x104 / 4] & 0x8000000) != 0 ? 0x100u : 0);
    }
    /// <summary>Uses the original optional remap only for triangle parameter register groups.</summary>
    private int Remap(int reg, uint offset)
    {
        if ((_registers[0x21c / 4] & 1) == 0 || (offset & 0x200000) == 0) return reg;
        int first = reg >= 0xa0 ? 0xa0 : 0x20, index = (reg - first) / 4;
        return reg >= first && reg < first + 96 ? first + (index % 3 * 8 + index / 3) * 4 : reg;
    }
    /// <summary>Loads real parameter latches and queues commands with immutable parameters; reset cancels their completion tokens.</summary>
    private void WriteRegister(int reg, uint value, uint offset)
    {
        reg = Remap(reg, offset);
        if ((_registers[0x210 / 4] & 8) != 0 && (offset & 0x200000) != 0) value = BinaryPrimitives.ReverseEndianness(value);
        if (reg is 0x200 or >= 0x210 and <= 0x21c)
        {
            if ((_config[0x40] & 1) == 0) return;
            if (reg == 0x210 && (value & 6) != 0)
            { if (_event != 0) _clock.Cancel(_event); _event = 0; _deadline = 0; _commands.Clear(); _fifoWords=0; _pendingSwaps=0; }
            if (reg == 0x214) value &= ~5u;
            if (reg == 0x21c) value = value & ~0x1f00u | _registers[reg / 4] & 0x1f00;
            _registers[reg / 4] = value; _revision++; return;
        }
        if (reg is 0x204 or >= 0x14c and <= 0x15c || reg == 0) return;
        if (reg is 0x208 or 0x20c or 0x220 or 0x224 or 0x228 or 0x22c or 0x230)
        {
            if (reg == 0x22c) TransferDac(value);
            if (reg == 0x228) { int index = (int)(value >> 24 & 63); if (index < 33) _clut[index] = value & 0xffffff; }
            _registers[reg / 4] = value; _revision++; return;
        }
        if ((_config[0x40] & 2) == 0 || (_registers[0x210 / 4] & 6) != 0) return;
        if (IsPaletteRegister((uint)reg) && (value & 0x80000000) != 0)
        {
            if (Busy) EnqueueCommand((uint)reg, value); else WriteTexturePalette((uint)reg, value);
            return; // Palette writes inhibit the aliased NCC coefficient write.
        }
        if (reg is 0x80 or 0x100 or 0x120 or 0x124 or 0x128)
        {
            if (reg is 0x80 or 0x100) ValidatePipeline(_registers,_nonfiniteParameters);
            EnqueueCommand((uint)reg, value); return;
        }
        if (reg is >= 0x88 and <= 0xfc)
        {
            float input = BitConverter.Int32BitsToSingle((int)value);
            int target = reg - 0x80; int fractional = target < 0x20 ? 4 : (target & 0x1f) switch { 0x14 or 0x18 => 18, 0x1c => 30, _ => 12 };
            // Parameter writes do not render. Preserve unconverted nonfinite payloads until the actual command can determine whether the channel is used.
            if (!float.IsFinite(input)) { _registers[target/4]=value; _nonfiniteParameters|=1u<<(target/4-2); return; }
            value = unchecked((uint)(long)Math.Truncate((double)input * (1L << fractional))); reg = target;
        }
        if(reg is >= 8 and <= 0x7c)_nonfiniteParameters&=~(1u<<(reg/4-2));
        _registers[reg / 4] = value;
    }
    /// <summary>Captures current setup for ordered commands/uploads; later register writes cannot alter an already queued operation.</summary>
    private void EnqueueCommand(uint operation, uint value)
    {
        uint[] command;
        if((operation&0x800000)!=0)command=[operation,value,_registers[0x300/4],_registers[0x304/4],_registers[0x30c/4],_registers[0x310/4],_registers[0x314/4],_registers[0x318/4]];
        else if((operation&0x400000)!=0)command=[operation,value,_registers[0x110/4],_registers[0x114/4],_registers[0x214/4],_registers[0x218/4],_registers[0x21c/4]];
        else if(operation is 0x120 or 0x128 || IsPaletteRegister(operation))command=[operation,value];
        else {command=new uint[259];_registers.CopyTo(command,0);command[256]=operation;command[257]=value;command[258]=_nonfiniteParameters;}
        if (MemoryFifoWords==0?_commands.Count>=63:_fifoWords+command.Length>MemoryFifoWords)
            throw new InvalidOperationException("SST-1 bounded ordered FIFO exhausted; poll the actual reported free space before another command.");
        _commands.Enqueue(command);_fifoWords+=command.Length;if(operation==0x128)_pendingSwaps++; if (_event == 0) ScheduleCommand();
    }
    /// <summary>Bounds the own ordered work queue by a programmed separate tail of the installed FBI DRAM and the original sixteen-bit free-space field.</summary>
    private int MemoryFifoWords
    {
        get
        {
            if((_registers[0x210/4]&0x2000)==0)return 0;
            uint rows=_registers[0x200/4];int first=(int)(rows>>8&1023),last=(int)(rows>>18&1023),allocation=(int)(_registers[0x218/4]>>11&511);
            return first>last||last>=DramBytes/4096||first<allocation*3?0:Math.Min(65535,(last-first+1)*1024);
        }
    }
    /// <summary>Reads the preserved command tag independently from its bounded full draw or compact transfer shape.</summary>
    private static uint CommandOperation(uint[] c)=>c[c.Length==259?256:0];
    /// <summary>Reads the original command/data word from either saved command shape.</summary>
    private static uint CommandValue(uint[] c)=>c[c.Length==259?257:1];
    /// <summary>Rebuilds only transfer-specific register operands; the scratch image is derived and never replaces saved command data.</summary>
    private static void ExpandCommand(uint[] c,Span<uint> p)
    {
        p.Clear();if(c.Length==259)
        {
            c.AsSpan(0,256).CopyTo(p);
            // Only the derived raster image converts nonfinite alpha/S/T inputs to their signed fixed-point endpoints.
            // The authoritative register/queued words and mask retain every original IEEE payload bit.
            uint special=c[258]&0x1c1c1c00;
            for(int bit=10;bit<=28;bit++)if((special&(1u<<bit))!=0)
                p[bit+2]=(bit&7)==2
                    ? (c[bit+2]&0x80000000)==0?0x7fffffu:0x800001u
                    : (c[bit+2]&0x80000000)==0?0x7fffffffu:0x80000001u;
            return;
        }
        if(c.Length==8){p[0x300/4]=c[2];p[0x304/4]=c[3];p[0x30c/4]=c[4];p[0x310/4]=c[5];p[0x314/4]=c[6];p[0x318/4]=c[7];}
        else if(c.Length==7){p[0x110/4]=c[2];p[0x114/4]=c[3];p[0x214/4]=c[4];p[0x218/4]=c[5];p[0x21c/4]=c[6];}
    }
    /// <summary>Schedules bounded work on guest time only; synchronized swaps wait for an actual later retrace boundary.</summary>
    private void ScheduleCommand()
    {
        uint[] c = _commands.Peek();uint operation=CommandOperation(c),value=CommandValue(c); ulong delay = operation is 0x80 or 0x100 or 0x124 ? RasterDelay(operation,c) : 1000;
        if (operation == 0x128 && (value & 1) != 0) delay = 16_666_667 - _clock.Now % 16_666_667 + (value >> 1 & 255) * 16_666_667UL;
        _deadline = checked(_clock.Now + delay);
        _event = _clock.ScheduleAfter(delay, CompleteCommand, this, "complete");
    }
    /// <summary>Commits one command before the next starts; all pending work and deadlines survive full snapshots.</summary>
    private void CompleteCommand()
    {
        _event = 0; _deadline = 0; uint[] c = _commands.Dequeue();_fifoWords-=c.Length;
        uint operation=CommandOperation(c),value=CommandValue(c);if(operation==0x128)_pendingSwaps--;ExpandCommand(c,_commandScratch);
        if ((operation & 0x800000) != 0) WriteTextureCore(_commandScratch, operation & 0x7fffff, value);
        else if ((operation & 0x400000) != 0) WriteLfbCore(_commandScratch, operation & 0x3fffff, value, (operation & 0x10000000) != 0 ? 2 : 4);
        else if (IsPaletteRegister(operation)) WriteTexturePalette(operation, value);
        else switch (operation)
        {
            case 0x80: case 0x100: DrawTriangle(_commandScratch); break;
            case 0x124: FastFill(_commandScratch); break;
            case 0x128: _front ^= 1; _revision++; break;
            case 0x120: if ((value & 1) != 0) Array.Clear(_registers, 0x14c / 4, 5); break;
        }
        if (_commands.Count != 0) ScheduleCommand();
    }
    /// <summary>Checks serialized dimensions, original fixed PCI bits and the complete queued-operation shape before adoption.</summary>
    private void ValidateState()
    {
        ValidateCard(_config, _front, _event, _deadline, _commands.ToArray());
        ValidateNonfiniteParameters(_registers,_nonfiniteParameters);
        _fifoWords=_commands.Sum(c=>c.Length);_pendingSwaps=_commands.Count(c=>CommandOperation(c)==0x128);Array.Clear(_commandScratch);
        ValidateDac(_dacPll, _dacPalette, _dacReadIndex, _dacWriteIndex, _dacReadPhase, _dacWritePhase, _dacPaletteReadPhase, _dacPaletteWritePhase);
        if (_frameId < 0 || _clut.Any(v => v > 0xffffff) || !ValidTexturePalette(_texturePalette)) throw new InvalidDataException("Invalid SST-1 gamma/palette table or frame sequence.");
        _revision++;
    }
    /// <summary>Validates typed saved data without constructing a device or opening a medium.</summary>
    public static void ValidateSavedState(StateBlock block)
    {
        var r = new StateReader(block, "DOSEmulator.Hardware.VoodooGraphics");
        ValidateCard(r.Value<byte[]>("_config"), r.Value<int>("_front"), r.Value<long>("_event"), r.Value<ulong>("_deadline"), r.Value<uint[][]>("_commands"));
        ValidateDac(r.Value<byte[]>("_dacPll"), r.Value<byte[]>("_dacPalette"), r.Value<byte>("_dacReadIndex"), r.Value<byte>("_dacWriteIndex"),
            r.Value<byte>("_dacReadPhase"), r.Value<byte>("_dacWritePhase"), r.Value<byte>("_dacPaletteReadPhase"), r.Value<byte>("_dacPaletteWritePhase"));
        _ = r.Value<byte>("_dacReadLatch"); _ = r.Value<byte>("_dacCommand"); _ = r.Value<byte>("_dacMask");
        _ = r.Value<byte>("_dacPaletteReadIndex"); _ = r.Value<byte>("_dacPaletteWriteIndex");
        var registers=r.Value<uint[]>("_registers");ValidateNonfiniteParameters(registers,r.Value<uint>("_nonfiniteParameters"));
        if (registers is not { Length: 256 } || !ValidBanks(r.Value<byte[][]>("_fbi")) ||
            !ValidBanks(r.Value<byte[][]>("_texture")) || !ValidTexturePalette(r.Value<uint[]>("_texturePalette")) || r.Value<uint[]>("_clut") is not { Length: 33 } clut || clut.Any(v => v > 0xffffff) ||
            r.Value<long>("_frameId") < 0) throw new InvalidDataException("Invalid SST-1 DRAM/register dimensions or scanout sequence.");
        r.Finish();
    }
    /// <summary>Checks four complete bounded banks without increasing the common snapshot string/collection limits.</summary>
    private static bool ValidBanks(byte[][] banks) => banks is { Length: 4 } && banks.All(b => b is { Length: BankBytes });
    /// <summary>Checks the fixed PCI identity and bounded immutable command data under both standalone and full-machine imports.</summary>
    private static void ValidateCard(byte[] config, int front, long token, ulong deadline, uint[][] commands)
    {
        if (config is not { Length: 256 } || commands is null || front is < 0 or > 1 || token < 0 || (token == 0) != (commands.Length == 0) || (deadline == 0) != (token == 0) ||
            commands.Length > 32767 || commands.Any(c => c is null || !ValidCommand(c)) || commands.Sum(c=>(long)c.Length)>65535 ||
            config[0] != 0x1a || config[1] != 0x12 || config[2] != 1 || config[3] != 0 || config[8] != 2 || config[0x3d] != 1 ||
            (config[4] & ~2) != 0 || (config[0x40] & 8) != 0 || (config[0x41] & 0xf0) != 0 ||
            config.Where((b, i) => i is not (0 or 1 or 2 or 4 or 8 or 0x13 or 0x3c or 0x3d or 0x40 or 0x41) && b != 0).Any())
            throw new InvalidDataException("Invalid SST-1 fixed configuration or pending command state.");
        try
        {
            Span<uint> p=stackalloc uint[256];
            foreach (uint[] c in commands)
            {
                uint operation=CommandOperation(c);ExpandCommand(c,p);
                if ((operation & 0x800000) != 0) ValidateTextureLayout(p,true);
                else if ((operation & 0x400000) != 0) { if (!ValidateLfbWrite(p, (operation & 0x10000000) != 0 ? 2 : 4)) throw new InvalidDataException("Invalid queued LFB transfer width."); }
                else if (operation is 0x80 or 0x100) ValidatePipeline(p,c[258]);
            }
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid queued SST-1 pipeline state.", error); }
    }
    /// <summary>Validates each saved register, texture or direct-LFB command tag and its complete aligned aperture extent.</summary>
    private static bool ValidCommand(uint[] c)
    {
        if(c is not {Length:2 or 7 or 8 or 259})return false;
        uint op=CommandOperation(c);
        if(c.Length==259){if((c[258]>>30)!=0)return false;ValidateNonfiniteParameters(c,c[258]);return op is 0x80 or 0x100 or 0x124;}
        return c.Length==2?op is 0x120 or 0x128 || IsPaletteRegister(op)&&(c[1]&0x80000000)!=0:c.Length==8?(op&0xffe00003)==0x800000:
            (op&0xefc00001)==0x400000&&((op&0x10000000)!=0||(op&3)==0);
    }
    /// <summary>Requires a bounded authoritative mask and the original nonfinite payload in every marked setup latch.</summary>
    private static void ValidateNonfiniteParameters(uint[] registers,uint mask)
    {
        if(registers is null||registers.Length<256||(mask>>30)!=0)throw new InvalidDataException("Invalid SST-1 deferred float parameter state.");
        if((registers[0x21c/4]&0x1f00)!=0x1000)throw new InvalidDataException("Invalid SST-1 revision-two power-on straps.");
        for(int bit=0;bit<30;bit++)if((mask&(1u<<bit))!=0&&float.IsFinite(BitConverter.Int32BitsToSingle((int)registers[bit+2])))
            throw new InvalidDataException("SST-1 deferred float mask does not describe its preserved payload.");
    }
}
