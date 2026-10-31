# Realtime execution and host calendar

## C52r1: native GUI RTC input at the CPU-owner boundary

The GUI passes its explicit wall-clock input through MachineSession. The
software path keeps PcMachine.SetCalendar's existing full-boundary checks.
WHPX uses a stopped native device boundary between complete slices, retaining
shared RAM and the continuous active host-time origin. RTC input does not
publish a full RAM mirror or alter CPU/device time. Native observations and
device callbacks reject the input before either RTC or policy history changes.
Native snapshot guards still require coherent complete RAM.

This repairs the published C52 GUI's first periodic correction after about one
second. The old compiled owner check omitted the frontend clock call and missed
the failure; its replacement includes periodic corrections and the selected
host-calendar behavior after pause/resume and native load. Deterministic core
execution and strict loaders continue to read no host wall clock themselves.

## C44/C44b: original Windows idle driver and bounded host recovery

The limited [APM 1.1 CPU-idle BIOS](hardware/APM-BIOS.md) lets the original
Windows power driver execute real guest `STI`/`HLT` and wake on emulated IRQs.
This removes unnecessary idle CPU execution. It does not replace the Windows
timer or the RTC, and it does not accelerate a CPU-bound task by changing its
clock. The user's host-local-calendar policy after load and pause/resume remains
unchanged; the deterministic core and strict loader do not read host wall time.

The GUI explicitly selects five seconds of recoverable host lag, with the same
20-ms maximum execution slice. A halted CPU may wait one host millisecond for a
sub-millisecond outstanding target; a running CPU behind its target still waits
zero. Default callers retain the old 100-ms recovery bound. Explicit resume or a
longer host suspension rebases the origin. Speed still measures actual guest
time over monotonic host time, including remaining deficits.

Actual Windows95/98 idle and steady test-app samples reach approximately 100%;
the measured cold app launches remain 43.56% and 78.94%, respectively. These are
different workloads, not universal realtime or a controlled CPU speedup claim.
See [the measured scopes](PERFORMANCE.md). All changes remain local, the hourly
automation stays **PAUSED**, and existing user working disks are not test media.

## Windows starter and resume repair, C18

The user requested that the hourly job be paused and the accepted C15 sources be committed/pushed before these repairs. That checkpoint is `c94cf37ea50b6ce5013f2a38b95f4f5edc7cae7e`; the job remains **PAUSED**. The following repairs remain local. The previous `run-win95.cmd` delegated to frozen V12, so it did not load the later CPU optimizations or host-clock policy merely because those sources existed in the workspace.

C18 also fixes a coalesced pause/resume: the owner increments a resume generation only after a successful transition to running, and the frontend passes that generation to the calendar policy after pumping requests. A resume processed between two frontend observations now still supplies current host local time. Negative/stale generations are rejected, machine replacement rebinds the policy, and a failed calendar update does not consume the generation. The original four-argument API remains available. Core hardware and the strict snapshot loader still never read host time.

Both repaired private Windows copies have the English DOSE Tools helper installed at `C:\DOSETOOLS\DOSETIME.EXE` and its actual Windows Run registration. Fresh C18 starts with a probe-only disk containing no installer show the running helper's date/time updates. Original Windows reports confirm 64 MiB profiles, one PCI display device without the stale legacy devnode, and current local calendar fields. The 100-MHz/128-MiB case also passes the same clock and memory report. These controlled headless probes explicitly seed the RTC; the compiled frontend policy supplies periodic host correction in normal GUI use. No continuous accuracy guarantee, native UI/input/audio acceptance or general 1:1 performance claim is derived from those samples.

All **14074** registered regressions pass, including nine new clock cases, and the state generator remains46 types/895 members/40 unchanged callback bodies. Windows software time still requires the helper; a bare RTC correction alone does not keep every already-running Windows clock synchronized. The previous build-specific results below retain their original scope.

The subsequent genuine C15 Windows gate now passes at100/133MHz and32MiB: two new cold boots from fresh own media, original S3/SB16 drivers, full1027-byte DAT and512-byte proof,94620 exact inner RGB pixels with no exclusions, natural Wave-DONE/Unprepare/Close, application close and regular shutdown. Four strict full imports/72 whole comparisons cover desktop and Wave-DONE with identical later PCM bytes/timestamps; these are not new active-DMA or independent native-sample/mixer/host-audio tests. CPU instruction/interrupt observers are absent.

A private64-bit scheduler-budget arithmetic experiment passes10388 BigInteger inequality controls and three invalid-parameter cases per variant, with no state mutation. Eight serial, mirrored processes with200-million-step warmups and normal .NET tiers compare actual C15 Windows desktop data. Every complete state/RAM/VRAM/image/later-PCM boundary and final journal agrees. The experiment takes0.07% less host time at100MHz and1.81% more at133MHz, so it is not integrated. The unchanged C15 baseline reaches80.11/57.74% realtime in these specific long-warmed segments; differing warmup/source phases prevent calling the absolute difference from older cohorts another code gain. The earlier short pair is exploratory. This comparison uses unedited same-shape private state data, not additional strict product imports or snapshot migration.

All68 frozen C15 and65 common V12 products remain unchanged. There is no new product compilation or full regression run: the accepted13883 C15 cases remain their existing evidence. The host-calendar choice and Tools policy remain unchanged; universal1:1, M4/RTM/Tools-transfer/M5–M8 are still open. Evidence: `artifacts/tests/windows-c15-release-path.txt`.

The later C15 [real-mode CS admission check](hardware/DECODED-EXECUTION.md) is verified by13883 regressions and genuine observer-free DOS/FreeDOS/original-EMM386 scalar/resume comparisons. It preserves the actual data-tagged CS cache seen in FreeDOS and admits the existing decoded path without changing PE policy, CPU clocks or saved fields. Two actual FreeDOS boot ABBA cohorts show no reliable overall gain (0.25% less owner time at100MHz;6.58% more at33MHz with baseline variation). This remains a private candidate; At that DOS checkpoint Windows throughput was still untested; the subsequent Windows evidence above leaves the universal1:1 target open. The existing explicit host-local calendar choice after load/resume and DOSE Tools policy are unchanged. Evidence: `artifacts/tests/decoded-dos-release-path.txt`.

The later [C14 threading experiments](hardware/HOST-THREADING.md) retain an optional joined video worker, off by default. Pixel conversion improves about45% in the controlled RGB565 data; whole Windows CPU-plus-capture throughput does not show a reliable improvement. Audio/disk prototypes do not change production device timing or host endpoints. The universal realtime target remains open, and the C13 CPU gains below retain their separate build/cohort scope. Evidence: `artifacts/tests/threading-release-path.txt`.

Status: 28 October 2026. The C13 follow-up is retained locally; the general realtime target remains open. Evidence: `artifacts/tests/realtime-followup-release-path.txt`.

C13 adds prefix-free group-1 immediate ALU/CMP instructions against ordinary RAM (`80/81/83`) to the guarded decoder. Current addressing, segment/page permissions, dirty state, code aliases and complete instruction bytes are still checked. CMP remains read-only; code and data TLB hits/ages retain their scalar order. A read-only independent review found no concrete defect. All 13813 registered cases pass, including 77 new cold/warm, signed-immediate, SS/SIB, readonly/dirty, code-mutation, MMIO and fetch/data-boundary cases. CPU, tests and frontend builds have no warnings/errors. The existing debug-overlay switch is included in the private frontend.

Three new normal-.NET ABBA cohorts compare the same Windows work on C11/C13, with one process at a time on the same selected host core. Two 100-MHz cohorts take 3.33 and 3.13 percent less host time; the 133-MHz cohort takes 6.32 percent less. Each process performs the same five-million-step warmup and three twenty-million-step segments. Complete CPU/RAM/VRAM/device/scheduler/scanout/future-PCM evidence matches at every boundary. Loading, hashing and scanout are outside the measured execution segments. Current candidate realtime rates are 69.70/70.69 percent at 100 MHz and 54.32 percent at 133 MHz, limited to these desktop segments. Host conditions differ from the older C11/V12 cohorts below; the difference between their absolute rates is not another implementation gain. No whole-GUI, native audio or universal realtime claim follows.

A separate normal-.NET C11 sampling run on the same quiet Windows desktop places about 81 percent of sampled owner intervals in CPU paths and under one percent in mixer/OPL paths. Sampling includes overlapping call stacks, JIT/GC effects and one workload; it excludes GUI presentation and does not characterize active music or multimedia generally. It supports examining decoder admission/execution before undertaking a broad threading change. A four-way TLB-unrolling prototype is archived and fully reverted: its 0.84-percent average difference was too weak to retain.

C13 retains 46 state types/895 reviewed members/40 callback bodies. Only the CPU changes among the five VM modules, so format 1 rejects an actual C11 CLI snapshot before creating a branch. A fixed own CLI definition passes 10001 direct steps versus 10000 plus full save/load plus one step with byte-equal complete RAM/state and equal status/image. An earlier unbound built-in comparison had separately generated definition IDs; that diagnostic is excluded. There are no new DOS/Windows cold boots or original-driver acceptance in this follow-up. Actual DOS/EMM386 fast-path evidence is still required before shared-starter promotion; accepted V12 products and user working PCs stay unchanged.

## C11 reference and historical evidence

Status: 28 October 2026. Locally tested C11 candidate; the realtime target is still open.

The target is one emulated second per elapsed host second in every supported hardware profile. The selected CPU frequency, existing four-clock instruction accounting, device deadlines and guest instructions are preserved. The frontend's pacer waits when execution is ahead and stops waiting when it falls behind. Displaying 100 percent or changing the emulated CPU frequency would not establish sufficient execution throughput.

The requested checkpoint was committed and pushed first to the verified private repository as `197937aa3f5f74731deeaae2c99cccb7a94bef7b`. Subsequent optimization changes remain local. The latest audit from “Emulator-Funktionen zusammenfassen” reported no new functional defect. It identified CPU execution as the main Windows cost and unnecessary reads in S3 drawing operations.

The local execution loop now uses [guarded decoded batches](hardware/DECODED-EXECUTION.md), including supported RAM and stack instructions. Current code bytes, mappings and permissions are checked again; faults, observers and unsuitable instructions retain scalar execution. The TLB selects a replacement only after a miss, and S3 skips reads whose values cannot affect its existing pixel operation. Earlier slower batch/cache prototypes were rejected. None changes CPU frequency, guest work or nominal device rates.

The C11 build has 13736/13736 registered regression cases, no build warnings/errors, and 46 state types/895 reviewed members/40 unchanged event bodies. The guarded cache adds no authoritative CPU/snapshot fields. Fixed own PE32 CPU/RAM work takes about 54–55 percent less host time than scalar execution on the same candidate modules. A separate two-host-second pacing probe holds approximately 100 percent at 33/90/100/133 MHz with independently checked CPU counts, fractional clocks and PIT deadlines. This covers that own instruction mix and factory reset profiles, not every hardware workload.

Controlled V12/C11 Windows desktop ABBA comparisons with tiered compilation disabled retain complete CPU/RAM/device/scheduler/scanout/future-PCM equality. The tested 100-MHz segments take 20.7–34.0 percent less host time and the 133-MHz segments 21.5–22.2 percent less. The candidate still achieves only about 42–49 percent realtime in those segments. A separate ABBA cohort with the normal .NET environment, including its ordinary tiering/PGO transitions, also passes every complete-state comparison. Across the three equal-work segments it takes 24.0 percent less host time at 100 MHz and 21.1 percent less at 133 MHz, reaching approximately 52.3 and 39.8 percent realtime respectively. Individual normal-runtime phases range from 36.7 to 62.5 percent; no universal realtime result follows. S3 command microbenchmarks show 28.6/32.7/43.2 percent less host time for fixed 8/16/32-bit-storage solid fills; these are not Windows FPS or additional color-mode acceptance.

The frontend now measures actual virtual time divided by elapsed monotonic host time in complete host-second windows, including pacing waits. The window title shows `SPEED`, and the English `TIME AND SPEED` page controls host calendar synchronization. Native window/input verification remains open.

With synchronization enabled, startup, machine replacement, snapshot restore and resume explicitly supply the current host local calendar to the emulated RTC. Running execution receives a correction once per host second. A paused machine does not execute devices or periodically change its RTC. This implements the user's requested policy: loading and resuming adopt current host time. The low-level hardware and strict snapshot loader never read the host clock themselves; callers can still run deterministic machines without this frontend policy.

The RTC preserves its scheduler-relative calendar origin and subsecond state in snapshots. It supports BCD/binary and 12/24-hour reads. The new calendar validation runs before media branch publication. Guest calendar writes, alarm interrupts and complete historical RTC behavior remain separate limitations.

Windows 95 maintains its own software clock. The own [DOSE Tools clock helper](../guest-tools/windows95/README.txt) reads real emulated CMOS ports and uses Windows time APIs. Earlier `/once` runs verify complete proof files and successful API returns. The C11 real guest additionally installs the English package, runs its hidden timer and starts it automatically after a fresh 133-MHz boot without the installation disk. Passive own helper records show successive timer updates and successful calendar corrections at 100/133 MHz, with measured Windows-versus-RTC differences of 70/30/20 milliseconds. Nonzero Win95 BOOL results are preserved as returned, rather than required to equal one. CMOS has whole-second resolution; the helper checks its result within one second of the RTC target. These samples are not a continuous host-clock accuracy guarantee when CPU execution is behind.

The actual English Install/Cancel dialog and success message were viewed in the headless guest. An independent sealed-COW/FAT16/VFAT reader verifies `C:\Share` as a real directory and every byte of `C:\DOSETOOLS\DOSETIME.EXE` against the own 7680-byte package. Both Windows profiles shut down regularly with the helper installed. Existing-share-file preservation follows the implementation but was not newly exercised with a populated share folder. File import/export, clipboard sharing and host-folder mapping remain separate unfinished work.

Two new actual Windows cold boots, four strict full snapshot imports and 72 complete 32-MiB comparisons cover the desktop, running helper, autostart and active SB16 DMA. The own Win32 file/GDI/Wave program reaches natural DONE and closes through the real APIs; all 1027 DAT bytes, 512 proof bytes and 94620 inner RGB pixels pass without exclusions. During the active-DMA continuation, 480 later published stereo frames are byte- and time-equal. No PCM/WAV output file, host audio endpoint, new independent mixer reconstruction or additional native sample/ISR acceptance is claimed. An actual V12 CLI snapshot is rejected before branch creation with the existing nonzero CLR exception exit; format 1 is not migrated.

The accepted common starter and existing private Windows working PCs remain on frozen V12. C11 uses its own frozen build and fresh private media. Snapshot format 1 remains strictly bound to the actual five VM modules. M4, original RTM, native input/GL/audio acceptance, DOSE Tools file transfer and M5–M8 remain open. The hourly development task stays active. Private evidence: `artifacts/tests/realtime-hostclock-release-path.txt`. Licensed media, installed Windows disks and RAM captures remain private and ignored; the user's working Windows/play disks were not inspected or changed.
