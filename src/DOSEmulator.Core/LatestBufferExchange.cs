// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Three owned buffers; publishing never overwrites a buffer held by a reader.</summary>
public sealed class LatestBufferExchange<T>(Func<T> create) where T : class
{
    private sealed class Slot(T value, int index) { public readonly T Value = value; public readonly int Index = index; public int Readers; public bool Writing; }
    private readonly object _gate = new();
    private readonly Slot[] _slots = [new(create(), 0), new(create(), 1), new(create(), 2)];
    private Slot? _latest;
    /// <summary>Reserves an unleased nonpublished buffer, fills it exclusively and publishes only a completed frame.</summary>
    public bool TryPublish(Action<T> fill) => TryPublish(fill, static (value, action) => action(value));
    /// <summary>Passes caller state without a per-frame closure while retaining exclusive ownership of the write buffer.</summary>
    public bool TryPublish<TState>(TState state, Action<T, TState> fill)
    {
        Slot? next;
        lock (_gate)
        {
            next = null;
            foreach (var slot in _slots)
                if (!ReferenceEquals(slot, _latest) && slot.Readers == 0 && !slot.Writing) { next = slot; break; }
            if (next is null) return false;
            next.Writing = true;
        }
        try { fill(next.Value, state); }
        catch { lock (_gate) next.Writing = false; throw; }
        lock (_gate) { next.Writing = false; _latest = next; }
        return true;
    }
    /// <summary>Acquires a disposable read lease that prevents the published buffer from being overwritten.</summary>
    public ReadLease? AcquireLatest()
    {
        lock (_gate)
        {
            if (_latest is not { } slot) return null;
            slot.Readers++; return new(slot.Value, this, slot.Index);
        }
    }
    public sealed class ReadLease : IDisposable
    {
        private LatestBufferExchange<T>? _owner;
        private readonly int _slot;
        /// <summary>Buffer value protected by this reader lease until it is disposed.</summary>
        public T Value { get; }
        /// <summary>Initializes ReadLease and connects the supplied dependencies before the component is used.</summary>
        internal ReadLease(T value, LatestBufferExchange<T> owner, int slot) { Value = value; _owner = owner; _slot = slot; }
        /// <summary>Releases this reader's buffer protection exactly once, including repeated or concurrent disposal calls.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
                lock (owner._gate) owner._slots[_slot].Readers--;
        }
    }
}
