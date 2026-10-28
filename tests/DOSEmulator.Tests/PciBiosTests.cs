// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual own PCI-BIOS ROM calls, protected BIOS32 entry and independently specified board/resource effects.</summary>
internal static class PciBiosTests
{
    /// <summary>Actual real, simulated-INT, V86 and protected calling environments.</summary>
    private enum Mode { Real, Fixed16, V86, Pe16, Flat32, Rom32 }
    /// <summary>One actual completed bus access, never an emulated BIOS return.</summary>
    private sealed record Port(ushort Address, uint Value, int Width, bool Write);
    /// <summary>Actual caller state observed before its final CLI and immutable access evidence.</summary>
    private sealed record Result(uint[] Registers, ushort[] Segments, uint Flags, uint StackBase, bool V86, bool Protected,
        int MaximumStack, Port[] Ports, uint Latch);

    /// <summary>Rejects a missing actual CPU, ABI or physical configuration effect.</summary>
    private static void Check(bool value, string why = "PCI BIOS invariant")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Own explicit instruction stream with byte-order-correct x86 immediates.</summary>
    private sealed class Code
    {
        private readonly List<byte> _bytes = [];
        /// <summary>Current stream extent for the real return-instruction observation.</summary>
        public int Count => _bytes.Count;
        /// <summary>Appends literal instruction bytes without executing a replacement BIOS operation.</summary>
        public void Put(params byte[] bytes) => _bytes.AddRange(bytes);
        /// <summary>Encodes an independently specified little-endian immediate word.</summary>
        public void Word(ushort value) { _bytes.Add((byte)value); _bytes.Add((byte)(value >> 8)); }
        /// <summary>Encodes an independently specified little-endian immediate dword.</summary>
        public void Dword(uint value) { for (int i = 0; i < 4; i++) _bytes.Add((byte)(value >> (i * 8))); }
        /// <summary>Loads one complete caller register in the selected instruction width.</summary>
        public void Mov(int register, uint value, bool code32)
        { if (!code32) Put(0x66); Put((byte)(0xb8 + register)); Dword(value); }
        /// <summary>Returns only own caller bytes.</summary>
        public byte[] Bytes() => _bytes.ToArray();
    }

    /// <summary>Complete newly constructed named board; all firmware services still execute from its actual ROM.</summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>Actual original CPU/buses/host/PIIX/S3/optical peers, including real POST configuration.</summary>
        public readonly PcMachine Machine;
        /// <summary>Only actual BIOS configuration accesses from the current call.</summary>
        private readonly List<Port> _ports = [];
        /// <summary>Discovers the checked BIOS32 entry rather than assuming an implementation label.</summary>
        public uint DirectoryEntry { get; }

        /// <summary>Boots the new additional profile without inserting or opening any host medium.</summary>
        public Rig()
        {
            var profile = new MachineProfile { Id = MachineProfile.PciBiosProfileId, Name = "Own PCI BIOS ABI board", Cpu = MachineProfile.P54cCpuId,
                CpuHz = MachineProfile.FxHostCpuHz, RamMiB = MachineProfile.FxHostRamMiB,
                VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2 };
            Machine = PcMachine.CreateWithOpticalDiscs(profile, new IReadOnlyBlockDevice?[4], null);
            Machine.RunSteps(250000); Check(Machine.Fault is null && Machine.Cpu.Halted, Machine.Fault ?? "Actual POST did not finish");
            Machine.Ports.Write(0x21, 255, 1); Machine.Ports.Write(0xa1, 255, 1);
            var found = new List<uint>();
            for (uint address = 0xe0000; address <= 0xffff0; address += 16)
            {
                if (Machine.Memory.ReadDword(address) != 0x5f32335f) continue;
                byte[] header = Enumerable.Range(0, 16).Select(i => Machine.Memory.ReadByte(address + (uint)i)).ToArray();
                Check(header[8] == 0 && header[9] == 1 && header.Skip(11).All(x => x == 0) && header.Sum(x => x) % 256 == 0, "Invalid real BIOS32 directory header");
                found.Add(Machine.Memory.ReadDword(address + 4));
            }
            Check(found.Count == 1 && found[0] is >= 0xf0000 and <= 0xfffff, "Missing/nonunique actual BIOS32 directory");
            DirectoryEntry = found[0]; Machine.Ports.Write(0xcf8, 0x80003864, 4); Machine.Ports.Accessed += Access;
        }

        /// <summary>Collects actual completed device transactions without invoking or acknowledging endpoints.</summary>
        private void Access(ushort address, uint value, int width, bool write)
        { if (address is >= 0xcf8 and <= 0xcff || address is 0x4d0 or 0x4d1 or 0x20 or 0x21 or 0xa0 or 0xa1) _ports.Add(new(address, value, width, write)); }

        /// <summary>Creates original caller sentinels with only architectural function inputs changed by each case.</summary>
        public static uint[] Input(ushort function)
            => [0x12340000u | function, 0x23456789, 0x3456789a, 0x456789ab, 0xabcd0400, 0x6789abcd, 0x789abcde, 0x89abcdef];

        /// <summary>Writes an explicit private GDT descriptor; it does not load or change CPU control state.</summary>
        private void Descriptor(ushort selector, uint basis, uint limit, byte access, byte flags)
        {
            uint address = 0x400u + ((uint)selector & 0xfff8);
            Machine.Memory.WriteDword(address, (limit & 0xffff) | (basis << 16));
            Machine.Memory.WriteDword(address + 4, ((basis >> 16) & 255) | ((uint)access << 8) | (limit & 0xf0000) | ((uint)flags << 20) | (basis & 0xff000000));
        }

        /// <summary>Creates code/read-only data/stack descriptors for a real guest LGDT/CR0 transition.</summary>
        private void Descriptors(uint serviceBase, bool narrow, bool virtualCaller, uint? dataBase = null)
        {
            uint callerDataBase = dataBase ?? serviceBase;
            Descriptor(8, 0, 0xfffff, 0x9a, 12); Descriptor(0x10, 0, 0xfffff, 0x92, 12);
            Descriptor(0x18, callerDataBase, callerDataBase == 0 ? 0xfffffu : 0x1ffffu, 0x90, callerDataBase == 0 ? (byte)12 : (byte)4);
            Descriptor(0x20, 0x60000, 0x3ff, 0x92, narrow ? (byte)0 : (byte)4);
            Descriptor(0x28, serviceBase, serviceBase == 0 ? 0xfffffu : 0x1ffffu, 0x98, narrow ? (byte)0 : serviceBase == 0 ? (byte)12 : (byte)4);
            Descriptor(0x30, 0x40000, 0xffff, 0x92, 0);
            if (virtualCaller)
            {
                // V86 port permissions always use a real TSS bitmap; IOPL3 permits only the flag-sensitive instructions.
                Descriptor(0x38, 0x20000, 0x2068, 0x89, 0);
                Machine.Memory.Ram.AsSpan(0x20000, 0x2069).Clear();
                Machine.Memory.WriteDword(0x20004, 0x9000); Machine.Memory.WriteWord(0x20008, 0x10);
                Machine.Memory.WriteWord(0x20066, 0x68); Machine.Memory.Ram.AsSpan(0x20068, 8193).Fill(255);
                foreach (int port in Enumerable.Range(0xcf8, 8).Concat(Enumerable.Range(0x4d0, 2)))
                    Machine.Memory.Ram[0x20068 + (port >> 3)] &= (byte)~(1 << (port & 7));
            }
            Machine.Memory.WriteWord(0x300, 0x3f); Machine.Memory.WriteDword(0x302, 0x400);
        }

        /// <summary>Calls the actual sixteen- or thirty-two-bit firmware and observes the first genuine caller instruction after return.</summary>
        public Result Call(Mode mode, uint[] supplied, uint flags = 0xed7, bool directory = false, uint? entry = null, ushort callerDs = 0xf000, uint? protectedDataBase = null)
        {
            var m = Machine; var input = supplied.ToArray(); bool wide = mode is Mode.Flat32 or Mode.Rom32;
            uint serviceBase = mode is Mode.Rom32 or Mode.Pe16 ? 0xf0000u : 0;
            m.Cpu.Reset(); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0x6000);
            m.Cpu.SetRegister(Register.Sp, 0xabcd0400); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 2;
            m.Memory.Ram.AsSpan(0x5ffe0, 1024 + 64).Fill(0xcd); _ports.Clear();
            uint bodyAddress = 0x1000; var code = new Code();
            if (mode is Mode.Pe16 or Mode.Flat32 or Mode.Rom32 or Mode.V86)
            {
                Descriptors(serviceBase, mode == Mode.Pe16, mode == Mode.V86, protectedDataBase);
                byte[] start = [0x0f,0x01,0x16,0,3,0x0f,0x20,0xc0,0x66,0x83,0xc8,1,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
                start.CopyTo(m.Memory.Ram, 0x1000); bodyAddress = 0x2000;
                // This caller remains flat PE32; only its far target and SS size select the BIOS interface.
                code.Put(0x66,0xb8,0x10,0,0x8e,0xd0); code.Mov(4, 0x9000, true);
                if (mode == Mode.V86)
                {
                    code.Put(0x66,0xb8,0x38,0,0x0f,0x00,0xd8); // Actual LTR before entering V86.
                    uint vmFlags = flags | 0x23000;
                    foreach (uint value in new uint[] { 0x4000,0x4000,callerDs,0x3000,0x6000,0xabcd0400,vmFlags,0x1000,0x100 })
                    { code.Put(0x68); code.Dword(value); }
                    code.Put(0xcf); code.Bytes().CopyTo(m.Memory.Ram, (int)bodyAddress);
                    code = new Code(); bodyAddress = 0x10100;
                }
                else
                {
                    code.Put(0x66,0xb8,0x20,0,0x8e,0xd0); code.Mov(4, 0x400, true);
                    code.Put(0x66,0xb8,0x18,0,0x8e,0xd8,0x66,0xb8,0x10,0,0x8e,0xc0);
                    code.Put(0x66,0xb8,0x18,0,0x8e,0xe0,0x8e,0xe8);
                    input[4] = mode == Mode.Pe16 ? 0xabcd0400u : 0x400;
                }
            }
            if (mode is Mode.Real or Mode.Fixed16)
            {
                m.Cpu.SetSegment(Segment.Ds, callerDs); m.Cpu.SetSegment(Segment.Es, 0x3000);
                m.Cpu.SetSegment(Segment.Fs, 0x4000); m.Cpu.SetSegment(Segment.Gs, 0x4000);
            }
            bool caller32 = mode is Mode.Pe16 or Mode.Flat32 or Mode.Rom32;
            for (int i = 0; i < 8; i++) code.Mov(i, input[i], caller32);
            uint wantedFlags = mode == Mode.V86 ? flags | 0x23000u : flags;
            if (caller32) { code.Put(0x68); code.Dword(wantedFlags); code.Put(0x9d); }
            else { code.Put(0x68); code.Word((ushort)wantedFlags); code.Put(0x9d); }
            if (mode == Mode.Real) code.Put(0xcd, 0x1a);
            else if (mode is Mode.Fixed16 or Mode.V86)
            { code.Put(0x9c,0x9a); code.Word(0xfe6e); code.Word(0xf000); }
            else if (mode == Mode.Pe16)
            { code.Put(0x66,0x9c,0x66,0x9a); code.Word(0xfe6e); code.Word(0x28); }
            else
            {
                uint physicalEntry = entry ?? (directory ? DirectoryEntry : throw new InvalidOperationException("Unresolved actual PCI32 entry"));
                code.Put(0x9a); code.Dword(physicalEntry - serviceBase); code.Word(0x28);
            }
            uint afterAddress = bodyAddress + (uint)code.Count; code.Put(0xfa,0xf4); code.Bytes().CopyTo(m.Memory.Ram, (int)bodyAddress);
            uint latch = m.Ports.Read(0xcf8, 4); _ports.Clear(); Result? result = null; int maximumStack = 0;
            // Observe completed caller return and a bounded real firmware stack without modifying CPU or device state.
            void Observe(uint address, byte opcode)
            {
                if (address is >= 0xf0000 and <= 0xfffff)
                {
                    uint sp = m.Cpu.GetRegister(Register.Sp) & (m.Cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 65535);
                    maximumStack = Math.Max(maximumStack, unchecked((int)((0x400u - sp) & 65535)));
                }
                if (address != afterAddress || opcode != 0xfa) return;
                result = new(Enumerable.Range(0, 8).Select(i => m.Cpu.GetRegister((Register)i)).ToArray(),
                    Enumerable.Range(0, 6).Select(i => m.Cpu.GetSegment((Segment)i)).ToArray(), m.Cpu.Eflags, m.Cpu.GetSegmentBase(Segment.Ss),
                    m.Cpu.Virtual8086, m.Cpu.ProtectedMode, maximumStack, _ports.ToArray(), m.Ports.Read(0xcf8, 4));
            }
            var unexpected = new List<byte>();
            // The real-mode INT1A itself is expected; a fixture fault or reset must never count as a normal BIOS return.
            void Entered(byte vector) { if (mode != Mode.Real || vector != 0x1a) unexpected.Add(vector); }
            m.Cpu.InstructionStarting += Observe; m.Cpu.InterruptEntered += Entered;
            try
            {
                for (int steps = 0; result is null && steps < 1000000; steps++)
                {
                    int executed = m.RunSteps(1);
                    Check(executed > 0 && m.Fault is null && !m.Cpu.TripleFaulted && unexpected.Count == 0,
                        m.Fault ?? "Unexpected fault/stopped machine before BIOS return: " + m.Cpu.Position + " vectors=" + string.Join(",", unexpected));
                }
            }
            finally { m.Cpu.InstructionStarting -= Observe; m.Cpu.InterruptEntered -= Entered; }
            // In V86 the following HLT would be privileged: stop at the observed caller CLI, before fetching that HLT.
            Check(result is not null && m.Fault is null && !m.Cpu.TripleFaulted && unexpected.Count == 0, m.Fault ?? "Actual BIOS did not return: " + m.Cpu.Position);
            Check(result!.MaximumStack <= 1024 && result.StackBase == 0x60000 && result.Registers[4] == input[4], "Actual BIOS exceeded/changed its1024-byte caller stack");
            Check(m.Memory.Ram.AsSpan(0x5ffe0, 32).IndexOfAnyExcept((byte)0xcd) < 0 && m.Memory.Ram.AsSpan(0x60400, 32).IndexOfAnyExcept((byte)0xcd) < 0, "BIOS wrote outside caller stack extent");
            ushort[] expectedSegments = mode is Mode.Real or Mode.Fixed16 ? [0x3000,0,0x6000,callerDs,0x4000,0x4000] :
                mode == Mode.V86 ? [0x3000,0x1000,0x6000,callerDs,0x4000,0x4000] : [0x10,8,0x20,0x18,0x18,0x18];
            Check(result.Segments.SequenceEqual(expectedSegments), "Firmware changed caller segment registers");
            Check(result.Latch == latch, "Firmware leaked a changed actual CF8 latch");
            Check(result.V86 == (mode == Mode.V86) && result.Protected == (mode is not (Mode.Real or Mode.Fixed16)), "Actual caller mode did not survive");
            return result;
        }

        /// <summary>Resolves the actual protected PCI service through a genuine BIOS32 far call.</summary>
        public uint Pci32(Mode mode)
        {
            var input = Input(0); input[0] = 0x49435024; input[3] = 0;
            Result r = Call(mode, input, directory: true);
            Check((r.Registers[0] & 255) == 0 && r.Registers[3] == 0xf0000 && r.Registers[1] == 0x10000 && r.Registers[2] < 0x10000, "Actual BIOS32 returned no coherent PCI service");
            return r.Registers[3] + r.Registers[2];
        }

        /// <summary>Dispatches the actual PCI function through the appropriate discovered or standard entry.</summary>
        public Result Pci(Mode mode, uint[] input, uint flags = 0xed7, ushort callerDs = 0xf000, uint? protectedDataBase = null)
            => Call(mode, input, flags, entry: mode is Mode.Flat32 or Mode.Rom32 ? Pci32(mode) : null, callerDs: callerDs, protectedDataBase: protectedDataBase);

        /// <summary>Requires defined outputs only, while checking every other complete GPR and non-CF flag.</summary>
        public static void Preserved(Result result, uint[] input, uint flags, params (Register Register, uint Mask)[] outputs)
        {
            for (int i = 0; i < 8; i++)
            {
                if (i == 4) continue;
                uint mask = outputs.Where(x => (int)x.Register == i).Aggregate(0u, (v, x) => v | x.Mask);
                Check((result.Registers[i] & ~mask) == (input[i] & ~mask), "Unexpected BIOS register modification: " + (Register)i);
            }
            uint expectedFlags = result.V86 ? flags | 0x23000u : flags;
            Check((result.Flags & ~1u) == (expectedFlags & ~1u), "Unexpected BIOS arithmetic/IF/DF flag modification");
        }

        /// <summary>Releases only this own media-free PC graph.</summary>
        public void Dispose() { Machine.Ports.Accessed -= Access; Machine.Dispose(); }
    }

    /// <summary>Checks exact status without treating an all-ones absent BDF as a failed read service.</summary>
    private static void Status(Result result, byte status)
        => Check((result.Registers[0] >> 8 & 255) == status && (result.Flags & 1) == (status == 0 ? 0u : 1u), "Unexpected PCI BIOS status " + (result.Registers[0] >> 8 & 255).ToString("X2"));

    /// <summary>Provides independent vendor/device/class tuples of the four actual constructor-bound PCI functions.</summary>
    private static readonly (ushort Bdf, ushort Vendor, ushort Device, uint Class)[] Functions =
        [(0,0x8086,0x122d,0x060000), (0x18,0x5333,0x8811,0x030000), (0x38,0x8086,0x122e,0x060100), (0x39,0x8086,0x1230,0x010180)];

    /// <summary>Builds the original packed 16- or32-bit far route-buffer ABI with independent guards.</summary>
    private static (uint Header, uint Data, uint[] Input) RouteBuffer(Rig rig, Mode mode, ushort capacity)
    {
        bool wide = mode is Mode.Flat32 or Mode.Rom32;
        bool real = mode is Mode.Real or Mode.Fixed16 or Mode.V86;
        uint header = real ? 0x30100u : 0x3100u, data = 0x40200;
        rig.Machine.Memory.Ram.AsSpan((int)header - 16, 64).Fill(0xa6);
        rig.Machine.Memory.Ram.AsSpan((int)data - 16, 80).Fill(0xcd);
        rig.Machine.Memory.WriteWord(header, capacity);
        if(wide){rig.Machine.Memory.WriteDword(header+2,data);rig.Machine.Memory.WriteWord(header+6,0x10);}
        else{rig.Machine.Memory.WriteWord(header+2,0x200);rig.Machine.Memory.WriteWord(header+4,real?(ushort)0x4000:(ushort)0x30);}
        var input=Rig.Input(0xb10e);input[3]&=0xffff0000;
        input[7]=wide?header:(input[7]&0xffff0000)|(real?0x100u:header);
        return(header,data,input);
    }

    /// <summary>Specifies the48-byte own board table without reading firmware tables or a renderer helper.</summary>
    private static byte[] RouteTable()
    {
        byte[] bytes=new byte[48];bytes[17]=0x18;bytes[18]=0x60;bytes[19]=0;bytes[20]=8;bytes[33]=0x38;return bytes;
    }
    /// <summary>Registers primary-interface and actual board effects without substituting any BIOS handler.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (Mode mode in new[] { Mode.Real, Mode.Fixed16, Mode.V86, Mode.Pe16, Mode.Flat32, Mode.Rom32 })
        foreach (uint flags in new uint[] { 0xcd7, 0xed6 })
        test("M4 PCI BIOS actual present mode=" + mode + " flags=" + flags.ToString("X"), () =>
        {
            using var r = new Rig(); var input = Rig.Input(0xb101); Result result = r.Pci(mode, input, flags); Status(result, 0);
            Check((result.Registers[0] & 255) == 1 && (result.Registers[3] & 65535) == 0x210 && (result.Registers[1] & 255) == 0 && result.Registers[2] == 0x20494350);
            Rig.Preserved(result, input, flags, (Register.Ax,65535),(Register.Bx,65535),(Register.Cx,255),(Register.Dx,uint.MaxValue));
        });
        foreach (Mode mode in new[] { Mode.Flat32, Mode.Rom32 })
        foreach ((uint id, uint function, byte status) in new (uint,uint,byte)[] { (0x49435024,0,0),(0x4e4f4e24,0,0x80),(0x49435024,1,0x81) })
        test("M4 BIOS32 real far directory mode=" + mode + " status=" + status.ToString("X2"), () =>
        {
            using var r = new Rig(); var input = Rig.Input(0); input[0] = id; input[3] = function; Result result = r.Call(mode,input,directory:true);
            Check((result.Registers[0] & 255) == status && result.Flags == 0xed7);
            Rig.Preserved(result,input,0xed7,(Register.Ax,255),(Register.Bx,uint.MaxValue),(Register.Cx,uint.MaxValue),(Register.Dx,uint.MaxValue));
            if(status==0) Check(result.Registers[3]==0xf0000 && result.Registers[1]==0x10000 && result.Registers[2]<0x10000);
        });
        foreach (Mode mode in new[] { Mode.Real, Mode.V86, Mode.Flat32 })
        foreach (var function in Functions)
        test("M4 PCI BIOS actual find vendor/device mode=" + mode + " bdf=" + function.Bdf.ToString("X2"), () =>
        {
            using var r=new Rig();var input=Rig.Input(0xb102);input[1]=(input[1]&0xffff0000)|function.Device;input[2]=(input[2]&0xffff0000)|function.Vendor;input[6]&=0xffff0000;
            Result result=r.Pci(mode,input);Status(result,0);Check((result.Registers[3]&65535)==function.Bdf);
            Check(result.Ports.Any(p=>p.Address==0xcfc&&!p.Write&&p.Width==4),"Find returned no actual device configuration read");
            Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00),(Register.Bx,65535));
        });
        foreach (Mode mode in new[] { Mode.Real, Mode.V86, Mode.Rom32 })
        foreach (var function in Functions)
        test("M4 PCI BIOS actual class match ignores upper input byte mode=" + mode + " class=" + function.Class.ToString("X6"), () =>
        {
            using var r=new Rig();var input=Rig.Input(0xb103);input[1]=function.Class|0xaa000000;input[6]&=0xffff0000;Result result=r.Pci(mode,input);Status(result,0);
            Check((result.Registers[3]&65535)==function.Bdf);Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00),(Register.Bx,65535));
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Flat32})
        foreach((ushort vendor,ushort device,ushort index,byte status) in new(ushort,ushort,ushort,byte)[]{(0xffff,0,0,0x83),(0x8086,0x122d,1,0x86),(0x1234,0x5678,0,0x86)})
        test("M4 PCI BIOS actual find rejection mode="+mode+" vendor="+vendor.ToString("X4")+" index="+index,()=>
        {
            using var r=new Rig();var input=Rig.Input(0xb102);input[1]=(input[1]&0xffff0000)|device;input[2]=(input[2]&0xffff0000)|vendor;input[6]=(input[6]&0xffff0000)|index;
            Result result=r.Pci(mode,input);Status(result,status);Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00),(Register.Bx,65535));
            if(vendor==0xffff)Check(!result.Ports.Any(p=>p.Address is >=0xcfc and <=0xcff),"Bad vendor touched a PCI data endpoint");
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.Pe16,Mode.Flat32})
        foreach(byte function in new byte[]{0,4,5,6,7,0x10,0xff})
        test("M4 PCI BIOS unsupported function has no config effect mode="+mode+" function="+function.ToString("X2"),()=>
        {
            using var r=new Rig();var input=Rig.Input((ushort)(0xb100|function));Result result=r.Pci(mode,input);Status(result,0x81);
            Check(!result.Ports.Any(p=>p.Address is >=0xcfc and <=0xcff),"Unsupported BIOS operation touched PCI data");Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00));
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Flat32,Mode.Rom32})
        foreach((ushort function,int width,ushort offset) in new(ushort,int,ushort)[]{(0xb108,1,0),(0xb109,2,2),(0xb10a,4,0),(0xb108,1,255),(0xb109,2,254),(0xb10a,4,252)})
        test("M4 PCI BIOS actual config read mode="+mode+" width="+width+" offset="+offset,()=>
        {
            using var r=new Rig();var input=Rig.Input(function);input[3]=(input[3]&0xffff0000)|0x18u;input[7]=(input[7]&0xffff0000)|offset;
            Result result=r.Pci(mode,input);Status(result,0);uint mask=width==4?uint.MaxValue:width==2?65535u:255;
            Port access=result.Ports.Last(p=>!p.Write&&p.Address is >=0xcfc and <=0xcff);Check(access.Width==width&&access.Address==0xcfcu+(offset&3)&&((result.Registers[1]&mask)==access.Value));
            if(offset==0)Check((result.Registers[1]&mask)==(width==1?0x33u:0x88115333u));
            Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00),(Register.Cx,mask));
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Rom32})
        foreach((ushort function,ushort offset) in new(ushort,ushort)[]{(0xb108,256),(0xb109,1),(0xb109,255),(0xb10a,2),(0xb10a,254),(0xb10b,256),(0xb10c,1),(0xb10d,2)})
        test("M4 PCI BIOS bad register rejects before config mode="+mode+" function="+function.ToString("X4")+" offset="+offset,()=>
        {
            using var r=new Rig();var input=Rig.Input(function);input[3]=(input[3]&0xffff0000)|0x18u;input[7]=(input[7]&0xffff0000)|offset;Result result=r.Pci(mode,input);Status(result,0x87);
            Check(!result.Ports.Any(p=>p.Address is >=0xcfc and <=0xcff),"Invalid register reached PCI data endpoint");Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00),(Register.Cx,function<=0xb10a?uint.MaxValue:0));
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Flat32,Mode.Rom32})
        test("M4 PCI BIOS write changes actual S3 BAR with preserved CF8 mode="+mode,()=>
        {
            using var r=new Rig();r.Machine.Ports.Write(0xcf8,0x80003864,4);var input=Rig.Input(0xb10d);input[3]=(input[3]&0xffff0000)|0x18u;input[7]=(input[7]&0xffff0000)|0x10u;input[1]=0xe1800000;
            Result written=r.Pci(mode,input);Status(written,0);Check(r.Machine.S3!.BarBase==0xe1800000&&written.Ports.Any(p=>p.Write&&p.Address==0xcfc&&p.Width==4&&p.Value==0xe1800000));
            Rig.Preserved(written,input,0xed7,(Register.Ax,0xff00));input[0]=(input[0]&0xffff0000)|0xb10au;Result read=r.Pci(mode,input);Status(read,0);Check(read.Registers[1]==0xe1800000);
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.Flat32})
        foreach(ushort function in new ushort[]{0xb108,0xb109,0xb10a,0xb10d})
        test("M4 PCI BIOS absent BDF uses actual floating bus mode="+mode+" function="+function.ToString("X4"),()=>
        {
            using var r=new Rig();var input=Rig.Input(function);input[3]=(input[3]&0xffff0000)|8u;input[7]&=0xffff0000;Result result=r.Pci(mode,input);Status(result,0);
            if(function!=0xb10d){uint mask=function==0xb108?255u:function==0xb109?65535u:uint.MaxValue;Check((result.Registers[1]&mask)==mask);}
        });
        foreach(Mode mode in new[]{Mode.Flat32,Mode.Rom32})
        foreach((uint selector,byte status) in new(uint,byte)[]{(0x12340000,0),(0x12340001,0x81)})
        test("M4 BIOS32 own compatibility ignores reserved upper EBX mode="+mode+" selector="+selector.ToString("X8"),()=>
        {
            using var r=new Rig();var input=Rig.Input(0);input[0]=0x49435024;input[3]=selector;Result result=r.Call(mode,input,directory:true);
            Check((result.Registers[0]&255)==status&&result.Flags==0xed7);
            if(status==0)Check(result.Registers[3]==0xf0000&&result.Registers[1]==0x10000&&result.Registers[2]<0x10000);
            else Check(result.Registers[3]==selector&&result.Registers[1]==input[1]&&result.Registers[2]==input[2]);
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Pe16,Mode.Flat32,Mode.Rom32})
        foreach(ushort capacity in new ushort[]{0,47,48,64})
        test("M4 PCI BIOS actual packed routing buffer mode="+mode+" capacity="+capacity,()=>
        {
            using var r=new Rig();var buffer=RouteBuffer(r,mode,capacity);Result result=r.Pci(mode,buffer.Input);Status(result,capacity<48?(byte)0x89:(byte)0);
            Check(r.Machine.Memory.ReadWord(buffer.Header)==48&&(result.Registers[3]&65535)==0,"Wrong actual routing count/exclusive IRQs at reset");
            if(capacity>=48)Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Data,48).SequenceEqual(RouteTable()),"Actual own-board routing table differs");
            else Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Data,48).IndexOfAnyExcept((byte)0xcd)<0,"Short routing output was partially written");
            Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Data-16,16).IndexOfAnyExcept((byte)0xcd)<0&&r.Machine.Memory.Ram.AsSpan((int)buffer.Data+48,16).IndexOfAnyExcept((byte)0xcd)<0,"Routing buffer guards changed");
            Rig.Preserved(result,buffer.Input,0xed7,(Register.Ax,0xff00),(Register.Bx,65535));
        });
        // Primary PCI BIOS2.1 requires ROM-based DS for16-bit B10E; these are expressly an own compatibility extension.
        foreach(var caller in new (Mode Mode, ushort Ds, uint? DataBase)[]
            { (Mode.Real,0x1234,null), (Mode.Fixed16,0x2345,null), (Mode.V86,0x3456,null), (Mode.Pe16,0x18,0x20000) })
        test("M4 PCI BIOS own compatibility routing with caller data outside ROM mode="+caller.Mode,()=>
        {
            using var r=new Rig();var buffer=RouteBuffer(r,caller.Mode,48);
            byte[] headerBefore=r.Machine.Memory.Ram.AsSpan((int)buffer.Header-16,64).ToArray();
            Result result=r.Pci(caller.Mode,buffer.Input,callerDs:caller.Ds,protectedDataBase:caller.DataBase);Status(result,0);
            Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Data,48).SequenceEqual(RouteTable()),"Own compatibility route read caller DS instead of the fixed motherboard table");
            Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Header-16,64).SequenceEqual(headerBefore),"Routing changed an already sufficient caller header or its guards");
            Check(r.Machine.Memory.Ram.AsSpan((int)buffer.Data-16,16).IndexOfAnyExcept((byte)0xcd)<0&&r.Machine.Memory.Ram.AsSpan((int)buffer.Data+48,16).IndexOfAnyExcept((byte)0xcd)<0,"Own compatibility routing output crossed its extent");
            uint expectedBase=caller.DataBase??((uint)caller.Ds<<4);
            Check(r.Machine.Cpu.GetSegmentBase(Segment.Ds)==expectedBase,"Own compatibility routing changed the caller data descriptor");
            Rig.Preserved(result,buffer.Input,0xed7,(Register.Ax,0xff00),(Register.Bx,65535));
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.V86,Mode.Flat32,Mode.Rom32})
        test("M4 PCI BIOS actual only own INTA routing programs PIIX ELCR and preserves caller resources mode="+mode,()=>
        {
            using var r=new Rig();var line=Rig.Input(0xb10b);line[3]=(line[3]&0xffff0000)|0x18u;line[7]=(line[7]&0xffff0000)|0x3cu;line[1]=(line[1]&0xffffff00)|255u;Status(r.Pci(mode,line),0);
            r.Machine.Ports.Write(0x4d0,8,1);r.Machine.Ports.Write(0x4d1,0x44,1);
            var input=Rig.Input(0xb10f);input[3]=(input[3]&0xffff0000)|0x18u;input[1]=(input[1]&0xffff0000)|0x0b0au;Result result=r.Pci(mode,input);Status(result,0);
            Check(r.Machine.PiixIrq!.ReadConfiguration(0x60,1)==11&&r.Machine.Ports.Read(0x4d0,1)==8&&r.Machine.Ports.Read(0x4d1,1)==0x4c,"Actual routed IRQ or retained ELCR bits differ");
            Check(r.Machine.S3!.ReadConfiguration(0x3c,1)==255&&!result.Ports.Any(p=>p.Write&&p.Address is 0x20 or 0x21 or 0xa0 or 0xa1),"BIOS changed caller-owned InterruptLine/PIC programming");
            Rig.Preserved(result,input,0xed7,(Register.Ax,0xff00));var buffer=RouteBuffer(r,mode,48);Result query=r.Pci(mode,buffer.Input);Status(query,0);Check((query.Registers[3]&65535)==0x800);
        });
        foreach(Mode mode in new[]{Mode.Real,Mode.Flat32})
        foreach((ushort bdf,byte pin,byte irq) in new(ushort,byte,byte)[]{(0,0x0a,11),(0x18,0x0b,11),(0x19,0x0a,11),(0x18,0x0a,10),(0x18,1,11),(0x18,0x0a,0)})
        test("M4 PCI BIOS unknown own route has no partial hardware effect mode="+mode+" bdf="+bdf.ToString("X4")+" pin="+pin+" irq="+irq,()=>
        {
            using var r=new Rig();var input=Rig.Input(0xb10f);input[3]=(input[3]&0xffff0000)|bdf;input[1]=(input[1]&0xffff0000)|((uint)irq<<8)|pin;
            uint before=r.Machine.PiixIrq!.ReadConfiguration(0x60,4);Result result=r.Pci(mode,input);Status(result,0x88);
            Check(r.Machine.PiixIrq.ReadConfiguration(0x60,4)==before&&r.Machine.Ports.Read(0x4d0,1)==0&&r.Machine.Ports.Read(0x4d1,1)==0);
            Check(!result.Ports.Any(p=>p.Write&&(p.Address is >=0xcfc and <=0xcff||p.Address is 0x4d0 or 0x4d1)),"Rejected route performed a partial PCI/ELCR write");
        });
        test("M4 PCI BIOS excludes arbitrary shared ISA PIRQ routes from its dedicated IRQ bitmap",()=>
        {
            using var r=new Rig();foreach(ushort offset in new ushort[]{0x60,0x61}){var write=Rig.Input(0xb10b);write[3]=(write[3]&0xffff0000)|0x38u;write[7]=(write[7]&0xffff0000)|offset;write[1]=(write[1]&0xffffff00)|9u;Status(r.Pci(Mode.Real,write),0);}
            var buffer=RouteBuffer(r,Mode.Real,48);Result result=r.Pci(Mode.Real,buffer.Input);Status(result,0);Check((result.Registers[3]&65535)==0,"An arbitrary shared ISA route was advertised as exclusively PCI");
        });
    }
}
