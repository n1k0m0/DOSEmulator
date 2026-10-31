# WHPX integration with C50 — local C51 candidate

The native backend is merged locally with C50. The selected 114-file C50 runtime
and all existing launchers retain their software CPU. WHPX currently has an
explicit experimental headless entry point; native GUI/input/GL/host-audio
acceptance is open. No existing user disk or original media is replaced.

## Clock and ownership

`WhpxPcSession.RunRealtime` maintains one monotonic host origin over native CPU
execution, I/O, MMIO, memory reconciliation, audio and observations. Device time
follows complete active elapsed time at exactly factor 1. Pauses are excluded;
HLT waits for real time. Private high-resolution waitable timers cancel bounded
native slices; no global `timeBeginPeriod` request is used. Other scale factors,
including the old branch's factor 64, are rejected.

The percentage measures clock following. It cannot establish game logic,
rendering or video throughput. Native instruction retirements are not available
from this backend. The profile frequency specifies its virtual P54C TSC; it
does not limit the host CPU's native instruction rate or make WHPX cycle-identical
to the software CPU's four-clock instruction model.

A session exclusively reserves its device graph. Start requires a fresh PC;
resume validates CPU topology/policy/profile before modifying a fresh graph.
Disposing the native CPU does not make its stale software register file
resumable. Software/native CPU migration is unsupported.

## RAM, devices and native exits

During a native execution window, aligned shared RAM is authoritative for both
the vCPU and DOSE devices. Bus reads/writes, DMA spans and the 430FX PAM peer use
that same backing. The complete managed RAM mirror is restored at public paused
boundaries. Default observers receive full reconciliation; an explicitly
`busWritesOnly` observer must use bus writes/notifications instead of mutating
the managed byte array. A20/DRB/hole mapping plans are bounded derived caches.
Executable ROM/PAM copies are reconciled separately. There is no whole-RAM
dirty-bitmap sweep at every native exit.

Scalar ports execute each original read/write with its exact width and side
effects. Input accumulator and RIP completion share one native register call.
String transfers retain the checked instruction-emulator fallback: all RAM
operands are validated before consuming ports, and completed I/O is never
replayed. Both legal ES/REP prefix orders are supported for protected OUTSW.
VGA and S3 MMIO still reach the actual DOSE bus. No unqualified read/write VGA
mapping, disk worker or audio worker was added.

Three narrowly verified flat-32 pixel-loop families can execute up to 32 complete
iterations per admission. Current code bytes, descriptors, debug/event/IRQ
state, addresses, RAM classifications and counter/code/lookup aliases are
checked. Every original pixel store, lookup, counter update and defined flag
effect is performed. Partial admission returns the exact completed-loop
boundary. There are no game addresses or media bytes in this implementation.
`DOSE_WHPX_VIDEO_BATCH=0` selects the same backend without this optimization
for controlled comparisons. Diagnostic iteration counts are derived data.

P54C CPUID, TSC/MSR and the existing historical V86 rule remain explicit.
Protected RDTSC permission reads CR4.TSD, independently of XMM3 storage.
Accepted PIC injection wakes a cached HLT even at a new owner-call boundary.
The PIT mode-zero one-shot keeps counting after terminal count without raising
another OUT edge; its restored callback follows the same rule.

## Native snapshots and command line

Native format 4 uses `.whpxsav`, a native register sidecar and separately typed
`DOSE-EXTERNAL-CPU-HARDWARE-1` backing. Loading validates the actual WHPX assembly,
all five VM modules, complete CPU topology/policy and media before branching.
Old native format 3 and different builds are rejected. The software format-1
CPU meaning is unchanged; its loader rejects native backing. There is no format
migration. The raw host TSC is excluded because guest RDTSC uses the saved
virtual TSC epoch and scheduler, not that advancing host register.

Full stopped CPU/RAM/device/scheduler state is captured. Native future
execution follows real host time and is not a deterministic instruction-count
replay. Equality at import and subsequent actual workload progress are checked
separately; no whole-future byte-equality claim is made for native execution.

The experimental headless runner accepts:

```
--engine whpx --machine <own-copy.dospc.json> --whpx-seconds 10
--capture-whpx-snapshot <new.whpxsav>
--load-whpx-snapshot <saved.whpxsav> --snapshot-branch <new.dosebranch>
```

It also supports explicitly requested reports, still images and media captures.
Its full-PC entry currently requires P54C and offline devices. Unsupported
software-step/network combinations fail explicitly. The 486 ID-flag/CPUID
discovery gap remains unadmitted for full DOS4GW PCs.

## Actual qualification and limits

The common C51 VM build passes 15467/15467 registered software regressions,
23 new groups over C50. Native tests use the real installed WHP API: complete
active-time accounting, HLT/IRQ, coherent CPU/device/DMA RAM, full native import,
ownership/invalid-import controls, eight ES/REP cases, four actual CPL3
RDTSC/IDT/TSS cases, twelve scalar I/O cases, 112 complete CPU/RAM/VGA loop
comparisons and 71 pure guard rejections. These native cases are separate from
the registered software total. The final whole-solution build has zero warnings
and errors; its native library is byte-identical to the guest-qualified R6
library. Source quality covers 950 code/project files, including 495 C# files;
state generation checks 48 components, 960 reviewed members and 43 callbacks.
There are 122 private candidate products. The exact 114 selected C50 products
remain unchanged.

The actual native CLI cold-starts its own tiny IDE guest, captures format 4,
loads it into a new branch and continues at the exact saved nanosecond. POST
remains one and reset count remains zero. A real older native cut is rejected
before branch creation with controlled exit code 1. The very short early-POST
CLI probes are diagnostic, not workload timing results.

On fresh own safety copies, the original Windows 98 player reaches 800×600
RGB565 and plays the own 18-second RLE8/PCM movie. A ten-second sample follows
the clock at 99.899%, observes 200 distinct valid source-frame indices spanning
21..220, and transfers exactly 110250 unsigned-8 mono samples at 11025 Hz.
An active native import matches the whole 128-MiB RAM, native CPU and hardware
state initially and continues real playback. Natural 18-second completion,
application close and regular Windows shutdown are observed. A sampled missing
frame index is not proof of lossless presentation; no native host audio or GL
claim follows.

The final restored Win98 serial five-second sample advances source indices
220..320 and transfers 55125 mono DSP samples, with a 99.831% clock ratio.
An earlier restored ten-second clock sample overlapped an audit and is excluded
from performance results. Whole movie-file checks cover the two final cold-run
seals and the restored Win98 seal; restored Win95 has no additional sealed-media
file-byte oracle.

Doom II reaches the original DOS4GW game with independent WAD HUD pixels and
active stereo SB16 DMA. The additional own DOS copy uses HIMEM `/TESTMEM:OFF`
to keep the 128-MiB cold RAM scan separate from active-game measurements; the
original `/TESTMEM:ON` fixture remains intact. The unoptimized native clock was near 100% while real
demo progress was only 185/700 expected tics. A byte-loop candidate reached
676/700 and the first combined loop candidate 679/700 over 20 seconds in their
recorded scenes. Gathered-pixel batching and scalar-port register completion
reach 698/700 tics (99.71%) with a 99.96% clock ratio, actual continuing stereo
DMA and 423 changed sampled images. Its final active native import initially
matches all 128-MiB RAM, CPU and hardware bytes, advances 175/175 expected demo
tics over five seconds, and reaches the natural DOS return. These different
scenes are not an equal-work
speedup factor. Universal 100% under load is not established; C50 remains selected.

The supplied Windows 95 OEM/950 copy still fails native cold start with a
protection error. A narrow real #DE trace identifies a calibration dividing
10000000000 by an elapsed-time delta of zero after a million-iteration empty
LOOP. This is a concrete fast-native-CPU timing incompatibility, not a missing
disc/driver hypothesis. This host rejects both native frequency-cap and CPU-cap
partition properties. An attempted timer-based empty-loop admission was not
reliable for loops finishing before host cancellation and was not integrated.
No time multiplier or intercepted DIV result masks the error. The unchanged
OEM/950 generation is preserved as this negative control.

An additional own safety generation was booted from the author's
[patcher9x v0.9.91](https://github.com/JHRobotics/patcher9x) floppy. Only `-select
speed` was applied, entirely inside the guest. Its log and independent sealed
FAT comparison identify changes to ESDI_506.PDR and SCSIPORT.PDR; VMM32.VXD,
WIN.COM and every byte of the own 498496-byte movie remain unchanged. No host
binary execution or other patch category was used. This is an explicit guest
compatibility patch, not a correction or silent replacement of original 950
drivers. The original generation remains usable with its C50 software CPU.

That speed-patched copy cold-starts natively to its original 800×600 desktop,
plays source frames 19..219 over ten seconds at 99.94% clock following, and
transfers exactly 110250 unsigned-8 mono samples. Natural completion at 18
seconds, application close and regular Windows shutdown are observed. Its
active native import initially matches all 64-MiB RAM, CPU and hardware bytes
and continues the same real video/audio workload. This limited qualification
does not establish unpatched Windows 95 WHPX compatibility or universal 100%.

Private evidence: `artifacts/tests/whpx-c50-release-path.txt`. Originals, personal
game journals and user working PCs are preserved. All changes remain local;
the recurring job remains paused.

API references: [native execution/cancellation](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvrunvirtualprocessor),
[physical mapping](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvmapgparange),
[partition properties](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvpartitionpropertydatatypes),
[GVA translation](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvtranslategva).
