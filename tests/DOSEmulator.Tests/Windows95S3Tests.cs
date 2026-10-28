// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Original-Trio64 direct-color pixels, real guest port/memory effects and raw-state continuation; not Windows acceptance.</summary>
internal static class Windows95S3Tests
{
    /// <summary>Own conflict-free physical PCI aperture, independent from CPU paging and VGA bank selection.</summary>
    private const uint High = 0xe0000000;
    /// <summary>Rejects a missing physical byte, exact source pixel, gate or continuation effect.</summary>
    private static void Check(bool value, string why = "Original Trio64 direct-color invariant")
    { if (!value) throw new InvalidOperationException(why); }
    /// <summary>One independently encoded RGB source sample and its expected opaque host pixel.</summary>
    private readonly record struct Sample(uint Encoded, uint Argb);
    /// <summary>Uses fixed RGB555/RGB565/BGRx vectors, including asymmetric channels and discarded source bits.</summary>
    private static Sample[] Samples(int bits) => bits switch
    {
        15 => [new(0,0xff000000),new(0x7fff,0xffffffff),new(0x7c00,0xffff0000),new(0x3e0,0xff00ff00),new(31,0xff0000ff),new(0x4210,0xff848484),new(0x8a3,0xff102918),new(0xffff,0xffffffff)],
        16 => [new(0,0xff000000),new(0xffff,0xffffffff),new(0xf800,0xffff0000),new(0x7e0,0xff00ff00),new(31,0xff0000ff),new(0x8410,0xff848284),new(0x1143,0xff102818),new(0xf81f,0xffff00ff)],
        24 => [new(0,0xff000000),new(0xffffff,0xffffffff),new(0xff0000,0xffff0000),new(0xff00,0xff00ff00),new(255,0xff0000ff),new(0x123456,0xff123456),new(0xabcdef,0xffabcdef),new(0x800001,0xff800001)],
        _ => throw new ArgumentOutOfRangeException(nameof(bits))
    };
    /// <summary>Returns the primary RAMDAC format, not an original-driver-detection substitute.</summary>
    private static byte Ramdac(int bits) => bits switch { 15 => 0x30, 16 => 0x50, 24 => 0xd0, _ => throw new ArgumentOutOfRangeException(nameof(bits)) };
    /// <summary>Constructs only original named devices on a fresh own physical memory/port graph.</summary>
    private sealed class Rig
    {
        /// <summary>Own scheduler, containing no replacement scanout or synthetic DMA callbacks.</summary>
        public readonly DeviceScheduler Clock = new();
        /// <summary>Fresh physical memory, with fixed VGA and one movable original PCI target.</summary>
        public readonly MemoryBus Memory = new(2 * 1048576);
        /// <summary>Actual registered port bus used by the protected CPU caller.</summary>
        public readonly IoPortBus Ports = new();
        /// <summary>Original two-MiB shared plane/latch/scanout implementation.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Actual PCI configuration and memory/I/O target.</summary>
        public readonly S3Trio64 Card;
        /// <summary>Records actual bus transfers for guest OUT and physical-store checks.</summary>
        public readonly List<(ushort Port,uint Value,int Width,bool Write)> Effects = [];
        /// <summary>Registers all resources before any guest access.</summary>
        public Rig()
        {
            Video=new(Clock,false,true);Card=new(Memory,Video);
            Memory.Map(0xa0000,0x20000,Card,"Own original Trio64 VGA");Memory.MapDynamic(Card);
            Ports.Map(0x3b0,0x30,Card);Ports.Map(0x46e8,1,Card);Ports.Map(0x102,1,Card);Ports.Map(0x4ae8,2,Card);
            Ports.Accessed+=(p,v,w,write)=>Effects.Add((p,v,w,write));
        }
        /// <summary>Writes actual indexed CRTC ports after their existing unlock rules.</summary>
        public void Cr(byte index,byte value){Ports.Write(0x3d4,index,1);Ports.Write(0x3d5,value,1);}
        /// <summary>Writes actual sequencer ports, preserving the original register masks.</summary>
        public void Sr(byte index,byte value){Ports.Write(0x3c4,index,1);Ports.Write(0x3c5,value,1);}
        /// <summary>Writes one actual VGA graphics-controller register.</summary>
        public void Gc(byte index,byte value){Ports.Write(0x3ce,index,1);Ports.Write(0x3cf,value,1);}
        /// <summary>Performs the actual PCI/POS wake, enables raw CPU writes, and programs an independently specified display layout.</summary>
        public void Configure(int bits,int width=320,int height=11,int start=0,int extraPitch=0)
        {
            Card.WriteConfiguration(4,3,2);Card.WriteConfiguration(0x10,High,4);
            Ports.Write(0x46e8,0x10,1);Ports.Write(0x102,1,1);Ports.Write(0x46e8,8,1);Ports.Write(0x3c2,0x67,1);
            Sr(0,3);Sr(1,1);Sr(2,15);Sr(4,14);Cr(0x38,0x48);Cr(0x39,0xa5);Sr(8,6);
            Sr(0x0a,0x80);Sr(0x15,0x80);Gc(5,0x40);Gc(6,5);Gc(8,255);
            Cr(0x40,1);Cr(0x31,8);Cr(0x3a,0x10);Cr(0x50,bits==24?(byte)0x30:(byte)0x10);Cr(0x67,Ramdac(bits));
            Cr(1,(byte)(width/8-1));Cr(7,0);Cr(9,0);Cr(0x12,(byte)(height-1));Cr(0x5d,0);Cr(0x5e,0);Cr(0x42,0);Cr(0x43,0);
            int pitch=width*(bits==24?4:2)+extraPitch,offset=pitch/8,dwordStart=start/4;
            Check(width%8==0&&pitch%8==0&&start%4==0,"Own layout must have documented byte/word alignment");
            Cr(0x13,(byte)offset);Cr(0x51,(byte)((offset>>4)&0x30));Cr(12,(byte)(dwordStart>>8));Cr(13,(byte)dwordStart);Cr(0x69,(byte)(dwordStart>>16));
            Cr(0x58,0x12);Ports.Write(0x4ae8,1,2);Ports.Read(0x3da,1);Ports.Write(0x3c0,0x20,1);
        }
        /// <summary>Writes only through the actual physical decoder, including unused fourth true-color bytes.</summary>
        public void Pixel(int address,int bits,Sample value,byte padding=0x5a)
        {
            int count=bits==24?4:2;
            for(int i=0;i<count;i++)Memory.WriteByte(High+(uint)address+(uint)i,bits==24&&i==3?padding:(byte)(value.Encoded>>(8*i)));
        }
    }
    /// <summary>Captures raw shared VRAM independently from the renderer's derived pixels and cache.</summary>
    private static byte[][] Planes(VgaAdapter video)=>video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Requires byte-identical storage across all four populated planes.</summary>
    private static bool Same(byte[][] a,byte[][] b)=>a.Zip(b).All(x=>x.First.AsSpan().SequenceEqual(x.Second));
    /// <summary>Requires every source pixel from fixed color vectors, independent start/pitch arithmetic and row padding.</summary>
    private static void Pattern(Rig rig,int bits,int width,int height,int start,int extraPitch)
    {
        int size=bits==24?4:2,pitch=width*size+extraPitch;Sample[] values=Samples(bits);
        for(int y=0;y<height;y++)
        {
            for(int b=0;b<pitch;b++)rig.Memory.WriteByte(High+(uint)(start+y*pitch+b),0xe7);
            for(int x=0;x<width;x++)rig.Pixel(start+y*pitch+x*size,bits,values[(x*5+y*3+(x>>2))%values.Length],(byte)(x+y));
        }
        byte[][] before=Planes(rig.Video);var frame=rig.Video.Capture(12345678);
        Check(frame.Width==width&&frame.Height==height&&frame.ArgbPixels.Length==width*height,"Actual CRTC display dimensions differ");
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            Check(frame.ArgbPixels[y*width+x]==values[(x*5+y*3+(x>>2))%values.Length].Argb,"Direct-color pixel "+bits+" at "+x+"/"+y);
        Check(!rig.Video.Indexed256&&Same(before,Planes(rig.Video)),"Direct scanout changed raw VRAM or reported indexed color");
    }
    /// <summary>Registers direct-color device proofs; real Windows installation and original-driver use remain separate acceptance.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int bits in new[]{15,16,24})
        {
            foreach(var layout in new[]{(Width:320,Height:11,Start:0,Extra:0),(Width:640,Height:7,Start:0x50000,Extra:16),(Width:800,Height:5,Start:0xd0000,Extra:2080)})
                test("M4 Win95 S3 original direct-color complete pixels bits="+bits+" width="+layout.Width,()=>
                {var r=new Rig();r.Configure(bits,layout.Width,layout.Height,layout.Start,layout.Extra);Pattern(r,bits,layout.Width,layout.Height,layout.Start,layout.Extra);});
            test("M4 Win95 S3 direct color bypasses palette and PEL mask bits="+bits,()=>
            {
                var r=new Rig();r.Configure(bits,32,1);var sample=Samples(bits)[6];
                for(int x=0;x<32;x++)r.Pixel(x*(bits==24?4:2),bits,sample,(byte)x);
                PortsPalette(r,0);r.Ports.Write(0x3c6,0,1);var first=r.Video.Capture(0);
                Check(first.ArgbPixels.All(p=>p==sample.Argb),"Direct RGB incorrectly passed through palette or PELmask");
                PortsPalette(r,63);r.Ports.Write(0x3c6,0xa5,1);var second=r.Video.Capture(0);
                Check(first.ArgbPixels.SequenceEqual(second.ArgbPixels),"DAC palette changed direct-color output");
            });
            foreach(string gate in new[]{"mapping","packed","blank","dac-sleep"})
                test("M4 Win95 S3 direct-color existing output gate bits="+bits+" gate="+gate,()=>
                {
                    var r=new Rig();r.Configure(bits,32,1);for(int x=0;x<32;x++)r.Pixel(x*(bits==24?4:2),bits,Samples(bits)[1]);
                    Check(r.Video.Capture(0).ArgbPixels.All(p=>p==0xffffffff),"Gate positive setup did not display real white pixels");
                    if(gate=="mapping")r.Cr(0x31,0);else if(gate=="packed")r.Cr(0x3a,0);else if(gate=="blank")r.Sr(1,0x21);else r.Sr(0x18,0x20);
                    Check(r.Video.Capture(0).ArgbPixels.All(p=>p==0xff000000),"Disabled source was displayed");
                });
            test("M4 Win95 S3 direct-color physical last VRAM byte does not wrap bits="+bits,()=>
            {
                var r=new Rig();r.Configure(bits,32,1,0x1ffffc);var sample=Samples(bits)[6];r.Pixel(0x1ffffc,bits,sample);
                if(bits!=24)r.Pixel(0x1ffffe,bits,Samples(bits)[5]);
                var frame=r.Video.Capture(0);Check(frame.ArgbPixels[0]==sample.Argb,"Last real pixel was not fetched");
                if(bits!=24)Check(frame.ArgbPixels[1]==Samples(bits)[5].Argb,"Actual last VRAM byte was not fetched");
                int after=bits==24?1:2;Check(frame.ArgbPixels.Skip(after).All(p=>p==0xffffffff),"Unpopulated scanout wrapped to low VRAM");
            });
            test("M4 Win95 S3 direct-color raw continuation retains open DAC and attribute phases bits="+bits,()=>
            {
                var r=new Rig();r.Configure(bits,32,2,0x10000,16);Pattern(r,bits,32,2,0x10000,16);
                r.Ports.Write(0x3c8,17,1);r.Ports.Write(0x3c9,7,1);r.Ports.Read(0x3da,1);r.Ports.Write(0x3c0,0x30,1);
                var copy=new Rig();copy.Video.LoadState(r.Video.SaveState());copy.Card.LoadState(r.Card.SaveState());
                Check(JsonSerializer.Serialize(r.Video.SaveState(),StateValues.Json)==JsonSerializer.Serialize(copy.Video.SaveState(),StateValues.Json),"Exact direct-color raw graph was not restored");
                foreach(var side in new[]{r,copy}){side.Ports.Write(0x3c9,23,1);side.Ports.Write(0x3c9,41,1);side.Ports.Write(0x3c0,0x41,1);side.Pixel(0x10000,bits,Samples(bits)[7],0xf1);}
                Check(r.Video.Capture(123).ArgbPixels.SequenceEqual(copy.Video.Capture(123).ArgbPixels),"Future direct-color pixels differ after actual port and VRAM effects");
                Check(JsonSerializer.Serialize(r.Video.SaveState(),StateValues.Json)==JsonSerializer.Serialize(copy.Video.SaveState(),StateValues.Json),"Future complete video fields differ");
            });
            test("M4 Win95 S3 direct-color cache retries visible memory changes bits="+bits,()=>
            {
                var r=new Rig();r.Configure(bits,32,1);var buffer=new VgaFrameBuffer();r.Video.Capture(0,buffer);long rendered=buffer.RenderedFrames;
                r.Video.Capture(0,buffer);Check(buffer.RenderedFrames==rendered,"Unchanged complete frame was rendered again");
                r.Pixel(0,bits,Samples(bits)[2]);var next=r.Video.Capture(0,buffer);
                Check(next.ArgbPixels[0]==Samples(bits)[2].Argb&&buffer.RenderedFrames==rendered+1,"Actual direct-color memory update reused stale pixels");
            });
            test("M4 Win95 S3 direct RAMDAC does not use graphics-command pixel length bits="+bits,()=>
            {
                var r=new Rig();r.Configure(bits,32,1);r.Pixel(0,bits,Samples(bits)[6]);r.Cr(0x50,0);r.Ports.Write(0x4ae8,5,2);
                Check(r.Video.Capture(0).ArgbPixels[0]==Samples(bits)[6].Argb,"Graphics engine-only mode controls replaced RAMDAC color coding");
            });
            test("M4 Win95 S3 real protected CPU OUT and physical store select direct color bits="+bits,()=>CpuPixel(bits));
        }
        foreach(byte mode in new byte[]{0x10,0x20,0x40,0x60,0x70,0x80,0x90,0xa0,0xb0,0xc0,0xe0,0xf0})
            test("M4 Win95 S3 unimplemented RAMDAC format does not fabricate direct pixels mode="+mode.ToString("X2"),()=>
            {var r=new Rig();r.Configure(24,32,1);r.Pixel(0,24,Samples(24)[1]);r.Cr(0x67,mode);Check(r.Video.Capture(0).ArgbPixels.All(p=>p==0xff000000));});
        test("M4 Win95 S3 indexed palette path remains distinct from direct-color bypass",()=>
        {
            var r=new Rig();r.Configure(16,32,1);r.Memory.WriteByte(High,0x91);r.Cr(0x50,0);r.Cr(0x67,0);
            PortsPalette(r,0);r.Ports.Write(0x3c6,0,1);Check(r.Video.Indexed256&&r.Video.Capture(0).ArgbPixels.All(p=>p==0xff000000));
            r.Ports.Write(0x3c8,0,1);r.Ports.Write(0x3c9,63,1);r.Ports.Write(0x3c9,0,1);r.Ports.Write(0x3c9,0,1);
            Check(r.Video.Capture(0).ArgbPixels.All(p=>p==0xffff0000),"Indexed0 must still resolve the actual DAC entry");
        });
    }
    /// <summary>Programs every actual VGA palette component, without writing a renderer cache or saved field.</summary>
    private static void PortsPalette(Rig rig,uint value)
    {rig.Ports.Write(0x3c8,0,1);for(int i=0;i<768;i++)rig.Ports.Write(0x3c9,value,1);}
    /// <summary>Runs own LGDT/CR0/far-transition code, actual word OUT and an actual32-bit physical pixel store.</summary>
    private static void CpuPixel(int bits)
    {
        var r=new Rig();r.Configure(bits,32,1);r.Cr(0x67,0);r.Effects.Clear();
        r.Memory.WriteWord(0x800,23);r.Memory.WriteDword(0x802,0x820);
        r.Memory.WriteDword(0x828,0x0000ffff);r.Memory.WriteDword(0x82c,0x00cf9a00);
        r.Memory.WriteDword(0x830,0x0000ffff);r.Memory.WriteDword(0x834,0x00cf9200);
        byte[] start=[0xfa,0x0f,0x01,0x16,0,8,0x0f,0x20,0xc0,0x66,0x83,0xc8,1,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
        start.CopyTo(r.Memory.Ram,0x1000);
        uint data=Samples(bits)[2].Encoded|0xa5000000u;
        var code=new List<byte>{0x66,0xb8,0x10,0,0x8e,0xd8,0x8e,0xd0,0xbc,0,0,9,0,0x66,0xba,0xd4,3,0x66,0xb8,0x67,Ramdac(bits),0x66,0xef,0xc7,5,0,0,0,0xe0};
        code.AddRange(BitConverter.GetBytes(data));code.AddRange(new byte[]{0x8b,0x1d,0,0,0,0xe0,0xf4});code.ToArray().CopyTo(r.Memory.Ram,0x2000);
        var cpu=new X86Cpu(r.Memory,r.Ports);cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Ss,0);cpu.Eip=0x1000;cpu.Eflags=2;
        for(int i=0;i<256&&!cpu.Halted;i++)cpu.Step();
        Check(cpu.Halted&&!cpu.TripleFaulted&&cpu.ProtectedMode&&cpu.GetRegister(Register.Bx)==data,"Actual own CPU pixel caller failed before its legitimate protected HLT");
        Check(r.Effects.Any(e=>e.Write&&e.Port==0x3d4&&e.Width==2&&e.Value==((uint)Ramdac(bits)<<8|0x67)),"No actual indexed CPU word OUT selected the RAMDAC mode");
        Check(r.Memory.ReadDword(High)==data&&r.Video.Capture(0).ArgbPixels[0]==Samples(bits)[2].Argb,"Actual CPU physical pixel store was not displayed");
    }
}
