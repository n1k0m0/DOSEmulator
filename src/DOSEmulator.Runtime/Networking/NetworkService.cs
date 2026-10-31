// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Owner-thread boundary between saved emulated NICs and generation-scoped external transports.</summary>
public sealed class NetworkService : IDisposable
{
    private readonly PcMachine _machine;
    private readonly bool _hostEnabled;
    private readonly IEthernetBackend?[] _backends = new IEthernetBackend?[2];
    private readonly Action<byte[]>?[] _senders = new Action<byte[]>?[2];
    private readonly string?[] _errors = new string?[2];
    private bool _disposed;

    /// <summary>Cold GUI startup may connect saved cables; factories and strict snapshot imports remain inert.</summary>
    public NetworkService(PcMachine machine, bool enableHostNetworking = false)
    {
        _machine = machine; _hostEnabled = enableHostNetworking;
        foreach (var profile in machine.Profile.NetworkAdapters)
        {
            int slot = profile.Slot;
            Action<byte[]> sender = packet => _backends[slot]?.Send(packet);
            _senders[slot] = sender; machine.NetworkCards[slot]!.Transmitted += sender;
            if (machine.IsRestoredSnapshot && profile.CableConnected)
                _errors[slot] = "Snapshot restored. Select Connect to start a new external network connection.";
            else if (enableHostNetworking && profile.CableConnected) SetConnected(slot, true);
        }
    }

    /// <summary>Changes one explicit cable on the owner; a failed transport never silently changes the requested network mode.</summary>
    public void SetConnected(int slot, bool connected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profile = Profile(slot);
        if (!connected)
        {
            Retire(slot); _errors[slot] = null; return;
        }
        if (!_hostEnabled) throw new InvalidOperationException("Host networking is disabled for this controller. Use an explicit networking-enabled frontend.");
        var requested = profile with { CableConnected = true }; requested.Validate(_machine.Profile.IsPciBiosBoard);
        IEthernetBackend? prepared = null;
        try
        {
            prepared = NetworkBackendFactory.Create(requested, explicitlyConnect: true);
            if (!prepared.IsConnected) throw new IOException(prepared.Error ?? "The selected network transport could not connect.");
            Attach(slot, prepared); prepared = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or
            System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            prepared?.Dispose(); _errors[slot] = error.Message;
        }
    }

    /// <summary>Adopts an explicitly supplied controlled peer for a local test or a prepared host connection.</summary>
    public void Attach(int slot, IEthernetBackend backend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(backend);
        _ = Profile(slot);
        if (!backend.IsConnected) throw new InvalidOperationException("The prepared Ethernet backend is not connected.");
        (Profile(slot) with { CableConnected = true }).Validate(_machine.Profile.IsPciBiosBoard);
        Retire(slot);
        _machine.SetNetworkCablePreference(slot, true); _backends[slot] = backend; _errors[slot] = null;
    }

    /// <summary>Publishes one coherent disconnected state before retiring a previous external connection, even if its disposer fails.</summary>
    private void Retire(int slot)
    {
        _machine.SetNetworkCablePreference(slot, false);
        var old = _backends[slot]; _backends[slot] = null;
        try { old?.Dispose(); }
        catch (Exception error)
        {
            _errors[slot] = "Previous connection failed to close: " + error.Message;
            throw new IOException(_errors[slot], error);
        }
    }

    /// <summary>Looks up only a present validated slot, rejecting missing adapters before any side effects.</summary>
    private NetworkAdapterProfile Profile(int slot) => slot is >= 0 and <= 1
        ? _machine.Profile.NetworkAdapters.SingleOrDefault(n => n.Slot == slot) ?? throw new ArgumentException("No network adapter in this slot.")
        : throw new ArgumentOutOfRangeException(nameof(slot));

    /// <summary>Returns the selected backend's actual state, independently from the persisted cable preference.</summary>
    public bool IsConnected(int slot) { _ = Profile(slot); return _backends[slot]?.IsConnected == true; }

    /// <summary>Retains a real connection failure for both the menu and the command response.</summary>
    public string? ConnectionError(int slot) { _ = Profile(slot); return _errors[slot] ?? _backends[slot]?.Error; }

    /// <summary>Reports a failed previous generation without reconnecting it or changing the guest CPU.</summary>
    internal void RecordFailure(string message)
    {
        foreach (var adapter in _machine.Profile.NetworkAdapters) _errors[adapter.Slot] = message;
    }

    /// <summary>Admits bounded complete copies only between CPU batches, without a host callback touching guest RAM or IRQs.</summary>
    public void Pump(bool admitFrames = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!admitFrames) return;
        for (int slot = 0; slot < 2; slot++)
        {
            if (_backends[slot] is not { } backend || _machine.NetworkCards[slot] is not { } nic) continue;
            if (!backend.IsConnected) { nic.SetCableConnected(false); continue; }
            for (int count = 0; count < 8 && backend.TryReceive(out var packet); count++)
            {
                if (packet.Length is < 14 or > Hardware.Ne2000.MaximumFrameLength) { _errors[slot] = "Unsupported Ethernet frame size dropped."; continue; }
                if (!backend.IsConnected) break;
                // libslirp and capture APIs can omit wire padding. A physical external Ethernet frame still contains it.
                byte[] wire = packet;
                if (packet.Length < 60) { wire = new byte[60]; packet.CopyTo(wire, 0); }
                try { nic.QueueReceive(wire); }
                catch (InvalidOperationException error) { _errors[slot] = error.Message; }
            }
        }
    }

    /// <summary>Returns host connection metadata and explicitly enumerated bridge choices without opening a capture.</summary>
    public object GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? bridgeError = NetworkBackendFactory.GetAvailabilityError("bridged");
        var bridges = Array.Empty<HostEthernetAdapter>();
        if (bridgeError is null)
        {
            try { bridges = NetworkBackendFactory.ListBridgedAdapters().ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or
                System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { bridgeError = error.Message; }
        }
        return new
        {
            adapters = _machine.Profile.NetworkAdapters.OrderBy(n => n.Slot).Select(n => new
            {
                slot = n.Slot, model = n.Model, mode = n.Mode,
                connected = _backends[n.Slot]?.IsConnected == true,
                description = _backends[n.Slot]?.Description ?? "Cable disconnected",
                error = _errors[n.Slot] ?? _backends[n.Slot]?.Error ?? (n.Mode == "bridged" ? bridgeError : null),
                macAddress = n.MacAddress, ioBase = n.IoBase, irq = n.Irq, hostAdapter = n.HostAdapter,
                virtualLan = n.VirtualLan
            }).ToArray(),
            bridgeAvailable = bridgeError is null, bridgeError,
            bridgeAdapters = bridges.Select(n => new { id = n.Name, description = n.Description }).ToArray()
        };
    }

    /// <summary>Retires external queues and joins workers before the old machine can be disposed or replaced.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        List<Exception>? errors = null;
        for (int slot = 0; slot < 2; slot++)
        {
            if (_senders[slot] is { } sender && _machine.NetworkCards[slot] is { } nic) nic.Transmitted -= sender;
            try { _backends[slot]?.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            _backends[slot] = null;
        }
        if (errors is not null) throw new AggregateException("Network transports could not close.", errors);
    }
}
