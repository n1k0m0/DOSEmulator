// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

/// <summary>Host-only completion, error, nesting and shutdown checks without windows or external devices.</summary>
internal static class JoinedWorkerTests
{
    /// <summary>Requires explicit completion and original exception behavior.</summary>
    private static void Check(bool value, string why)
    { if (!value) throw new Exception(why); }

    /// <summary>Registers bounded worker lifecycle and publication-barrier controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Joined worker uses a separate thread and joins all output", () =>
        {
            using var worker = new JoinedWorker("DOSE worker test"); int caller = Environment.CurrentManagedThreadId, other = caller;
            int[] output = new int[10000];
            worker.Run(() => { other = Environment.CurrentManagedThreadId; for (int i = 5000; i < output.Length; i++) output[i] = i; },
                () => { for (int i = 0; i < 5000; i++) output[i] = i; });
            Check(other != caller && worker.CompletedBatches == 1, "Work did not run on the separate host thread.");
            Check(output.Select((value, index) => value == index).All(value => value), "A worker returned incomplete output.");
        });
        test("Joined worker propagates its original error and accepts the next operation", () =>
        {
            using var worker = new JoinedWorker("DOSE worker error test"); var expected = new InvalidDataException("own worker error"); Exception? actual = null;
            try { worker.Run(() => throw expected, () => { }); } catch (Exception error) { actual = error; }
            Check(ReferenceEquals(expected, actual), "Worker error was wrapped or lost.");
            int complete = 0; worker.Run(() => complete = 1, () => { }); Check(complete == 1 && worker.CompletedBatches == 2, "Worker did not recover after its error.");
        });
        test("Joined worker still joins background work when the foreground fails", () =>
        {
            using var worker = new JoinedWorker("DOSE foreground error test"); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int complete = 0; var expected = new InvalidDataException("own foreground error"); Exception? actual = null;
            try { worker.Run(() => { entered.Set(); release.Wait(); complete = 1; }, () => { entered.Wait(); release.Set(); throw expected; }); }
            catch (Exception error) { actual = error; }
            Check(ReferenceEquals(expected, actual) && complete == 1, "Foreground failure escaped before the worker completed.");
        });
        test("Joined worker rejects nested work without corrupting its pending operation", () =>
        {
            using var worker = new JoinedWorker("DOSE nesting test"); Exception? actual = null;
            try { worker.Run(() => { }, () => worker.Run(() => { }, () => { })); } catch (Exception error) { actual = error; }
            Check(actual is InvalidOperationException, "Nested join was accepted.");
            worker.Run(() => { }, () => { }); Check(worker.CompletedBatches == 2, "Nested rejection corrupted later work.");
        });
        test("Joined worker shutdown is idempotent and subsequent submissions are rejected", () =>
        {
            var worker = new JoinedWorker("DOSE shutdown test"); worker.Run(() => { }, () => { }); worker.Dispose(); worker.Dispose(); Exception? actual = null;
            try { worker.Run(() => { }, () => { }); } catch (Exception error) { actual = error; }
            Check(actual is ObjectDisposedException, "A disposed worker accepted new work.");
        });
    }
}
