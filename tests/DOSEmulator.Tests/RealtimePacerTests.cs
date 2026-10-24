using DOSEmulator.Core;
using DOSEmulator.Runtime;

internal static class RealtimePacerTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Host pacing assertion failed."); }
    public static void AddTo(Action<string, Action> test)
    {
        test("host pacing follows elapsed time for a running machine", () =>
        {
            var p = new RealtimePacer(); p.Reset(5000000, 900000000);
            Check(p.Target(5000000, 912000000) == 17000000 && p.Resynchronizations == 0);
        });
        test("host catch-up is split into bounded virtual slices", () =>
        {
            var p = new RealtimePacer();
            Check(p.Target(0, 80000000) == 20000000 && p.Target(20000000, 80000000) == 40000000);
            Check(p.Target(40000000, 80000000) == 60000000 && p.Resynchronizations == 0);
        });
        test("late host anchors rebase without adding elapsed host delay to virtual time", () =>
        {
            var p = new RealtimePacer(); p.Reset(5000000, 10000000);
            Check(p.Target(5000000, 1010000000) == 5000000 && p.Resynchronizations == 1);
            Check(p.Target(5000000, 1022000000) == 17000000 && p.Resynchronizations == 1);
        });
        test("host pacing never rewinds an instruction which overshoots the target", () =>
        {
            var p = new RealtimePacer(); Check(p.Target(10000003, 10000000) == 10000003);
        });
        test("audio look-ahead provides a fixed queue lead without changing elapsed virtual rate", () =>
        {
            var p = new RealtimePacer(40000000); p.Reset(5000000, 100000000);
            Check(p.Target(5000000, 100000000) == 25000000);
            Check(p.Target(25000000, 100000000) == 45000000);
            Check(p.Target(45000000, 110000000) == 55000000 && p.Target(55000000, 120000000) == 65000000);
        });
        test("pause and machine reset establish a fresh host-time origin", () =>
        {
            var p = new RealtimePacer(); p.Reset(7000000, 9000000000);
            Check(p.Target(7000000, 9003000000) == 10000000);
            p.Reset(0, 10000000000); Check(p.Target(0, 10005000000) == 5000000);
        });
        test("a machine behind host time yields briefly instead of accumulating a full sleep per slice", () =>
        {
            var p = new RealtimePacer(); Check(p.DelayMilliseconds(0, 20000000) == 16);
            Check(p.DelayMilliseconds(0, 20000001) == 1 && p.DelayMilliseconds(20000001, 20000000) == 16);
        });
        test("host lag recovery leaves device deadlines intact and delivers their events once", () =>
        {
            var clock = new DeviceScheduler(); int delivered = 0; clock.AdvanceTo(5000000);
            clock.ScheduleAfter(1000000, () => delivered++);
            var p = new RealtimePacer(); p.Reset(clock.Now, 0);
            clock.AdvanceTo(p.Target(clock.Now, 1000000000)); Check(clock.Now == 5000000 && delivered == 0);
            clock.AdvanceTo(p.Target(clock.Now, 1002000000)); Check(clock.Now == 7000000 && delivered == 1);
            clock.AdvanceTo(p.Target(clock.Now, 1003000000)); Check(delivered == 1);
        });
    }
}
