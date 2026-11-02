// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Hardware;
using System.Text.Json;
using System.Security.Cryptography;
using DOSEmulator.Core;

/// <summary>Independent original uncompressed-texel, aspect, filter and normalized reciprocal-W oracles required by real Glide initialization.</summary>
internal static class VoodooTexturePipelineTests
{
    /// <summary>Pure-query control peer; an attempted preflight bus effect fails immediately.</summary>
    private sealed class ByteSpanPeer(uint start):IMemoryDecoder
    {
        /// <summary>Offers precisely eight high physical bytes.</summary>
        public bool Decodes(uint address)=>address>=start&&(ulong)address< (ulong)start+8;
        /// <summary>Span admission must never perform a speculative device read.</summary>
        public byte ReadDecodedByte(uint address)=>throw new Exception("Preflight read a byte device");
        /// <summary>Span admission must never perform a speculative device write.</summary>
        public void WriteDecodedByte(uint address,byte value)=>throw new Exception("Preflight wrote a byte device");
    }
    private const uint Base=0xd0000000,Lfb=Base+0x400000;
    /// <summary>Requires real stored pixels and rejects any silently substituted rendering path.</summary>
    private static void Check(bool value){if(!value)throw new Exception("SST-1 texture/depth pixel oracle differs");}
    /// <summary>Sets one fixed mip level, complete local texture color/alpha replacement and an ordinary front-buffer triangle.</summary>
    private static VoodooGraphicsTests.Rig Texture(int format,int level=0,uint layout=0,uint filter=0)
    {
        var r=new VoodooGraphicsTests.Rig();r.Write(0x300,(uint)format<<8|1<<12|1<<18|1<<21|1<<27|filter);
        r.Write(0x304,(uint)(level*4|(level*4)<<6)|layout);r.Write(0x104,1u<<27|5);
        VoodooGraphicsTests.Triangle(r);r.Write(0x110,0x200);return r;
    }
    /// <summary>Completes an actual primitive and reads its first stored color/depth word through the LFB selector.</summary>
    private static ushort Draw(VoodooGraphicsTests.Rig r,bool depth=false)
    {r.Write(0x80,0);r.Finish();r.Write(0x114,depth?0x80u:0);return r.Memory.ReadWord(Lfb);}
    /// <summary>Registers independent exact data, filtering and depth boundaries, including the original SDK's rectangular alpha font.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int mux in new[]{0,2,4,6})test($"Voodoo original iterated-alpha fog mux {mux} independently checks every covered pixel",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r,0x20c0e0);r.Write(0x110,0x200);
            r.Write(0x30,0);r.Write(0x50,8u<<12);r.Write(0x70,3u<<12);r.Write(0x108,(uint)(9|mux));r.Write(0x12c,0xe04018);
            r.Write(0x80,0);r.Finish();
            for(uint y=0;y<32;y++)for(uint x=0;x<32;x++)
            {
                bool inside=x+y+1<32;int alpha=Math.Min(255,(int)x*8+(int)y*3);int[] source=[32,192,224],fog=[224,64,24];int[] result=new int[3];
                for(int c=0;c<3;c++)
                {int original=(mux&4)==0?source[c]:0,target=(mux&2)==0?fog[c]:0;result[c]=Math.Clamp((int)Math.Floor((original*(255-alpha)+target*(alpha+1))/256d),0,255);}
                int expected=result[0]/8<<11|result[1]/4<<5|result[2]/8;
                Check(r.Memory.ReadWord(Lfb+y*2048+x*2)==(inside?expected:0));
            }
        });
        foreach(bool bias in new[]{false,true})test($"Voodoo packed table fog interpolates its 6.2 delta and biased W index {bias}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r,0x0000ff);r.Write(0x110,bias?0x10200u:0x200);
            r.Write(0x3c,(1u<<29)-(1u<<23));r.Write(0x168,0x40002080);r.Write(0x130,1024);r.Write(0x108,1);r.Write(0x12c,0xff0000);
            int factor=(bias?64:36)+1;Check(Draw(r)==((255*factor/256/8)<<11|((255*(256-factor)/256)/8)));
        });
        test("Voodoo fog Z overrides alpha while constant color overrides both and leaves source alpha testing intact",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r,0,z:0x8000);r.Write(0x110,0x200);r.Write(0x108,25);r.Write(0x12c,0xff0000);
            Check(Draw(r)==0x8000);r.Write(0x108,57);Check(Draw(r)==0xf800);
            r.Write(0x108,61);Check(Draw(r)==0xf800);r.Write(0x108,0);Check(Draw(r)==0);
            r.Write(0x108,9);r.Write(0x10c,1u|2u<<1|255u<<24);Check(Draw(r)==0xf800&&r.Memory.ReadDword(Base+0x158)==0);
        });
        foreach(int format in new[]{5,14})test($"Voodoo original paletted format {format} independently reads every entry through all eight aliased NCC ports",()=>
        {
            var r=Texture(format);
            for(uint index=0;index<256;index++)
            {
                uint red=(index*73+17)&255,green=(index*29+91)&255,blue=(index*101+3)&255;
                uint port=0x334+(index&7)*4,color=red<<16|green<<8|blue;
                r.Write((int)port,0x123456);r.Write((int)port,0x80000000|index/2<<24|color);
                Check(r.Memory.ReadDword(Base+port)==0x123456); // Palette write inhibits the NCC coefficient write.
                r.Memory.WriteDword(Base+0x800000,index|(format==14?0x4000u:0));
                Check(Draw(r)==(red/8<<11|green/4<<5|blue/8));
            }
            r.Write(0x10c,1u|2u<<1|(format==14?64u:255u)<<24);_=Draw(r);
            Check(r.Memory.ReadDword(Base+0x158)==0); // Original AP88 high byte, or opaque P8, passes alpha equality.
        });
        test("Voodoo palette chip-select and table-zero/MSB controls preserve actual texel colors",()=>
        {
            var r=Texture(5);r.Memory.WriteDword(Base+0x800000,7);r.Write(0x338,0x8300ff00);Check(Draw(r)==0x07e0);
            r.Memory.WriteDword(Base+0x400+0x338,0x83ff0000);Check(Draw(r)==0x07e0);
            r.Write(0x368,0x83ff0000);Check(Draw(r)==0x07e0);
            r.Write(0x338,0x03ff0000);Check(Draw(r)==0x07e0);
            r.Memory.WriteDword(Base+0x800+0x338,0x83ff0000);Check(Draw(r)==0xf800);
            var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);var other=Texture(5);other.Card.LoadState(saved);
            Check(Draw(other)==0xf800);
        });
        test("Voodoo queued palette upload is ordered after earlier drawing and survives complete component state",()=>
        {
            var r=Texture(5);r.Memory.WriteDword(Base+0x800000,7);r.Write(0x338,0x83ff0000);r.Write(0x80,0);
            r.Write(0x338,0x830000ff);r.Write(0x80,0);var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            Check(saved.Fields["_commands"].GetArrayLength()==3&&saved.Fields["_commands"][1].GetArrayLength()==2);
            Check(saved.Fields["_texturePalette"][7].GetUInt32()==0xff0000);
            var other=Texture(5);other.Card.LoadState(saved);
            Check(JsonSerializer.Serialize(other.Card.SaveState(),StateValues.Json)==JsonSerializer.Serialize(saved,StateValues.Json));
            r.Finish();Check(r.Memory.ReadWord(Lfb)==0xf800);r.Finish();Check(r.Memory.ReadWord(Lfb)==0xf800);
            r.Finish();Check(r.Memory.ReadWord(Lfb)==0x001f);
        });
        test("Voodoo full FIFO palette preflight is pure and reset retains powered palette SRAM",()=>
        {
            var r=Texture(5);r.Write(0x338,0x83ff0000);r.Memory.WriteDword(Base+0x800000,7);r.Write(0x210,0x10);
            for(int i=0;i<63;i++)r.Write(0x124,0);
            string before=JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json);
            Check(r.Memory.TransactionReadyAt(Base+0x338,true)==r.Card.CompletionNanoseconds);
            bool rejected=false;try{r.Write(0x338,0x830000ff);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&before==JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json));
            r.Card.ApplyPiixHardReset();Check(r.Card.SaveState().Fields["_texturePalette"][7].GetUInt32()==0xff0000);
        });
        test("Voodoo-related native m64 span preflight covers movable byte peers and rejects split, RAM, register and wrapped spans",()=>
        {
            var bus=new MemoryBus(2<<20);bus.MapDynamic(new ByteSpanPeer(Base));
            Check(bus.IsContiguousByteDeviceSpan(Base,8)&&!bus.IsContiguousByteDeviceSpan(Base+4,8)&&!bus.IsContiguousByteDeviceSpan(0x1000,8));
            Check(!bus.IsContiguousByteDeviceSpan(uint.MaxValue-3,8)&&!bus.IsContiguousByteDeviceSpan(Base,9));
            bus.Map(Base+4,4,new OpenBusMemory(),"Own split byte span");Check(!bus.IsContiguousByteDeviceSpan(Base,8));
            var r=new VoodooGraphicsTests.Rig();Check(!r.Memory.IsContiguousByteDeviceSpan(Base+0x34,8));
        });
        test("Voodoo revision-two power-on VGA strap survives writes and selects the original-driver mux polarity",()=>
        {
            var r=new VoodooGraphicsTests.Rig();Check(r.Card.ReadConfiguration(8,1)==2&&(r.Memory.ReadDword(Base+0x21c)&0x1f00)==0x1000);
            r.Write(0x21c,0xffffffff);Check((r.Memory.ReadDword(Base+0x21c)&0x1f00)==0x1000);
            r.Write(0x210,0x11);Check(!r.Card.OutputEnabled);r.Write(0x210,0x10);Check(r.Card.OutputEnabled);
            r.Card.ApplyPiixHardReset();Check(!r.Card.OutputEnabled);r.Config(0x10,Base);r.Config(4,2);
            Check((r.Memory.ReadDword(Base+0x21c)&0x1f00)==0x1000);
        });
        test("Voodoo bus admission remains pure and respects read, FIFO, decode and fixed-map priority",()=>
        {
            var r=new VoodooGraphicsTests.Rig();r.Write(0x210,0x10);r.Write(0x124,0);
            string before=JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json);
            for(int i=0;i<100;i++)Check(r.Memory.TransactionReadyAt(Lfb,false)==r.Card.CompletionNanoseconds&&r.Memory.TransactionReadyAt(Base,false)==null);
            Check(before==JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json));
            for(int i=1;i<63;i++)r.Write(0x124,0);
            Check(r.Memory.TransactionReadyAt(Base+0x124,true)==r.Card.CompletionNanoseconds&&r.Memory.TransactionReadyAt(Base+0x148,true)==null);
            Check(r.Memory.TransactionReadyAt(Base+0x800124,true)==r.Card.CompletionNanoseconds&&r.Memory.TransactionReadyAt(Base+0x800+0x124,true)==null);
            r.Write(0x210,0);Check(r.Memory.TransactionReadyAt(Base+0x124,true)==null);r.Write(0x210,0x10);
            r.Memory.Map(Lfb,4096,new OpenBusMemory(),"Own fixed bus-priority control");Check(r.Memory.TransactionReadyAt(Lfb,false)==null);
            r.Config(4,0);Check(r.Memory.TransactionReadyAt(Base+0x124,true)==null);
        });
        foreach(bool noScanout in new[]{false,true})foreach(bool small in new[]{false,true})test($"Voodoo original 256-color dither readback table and every pixel matrix {small} unconfigured video {noScanout}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r);r.Write(0x104,2);r.Write(0x110,small?0xb00u:0x300u);
            if(noScanout)r.Write(0x20c,0);
            var redSums=new HashSet<int>();var greenSums=new HashSet<int>();byte[] pixels=new byte[256*16*2];int at=0;
            for(uint color=0;color<256;color++)
            {
                r.Write(0x148,color*0x010101);r.Write(0x80,0);r.Finish();int red=0,green=0,blue=0;
                for(uint y=0;y<4;y++)for(uint x=0;x<4;x++)
                {
                    ushort pixel=r.Memory.ReadWord(Lfb+y*2048+x*2);pixels[at++]=(byte)pixel;pixels[at++]=(byte)(pixel>>8);
                    red+=(pixel>>11)*8;green+=(pixel>>5&63)*4;blue+=(pixel&31)*8;
                }
                bool uniqueRed=redSums.Add(red),uniqueGreen=greenSums.Add(green);
                Check(red==blue&&(small||uniqueRed&&uniqueGreen));
            }
            Check(redSums.Count==(small?80:256)&&greenSums.Count==(small?112:256));
            string expected=small?"D0A4633D04BAC7453576D9CA4F0E13F5E64296BB36070E386DF2DDA43016422E":"716456BA0F096B11F4103565EA0F711B2A0DB38DF2D95329B2BD0EA25CCD0F8E";
            Check(Convert.ToHexString(SHA256.HashData(pixels))==expected);
        });
        foreach(bool fill in new[]{false,true})test($"Voodoo raster storage extends beyond a smaller active scanout fill {fill}",()=>
        {
            var r=new VoodooGraphicsTests.Rig();r.Write(0x20c,16u<<16|15);VoodooGraphicsTests.Triangle(r);r.Write(0x110,0x200);
            r.Write(0x118,32);r.Write(0x11c,32);r.Write(0x148,0xff0000);r.Write(fill?0x124:0x80,0);r.Finish();
            for(uint y=0;y<35;y++)for(uint x=0;x<35;x++)Check(r.Memory.ReadWord(Lfb+y*2048+x*2)==(fill?x<32&&y<32?0xf800:0:x+y+1<32?0xf800:0));
            Check(r.Card.Capture(r.Clock.Now,new()).Width==16&&r.Card.Capture(r.Clock.Now,new()).Height==16);
        });
        test("Voodoo actual TREX configuration output is chip-selected and leaves texel storage intact",()=>
        {
            var r=Texture(13);r.Memory.WriteDword(Base+0x800000,0x7f7f7f7f);Check(Draw(r)==0x7bef);
            r.Memory.WriteDword(Base+0x1000+0x320,0x40000);Check(Draw(r)==0x7bef); // Absent downstream TMU.
            r.Memory.WriteDword(Base+0x400+0x320,0x40000);Check(Draw(r)==0x7bef); // FBI-only select.
            r.Memory.WriteDword(Base+0x800+0x320,0x40000);r.Write(0x110,0x300);_=Draw(r);
            int red=0,green=0,blue=0;
            for(uint y=0;y<4;y++)for(uint x=0;x<4;x++)
            {ushort pixel=r.Memory.ReadWord(Lfb+y*2048+x*2);red+=pixel>>11;green+=pixel>>5&63;blue+=(pixel&31)*8;}
            Check(red==0&&green==0&&blue==16); // Independently un-dithers to one revision-one TMU, no downstream presence bits.
            r.Memory.WriteDword(Base+0x800+0x320,0);r.Write(0x110,0x200);Check(Draw(r)==0x7bef);
        });
        test("Voodoo original rendered memory-sizing sentinels wrap only at the installed two MiB",()=>
        {
            var r=Texture(10);r.Write(0x54,1u<<18);
            r.Write(0x30c,0x200000/8);r.Memory.WriteDword(Base+0x800000,0x92f56eb0);
            r.Write(0x30c,0x100000/8);r.Memory.WriteDword(Base+0x800000,0xf2a916b5);
            r.Write(0x30c,0);r.Memory.WriteDword(Base+0x800000,0xbadbeef1);
            foreach(uint address in new[]{0x200000u,0x100000u,0u})
            {r.Write(0x30c,address/8);_=Draw(r);Check(r.Memory.ReadDword(Lfb)==(address==0x100000?0xf2a916b5u:0xbadbeef1u));}
        });
        foreach(var c in new (int Format,uint Data,int Pixel)[]{(0,0xe0u,0xf800),(2,0x20u,0x2104),(3,0x11u,0x1082),(4,0x83u,0x3186),
            (8,0x7fe0u,0xf800),(10,0xf123u,0xf123),(11,0xfc00u,0xf800),(12,0xaf00u,0xf800),(13,0x9233u,0x3186)})
        {
            var c0=c;test($"Voodoo original uncompressed texel format {c0.Format}",()=>
            {var r=Texture(c0.Format);r.Memory.WriteDword(Base+0x800000,c0.Data);Check(Draw(r)==c0.Pixel);});
        }
        foreach(bool sequential in new[]{false,true})test($"Voodoo actual rectangular alpha font upload sequential {sequential}",()=>
        {
            var r=Texture(2,1,1u<<20|1u<<21,sequential?1u<<31:0);
            r.Memory.WriteDword(Base+0x800000+(1<<17)+(63<<9)+(sequential?4u:8u),0x7f7f7f7f);
            r.Write(0x34,8u<<18);r.Write(0x38,126u<<18);Check(Draw(r)==0x7bef);
            r.Write(0x10c,1u|6u<<1|128u<<24);Check(Draw(r)==0x7bef&&r.Memory.ReadDword(Base+0x158)==496);
            r.Write(0x10c,1u|3u<<1|128u<<24);Check(Draw(r)==0x7bef&&r.Memory.ReadDword(Base+0x15c)==992);
        });
        foreach(bool clamp in new[]{false,true})test($"Voodoo actual bilinear RGB565 corner and edge clamp {clamp}",()=>
        {
            var r=Texture(10,7,0,6u|(clamp?192u:0));
            r.Memory.WriteDword(Base+0x800000+(7<<17),0x07e0f800);r.Memory.WriteDword(Base+0x800000+(7<<17)+512,0xffff001f);
            r.Write(0x34,128u<<18);r.Write(0x38,128u<<18);Check(Draw(r)==0x7bef);
            r.Write(0x34,0);r.Write(0x38,0);Check(Draw(r)==(clamp?0xf800:0x7bef));
        });
        foreach(uint swizzle in new[]{0u,1u<<25,1u<<26,3u<<25})test($"Voodoo original texture upload endian controls {swizzle:X}",()=>
        {
            var r=Texture(10,0,swizzle);r.Memory.WriteDword(Base+0x800000,0x001ff800);
            ushort expected=swizzle switch{0=>0xf800,1u<<25=>0x1f00,1u<<26=>0x001f,_=>0x00f8};Check(Draw(r)==expected);
        });
        foreach(var c in new (uint W,int Depth)[]{(0,65535),(1u<<29,4096),(1u<<28,8192),(3u<<28,2048),(1u<<30,0),(uint.MaxValue,0),(1u<<15,61440)})
        {
            var c0=c;test($"Voodoo reciprocal W normalization {c0.W:X8}",()=>
            {var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r,z:1234);r.Write(0x3c,c0.W);r.Write(0x110,0x608);Check(Draw(r,true)==c0.Depth);});
        }
        test("Voodoo W depth signed bias retains the original clamp and independent Z iterator",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r,z:1234);r.Write(0x3c,1u<<29);r.Write(0x130,unchecked((ushort)-4097));r.Write(0x110,0x10608);
            Check(Draw(r,true)==0);r.Write(0x110,0x600);Check(Draw(r,true)==1234);
        });
        test("Voodoo deferred nonfinite setup preserves raw bytes and allows an unused S channel",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r);r.Write(0xb4,0xffc00000);
            Check(!r.Card.Busy&&r.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==1u<<11);
            Check(Draw(r)==0xf800);var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            var restored=new VoodooGraphicsTests.Rig();restored.Card.LoadState(saved);
            Check(restored.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==1u<<11);
        });
        test("Voodoo signed nonfinite S overflow renders with original raw queued payload retained",()=>
        {
            var r=Texture(10,level:8);r.Memory.WriteDword(Base+0x900000,0xf800f800);r.Write(0xb4,0x7f800000);r.Write(0x80,0);
            var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            Check(saved.Fields["_registers"][13].GetUInt32()==0x7f800000&&saved.Fields["_commands"][0][13].GetUInt32()==0x7f800000);
            r.Finish();Check(r.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==1u<<11);
            r.Write(0xb4,0x3f800000);Check(r.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==0);
        });
        test("Voodoo nonfinite alpha slope reaches the alpha test without altering its queued IEEE word",()=>
        {
            var r=new VoodooGraphicsTests.Rig();VoodooGraphicsTests.Triangle(r);r.Write(0x30,0);
            r.Write(0xd0,0x7f800000);r.Write(0x10c,1u|4u<<1|128u<<24);r.Write(0x80,0);
            var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            Check(saved.Fields["_registers"][0x50/4].GetUInt32()==0x7f800000&&
                saved.Fields["_commands"][0][0x50/4].GetUInt32()==0x7f800000);
            r.Finish();Check(r.Memory.ReadWord(Lfb)==0&&r.Memory.ReadWord(Lfb+2)==0xf800);
        });
        test("Voodoo integer setup overwrites a marked float latch without retaining its undefined conversion",()=>
        {
            var r=new VoodooGraphicsTests.Rig();r.Write(0xb4,0xffc00000);r.Write(0x34,0x12345678);
            Check(r.Card.SaveState().Fields["_nonfiniteParameters"].GetUInt32()==0&&r.Memory.ReadDword(Base+0x34)==0x12345678);
        });
        test("Voodoo configured FIFO retains a complete 2048-DWORD original-font-sized upload behind active drawing",()=>
        {
            var r=Texture(10);r.Write(0x200,450u<<8|511u<<18);r.Write(0x210,0x2000);r.Write(0x124,0);
            for(uint i=0;i<2048;i++)r.Memory.WriteDword(Base+0x800000,i|i<<16);
            Check((r.Memory.ReadDword(Base)>>12&65535)==63488-259-2048*8&&(r.Memory.ReadDword(Base)&63)==63);
            var saved=r.Card.SaveState();VoodooGraphics.ValidateSavedState(saved);
            Check(saved.Fields["_commands"].GetArrayLength()==2049&&saved.Fields["_commands"][1].GetArrayLength()==8);
            r.Clock.AdvanceTo(10_000_000);Check(!r.Card.Busy&&(r.Memory.ReadDword(Base)>>12&65535)==63488);
            Check(Draw(r)==2047); // The final upload, not a substituted texture or reordered primitive, supplies the actual texel.
        });
        test("Voodoo configured FIFO exhaustion leaves every queued byte and deadline unchanged",()=>
        {
            var r=Texture(10);r.Write(0x200,450u<<8|450u<<18);r.Write(0x210,0x2000);r.Write(0x124,0);
            for(uint i=0;i<95;i++)r.Memory.WriteDword(Base+0x800000,i);
            Check((r.Memory.ReadDword(Base)>>12&65535)==5);
            string before=JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json);bool rejected=false;
            try{r.Memory.WriteDword(Base+0x800000,0xdeadbeef);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&before==JsonSerializer.Serialize(r.Card.SaveState(),StateValues.Json));
            r.Card.ApplyPiixHardReset();Check(!r.Card.Busy);r.Clock.AdvanceTo(20_000_000);Check(!r.Card.Busy);
        });
    }
}
