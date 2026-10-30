# Networking

DOSE provides two optional guest Ethernet adapters: **NE2000 ISA** and **RTL8029 PCI**. Each of the two adapter slots independently selects **NAT**, **Host-only** or **Bridged**. Existing PCs retain no installed network card; adding a card leaves its cable disconnected.

## Configure a card

Open **MENU / NETWORK / ADAPTER 1** or **ADAPTER 2**. Select the model, connection mode and resources, approve the normal cold restart, then switch **CABLE** on. The menu shows the actual connection result, including an unavailable driver or failed connection. A failed bridge never falls back to NAT or another host adapter.

Model, mode, bridge interface and resource changes use the existing configuration plan and cold restart. Cable changes run through the emulation owner's queue. Each new card receives a persistent locally administered unicast MAC address. The MAC survives saving/reopening the PC and changing its model. Custom MAC addresses and Host-only LAN names can be edited in the saved PC file while the PC is closed.

| Guest card | Resources | Availability |
|---|---|---|
| NE2000 ISA | 300h or 320h; IRQ 9 or 10 | 486 and supported Pentium boards |
| RTL8029 PCI | 6000h or 6020h; shared level IRQ 11 | Boards with DOSE PCI BIOS, including the Windows 95 boards |

Conflicting card, sound and MPU resources are rejected before opening media. Two RTL8029 cards can share their physical PCI interrupt route; ISA cards require distinct resources. These are implemented development models, without a claim of complete analog Ethernet, collision or every chip revision's behavior.

## Connection modes

| Mode | Guest configuration | Reachable systems |
|---|---|---|
| NAT | DHCP; adapter 1 uses 10.0.2.0/24 and adapter 2 uses 10.0.3.0/24 | Outbound IPv4 TCP/UDP through the host; the virtual gateway at `.2` reaches host loopback services. DNS is `.3`. |
| Host-only | DHCP on 10.0.2.0/24; host gateway 10.0.2.2 | Other DOSE cards with the same virtual LAN name and Windows user, including separate processes; host loopback services. No Internet routing or external DNS. |
| Bridged | Select the exact host adapter, then use the LAN's DHCP or static settings | The selected physical Ethernet LAN, subject to the host driver/interface and LAN policy. |

Host-only is a private Ethernet switch with a single DHCP/loopback gateway per named LAN. It also forwards non-IP Ethernet such as IPX between guests. It does not create a Windows host virtual adapter, install a service or change host routes/firewall settings. The coordinating DOSE process must remain open while the other guests use that LAN; its closure disconnects those peers. Reconnecting creates or joins a new isolated LAN. The switch supports at most 16 connected cards per named LAN, with bounded copied packet queues.

NAT uses a bundled, pinned Win64 libslirp runtime. It does not expose an inbound listener by default. The internal localhost-only port-forward API is not yet exposed in the menu. IPv6 networking, automatic SMB shares, file transfer, Remote Play and automatic host-service configuration are separate work.

Bridged requires a separately installed [Npcap](https://npcap.com/) with WinPcap-compatible library access. DOSE does not install its kernel driver. If Npcap is missing, the menu displays **BRIDGED: UNAVAILABLE / NPCAP REQUIRED**. Refresh status after installing it, select an explicit adapter and connect. Some wireless adapters/APs cannot carry a separate guest MAC in the same way as wired Ethernet; DOSE does not disguise this as a successful bridge.

## Windows 95 drivers

The original NE2000-compatible Windows driver is available on Windows 95 media. Configure the selected ISA address/IRQ when installing an ISA card. PCI detection uses the real RTL8029 identity `PCI\\VEN_10EC&DEV_8029`; Windows 95 may request a manufacturer disk.

For the tested original Windows 95 / 950a family, use the separate original **`win95a-8029.exe`** package in the [official D-Link driver archive](https://ftp.dlink.de/%40archive/neteasy/drn-32ct/rtl8029/). Its `RTL8029.SYS` is 26624 bytes. The archive's general `win95-8029(506).exe` contains a different 25088-byte miniport: the actual 950a NDIS provider lacks its `NdisDprAcquireSpinLock` and `NdisDprReleaseSpinLock` imports, and that combination reported Code 2. Version 5.06 here is a manufacturer version, not an NDIS 5 requirement.

Keep downloaded manufacturer drivers private; they are not source or firmware of DOSE. Install `NETRTP.INF` and the matching `RTL8029.SYS` through Windows, then add Microsoft's TCP/IP protocol and select automatic address assignment. Restart Windows after changing the miniport. Do not mix the two SYS files solely because their INF is identical.

## Pause, reset and snapshots

Only the emulation owner changes guest packet RAM, DMA latches, IRQ sources or device deadlines. Native transport workers exchange bounded owned frame copies; they never mutate guest RAM. Complete card state, including pending transmission/reception and the RTL EEPROM/autoload state, belongs to the full snapshot graph.

External connections and the remote systems' TCP/DHCP state cannot be rewound by a snapshot. A strict restore leaves external networking disconnected, even when its saved cable preference was on. Select **CABLE: ON** to establish a new connection; renew DHCP or reconnect guest applications when necessary. A hard reset closes the old connection. Queued cable actions and prepared configurations from a retired owner/reset generation are rejected.

Headless operation is inert unless explicitly requested with `--enable-network`. Reconnecting after a strict load additionally requires `--reconnect-network`. Ordinary component tests, machine factories and the strict snapshot loader never open a host connection on their own.

## Runtime preparation

`scripts/prepare-network-runtime.py --download --output artifacts/network-runtime/win-x64` downloads pinned official MSYS2 packages, verifies their archive hashes and actual PE dependency closure, and retains original license notices. The normal build copies the prepared runtime into each frontend's `native/win-x64` directory. There is no global DLL search-path change or system installation. See [third-party notices](../THIRD-PARTY-NOTICES.md).

Acceptance evidence and current limitations are recorded in `artifacts/tests/network-release-path.txt`. Physical Bridged operation requires a host with Npcap and a selected Ethernet interface; component tests and controlled local packet exchanges alone do not establish physical LAN compatibility.

## Accepted C19 scope

The frozen C19 build passes 14775 registered regressions, including 701 added since C18, with no build warnings or errors. Six separate native transport groups cover controlled loopback TCP/UDP, DHCP/ARP, shared Host-only Ethernet/IPX, separate processes and the 16-card limit. They do not connect to an external Internet service.

An actual FreeDOS boot with the original Crynwr NE2000 packet driver reads the configured MAC and exchanges a complete ARP request/reply. Two actual 100 MHz / 64 MiB Windows 95 A boots with the original RTL8029 miniport exchange every byte of 257-byte UDP and 1027-byte TCP payloads in both NAT and Host-only, including natural TCP EOF. Independently read sealed FAT/COW files bind the real guest API results and payloads; both Windows boots then perform normal shutdown.

After disconnecting external networking, both Windows runs pass a strict full import and 18 complete 64 MiB/state comparisons each. The DOS cut passes a strict full import with all 8 MiB/state bytes equal after 250000 steps per side. These are completed-transfer cuts. Pending transmit/receive, DMA-lane, ring, reset, EEPROM and shared IRQ futures also have separate permanent device/integration cases. No external TCP connection is claimed to survive rewind.
