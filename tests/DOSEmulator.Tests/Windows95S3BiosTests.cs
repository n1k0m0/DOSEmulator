// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Real own Trio64 BIOS instructions, original in-box mode identifiers, physical pixels and complete continuation.</summary>
internal static class Windows95S3BiosTests
{
    private sealed record Mode(ushort Number, int Width, int Height, int Pitch, int Bits, byte Dac, byte Engine, int Red, int Green, int Blue, int Reserved);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write);
    private static readonly Mode[] Modes =
    [
        new(0x101,640,480,640,8,0,0x40,0,0,0,0), new(0x103,800,600,800,8,0,0x80,0,0,0,0), new(0x105,1024,768,1024,8,0,0,0,0,0,0),
        new(0x110,640,480,1280,16,0x30,0x50,5,5,5,1), new(0x111,640,480,1280,16,0x50,0x50,5,6,5,0),
        new(0x114,800,600,1600,16,0x50,0x90,5,6,5,0), new(0x117,1024,768,2048,16,0x50,0x10,5,6,5,0),
        new(0x201,640,480,640,8,0,0x40,0,0,0,0), new(0x203,800,600,800,8,0,0x80,0,0,0,0), new(0x205,1024,768,1024,8,0,0,0,0,0,0),
        new(0x211,640,480,1280,16,0x50,0x50,5,6,5,0), new(0x214,800,600,1600,16,0x50,0x90,5,6,5,0), new(0x220,640,480,2560,32,0xd0,0x70,8,8,8,8)
    ];

    /// <summary>Rejects missing architectural, port, buffer or independently expected pixel effects.</summary>
    private static void Check(bool value, string why)
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Actual newly constructed own Windows board, without a host disk, CD, input endpoint or injected BIOS return.</summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>Complete product PC and constructor-selected own VBE ROM.</summary>
        public PcMachine Machine { get; }
        /// <summary>Only actual completed guest/fixture bus transactions.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Builds and POSTs only the expressly named new board and masks unrelated timer interrupts for ABI tests.</summary>
        public Rig(bool mhz133 = false)
        {
            var profile = new MachineProfile { Id = mhz133 ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
                Name = "Own Trio64 BIOS proof", Cpu = MachineProfile.P54cCpuId, CpuHz = mhz133 ? 133000000 : 100000000,
                RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2 };
            Machine = PcMachine.CreateWithOpticalDiscs(profile, new IReadOnlyBlockDevice?[4], null);
            Machine.RunSteps(300000); Check(Machine.Fault is null && Machine.Cpu.Halted, Machine.Fault ?? "Own BIOS POST did not halt on absent medium.");
            Machine.Ports.Write(0x21,255,1); Machine.Ports.Write(0xa1,255,1); Machine.Ports.Write(0x92,2,1);
            Machine.Ports.Accessed += Access;
        }
        /// <summary>Records completed accesses without reading devices again.</summary>
        private void Access(ushort address, uint value, int width, bool write) => Ports.Add(new(address,value,width,write));
        /// <summary>Executes an actual INT10 or returned ROM far-function call with independent32-bit register sentinels.</summary>
        public uint[] Call(ushort ax, ushort bx = 0, ushort cx = 0, ushort dx = 0, ushort es = 0x3000, ushort di = 0, uint far = 0)
        {
            var cpu = Machine.Cpu; cpu.Reset(); cpu.SetSegment(Segment.Cs,0); cpu.SetSegment(Segment.Ss,0); cpu.SetSegment(Segment.Ds,0x2345); cpu.SetSegment(Segment.Es,es);
            cpu.Eip = 0x1000; cpu.Eflags = X86Cpu.Direction | X86Cpu.Zero | X86Cpu.Parity | 2;
            uint[] values = [0x12340000u|ax,0x23450000u|cx,0x34560000u|dx,0x45670000u|bx,0x9000,0x67891234,0x789a1234,0x89ab0000u|di];
            for (int i=0;i<8;i++) cpu.SetRegister((Register)i,values[i]);
            byte[] code = far == 0 ? [0xdb,0xe3,0xd9,0xeb,0xcd,0x10,0xfa,0xf4] :
                [0xdb,0xe3,0xd9,0xeb,0x9a,(byte)far,(byte)(far>>8),(byte)(far>>16),(byte)(far>>24),0xfa,0xf4];
            for (int i=0;i<code.Length;i++) Machine.Memory.WriteByte(0x1000u+(uint)i,code[i]);
            Ports.Clear(); long count=0;
            while (!cpu.Halted && Machine.Fault is null && count<3000000) { int step=Machine.RunSteps(4096); count+=step; if(step==0)break; }
            Check(cpu.Halted && Machine.Fault is null, Machine.Fault ?? "Real own BIOS did not return.");
            uint[] actual = Enumerable.Range(0,8).Select(i=>cpu.GetRegister((Register)i)).ToArray();
            Check((actual[0]&0xffff0000)==(values[0]&0xffff0000),"BIOS changed upper EAX outside16-bit AX output");
            for (int i=1;i<8;i++)
            {
                uint outputMask = ax==0x4f03 && i==3 || ax==0x4f05 && (bx>>8)==1 && i==2 ? 65535u :
                    (ax is 0x1007 or 0x1008) && i==3 ? 0xff00u : 0;
                Check((actual[i]&~outputMask)==(values[i]&~outputMask),"BIOS changed preserved GPR bits"+i);
            }
            Check(cpu.GetSegment(Segment.Ds)==0x2345 && cpu.GetSegment(Segment.Es)==es && cpu.Eflags==(X86Cpu.Direction|X86Cpu.Zero|X86Cpu.Parity|2),"BIOS changed caller flags/segments");
            Check(cpu.FpuControlWord==0x37f && (cpu.FpuStatusWord&0x3800)==0x3800,"BIOS disturbed actual raw x87 stack");
            return actual;
        }
        /// <summary>Reads only actual own physical RAM containing BIOS output.</summary>
        public byte[] Buffer(uint address = 0x30000, int length = 256) => Enumerable.Range(0,length).Select(i=>Machine.Memory.ReadByte(address+(uint)i)).ToArray();
        /// <summary>Sets one actual bank and requires the firmware return.</summary>
        public void Bank(int value) => Check((Call(0x4f05,dx:(ushort)value)[0]&65535)==0x4f,"Real bank service failed");
        /// <summary>Releases only the complete own PC and its observation hook.</summary>
        public void Dispose() { Machine.Ports.Accessed-=Access; Machine.Dispose(); }
    }

    /// <summary>Reads actual canonical VRAM through the registered raw state, independently of the renderer.</summary>
    private static byte[][] Planes(PcMachine machine) => machine.Video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Checks source byteN against the fixed four-plane interleave, never through bank/LFB aliases.</summary>
    private static byte Raw(byte[][] planes,int address) => planes[address&3][address>>2];
    /// <summary>Encodes one independent whole raw hardware graph for exact continuation comparisons.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(),StateValues.Json);

    /// <summary>Checks mode-information fields from the primary1.2 format and independent surface arithmetic.</summary>
    private static void Info(byte[] data,Mode mode)
    {
        Check(data.Length==256 && BinaryPrimitives.ReadUInt16LittleEndian(data)==0x1b && data[2]==7 && data[3]==0,"Mode support/window attributes");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4))==64 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6))==64 &&
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8))==0xa000 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10))==0,"Actual shared64KiB window");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16))==mode.Pitch && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18))==mode.Width &&
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20))==mode.Height && data[24]==1 && data[25]==mode.Bits && data[26]==1 && data[27]==(mode.Bits==8?4:6),"Exact actual surface layout");
        Check(data[29]==2097152/(mode.Pitch*mode.Height)-1 && data[30]==1,"Actual complete image pages/reserved field");
        Check(data[31]==mode.Red && data[33]==mode.Green && data[35]==mode.Blue && data[37]==mode.Reserved && data[39]==0,"Truthful direct-color masks/ramp");
        Check(data.Skip(40).All(x=>x==0),"VBE2/LFB fields falsely advertised");
        if(mode.Dac==0x30)Check(data[32]==10&&data[34]==5&&data[36]==0&&data[38]==15,"555 field positions");
        if(mode.Dac==0x50)Check(data[32]==11&&data[34]==5&&data[36]==0,"565 field positions");
        if(mode.Dac==0xd0)Check(data[32]==16&&data[34]==8&&data[36]==0&&data[38]==24,"BGRx32 field positions");
    }

    /// <summary>Checks every real mode's scanout and command-depth registers against separately fixed expected values.</summary>
    private static void Layout(Rig rig,Mode mode)
    {
        var m=rig.Machine;var fields=m.Video.SaveState().Fields;byte[] crtc=fields["_crtc"].Deserialize<byte[]>(StateValues.Json)!;
        Check(m.Video.GraphicsMode && m.Video.Width==mode.Width && m.Video.Height==mode.Height,"Actual mode geometry");
        Check(crtc[0x67]==mode.Dac && crtc[0x50]==mode.Engine && (crtc[0x13]|((crtc[0x51]&0x30)<<4))*8==mode.Pitch,"Actual RAMDAC/engine/scanout byte pitch");
        Check(crtc[0x6a]==0 && (crtc[0x35]&15)==0 && (crtc[0x58]&0x10)==0 && (crtc[0x31]&9)==9,"Actual bank0 and disabled LFB");
        Check(rig.Ports.Any(p=>p.Write&&p.Address==0x4ae8&&p.Width==2&&p.Value==1) && rig.Ports.Any(p=>p.Write&&p.Address==0x3d4&&p.Width==2&&(p.Value&255)==0x67),"No genuine enhanced mode OUTs");
        var frame=m.CaptureFrame();Check(frame.Width==mode.Width&&frame.Height==mode.Height,"Actual frame dimensions");
    }

    /// <summary>Writes independent channel vectors only through physical banks and checks every visible scanout pixel.</summary>
    private static void PixelProof(Rig rig,Mode mode)
    {
        rig.Bank(0);var m=rig.Machine;int bytes=mode.Bits/8;
        uint[] encoded = mode.Bits==8 ? [0u,1u,2u,3u] : mode.Dac==0x30 ? [0u,0x7c00u,0x3e0u,31u] : mode.Dac==0x50 ? [0u,0xf800u,0x7e0u,31u] : [0u,0xff0000u,0xff00u,255u];
        uint[] colors = mode.Bits==8 ? [0xff000000u,0xff0000ffu,0xff00ff00u,0xff00ffffu] : [0xff000000u,0xffff0000u,0xff00ff00u,0xff0000ffu];
        if(mode.Bits==8)
        {
            m.Ports.Write(0x3c8,0,1);
            foreach(byte value in new byte[]{0,0,0,0,0,63,0,63,0,0,63,63})m.Ports.Write(0x3c9,value,1);
        }
        for(int x=0;x<mode.Width;x++)for(int b=0;b<bytes;b++)m.Memory.WriteByte(0xa0000u+(uint)(x*bytes+b),(byte)(encoded[x%4]>>(b*8)));
        byte[][] raw=Planes(m);var frame=m.CaptureFrame();
        for(int y=0;y<mode.Height;y++)for(int x=0;x<mode.Width;x++)Check(frame.ArgbPixels[y*mode.Width+x]==(y==0?colors[x%4]:colors[0]),"Independent BIOS pixel mismatch at"+x+"/"+y);
        for(int x=0;x<mode.Width;x++)for(int b=0;b<bytes;b++)Check(Raw(raw,x*bytes+b)==(byte)(encoded[x%4]>>(b*8)),"Physical raw interleave mismatch");
    }

    /// <summary>Registers bounded CPU/firmware/device proofs; an installed original Windows driver remains a separate acceptance.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(Mode mode in Modes)
        {
            test("M4 Win95 own S3 VBE actual1.2 info mode="+mode.Number.ToString("X"),()=>
            {
                using var r=new Rig();Check((r.Call(0x4f01,cx:mode.Number)[0]&65535)==0x4f,"ModeInfo failed");Info(r.Buffer(),mode);
            });
            test("M4 Win95 own S3 VBE real mode registers/pixels mode="+mode.Number.ToString("X"),()=>
            {
                using var r=new Rig();Check((r.Call(0x4f02,bx:mode.Number)[0]&65535)==0x4f,"ModeSet failed");Layout(r,mode);PixelProof(r,mode);
                Check((r.Call(0x4f03)[3]&65535)==mode.Number,"Current-mode OEM identity lost");
            });
        }
        test("M4 Win95 own S3 VBE controller truthful2MiB list and actual far pointers",()=>
        {
            using var r=new Rig();Check((r.Call(0x4f00)[0]&65535)==0x4f,"ControllerInfo failed");byte[] data=r.Buffer();
            Check(data.AsSpan(0,4).SequenceEqual("VESA"u8)&&BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4))==0x102&&BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10))==0&&BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18))==32,"False controller capability");
            uint modes=((uint)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16))<<4)+BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
            for(int i=0;i<Modes.Length;i++)Check(r.Machine.Memory.ReadWord(modes+(uint)i*2)==Modes[i].Number,"Advertised mode without real implementation");
            Check(r.Machine.Memory.ReadWord(modes+(uint)Modes.Length*2)==65535,"Mode list terminator");
            uint oem=((uint)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8))<<4)+BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
            Check(r.Machine.Memory.ReadDword(oem)==0x45534f44,"OEM text does not identify our own DOSE firmware");
        });
        foreach(ushort function in new ushort[]{0x4f00,0x4f01})
            test("M4 Win95 own S3 VBE exact info segment boundary function="+function.ToString("X"),()=>
            {
                using var r=new Rig();for(uint i=0x2feff;i<=0x30000;i++)r.Machine.Memory.WriteByte(i,0xa5);
                Check((r.Call(function,cx:0x101,es:0x2000,di:0xff00)[0]&65535)==0x4f,"Last complete buffer rejected");
                Check(r.Machine.Memory.ReadByte(0x2feff)==0xa5&&r.Machine.Memory.ReadByte(0x30000)==0xa5,"Info overflowed exact256B buffer");
                byte[] before=r.Buffer(0x2feff,258);Check((r.Call(function,cx:0x101,es:0x2000,di:0xff01)[0]&65535)==0x14f&&r.Buffer(0x2feff,258).SequenceEqual(before),"Cross-segment info buffer changed");
            });
        foreach((ushort function,ushort bx,ushort cx,ushort dx) in new (ushort,ushort,ushort,ushort)[]{(0x4f02,0x112,0,0),(0x4f02,0x4101,0,0),(0x4f02,0x6001,0,0),(0x4f01,0,0x222,0),(0x4f04,0,0,0),(0x4f06,0,0,0),(0x4f05,0,0,32),(0x4f05,1,0,0),(0x4f05,0x200,0,0)})
            test("M4 Win95 own S3 VBE truthful rejection "+function.ToString("X")+"/"+bx.ToString("X")+"/"+cx.ToString("X")+"/"+dx,()=>
            {
                using var r=new Rig();r.Call(0x4f02,bx:0x8101);string before=JsonSerializer.Serialize(r.Machine.Video.SaveState(),StateValues.Json);
                Check((r.Call(function,bx,cx,dx)[0]&65535)==0x14f,"Unimplemented service reported success");
                Check(before==JsonSerializer.Serialize(r.Machine.Video.SaveState(),StateValues.Json),"Rejected service changed video state");
            });
        test("M4 Win95 own S3 VBE actual32banks clear/preserve/far window and legacy return",()=>
        {
            using var r=new Rig();r.Call(0x4f02,bx:0x8101);r.Call(0x4f01,cx:0x101);uint pointer=BinaryPrimitives.ReadUInt32LittleEndian(r.Buffer().AsSpan(12));
            for(int bank=0;bank<32;bank++){r.Bank(bank);r.Machine.Memory.WriteByte(0xa0000,(byte)(bank+1));r.Machine.Memory.WriteByte(0xaffff,(byte)(255-bank));}
            Check((r.Call(0x1234,bx:0,dx:31,far:pointer)[0]&65535)==0x4f,"Far bank entry requires fake AX");
            Check((r.Call(0x4f05,bx:0x100)[2]&65535)==31,"Actual bank31 get failed");
            Check((r.Call(0x4f02,bx:0x8205)[0]&65535)==0x4f,"OEM preserve failed");byte[][] preserved=Planes(r.Machine);
            for(int bank=0;bank<32;bank++)Check(Raw(preserved,bank*65536)==bank+1&&Raw(preserved,(bank+1)*65536-1)==255-bank,"Mode preservation lost physical bank");
            r.Call(0x4f02,bx:0x101);Check(Planes(r.Machine).All(p=>p.All(b=>b==0)),"Nonpreserving mode did not clear all2MiB");
            r.Call(3);Check((r.Call(0x4f03)[3]&65535)==3&&!r.Machine.Video.GraphicsMode,"Actual legacy3 return failed");
        });
        test("M4 Win95 MINI VGA actual1002 attribute mapping changes blue pixel to intended red",()=>
        {
            using var r=new Rig();r.Call(0x12);
            r.Call(0x1010,bx:1,cx:63);r.Call(0x1010,bx:12,dx:0x3f00);r.Call(0x0c01);
            Check(r.Machine.CaptureFrame().ArgbPixels[0]==0xff0000ff,"Independent initial palette1 must actually be blue");
            byte[] palette=[0,12,10,14,1,21,35,7,15,36,18,54,9,45,27,63,0];
            for(int i=0;i<palette.Length;i++)r.Machine.Memory.WriteByte(0x30000u+(uint)i,palette[i]);
            r.Call(0x1002);byte[] actual=r.Machine.Video.SaveState().Fields["_attr"].Deserialize<byte[]>(StateValues.Json)!;
            Check(actual.AsSpan(0,16).SequenceEqual(palette.AsSpan(0,16))&&actual[17]==0,"Actual original MINI palette17 bytes not applied");
            Check(r.Ports.Any(p=>p.Write&&p.Address==0x3c0&&p.Width==1),"Palette service bypassed actual attribute ports");
            Check(r.Machine.CaptureFrame().ArgbPixels[0]==0xffff0000,"Original1002 palette did not select the actual red DAC entry");
        });
        test("M4 Win95 own VGA attribute individual/overscan/readall actual ports and BH ABI",()=>
        {
            using var r=new Rig();r.Call(0x1000,bx:0x3d05);Check((r.Call(0x1007,bx:5)[3]>>8&255)==0x3d,"Single palette read BH");
            r.Call(0x1001,bx:0x2a00);Check((r.Call(0x1008)[3]>>8&255)==0x2a,"Overscan read BH");
            r.Machine.Memory.WriteByte(0x2ffff,0xc7);r.Machine.Memory.WriteByte(0x30011,0xd8);r.Call(0x1009);
            byte[] all=r.Buffer(0x30000,17);Check(all[5]==0x3d&&all[16]==0x2a&&r.Machine.Memory.ReadByte(0x2ffff)==0xc7&&r.Machine.Memory.ReadByte(0x30011)==0xd8,"ReadAll palette buffer/guards");
        });
        foreach(ushort function in new ushort[]{0x1002,0x1009})test("M4 Win95 own VGA palette exact17B segment limit "+function.ToString("X"),()=>
        {
            using var r=new Rig();for(uint i=0x2ffee;i<=0x30000;i++)r.Machine.Memory.WriteByte(i,0x25);
            r.Call(function,dx:0xffef,es:0x2000);Check(r.Machine.Memory.ReadByte(0x2ffee)==0x25&&r.Machine.Memory.ReadByte(0x30000)==0x25,"Palette17 buffer crossed guards");
            byte[] before=r.Buffer(0x2ffee,19);string state=JsonSerializer.Serialize(r.Machine.Video.SaveState(),StateValues.Json);
            r.Call(function,dx:0xfff0,es:0x2000);Check(before.SequenceEqual(r.Buffer(0x2ffee,19))&&state==JsonSerializer.Serialize(r.Machine.Video.SaveState(),StateValues.Json)&&r.Ports.Count==0,"Crossing palette17 changed memory/hardware");
        });
        foreach(bool mono in new[]{false,true})test("M4 Win95 own VGA palette active status decoder/display latch "+mono,()=>
        {
            using var r=new Rig();if(mono)r.Machine.Ports.Write(0x3c2,0x66,1);
            r.Machine.Ports.Read(mono?(ushort)0x3ba:(ushort)0x3da,1);r.Machine.Ports.Write(0x3c0,5,1);
            r.Call(0x1000,bx:0x1603);var fields=r.Machine.Video.SaveState().Fields;
            Check(fields["_attrIndex"].GetByte()==5&&!fields["_displayEnabled"].GetBoolean()&&!fields["_attributeData"].GetBoolean(),"Palette call changed address/display latch or left incomplete data phase");
            Check(r.Ports.Any(p=>!p.Write&&p.Address==(mono?0x3ba:0x3da)),"Palette call polled inactive status port");
        });
        foreach(bool mhz133 in new[]{false,true})test("M4 Win95 own S3 VBE complete running store continuation "+(mhz133?133:100),()=>Snapshot(mhz133));
    }

    /// <summary>Uses the real store and fresh branch for all CPU/RAM/VRAM/devices/events, including open DAC/ATC and raw x87.</summary>
    private static void Snapshot(bool mhz133)
    {
        string root=Path.Combine(Path.GetTempPath(),"dose-own-s3-vbe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var r=new Rig(mhz133);r.Call(0x4f02,bx:0x8214);r.Bank(17);var m=r.Machine;
            m.Memory.WriteWord(0xa0020,0xf800);m.Ports.Write(0x3c8,37,1);m.Ports.Write(0x3c9,12,1);m.Ports.Read(0x3da,1);m.Ports.Write(0x3c0,0x10,1);
            string save=Path.Combine(root,"cut.dossav"),branch=Path.Combine(root,"resumed.dosebranch");SnapshotStore.Capture(m,save);
            using var resumed=SnapshotStore.Load(save,branch);Check(State(m)==State(resumed)&&m.Memory.Ram.AsSpan().SequenceEqual(resumed.Memory.Ram),"Full restored own VBE cut differs");
            foreach(var side in new[]{m,resumed}){side.Ports.Write(0x3c9,23,1);side.Ports.Write(0x3c9,34,1);side.Ports.Write(0x3c0,1,1);side.Ports.Write(0x3c0,0x20,1);side.Memory.WriteWord(0xa0040,0x7e0);side.RunSteps(100,side.Scheduler.Now+1000000);}
            Check(State(m)==State(resumed)&&m.Memory.Ram.AsSpan().SequenceEqual(resumed.Memory.Ram),"Full future own VBE graph/RAM differs");
            Check(m.CaptureFrame().ArgbPixels.SequenceEqual(resumed.CaptureFrame().ArgbPixels),"Actual future scanout differs");
        }
        finally
        {
            string full=Path.GetFullPath(root),temporary=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(string.Equals(Path.GetDirectoryName(full),temporary,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("dose-own-s3-vbe-",StringComparison.Ordinal),"Refuse deleting a path outside this own temporary generation");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
