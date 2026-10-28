// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual CF9 instructions and coordinated reset-domain futures on complete own-media PCs.</summary>
internal static class PiixResetPcTests
{
    /// <summary>Selects the fixed own reset board and optional real ISA peers.</summary>
    private static MachineProfile Profile(bool peripherals = false) => new() { Id = MachineProfile.PiixResetProfileId,
        Name = "Own CF9 reset PC", Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        SoundCard = peripherals ? SoundBlaster16.ProfileId : "none", FloppyDriveA = peripherals ? "1440k" : "none", FloppyDriveB = peripherals ? "1440k" : "none" };
    /// <summary>Requires an independently expected instruction, byte, pin or full-state result.</summary>
    private static void Check(bool condition, string message = "PIIX reset whole-PC invariant") { if (!condition) throw new Exception(message); }
    /// <summary>Captures all explicitly registered state, including timer tokens and future publication order.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Captures one peer without mutating its bus-facing state.</summary>
    private static string Peer(IStateObject peer) => JsonSerializer.Serialize(peer.SaveState(), StateValues.Json);
    /// <summary>Requires every registered state field and every installed RAM byte to agree.</summary>
    private static void Same(PcMachine a, PcMachine b) { Check(State(a) == State(b), "Complete state graph differs"); Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete32MiB RAM differs"); }
    /// <summary>Installs and executes only an own short loop with an actual raw x87 constant.</summary>
    private static void OwnCode(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0);
        m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eflags = 2; m.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0xdb, 0xe3, 0xd9, 0xeb, 0x90, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000);
        Check(m.RunSteps(3) == 3 && m.Fault is null && m.Cpu.FpuTop == 7);
    }
    /// <summary>Runs a real MOV DX/MOV accumulator/OUT sequence, stopping immediately after the completed OUT.</summary>
    private static void Out(PcMachine m, ushort port, uint value, int width)
    {
        var code = new List<byte> { 0xba, (byte)port, (byte)(port >> 8) };
        if (width == 1) code.AddRange([0xb0, (byte)value, 0xee]);
        else if (width == 2) code.AddRange([0xb8, (byte)value, (byte)(value >> 8), 0xef]);
        else code.AddRange([0x66, 0xb8, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24), 0x66, 0xef]);
        code.CopyTo(m.Memory.Ram, 0x2000); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x2000; Check(m.RunSteps(3) == 3 && m.Fault is null, "Own OUT sequence failed");
    }
    /// <summary>Services only the owner boundary, before any instruction or additional virtual nanosecond.</summary>
    private static void Boundary(PcMachine m) { ulong now = m.Scheduler.Now; Check(m.RunSteps(1, now) == 0 && m.Scheduler.Now == now && m.Fault is null); }
    /// <summary>Enables both actual IDE channels and PCI master access through mechanism1.</summary>
    private static void EnableIde(PcMachine m)
    {
        m.HostBridge!.WriteConfiguration(0x64, 8, 1);
        m.Ports.Write(0xcf8, 0x80003920, 4); m.Ports.Write(0xcfc, 0xc001, 4);
        m.Ports.Write(0xcf8, 0x80003940, 4); m.Ports.Write(0xcfc, 0x80008000, 4);
        m.Ports.Write(0xcf8, 0x80003904, 4); m.Ports.Write(0xcfc, 7, 2);
    }
    /// <summary>Issues one actual command to a selected drive on a complete channel.</summary>
    private static void AtaCommand(PcMachine m, int channel, int drive, byte command, uint lba)
    {
        ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
        m.Ports.Write((ushort)(port + 6), 0xe0u | (uint)(drive << 4), 1); m.Ports.Write((ushort)(port + 2), 1, 1);
        m.Ports.Write((ushort)(port + 3), lba, 1); m.Ports.Write((ushort)(port + 4), 0, 1); m.Ports.Write((ushort)(port + 5), 0, 1); m.Ports.Write((ushort)(port + 7), command, 1);
    }
    /// <summary>Borrows only the known four-slot field for read-only identity/sector observations, without acquiring or replacing a lease.</summary>
    private static IReadOnlyBlockDevice Disk(PcMachine m, int slot)
    {
        var field = typeof(PcMachine).GetField("_disks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if ((uint)slot >= 4 || field?.GetValue(m) is not IReadOnlyBlockDevice?[] disks || disks.Length != 4 || disks[slot] is not { } disk)
            throw new Exception("Own fixed disk topology unavailable to the read-only test observer.");
        return disk;
    }
    /// <summary>Reads one borrowed own protected medium through its published sector API.</summary>
    private static byte[] Sector(PcMachine m, int slot, long lba) { byte[] bytes = new byte[512]; Disk(m, slot).ReadSector(lba, bytes); return bytes; }
    /// <summary>Creates four fresh RAW/COW hard disks and optional fresh floppy generations, without touching external media.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/piix-reset-pc-" + Guid.NewGuid().ToString("N")));
        public readonly PcMachine Machine;
        /// <summary>Writes all own immutable bases before acquiring any journal lease.</summary>
        public Fixture(bool peripherals = false)
        {
            Directory.CreateDirectory(Root); var disks = new List<DiskAttachment>(); var floppies = new List<DiskAttachment>();
            for (int slot = 0; slot < 4; slot++)
            {
                byte[] bytes = new byte[1048576]; DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(bytes);
                for (int i = 512; i < bytes.Length; i++) bytes[i] = unchecked((byte)(i * 13 + slot * 41));
                string path = PathFor("own-base" + slot + ".img"); File.WriteAllBytes(path, bytes); disks.Add(new(slot, path, PathFor("own-active" + slot + ".cow")));
            }
            if (peripherals) for (int slot = 0; slot < 2; slot++)
            { string path = PathFor("own-floppy" + slot + ".img"); File.WriteAllBytes(path, new byte[1474560]); floppies.Add(new(slot, path, PathFor("own-floppy" + slot + ".cow"))); }
            Machine = MachineFactory.FromDefinition(new() { Hardware = Profile(peripherals), Name = "Own reset state fixture", HardDisks = disks.ToArray(), Floppies = floppies.ToArray() }); OwnCode(Machine);
        }
        /// <summary>Names only a new child of this private fixture.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Releases all leases before deleting only the verified own directory.</summary>
        public void Dispose()
        {
            Machine.Dispose(); string resolved = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(resolved) == parent && Path.GetFileName(resolved).StartsWith("piix-reset-pc-", StringComparison.Ordinal)); Directory.Delete(resolved, true);
        }
    }
    /// <summary>Starts genuine automatic PCM byte DMA with a long block and at least one actual grant.</summary>
    private static void Pcm(PcMachine m)
    {
        m.SoundBlaster16!.Write(0x226, 1, 1); m.Scheduler.AdvanceTo(m.Scheduler.Now + 3000); m.SoundBlaster16!.Write(0x226, 0, 1);
        m.Scheduler.AdvanceTo(m.Scheduler.Now + 100000); Check(m.SoundBlaster16!.Read(0x22a, 1) == 0xaa);
        m.Memory.Ram.AsSpan(0x12000, 1024).Fill(0x69); m.Dma.Write(0x0a, 5, 1); m.Dma.Write(0x0c, 0, 1);
        m.Dma.Write(2, 0, 1); m.Dma.Write(2, 0x20, 1); m.Dma.Write(3, 0xff, 1); m.Dma.Write(3, 3, 1); m.Dma.Write(0x83, 1, 1); m.Dma.Write(0x0b, 0x59, 1); m.Dma.Write(0x0a, 1, 1);
        foreach (byte b in new byte[] { 0x41, 0x1f, 0x40, 0xc6, 0, 0xff, 3 })
        { Check(m.SoundBlaster16!.Read(0x22c, 1) == 0); m.SoundBlaster16!.Write(0x22c, b, 1); m.Scheduler.AdvanceTo(m.Scheduler.Now + 1000); }
        m.Scheduler.AdvanceTo(m.Scheduler.Now + 250000); m.Audio.AdvanceTo(m.Scheduler.Now);
        Check(m.SoundBlaster16!.Active && m.SoundBlaster16!.Dma8Grants > 0, "PCM must actually be live before reset");
    }
    /// <summary>Registers actual decoder, reset, retained-byte and complete sealed-container continuations.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 reset factory creates real fixed peers with physical P54C power-on x87", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Check(m.Profile.IsPiixReset && m.Profile.IsPiixIde && m.Pci!.FunctionCount == 3);
            Check(m.PiixReset!.HasBindings(m.Scheduler, m.Pci!, m) && m.PiixReset!.Phase == PiixResetPhase.Idle && m.PiixReset!.ControlStored == 0);
            Check(m.Cpu.FpuControlWord == 0x0040 && m.Cpu.FpuTagWord == 0x5555 && m.Cpu.TimestampCounter == 0 && m.Cpu.Idtr.Limit == 0xffff);
            m.CaptureHardwareState();
        });
        foreach (int old in new[] { 0, 1, 2, 3, 4 })
            test("M4 CF9 new decoder leaves earlier product boards isolated " + old, () =>
            {
                var profile = old == 0 ? new MachineProfile() : old == 1 ? new MachineProfile().WithDevelopmentCpu(true) : Profile() with { Id = old == 2 ? MachineProfile.FxHostProfileId : old == 3 ? MachineProfile.PiixIrqProfileId : MachineProfile.PiixIdeProfileId };
                using var m = PcMachine.CreateDemo(profile); OwnCode(m); Out(m, 0xcf9, 6, 1);
                Check(m.PiixReset is null && m.HardwareResetCount == 0 && !m.CpuResetHeld && m.Cpu.GetSegment(Segment.Cs) == 0 && m.Cpu.Eip == 0x2006);
            });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 actual CF8 OUT cannot split into a hidden CF9 reset width " + width, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); OwnCode(m); Out(m, 0xcf8, 0x80000604, 4); uint latched = m.Pci!.ConfigurationAddress;
                Check(latched == 0x80000604 && m.PiixReset!.PendingRequest == PiixResetRequest.None && m.PiixReset!.SoftEdges == 0 && m.PiixReset!.HardEdges == 0);
                Out(m, 0xcf8, 0x80000604, width); Boundary(m);
                Check(m.Pci!.ConfigurationAddress == latched && m.HardwareResetCount == 0 && m.PiixReset!.ControlStored == 0 && m.Ports.Read(0xcf9, width) == (width == 1 ? 0u : width == 2 ? 0xff00u : 0xffffff00u));
            });
        foreach (int width in new[] { 1, 2, 4 }) foreach (bool hard in new[] { false, true })
            test($"M4 actual CF9 OUT samples only low byte width {width} hard {hard}", () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); OwnCode(m); uint address = 0x80003800; m.Ports.Write(0xcf8, address, 4);
                long before = m.Cpu.Instructions; Out(m, 0xcf9, 0xa5a50000u | (hard ? 6u : 4), width);
                Check(m.PiixReset!.PendingRequest == (hard ? PiixResetRequest.Hard : PiixResetRequest.Init) && m.HardwareResetCount == 0 && m.Cpu.Instructions == before + 3 && m.Pci!.ConfigurationAddress == address);
                ulong now = m.Scheduler.Now; Boundary(m); Check(m.HardwareResetCount == 1 && m.LastResetTime == now && m.Scheduler.Now == now);
                Check(m.Cpu.Eip == 0xfff0 && m.Cpu.GetSegment(Segment.Cs) == 0xf000 && m.CpuResetHeld == hard && m.PiixReset!.Phase == (hard ? PiixResetPhase.AllAsserted : PiixResetPhase.Idle));
                Check(hard ? m.Cpu.FpuControlWord == 0x0040 && m.Cpu.FpuTagWord == 0x5555 : m.Cpu.FpuControlWord == 0x037f && m.Cpu.FpuTop == 7);
            });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 CF9 reads do not rearm hidden RCPU and only a real low edge rearms width " + width, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); OwnCode(m); Out(m, 0xcf9, 4, width); Boundary(m);
                Check(m.Ports.Read(0xcf9, width) == (width == 1 ? 0u : width == 2 ? 0xff00u : 0xffffff00u) && m.PiixReset!.ControlStored == 4);
                Out(m, 0xcf9, 4, width); Boundary(m); Check(m.HardwareResetCount == 1 && m.PiixReset!.SoftEdges == 1 && m.PiixReset!.PendingRequest == PiixResetRequest.None);
                Out(m, 0xcf9, 0, width); Boundary(m); Out(m, 0xcf9, 4, width); Boundary(m);
                Check(m.HardwareResetCount == 2 && m.PiixReset!.SoftEdges == 2 && m.PiixReset!.HardEdges == 0);
            });
        test("M4 INIT retains all powered peer state RAM and a written high TSC at its boundary", () =>
        {
            using var f = new Fixture(); var m = f.Machine; EnableIde(m); m.Memory.Ram[^1] = 0x97;
            m.Cpu.SetRegister(Register.Cx, 0x10); m.Cpu.SetRegister(Register.Ax, 0xfffffffc); m.Cpu.SetRegister(Register.Dx, 0xffffffff);
            new byte[] { 0x0f, 0x30 }.CopyTo(m.Memory.Ram, 0x2000); m.Cpu.Eip = 0x2000; Check(m.RunSteps(1) == 1);
            Out(m, 0xcf9, 4, 1); ulong tsc = m.Cpu.TimestampCounter; byte[] ram = m.Memory.Ram.ToArray();
            IStateObject[] peers = [m.HostBridge!, m.Pci!, m.PiixIrq!, m.PiixIde!, m.Ata, m.SecondaryAta, m.Pic, m.Pit, m.Dma, m.Keyboard, m.Floppy, m.Video, m.Rtc, m.Audio];
            string[] before = peers.Select(Peer).ToArray(); object[] identities = [m.Cpu, m.Memory, m.Scheduler, m.Audio, Disk(m, 0)];
            Boundary(m); Check(m.Cpu.TimestampCounter == tsc && m.Cpu.FpuTop == 7 && m.Cpu.FpuControlWord == 0x037f && m.Memory.Ram.AsSpan().SequenceEqual(ram));
            Check(before.SequenceEqual(peers.Select(Peer)) && ReferenceEquals(identities[0], m.Cpu) && ReferenceEquals(identities[1], m.Memory) && ReferenceEquals(identities[2], m.Scheduler) && ReferenceEquals(identities[3], m.Audio) && ReferenceEquals(identities[4], Disk(m, 0)));
        });
        foreach (bool sce in new[] { false, true })
            test("M4 real CPU triple fault follows original SCE shutdown policy enabled " + sce, () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); OwnCode(m); m.PiixIrq!.WriteConfiguration(4, sce ? 15u : 7, 2);
                m.Memory.WriteWord(0x300, 0); m.Memory.WriteDword(0x302, 0x8000);
                new byte[] { 0x0f, 0x01, 0x1e, 0, 3, 0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0xcd, 0x40 }.CopyTo(m.Memory.Ram, 0x2000);
                m.Cpu.Eip = 0x2000; Check(m.RunSteps(4) == 4 && m.Cpu.TripleFaulted && m.HardwareResetCount == 0 && m.Fault is null, "Guest must really exhaust exception delivery");
                long instructions = m.Cpu.Instructions; ulong now = m.Scheduler.Now; ushort tags = m.Cpu.FpuTagWord; var raw = m.Cpu.GetFpuRegister(0);
                if (!sce)
                { Check(m.RunSteps(30, now + 1000) == 0 && m.Cpu.TripleFaulted && m.Cpu.Instructions == instructions && m.HardwareResetCount == 0 && m.Scheduler.Now == now + 1000 && m.GetStatus().State == MachineRunState.Halted); }
                else
                { Check(m.RunSteps(1) == 1 && !m.Cpu.TripleFaulted && m.HardwareResetCount == 1 && m.LastResetSource == "pci-shutdown-init" && !m.CpuResetHeld && m.Cpu.Instructions == instructions + 1 && m.Cpu.FpuTagWord == tags && m.Cpu.GetFpuRegister(0) == raw); }
            });
        test("M4 hard reset retains RAM VRAM RTC media identities and uses exact split release boundaries", () =>
        {
            using var f = new Fixture(); var m = f.Machine; EnableIde(m); m.Memory.Ram[^1] = 0x7d; m.Memory.WriteDword(0x5000, 0x12345678); m.Memory.WriteByte(0xb8000, 0x69);
            m.Rtc.Write(0x70, 0x38, 1); m.Rtc.Write(0x71, 0xa7, 1); m.Memory.WriteWord(0x472, 0x1234);
            Out(m, 0xcf9, 6, 1); byte[] ram = m.Memory.Ram.ToArray(); string planes = m.Video.SaveState().Fields["_planes"].GetRawText(), rtc = Peer(m.Rtc);
            object[] peers = [m.Cpu, m.Memory, m.Scheduler, m.Video, m.Rtc, m.Ata, m.SecondaryAta, m.Pic, m.PiixIde!, Disk(m, 0)];
            Boundary(m); var reset = m.PiixReset!; ulong start = reset.AssertedAt;
            Check(reset.Phase == PiixResetPhase.AllAsserted && m.CpuResetHeld && m.Cpu.Instructions == 0 && m.Cpu.TimestampCounter == 0 && m.LastWarmBootFlag == 0x1234 && m.Memory.DecodedRamBytes == 8 * 1048576);
            m.Scheduler.AdvanceTo(start + 999999); Check(reset.PciIsaResetHeld && reset.CpuResetHeld && !reset.CpuReleaseReady && m.Cpu.Instructions == 0);
            m.Scheduler.AdvanceTo(start + 1000000); Check(reset.Phase == PiixResetPhase.CpuOnly && !reset.PciIsaResetHeld && reset.CpuResetHeld && !reset.CpuReleaseReady && m.Cpu.Instructions == 0);
            m.Scheduler.AdvanceTo(start + 1999999); Check(reset.CpuResetHeld && !reset.CpuReleaseReady);
            m.Scheduler.AdvanceTo(start + 2000000); Check(reset.CpuResetHeld && reset.CpuReleaseReady && m.Cpu.Instructions == 0);
            Boundary(m); Check(!reset.CpuResetHeld && reset.Phase == PiixResetPhase.Idle && m.Cpu.TimestampCounter == 0 && m.Cpu.Instructions == 0 && m.Cpu.Eip == 0xfff0);
            Check(m.Memory.Ram.AsSpan().SequenceEqual(ram) && m.Video.SaveState().Fields["_planes"].GetRawText() == planes && Peer(m.Rtc) == rtc);
            object[] after = [m.Cpu, m.Memory, m.Scheduler, m.Video, m.Rtc, m.Ata, m.SecondaryAta, m.Pic, m.PiixIde!, Disk(m, 0)];
            Check(peers.Zip(after).All(p => ReferenceEquals(p.First, p.Second)));
            Check(m.RunSteps(1) == 1 && m.Cpu.TimestampCounter == 4 && m.Cpu.Instructions == 1 && m.Fault is null);
        });
        foreach (bool alreadyHigh in new[] { false, true })
            test("M4 hard reset establishes busy SRST on all four ATA devices even after SRST-high " + alreadyHigh, () =>
            {
                using var f = new Fixture(); var m = f.Machine; EnableIde(m);
                if (alreadyHigh) { m.Ata.Write(0x3f6, 4, 1); m.SecondaryAta.Write(0x376, 4, 1); }
                Out(m, 0xcf9, 6, 1); Boundary(m); var graph = m.CaptureHardwareState();
                for (int channel = 0; channel < 2; channel++) for (int drive = 0; drive < 2; drive++)
                {
                    var fields = graph.Components[$"ata{channel}/_drives/{drive}"].Fields;
                    Check(StateValues.Read<bool>(fields["_resetActive"]) && StateValues.Read<byte>(fields["_control"]) == 4 && StateValues.Read<byte>(fields["Status"]) == 0x80);
                    Check(StateValues.Read<long>(fields["_event"]) == 0 && StateValues.Read<AtaDmaDirection>(fields["_dmaDirection"]) == AtaDmaDirection.None && !StateValues.Read<bool>(fields["_writing"]));
                }
                m.Scheduler.AdvanceTo(m.PiixReset!.PciIsaReleaseAt); graph = m.CaptureHardwareState();
                for (int channel = 0; channel < 2; channel++) for (int drive = 0; drive < 2; drive++)
                {
                    var fields = graph.Components[$"ata{channel}/_drives/{drive}"].Fields;
                    Check(!StateValues.Read<bool>(fields["_resetActive"]) && StateValues.Read<byte>(fields["_control"]) == 0 && StateValues.Read<byte>(fields["Status"]) == 0x50);
                }
            });
        test("M4 hard reset cancels actual pending ATA commit BM grant PCM and FDC seek without rollback", () =>
        {
            using var f = new Fixture(true); var m = f.Machine; EnableIde(m); Pcm(m);
            AtaCommand(m, 0, 0, 0x30, 7); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            for (int i = 0; i < 256; i++) m.Ports.Write(0x1f0, 0x5a5a, 2);
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000); Check(Sector(m, 0, 7).All(v => v == 0x5a) && m.Ata.WrittenSectorCount == 1);
            m.Memory.Ram.AsSpan(0x20000, 512).Fill(0xa5);
            BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan(0x10000, 4), 0x20000); BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan(0x10004, 4), 0x80000200);
            AtaCommand(m, 0, 0, 0xc8, 3); m.Ports.Write(0xc004, 0x10000, 4); m.Ports.Write(0xc000, 9, 1);
            for (int i = 0; m.PiixIde!.GetChannelState(0).BytesTransferred == 0 && i < 50; i++) m.Scheduler.AdvanceTo(m.Scheduler.NextEventTime!.Value);
            Check(m.PiixIde!.GetChannelState(0).BytesTransferred == 32 && m.PiixIde!.GetChannelState(0).Active && m.Memory.Ram.AsSpan(0x20000, 32).SequenceEqual(Sector(m, 0, 3).AsSpan(0, 32)));
            byte[] beforeTarget = Sector(m, 3, 8); AtaCommand(m, 1, 1, 0x30, 8); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            for (int i = 0; i < 256; i++) m.Ports.Write(0x170, 0xb7b7, 2);
            Check((m.SecondaryAta.Status & 0x80) != 0 && m.SecondaryAta.WrittenSectorCount == 0);
            m.Floppy.Write(0x3f2, 0x1c, 1); m.Floppy.Write(0x3f5, 0x0f, 1); m.Floppy.Write(0x3f5, 0, 1); m.Floppy.Write(0x3f5, 10, 1); Check(m.Floppy.Busy);
            var before = m.CaptureHardwareState(); var oldEvents = before.Scheduler.Events.Where(e => !before.Scheduler.Cancelled.Contains(e.Id) && (e.Owner.StartsWith("ata", StringComparison.Ordinal) || e.Owner is "piix-ide" or "sb16" or "floppy")).Select(e => e.Id).ToHashSet();
            Check(oldEvents.Count >= 4, "At least four real old future publications must exist");
            Out(m, 0xcf9, 6, 1); long samples = m.SoundBlaster16!.TransferredSamples, grants = m.SoundBlaster16!.Dma8Grants, frames = m.Audio.GeneratedFrames; byte[] retained = m.Memory.Ram.ToArray();
            Boundary(m); var held = m.CaptureHardwareState();
            Check(held.Scheduler.Events.All(e => !oldEvents.Contains(e.Id) || held.Scheduler.Cancelled.Contains(e.Id)), "Old work remained live after RSTDRV");
            Check(!m.SoundBlaster16!.Active && m.Ata.DmaDirection == AtaDmaDirection.None && !m.PiixIde!.GetChannelState(0).Active && !m.Floppy.Busy);
            m.Scheduler.AdvanceTo(m.PiixReset!.CpuReleaseAt); m.Audio.AdvanceTo(m.Scheduler.Now);
            Check(m.Memory.Ram.AsSpan().SequenceEqual(retained) && Sector(m, 0, 7).All(v => v == 0x5a) && Sector(m, 3, 8).SequenceEqual(beforeTarget));
            Check(m.Ata.WrittenSectorCount == 1 && m.SecondaryAta.WrittenSectorCount == 0 && m.SoundBlaster16!.TransferredSamples == samples && m.SoundBlaster16!.Dma8Grants == grants && m.Audio.GeneratedFrames >= frames);
            Check(StateValues.Read<byte[]>(m.Floppy.SaveState().Fields["_cylinder"])[0] == 0, "Aborted old seek moved the physical head");
        });
        foreach (string phase in new[] { "pending", "all", "cpu-only", "release-ready" })
            test("M4 complete CF9 reset SnapshotStore future with four protected COWs phase " + phase, () =>
            {
                using var f = new Fixture(); var m = f.Machine; EnableIde(m); m.Memory.Ram[^1] = 0x6b; Out(m, 0xcf9, 6, 4);
                if (phase != "pending") Boundary(m);
                if (phase == "all") m.Scheduler.AdvanceTo(m.PiixReset!.AssertedAt + 500000);
                if (phase == "cpu-only") m.Scheduler.AdvanceTo(m.PiixReset!.PciIsaReleaseAt + 250000);
                if (phase == "release-ready") m.Scheduler.AdvanceTo(m.PiixReset!.CpuReleaseAt);
                string saved = f.PathFor("reset.dossav"); SnapshotStore.Capture(m, saved); using var b = SnapshotStore.Load(saved, f.PathFor("reset.dosebranch")); Same(m, b);
                Check(m.PiixReset!.PendingRequest == b.PiixReset!.PendingRequest && m.CpuResetHeld == b.CpuResetHeld);
                if (phase == "pending") { Boundary(m); Boundary(b); Same(m, b); }
                if (m.PiixReset!.PciIsaResetHeld)
                { ulong at = m.PiixReset!.PciIsaReleaseAt; m.Scheduler.AdvanceTo(at); b.Scheduler.AdvanceTo(at); m.Audio.AdvanceTo(at); b.Audio.AdvanceTo(at); Same(m, b); }
                if (!m.PiixReset!.CpuReleaseReady)
                { ulong at = m.PiixReset!.CpuReleaseAt; m.Scheduler.AdvanceTo(at); b.Scheduler.AdvanceTo(at); m.Audio.AdvanceTo(at); b.Audio.AdvanceTo(at); Same(m, b); }
                Boundary(m); Boundary(b); Same(m, b); Check(m.Cpu.TimestampCounter == 0 && b.Cpu.TimestampCounter == 0 && m.Cpu.Eip == 0xfff0 && !m.CpuResetHeld);
                for (int i = 0; i < 12; i++) { Check(m.RunSteps(17) == b.RunSteps(17)); Same(m, b); }
                Check(m.Fault is null && b.Fault is null);
                for (int slot = 0; slot < 4; slot++) { Check(!ReferenceEquals(Disk(m, slot), Disk(b, slot))); Check(Sector(m, slot, 7).SequenceEqual(Sector(b, slot, 7))); }
            });
    }
}
