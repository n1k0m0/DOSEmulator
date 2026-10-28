// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.ExceptionServices;

namespace DOSEmulator.Core;

/// <summary>One sleeping host worker; every paired operation joins before returning and retains no guest callback afterward.</summary>
public sealed class JoinedWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly ManualResetEventSlim _done = new(false, 0);
    private readonly Thread _thread;
    private Action? _background;
    private ExceptionDispatchInfo? _error;
    private bool _stopping, _disposed, _running;
    private long _completed;

    /// <summary>Starts one background worker without any host audio, window, storage or network endpoint.</summary>
    public JoinedWorker(string name)
    {
        _thread = new Thread(Work) { IsBackground = true, Name = name };
        try { _thread.Start(); }
        catch { _ready.Dispose(); _done.Dispose(); throw; }
    }

    /// <summary>Completed worker operations, for host-only diagnostics outside saved machine state.</summary>
    public long CompletedBatches => Interlocked.Read(ref _completed);

    /// <summary>Runs disjoint work on two threads, waits for both even on failure, and propagates the original exception.</summary>
    public void Run(Action background, Action foreground)
    {
        ArgumentNullException.ThrowIfNull(background); ArgumentNullException.ThrowIfNull(foreground);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            throw new InvalidOperationException("A joined worker cannot submit to itself.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("A joined operation cannot be nested.");
            _running = true; _error = null; _background = background; _done.Reset(); _ready.Set();
            try
            {
                try { foreground(); }
                finally { _done.Wait(); }
                _error?.Throw();
            }
            finally { _background = null; _running = false; }
        }
    }

    /// <summary>Consumes one published callback at a time and signals completion before sleeping without spinning.</summary>
    private void Work()
    {
        while (true)
        {
            _ready.WaitOne();
            if (_stopping) return;
            try { _background!(); }
            catch (Exception error) { _error = ExceptionDispatchInfo.Capture(error); }
            finally { Interlocked.Increment(ref _completed); _done.Set(); }
        }
    }

    /// <summary>Waits for an in-flight pair, then joins the sleeping thread before releasing its events.</summary>
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            throw new InvalidOperationException("A joined worker cannot dispose itself.");
        lock (_gate)
        {
            if (_disposed) return;
            if (_running) throw new InvalidOperationException("A joined worker cannot be disposed from its foreground callback.");
            _disposed = true; _stopping = true; _ready.Set(); _thread.Join(); _ready.Dispose(); _done.Dispose();
        }
    }
}
