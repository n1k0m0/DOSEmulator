// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class SoundBlaster16Tests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string text = "SB16 contract failed") { if (!value) throw new Exception(text); }
    private sealed class Rig
    {
        public readonly MemoryBus Memory = new(2 * 1048576);
        public readonly DeviceScheduler Clock = new();
        public readonly Dma8237 Dma;
        public readonly PcAudioMixer Audio;
        public readonly SoundBlaster16 Card;
        public readonly List<SoundBlaster16.Sample> Samples = [];
        public readonly List<SoundBlaster16.Transfer> Starts = [];
        public readonly List<(int Irq, bool High)> Irqs = [];
        public readonly List<float> Pcm = [];
        /// <summary>Builds the isolated rig fixture and initializes its caller-owned test state.</summary>
        public Rig(int port = 0x220, int irq = 5, int dma8 = 1, int dma16 = 5)
        {
            Dma = new(Memory); Audio = new(Clock); Card = new(Clock, Dma, Audio, (line, high) => Irqs.Add((line, high)), (ushort)port, irq, dma8, dma16);
            Card.SampleTransferred += Samples.Add; Card.TransferStarted += Starts.Add; Audio.AudioReady += b => Pcm.AddRange(b.Samples);
        }
        /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
        public void Reset() { Card.Write((ushort)(Card.IoBasePort + 6), 1, 1); Advance(3000); Card.Write((ushort)(Card.IoBasePort + 6), 0, 1); Advance(100000); Check(Read(0xa) == 0xaa); }
        /// <summary>Advances the fixture clock and integrated mixer together to the requested virtual instant.</summary>
        public void Advance(ulong time) { Clock.AdvanceTo(Clock.Now + time); Audio.AdvanceTo(Clock.Now); }
        /// <summary>Sends the controlled test data through the real device-facing transfer interface.</summary>
        public void Send(params byte[] bytes) { foreach (byte b in bytes) { Check(Read(0xc) == 0); Card.Write((ushort)(Card.IoBasePort + 0xc), b, 1); Advance(1000); } }
        /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
        public uint Read(int offset) => Card.Read((ushort)(Card.IoBasePort + offset), 1);
        /// <summary>Writes an actual card mixer register through its address/data ports.</summary>
        public uint Mixer(byte register) { Card.Write((ushort)(Card.IoBasePort + 4), register, 1); return Read(5); }
        /// <summary>Writes an actual card mixer register through its address/data ports.</summary>
        public void Mixer(byte register, byte value) { Card.Write((ushort)(Card.IoBasePort + 4), register, 1); Card.Write((ushort)(Card.IoBasePort + 5), value, 1); }
        /// <summary>Programs the actual guest-visible DMA registers with the case's address, count, page and mode.</summary>
        public uint Program(bool word, ReadOnlySpan<byte> bytes, bool recording = false, bool auto = false, int? channel = null, ushort? countOverride = null)
        {
            int ch = channel ?? (word ? Card.Dma16Channel : Card.Dma8Channel), local = ch & 3;
            bool high = ch >= 5; ushort addressPort = (ushort)(high ? 0xc0 + local * 4 : local * 2);
            ushort flip = (ushort)(high ? 0xd8 : 0x0c), mask = (ushort)(high ? 0xd4 : 0x0a);
            uint physical = high ? 0x22000u : 0x12000u;
            bytes.CopyTo(Memory.Ram.AsSpan((int)physical));
            Dma.Write(mask, (uint)(4 | local), 1); Dma.Write(flip, 0, 1);
            ushort address = high ? (ushort)0x1000 : (ushort)0x2000;
            ushort count = countOverride ?? (ushort)(bytes.Length / (high ? 2 : 1) - 1);
            Dma.Write(addressPort, (byte)address, 1); Dma.Write(addressPort, (byte)(address >> 8), 1);
            ushort countPort = (ushort)(addressPort + (high ? 2 : 1));
            Dma.Write(countPort, (byte)count, 1); Dma.Write(countPort, (byte)(count >> 8), 1);
            ushort pagePort = ch switch { 0 => 0x87, 1 => 0x83, 3 => 0x82, 5 => 0x8b, 6 => 0x89, _ => 0x8a };
            Dma.Write(pagePort, high ? 2u : 1u, 1);
            Dma.Write((ushort)(high ? 0xd6 : 0x0b), (uint)(local | 0x40 | (recording ? 4 : 8) | (auto ? 0x10 : 0)), 1);
            Dma.Write(mask, (uint)local, 1); return physical;
        }
    }
    /// <summary>Registers the named sound blaster16 regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int port in new[] { 0x220, 0x240, 0x260, 0x280 })
        foreach (int irq in new[] { 5, 7, 9, 10 })
        foreach (int low in new[] { 0, 1, 3 })
        foreach (int high in new[] { 5, 6, 7 })
            test($"CT1740 resources A{port:X3} I{irq} D{low} H{high}", () =>
            {
                var profile = new MachineProfile { SoundCard = SoundBlaster16.ProfileId, SoundBlasterBasePort = port,
                    SoundBlasterIrq = irq, SoundBlasterDmaChannel = low, SoundBlasterDma16Channel = high }; profile.Validate();
                using var machine = PcMachine.CreateDemo(profile); var card = machine.SoundBlaster16!;
                Check(machine.SoundBlaster is null && machine.Opl is null);
                machine.Ports.Write((ushort)(port + 4), 0x80, 1);
                Check(machine.Ports.Read((ushort)(port + 5), 1) == (irq == 9 ? 1u : irq == 5 ? 2u : irq == 7 ? 4u : 8u));
                machine.Ports.Write((ushort)(port + 4), 0x81, 1);
                Check(machine.Ports.Read((ushort)(port + 5), 1) == (uint)((1 << low) | (1 << high)));
                Check(card.IoBasePort == port && profile.GetBlasterEnvironment() == $"A{port:X3} I{irq} D{low} H{high} T6");
                Check(machine.Opl3 is not null && machine.Ports.Read(0x388, 1) == 0 && machine.Ports.Read(0x330, 1) == 255);
            });
        foreach (bool word in new[] { false, true })
        foreach (bool stereo in new[] { false, true })
        foreach (bool signed in new[] { false, true })
        foreach (bool recording in new[] { false, true })
        foreach (bool auto in new[] { false, true })
        foreach (bool fifo in new[] { false, true })
            test($"DSP4 PCM {(word ? 16 : 8)} {(stereo ? "stereo" : "mono")} signed {signed} ADC {recording} auto {auto} FIFO flag {fifo}", () =>
            {
                var r = new Rig(); r.Reset(); r.Send(0x41,0x1f,0x40,0x42,0x1f,0x40);
                ushort[] raw = word ? [0, 0xffff, 0x1234, 0x8000, 0x7fff, 0x5678] : [0, 255, 128, 127, 33, 211];
                byte[] data = new byte[6 * (word ? 2 : 1)];
                for (int i = 0; i < 6; i++) { if (word) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i*2),raw[i]); else data[i]=(byte)raw[i]; }
                if (recording) Array.Fill<byte>(data,0xcc);
                r.Card.AdcInput = (_, right) => right ? (short)-12345 : (short)23456;
                uint physical = r.Program(word,data,recording,auto);
                byte command = (byte)((word ? 0xb0 : 0xc0) | (recording ? 8 : 0) | (auto ? 4 : 0) | (fifo ? 2 : 0));
                r.Send(command,(byte)((stereo?0x20:0)|(signed?0x10:0)),5,0);
                ulong start = r.Starts.Single().Time, blockTime = (ulong)(stereo?3:6)*125000;
                r.Clock.AdvanceTo(start+blockTime-1); Check(r.Card.CompletedBlocks==0);
                r.Clock.AdvanceTo(start+blockTime); Check(r.Card.CompletedBlocks==1 && r.Samples.Count==6);
                if (auto) { Check(r.Card.Active); r.Send(word?(byte)0xd9:(byte)0xda); r.Clock.AdvanceTo(start+blockTime*2); }
                Check(!r.Card.Active && r.Card.CompletedBlocks==(auto?2:1) && r.Samples.Count==(auto?12:6));
                Check((word?r.Card.Irq16Requests:r.Card.Irq8Requests)==(auto?2:1));
                Check(r.Card.InterruptSources==(word?2:1) && r.Irqs.SequenceEqual(new[]{(5,true)}));
                Check(r.Card.TransferredSamples==(recording?0:r.Samples.Count) && r.Card.RecordedSamples==(recording?r.Samples.Count:0));
                for (int i=0;i<r.Samples.Count;i++)
                {
                    var actual=r.Samples[i]; bool right=stereo&&(i&1)!=0;
                    int input=right?-12345:23456;
                    ushort expected=recording ? (word ? unchecked((ushort)(input^(signed?0:0x8000))) : unchecked((byte)((input>>8)+(signed?0:128)))) : raw[i%6];
                    Check(actual.Raw==expected && actual.Right==right && actual.Time==start+(ulong)(i/(stereo?2:1)+1)*125000);
                    int sample=word ? unchecked((short)(expected^(signed?0:0x8000))) : (signed?unchecked((sbyte)(byte)expected):(byte)expected-128)*256;
                    Check(actual.Value==sample);
                    if(recording)
                    {
                        int at=(int)physical+(i%6)*(word?2:1);
                        Check(word?r.Memory.ReadWord((uint)at)==expected:r.Memory.ReadByte((uint)at)==expected);
                    }
                }
                if(recording) Check(r.Card.DacLeft==0 && r.Card.DacRight==0);
                Check(r.Card.Dma16Grants==(word?r.Samples.Count:0) && r.Card.Dma8Grants==(word?0:r.Samples.Count));
                r.Read(word?0xf:0xe); Check(r.Card.InterruptSources==0 && r.Irqs.Last()==(5,false));
            });
        test("DSP4 reset readiness, busy framing, version 4.05 and DSP/mixer independence",()=>
        {
            var r=new Rig();Check(r.Read(0xc)==128);r.Card.Write(0x226,1,1);r.Advance(3000);r.Card.Write(0x226,0,1);
            r.Advance(99999);Check(r.Read(0xe)==0);r.Advance(1);Check(r.Read(0xe)==128 && r.Read(0xa)==0xaa);
            r.Send(0xe1);Check(r.Read(0xa)==4 && r.Read(0xa)==5 && r.Read(0xa)==255);
            r.Mixer(0x30,0xf8);r.Reset();Check(r.Mixer(0x30)==0xf8);
            r.Card.Write(0x22c,0x41,1);r.Card.Write(0x22c,0xff,1);Check(r.Card.RejectedWrites==1);
            r.Advance(1000);r.Reset();r.Send(0xe1);Check(r.Read(0xa)==4 && r.Read(0xa)==5);
        });
        test("SB16 separate IRQ8/IRQ16 latches, read-only status and wrong acknowledgment",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x41,0x1f,0x40);r.Program(false,[128]);r.Send(0xc0,0,0,0);r.Advance(125000);
            Check(r.Mixer(0x82)==1);r.Read(0xf);Check(r.Mixer(0x82)==1);r.Mixer(0x82,0);Check(r.Card.InterruptSources==1);
            r.Program(true,[0,0]);r.Send(0xb0,0x10,0,0);r.Advance(125000);Check(r.Mixer(0x82)==3);
            r.Read(0xe);Check(r.Mixer(0x82)==2 && r.Irqs.Count==1);r.Read(0xf);Check(r.Mixer(0x82)==0 && r.Irqs.Last()==(5,false));
        });
        foreach (int irq in new[] { 5, 7, 9, 10 })
        foreach (bool word in new[] { false, true })
            test($"SB16 software diagnostic IRQ width {word} on IRQ{irq} coalesces without fabricated PCM or DMA", () =>
            {
                var r = new Rig(irq: irq); r.Reset();
                byte command = word ? (byte)0xf3 : (byte)0xf2, bit = word ? (byte)2 : (byte)1;
                r.Send(command, command); Check(r.Mixer(0x82) == bit && r.Irqs.SequenceEqual(new[] { (irq, true) }));
                Check(r.Card.CompletedBlocks == 0 && r.Card.TransferredSamples == 0 && r.Card.RecordedSamples == 0 && r.Starts.Count == 0);
                Check(r.Card.Dma8Grants == 0 && r.Card.Dma16Grants == 0 && !r.Card.Active && r.Card.RemainingSamples == 0);
                Check((word ? r.Card.SoftwareIrq16Requests : r.Card.SoftwareIrq8Requests) == 2);
                r.Read(word ? 0xe : 0xf); r.Mixer(0x82, 0); Check(r.Mixer(0x82) == bit);
                r.Send(word ? (byte)0xf2 : (byte)0xf3); Check(r.Mixer(0x82) == 3 && r.Irqs.Count == 1);
                r.Read(word ? 0xf : 0xe); Check(r.Mixer(0x82) == (bit ^ 3) && r.Irqs.Count == 1);
                r.Read(word ? 0xe : 0xf); Check(r.Mixer(0x82) == 0 && r.Irqs.Last() == (irq, false));
                Check(r.Read(0xa) == 255 && r.Card.UnsupportedCommands == 0);
            });
        foreach (bool word in new[] { false, true })
            test($"SB16 software diagnostic keeps a pending/paused PCM width {word} separate from a completed block", () =>
            {
                var r = new Rig(); r.Reset(); r.Send(0x41, 0x1f, 0x40); r.Program(word, word ? new byte[] { 0x34, 0x12, 0x78, 0x56 } : new byte[] { 25, 210 });
                r.Send(word ? (byte)0xb0 : (byte)0xc0, 0x10, 1, 0); r.Send(word ? (byte)0xd5 : (byte)0xd0);
                int remaining = r.Card.RemainingSamples; r.Send(0xf2, 0xf3); r.Advance(250000);
                Check(r.Card.Active && r.Card.Paused && r.Card.RemainingSamples == remaining && r.Card.CompletedBlocks == 0 && r.Samples.Count == 0);
                r.Read(0xe); r.Read(0xf); r.Send(word ? (byte)0xd6 : (byte)0xd4); r.Advance(250000);
                Check(r.Card.CompletedBlocks == 1 && r.Samples.Count == 2 && r.Mixer(0x82) == (word ? 2 : 1));
                Check(r.Card.SoftwareIrq8Requests == 1 && r.Card.SoftwareIrq16Requests == 1 && r.Card.Irq8Requests + r.Card.Irq16Requests == 3);
            });
        test("SB16 reset cancels a queued diagnostic request and clears pending sources independently of PIC", () =>
        {
            var r = new Rig(); r.Reset(); r.Card.Write(0x22c, 0xf2, 1); r.Reset(); r.Advance(100000);
            Check(r.Card.SoftwareIrq8Requests == 0 && r.Card.InterruptSources == 0 && r.Irqs.Count == 0);
            r.Send(0xf2, 0xf3); r.Reset(); Check(r.Card.InterruptSources == 0 && r.Irqs.Last() == (5, false));
        });
        test("CT1745 absent CT1345 control 0Eh has no stereo/filter/gain/IRQ effects and remains diagnosed separately", () =>
        {
            var r = new Rig(); r.Reset(); r.Send(0xf2); double left = r.Card.LeftAnalogGain, right = r.Card.RightAnalogGain;
            for (int value = 0; value <= 255; value++)
            {
                r.Mixer(0x0e, (byte)value); Check(r.Mixer(0x0e) == 255);
                Check(r.Card.LeftAnalogGain == left && r.Card.RightAnalogGain == right && r.Card.InterruptSources == 1);
            }
            Check(r.Card.LegacyOutputControlAccesses == 512 && r.Card.UnsupportedMixerAccesses == 0);
            r.Mixer(0x0e, 253); r.Reset(); Check(r.Mixer(0x0e) == 255); r.Mixer(0, 0); Check(r.Mixer(0x0e) == 255);
            r.Mixer(0x7f, 1); Check(r.Mixer(0x7f) == 255 && r.Card.UnsupportedMixerAccesses == 2);
        });
        test("SB16 IRQ resource moves pending level; invalid multiple selections preserve resources",()=>
        {
            var r=new Rig();r.Reset();r.Program(true,[0,0]);r.Send(0xb0,0x10,0,0);r.Advance(50000);
            r.Mixer(0x80,8);Check(r.Card.Irq==10 && r.Irqs.SequenceEqual(new[]{(5,true),(5,false),(10,true)}));
            r.Mixer(0x80,0);Check(r.Card.Irq==-1 && r.Card.InterruptSources==2 && r.Irqs.Last()==(10,false));
            r.Mixer(0x80,1);Check(r.Irqs.Last()==(9,true));r.Mixer(0x80,3);Check(r.Card.Irq==9 && r.Card.RejectedResourceWrites==1);
            r.Mixer(0x81,0x63);Check(r.Card.Dma8Channel==1 && r.Card.Dma16Channel==5 && r.Card.RejectedResourceWrites==2);
        });
        foreach(bool recording in new[]{false,true})
            test($"SB16 16-bit fallback over DMA8 preserves half-word while masked, ADC {recording}",()=>
            {
                var r=new Rig();r.Reset();r.Send(0x41,0x1f,0x40,0x42,0x1f,0x40);r.Mixer(0x81,2);
                r.Card.AdcInput=(time,_)=>time<300000?(short)0x1234:(short)0x5678;
                uint p=r.Program(false,[0x34,0x12],recording,countOverride:0);r.Send((byte)(recording?0xb8:0xb0),0x10,0,0);
                ulong start=r.Starts.Last().Time;r.Clock.AdvanceTo(start+125000);
                Check(r.Card.Active && r.Card.Dma8Grants==1 && r.Samples.Count==0 && r.Card.Irq16Requests==0);
                r.Dma.Write(0x0c,0,1);r.Dma.Write(2,1,1);r.Dma.Write(2,0x20,1);r.Dma.Write(3,0,1);r.Dma.Write(3,0,1);r.Dma.Write(0x0a,1,1);
                r.Clock.AdvanceTo(start+250000);Check(!r.Card.Active && r.Card.Dma8Grants==2 && r.Samples.Single().Raw==0x1234);
                Check(r.Card.Irq16Requests==1 && r.Card.ConvertedSamples==(recording?1:0));
                if(recording)Check(r.Memory.ReadWord(p)==0x1234);
            });
        test("SB16 partial stereo frame waits for its right word without consuming the next left",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x41,0x1f,0x40);r.Program(true,[0x11,0x11,0x22,0x22],countOverride:0);
            r.Send(0xb0,0x30,1,0);ulong start=r.Starts.Single().Time;r.Clock.AdvanceTo(start+125000);
            Check(r.Samples.Count==1 && r.Card.RemainingSamples==1 && r.Card.DacLeft==0x1111 && r.Card.DacRight==0);
            r.Dma.Write(0xd8,0,1);r.Dma.Write(0xc4,1,1);r.Dma.Write(0xc4,0x10,1);r.Dma.Write(0xc6,0,1);r.Dma.Write(0xc6,0,1);r.Dma.Write(0xd4,1,1);
            r.Clock.AdvanceTo(start+250000);Check(r.Samples.Count==2 && r.Card.DacRight==0x2222 && !r.Card.Active);
        });
        test("SB16 pause/resume/exit commands affect only their format and preserve remaining deadline",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x41,0x1f,0x40);r.Program(true,[0,0,1,0],auto:true);r.Send(0xb4,0x10,1,0);
            r.Advance(50000);r.Send(0xd0);Check(!r.Card.Paused);r.Send(0xd5);Check(r.Card.Paused);
            ulong elapsed=r.Clock.Now-r.Starts.Single().Time;r.Advance(1000000);r.Send(0xd4);Check(r.Card.Paused);r.Send(0xd6);
            ulong resumed=r.Clock.Now;r.Clock.AdvanceTo(resumed+125000-elapsed-1);Check(r.Samples.Count==0);
            r.Clock.AdvanceTo(resumed+125000-elapsed);Check(r.Samples.Count==1);
            r.Send(0xda);r.Advance(125000);Check(r.Card.CompletedBlocks==1 && r.Card.Active);
            r.Send(0xd9);r.Advance(250000);Check(r.Card.CompletedBlocks==2 && !r.Card.Active);
        });
        foreach(int rate in new[]{5000,11025,22050,44100})
            test($"SB16 rational sample deadlines at {rate} Hz have no repeated period truncation",()=>
            {
                var r=new Rig();r.Reset();r.Send(0x41,(byte)(rate>>8),(byte)rate);r.Program(true,new byte[2048]);r.Send(0xb0,0x10,0xff,3);
                ulong start=r.Starts.Single().Time;r.Clock.AdvanceTo(start+1024_000_000_000UL/(uint)rate);
                Check(r.Samples.Count==1024 && !r.Card.Active);
                for(int i=0;i<r.Samples.Count;i++)Check(r.Samples[i].Time==start+(ulong)(i+1)*1_000_000_000/(uint)rate);
            });
        test("SB16 legacy time constant keeps exact microsecond periods and direct DAC is mono",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x40,129);r.Program(false,[0,128,255]);r.Send(0x14,2,0);ulong start=r.Starts.Single().Time;
            r.Clock.AdvanceTo(start+381000);Check(r.Samples.Count==3 && r.Samples[2].Time==start+381000);
            r.Program(true,[1,0,2,0]);r.Send(0xb0,0x30,1,0);r.Advance(254000);r.Send(0x10,0x90);
            Check(r.Card.DacLeft==4096 && r.Card.DacRight==4096);
        });
        test("SB16 invalid rate/mode packets retain prior stream and reset cancels queued transfer",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x41,0,0,0x42,0xff,0xff);Check(r.Card.RejectedParameters==2 && r.Card.OutputRate==22050);
            r.Program(true,[0,0]);r.Send(0xb1,0x10,0,0,0xb0,0x11,0,0);Check(!r.Card.Active && r.Card.RejectedParameters==4);
            r.Send(0xe1);Check(r.Read(0xa)==4 && r.Read(0xa)==5);r.Send(0xb0,0x10,0,0);r.Reset();r.Advance(1000000);
            Check(r.Samples.Count==0 && r.Card.InterruptSources==0 && !r.Card.Active && !r.Card.WordFormat && !r.Card.Stereo && !r.Card.FifoRequested);
        });
        test("CT1745 core volume masks, legacy aliases, nominal dB gain and reset are independent",()=>
        {
            var r=new Rig();Check(r.Mixer(0x30)==0xc0 && Math.Abs(r.Card.LeftAnalogGain-Math.Pow(10,-28/20.0))<1e-12);
            r.Mixer(0x30,0xff);r.Mixer(0x32,0xff);r.Mixer(0x31,0xf8);r.Mixer(0x33,0xe8);Check(r.Mixer(0x30)==0xf8);
            Check(Math.Abs(r.Card.LeftAnalogGain-1)<1e-12 && Math.Abs(r.Card.RightAnalogGain-Math.Pow(10,-4/20.0))<1e-12);
            r.Mixer(0x41,0xff);Check(r.Mixer(0x41)==0xc0 && r.Card.LeftAnalogGain>7.94);
            r.Mixer(4,0xf1);Check(r.Mixer(4)==0xf1 && r.Mixer(0x32)==0xf8 && r.Mixer(0x33)==0x18);
            r.Mixer(0x81,0x88);r.Mixer(0,123);Check(r.Mixer(0x81)==0x88 && r.Mixer(0x30)==0xc0 && r.Mixer(0x41)==0);
            Check(r.Mixer(0x44)==255 && r.Card.UnsupportedMixerAccesses==1);
        });
        test("DSP4 speaker commands change status only and do not mute PCM",()=>
        {
            var r=new Rig();r.Reset();r.Mixer(0x30,0xf8);r.Mixer(0x31,0xf8);r.Mixer(0x32,0xf8);r.Mixer(0x33,0xf8);
            r.Send(0xd3,0xd8);Check(r.Read(0xa)==0);r.Send(0x10,0xff);r.Advance(100000);r.Audio.Flush();Check(r.Pcm.Any(x=>x>0.4));
            r.Send(0xd1,0xd8);Check(r.Read(0xa)==255);
        });
        test("audio stereo routing and mid-host-sample guest gain integrate both time intervals",()=>
        {
            var clock=new DeviceScheduler();var audio=new PcAudioMixer(clock);var pcm=new List<float>();audio.AudioReady+=b=>pcm.AddRange(b.Samples);
            var (left,right)=audio.AddStereoSource((a,b)=>(Int128)(b-a)*PcAudioMixer.IntegralScale,(a,b)=>-(Int128)(b-a)*PcAudioMixer.IntegralScale,1);
            left.DeviceGain=0.5;clock.AdvanceTo(10000);left.DeviceGain=0.25;clock.AdvanceTo(20834);audio.Flush();
            double decay=Math.Exp(-2*Math.PI*20/48000),expected=(10000*0.5+10834*0.25)/20834*decay;
            Check(pcm.Count==2 && Math.Abs(pcm[0]-expected)<1e-7 && Math.Abs(pcm[1]+decay)<1e-7);
            right.Gain=0;Check(left.Gain==1 && right.Gain==0);
        });
        test("SB16 DSP FFFF length means 65536 words; count and IRQ are not byte counts",()=>
        {
            var r=new Rig();r.Reset();r.Send(0x41,0x1f,0x40);r.Program(true,new byte[131072]);r.Send(0xb0,0x10,255,255);
            r.Clock.AdvanceTo(r.Starts.Single().Time+65536UL*125000);
            Check(r.Card.TransferredSamples==65536 && r.Card.Dma16Grants==65536 && r.Card.CompletedBlocks==1 && !r.Card.Active);
        });
        test("physical PIC acknowledgment trace distinguishes IRQ5/GP and remapped/slave vectors",()=>
        {
            var pic=new Pic8259();var accepted=new List<(int,byte,bool)>();pic.InterruptAcknowledged+=(line,vector,spurious)=>accepted.Add((line,vector,spurious));
            pic.Write(0x21,0xfb,1);pic.Write(0xa1,0xfb,1);pic.SetLine(10,true);Check(pic.Acknowledge()==0x72);
            Check(accepted.SequenceEqual(new[]{(10,(byte)0x72,false)}) && pic.Acknowledge() is null);
            pic.Write(0xa0,0x20,1);pic.Write(0x20,0x20,1);
            pic.Write(0x20,0x11,1);pic.Write(0x21,0x50,1);pic.Write(0x21,4,1);pic.Write(0x21,1,1);pic.Write(0x21,0xdf,1);
            pic.SetLine(5,true);Check(pic.Acknowledge()==0x55 && accepted.Last()==(5,(byte)0x55,false));
        });
    }
}
