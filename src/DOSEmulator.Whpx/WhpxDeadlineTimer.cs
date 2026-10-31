// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DOSEmulator.Whpx;

/// <summary>A sleeping high-resolution deadline helper cancels native CPU runs; a separate timer waits during guest HLT.</summary>
internal sealed class WhpxDeadlineTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly nint _partition, _timer, _haltTimer;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _worker;
    private long _due;
    private bool _armed, _disposed;
    private Exception? _failure;

    /// <summary>Creates a private high-resolution waitable timer from the Windows system library.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateTimer(nint attributes, string? name, uint flags, uint access);
    /// <summary>Arms one relative private deadline without changing global timer resolution.</summary>
    [DllImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTimer(nint timer, in long due, int period, nint callback, nint argument, nint wakeContext, uint tolerance);
    /// <summary>Disarms only the supplied private waitable timer.</summary>
    [DllImport("kernel32.dll", EntryPoint = "CancelWaitableTimer", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelTimer(nint timer);
    /// <summary>Waits for the private stop signal or a timer deadline without polling the CPU.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint timeout);
    /// <summary>Releases the native handle after the only timer worker has joined.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>Creates only private timers and a background deadline thread, with no UI or host audio/network endpoint.</summary>
    internal WhpxDeadlineTimer(nint partition)
    {
        _partition = partition;
        _timer = CreateTimer(0, null, 2, 0x100002); // HIGH_RESOLUTION, MODIFY_STATE | SYNCHRONIZE
        _haltTimer = CreateTimer(0, null, 2, 0x100002);
        if (_timer == 0 || _haltTimer == 0)
        {
            if (_timer != 0) CloseHandle(_timer);
            if (_haltTimer != 0) CloseHandle(_haltTimer);
            _stop.Dispose();
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WHP high-resolution timers are unavailable.");
        }
        _worker = new Thread(WaitAndCancel) { IsBackground = true, Name = "DOSE WHP deadline" };
        _worker.Start();
    }

    /// <summary>Arms a new native-run deadline; an old timer wake can never cancel before this new deadline.</summary>
    internal void Arm(ulong nanoseconds)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckFailure();
            nanoseconds = Math.Max(10_000UL, nanoseconds);
            long now = Stopwatch.GetTimestamp();
            long due = checked(now + (long)(((UInt128)nanoseconds * (ulong)Stopwatch.Frequency + 999_999_999) / 1_000_000_000));
            // An existing earlier cancellation is also a valid upper bound.
            // Keep its sleeping timer across short MMIO/port exits.
            if (_armed && _due > now && due >= _due) return;
            _due = due;
            _armed = true;
            SetRelative(_timer, nanoseconds);
        }
    }

    /// <summary>Disarms immediately after native execution and before any instruction emulation or snapshot.</summary>
    internal void Disarm()
    {
        lock (_gate) { _armed = false; CancelTimer(_timer); }
    }

    /// <summary>Waits without CPU spinning while halted, waking at the earlier device/owner deadline.</summary>
    internal void WaitHalted(ulong nanoseconds)
    {
        CheckFailure();
        if (nanoseconds == 0) return;
        SetRelative(_haltTimer, nanoseconds);
        uint result = WaitForMultipleObjects(2, [_stop.SafeWaitHandle.DangerousGetHandle(), _haltTimer], false, uint.MaxValue);
        if (result == uint.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CheckFailure();
    }

    /// <summary>Retries a due cancellation until the owner disarms, including a host preemption just before WHvRun enters.</summary>
    private void WaitAndCancel()
    {
        try
        {
            nint[] handles = [_stop.SafeWaitHandle.DangerousGetHandle(), _timer];
            while (true)
            {
                uint wake = WaitForMultipleObjects(2, handles, false, uint.MaxValue);
                if (wake == 0) break;
                if (wake != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WHP deadline wait failed.");
                lock (_gate)
                {
                    if (!_armed || _disposed) continue;
                    long remaining = _due - Stopwatch.GetTimestamp();
                    if (remaining <= 0)
                    {
                        WhpxNative.WHvCancelRunVirtualProcessor(_partition, 0, 0);
                        SetRelative(_timer, 1_000_000);
                    }
                    else SetRelative(_timer, (ulong)((UInt128)(ulong)remaining * 1_000_000_000 / (ulong)Stopwatch.Frequency));
                }
            }
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            while (Volatile.Read(ref _armed) && !Volatile.Read(ref _disposed))
            {
                WhpxNative.WHvCancelRunVirtualProcessor(_partition, 0, 0);
                Thread.Sleep(1);
            }
            _stop.Set();
        }
    }

    /// <summary>Reports a helper failure on the owner instead of aborting the host process from a background thread.</summary>
    internal void CheckFailure()
    {
        if (Volatile.Read(ref _failure) is { } error) throw new InvalidOperationException("WHP deadline helper failed.", error);
    }

    /// <summary>Uses a relative 100-ns deadline, with no recurring timer and no global timer-resolution request.</summary>
    private static void SetRelative(nint timer, ulong nanoseconds)
    {
        long due = -checked((long)Math.Max(1UL, (nanoseconds + 99) / 100));
        if (!SetTimer(timer, in due, 0, 0, 0, 0, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Joins the only helper before the WHP partition or timer handles can be released.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _armed = false; CancelTimer(_timer); _stop.Set();
        }
        _worker.Join();
        CloseHandle(_timer); CloseHandle(_haltTimer); _stop.Dispose();
    }
}
