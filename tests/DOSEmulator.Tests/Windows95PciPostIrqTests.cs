// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual cold Windows-board POST allocation and unchanged reset, warm-entry and older-ROM contracts.</summary>
internal static class Windows95PciPostIrqTests
{
    /// <summary>Requires a concrete CPU, port, resource or whole-container result.</summary>
    private static void Check(bool condition, string why = "Windows95 PCI POST IRQ invariant failed.")
    { if (!condition) throw new InvalidOperationException(why); }

    /// <summary>Creates the exact fixed own 100/133 MHz tuple or its unchanged earlier PCI-BIOS control.</summary>
    private static MachineProfile Profile(bool fast, bool prior = false) => new()
    {
        Id = prior ? MachineProfile.PciBiosProfileId : fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
        Name = "Own PCI POST IRQ fixture", Cpu = MachineProfile.P54cCpuId,
        CpuHz = fast && !prior ? 133_000_000 : 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2
    };

    /// <summary>Uses a fresh immutable own disk with a genuine CLI/HLT boot sector, without any operating-system bytes.</summary>
    private static MemoryDisk Disk()
    {
        byte[] bytes = new byte[128 * 512];
        new byte[] { 0xfa, 0xf4, 0xeb, 0xfd }.CopyTo(bytes, 0);
        bytes[510] = 0x55; bytes[511] = 0xaa;
        return new MemoryDisk(bytes, "Own PCI POST IRQ boot sector");
    }

    /// <summary>Own new source PC and checked private directory for complete snapshots and isolated branches.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Fresh output root; no original, personal or pre-existing medium is opened.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "artifacts/tests/windows95-pci-post-" + Guid.NewGuid().ToString("N")));
        /// <summary>The real fixed CPU, firmware, chipset and video graph whose instructions are executed.</summary>
        public readonly PcMachine Machine;
        /// <summary>Constructs only this new own disk and the selected cold firmware.</summary>
        public Fixture(bool fast, bool prior = false)
        {
            Directory.CreateDirectory(Root);
            Machine = PcMachine.CreateWithDisks(Profile(fast, prior), [Disk(), null, null, null]);
        }
        /// <summary>Returns only a destination inside the checked own output root.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Releases all leases before deleting only this verified new private output directory.</summary>
        public void Dispose()
        {
            Machine.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("windows95-pci-post-", StringComparison.Ordinal));
            Directory.Delete(Root, true);
        }
    }

    /// <summary>One actual completed transaction, including the real CF8 selector and executing ROM location.</summary>
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, uint Selector, uint Instruction, ulong Time);

    /// <summary>Passive bounded recording of actual firmware accesses, without reading data endpoints in callbacks.</summary>
    private sealed class Trace : IDisposable
    {
        private readonly PcMachine _machine;
        private uint _selector;
        /// <summary>Observed completed configuration, interrupt-route and PIC transactions.</summary>
        public readonly List<Port> Rows = [];
        /// <summary>Starts at an owner boundary using the actual saved configuration-address latch.</summary>
        public Trace(PcMachine machine)
        {
            _machine = machine; _selector = machine.Ports.Read(0xcf8, 4);
            machine.Ports.Accessed += Access;
        }
        /// <summary>Records only completed relevant bus transactions and their pre-advance virtual time.</summary>
        private void Access(ushort address, uint value, int width, bool write)
        {
            if (write && address == 0xcf8 && width == 4) _selector = value;
            if (!(address is >= 0xcf8 and <= 0xcff || address is 0x4d0 or 0x4d1 or 0x20 or 0x21 or 0xa0 or 0xa1)) return;
            Check(Rows.Count < 4096, "The bounded POST port ledger overflowed.");
            Rows.Add(new(address, value, width, write, _selector, _machine.Cpu.LinearInstructionAddress, _machine.Scheduler.Now));
        }
        /// <summary>Detaches the passive observer without changing the machine or replaying any access.</summary>
        public void Dispose() => _machine.Ports.Accessed -= Access;
    }

    /// <summary>Identifies the actual byte write to the original PIIX PIRQA routing register.</summary>
    private static bool IsRoute(Port row) => row.Write && row.Address == 0xcfc && row.Selector == 0x80003860;
    /// <summary>Identifies the actual byte write to S3 InterruptLine, separate from its readonly INTA pin.</summary>
    private static bool IsLine(Port row) => row.Write && row.Address == 0xcfc && row.Selector == 0x8000183c;
    /// <summary>Identifies the actual high ELCR byte write performed by the new cold POST block.</summary>
    private static bool IsLevel(Port row) => row.Write && row.Address == 0x4d1;

    /// <summary>Runs actual reset/POST instructions until a passive boundary condition or a bounded failure.</summary>
    private static void RunUntil(PcMachine machine, Func<bool> done)
    {
        for (int step = 0; step < 250000; step++)
        {
            if (done()) return;
            Check(machine.RunSteps(1) == 1 && machine.Fault is null && !machine.Cpu.TripleFaulted,
                machine.Fault ?? "The real POST stopped before the requested boundary: " + machine.Cpu.Position);
        }
        throw new InvalidOperationException("The real POST did not reach the bounded observation.");
    }

    /// <summary>Requires the actual own disk boot rather than a BIOS error HLT or a stopped reset image.</summary>
    private static void Boot(PcMachine machine)
    {
        machine.RunSteps(250000);
        Check(machine.Fault is null && !machine.Cpu.TripleFaulted && machine.Cpu.Halted &&
            machine.Cpu.GetSegment(Segment.Cs) == 0 && machine.Cpu.Eip == 0x7c02,
            machine.Fault ?? "Actual POST did not boot the own CLI/HLT sector: " + machine.Cpu.Position);
    }

    /// <summary>Seeds unrelated legal bytes to verify byte-lane preservation and ELCR read/modify/write.</summary>
    private static void Seed(PcMachine machine)
    {
        machine.PiixIrq!.WriteConfiguration(0x61, 0x8a, 1);
        machine.PiixIrq.WriteConfiguration(0x62, 0x8c, 1);
        machine.PiixIrq.WriteConfiguration(0x63, 0x89, 1);
        machine.Ports.Write(0x4d0, 0x20, 1); machine.Ports.Write(0x4d1, 0x50, 1);
    }

    /// <summary>Requires the actual dedicated IRQ11 route while retaining unrelated configuration and masked PIC inputs.</summary>
    private static void Assigned(PcMachine machine, bool seeded)
    {
        Check(machine.S3!.ReadConfiguration(0x3c, 2) == 0x010b, "S3 Line or readonly INTA pin differs.");
        Check(machine.PiixIrq!.ReadConfiguration(0x60, 4) == (seeded ? 0x898c8a0bu : 0x8080800bu),
            "The actual PIRQA byte write changed another route.");
        Check(machine.PiixIrq.Elcr == (seeded ? 0x5820 : 0x0800) && machine.Pic.LevelMask == machine.PiixIrq.Elcr,
            "ELCR level selection or preserved unrelated bits differ.");
        Check(machine.PiixIrq.ReadConfiguration(0x70, 2) == 0x800f, "POST changed the existing motherboard IDE route.");
        Check(machine.Ports.Read(0x21, 1) == 0xf8 && machine.Ports.Read(0xa1, 1) == 0xff,
            "POST must leave the cascade open and every slave source masked.");
        Check(machine.PiixIrq.PirqAssertions == 0 && (machine.Pic.InputLines & 0x0800) == 0,
            "Resource allocation manufactured a S3 interrupt source.");
        Check(machine.Ports.Read(0xcf8, 4) == 0, "Cold POST leaked a changed CF8 end value.");
    }

    /// <summary>Reads every explicit device/scheduler field without a port access or import normalization.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares the full actual 32-MiB RAM, CPU, video, devices, scheduler and rebound TSC.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) &&
        a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram) && a.Cpu.TimestampCounter == b.Cpu.TimestampCounter,
        "Full POST continuation differs.");

    /// <summary>Defined actual caller outputs and completed bus work after a real INT1A/IRET.</summary>
    private sealed record BiosResult(ushort Ax, ushort Bx, uint Flags, Port[] Ports);
    /// <summary>Executes the installed actual INT1A handler on a fresh own caller stack, preserving powered device state.</summary>
    private static BiosResult Bios(PcMachine machine, ushort function, ushort bx, ushort cx = 0)
    {
        machine.Cpu.Reset(); machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.SetSegment(Segment.Ss, 0);
        machine.Cpu.SetSegment(Segment.Ds, 0xf000); machine.Cpu.SetSegment(Segment.Es, 0x2000);
        machine.Cpu.SetRegister(Register.Sp, 0x9000); machine.Cpu.SetRegister(Register.Ax, function);
        machine.Cpu.SetRegister(Register.Bx, bx); machine.Cpu.SetRegister(Register.Cx, cx); machine.Cpu.SetRegister(Register.Di, 0);
        machine.Cpu.Eip = 0x1000; machine.Cpu.Eflags = 2;
        new byte[] { 0xcd, 0x1a, 0xfa, 0xf4 }.CopyTo(machine.Memory.Ram, 0x1000);
        using var trace = new Trace(machine); BiosResult? result = null;
        // Observe the real caller instruction only after the actual IRET has completed.
        void Returned(uint address, byte opcode)
        {
            if (address == 0x1002 && opcode == 0xfa)
                result = new((ushort)machine.Cpu.GetRegister(Register.Ax), (ushort)machine.Cpu.GetRegister(Register.Bx), machine.Cpu.Eflags, trace.Rows.ToArray());
        }
        machine.Cpu.InstructionStarting += Returned;
        try { RunUntil(machine, () => result is not null); }
        finally { machine.Cpu.InstructionStarting -= Returned; }
        Check(result is not null && (result.Ax & 0xff00) == 0 && (result.Flags & 1) == 0,
            "The actual PCI BIOS did not successfully return through IRET.");
        return result!;
    }

    /// <summary>Builds the normative 16-bit routing parameter and guards without using the firmware's data table.</summary>
    private static void RoutingBuffer(PcMachine machine)
    {
        machine.Memory.Ram.AsSpan(0x20000, 0x150).Fill(0xcd);
        machine.Memory.WriteWord(0x20000, 48); machine.Memory.WriteWord(0x20002, 0x100);
        machine.Memory.WriteWord(0x20004, 0x2000);
    }
    /// <summary>Requires the independent fixed48-byte table, exact returned length and untouched surrounding bytes.</summary>
    private static void CheckRoutingBuffer(PcMachine machine)
    {
        byte[] expected = new byte[48]; expected[17] = 0x18; expected[18] = 0x60; expected[20] = 8; expected[33] = 0x38;
        Check(machine.Memory.ReadWord(0x20000) == 48 && machine.Memory.Ram.AsSpan(0x20100, 48).SequenceEqual(expected));
        Check(machine.Memory.Ram.AsSpan(0x200f0, 16).IndexOfAnyExcept((byte)0xcd) < 0 &&
            machine.Memory.Ram.AsSpan(0x20130, 32).IndexOfAnyExcept((byte)0xcd) < 0, "Routing output changed its guards.");
    }

    /// <summary>Registers real POST, primary-interface, reset-domain and full-container continuation checks.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 Windows95 PCI POST retains all six older ROM payloads", () =>
        {
            foreach (var image in new (string Field, string Hash)[]
            {
                ("CompressedImage", "353CFA9B957CF6433F8F2B92EFA1BD33F90CDE2FA1C3BC4D63D60AA87A56FC67"),
                ("FxHostCompressedImage", "E974CA88856E5F9CD0481BA27E7517E3D6D094571DDD6C724C9816B0AEBECA84"),
                ("PiixIdeCompressedImage", "E74780B5B2463FC9E2EDBACC39ED342252C0A853195BB2C0A10D18758751C6EA"),
                ("PiixResetCompressedImage", "A708D4AA984487CDF2709550FEA46E2205C422C200C70C34D5D4E9B497831F69"),
                ("Trio64CompressedImage", "991EA2B31107937D81FF5EB83BA347F4132A5BE83F5D8860ECA3332B3D92F688"),
                ("PciBiosCompressedImage", "86DAF7A610CFDB5E33BB0DF46E80FCDD601C385D8FF5D1B559DAF1C134D9E096")
            })
            {
                string encoded = (string)typeof(DOSEBios).GetField(image.Field, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
                using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress); using var bytes = new MemoryStream(); gzip.CopyTo(bytes);
                Check(bytes.Length == 65536 && Convert.ToHexString(SHA256.HashData(bytes.ToArray())) == image.Hash,
                    "An older ROM payload changed: " + image.Field);
            }
        });
        test("M4 Windows95 PCI POST leaves the prior PCI-BIOS profile unassigned", () =>
        {
            using var f = new Fixture(false, true); using var trace = new Trace(f.Machine); Boot(f.Machine);
            Check(f.Machine.S3!.ReadConfiguration(0x3c, 2) == 0x0100 && f.Machine.PiixIrq!.ReadConfiguration(0x60, 4) == 0x80808080 &&
                f.Machine.PiixIrq.Elcr == 0 && !trace.Rows.Any(IsLevel) && !trace.Rows.Any(IsRoute) && !trace.Rows.Any(IsLine));
            RoutingBuffer(f.Machine); Check(Bios(f.Machine, 0xb10e, 0).Bx == 0); CheckRoutingBuffer(f.Machine);
        });
        foreach (bool fast in new[] { false, true })
        {
            test("M4 Windows95 PCI POST retains factory and asserted hard-reset defaults fast=" + fast, () =>
            {
                using var f = new Fixture(fast); var m = f.Machine;
                Check(m.Cpu.Instructions == 0 && m.S3!.ReadConfiguration(0x3c, 2) == 0x0100 &&
                    m.PiixIrq!.ReadConfiguration(0x60, 4) == 0x80808080 && m.PiixIrq.Elcr == 0 && m.Pic.LevelMask == 0);
                m.S3!.WriteConfiguration(0x3c, 11, 1); m.PiixIrq!.WriteConfiguration(0x60, 11, 1); m.Ports.Write(0x4d1, 8, 1);
                m.Ports.Write(0xcf9, 6, 1);
                Check(m.RunSteps(1, m.Scheduler.Now) == 0 && m.CpuResetHeld && m.PiixReset!.Phase == PiixResetPhase.AllAsserted);
                Check(m.S3.ReadConfiguration(0x3c, 2) == 0x0100 && m.PiixIrq.ReadConfiguration(0x60, 4) == 0x80808080 &&
                    m.PiixIrq.Elcr == 0 && m.Pic.LevelMask == 0 && m.Cpu.Instructions == 0);
            });
            test("M4 Windows95 PCI POST actual guest byte writes preserve other resources fast=" + fast, () =>
            {
                using var f = new Fixture(fast); var m = f.Machine; Seed(m);
                using var trace = new Trace(m); Boot(m); Port[] rows = trace.Rows.ToArray(); Assigned(m, true);
                var levels = rows.Where(IsLevel).ToArray(); var routes = rows.Where(IsRoute).ToArray(); var lines = rows.Where(IsLine).ToArray();
                Check(levels.Length == 1 && routes.Length == 1 && lines.Length == 1);
                Check(levels[0].Width == 1 && levels[0].Value == 0x58 && routes[0].Width == 1 && routes[0].Value == 11 &&
                    lines[0].Width == 1 && lines[0].Value == 11 && new[] { levels[0], routes[0], lines[0] }.All(p => p.Instruction is >= 0xf0000 and <= 0xfffff));
                Check(Array.IndexOf(rows, levels[0]) < Array.IndexOf(rows, routes[0]) && Array.IndexOf(rows, routes[0]) < Array.IndexOf(rows, lines[0]));
                Check(rows.Where(p => p.Write && p.Address == 0x21).Select(p => p.Value).SequenceEqual(new uint[] { 8, 4, 1, 0xf8 }) &&
                    rows.Where(p => p.Write && p.Address == 0xa1).Select(p => p.Value).SequenceEqual(new uint[] { 0x70, 2, 1, 0xff }),
                    "The new allocation changed the original PIC initialization or masks.");
                Check(rows.Any(p => !p.Write && p.Address == 0x4d1 && p.Width == 1 && p.Value == 0x50));
                // This is a controlled router pin, not an invented S3-produced interrupt.
                m.PiixIrq!.SetPirq(0, true); Check((m.Pic.InputLines & 0x0800) != 0 && !m.Pic.HasPending);
                m.PiixIrq.SetPirq(0, false); Check((m.Pic.InputLines & 0x0800) == 0);
            });
            test("M4 Windows95 PCI POST B10E and B10F use the actual allocated route fast=" + fast, () =>
            {
                using var f = new Fixture(fast); var m = f.Machine; Boot(m); RoutingBuffer(m);
                m.Ports.Write(0xcf8, 0x8000f8a0, 4);
                Check(Bios(m, 0xb10e, 0).Bx == 0x0800 && m.Ports.Read(0xcf8, 4) == 0x8000f8a0); CheckRoutingBuffer(m);
                m.PiixIrq!.WriteConfiguration(0x60, 0x80, 1); m.Ports.Write(0x4d0, 0x20, 1); m.Ports.Write(0x4d1, 0x40, 1);
                m.S3!.WriteConfiguration(0x3c, 0xff, 1); RoutingBuffer(m); Check(Bios(m, 0xb10e, 0).Bx == 0);
                var result = Bios(m, 0xb10f, 0x18, 0x0b0a);
                Check(m.PiixIrq.ReadConfiguration(0x60, 1) == 11 && m.PiixIrq.Elcr == 0x4820 && m.S3.ReadConfiguration(0x3c, 1) == 255);
                Check(!result.Ports.Any(p => p.Write && (p.Address is 0x20 or 0x21 or 0xa0 or 0xa1 || IsLine(p))) &&
                    m.Ports.Read(0x21, 1) == 0xf8 && m.Ports.Read(0xa1, 1) == 0xff && m.Ports.Read(0xcf8, 4) == 0x8000f8a0);
                RoutingBuffer(m); Check(Bios(m, 0xb10e, 0).Bx == 0x0800); CheckRoutingBuffer(m);
            });
            foreach (string cut in new[] { "ELCR", "PIRQA" })
                test("M4 Windows95 PCI POST full container between actual OUTs fast=" + fast + " cut=" + cut, () =>
                {
                    using var f = new Fixture(fast); var m = f.Machine;
                    using (var trace = new Trace(m)) RunUntil(m, () => trace.Rows.Any(cut == "ELCR" ? IsLevel : IsRoute));
                    Check(m.Cpu.GetSegment(Segment.Cs) == 0xf000 && m.PiixIrq!.Elcr == 0x0800 && m.S3!.ReadConfiguration(0x3c, 1) == 0 &&
                        m.PiixIrq.ReadConfiguration(0x60, 1) == (cut == "ELCR" ? 0x80u : 11u));
                    string saved = f.PathFor("cut.dossav"); SnapshotStore.Capture(m, saved);
                    using var restored = SnapshotStore.Load(saved, f.PathFor("resume.dosebranch")); Same(m, restored);
                    using var a = new Trace(m); using var b = new Trace(restored);
                    foreach (int steps in new[] { 1, 1, 2, 4, 8, 16, 32, 64, 128 })
                    {
                        Check(m.RunSteps(steps) == steps && restored.RunSteps(steps) == steps && m.Fault is null && restored.Fault is null);
                        Same(m, restored); Check(a.Rows.SequenceEqual(b.Rows), "Actual later firmware port publications differ.");
                    }
                    Assigned(m, false); Assigned(restored, false);
                    Check(a.Rows.Any(IsLine) && a.Rows.Any(p => p.Write && p.Address == 0xcf8 && p.Width == 4 && p.Value == 0));
                });
            test("M4 Windows95 PCI POST warm INIT rendezvous preserves caller resource choices fast=" + fast, () =>
            {
                using var f = new Fixture(fast); var m = f.Machine; Boot(m);
                m.S3!.WriteConfiguration(0x3c, 255, 1); m.PiixIrq!.WriteConfiguration(0x60, 0x80, 1); m.Ports.Write(0x4d1, 0x40, 1);
                m.Memory.WriteWord(0x467, 0x7000); m.Memory.WriteWord(0x469, 0); m.Memory.WriteWord(0x472, 0x1234);
                new byte[] { 0xc6, 0x06, 0, 0x71, 0xa6, 0xfa, 0xf4 }.CopyTo(m.Memory.Ram, 0x7000);
                m.Ports.Write(0x70, 0x0f, 1); m.Ports.Write(0x71, 0x0a, 1);
                using var trace = new Trace(m); m.Ports.Write(0xcf9, 4, 1);
                RunUntil(m, () => m.Memory.Ram[0x7100] == 0xa6);
                Check(m.LastResetSource == "cf9-init" && m.Memory.ReadWord(0x472) == 0 &&
                    m.S3.ReadConfiguration(0x3c, 1) == 255 && m.PiixIrq.ReadConfiguration(0x60, 1) == 0x80 && m.PiixIrq.Elcr == 0x4000);
                Check(!trace.Rows.Any(IsLevel) && !trace.Rows.Any(IsRoute) && !trace.Rows.Any(IsLine) &&
                    !trace.Rows.Any(p => p.Write && p.Address is 0x21 or 0xa1), "Warm entry executed cold POST resource allocation.");
            });
        }
    }
}
