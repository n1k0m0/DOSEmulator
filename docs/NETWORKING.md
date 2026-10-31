# Networking

DOSE provides two optional guest Ethernet adapters: **NE2000 ISA** and **RTL8029 PCI**. Each of the two adapter slots independently selects **NAT**, **Host-only** or **Bridged**. Adding a card through the menu leaves its cable disconnected until explicitly connected. A newer emulator alone does not install the guest's network driver.

## Configure a card

Open **MENU / NETWORK ADAPTERS / ADAPTER 1** or **ADAPTER 2**. Select the model, connection mode and resources, then choose **APPLY CHANGES / COLD RESTART** and switch **CABLE** on. Changes can be combined in a validated draft or discarded. Long menu pages scroll and accept clicks. The status row shows the actual connection result, including an unavailable backend or failed connection. A failed bridge never falls back to NAT or another host adapter.

Model, mode, bridge interface and resource changes use the existing configuration plan and cold restart. Cable changes run through the emulation owner's queue. Each new card receives a persistent locally administered unicast MAC address. The MAC survives saving/reopening the PC and changing its model. The **MAC** and **VIRTUAL LAN** menu rows open English value editors. Successful changes automatically update the bound saved PC, with conflict detection if another program edited that file. See [Settings and launchers](SETTINGS-AND-LAUNCHERS.md).

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

## Windows 95 safety-copy installations, 31 October 2026

The four Windows 95 root launchers now open explicitly approved safety copies
with the matching 26624-byte original RTL8029 miniport and Microsoft's TCP/IP
installed through normal Windows setup. DHCP and Windows logon are selected.
The main copy also received the missing original POWER.DRV/VPOWERD.VXD APM
drivers from the original Windows CD; those files were already correct in the
other three copies. Their installed bytes and SYSTEM.INI entries were checked
independently from sealed FAT/COW files.

Each copy passed an actual cold boot, normal Windows restart, real Winsock NAT
transfer and regular shutdown on the installed, unchanged C47r2 runtime. A finite
peer on host loopback independently verifies every byte of a 257-byte UDP echo
and 1027-byte TCP echo, including natural EOF. A separate sealed guest-file
verifier checks the real API results, payloads and guards. The main copy also
passes one strict C47r2 restore and 18 complete 64 MiB/state/VRAM/device/scheduler/
frame/timed-PCM future comparisons after networking is disconnected.

These are four current NAT guest installations. No Internet service or physical
bridge was exercised. Npcap is still absent on this host. The earlier controlled
Host-only/device/backend evidence retains its own scope. New copies keep their
original hardware, MACs and floppy bindings, while all four source PC files and
journals remain byte-identical. See the [copy/original mapping](SETTINGS-AND-LAUNCHERS.md#windows-95-safety-copies-31-october-2026).
Evidence: `artifacts/tests/win95-drivers-release-path.txt`.

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

## C47 settings and deployment check

All 13 root launchers now resolve the same immutable C47r2 runtime, including its
pinned native dependencies. Six controlled NAT/Host-only transport groups pass
on that installed set. Physical Bridged operation remains unavailable on this
host without separately installed Npcap; no physical LAN or Internet test is
claimed. See [Settings and launchers](SETTINGS-AND-LAUNCHERS.md).

Two actual private cold boots on the first C47 build exercise original Windows
95 A and Windows 98 SE RTL8029 drivers through real Windows Winsock and NAT.
Independent sealed FAT/COW reads verify all 257 UDP and 1027 TCP payload bytes
in both directions and natural TCP EOF. Both guests disconnect networking,
pass one strict full import and 18 complete state/RAM/VRAM/device/scheduler/
frame/timed-PCM comparisons each, then close applications and shut down normally.
The Win95 continuation uses 64 MiB and 40 million steps per side; Win98 uses
128 MiB and 320000 steps per side. These are completed-transfer cuts, without
an external connection restored by the snapshot.

Final C47r2 changes only Runtime's tested temporary-boot autosave binding relative
to that guest build. The other four VM modules are byte-identical. Its full
15275-case suite, six native transport groups and actual full CLI save/load
comparison pass separately; C47 snapshots retain their C47 identity.

Eight known Windows PC JSON definitions have text backups, RTL8029/NAT cables
enabled and unique local MAC addresses. Their media bindings and other hardware
remain unchanged. That 30 October metadata-only preparation did not install guest
drivers or open/copy working Windows disks/journals. The subsequently approved
31 October Windows 95 copy installations are described above; the originals
remain unchanged. Windows 98 working media were not part of that copy upgrade.
The private `artifacts/current/driver-media/rtl8029-win95a.img` contains the matching
26624-byte original miniport for the tested 950a family. Select it through
**NETWORK HELP / INSERT NIC DRIVER DISK**, then use Windows' normal driver setup.
Do not substitute the incompatible 25088-byte package. The original Win98 driver
works on the separate tested Win98 fixture. Driver installation in each existing
working PC remains distinct from the proven emulator/backend behavior.

An initial Win95 diagnostic used the known incompatible older miniport and is
excluded. One initial Win98 exchange timed out at the host peer while the guest
was paused; the final continuously serviced repeat passes on both sides. These
diagnostics are not extra accepted boots or accepted failed-network results.
Current release evidence: `artifacts/tests/settings-network-release-path.txt`.
