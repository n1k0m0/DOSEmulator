using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

// Fixed-work microbenchmarks, not historical CPU timing or a sampling profiler.
int scale = args.Length > 0 ? int.Parse(args[0]) : 1;
int repeats = args.Length > 1 ? int.Parse(args[1]) : 7;
if (scale < 1 || repeats < 3) throw new ArgumentOutOfRangeException();
var rows = new List<object>();
void Measure(string name, int iterations, Func<Action<int>> setup)
{
    setup()(Math.Min(iterations, 200_000)); // JIT/PGO warmup outside timed samples.
    var times = new List<double>();
    for (int i = 0; i < repeats; i++)
    {
        var action = setup(); GC.Collect(); GC.WaitForPendingFinalizers();
        long start = Stopwatch.GetTimestamp(); action(iterations);
        times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    double median = times.Order().ElementAt(times.Count / 2);
    rows.Add(new { name, iterations, repetitions = repeats, medianMilliseconds = median,
        operationsPerSecond = iterations * 1000.0 / median, milliseconds = times, checksum = State.Checksum.ToString("X16") });
}
MemoryBus Bus()
{
    var m = new MemoryBus(8 * 1024 * 1024);
    m.Map(0xf0000, 65536, new RomDevice(new byte[65536]), "ROM");
    m.Map(0xffff0000, 65536, new OpenBusMemory(), "reset alias");
    m.Map(0xffef0000, 65536, new OpenBusMemory(), "A20 reset alias");
    m.Map(0xa0000, 131072, new OpenBusMemory(), "VGA");
    m.Map(0xc0000, 196608, new OpenBusMemory(), "ISA");
    for (int i = 0; i < 65540; i++) m.Ram[0x200000 + i] = (byte)(i * 31 + 17);
    return m;
}
foreach (int width in new[] { 1, 2, 4 })
{
    Measure("ram-read-" + width, 8_000_000 * scale, () => { var m = Bus(); return n => {
        ulong sum = 0; for (int i = 0; i < n; i++) { uint a = 0x200000u + ((uint)i & 65535);
            sum += width == 1 ? m.ReadByte(a) : width == 2 ? m.ReadWord(a) : m.ReadDword(a); } State.Checksum = sum; }; });
    Measure("ram-write-" + width, 8_000_000 * scale, () => { var m = Bus(); return n => {
        for (int i = 0; i < n; i++) { uint a = 0x200000u + ((uint)i & 65535);
            if (width == 1) m.WriteByte(a, (byte)i); else if (width == 2) m.WriteWord(a, (ushort)i); else m.WriteDword(a, (uint)i); }
        State.Checksum = m.ReadDword(0x201234) ^ m.ReadDword(0x200000); }; });
}
foreach (int count in new[] { 0, 1, 7, 16, 31 })
{
    Measure("cpu-shift-cl-" + count, 1_000_000 * scale, () => {
        var m = Bus(); var c = new X86Cpu(m, new()); Setup(c,m,[0xd3,0xe0,0xd3,0xc8,0xd3,0xfa,0x83,0xc0,0x13,0xeb,0xf5]);
        c.SetRegister(Register.Cx,(uint)count); return n => { for(int i=0;i<n;i++) c.Step(); State.Checksum=c.GetRegister(Register.Ax)^c.GetRegister(Register.Dx)^c.Eflags; }; });
}
foreach(bool machine in new[]{false,true})
{
    Measure(machine?"machine-memory-loop":"cpu-memory-loop",1_000_000*scale,()=>{
        PcMachine? pc=machine?PcMachine.CreateDemo():null;var m=pc?.Memory??Bus();var c=pc?.Cpu??new X86Cpu(m,new());
        Setup(c,m,[0xa1,0,0,0x20,0,0x03,0xc2,0xa3,0,0,0x20,0,0xeb,0xf2]);
        return n=>{if(pc is null){for(int i=0;i<n;i++)c.Step();}else pc.RunSteps(n);
            State.Checksum=c.GetRegister(Register.Ax)^m.ReadDword(0x200000);pc?.Dispose();};
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
    processor=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),stopwatchFrequency=Stopwatch.Frequency,
    tieredCompilation=Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),readyToRun=Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"),
    fixedVirtualTimingUnchanged=true,profilingMethod="fixed-work stopwatch microbenchmarks",rows },new JsonSerializerOptions{WriteIndented=true}));

static void Setup(X86Cpu c,MemoryBus m,byte[] code)
{
    void Descriptor(uint at,byte access) {m.WriteDword(at,0xffff);m.WriteDword(at+4,0x00cf0000u|((uint)access<<8));}
    Descriptor(0x408,0x9a);Descriptor(0x410,0x92);m.WriteWord(0x300,0x17);m.WriteDword(0x302,0x400);
    byte[] boot=[0x0f,0x01,0x16,0,3,0x66,0xb8,0x11,0,0,0x60,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
    boot.CopyTo(m.Ram,0x1000);code.CopyTo(m.Ram,0x2000);c.SetSegment(Segment.Cs,0);c.Eip=0x1000;
    for(int i=0;i<4;i++)c.Step();
    if(c.Eip!=0x2000||!c.ProtectedMode)throw new Exception("Benchmark PE setup failed");
    c.SetSegment(Segment.Ds,16);c.SetSegment(Segment.Ss,16);c.SetRegister(Register.Sp,0x18000);
    c.SetRegister(Register.Ax,0x98fedcba);c.SetRegister(Register.Dx,0x87654321);c.Eflags=2;
}
static class State { public static ulong Checksum; }
