// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent PIIX A1 transactions, physical scatter DMA and raw future tests using only owned memory media.</summary>
internal static class PiixIdeTests
{
    /// <summary>Owned deterministic backend; only real complete sector writes change its content.</summary>
    private sealed class Disk(byte marker) : IWritableBlockDevice
    {
        /// <summary>Fixed complete-sector capacity without operating-system or user bytes.</summary>
        public long SectorCount => 4096;
        /// <summary>Independent identity for each channel and drive.</summary>
        public string Name => "Own PIIX IDE " + marker;
        /// <summary>Actual complete successfully written sectors.</summary>
        public readonly Dictionary<long, byte[]> Writes = [];
        /// <summary>Returns independently specified original or committed bytes.</summary>
        public byte[] Sector(long lba) => Writes.TryGetValue(lba, out var bytes) ? bytes.ToArray() : Pattern(marker, lba);
        /// <summary>Copies one owned sector into the real device buffer.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        { if (lba < 0 || lba >= SectorCount) throw new IOException("Own range"); Sector(lba).CopyTo(destination); }
        /// <summary>Commits exactly one complete sector.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        { Check(lba >= 0 && lba < SectorCount && source.Length == 512); Writes[lba] = source.ToArray(); }
        /// <summary>No deferred storage exists in this fixture.</summary>
        public void Flush() { }
        /// <summary>Copies exact owned generations for a separate future.</summary>
        public Disk Clone() { var copy = new Disk(marker); foreach (var (lba, bytes) in Writes) copy.Writes[lba] = bytes.ToArray(); return copy; }
        /// <summary>Reopens an independent owned generation.</summary>
        public IReadOnlyBlockDevice Reopen() => Clone();
        /// <summary>No host resource was acquired.</summary>
        public void Dispose() { }
    }
    /// <summary>Visible fixed device proves rejection never calls foreign methods.</summary>
    private sealed class Spy : IMemoryDevice, IIoPortDevice
    {
        /// <summary>Actual side effects from memory or colliding I/O accesses.</summary>
        public int Reads, Writes;
        /// <summary>Records one real memory read.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0xa7; }
        /// <summary>Records one real memory write.</summary>
        public void WriteByte(uint offset, byte value) => Writes++;
        /// <summary>Records one real fixed-I/O read.</summary>
        public uint Read(ushort port, int width) { Reads++; return 0xa7; }
        /// <summary>Records one real fixed-I/O write.</summary>
        public void Write(ushort port, uint value, int width) => Writes++;
    }
    /// <summary>Actual configuration bus, legacy targets, dynamic BAR and fixed memory peers.</summary>
    private sealed class Rig
    {
        /// <summary>Actual deterministic scheduler.</summary>
        public readonly DeviceScheduler Clock = new();
        /// <summary>Installed physical memory; DMA must not inherit CPU A20.</summary>
        public readonly MemoryBus Memory = new(32 * 1048576);
        /// <summary>Actual fixed and fallback I/O bus.</summary>
        public readonly IoPortBus Ports = new();
        /// <summary>Fixed host-memory target.</summary>
        public readonly Intel82437Fx Host;
        /// <summary>Actual function-one target and bus master.</summary>
        public readonly Intel82371FbIde Ide;
        /// <summary>Actual mechanism-one transport.</summary>
        public readonly PciConfigurationBus Pci = new();
        /// <summary>Two real independent ATA channels.</summary>
        public readonly AtaPioController[] Ata;
        /// <summary>Four separate owned media generations.</summary>
        public readonly Disk[] Disks;
        /// <summary>Foreign VGA/SMRAM stand-in that no PCI grant may touch.</summary>
        public readonly Spy Foreign = new();
        /// <summary>Builds all peers before registering complete port topology.</summary>
        public Rig(Disk[]? disks = null)
        {
            Disks = disks ?? [new(0x11), new(0x52), new(0x93), new(0xd4)];
            Host = new(Memory, Enumerable.Repeat((byte)0xf4, 65536).ToArray()); Host.WriteConfiguration(0x64, 8, 1);
            Memory.Map(0xc0000, 0x40000, Host, "Own host PAM"); Memory.Map(0xa0000, 0x20000, Foreign, "Foreign VGA and unavailable SMRAM");
            Ata = [new(Clock, Disks[0], Disks[1], 0x1f0, 0x3f6, true), new(Clock, Disks[2], Disks[3], 0x170, 0x376, true)];
            Ide = new(Clock, Host, Ata[0], Ata[1], Ports);
            Pci.RegisterFunction(0, 0, 0, Host); Pci.RegisterFunction(0, 7, 1, Ide);
            Ports.Map(0xcf8, 8, Pci); Ports.Map(0x1f0, 8, Ide); Ports.Map(0x3f6, 1, Ide); Ports.Map(0x170, 8, Ide); Ports.Map(0x376, 1, Ide); Ports.MapDynamic(Ide);
        }
        /// <summary>Reads exact function-one lanes through real CF8/CFC I/O.</summary>
        public uint Config(int offset, int width = 4)
        { Ports.Write(0xcf8, 0x80003900u | (uint)(offset & ~3), 4); return Ports.Read((ushort)(0xcfc + (offset & 3)), width); }
        /// <summary>Programs exact mechanism-one bytes without field edits.</summary>
        public void Config(int offset, uint value, int width)
        { Ports.Write(0xcf8, 0x80003900u | (uint)(offset & ~3), 4); Ports.Write((ushort)(0xcfc + (offset & 3)), value, width); }
        /// <summary>Enables both target channels and independent master permission.</summary>
        public void Enable(ushort basis = 0xc000)
        { Config(0x20, (uint)basis | 1, 4); Config(4, 7, 2); Config(0x40, 0x80008000, 4); }
        /// <summary>Reads a complete real busmaster register.</summary>
        public uint Bm(int channel, int offset, int width = 1) => Ports.Read((ushort)(Ide.BusMasterBase + channel * 8 + offset), width);
        /// <summary>Writes a complete real busmaster register.</summary>
        public void SetBm(int channel, int offset, uint value, int width = 1) => Ports.Write((ushort)(Ide.BusMasterBase + channel * 8 + offset), value, width);
        /// <summary>Creates one independent owned eight-byte physical descriptor.</summary>
        public void Prd(uint table, uint address, uint count, bool end)
        { BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan((int)table, 4), address); BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan((int)table + 4, 4), count | (end ? 0x80000000u : 0)); }
        /// <summary>Programs one real LBA command and selected drive.</summary>
        public void Command(int channel, int drive, byte command, int sectors = 1, uint lba = 3)
        {
            ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
            Ports.Write((ushort)(port + 6), 0xe0u | (uint)(drive << 4) | (lba >> 24 & 15), 1); Ports.Write((ushort)(port + 2), (uint)(sectors & 255), 1);
            Ports.Write((ushort)(port + 3), lba & 255, 1); Ports.Write((ushort)(port + 4), lba >> 8 & 255, 1); Ports.Write((ushort)(port + 5), lba >> 16 & 255, 1); Ports.Write((ushort)(port + 7), command, 1);
        }
        /// <summary>Starts a fresh command edge, retaining sticky status for explicit acknowledgement tests.</summary>
        public void Start(int channel, bool read, uint table = 0x10000)
        { SetBm(channel, 0, read ? 8u : 0); SetBm(channel, 4, table, 4); SetBm(channel, 0, read ? 9u : 1); }
        /// <summary>Runs only actual deadlines and requires bounded progress.</summary>
        public void Until(Func<bool> done, int maximum = 20000)
        {
            for (int i = 0; !done() && i < maximum; i++)
            { Check(Clock.NextEventTime is not null, "No real event can reach the requested DMA boundary."); Clock.AdvanceTo(Clock.NextEventTime!.Value); }
            Check(done(), "Bounded DMA deadline count exceeded.");
        }
        /// <summary>Requires distinct BM and terminal ATA completion.</summary>
        public void Complete(int channel) => Until(() => !Ide.GetChannelState(channel).Active && Ata[channel].DmaDirection == AtaDmaDirection.None);
    }
    /// <summary>Expected bytes depend only on fixture operands and address.</summary>
    private static byte[] Pattern(byte marker, long lba) => Enumerable.Range(0, 512).Select(i => unchecked((byte)(marker + lba * 19 + i * 11))).ToArray();
    /// <summary>Requires concrete bytes, registers, IRQs or absence of effects.</summary>
    private static void Check(bool value, string why = "PIIX IDE independent contract failed.") { if (!value) throw new Exception(why); }
    /// <summary>Requires exact argument rejection.</summary>
    private static void Invalid(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejected IDE transaction."); }
    /// <summary>Enumerates each actual owned node once.</summary>
    private static Dictionary<string, IStateObject> Graph(Rig r)
    {
        var graph = new Dictionary<string, IStateObject>();
        // Recursively registers only the explicit owned state children for this isolated future comparison.
        void Add(string name, IStateObject item) { graph.Add(name, item); foreach (var (child, value) in item.StateChildren) Add(name + "/" + child, value); }
        Add("memory", r.Memory); Add("host", r.Host); Add("pci", r.Pci); Add("ata0", r.Ata[0]); Add("ata1", r.Ata[1]); Add("ide", r.Ide); return graph;
    }
    /// <summary>Captures every explicit field and event without device access.</summary>
    private static string State(Rig r)
    {
        var graph = Graph(r); var owners = graph.ToDictionary(p => (object)p.Value, p => p.Key, ReferenceEqualityComparer.Instance);
        return JsonSerializer.Serialize(new { Nodes = graph.ToDictionary(p => p.Key, p => p.Value.SaveState()), Clock = r.Clock.SaveState(owners) }, StateValues.Json);
    }
    /// <summary>Rehydrates fresh peers/media without replaying pins or consuming PRDs.</summary>
    private static Rig Restore(Rig source)
    {
        var restored = new Rig(source.Disks.Select(d => d.Clone()).ToArray()); var a = Graph(source); var b = Graph(restored);
        var owners = a.ToDictionary(p => (object)p.Value, p => p.Key, ReferenceEqualityComparer.Instance); var clock = source.Clock.SaveState(owners);
        foreach (var (name, component) in a) b[name].LoadState(component.SaveState()); source.Memory.Ram.CopyTo(restored.Memory.Ram, 0); restored.Clock.LoadState(clock, b);
        Check(State(source) == State(restored) && source.Memory.Ram.SequenceEqual(restored.Memory.Ram)); return restored;
    }
    /// <summary>Registers the bounded functional contract, without historical timing or Windows acceptance.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int width in new[] { 1, 2, 4 })
            test("PIIX IDE independent A1 function-one identification width " + width, () =>
            {
                var r = new Rig(); byte[] header = [0x86, 0x80, 0x30, 0x12, 2, 0, 0x80, 2, 2, 0x80, 1, 1, 0, 0, 0, 0];
                for (int at = 0; at + width <= 16; at++) { uint expected = 0; for (int lane = 0; lane < width; lane++) expected |= (uint)header[at + lane] << (lane * 8); Check(r.Ide.ReadConfiguration(at, width) == expected); }
                Check(r.Config(0) == 0x12308086 && r.Config(8) == 0x01018002 && r.Config(0x20) == 1 && r.Config(0x40) == 0);
                Check(!r.Ide.IoSpaceEnabled && !r.Ide.BusMasterEnabled && r.Ports.Read(0x1f7, 1) == 255 && r.Ports.Read(0xc000, 4) == uint.MaxValue);
                Check(r.Ports.DynamicDecoderCount == 1 && r.Ports.HasOnlyDynamicDecoder(r.Ide));
            });
        foreach (uint incoming in new uint[] { 0, 0xffffffff, 0x12345678, 5 })
            test("PIIX IDE exact independent config masks " + incoming.ToString("X8"), () =>
            {
                var r = new Rig(); r.Config(4, incoming, 2); Check(r.Config(4, 2) == ((incoming & 5) | 2));
                r.Config(0x20, incoming, 4); Check(r.Config(0x20) == ((incoming & 0xfff0) | 1));
                r.Config(0x40, incoming, 4); Check(r.Config(0x40) == (incoming & 0xb3ffb3ff));
                r.Ide.WriteConfiguration(0x0d, incoming, 1); Check(r.Ide.ReadConfiguration(0x0d, 1) == (incoming & 0xf0));
            });
        foreach (int channel in new[] { 0, 1 })
            test("PIIX IDE IOSE and per-channel DE gate actual ports " + channel, () =>
            {
                var r = new Rig(); ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170; r.Config(0x40 + channel * 2, 0x8000, 2);
                r.Command(channel, 0, 0xc8); Check(r.Ata[channel].DmaDirection == AtaDmaDirection.None && r.Ports.Read((ushort)(port + 7), 1) == 255);
                r.Config(4, 3, 2); r.Command(channel, 0, 0xc8); Check(r.Ata[channel].DmaDirection == AtaDmaDirection.DeviceToMemory && r.Ports.Read((ushort)(port + 7), 1) == 0x80);
                r.Config(0x40 + channel * 2, 0, 2); Check(r.Ports.Read((ushort)(port + 7), 1) == 255 && r.Ata[channel].DmaDirection == AtaDmaDirection.DeviceToMemory);
            });
        foreach (ushort basis in new ushort[] { 0x400, 0xc000, 0xfff0 })
            test("PIIX IDE complete dynamic BAR window relocation " + basis.ToString("X4"), () =>
            {
                var r = new Rig(); r.Enable(basis); Check(r.Ide.Decodes(basis, 4) && r.Ide.Decodes((ushort)(basis + 12), 4));
                r.SetBm(0, 4, 0x1234567b, 4); Check(r.Bm(0, 4, 4) == 0x12345678);
                uint other = basis == 0xc000 ? 0xd001u : 0xc001u; r.Config(0x20, other, 4);
                Check(r.Ports.Read((ushort)(basis + 4), 4) == uint.MaxValue && r.Bm(0, 4, 4) == 0x12345678);
            });
        foreach (int collided in new[] { 0, 7, 15 })
            test("PIIX IDE any fixed ISA collision blocks whole BM window " + collided, () =>
            {
                var r = new Rig(); r.Enable(); var spy = new Spy(); r.Ports.Map((ushort)(0xc000 + collided), 1, spy);
                Check(r.Config(0x20) == 0xc001 && !r.Ide.Decodes(0xc000, 1) && !r.Ide.Decodes(0xc008, 4));
                for (int at = 0; at < 16; at++) if (at != collided) { Check(r.Ports.Read((ushort)(0xc000 + at), 1) == 255); r.Ports.Write((ushort)(0xc000 + at), 255, 1); }
                Check(r.Ide.GetChannelState(0).Command == 0 && r.Ide.GetChannelState(1).Command == 0 && spy.Reads == 0 && spy.Writes == 0);
                Check(r.Ports.Read((ushort)(0xc000 + collided), 1) == 0xa7 && spy.Reads == 1);
            });
        foreach (int channel in new[] { 0, 1 }) foreach (int drive in new[] { 0, 1 }) foreach (bool write in new[] { false, true }) foreach (bool first in new[] { false, true })
            test($"PIIX IDE actual scatter DMA channel{channel}/drive{drive}/write{write}/startfirst{first}", () =>
            {
                var r = new Rig(); r.Enable(); byte[] payload = Pattern(0xe7, 3).Concat(Pattern(0xb5, 4)).ToArray();
                r.Prd(0x10000, 0x20000, 510, false); r.Prd(0x10008, 0x30000, 514, true); r.Memory.Ram.AsSpan(0x20000, 512).Fill(0x5a); r.Memory.Ram.AsSpan(0x30000, 516).Fill(0x5a);
                if (write) { payload.AsSpan(0, 510).CopyTo(r.Memory.Ram.AsSpan(0x20000)); payload.AsSpan(510).CopyTo(r.Memory.Ram.AsSpan(0x30000)); }
                if (first) r.Start(channel, !write); r.Command(channel, drive, write ? (byte)0xca : (byte)0xc8, 2); if (!first) r.Start(channel, !write); r.Complete(channel);
                var c = r.Ide.GetChannelState(channel); Check(c.BytesTransferred == 1024 && (c.Status & 7) == 4 && r.Ata[channel].InterruptAsserted);
                var disk = r.Disks[channel * 2 + drive];
                if (write) Check(disk.Writes.Count == 2 && disk.Writes[3].SequenceEqual(payload.AsSpan(0, 512).ToArray()) && disk.Writes[4].SequenceEqual(payload.AsSpan(512).ToArray()));
                else
                {
                    byte[] expected = disk.Sector(3).Concat(disk.Sector(4)).ToArray();
                    Check(r.Memory.Ram.AsSpan(0x20000, 510).SequenceEqual(expected.AsSpan(0, 510)) && r.Memory.Ram.AsSpan(0x30000, 514).SequenceEqual(expected.AsSpan(510)));
                    Check(r.Memory.Ram[0x201fe] == 0x5a && r.Memory.Ram[0x201ff] == 0x5a && r.Memory.Ram[0x30202] == 0x5a && r.Memory.Ram[0x30203] == 0x5a);
                }
                Check(r.Disks.Where((_, slot) => slot != channel * 2 + drive).All(d => d.Writes.Count == 0));
            });
        foreach (int channel in new[] { 0, 1 })
            test("PIIX IDE PRD2 plus510 is one actual ATA sector " + channel, () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 2, false); r.Prd(0x10008, 0x30000, 510, true); r.Command(channel, 0, 0xc8); r.Start(channel, true); r.Complete(channel);
                byte[] expected = r.Disks[channel * 2].Sector(3); Check(r.Memory.Ram.AsSpan(0x20000, 2).SequenceEqual(expected.AsSpan(0, 2)) && r.Memory.Ram.AsSpan(0x30000, 510).SequenceEqual(expected.AsSpan(2)));
                Check(r.Ata[channel].ReadSectorCount == 1 && r.Ide.GetChannelState(channel).BytesTransferred == 512);
            });
        test("PIIX IDE PRD countzero retains65536 and completes128 actual sectors", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 0, true); r.Command(0, 0, 0xc8, 128); r.Start(0, true);
            r.Until(() => r.Ide.GetChannelState(0).BytesTransferred == 32); Check(r.Ide.GetChannelState(0).DescriptorRemaining == 65504 && r.Ide.GetChannelState(0).Active);
            r.Complete(0); Check(r.Ide.GetChannelState(0).BytesTransferred == 65536 && r.Ata[0].ReadSectorCount == 128);
            byte[] expected = Enumerable.Range(3, 128).SelectMany(lba => r.Disks[0].Sector(lba)).ToArray(); Check(r.Memory.Ram.AsSpan(0x20000, 65536).SequenceEqual(expected));
        });
        test("PIIX IDE reserved PRD bits and odd address count obey explicit masking", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20001, 0x7fff0201, true); r.Command(0, 0, 0xc8); r.Start(0, true); r.Complete(0);
            Check(r.Ide.GetChannelState(0).BytesTransferred == 512 && r.Memory.Ram.AsSpan(0x20000, 512).SequenceEqual(r.Disks[0].Sector(3)) && (r.Ide.GetChannelState(0).Status & 7) == 4);
        });
        foreach (uint table in new uint[] { 0x10ffc, 0x10ff8 })
            test("PIIX IDE complete4KiB table bound stops without PCIerror " + table.ToString("X"), () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(table, 0x20000, 2, false); r.Memory.Map(0x11000, 4096, new Spy(), "Unavailable next table page");
                r.Command(0, 0, 0xc8); r.Start(0, true, table); r.Until(() => !r.Ide.GetChannelState(0).Active);
                var c = r.Ide.GetChannelState(0); Check((c.Status & 6) == 0 && r.Config(6, 2) == 0x0280 && !r.Ata[0].InterruptAsserted && r.Ata[0].ReadSectorCount == 0);
                Check(c.BytesTransferred == (table == 0x10ff8 ? 2 : 0));
            });
        test("PIIX IDE invalid64KiB region stops quietly before ATA consumption", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x2fffe, 4, true); r.Command(0, 0, 0xc8); r.Start(0, true); r.Until(() => !r.Ide.GetChannelState(0).Active);
            Check(r.Ide.GetChannelState(0).BytesTransferred == 0 && (r.Bm(0, 2) & 7) == 0 && r.Config(6, 2) == 0x0280 && r.Ata[0].DmaDirection == AtaDmaDirection.DeviceToMemory);
        });
        test("PIIX IDE EOT never fetches another descriptor", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10ff8, 0x20000, 512, true); var spy = new Spy(); r.Memory.Map(0x11000, 4096, spy, "Forbidden postEOT descriptor");
            r.Command(0, 0, 0xc8); r.Start(0, true, 0x10ff8); r.Complete(0); Check((r.Bm(0, 2) & 7) == 4 && spy.Reads == 0 && spy.Writes == 0 && r.Config(6, 2) == 0x0280);
        });
        test("PIIX IDE completed ATA command needs no next nonEOT descriptor", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10ff8, 0x20000, 512, false); var spy = new Spy(); r.Memory.Map(0x11000, 4096, spy, "Unneeded next descriptor");
            r.Command(0, 0, 0xc8); r.Start(0, true, 0x10ff8); r.Until(() => r.Ata[0].DmaDirection == AtaDmaDirection.None);
            Check((r.Bm(0, 2) & 7) == 5 && r.Ide.GetChannelState(0).BytesTransferred == 512 && r.Config(6, 2) == 0x0280 && spy.Reads == 0 && spy.Writes == 0);
            r.SetBm(0, 0, 8); Check((r.Bm(0, 2) & 7) == 4 && r.Memory.Ram.AsSpan(0x20000, 512).SequenceEqual(r.Disks[0].Sector(3)));
        });
        foreach (bool small in new[] { false, true })
            test("PIIX IDE short versus long PRD retains independent ATA completion " + small, () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, small ? 256u : 1024u, true); r.Command(0, 0, 0xc8); r.Start(0, true);
                r.Until(() => small ? !r.Ide.GetChannelState(0).Active : r.Ata[0].DmaDirection == AtaDmaDirection.None);
                var c = r.Ide.GetChannelState(0); Check((c.Status & 7) == (small ? 0 : 5) && c.BytesTransferred == (small ? 256 : 512));
                Check(small ? r.Ata[0].DmaOffset == 256 && r.Ata[0].DmaRemainingSectors == 1 && !r.Ata[0].InterruptAsserted : r.Ata[0].InterruptAsserted);
            });
        foreach (int channel in new[] { 0, 1 })
            test("PIIX IDE nIEN ATAstatus and BMW1C acknowledge separately " + channel, () =>
            {
                var r = new Rig(); r.Enable(); ushort control = channel == 0 ? (ushort)0x3f6 : (ushort)0x376, status = channel == 0 ? (ushort)0x1f7 : (ushort)0x177;
                r.Ports.Write(control, 2, 1); r.Prd(0x10000, 0x20000, 512, true); r.Command(channel, 0, 0xc8); r.Start(channel, true); r.Complete(channel);
                Check((r.Bm(channel, 2) & 7) == 0 && !r.Ata[channel].InterruptAsserted); r.Ports.Write(control, 0, 1); Check((r.Bm(channel, 2) & 7) == 4 && r.Ata[channel].InterruptAsserted);
                r.SetBm(channel, 2, 4); Check((r.Bm(channel, 2) & 7) == 0 && r.Ata[channel].InterruptAsserted); r.Ports.Read(control, 1); Check(r.Ata[channel].InterruptAsserted);
                r.SetBm(channel, 2, 4); Check((r.Bm(channel, 2) & 7) == 0); r.Ports.Read(status, 1); Check(!r.Ata[channel].InterruptAsserted);
                r.Command(channel, 0, 0xc8, lba: 4); r.Start(channel, true); r.Complete(channel); Check((r.Bm(channel, 2) & 7) == 4);
                r.Ports.Read(status, 1); Check((r.Bm(channel, 2) & 7) == 4 && !r.Ata[channel].InterruptAsserted);
            });
        foreach (int channel in new[] { 0, 1 })
            test("PIIX IDE BME pause retains cursor independently of IOSE DE BAR " + channel, () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 1024, true); r.Command(channel, 0, 0xc8, 2); r.Start(channel, true);
                r.Until(() => r.Ide.GetChannelState(channel).BytesTransferred == 32); r.Config(4, 3, 2); var paused = r.Ide.GetChannelState(channel); byte[] bytes = r.Memory.Ram.AsSpan(0x20000, 1024).ToArray();
                Check(paused.Active && paused.EventToken == 0); r.Clock.AdvanceTo(r.Clock.Now + 1000000); Check(r.Ide.GetChannelState(channel) == paused && r.Memory.Ram.AsSpan(0x20000, 1024).SequenceEqual(bytes));
                r.Config(4, 6, 2); Check(r.Ports.Read(r.Ide.BusMasterBase, 1) == 255); r.Config(0x40 + channel * 2, 0, 2); r.Config(0x20, 0xd001, 4); r.Complete(channel);
                Check(r.Ide.GetChannelState(channel).BytesTransferred == 1024 && r.Ata[channel].ReadSectorCount == 2); r.Config(4, 7, 2); Check(r.Bm(channel, 2) == 4);
                ushort status = channel == 0 ? (ushort)0x1f7 : (ushort)0x177; Check(r.Ports.Read(status, 1) == 255); r.Config(0x40 + channel * 2, 0x8000, 2); Check(r.Ports.Read(status, 1) == 0x50);
            });
        foreach (bool write in new[] { false, true })
            test("PIIX IDE Stop discards only BM cursor without resetting ATA " + write, () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 1024, true); Pattern(0x7d, 3).Concat(Pattern(0x7d, 4)).ToArray().CopyTo(r.Memory.Ram, 0x20000);
                r.Command(0, 0, write ? (byte)0xca : (byte)0xc8, 2); r.Start(0, !write); r.Until(() => r.Ide.GetChannelState(0).BytesTransferred == 32);
                byte[] before = r.Memory.Ram.AsSpan(0x20000, 1024).ToArray(); var direction = r.Ata[0].DmaDirection; r.SetBm(0, 0, write ? 8u : 0u);
                var c = r.Ide.GetChannelState(0); Check(!c.Active && c.BytesTransferred == 0 && c.DescriptorAddress == 0 && c.RegionAddress == 0 && c.DescriptorRemaining == 0 && c.Direction == direction && c.SectorOffset == 32);
                Check((c.Command & 8) == (write ? 0 : 8), "Active direction changed during simultaneous Stop."); r.Clock.AdvanceTo(r.Clock.Now + 100000);
                Check(r.Ata[0].DmaOffset == 32 && r.Disks[0].Writes.Count == 0 && r.Memory.Ram.AsSpan(0x20000, 1024).SequenceEqual(before));
            });
        foreach (bool descriptor in new[] { false, true }) foreach (bool write in new[] { false, true })
            test($"PIIX IDE missing PCI target abort isolated descriptor{descriptor}/write{write}", () =>
            {
                var r = new Rig(); r.Enable(); uint table = descriptor ? 0x02000000u : 0x10000u; if (!descriptor) r.Prd(table, 0x02000000, 512, true);
                r.Command(0, 0, write ? (byte)0xca : (byte)0xc8); r.Start(0, !write, table); r.Until(() => !r.Ide.GetChannelState(0).Active);
                Check((r.Bm(0, 2) & 7) == 2 && (r.Config(6, 2) & 0x2000) != 0 && (r.Host.ReadConfiguration(6, 2) & 0x2000) == 0);
                Check(r.Ide.GetChannelState(0).BytesTransferred == 0 && r.Ata[0].DmaOffset == 0 && r.Ata[0].DmaDirection != AtaDmaDirection.None && !r.Ata[0].InterruptAsserted && r.Disks[0].Writes.Count == 0);
                r.SetBm(0, 2, 2); Check((r.Bm(0, 2) & 7) == 0 && (r.Config(6, 2) & 0x2000) != 0); r.Config(6, 0x2000, 2); Check(r.Config(6, 2) == 0x0280);
            });
        test("PIIX IDE later PCI denial retains exact visible32 bytes", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 512, true); r.Memory.Ram.AsSpan(0x20000, 512).Fill(0x5a); r.Command(0, 0, 0xc8); r.Start(0, true);
            r.Until(() => r.Ide.GetChannelState(0).BytesTransferred == 32); r.Host.WriteConfiguration(4, 0, 1); r.Until(() => !r.Ide.GetChannelState(0).Active);
            Check((r.Bm(0, 2) & 7) == 2 && r.Ide.GetChannelState(0).BytesTransferred == 32 && r.Ata[0].DmaOffset == 32 && r.Ata[0].ReadSectorCount == 0);
            Check(r.Memory.Ram.AsSpan(0x20000, 32).SequenceEqual(r.Disks[0].Sector(3).AsSpan(0, 32)) && r.Memory.Ram.AsSpan(0x20020, 480).ToArray().All(b => b == 0x5a));
        });
        test("PIIX IDE DMA physical address ignores CPU A20 folding", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x120000, 512, true); r.Memory.A20Enabled = false; r.Memory.Ram.AsSpan(0x20000, 512).Fill(0x5a);
            r.Command(0, 0, 0xc8); r.Start(0, true); r.Complete(0); Check(r.Memory.Ram.AsSpan(0x120000, 512).SequenceEqual(r.Disks[0].Sector(3)) && r.Memory.Ram.AsSpan(0x20000, 512).ToArray().All(b => b == 0x5a));
        });
        foreach (uint target in new uint[] { 0xa0000, 0xb0000, 0xc0000, 0xf0000, 0xffffffff })
            test("PIIX IDE denied PCI MMIO PAM ROM wrap cannot consume ATA " + target.ToString("X8"), () =>
            {
                var r = new Rig(); r.Enable(); r.Prd(0x10000, target, target == 0xffffffff ? 2u : 512u, true); r.Command(0, 0, 0xc8); r.Start(0, true); r.Until(() => !r.Ide.GetChannelState(0).Active);
                Check(r.Ide.GetChannelState(0).BytesTransferred == 0 && r.Ata[0].DmaOffset == 0 && r.Ata[0].ReadSectorCount == 0 && r.Foreign.Reads == 0 && r.Foreign.Writes == 0);
                Check((r.Bm(0, 2) & 7) == 2);
            });
        foreach (bool write in new[] { false, true })
            test("PIIX IDE independent PCI PAM permission selects actual shadow " + write, () =>
            {
                var r = new Rig(); r.Enable(); byte[] payload = Pattern(0xe3, 3); payload.CopyTo(r.Memory.Ram, 0xc0000);
                r.Host.WriteConfiguration(0x5a, write ? 1u : 2u, 1); r.Prd(0x10000, 0xc0000, 512, true); r.Command(0, 0, write ? (byte)0xca : (byte)0xc8); r.Start(0, !write); r.Complete(0);
                Check(write ? r.Disks[0].Writes[3].SequenceEqual(payload) : r.Memory.Ram.AsSpan(0xc0000, 512).SequenceEqual(r.Disks[0].Sector(3)));
            });
        foreach (int channel in new[] { 0, 1 }) foreach (int drive in new[] { 0, 1 }) foreach (bool enabled in new[] { false, true })
            test($"PIIX IDE TIME selects current drive for real32bitPIO {channel}/{drive}/{enabled}", () =>
            {
                var r = new Rig(); r.Enable(); ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
                uint selected = drive == 0 ? 1u : 0x10u, other = drive == 0 ? 0x10u : 1u;
                r.Config(0x40 + channel * 2, 0x8000 | (enabled ? selected : other), 2); r.Command(channel, drive, 0xec); r.Clock.AdvanceTo(5000);
                Check(r.Ports.Read(port, 4) == (enabled ? 0x04000040u : uint.MaxValue)); Check(r.Ports.Read(port, 2) == (enabled ? 0u : 0x40u));
                Check(r.Ata[channel].SelectedDriveIndex == drive && r.Ata[channel].DmaDirection == AtaDmaDirection.None);
            });
        foreach (int channel in new[] { 0, 1 }) foreach (int drive in new[] { 0, 1 })
            test($"PIIX IDE enabled32bitPIO writes are two real words {channel}/{drive}", () =>
            {
                var r = new Rig(); r.Enable(); ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170; r.Config(0x40 + channel * 2, drive == 0 ? 0x8001u : 0x8010u, 2);
                r.Command(channel, drive, 0x30); r.Clock.AdvanceTo(5000); r.Ports.Write(port, 0x13572468, 4); for (int i = 0; i < 254; i++) r.Ports.Write(port, 0xa5b6, 2);
                Check(r.Disks[channel * 2 + drive].Writes.Count == 0); r.Clock.AdvanceTo(10000); var data = r.Disks[channel * 2 + drive].Writes[3];
                Check(data.AsSpan(0, 4).SequenceEqual(new byte[] { 0x68, 0x24, 0x57, 0x13 }) && data.AsSpan(4).ToArray().Where((b, i) => b != (i % 2 == 0 ? 0xb6 : 0xa5)).Count() == 0);
            });
        foreach (int phase in Enumerable.Range(0, 5))
            test("PIIX IDE exact raw RAM device timer continuation phase " + phase, () =>
            {
                var r = new Rig(); r.Enable(); bool write = phase == 2;
                r.Prd(0x10000, 0x20000, phase == 3 ? 1024u : 512u, true); Pattern(0xe6, 3).CopyTo(r.Memory.Ram, 0x20000);
                r.Command(0, 0, write ? (byte)0xca : (byte)0xc8); r.Start(0, !write);
                if (phase is 1 or 4) r.Until(() => r.Ide.GetChannelState(0).BytesTransferred == 32);
                if (phase == 2) r.Until(() => r.Ide.GetChannelState(0).WriteCommitPending);
                if (phase == 3) r.Until(() => r.Ata[0].DmaDirection == AtaDmaDirection.None);
                if (phase == 4) r.Config(4, 3, 2);
                var restored = Restore(r); if (phase == 4) { r.Config(4, 7, 2); restored.Config(4, 7, 2); }
                for (int i = 0; i < 50 && r.Clock.NextEventTime is { } deadline; i++)
                {
                    Check(restored.Clock.NextEventTime == deadline); r.Clock.AdvanceTo(deadline); restored.Clock.AdvanceTo(deadline);
                    Check(State(r) == State(restored) && r.Memory.Ram.SequenceEqual(restored.Memory.Ram));
                    Check(r.Disks.Zip(restored.Disks).All(pair => pair.First.Writes.Count == pair.Second.Writes.Count && pair.First.Writes.All(p => pair.Second.Writes[p.Key].SequenceEqual(p.Value))));
                }
                Check(r.Ata[0].DmaDirection == AtaDmaDirection.None && restored.Ata[0].DmaDirection == AtaDmaDirection.None);
                Check((r.Ide.GetChannelState(0).Status & 7) == (phase == 3 ? 5 : 4));
            });
        foreach (var input in new (int Offset, int Width)[] { (-1, 1), (256, 1), (255, 2), (253, 4), (0x60, 3) })
            test($"PIIX IDE invalid full config transaction unchanged {input.Offset}/{input.Width}", () =>
            {
                var r = new Rig(); r.Enable(); string before = State(r); Invalid(() => r.Ide.ReadConfiguration(input.Offset, input.Width)); Invalid(() => r.Ide.WriteConfiguration(input.Offset, 0xffffffff, input.Width)); Check(State(r) == before);
            });
        test("PIIX IDE dynamic overrun and invalid channel queries have no effect", () =>
        {
            var r = new Rig(); r.Enable(); string before = State(r); Check(r.Ports.Read(0xc00f, 2) == 65535); r.Ports.Write(0xc00f, 0xffff, 2);
            Invalid(() => r.Ide.GetChannelState(-1)); Invalid(() => r.Ide.GetChannelState(2)); Invalid(() => r.Ide.Read(0xc000, 3)); Invalid(() => r.Ide.Write(0xc000, 0, 3));
            Check(State(r) == before && !r.Ide.GetChannelState(0).Active);
        });
        test("PIIX IDE initial direction mismatch waits without a fake transfer or IRQ", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 512, true); r.Command(0, 0, 0xc8); r.Start(0, false);
            r.Clock.AdvanceTo(100000); var c = r.Ide.GetChannelState(0); Check(c.Active && (c.Status & 6) == 0 && c.BytesTransferred == 0 && c.EventToken == 0 && r.Ata[0].DmaReady && r.Ata[0].DmaOffset == 0);
            r.SetBm(0, 0, 9); Check(r.Ide.GetChannelState(0).Command == 1 && r.Ide.GetChannelState(0).BytesTransferred == 0);
            r.SetBm(0, 0, 0); r.Start(0, true); r.Complete(0); Check(r.Memory.Ram.AsSpan(0x20000, 512).SequenceEqual(r.Disks[0].Sector(3)));
        });
        test("PIIX IDE BM capabilities are software latches and not DMA grants", () =>
        {
            var r = new Rig(); r.Enable(); r.SetBm(0, 2, 0x60); Check(r.Bm(0, 2) == 0x60); r.SetBm(0, 2, 0); Check(r.Bm(0, 2) == 0);
            r.Prd(0x10000, 0x20000, 512, true); r.Command(0, 0, 0xc8); r.Start(0, true); r.Complete(0); Check(r.Bm(0, 2) == 4);
            r.SetBm(0, 2, 0x64); Check(r.Bm(0, 2) == 0x60 && r.Ata[0].InterruptAsserted);
        });
        test("PIIX IDE stopping a filled write preserves its already accepted media commit", () =>
        {
            var r = new Rig(); r.Enable(); byte[] data = Pattern(0xa8, 3); data.CopyTo(r.Memory.Ram, 0x20000); r.Prd(0x10000, 0x20000, 1024, true);
            r.Command(0, 0, 0xca, 2); r.Start(0, false); r.Until(() => r.Ide.GetChannelState(0).WriteCommitPending);
            Check(r.Disks[0].Writes.Count == 0); r.SetBm(0, 0, 0); r.Clock.AdvanceTo(r.Clock.Now + 5000);
            Check(r.Disks[0].Writes.Count == 1 && r.Disks[0].Writes[3].SequenceEqual(data) && r.Ata[0].WrittenSectorCount == 1 && r.Ata[0].DmaRemainingSectors == 1 && r.Ata[0].DmaReady && !r.Ide.GetChannelState(0).Active);
        });
        test("PIIX IDE DMA diagnostics follow latched drive during legal task selection", () =>
        {
            var r = new Rig(); r.Enable(); r.Prd(0x10000, 0x20000, 512, true); r.Command(0, 0, 0xca); r.Start(0, false);
            r.Until(() => r.Ide.GetChannelState(0).WriteCommitPending); r.Ports.Write(0x1f6, 0xf0, 1);
            var c = r.Ide.GetChannelState(0); Check(r.Ata[0].SelectedDriveIndex == 1 && c.DeviceIndex == 0 && c.SectorOffset == 512 && c.AtaBusy && c.WriteCommitPending);
            r.Complete(0); Check(r.Disks[0].Writes.Count == 1 && r.Disks[1].Writes.Count == 0);
        });
    }
}
