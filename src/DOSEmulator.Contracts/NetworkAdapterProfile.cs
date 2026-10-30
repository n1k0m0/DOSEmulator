// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Persisted guest NIC resources and an explicitly disconnected-by-default host connection choice.</summary>
public sealed record NetworkAdapterProfile
{
    /// <summary>Original ISA NE2000 model, separate from the PCI-compatible RTL8029.</summary>
    public const string Ne2000Isa = "ne2000-isa";
    /// <summary>Original RTL8029 PCI adapter, allowed only on the validated PCI-firmware boards.</summary>
    public const string Rtl8029Pci = "rtl8029-pci";
    /// <summary>Stable card position, independent from model, guest driver or connection state.</summary>
    public int Slot { get; init; }
    /// <summary>Exactly one implemented guest-visible NIC identifier.</summary>
    public string Model { get; init; } = Ne2000Isa;
    /// <summary>Explicit NAT, virtual host-only LAN or selected host-bridge policy; no mode silently substitutes another.</summary>
    public string Mode { get; init; } = "nat";
    /// <summary>Persistent uppercase locally administered unicast address; generated once when the user adds the card.</summary>
    public string MacAddress { get; init; } = "";
    /// <summary>One complete 32-port guest resource window; PCI and ISA resources never alias implicitly.</summary>
    public int IoBase { get; init; } = 0x300;
    /// <summary>ISA IRQ9/10, or the board's shared PCI PIRQA route on IRQ11.</summary>
    public int Irq { get; init; } = 10;
    /// <summary>Explicit cable preference; constructing or loading a profile alone never opens a host backend.</summary>
    public bool CableConnected { get; init; }
    /// <summary>Exact enumerated host-adapter ID for bridging, retained privately in the user's PC definition.</summary>
    public string? HostAdapter { get; init; }
    /// <summary>Named isolated virtual LAN used by the host-only backend.</summary>
    public string VirtualLan { get; init; } = "DOSE";

    /// <summary>Decodes only a complete canonical MAC, without generating or changing persistent guest identity.</summary>
    public byte[] GetMacBytes()
    {
        if (MacAddress is null || MacAddress.Length != 17) throw new ArgumentException("Use an uppercase six-byte MAC address, for example 02:44:4F:53:45:01.");
        byte[] result = new byte[6];
        for (int i = 0; i < result.Length; i++)
        {
            int position = i * 3;
            int high = Hex(MacAddress[position]), low = Hex(MacAddress[position + 1]);
            if (high < 0 || low < 0 || i != 5 && MacAddress[position + 2] != ':')
                throw new ArgumentException("Use an uppercase six-byte MAC address separated by colons.");
            result[i] = (byte)((high << 4) | low);
        }
        if ((result[0] & 3) != 2) throw new ArgumentException("A guest MAC must be locally administered and unicast.");
        return result;
    }

    /// <summary>Converts exactly one uppercase hexadecimal character; locale and permissive whitespace are excluded.</summary>
    private static int Hex(char value) => value is >= '0' and <= '9' ? value - '0'
        : value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;

    /// <summary>Rejects unknown modes, unstable identities and unsupported board resources before any backend can be opened.</summary>
    public void Validate(bool pciCapable)
    {
        if (Slot is < 0 or > 1) throw new ArgumentException("A network adapter must occupy slot 0 or 1.");
        if (Model is not (Ne2000Isa or Rtl8029Pci)) throw new ArgumentException("Choose NE2000 ISA or RTL8029 PCI.");
        if (Mode is not ("nat" or "host-only" or "bridged")) throw new ArgumentException("Choose NAT, host-only or bridged networking.");
        _ = GetMacBytes();
        if (Model == Ne2000Isa && (IoBase is not (0x300 or 0x320) || Irq is not (9 or 10)))
            throw new ArgumentException("NE2000 ISA requires I/O 300h or 320h and IRQ9 or IRQ10.");
        if (Model == Rtl8029Pci && (!pciCapable || IoBase is not (0x6000 or 0x6020) || Irq != 11))
            throw new ArgumentException("RTL8029 requires a PCI BIOS board, I/O 6000h or 6020h and shared PIRQA/IRQ11.");
        if (HostAdapter is not null && (HostAdapter.Length is < 1 or > 512 || HostAdapter != HostAdapter.Trim() || HostAdapter.Any(char.IsControl)))
            throw new ArgumentException("A host-adapter ID must contain 1 to 512 non-control characters without surrounding whitespace.");
        if (Mode != "bridged" && HostAdapter is not null)
            throw new ArgumentException("A host-adapter ID belongs only to bridged networking.");
        if (Mode == "bridged" && CableConnected && HostAdapter is null)
            throw new ArgumentException("Select a host adapter before connecting a bridged cable.");
        if (VirtualLan is null || VirtualLan.Length is < 1 or > 64 || VirtualLan != VirtualLan.Trim() ||
            VirtualLan.Any(value => !(value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '.' or '_' or '-')))
            throw new ArgumentException("A virtual LAN name must contain 1 to 64 letters, digits, spaces, dots, underscores or hyphens.");
    }
}
