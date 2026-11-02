// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VoodooGraphics
{
    /// <summary>Rejects currently unsupported rendering modes before enqueue, preserving an honest limited hardware contract.</summary>
    private static void ValidatePipeline(ReadOnlySpan<uint> p,uint nonfinite = 0)
    {
        uint mode = p[0x110 / 4], texture = p[0x300 / 4];
        if ((mode & (1u << 18)) != 0 || (p[0x218 / 4] & 16) != 0)
            throw new InvalidOperationException("SST-1 destination-alpha and triple buffering remain outside the development renderer.");
        if ((p[0x108/4]&1)!=0 && (p[0x108/4]&~63u)!=0)
            throw new InvalidOperationException("SST-1 reserved fog controls remain unsupported.");
        if ((p[0x104 / 4] & 0x8000000) != 0)
        {
            ValidateTextureLayout(p);
            if ((texture & 0x40000000) != 0) throw new InvalidOperationException("SST-1 trilinear filtering remains outside the development renderer.");
        }
        if ((mode & 4) != 0 && (mode & 0x1000) == 0 || (texture >> 14 & 7) > 3 || (texture >> 23 & 7) > 3 ||
            (p[0x104 / 4] >> 10 & 7) > 4 || (p[0x104 / 4] >> 19 & 7) > 4 ||
            (p[0x10c / 4] >> 8 & 15) is >= 8 and <= 14 || (p[0x10c / 4] >> 12 & 15) is >= 8 and <= 14)
            throw new InvalidOperationException("Reserved/unsupported SST-1 combiner or rotating-stipple selector.");
        if(nonfinite==0)return;
        uint needed=63;
        // Include the start and both derivatives for one selected setup channel.
        uint Parameters(int n)=>1u<<(n+6)|1u<<(n+14)|1u<<(n+22);
        uint path=p[0x104/4];bool iteratedRgb=UsesIteratedRgb(path);
        if(iteratedRgb)needed|=Parameters(0)|Parameters(1)|Parameters(2);
        needed|=Parameters(4); // Alpha is still conservatively required by the bounded combiner.
        if((mode&0x410)!=0&&(mode&8)==0||(path>>5&3)>=2)needed|=Parameters(3);
        if((mode&0x410)!=0&&(mode&8)!=0)needed|=Parameters(7);
        uint fog=p[0x108/4];
        if((fog&0x31)==1&&(fog&8)==0)needed|=Parameters(7);
        if((fog&0x31)==0x11)needed|=Parameters(3);
        if((path&0x8000000)!=0&&(p[0x320/4]&0x40000)==0){needed|=Parameters(5)|Parameters(6);if((texture&1)!=0)needed|=Parameters(7);}
        // The derived raster image saturates the three supported setup channels; the queued IEEE payload remains intact.
        if((nonfinite&needed&~0x1c1c1c00u)!=0)throw new InvalidOperationException($"SST-1 active nonfinite render parameter mask {nonfinite&needed&~0x1c1c1c00u:X8} remains unsupported.");
    }
    /// <summary>Truncates the signed fixed-point RGB parameter to its actual 24-bit setup latch.</summary>
    private static long SignedParameter(uint value, int parameter) => parameter is 0 or 1 or 2 or 4 ? unchecked((int)(value << 8)) >> 8 : unchecked((int)value);
    /// <summary>Interpolates one parameter at a pixel center, including optional 12.4 subpixel adjustment relative to vertex A.</summary>
    private static long Iterate(ReadOnlySpan<uint> p, int parameter, int x, int y)
    {
        long start = SignedParameter(p[8 + parameter], parameter), dx = SignedParameter(p[16 + parameter], parameter), dy = SignedParameter(p[24 + parameter], parameter);
        int ax = unchecked((short)p[2]), ay = unchecked((short)p[3]);
        if ((p[0x104 / 4] & 0x4000000) != 0) return start + (dx * ((long)x * 16 + 8 - ax) + dy * ((long)y * 16 + 8 - ay)) / 16;
        return start + dx * (x - (ax >> 4)) + dy * (y - (ay >> 4));
    }
    /// <summary>Identifies actual RGB dependencies of the supported combiner; zeroed other inputs and constant locals need no iterated color.</summary>
    private static bool UsesIteratedRgb(uint path)
    {
        uint combine=path>>8;
        return (path&3)==0&&(combine&1)==0||(path&16)==0&&((combine&66)!=0||(combine>>2&7)==1);
    }
    /// <summary>Clamps an interpolated signed 12.12 color to one output byte.</summary>
    private static int Color(long value) => (int)Math.Clamp(value >> 12, 0, 255);
    /// <summary>Tests the original eight ordered depth/alpha comparison functions.</summary>
    private static bool Compare(int op, int a, int b) => op switch
    { 0 => false, 1 => a < b, 2 => a == b, 3 => a <= b, 4 => a > b, 5 => a != b, 6 => a >= b, _ => true };
    /// <summary>Converts 24-bit input to RGB565 with explicit ordered development dithering or exact truncation.</summary>
    private static ushort Rgb565(int r, int g, int b, int x, int y, bool dither, bool small = false)
    {
        // Range correction precedes matrix addition. Saturating the old uncorrected sum lost seven colors in the original driver's readback table.
        int bias = dither ? DitherBias(x,y,small) : 0;
        r=Math.Clamp(r,0,255);g=Math.Clamp(g,0,255);b=Math.Clamp(b,0,255);
        int red=dither?(r*2-r/16+r/128+bias)/16:r/8,green=dither?(g*4-g/16+g/64+bias)/16:g/4,blue=dither?(b*2-b/16+b/128+bias)/16:b/8;
        return (ushort)(red << 11 | green << 5 | blue);
    }
    /// <summary>Own ordered quantization matrices; filter/analog precision remains a separate development boundary.</summary>
    private static int DitherBias(int x,int y,bool small)
    {
        ReadOnlySpan<byte> matrix=small?[8,10,11,9]:[0,8,2,10,12,4,14,6,3,11,1,9,15,7,13,5];
        return matrix[small?(y&1)*2+(x&1):(y&3)*4+(x&3)];
    }
    /// <summary>Normalizes the original 16.32 internal reciprocal-W into a four-bit exponent/inverted twelve-bit mantissa and applies the depth carry/clamp.</summary>
    private static int WDepth(long fixedW)
    {
        long w=fixedW<<2;
        if(w<0||w>=1L<<32)return 0;
        if(w<65536)return 65535;
        int exponent=System.Numerics.BitOperations.LeadingZeroCount((uint)w);
        int mantissa=(int)(((ulong)w<<exponent)>>19)&4095;
        return Math.Min(65535,(exponent<<12|(~mantissa&4095))+1);
    }
    /// <summary>Selects original iterated-alpha/Z or 64-entry reciprocal-W fog, including packed 6.2 deltas and the signed depth bias.</summary>
    private static int FogAlpha(ReadOnlySpan<uint> p,int alpha,int z,long w)
    {
        uint control=p[0x108/4];if((control&16)!=0)return z>>8;if((control&8)!=0)return alpha;
        int depth=WDepth(w);if((p[0x110/4]&0x10000)!=0)depth=Math.Clamp(depth+unchecked((short)p[0x130/4]),0,65535);
        uint pair=p[0x160/4+(depth>>11)]>>((depth>>10&1)*16);
        return Math.Clamp((int)(pair>>8&255)+(int)((pair&255)*(uint)(depth>>2&255)>>10),0,255);
    }
    /// <summary>Applies the original fog add/multiply muxes to RGB before destination blending; alpha remains the color-combine result.</summary>
    private static int FogChannel(int input,int color,int alpha,uint control)
    {
        if((control&32)!=0)return Math.Min(255,color+((control&4)==0?input:0));
        int source=(control&4)==0?input:0,other=(control&2)==0?color:0;
        return Math.Clamp(source+((other-source)*(alpha+1)>>8),0,255);
    }
    /// <summary>Clears only the inclusive-low/exclusive-high clip rectangle and enabled RGB/auxiliary planes.</summary>
    private void FastFill(ReadOnlySpan<uint> p)
    {
        // Raster storage exists before video timing is programmed; original initialization renders and reads it with videoDimensions=0.
        uint mode = p[0x110 / 4], color = p[0x148 / 4]; int width = (int)(p[0x214 / 4] >> 4 & 15) * 64, height = 1024;
        int left = Math.Min(width, (int)(p[0x118 / 4] >> 16 & 1023)), right = Math.Min(width, (int)(p[0x118 / 4] & 1023));
        int low = Math.Min(height, (int)(p[0x11c / 4] >> 16 & 1023)), high = Math.Min(height, (int)(p[0x11c / 4] & 1023));
        for (int y = low; y < high; y++) for (int x = left; x < right; x++)
        {
            int row = (mode & 0x20000) != 0 ? (int)(p[0x21c / 4] >> 22 & 1023) - y : y;
            if ((mode & 0x200) != 0) Pixel(p, Buffer((int)(mode >> 14 & 3)), x, row, Rgb565((int)(color >> 16 & 255), (int)(color >> 8 & 255), (int)(color & 255), x, y, (mode & 0x100) != 0,(mode&0x800)!=0));
            if ((mode & 0x400) != 0) Pixel(p, 2, x, row, (ushort)p[0x130 / 4]);
        }
    }
    /// <summary>Computes an exact signed fixed-point edge cross product without host floating-point raster decisions.</summary>
    private static long Edge(int ax, int ay, int bx, int by, int x, int y) => ((long)bx - ax) * (y - ay) - ((long)by - ay) * (x - ax);
    /// <summary>Renders the bounded original 12.4 triangle setup, interpolated RGBA/Z and optional single-TMU point sampling.</summary>
    private void DrawTriangle(ReadOnlySpan<uint> p)
    {
        if(!TriangleBounds(p,out var triangle))return;
        double lodBase = 0;
        if ((p[0x104 / 4] & 0x8000000) != 0 && (p[0x304 / 4] & 63) != (p[0x304 / 4] >> 6 & 63))
        {
            double sx=SignedParameter(p[21],5), sy=SignedParameter(p[29],5);
            double tx=SignedParameter(p[22],6), ty=SignedParameter(p[30],6);
            double footprint=Math.Sqrt(Math.Max(sx*sx+tx*tx,sy*sy+ty*ty));
            lodBase=footprint>0?Math.Log2(footprint/(1<<18)):double.NegativeInfinity;
        }
        for(int y=triangle.Low;y<triangle.High;y++)
        {
            if(!TriangleSpan(triangle,y,out int first,out int last))continue;
            for(int x=first;x<=last;x++)Shade(p,x,y,lodBase);
        }
    }
    /// <summary>Samples the selected mip level with independent minification/magnification filters and eight-bit bilinear weights.</summary>
    private uint SampleTexture(ReadOnlySpan<uint> p, int x, int y, double lodBase)
    {
        // The original TREX serial configuration output bypasses texel fetch/combine. This board has one revision-one TMU and no downstream chip.
        if((p[0x320/4]&0x40000)!=0)return 0xff000001;
        uint mode = p[0x300 / 4], lodControl=p[0x304 / 4];
        int minimum=(int)(lodControl&63), maximum=(int)(lodControl>>6&63);
        long s = Iterate(p, 5, x, y), t = Iterate(p, 6, x, y);
        long w=0;
        if ((mode & 1) != 0)
        {
            w=Iterate(p,7,x,y);
            if(w<=0&&(mode&8)!=0)s=t=0;
            else if(w!=0){s=PerspectiveDivide(s,w);t=PerspectiveDivide(t,w);}
            else s=t=0;
        }
        else { s >>= 10; t >>= 10; }
        double lod=minimum/4.0;
        if(minimum!=maximum)
        {
            int bias=(int)(lodControl>>12&63);bias=(bias<<26)>>26;
            lod=lodBase+bias/4.0;
            if((mode&1)!=0&&w>0)lod-=Math.Log2((double)w/(1L<<30));
            if((mode&16)!=0)lod+=DitherBias(x,y,false)/16.0;
            lod=Math.Clamp(lod,minimum/4.0,Math.Min(8,maximum/4.0));
        }
        int level=Math.Clamp((int)lod,0,8);
        bool bilinear=(mode&(lod==minimum/4.0?4u:2u))!=0;
        s >>= level; t >>= level;
        uint texel;
        if(!bilinear)texel=ReadTexel(p,level,(int)(s>>8),(int)(t>>8));
        else
        {
            s-=128;t-=128;int u=(int)(s>>8),v=(int)(t>>8),fx=(int)(s&255),fy=(int)(t&255);texel=0;
            uint a=ReadTexel(p,level,u,v),b=ReadTexel(p,level,u+1,v),c=ReadTexel(p,level,u,v+1),d=ReadTexel(p,level,u+1,v+1);
            for(int shift=0;shift<=24;shift+=8)
            {
                long value=((a>>shift&255)*(256-fx)+(b>>shift&255)*fx)*(256-fy)+((c>>shift&255)*(256-fx)+(d>>shift&255)*fx)*fy;
                texel|=(uint)(value>>16)<<shift;
            }
        }
        uint result = 0;
        int texelAlpha = (int)(texel >> 24);
        for (int shift = 0; shift <= 24; shift += 8)
            result |= (uint)Combine(255, (int)(texel >> shift & 255), 255, texelAlpha, 0, mode >> (shift == 24 ? 21 : 12)) << shift;
        return result;
    }
    /// <summary>Uses exact signed 64-bit division when the scaled numerator fits; wide values and the signed division overflow case retain the original Int128 calculation.</summary>
    private static long PerspectiveDivide(long coordinate,long w)
    {
        if(coordinate>=long.MinValue>>20&&coordinate<=long.MaxValue>>20)
        {
            long numerator=coordinate<<20;
            if(numerator!=long.MinValue||w!=-1)return numerator/w;
        }
        return (long)(((Int128)coordinate<<20)/w);
    }
    /// <summary>Executes one original color-combine channel with the documented zero/subtract/multiply/add/invert selectors.</summary>
    private static int Combine(int other, int local, int otherAlpha, int localAlpha, int textureAlpha, uint control)
    {
        int factor = (control >> 2 & 7) switch { 0 => 0, 1 => local, 2 => otherAlpha, 3 => localAlpha, 4 => textureAlpha, _ => 0 };
        factor += factor >> 7; if ((control & 32) == 0) factor = 256 - factor;
        int value = (((control & 1) != 0 ? 0 : other) - ((control & 2) != 0 ? local : 0)) * factor / 256;
        if ((control & 64) != 0) value += local; if ((control & 128) != 0) value += localAlpha;
        value = Math.Clamp(value, 0, 255); return (control & 256) != 0 ? 255 - value : value;
    }
    /// <summary>Processes real color/depth masks, clip/stipple/chroma/alpha/depth tests, color combine and bounded RGB blending.</summary>
    private void Shade(ReadOnlySpan<uint> p, int x, int y, double lodBase)
    {
        uint mode = p[0x110 / 4], path = p[0x104 / 4], alpha = p[0x10c / 4];
        if ((mode & 1) != 0 && (x < (p[0x118 / 4] >> 16 & 1023) || x >= (p[0x118 / 4] & 1023) || y < (p[0x11c / 4] >> 16 & 1023) || y >= (p[0x11c / 4] & 1023))) return;
        if ((mode & 4) != 0)
        {
            if ((mode & 0x1000) == 0) throw new InvalidOperationException("Rotating SST-1 stipple requires a later pipeline implementation.");
            if ((p[0x140 / 4] & 1u << (((y & 3) << 3) | (~x & 7))) == 0) return;
        }
        _registers[0x14c / 4] = (_registers[0x14c / 4] + 1) & 0xffffff;
        int a = Color(Iterate(p, 4, x, y)), iteratedZ=(int)Math.Clamp(Iterate(p,3,x,y)>>12,0,65535);
        int z=(mode&8)!=0?WDepth(Iterate(p,7,x,y)):iteratedZ;
        uint tex = (path & 0x8000000) != 0 ? SampleTexture(p, x, y, lodBase) : 0xffffffff, c0 = p[0x144 / 4], c1 = p[0x148 / 4];
        int ta = (int)(tex >> 24), otherAlpha = (path >> 2 & 3) switch { 0 => a, 1 => ta, _ => (int)(c1 >> 24) };
        int localAlpha = (path >> 5 & 3) switch { 0 => a, 1 => (int)(c0 >> 24), _ => iteratedZ >> 8 };
        int outputAlpha = Combine(otherAlpha, localAlpha, otherAlpha, localAlpha, ta, path >> 17);
        uint fog=p[0x108/4];int fogAlpha=(fog&1)!=0&&(fog&32)==0?FogAlpha(p,a,iteratedZ,Iterate(p,7,x,y)):0;
        if ((mode & 0x2000) != 0 && (outputAlpha & 1) == 0) return;
        if ((alpha & 1) != 0 && !Compare((int)(alpha >> 1 & 7), outputAlpha, (int)(alpha >> 24))) { _registers[0x158 / 4]++; return; }
        int row = (mode & 0x20000) != 0 ? (int)(p[0x21c / 4] >> 22 & 1023) - y : y;
        if ((mode & 0x10000) != 0) z = Math.Clamp(z + unchecked((short)p[0x130 / 4]), 0, 65535);
        int depthCompare = (mode & 0x100000) != 0 ? (int)(p[0x130 / 4] & 65535) : z;
        if ((mode & 16) != 0 && !Compare((int)(mode >> 5 & 7), depthCompare, Pixel(p, 2, x, row))) { _registers[0x154 / 4]++; return; }
        Span<int> rgb = stackalloc int[3];
        uint destination=(alpha&16)!=0?Expand565(Pixel(p,Buffer((int)(mode>>14&3)),x,row)):0;
        bool iteratedRgb=UsesIteratedRgb(path);
        for (int channel = 0; channel < 3; channel++)
        {
            int shift = 16 - channel * 8, iterated = iteratedRgb?Color(Iterate(p, channel, x, y)):0;
            int other = (path & 3) switch { 0 => iterated, 1 => (int)(tex >> shift & 255), _ => (int)(c1 >> shift & 255) };
            int local = (path & 16) != 0 ? (int)(c0 >> shift & 255) : iterated;
            rgb[channel] = Combine(other, local, otherAlpha, localAlpha, ta, path >> 8);
            if((fog&1)!=0)rgb[channel]=FogChannel(rgb[channel],(int)(p[0x12c/4]>>shift&255),fogAlpha,fog);
            if ((alpha & 16) != 0)
            {
                int dest = (int)(destination >> shift & 255);
                if((mode&0x80000)!=0)
                {
                    int bias=DitherBias(x,y,(mode&0x800)!=0);
                    // Remove the matching development quantization contribution before blending; absent stored alpha stays opaque.
                    dest=Math.Max(0,dest-bias/(channel==1?4:2));
                }
                int sourceFactor = BlendFactor((int)(alpha >> 8 & 15), outputAlpha, dest, rgb[channel], true);
                int destinationFactor = BlendFactor((int)(alpha >> 12 & 15), outputAlpha, dest, rgb[channel], false);
                rgb[channel] = Math.Clamp((rgb[channel] * sourceFactor + dest * destinationFactor) / 255, 0, 255);
            }
        }
        if ((mode & 2) != 0 && ((uint)(rgb[0] << 16 | rgb[1] << 8 | rgb[2]) == (p[0x134 / 4] & 0xffffff))) { _registers[0x150 / 4]++; return; }
        if ((mode & 0x200) != 0) Pixel(p, Buffer((int)(mode >> 14 & 3)), x, row, Rgb565(rgb[0], rgb[1], rgb[2], x, y, (mode & 0x100) != 0,(mode&0x800)!=0));
        if ((mode & 0x400) != 0) Pixel(p, 2, x, row, (ushort)z);
        _registers[0x15c / 4] = (_registers[0x15c / 4] + 1) & 0xffffff;
    }
    /// <summary>Provides original common RGB blend factors; absent auxiliary alpha is opaque in the supported RGB/Z mode.</summary>
    private static int BlendFactor(int code, int alpha, int destination, int source, bool sourceFactor) => code switch
    { 0 => 0, 1 => alpha, 2 => sourceFactor ? destination : source, 3 => 255, 4 => 255, 5 => 255 - alpha,
      6 => 255 - (sourceFactor ? destination : source), 7 => 0, 15 => sourceFactor ? 0 : source, _ => throw new InvalidOperationException("Unsupported SST-1 blend selector.") };
}
