// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections.Concurrent;
using DOSEmulator.Runtime;

internal static class IpcLifecycleTests
{
    private sealed class NonPumpingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();
        /// <summary>Number of continuations posted to the deliberately nonpumping test synchronization context.</summary>
        public int Posts { get; private set; }
        /// <summary>Records a continuation without executing it, simulating a stopped GUI synchronization context.</summary>
        public override void Post(SendOrPostCallback callback, object? state) { Posts++; _posted.Enqueue((callback, state)); }
        /// <summary>Executes only the continuations explicitly queued by this controlled test context.</summary>
        public void Drain() { while (_posted.TryDequeue(out var work)) work.Callback(work.State); }
    }

    /// <summary>Proves IPC cancellation/disposal can finish without pumping a closing GUI synchronization context.</summary>
    public static void CheckShutdown()
    {
        var previous = SynchronizationContext.Current;
        var context = new NonPumpingContext();
        Task? disposal = null;
        using var session = new MachineSession(PcMachine.CreateDemo());
        bool completed;
        try
        {
            // Equivalent to a GUI synchronously closing after its message loop stops.
            SynchronizationContext.SetSynchronizationContext(context);
            var server = new IpcServer(session);
            disposal = server.DisposeAsync().AsTask();
            completed = disposal.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            // Clean up even when running this test against the intentionally faulty old server.
            long deadline = Environment.TickCount64 + 5000;
            while (disposal is { IsCompleted: false } && Environment.TickCount64 < deadline)
            {
                context.Drain(); Thread.Sleep(1);
            }
            context.Drain();
        }
        if (!completed || context.Posts != 0) throw new Exception("IPC shutdown needs the stopped GUI synchronization context.");
        disposal.GetAwaiter().GetResult();
    }
}
