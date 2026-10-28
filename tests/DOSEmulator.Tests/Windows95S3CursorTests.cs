// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Guest-register/VRAM hardware-cursor composition and actual color/origin futures; no host cursor or native-input claim.</summary>
internal static class Windows95S3CursorTests
{
    /// <summary>Own conflict-free framebuffer and last populated one-KiB cursor page.</summary>
    private const uint High=0xe0000000,Storage=0x1ffc00;
    /// <summary>Rejects incorrect cursor bytes, registers, complete source pixels or future raw state.</summary>
    private static void Check(bool value,string why="Original Trio64 cursor invariant"){if(!value)throw new InvalidOperationException(why);}
    /// <summary>Independently stated source format and fixed foreground/background direct-color expectations.</summary>
    private readonly record struct Format(int Bits,int Bytes,uint Foreground,uint Background,uint FgArgb,uint BgArgb);
    /// <summary>Concrete indexed, RGB555, RGB565 and original24-in32 source modes.</summary>
    private static readonly Format[] Formats=[new(8,1,0xe1,0x13,0,0),new(15,2,0x7c00,0x3e0,0xffff0000,0xff00ff00),new(16,2,0xf800,0x7e0,0xffff0000,0xff00ff00),new(24,4,0xec7042,0x1784cd,0xffec7042,0xff1784cd)];
    /// <summary>Own actual PCI/VGA/VRAM graph, independently constructed again for every restoration.</summary>
    private sealed class Rig
    {
        /// <summary>Fresh physical bus, including real fixed VGA and movable PCI targets.</summary>
        public readonly MemoryBus Memory=new(2*1048576);
        /// <summary>Actual component port bus, with no replacement cursor renderer.</summary>
        public readonly IoPortBus Ports=new();
        /// <summary>Actual original shared display and VRAM implementation.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Actual original PCI/wake/control endpoint.</summary>
        public readonly S3Trio64 Card;
        /// <summary>Independent source format used for the known framebuffer input.</summary>
        public readonly Format Format;
        /// <summary>Performs real wake, original enhanced setup, palette writes and literal guest mask storage.</summary>
        public Rig(Format format)
        {
            Format=format;Video=new(null,false,true);Card=new(Memory,Video);Memory.Map(0xa0000,0x20000,Card,"Own original cursor VGA");Memory.MapDynamic(Card);
            Ports.Map(0x3b0,0x30,Card);Ports.Map(0x46e8,1,Card);Ports.Map(0x102,1,Card);Ports.Map(0x4ae8,2,Card);
            Card.WriteConfiguration(4,3,2);Card.WriteConfiguration(0x10,High,4);Ports.Write(0x46e8,0x10,1);Ports.Write(0x102,1,1);Ports.Write(0x46e8,8,1);Ports.Write(0x3c2,0x67,1);
            Cr(0x38,0x48);Cr(0x39,0xa5);Sr(8,6);Sr(0,3);Sr(1,1);Sr(2,15);Sr(4,14);Sr(0x0a,0x80);Sr(0x15,0x80);Gc(5,0);Gc(6,5);Gc(8,255);
            Cr(0x40,1);Cr(0x31,8);Cr(0x3a,0x10);Cr(0x50,format.Bytes==1?(byte)0:format.Bytes==2?(byte)0x10:(byte)0x30);Cr(0x67,(byte)(format.Bits switch{8=>0,15=>0x30,16=>0x50,_=>0xd0}));
            Cr(1,15);Cr(7,0);Cr(9,0);Cr(0x12,95);Cr(0x13,(byte)(16*format.Bytes));Cr(0x51,0);Cr(12,0);Cr(13,0);Cr(0x69,0);Cr(0x58,0x12);Ports.Write(0x4ae8,1,2);
            Ports.Write(0x3c8,0,1);for(int i=0;i<256;i++){Ports.Write(0x3c9,(uint)((i*3+7)&63),1);Ports.Write(0x3c9,(uint)((i*5+11)&63),1);Ports.Write(0x3c9,(uint)((i*7+13)&63),1);}
            Ports.Read(0x3da,1);Ports.Write(0x3c0,0x20,1);
            for(int y=0;y<96;y++)for(int x=0;x<128;x++){uint value=SourceValue(format,x,y);for(int b=0;b<format.Bytes;b++)Memory.WriteByte(High+(uint)((y*128+x)*format.Bytes+b),(byte)(value>>(8*b)));}
            for(int y=0;y<64;y++)for(int word=0;word<4;word++)
            {uint value=y>=32||word>=2?0x0000ffffu:(y%5) switch{0=>0xaa550ff0u,1=>0u,2=>0xffff0000u,3=>0x0000ffffu,_=>uint.MaxValue};Memory.WriteDword(High+Storage+(uint)(y*16+word*4),value);}
            Cr(0x4c,7);Cr(0x4d,255);Colors(format.Foreground,format.Background);Position(3,4);Cr(0x45,1);
        }
        /// <summary>Writes the active color CRTC through actual indexed ports.</summary>
        public void Cr(byte index,byte value){Ports.Write(0x3d4,index,1);Ports.Write(0x3d5,value,1);}
        /// <summary>Reads the active color CRTC, including real color-stack reset effects.</summary>
        public byte Cr(byte index){Ports.Write(0x3d4,index,1);return(byte)Ports.Read(0x3d5,1);}
        /// <summary>Writes actual sequencer latches.</summary>
        public void Sr(byte index,byte value){Ports.Write(0x3c4,index,1);Ports.Write(0x3c5,value,1);}
        /// <summary>Writes actual VGA graphics-controller latches.</summary>
        public void Gc(byte index,byte value){Ports.Write(0x3ce,index,1);Ports.Write(0x3cf,value,1);}
        /// <summary>Uses two actual CR45 reads and six sequential cursor color writes.</summary>
        public void Colors(uint fg,uint bg){Cr(0x45);for(int i=0;i<3;i++)Cr(0x4a,(byte)(fg>>(8*i)));Cr(0x45);for(int i=0;i<3;i++)Cr(0x4b,(byte)(bg>>(8*i)));}
        /// <summary>Uses the original driver's position/offset order and actual final CR48 registration.</summary>
        public void Position(int x,int y,int ox=0,int oy=0){Cr(0x47,(byte)x);Cr(0x46,(byte)(x>>8));Cr(0x49,(byte)y);Cr(0x4e,(byte)ox);Cr(0x4f,(byte)oy);Cr(0x48,(byte)(y>>8));}
    }
    /// <summary>Specifies actual source storage independently from device scanout and cursor-address helpers.</summary>
    private static uint SourceValue(Format format,int x,int y)
    {
        if(format.Bits==8)return(uint)(((x*3+y*5)^0x59)&255);
        int selected=(x*5+y*3)&3;
        return format.Bits switch{15=>new uint[]{0x7c00,0x3e0,31,0x4210}[selected],16=>new uint[]{0xf800,0x7e0,31,0x8410}[selected],_=>new uint[]{0x123456,0xabcdef,0xff0000,0}[selected]};
    }
    /// <summary>Reconstructs one independently programmed six-bit-DAC entry without a device color cache.</summary>
    private static uint Palette(int index)
    {uint r=(uint)((index*3+7)&63),g=(uint)((index*5+11)&63),b=(uint)((index*7+13)&63);return 0xff000000u|(r*4+r/16)<<16|(g*4+g/16)<<8|(b*4+b/16);}
    /// <summary>Returns a fixed known source color, including the explicitly named functional indexed PA-mask policy.</summary>
    private static uint SourceColor(Format format,int x,int y,int mask=255)
    {
        if(format.Bits==8)return Palette((int)SourceValue(format,x,y)&mask);
        int selected=(x*5+y*3)&3;
        return format.Bits switch{15=>new uint[]{0xffff0000,0xff00ff00,0xff0000ff,0xff848484}[selected],16=>new uint[]{0xffff0000,0xff00ff00,0xff0000ff,0xff848284}[selected],_=>new uint[]{0xff123456,0xffabcdef,0xffff0000,0xff000000}[selected]};
    }
    /// <summary>Decodes literal AA550FF0 test words from a manually specified16-pixel truth sequence, not DUT address arithmetic.</summary>
    private static int Shape(int x,int y)
    {
        if(x>=32||y>=32)return 2;
        ReadOnlySpan<byte> firstRow=[2,3,2,3,0,1,0,1,1,0,1,0,3,2,3,2];
        return(y%5) switch{0=>firstRow[x&15],1=>0,2=>1,3=>2,_=>3};
    }
    /// <summary>Builds every independently expected framebuffer pixel and the primary MS/X11 cursor truth table.</summary>
    private static uint[] Expected(Format format,int x0=3,int y0=4,int ox=0,int oy=0,bool x11=false,bool cursor=true,int mask=255,uint? fgOverride=null)
    {
        var result=new uint[128*96];uint fg=fgOverride??(format.Bits==8?Palette((int)format.Foreground&mask):format.FgArgb),bg=format.Bits==8?Palette((int)format.Background&mask):format.BgArgb;
        for(int y=0;y<96;y++)for(int x=0;x<128;x++)
        {
            uint value=SourceColor(format,x,y,mask);int cx=x-x0+ox,cy=y-y0+oy;
            if(cursor&&x>=x0&&y>=y0&&cx<64&&cy<64)
            {int operation=Shape(cx,cy);if(x11){if(operation==2)value=bg;else if(operation==3)value=fg;}else if(operation==0)value=bg;else if(operation==1)value=fg;else if(operation==3)value=format.Bits==8?Palette((~(int)SourceValue(format,x,y))&mask):value^0xffffffu;}
            result[y*128+x]=value;
        }return result;
    }
    /// <summary>Requires complete source dimensions and every actual host-surface pixel.</summary>
    private static void Pixels(Rig rig,uint[] expected)
    {var frame=rig.Video.Capture(123456);Check(frame.Width==128&&frame.Height==96&&frame.ArgbPixels.AsSpan().SequenceEqual(expected),"Complete cursor/source pixel oracle differs");}
    /// <summary>Returns actual raw color-stack bytes, independent from CRTC read effects.</summary>
    private static byte[] Stack(Rig r,string name)=>r.Video.SaveState().Fields[name].Deserialize<byte[]>(StateValues.Json)!;
    /// <summary>Captures complete physical VRAM to prove that cursor composition itself writes no guest bytes.</summary>
    private static byte[][] Planes(Rig r)=>r.Video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Requires every populated shared plane byte.</summary>
    private static bool Same(byte[][] a,byte[][] b)=>a.Zip(b).All(v=>v.First.AsSpan().SequenceEqual(v.Second));
    /// <summary>Restores actual device state into independently constructed equal resources.</summary>
    private static Rig Copy(Rig source){var result=new Rig(source.Format);result.Video.LoadState(source.Video.SaveState());result.Card.LoadState(source.Card.SaveState());return result;}
    /// <summary>Requires all saved fields after the same real future port and scanout effects.</summary>
    private static void Equal(Rig a,Rig b)=>Check(JsonSerializer.Serialize(a.Video.SaveState(),StateValues.Json)==JsonSerializer.Serialize(b.Video.SaveState(),StateValues.Json),"Full cursor future state differs");
    /// <summary>Registers actual guest-device cursor tests; original Windows/native-input acceptance is separate.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(var format in Formats)
        {
            foreach(bool x11 in new[]{false,true})foreach(var offset in new[]{(X:0,Y:0),(X:7,Y:3),(X:15,Y:11),(X:31,Y:31)})
                test("M4 Win95 S3 cursor full literal MS X11 pixels bits="+format.Bits+" x11="+x11+" offset="+offset,()=>
                {var r=new Rig(format);r.Cr(0x55,x11?(byte)0x10:(byte)0);r.Position(3,4,offset.X,offset.Y);byte[][] before=Planes(r);Pixels(r,Expected(format,3,4,offset.X,offset.Y,x11));Check(Same(before,Planes(r)),"Cursor composition modified guest VRAM");});
            foreach(var position in new[]{(X:0,Y:0),(X:110,Y:80),(X:127,Y:95),(X:2047,Y:2047)})
                test("M4 Win95 S3 cursor physical display clipping bits="+format.Bits+" position="+position,()=>
                {var r=new Rig(format);r.Position(position.X,position.Y);Pixels(r,Expected(format,position.X,position.Y));});
        }
        test("M4 Win95 S3 cursor common color stack write progression and declared read policy",()=>
        {
            var r=new Rig(Formats[3]);r.Cr(0x45);r.Cr(0x4a,0x12);r.Cr(0x4b,0x34);r.Cr(0x4a,0x56);Check(r.Video.TrioCursorColorIndex==0);
            Check(Stack(r,"_trioCursorForeground").AsSpan().SequenceEqual(new byte[]{0x12,0x70,0x56})&&Stack(r,"_trioCursorBackground").AsSpan().SequenceEqual(new byte[]{0xcd,0x34,0x17}));
            r.Cr(0x45);r.Cr(0x4b,0x78);r.Cr(0x4b,0x9a);r.Cr(0x4b,0xbc);Check(r.Video.TrioCursorColorIndex==0&&r.Cr(0x4a)==0x12&&r.Cr(0x4a)==0x12);
            r.Cr(0x4a,0x12);Check(r.Video.TrioCursorColorIndex==1,"An unchanged color byte failed to advance the actual pointer");
        });
        test("M4 Win95 S3 cursor read locks and inactive mono decoder have no hidden stack reset",()=>
        {
            var r=new Rig(Formats[3]);r.Cr(0x45);r.Cr(0x4a,0x12);r.Cr(0x39,0);Check(r.Cr(0x45)==255&&r.Video.TrioCursorColorIndex==1);r.Cr(0x4a,0xff);Check(Stack(r,"_trioCursorForeground")[0]==0x12);
            r.Cr(0x39,0xa5);r.Ports.Write(0x3b4,0x45,1);Check(r.Ports.Read(0x3b5,1)==255&&r.Video.TrioCursorColorIndex==1);r.Cr(0x45);Check(r.Video.TrioCursorColorIndex==0);
        });
        test("M4 Win95 S3 cursor original masks and repeated CR48 register the whole pending pair",()=>
        {
            var r=new Rig(Formats[3]);Pixels(r,Expected(Formats[3]));r.Cr(0x47,9);r.Cr(0x49,7);Check(r.Video.TrioCursorOriginX==3&&r.Video.TrioCursorOriginY==4);Pixels(r,Expected(Formats[3]));
            r.Cr(0x48,0);Check(r.Video.TrioCursorOriginX==9&&r.Video.TrioCursorOriginY==7);Pixels(r,Expected(Formats[3],9,7));
            r.Cr(0x46,255);r.Cr(0x48,255);r.Cr(0x4c,255);r.Cr(0x4e,255);r.Cr(0x4f,255);Check(r.Cr(0x46)==7&&r.Cr(0x48)==7&&r.Cr(0x4c)==15&&r.Cr(0x4e)==63&&r.Cr(0x4f)==63);
        });
        test("M4 Win95 S3 cursor original driver transparent padding matches last VRAM cursor page",()=>
        {var r=new Rig(Formats[0]);r.Position(0,0,63,63);Pixels(r,Expected(Formats[0],0,0,63,63));Check(r.Memory.ReadDword(High+Storage+1020)==0x0000ffff,"Literal last padded cursor word differs");});
        test("M4 Win95 S3 cursor unchanged capture keeps cache and real mask write invalidates it",()=>
        {
            var r=new Rig(Formats[3]);var buffer=new VgaFrameBuffer();r.Video.Capture(0,buffer);long before=buffer.RenderedFrames;r.Video.Capture(0,buffer);Check(buffer.RenderedFrames==before);
            uint[] expected=Expected(Formats[3]);r.Memory.WriteByte(High+Storage+2,0xaa);
            ReadOnlySpan<byte> operations=[3,2,3,2,1,0,1,0];for(int i=0;i<8;i++){int x=3+i,y=4;expected[y*128+x]=operations[i] switch{0=>Formats[3].BgArgb,1=>Formats[3].FgArgb,2=>SourceColor(Formats[3],x,y),_=>SourceColor(Formats[3],x,y)^0xffffffu};}
            Check(r.Video.Capture(0,buffer).ArgbPixels.AsSpan().SequenceEqual(expected)&&buffer.RenderedFrames==before+1,"Cursor shape update reused stale pixels");
        });
        foreach(bool partial in new[]{false,true})
            test("M4 Win95 S3 cursor raw future retains shared stack and pending XY partial="+partial,()=>
            {
                var r=new Rig(Formats[3]);r.Cr(0x45);r.Cr(0x4a,0x17);r.Cr(0x47,15);r.Cr(0x49,7);if(partial)r.Cr(0x4a,0x22);var copy=Copy(r);
                foreach(var side in new[]{r,copy}){if(!partial)side.Cr(0x4a,0x22);side.Cr(0x4a,0x33);side.Cr(0x48,0);}
                Pixels(r,Expected(Formats[3],15,7,fgOverride:0xff332217));Pixels(copy,Expected(Formats[3],15,7,fgOverride:0xff332217));Equal(r,copy);
            });
        foreach(bool hard in new[]{false,true})
            test("M4 Win95 S3 cursor reset domains retain VRAM and separate GE control hard="+hard,()=>
            {
                var r=new Rig(Formats[3]);byte[][] before=Planes(r);if(hard)r.Card.ApplyPiixHardReset();else{r.Card.Write(0x42e8,0x8000,2);r.Card.Write(0x42e8,0x4000,2);}
                Check(Same(before,Planes(r)));if(hard)Check(VgaAdapter.IsTrio64CursorResetState(r.Video.SaveState()));else Pixels(r,Expected(Formats[3]));
            });
        test("M4 Win95 S3 cursor enhanced only preserves ordinary VGA scanout",()=>
        {var r=new Rig(Formats[0]);r.Ports.Write(0x4ae8,0,2);uint[] visible=r.Video.Capture(0).ArgbPixels.ToArray();r.Cr(0x45,0);Check(r.Video.Capture(0).ArgbPixels.AsSpan().SequenceEqual(visible));});
        foreach(string gate in new[]{"sequencer","display","dac"})
            test("M4 Win95 S3 cursor cannot bypass existing output blank gate "+gate,()=>
            {var r=new Rig(Formats[3]);if(gate=="sequencer")r.Sr(1,0x21);else if(gate=="dac")r.Sr(0x18,0x20);else{r.Ports.Read(0x3da,1);r.Ports.Write(0x3c0,0,1);}Check(r.Video.Capture(0).ArgbPixels.All(p=>p==0xff000000));});
        test("M4 Win95 S3 cursor right storage remains explicitly outside contiguous cursor support",()=>
        {var r=new Rig(Formats[0]);r.Cr(0x45,0x11);Pixels(r,Expected(Formats[0],cursor:false));});
        test("M4 Win95 S3 cursor functional indexed PA palette mask contract remains explicit",()=>
        {var r=new Rig(Formats[0]);r.Ports.Write(0x3c6,0x5a,1);Pixels(r,Expected(Formats[0],mask:0x5a));});
        test("M4 Win95 S3 cursor literal original driver coordinate word OUT order",()=>
        {
            var r=new Rig(Formats[3]);foreach(ushort word in new ushort[]{0xa039,0x004e,0x004f,0x0947,0x0046,0x0749,0x0048})r.Ports.Write(0x3d4,word,2);
            Check(r.Video.TrioCursorOriginX==9&&r.Video.TrioCursorOriginY==7);Pixels(r,Expected(Formats[3],9,7));
        });
    }
}
