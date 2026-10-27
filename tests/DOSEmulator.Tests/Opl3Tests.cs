// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class Opl3Tests
{
    private static readonly int[] Offsets = [0,3,1,4,2,5,8,11,9,12,10,13,16,19,17,20,18,21];
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "YMF262 contract failed") { if (!value) throw new Exception(message); }
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly PcAudioMixer Audio;
        public readonly Opl3 Synth;
        public readonly List<Opl3.QuadSample> Native = [];
        public readonly List<float> Pcm = [];
        /// <summary>Builds the isolated rig fixture and initializes its caller-owned test state.</summary>
        public Rig() { Audio = new(Clock); Synth = new(Clock, Audio); Synth.SampleGenerated += Native.Add; Audio.AudioReady += b => Pcm.AddRange(b.Samples); }
        /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
        public void Write(int address, int value)
        { Synth.Write((ushort)(address >= 256 ? 0x38a : 0x388), (byte)address, 1); Synth.Write(0x389, (byte)value, 1); }
        /// <summary>Renders the current guest text/graphics state into a complete caller-owned pixel surface.</summary>
        public void Render(ulong time) { Clock.AdvanceTo(time); Audio.AdvanceTo(time); }
        /// <summary>Programs one explicit FM operator for the controlled synthesis/routing case.</summary>
        public void Operator(int slot, int level = 0, int wave = 0)
        {
            int offset = (slot >= 18 ? 256 : 0) + Offsets[slot % 18];
            Write(0x20 + offset, 0x21); Write(0x40 + offset, level); Write(0x60 + offset, 0xf0);
            Write(0x80 + offset, 0); Write(0xe0 + offset, wave);
        }
        /// <summary>Programs an explicit reference FM tone through its actual address/data ports.</summary>
        public void Tone(int channel, int routing = 3, int wave = 0)
        {
            Operator(channel * 2,63); Operator(channel * 2 + 1,0,wave);
            int bank = channel >= 9 ? 256 : 0, local = channel % 9;
            Write(bank+0xc0+local,routing<<4); Write(bank+0xa0+local,0x44); Write(bank+0xb0+local,0x32);
        }
    }
    /// <summary>Retrieves one explicitly named launch-option value or rejects a missing required argument.</summary>
    private static short Value(Opl3.QuadSample s, int output) => output switch { 0 => s.A, 1 => s.B, 2 => s.C, _ => s.D };
    /// <summary>Registers the named opl3 regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach (int channel in Enumerable.Range(0,18))
        foreach (int routing in Enumerable.Range(0,16))
            test($"OPL3 channel {channel} independently routes to ABCD mask {routing:X}",()=>
            {
                var r=new Rig();r.Write(0x105,1);r.Tone(channel,routing);r.Render(3_000_000);
                Check(r.Native.Count==149 && r.Synth.KeyOnCount==2);
                for(int output=0;output<4;output++)
                {
                    int peak=r.Native.Max(s=>Math.Abs((int)Value(s,output)));
                    Check((routing&(1<<output))==0?peak==0:peak>7000,$"output {output} peak {peak}");
                }
                Check(r.Synth.InspectOperator(channel,true).Keyed);
            });
        foreach(int pair in Enumerable.Range(0,6))
        foreach(int algorithm in Enumerable.Range(0,4))
        foreach(int solo in Enumerable.Range(0,4))
            test($"OPL3 4-op pair {pair} algorithm {algorithm} audible carrier {solo}",()=>
            {
                var r=new Rig();r.Write(0x105,1);r.Write(0x104,1<<pair);
                int primary=pair<3?pair:pair+6,bank=primary>=9?256:0,local=primary%9;
                int[] slots=[primary*2,primary*2+1,(primary+3)*2,(primary+3)*2+1];
                for(int op=0;op<4;op++)r.Operator(slots[op],op==solo?0:63);
                r.Write(bank+0xc0+local,0x10|((algorithm>>1)&1));r.Write(bank+0xc3+local,0x20|(algorithm&1));
                r.Write(bank+0xa0+local,0x44);r.Write(bank+0xb0+local,0x32);r.Render(30_000_000);
                Check(r.Synth.KeyOnCount==4);
                bool audible=algorithm switch{0=>solo==3,1=>solo is 1 or 3,2=>solo is 0 or 3,_=>solo is 0 or 2 or 3};
                int peak=r.Native.Skip(500).Max(s=>Math.Abs((int)s.A));Check(audible?peak>7000:peak<300,$"carrier peak {peak}");
                Check(r.Native.All(s=>s.B==0&&s.C==0&&s.D==0),"paired secondary pan leaked");
            });
        foreach(int wave in Enumerable.Range(0,8))
            test($"OPL3 waveform {wave} survives stored register and NEW changes",()=>
            {
                var r=new Rig();r.Write(0x105,1);r.Tone(0,1,wave);r.Render(3_000_000);
                var expected=r.Native.Select(s=>s.A).ToArray();Check(expected.Any(x=>x!=0));
                r.Write(0x105,0);r.Render(6_000_000);Check(r.Synth.InspectRegister(0xe3)==wave);
                r.Write(0x105,1);r.Write(0xe3,wave);
                Check(r.Synth.InspectOperator(0,true).Keyed && r.Synth.KeyOnCount==2);
            });
        test("OPL3 one nine-bit latch across both data ports and CT1740 aliases",()=>
        {
            var r=new Rig();Check(r.Synth.Read(0x388,1)==0&&r.Synth.Read(0x38a,1)==255);
            r.Synth.Write(0x222,5,1);r.Synth.Write(0x229,1,1);Check(r.Synth.NewMode);
            r.Synth.Write(0x38a,0x20,1);r.Synth.Write(0x221,0x21,1);Check(r.Synth.InspectRegister(0x120)==0x21);
            r.Synth.Write(0x228,0x20,1);r.Synth.Write(0x38b,0x22,1);Check(r.Synth.InspectRegister(0x20)==0x22);
            long before=r.Synth.RegisterWrites;r.Synth.Write(0x388,0x1234,2);r.Synth.Write(0x399,0,1);
            Check(r.Synth.RegisterWrites==before&&r.Synth.RejectedWrites==2);
        });
        test("YMF262 upper address aliases lower bank in compatibility mode except NEW register",()=>
        {
            var r=new Rig();r.Synth.Write(0x38a,0x20,1);r.Synth.Write(0x38b,0x21,1);
            Check(r.Synth.InspectRegister(0x20)==0x21&&r.Synth.InspectRegister(0x120)==0&&r.Synth.CompatibilityAliases==1);
            r.Write(0x105,1);r.Write(0x120,0x22);Check(r.Synth.InspectRegister(0x120)==0x22&&r.Synth.CompatibilityAliases==1);
            r.Write(0x105,0);r.Write(0x104,0x80);Check(r.Synth.InspectRegister(4)==0&&r.Synth.CompatibilityAliases==2);
        });
        foreach(int pair in Enumerable.Range(0,6))
            test($"OPL3 4-op pair {pair} primary frequency/key and independent secondary connection",()=>
            {
                var r=new Rig();r.Write(0x105,1);r.Write(0x104,1<<pair);
                int ch=pair<3?pair:pair+6,b=ch>=9?256:0,n=ch%9;
                foreach(int s in new[]{ch*2,ch*2+1,(ch+3)*2,(ch+3)*2+1})r.Operator(s);
                r.Write(b+0xa0+n,0x44);r.Write(b+0xb0+n,0x32);r.Render(1_000_000);
                uint p=r.Synth.InspectOperator(ch,true).Phase;Check(p==r.Synth.InspectOperator(ch+3,true).Phase&&r.Synth.KeyOnCount==4);
                r.Write(b+0xa3+n,0xff);r.Write(b+0xb3+n,0);r.Render(2_000_000);
                Check(r.Synth.InspectOperator(ch,true).Phase==r.Synth.InspectOperator(ch+3,true).Phase);
                Check(r.Synth.InspectOperator(ch+3,true).Keyed&&r.Synth.KeyOnCount==4);
                r.Write(b+0xb0+n,0x12);Check(!r.Synth.InspectOperator(ch+3,true).Keyed);
            });
        test("OPL3 timer clocks, mask/reset/reload and absence of CSM keys",()=>
        {
            var r=new Rig();r.Write(2,255);r.Write(3,255);r.Write(8,0x80);r.Write(4,3);
            ulong first=(1152_000_000_000UL+Opl3.MasterFrequency-1)/Opl3.MasterFrequency;
            r.Clock.AdvanceTo(first-1);Check(r.Synth.Timer1Overflows==0);r.Clock.AdvanceTo(first);
            Check(r.Synth.Timer1Overflows==1&&r.Synth.Read(0x388,1)==0xc0);
            r.Write(4,0x80);Check(r.Synth.Read(0x388,1)==0);r.Clock.AdvanceTo(first*4+4);
            Check(r.Synth.Timer2Overflows==1&&r.Synth.Read(0x388,1)==0xe0&&r.Synth.KeyOnCount==0&&r.Synth.UnsupportedWrites==1);
            r.Write(4,0x63);Check(!r.Synth.TimerInterruptAsserted);r.Clock.AdvanceTo(first*9);Check(!r.Synth.TimerInterruptAsserted);
            r.Write(4,0);long old=r.Synth.Timer1Overflows;r.Clock.AdvanceTo(first*20);Check(old==r.Synth.Timer1Overflows);
        });
        test("OPL3 native clock and stereo cached integration are independent of host render chunks",()=>
        {
            var a=new Rig();var b=new Rig();foreach(var r in new[]{a,b}){r.Write(0x105,1);r.Tone(0,1);r.Tone(9,2,2);}
            a.Render(50_000_000);for(ulong t=10000;t<=50_000_000;t+=10000)b.Render(t);
            a.Audio.Flush();b.Audio.Flush();Check(a.Native.SequenceEqual(b.Native)&&a.Pcm.SequenceEqual(b.Pcm));
            Check((ulong)a.Synth.GeneratedSamples==50_000_000UL*Opl3.MasterFrequency/(Opl3.ClocksPerSample*1_000_000_000UL));
            Check(a.Pcm.Where((_,i)=>(i&1)==0).Zip(a.Pcm.Where((_,i)=>(i&1)!=0)).Any(p=>p.First!=p.Second));
        });
        foreach(int drum in new[]{1,2,4,8,16})
            test($"OPL3 rhythm instrument {drum} keeps second bank melodic keys independent",()=>
            {
                var r=new Rig();r.Write(0x105,1);for(int s=12;s<18;s++)r.Operator(s);r.Tone(15,2);
                r.Write(0xc6,0x10);r.Write(0xc7,0x40);r.Write(0xc8,0x80);r.Write(0xa6,0x44);r.Write(0xb6,0x12);
                r.Write(0xa7,0x55);r.Write(0xb7,0x12);r.Write(0xa8,0x66);r.Write(0xb8,0x12);r.Write(0xbd,0x20|drum);
                Check(r.Synth.InspectOperator(15,true).Keyed);long keys=r.Synth.KeyOnCount;r.Write(0xbd,0x20|drum);Check(r.Synth.KeyOnCount==keys);
                r.Render(10_000_000);Check(r.Native.Any(s=>s.B!=0));r.Write(0xbd,0);Check(r.Synth.InspectOperator(15,true).Keyed);
            });
        test("CT1740 FM ports share ISA timing and DSP reset preserves live FM",()=>
        {
            using var m=PcMachine.CreateDemo(new MachineProfile{SoundCard=SoundBlaster16.ProfileId});
            Check(m.Opl3 is not null&&m.Opl is null);var fm=m.Opl3!;
            m.Ports.Write(0x222,5,1);m.Ports.Write(0x223,1,1);Check(fm.NewMode&&m.Scheduler.Now==1500);
            m.Ports.Write(0x388,0x20,1);m.Ports.Write(0x229,0x21,1);Check(fm.InspectRegister(0x20)==0x21&&m.FmIoTiming!.Accesses==4);
            m.Ports.Write(0x226,1,1);m.Scheduler.AdvanceTo(10000);m.Ports.Write(0x226,0,1);Check(fm.NewMode&&fm.InspectRegister(0x20)==0x21);
            Check(m.Pic.Read(0x20,1)==0);fm.Reset();Check(!fm.NewMode&&fm.InspectRegister(0x20)==0&&fm.FourOperatorMask==0);
        });
        test("CT1745 FM gains follow master/FM/output and remain separate from PCM and host level",()=>
        {
            var clock=new DeviceScheduler();var audio=new PcAudioMixer(clock);var card=new SoundBlaster16(clock,new(new MemoryBus(1048576)),audio,(_,_)=>{});
            var fm=new Opl3(clock,audio);card.AttachFm(fm);
            // Writes an actual card mixer register through its address/data ports.
            void Mixer(byte address,byte value){card.Write(0x224,address,1);card.Write(0x225,value,1);}
            double pcm=card.LeftAnalogGain;Mixer(0x34,0xf8);Check(card.LeftAnalogGain==pcm&&fm.LeftMixerGain>fm.RightMixerGain);
            Mixer(0x26,0xf1);card.Write(0x224,0x26,1);Check(card.Read(0x225,1)==0xf1);
            Mixer(0x30,0xf8);Mixer(0x34,0xf8);Check(Math.Abs(fm.LeftMixerGain-1)<1e-12);
            fm.Volume=0;Check(fm.LeftMixerGain==1&&fm.Volume==0);Mixer(0,0);Check(fm.LeftMixerGain<0.04);
        });
    }
}
