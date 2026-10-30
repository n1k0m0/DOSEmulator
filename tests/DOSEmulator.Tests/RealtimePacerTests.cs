// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Runtime;

internal static class RealtimePacerTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition) { if (!condition) throw new Exception("Host pacing assertion failed."); }
    /// <summary>Registers the named realtime pacer regression cases with the common runner.</summary>
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
            var p = new RealtimePacer(); Check(p.DelayMilliseconds(0, 20000000) == 0);
            Check(p.DelayMilliseconds(0, 20000001) == 0 && p.DelayMilliseconds(20000001, 20000000) == 1);
            Check(p.DelayMilliseconds(2000000,0)==2 && p.DelayMilliseconds(2000001,0)==3 && p.DelayMilliseconds(50000000,0)==16);
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
        test("interactive pacing executes a short stall in bounded slices without discarding host time", () =>
        {
            var clock=new DeviceScheduler(); var delivered=new List<ulong>();
            foreach(ulong due in new ulong[]{1,20_000_000,20_000_001,375_000_000,750_000_000})
            { clock.ScheduleAfter(due,()=>delivered.Add(clock.Now)); }
            var p=new RealtimePacer(0,2_000_000_000);
            for(int i=0;i<38;i++)
            {
                ulong before=clock.Now,target=p.Target(before,750_000_000);
                Check(target>=before && target-before<=20_000_000 && p.Resynchronizations==0);
                clock.AdvanceTo(target);
            }
            Check(clock.Now==750_000_000 && delivered.SequenceEqual(new ulong[]{1,20_000_000,20_000_001,375_000_000,750_000_000}));
        });
        test("interactive pacing keeps audio lead through a recoverable host stall", () =>
        {
            var p=new RealtimePacer(40_000_000,2_000_000_000);p.Reset(5_000_000,100_000_000);
            ulong now=5_000_000;
            for(int i=0;i<27;i++)now=p.Target(now,600_000_000);
            Check(now==545_000_000 && p.Resynchronizations==0);
            Check(p.Target(now,610_000_000)==555_000_000);
        });
        test("interactive pacing recovers a Windows loader stall by executing all pending device events", () =>
        {
            var clock=new DeviceScheduler();var actual=new List<ulong>();
            foreach(ulong due in new ulong[]{1_000_000,100_000_000,2_000_000_000,3_500_000_000})
                clock.ScheduleAfter(due,()=>actual.Add(clock.Now));
            var p=new RealtimePacer(0,5_000_000_000);
            for(int i=0;i<175;i++)
            {
                ulong before=clock.Now,target=p.Target(before,3_500_000_000);
                Check(target>=before && target-before<=20_000_000 && p.Resynchronizations==0);
                clock.AdvanceTo(target);
            }
            Check(clock.Now==3_500_000_000 && actual.SequenceEqual(new ulong[]{1_000_000,100_000_000,2_000_000_000,3_500_000_000}));
        });
        test("interactive pacing resets a prolonged host suspension without advancing any guest time", () =>
        {
            var p=new RealtimePacer(0,2_000_000_000);p.Reset(5_000_000,10_000_000);
            Check(p.Target(5_000_000,2_010_000_000)==25_000_000 && p.Resynchronizations==0);
            Check(p.Target(5_000_000,2_010_000_001)==5_000_000 && p.Resynchronizations==1);
            Check(p.Target(5_000_000,2_011_000_001)==6_000_000);
        });
        test("halted host pacing coalesces tiny work but never adds a sleep to a busy late CPU", () =>
        {
            var p=new RealtimePacer();
            foreach(ulong behind in new ulong[]{1,100_000,999_999})
                Check(p.DelayMilliseconds(0,behind,true)==1 && p.DelayMilliseconds(0,behind,false)==0);
            foreach(ulong behind in new ulong[]{1_000_000,20_000_000,99_000_000})
                Check(p.DelayMilliseconds(0,behind,true)==0 && p.DelayMilliseconds(0,behind,false)==0);
            Check(p.Resynchronizations==0 && p.DelayMilliseconds(0,0,true)==1);
        });
        foreach(ulong maximum in new ulong[]{0,19_999_999,60_000_000_001})
            test("host pacing rejects an unsupported recovery interval "+maximum,()=>
            {
                bool rejected=false;
                try{_ = new RealtimePacer(0,maximum);}catch(ArgumentOutOfRangeException){rejected=true;}
                Check(rejected);
            });
    }
}
