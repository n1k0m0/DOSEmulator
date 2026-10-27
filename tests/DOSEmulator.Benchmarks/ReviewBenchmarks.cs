// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class ReviewBenchmarks
{
    /// <summary>Executes the component's explicit runner with the supplied bounds and reports its resulting evidence.</summary>
    public static void Run(int repetitions)
    {
        var rows=new List<object>();ulong checksum=0;
        // Measures the explicitly delimited phase without including fixture construction in its timing window.
        void Measure(string name,int iterations,Func<Action<int>> setup)
        {
            setup()(Math.Min(iterations,10000));var samples=new List<double>();var allocations=new List<long>();var checks=new List<ulong>();
            for(int i=0;i<repetitions;i++)
            {
                var action=setup();GC.Collect();GC.WaitForPendingFinalizers();long bytes=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();action(iterations);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);allocations.Add(GC.GetAllocatedBytesForCurrentThread()-bytes);checks.Add(checksum);
            }
            if(checks.Distinct().Count()!=1)throw new Exception("Unstable benchmark output "+name);
            rows.Add(new{name,iterations,repetitions,milliseconds=samples,medianMilliseconds=samples.Order().ElementAt(samples.Count/2),allocatedBytes=allocations,checksum=checksum.ToString("X16")});
        }
        foreach(bool paging in new[]{false,true})
        {
            Measure("protected-register-loop"+(paging?"-paging":""),2_000_000,()=>
            {
                var m=Machine([0x83,0xc0,1,0x31,0xd0,0xeb,0xf9],paging);
                return n=>{for(int i=0;i<n;i++)m.Cpu.Step();checksum=m.Cpu.GetRegister(Register.Ax)^m.Cpu.Eip^m.Cpu.Eflags;m.Dispose();};
            });
            Measure("rep-movsd-machine"+(paging?"-paging":""),2_000_000,()=>
            {
                var m=Machine([0xb9,0,2,0,0,0xbe,0,0,0x20,0,0xbf,0,0,0x30,0,0xf3,0xa5,0xeb,0xed],paging);
                for(int i=0;i<4096;i++)m.Memory.Ram[0x200000+i]=(byte)(i*29+7);
                return n=>{m.RunSteps(n);checksum=m.Cpu.GetRegister(Register.Cx)^m.Cpu.Eip^m.Memory.ReadDword(0x300000)^(ulong)m.Cpu.Instructions;m.Dispose();};
            });
        }
        Measure("vga-chain4-byte-write",8_000_000,()=>
        {
            var v=new VgaAdapter();v.Write(0x3c4,0x0e04,2);v.Write(0x3c4,0x0f02,2);v.Write(0x3ce,0x4005,2);v.Write(0x3ce,0x0506,2);v.Write(0x3ce,0xff08,2);
            return n=>{for(int i=0;i<n;i++)v.WriteByte((uint)i&65535,(byte)(i*17>>7));checksum=(uint)(v.ReadByte(1234)^v.ReadByte(62999));};
        });
        Measure("pic-unchanged-line-and-pending",8_000_000,()=>
        {
            var p=new Pic8259();return n=>{ulong sum=0;for(int i=0;i<n;i++){p.SetLine(14,false);if(p.HasPending)sum++;}checksum=sum+p.Read(0xa1,1);};
        });
        Measure("scheduler-fixed-four-clocks",8_000_000,()=>
        {
            var c=new DeviceScheduler();int delivered=0;Action? tick=null;tick=()=>{delivered++;c.ScheduleAfter(1000000,tick!);};c.ScheduleAfter(1000000,tick);
            return n=>{for(int i=0;i<n;i++)c.AdvanceCycles(4,33000000);checksum=c.Now^(uint)delivered;};
        });
        Measure("audio-disabled-speaker",200,()=>
        {
            var c=new DeviceScheduler();var pit=new Pit8254(c,()=>{});var a=new PcAudioMixer(c);_ = new PcSpeaker(c,pit.SpeakerCounter,a);
            return n=>{for(int i=0;i<n;i++){c.AdvanceTo((ulong)(i+1)*100_000_000);a.AdvanceTo(c.Now);}checksum=(ulong)a.GeneratedFrames;};
        });
        Measure("unchanged-graphics-capture",1000,()=>
        {
            var m=PcMachine.CreateDemo();m.RunSteps(150000);m.Cpu.SetSegment(Segment.Cs,0);m.Cpu.Eip=0x1000;m.Memory.WriteByte(0x1000,0xf4);m.Cpu.SetRegister(Register.Ax,0x13);m.Cpu.EnterRealModeInterrupt(0x10);m.RunSteps(500000);
            Type? bufferType=typeof(VgaAdapter).Assembly.GetType("DOSEmulator.Hardware.VgaFrameBuffer");object? surface=bufferType is null?null:Activator.CreateInstance(bufferType);
            MethodInfo capture=typeof(PcMachine).GetMethod("CaptureFrame",surface is null?Type.EmptyTypes:[bufferType!])!;
            return n=>{ulong sum=0;for(int i=0;i<n;i++){var f=(VideoFrame)capture.Invoke(m,surface is null?null:[surface])!;sum+=f.ArgbPixels[0]+(uint)f.Width+(uint)f.Height;}checksum=sum;m.Dispose();};
        });
        Console.WriteLine(JsonSerializer.Serialize(new{runtime=RuntimeInformation.FrameworkDescription,processor=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),readyToRun=Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"),method="fixed work with setup and warmup outside measurements; capture uses reflection on both builds to preserve the old ABI",rows},new JsonSerializerOptions{WriteIndented=true}));
    }
    /// <summary>Builds an isolated machine fixture with the specified guest code and optional identity paging.</summary>
    private static PcMachine Machine(byte[] code,bool paging)
    {
        var m=PcMachine.CreateDemo();var c=m.Cpu;var ram=m.Memory;
        // Writes the explicit code/data descriptor required by the benchmark's protected-mode setup.
        void Desc(uint at,byte access){ram.WriteDword(at,0xffff);ram.WriteDword(at+4,0xcf0000u|((uint)access<<8));}
        Desc(0x408,0x9a);Desc(0x410,0x92);ram.WriteWord(0x300,0x17);ram.WriteDword(0x302,0x400);
        byte[] boot=[0x0f,0x01,0x16,0,3,0x66,0xb8,0x11,0,0,0x60,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
        boot.CopyTo(ram.Ram,0x1000);code.CopyTo(ram.Ram,0x2000);c.SetSegment(Segment.Cs,0);c.Eip=0x1000;for(int i=0;i<4;i++)c.Step();
        c.SetSegment(Segment.Ds,16);c.SetSegment(Segment.Es,16);c.SetSegment(Segment.Ss,16);c.SetRegister(Register.Sp,0x18000);c.Eflags=2;
        if(paging)
        {
            ram.WriteDword(0x1c000,0x1d007);for(uint i=0;i<1024;i++)ram.WriteDword(0x1d000+i*4,i*4096|7);
            c.SetRegister(Register.Ax,0x1c000);new byte[]{0x0f,0x22,0xd8}.CopyTo(ram.Ram,0x2200);c.Eip=0x2200;c.Step();
            c.SetRegister(Register.Ax,c.Cr0|0x80010000);new byte[]{0x0f,0x22,0xc0}.CopyTo(ram.Ram,0x2200);c.Eip=0x2200;c.Step();c.Eip=0x2000;
        }
        return m;
    }
}
