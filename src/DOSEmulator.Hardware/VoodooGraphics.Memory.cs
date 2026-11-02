// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class VoodooGraphics
{
    /// <summary>Resolves front/back/auxiliary DRAM in original 4-KiB video-buffer-offset units and programmed tile pitch.</summary>
    private int PixelAddress(ReadOnlySpan<uint> p, int buffer, int x, int y)
    {
        int pitch = (int)(p[0x214 / 4] >> 4 & 15) * 64;
        int allocation = (int)(p[0x218 / 4] >> 11 & 511) * 4096;
        if (buffer is < 0 or > 2 || x < 0 || y < 0 || pitch == 0 || x >= pitch || allocation == 0) return -1;
        long address = (long)buffer * allocation + ((long)y * pitch + x) * 2;
        return address < 0 || address + 2 > DramBytes ? -1 : (int)address;
    }
    /// <summary>Maps relative front/back selectors to the current displayed physical buffer; auxiliary storage is independent.</summary>
    private int Buffer(int selected) => selected switch { 0 => _front, 1 => _front ^ 1, 2 => 2, _ => -1 };
    /// <summary>Reads one checked physical RGB/depth word without exposing DRAM outside the installed card.</summary>
    private ushort Pixel(ReadOnlySpan<uint> p, int buffer, int x, int y) => PixelAddress(p, buffer, x, y) is int at && at >= 0
        ? BinaryPrimitives.ReadUInt16LittleEndian(_fbi[at >> BankShift].AsSpan(at & (BankBytes - 1), 2)) : (ushort)0xffff;
    /// <summary>Stores one checked physical RGB/depth word and invalidates derived scanout caches.</summary>
    private void Pixel(ReadOnlySpan<uint> p, int buffer, int x, int y, ushort value)
    {
        int at = PixelAddress(p, buffer, x, y); if (at < 0) return;
        BinaryPrimitives.WriteUInt16LittleEndian(_fbi[at >> BankShift].AsSpan(at & (BankBytes - 1), 2), value);
        // Back/auxiliary writes invalidate scanout only when their physical storage overlaps the displayed extent.
        // This includes deliberately overlapping allocations and ignores neither DRAM writes nor later swaps.
        int first=_front*(int)(_registers[0x218/4]>>11&511)*4096;
        long last=(long)first+(long)Height*(int)(_registers[0x214/4]>>4&15)*128;
        if(at>=first&&at<last)_revision++;
    }
    /// <summary>Applies original LFB lane/word swizzles within a complete 32-bit host transfer.</summary>
    private static uint Swizzle(uint value, uint mode, int width, bool read)
    {
        if ((mode & (read ? 1u << 16 : 1u << 12)) != 0)
            value = width == 4 ? BinaryPrimitives.ReverseEndianness(value) : BinaryPrimitives.ReverseEndianness((ushort)value);
        if (width == 4 && (mode & (read ? 1u << 15 : 1u << 11)) != 0) value = value << 16 | value >> 16;
        return value;
    }
    /// <summary>Reads RGB565/depth words at the original fixed 1024-pixel LFB address pitch.</summary>
    private uint ReadLfb(uint offset, int width)
    {
        uint mode = _registers[0x114 / 4], result = 0; int buffer = Buffer((int)(mode >> 6 & 3));
        for (int lane = 0; lane < width; lane += 2)
        {
            uint pixel = (offset + (uint)lane) >> 1; int x = (int)(pixel & 1023), y = (int)(pixel >> 10);
            if ((mode & 0x2000) != 0) y = (int)(_registers[0x21c / 4] >> 22 & 1023) - y;
            result |= (uint)Pixel(_registers, buffer, x, y) << (lane * 8);
        }
        return Swizzle(result, mode, width, true);
    }
    /// <summary>Converts supported original direct LFB pixel formats; unsupported pipeline modes stop explicitly instead of faking rendering.</summary>
    private void WriteLfb(uint offset, uint value, int width)
    {
        if (!ValidateLfbWrite(_registers, width)) return;
        if (Busy) EnqueueCommand(0x400000 | offset | (width == 2 ? 0x10000000u : 0), value);
        else WriteLfbCore(_registers, offset, value, width);
    }
    /// <summary>Checks a complete LFB write mode before it can enter the FIFO or modify any plane.</summary>
    private static bool ValidateLfbWrite(ReadOnlySpan<uint> p, int width)
    {
        uint mode = p[0x114 / 4]; int format = (int)(mode & 15);
        if ((mode & 0x700) != 0 || format is 3 or >= 6 and <= 11 || (p[0x110 / 4] & 0x40000) != 0)
            throw new InvalidOperationException("SST-1 pipeline LFB/alternate RGBA lanes/auxiliary alpha are not implemented by this development renderer.");
        return (format is not (4 or 5 or >= 12 and <= 14)) || width == 4;
    }
    /// <summary>Commits an ordered direct LFB write using precisely the setup captured at that write.</summary>
    private void WriteLfbCore(ReadOnlySpan<uint> p, uint offset, uint value, int width)
    {
        uint mode = p[0x114 / 4]; int format = (int)(mode & 15);
        if (!ValidateLfbWrite(p, width)) return;
        value = Swizzle(value, mode, width, false);
        int bytes = format is 4 or 5 or >= 12 and <= 14 ? 4 : 2;
        for (int lane = 0; lane < width; lane += bytes)
        {
            uint index = offset / (uint)bytes + (uint)(lane / bytes), data = value >> (lane * 8);
            int x = (int)(index & 1023), y = (int)(index >> 10);
            if ((mode & 0x2000) != 0) y = (int)(p[0x21c / 4] >> 22 & 1023) - y;
            ushort rgb = format switch
            {
                0 or 12 => (ushort)data,
                1 or 2 or 13 or 14 => (ushort)((data & 0x7c00) << 1 | (data & 0x3e0) << 1 | (data & 0x200) >> 4 | data & 31),
                _ => Rgb565((int)(data >> 16 & 255), (int)(data >> 8 & 255), (int)(data & 255), x, y, false)
            };
            if (format != 15) Pixel(p, Buffer((int)(mode >> 4 & 3)), x, y, rgb);
            if (format >= 12) Pixel(p, 2, x, y, format == 15 ? (ushort)data : (ushort)(data >> 16));
        }
    }
    /// <summary>Uploads aligned 16-bit square mipmaps through actual LOD/T/S texture aperture fields to one installed TMU.</summary>
    private void WriteTexture(uint offset, uint value)
    {
        if ((_config[0x40] & 2) == 0 || (offset & 0x600000) != 0) return;
        ValidateTextureLayout(_registers, true);
        if (Busy) EnqueueCommand(0x800000 | offset, value); else WriteTextureCore(_registers, offset, value);
    }
    /// <summary>Checks physical texel storage independently from address-selected upload levels and sampling/filter controls.</summary>
    private static void ValidateTextureLayout(ReadOnlySpan<uint> p, bool upload = false)
    {
        uint mode = p[0x300 / 4], lod = p[0x304 / 4], minimum = lod & 63;
        int format = (int)(mode >> 8 & 15);
        if (format is 1 or 6 or 7 or 9 or >= 15 || (lod & 0xc0000) != 0 || upload && (lod & 0x8000000) != 0 ||
            minimum > (lod >> 6 & 63) || (lod >> 6 & 63) > 32)
            throw new InvalidOperationException("SST-1 NCC/split/direct texture layout remains outside the development TMU.");
    }
    /// <summary>Only the original table-zero I/Q ports alias the revision-one TMU's 256-entry RGB palette.</summary>
    private static bool IsPaletteRegister(uint register) => register is >= 0x334 and <= 0x350 && (register & 3) == 0;
    /// <summary>Combines address bit two with data bits 30:24; the remaining 24 bits are real stored RGB, without replacing NCC coefficients.</summary>
    private void WriteTexturePalette(uint register, uint value) => _texturePalette[(value >> 23 & 254) | ((register - 0x334) >> 2 & 1)] = value & 0xffffff;
    /// <summary>Checks every authoritative palette entry before a full saved state can be adopted.</summary>
    private static bool ValidTexturePalette(uint[] palette) => palette is { Length: 256 } && palette.All(v => v <= 0xffffff);
    /// <summary>Commits one original DWORD TMU upload after every earlier draw, using its own captured layout/base.</summary>
    private void WriteTextureCore(ReadOnlySpan<uint> p, uint offset, uint value)
    {
        ValidateTextureLayout(p, true);
        int level = (int)(offset >> 17 & 15); if (level > 8) return;
        int bytes = (p[0x300 / 4] & 0x800) == 0 ? 1 : 2;
        var (width,height) = TextureDimensions(p,level);
        int x = bytes == 1 && (p[0x300 / 4] & 0x80000000) != 0 ? (int)(offset & 252) : (int)(offset >> 1 & 254);
        if(bytes==1)x&=~3;
        int y = (int)(offset >> 9 & 255);
        if (x >= width || y >= height) return;
        uint lod=p[0x304/4];if((lod&0x2000000)!=0)value=BinaryPrimitives.ReverseEndianness(value);if((lod&0x4000000)!=0)value=value<<16|value>>16;
        int start = TextureStart(p, level), at = (start + (y * width + x) * bytes) & (DramBytes - 1);
        for(int lane=0;lane<4&&x+lane/bytes<width;lane++)TextureByte(at+lane,(byte)(value>>(lane*8)));
    }
    /// <summary>Derives the independent S/T dimensions from the selected original power-of-two aspect ratio.</summary>
    private static (int Width,int Height) TextureDimensions(ReadOnlySpan<uint> p,int level)
    {
        uint lod=p[0x304/4];int size=256>>level,aspect=(int)(lod>>21&3),narrow=Math.Max(1,size>>aspect);
        return(lod&0x100000)!=0?(size,narrow):(narrow,size);
    }
    /// <summary>Reads one wrapped physical TMU byte from bounded saved banks.</summary>
    private byte TextureByte(int at) => _texture[(at & (DramBytes - 1)) >> BankShift][at & (BankBytes - 1)];
    /// <summary>Stores one wrapped physical TMU byte without exposing a texture read aperture.</summary>
    private void TextureByte(int at, byte value) => _texture[(at & (DramBytes - 1)) >> BankShift][at & (BankBytes - 1)] = value;
    /// <summary>Computes packed original 8/16-bit mip offsets and optional level-specific bases, including half-unit tails and physical DRAM wrapping.</summary>
    private int TextureStart(ReadOnlySpan<uint> p, int level)
    {
        int first=0,register=0x30c;
        if((p[0x304/4]&0x1000000)!=0){first=Math.Min(3,level);register+=first*4;}
        int offset = (int)(p[register / 4] & 0x7ffff) * 8,aspect=(int)(p[0x304/4]>>21&3),unit=(p[0x300/4]&0x800)!=0?8:4;
        for (int l = first; l < level; l++)
        {
            int units=l>=7?1:l==6?(aspect==0?4:2):l==5?(aspect==0?16:aspect==1?8:4):(1<<(14-l*2))>>aspect;
            offset+=units*unit;
        }
        return offset & (DramBytes - 1);
    }
    /// <summary>Expands one actual uncompressed texel with the original integer channel replication and alpha/intensity interpretation.</summary>
    private uint ReadTexel(ReadOnlySpan<uint> p,int level,int x,int y)
    {
        var (width,height)=TextureDimensions(p,level);uint mode=p[0x300/4];
        x=(mode&64)!=0?Math.Clamp(x,0,width-1):x&(width-1);y=(mode&128)!=0?Math.Clamp(y,0,height-1):y&(height-1);
        int format=(int)(mode>>8&15),bytes=format>=8?2:1,at=TextureStart(p,level)+(y*width+x)*bytes;
        uint value=(uint)(TextureByte(at)|(bytes==2?TextureByte(at+1)<<8:0));
        // Expand the original packed three/three/two channel bits by replication.
        uint Rgb332(uint c){uint r=c>>5&7,g=c>>2&7,b=c&3;return(r<<5|r<<2|r>>1)<<16|(g<<5|g<<2|g>>1)<<8|b*85;}
        return format switch
        {
            0=>0xff000000|Rgb332(value),2=>value*0x01010101,3=>0xff000000|value*0x010101,
            4=>(value>>4)*17<<24|(value&15)*17*0x010101,5=>0xff000000|_texturePalette[value],8=>(value>>8)<<24|Rgb332(value),10=>Expand565((ushort)value),
            11=>(value>>15)*255<<24|((value>>10&31)*8+(value>>12&7))<<16|((value>>5&31)*8+(value>>7&7))<<8|(value&31)*8+(value>>2&7),
            12=>(value>>12)*17<<24|(value>>8&15)*17<<16|(value>>4&15)*17<<8|(value&15)*17,
            13=>(value>>8)<<24|(value&255)*0x010101,14=>(value>>8)<<24|_texturePalette[value&255],_=>throw new InvalidOperationException("Unsupported SST-1 texel format")
        };
    }
    /// <summary>Expands a physical RGB565 word by bit replication, before the real card gamma lookup.</summary>
    private static uint Expand565(ushort c)
    { uint r = (uint)(c >> 11), g = (uint)(c >> 5 & 63), b = (uint)(c & 31); return 0xff000000 | (r << 3 | r >> 2) << 16 | (g << 2 | g >> 4) << 8 | b << 3 | b >> 2; }
    /// <summary>Interpolates the original 33-entry per-channel gamma lookup; no host gamma or endpoint is consulted.</summary>
    private uint Gamma(uint rgb)
    {
        uint result = 0xff000000;
        for (int shift = 0; shift <= 16; shift += 8)
        { int c = (int)(rgb >> shift & 255), i = c >> 3, fraction = c & 7; uint a = _clut[i] >> shift & 255, b = _clut[i + 1] >> shift & 255; result |= ((a * (uint)(8 - fraction) + b * (uint)fraction) >> 3) << shift; }
        return result;
    }
    /// <summary>Publishes completed stable Voodoo pixels through the same leased surface contract as VGA; guest work is already joined.</summary>
    public VideoFrame Capture(ulong now, VgaFrameBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer); int width = Width, height = Math.Max(1, Height);
        if (!ReferenceEquals(buffer.Owner, this) || buffer.Revision != _revision || buffer.Width != width || buffer.Height != height)
        {
            if (buffer.Pixels.Length != width * height) buffer.Pixels = new uint[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) buffer.Pixels[y * width + x] = Gamma(Expand565(Pixel(_registers, _front, x, y)));
            buffer.Owner = this; buffer.Revision = _revision; buffer.Width = width; buffer.Height = height; buffer.Phase = 0; buffer.RenderedFrames++;
        }
        return buffer.Frame = new(++_frameId, 0x535354, now, width, height, buffer.Pixels);
    }
}
