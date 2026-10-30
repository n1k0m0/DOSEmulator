# APM CPU idle firmware

DOSE adds a separate original APM ROM for the existing Windows boards. This is a
limited APM 1.1 CPU-idle interface, not a complete power-management, chipset or SMM
implementation. Historical non-APM ROM payloads remain available and unchanged.

The guest's original power driver decides when its CPU is idle. The BIOS executes
real guest `STI`/`HLT` instructions and returns after a real emulated interrupt.
CPU frequency, four clocks per actual ordinary instruction, DMA, device deadlines
and interrupt delivery are unchanged. A busy application does not become faster
merely because idle support exists.

The real, 16-bit protected and 32-bit protected interfaces share ordinary guest
RAM at EBDA offsets `180h..185h`. The real connection follows the actual BDA EBDA
pointer, including DOS relocation. Protected-mode callers use the consecutive
descriptors returned by the APM connection service. These bytes are disjoint from
the disk/floppy bounce buffer at `200h..3FFh` and the existing video, mouse and
AH87 data. Snapshots preserve this RAM and the existing CPU halt state; no new
serialized component, callback or host timer is needed.

Implemented services cover installation and connection, disconnect, driver
version negotiation through 1.1, CPU Idle/Busy, enable/disable and engagement,
default restoration, online/no-battery status and current system power state.
Standby, suspend and power-off requests fail with the APM state-change error.
There are no invented battery events or successful unsupported transitions.

## Original Windows drivers

Windows installations made before the APM firmware was available may lack the
original power drivers. A Device Manager entry alone is insufficient. Microsoft
documents that the original Windows 95 installation needs the necessary files
and registration; retail Windows 95 may require Setup again. See
[Microsoft Q135136](https://jeffpar.github.io/kbarchive/kb/135/Q135136/).

In the final private 950a test, the actual Hardware Wizard reads the thin own
`APM95.INF` from a read-only FAT12 disk. Windows obtains original `POWER.DRV`
(1920 bytes) and `VPOWERD.VXD` (19669 bytes) directly from its OEM CD. The actual
source dialog must point to that CD's `WIN95` directory; the earlier aborted
source-navigation attempts are preliminary. Every installed driver byte is
independently compared with the original selected CAB member. The resulting
real `SYSTEM.INI` retains its previous drivers and adds `power.drv` and the
original `vpowerd.vxd`. Microsoft binaries are not distributed with DOSE.

After a normal Windows restart, the original VPOWERD calls the returned protected
BIOS entry with AX=`5305h`. The observed CPU is halted inside the actual ROM;
its DS points to the relocated EBDA, whose connection/version bytes are checked.
An initial strict full import of this running Windows cut passes 18 whole
64-MiB RAM/CPU/VRAM/device/scheduler comparisons and equal later PCM publications.
The original file/GDI/Wave test and normal application close also pass. These
were initially private C43 results. C44 also passes 18 whole 64-MiB comparisons,
actual file/GDI/Wave tests in 800x600 RGB565, application close and normal Windows
shutdown. Changing to that actual enumerated display mode returns the original
API's restart-required result; the normal OS restart reaches the new mode.
This is not native UI, GL or host-audio acceptance.

Original Windows98 SE also installs its own POWER.DRV (1920 bytes) and VPOWERD.VXD
(37523 bytes) through the real Hardware Wizard and matching original CD. Its
dynamic `DevLoader` connection is distinct from the static Win95 registration.
Full original file/GDI/Wave oracles and normal shutdown pass. A strict full
128-MiB active-DMA import runs two further seconds with every state field and
96000 later stereo frames equal. The shorter idle cut has 18 full comparisons;
the active two-second cut has four, because HLT advances to the requested time
before all requested instruction slices. Neither count is expanded artificially.
The Win98 acceptance used its original MACHINE.INF APM section; direct use of
the equivalent thin own `APM98.INF` is still experimental. The prepared test PC
already has the tested original driver installation and needs no additional
APM device. A fresh cold boot without any installation floppy reconnects that
driver automatically and subsequently reaches 99.989% in a ten-second idle
sample, then shuts down normally. Its original daylight-saving notice was
acknowledged through the guest Date/Time dialog; it was not treated as a freeze.

## Pacing work

The C44 frontend follow-up retains up to five seconds of short host stalls for
actual catch-up execution. Individual targets remain bounded to 20 ms. Long host
suspension and explicit pause/resume still reset the pacing origin. A halted CPU
may wait one host millisecond when its outstanding target is below one
millisecond; a running CPU behind its target keeps the existing zero-wait path.
This changes host scheduling only and does not advance virtual hardware itself.

The final Win95 idle sample is 10.0081093 host seconds for 10.0080707 actual
guest seconds, with 729 physical IRQ0 acknowledgements and 437.5 ms process CPU.
Windows98 idle reaches 100.005%, the open apps 99.992%/99.998%, and the actual
four-second Win98 active-wave interval 99.654%. All output samples remain in RAM.
Cold program launch is still slower: first-ten-second samples are 43.564% for
Win95 and 78.936% for Win98. See [all measured scopes](../PERFORMANCE.md).
General 1:1 under arbitrary active workloads remains open.

The full C44 suite passes 14912/14912 cases. C44b changes only the GUI recovery
choice from two to five seconds and adds one permanent test; its 317 host-filter
cases pass. The five actual C44/C44b VM modules are byte-identical. This is not
a claim that a new full 14913-case suite was run. No CPU or I/O research prototype
is promoted. The legacy nine ROM payloads retain their bytes; two new APM variants
are selected for the Windows boards. State inventory stays 48/957/43.
