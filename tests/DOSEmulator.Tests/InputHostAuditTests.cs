// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using SharpPixels;

/// <summary>Audited input admission, capability and host presentation policies without native windows or input.</summary>
internal static class InputHostAuditTests
{
    /// <summary>Requires independently observable packet bytes, responses, revisions or pacing boundaries.</summary>
    private static void Check(bool value, string message = "Input/host audit assertion failed") { if (!value) throw new Exception(message); }
    /// <summary>Pumps one actual session request on its owner without a pipe or native input source.</summary>
    private static IpcResponse Send(MachineSession session, string command, object? payload = null)
    { var task = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return task.GetAwaiter().GetResult(); }
    /// <summary>Reads all actual queued bytes via the controller data port.</summary>
    private static byte[] Drain(Keyboard8042 keyboard)
    { var result = new List<byte>(); while (keyboard.OutputByteCount != 0) result.Add((byte)keyboard.Read(0x60, 1)); return result.ToArray(); }
    /// <summary>Captures only the explicit controller record to prove rejection has no partial state effects.</summary>
    private static string State(Keyboard8042 keyboard) => JsonSerializer.Serialize(keyboard.SaveState(), StateValues.Json);

    /// <summary>Registers actual queue/guest/response checks and exact production upload-policy decisions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Audit input 65th request is rejected and retried release reaches real CPU ports", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo()); var m = session.Machine;
            var tasks = Enumerable.Range(0, 65).Select(i => session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCode = i == 64 ? 0x9e : 0x1e }))).ToArray();
            session.PumpCommands(); Check(tasks.Take(64).All(t => t.Result.Success));
            Check(!tasks[64].Result.Success && tasks[64].Result.ErrorCode == "InputNotAccepted" && m.Keyboard.OutputByteCount == 64);
            byte[] code = [0xbf, 0, 0x60, 0xb9, 0x40, 0, 0xe4, 0x60, 0xaa, 0xe2, 0xfb, 0xf4];
            code.CopyTo(m.Memory.Ram, 0x1000); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetSegment(Segment.Ss, 0);
            m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 2; m.RunSteps(1000);
            Check(m.Fault is null && m.Cpu.Halted && m.Keyboard.OutputByteCount == 0 && m.Cpu.GetRegister(Register.Di) == 0x6040);
            Check(m.Memory.Ram.AsSpan(0x6000, 64).ToArray().All(v => v == 0x1e), "CPU did not receive exactly admitted make bytes");
            Check(Send(session, "SendScanCode", new { scanCode = 0x9e }).Success && m.Keyboard.Read(0x60, 1) == 0x9e);
        });
        foreach (int fill in new[] { 0, 58, 59, 64 })
            test("Audit input CtrlAltDel is an atomic six-byte packet at fill " + fill, () =>
            {
                using var session = new MachineSession(PcMachine.CreateDemo()); var k = session.Machine.Keyboard;
                for (int i = 0; i < fill; i++) Check(k.TryInjectTranslatedSet1(0x1e)); string before = State(k);
                var response = Send(session, "SendCtrlAltDel"); bool accepted = fill <= 58;
                Check(response.Success == accepted && (accepted || response.ErrorCode == "InputNotAccepted"));
                if (!accepted) Check(before == State(k));
                byte[] bytes = Drain(k); Check(bytes.Take(fill).All(v => v == 0x1e) && bytes.Length == fill + (accepted ? 6 : 0));
                if (accepted) Check(bytes.Skip(fill).SequenceEqual(new byte[] { 0x1d, 0x38, 0x53, 0xd3, 0xb8, 0x9d }));
            });
        foreach (int fill in new[] { 0, 62, 63, 64 })
            test("Audit input extended prefix packet has no partial admission at fill " + fill, () =>
            {
                using var session = new MachineSession(PcMachine.CreateDemo()); var k = session.Machine.Keyboard;
                for (int i = 0; i < fill; i++) Check(k.TryInjectTranslatedSet1(0x1e)); string before = State(k);
                var response = Send(session, "SendScanCode", new { scanCodes = new[] { 0xe0, 0xc8 } }); bool accepted = fill <= 62;
                Check(response.Success == accepted && (accepted || response.ErrorCode == "InputNotAccepted"));
                if (!accepted) Check(State(k) == before);
                byte[] bytes = Drain(k); Check(bytes.Length == fill + (accepted ? 2 : 0));
                if (accepted) Check(bytes[^2] == 0xe0 && bytes[^1] == 0xc8);
            });
        foreach (int mode in new[] { 0, 1, 2 })
            test("Audit input disabled scanning/controller/translation returns a rejection mode " + mode, () =>
            {
                using var session = new MachineSession(PcMachine.CreateDemo()); var k = session.Machine.Keyboard;
                if (mode == 0) k.Write(0x64, 0xad, 1);
                else if (mode == 1) { k.Write(0x60, 0xf5, 1); Drain(k); }
                else { k.Write(0x64, 0x60, 1); k.Write(0x60, 0x05, 1); }
                string before = State(k); var response = Send(session, "SendScanCode", new { scanCode = 0x1e });
                Check(!response.Success && response.ErrorCode == "InputNotAccepted" && State(k) == before);
                response = Send(session, "SendCtrlAltDel"); Check(!response.Success && response.ErrorCode == "InputNotAccepted" && State(k) == before);
            });
        foreach (object payload in new object[] { new { scanCodes = Array.Empty<int>() }, new { scanCodes = Enumerable.Repeat(1, 17).ToArray() },
            new { scanCode = 1, scanCodes = new[] { 2 } }, new { scanCodes = new[] { 0xe0, 256 } }, new { scanCodes = new[] { -1 } }, new { scanCode = 256 } })
            test("Audit input malformed packet is rejected without escaping owner or partial prefix " + JsonSerializer.Serialize(payload), () =>
            {
                using var session = new MachineSession(PcMachine.CreateDemo()); string before = State(session.Machine.Keyboard);
                var response = Send(session, "SendScanCode", payload); Check(!response.Success && response.ErrorCode == "InvalidRequest" && State(session.Machine.Keyboard) == before);
                Check(Send(session, "Ping").Success, "Owner did not survive a rejected input payload");
            });
        test("Audit input packet from retired machine generation cannot reach the replacement", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo()); var reset = session.Enqueue(IpcProtocol.CreateRequest("PowerCycleDemo"));
            var input = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCodes = new[] { 0xe0, 0xc8 } })); session.PumpCommands();
            Check(reset.Result.Success && !input.Result.Success && input.Result.ErrorCode == "StaleInputGeneration" && session.Machine.Keyboard.OutputByteCount == 0);
        });
        test("Audit input atomic packet survives explicit controller state continuation", () =>
        {
            var a = new Keyboard8042(new MemoryBus(1024 * 1024), _ => { }); var b = new Keyboard8042(new MemoryBus(1024 * 1024), _ => { });
            Check(a.TryInjectTranslatedSet1(new byte[] { 0xe0, 0x48, 0xe0, 0xc8 })); b.LoadState(a.SaveState());
            Check(State(a) == State(b) && Drain(a).SequenceEqual(Drain(b)) && a.OutputByteCount == b.OutputByteCount);
        });
        test("Audit capability reports implemented protected mode while host network remains disabled", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo()); var response = Send(session, "GetCapabilities");
            var data = JsonSerializer.SerializeToElement(response.Payload, IpcProtocol.JsonOptions);
            Check(response.Success && data.GetProperty("protectedMode").GetBoolean() && data.GetProperty("atomicKeyboardPackets").GetBoolean());
            Check(data.GetProperty("keyboardInputBackpressure").GetBoolean() && !data.GetProperty("guestNetwork").GetBoolean() && !data.GetProperty("hostAudioInput").GetBoolean());
        });
        foreach (byte make in new byte[] { 0x1e, 0x0e, 0x48, 0x50, 0x3b, 0x1c })
            test("Audit host held mapped make repeats without releasing key " + make, () => Check(GuestKeyRepeatPolicy.ShouldRepeat(make, true) && !GuestKeyRepeatPolicy.ShouldRepeat(make, false)));
        foreach (byte make in new byte[] { 0, 0x1d, 0x2a, 0x36, 0x38, 0x3a, 0x45, 0x46, 0x9e, 0xe0 })
            test("Audit host modifiers locks releases and unmapped codes never typematically repeat " + make, () => Check(!GuestKeyRepeatPolicy.ShouldRepeat(make, true)));
        test("Audit host admitted repeated make bytes and a final break remain ordered", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo());
            foreach (int code in new[] { 0x1e, 0x1e, 0x1e, 0x9e }) Check(Send(session, "SendScanCode", new { scanCode = code }).Success);
            Check(Drain(session.Machine.Keyboard).SequenceEqual(new byte[] { 0x1e, 0x1e, 0x1e, 0x9e }));
        });
        foreach (ulong behind in new ulong[] { 1, 100_000, 20_000_000, 99_000_000 })
            test("Audit host pacing behind target introduces no mandatory sleep ns " + behind, () =>
            { var p = new RealtimePacer(); Check(p.DelayMilliseconds(0, behind) == 0 && p.Resynchronizations == 0); });
        test("Audit host pacing equal ahead pause and catch-up bounds retain their waits", () =>
        {
            var p = new RealtimePacer(); Check(p.DelayMilliseconds(0, 0) == 1 && p.DelayMilliseconds(1, 0) == 1 && p.DelayMilliseconds(20_000_000, 0) == 16);
            Check(p.Target(0, 99_000_000) == 20_000_000); Check(p.Target(0, 101_000_000) == 0 && p.Resynchronizations == 1);
            p.Reset(50_000_000, 1_000_000_000); Check(p.DelayMilliseconds(50_000_000, 1_000_000_000) == 1 && p.Target(50_000_000, 1_003_000_000) == 53_000_000);
        });
        test("Audit host GPU production cache retries until actual upload commit and invalidates source changes", () =>
        {
            var cache = new GpuSourceRevision(); var pixels = new uint[4];
            Check(cache.NeedsUpload(pixels, 2, 2, 1) && cache.NeedsUpload(pixels, 2, 2, 1)); // No upload committed yet.
            cache.Uploaded(pixels, 2, 2, 1); Check(!cache.NeedsUpload(pixels, 2, 2, 1));
            Check(cache.NeedsUpload(new uint[4], 2, 2, 1) && cache.NeedsUpload(pixels, 1, 4, 1) && cache.NeedsUpload(pixels, 2, 2, 2));
            cache.Invalidate(); Check(cache.NeedsUpload(pixels, 2, 2, 1));
        });
        test("Audit host GPU rejected upload invalidates old identity and retries until confirmed", () =>
        {
            var cache = new GpuSourceRevision(); var pixels = new uint[4];
            Check(cache.CompleteUpload(pixels, 2, 2, 1, true) && !cache.NeedsUpload(pixels, 2, 2, 1));
            Check(!cache.CompleteUpload(pixels, 2, 2, 2, false));
            Check(cache.NeedsUpload(pixels, 2, 2, 1) && cache.NeedsUpload(pixels, 2, 2, 2));
            Check(cache.CompleteUpload(pixels, 2, 2, 2, true) && !cache.NeedsUpload(pixels, 2, 2, 2));
        });
        test("Audit host GPU unversioned legacy callers always upload and cannot poison versioned cache", () =>
        {
            var cache = new GpuSourceRevision(); var pixels = new uint[4]; cache.Uploaded(pixels, 2, 2, 1);
            Check(cache.NeedsUpload(pixels, 2, 2, null)); cache.Uploaded(pixels, 2, 2, null);
            Check(cache.NeedsUpload(pixels, 2, 2, null) && cache.NeedsUpload(pixels, 2, 2, 1));
        });
        test("Audit host GPU stable source policy removes 9999 repeated upload decisions", () =>
        {
            var cache = new GpuSourceRevision(); var pixels = new uint[4]; int versioned = 0, legacy = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (cache.NeedsUpload(pixels, 2, 2, 1)) { versioned++; cache.Uploaded(pixels, 2, 2, 1); }
                if (cache.NeedsUpload(pixels, 2, 2, null)) legacy++;
            }
            Check(versioned == 1 && legacy == 10000); Console.WriteLine("MEASURE audit GPU policy: versionedUploads=1 legacyUploads=10000 iterations=10000; no native GL timing claim");
        });
        test("Audit host same-pixel identity excludes capture timestamps but includes owner and blink phase", () =>
        {
            var v = new VgaAdapter(); var a = new VgaFrameBuffer(); var b = new VgaFrameBuffer();
            Check(!a.HasSamePixels(b)); var fa = v.Capture(0, a); var fb = v.Capture(1, b);
            Check(fa.FrameId != fb.FrameId && fa.VirtualNanoseconds != fb.VirtualNanoseconds && a.HasSamePixels(b));
            v.Capture(300_000_000, b); Check(!a.HasSamePixels(b)); new VgaAdapter().Capture(0, b); Check(!a.HasSamePixels(b));
        });
        test("Audit host identical surfaces retain read lease through later presentation", () =>
        {
            var video = new VgaAdapter(); var frames = new LatestBufferExchange<VgaFrameBuffer>(() => new());
            frames.TryPublish(surface => video.Capture(0, surface)); using var held = frames.AcquireLatest()!; uint[] pixels = held.Value.Frame!.ArgbPixels; uint[] copy = pixels.ToArray();
            for (int i = 0; i < 6; i++) { Check(frames.TryPublish(surface => video.Capture(1, surface))); using var next = frames.AcquireLatest()!; Check(held.Value.HasSamePixels(next.Value)); }
            Check(pixels.SequenceEqual(copy) && ReferenceEquals(pixels, held.Value.Frame!.ArgbPixels));
        });
    }
}
