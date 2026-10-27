// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using static ProtectedModeTests;

/// <summary>Second audit: IF-sensitive STI recognition and ENTER allocation endpoints, preserving sequential stack effects.</summary>
internal static class Audit2CpuTests
{
    /// <summary>Requires an architectural frame, completed instruction or independently calculated stack byte.</summary>
    private static void Check(bool value, string message = "Second CPU audit contract failed.") { if (!value) throw new Exception(message); }
    /// <summary>Reads one explicitly selected operand width without using a CPU stack helper.</summary>
    private static uint Read(MemoryBus m, uint at, int width) => width == 2 ? m.ReadWord(at) : m.ReadDword(at);
    /// <summary>Writes one test operand at its exact width, without overwriting adjacent frame-chain words.</summary>
    private static void Write(MemoryBus m, uint at, uint value, int width)
    { if (width == 2) m.WriteWord(at, (ushort)value); else m.WriteDword(at, value); }
    /// <summary>Encodes independent ENTER operand/address prefixes, allocation bytes and the raw nesting byte.</summary>
    private static byte[] Enter(bool code32, int width, ushort allocation, byte nesting, bool addressPrefix = false) =>
        [.. (code32 == (width == 4) ? Array.Empty<byte>() : new byte[] { 0x66 }), .. (addressPrefix ? new byte[] { 0x67 } : Array.Empty<byte>()),
            0xc8, (byte)allocation, (byte)(allocation >> 8), nesting];
    /// <summary>Restores an actual error-code frame through guest ADD ESP and IRETD, then leaves the original instruction ready to retry.</summary>
    private static void ReturnError(X86Cpu c, MemoryBus m)
    {
        new byte[] { 0x83, 0xc4, 4, 0xcf }.CopyTo(m.Ram, 0x6000);
        c.Eip = 0x6000; c.Step(); c.Step(); Check(c.Eip == 0x2000 && !c.TripleFaulted, "Fault handler did not return to the original instruction.");
    }
    /// <summary>Builds an actual privilege transition and separate ring-zero fault stack using the existing explicit TSS fixture.</summary>
    private static void Ring(X86Cpu c, MemoryBus m, int ring)
    {
        PagingTests.Tss(c, m);
        Descriptor(m, 0x28, 0, 0xfffff, (byte)(0x9a | ring << 5), 12);
        Descriptor(m, 0x30, 0, 0xfffff, (byte)(0x92 | ring << 5), 12);
        uint[] frame = [0x2000, (uint)(0x28 | ring), 0x202, 0x1b004, (uint)(0x30 | ring)];
        for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
        c.SetRegister(Register.Sp, 0x18000); PagingTests.At(c, m, [0xcf], 0x2100);
        Check(c.CurrentPrivilegeLevel == ring && c.Eip == 0x2000, "Actual IRETD privilege setup failed.");
    }
    /// <summary>Independent source-chain pattern, truncated only by the selected operand width.</summary>
    private static uint Chain(int index, int width) => (0x51000000u | (uint)(index * 0x101)) & (width == 2 ? 0xffffu : uint.MaxValue);
    /// <summary>Fills every possible input frame-chain element before any ENTER access.</summary>
    private static void FillChain(MemoryBus m, uint bp, int width, int nesting)
    { for (int i = 1; i < nesting; i++) Write(m, bp - (uint)(i * width), Chain(i, width), width); }

    /// <summary>Actual PIT/PIC fixture; time is advanced separately so an IRQ can arrive precisely after a decoded STI.</summary>
    private sealed class InterruptRig
    {
        public readonly X86Cpu Cpu;
        public readonly MemoryBus Memory;
        public readonly DeviceScheduler Clock = new();
        public readonly Pic8259 Pic = new();
        public readonly Pit8254 Pit;
        public readonly uint Frequency;
        public readonly int Mode;
        /// <summary>Connects actual timer callbacks, PIC ports and an EOI/IRET guest handler in real, PE16, PE32 or paged V86 mode.</summary>
        public InterruptRig(X86CpuModel model, int mode, byte[] code)
        {
            Mode = mode; Frequency = model == X86CpuModel.Intel486Dx ? 33_000_000u : 90_000_000u;
            var io = new IoPortBus(); io.Map(0x20, 2, Pic); io.Map(0xa0, 2, Pic);
            Pit = new(Clock, () => { Pic.SetLine(0, true); Pic.SetLine(0, false); }); io.Map(0x40, 4, Pit);
            if (mode == 0)
            {
                Memory = new(2 * 1048576); Cpu = new(Memory, io, model, model == X86CpuModel.IntelP54cB5Subset ? Clock : null, model == X86CpuModel.IntelP54cB5Subset ? Frequency : 0);
                Cpu.SetSegment(Segment.Cs, 0); Cpu.SetSegment(Segment.Ss, 0); Cpu.Eip = 0x2000; Cpu.SetRegister(Register.Sp, 0xabcd9000); code.CopyTo(Memory.Ram, 0x2000);
                Memory.WriteWord(0x80, 0x6000); Memory.WriteWord(0x82, 0);
            }
            else if (mode < 3)
                (Cpu, Memory) = ProtectedModeTests.Rig(code, code32: mode == 2, stack32: mode == 2, io: io, model: model,
                    timestampSource: model == X86CpuModel.IntelP54cB5Subset ? Clock : null, timestampFrequency: model == X86CpuModel.IntelP54cB5Subset ? Frequency : 0);
            else
            {
                (Cpu, Memory) = PagingTests.Create([0x90], io: io, model: model,
                    timestampSource: model == X86CpuModel.IntelP54cB5Subset ? Clock : null, timestampFrequency: model == X86CpuModel.IntelP54cB5Subset ? Frequency : 0);
                PagingTests.Tss(Cpu, Memory); uint[] frame = [0x2000, 0x1000, 0x23002, 0xabcd9000, 0, 0, 0, 0, 0];
                for (uint i = 0; i < frame.Length; i++) Memory.WriteDword(0x18000 + i * 4, frame[i]);
                code.CopyTo(Memory.Ram, 0x12000); Cpu.SetRegister(Register.Sp, 0x18000); PagingTests.At(Cpu, Memory, [0xcf]);
                Check(Cpu.Virtual8086 && Cpu.PagingEnabled, "Actual paged V86 entry missing.");
            }
            if (mode != 0) Gate(Memory, 0x20, mode == 1 ? (ushort)24 : (ushort)8, 0x6000, mode == 1 ? (byte)0x86 : (byte)0x8e);
            new byte[] { 0xb0, 0x20, 0xe6, 0x20, 0xcf }.CopyTo(Memory.Ram, 0x6000);
            Pic.Write(0x20, 0x11, 1); Pic.Write(0x21, 0x20, 1); Pic.Write(0x21, 4, 1); Pic.Write(0x21, 1, 1);
            Pic.Write(0xa0, 0x11, 1); Pic.Write(0xa1, 0x28, 1); Pic.Write(0xa1, 2, 1); Pic.Write(0xa1, 1, 1);
            Pic.Write(0x21, 0xfe, 1); Pic.Write(0xa1, 0xff, 1);
            Pit.Write(0x43, 0x34, 1); Pit.Write(0x40, 2, 1); Pit.Write(0x40, 0, 1);
        }
        /// <summary>Completes exactly one instruction and its fixed virtual core clocks, without fabricating an IRQ.</summary>
        public void Step() { uint cycles = Cpu.Step(); Check(cycles != 0); Clock.AdvanceCycles(cycles, Frequency); }
        /// <summary>Lets the next actual PIT deadline create an eligible PIC request without executing another instruction.</summary>
        public void Tick()
        {
            ulong deadline = Clock.NextEventTime ?? throw new Exception("Missing actual PIT deadline."); Clock.AdvanceTo(deadline);
            Check(Pit.InterruptPulses > 0 && Pic.HasPending, "Physical PIT pulse did not reach PIC IRQ0.");
        }
        /// <summary>Acknowledges only through the PIC after CPU recognition permits it, then checks the real interrupt frame.</summary>
        public void Interrupt(uint expectedIp, uint oldSp, ushort oldCs, uint oldFlags)
        {
            Check(Cpu.CanAcceptMaskableInterrupt && Pic.HasPending, "Incorrect STI recognition boundary.");
            byte? vector = Pic.Acknowledge(); Check(vector == 0x20); Cpu.EnterHardwareInterrupt(vector ?? throw new Exception("Missing actual PIC vector."));
            Pic.Write(0x20, 0x0b, 1); Check(Pic.Read(0x20, 1) == 1, "PIC IRQ0 was not genuinely in service.");
            int width = Mode is 0 or 1 ? 2 : 4;
            uint pointer = Cpu.GetRegister(Register.Sp) & (Cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffffu);
            Check(Read(Memory, pointer, width) == expectedIp && Read(Memory, pointer + (uint)width, width) == oldCs, "IRQ frame points to the wrong completed boundary.");
            Check((Read(Memory, pointer + (uint)(2 * width), width) & 0x3200) == (oldFlags & 0x3200), "IRQ frame lost IF/IOPL.");
            if (Mode == 3) Check(Memory.ReadDword(pointer + 12) == oldSp && Memory.ReadDword(pointer + 16) == 0, "V86 IRQ did not preserve its actual old stack.");
            Check((Cpu.Eflags & X86Cpu.InterruptEnable) == 0 && Cpu.Eip == 0x6000);
            Step(); Step(); Check(Pic.Read(0x20, 1) == 0, "Guest EOI did not release the actual PIC request."); Step();
            Check(Cpu.Eip == expectedIp && Cpu.GetSegment(Segment.Cs) == oldCs && Cpu.GetRegister(Register.Sp) == oldSp && Cpu.CanAcceptMaskableInterrupt, "Actual IRET did not restore the selected boundary.");
            Check(Cpu.Virtual8086 == (Mode == 3));
        }
    }
    /// <summary>Mapped endpoint sentinel: allocation validation must not manufacture local-variable reads or writes.</summary>
    private sealed class Endpoint : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Records an actual bus read without providing CPU stack storage.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x6d; }
        /// <summary>Records an actual bus write without changing unrelated RAM.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    /// <summary>Registers model-separated recognition, privilege, stack-limit, sequential-fault and restart checks.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (bool enabled in new[] { false, true })
        foreach (int prefix in new[] { 0, 1, 2, 3 })
            test($"Audit2 N1 STI actual PIT IRQ model {model} mode {mode} IF {enabled} prefixes {prefix}", () =>
            {
                byte[] prefixes = prefix switch { 1 => [0x66], 2 => [0x67], 3 => [0x66, 0x67], _ => [] };
                var f = new InterruptRig(model, mode, [..prefixes, 0xfb, 0x43, 0x90]); var c = f.Cpu;
                c.Eflags = (mode == 3 ? 0x23002u : 2u) | (enabled ? 0x200u : 0); c.SetRegister(Register.Bx, 0x13570000);
                f.Step(); Check(c.CanAcceptMaskableInterrupt == enabled, "STI extended an already enabled interrupt window."); f.Tick();
                if (!enabled) { Check(!c.CanAcceptMaskableInterrupt); f.Step(); }
                uint target = (uint)(0x2000 + prefixes.Length + 1 + (enabled ? 0 : 1));
                Check(c.Eip == target && c.GetRegister(Register.Bx) == 0x13570000u + (enabled ? 0u : 1u));
                f.Interrupt(target, c.GetRegister(Register.Sp), c.GetSegment(Segment.Cs), c.Eflags);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int mode in new[] { 0, 1, 2 })
        foreach (bool enabled in new[] { false, true })
            test($"Audit2 N1 consecutive STI does not renew consumed shadow model {model} mode {mode} IF {enabled}", () =>
            {
                var f = new InterruptRig(model, mode, [0xfb, 0xfb, 0x43]); f.Cpu.Eflags = enabled ? 0x202u : 2u;
                f.Step(); Check(f.Cpu.CanAcceptMaskableInterrupt == enabled); f.Step(); Check(f.Cpu.CanAcceptMaskableInterrupt); f.Tick();
                f.Interrupt(0x2002, f.Cpu.GetRegister(Register.Sp), f.Cpu.GetSegment(Segment.Cs), f.Cpu.Eflags);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int mode in new[] { 0, 1, 2 })
        foreach (bool pop in new[] { false, true })
            test($"Audit2 N1 prior {(pop ? "POP" : "MOV")} SS consumed by IF1 STI model {model} mode {mode}", () =>
            {
                byte[] load = pop ? [0x17] : [0x8e, 0xd0]; var f = new InterruptRig(model, mode, [..load, 0xfb, 0x43]); var c = f.Cpu;
                c.Eflags = 0x202; c.SetRegister(Register.Ax, c.GetSegment(Segment.Ss));
                Write(f.Memory, c.GetRegister(Register.Sp) & (mode == 2 ? uint.MaxValue : 0xffffu), c.GetSegment(Segment.Ss), mode == 2 ? 4 : 2);
                f.Step(); Check(!c.CanAcceptMaskableInterrupt, "SS load lost its own following-instruction shadow."); f.Step(); Check(c.CanAcceptMaskableInterrupt); f.Tick();
                f.Interrupt((uint)(0x2000 + load.Length + 1), c.GetRegister(Register.Sp), c.GetSegment(Segment.Cs), c.Eflags);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int mode in new[] { 0, 1, 2 })
            test($"Audit2 N1 following MOV SS retains its own shadow model {model} mode {mode}", () =>
            {
                var f = new InterruptRig(model, mode, [0xfb, 0x8e, 0xd0, 0x43]); var c = f.Cpu; c.Eflags = 2; c.SetRegister(Register.Ax, c.GetSegment(Segment.Ss));
                f.Step(); Check(!c.CanAcceptMaskableInterrupt); f.Step(); Check(!c.CanAcceptMaskableInterrupt); f.Tick(); f.Step();
                f.Interrupt(0x2004, c.GetRegister(Register.Sp), c.GetSegment(Segment.Cs), c.Eflags);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int ring in new[] { 1, 2, 3 })
        foreach (bool enabled in new[] { false, true })
            test($"Audit2 N1 forbidden STI keeps old IF and exact GP restart frame model {model} ring {ring} IF {enabled}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0xfb, 0x90], model: model); Ring(c, m, ring); c.Eflags = enabled ? 0x202u : 2u;
                uint oldSp = c.GetRegister(Register.Sp); byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                uint frame = 0x1a000 - 24; Check(vector == 13 && c.Eip == 0x6000 && c.CurrentPrivilegeLevel == 0 && c.GetRegister(Register.Sp) == frame);
                Check(m.ReadWord(frame) == 0 && m.ReadDword(frame + 4) == 0x2000 && m.ReadDword(frame + 8) == (uint)(0x28 | ring));
                uint flags = m.ReadDword(frame + 12); Check((flags & 0x10200) == (0x10000u | (enabled ? 0x200u : 0)) && m.ReadDword(frame + 16) == oldSp);
                m.WriteDword(frame + 12, flags | 0x3000); ReturnError(c, m); Check(c.CurrentPrivilegeLevel == ring); c.Step();
                Check(c.Eip == 0x2001 && c.CanAcceptMaskableInterrupt == enabled, "Successful retry changed IF0/IF1 shadow semantics.");
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool enabled in new[] { false, true })
            test($"Audit2 N1 STI TF trap commits IF and does not suppress DB model {model} IF {enabled}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0xfb, 0x90], model: model); c.Eflags = 0x102u | (enabled ? 0x200u : 0); c.Step();
                uint frame = c.GetRegister(Register.Sp); Check(c.SingleStepTraps == 1 && c.Eip == 0x6000 && m.ReadDword(frame) == 0x2001 && (m.ReadDword(frame + 8) & 0x300) == 0x300);
                m.WriteDword(frame + 8, m.ReadDword(frame + 8) & ~0x100u); m.WriteByte(0x6000, 0xcf); c.Step(); Check(c.Eip == 0x2001 && c.CanAcceptMaskableInterrupt);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int iopl in new[] { 0, 1, 2 })
        foreach (bool enabled in new[] { false, true })
            test($"Audit2 N1 V86 forbidden STI retains full VM GP frame model {model} IOPL {iopl} IF {enabled}", () =>
            {
                var f = new InterruptRig(model, 3, [0xfb, 0x90]); var c = f.Cpu; var m = f.Memory; uint oldSp = c.GetRegister(Register.Sp);
                c.Eflags = 0x20002u | (uint)(iopl << 12) | (enabled ? 0x200u : 0); byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                uint frame = 0x1a000 - 40; Check(vector == 13 && c.Eip == 0x6000 && c.GetRegister(Register.Sp) == frame && !c.Virtual8086);
                Check(m.ReadWord(frame) == 0 && m.ReadDword(frame + 4) == 0x2000 && m.ReadDword(frame + 8) == 0x1000 && m.ReadDword(frame + 16) == oldSp);
                Check((m.ReadDword(frame + 12) & 0x33200) == (0x30000u | (uint)(iopl << 12) | (enabled ? 0x200u : 0)), "V86 protection fault lost VM/RF/IOPL or original IF.");
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool enabled in new[] { false, true })
            test($"Audit2 N1 SS then STI preserves deferred TF model {model} IF {enabled}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x8e, 0xd0, 0xfb, 0x90], model: model); c.SetRegister(Register.Ax, 16); c.Eflags = 0x102u | (enabled ? 0x200u : 0);
                c.Step(); Check(c.SingleStepTraps == 0 && !c.CanAcceptMaskableInterrupt); c.Step(); uint frame = c.GetRegister(Register.Sp);
                Check(c.SingleStepTraps == 1 && c.Eip == 0x6000 && m.ReadDword(frame) == 0x2003 && (m.ReadDword(frame + 8) & 0x300) == 0x300);
                m.WriteDword(frame + 8, m.ReadDword(frame + 8) & ~0x100u); m.WriteByte(0x6000, 0xcf); c.Step(); Check(c.Eip == 0x2003 && c.CanAcceptMaskableInterrupt);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool code32 in new[] { false, true })
        foreach (bool stack32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
        foreach (byte rawNesting in new byte[] { 0, 1, 3, 31, 32, 255 })
            test($"Audit2 N2 ENTER preserved frame bytes model {model} code32 {code32} stack32 {stack32} operand {width} nesting {rawNesting}", () =>
            {
                byte[] code = Enter(code32, width, 0x25, rawNesting, true); var (c, m) = ProtectedModeTests.Rig(code, code32: code32, stack32: stack32, model: model);
                uint oldSp = stack32 ? 0x9000u : 0xabcd9000u, oldBp = width == 2 ? 0xabcd7000u : 0x7000u, mask = stack32 ? uint.MaxValue : 0xffffu;
                c.SetRegister(Register.Sp, oldSp); c.SetRegister(Register.Bp, oldBp); c.Eflags = 0x602; int nesting = rawNesting & 31; FillChain(m, 0x7000, width, nesting);
                uint frame = ((oldSp & mask) - (uint)width) & mask; uint final = ((oldSp & mask) - (uint)((nesting == 0 ? 1 : nesting + 1) * width) - 0x25) & mask;
                c.Step(); Check(c.Eip == 0x2000 + code.Length && c.Eflags == 0x602 && !c.TripleFaulted);
                Check(c.GetRegister(Register.Sp) == (stack32 ? final : oldSp & 0xffff0000 | final));
                Check(c.GetRegister(Register.Bp) == (width == 4 ? frame : oldBp & 0xffff0000 | frame & 0xffff), "Existing operand-selected frame-pointer contract changed.");
                Check(Read(m, frame, width) == (oldBp & (width == 2 ? 0xffffu : uint.MaxValue)));
                for (int i = 1; i < nesting; i++) Check(Read(m, (oldSp & mask) - (uint)((i + 1) * width), width) == Chain(i, width));
                if (nesting != 0) Check(Read(m, (oldSp & mask) - (uint)((nesting + 1) * width), width) == (frame & (width == 2 ? 0xffffu : uint.MaxValue)));
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool stack32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
        foreach (bool down in new[] { false, true })
        foreach (byte nesting in new byte[] { 0, 1, 5, 31 })
            test($"Audit2 N2 ENTER final SS fault and retry model {model} stack32 {stack32} operand {width} expandDown {down} nesting {nesting}", () =>
            {
                byte[] code = Enter(true, width, 0x200, nesting); var (c, m) = ProtectedModeTests.Rig(code, model: model);
                uint oldLow = down ? 0x9000u : 0x100u, oldSp = stack32 ? oldLow : 0xabcd0000 | oldLow, oldBp = down ? 0x9a00u : 0x400u;
                Descriptor(m, 40, 0, down ? 0x8f00u : 0x7fffu, down ? (byte)0x96 : (byte)0x92, stack32 ? (byte)4 : (byte)0);
                c.SetSegment(Segment.Ss, 40); c.SetRegister(Register.Sp, oldSp); c.SetRegister(Register.Bp, oldBp); c.Eflags = 0x602; FillChain(m, oldBp, width, nesting);
                byte? vector = null; c.InterruptEntered += v => vector = v; c.Step(); uint faultFrame = oldLow - 16;
                Check(vector == 12 && c.Eip == 0x6000 && !c.TripleFaulted && c.GetRegister(Register.Bp) == oldBp);
                Check((c.GetRegister(Register.Sp) & (stack32 ? uint.MaxValue : 0xffffu)) == faultFrame && m.ReadWord(faultFrame) == 0 && m.ReadDword(faultFrame + 4) == 0x2000);
                Check((m.ReadDword(faultFrame + 12) & 0x10600) == 0x10600, "SS fault did not retain original IF/DF and restart RF.");
                uint firstFrame = oldLow - (uint)width;
                if ((nesting + 1) * width > 16 && nesting != 0) Check(Read(m, oldLow - (uint)((nesting + 1) * width), width) == firstFrame, "Earlier sequential ENTER write was incorrectly rolled back.");
                Descriptor(m, 40, 0, down ? 0u : stack32 ? 0xfffffu : 0xffffu, down ? (byte)0x96 : (byte)0x92, stack32 ? (byte)(down ? 4 : 12) : (byte)0);
                c.SetSegment(Segment.Ss, 40); ReturnError(c, m); Check(c.GetRegister(Register.Sp) == oldSp && c.GetRegister(Register.Bp) == oldBp); c.Step();
                uint mask = stack32 ? uint.MaxValue : 0xffffu, final = unchecked(oldLow - (uint)((nesting == 0 ? 1 : nesting + 1) * width) - 0x200) & mask;
                Check(c.Eip == 0x2000 + code.Length && c.GetRegister(Register.Sp) == (stack32 ? final : oldSp & 0xffff0000 | final));
                Check(c.GetRegister(Register.Bp) == firstFrame && (c.Eflags & 0x600) == 0x600);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
            test($"Audit2 N2 ENTER legal sixteen-bit wrap preserves upper ESP model {model} operand {width}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(Enter(true, width, 0x200, 0), stack32: false, model: model); c.SetRegister(Register.Sp, 0xabcd0100); c.SetRegister(Register.Bp, 0x1234); c.Step();
                Check(c.Eip == (width == 2 ? 0x2005u : 0x2004u) && c.GetRegister(Register.Sp) == (0xabcd0000u | ((0x100u - (uint)width - 0x200u) & 0xffff)));
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
            test($"Audit2 N2 ENTER endpoint at FFFF is a pointer check model {model} operand {width}", () =>
            {
                byte[] code = Enter(true, width, (ushort)(0x101 - width), 0); var (c, m) = ProtectedModeTests.Rig(code, stack32: false, model: model);
                c.SetRegister(Register.Sp, 0xabcd0100); c.SetRegister(Register.Bp, 0x1234); c.Step(); Check(c.Eip == 0x2000 + code.Length && c.GetRegister(Register.Sp) == 0xabcdffff);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool stack32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
        foreach (bool valid in new[] { false, true })
            test($"Audit2 N2 ENTER exact expand-down endpoint model {model} stack32 {stack32} operand {width} valid {valid}", () =>
            {
                ushort allocation = (ushort)(0x9000 - width - (valid ? 0x8f01 : 0x8f00)); byte[] code = Enter(true, width, allocation, 0);
                var (c, m) = ProtectedModeTests.Rig(code, model: model); Descriptor(m, 40, 0, 0x8f00, 0x96, stack32 ? (byte)4 : (byte)0); c.SetSegment(Segment.Ss, 40);
                c.SetRegister(Register.Sp, stack32 ? 0x9000u : 0xabcd9000); c.SetRegister(Register.Bp, 0x9a00); byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                if (valid) Check(vector is null && c.Eip == 0x2000 + code.Length && c.GetRegister(Register.Sp) == (stack32 ? 0x8f01u : 0xabcd8f01));
                else Check(vector == 12 && c.Eip == 0x6000 && c.GetRegister(Register.Bp) == 0x9a00 && m.ReadDword((c.GetRegister(Register.Sp) & 0xffff) + 4) == 0x2000);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool badFinal in new[] { false, true })
            test($"Audit2 N2 ENTER nested read PF precedes final allocation check model {model} badFinal {badFinal}", () =>
            {
                var (c, m) = PagingTests.Create(Enter(true, 4, badFinal ? (ushort)0x200 : (ushort)0x20, 3), model: model);
                if (badFinal) { Descriptor(m, 40, 0, 0xffff, 0x92, 4); c.SetSegment(Segment.Ss, 40); c.SetRegister(Register.Sp, 0x100); }
                else c.SetRegister(Register.Sp, 0x9000);
                c.SetRegister(Register.Bp, 0x7004); m.WriteDword(0x7000, 0x11223344); m.WriteDword(0x6ffc, 0x55667788); PagingTests.Pte(m, 0x7000, 0x7000, 0);
                byte? vector = null; c.InterruptEntered += v => vector = v; c.Step(); Check(vector == 14 && c.Cr2 == 0x7000 && c.PageFaults == 1 && c.GetRegister(Register.Bp) == 0x7004);
                uint frame = c.GetRegister(Register.Sp); Check(m.ReadWord(frame) == 0 && m.ReadDword(frame + 4) == 0x2000);
                PagingTests.Pte(m, 0x7000, 0x7000); uint ax = c.GetRegister(Register.Ax); c.SetRegister(Register.Ax, c.Cr3); PagingTests.At(c, m, [0x0f, 0x22, 0xd8]); c.SetRegister(Register.Ax, ax); ReturnError(c, m); c.Step();
                Check(badFinal ? vector == 12 && c.Eip == 0x6000 : c.Eip == 0x2004, "Fault retry did not retain the original sequential ordering.");
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool split in new[] { false, true })
            test($"Audit2 N2 ENTER real user stack write PF and retry model {model} split {split}", () =>
            {
                var (c, m) = PagingTests.Create(Enter(true, 4, 0x20, 0), model: model); Ring(c, m, 3);
                uint oldSp = split ? 0x1b002u : 0x1b004u; c.SetRegister(Register.Sp, oldSp); c.SetRegister(Register.Bp, 0x12345678);
                m.WriteDword(oldSp - 4, 0xa5a5a5a5); PagingTests.Pte(m, 0x1b000, 0x1b000, 5); byte? vector = null; c.InterruptEntered += v => vector = v; c.Step();
                uint frame = 0x1a000 - 24; Check(vector == 14 && c.Eip == 0x6000 && c.GetRegister(Register.Sp) == frame && c.Cr2 == 0x1b000 && m.ReadWord(frame) == 7);
                Check(m.ReadDword(frame + 4) == 0x2000 && m.ReadDword(frame + 16) == oldSp && m.ReadDword(frame + 20) == 0x33 && m.ReadDword(oldSp - 4) == 0xa5a5a5a5);
                Check(c.GetRegister(Register.Bp) == 0x12345678); PagingTests.Pte(m, 0x1b000, 0x1b000); uint ax = c.GetRegister(Register.Ax); c.SetRegister(Register.Ax, c.Cr3); PagingTests.At(c, m, [0x0f, 0x22, 0xd8]); c.SetRegister(Register.Ax, ax); ReturnError(c, m); c.Step();
                Check(c.CurrentPrivilegeLevel == 3 && c.Eip == 0x2004 && c.GetRegister(Register.Sp) == oldSp - 4 - 0x20 && c.GetRegister(Register.Bp) == oldSp - 4 && m.ReadDword(oldSp - 4) == 0x12345678);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test($"Audit2 N2 ENTER endpoint validation invents no MMIO access model {model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(Enter(true, 4, 0x3000, 0), model: model); var endpoint = new Endpoint(); m.Map(0x14000, 0x1000, endpoint, "Own unused ENTER locals"); c.SetRegister(Register.Sp, 0x18000); c.SetRegister(Register.Bp, 0x1234); c.Step();
                Check(c.Eip == 0x2004 && c.GetRegister(Register.Sp) == 0x14ffc && endpoint.Reads == 0 && endpoint.Writes == 0);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
            test($"Audit2 N2 ENTER TF trap uses committed allocation model {model} operand {width}", () =>
            {
                byte[] code = Enter(true, width, 0x20, 0); var (c, m) = ProtectedModeTests.Rig(code, model: model); c.SetRegister(Register.Sp, 0x18000); c.SetRegister(Register.Bp, 0x1234); c.Eflags = 0x302; c.Step();
                uint final = 0x18000u - (uint)width - 0x20; Check(c.SingleStepTraps == 1 && c.Eip == 0x6000 && c.GetRegister(Register.Sp) == final - 12 && c.GetRegister(Register.Bp) == ((0x18000u - (uint)width) & (width == 2 ? 0xffffu : uint.MaxValue)));
                Check(m.ReadDword(final - 12) == 0x2000 + code.Length && (m.ReadDword(final - 4) & 0x300) == 0x300);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool enter in new[] { false, true })
            test($"Audit2 CPU LOCK rejection precedes {(enter ? "ENTER" : "STI")} effects model {model}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(enter ? [0xf0, 0xc8, 0x20, 0, 0] : [0xf0, 0xfb], model: model); c.Eflags = 2; c.SetRegister(Register.Bp, 0x1234); Fault(c, m, 6, null);
                Check(c.GetRegister(Register.Bp) == 0x1234 && (m.ReadDword(c.GetRegister(Register.Sp) + 8) & 0x200) == 0);
            });
    }
}
