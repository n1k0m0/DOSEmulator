using DOSEmulator.Hardware;

internal static partial class Opl2Tests
{
    private static void ConfigureCsm(Rig r, byte preset = 128, byte control = 0x41)
    {
        r.Write(0x23, 0x21); r.Write(0x63, 0xf0); r.Write(0x83, 0x0f);
        r.Write(0xa0, 0x44); r.Write(0xb0, 0x12); r.Write(0xc0, 1);
        r.Write(8, 0x80); r.Write(2, preset); r.Write(4, control);
    }
    private static ulong NativeSampleTime(long sample) => (ulong)(((UInt128)(ulong)sample * 72 * 1000000000 + 3579544) / 3579545);
    private static void FirstCsmEdge(Rig r) => r.Advance(r.Clock.NextEventTime!.Value - r.Clock.Now);
    public static void AddCsmTo(Action<string, Action> test)
    {
        test("CSM waits for timer one then keys all 18 slots at its edge", () =>
        {
            var r = new Rig(); ConfigureCsm(r); ulong deadline = r.Clock.NextEventTime!.Value;
            r.Advance(deadline - r.Clock.Now - 1); Check(r.Opl.CsmKeyPulses == 0 && r.Opl.KeyOnCount == 0 && r.Samples().All(x => x == 0));
            r.Advance(1); Check(r.Opl.CsmKeyPulses == 1 && r.Opl.KeyOnCount == 18 && r.Opl.CsmPulseActive);
            for (int ch = 0; ch < 9; ch++) foreach (bool carrier in new[] { false, true })
                Check(r.Opl.InspectOperator(ch, carrier) is { Keyed: true, Stage: OplEnvelopeStage.Attack });
        });
        test("CSM key pulse renders a native attack sample then releases without B-register keys", () =>
        {
            var r = new Rig(); ConfigureCsm(r); FirstCsmEdge(r);
            ulong sample = NativeSampleTime(r.Opl.GeneratedSamples + 1); r.Advance(sample - r.Clock.Now - 1);
            Check(r.Opl.CsmPulseActive); r.Advance(1);
            Check(!r.Opl.CsmPulseActive && r.Opl.Output != 0);
            for (int ch = 0; ch < 9; ch++) Check(!r.Opl.InspectOperator(ch, true).Keyed && (r.Opl.InspectRegister((byte)(0xb0 + ch)) & 0x20) == 0);
            Check(r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Release);
            r.Advance(5000000); Check(r.Opl.Output == 0 && r.Opl.InspectOperator(0, true).Stage == OplEnvelopeStage.Off);
        });
        test("CSM masked timer overflows continue key pulses with no status interrupt", () =>
        {
            var r = new Rig(); ConfigureCsm(r); r.Advance(100000000);
            Check(r.Opl.Timer1Overflows == r.Opl.CsmKeyPulses && r.Opl.CsmKeyPulses >= 9 && r.Opl.KeyOnCount == 18 * r.Opl.CsmKeyPulses);
            Check(!r.Opl.TimerInterruptAsserted && r.Opl.Read(0x388, 1) == 6 && r.RmsTail() > 0.003);
        });
        test("timer two never starts CSM key pulses", () =>
        {
            var r = new Rig(); ConfigureCsm(r, 255, 0); r.Write(3, 255); r.Write(4, 2); r.Advance(10000000);
            Check(r.Opl.Timer2Overflows > 20 && r.Opl.CsmKeyPulses == 0 && r.Opl.KeyOnCount == 0 && r.Samples().All(x => x == 0));
        });
        test("CSM flag reset neither retriggers a key nor stops subsequent pulses", () =>
        {
            var r = new Rig(); ConfigureCsm(r, control: 1); FirstCsmEdge(r); r.Advance(1000000);
            Check(r.Opl.TimerInterruptAsserted && r.Opl.CsmKeyPulses == 1); r.Write(4, 0x80);
            Check(!r.Opl.TimerInterruptAsserted && r.Opl.CsmKeyPulses == 1); FirstCsmEdge(r);
            Check(r.Opl.TimerInterruptAsserted && r.Opl.CsmKeyPulses == 2 && r.Opl.KeyOnCount == 36);
        });
        test("CSM disable removes its pending key source while timer one keeps counting", () =>
        {
            var r = new Rig(); ConfigureCsm(r); ulong deadline = r.Clock.NextEventTime!.Value;
            r.Opl.Write(0x388, 8, 1); r.Advance(Opl2.AddressWaitNanoseconds); r.Advance(deadline - r.Clock.Now);
            Check(r.Opl.CsmPulseActive); r.Opl.Write(0x389, 0, 1);
            Check(!r.Opl.CsmEnabled && !r.Opl.CsmPulseActive && !r.Opl.InspectOperator(0, true).Keyed);
            r.Advance(50000000); Check(r.Opl.CsmKeyPulses == 1 && r.Opl.Timer1Overflows > 4);
        });
        test("stopping timer one lets the already triggered pulse release normally", () =>
        {
            var r = new Rig(); ConfigureCsm(r); ulong deadline = r.Clock.NextEventTime!.Value;
            r.Opl.Write(0x388, 4, 1); r.Advance(Opl2.AddressWaitNanoseconds); r.Advance(deadline - r.Clock.Now);
            r.Opl.Write(0x389, 0, 1); Check(r.Opl.CsmPulseActive); r.Advance(200000000);
            Check(r.Opl.CsmKeyPulses == 1 && !r.Opl.CsmPulseActive && r.Opl.ActiveOperators == 0 && r.RmsTail(500) < 1e-8);
        });
        test("CSM release does not remove a simultaneously held melodic key", () =>
        {
            var r = new Rig(); ConfigureCsm(r); r.Write(0xb0, 0x32); FirstCsmEdge(r); r.Advance(1000000);
            Check(r.Opl.InspectOperator(0, true).Keyed && r.Opl.KeyOnCount == 18 && !r.Opl.InspectOperator(1, true).Keyed);
            r.Write(8, 0); Check(r.Opl.InspectOperator(0, true).Keyed);
            r.Write(0xb0, 0x12); Check(!r.Opl.InspectOperator(0, true).Keyed);
        });
        test("melodic key-off during a CSM pulse leaves CSM in control until its release", () =>
        {
            var r = new Rig(); ConfigureCsm(r); r.Write(0xb0, 0x32); ulong deadline = r.Clock.NextEventTime!.Value;
            r.Opl.Write(0x388, 0xb0, 1); r.Advance(Opl2.AddressWaitNanoseconds); r.Advance(deadline - r.Clock.Now);
            r.Opl.Write(0x389, 0x12, 1); Check(r.Opl.CsmPulseActive && r.Opl.InspectOperator(0, true).Keyed);
            r.Advance(30000); Check(!r.Opl.CsmPulseActive && !r.Opl.InspectOperator(0, true).Keyed);
        });
        test("CSM rendering follows timer events identically across host chunk sizes", () =>
        {
            var a = new Rig(); var b = new Rig(); ConfigureCsm(a, 255); ConfigureCsm(b, 255);
            a.Advance(100000000); for (int i = 0; i < 1000; i++) b.Advance(100000);
            Check(a.Opl.CsmKeyPulses > 1200 && a.Opl.CsmKeyPulses == b.Opl.CsmKeyPulses);
            Check(a.Samples().AsSpan().SequenceEqual(b.Samples()));
        });
        test("CSM reset cancels timers and a pending native-sample release", () =>
        {
            var r = new Rig(); ConfigureCsm(r); FirstCsmEdge(r); r.Opl.Reset(); r.Advance(100000000);
            Check(r.Opl.CsmKeyPulses == 1 && !r.Opl.CsmEnabled && !r.Opl.CsmPulseActive && r.Opl.ActiveOperators == 0 && r.RmsTail() < 1e-8);
        });
        test("CSM zero attack remains silent and its incomplete accuracy stays diagnosed", () =>
        {
            var r = new Rig(); ConfigureCsm(r); r.Write(0x63, 0); r.Advance(100000000);
            Check(r.Opl.CsmKeyPulses > 0 && r.Samples().All(x => x == 0) && r.Opl.UnsupportedCsm && r.Opl.UnsupportedProgramming);
        });
    }
}
