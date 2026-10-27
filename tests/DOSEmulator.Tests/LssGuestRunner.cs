// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Real PIT/DOS LSS boundary execution and a file-backed continuation before the pending interrupt is accepted.</summary>
internal static class LssGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, int ProofOffset, bool ExpectOldFailure = false);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { IgnoreReadOnlyProperties = false, WriteIndented = false };
    /// <summary>Requires an actual architectural or original/restored invariant.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    /// <summary>Encodes every implemented component and scheduler field through the explicit state API.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes one owned raw payload without numeric conversion.</summary>
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));
    /// <summary>Requires the real shell prompt after the own program's end marker.</summary>
    private static bool Returned(PcMachine? m) => m is not null && m.Board.DebugText.LastIndexOf("A:\\>", StringComparison.Ordinal) >
        m.Board.DebugText.LastIndexOf("LSS-DONE", StringComparison.Ordinal) && m.Board.DebugText.Contains("LSS-DONE", StringComparison.Ordinal);

    /// <summary>Runs an own real/V86 DOS caller without host inputs, synthetic interrupts or guest-state injection.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), Json)!;
        string root = Path.GetFullPath(input.Output); Directory.CreateDirectory(root);
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        var timer = Stopwatch.StartNew(); var events = new List<object>(); var ports = new List<object>();
        ushort guestCs = 0; bool lastLss = false; uint proofAddress = 0; long steps = 0;
        // Both machines use the same instruction-observer policy, including the protected restart fallback.
        void Observe(PcMachine machine, uint physical, byte opcode, bool record)
        {
            if (!record) return;
            byte first = machine.Memory.ReadByte(physical);
            bool lss = opcode == 0x0f && machine.Cpu.GetSegment(Segment.Cs) == guestCs && guestCs != 0 &&
                machine.Memory.ReadByte(physical + (first == 0x66 ? 2u : 1u)) == 0xb2;
            lastLss |= lss;
            if (lss) events.Add(new { kind = "lss", time = machine.Scheduler.Now, physical, ip = machine.Cpu.Eip, cs = guestCs });
        }
        original.Cpu.InstructionStarting += (p, op) => Observe(original, p, op, true);
        original.Ports.Accessed += (port, value, width, write) =>
        {
            if (port is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x40 or 0x43 or 0xe9)
                ports.Add(new { time = original.Scheduler.Now, port, value, width, write });
        };
        original.Pic.InterruptAcknowledged += (line, vector, spurious) =>
        {
            if (line == 0 && guestCs != 0) events.Add(new { kind = "irq", time = original.Scheduler.Now, line, vector, spurious,
                ip = original.Cpu.Eip, cs = original.Cpu.GetSegment(Segment.Cs), ss = original.Cpu.GetSegment(Segment.Ss),
                esp = original.Cpu.GetRegister(Register.Sp), canAccept = original.Cpu.CanAcceptMaskableInterrupt });
        };
        PcMachine? resumed = null; SnapshotManifest? manifest = null;
        try
        {
            while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && timer.Elapsed.TotalSeconds < 90 && original.Fault is null)
                steps += original.RunSteps(10000);
            Check(original.Fault is null && timer.Elapsed.TotalSeconds < 90, "DOS boot failed.");
            byte[] keys = input.GermanKeyboard ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rLSSBOUND.COM\r")).ToArray()
                : GuestKeyboard.Encode("A:\rLSSBOUND.COM\r");
            byte[] release = GuestKeyboard.Encode("k"); int sent = 0, released = 0, comparisons = 0;
            ulong lastCompare = 0; byte[]? capture = null;
            while (!(Returned(original) && Returned(resumed)) && timer.Elapsed.TotalSeconds < 180)
            {
                Check(original.Fault is null && resumed?.Fault is null, "Guest CPU fault.");
                if (original.Board.DebugText.Contains("LSS-FAILED", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), original.Memory.Ram);
                    File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(original));
                    throw new InvalidDataException("Own LSS guest rejected the actual boundary.");
                }
                if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
                if (guestCs == 0)
                {
                    var ready = Regex.Match(original.Board.DebugText, "LSS-READY=([0-9A-F]{4})\\r\\n");
                    if (ready.Success)
                    {
                        guestCs = Convert.ToUInt16(ready.Groups[1].Value, 16);
                        proofAddress = ((uint)guestCs << 4) + 256 + (uint)input.ProofOffset;
                    }
                }
                if (guestCs != 0 && released < release.Length)
                {
                    bool a = GuestKeyboard.TryInjectForBios(original, release[released]);
                    bool b = resumed is null ? a : GuestKeyboard.TryInjectForBios(resumed, release[released]);
                    Check(a == b, "Restored KBC acceptance differs."); if (a) released++;
                }
                lastLss = false;
                ulong deadline = original.Scheduler.Now + 1000000;
                int count = original.RunSteps(guestCs != 0 && resumed is null ? 1 : 10000, deadline); steps += count;
                if (resumed is not null)
                {
                    Check(resumed.RunSteps(10000, deadline) == count, "Future instruction count differs.");
                    if (original.Scheduler.Now >= lastCompare + 10000000)
                    {
                        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Future full state/RAM differs.");
                        comparisons++; lastCompare = original.Scheduler.Now;
                    }
                }
                else if (lastLss)
                {
                    Check(original.Pic.HasPending, "Actual pending PIT IRQ missing at LSS boundary.");
                    if (input.ExpectOldFailure)
                    {
                        Check(!original.Cpu.CanAcceptMaskableInterrupt, "Archived old CPU did not reproduce the extra shadow.");
                        File.WriteAllBytes(Path.Combine(root, "old-boundary-state.json"), State(original));
                        File.WriteAllText(Path.Combine(root, "old-boundary.json"), JsonSerializer.Serialize(new { reproduced = true, proofAddress, time = original.Scheduler.Now }, Json));
                        return 0;
                    }
                    Check(original.Cpu.CanAcceptMaskableInterrupt, "LSS incorrectly inhibits the pending PIT IRQ.");
                    manifest = SnapshotStore.Capture(original, Path.Combine(root, "pending.dossav")); capture = State(original);
                    resumed = SnapshotStore.Load(Path.Combine(root, "pending.dossav"), Path.Combine(root, "resumed.dosebranch"));
                    resumed.Cpu.InstructionStarting += (p, op) => Observe(resumed, p, op, false);
                    Check(capture.SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Initial restored state differs.");
                    lastCompare = original.Scheduler.Now;
                }
            }
            Check(Returned(original) && Returned(resumed), "Real DOS prompt after TSR is missing.");
            original.Audio.Flush(); resumed!.Audio.Flush();
            byte[] state = State(original);
            Check(state.SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final original/restored state differs.");
            var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
            var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
            Check(JsonSerializer.Serialize(mediaA.Media, Json) == JsonSerializer.Serialize(mediaB.Media, Json), "Final media generations differ.");
            File.WriteAllBytes(Path.Combine(root, "final-state.json"), state); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
            File.WriteAllText(Path.Combine(root, "events.json"), JsonSerializer.Serialize(events, Json));
            File.WriteAllText(Path.Combine(root, "ports.json"), JsonSerializer.Serialize(ports, Json));
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, manifest, proofAddress, steps, comparisons,
                capturedStateSha256 = Hash(capture!), finalStateSha256 = Hash(state), finalRamSha256 = Hash(original.Memory.Ram),
                text = original.Video.ReadText(), debug = original.Board.DebugText, original.Cpu.ProtectedInstructions, original.Cpu.Virtual8086Instructions,
                hostSeconds = timer.Elapsed.TotalSeconds }, new JsonSerializerOptions(Json) { WriteIndented = true }));
            Console.WriteLine("PASS actual LSS/PIT/DOS snapshot continuation " + root); return 0;
        }
        finally { resumed?.Dispose(); }
    }
}
