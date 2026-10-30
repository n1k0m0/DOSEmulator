// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Independent port, DMA, ring, filter, CRC, physical-frame timing and complete component continuation tests.</summary>
internal static class NetworkCardTests
{
    private static readonly byte[] Mac = [0x02, 0x44, 0x4f, 0x53, 0x45, 0x10];

    /// <summary>Requires a concrete device/bus/data invariant with a useful failure description.</summary>
    private static void Check(bool value, string description)
    { if (!value) throw new Exception("Network card invariant: " + description); }
    /// <summary>Requires a malformed caller transaction to fail instead of quietly publishing a partial update.</summary>
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Malformed NIC transaction was accepted."); }
    /// <summary>Requires a coherent but semantically impossible state container to fail its pre-media validation.</summary>
    private static void RejectState(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid NIC state was accepted."); }

    /// <summary>Owns one private controller graph and records actual IRQ edges and completed copied wire frames.</summary>
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly IoPortBus Ports = new();
        public readonly Ne2000 Nic;
        public readonly Rtl8029? Pci;
        public readonly List<bool> Edges = [];
        public readonly List<(ulong Time, byte[] Frame)> Sent = [];
        public readonly bool IsPci;
        /// <summary>Constructs an unmapped/disconnected graph, then registers its actual ISA or relocated PCI window.</summary>
        public Rig(bool pci,byte[]? identity = null)
        {
            IsPci = pci; Nic = new(Clock, 0x300, identity??Mac, level => Edges.Add(level));
            Nic.Transmitted += bytes => Sent.Add((Clock.Now, bytes.ToArray()));
            if (pci)
            {
                Pci = new(Nic, Ports); Ports.MapDynamic(Pci);
                Pci.WriteConfiguration(0x10, 0x6001, 4); Pci.WriteConfiguration(4, 1, 2);
            }
            else Ports.Map(0x300, 32, Nic);
        }
        /// <summary>Resolves the current complete PCI BAR only for these bounded fixture addresses.</summary>
        public ushort Base => IsPci ? checked((ushort)Pci!.IoBase) : (ushort)0x300;
        /// <summary>Writes one actual fixture port with the selected bus width.</summary>
        public void Out(int offset, uint value, int width = 1) => Ports.Write((ushort)(Base + offset), value, width);
        /// <summary>Reads one actual fixture port, preserving device side effects.</summary>
        public uint In(int offset, int width = 1) => Ports.Read((ushort)(Base + offset), width);
        /// <summary>Initializes real packet RAM/ring/node registers while stopped, then starts normal receive/transmit mode.</summary>
        public void Start(byte rx = 4, byte first = 0x46, byte stop = 0x80, byte boundary = 0x46, byte current = 0x47)
        {
            Out(0, 0x21); Out(14, 9); Out(1, first); Out(2, stop); Out(3, boundary);
            Out(0, 0x61); byte[] identity=Nic.MacAddress; for (int i = 0; i < 6; i++) Out(i + 1, identity[i]); Out(7, current);
            Out(0, 0x21); Out(12, rx); Out(13, 0); Out(7, 0x7f); Out(15, 0); Out(0, 0x22);
            Nic.SetCableConnected(true);
        }
        /// <summary>Programs one independent complete remote DMA operation without using controller internals.</summary>
        public void Dma(int address, int count, bool read, int width = 2, bool swapped = false)
        {
            Out(0, 0x22); Out(14, (uint)(width == 1 ? 8 : swapped ? 11 : 9)); Out(7, 0x40);
            Out(8, (byte)address); Out(9, (byte)(address >> 8)); Out(10, (byte)count); Out(11, (byte)(count >> 8));
            Out(0, read ? 0x0au : 0x12u);
        }
        /// <summary>Writes the exact supplied bytes through host data-port transfers with an explicit final partial word.</summary>
        public void Store(int address, byte[] data, int width = 2)
        {
            Dma(address, data.Length, false, width);
            for (int i = 0; i < data.Length; i += width)
            {
                uint value = 0; for (int j = 0; j < width && i + j < data.Length; j++) value |= (uint)data[i + j] << (8 * j);
                Out(0x10, value, width);
            }
        }
        /// <summary>Reads exactly the requested bytes through actual data-port accesses, including a bounded last odd word.</summary>
        public byte[] Load(int address, int count, int width = 2)
        {
            Dma(address, count, true, width); byte[] bytes = new byte[count];
            for (int i = 0; i < count; i += width)
            {
                uint value = In(0x10, width); for (int j = 0; j < width && i + j < count; j++) bytes[i + j] = (byte)(value >> (j * 8));
            }
            return bytes;
        }
        /// <summary>Starts the real programmed frame; the caller controls virtual time and observes its hardware completion.</summary>
        public void Send(byte[] frame, byte config = 0, byte? dataConfiguration = null)
        {
            Store(0x4000, frame); Out(0, 0x22); Out(4, 0x40); Out(5, (byte)frame.Length); Out(6, (byte)(frame.Length >> 8));
            Out(13, config); if (dataConfiguration is { } data) Out(14, data); Out(7, 0x7f); Out(0, 0x26);
        }
    }
    /// <summary>Produces an independently specified Ethernet frame with varied body bytes and an explicit destination.</summary>
    private static byte[] Frame(int length = 60, byte[]? destination = null, int seed = 11)
    {
        byte[] bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 37 + seed)).ToArray();
        (destination ?? Mac).CopyTo(bytes, 0); new byte[] { 2, 7, 6, 5, 4, 3 }.CopyTo(bytes, 6); bytes[12] = 8; bytes[13] = 0; return bytes;
    }
    /// <summary>Builds the independent FCS by polynomial long division in the opposite bit order from the device implementation.</summary>
    private static uint Fcs(byte[] bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte item in bytes)
            for (int bit = 0; bit < 8; bit++)
            { bool top = (value & 0x80000000) != 0, input = (item & (1 << bit)) != 0; value <<= 1; if (top != input) value ^= 0x04c11db7; }
        uint reversed = 0; for (int i = 0; i < 32; i++) { reversed = (reversed << 1) | (value & 1); value >>= 1; }
        return ~reversed;
    }
    /// <summary>Derives the National top-six-bit filter index from the independent reflected FCS oracle.</summary>
    private static int Hash(byte[] address)
    { uint bits = ~Fcs(address); int index = 0; for (int i = 0; i < 6; i++) { index = (index << 1) | (int)(bits & 1); bits >>= 1; } return index; }
    /// <summary>Creates the actual independent wire sequence used by a local CRC diagnostic.</summary>
    private static byte[] Wire(byte[] frame, bool generated)
    { if (!generated) return frame.ToArray(); uint crc = Fcs(frame); return [.. frame, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)]; }
    /// <summary>Constructs all eight physical FIFO locations, including the separately documented appended count writes.</summary>
    private static byte[] DiagnosticFifo(byte[] wire)
    {
        byte[] expected = new byte[8]; for (int i = Math.Max(0, wire.Length - 8); i < wire.Length; i++) expected[i % 8] = wire[i];
        expected[wire.Length % 8] = (byte)wire.Length; expected[(wire.Length + 1) % 8] = (byte)(wire.Length >> 8);
        expected[(wire.Length + 2) % 8] = (byte)(wire.Length >> 8); return expected;
    }
    /// <summary>Checks diagnostic results independently, with every SRAM byte and the actual ring cursor preserved.</summary>
    private static void DiagnosticProof(Rig r, byte[] beforeRam, byte beforeCurrent, byte[] wire, byte isr, byte rsr)
    {
        var state = r.Nic.SaveState(); Check(StateValues.Read<byte[]>(state.Fields["_ram"]).SequenceEqual(beforeRam), "loopback never DMA-writes SRAM");
        Check(state.Fields["_currentPage"].GetByte() == beforeCurrent, "loopback never advances CURR");
        Check(r.In(7) == isr && r.In(12) == rsr && r.In(4) == 0x53 && (r.In(0) & 4) == 0 && r.Sent.Count == 0,
            "independent FIFO-only diagnostic ISR/RSR/TSR");
        Check(Enumerable.Range(0, 8).Select(_ => (byte)r.In(6)).SequenceEqual(DiagnosticFifo(wire)), "all physical FIFO/count locations"); Validate(r);
    }
    /// <summary>Checks all actual receive bytes, the exact independently computed ring successor and the generated FCS.</summary>
    private static void RingProof(Rig r, int startPage, int nextPage, byte[] frame, bool group = false)
    {
        byte[] actual = r.Load(startPage << 8, frame.Length + 8);
        Check(actual[0] == (group ? 0x21 : 1) && actual[1] == nextPage && (actual[2] | actual[3] << 8) == frame.Length + 4, "ring header");
        Check(actual.AsSpan(4, frame.Length).SequenceEqual(frame), "entire actual receive payload");
        uint crc = Fcs(frame); for (int i = 0; i < 4; i++) Check(actual[frame.Length + 4 + i] == (byte)(crc >> (8 * i)), "independent FCS byte " + i);
        r.Out(0, 0x62); Check(r.In(7) == nextPage, "actual CURR"); r.Out(0, 0x22);
    }
    /// <summary>Clones every generated hardware field and the complete queue/clock, retaining independent peers and callbacks.</summary>
    private static Rig Clone(Rig original)
    {
        Validate(original);
        Rig copy = new(original.IsPci,original.Nic.MacAddress); copy.Nic.LoadState(original.Nic.SaveState());
        if (copy.Pci is not null) copy.Pci.LoadState(original.Pci!.SaveState());
        var oldOwners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [original.Nic] = "nic" };
        var newOwners = new Dictionary<string, IStateObject> { ["nic"] = copy.Nic };
        if (copy.Pci is not null) { oldOwners[original.Pci!] = "pci"; newOwners["pci"] = copy.Pci; }
        copy.Clock.LoadState(original.Clock.SaveState(oldOwners), newOwners); copy.Nic.ValidateSnapshot(); copy.Pci?.ValidateSnapshot(); return copy;
    }
    /// <summary>Runs the same nonmutating semantic checks required before SnapshotStore can prepare any new media branch.</summary>
    private static void Validate(Rig r)
    {
        var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [r.Nic] = "nic" };
        if (r.Pci is not null) owners[r.Pci] = "pci";
        SchedulerState time = r.Clock.SaveState(owners); Ne2000.ValidateSnapshot(r.Nic.SaveState(), time, "nic", 0x300);
        if (r.Pci is not null) Rtl8029.ValidateSnapshot(r.Pci.SaveState(), time, "pci");
    }
    /// <summary>Replaces one exact typed state member in a fresh independent field dictionary for negative controls.</summary>
    private static StateBlock Alter<T>(StateBlock state, string field, T value)
    {
        var fields = new Dictionary<string, JsonElement>(state.Fields, StringComparer.Ordinal) { [field] = JsonSerializer.SerializeToElement(value, StateValues.Json) };
        return state with { Fields = fields };
    }
    /// <summary>Clocks an explicit 93C46 read transaction, including its dummy zero, through real original Realtek page-three pins.</summary>
    private static ushort EepromRead(Rig r, int word)
    {
        r.Out(0, 0xe2); r.Out(1, 0x80); int command = 0x180 | word;
        for (int bit = 8; bit >= 0; bit--)
        { uint pins = (uint)(0x88 | ((command >> bit) & 1) << 1); r.Out(1, pins); r.Out(1, pins | 4); }
        Check((r.In(1) & 1) == 0, "actual EEPROM read dummy zero"); ushort result = 0;
        for (int bit = 0; bit < 16; bit++) { r.Out(1, 0x88); r.Out(1, 0x8c); result = (ushort)(((uint)result << 1) | (r.In(1) & 1)); }
        r.Out(1, 0x80); r.Out(1, 0); return result;
    }
    /// <summary>Programs real stopped DMA/ring/TX registers from power-on without ever issuing a STA command.</summary>
    private static Rig ProgramStopped(bool pci)
    {
        Rig r = new(pci); byte[] frame = Frame(); r.Out(14, 9); r.Out(8, 0); r.Out(9, 0x40); r.Out(10, 60); r.Out(11, 0); r.Out(0, 0x11);
        for (int i = 0; i < frame.Length; i += 2) r.Out(0x10, (uint)(frame[i] | frame[i + 1] << 8), 2);
        r.Out(0, 0x61); for (int i = 0; i < 6; i++) r.Out(i + 1, Mac[i]); r.Out(7, 0x47);
        r.Out(0, 0x21); r.Out(1, 0x46); r.Out(2, 0x80); r.Out(3, 0x46); r.Out(4, 0x40); r.Out(5, 60); r.Out(6, 0);
        r.Out(12, 4); r.Out(13, 0); r.Out(7, 0x7f); r.Nic.SetCableConnected(true); return r;
    }
    /// <summary>Programs the original Crynwr NE2000 PROM access contract: WTS, thirty-two DMA bytes, address zero and byte INs.</summary>
    private static void CrynwrProm(Rig r,bool word)
    {
        r.Out(0,0x21);r.Out(14,word?0x49u:0x48u);r.Out(10,0x20);r.Out(11,0);r.Out(8,0);r.Out(9,0);r.Out(0,0x0a);
    }
    /// <summary>Builds the logical fitted PROM independently from the board's byte/word host acknowledgment behavior.</summary>
    private static byte[] LogicalProm(byte[] identity)
    { byte[] bytes=new byte[16];identity.CopyTo(bytes,0);bytes[14]=bytes[15]=0x57;return bytes; }
    /// <summary>Compares all generated NIC/PCI/RAM/queue fields and every scheduler field without exclusions.</summary>
    private static void Same(Rig left, Rig right)
    {
        // Serialize all card, SRAM, PCI and scheduler fields for an exact continuation comparison.
        static string State(Rig r)
        {
            var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [r.Nic] = "nic" };
            if (r.Pci is not null) owners[r.Pci] = "pci";
            return JsonSerializer.Serialize(new { Nic = r.Nic.SaveState(), Pci = r.Pci?.SaveState(), Clock = r.Clock.SaveState(owners) }, StateValues.Json);
        }
        Check(State(left) == State(right), "complete NIC/RAM/queue/PCI/scheduler state");
    }
    /// <summary>Registers device-level actual bus/packet/IRQ and full generated-component continuation controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool pci in new[] { false, true })
        {
            string prefix = pci ? "Network RTL8029: " : "Network NE2000: ";
            // Register this card-specific regression under its distinct bus/model prefix.
            void Add(string name, Action action) => test(prefix + name, action);
            Add("immutable binding and disconnected reset identity", () =>
            {
                Rig r = new(pci); Check(r.Nic.HasBindings(r.Clock, 0x300, Mac) && !r.Nic.HasBindings(new(), 0x300, Mac), "immutable bindings");
                Check(!r.Nic.CableConnected && r.In(0) == 0x21 && r.In(7) == 0x80 && !r.Nic.InterruptAsserted && r.Clock.NextEventTime is null, "actual reset");
                byte[] copy = r.Nic.MacAddress; copy[0] ^= 0xff; Check(r.Nic.HasBindings(r.Clock, 0x300, Mac), "MAC getter ownership");
                r.Nic.ValidateSnapshot(); r.Pci?.ValidateSnapshot();
            });
            foreach (int width in pci ? new[] { 1, 2, 4 } : new[] { 1, 2 })
            foreach (int size in new[] { 1, 2, 3, 15, 16, 31, 32, 255, 256, 511, 512, 1514, 16_384 })
            {
                int w = width, n = size;
                Add($"real DMA roundtrip width {w}, count {n}", () =>
                {
                    Rig r = new(pci); r.Start(); byte[] expected = Enumerable.Range(0, n).Select(i => (byte)(i * 73 + 41)).ToArray();
                    r.Store(0x4000, expected, w); Check((r.In(7) & 0x40) != 0, "actual write RDC");
                    Check(r.Load(0x4000, n, w).SequenceEqual(expected), "every DMA payload byte"); Check((r.In(7) & 0x40) != 0, "actual read RDC");
                    Check(r.In(0x10, w) == (w == 4 ? uint.MaxValue : w == 2 ? 65535u : 255u), "no overrun after terminal transfer");
                    r.Nic.ValidateSnapshot();
                });
            }
            Add("PROM byte stream is actual immutable board identity", () =>
            {
                Rig r = new(pci); r.Dma(0, 32, true, 1); byte[] actual = Enumerable.Range(0, 32).Select(_ => (byte)r.In(0x10)).ToArray();
                byte[] expected = new byte[32];
                for (int i = 0; i < expected.Length; i++) { int j = pci ? i & 15 : i >> 1; expected[i] = j < 6 ? Mac[j] : j >= 14 ? (byte)(pci && i >= 30 ? 0x42 : 0x57) : (byte)0; }
                Check(actual.SequenceEqual(expected), "entire physical PROM");
                r.Store(0, Enumerable.Repeat((byte)0xa5, 32).ToArray(), 1); Check(r.Load(0, 32, 1).SequenceEqual(expected), "PROM write protection");
            });
            Add("word-mode byte PROM reads honor ISA word ACK and PCI byte enables", () =>
            {
                Rig r = new(pci); r.Dma(0, 32, true, 2); byte[] actual = Enumerable.Range(0, 32).Select(_ => (byte)r.In(0x10)).ToArray();
                byte[] expected=pci?[..LogicalProm(Mac),..LogicalProm(Mac)[..14],0x42,0x42]:[..LogicalProm(Mac),..Enumerable.Repeat((byte)255,16)];
                Check(actual.SequenceEqual(expected),"complete board-specific byte-IN PROM stream");
            });
            Add("remote read prefetch advances CRDA without data-port polling", () =>
            {
                Rig r = new(pci); r.Start(); r.Store(0x4000, [0x12, 0x34, 0x56]); r.Dma(0x4000, 3, true, 2);
                Check(r.In(8) == 2 && r.In(9) == 0x40 && (r.In(7) & 0x40) == 0, "real first prefetch");
                Check(r.In(0x10, 2) == 0x3412 && r.In(8) == 3 && (r.In(7) & 0x40) != 0 && r.In(0x10, 2) == 0xff56, "prefetch completion and odd word");
            });
            Add("BOS changes word lanes without reordering packet RAM", () =>
            {
                Rig r = new(pci); r.Start(); r.Dma(0x4000, 2, false, 2, true); r.Out(0x10, 0x1234, 2);
                Check(r.Load(0x4000, 2).SequenceEqual(new byte[] { 0x12, 0x34 }), "stored swapped lanes");
                r.Dma(0x4000, 2, true, 2, true); Check(r.In(0x10, 2) == 0x1234, "read swapped lanes");
            });
            Add("mixed byte word reads follow the actual board transfer acknowledgment", () =>
            {
                Rig r = new(pci); r.Start(); r.Store(0x4000, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]); r.Dma(0x4000, 7, true, 2);
                if(pci)Check(r.In(0x10) == 0x11 && r.In(0x10, 2) == 0x3322 && r.In(0x10) == 0x44 && r.In(0x10, 2) == 0x6655 &&
                    r.In(0x10, 2) == 0xff77 && r.In(0x10) == 255, "PCI byte enables preserve every unrequested lane");
                else Check(r.In(0x10)==0x11&&r.In(0x10,2)==0x4433&&r.In(0x10)==0x55&&r.In(0x10,2)==0xff77&&r.In(0x10)==255,
                    "each ISA byte-IN acknowledges and discards its unrequested upper word lane");
            });
            Add("clearing WTS preserves the existing byte-wide ACK even with a prefetched word",()=>
            {
                Rig r=new(pci);r.Start();r.Store(0x4000,[0x11,0x22,0x33]);r.Dma(0x4000,3,true,2);r.Out(14,8);
                Check(r.In(0x10)==0x11&&r.In(0x10)==0x22&&r.In(0x10)==0x33&&r.In(0x10)==255,"WTS0 byte reads never discard a saved upper byte");Validate(r);
            });
            foreach(bool word in new[]{false,true})
            {
                bool wordMode=word;
                Add("original PROM sequence and exact CRDA RBCR RDC word="+word,()=>
                {
                    byte[] identity=[2,0x44,0x4f,0x53,0x19,2];Rig r=new(pci,identity);CrynwrProm(r,wordMode);byte[] logical=LogicalProm(identity);
                    byte[] expected=pci?[..logical,..logical[..14],0x42,0x42]:wordMode?logical:logical.SelectMany(value=>new[]{value,value}).ToArray();
                    int reads=expected.Length;
                    for(int i=0;i<reads;i++)
                    {
                        Check(r.In(0x10)==expected[i],"actual original PROM byte "+i);int acknowledged=i+1;
                        int cursor=wordMode?Math.Min((!pci?acknowledged+1:acknowledged/2+1)*2,32):Math.Min(acknowledged+1,32);
                        var actual=r.Nic.SaveState();Check(r.In(8)==cursor&&r.In(9)==0&&actual.Fields["_remoteCount"].GetUInt16()==32-cursor,
                            "exact prefetched DMA cursor/count after byte IN "+i);
                        Check(((r.In(7)&0x40)!=0)==(cursor==32),"RDC follows the real remaining prefetched transfer "+i);
                    }
                    Check(r.In(0x10)==255&&r.Nic.SaveState().Fields["_remoteReadValidBytes"].GetByte()==0,"terminal PROM byte IN is undriven");Validate(r);
                });
            }
            foreach(int cut in new[]{0,1,5,14,15})
            {
                int boundary=cut;
                Add("actual word-board PROM byte-IN saved future cut "+cut,()=>
                {
                    byte[] identity=[2,0x44,0x4f,0x53,0x19,2];Rig r=new(pci,identity);CrynwrProm(r,true);
                    byte[] logical=LogicalProm(identity),expected=pci?[..logical,..logical[..14],0x42,0x42]:logical;
                    for(int i=0;i<boundary;i++)Check(r.In(0x10)==expected[i],"actual pre-cut PROM byte "+i);
                    Rig copy=Clone(r);Same(r,copy);
                    for(int i=boundary;i<expected.Length;i++){Check(r.In(0x10)==expected[i]&&copy.In(0x10)==expected[i],"independent resumed PROM byte "+i);Same(r,copy);}
                    Check(r.In(0x10)==255&&copy.In(0x10)==255,"no bytes after terminal word ACK");Same(r,copy);Validate(r);Validate(copy);
                });
            }
            Add("automatic packet boundary advances only after the last board-specific byte ACK",()=>
            {
                Rig r=new(pci);r.Start();r.Store(0x4600,[1,0x48,4,0,0xa1,0xb2,0xc3,0xd4]);r.Out(0,0x22);r.Out(14,0x19);
                r.Out(10,0);r.Out(11,0x0f);r.Out(0,0x1a);byte[] expected=pci?[0xa1,0xb2,0xc3,0xd4]:[0xa1,0xc3];
                for(int i=0;i<expected.Length-1;i++){Check(r.In(0x10)==expected[i]&&r.In(3)==0x46,"no early automatic packet release");}
                Rig copy=Clone(r);Same(r,copy);
                foreach(Rig side in new[]{r,copy})
                {
                    Check(side.Nic.SaveState().Fields["_remoteAutoPacket"].GetBoolean(),"actual pending final automatic ACK");
                    Check(side.In(0x10)==expected[^1]&&side.In(3)==0x48&&(side.In(7)&0x40)!=0&&side.In(0x10)==255,
                        "last real byte ACK releases exactly the original next-page pointer");Validate(side);
                }
                Same(r,copy);
            });
            Add("RDC masked source, late IMR enable and W1C preserve IRQ edges", () =>
            {
                Rig r = new(pci); r.Start(); r.Dma(0x4000, 2, false); r.Out(0x10, 0xa1b2, 2);
                Check(!r.Nic.InterruptAsserted && r.Edges.Count == 0, "masked completion"); r.Out(15, 0x40);
                Check(r.Nic.InterruptAsserted && r.Edges.SequenceEqual(new[] { true }), "late enable"); r.Out(7, 1); Check(r.Nic.InterruptAsserted, "unrelated W1C");
                r.Out(7, 0x40); Check(!r.Nic.InterruptAsserted && r.Edges.SequenceEqual(new[] { true, false }), "exact IRQ release");
            });
            foreach (int length in new[] { 60, 61, 248, 249, 250, 251, 252, 253, 254, 255, 256, 511, 512, 1514 })
            {
                int n = length;
                Add("actual 10 Mbit TX deadline and immutable data length " + n, () =>
                {
                    Rig r = new(pci); r.Start(); byte[] frame = Frame(n); r.Out(15, 2); r.Send(frame);
                    ulong expected = (ulong)(8 + n + 4) * 8 * 100;
                    Check(r.Nic.TransmitDeadline == expected && r.Sent.Count == 0 && (r.In(0) & 4) != 0, "actual pending TX");
                    r.Store(0x4000, Enumerable.Repeat((byte)0xe7, n).ToArray()); r.Out(0, 0x22); Check((r.In(0) & 4) != 0, "TXP zero cannot cancel");
                    r.Clock.AdvanceTo(expected - 1); Check(r.Sent.Count == 0 && (r.In(7) & 2) == 0, "not early");
                    r.Clock.AdvanceTo(expected); Check(r.Sent.Count == 1 && r.Sent[0].Time == expected && r.Sent[0].Frame.SequenceEqual(frame), "entire captured wire packet");
                    Check((r.In(7) & 2) != 0 && r.In(4) == 1 && (r.In(0) & 4) == 0 && r.Nic.InterruptAsserted, "actual PTX IRQ/status");
                    r.Out(7, 2); Check(!r.Nic.InterruptAsserted, "PTX ack"); r.Nic.ValidateSnapshot();
                });
                Add("actual RX ring header payload FCS length " + n, () =>
                {
                    Rig r = new(pci); r.Start(); byte[] frame = Frame(n); r.Out(15, 1); Check(r.Nic.QueueReceive(frame), "input accepted"); frame = frame.ToArray();
                    ulong expected = (ulong)(8 + n + 4) * 800; r.Clock.AdvanceTo(expected - 1); Check((r.In(7) & 1) == 0, "no early PRX");
                    r.Clock.AdvanceTo(expected); Check((r.In(7) & 1) != 0 && r.Nic.InterruptAsserted, "actual PRX");
                    RingProof(r, 0x47, 0x47 + (n + 8 + 255) / 256, frame); r.Out(7, 1); Check(!r.Nic.InterruptAsserted, "PRX ack");
                });
            }
            Add("disconnected transmission reports carrier loss without backend output", () =>
            {
                Rig r = new(pci); r.Start(); r.Nic.SetCableConnected(false); r.Send(Frame()); ulong end = r.Nic.TransmitDeadline; r.Clock.AdvanceTo(end);
                Check(r.Sent.Count == 0 && r.In(4) == 0x11 && (r.In(7) & 0x0a) == 2, "CRS accompanies completed MAC transmission, without TXE");
            });
            Add("disconnect reconnect cannot repair a physically interrupted frame", () =>
            {
                Rig r = new(pci); r.Start(); r.Send(Frame()); r.Clock.AdvanceTo(1000); r.Nic.SetCableConnected(false); r.Nic.SetCableConnected(true);
                Rig clone = Clone(r); foreach (Rig side in new[] { r, clone }) side.Clock.AdvanceTo(side.Nic.TransmitDeadline);
                Check(r.Sent.Count == 0 && clone.Sent.Count == 0 && r.In(4) == 0x11 && clone.In(4) == 0x11, "latched carrier loss survives continuation"); Same(r, clone);
            });
            foreach (int length in new[] { 60, 511, 1514 })
            foreach (byte mask in new byte[] { 2, 8 })
                Add($"Win98 CRS completes MAC without abort length={length} mask={mask}", () =>
                {
                    Rig r = new(pci); r.Start(); r.Nic.SetCableConnected(false); r.Out(15, mask);
                    byte[] frame = Frame(length); r.Send(frame); ulong deadline = r.Nic.TransmitDeadline;
                    Check(deadline == Ne2000.WireDuration(length) && (r.In(0) & 4) != 0, "unchanged accepted frame and deadline");
                    r.Clock.AdvanceTo(deadline - 1);
                    Check((r.In(7) & 10) == 0 && !r.Nic.InterruptAsserted, "no early completion");
                    r.Clock.AdvanceTo(deadline);
                    Check(r.In(4) == 0x11 && (r.In(7) & 10) == 2 && (r.In(0) & 4) == 0 && r.Sent.Count == 0,
                        "CRS must complete through PTX without ABT/FU/TXE or external publication");
                    Check(r.Nic.InterruptAsserted == (mask == 2), "PTX and TXE masks remain separate");
                    r.Out(7, 8); Check((r.In(7) & 2) != 0, "wrong source acknowledgment preserves PTX");
                    r.Out(7, 2); Check(!r.Nic.InterruptAsserted && r.In(4) == 0x11, "W1C and sticky TSR remain separate");
                    r.Nic.SetCableConnected(true); r.Send(frame); r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                    Check(r.In(4) == 1 && (r.In(7) & 10) == 2 && r.Sent.Count == 1 && r.Sent[0].Frame.SequenceEqual(frame),
                        "a later connected transmission publishes only its own complete packet");
                });
            foreach (bool reconnect in new[] { false, true })
                Add($"Win98 CRS active full component continuation reconnect={reconnect}", () =>
                {
                    Rig r = new(pci); r.Start(); r.Out(15, 10); r.Send(Frame(511)); r.Clock.AdvanceTo(1000);
                    r.Nic.SetCableConnected(false); if (reconnect) r.Nic.SetCableConnected(true);
                    Rig clone = Clone(r); ulong deadline = r.Nic.TransmitDeadline;
                    foreach (Rig side in new[] { r, clone })
                    {
                        side.Clock.AdvanceTo(deadline - 1); Check((side.In(7) & 10) == 0, "restored loss is not early");
                        side.Clock.AdvanceTo(deadline); Check(side.In(4) == 0x11 && (side.In(7) & 10) == 2 && side.Sent.Count == 0,
                            "restored carrier loss has PTX/CRS and no external delivery");
                    }
                    Same(r, clone);
                });
            Add("Win98 CRS software STOP retains real completion and reset source", () =>
            {
                Rig r = new(pci); r.Start(); r.Nic.SetCableConnected(false); r.Send(Frame()); ulong end = r.Nic.TransmitDeadline;
                r.Out(0, 0x21); Check(r.Nic.TransmitDeadline == end && (r.In(7) & 0x82) == 0, "STOP does not fabricate early completion");
                r.Clock.AdvanceTo(end); Check(r.In(4) == 0x11 && (r.In(7) & 0x8a) == 0x82 && r.Sent.Count == 0,
                    "STOP receives actual PTX plus RST, not a carrier-induced abort");
            });
            Add("Win98 CRS internal loopback remains independent of disconnected external carrier", () =>
            {
                Rig r = new(pci); r.Start(); r.Nic.SetCableConnected(false); r.Send(Frame(), config: 2, dataConfiguration: 0x40);
                r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                Check(r.In(4) == 0x53 && (r.In(7) & 0x0a) == 2 && r.Sent.Count == 0, "existing diagnostic FIFO/PTX behavior remains separate");
            });
            Add("disconnect discards every queued incoming frame", () =>
            {
                Rig r = new(pci); r.Start(); Check(r.Nic.QueueReceive(Frame()) && r.Nic.QueueReceive(Frame()), "two pending frames");
                r.Nic.SetCableConnected(false); r.Nic.SetCableConnected(true); r.Clock.AdvanceTo(1_000_000);
                Check(r.Nic.PendingReceiveCount == 0 && (r.In(7) & 1) == 0, "no pre-disconnect frame reappears");
            });
            Add("interframe gap delays the next real packet", () =>
            {
                Rig r = new(pci); r.Start(); byte[] frame = Frame(); r.Send(frame); r.Clock.AdvanceTo(r.Nic.TransmitDeadline); ulong first = r.Clock.Now;
                r.Send(frame); Check(r.Nic.TransmitDeadline == first + 9600 + (60 + 12) * 800, "actual next deadline including IFG");
                r.Clock.AdvanceTo(r.Nic.TransmitDeadline); Check(r.Sent.Count == 2, "two real packets");
            });
            Add("queued incoming frames preserve 10 Mbit interframe phase", () =>
            {
                Rig r = new(pci); r.Start(); r.Nic.QueueReceive(Frame()); r.Nic.QueueReceive(Frame()); ulong first = r.Nic.ReceiveDeadline;
                r.Clock.AdvanceTo(first); Check(r.Nic.ReceiveDeadline == first + 9600 + (60 + 12) * 800, "second RX deadline including IFG");
                r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); r.Out(0, 0x62); Check(r.In(7) == 0x49, "two actual complete ring packets");
            });
            Add("internal loopback completes through FIFO without SRAM or ISR PRX", () =>
            {
                Rig r = new(pci); r.Start(); r.Nic.SetCableConnected(false); byte[] frame = Frame(); r.Out(15, 6); r.Send(frame, 2, 0);
                var before = r.Nic.SaveState(); r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                DiagnosticProof(r, StateValues.Read<byte[]>(before.Fields["_ram"]), before.Fields["_currentPage"].GetByte(), Wire(frame, true), (byte)(pci ? 6 : 2), 2);
                Check(r.Nic.InterruptAsserted, "actual diagnostic IRQ"); r.Out(7, 2); Check(r.Nic.InterruptAsserted == pci, "PTX and receiver error ACK remain separate"); r.Out(7, 4); Check(!r.Nic.InterruptAsserted, "RXE ACK");
            });
            Add("loopback FIFO cursor has a full independent saved future", () =>
            {
                Rig r = new(pci); r.Start(); r.Send(Frame(), 2, 0); r.Clock.AdvanceTo(r.Nic.TransmitDeadline); r.In(6); r.In(6); r.In(6);
                Rig clone = Clone(r); for (int i = 0; i < 13; i++) Check(r.In(6) == clone.In(6), "actual retained FIFO continuation byte " + i); Same(r, clone);
            });
            Add("CRC inhibit accepts genuine supplied FCS without forwarding it as payload", () =>
            {
                Rig r = new(pci); r.Start(); byte[] frame = Frame(); uint crc = Fcs(frame);
                byte[] bytes = [.. frame, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)];
                r.Send(bytes, 1); r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                Check(r.Sent.Count == 1 && r.Sent[0].Frame.SequenceEqual(frame), "actual supplied CRC and forwarded frame");
            });
            Add("bad supplied CRC is sender PTX and is dropped at the validated backend boundary", () =>
            {
                Rig r = new(pci); r.Start(); r.Send([.. Frame(), 0, 0, 0, 0], 1); r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                Check(r.Sent.Count == 0 && (r.In(7) & 0x0a) == 2 && r.In(4) == 1, "bad wire CRC is not a sender ABT/TXE");
            });
            foreach (bool good in new[] { false, true })
            foreach (bool matches in new[] { false, true })
            foreach (int length in new[] { 60, 61, 65, 255, 511 })
            {
                bool crcGood = good, match = matches; int n = length;
                Add($"supplied CRC loopback checker/address/FIFO n{n} good{good} match{matches}", () =>
                {
                    Rig r = new(pci); r.Start(); byte[] payload = Frame(n, match ? Mac : new byte[] { 2, 9, 8, 7, 6, 5 }); byte[] wire = Wire(payload, true);
                    if (!crcGood) wire[^1] ^= 0x80; r.Out(15, 6); r.Send(wire, 3, 0); var before = r.Nic.SaveState();
                    r.Clock.AdvanceTo(r.Nic.TransmitDeadline); byte expected = (byte)(match && !crcGood ? 2 : 1);
                    DiagnosticProof(r, StateValues.Read<byte[]>(before.Fields["_ram"]), before.Fields["_currentPage"].GetByte(), wire, (byte)(expected == 2 ? 6 : 2), expected);
                    Check(r.In(14) == (expected == 2 ? 1u : 0), "CRC tally follows only the address-matching receiver error");
                });
            }
            foreach (int length in new[] { 60, 61, 62, 63, 64, 65, 66, 67 })
            {
                int n = length;
                Add("generated CRC physical FIFO alignment " + n, () =>
                {
                    Rig r = new(pci); r.Start(); byte[] frame = Frame(n); r.Send(frame, 2, 0); var before = r.Nic.SaveState();
                    r.Clock.AdvanceTo(r.Nic.TransmitDeadline); DiagnosticProof(r, StateValues.Read<byte[]>(before.Fields["_ram"]), before.Fields["_currentPage"].GetByte(), Wire(frame, true), (byte)(pci ? 6 : 2), 2);
                    if (n == 60) { var fifo = StateValues.Read<byte[]>(r.Nic.SaveState().Fields["_loopbackFifo"]); Check(fifo[0] == 64 && fifo[1] == 0 && fifo[2] == 0 && fifo[3] == frame[^1], "National P30 explicit 64-byte FIFO diagram"); }
                });
            }
            foreach (byte data in new byte[] { 1, 3 })
            {
                byte dcr = data;
                Add("original ISA word loopback lane versus Realtek byte-addressed path DCR " + data, () =>
                {
                    Rig r = new(pci); r.Start(); byte[] frame = Frame(); byte[] memory;
                    if (pci) memory = frame;
                    else { memory = Enumerable.Repeat((byte)0xa7, frame.Length * 2).ToArray(); for (int i = 0; i < frame.Length; i++) memory[i * 2 + ((dcr & 2) != 0 ? 0 : 1)] = frame[i]; }
                    r.Send(memory, 2, dcr); Check(r.Nic.TransmitDeadline == (60 + 12) * 800, "one-byte wire clock despite original 2*n TBCR"); var before = r.Nic.SaveState();
                    r.Clock.AdvanceTo(r.Nic.TransmitDeadline); DiagnosticProof(r, StateValues.Read<byte[]>(before.Fields["_ram"]), before.Fields["_currentPage"].GetByte(), Wire(frame, true), (byte)(pci ? 6 : 2), 2);
                });
            }
            Add("internal loopback excludes external cable traffic", () =>
            {
                Rig r = new(pci); r.Start(); r.Out(13, 2); Check(!r.Nic.QueueReceive(Frame()) && r.Nic.ReceiveEventToken == 0, "internal path ignores the cable");
                r.Out(13, 0); Check(r.Nic.QueueReceive(Frame()), "normal receiver is enabled again"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); RingProof(r, 0x47, 0x48, Frame());
            });
            foreach (int mode in new[] { 4, 6 })
            {
                int config = mode;
                Add("unattached external loopback mode fails explicitly " + mode, () =>
                { Rig r = new(pci); r.Start(); r.Send(Frame(), (byte)config); r.Clock.AdvanceTo(r.Nic.TransmitDeadline); Check(r.Sent.Count == 0 && (r.In(7) & 8) != 0, "external loopback error"); });
            }
            Add("receive wraps through actual ring stop without touching transmit RAM", () =>
            {
                Rig r = new(pci); r.Start(4, 0x46, 0x4a, 0x48, 0x49); byte[] guard = Enumerable.Repeat((byte)0xd7, 256).ToArray(); r.Store(0x4000, guard);
                byte[] frame = Frame(300); Check(r.Nic.QueueReceive(frame), "wrapped input"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                RingProof(r, 0x49, 0x47, frame); Check(r.Load(0x4000, guard.Length).SequenceEqual(guard), "transmit guard untouched");
            });
            Add("ring overflow preserves every previously stored byte and requires software recovery", () =>
            {
                Rig r = new(pci); r.Start(4, 0x46, 0x4a, 0x48, 0x49); byte[] old = Enumerable.Repeat((byte)0x9e, 1024).ToArray(); r.Store(0x4600, old);
                Check(r.Nic.QueueReceive(Frame(800)), "overflow input"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                Check((r.In(7) & 0x91) == 0x90 && r.In(12) == 0x10 && r.In(15) == 1, "OVW/RST/missed status");
                Check(r.Load(0x4600, old.Length).SequenceEqual(old), "entire old ring preserved");
            });
            foreach (byte rx in new byte[] { 0, 4, 0x10, 0x20, 0x24 })
            {
                byte config = rx;
                Add("broadcast and monitor filter " + rx, () =>
                {
                    Rig r = new(pci); r.Start(config); byte[] frame = Frame(60, Enumerable.Repeat((byte)0xff, 6).ToArray());
                    Check(r.Nic.QueueReceive(frame), "physical broadcast ingress"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                    bool expected = (config & 0x20) == 0 && (config & 4) != 0;
                    Check(((r.In(7) & 1) != 0) == expected, "actual broadcast filter"); if (expected) RingProof(r, 0x47, 0x48, frame, true);
                });
            }
            foreach (bool enabled in new[] { false, true })
            {
                bool accept = enabled;
                Add("multicast hash bitmap " + enabled, () =>
                {
                    Rig r = new(pci); r.Start(8); r.Out(0, 0x62); for (int i = 8; i < 16; i++) r.Out(i, accept ? 255u : 0); r.Out(0, 0x22);
                    byte[] frame = Frame(60, [1, 0, 0x5e, 0, 0, 1]); Check(r.Nic.QueueReceive(frame), "physical multicast"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                    Check(((r.In(7) & 1) != 0) == accept, "actual MAR filtering"); if (accept) RingProof(r, 0x47, 0x48, frame, true);
                });
                Add("runt acceptance has a real RCR gate " + enabled, () =>
                {
                    Rig r = new(pci); r.Start((byte)(accept ? 2 : 0)); byte[] frame = Frame(42); Check(r.Nic.QueueReceive(frame), "physical runt"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                    Check(((r.In(7) & 1) != 0) == accept, "actual AR gate"); if (accept) RingProof(r, 0x47, 0x48, frame);
                });
            }
            foreach (byte[] destination in new byte[][] { [1, 0, 0x5e, 0, 0, 1], [1, 0, 0x5e, 127, 255, 250], [0x33, 0x33, 0, 0, 0, 1], [3, 4, 5, 6, 7, 8] })
            foreach (bool enabled in new[] { false, true })
            {
                byte[] target = destination.ToArray(); bool accept = enabled;
                Add("one exact multicast CRC filter bit " + Convert.ToHexString(target) + " " + accept, () =>
                {
                    Rig r = new(pci); r.Start(8); int hash = Hash(target), selected = accept ? hash : hash ^ 1;
                    r.Out(0, 0x62); for (int i = 8; i < 16; i++) r.Out(i, 0); r.Out(8 + (selected >> 3), 1u << (selected & 7)); r.Out(0, 0x22);
                    byte[] frame = Frame(60, target); r.Nic.QueueReceive(frame); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                    Check(((r.In(7) & 1) != 0) == accept, "independent exact CRC index " + hash); if (accept) RingProof(r, 0x47, 0x48, frame, true);
                });
            }
            Add("PRO changes unicast comparison while keeping broadcast and multicast gates", () =>
            {
                Rig r = new(pci); r.Start(0x10); byte[] foreign = Frame(60, [2, 3, 4, 5, 6, 7]); r.Nic.QueueReceive(foreign); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline);
                RingProof(r, 0x47, 0x48, foreign); r.Out(7, 1);
                r.Nic.QueueReceive(Frame(60, [1, 0, 0x5e, 0, 0, 1])); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); Check((r.In(7) & 1) == 0, "PRO does not bypass AM/MAR");
                r.Nic.QueueReceive(Frame(60, Enumerable.Repeat((byte)0xff, 6).ToArray())); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); Check((r.In(7) & 1) == 0, "PRO does not bypass AB");
            });
            Add("monitor tallies saturate at C0 and CNT has its actual masked source", () =>
            {
                Rig r = new(pci); r.Start(0x20); r.Out(15, 0x20); Check(r.In(12) == 0x40, "monitor disabled status");
                for (int i = 0; i < 200; i++) { r.Nic.QueueReceive(Frame()); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); }
                Check(r.In(15) == 0xc0 && r.In(15) == 0 && r.In(12) == 0x50 && (r.In(7) & 0x21) == 0x20 && r.Nic.InterruptAsserted, "saturating missed counter and actual CNT IRQ");
                r.Out(7, 0x20); Check(!r.Nic.InterruptAsserted, "CNT acknowledgment"); r.Out(12, 0); Check((r.In(12) & 0x40) == 0, "exit monitor clears DIS"); Validate(r);
            });
            Add("input ownership bounded queue and reset cancel only own future work", () =>
            {
                Rig r = new(pci); r.Start(); byte[] frame = Frame(); Check(r.Nic.QueueReceive(frame), "first physical input"); frame[20] ^= 0xff;
                for (int i = 1; i < Ne2000.ReceiveQueueLimit; i++) Check(r.Nic.QueueReceive(Frame()), "bounded input " + i);
                Check(!r.Nic.QueueReceive(Frame()) && r.Nic.PendingReceiveCount == Ne2000.ReceiveQueueLimit, "drop new on bounded ingress overflow");
                bool peer = false; r.Clock.ScheduleAfter(200_000, () => peer = true); r.Send(Frame()); r.Nic.Reset();
                r.Clock.AdvanceTo(200_000); Check(peer && r.Sent.Count == 0 && r.Nic.PendingReceiveCount == 0 && r.In(7) == 0x80 && r.Nic.CableConnected, "reset cancels only NIC callbacks");
            });
            Add("caller input is copied before delayed receive", () =>
            {
                Rig r = new(pci); r.Start(); byte[] frame = Frame(), expected = frame.ToArray(); Check(r.Nic.QueueReceive(frame), "physical input"); Array.Fill(frame, (byte)0);
                r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); RingProof(r, 0x47, 0x48, expected);
            });
            Add("stopped and disconnected ingress cannot replay after later start", () =>
            {
                Rig r = new(pci); Check(!r.Nic.QueueReceive(Frame()), "unconnected stopped input rejected"); r.Start(); r.Nic.SetCableConnected(false);
                Check(!r.Nic.QueueReceive(Frame()), "unconnected running input rejected"); r.Nic.SetCableConnected(true); r.Clock.AdvanceTo(1_000_000);
                Check((r.In(7) & 1) == 0 && r.Nic.PendingReceiveCount == 0, "no stale packet");
            });
            Add("reset CR20 and CR24 cannot create work before an actual START", () =>
            {
                Rig r = ProgramStopped(pci); Check(r.In(0) == 0x21, "never-started stopped controller"); r.Out(0, 0x20);
                Check(!r.Nic.QueueReceive(Frame()) && r.Nic.ReceiveEventToken == 0, "STP zero without STA is not RX START");
                r.Out(0, 0x24); Check(r.Nic.TransmitEventToken == 0 && (r.In(0) & 4) == 0, "STP zero without STA is not TX START");
                r.Clock.AdvanceTo(1_000_000); Check(r.Sent.Count == 0 && (r.In(7) & 3) == 0, "no ghost completion");
                r.Out(0, 0x26); Check(r.Nic.TransmitEventToken > 0 && (r.In(0) & 3) == 2, "real STA admits TX"); r.Clock.AdvanceTo(r.Nic.TransmitDeadline);
                Check(r.Sent.Count == 1 && r.Sent[0].Frame.SequenceEqual(Frame()), "real admitted packet"); r.Out(0, 0x22);
                Check(r.Nic.QueueReceive(Frame()), "real STA admits RX"); r.Clock.AdvanceTo(r.Nic.ReceiveDeadline); RingProof(r, 0x47, 0x48, Frame()); Validate(r);
            });
            foreach (int scenario in new[] { 0, 1, 2 })
            foreach (ulong elapsed in new ulong[] { 0, 1000, 57_599 })
            {
                int kind = scenario; ulong beforeStop = elapsed;
                Add($"software STOP lets active wire frames finish kind{kind} elapsed{elapsed}", () =>
                {
                    Rig r = new(pci); r.Start(); r.Out(15, 3);
                    if (kind != 1) r.Send(Frame());
                    if (kind != 0) { r.Nic.QueueReceive(Frame(512)); r.Nic.QueueReceive(Frame(60, seed: 99)); }
                    ulong txEnd = r.Nic.TransmitDeadline, rxEnd = r.Nic.ReceiveDeadline; r.Clock.AdvanceTo(beforeStop); r.Out(0, 0x21);
                    Check((r.In(0) & 3) == 3 && (r.In(7) & 0x80) == 0 && !r.Nic.QueueReceive(Frame()), "STOP preserves STA and defers RST while blocking new ingress");
                    Check(r.Nic.PendingReceiveCount == (kind == 0 ? 0 : 1), "only already-started receive survives");
                    long oldTx = r.Nic.TransmitEventToken, oldRx = r.Nic.ReceiveEventToken; r.Out(0, 0x25);
                    Check(r.Nic.TransmitEventToken == oldTx && r.Nic.ReceiveEventToken == oldRx && r.Nic.TransmitDeadline == txEnd && r.Nic.ReceiveDeadline == rxEnd,
                        "STOP plus TXP never starts or restarts work");
                    Rig copy = Clone(r); Same(r, copy);
                    foreach (Rig side in new[] { r, copy })
                    {
                        if (kind == 2)
                        {
                            side.Clock.AdvanceTo(txEnd); Check((side.In(7) & 0x82) == 2 && side.Nic.ReceiveEventToken != 0, "RST waits for longer active RX after TX");
                        }
                        side.Clock.AdvanceTo(Math.Max(txEnd, rxEnd));
                        Check((side.In(0) & 7) == 3 && (side.In(7) & 0x80) != 0 && side.Nic.TransmitEventToken == 0 && side.Nic.ReceiveEventToken == 0,
                            "wire completion enters stopped/reset state");
                        Check(side.Sent.Count == (kind == 1 ? 0 : 1), "exact completed pre-STOP transmission count");
                        Check(!side.Nic.QueueReceive(Frame()), "receiver remains stopped after completion");
                        if (kind != 0) RingProof(side, 0x47, 0x4a, Frame(512));
                        else side.Out(0, 0x22);
                        Check((side.In(7) & 0x80) == 0, "explicit START clears software RST"); Validate(side);
                    }
                    Same(r, copy);
                });
            }
            Add("port reset still aborts frames pending behind a software STOP", () =>
            {
                Rig r = new(pci); r.Start(); r.Send(Frame()); r.Nic.QueueReceive(Frame(512)); r.Out(0, 0x21); Check(r.Nic.TransmitEventToken != 0 && r.Nic.ReceiveEventToken != 0, "software STOP kept active work");
                r.In(0x1f); Check(r.Nic.TransmitEventToken == 0 && r.Nic.ReceiveEventToken == 0 && r.In(0) == 0x21 && r.In(7) == 0x80, "port reset cancels both frames immediately");
                r.Clock.AdvanceTo(1_000_000); Check(r.Sent.Count == 0 && r.Nic.PendingReceiveCount == 0, "cancelled frames do not reappear"); Validate(r);
            });
            foreach (int width in new[] { 2, 4 })
            {
                int busWidth = width;
                Add("wide register access does not touch adjacent byte registers " + width, () =>
                {
                    Rig r = new(pci); r.Start(); string before = JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json);
                    Check(r.In(7, busWidth) == (busWidth == 2 ? 65535u : uint.MaxValue), "wide register open bus"); r.Out(7, uint.MaxValue, busWidth);
                    Check(before == JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json), "all state unchanged by wide register access");
                });
            }
            Add("data transfer cannot cross into the reset alias range", () =>
            {
                Rig r = new(pci); r.Start(); r.Dma(0x4000, 4, false); string before = JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json);
                r.Out(0x17, uint.MaxValue, 2); Check(r.In(0x17, 2) == 65535, "crossing data offer floats");
                Check(before == JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json), "complete crossing offer has no hidden DMA/reset side effect");
            });
            foreach (int reset in Enumerable.Range(0x18, 8))
            {
                int alias = reset;
                Add("real board reset alias " + reset.ToString("X2"), () =>
                {
                    Rig r = new(pci); r.Start(); byte[] guard = Enumerable.Range(0, 64).Select(i => (byte)(i * 19)).ToArray(); r.Store(0x4300, guard); r.Send(Frame());
                    Check(r.In(alias) == 0 && r.In(0) == 0x21 && r.In(7) == 0x80 && r.Nic.TransmitEventToken == 0, "actual alias reset");
                    Check(r.Load(0x4300, guard.Length).SequenceEqual(guard), "powered packet RAM survives reset"); r.Clock.AdvanceTo(1_000_000); Check(r.Sent.Count == 0, "reset killed old frame"); Validate(r);
                });
            }
            foreach (int cut in new[] { 0, 1, 2, 3 })
            {
                int boundary = cut;
                Add("full component/scheduler future cut " + cut, () =>
                {
                    Rig r = new(pci); r.Start(); byte[] frame = Frame(511); r.Send(frame); Check(r.Nic.QueueReceive(Frame(512)), "future RX");
                    if (boundary == 1) { r.Dma(0x4000, 511, true); Check(r.In(0x10) == frame[0], "native byte ACK at a word-prefetch boundary"); }
                    if (boundary == 2) r.Clock.AdvanceTo(1000);
                    if (boundary == 3) { r.Dma(0x4100, 3, false); r.Out(0x10, 0x6ba9); }
                    Rig copy = Clone(r); Same(r, copy);
                    ulong end = Math.Max(r.Nic.TransmitDeadline, r.Nic.ReceiveDeadline) + 100_000;
                    foreach (Rig side in new[] { r, copy })
                    {
                        if (boundary == 1) { side.In(0x10); side.In(0x10, 2); }
                        if (boundary == 3) { side.Out(0x10, 0x8c); side.Out(0x10, 0x7d); }
                        side.Clock.AdvanceTo(end); side.Nic.ValidateSnapshot(); side.Pci?.ValidateSnapshot();
                        Check(side.Sent.Count == 1 && side.Sent[0].Frame.SequenceEqual(frame), "independent actual future transmit");
                        RingProof(side, 0x47, 0x4a, Frame(512));
                    }
                    Same(r, copy); Check(r.Load(0x4700, 520).SequenceEqual(copy.Load(0x4700, 520)), "entire future ring"); Same(r, copy);
                });
            }
            foreach (string field in new[] { "_ram", "_physicalAddress", "_multicast", "_tally", "_remoteReadValidBytes", "_interruptAsserted", "_transmitDeadline", "_receiveDeadline", "_transmitFrame", "_receiveQueue", "_basePort", "_receiveEligibleAt", "_unknown" })
            {
                string member = field;
                Add("pre-media invalid state member " + field, () =>
                {
                    Rig r = new(pci); r.Start(); r.Send(Frame()); r.Nic.QueueReceive(Frame());
                    var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [r.Nic] = "nic" }; if (r.Pci is not null) owners[r.Pci] = "pci";
                    StateBlock raw = r.Nic.SaveState(); SchedulerState time = r.Clock.SaveState(owners);
                    StateBlock bad = member switch
                    {
                        "_ram" or "_physicalAddress" or "_multicast" or "_tally" => Alter(raw, member, new byte[1]),
                        "_remoteReadValidBytes" => Alter(raw, member, (byte)3), "_interruptAsserted" => Alter(raw, member, true),
                        "_transmitDeadline" or "_receiveDeadline" => Alter(raw, member, 1UL),
                        "_transmitFrame" => Alter(raw, member, new byte[3]), "_receiveQueue" => Alter(raw, member, new[] { new byte[2] }),
                        "_receiveEligibleAt" => Alter(raw, member, 1_000_000UL), "_unknown" => Alter(raw, member, 1),
                        _ => Alter(raw, member, (ushort)0x301)
                    };
                    RejectState(() => Ne2000.ValidateSnapshot(bad, time, "nic", 0x300)); Validate(r);
                    Check(JsonSerializer.Serialize(raw, StateValues.Json) == JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json), "validation did not mutate actual device");
                });
            }
            foreach (string kind in new[] { "owner", "operation", "deadline", "cancelled", "payload", "duplicate" })
            {
                string mutation = kind;
                Add("pre-media event association rejects " + kind, () =>
                {
                    Rig r = new(pci); r.Start(); r.Send(Frame()); var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [r.Nic] = "nic" };
                    if (r.Pci is not null) owners[r.Pci] = "pci";
                    StateBlock raw = r.Nic.SaveState(); SchedulerState time = r.Clock.SaveState(owners); ScheduledStateEvent first = time.Events.Single();
                    SchedulerState bad = mutation switch
                    {
                        "owner" => time with { Events = [first with { Owner = "unrelated" }] },
                        "operation" => time with { Events = [first with { Operation = "receive" }] },
                        "deadline" => time with { Events = [first with { Time = first.Time + 1 }] },
                        "cancelled" => time with { Cancelled = [first.Id] },
                        "payload" => time with { Events = [first with { Payload = [JsonSerializer.SerializeToElement(1)] }] },
                        _ => time with { Sequence = time.Sequence + 1, Events = [first, first with { Id = time.Sequence + 1 }] }
                    };
                    RejectState(() => Ne2000.ValidateSnapshot(raw, bad, "nic", 0x300)); Validate(r);
                });
            }
        }
        test("Network NE2000: actual CPU byte-IN identification returns original logical MAC",()=>
        {
            byte[] identity=[2,0x44,0x4f,0x53,0x19,2];Rig r=new(false,identity);CrynwrProm(r,true);
            var memory=new MemoryBus(2*1024*1024);var cpu=new X86Cpu(memory,r.Ports);
            // A small independent real-mode IN AL/STOSB/LOOP probe uses exactly
            // the board access width required by the original driver's init.
            byte[] code=[0xb9,0x10,0x00,0xba,0x10,0x03,0xbf,0x00,0x40,0xec,0xaa,0xe2,0xfc,0xf4];code.CopyTo(memory.Ram,0x1000);
            memory.Ram[0x3fff]=0xa5;memory.Ram[0x4010]=0x5a;cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Es,0);cpu.Eip=0x1000;
            cpu.SetRegister(Register.Ax,0xa1b2c3d4);int steps=0;while(!cpu.Halted&&steps++<128)cpu.Step();
            Check(cpu.Halted&&cpu.GetRegister(Register.Cx)==0&&cpu.GetRegister(Register.Di)==0x4010,"actual 16 byte-IN loop completed");
            Check(memory.Ram.AsSpan(0x4000,16).SequenceEqual(LogicalProm(identity)),"all original logical PROM bytes, including the exact MAC");
            Check(memory.Ram[0x3fff]==0xa5&&memory.Ram[0x4010]==0x5a&&memory.Ram.AsSpan(0x1000,code.Length).SequenceEqual(code),"CPU buffer/code guards");
            Check(cpu.GetRegister(Register.Ax)==0xa1b2c357&&(r.In(7)&0x40)!=0,"IN AL preserves other EAX lanes and completes real DMA");Validate(r);
        });
        AddPciCases(test);
        test("Network constructor rejects invalid immutable identities", () =>
        {
            Reject(() => new Ne2000(new(), 0x301, Mac, _ => { })); Reject(() => new Ne2000(new(), 0xfff0, Mac, _ => { }));
            Reject(() => new Ne2000(new(), 0x300, [1, 2, 3, 4, 5, 6], _ => { })); Reject(() => new Ne2000(new(), 0x300, new byte[6], _ => { }));
            Reject(() => new Ne2000(new(), 0x300, [2, 3], _ => { }));
        });
    }
    /// <summary>Provides a fixed occupied board range without hidden target state or a dynamic decoder.</summary>
    private sealed class Occupied : IIoPortDevice
    {
        /// <summary>Returns one explicit fixture byte value under any supported bus transaction.</summary>
        public uint Read(ushort port, int width) => 0x5a;
        /// <summary>Accepts a fixture write without affecting the NIC under test.</summary>
        public void Write(ushort port, uint value, int width) { }
    }
    /// <summary>Registers actual PCI identity/BAR/IOSE/conflict/power/EEPROM controls independently from ISA behavior.</summary>
    private static void AddPciCases(Action<string, Action> test)
    {
        // Register one named actual PCI wrapper regression.
        void Add(string name, Action action) => test("Network RTL8029 PCI: " + name, action);
        Add("manufacturer header masks and complete BAR probe", () =>
        {
            Rig r = new(true); Rtl8029 p = r.Pci!; Check(p.ReadConfiguration(0, 4) == 0x802910ec && p.ReadConfiguration(8, 4) == 0x02000000, "IDs and class");
            Check(p.ReadConfiguration(6, 2) == 0x0200 && p.ReadConfiguration(0x2c, 4) == 0x802910ec && p.ReadConfiguration(0x3d, 1) == 1, "real header bytes");
            p.WriteConfiguration(0, uint.MaxValue, 4); p.WriteConfiguration(4, uint.MaxValue, 4); p.WriteConfiguration(0x3c, uint.MaxValue, 4);
            Check(p.ReadConfiguration(0, 4) == 0x802910ec && p.ReadConfiguration(4, 4) == 0x02000003 && p.ReadConfiguration(0x3c, 2) == 0x010f, "actual writable masks");
            p.WriteConfiguration(0x10, uint.MaxValue, 4); Check(p.ReadConfiguration(0x10, 4) == 0xffffffe1 && !p.Decodes(0xffe0, 1), "32-byte BAR probe is not a low alias");
            p.WriteConfiguration(0x10, 0x6027, 4); Check(p.ReadConfiguration(0x10, 4) == 0x6021 && p.Decodes(0x6020, 1) && !p.Decodes(0x6000, 1), "actual relocation/alignment");
            p.WriteConfiguration(4, 0, 2); Check(!p.Decodes(0x6020, 1), "actual IOSE gate"); p.ValidateSnapshot();
        });
        foreach (int offset in new[] { -1, 253, 255, 256, int.MaxValue })
        {
            int at = offset;
            Add("invalid complete config range is atomic " + at, () =>
            {
                Rig r = new(true); Rtl8029 p = r.Pci!; string before = JsonSerializer.Serialize(p.SaveState(), StateValues.Json);
                Reject(() => p.WriteConfiguration(at, uint.MaxValue, 4)); Reject(() => p.ReadConfiguration(at, 4));
                Check(before == JsonSerializer.Serialize(p.SaveState(), StateValues.Json), "entire config unchanged");
            });
        }
        Add("fixed-board collision makes entire selected PCI BAR inert", () =>
        {
            Rig r = new(true); Rtl8029 p = r.Pci!; r.Ports.Map(0x6021, 1, new Occupied()); p.WriteConfiguration(0x10, 0x6021, 4);
            Check(!p.Decodes(0x6020, 1) && r.Ports.Read(0x6020, 1) == 255 && r.Ports.Read(0x6021, 1) == 0x5a, "no partial colliding target");
        });
        Add("two live PCI BARs are both inert on conflict and recover after relocation", () =>
        {
            var s = new DeviceScheduler(); var bus = new IoPortBus(); var a = new Rtl8029(new(s, 0x300, Mac, _ => { }), bus);
            var b = new Rtl8029(new(s, 0x320, [2, 4, 3, 2, 1, 9], _ => { }), bus); bus.MapDynamic(a); bus.MapDynamic(b);
            a.WriteConfiguration(0x10, 0x6001, 4); b.WriteConfiguration(0x10, 0x6001, 4); a.WriteConfiguration(4, 1, 2); b.WriteConfiguration(4, 1, 2);
            Check(!a.Decodes(0x6000, 1) && !b.Decodes(0x6000, 1) && bus.Read(0x6000, 1) == 255, "symmetric conflict gate");
            b.WriteConfiguration(0x10, 0x6021, 4); Check(bus.Read(0x6000, 1) == 0x21 && bus.Read(0x6020, 1) == 0x21, "independent adjacent targets");
        });
        Add("original identification pages and locked configuration have real gates", () =>
        {
            Rig r = new(true); Check(r.In(10) == 0x50 && r.In(11) == 0x43, "actual page-zero PC IDs");
            r.Out(0, 0xe1); Check(r.In(14) == 255 && r.In(15) == 255, "AS-only IDs unavailable");
            r.Out(5, 0xc0); Check(r.In(5) == 0, "locked CONFIG2"); r.Out(1, 0xc0); r.Out(5, 0xff); r.Out(6, 0xff);
            Check(r.In(5) == 0xc0 && r.In(6) == 6 && r.In(3) == 4, "original media/power bits, no AS flow/full-duplex bits");
            r.Out(9, 0x48); Check(r.In(1) == 255, "halted register access"); r.Out(9, 0x52); Check(r.In(5) == 0xc0, "HLTCLK wakes same controller");
        });
        Add("EEPROM programming gates prefetch and zero-count completion before reissued DMA", () =>
        {
            Rig r = new(true); r.Start(); r.Store(0x4000, [0x12, 0x34, 0x56]); r.Out(0, 0xe2); r.Out(1, 0x80); r.Out(0, 0x22); r.Out(7, 0x7f);
            r.Out(8, 0); r.Out(9, 0x40); r.Out(10, 3); r.Out(11, 0); r.Out(0, 0x0a);
            Check(r.In(8) == 0 && r.In(9) == 0x40 && (r.In(7) & 0x40) == 0 && r.Nic.SaveState().Fields["_remoteCount"].GetUInt16() == 3,
                "disabled read command cannot prefetch, advance CRDA/RBCR or set RDC");
            string before = JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json); Check(r.In(0x10, 2) == 65535, "disabled data port floats");
            Check(before == JsonSerializer.Serialize(r.Nic.SaveState(), StateValues.Json), "disabled data read has no hidden DMA effects"); Rig clone = Clone(r); Same(r, clone);
            foreach (Rig side in new[] { r, clone })
            {
                side.Out(10, 0); side.Out(0, 0x12); Check((side.In(7) & 0x40) == 0, "disabled zero-count write cannot fabricate RDC");
                side.Out(10, 3); side.Out(0, 0xe2); side.Out(1, 0); side.Out(0, 0x0a);
                Check(side.In(8) == 2 && side.In(0x10, 2) == 0x3412 && side.In(0x10, 2) == 0xff56 && (side.In(7) & 0x40) != 0,
                    "explicit reissued read after EEPROM mode performs exact DMA"); Validate(side);
            }
            Same(r, clone);
        });
        Add("EEPROM programming gates send-packet autoinit and new local transmit", () =>
        {
            Rig r = new(true); r.Start(); byte[] payload = Frame(), wire = Wire(payload, true);
            r.Store(0x4000, payload); r.Store(0x4600, [1, 0x48, (byte)wire.Length, 0, .. wire]); r.Out(14, 0x19);
            r.Out(0, 0xe2); r.Out(1, 0x80); r.Out(0, 0x22); r.Out(7, 0x7f); r.Out(8, 0x55); r.Out(9, 0x55); r.Out(10, 0); r.Out(11, 0x0f); r.Out(0, 0x1a);
            var disabled = r.Nic.SaveState(); Check(r.In(8) == 0x55 && r.In(9) == 0x55 && disabled.Fields["_remoteCount"].GetUInt16() == 0x0f00 &&
                !disabled.Fields["_remoteAutoPacket"].GetBoolean() && disabled.Fields["_remoteReadValidBytes"].GetByte() == 0 && (r.In(7) & 0x40) == 0,
                "disabled send-packet cannot inspect SRAM header or initialize a remote transfer");
            r.Out(4, 0x40); r.Out(5, 60); r.Out(6, 0); r.Out(0, 0x26);
            Check(r.Nic.TransmitEventToken == 0 && (r.In(0) & 4) == 0 && (r.In(7) & 8) == 0, "disabled local DMA cannot start or invent a transmitter failure");
            r.Out(0, 0xe2); r.Out(1, 0); r.Out(0, 0x1a);
            Check(r.In(8) == 6 && r.In(9) == 0x46 && r.In(0x10, 2) == (uint)(Mac[0] | Mac[1] << 8), "reenabled original packet header initializes real data");
            for (int i = 1; i < wire.Length / 2; i++) r.In(0x10, 2);
            Check((r.In(7) & 0x40) != 0 && r.In(3) == 0x48, "actual packet removal completes only after data transfers");
            r.Out(14, 9); r.Out(0, 0x26); r.Clock.AdvanceTo(r.Nic.TransmitDeadline); Check(r.Sent.Count == 1 && r.Sent[0].Frame.SequenceEqual(payload), "new TX works after gate reopens"); Validate(r);
        });
        Add("nominal two-millisecond EEPROM reload has actual delayed reset and continuation", () =>
        {
            Rig r = new(true); r.Start(); Rtl8029 p = r.Pci!; r.Out(0, 0xe2); r.Out(1, 0x40); Check(p.AutoloadEventToken > 0 && p.AutoloadDeadline == 2_000_000, "real autoload pending");
            Rig clone = Clone(r); Same(r, clone);
            foreach (Rig side in new[] { r, clone })
            {
                side.Clock.AdvanceTo(1_999_999); Check((side.In(1) & 0xc0) == 0x40, "autoload not early");
                side.Clock.AdvanceTo(2_000_000); Check(side.In(0) == 0x21 && side.In(7) == 0x80 && side.Pci!.IoSpaceEnabled, "core reset retains PCI resources");
            }
            Same(r, clone);
        });
        foreach (int address in new[] { 0, 1, 2, 3, 4, 59, 63 })
        {
            int word = address;
            Add("original fixed EEPROM serial word " + word, () =>
            {
                Rig r = new(true); r.Start(); ushort expected = word is >= 1 and <= 3 ? (ushort)(Mac[(word - 1) * 2] | Mac[(word - 1) * 2 + 1] << 8) : (ushort)0;
                Check(EepromRead(r, word) == expected, "actual immutable EEPROM data"); Validate(r);
            });
        }
        foreach (int cut in new[] { 0, 1, 8, 9, 10, 17, 24 })
        {
            int boundary = cut;
            Add("original serial EEPROM complete state future bit " + cut, () =>
            {
                Rig r = new(true); r.Start(); r.Out(0, 0xe2); r.Out(1, 0x80);
                List<uint> writes = []; int command = 0x181;
                for (int bit = 8; bit >= 0; bit--) { uint pins = (uint)(0x88 | ((command >> bit) & 1) << 1); writes.Add(pins); writes.Add(pins | 4); }
                for (int i = 0; i < 16; i++) { writes.Add(0x88); writes.Add(0x8c); }
                for (int i = 0; i < boundary * 2; i++) r.Out(1, writes[i]);
                Rig clone = Clone(r); Same(r, clone);
                for (int i = boundary * 2; i < writes.Count; i++)
                { r.Out(1, writes[i]); clone.Out(1, writes[i]); Check(r.In(1) == clone.In(1), "actual remaining EEPROM bit " + i); Same(r, clone); }
                Validate(r); Validate(clone);
            });
        }
        Add("PCI reset retains actual peer but clears resources and frame work", () =>
        {
            Rig r = new(true); r.Start(); r.Send(Frame()); Rtl8029 p = r.Pci!; p.Reset();
            Check(p.HasBindings(r.Nic, r.Ports) && p.IoBase == 0 && !p.IoSpaceEnabled && r.Nic.TransmitEventToken == 0, "same peer and actual reset config");
            r.Clock.AdvanceTo(1_000_000); Check(r.Sent.Count == 0, "no cancelled future frame"); p.ValidateSnapshot(); r.Nic.ValidateSnapshot();
        });
    }
}
