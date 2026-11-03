// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.ExceptionServices;

namespace DOSEmulator.Whpx;

/// <summary>Runs one ordered native present while the guest cooperatively waits; never owns guest memory, registers, IRQs or device time.</summary>
internal sealed unsafe class WhpxGlideSwapWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _complete = new(true);
    private readonly Func<nint, uint, ulong> _invoke;
    private Thread? _thread;
    private uint _issued, _pending, _interval;
    private nint _function;
    private bool _finished, _stopping, _disposed;
    private ExceptionDispatchInfo? _error;

    /// <summary>Defers creation of the sleeping worker until a guest explicitly submits a cooperative swap.</summary>
    internal WhpxGlideSwapWorker() : this(CallNative) { }

    /// <summary>Allows finite failure and lifetime oracles without a host renderer or guest-memory access.</summary>
    internal WhpxGlideSwapWorker(Func<nint, uint, ulong> invoke) => _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));

    /// <summary>Includes completed but unconsumed results, which must not be silently captured or reused.</summary>
    internal bool HasPending => Volatile.Read(ref _pending) != 0;

    /// <summary>Copies one immutable function/interval pair and returns a nonreused completion token.</summary>
    internal uint Begin(nint function, uint interval)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (function == 0) throw new ArgumentException("Missing native Glide swap function.", nameof(function));
            if (_pending != 0) throw new InvalidOperationException("A Glide swap is already awaiting completion.");
            uint token = checked(_issued + 1);
            _issued = _pending = token; _function = function; _interval = interval; _finished = false; _error = null;
            _complete.Reset();
            if (_thread is null)
            {
                var worker = new Thread(Work) { IsBackground = true, Name = "MiniGlide swap" };
                try { worker.Start(); _thread = worker; }
                catch { _pending = 0; _complete.Set(); throw; }
            }
            _wake.Set();
            return token;
        }
    }

    /// <summary>Returns pending/complete exactly once; stale and foreign tokens never consume another request.</summary>
    internal uint Poll(uint token)
    {
        ExceptionDispatchInfo? error;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (token == 0 || token != _pending) throw new InvalidDataException("Stale Glide swap completion token.");
            if (!_finished) return 0;
            error = _error; _pending = 0; _error = null;
        }
        error?.Throw();
        return 1;
    }

    /// <summary>Serializes later renderer calls behind the accepted swap without consuming its guest completion.</summary>
    internal void Drain()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        _complete.Wait();
        lock (_gate) _error?.Throw();
    }

    /// <summary>Finishes accepted native work before reset and invalidates its result, retaining the token sequence.</summary>
    internal void Reset()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        _complete.Wait();
        lock (_gate) { _pending = 0; _error = null; }
    }

    /// <summary>Calls the same native export with the original complete interval bits.</summary>
    private static ulong CallNative(nint function, uint interval) => ((delegate* unmanaged<ulong, ulong>)function)(interval);

    /// <summary>Sleeps between requests and publishes completion only after the native call has actually returned.</summary>
    private void Work()
    {
        while (true)
        {
            _wake.WaitOne();
            nint function; uint interval;
            lock (_gate) { if (_stopping) return; function = _function; interval = _interval; }
            ExceptionDispatchInfo? error = null;
            try { _invoke(function, interval); }
            catch (Exception caught) { error = ExceptionDispatchInfo.Capture(caught); }
            lock (_gate) { _error = error; _finished = true; _complete.Set(); }
        }
    }

    /// <summary>Joins accepted work and the sleeping thread before the caller can unload its native library.</summary>
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _complete.Wait();
        lock (_gate) { _stopping = true; _pending = 0; _wake.Set(); }
        _thread?.Join();
        _wake.Dispose(); _complete.Dispose();
    }
}
