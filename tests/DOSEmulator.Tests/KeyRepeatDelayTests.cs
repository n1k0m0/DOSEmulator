// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;
using SharpPixels;

/// <summary>Independent first-delay/follow-rate timelines and actual POST/KBC/PIC/BIOS delivery without native windows.</summary>
internal static class KeyRepeatDelayTests
{
    private const uint Initial = 500, Interval = 50;
    /// <summary>Requires the independently specified timing decision or actual BIOS/PIC effect.</summary>
    private static void Check(bool value, string message = "Key first-delay invariant failed") { if (!value) throw new Exception(message); }
    /// <summary>Checks a literal monotonic sample sequence while completing each admitted callback at that same sample.</summary>
    private static void Timeline((long Time, bool Due)[] samples)
    {
        var clock = new KeyRepeatClock(); clock.Restart(0); long previous = -1;
        foreach (var sample in samples)
        {
            Check(sample.Time >= previous, "Invalid independent test timeline ordering."); previous = sample.Time;
            bool due = clock.TryTake(sample.Time, Initial, Interval, true);
            Check(due == sample.Due, "Unexpected admission at monotonic millisecond " + sample.Time);
            if (due) clock.CompleteRepeat(sample.Time);
        }
    }
    /// <summary>Actual own POST, hardware keyboard interrupts, unmodified ROM handlers and independent raw BDA observations.</summary>
    private sealed class BiosRig : IDisposable
    {
        private readonly MachineSession _session;
        private readonly ulong _anchor;
        private long _lastHost;
        private int _softwareCalls;
        private readonly List<byte> _readBytes = [];
        /// <summary>Actual physical nonspurious PIC IRQ1 acknowledgements.</summary>
        public int Acknowledgements { get; private set; }
        /// <summary>Actual instruction fetches at the unchanged POST-installed keyboard handler.</summary>
        public int HandlerEntries { get; private set; }
        /// <summary>Real PIC EOIs written by the BIOS handler; IRQ0 is masked in this bounded own fixture.</summary>
        public int Eois { get; private set; }
        /// <summary>Own currently owned machine, with the product DOSEBios actually executed through POST.</summary>
        private PcMachine Machine => _session.Machine;
        /// <summary>Boots only the built-in own memory disk and retains the firmware-installed IVT/BDA/device state.</summary>
        public BiosRig()
        {
            var machine = PcMachine.CreateDemo(new MachineProfile { RamMiB = 4 });
            try
            {
                machine.RunSteps(100000);
                Check(machine.Fault is null && machine.Cpu.Halted && machine.Memory.ReadWord(0x500) == 0xbeef, "Own real BIOS POST/demo boot failed.");
                ushort keyboardIp = machine.Memory.ReadWord(0x24), keyboardCs = machine.Memory.ReadWord(0x26);
                ushort serviceIp = machine.Memory.ReadWord(0x58), serviceCs = machine.Memory.ReadWord(0x5a);
                Check(keyboardCs == 0xf000 && serviceCs == 0xf000 && keyboardIp != serviceIp && keyboardIp != 0 && serviceIp != 0,
                    "Actual ROM keyboard vectors were not installed by POST.");
                // Reset only this fixture's CPU to clear HLT; do not rerun or replace firmware, IVT, PIC, KBC or BDA.
                machine.Cpu.Reset(); SetCaller(machine, 0x1000, true);
                new byte[] { 0xfb, 0xf4, 0xeb, 0xfd }.CopyTo(machine.Memory.Ram, 0x1000);
                machine.Ports.Write(0x21, 0xfd, 1); machine.Ports.Write(0xa1, 0xff, 1);
                machine.RunSteps(2); Check(machine.Cpu.Halted && machine.Cpu.CanAcceptMaskableInterrupt);
                Check(machine.Memory.ReadWord(0x41a) == machine.Memory.ReadWord(0x41c) && machine.Keyboard.OutputByteCount == 0,
                    "POST keyboard queues were not initially empty.");
                _session = new(machine); _anchor = machine.Scheduler.Now;
                uint keyboardTarget = ((uint)keyboardCs << 4) + keyboardIp, serviceTarget = ((uint)serviceCs << 4) + serviceIp;
                machine.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
                { if (irq == 1) { Check(vector == 9 && !spurious, "IRQ1 did not receive its physical PIC vector."); Acknowledgements++; } };
                machine.Cpu.InstructionStarting += (physical, opcode) =>
                { if (physical == keyboardTarget) HandlerEntries++; if (physical == serviceTarget) _softwareCalls++; };
                machine.Ports.Accessed += (port, value, width, write) =>
                {
                    if (port == 0x60 && width == 1 && !write) _readBytes.Add((byte)value);
                    if (port == 0x20 && width == 1 && write && value == 0x20) Eois++;
                };
            }
            catch { machine.Dispose(); throw; }
        }
        /// <summary>Installs only an own RAM caller while preserving actual firmware vectors and device state.</summary>
        private static void SetCaller(PcMachine machine, uint ip, bool interrupts)
        {
            foreach (Segment segment in Enum.GetValues<Segment>()) machine.Cpu.SetSegment(segment, 0);
            machine.Cpu.SetRegister(Register.Sp, 0x9000); machine.Cpu.Eip = ip; machine.Cpu.Eflags = interrupts ? 0x202u : 2u; machine.Running = true;
        }
        /// <summary>Advances actual virtual devices to the independent host sample, rejecting accidental backwards/duplicate delivery timelines.</summary>
        public void At(long hostMilliseconds)
        {
            Check(hostMilliseconds >= _lastHost, "Host test timeline moved backwards."); _lastHost = hostMilliseconds;
            ulong target = checked(_anchor + (ulong)hostMilliseconds * 1_000_000UL);
            Check(Machine.Scheduler.Now <= target, "Previous guest delivery overran the next explicit host test sample.");
            Machine.RunSteps(20000, target);
            Check(Machine.Fault is null && Machine.Scheduler.Now >= target && Machine.Cpu.Halted, "Own idle guest failed to reach its device deadline.");
        }
        /// <summary>Uses real session admission and permits only actual KBC IRQ1/ROM handler processing.</summary>
        public void Send(byte scanCode)
        {
            int acks = Acknowledgements, entries = HandlerEntries, eois = Eois, reads = _readBytes.Count;
            var task = _session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCode })); _session.PumpCommands();
            Check(task.IsCompletedSuccessfully && task.Result.Success, "Real KBC did not admit the own key event.");
            Machine.RunSteps(2000, Machine.Scheduler.Now + 100_000);
            Check(Machine.Fault is null && Machine.Cpu.Halted && Machine.Keyboard.OutputByteCount == 0 &&
                Acknowledgements == acks + 1 && HandlerEntries == entries + 1 && Eois == eois + 1 &&
                _readBytes.Count == reads + 1 && _readBytes[^1] == scanCode,
                "One admitted key did not produce exactly one actual PIC/BIOS read/EOI/return.");
        }
        /// <summary>Reads all queued BIOS words directly from the actual BDA ring without consuming them.</summary>
        private ushort[] Queued()
        {
            ushort head = Machine.Memory.ReadWord(0x41a), tail = Machine.Memory.ReadWord(0x41c);
            Check(head is >= 0x1e and < 0x3e && tail is >= 0x1e and < 0x3e && ((head | tail) & 1) == 0);
            var words = new List<ushort>();
            for (int count = 0; head != tail && count < 16; count++)
            { words.Add(Machine.Memory.ReadWord((uint)head + 0x400)); head = (ushort)(head == 0x3c ? 0x1e : head + 2); }
            Check(head == tail, "Invalid actual BIOS typeahead ring."); return words.ToArray();
        }
        /// <summary>Requires exact make/break effects, then consumes the same words through actual guest INT16 opcodes and reaches an own HLT.</summary>
        public void Finish(int expectedMakes, byte[] expectedReads)
        {
            Check(_readBytes.SequenceEqual(expectedReads) && Acknowledgements == expectedReads.Length &&
                HandlerEntries == expectedReads.Length && Eois == expectedReads.Length, "Actual short/held key transfer count differs.");
            ushort[] queue = Queued(); Check(queue.Length == expectedMakes && queue.All(word => word == 0x1e61), "A short press duplicated its BIOS character or lost its real translation.");
            Machine.Cpu.Reset(); SetCaller(Machine, 0x2000, false);
            // MOV CX,count; MOV DI,6000; MOV AH,0; INT16; STOSW; LOOP to MOV AH; CLI; HLT.
            byte[] caller = [0xb9, (byte)expectedMakes, 0, 0xbf, 0, 0x60, 0xb4, 0, 0xcd, 0x16, 0xab, 0xe2, 0xf9, 0xfa, 0xf4];
            caller.CopyTo(Machine.Memory.Ram, 0x2000); Machine.RunSteps(10000);
            Check(Machine.Fault is null && Machine.Cpu.Halted && Machine.Cpu.Eip == 0x200f && _softwareCalls == expectedMakes,
                "Actual BIOS INT16/guest return did not complete.");
            for (int i = 0; i < expectedMakes; i++) Check(Machine.Memory.ReadWord(0x6000u + (uint)i * 2) == 0x1e61);
            Check(Queued().Length == 0 && _readBytes.SequenceEqual(expectedReads) && Acknowledgements == expectedReads.Length && Eois == expectedReads.Length,
                "INT16 manufactured another IRQ/key or failed to drain the real BIOS queue.");
        }
        /// <summary>Closes only the own built-in fixture; there are no host media or native endpoints.</summary>
        public void Dispose() => _session.Dispose();
    }
    /// <summary>Registers literal timing expectations and separate real IRQ/BIOS consequences.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int release in new[] { 25, 50, 90, 120, 249, 499 })
            test("Key initial delay tap " + release + "ms emits one make and one break", () =>
            {
                var clock = new KeyRepeatClock(); clock.Restart(0); int makes = 1;
                for (long now = 1; now < release; now++) if (clock.TryTake(now, Initial, Interval, true)) { makes++; clock.CompleteRepeat(now); }
                // Key-up occurs before polling at this exact release timestamp.
                clock.Reset(); Check(!clock.TryTake(release, Initial, Interval, false) && makes == 1);
                Check(!clock.TryTake(release + 1000, Initial, Interval, false));
            });
        test("Key initial delay literal 499/500 and 549/550 boundaries", () => Timeline(
            [(0, false), (49, false), (50, false), (120, false), (249, false), (499, false), (500, true),
             (500, false), (501, false), (549, false), (550, true), (599, false), (600, true), (600, false)]));
        test("Key initial delay exact 500ms release before poll suppresses that timestamp's repeat", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(!clock.TryTake(499, Initial, Interval, true));
            clock.Reset(); Check(!clock.TryTake(500, Initial, Interval, false));
        });
        test("Key initial delay polling 500 before a later release admits only one repeat", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(500, Initial, Interval, true)); clock.CompleteRepeat(500);
            Check(!clock.TryTake(500, Initial, Interval, true)); clock.Reset(); Check(!clock.TryTake(501, Initial, Interval, false));
        });
        foreach (long stall in new long[] { 1000, 60000, 3600000 })
            test("Key initial delay stalled initial phase drops old deadlines " + stall, () =>
            {
                var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(stall, Initial, Interval, true)); clock.CompleteRepeat(stall);
                foreach (long now in new[] { stall, stall + 1, stall + 16, stall + 32, stall + 49 }) Check(!clock.TryTake(now, Initial, Interval, true));
                Check(clock.TryTake(stall + 50, Initial, Interval, true));
            });
        test("Key initial delay callback completion preserves follow phase after a modal stall", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(500, Initial, Interval, true));
            clock.CompleteRepeat(10500);
            Check(!clock.TryTake(10500, Initial, Interval, true) && !clock.TryTake(10549, Initial, Interval, true) && clock.TryTake(10550, Initial, Interval, true));
        });
        test("Key initial delay reset during callback remains disarmed after completion", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(500, Initial, Interval, true));
            clock.Reset(); clock.CompleteRepeat(5000);
            Check(!clock.TryTake(5500, Initial, Interval, true) && !clock.TryTake(5999, Initial, Interval, true) && clock.TryTake(6000, Initial, Interval, true));
        });
        test("Key initial delay callback completion cannot arm an idle clock", () =>
        {
            var clock = new KeyRepeatClock(); clock.CompleteRepeat(1000);
            Check(!clock.TryTake(1500, Initial, Interval, true) && !clock.TryTake(1999, Initial, Interval, true) && clock.TryTake(2000, Initial, Interval, true));
        });
        test("Key initial delay a new key-down starts the initial phase again", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(500, Initial, Interval, true)); clock.CompleteRepeat(500);
            clock.Restart(520); Check(!clock.TryTake(550, Initial, Interval, true) && !clock.TryTake(1019, Initial, Interval, true) && clock.TryTake(1020, Initial, Interval, true));
        });
        test("Key initial delay disable and focus re-enable have no inherited held debt", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(!clock.TryTake(5000, Initial, Interval, false));
            clock.CompleteRepeat(5000); Check(!clock.TryTake(10000, Initial, Interval, true));
            Check(!clock.TryTake(10499, Initial, Interval, true) && clock.TryTake(10500, Initial, Interval, true));
        });
        test("Key initial delay zero repeat interval remains disabled", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0);
            Check(!clock.TryTake(500, Initial, 0, true) && !clock.TryTake(5000, Initial, 0, true));
            Check(!clock.TryTake(6000, Initial, Interval, true) && clock.TryTake(6500, Initial, Interval, true));
        });
        test("Key initial delay explicitly zero initial delay still has bounded follow spacing", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(0, 0, Interval, true)); clock.CompleteRepeat(0);
            Check(!clock.TryTake(0, 0, Interval, true) && !clock.TryTake(49, 0, Interval, true) && clock.TryTake(50, 0, Interval, true));
        });
        test("Key initial delay timestamp near Int64 maximum does not add an overflowing deadline", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(long.MaxValue - 550);
            Check(!clock.TryTake(long.MaxValue - 51, Initial, Interval, true) && clock.TryTake(long.MaxValue - 50, Initial, Interval, true));
            clock.CompleteRepeat(long.MaxValue - 50);
            Check(!clock.TryTake(long.MaxValue - 1, Initial, Interval, true) && clock.TryTake(long.MaxValue, Initial, Interval, true));
            clock.CompleteRepeat(long.MaxValue); Check(!clock.TryTake(long.MaxValue, Initial, Interval, true));
        });
        test("Key initial delay uint initial delay and interval retain their full ranges", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0);
            Check(!clock.TryTake((long)uint.MaxValue - 1, uint.MaxValue, uint.MaxValue, true));
            Check(clock.TryTake(uint.MaxValue, uint.MaxValue, uint.MaxValue, true)); clock.CompleteRepeat(uint.MaxValue);
            Check(!clock.TryTake(8589934589L, uint.MaxValue, uint.MaxValue, true) && clock.TryTake(8589934590L, uint.MaxValue, uint.MaxValue, true));
        });
        test("Key initial delay negative samples reject without consuming the pending first delay", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); bool rejected = false;
            try { clock.TryTake(-1, Initial, Interval, true); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && clock.TryTake(500, Initial, Interval, true));
        });
        test("Key initial delay shared multi-key policy restarts the first delay for every held key", () =>
        {
            var clock = new KeyRepeatClock(); int aMakes = 1, bMakes = 0; clock.Restart(0);
            Check(!clock.TryTake(299, Initial, Interval, true)); bMakes++; clock.Restart(300);
            foreach (long time in new long[] { 499, 500, 550, 799 }) Check(!clock.TryTake(time, Initial, Interval, true));
            Check(clock.TryTake(800, Initial, Interval, true)); aMakes++; bMakes++; clock.CompleteRepeat(800);
            Check(!clock.TryTake(849, Initial, Interval, true) && clock.TryTake(850, Initial, Interval, true)); aMakes++; bMakes++;
            Check(aMakes == 3 && bMakes == 3, "This is a shared window clock, not an advertised per-key typematic model.");
        });
        foreach (int release in new[] { 90, 499, 500 })
            test("Key initial delay actual POST KBC IRQ1 BIOS short press release-before-poll " + release, () =>
            {
                using var bios = new BiosRig(); var clock = new KeyRepeatClock(); clock.Restart(0); bios.Send(0x1e);
                for (long now = 1; now < release; now++)
                {
                    bios.At(now);
                    if (clock.TryTake(now, Initial, Interval, true)) { bios.Send(0x1e); clock.CompleteRepeat(now); }
                }
                bios.At(release); clock.Reset(); Check(!clock.TryTake(release, Initial, Interval, false)); bios.Send(0x9e);
                bios.Finish(1, [0x1e, 0x9e]);
            });
        test("Key initial delay actual POST KBC IRQ1 BIOS hold repeats only at 500 550 600 before release601", () =>
        {
            using var bios = new BiosRig(); var clock = new KeyRepeatClock(); clock.Restart(0); bios.Send(0x1e); var admitted = new List<long>();
            for (long now = 1; now <= 600; now++)
            {
                bios.At(now);
                if (clock.TryTake(now, Initial, Interval, true)) { admitted.Add(now); bios.Send(0x1e); clock.CompleteRepeat(now); }
            }
            Check(admitted.SequenceEqual(new long[] { 500, 550, 600 }));
            bios.At(601); clock.Reset(); Check(!clock.TryTake(601, Initial, Interval, false)); bios.Send(0x9e);
            bios.Finish(4, [0x1e, 0x1e, 0x1e, 0x1e, 0x9e]);
        });
    }
}
