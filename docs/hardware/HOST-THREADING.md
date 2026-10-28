# Host worker experiments

The later genuine C15 Windows runs now pass original-driver cold starts/shutdown and full desktop/Wave-DONE continuation at100/133MHz. Fresh desktop sampling again points to CPU execution/admission rather than another multimedia worker. A private exact scheduler-arithmetic experiment has no reliable long-warmed gain and is not integrated; all68 C15 and65 shared V12 products remain unchanged. The existing optional joined-video result below retains its limited scope, and no audio/disk worker or host endpoint is added. The universal1:1 target remains open. Evidence: `artifacts/tests/windows-c15-release-path.txt`.

Status: 28 October 2026. Private C14 candidate; shared V12 products and user PCs are unchanged. Evidence: `artifacts/tests/threading-release-path.txt`.

The subsequent [C15 CPU admission work](DECODED-EXECUTION.md) completes actual observer-free DOS/scalar/full-resume evidence and admits the inherited FreeDOS real-mode CS data tag. It adds no worker and retains the C14 video/audio/I/O conclusions below. C14 remains frozen; the separate C15 candidate changes only the CPU VM module and its regression assembly. No general realtime gain or shared promotion follows from its boot timing.

## Optional joined video worker

`VIDEO SETTINGS → MULTICORE VIDEO ON/OFF` selects a host-only scanout option. The default is **OFF**; the setting is saved as `parallelVideo` in the existing UI preferences. Small surfaces below 131072 pixels and single-processor hosts retain scalar conversion. The existing debug-overlay preference remains independent.

When enabled, the emulation owner lazily creates one sleeping `JoinedWorker`. The owner renders the upper rows while the worker renders the lower rows from the same stable register/VRAM image. The owner waits for completion, then composites the S3 cursor and publishes the completed surface. CPU emulation and device callbacks stay at their existing owner boundary during this operation. This parallelizes pixel conversion; it does not run a whole emulated video card independently of the CPU. OpenGL presentation remains on the UI thread.

There is at most one worker callback in flight. Both halves complete even when one throws; the original exception is propagated. Nested submissions and use after disposal are rejected. The callback is cleared after the join. Window shutdown stops the emulation owner first, then joins/disposes the video worker before releasing hardware/media. No callback, worker, queue or host event becomes guest snapshot state. Reader leases remain held through the later GPU upload.

The 27 video cases compare every pixel, frame metadata and every saved video member for VGA, ET4000 and S3 text/indexed/planar/direct-color forms, blanking, cache/blink phases and cursor composition. Five separate worker cases cover an actual different thread, complete output, failures, nesting, reuse and idempotent shutdown. The full candidate passes **13845/13845** regressions with zero build warnings/errors. These controls do not establish native input, OpenGL or host-audio acceptance, nor a new original-driver color mode.

## Video measurements and interpretation

The private Windows fixture uses the known approved OEM desktop at 800×600 RGB565. Every process has the same five-million-step warmup, three twenty-million-step segments and a completed capture after each 200000 steps. Motion cases inject the same bounded relative movement through the existing virtual PS/2 API; they never use Windows host input. Complete CPU/RAM/VRAM/device/scheduler/image/future-PCM evidence, including a digest of all intermediate images, is equal between scalar and joined modes. Hashing and fixture loading are outside measured intervals.

An initial four-way `Parallel.For` prototype roughly halved conversion cost but regressed one whole-pipeline cohort; it is not retained. The sleeping two-way worker avoids the pool's extra runnable work. Exploratory unpinned cohorts show substantial variation and are not accepted as general speedups. The more controlled cohort keeps the owner on host logical processor20 and the worker on22, with the same idle worker present in both variants; those affinity controls exist only in the private harness.

| Controlled cohort | Less pixel-conversion host time | Less CPU-plus-capture host time |
|---|---:|---:|
| 100 MHz, virtual mouse movement | 45.19% | 0.42% |
| 133 MHz, virtual mouse movement | 45.87% | -0.50% |

The idle control performs just one initial conversion and still varies by several percent in total CPU time. Therefore the roughly45% conversion reduction is the supported result; a whole-Windows throughput/FPS or 1:1 realtime improvement is **not** established. The option remains off by default. The owner still performs the guest work at the original clocks/deadlines; frame dropping, clock changes or artificial speed readings are not used.

## Audio and disk experiments

Host audio playback already consumes the bounded PCM ring on its own callback thread. An extra joined worker for the existing 480-frame gain/clamp/PCM16 conversion yields only1.06% less time in the controlled paired-CPU experiment. That is too weak to add another production stage. The probe uses synthetic float samples only in RAM; it opens no audio endpoint and writes no WAV/PCM file. It does not move SoundBlaster DMA/IRQs, OPL synthesis, mixer state or timestamped register changes to another thread.

The disk probe uses only a new own8-MiB RAW base and new private journals,32-sector host batches and2000 independent real CPU instructions per batch. All bytes, complete CPU/RAM/scheduler evidence and final journal bytes agree. Warm sequential reads show18.86% less combined host time with overlap; durable writes show15.69% more time. Each sector retains the existing `Flush(true)` contract. These are backend feasibility probes, not Windows file-copy or ATA/ATAPI driver benchmarks. No cache flush is used to claim a cold-disk result.

No disk worker is integrated. A future useful read path needs bounded requests, immutable input, generation checks, errors at the correct guest completion point, reset/shutdown handling and full snapshot continuation. The present proof does not authorize treating the pending host task as saved guest state or publishing an early virtual completion. Emulated audio/IDE behavior, host endpoints and the existing durability rules remain as before.

## Follow-up with actual ATA completions

The separate private experiment at `artifacts/tests/threading-ata-release-path.txt` now drives the real ATA controller through an own finite RAM program's OUT, IN and REP INSW instructions. It uses only a new own8-MiB protected RAW image. Its modified `ReadOnlyRawDisk` and one bounded speculative worker exist exclusively in the private prototype build; repository product sources, all68 C14 products and all65 shared V12 products remain unchanged. No disk-worker menu or default has been introduced.

Only immutable source bytes are eligible for lookahead. The existing controller callback still copies the demanded sector and changes DRQ/IRQ at its original virtual deadline. A failed speculative read is discarded; the original demand retries and reports any actual error at that deadline. A late host result stalls the owner there. The cache and thread are derived host resources, absent from the saved component graph, and shutdown joins the read before closing its file lease.

The final21 private cases pass77 whole-PC/RAM/device/scheduler comparisons, independently checking every requested payload byte. They cover1/2/32/128/256-sector commands, first/interior/last extents, speculative versus actual I/O failure with equal IRQ edge times, SRST followed by a different LBA, delayed host work and shutdown. Two actual strict full snapshots are captured in DRQ and next-sector BUSY while speculative work is deliberately blocked, then restored with a cold cache and continued identically. These are synthetic hardware fixtures, not DOS/Windows cold starts or a new registered regression suite. COW, bus-master DMA, ATAPI, media replacement/adoption and real Windows file copies are outside this experiment's acceptance.

Timing first used24 short-warmup processes; those results are exploratory. The accepted comparison adds24 serial processes with4096 identical warmup commands,2048 measured32-sector commands, normal .NET tiering and private owner/worker affinities20/22. It includes the unmodified C14 reader and an identical synchronous lookahead cache to distinguish batching from threading. Every whole state/RAM/payload digest and the executed instruction, sector and virtual-clock counts agree. The final unused speculative read is drained inside the timing boundary.

| Actual synthetic ATA workload | Synchronous32-sector lookahead: less host time than C14 | Worker: more host time than the same synchronous cache |
|---|---:|---:|
| 486 / 33 MHz | 8.84% | 10.39% |
| P54C / 90 MHz | 8.48% | 0.57% |

The one-sector control also makes the worker7.03% slower than the same cache. The supported lead is fewer source reads, not an additional I/O thread. The roughly25–30% initial batch figures are not substituted for the longer-warmup results. No worker is promoted, and no Windows throughput or1:1 realtime gain follows from these own RAW/PIO measurements. C14's video option, the C13 CPU path and all earlier guest evidence retain their existing scope.

## Snapshot and release boundaries

The generator still checks46 component types/895 members/40 callback bodies. Core, Hardware and Runtime module identities change; format1 remains strictly bound to the actual five VM modules. A real C13 CLI snapshot is rejected before branch creation. The new fixed own CLI case passes10001 direct steps versus10000 plus capture/load plus one step with byte-equal whole RAM/state and equal status/image. This is no additional DOS/Windows cold boot or strict Windows import acceptance. Shared V12 is unchanged; DOS/EMM386 fast-path promotion, actual original-driver/RTM work and M4–M8 remain open.
