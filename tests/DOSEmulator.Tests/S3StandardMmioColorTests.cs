// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Native physical standard-MMIO color WORD RSF, unchanged BYTE/DWORD/read lanes and complete retained futures.</summary>
internal static class S3StandardMmioColorTests
{
    /// <summary>Own nonconflicting physical LFB target, separate from the fixed A0000 register aperture.</summary>
    private const uint High=0xe0000000;
    /// <summary>Primary Table13-1 standard bases, packed aliases and independent canonical BG/FG/mask/compare indices.</summary>
    private sealed record ColorRegister(ushort Port,uint Packed,int Index);
    /// <summary>All five original color/mask registers; no aliases are inferred from the implementation under test.</summary>
    private static readonly ColorRegister[] Registers=
    [new(0xa2e8,0xa8120,0),new(0xa6e8,0xa8124,1),new(0xaae8,0xa8128,2),new(0xaee8,0xa812c,3),new(0xb2e8,0xa8130,4)];

    /// <summary>Rejects a wrong complete state, source word, bus result, independent pixel or unchanged control.</summary>
    private static void Check(bool value,string why){if(!value)throw new InvalidOperationException(why);}
    /// <summary>Uses only newly owned RAM and the actual original S3 card; no DOS, Windows, host endpoint or private medium is used.</summary>
    private sealed class Rig
    {
        /// <summary>Owned physical RAM and actual fixed/dynamic S3 mappings.</summary>
        public readonly MemoryBus Memory=new(2*1048576);
        /// <summary>Owned actual normal VGA and enhanced-register I/O bus.</summary>
        public readonly IoPortBus Ports=new();
        /// <summary>Real shared register, latch, VRAM and scanout component.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Real original PCI/POS-gated fixed and dynamic video target.</summary>
        public readonly S3Trio64 Card;
        /// <summary>Explicit test-selected source storage size, independent from the card's decoder.</summary>
        public readonly int Bytes;
        /// <summary>Creates and programs only the own device graph at the specified original pixel storage size.</summary>
        public Rig(int bytes=4)
        {
            Bytes=bytes;Video=new(null,false,true);Card=new(Memory,Video);
            Memory.Map(0xa0000,0x20000,Card,"Own standard-MMIO color proof");Memory.MapDynamic(Card);
            Ports.Map(0x3b0,0x30,Card);Ports.Map(0x46e8,1,Card);Ports.Map(0x102,1,Card);Ports.Map(0x4ae8,2,Card);
            foreach(var window in S3Trio64.EnginePortWindows)Ports.Map(window.Start,window.Length,Card);
            Configure(Ports,Card,bytes);
        }
        /// <summary>Programs actual MULT_MISC through its existing original indexed port.</summary>
        public void Misc(int value)=>Ports.Write(0xbee8,(uint)(0xe000|value),2);
        /// <summary>Seeds a complete independent source value through the packed direct DWORD alias.</summary>
        public void Seed(ColorRegister register,uint value)=>Memory.WriteDword(register.Packed,value);
        /// <summary>Offers a real physical WORD to the original bus/device interface without synthesizing byte accesses.</summary>
        public void Word(ColorRegister register,ushort value)=>Memory.WriteWord(0xa0000u+register.Port,value);
    }

    /// <summary>Programs real primary PCI/POS/CRTC/engine resources with independent640-pixel scanout and command pitches.</summary>
    private static void Configure(IoPortBus ports,S3Trio64 card,int bytes)
    {
        card.WriteConfiguration(4,3,2);card.WriteConfiguration(0x10,High,4);
        ports.Write(0x46e8,0x10,1);ports.Write(0x102,1,1);ports.Write(0x46e8,8,1);ports.Write(0x3c2,0x67,1);
        Cr(ports,0x38,0x48);Cr(ports,0x39,0xa5);Sr(ports,8,6);Sr(ports,0,3);Sr(ports,1,1);Sr(ports,2,15);Sr(ports,4,14);
        Sr(ports,0x0a,0x80);Sr(ports,0x15,0x80);ports.Write(0x3ce,0x4005,2);ports.Write(0x3ce,0x0506,2);ports.Write(0x3ce,0xff08,2);
        Cr(ports,0x40,1);Cr(ports,0x31,8);Cr(ports,0x3a,0x10);Cr(ports,0x50,(byte)(0x40|(bytes==4?0x30:bytes==2?0x10:0)));
        Cr(ports,0x67,bytes==4?(byte)0xd0:bytes==2?(byte)0x50:(byte)0);Cr(ports,0x58,0x12);
        Cr(ports,1,79);Cr(ports,7,0);Cr(ports,9,0);Cr(ports,0x12,31);Cr(ports,0x13,(byte)(80*bytes));Cr(ports,0x51,(byte)(((80*bytes)>>4)&0x30));
        Cr(ports,12,0);Cr(ports,13,0);Cr(ports,0x69,0);Cr(ports,0x42,0);Cr(ports,0x43,0);Cr(ports,0x5d,0);Cr(ports,0x5e,0);
        ports.Write(0x4ae8,1,2);ports.Write(0x42e8,0x4000,2);ports.Write(0xbee8,0xe000,2);
        foreach(ushort port in new ushort[]{0xaae8,0xaee8})
        {ports.Write(port,65535,2);if(bytes==4)ports.Write(port,65535,2);}
        foreach(ushort value in new ushort[]{0x1000,0x2000,0x3fff,0x4fff,0xa000})ports.Write(0xbee8,value,2);
        ports.Write(0xbae8,0x27,2);ports.Write(0xb6e8,7,2);Cr(ports,0x53,0x10);
        ports.Read(0x3da,1);ports.Write(0x3c0,0x20,1);
    }
    /// <summary>Uses the existing real indexed color CRTC decoder.</summary>
    private static void Cr(IoPortBus ports,byte index,byte value){ports.Write(0x3d4,index,1);ports.Write(0x3d5,value,1);}
    /// <summary>Uses the existing real sequencer decoder.</summary>
    private static void Sr(IoPortBus ports,byte index,byte value){ports.Write(0x3c4,index,1);ports.Write(0x3c5,value,1);}
    /// <summary>Clears the own visible surface through its real LFB, including the adapter constructor's actual initial font bytes.</summary>
    private static void ClearCanvas(MemoryBus memory,int bytes)
    {for(uint offset=0;offset<640u*32u*(uint)bytes;offset+=4)memory.WriteDword(High+offset,0);}
    /// <summary>Reads only canonical saved numeric registers, without consuming an RSF-sensitive device read.</summary>
    private static (uint[] Colors,ushort Misc) Values(StateBlock state)=>(state.Fields["_trioEngineColors"].Deserialize<uint[]>(StateValues.Json)!,
        state.Fields["_trioEngineWords"].Deserialize<ushort[]>(StateValues.Json)![12]);
    /// <summary>Models exactly the two allowed field changes and compares every actual video field/VRAM byte, with no exclusions.</summary>
    private static void Expected(VgaAdapter video,StateBlock before,int index,uint color,ushort misc)
    {
        var fields=new Dictionary<string,JsonElement>(before.Fields);var (colors,_)=Values(before);colors[index]=color;
        var words=before.Fields["_trioEngineWords"].Deserialize<ushort[]>(StateValues.Json)!;words[12]=misc;
        fields["_trioEngineColors"]=JsonSerializer.SerializeToElement(colors,StateValues.Json);
        fields["_trioEngineWords"]=JsonSerializer.SerializeToElement(words,StateValues.Json);
        Check(JsonSerializer.Serialize(before with {Fields=fields},StateValues.Json)==JsonSerializer.Serialize(video.SaveState(),StateValues.Json),"Complete expected video register/VRAM state differs.");
    }
    /// <summary>Restores the whole actual raw video/PCI graph and own RAM, retaining every existing partial latch.</summary>
    private static Rig Copy(Rig original)
    {
        var copy=new Rig(original.Bytes);original.Memory.Ram.CopyTo(copy.Memory.Ram,0);
        copy.Video.LoadState(original.Video.SaveState());copy.Card.LoadState(original.Card.SaveState());return copy;
    }
    /// <summary>Compares all fields, populated storage, PCI latches and full own RAM after the same actual future accesses.</summary>
    private static void Equal(Rig original,Rig copy)
    {
        Check(JsonSerializer.Serialize(original.Video.SaveState(),StateValues.Json)==JsonSerializer.Serialize(copy.Video.SaveState(),StateValues.Json)&&
            JsonSerializer.Serialize(original.Card.SaveState(),StateValues.Json)==JsonSerializer.Serialize(copy.Card.SaveState(),StateValues.Json)&&
            original.Memory.Ram.AsSpan().SequenceEqual(copy.Memory.Ram),"Whole raw color-word continuation differs.");
    }

    /// <summary>Executes a real guest MOV AX,imm/MOV ES:moffs16,AX/HLT sequence; neither port return nor memory result is injected.</summary>
    private static void GuestWord(Rig rig,ushort port,ushort word)
    {
        byte[] program=[0xb8,(byte)word,(byte)(word>>8),0x26,0xa3,(byte)port,(byte)(port>>8),0xf4];program.CopyTo(rig.Memory.Ram,0x1000);
        var cpu=new X86Cpu(rig.Memory,rig.Ports);cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Es,0xa000);cpu.Eip=0x1000;cpu.Eflags=2;
        for(int i=0;i<4&&!cpu.Halted;i++)cpu.Step();
        Check(cpu.Halted&&cpu.Eip==0x1008,"Actual guest WORD store did not retire and halt.");
    }
    /// <summary>Draws one actual NEW foreground rectangle pixel using the separately programmed32-bit source register.</summary>
    private static void Draw(IoPortBus ports,int x=3,int y=2)
    {
        ports.Write(0x86e8,(uint)x,2);ports.Write(0x82e8,(uint)y,2);ports.Write(0x96e8,0,2);ports.Write(0xbee8,0,2);ports.Write(0xbae8,0x27,2);
        ports.Write(0x9ae8,0x40b1,2);
    }

    /// <summary>Registers independent native-word, RSF, byte-cut, direct-lane, gate, legacy-depth and full-store controls.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(ColorRegister register in Registers)
        {
            foreach(bool upper in new[]{false,true})
            {
                test("S3 standard MMIO32 color WORD sequence "+register.Port.ToString("X")+" upper="+upper,()=>
                {
                    var r=new Rig();r.Seed(register,0x89abcdef);r.Misc(upper?0x10:0);var before=r.Video.SaveState();
                    r.Word(register,0xc0c0);Expected(r.Video,before,register.Index,upper?0xc0c0cdefu:0x89abc0c0u,(ushort)(upper?0:0x10));
                    r.Word(register,0x00c0);Expected(r.Video,before,register.Index,upper?0xc0c000c0u:0x00c0c0c0u,(ushort)(upper?0x10:0));
                });
                foreach(bool cmr32 in new[]{false,true})
                {
                    test("S3 standard MMIO32 unchanged direct WORD/DWORD reads RSF="+upper+" CMR32="+cmr32+"/"+register.Port.ToString("X"),()=>
                    {
                        var r=new Rig();r.Seed(register,0xa1b2c3d4);ushort misc=(ushort)((upper?0x10:0)|(cmr32?0x200:0));r.Misc(misc);
                        var before=r.Video.SaveState();uint address=0xa0000u+register.Port;
                        Check(r.Memory.ReadWord(address)==0xc3d4&&r.Memory.ReadWord(address)==0xc3d4,"Existing direct low-word read changed or consumed RSF.");
                        Check(r.Memory.ReadWord(address+2)==0xa1b2,"Existing explicit high-word read changed.");
                        Check(r.Memory.ReadDword(address)==0xa1b2c3d4&&r.Memory.ReadDword(address)==0xa1b2c3d4,"Actual four-lane DWORD read changed or consumed RSF.");
                        Expected(r.Video,before,register.Index,0xa1b2c3d4,misc);
                    });
                    test("S3 standard MMIO32 unchanged BYTE/DWORD writes RSF="+upper+" CMR32="+cmr32+"/"+register.Port.ToString("X"),()=>
                    {
                        var r=new Rig();r.Seed(register,0x89abcdef);ushort misc=(ushort)((upper?0x10:0)|(cmr32?0x200:0));r.Misc(misc);
                        var before=r.Video.SaveState();uint address=0xa0000u+register.Port;
                        foreach((int lane,byte value) in new[]{(0,(byte)0xd4),(1,(byte)0xc3),(2,(byte)0xb2),(3,(byte)0xa1)})r.Memory.WriteByte(address+(uint)lane,value);
                        Expected(r.Video,before,register.Index,0xa1b2c3d4,misc);
                        r.Memory.WriteDword(address,0x1234abcd);Expected(r.Video,before,register.Index,0x1234abcd,misc);
                    });
                }
            }
            foreach(bool cmr32 in new[]{false,true})
            {
                test("S3 packed color DWORD/direct upper lanes preserve RSF "+register.Port.ToString("X")+" CMR32="+cmr32,()=>
                {
                    var r=new Rig();ushort misc=(ushort)(0x10|(cmr32?0x200:0));r.Misc(misc);var before=r.Video.SaveState();
                    r.Memory.WriteDword(register.Packed,0x1234abcd);Expected(r.Video,before,register.Index,0x1234abcd,misc);
                    r.Memory.WriteWord(register.Packed+2,0x5678);Expected(r.Video,before,register.Index,0x5678abcd,misc);
                    Check(r.Memory.ReadDword(register.Packed)==uint.MaxValue,"Write-only packed alias returned a color word.");
                    Expected(r.Video,before,register.Index,0x5678abcd,misc);
                });
                test("S3 PIO color DWORD and forbidden widths preserve existing CMR32 policy "+register.Port.ToString("X")+" CMR32="+cmr32,()=>
                {
                    var r=new Rig();r.Seed(register,0x89abcdef);ushort misc=(ushort)(0x10|(cmr32?0x200:0));r.Misc(misc);var before=r.Video.SaveState();
                    r.Ports.Write(register.Port,0x1234abcd,4);uint expected=cmr32?0x1234abcdu:0x89abcdefu;
                    Expected(r.Video,before,register.Index,expected,misc);
                    Check(r.Ports.Read(register.Port,4)==(cmr32?expected:uint.MaxValue),"PIO DWORD CMR32 return differs.");
                    if(cmr32)
                    {
                        r.Ports.Write(register.Port,0x9876,2);r.Ports.Write(register.Port,0x55,1);
                        Check(r.Ports.Read(register.Port,2)==65535,"CMR32 accepted an unsupported PIO word read.");
                        Expected(r.Video,before,register.Index,expected,misc);
                    }
                });
                foreach(int bytes in new[]{1,2})test("S3 standard MMIO old depth direct WORD "+register.Port.ToString("X")+" bytes="+bytes+" CMR32="+cmr32,()=>
                {
                    var r=new Rig(bytes);r.Seed(register,0x89abcdef);ushort misc=(ushort)(0x10|(cmr32?0x200:0));r.Misc(misc);var before=r.Video.SaveState();
                    r.Word(register,0x3344);r.Word(register,0x1122);Expected(r.Video,before,register.Index,0x89ab1122,misc);
                    Check(r.Memory.ReadWord(0xa0000u+register.Port)==0x1122,"Old8/16bpp standard MMIO word read differs.");
                    Expected(r.Video,before,register.Index,0x89ab1122,misc);
                });
            }
            test("S3 standard MMIO direct CMR32 DWORD/lane addresses "+register.Port.ToString("X"),()=>
            {
                var r=new Rig();r.Misc(0x210);var before=r.Video.SaveState();uint address=0xa0000u+register.Port;
                r.Memory.WriteDword(address,0xa1b2c3d4);Expected(r.Video,before,register.Index,0xa1b2c3d4,0x210);
                Check(r.Memory.ReadDword(address)==0xa1b2c3d4,"Direct standard CMR32 DWORD read differs.");
                r.Memory.WriteWord(address+2,0x5678);r.Memory.WriteWord(address,0x1122);
                Expected(r.Video,before,register.Index,0x56781122,0x210);
            });
            test("S3 standard MMIO and original PIO share one RSF "+register.Port.ToString("X"),()=>
            {
                var r=new Rig();r.Misc(0);var before=r.Video.SaveState();
                r.Ports.Write(register.Port,0x3344,2);r.Word(register,0x1122);Expected(r.Video,before,register.Index,0x11223344,0);
                r.Word(register,0x7788);r.Ports.Write(register.Port,0x5566,2);Expected(r.Video,before,register.Index,0x55667788,0);
            });
            foreach(int cut in new[]{0,1,2})test("S3 standard MMIO32 unchanged BYTE-lane raw continuation "+register.Port.ToString("X")+" cut="+cut,()=>RawByteCut(register,cut));
            test("S3 standard MMIO32 whole-WORD raw continuation "+register.Port.ToString("X"),()=>
            {
                var r=new Rig();r.Seed(register,0x89776655);r.Misc(0);r.Word(register,0xc3d4);
                var value=Values(r.Video.SaveState());Check(value.Colors[register.Index]==0x8977c3d4&&value.Misc==0x10,"Actual complete low-WORD/RSF cut differs.");
                var copy=Copy(r);Equal(r,copy);
                foreach(var side in new[]{r,copy})
                {
                    side.Word(register,0xa1b2);var complete=Values(side.Video.SaveState());
                    Check(complete.Colors[register.Index]==0xa1b2c3d4&&complete.Misc==0,"Whole-WORD continuation resumed into the wrong half.");
                }
                Equal(r,copy);
            });
            test("S3 standard MMIO32 unchanged direct read-latch import "+register.Port.ToString("X"),()=>
            {
                var r=new Rig();r.Seed(register,0xa1b2c3d4);r.Misc(0x10);uint address=0xa0000u+register.Port;
                Check(r.Memory.ReadByte(address)==0xd4,"Original sampled low byte differs.");
                var raw=r.Video.SaveState();Check(raw.Fields["_trioEngineReadValid"].GetBoolean()&&Values(raw).Misc==0x10,"Existing direct read latch changed RSF before its cut.");
                var copy=Copy(r);Equal(r,copy);
                foreach(var side in new[]{r,copy})
                {
                    Check(side.Memory.ReadByte(address+1)==0xc3,"Restored high byte resampled the direct read latch.");
                    Check(side.Memory.ReadWord(address)==0xc3d4&&side.Memory.ReadWord(address+2)==0xa1b2&&side.Memory.ReadDword(address)==0xa1b2c3d4,"Restored direct read lanes differ.");
                    Check(Values(side.Video.SaveState()).Misc==0x10,"Existing direct read continuation changed write-RSF.");
                }
                Equal(r,copy);
            });
        }
        test("S3 native standard WORD pair preserves grey and all three independent BGRx channels",()=>
        {
            var r=new Rig();ClearCanvas(r.Memory,4);r.Misc(0);uint[] colors=[0x00c0c0c0,0x00ff0000,0x0000ff00,0x000000ff];
            for(int i=0;i<colors.Length;i++)
            {
                GuestWord(r,0xa6e8,(ushort)colors[i]);GuestWord(r,0xa6e8,(ushort)(colors[i]>>16));Draw(r.Ports,3+i,2);
                Check(Values(r.Video.SaveState()).Colors[1]==colors[i],"Actual guest two-WORD color lost an upper channel.");
            }
            var frame=r.Video.Capture(0);Check(frame.Width==640&&frame.Height==32,"Independent native color surface geometry.");
            for(int y=0;y<32;y++)for(int x=0;x<640;x++)
            {
                uint expected=y==2&&x is >=3 and <7?0xff000000u|colors[x-3]:0xff000000u;
                if(frame.ArgbPixels[y*640+x]!=expected)throw new InvalidOperationException($"Native WORD pixel/guard {x}/{y}: {frame.ArgbPixels[y*640+x]:X8} != {expected:X8}.");
            }
        });
        foreach(string gate in new[]{"memory-off","asleep","enhanced-locked","mmio-off","mmio-only","io-off"})
            test("S3 standard MMIO32 original target gate "+gate,()=>Gate(gate));
        foreach(int bytes in new[]{1,2,4})foreach(bool cmr32 in new[]{false,true})
            test("S3 standard MMIO physical WORD offer depth="+bytes+" CMR32="+cmr32,()=>
            {
                var r=new Rig(bytes);r.Seed(Registers[1],0x89abcdef);ushort misc=(ushort)(0x10|(cmr32?0x200:0));r.Misc(misc);
                bool expected=bytes==4&&!cmr32;
                if(!expected)Check(r.Memory.ReadByte(0xaa6e8)==0xef,"Own pending direct read-latch setup differs.");
                var before=r.Video.SaveState();bool accepted=r.Card.TryWriteWord(0xa6e8,0x1234);
                Check(accepted==expected,"Physical WORD acceptance ignored its exact depth/CMR32 guards.");
                Expected(r.Video,before,1,expected?0x1234cdefu:0x89abcdefu,expected?(ushort)0:misc);
            });
        test("S3 standard MMIO physical WORD invalid/packed/upper aliases reject before latch effects",()=>
        {
            var r=new Rig();r.Seed(Registers[1],0x89abcdef);r.Misc(0x10);Check(r.Memory.ReadByte(0xaa6e8)==0xef,"Own pending direct read-latch setup differs.");
            var before=r.Video.SaveState();
            foreach(uint offset in new uint[]{0x8124,0xa6ea,0xa6e9,0xa6ec,0x1a6e8})
            {
                Check(!r.Card.TryWriteWord(offset,0x1234),"An unaccepted physical WORD alias changed the original contract.");
                Expected(r.Video,before,1,0x89abcdef,0x10);
            }
        });
        foreach(int bytes in new[]{1,2,4})test("S3 standard color-word correction preserves CPU pixel-transfer bytes depth="+bytes,()=>
        {
            var r=new Rig(bytes);r.Misc(0);r.Ports.Write(0x86e8,3,2);r.Ports.Write(0x82e8,2,2);r.Ports.Write(0x96e8,0,2);r.Ports.Write(0xbee8,0,2);
            r.Ports.Write(0xbae8,0x47,2);r.Ports.Write(0x9ae8,0x55b1,2);r.Memory.WriteDword(0xa0000,0xa1b2c3d4);
            Check(!r.Video.TrioEngineWaiting,"A complete original CPU pixel remained outstanding.");
            for(int b=0;b<bytes;b++)Check(r.Memory.ReadByte(High+(uint)((2*640+3)*bytes+b))==(byte)(0xa1b2c3d4u>>(8*b)),"Original through-plane pixel transfer byte changed.");
            Check(Values(r.Video.SaveState()).Misc==0,"Pixel transfer changed RSF.");
        });
        foreach(int cut in new[]{0,1,2})test("S3 standard MMIO32 full SnapshotStore whole-WORD/read-latch future cut="+cut,()=>FullStore(cut));
    }

    /// <summary>Restores the unchanged direct BYTE-lane policy after one, two or three separate bytes, without inventing physical WORD offers.</summary>
    private static void RawByteCut(ColorRegister register,int cut)
    {
        var r=new Rig();r.Seed(register,0x89776655);r.Misc(0);uint address=0xa0000u+register.Port;
        r.Memory.WriteByte(address,0xd4);if(cut>=1)r.Memory.WriteByte(address+1,0xc3);if(cut>=2)r.Memory.WriteByte(address+2,0xb2);
        var raw=r.Video.SaveState();var value=Values(raw);
        uint[] partial=[0x897766d4,0x8977c3d4,0x89b2c3d4];
        Check(value.Colors[register.Index]==partial[cut]&&value.Misc==0,"A direct BYTE-lane write incorrectly consumed write-RSF.");
        var copy=Copy(r);Equal(r,copy);
        foreach(var side in new[]{r,copy})
        {
            if(cut==0)side.Memory.WriteByte(address+1,0xc3);
            if(cut<2)side.Memory.WriteByte(address+2,0xb2);
            side.Memory.WriteByte(address+3,0xa1);
            var complete=Values(side.Video.SaveState());Check(complete.Colors[register.Index]==0xa1b2c3d4&&complete.Misc==0,"Direct BYTE-lane continuation changed its explicit addressed bytes or RSF.");
        }
        Equal(r,copy);
    }

    /// <summary>Checks rejected targets and the independent memory/PIO gates without treating a declined word as a consumed bus store.</summary>
    private static void Gate(string gate)
    {
        var r=new Rig();r.Misc(0);
        if(gate=="memory-off")r.Card.WriteConfiguration(4,1,2);
        if(gate=="asleep")r.Ports.Write(0x46e8,0x10,1);
        if(gate=="enhanced-locked")Cr(r.Ports,0x40,0);
        if(gate=="mmio-off")Cr(r.Ports,0x53,0);
        if(gate=="mmio-only")Sr(r.Ports,9,0x80);
        if(gate=="io-off")r.Card.WriteConfiguration(4,2,2);
        var before=r.Video.SaveState();r.Word(Registers[1],0xc3d4);r.Word(Registers[1],0xa1b2);
        bool accepted=gate is "mmio-only" or "io-off";Expected(r.Video,before,1,accepted?0xa1b2c3d4u:0u,0);
        if(gate=="mmio-only")
        {r.Ports.Write(0xa6e8,0x8877,2);Expected(r.Video,before,1,0xa1b2c3d4,0);}
    }

    /// <summary>Uses a complete32-MiB product container after a real MMIO/PIO WORD and with an existing sampled-read latch.</summary>
    private static void FullStore(int cut)
    {
        string root=Path.Combine(Path.GetTempPath(),"dose-s3-standard-mmio-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var profile=new MachineProfile { Id=MachineProfile.S3Trio64ProfileId,Cpu=MachineProfile.P54cCpuId,CpuHz=100000000,RamMiB=32,VideoCard=MachineProfile.S3Trio64VideoId };
            using var source=PcMachine.CreateWithDisks(profile,[new MemoryDisk(new byte[128*512],"Own immutable standard-MMIO continuation fixture"),null,null,null]);
            Configure(source.Ports,source.S3!,4);ClearCanvas(source.Memory,4);
            source.Cpu.SetSegment(Segment.Cs,0);source.Cpu.SetSegment(Segment.Ss,0);source.Cpu.SetRegister(Register.Sp,0x9000);source.Cpu.Eip=0x1000;source.Cpu.Eflags=2;
            new byte[]{0x90,0xeb,0xfd}.CopyTo(source.Memory.Ram,0x1000);Check(source.RunSteps(2)==2&&source.Fault is null,"Own actual continuation loop did not execute.");
            source.Memory.WriteDword(0xa8124,0x89776655);source.Ports.Write(0xbee8,0xe000,2);
            if(cut==1)source.Ports.Write(0xa6e8,0xc3d4,2);else source.Memory.WriteWord(0xaa6e8,0xc3d4);
            if(cut==2)Check(source.Memory.ReadByte(0xaa6e8)==0xd4,"Full own read-latch cut did not sample the addressed low byte.");
            var cutState=source.Video.SaveState();var cutValue=Values(cutState);
            Check(cutValue.Colors[1]==0x8977c3d4&&cutValue.Misc==0x10,"Full own source cut lacks its actual complete low-WORD/RSF state.");
            Check(cutState.Fields["_trioEngineReadValid"].GetBoolean()==(cut==2),"Full own source read-latch presence differs.");
            if(cut==2)Check(cutState.Fields["_trioEngineReadAddress"].GetUInt16()==0xa6e8&&cutState.Fields["_trioEngineReadLatch"].GetUInt16()==0xc3d4,"Full own source sampled read-word identity differs.");
            string saved=Path.Combine(root,"cut.dossav"),branch=Path.Combine(root,"resumed.dosebranch");SnapshotStore.Capture(source,saved);
            using var restored=SnapshotStore.Load(saved,branch);
            // Capture the complete owned graph; no timing, RAM, device or scheduler fields are filtered.
            static string Whole(PcMachine pc)=>JsonSerializer.Serialize(pc.CaptureHardwareState(),StateValues.Json);
            Check(Whole(source)==Whole(restored)&&source.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram),"Full whole-WORD/read-latch import differs before its first future operation.");
            foreach(var side in new[]{source,restored})
            {
                if(cut==2)Check(side.Memory.ReadByte(0xaa6e9)==0xc3,"Full imported direct read-latch high byte changed.");
                side.Memory.WriteWord(0xaa6e8,0xa1b2);
                Check(Values(side.Video.SaveState()).Colors[1]==0xa1b2c3d4&&Values(side.Video.SaveState()).Misc==0,"Full imported standard word resumed into the wrong half.");
                Draw(side.Ports);Check(side.RunSteps(50)==50&&side.Fault is null,"Actual future own CPU loop failed.");
            }
            Check(Whole(source)==Whole(restored)&&source.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram),"Full future CPU/RAM/VRAM/devices/scheduler differ.");
            var originalFrame=source.CaptureFrame();var restoredFrame=restored.CaptureFrame();
            Check(originalFrame.Width==640&&originalFrame.Height==32&&originalFrame.ArgbPixels.SequenceEqual(restoredFrame.ArgbPixels),"Full future color-word scanout differs.");
            for(int y=0;y<32;y++)for(int x=0;x<640;x++)
            {
                uint expected=x==3&&y==2?0xffb2c3d4u:0xff000000u;
                if(originalFrame.ArgbPixels[y*640+x]!=expected)throw new InvalidOperationException($"Full imported WORD pixel/guard {x}/{y}: {originalFrame.ArgbPixels[y*640+x]:X8} != {expected:X8}.");
            }
        }
        finally
        {
            string full=Path.GetFullPath(root),parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(string.Equals(Path.GetDirectoryName(full),parent,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("dose-s3-standard-mmio-",StringComparison.Ordinal),"Refuse cleaning outside this newly owned temporary fixture.");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
