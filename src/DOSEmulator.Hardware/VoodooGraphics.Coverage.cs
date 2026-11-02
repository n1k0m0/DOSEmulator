// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VoodooGraphics
{
    /// <summary>Ephemeral oriented setup and clipped integer bounds; no derived geometry is saved in the device.</summary>
    private readonly record struct RasterTriangle(int Ax,int Ay,int Bx,int By,int Cx,int Cy,int Left,int Right,int Low,int High);

    /// <summary>Orients the original signed 12.4 vertices and intersects raster storage with the optional hardware clip rectangle.</summary>
    private static bool TriangleBounds(ReadOnlySpan<uint> p,out RasterTriangle result)
    {
        int ax=unchecked((short)p[2]),ay=unchecked((short)p[3]),bx=unchecked((short)p[4]),by=unchecked((short)p[5]),cx=unchecked((short)p[6]),cy=unchecked((short)p[7]);
        long area=Edge(ax,ay,bx,by,cx,cy);if(area<0){(bx,cx)=(cx,bx);(by,cy)=(cy,by);}
        int width=(int)(p[0x214/4]>>4&15)*64;
        int left=Math.Clamp(Math.Min(ax,Math.Min(bx,cx))>>4,0,width),right=Math.Clamp((Math.Max(ax,Math.Max(bx,cx))+15)>>4,0,width);
        int low=Math.Clamp(Math.Min(ay,Math.Min(by,cy))>>4,0,1024),high=Math.Clamp((Math.Max(ay,Math.Max(by,cy))+15)>>4,0,1024);
        if((p[0x110/4]&1)!=0)
        {
            left=Math.Max(left,(int)(p[0x118/4]>>16&1023));right=Math.Min(right,(int)(p[0x118/4]&1023));
            low=Math.Max(low,(int)(p[0x11c/4]>>16&1023));high=Math.Min(high,(int)(p[0x11c/4]&1023));
        }
        result=new(ax,ay,bx,by,cx,cy,left,right,low,high);return area!=0&&left<right&&low<high;
    }
    /// <summary>Solves one exact top-left half-plane inequality for integer pixel centers, using mathematical floor/ceiling for negative numerators.</summary>
    private static bool ClipRasterEdge(int ax,int ay,int bx,int by,int y,ref int first,ref int last)
    {
        long dx=(long)bx-ax,dy=(long)by-ay,coefficient=-16*dy,constant=dx*((long)y*16+8-ay)-dy*(8L-ax);
        long threshold=by<ay||by==ay&&bx>ax?0:1;
        if(coefficient==0)return constant>=threshold;
        if(coefficient>0)
        {
            long numerator=threshold-constant,quotient=Math.DivRem(numerator,coefficient,out long remainder);
            if(remainder>0)quotient++;if(quotient>last)return false;first=(int)Math.Max(first,quotient);
        }
        else
        {
            long numerator=constant-threshold,quotient=Math.DivRem(numerator,-coefficient,out long remainder);
            if(remainder<0)quotient--;if(quotient<first)return false;last=(int)Math.Min(last,quotient);
        }
        return first<=last;
    }
    /// <summary>Produces the inclusive covered span without per-pixel edge tests or floating-point coverage decisions.</summary>
    private static bool TriangleSpan(RasterTriangle t,int y,out int first,out int last)
    {
        first=t.Left;last=t.Right-1;
        return ClipRasterEdge(t.Ax,t.Ay,t.Bx,t.By,y,ref first,ref last)&&ClipRasterEdge(t.Bx,t.By,t.Cx,t.Cy,y,ref first,ref last)&&
            ClipRasterEdge(t.Cx,t.Cy,t.Ax,t.Ay,y,ref first,ref last);
    }
    /// <summary>Counts actual covered candidate pixels before depth/chroma/alpha tests, independently from the active scanout size.</summary>
    private static ulong TrianglePixels(ReadOnlySpan<uint> p)
    {
        if(!TriangleBounds(p,out var triangle))return 0;
        ulong pixels=0;for(int y=triangle.Low;y<triangle.High;y++)if(TriangleSpan(triangle,y,out int first,out int last))pixels+=(uint)(last-first+1);
        return pixels;
    }
    /// <summary>Counts the same exclusive clear rectangle and installed raster pitch used by the actual clear operation.</summary>
    private static ulong ClearPixels(ReadOnlySpan<uint> p)
    {
        if((p[0x110/4]&0x600)==0)return 0;
        int width=(int)(p[0x214/4]>>4&15)*64;
        int left=Math.Min(width,(int)(p[0x118/4]>>16&1023)),right=Math.Min(width,(int)(p[0x118/4]&1023));
        int low=(int)(p[0x11c/4]>>16&1023),high=(int)(p[0x11c/4]&1023);
        return (ulong)Math.Max(0,right-left)*(ulong)Math.Max(0,high-low);
    }
    /// <summary>Own bounded setup-plus-fill timing: 50 setup clocks, one covered triangle pixel per graphics clock, two clear pixels per clock.
    /// The original SST-1 peak datapath and actual selected ICS A/B PLL replace the old full-screen cost for every small triangle; bus/refresh contention remains unmodeled.</summary>
    private ulong RasterDelay(uint operation,ReadOnlySpan<uint> p)
    {
        ulong pixels=operation==0x124?ClearPixels(p):TrianglePixels(p),cycles=50+(operation==0x124?(pixels+1)/2:pixels);
        int index=20+((_dacPll[28]>>4&1)*2),m=_dacPll[index]&127,n=_dacPll[index+1];
        ulong hz=14_318_000UL*(ulong)(m+2)/((ulong)((n&31)+2)<<(n>>5&3));
        return (cycles*1_000_000_000+hz-1)/hz;
    }
}
