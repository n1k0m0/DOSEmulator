// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Host-side Ethernet transport. Workers publish bounded copies; only the machine owner admits them to a NIC.</summary>
public interface IEthernetBackend : IDisposable
{
    /// <summary>English connection description for the host UI.</summary>
    string Description { get; }
    /// <summary>Whether the selected host transport is usable.</summary>
    bool IsConnected { get; }
    /// <summary>Last transport failure, without substituting a different networking mode.</summary>
    string? Error { get; }
    /// <summary>Queues one complete Ethernet frame without an FCS, never borrowing the caller's buffer.</summary>
    void Send(ReadOnlySpan<byte> frame);
    /// <summary>Removes one bounded owned frame, or returns false without waiting.</summary>
    bool TryReceive(out byte[] frame);
}
