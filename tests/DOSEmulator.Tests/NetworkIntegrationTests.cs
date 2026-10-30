// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;
using DOSEmulator.Runtime.Networking;

/// <summary>Whole-PC Ethernet wiring, actual CPU ports/IRQ, strict futures and external-connection retirement checks.</summary>
internal static class NetworkIntegrationTests
{
    /// <summary>Requires an independently specified packet, physical wire or full serialized-state property.</summary>
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    /// <summary>Builds only a fresh own NIC profile with stable distinct identities and no host connection.</summary>
    private static MachineProfile Profile(bool pci, int cards = 1)
    {
        var p = pci ? new MachineProfile { Id=MachineProfile.Windows95ProfileId,Cpu=MachineProfile.P54cCpuId,CpuHz=100000000,RamMiB=32,VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId } : new MachineProfile();
        return p with { NetworkAdapters=Enumerable.Range(0,cards).Select(slot=>new NetworkAdapterProfile
        {Slot=slot,Model=pci?"rtl8029-pci":"ne2000-isa",IoBase=pci?0x6000+slot*32:0x300+slot*32,Irq=pci?11:10-slot,MacAddress="02:44:4F:53:45:"+(0x80+slot).ToString("X2")}).ToArray() };
    }
    /// <summary>Own complete Ethernet frame used for exact TX/RX bytes and snapshot futures.</summary>
    private static byte[] Packet(int length=257)
    {
        byte[] data=Enumerable.Range(0,length).Select(i=>(byte)(i*43+17)).ToArray();
        data.AsSpan(0,6).Fill(255);new byte[]{2,1,2,3,4,5}.CopyTo(data,6);data[12]=0x81;data[13]=0x37;return data;
    }
    /// <summary>Programs the actual port-visible NIC ring and receive mask, without reaching into packet storage.</summary>
    private static void Start(PcMachine m,int slot)
    {
        var n=m.Profile.NetworkAdapters.Single(n=>n.Slot==slot);ushort b=(ushort)n.IoBase;
        // Perform each real byte-wide card register write through the board's port decoder.
        void W(int offset,uint value)=>m.Ports.Write((ushort)(b+offset),value,1);
        W(0,0x21);W(14,9);W(1,0x46);W(2,0x80);W(3,0x46);W(0,0x61);
        for(int i=0;i<6;i++)W(i+1,n.GetMacBytes()[i]);W(7,0x47);W(0,0x21);W(12,4);W(13,0);W(7,0x7f);W(15,0);W(0,0x22);
        m.SetNetworkCablePreference(slot,true);
    }
    /// <summary>Uses the real PCI header to establish a known decode for non-POST component controls.</summary>
    private static void Configure(PcMachine m)
    {
        foreach(var n in m.Profile.NetworkAdapters.Where(n=>n.Model=="rtl8029-pci"))
        {m.NetworkPciCards[n.Slot]!.WriteConfiguration(0x10,(uint)n.IoBase|1,4);m.NetworkPciCards[n.Slot]!.WriteConfiguration(4,1,2);}
    }
    /// <summary>Reads or writes exact bytes through an actual remote DMA command and word data-port transactions.</summary>
    private static byte[] Dma(PcMachine m,int slot,int address,int count,byte[]? write=null)
    {
        ushort b=(ushort)m.Profile.NetworkAdapters.Single(n=>n.Slot==slot).IoBase;
        // Program the selected card through its real current I/O window.
        void W(int o,uint v)=>m.Ports.Write((ushort)(b+o),v,1);
        W(0,0x22);W(14,9);W(8,(byte)address);W(9,(byte)(address>>8));W(10,(byte)count);W(11,(byte)(count>>8));W(0,write is null?0x0au:0x12u);
        byte[] data=new byte[count];
        for(int i=0;i<count;i+=2)
        {
            if(write is not null)m.Ports.Write((ushort)(b+16),(uint)(write[i]|(i+1<count?write[i+1]<<8:0)),2);
            else {uint value=m.Ports.Read((ushort)(b+16),2);data[i]=(byte)value;if(i+1<count)data[i+1]=(byte)(value>>8);}
        }
        return data;
    }
    /// <summary>Starts a real on-card TX after storing every frame byte through remote DMA.</summary>
    private static void Send(PcMachine m,int slot,byte[] frame)
    {
        Dma(m,slot,0x4000,frame.Length,frame);ushort b=(ushort)m.Profile.NetworkAdapters.Single(n=>n.Slot==slot).IoBase;
        m.Ports.Write(b,0x22,1);m.Ports.Write((ushort)(b+4),0x40,1);m.Ports.Write((ushort)(b+5),(byte)frame.Length,1);m.Ports.Write((ushort)(b+6),(byte)(frame.Length>>8),1);m.Ports.Write(b,0x26,1);
    }
    /// <summary>Compares complete RAM and every authoritative device/scheduler/CPU field without capture exclusions.</summary>
    private static void Same(PcMachine a,PcMachine b)
    {Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram),"whole RAM");Check(JsonSerializer.Serialize(a.CaptureHardwareState(),StateValues.Json)==JsonSerializer.Serialize(b.CaptureHardwareState(),StateValues.Json),"whole hardware and scheduler");}
    /// <summary>Own controlled peer; it never opens a socket, pipe, adapter or native library.</summary>
    private sealed class Peer(bool failDispose=false):IEthernetBackend
    {
        internal bool Closed;internal readonly Queue<byte[]> Frames=new();
        /// <summary>Identifies only this in-memory controlled Ethernet peer.</summary>
        public string Description=>"Own controlled Ethernet peer";
        /// <summary>Disconnects exactly when the controlled disposer has run.</summary>
        public bool IsConnected=>!Closed;
        /// <summary>Reports no asynchronous error from this synchronous peer.</summary>
        public string? Error=>null;
        /// <summary>Copies every complete transmitted frame into the controlled return queue.</summary>
        public void Send(ReadOnlySpan<byte> frame)=>Frames.Enqueue(frame.ToArray());
        /// <summary>Returns one previously copied frame without any native endpoint.</summary>
        public bool TryReceive(out byte[] frame){if(Frames.Count>0){frame=Frames.Dequeue();return true;}frame=[];return false;}
        /// <summary>Marks retirement before optionally reproducing a real close failure.</summary>
        public void Dispose(){Closed=true;if(failDispose)throw new IOException("Own controlled close failure");}
    }
    /// <summary>Registers packet/IRQ/topology/future/negative-import and strict networking-generation cases.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        short[] windows=[0,0x100,0x10,0x110,0x200,0x300,0x210,0x310];
        for(int bits=0;bits<8;bits++){int value=bits;test("Network Windows poll flags "+value,()=>Check(SlirpEthernetBackend.WindowsPollEvents(value)==windows[value],"Microsoft-supported readiness flags"));}
        foreach(var pair in new[]{(0x100,1),(0x10,2),(0x200,4),(1,8),(4,8),(2,16),(0x313,31)})
        {var row=pair;test("Network Windows poll results "+row.Item1,()=>Check(SlirpEthernetBackend.SlirpPollEvents((short)row.Item1)==row.Item2,"independent normal/urgent/error/hangup"));}
        foreach(bool pci in new[]{false,true})
        {
            test("Network external ARP padding reaches a normal non-runt NIC ring",()=>
        {
            using var f=new SnapshotTests.Fixture(Profile(false),false);Start(f.Machine,0);using var service=new NetworkService(f.Machine);var peer=new Peer();service.Attach(0,peer);
            byte[] arp=Packet(42);arp[12]=8;arp[13]=6;peer.Frames.Enqueue(arp);service.Pump();
            f.Machine.Scheduler.AdvanceTo(f.Machine.NetworkCards[0]!.ReceiveDeadline);
            byte[] ring=Dma(f.Machine,0,0x4700,68);Check(ring[0]==0x21&&(ring[2]|ring[3]<<8)==64,"physical padded receive count");
            Check(ring.AsSpan(4,42).SequenceEqual(arp)&&ring.AsSpan(46,18).IndexOfAnyExcept((byte)0)<0,"all source bytes and zero wire padding");
        });
        foreach(int cards in new[]{1,2})
            {
                int count=cards;
                test($"Network full board topology PCI={pci} cards={cards}",()=>
                {using var f=new SnapshotTests.Fixture(Profile(pci,count),false);var m=f.Machine;Configure(m);var state=m.CaptureHardwareState();for(int i=0;i<count;i++)Check(state.Components.ContainsKey("nic"+i),"each actual NIC node");Check(m.Pci?.FunctionCount==(pci?4+count:null),"exact PCI function count");});
            }
            test($"Network actual CPU PROM port transfer PCI={pci}",()=>
            {
                using var f=new SnapshotTests.Fixture(Profile(pci),false);var m=f.Machine;Configure(m);ushort b=(ushort)m.Profile.NetworkAdapters[0].IoBase;
                m.Ports.Write(b,0x21,1);m.Ports.Write((ushort)(b+14),9,1);m.Ports.Write((ushort)(b+8),0,1);m.Ports.Write((ushort)(b+9),0,1);m.Ports.Write((ushort)(b+10),2,1);m.Ports.Write((ushort)(b+11),0,1);m.Ports.Write(b,0x0a,1);
                byte[] code=[0xba,(byte)(b+16),(byte)((b+16)>>8),0xed,0xa3,0,0x50,0xf4];code.CopyTo(m.Memory.Ram,0x1000);m.RunSteps(20);
                Check(m.Cpu.Halted&&!m.Cpu.TripleFaulted&&m.Memory.ReadWord(0x5000)==(pci?0x4402:0x0202),"actual CPU IN AX and both PROM lanes");
            });
            foreach(string phase in new[]{"remote-half","transmit","receive"})
            {
                string cut=phase;
                test($"Network strict full future PCI={pci} phase={cut}",()=>
                {
                    using var f=new SnapshotTests.Fixture(Profile(pci),false);var a=f.Machine;Configure(a);Start(a,0);byte[] frame=Packet();
                    var sentA=new List<byte[]>();a.NetworkCards[0]!.Transmitted+=packet=>sentA.Add(packet);
                    if(cut=="transmit")Send(a,0,frame);
                    else if(cut=="receive")a.NetworkCards[0]!.QueueReceive(frame);
                    else {Dma(a,0,0x4000,frame.Length,frame);ushort p=(ushort)a.Profile.NetworkAdapters[0].IoBase;a.Ports.Write(p,0x22,1);a.Ports.Write((ushort)(p+8),0,1);a.Ports.Write((ushort)(p+9),0x40,1);a.Ports.Write((ushort)(p+10),7,1);a.Ports.Write((ushort)(p+11),0,1);a.Ports.Write(p,0x0a,1);a.Ports.Read((ushort)(p+16),1);}
                    using var b=f.Restore();var sentB=new List<byte[]>();b.NetworkCards[0]!.Transmitted+=packet=>sentB.Add(packet);Same(a,b);
                    ulong end=a.Scheduler.Now+1_000_000;a.Scheduler.AdvanceTo(end);b.Scheduler.AdvanceTo(end);Same(a,b);
                    if(cut=="transmit")Check(sentA.Count==1&&sentB.Count==1&&sentA[0].SequenceEqual(frame)&&sentB[0].SequenceEqual(frame),"whole later TX frame");
                    if(cut=="receive"){byte[] x=Dma(a,0,0x4700,frame.Length+8),y=Dma(b,0,0x4700,frame.Length+8);Check(x.SequenceEqual(y)&&x.AsSpan(4,frame.Length).SequenceEqual(frame),"entire received ring frame");Same(a,b);}
                    if(cut=="remote-half"){ushort p=(ushort)a.Profile.NetworkAdapters[0].IoBase;for(int i=0;i<3;i++)Check(a.Ports.Read((ushort)(p+16),2)==b.Ports.Read((ushort)(p+16),2),"retained remote latch future");Same(a,b);}
                });
            }
            test($"Network controlled peer routes complete frame through real device PCI={pci}",()=>
            {
                using var f=new SnapshotTests.Fixture(Profile(pci),false);var m=f.Machine;Configure(m);Start(m,0);using var service=new NetworkService(m);var peer=new Peer();service.Attach(0,peer);byte[] frame=Packet();Send(m,0,frame);m.Scheduler.AdvanceTo(m.NetworkCards[0]!.TransmitDeadline);service.Pump();m.Scheduler.AdvanceTo(m.NetworkCards[0]!.ReceiveDeadline);byte[] got=Dma(m,0,0x4700,frame.Length+8);Check(got.AsSpan(4,frame.Length).SequenceEqual(frame),"owner TX/backend/RX/ring payload");m.CaptureHardwareState();
            });
        }
        foreach(int cards in new[]{1,2})
        {
            int count=cards;
            test("Network real firmware assigns PCI cards "+count,()=>
            {using var m=PcMachine.CreateDemo(Profile(true,count));m.RunSteps(3000);Check(m.Fault is null,"firmware executes");for(int slot=0;slot<count;slot++)Check(m.NetworkPciCards[slot]!.IoBase==0x6000+slot*32&&m.NetworkPciCards[slot]!.IoSpaceEnabled,"real ROM BAR/IOSE OUTs");Check((m.PiixIrq!.Elcr&0x800)!=0,"real level IRQ11");m.CaptureHardwareState();});
        }
        test("Network two physical PCI INTAs share a real level IRQ until both sources acknowledge",()=>
        {
            using var f=new SnapshotTests.Fixture(Profile(true,2),false);var m=f.Machine;Configure(m);
            m.Ports.Write(0x20,0x11,1);m.Ports.Write(0x21,0x20,1);m.Ports.Write(0x21,4,1);m.Ports.Write(0x21,1,1);
            m.Ports.Write(0xa0,0x11,1);m.Ports.Write(0xa1,0x28,1);m.Ports.Write(0xa1,2,1);m.Ports.Write(0xa1,1,1);
            m.Ports.Write(0x21,0xfb,1);m.Ports.Write(0xa1,0xf7,1);m.PiixIrq!.WriteConfiguration(0x60,11,1);m.Ports.Write(0x4d1,8,1);
            for(int slot=0;slot<2;slot++)
            {
                Start(m,slot);ushort b=(ushort)m.Profile.NetworkAdapters.Single(n=>n.Slot==slot).IoBase;
                m.Ports.Write((ushort)(b+15),0x40,1);Dma(m,slot,0x4000,2,new byte[]{0x35,0x79});
            }
            Check(m.PiixIrq.PirqAssertions==1&&m.Pic.Acknowledge()==0x2b,"first actual IRQ11 acknowledgement");
            m.Ports.Write(0x6007,0x40,1);Check(m.PiixIrq.PirqAssertions==1,"second card retains shared wire");
            m.Ports.Write(0xa0,0x20,1);m.Ports.Write(0x20,0x20,1);Check(m.Pic.Acknowledge()==0x2b,"level reassertion after real EOIs");
            m.Ports.Write(0x6027,0x40,1);Check(m.PiixIrq.PirqAssertions==0,"both source acknowledgements release wire");
            m.Ports.Write(0xa0,0x20,1);m.Ports.Write(0x20,0x20,1);m.CaptureHardwareState();
        });
        test("Network failed old peer close leaves coherent disconnected slot",()=>
        {
            using var f=new SnapshotTests.Fixture(Profile(false),false);using var service=new NetworkService(f.Machine);var old=new Peer(true);service.Attach(0,old);var next=new Peer();bool rejected=false;
            try{service.Attach(0,next);}catch(IOException){rejected=true;}
            Check(rejected&&old.Closed&&!next.Closed&&!f.Machine.NetworkCards[0]!.CableConnected&&!f.Machine.Profile.NetworkAdapters[0].CableConnected,"no partially adopted backend");next.Dispose();
        });
        test("Network old controller cable request cannot activate replaced owner",()=>
        {
            using var session=new MachineSession(PcMachine.CreateDemo(Profile(false)));long old=session.InputGeneration;
            var power=session.Enqueue(IpcProtocol.CreateRequest("PowerCycleDemo",Profile(false)));
            var connect=session.Enqueue(IpcProtocol.CreateRequest("SetNetworkConnection",new{slot=0,connected=true}),old);session.PumpCommands();
            Check(power.Result.Success&&!connect.Result.Success&&connect.Result.ErrorCode=="StaleNetworkGeneration","retired network epoch");
        });
        test("Network failed owner retirement invalidates a prepared reconnect plan",()=>
        {
            using var session=new MachineSession(PcMachine.CreateDemo(Profile(false)));
            var original=session.Machine;
            var service=(NetworkService)typeof(MachineSession).GetField("_network",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
            service.Attach(0,new Peer(true));
            var prepare=session.Enqueue(IpcProtocol.CreateRequest("PrepareConfigurationChange",session.Machine.Profile));session.PumpCommands();
            Check(prepare.Result.Success,"prepared original cable-on plan");
            var plan=(ConfigurationPlan)prepare.Result.Payload!;long revision=session.Revision;
            var power=session.Enqueue(IpcProtocol.CreateRequest("PowerCycleDemo",Profile(false)));session.PumpCommands();
            Check(!power.Result.Success&&ReferenceEquals(session.Machine,original)&&session.Revision==revision+1&&!original.Profile.NetworkAdapters[0].CableConnected,"rollback retains old PC and advances changed configuration");
            var commit=session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange",plan));session.PumpCommands();
            Check(!commit.Result.Success&&commit.Result.ErrorCode=="StaleConfiguration"&&!session.Machine.Profile.NetworkAdapters[0].CableConnected,"old cable-on plan cannot reopen networking");
        });
        test("Network failed cable retirement invalidates an older reconnect plan",()=>
        {
            using var session=new MachineSession(PcMachine.CreateDemo(Profile(false)));
            var service=(NetworkService)typeof(MachineSession).GetField("_network",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
            service.Attach(0,new Peer(true));
            var prepare=session.Enqueue(IpcProtocol.CreateRequest("PrepareConfigurationChange",session.Machine.Profile));session.PumpCommands();
            var plan=(ConfigurationPlan)prepare.Result.Payload!;long revision=session.Revision;
            var close=session.Enqueue(IpcProtocol.CreateRequest("SetNetworkConnection",new{slot=0,connected=false}));session.PumpCommands();
            Check(!close.Result.Success&&session.Revision==revision+1&&!session.Machine.Profile.NetworkAdapters[0].CableConnected,"failed close publishes cable off and revision");
            var commit=session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange",plan));session.PumpCommands();
            Check(!commit.Result.Success&&commit.Result.ErrorCode=="StaleConfiguration","old plan cannot reconnect after cable close failure");
        });
        test("Network hard reset invalidates queued cable without changing keyboard generation",()=>
        {
            using var session=new MachineSession(PcMachine.CreateDemo(Profile(true)));long input=session.InputGeneration;
            var connect=session.Enqueue(IpcProtocol.CreateRequest("SetNetworkConnection",new{slot=0,connected=true}),input);
            session.Machine.Ports.Write(0xcf9,6,1);session.Machine.RunSteps(1);session.PumpCommands();
            Check(!connect.Result.Success&&connect.Result.ErrorCode=="StaleNetworkGeneration"&&session.InputGeneration==input,"network-only reset epoch");
        });
        test("Network structural profile equality avoids false cold restart",()=>
        {
            using var session=new MachineSession(PcMachine.CreateDemo(Profile(false)));var request=session.Enqueue(IpcProtocol.CreateRequest("PrepareConfigurationChange",Profile(false)));session.PumpCommands();
            var body=JsonSerializer.SerializeToElement(request.Result.Payload,IpcProtocol.JsonOptions);Check(request.Result.Success&&!body.GetProperty("requiresPowerCycle").GetBoolean(),"equivalent deserialized NIC lists");
        });
    }
}
