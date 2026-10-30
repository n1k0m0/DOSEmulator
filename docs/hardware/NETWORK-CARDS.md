# NE2000 ISA and RTL8029 PCI

These optional development models share the implemented 8390 packet core while retaining different bus, PROM, transfer and diagnostic behavior. They are real guest devices accessed through guest port instructions and original drivers; they do not replace DOS/Windows networking or expose host files as guest files. See [network configuration](../NETWORKING.md).

## Device and resource contract

NE2000 ISA has 16 KiB packet SRAM at NIC addresses `4000h–7FFFh`, a duplicated MAC PROM, paged 8390 registers, remote byte/word DMA and the reset/data ASIC ports. The configured ISA resources are `300h` or `320h`, IRQ9 or IRQ10.

RTL8029 uses PCI identity `10EC:8029`, network class `020000`, a relocatable 32-port I/O BAR, IOSE and physical INTA. The board installs it at `00:08.0` and optionally `00:09.0`; the network firmware actually assigns `6000h`/`6020h`, enables I/O and routes shared PIRQA to level IRQ11. BAR probing, disabling and full-window conflict detection work; high addresses do not alias low ports. There is no advertised bus-master DMA, memory BAR or boot ROM. PCI DWORD data-port transfers remain distinct from ISA transfers. This is the original RTL8029 compatibility subset, without advertising AS-only identifiers or extensions.

The two network BIOS variants are selected only for PCs with an actual PCI network card. Previous NIC-free ROM payloads remain separate. The guest performs the PCI BIOS/configuration cycles; a host shortcut does not pretend that a missing device or resource assignment succeeded.

## Packet execution

Remote DMA transfers change actual NIC SRAM and retain prefetch/lane state across byte, word, odd-length and mixed-width accesses. On the ISA word board, a byte IN with DCR.WTS set acknowledges the whole prefetched word, returning its low lane and discarding the upper lane. The original Crynwr PROM identification loop relies on this behavior to read the logical MAC from the duplicated physical PROM. Byte DMA and the PCI byte-enable behavior remain separate; clearing WTS can preserve a previously prefetched upper lane. OUT and wider transfers retain their own contracts.

The receive ring uses its real PSTART/PSTOP, CURR and BNRY registers, wraps at the programmed boundary and writes status/next-page/count headers followed by packet bytes and FCS. Length includes FCS. Complete preflight prevents crossing the unread boundary; detailed electrical overflow/partial-DMA recovery is not claimed.

Unicast/PAR, broadcast/AB and multicast/AM with the 8390 CRC hash are separate receive decisions. Promiscuous mode bypasses the unicast comparison. W1C causes, interrupt masks, counter overflow and source acknowledgements are independent. Tally counters saturate at `C0h` in this model.

The host-facing frame is 14–1514 bytes without FCS. The card copies up to 64 pending ingress frames and drops new excess input. Frame time is `(8 + frame bytes + generated FCS bytes) × 800 ns`, with a 9600 ns interframe gap, on the existing virtual scheduler. Software is responsible for transmit padding. The external transport boundary supplies missing wire padding for short host API responses; explicit hardware tests can still deliver runts.

Internal diagnostic loopback completes through FIFO/status/PTX. It neither writes packet SRAM nor advances CURR nor raises PRX. FIFO position/count bytes and software-supplied bad CRC are checked separately: receiver CRC/RXE can accompany completed transmission. National word loopback follows its documented lane/`2*n` layout; the RTL byte-addressed word-loopback interpretation remains an explicit model inference. No electrical FIFO/collision/backoff, complete external-loopback or bus-arbitration conformance is claimed.

## Stop, reset and EEPROM

Software STOP rejects new work, retains STA and allows accepted in-flight frames to finish before RST. Hardware/ASIC reset cancels pending card work immediately while retaining packet SRAM and the cable input. Disconnect clears staged ingress; an active transmission retains its observed carrier loss across reconnect.

The Windows 98 installation exposed an incorrect normal-transmit completion in C21: a disconnected virtual carrier produced TSR=`10h` with ISR.TXE, although this model had simulated neither excessive collisions nor FIFO underrun. The original guest repeatedly entered its NIC reset routine after transmitting an ICMP router solicitation; input processing became extremely slow. A private same-build counterfactual changed only saved virtual cable presence and its matching profile preference, with no host backend or OS-code change; the queued timezone input then completed. That counterfactual is diagnostic evidence, not an accepted installation or performance measurement.

C22 reports the completed MAC transfer with PTX plus retained CRS (`11h`, ISR.PTX). The [National DP8390D datasheet, July 1995, TSR on page 24](https://www.elektronik.ropla.eu/pdf/stock/nsc/dp8390d_ns32490d.pdf) states that carrier loss does not abort transmission; ABT and FIFO underrun are separate causes. A disconnected or interrupted frame is still **never published to an external backend**, including after reconnect. Its original deadline, interframe gap, IRQ masks/W1C and software-STOP completion remain intact. Twenty added component cases cover both card mappings, three lengths, PTX/TXE masks, sticky status, next-packet recovery, active continuation and unchanged internal loopback: C21 with the same checker gives 2 PASS/18 FAIL, C22 gives 20 PASS. C22 passes 14845/14845 regressions; C23 preserves the contract in its 14883/14883 suite. Actual Windows 98 desktop, cold-start and shutdown checks are recorded in [the installation report](../WINDOWS98-INSTALLATION.md); its cable stays disconnected and no new external-network acceptance is claimed.

This is the existing model's normal virtual-carrier contract. It does not add electrical collision/backoff simulation or claim the medium-specific external-loopback tests of later RTL8029AS documentation. Connected normal frames, internal loopback and other explicitly aborted operations retain their separate behavior.

RTL has fixed read-only serial EEPROM contents, configuration-write gates and a nominal 2 ms autoload event. Programming mode gates DMA and new transmission; EEPROM programming is unsupported. The implementation does not advertise writable EEPROM, AS flow control or complete sequential programming behavior.

## Continuation and host ownership

Both cards are explicit snapshot components with all queues, packet buffers, byte/word latches, ring cursors, IRQ sources, deadlines, reset and EEPROM/autoload fields inventoried. The MAC and fixed resources are bound to the sealed profile. Network scheduler events carry only the existing checked operation identifiers; host sockets, named pipes, capture handles and workers are outside guest state. Import validates topology, resource windows, IRQ aggregation, buffer bounds and pending events before media adoption.

Native transport threads publish bounded owned frame copies. Only the machine owner admits those copies and changes packet RAM, scheduler or IRQ state, between CPU batches. Strict restore does not resume external endpoints. Cable commands have a separate network generation; configuration revisions also advance on a failed retirement that has already disconnected the card, so stale plans cannot reopen it.

## References and acceptance boundary

- National Semiconductor, *DP8390D/NS32490D NIC*, July 1995, sections 7–10 and 12: [manufacturer datasheet](https://gramlich.net/projects/datasheets/national/dp8390d.pdf).
- Realtek, *RTL8029AS*, LS009.0, 16 January 1997, sections 5–6 and 6.6: [manufacturer datasheet](https://people.eecs.berkeley.edu/~kubitron/cs194-24/hand-outs/rtl8029as.pdf). AS extensions are distinguished from its legacy compatibility statements.
- Original Crynwr packet-driver API/source and the original Realtek Windows 95 miniport are independent guest-driver checks, not implementation sources copied from another emulator.

Component, native transport and real guest evidence have separate scopes. The release report is `artifacts/tests/network-release-path.txt`; a native local packet exchange alone is not a DOS/Windows driver acceptance or physical Bridged LAN test.
