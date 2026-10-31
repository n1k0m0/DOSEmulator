// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

/// <summary>Real host-clock, stopped-owner and separately typed snapshot controls on the actual WHP API.</summary>
internal static class NativeRealtimeChecks
{
    /// <summary>Rejects artificial clock multipliers and proves that I/O work and halted waits consume real guest time.</summary>
    internal static void Run()
    {
        var memory = new MemoryBus(1 << 20);
        var ports = new IoPortBus(); var scheduler = new DeviceScheduler();
        ports.Map(0xe9, 1, new SlowPort());
        new byte[] { 0xb0, 1, 0xe6, 0xe9, 0xf4 }.CopyTo(memory.Ram.AsSpan(0x1000));
        using (var cpu = new WhpxMappedMachine(memory, ports, new Pic8259(), scheduler))
        {
            cpu.SetEntry(0, 0x1000);
            cpu.RunUntilHalt(TimeSpan.FromSeconds(5));
            Require(scheduler.Now >= 30_000_000, "Slow I/O was excluded from active elapsed guest time.");
        }
        bool scaled = false;
        try { using var invalid = new WhpxMappedMachine(memory, ports, new Pic8259(), new DeviceScheduler(), virtualTimeScale: 64); }
        catch (ArgumentOutOfRangeException) { scaled = true; }
        Require(scaled, "A synthetic WHP clock multiplier was accepted.");

        memory = new MemoryBus(1 << 20); scheduler = new DeviceScheduler();
        memory.Ram[0x1000] = 0xf4;
        int ticks = 0;
        // Counts and reschedules only the own finite 200-Hz timing oracle.
        void Tick() { ticks++; if (ticks < 40) scheduler.ScheduleAfter(5_000_000, Tick); }
        scheduler.ScheduleAfter(5_000_000, Tick);
        using (var cpu = new WhpxMappedMachine(memory, new IoPortBus(), new Pic8259(), scheduler))
        {
            cpu.SetEntry(0, 0x1000); var watch = Stopwatch.StartNew();
            cpu.RunUntilVirtualTime(200_000_000, TimeSpan.FromSeconds(5)); watch.Stop();
            Require(scheduler.Now == 200_000_000 && ticks == 40 && watch.Elapsed.TotalMilliseconds >= 190 && watch.Elapsed.TotalMilliseconds < 1000,
                "HLT skipped elapsed time or lost a scheduled device tick.");
        }
        Console.WriteLine("PASS: complete active I/O wall time, real HLT wait, 40 timed events and multiplier rejection.");
        CheckSharedRam();
        CheckPendingHaltWake(false);
        CheckPendingHaltWake(true);
        foreach (bool repeatFirst in new[] { false, true })
            foreach (bool backwards in new[] { false, true })
                foreach (int count in new[] { 1, 64 }) CheckWordPrefixOrder(repeatFirst, backwards, count);
        foreach(bool tsd in new[] { false,true }) foreach(ulong xmm in new ulong[] { 0,4 }) CheckUserTimestamp(tsd,xmm);
        NativeVideoBatchChecks.Run();
        foreach(bool code32 in new[] { false,true })foreach(bool input in new[] { false,true })foreach(int width in new[] {1,2,4})CheckScalarPort(code32,input,width);
        CheckOwnershipAndSnapshots();
    }

    /// <summary>Executes actual scalar IN/OUT encodings and checks exact port width, accumulator lanes, other registers, flags and complete RAM.</summary>
    private static void CheckScalarPort(bool code32,bool input,int width)
    {
        var memory=new MemoryBus(2<<20);var ports=new IoPortBus();var log=new ScalarPort();ports.Map(0xe9,1,log);
        var code=new List<byte>();
        if(width!=1&&((width==4)!=code32))code.Add(0x66);
        code.Add((byte)(input?(width==1?0xec:0xed):(width==1?0xee:0xef)));code.Add(0xf4);
        code.CopyTo(memory.Ram,0x1000);byte[] before=(byte[])memory.Ram.Clone();
        using var cpu=new WhpxMappedMachine(memory,ports,new Pic8259(),new DeviceScheduler());cpu.SetEntry(0,0x1000);
        var state=cpu.CaptureCpuState();var regs=state.Registers;
        // Constructs only explicit native fixture registers and descriptors.
        void Set(uint n,ulong low,ulong high=0)=>regs[Array.FindIndex(regs,r=>r.Name==n)]=new(n,low,high);
        if(code32)
        {
            Set(0x13,0,0xffffffffUL|(8UL<<32)|(0xc09bUL<<48));
            foreach(uint n in new uint[]{0x12,0x14,0x15})Set(n,0,0xffffffffUL|(0x10UL<<32)|(0xc093UL<<48));
            Set(0x1c,cpu.GetRegister(0x1c)|1);
        }
        for(uint n=0;n<8;n++)Set(n,0x10203040+n*0x111111);
        Set(0,0xa1b2c3d4);Set(2,0x667700e9);Set(0x11,0x897);
        cpu.RestoreCpuState(state with {Registers=regs});var expected=Enumerable.Range(0,8).Select(n=>cpu.GetRegister((uint)n)).ToArray();
        var run=cpu.RunUntilHalt(TimeSpan.FromSeconds(5));uint mask=width==4?uint.MaxValue:(1U<<(width*8))-1;
        if(input)expected[0]=(expected[0]&~mask)|(0x12345678U&mask);
        Require(run.IoExits==1&&log.Reads==(input?1:0)&&log.Writes==(input?0:1)&&log.Width==width&&
            (input||log.Value==(0xa1b2c3d4U&mask))&&before.AsSpan().SequenceEqual(memory.Ram)&&
            expected.SequenceEqual(Enumerable.Range(0,8).Select(n=>cpu.GetRegister((uint)n)))&&
            cpu.GetRegister(0x10)==0x1000UL+(uint)code.Count&&cpu.GetRegister(0x11)==0x897&&cpu.CaptureCpuState().Halted,
            "Scalar port completion changed width, lanes, non-target registers, flags, memory or instruction length.");
        Console.WriteLine($"PASS: native scalar {(input?"IN":"OUT")} {width*8}-bit data in {(code32?32:16)}-bit code, exact side effects and accumulator lanes.");
    }

    /// <summary>Records the actual scalar callback independently of the register-completion implementation.</summary>
    private sealed class ScalarPort:IIoPortDevice
    {
        internal int Reads,Writes,Width;internal uint Value;
        /// <summary>Returns a fixed full-width value for the independent accumulator-lane oracle.</summary>
        public uint Read(ushort port,int width){Require(port==0xe9,"Scalar input port changed.");Reads++;Width=width;return 0x12345678;}
        /// <summary>Retains the one actual port write and its original width.</summary>
        public void Write(ushort port,uint value,int width){Require(port==0xe9,"Scalar output port changed.");Writes++;Value=value;Width=width;}
    }

    /// <summary>Runs real CPL3 RDTSC with independent CR4.TSD and XMM3 values, ending in own hardware-switched ring-zero handlers.</summary>
    private static void CheckUserTimestamp(bool tsd,ulong xmm)
    {
        var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();
        // Stores one own flat segment/TSS descriptor in the actual guest GDT.
        void Descriptor(int slot,uint baseAddress,uint limit,byte access,byte flags)
        {
            ulong value=(limit&65535UL)|((ulong)(baseAddress&0xffff)<<16)|((ulong)((baseAddress>>16)&255)<<32)|
                ((ulong)access<<40)|((ulong)((limit>>16)&15)<<48)|((ulong)(flags&0xf0)<<48)|((ulong)(baseAddress>>24)<<56);
            BitConverter.GetBytes(value).CopyTo(memory.Ram,0x6000+slot*8);
        }
        Descriptor(1,0,0xfffff,0x9a,0xc0);Descriptor(2,0,0xfffff,0x92,0xc0);
        Descriptor(3,0,0xfffff,0xfa,0xc0);Descriptor(4,0,0xfffff,0xf2,0xc0);Descriptor(5,0x7000,0x67,0x89,0);
        BitConverter.GetBytes(0x9000U).CopyTo(memory.Ram,0x7004);memory.Ram[0x7008]=0x10;memory.Ram[0x7066]=0x68;
        // Installs a real present 32-bit IDT gate at the requested privilege level.
        void Gate(int vector,uint address,byte attributes)
        {
            ulong value=(address&65535UL)|(8UL<<16)|((ulong)attributes<<40)|((ulong)(address>>16)<<48);
            BitConverter.GetBytes(value).CopyTo(memory.Ram,0x8000+vector*8);
        }
        Gate(13,0x3000,0x8e);Gate(0x20,0x3100,0xee);
        new byte[]{0x0f,0x31,0xa3,0,0x40,0,0,0x89,0x15,4,0x40,0,0,0xcd,0x20,0x0f,0x0b}.CopyTo(memory.Ram,0x1000);
        new byte[]{0xc6,5,8,0x40,0,0,13,0xf4}.CopyTo(memory.Ram,0x3000);
        new byte[]{0xc6,5,8,0x40,0,0,0x20,0xf4}.CopyTo(memory.Ram,0x3100);
        using var cpu=new WhpxMappedMachine(memory,new IoPortBus(),new Pic8259(),clock,WhpxCpuContract.IntelP54cB5Subset,100_000_000);
        cpu.SetEntry(0,0x1000);var state=cpu.CaptureCpuState();var regs=state.Registers;
        // Replaces only the explicitly constructed native fixture register.
        void Set(uint name,ulong low,ulong high=0)=>regs[Array.FindIndex(regs,r=>r.Name==name)]=new(name,low,high);
        Set(0x13,0,0xffffffffUL|(0x1bUL<<32)|(0xc0fbUL<<48));
        foreach(uint name in new uint[]{0x12,0x14,0x15,0x16,0x17})Set(name,0,0xffffffffUL|(0x23UL<<32)|(0xc0f3UL<<48));
        Set(0x19,0x7000,0x67UL|(0x28UL<<32)|(0x8bUL<<48));Set(0x1a,0x7ffUL<<48,0x8000);Set(0x1b,47UL<<48,0x6000);
        Set(0x1c,cpu.GetRegister(0x1c)|1);Set(0x1f,tsd?4UL:0);Set(4,0xa000);Set(0x1003,xmm);
        cpu.RestoreCpuState(state with { Registers=regs });var run=cpu.RunUntilHalt(TimeSpan.FromSeconds(5));
        ulong timestamp=BitConverter.ToUInt64(memory.Ram,0x4000);
        Require(cpu.CaptureCpuState().Halted&&memory.Ram[0x4008]==(tsd?13:0x20)&&
            cpu.GetRegister(0x10)==(tsd?0x3008UL:0x3108UL)&&
            (tsd?timestamp==0:timestamp>0&&timestamp<=clock.ClockTicks(100_000_000))&&run.PicInterrupts==0,
            "CPL3 timestamp permission used XMM storage instead of CR4.TSD, or its real privilege-switch handler differs.");
        Console.WriteLine($"PASS: actual CPL3 RDTSC TSD={(tsd?1:0)}, XMM3={xmm}, native IDT/TSS ring-zero {(tsd?"#GP":"completion")} handler.");
    }

    /// <summary>Runs both legal ES/REP prefix orders through a real 16-bit protected OUTSW, including DF and high index/count bits.</summary>
    private static void CheckWordPrefixOrder(bool repeatFirst, bool backwards, int count)
    {
        var memory = new MemoryBus(2 << 20); var ports = new IoPortBus(); var output = new WordLog();
        ports.Map(0xe2e8, 2, output);
        byte[] code = repeatFirst ? [0xf3, 0x26, 0x6f, 0xf4] : [0x26, 0xf3, 0x6f, 0xf4];
        code.CopyTo(memory.Ram.AsSpan(0x1000));
        int first = backwards ? 0x582 + (count - 1) * 2 : 0x582;
        var expected = new List<uint>();
        for (int index = 0; index < count; index++)
        {
            ushort value = (ushort)(0x8a00 + index * 31); expected.Add(value);
            BitConverter.GetBytes(value).CopyTo(memory.Ram.AsSpan(0x1a80 + first + (backwards ? -2 : 2) * index));
        }
        byte[] before = (byte[])memory.Ram.Clone();
        using var cpu = new WhpxMappedMachine(memory, ports, new Pic8259(), new DeviceScheduler());
        cpu.SetEntry(0, 0x1000); var state = cpu.CaptureCpuState(); var registers = state.Registers;
        // Sets only the actual native fixture registers; DS deliberately addresses different source bytes.
        void Set(uint name, ulong low, ulong high = 0) => registers[Array.FindIndex(registers, r => r.Name == name)] = new(name, low, high);
        Set(0x13, 0, 0xffffUL | (8UL << 32) | (0x9bUL << 48));
        Set(0x15, 0, 0xffffUL | (0x10UL << 32) | (0x93UL << 48));
        Set(0x12, 0x1a80, 0x1000UL | (0x18UL << 32) | (0x93UL << 48));
        Set(0x14, 0, 0xffffUL | (0x10UL << 32) | (0x93UL << 48));
        Set(0x1c, cpu.GetRegister(0x1c) | 1);
        Set(6, 0x12340000UL | (uint)first); Set(7, 0x76543210); Set(1, 0xabcd0000UL | (uint)count);
        Set(2, 0x55e2e8); Set(0x11, backwards ? 0x402UL : 2UL);
        cpu.RestoreCpuState(state with { Registers = registers });
        var run = cpu.RunUntilHalt(TimeSpan.FromSeconds(5));
        ulong next = 0x12340000UL | unchecked((ushort)(first + (backwards ? -2 : 2) * count));
        Require(run.IoExits == 1 && output.Values.SequenceEqual(expected) && memory.Ram.AsSpan().SequenceEqual(before) &&
            cpu.GetRegister(6) == next && cpu.GetRegister(7) == 0x76543210 && cpu.GetRegister(1) == 0xabcd0000 &&
            cpu.GetRegister(0x10) == 0x1004 && cpu.CaptureCpuState().Halted,
            "ES/REP order, word values, RAM purity or preserved protected-mode index/count bits differ.");
        Console.WriteLine($"PASS: actual {(repeatFirst ? "REP ES" : "ES REP")} OUTSW, DF={(backwards ? 1 : 0)}, {count} ordered port words and complete RAM preservation.");
    }

    /// <summary>Records every real port word independently of the fallback decoder.</summary>
    private sealed class WordLog : IIoPortDevice
    {
        internal readonly List<uint> Values = [];
        /// <summary>Returns a fixed unused fixture response.</summary>
        public uint Read(ushort port, int size) => 0;
        /// <summary>Rejects a narrowed port operation and retains each original word.</summary>
        public void Write(ushort port, uint value, int size)
        { Require(port == 0xe2e8 && size == 2, "OUTSW changed the actual port or width."); Values.Add(value); }
    }

    /// <summary>Requires an IRQ queued between owner calls to wake a live or restored HLT before the native pending event can be delivered.</summary>
    private static void CheckPendingHaltWake(bool restore)
    {
        var memory = new MemoryBus(2 << 20); var pic = new Pic8259(); var ports = new IoPortBus(); var clock = new DeviceScheduler();
        ports.Map(0x20, 2, pic); pic.Write(0x21, 0xfe, 1);
        memory.Ram[0x20] = 0; memory.Ram[0x21] = 0x30;
        new byte[] { 0xfb, 0xf4, 0xf4 }.CopyTo(memory.Ram.AsSpan(0x1000));
        new byte[] { 0xc6, 0x06, 0, 0x21, 0x5a, 0xb0, 0x20, 0xe6, 0x20, 0xcf }.CopyTo(memory.Ram.AsSpan(0x3000));
        using var original = new WhpxMappedMachine(memory, ports, pic, clock);
        original.SetEntry(0, 0x1000); original.RunUntilHalt(TimeSpan.FromSeconds(5));
        var stopped = original.CaptureCpuState(); Require(stopped.Halted, "Own first HLT was not retained.");
        if (!restore)
        {
            pic.SetLine(0, true); pic.SetLine(0, false);
            original.RunUntilVirtualTime(clock.Now + 50_000_000, TimeSpan.FromSeconds(5));
            Require(memory.Ram[0x2100] == 0x5a && original.CaptureCpuState().Halted,
                "An IRQ queued at owner entry remained blocked behind the cached HLT state.");
        }
        else
        {
            original.Dispose();
            using var resumed = new WhpxMappedMachine(memory, ports, pic, clock);
            resumed.RestoreCpuState(stopped); pic.SetLine(0, true); pic.SetLine(0, false);
            resumed.RunUntilVirtualTime(clock.Now + 50_000_000, TimeSpan.FromSeconds(5));
            Require(memory.Ram[0x2100] == 0x5a && resumed.CaptureCpuState().Halted,
                "A stopped HLT checkpoint lost an IRQ queued before the first resumed owner call.");
        }
        Console.WriteLine("PASS: native IRQ queued before " + (restore ? "restored" : "live") + " HLT owner call reached and returned from its real guest handler.");
    }

    /// <summary>Requires a timed device to see a native CPU store before copying its DMA response back to that same CPU.</summary>
    private static void CheckSharedRam()
    {
        var memory = new MemoryBus(2 << 20); var ports = new IoPortBus(); var pic = new Pic8259(); var clock = new DeviceScheduler();
        ports.Map(0x20, 2, pic); pic.Write(0x21, 0xfe, 1);
        memory.Ram[0x20] = 0; memory.Ram[0x21] = 0x30;
        new byte[] { 0xfb, 0xc6, 0x06, 0, 0x22, 0x6a, 0xf4, 0xa0, 0, 0x23, 0xa2, 1, 0x23, 0xf4 }.CopyTo(memory.Ram.AsSpan(0x1000));
        new byte[] { 0xb0, 0x20, 0xe6, 0x20, 0xcf }.CopyTo(memory.Ram.AsSpan(0x3000));
        bool deviceObserved = false;
        clock.ScheduleAfter(10_000_000, () =>
        {
            Require(memory.HasExternalRam && memory.ReadByte(0x2200) == 0x6a, "A real scheduled device read stale managed RAM.");
            Require(memory.TryGetPhysicalRamSpan(0x2300, 1, null, out var dma), "Own DMA range was rejected.");
            dma[0] = 0x37; deviceObserved = true; pic.SetLine(0, true); pic.SetLine(0, false);
        });
        using (var cpu = new WhpxMappedMachine(memory, ports, pic, clock))
        {
            cpu.SetEntry(0, 0x1000); cpu.RunUntilVirtualTime(20_000_000, TimeSpan.FromSeconds(5));
            Require(deviceObserved && !memory.HasExternalRam && memory.Ram[0x2200] == 0x6a && memory.Ram[0x2301] == 0x37,
                "Native CPU/device DMA coherence or complete stopped mirror differs.");
        }
        Console.WriteLine("PASS: native store observed by real timed device, shared DMA response read by native CPU, full stopped mirror and lease release.");
    }

    /// <summary>Uses only an own demo PC and new private paths to prove owner separation and full native restore.</summary>
    private static void CheckOwnershipAndSnapshots()
    {
        string parent = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string root = Path.Combine(parent, "whpx-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var used = PcMachine.CreateDemo())
            {
                used.RunSteps(1);
                RequireThrows<InvalidOperationException>(() => { using var invalid = WhpxPcSession.Start(used); });
                Require(used.Cpu.Instructions == 1, "Native admission reset an already executing software CPU.");
            }
            using var pc = PcMachine.CreateDemo();
            using var session = WhpxPcSession.Start(pc);
            string wrong = Path.Combine(root, "wrong.dossav");
            RequireThrows<InvalidOperationException>(() => SnapshotStore.Capture(pc, wrong));
            Require(!Directory.Exists(wrong), "Software capture created a target for a native CPU.");
            int observations = 0;
            var run = session.RunRealtime(TimeSpan.FromMilliseconds(200), _ => { observations++; Thread.Sleep(12); });
            Require(run.EndedAt - run.StartedAt == 200_000_000 && observations > 0 && run.HostNanoseconds >= 190_000_000 && run.HostNanoseconds < 1_000_000_000,
                "Continuous observations were excluded from the shared active clock origin.");
            string saved = Path.Combine(root, "own.whpxsav");
            var cut = session.Capture(); WhpxSnapshotStore.Capture(session, saved);
            using (var fresh = PcMachine.CreateDemo())
            {
                var before = fresh.CaptureHardwareState(); byte[] beforeRam = (byte[])fresh.Memory.Ram.Clone();
                var bad = cut with { Cpu = cut.Cpu with { Registers = cut.Cpu.Registers[..^1] } };
                RequireThrows<InvalidDataException>(() => { using var invalid = WhpxPcSession.Resume(fresh, bad); });
                Require(beforeRam.AsSpan().SequenceEqual(fresh.Memory.Ram) &&
                    JsonSerializer.Serialize(before, StateValues.Json) == JsonSerializer.Serialize(fresh.CaptureHardwareState(), StateValues.Json),
                    "An invalid native resume modified the fresh PC before validation.");
            }
            string nested = Path.Combine(root, "nested.dosebranch");
            RequireThrows<InvalidDataException>(() => { using var invalid = SnapshotStore.Load(Path.Combine(saved, "hardware.dossav"), nested); });
            Require(!Directory.Exists(nested), "Native backing was loaded as software before branching.");
            session.Dispose();
            RequireThrows<InvalidOperationException>(() => pc.RunSteps(1));
            RequireThrows<InvalidOperationException>(() => { using var invalid = WhpxPcSession.Start(pc); });
            RequireThrows<InvalidOperationException>(() => SnapshotStore.Capture(pc, wrong));
            using (var loaded = WhpxSnapshotStore.Load(saved, Path.Combine(root, "own.dosebranch")))
            {
                var restored = loaded.Capture();
                string beforeHardware = JsonSerializer.Serialize(cut.Hardware, StateValues.Json), afterHardware = JsonSerializer.Serialize(restored.Hardware, StateValues.Json);
                string beforeCpu = JsonSerializer.Serialize(cut.Cpu), afterCpu = JsonSerializer.Serialize(restored.Cpu);
                int firstHardware = Enumerable.Range(0, Math.Min(beforeHardware.Length, afterHardware.Length)).FirstOrDefault(i => beforeHardware[i] != afterHardware[i], -1);
                Require(cut.Ram.AsSpan().SequenceEqual(restored.Ram) && beforeCpu == afterCpu && beforeHardware == afterHardware,
                    "The complete native cut differs: RAM=" + cut.Ram.AsSpan().SequenceEqual(restored.Ram) +
                    "; CPU=" + (beforeCpu == afterCpu ? "equal" : string.Join(';', cut.Cpu.Registers.Zip(restored.Cpu.Registers).Where(p => p.First != p.Second).Select(p => $"{p.First.Name:X}:{p.First.Low:X}/{p.First.High:X}->{p.Second.Low:X}/{p.Second.High:X}"))) +
                    "; hardware offset=" + firstHardware + (firstHardware < 0 ? "" : "; before=" + beforeHardware.Substring(Math.Max(0, firstHardware - 60), Math.Min(180, beforeHardware.Length - Math.Max(0, firstHardware - 60))) + "; after=" + afterHardware.Substring(Math.Max(0, firstHardware - 60), Math.Min(180, afterHardware.Length - Math.Max(0, firstHardware - 60)))));
            }
            string cpuPath = Path.Combine(saved, "cpu.json");
            var malformed = cut.Cpu with { Registers = cut.Cpu.Registers[..^1] };
            byte[] badCpu = JsonSerializer.SerializeToUtf8Bytes(malformed);
            File.WriteAllBytes(cpuPath, badCpu);
            string manifestPath = Path.Combine(saved, "whpx.json");
            var manifest = JsonSerializer.Deserialize<WhpxSnapshotManifest>(File.ReadAllBytes(manifestPath))!;
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { CpuSha256 = Convert.ToHexString(SHA256.HashData(badCpu)) }));
            string invalidBranch = Path.Combine(root, "invalid.dosebranch");
            RequireThrows<InvalidDataException>(() => { using var invalid = WhpxSnapshotStore.Load(saved, invalidBranch); });
            Require(!Directory.Exists(invalidBranch), "A correctly hashed invalid native register topology created a branch.");
            Console.WriteLine("PASS: continuous callback clock, full WHP snapshot equality, typed backing, detached CPU and pre-branch topology rejection.");
        }
        finally
        {
            Require(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unexpected owned test root.");
            Directory.Delete(root, true);
        }
    }

    /// <summary>Fails with the independent behavioral oracle that was not satisfied.</summary>
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Requires the specified failure rather than treating any exception as an accepted negative control.</summary>
    private static void RequireThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    /// <summary>A slow actual port callback detects clocks which account only for WHvRunVirtualProcessor.</summary>
    private sealed class SlowPort : IIoPortDevice
    {
        /// <summary>Returns the own slow-port fixture's fixed response.</summary>
        public uint Read(ushort port, int size) => 0;
        /// <summary>Delays one actual own port callback to detect omitted host processing time.</summary>
        public void Write(ushort port, uint value, int size) => Thread.Sleep(35);
    }
}
