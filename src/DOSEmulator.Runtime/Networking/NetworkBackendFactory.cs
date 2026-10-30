// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;

namespace DOSEmulator.Runtime.Networking;

/// <summary>Creates host transports only at an explicit frontend Connect boundary; normal factory/snapshot construction remains inert.</summary>
public static class NetworkBackendFactory
{
    /// <summary>The default never loads a DLL, opens an adapter/socket or restores a previous external connection.</summary>
    public static IEthernetBackend Create(NetworkAdapterProfile profile, bool explicitlyConnect = false, string? nativeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.CableConnected) return new Disconnected(profile.Mode, "The virtual network cable is disconnected.");
        if (!explicitlyConnect) return new Disconnected(profile.Mode, "Networking is disconnected. Select Connect to activate the configured transport.");
        return profile.Mode switch
        {
            "nat" => new SlirpEthernetBackend(false, true, nativeDirectory, 2 + profile.Slot),
            "host-only" => new HostOnlyLanBackend(profile.VirtualLan, true, nativeDirectory),
            "bridged" => new NpcapEthernetBackend(profile.HostAdapter ?? "", profile.MacAddress, true),
            _ => new Disconnected(profile.Mode, "The selected network transport is unsupported.")
        };
    }

    /// <summary>Checks installed runtime files without loading native code, opening endpoints or changing the selected mode.</summary>
    public static string? GetAvailabilityError(string mode, string? nativeDirectory = null)
    {
        try
        {
            if (mode is "nat" or "host-only") NativeSlirp.VerifyRuntime(nativeDirectory);
            else if (mode == "bridged") NativePcap.LibraryPath();
            else return "The selected network transport is unsupported.";
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or
                                      System.Text.Json.JsonException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        { return error.Message; }
    }

    /// <summary>Explicit local adapter discovery; no capture is opened and no default interface is selected.</summary>
    public static IReadOnlyList<HostEthernetAdapter> ListBridgedAdapters()
    { using var native = new NativePcap(); return native.Adapters(); }

    /// <summary>Parse only one complete unicast6-byte hardware address, without culture-sensitive or partial decoding.</summary>
    internal static byte[] ParseMacAddress(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (value.Length != 12 || !value.All(Uri.IsHexDigit)) throw new ArgumentException("A complete six-byte Ethernet MAC address is required.", nameof(text));
        byte[] bytes = Convert.FromHexString(value);
        if ((bytes[0] & 1) != 0 || bytes.All(item => item == 0)) throw new ArgumentException("A nonzero unicast Ethernet MAC address is required.", nameof(text));
        return bytes;
    }

    /// <summary>Non-native disconnected state used for cold construction and every strict snapshot import.</summary>
    private sealed class Disconnected(string mode, string message) : IEthernetBackend
    {
        /// <summary>Reports the requested mode without implying an active transport.</summary>
        public string Description => mode + " / disconnected";
        /// <summary>No host connection exists in this inert backend.</summary>
        public bool IsConnected => false;
        /// <summary>Explains the explicit connection step or unavailable mode.</summary>
        public string? Error => message;
        /// <summary>Discards a guest transmission while the external cable is absent.</summary>
        public void Send(ReadOnlySpan<byte> frame) { }
        /// <summary>Never synthesizes received frames for a disconnected cable.</summary>
        public bool TryReceive(out byte[] frame) { frame = []; return false; }
        /// <summary>Owns no native handle, worker or queued frame to retire.</summary>
        public void Dispose() { }
    }
}
