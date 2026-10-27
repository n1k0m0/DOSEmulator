// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class ReviewPipelineTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition,string detail="review pipeline invariant"){if(!condition)throw new Exception(detail);}
    private sealed class Surface {public int Ticket;public readonly int[] Pixels=new int[256];}
    /// <summary>Writes the controlled indexed VGA register through its actual port pair.</summary>
    private static void Register(VgaAdapter v,int index,int value)=>v.Write(0x3d4,(uint)(index|value<<8),2);
    /// <summary>Registers the named review pipeline regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("scheduler cached cycle conversion matches exact integer division across frequency and cycle changes",()=>
        {
            var c=new DeviceScheduler();ulong now=0,remainder=0;var rng=new Random(719);
            for(int i=0;i<200000;i++)
            {
                uint freq=i%73==0?(uint)rng.Next(1,100000001):33_000_000,cycles=i%41==0?(uint)rng.Next(0,100000):4;
                ulong numerator=(ulong)cycles*1_000_000_000+remainder;remainder=numerator%freq;now+=numerator/freq;c.AdvanceCycles(cycles,freq);
                Check(c.Now==now,"frequency remainder changed");
            }
        });
        test("scheduler cached event deadline retains cancellation, equal-time order and zero-delay nested events",()=>
        {
            var c=new DeviceScheduler();var log=new List<string>();long cancelled=c.ScheduleAfter(100,()=>log.Add("cancelled"));c.Cancel(cancelled);
            c.ScheduleAfter(100,()=>{log.Add("first:"+c.Now);c.ScheduleAfter(0,()=>log.Add("nested:"+c.Now));});c.ScheduleAfter(100,()=>log.Add("second:"+c.Now));
            Check(c.NextEventTime==100);c.AdvanceTo(99);Check(log.Count==0);c.AdvanceTo(101);
            Check(log.SequenceEqual(new[]{"first:100","second:100","nested:100"})&&c.NextEventTime is null&&c.Now==101);
        });
        test("scheduler REP budget never executes past a deadline and keeps fractional phase equal to single steps",()=>
        {
            foreach(uint frequency in new uint[]{3,1001,33_000_000,100_000_000})foreach(int lead in new[]{0,1,17,125,8191,1000000})
            {
                var a=new DeviceScheduler();var b=new DeviceScheduler();a.AdvanceCycles(7,frequency);b.AdvanceCycles(7,frequency);ulong stop=a.Now+(ulong)lead;
                int budget=a.StepBudget(4,frequency,4096,stop);a.AdvanceCycles((uint)budget*4,frequency);for(int i=0;i<budget;i++)b.AdvanceCycles(4,frequency);
                Check(a.Now==b.Now&&a.Now<=stop&&budget<=4096);a.AdvanceCycles(4,frequency);b.AdvanceCycles(4,frequency);Check(a.Now==b.Now);
            }
        });
        test("video exchange never overwrites any leased frame and returns buffers after idempotent release",()=>
        {
            var e=new LatestBufferExchange<Surface>(()=>new());
            // Publishes a fully filled fixture buffer with a distinct sequence/pixel pattern.
            void Publish(int n){Check(e.TryPublish(s=>{s.Ticket=n;Array.Fill(s.Pixels,n);}));}
            Publish(1);using var a=e.AcquireLatest()!;Publish(2);using var b=e.AcquireLatest()!;Publish(3);using var c=e.AcquireLatest()!;
            Check(!e.TryPublish(s=>s.Ticket=999));Check(a.Value.Pixels.All(x=>x==1)&&b.Value.Pixels.All(x=>x==2)&&c.Value.Pixels.All(x=>x==3));
            a.Dispose();a.Dispose();Publish(4);Check(b.Value.Pixels.All(x=>x==2)&&c.Value.Pixels.All(x=>x==3));
        });
        test("video exchange publishes complete frames under concurrent producer and slow consumer",()=>
        {
            var e=new LatestBufferExchange<Surface>(()=>new());bool done=false;int reads=0;
            var producer=Task.Run(()=>{try{for(int n=1;n<=30000;n++)e.TryPublish(s=>{s.Ticket=n;Array.Fill(s.Pixels,n);});}finally{Volatile.Write(ref done,true);}});
            var consumer=Task.Run(()=>{int old=0;do{using var lease=e.AcquireLatest();if(lease is null)continue;int n=lease.Value.Ticket;Thread.Yield();Check(n>=old&&lease.Value.Pixels.All(x=>x==n),"torn or reversed frame");old=n;reads++;}while(!Volatile.Read(ref done));});
            Task.WhenAll(producer,consumer).GetAwaiter().GetResult();Check(reads>0);
        });
        test("video exchange failed rendering preserves the last published frame",()=>
        {
            var e=new LatestBufferExchange<Surface>(()=>new());Check(e.TryPublish(s=>s.Ticket=5));
            try{e.TryPublish(s=>{s.Ticket=99;throw new InvalidOperationException();});throw new Exception("missing render error");}catch(InvalidOperationException){}
            using var read=e.AcquireLatest()!;Check(read.Value.Ticket==5);Check(e.TryPublish(s=>s.Ticket=6));
        });
        test("reusable VGA scanout matches fresh frames after RAM, font, cursor, blink, DAC, mode and blanking changes",()=>
        {
            var v=new VgaAdapter();var buffer=new VgaFrameBuffer();
            // Compares cached and independently freshly rendered scanouts at the same virtual instant.
            void Match(ulong time){var cached=v.Capture(time,buffer);var fresh=v.Capture(time);Check(cached.Width==fresh.Width&&cached.Height==fresh.Height&&cached.ModeGeneration==fresh.ModeGeneration&&cached.ArgbPixels.AsSpan().SequenceEqual(fresh.ArgbPixels));}
            Match(0);long rendered=buffer.RenderedFrames;uint[] pixels=buffer.Frame!.ArgbPixels;Match(1);Check(buffer.RenderedFrames==rendered&&ReferenceEquals(pixels,buffer.Frame!.ArgbPixels));
            v.WriteByte(0x18000,65);v.WriteByte(0x18001,0x9f);Register(v,10,14);Register(v,11,15);Match(0);Match(16*14_285_714UL);Match(32*14_285_714UL);
            v.Write(0x3c8,1,1);v.Write(0x3c9,63,1);Match(32*14_285_714UL);
            v.Write(0x3c4,1,1);v.Write(0x3c5,0x21,1);Match(100);v.Write(0x3c5,1,1);Match(100);
            v.Write(0x3c4,4,1);v.Write(0x3c5,14,1);v.Write(0x3ce,5,1);v.Write(0x3cf,0x40,1);v.Write(0x3ce,6,1);v.Write(0x3cf,5,1);Register(v,9,1);Register(v,1,79);Match(100);
            for(int i=0;i<500;i++)v.WriteByte((uint)i,(byte)i);Match(200);Match(33*14_285_714UL);
            var other=new VgaAdapter();var swapped=other.Capture(0,buffer);Check(swapped.ArgbPixels.AsSpan().SequenceEqual(other.Capture(0).ArgbPixels));
        });
        foreach(bool listener in new[]{false,true})
            test($"audio exact-silence path matches scalar samples, filter tails, gain transitions and timestamps, listener={listener}",()=>
            {
                var a=new DeviceScheduler();var b=new DeviceScheduler();var fast=new PcAudioMixer(a);var scalar=new PcAudioMixer(b);bool silent=true;
                // Returns the synthetic source's explicitly known signal area for the requested interval.
                Int128 Area(ulong start,ulong end)=>silent?0:(Int128)(end-start)*PcAudioMixer.IntegralScale/3;
                var gainA=fast.AddSource(Area,0.5,()=>silent);var gainB=scalar.AddSource(Area,0.5);var audioA=new List<AudioBlock>();var audioB=new List<AudioBlock>();
                if(listener){fast.AudioReady+=audioA.Add;scalar.AudioReady+=audioB.Add;}
                for(int tick=1;tick<=1000;tick++)
                {
                    ulong time=(ulong)tick*1_000_003;a.AdvanceTo(time);b.AdvanceTo(time);fast.AdvanceTo(time);scalar.AdvanceTo(time);
                    if(tick is 100 or 140 or 600 or 620)silent=!silent;
                    if(tick==110){gainA.Gain=0;gainB.Gain=0;}if(tick==130){gainA.Gain=1;gainB.Gain=1;}
                    if(tick%127==0){fast.Flush();scalar.Flush();}
                }
                fast.Flush();scalar.Flush();Check(fast.GeneratedFrames==scalar.GeneratedFrames&&fast.SilentFramesSkipped>1000&&audioA.Count==audioB.Count);
                for(int i=0;i<audioA.Count;i++)Check(audioA[i].VirtualNanoseconds==audioB[i].VirtualNanoseconds&&audioA[i].Samples.Select(BitConverter.SingleToInt32Bits).SequenceEqual(audioB[i].Samples.Select(BitConverter.SingleToInt32Bits)),"PCM/timestamp changed");
            });
        test("opaque silent-valued audio sources still execute their integrator",()=>
        {
            var clock=new DeviceScheduler();var mixer=new PcAudioMixer(clock);int calls=0;mixer.AddSource((_,_)=>{calls++;return 0;},1);clock.AdvanceTo(1_000_000);mixer.AdvanceTo(clock.Now);
            Check(calls==48&&mixer.GeneratedFrames==48&&mixer.SilentFramesSkipped==0);
        });
    }
}
