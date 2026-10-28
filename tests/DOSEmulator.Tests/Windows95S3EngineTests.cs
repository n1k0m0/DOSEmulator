// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent original-Trio64 command/port/MMIO effects on physical VRAM, including incomplete CPU-transfer futures.</summary>
internal static class Windows95S3EngineTests
{
    /// <summary>Conflict-free own high-memory framebuffer target.</summary>
    private const uint High=0xe0000000;
    /// <summary>Rejects an incorrect physical pixel, command state, gate, register or continuation.</summary>
    private static void Check(bool value,string why="Original Trio64 engine invariant"){if(!value)throw new InvalidOperationException(why);}
    /// <summary>Fresh own device graph, with no original driver substituted for the tested command implementation.</summary>
    private sealed class Rig
    {
        /// <summary>Actual physical memory and VGA/linear target mappings.</summary>
        public readonly MemoryBus Memory=new(2*1048576);
        /// <summary>Actual registered original port endpoint.</summary>
        public readonly IoPortBus Ports=new();
        /// <summary>Actual shared plane/latch/scanout component.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Actual PCI control, normal VGA and graphics-engine target.</summary>
        public readonly S3Trio64 Card;
        /// <summary>Programmed command pixel storage, used only by the independent source-address oracle.</summary>
        public readonly int Bytes;
        /// <summary>Caller-owned remembered MULT_MISC programming for safe32-bit color-register word sequencing.</summary>
        private ushort _misc;
        /// <summary>Registers resources, performs real wake/unlock, and selects a640-pixel engine surface.</summary>
        public Rig(int bytes=1)
        {
            Bytes=bytes;Video=new(null,false,true);Card=new(Memory,Video);Memory.Map(0xa0000,0x20000,Card,"Own original engine MMIO");Memory.MapDynamic(Card);
            Ports.Map(0x3b0,0x30,Card);Ports.Map(0x46e8,1,Card);Ports.Map(0x102,1,Card);Ports.Map(0x4ae8,2,Card);
            foreach(var window in S3Trio64.EnginePortWindows)Ports.Map(window.Start,window.Length,Card);
            Card.WriteConfiguration(4,3,2);Card.WriteConfiguration(0x10,High,4);Ports.Write(0x46e8,0x10,1);Ports.Write(0x102,1,1);Ports.Write(0x46e8,8,1);Ports.Write(0x3c2,0x67,1);
            Cr(0x38,0x48);Cr(0x39,0xa5);Sr(8,6);Sr(0,3);Sr(1,1);Sr(2,15);Sr(4,14);Sr(0x0a,0x80);Sr(0x15,0x80);Gc(5,0);Gc(6,5);Gc(8,255);
            Cr(0x40,1);Cr(0x31,8);Cr(0x3a,0x10);Cr(0x50,(byte)(0x40|(bytes==2?0x10:bytes==4?0x30:0)));Cr(0x58,0x12);Cr(0x67,bytes==2?(byte)0x50:bytes==4?(byte)0xd0:(byte)0);
            Cr(1,79);Cr(7,0);Cr(9,0);Cr(0x12,31);Cr(0x13,(byte)(80*bytes));Cr(12,0);Cr(13,0);Cr(0x69,0);Ports.Write(0x4ae8,1,2);
            W(0x42e8,0x4000);Color(0xaae8,uint.MaxValue);Color(0xaee8,uint.MaxValue);Mf(1,0);Mf(2,0);Mf(3,4095);Mf(4,4095);Mf(10,0);W(0xbae8,0x27);W(0xb6e8,7);
            Ports.Read(0x3da,1);Ports.Write(0x3c0,0x20,1);
        }
        /// <summary>Writes actual indexed CRTC ports.</summary>
        public void Cr(byte i,byte v){Ports.Write(0x3d4,i,1);Ports.Write(0x3d5,v,1);}
        /// <summary>Writes actual unlocked sequencer ports.</summary>
        public void Sr(byte i,byte v){Ports.Write(0x3c4,i,1);Ports.Write(0x3c5,v,1);}
        /// <summary>Writes actual ordinary graphics-controller ports.</summary>
        public void Gc(byte i,byte v){Ports.Write(0x3ce,i,1);Ports.Write(0x3cf,v,1);}
        /// <summary>Issues one real word-wide register transaction.</summary>
        public void W(ushort p,ushort v)=>Ports.Write(p,v,2);
        /// <summary>Reads one real word-wide status or register transaction.</summary>
        public ushort R(ushort p)=>(ushort)Ports.Read(p,2);
        /// <summary>Programs the indexed original multifunction port.</summary>
        public void Mf(int index,int value){if(index==14)_misc=(ushort)(value&0x3ff);W(0xbee8,(ushort)((index<<12)|(value&4095)));}
        /// <summary>Writes original color registers using actual RSF word sequencing in32-bit command mode.</summary>
        public void Color(ushort p,uint value)
        {
            if(Bytes==4){Mf(14,_misc&~0x10);W(p,(ushort)value);W(p,(ushort)(value>>16));}
            else W(p,(ushort)value);
        }
        /// <summary>Programs the actual current/source and destination coordinate registers.</summary>
        public void Position(int x,int y,int dx=0,int dy=0){W(0x86e8,(ushort)x);W(0x82e8,(ushort)y);W(0x8ee8,(ushort)dx);W(0x8ae8,(ushort)dy);}
        /// <summary>Programs rectangle extents using the primary minus-one convention.</summary>
        public void Size(int width,int height){W(0x96e8,(ushort)(width-1));Mf(0,height-1);}
        /// <summary>Writes an independent logical source pixel through the actual high physical target.</summary>
        public void Pixel(int x,int y,uint value){for(int i=0;i<Bytes;i++)Memory.WriteByte(High+(uint)((y*640+x)*Bytes+i),(byte)(value>>(8*i)));}
        /// <summary>Reads an actual physical pixel through the normal VGA/LFB storage path.</summary>
        public uint Pixel(int x,int y){uint value=0;for(int i=0;i<Bytes;i++)value|=(uint)Memory.ReadByte(High+(uint)((y*640+x)*Bytes+i))<<(8*i);return value;}
    }
    /// <summary>Independently specifies the16 primary raster operations as four-entry boolean truth tables.</summary>
    private static uint Mix(uint source,uint destination,int operation)
    {
        ReadOnlySpan<byte> truth=[5,0,15,10,3,6,9,12,7,11,13,14,8,4,2,1];uint result=0;
        for(int bit=0;bit<32;bit++){int index=(int)(((source>>bit)&1)*2+((destination>>bit)&1));if((truth[operation]&(1<<index))!=0)result|=1u<<bit;}return result;
    }
    /// <summary>Returns the fixed source pixel mask without consulting any device mode decoder.</summary>
    private static uint Mask(int bytes)=>bytes==4?uint.MaxValue:bytes==2?65535u:255u;
    /// <summary>Seeds a complete32-by32 independent pixel grid, including all later destination guards.</summary>
    private static uint[] Seed(Rig rig)
    {
        var values=new uint[1024];for(int y=0;y<32;y++)for(int x=0;x<32;x++){uint value=(0x86420096u+(uint)(x*0x35+y*0x17))&Mask(rig.Bytes);values[y*32+x]=value;rig.Pixel(x,y,value);}return values;
    }
    /// <summary>Requires every seeded source/guard pixel, not just a checksum or a synthetic success flag.</summary>
    private static void Grid(Rig rig,uint[] expected)
    {for(int y=0;y<32;y++)for(int x=0;x<32;x++)Check(rig.Pixel(x,y)==expected[y*32+x],"Actual VRAM pixel "+x+"/"+y);}
    /// <summary>Captures the whole physical two-MiB storage without modifying read latches.</summary>
    private static byte[][] Planes(Rig rig)=>rig.Video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Requires exact plane bytes independently of the visible display window.</summary>
    private static bool Same(byte[][] a,byte[][] b)=>a.Zip(b).All(x=>x.First.AsSpan().SequenceEqual(x.Second));
    /// <summary>Clones only actual device raw state into an independently constructed equal resource graph.</summary>
    private static Rig Copy(Rig original)
    {var copy=new Rig(original.Bytes);copy.Video.LoadState(original.Video.SaveState());copy.Card.LoadState(original.Card.SaveState());return copy;}
    /// <summary>Requires every serialized video field and all populated VRAM bytes after actual future accesses.</summary>
    private static void Equal(Rig a,Rig b)=>Check(JsonSerializer.Serialize(a.Video.SaveState(),StateValues.Json)==JsonSerializer.Serialize(b.Video.SaveState(),StateValues.Json),"Complete future video state differs");
    /// <summary>Registers concrete original drawing, CPU/mono transfers, MMIO, gates, resets and semantic continuation checks.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int bytes in new[]{1,2,4})
        {
            for(int operation=0;operation<16;operation++)
            {int selected=operation;test("M4 Win95 S3 engine all logical mixes bytes="+bytes+" rop="+selected,()=>
            {
                var r=new Rig(bytes);var values=Seed(r);uint color=0xa5c3e179&Mask(bytes);r.Color(0xa6e8,color);r.W(0xbae8,(ushort)(0x20|selected));r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);
                for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=Mix(color,values[y*32+x],selected)&Mask(bytes);
                Grid(r,values);Check(r.R(0x9ae8)==0x400&&!r.Video.TrioEngineWaiting&&r.Video.TrioEngineUnsupportedCommand is null);
            });}
            test("M4 Win95 S3 engine per-bit write mask preserves physical bits bytes="+bytes,()=>
            {
                var r=new Rig(bytes);var values=Seed(r);uint mask=0x0f0f0f0f&Mask(bytes),color=0xa5c3e179&Mask(bytes);r.Color(0xaae8,mask);r.Color(0xa6e8,color);r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);
                for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=(values[y*32+x]&~mask)|(color&mask);Grid(r,values);
            });
            foreach(bool outside in new[]{false,true})
                test("M4 Win95 S3 engine inclusive and external scissors bytes="+bytes+" outside="+outside,()=>
                {
                    var r=new Rig(bytes);var values=Seed(r);uint color=0xe1739ba5&Mask(bytes);r.Color(0xa6e8,color);r.Mf(1,3);r.Mf(2,4);r.Mf(3,5);r.Mf(4,8);r.Mf(14,outside?0x20:0);r.Position(2,2);r.Size(9,6);r.W(0x9ae8,0x40b1);
                    for(int y=2;y<8;y++)for(int x=2;x<11;x++){bool inside=x>=4&&x<=8&&y>=3&&y<=5;if(inside!=outside)values[y*32+x]=color;}Grid(r,values);
                });
            foreach(bool equal in new[]{false,true})
                test("M4 Win95 S3 engine source color compare before write mask bytes="+bytes+" equal="+equal,()=>
                {
                    var r=new Rig(bytes);var values=Seed(r);uint color=0xe1739ba5&Mask(bytes);r.Color(0xa6e8,color);r.Color(0xb2e8,color);r.Mf(14,equal?0x180:0x100);r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);
                    if(equal)for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=color;Grid(r,values);
                });
            foreach(var direction in new[]{(Dx:1,Dy:1),(Dx:-1,Dy:1),(Dx:1,Dy:-1),(Dx:-1,Dy:-1)})
                test("M4 Win95 S3 engine actual overlapping BitBLT bytes="+bytes+" x="+direction.Dx+" y="+direction.Dy,()=>
                {
                    var r=new Rig(bytes);var values=Seed(r);uint[] old=values.ToArray();int sx=direction.Dx==1?5:2,sy=direction.Dy==1?5:2,tx=direction.Dx==1?2:3,ty=direction.Dy==1?2:3;
                    int ex=direction.Dx==1?0:5,ey=direction.Dy==1?0:3;r.Position(sx+ex,sy+ey,tx+ex,ty+ey);r.Size(6,4);r.W(0xbae8,0x67);r.W(0x9ae8,(ushort)(0xc053|(direction.Dx==1?0x20:0)|(direction.Dy==1?0x80:0)));
                    for(int y=0;y<4;y++)for(int x=0;x<6;x++)values[(ty+y)*32+tx+x]=old[(sy+y)*32+sx+x];Grid(r,values);
                });
            test("M4 Win95 S3 engine read mask changes bitmap source not source VRAM bytes="+bytes,()=>
            {
                var r=new Rig(bytes);var values=Seed(r);uint[] old=values.ToArray();uint mask=0x0f0f0f0f&Mask(bytes);r.Color(0xaee8,mask);r.Position(2,2,12,12);r.Size(6,4);r.W(0xbae8,0x67);r.W(0x9ae8,0xc0f3);
                for(int y=0;y<4;y++)for(int x=0;x<6;x++)values[(12+y)*32+12+x]=old[(2+y)*32+2+x]&mask;Grid(r,values);
            });
            test("M4 Win95 S3 engine globally aligned eight by eight PatBLT bytes="+bytes,()=>
            {
                var r=new Rig(bytes);var values=Seed(r);uint[] old=values.ToArray();r.Position(0,16,3,2);r.Size(13,9);r.W(0xbae8,0x67);r.W(0x9ae8,0xe0b1);
                for(int y=2;y<11;y++)for(int x=3;x<16;x++)values[y*32+x]=old[(16+(y&7))*32+(x&7)];Grid(r,values);
            });
            foreach(int group in new[]{1,2,4})
                test("M4 Win95 S3 engine real mono CPU words row padding bytes="+bytes+" group="+group,()=>
                {
                    var r=new Rig(bytes);var values=Seed(r);uint fg=0x00e1739b&Mask(bytes),bg=0x0013975a&Mask(bytes);r.Color(0xa6e8,fg);r.Color(0xa2e8,bg);r.Mf(10,0x80);r.Position(3,2);r.Size(17,3);
                    ushort cmd=(ushort)(0x51b3|(group==2?0x200:group==4?0x400:0));r.W(0x9ae8,cmd);Check(r.R(0x9ae8)==0x600&&r.Video.TrioEngineWaiting);
                    for(int y=0;y<3;y++)
                    {
                        byte[] data=[(byte)(0xa1^(y*0x31)),(byte)(0x5c^(y*0x31)),(byte)(0xe2^(y*0x31)),(byte)(0x3b^(y*0x31))];
                        for(int x=0;x<17;x++)values[(2+y)*32+3+x]=(data[x/8]&(128>>(x&7)))!=0?fg:bg;
                        int groups=(17+group*8-1)/(group*8);for(int g=0;g<groups;g++){uint value=0;for(int b=0;b<group;b++)value|=(uint)data[g*group+b]<<(8*b);r.Ports.Write(0xe2e8,value,group);}
                    }
                    Check(r.R(0x9ae8)==0x400&&!r.Video.TrioEngineWaiting);Grid(r,values);
                });
        }
        test("M4 Win95 S3 engine primary axial Bresenham pixels and LastPixelOff",()=>
        {
            foreach(bool lastOff in new[]{false,true})
            {
                var r=new Rig();var values=Seed(r);r.Color(0xa6e8,0x5a);r.Position(2,3);r.W(0x8ae8,6);r.W(0x8ee8,0x3ffa);r.W(0x92e8,0);r.W(0x96e8,6);r.W(0x9ae8,(ushort)(0x20b1|(lastOff?4:0)));
                var points=new(int X,int Y)[]{(2,3),(3,4),(4,4),(5,5),(6,5),(7,6),(8,6)};foreach(var point in lastOff?points[..^1]:points)values[point.Y*32+point.X]=0x5a;Grid(r,values);
                Check(r.R(0x86e8)==9&&r.R(0x82e8)==7,"Line current position was not advanced through the full request");
            }
        });
        foreach(var direction in new[]{(X:1,Y:0),(X:1,Y:-1),(X:0,Y:-1),(X:-1,Y:-1),(X:-1,Y:0),(X:-1,Y:1),(X:0,Y:1),(X:1,Y:1)}.Select((value,index)=>(value,index)))
            test("M4 Win95 S3 engine primary radial line direction "+direction.index,()=>
            {var r=new Rig();var values=Seed(r);r.Color(0xa6e8,0x5a);r.Position(10,10);r.W(0x96e8,3);r.W(0x9ae8,(ushort)(0x2019|(direction.index<<5)));for(int i=0;i<4;i++)values[(10+i*direction.value.Y)*32+10+i*direction.value.X]=0x5a;Grid(r,values);});
        foreach(string style in new[]{"pio","standard-mmio","packed-mmio"})
            test("M4 Win95 S3 engine actual command endpoint and aliases "+style,()=>
            {
                var r=new Rig();var values=Seed(r);r.Color(0xa6e8,0x5a);r.Cr(0x53,0x10);
                if(style=="pio"){r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);}
                else if(style=="standard-mmio")
                {r.Memory.WriteWord(0xa86e8,3);r.Memory.WriteWord(0xa82e8,2);r.Memory.WriteWord(0xa96e8,6);r.Memory.WriteWord(0xabee8,4);byte[][] before=Planes(r);r.Memory.WriteByte(0xa9ae8,0xb1);Check(Same(before,Planes(r)),"Low command byte committed early");r.Memory.WriteByte(0xa9ae9,0x40);}
                else
                {r.Memory.WriteDword(0xa8100,0x00030002);r.Memory.WriteDword(0xa8148,0x00060004);r.Memory.WriteWord(0xa8118,0x40b1);Check(r.Memory.ReadWord(0xa8118)==65535,"Packed command alias must remain write-only");}
                for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=0x5a;Grid(r,values);Check(r.R(0x9ae8)==0x400);
            });
        test("M4 Win95 S3 engine CPU16 transfer retains partial32-bit pixels across raw restore",()=>
        {
            var r=new Rig(4);var values=Seed(r);r.Position(3,2);r.Size(2,1);r.W(0xbae8,0x47);r.W(0x9ae8,0x53b1);r.W(0xe2e8,0xabcd);
            Check(r.Pixel(3,2)==values[2*32+3]&&r.Video.TrioEngineWaiting,"Half a32-bit pixel was committed");var copy=Copy(r);
            foreach(var side in new[]{r,copy}){side.W(0xe2e8,0x1234);side.W(0xe2e8,0x2468);side.W(0xe2e8,0xace0);}
            values[2*32+3]=0x1234abcd;values[2*32+4]=0xace02468;Grid(r,values);Grid(copy,values);Equal(r,copy);
        });
        test("M4 Win95 S3 engine CPU image retains partialMMIO group and exact padding across restore",()=>
        {
            var r=new Rig();var values=Seed(r);r.Cr(0x53,0x10);r.Position(3,2);r.Size(5,2);r.W(0xbae8,0x47);r.W(0x9ae8,0x53b1);r.W(0xe2e8,0x2211);r.Memory.WriteByte(0xa0000,0x33);var copy=Copy(r);
            foreach(var side in new[]{r,copy}){side.Memory.WriteByte(0xa0001,0x44);foreach(ushort data in new ushort[]{0xee55,0x7766,0x9988,0xddaa})side.W(0xe2e8,data);}
            byte[] pixels=[0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0x99,0xaa];for(int y=0;y<2;y++)for(int x=0;x<5;x++)values[(2+y)*32+3+x]=pixels[y*5+x];Grid(r,values);Grid(copy,values);Equal(r,copy);
        });
        test("M4 Win95 S3 engine incompleteMMIO status read preserves actual word and selector side effect",()=>
        {
            var r=new Rig();r.Cr(0x53,0x10);r.Mf(15,7);Check(r.Memory.ReadByte(0xabee8)==0,"Actual GP_STAT low byte differs");var copy=Copy(r);
            Check(r.Memory.ReadByte(0xabee9)==4&&copy.Memory.ReadByte(0xabee9)==4,"Cached actual status high byte differs");Equal(r,copy);
            Check(r.R(0xbee8)==0x80&&copy.R(0xbee8)==0x80,"Multifunction read index advanced twice for a split word");Equal(r,copy);
        });
        foreach(string gate in new[]{"pci-io","cr40","sleep","mmio-only"})
            test("M4 Win95 S3 engine target gates reject PIO before drawing "+gate,()=>
            {
                var r=new Rig();var values=Seed(r);r.Color(0xa6e8,0x5a);r.Position(3,2);r.Size(7,5);byte[][] before=Planes(r);
                if(gate=="pci-io")r.Card.WriteConfiguration(4,2,2);else if(gate=="cr40")r.Cr(0x40,0);else if(gate=="sleep")r.Ports.Write(0x46e8,0,1);else{r.Cr(0x53,0x10);r.Sr(9,0x80);}
                r.W(0x9ae8,0x40b1);Check(r.R(0x9ae8)==65535&&Same(before,Planes(r)),"Disabled engine port responded or changed physical VRAM");
                // An asleep target also floats LFB reads; wake it only after proving every retained plane byte.
                if(gate=="sleep")r.Ports.Write(0x46e8,8,1);Grid(r,values);
                if(gate=="mmio-only"){r.Memory.WriteWord(0xa9ae8,0x40b1);for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=0x5a;Grid(r,values);}
            });
        foreach(ushort command in new ushort[]{0x6001,0x8001,0xa001,0x6801,0x8801,0x57b1})
            test("M4 Win95 S3 engine unsupported command cannot invent completed idle "+command.ToString("X4"),()=>
            {var r=new Rig();var before=Planes(r);r.W(0x9ae8,command);Check(r.Video.TrioEngineUnsupportedCommand==command&&(r.R(0x9ae8)&0x200)!=0&&Same(before,Planes(r)));r.W(0x42e8,0x8000);r.W(0x42e8,0x4000);Check(r.Video.TrioEngineUnsupportedCommand is null&&r.R(0x9ae8)==0x400);});
        foreach(bool hard in new[]{false,true})
            test("M4 Win95 S3 engine reset aborts CPU transfer while retaining all committed VRAM hard="+hard,()=>
            {
                var r=new Rig();r.Color(0xa6e8,0x5a);r.Color(0xa2e8,0x13);r.Mf(10,0x80);r.Position(3,2);r.Size(17,2);r.W(0x9ae8,0x53b3);r.W(0xe2e8,0xa15c);byte[][] before=Planes(r);
                if(hard)r.Card.ApplyPiixHardReset();else r.W(0x42e8,0x8000);
                Check(!r.Video.TrioEngineWaiting&&Same(before,Planes(r)),"Reset undid committed graphics writes");if(hard)Check(VgaAdapter.IsTrio64EngineResetState(r.Video.SaveState()),"Held PCI reset retained hidden engine state");
            });
        test("M4 Win95 S3 engine command pitch is independent from display offset",()=>
        {var r=new Rig();var values=Seed(r);r.Cr(0x13,123);r.Color(0xa6e8,0x5a);r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=0x5a;Grid(r,values);});
        test("M4 Win95 S3 engine foreground low-byte writes remain live original driver forms",()=>
        {var r=new Rig();var values=Seed(r);r.Color(0xa6e8,0x5a);r.Ports.Write(0xbae8,0x27,1);r.Position(3,2);r.Size(7,5);r.W(0x9ae8,0x40b1);for(int y=2;y<7;y++)for(int x=3;x<10;x++)values[y*32+x]=0x5a;Grid(r,values);});
    }
}
