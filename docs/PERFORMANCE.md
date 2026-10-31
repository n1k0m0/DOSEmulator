# Performance: gemessene Interpreter- und Geräteoptimierungen

## C47r2: Windows 95 driver copies, 31 October 2026

Four approved safety copies now have working original RTL8029/TCP-IP drivers
and the original APM drivers. The main copy received APM in this task; the other
three already had it. After each real network transfer and application close,
networking was disconnected for one stock-pacer idle sample: two host seconds
of warmup and ten measured host seconds, 100 MHz / 64 MiB, unchanged C47r2.

| Windows 95 safety copy | Simulated time / host time |
|---|---:|
| Main | 99.999774% |
| APM | 100.000567% |
| Load-test installation, idle | 99.999685% |
| Multimedia installation, idle | 99.995268% |

These are idle desktop samples, with actual APM halt behavior. They do not
measure a running load-test program or video playback. No CPU/device clock,
instruction work or deadline changed, and no new emulator optimization/build
was introduced here. Differences from older installations or measurements are
not a matched code-speedup result. Continuous 1:1 under games/video remains open.
All four copies completed normal Windows restart and shutdown. The installed
114-product C47r2 runtime remains byte-identical; its existing regression proof
is retained, without a new full-suite run.

See [safety copies and retained originals](SETTINGS-AND-LAUNCHERS.md#windows-95-safety-copies-31-october-2026)
and `artifacts/tests/win95-drivers-release-path.txt`.

## C47: shared launchers, settings and presentation, 30 October 2026

All 13 root launchers now use one immutable current runtime, C47r2, including
the previously measured C46 CPU/device optimizations. Selecting that runtime
does not replace an already loaded process or install Windows guest drivers.
The original PC disks and journals remain in place. Each of the eight Windows
PC configuration files has a text backup; its existing non-network hardware
and all media bindings are preserved.

The GUI owner now admits host frame conversion at 30, 60 or 120 Hz (default 60),
instead of attempting one capture after every execution batch. The first image
of a replacement PC is forced. Old host frames are not queued for catch-up;
guest execution, device/audio work, scheduler deadlines and reader leases are
unchanged. This is a frontend workload bound, with permanent admission tests.
No measured overall percentage gain is claimed for it; the headless probes
below are not a GUI-before/after comparison. Multicore video remains optional
and off by default, with its earlier limited pixel-conversion scope.

Fresh private network-check PCs using C47's actual S3/RTL8029 drivers give these
separate stock-pacer samples after two host seconds of warmup and ten measured
host seconds:

| Actual guest/work | Profile | Simulated time / host time |
|---|---|---:|
| Win95 A desktop, older network-test installation without APM | 100 MHz / 64 MiB | 77.954% |
| Win98 SE desktop with original APM driver | 133 MHz / 128 MiB | 100.027% |
| Win98 SE original Media Player, same own RLE8/PCM AVI | 133 MHz / 128 MiB | 43.477% |

The video measurement covers 4.347979670 simulated seconds in 10.000656600 host
seconds. Its 379 valid marker observations include 88 distinct source frames,
indices 14 through 101; SB16 DMA is active at the observed end, unsigned 8-bit
mono at 11025 Hz. The stock pacer reports one recovery. The application later
reaches its natural stopped state at 18.00 seconds, closes, and Windows shuts
down normally. Samples stay in RAM; no host audio endpoint or recording is used.

These are current absolute workload samples. Different guest installations,
warmup, phases and host conditions prevent treating their differences from
the C46 table as another code improvement. APM permits the idle guest CPU to
halt; it does not make a busy decoder or video codec run at 1:1. Continuous
100% under games/video remains open. No clock or device deadline was changed.

The Windows networking/continuation probes use the first C47 VM identity.
Final C47r2 adds only the tested temporary-boot autosave detachment in Runtime;
Contracts/Core/CPU/Hardware are byte-identical. Those earlier strict guest
imports remain associated with their actual build. The shipped C47r2 has its
own complete CLI save/load comparison, old-build rejection, 15275 regressions
and six controlled native network groups. No snapshot migration is implied.

See [settings/launchers](SETTINGS-AND-LAUNCHERS.md) and the private release
evidence at `artifacts/tests/settings-network-release-path.txt`.

## C46: profiled Doom II and actual Windows video, 30 October 2026

C46 promotes a bounded interpreter extension, with current code/segment/address/
permission/dirty/alias checks and the original four nominal clocks. Harmless
operand/address/segment prefixes, FE/FF INC/DEC, F6/F7 TEST/NOT/NEG, C6/C7 stores,
memory MOVSX/MOVZX, PUSH immediate or r/m, LEAVE and register SHLD/SHRD avoid
repeated scalar decoding. Exact-entry backedges replay only inside the original
scheduler budget. TEST retains its data-before-immediate TLB sequence; group-5
PUSH retains its full checkpoint and sequential displacement-byte fetches.
Constructing the restart record directly avoids an intermediate large copy.

The explicit VGA capability permits one simple mode-0 byte store only after
current aperture/bank/plane/raster/mask and S3 target/MMIO checks. Latches,
revision changes, full CPU device checkpoints and all other bus paths remain.
Both target and revision backing must be disjoint from retained code. Current
single-page DRAM proofs still reject mappings, holes, bad extents and A20/range
boundaries. No JIT, disk/audio worker, skipped instruction, changed CPU clock or
altered device deadline is promoted.

Passive actual-workload profiles place 97–99% of measured owner-plus-scanout
time in execution and 1–3% in scanout. Managed stack sampling includes inlining
and a separate gate wait; these are not exact cycle percentages. Instrumented
opcode counts identify memory counters and SHLD/SHRD in Doom and memory TEST/
PUSH plus 16-bit size prefixes in the original players. Instrumented passes and
earlier CLR compiler/lease/chaining experiments remain separate diagnostics.

Twelve final serial processes compare the frozen C45 with all five actual C46
VM modules. Each uses the same private host-core-20 affinity and normal .NET
tiers, with 200M Doom warmup steps or eight AVI virtual seconds, followed by four
40M-step or two-virtual-second sections. All nine initial and 45 later entire
CPU/RAM/VRAM/device/scheduler boundaries, images, timed PCM and final actual
journals agree. This same-data diagnostic replay is not a strict cross-build
snapshot import or format migration. Earlier prototype values are not the final
C46 performance figures.

| Matched actual workload | C45 mean host time | C46 mean host time | Less host time |
|---|---:|---:|---:|
| Original Doom II / 133 MHz, ABBA | 7.592371 s | 6.350515 s | 16.36% |
| Win95 original Media Player / AVI, BAAB | 19.891461 s | 18.408317 s | 7.46% |
| Win98 original Media Player / same AVI, ABBA | 20.588966 s | 17.434305 s | 15.32% |

These are observed means for these sections. Process spread is substantial,
especially on Win95, where one C45 run is faster than both C46 runs; these
figures are not a guaranteed improvement for every video phase or host load.
All individual values and phase spread remain in the private report. Private
affinity is a measurement control, with no production setting. These relative
results must be kept separate from the following actual unpinned stock-pacer
probes on four fresh cold branches. Each first probe has two host seconds of
warmup, ten measured seconds and a 60-Hz headless capture target:

| Actual C46 load | Profile | Guest time / host time |
|---|---|---:|
| Original Doom II DEMO1, PCM/FM | 486 / 33 MHz / 8 MiB | 100.050% |
| Same original timedemo, first measured phase | P54C / 133 MHz / 128 MiB | 100.076% |
| Later, heavier timedemo phase, 20 host seconds | Same P54C profile | 79.690% |
| Original Media Player, own RLE8/PCM AVI | Win95 / 100 MHz / 64 MiB | 43.547% |
| Original Media Player, same AVI | Win98 / 133 MHz / 128 MiB | 33.486% |

The later Doom result establishes that the first 100% window is not sustained
100% across the demo. Both Windows video probes remain far below 1:1. Their
three-row input-frame markers advance with real SB16 DMA; a stopped player or
idle clock is not accepted as a video result. The input remains the wholly
generated 18-second, 320×240, 20-fps MSRLE8/PCM AVI from the previous section.
Win95 retains one stopped original player behind its active instance; Win98
uses one original player. Natural 18-second completion and application close
are observed in both guests. The first short Win98 completion attempt stopped
at its host bound and was not accepted; a later complete virtual-time run proves
the natural end. No additional cold start is counted for that repeat playback.

Both Doom cuts bind 3591 independent original-WAD HUD pixels. Four actual
same-build SnapshotStore imports retain all fields through 72 whole comparisons:
8 MiB/8M steps per side for Doom33; 128/64/128 MiB and 40M steps per side for
Doom133/Win95/Win98. The two video cuts have active real DMA. Later PCM is only
hashed in RAM; there is no new independent full-mixer or native host-audio test.
Regular Windows shutdown reaches the original safe-to-turn-off screen in both
systems; controller exit alone is not counted as shutdown. All own owners exit.

C46 passes 15252/15252 registered regressions, including 283 added cases and
retained scalar futures. Eighteen old admission-denial expectations are updated
to the new supported encodings, without weakening the remaining guards. Final
builds have zero warnings/errors; source quality and state generation pass,
with unchanged 48 types, 957 members and 43 callback bodies. The actual CLI
compares 10001 direct steps with 10000 plus save/load plus one: whole RAM/state/
image and 36 other report fields agree. Exactly four capture UUIDs are checked
separately. A real old C45 import is rejected before a branch with the existing
nonzero CLR abort; no migration or controlled exit 1 is claimed.

`run-doom2-media.cmd`, `run-win95-media.cmd` and `run-win98-media.cmd` open
separate own C46 test copies with 114 frozen products, fresh journals and the
own movie. Existing PCs, launchers and loaded runtimes are preserved. No native
window/input/focus/GL/audio endpoint or recording is used during preparation.
Other codecs, Windows-native games/3D and universal 1:1 remain open. The paused
automation remains paused; there is no new commit or push.
Evidence: `artifacts/tests/games-media-opt-release-path.txt`.

## C45: original Doom II and Windows multimedia, 30 October 2026

The frozen C45 build at checkpoint `2d896b3` was additionally checked with
original Doom II and original Windows Media Player (`mplayer.exe`). These actual
applications do not reproduce the earlier own Win32 arithmetic-loop gains.

Four genuine cold starts use fresh private branches: two DOS/Doom profiles and
Windows 95 OEM/950 and Windows 98 SE. Doom runs its original IWAD `DEMO1` with
`-timedemo demo1` and SB16 PCM/FM. Each active scene matches 3591 independent WAD
HUD pixels. This unthrottled timedemo is a bounded game workload, not a measurement
of normal interactive game FPS or a historical CPU calibration.

The multimedia input is wholly generated: an 18-second, 320×240, 20-fps Microsoft
RLE8 AVI with synthesized unsigned-8-bit mono PCM at 11025 Hz. Every one of its
360 independently decoded RGB frames matches the generated input. The original
guest file functions copy it from an own RO FAT12 floppy to the guest hard disk;
the original players then actually play it with active SB16 DMA. No host audio
endpoint, recording or WAV/PCM output file is used.

Each following probe uses the production pacer, two host seconds of warmup and
ten measured host seconds, with a 60-Hz headless-capture target and ordinary
unpinned .NET execution. Percentages are measured guest time divided by host
time, not application FPS:

| Actual C45 workload | Profile | Guest time / host time |
|---|---|---:|
| Original Doom II DEMO1, PCM/FM | 486 / 33 MHz / 8 MiB | 100.083% |
| Original Doom II DEMO1, PCM/FM | P54C / 133 MHz / 128 MiB | 46.305% |
| Original Media Player, generated AVI | Win95 / 100 MHz / 64 MiB | 33.001% |
| Original Media Player, same AVI | Win98 / 133 MHz / 128 MiB | 25.088% |

The three lagging probes each report one pacer debt-anchor resynchronization;
their percentages still use the entire actual measured time difference. All
997 AVI observations have valid, agreeing three-row frame-number markers. During
the measured intervals the observed indices advance from 43 to 109 under Win95
and 41 to 91 under Win98. These observations establish continued rendering, not
complete delivery of every source frame. The Win95 cut retains a second stopped
original player behind the active instance. No native GL or host-audio result is
inferred from the headless probes.

Twelve separate serial comparison processes replay identical actual active data
under C44b and C45. Each variant has the same private host-core-20 affinity and
normal .NET tiers. Doom uses 200 million warmup steps and four measured
40-million-step intervals. AVI uses eight virtual seconds of warmup and four
measured two-virtual-second intervals, without injected input. Its five observed
phase images differ and subsequent PCM publication and instruction counts
continue to increase. All nine initial and 45 later whole CPU/RAM/VRAM/device/
scheduler boundaries, scanouts, timed PCM blocks and final actual journals
agree, without state-field exclusions. This diagnostic cross-build data replay
is not a strict product snapshot import or a format migration.

| Matched actual workload | C44b mean host time | C45 mean host time | Result |
|---|---:|---:|---|
| Doom II / 133 MHz, ABBA / 160M measured steps | 7.160896 s | 6.849115 s | 4.35% less host time |
| Win95 AVI, ABBA / 8 measured virtual seconds | 15.091835 s | 15.148075 s | No demonstrated gain; mean 0.37% higher |
| Win98 AVI, BAAB / 8 measured virtual seconds | 18.970661 s | 19.305230 s | No demonstrated gain; mean 1.76% higher |

The AVI individual values and host drift remain in the private report; these
small mean differences do not establish a general regression. Absolute rates
from the pinned, sparsely captured comparison runs must not be substituted for
the separate production-pacing probes. General 100% for games and multimedia
remains open. Windows-native games, DirectX/3D, other codecs, long play sessions,
regular game exit and new Windows shutdown acceptance are not established here.
Earlier pending Paint and player-menu attempts remain excluded diagnostics.

No product source or product binary changes in this follow-up. The accepted
14969 C45 regressions retain their prior result; there is no new full-suite run.
The five frozen VM modules in both frontends and the named own source files are
rechecked. The ordinary Doom launcher selects a different CPU/Core/Runtime DLL
set, so the numbers above describe the separate frozen C45 test branch. Personal
game COWs and working Windows PCs remain unopened. All own controllers exit;
that action is not a guest shutdown. Automation remains paused and no new
commit/push is performed. Evidence: `artifacts/tests/games-multimedia-release-path.txt`.

## C45: measured Windows CPU/RAM load, 30 October 2026

The guarded decoder now admits register shifts/rotates (`C0/C1/D0/D1/D2/D3`)
and signed IMUL (`69/6B/0F AF`). Longer ordinary decoded runs avoid repeated
scalar decoding and admission. The scalar shift helper and IMUL flag policy,
current code/operand/translation checks, four clocks and owner deadlines remain.
No JIT, worker, memory-form admission or frequency change is introduced.

An own Win32 program executes a ten-instruction register/RAM/ROL/IMUL loop in
real timer callbacks while retaining real file/GDI/Wave calls. A separate page
walker binds its counters and exact code bytes to actual CPL3/current CR3.
Eight serial mirrored processes compare C44b with the complete C45 build, with
normal .NET tiers, 200 million warmup steps and four measured 40-million-step
intervals per process. Private host-core affinity is identical in both modes;
there is no product affinity setting.

| Own mixed Win32 load | Old mean host time | C45 mean host time | Less host time |
|---|---:|---:|---:|
| Win95 OEM/950, 100 MHz / 64 MiB, BAAB | 5.419538 s | 3.914080 s | 27.78% |
| Win98 SE, 133 MHz / 128 MiB, ABBA | 5.330805 s | 3.021502 s | 43.32% |

All six initial and thirty later whole CPU/RAM/VRAM/device/scheduler comparison
boundaries, scanouts, published PCM and final actual journals agree. No state
fields are omitted. This cross-build diagnostic same-data restoration is not a
strict product import or migration. All individual times remain available; the
earlier two-process 42.75% Win98 pilot is separate and exploratory.

Two genuine C45 cold starts additionally load the own program through the real
FDC with original Windows/S3/SB16 paths. Production pacing and 60-Hz headless
captures give these bounded results:

| C45 mixed-load interval | Host duration / warmup | Guest time / host time |
|---|---|---:|
| Win95, first interval after its active-cut continuation | 10 s / 0 s | 100.002% |
| Win95, later steady interval | 20 s / 10 s | 100.000% |
| Win98, first interval after its active-cut continuation | 10 s / 0 s | 90.684% |
| Win98, later steady interval | 20 s / 10 s | 100.149% |

The last interval includes a small amount of actual catch-up. None resets a
pacing debt anchor; the initial shortfall remains in the report. These are not
matched old/new pacing comparisons. General 100% for boot, cold app launch,
games, graphics, sound, disk workloads or other hosts remains open. No native
GUI/GL or host-audio behavior is measured.

Each cold-started system passes a strict full import in its actual load loop
with active SB16 DMA: 18 whole comparisons, 40 million steps per side, all
64/128 MiB and devices/scheduler retained. Futures of 1.600000000/1.203007519 s
publish 76800/57600 identical timed stereo frames per side, without a new
independent DAC/ISR/EOI/mixer reconstruction. Both naturally reach Wave-DONE,
close through original APIs and reach their original safe-to-power-off screens.
Independent FAT16/FAT32 checks read all 1027 data and 512 proof bytes from sealed
own journals alone, without opening a RAW base. Three fixed central GDI
rectangles contain 36864 exact RGB pixels per system, with no exclusions inside.

C45 passes **14969/14969** regressions, including 56 new cases with 12586 internal
full-state/budget/denial vectors across both CPU models and seven modes. The
identical checker gives 14 PASS/42 FAIL on the old CPU: those are missing
fast-path admission, not old arithmetic faults. Builds have zero warnings/errors;
state generation stays 48 types / 957 members / 43 unchanged callback bodies.
Only CPU production source changes, although all five normally rebuilt VM files
have new identities. An actual old C44 snapshot is rejected before branching
with the existing nonzero CLR abort, without migration or a controlled exit 1.
The new CLI preserves whole RAM/state/frame and 36 other report fields for
10001 direct steps versus 10000 plus capture/load plus one; four capture UUIDs
and their actual metadata-file digests are independently bound.

Separate clean C45 copies use `run-win95-load.cmd` / `run-win98-load.cmd`, a frozen
114-file runtime and independent journals. Their own RO floppy provides
`A:\DOSELOAD.EXE -wave`. Existing PCs, launchers and loaded modules stay intact.
Preparation opened no native window; automation remains paused. Private media
stay excluded from Git. Evidence: `artifacts/tests/windows-load-release-path.txt`.
The inconsistent clock-only prototype and unbuilt cache-context experiment are
excluded; the earlier rejected CLR-JIT remains separate.

## Dynamic execution experiments, 30 October 2026

The private follow-up to checkpoint `5c910a2` tests generated CLR code with
register locals, arithmetic-flag liveness and individually guarded RAM accesses.
The existing complete code-byte/context checks, current data permissions,
dirty-bit checks, code-write aliases, TLB history, Step checkpoints and owner
deadlines remain mandatory. Unsupported instructions and control flow retain
the reference paths. This experiment does not implement native block chaining
or fault-based fastmem.

| Measured workload | Change in host time |
|---|---:|
| Own fixed CPU/RAM loop, four serial ABBA processes | −22.20% |
| Windows 95, actual boot interval, 100 MHz / 64 MiB, BAAB | +2.33% |
| Windows 98 SE, actual boot interval, 133 MHz / 128 MiB, ABBA | +16.76% |

Each Windows process executes 200 million warmup steps followed by four measured
40-million-step intervals, with normal .NET tiers and the same private host
processor affinity. These eight runs use fresh branches of the explicitly owned
clean C44 shutdown media. They cover bounded boot progress; they do not certify
another complete desktop/startup/shutdown or strict Windows snapshot import.
All initial states, thirty subsequent whole CPU/RAM/VRAM/device/scheduler
comparison boundaries, frame digests, published PCM blocks and final actual
journals agree. No state fields are excluded. This is not another active-DMA,
independent audio-mix, native GUI or host-audio acceptance.

The generated region covers only 0.0483% of measured Win95 elements and 9.5876%
of Win98 elements. Across warmup plus measurement it compiles 1400/5402 methods.
Short admitted regions and compilation/cache turnover are therefore important
follow-up targets; their individual contribution has not been isolated.
**The prototype is not promoted.** The 480 selected existing CPU cases plus
560 register and 280 RAM/ALU full-state differential vectors pass with forced
generated execution; these are private executions, not new registered tests or
a new full-suite run. The frozen C44b products and CPU sources remain unchanged.

The same investigation verifies Windows Hypervisor Platform availability through
the documented capability API and executes three finite own real-mode RAM
programs through an actual hardware virtual processor, obtaining the expected
counter registers and HLT exits. All private partitions are deleted. This is a
CPU feasibility result, **not a DOSE WHPX backend or Windows performance result**.
Device exits, the existing four-clock/REP timing contract, interrupts, historical
CPU behavior and complete snapshots still need a separate implementation and
validation. No Windows feature or host endpoint was enabled. API reference:
[Microsoft WHP](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/hypervisor-platform).

General 1:1 remains open. Private results and exploratory exclusions are pinned by
`artifacts/tests/jit-residency-release-path.txt`. The automation remains paused;
existing user machines, launchers and loaded modules are unchanged.

## APM und Host-Pacing, lokaler C44/C44b vom 30. Oktober 2026

Die originalen Windows-95/98-Energietreiber können über den neuen begrenzten
[APM-BIOS-Pfad](hardware/APM-BIOS.md) eine untätige CPU mit wirklichen
Gastbefehlen anhalten. Ein tatsächlicher emulierter Interrupt weckt sie wieder.
Das vermeidet die bisherige Windows-Leerlaufschleife, ohne CPU-Takt,
Instruktions-Clocks oder Gerätefristen zu verändern. Der GUI-Pacer bewahrt bis
zu fünf Sekunden Rückstand für wirkliche Nachholarbeit und wartet bei weniger
als einer Millisekunde offener HLT-Arbeit kurz auf dem Host. Laufende CPUs
bekommen bei Rückstand weiterhin keine solche Wartezeit.

Serielle Messungen mit dem tatsächlichen Produktions-Pacer, normalem .NET und
60-Hz-Headless-Bildabfragen ergeben folgende begrenzte Ergebnisse. Es werden
virtuelle Nanosekunden durch gemessene monotone Hostzeit geteilt; kein
vorgegebener Prozentwert und keine FPS-Hochrechnung.

| Tatsächliche Windows-Last | Profil | Gastzeit / Hostzeit |
|---|---|---:|
| 95 OEM/950a, Leerlauf nach Originaltreiber-Neustart, 10 s | 100 MHz, 64 MiB | 100,000 % |
| 95, geöffnetes Datei-/GDI-/Wave-Testprogramm, 10 s | 100 MHz, 64 MiB | 99,992 % |
| 98 SE, Leerlauf nach Originaltreiber-Installation, 10 s | 133 MHz, 128 MiB | 100,005 % |
| 98 SE, geöffnetes Testprogramm mit periodischem Dateischreiben, 10 s | 133 MHz, 128 MiB | 99,998 % |
| 98 SE, vier Sekunden ab tatsächlich aktiver SB16-DMA bis natürlichem DONE | 133 MHz, 128 MiB | 99,654 % |
| 95, erste zehn Sekunden eines kalten Programmstarts | 100 MHz, 64 MiB | 43,564 % |
| 98 SE, erste zehn Sekunden eines kalten Programmstarts | 133 MHz, 128 MiB | 78,936 % |

Das sind verschiedene reale Lasten, keine ABBA-Zusage eines CPU-Codegewinns.
Beim letzten Win98-Kaltstarttest verwirft der Fünf-Sekunden-Pacer keinen Anker;
der Rückstand bleibt trotzdem zunächst bestehen. Beim Win95-Start wird die
größere Grenze einmal überschritten. Auch ein gesonderter Win95-Bootabschnitt
bleibt deutlich unter Echtzeit. **Allgemeines 1:1 bei Boot, Programmstart oder
dauernder CPU-Last ist weiterhin offen.** Der Win95-Leerlauf verbraucht in seiner
Zehn-Sekunden-Probe 437,5 ms Prozess-CPU, Win98 1125 ms; das sind keine nativen
GUI-/GL-/Hostaudio-Messungen. Die zuvor verworfenen CPU-/I/O-/Audioprototypen
werden nicht übernommen.

Beide Windows-Versionen bestehen mit originalen S3-/SB16-Treibern, vollständigen
1027 DAT-/512 Proofbytes, je 94620 exakten inneren RGB-Pixeln, natürlichen
Wave-/Anwendungsschluss-APIs und regulärem Shutdown. Strikte volle C44-Fortsetzungen
vergleichen sämtliche 64 beziehungsweise 128 MiB und Geräte-/Schedulerzustände;
der aktive Win98-Schnitt führt zwei weitere Sekunden mit 96000 gleichen späteren
Stereo-Frames aus. Kein neues unabhängiges Sample-/ISR-/EOI-/Mixer- oder
Hostaudio-Oracle wird daraus abgeleitet. Nachweise und Bereitstellungsstand:
`artifacts/tests/windows-apm-realtime-release-path.txt`.

## Benutzerstarter C18 vom 29. Oktober 2026

Die Prüfung des tatsächlichen `run-win95.cmd` ergab, dass dessen privater Unterstarter weiterhin den eingefrorenen V12 lud. Die später geprüften C11–C15-CPU-Pfade sowie Debuganzeige, Videoworker und Hostuhrpolitik waren dort deshalb nicht enthalten. Die reparierten privaten Windows-Starter wählen jetzt den vollständig getesteten C18 mit diesen bereits belegten Optimierungen. Die bisherigen V12-Starter und ursprünglichen Benutzerplatten bleiben als getrennte Rückfälle erhalten. Neue Funktionsnachweise bei 100/133 MHz, 64 MiB und zusätzlich 128 MiB sind in `artifacts/tests/win95-usability-release-path.txt` gebunden.

C18 ergänzt RAM-Auswahl, Kalender-Fortsetzen und tatsächliche Original-S3-Farbpfade; diese Reparatur enthält keine neue vergleichende Gesamtzeitmessung. Frühere Gewinne gelten weiterhin ihren ausdrücklich benannten Lasten und Kohorten. **Ruckelfreie allgemeine Windows-Ausführung beziehungsweise 1:1 in jeder Konfiguration ist weiterhin nicht erreicht.** Die Debuganzeige ist in den reparierten Voreinstellungen ausgeschaltet; der Videoworker bleibt optional. Die stündliche Automation ist auf Benutzerwunsch pausiert.

## C15: tatsächliche Windows-Profile und verworfener Rechenprototyp

Zwei neue reale OEM-/950-Windows-Kaltstarts bei100/133MHz mit jeweils32MiB bestehen mit dem unveränderten C15, Original-S3/SB16, eigenen Datei-/GDI-/Wave-APIs und regulärem Shutdown. Vier strikte volle Fortsetzungen/72 ganze Vergleiche betreffen Desktop und Wave-DONE. Diese Funktionsprüfung ist getrennt von der folgenden Zeitmessung; sie fügt keine aktiven DMA-Schnitte oder unabhängige Hostaudio-/Mixerabnahme hinzu.

Passives .NET-Stacksampling im frischen100-MHz-Desktop findet ungefähr27,7Prozent der Produktintervalle im tiefsten sichtbaren `TryRunDecodedBatchCore`-Frame, je rund9Prozent im Owner und skalaren Step und7,5Prozent im Decodercache sowie Fetch. Inlinearbeit kann im aufrufenden Frame erscheinen; diese Gewichte sind keine exakten CPU-Zyklusanteile. Eine zusätzliche Bootaufnahme enthält andere Last und darf nicht als ruhiger Desktop verglichen werden.

Der private Versuch berechnet Fristintervalle bis`uint.MaxValue` exakt in`ulong`, mit unverändertem Rest-/Unterlaufschutz und ursprünglichem`UInt128`-Fallback. Ein unabhängiger schreibfreier Gegenreview und ein BigInteger-Ungleichungsoracle bestätigen je10388 Fälle einschließlich großer Grenzen, Frequenz-/Restwechseln, unveränderlicher Schedulerzustände und drei Parameterablehnungen. Das ist keine neue registrierte Produktsuite.

Die maßgeblichen beiden gespiegelten Kohorten verwenden je vier serielle Prozesse auf demselben privaten Hostkern20, normale .NET-Stufen, **200Millionen Aufwärmschritte** und vier gemessene Abschnitte mit je40Millionen Schritten. Alle sechs ganzen Zustands-/32MiB-RAM-/VRAM-/Bild-/späteren PCM-Grenzen und die Endjournale stimmen überein. Die Daten stammen aus den neuen echten C15-Kaltstarts; das manuelle unveränderte Wiederherstellen für diesen Vergleich ist ausdrücklich eine private Datenprobe, kein zusätzlicher strikter Produktimport und keine Migration.

| Aktuelle Windows-Daten | C15 Hostsekunden, Mittel | Privater Versuch | Weniger Hostzeit | C15 virtuell / real |
|---|---:|---:|---:|---:|
|100MHz /32MiB |7,989261 |7,983295 |0,07% |80,11% |
|133MHz /32MiB |8,333301 |8,483725 |-1,81% |57,74% |

Damit wird der Versuch **nicht übernommen**. Zwei vorherige kürzer aufgewärmte Einzelprozesse waren explorativ; ihr25-Prozent-Unterschied ist kein belastbares Ergebnis. Die aktuellen absoluten Raten gehören dieser längeren JIT-Aufwärmung und diesen Desktopdaten, nicht einem neuen Produktgewinn. CPUHz, vier Clocks, Fristen und ausgeführte Arbeit bleiben unverändert; allgemeines1:1 ist offen. Kein neuer Audio-/Diskworker und keine Produktionsaffinität. Alle68 C15- und65 gemeinsamen V12-Produkte bleiben bytegleich; Nachweis `artifacts/tests/windows-c15-release-path.txt`.

## C15: tatsächliche FreeDOS-Decoderzulassung

Der [C15-Abgleich](hardware/DECODED-EXECUTION.md) erlaubt den im tatsächlichen FreeDOS-Real-Mode-Gast erhaltenen CS-Datentag unter denselben unveränderten Grenzen/Breiten wie der skalare Fetch. C14 verwarf dort jeden schnellen Aufruf vor dem Cachezugriff.38 neue Kontrollen, kompletter Lauf13883/13883, unabhängiger Gegenreview und24 reale DOS-Kaltstarts einschließlich skalarer Kontrollen mit24 strikten Fortsetzungen bestätigen die Zustands-/Datei-/Audio-Gleichheit. Die eigentliche Beschleunigung bleibt eine Messfrage.

Zwei gespiegelte ABBA-Kohorten verwenden je vier serielle Prozesse, normale .NET-Stufen, denselben privaten Hostkern20 und je zwei echte separate Aufwärmstarts vor dem gemessenen dritten FreeDOS-Start. Öffnen, Hashen und Textabfragen liegen außerhalb der gemessenen Owner-Ausführung. Alle drei Starts jedes Prozesses sowie sämtliche Varianten erreichen bei gleicher Befehlszahl und virtueller Zeit denselben ganzen RAM-/Zustands-/Journalstand. Die8 gemessenen und16 Aufwärmstarts zählen getrennt von der DOS-Abnahmematrix.

| Eigener tatsächlicher FreeDOS-Boot | C14 Ownersekunden, Mittel | C15 Ownersekunden, Mittel | Weniger Ownerzeit |
|---|---:|---:|---:|
| P54C /100MHz /32MiB | 2,837813 | 2,830686 | 0,25% |
| 486 /33MHz /8MiB | 1,074330 | 1,145025 | -6,58% |

Die486-Baseline schwankt bereits zwischen0,963 und1,185Sekunden. Damit ist kein belastbarer allgemeiner Bootgewinn belegt. C15 stellt die gültige Zulassung her und bleibt ein privater Kandidat für weitere Profilierung; gemeinsamer V12 und alle Benutzer-PCs bleiben unverändert. Dieser damalige DOS-Schritt enthält keine Windows-/GUI-/FPS-/Pacing- oder allgemeine1:1-Abnahme; die anschließende Windows-Prüfung steht oben. Rohsound wird weiterhin ausschließlich im RAM beziehungsweise als Digest geprüft. Nachweise: `artifacts/tests/decoded-dos-release-path.txt`.

## Lokale Threading-Versuche vom 28. Oktober 2026

Die [anschließende echte ATA-Probe](hardware/HOST-THREADING.md#follow-up-with-actual-ata-completions) trennt zusätzlich den Effekt eines Lesecaches vom Thread. Nach identischem längerem Aufwärmen braucht synchrones32-Sektor-Lookahead im eigenen PIO-RAM-Programm8,84/8,48Prozent weniger Hostzeit als der unveränderte C14-Leser bei33/90MHz. Derselbe Cache mit Worker benötigt dagegen10,39/0,57Prozent mehr Zeit als die synchrone Variante.24 kontrollierte serielle Prozesse, ganze Zustände/RAM/Nutzbytes und unveränderte Befehls-/Sektor-/Clockzahlen;24 frühere kurz aufgewärmte Prozesse bleiben explorativ.21 separate private Kontrollen mit77 ganzen Vergleichen und zwei strikten vollen Snapshotfortsetzungen bestehen. Kein I/O-Worker wird integriert; keine neue Windows-Dateikopier-, DMA-/ATAPI-, DOS- oder allgemeine Echtzeitabnahme. C14 und V12 bleiben bytegleich; `artifacts/tests/threading-ata-release-path.txt`.

Der [optionale C14-Videoworker](hardware/HOST-THREADING.md) reduziert die eigentliche RGB565-Pixelumrechnung in den kontrollierten100-/133-MHz-Proben um45,19/45,87Prozent. Für CPU plus Capture ergeben sich bei festem Owner-/Worker-Hostkern nur+0,42/−0,50Prozent; ein Idle-Kontrolllauf zeigt größere CPU-Streuung ohne entsprechende Grafikarbeit. Darum keine allgemeine FPS-/Windows-/1:1-Zusage und Standard **MULTICORE VIDEO OFF**. Vollständige Pixel-/Zustands-/RAM-/spätere PCM-Vergleiche bestehen;13845Regressionen,32neue Fälle,0Warnungen/Fehler. Der erste Vier-Thread-Pool-Prototyp wird nicht übernommen.

Zusätzliche reine Hostblock-Proben: Audiokonversion nur1,06Prozent besser; warmer eigener RAW-Lesepfad mit32-Sektor-Blöcken und unabhängiger CPU-Arbeit18,86Prozent besser, dauerhaftes COW-Schreiben15,69Prozent langsamer. Es sind keine emulierten Soundkarten-/ATA-/Windows-Dateikopier-Abnahmen. Audio bleibt beim vorhandenen Callbackpfad, Disk-I/O beim bisherigen Produktionspfad; kein Durabilitätsvertrag wurde gelockert. Rohsound existiert nur im RAM. Quellen und alle Einzelwerte: `artifacts/tests/threading-release-path.txt`.

## Lokaler C13-Folgeschritt vom 28. Oktober 2026

Der bytegeprüfte Decoder übernimmt zusätzlich unmittelbare ALU-/CMP-RAM-Operanden (`80/81/83`) mit unveränderten Rechten, Faults, Codeguards, TLB-Zählungen, Flags und vier nominalen Clocks je tatsächlich ausgeführtem Element. 77 neue Fälle und die ganze Suite mit 13813/13813 Fällen bestehen; ein unabhängiger statischer Gegenreview meldet keinen konkreten Fehler. Kein neuer Gerätesthread oder Hostendpunkt wurde dafür eingeführt.

| Neue normale .NET-Kohorte, C11 → C13 | Weniger Hostzeit für gleiche Windows-Arbeit | C13: virtuelle Zeit / Hostzeit |
|---|---:|---:|
| 100 MHz, erste ABBA-Reihe | 3,33 % | 69,70 % |
| 100 MHz, zweite ABBA-Reihe | 3,13 % | 70,69 % |
| 133 MHz, ABBA-Reihe | 6,32 % | 54,32 % |

Jede Reihe besteht aus vier seriellen Prozessen, fünf Millionen Aufwärmschritten und je drei Abschnitten mit zwanzig Millionen Schritten. Sämtliche ganzen Zustands-/RAM-/VRAM-/Bild-/späteren PCM-Grenzen sind gleich. Hashen, Scanout und Laden liegen außerhalb der Ausführungszeit. Die Hostbedingungen dieser neuen Reihen unterscheiden sich von den historischen C11/V12-Messungen unten; absolute Raten über beide Kohorten hinweg sind kein zusätzlicher Codegewinn. Ein ungerollter TLB-Prototyp mit nur 0,84 Prozent instabilem Mittelwert wurde vollständig zurückgenommen. Das allgemeine 1:1-Ziel bleibt offen; Nachweise: `artifacts/tests/realtime-followup-release-path.txt`.

## C11 und frühere Messungen

## Audit und Echtzeitkandidat vom 28. Oktober 2026

Der aktuelle lokale [C11-Kandidat](REALTIME-AND-HOST-CLOCK.md) ergänzt [bytegeprüfte Decoderbatches](hardware/DECODED-EXECUTION.md), aktuelle RAM-/Stackprüfungen, spätere TLB-Ersetzungswahl und wirkungsgleiche S3-Lesevermeidung. CPU-Frequenz, vier nominale Clocks je ausgeführtem Element, Gerätefristen, Fehler-/Debugpfade und sämtliche gespeicherten Diagnosen bleiben erhalten. Frühere langsamere Prototypen sind keine Freigabe. 13736 registrierte Regressionen bestehen; Quellprüfung: 853 Code-/Projektdateien ohne Probleme. Der bestätigte Quellcheckpoint `197937a` wurde zuerst gepusht; diese späteren Änderungen bleiben lokal. Der gemeinsame V12-Starter ist unverändert.

Vier Prozesse je ABBA-Kohorte auf demselben Hostkern, Tiered Compilation nur dort aus, getrennte vollständige DLL-Sätze und identisches Messprogramm. Zeitfenster umfassen ausschließlich gleiche ausgeführte Gastarbeit; Aufbereitung, Hashes und vollständige Zustandsprüfungen liegen außerhalb. Drei feste 20-Millionen-Schritt-Segmente je Prozess auf den eigenen tatsächlichen Windows-Desktopdaten ergeben:

| Kontrollierte Windows-Probe | Weniger Hostzeit je Segment | Verbleibende Echtzeitgeschwindigkeit |
| --- | ---: | ---: |
| 100 MHz / 32 MiB | 23,5 / 20,7 / 34,0 % | etwa 48–49 % |
| 133 MHz / 32 MiB | 21,5 / 21,9 / 22,2 % | etwa 42 % |

Ganze CPU-/RAM-/Geräte-/Scheduler-/Bild-/spätere PCM-Zustände sind gleich. Die alte V12-Diagnosekopie erhält ausschließlich das einzeln geprüfte neue RTC-Ursprungsfeld mit Wert null; das ist eine private Vergleichsfixture, kein Produktimport oder Migrationsvertrag.

Eine getrennte vollständige ABBA-Kohorte verwendet normale .NET-Einstellungen: `DOTNET_TieredCompilation` und `DOTNET_TieredPGO` sind nicht gesetzt; ihre tatsächlichen Werte stehen im Bericht. Derselbe vollständige Zustandsvergleich besteht. Über die drei gleich großen Segmente zusammen ergeben sich:

| Normaler .NET-Betrieb, Windows-Desktopdaten | Weniger Hostzeit insgesamt | Virtuelle Zeit / Hostzeit, Kandidat | Tatsächliche Geschwindigkeit |
| --- | ---: | ---: | ---: |
| 100 MHz / 32 MiB | 23,97 % | 2,4000 s / 4,5916 s | 52,27 % |
| 133 MHz / 32 MiB | 21,05 % | 1,8045 s / 4,5360 s | 39,78 % |

Die einzelnen JIT-Phasen streuen: 21,1/21,3/30,3 Prozent weniger Hostzeit bei 100 MHz und 19,1/19,4/25,2 bei 133 MHz. Jeder Tabellenwert verwendet die Mittelwerte der beiden tatsächlichen A- beziehungsweise B-Prozesse, keine neue oder ausgelassene Gastarbeit. Die spätere normale .NET-Optimierungsstufe ist kein zusätzlicher Implementierungsgewinn. Die Werte sind auf diesen Hostkern und diese Desktopdaten begrenzt; das allgemeine 1:1-Ziel bleibt offen.

Die eigene ungepagte PE32-Register-/MOV-RAM-Schleife benötigt beim direkten Vergleich derselben C11-Module rund 54–55 Prozent weniger Hostzeit. Vollständige Zustands-/8- beziehungsweise 32-MiB-RAM-/VRAM-Grenzen und unabhängige Befehls-, Daten-, Bruchteiltakt- und PIT-Fristenoracles bestehen. Eine getrennte Zwei-Hostsekunden-Pacingprobe liefert bei 33/90/100/133 MHz annähernd 100 Prozent, ohne verworfene Rückstände aus der Gesamtrate herauszurechnen. Die Geräte stehen dabei im gültigen Factory-Resetzustand; dies ist keine POST-/Windows-/alle-Karten-Abnahme.

Ein eigener tatsächlicher S3-Portbefehlstest über jeweils 512 Rechtecke und 25165824 Pixel je Zeile vergleicht vollständiges Videozustandsabbild, alle 2 MiB VRAM, eigene RAM-Guards, Pixel und Revisionen gegen ein unabhängiges altes Byteoracle. Feste Solid-Fills in 8/16/32-Bit-Speicherformaten benötigen 28,6/32,7/43,2 Prozent weniger Hostzeit; überlappende Kopien reichen von praktisch unverändert bis 15,1 Prozent weniger. Die 32-Bit-Speicherprobe ist keine neue Originaltreiber-/Farbmodusabnahme.

Zusätzlich bootet C11 tatsächlich zweimal in Windows, setzt vier strikte volle Container fort und fährt beide Taktraten regulär herunter. Die englischen Tools, Dateibytes, GDI-Pixel und natürliche Wave-API-Rückkehr sind Gastnachweise, keine weiteren Zeitmessungen. Native Hostaudio-/GL-/Mausbedienung und ein universelles Echtzeitergebnis bleiben offen. Private Belege: `artifacts/tests/realtime-hostclock-release-path.txt`.

Der zusätzliche [Gemini-Abgleich](GEMINI-REVIEW-2026-10-27.md) bestätigt vorhandene RAM-/REP-/DMA-Spans und die synchronisierten Audiopuffer. Seine SIMD-/Dispatchfaktoren sind keine gemessenen Werte dieses Emulators. 801 gezielte Testausführungen und zwölf reine Puffer-Stressfälle sind Korrektheitsprüfungen, keine neue Hostzeit-/FPS-/Audioqualitätsmessung. Der 11563er-Produktcode bleibt unverändert.

## Erneute Auditprüfung vom 27. Oktober 2026

Der [11563er-Reviewfolgebuild](AUDIT2-RECHECK-2026-10-27.md) ergänzt zwei konkrete PIO-/Eingabekorrekturen und bestätigt die bisherigen Integer-/VGA-Optimierungen. Keine neue Hostzeitmessung oder Doom-FPS-Zusage. Größere Fetch-/Audio-/FM-/FSINCOS-/Hash-/Export-/JSON-Ansätze bleiben getrennte Profilaufgaben; der nächste Produktschritt bleibt DOS-CD-Treiber/MSCDEX und die Win95-Plattform.

## Zweite Auditfolge vom 27. Oktober 2026

Der [zweite Auditfolgebuild](AUDIT2-FIXES-2026-10-27.md) ergänzt ausschließlich die exakt geprüfte Int64→Float80-Konversion und feinere VGA-/ET4000-Pixelinvalidierung. Drei ABBA-Blöcke mit tatsächlichen Produkt-DLLs sparen bei 500000 Integeraufrufen 31594592 Heapbytes und in der festen Probe im Median 97,09 Prozent Hostzeit. Bei je 1000 CPU-only-GC-/Capturezyklen einer 320×200-Szene entfallen 1000 zusätzliche Render (98,05/98,13 Prozent weniger Probehostzeit bei DOSE VGA/ET4000). Ganze Pixel, CPU-Lesewirkungen, spätere echte VRAM-Writes und normalisierte ganze VGA-Zustände stimmen; VGA-Allokationen bleiben gleich. Keine allgemeine x87-/FILD-/Spiel-FPS-, GL- oder Echtzeitzusage. Größere Fetch-/Audio-/FM-/FSINCOS-/Exportideen bleiben gesondert offen. Alle folgenden historischen Werte und Aussagen über damals unveränderte x87-Dateien behalten ihren damaligen Buildbezug.

## Auditfolge vom 27. Oktober 2026

Der 8870er-Build behält nur die im [Auditfolgepaket](AUDIT-FIXES-2026-10-27.md) belegten Änderungen: 23,87–37,58 Prozent weniger Hostzeit in drei festen CPU-Mikroprogrammen (14 Proben je Variante, 300000→0 große Kontrollsicherungen pro 600000 Steps); kein zwingendes Sleep bei Rückstand; versionierte, fehlersicher bestätigte GPU-Uploadentscheidungen; OPL-Nulltests/Masken und wiederverwendete COW-SHA-Zielpuffer. Native/PCM-/Zustands-/Journalgleichheit bleibt geprüft. OPL-Rhythmusprobe spart gemessen 120/720 Heapbytes je OPL2/OPL3-Schreibzugriff; COW spart 56 Byte je geprüftem Record bei gleicher I/O-/Flushreihenfolge. Keine neue allgemeine Doom-/GL-/Disklatenz-/Echtzeitzusage.

Drei x87-Spanprototypen erreichten keinen stabilen positiven Durchsatz und wurden vollständig verworfen. Alle x87-/Float80-Quellen bleiben zum 8689er-Stand bytegleich; 88 Heapbytes je einfachem m64-Load bleiben offen. Vorläufige Messungen dieser Varianten sind keine Produktfreigabe. Die festen unpaged CPU-Messungen ignorieren nur den geänderten Kontrollsicherungszähler und einen explizit inaktiven alten Kontrollcache; Pagingdiagnosen können bei häufigerem Immediate-Fastpath abweichen. Weitere Profil-/Optimierungsvorschläge bleiben getrennt offen.

## Vollständige Zustandssicherung vom 26. Oktober 2026

Die [Snapshot-Koordination](SNAPSHOTS.md) ergänzt benannte Schedulerereignisse. Deren Parameter werden erst auf ausdrückliche Erfassung hin in JSON kodiert, nicht bei normalen Sample-/Timeraufrufen. Der CPU-Ownerguard gilt je Ausführungsabschnitt; Fetch, REP-Fristen, GPR-/Fault-/Task-/Debugsicherungen und die vier Entwicklungstakte je Step/Element bleiben erhalten. Audio-Integratoren/Filterreste und angefangene PCM-Blöcke werden gespeichert, nicht für den Snapshot übersprungen oder vorzeitig geflusht.

Exakte Original-/Restaurierungsvergleiche unter DOS/EMM386 und im laufenden Doom-Spiel prüfen Zustände, virtuelle Zeit und PCM. Sie sind keine neue Hostzeit-/FPS-Messung. Erfassung ist synchrone Hostarbeit; vollständige Basishashes, Journal-Kopien, unkomprimierter RAM und JSON begründen keine zusätzliche Latenz-/Platzersparniszusage. Die folgenden historischen Messwerte behalten ihre dokumentierten Builds.

## BIOS-Review vom 26. Oktober 2026

Nach dem parallelen Review wurden BIOS-AH87-REP aus unveränderlichem ROM und echte ATA-Mehrsektorbefehle optimiert. Die PIO-Schleife entscheidet ihre Richtung je Sektor statt je Wort. Zusätzlich sind Caps+Shift, der Mitternachtszähler und FDC-Timeout-/Abbruchpfade korrigiert. Das BIOS besitzt eine eigene Identität und POST-Startanzeige. [Vertrag und Verifikation](hardware/DOSE-BIOS.md).

Der anschließende 5276er-Reviewnachtrag korrigiert ungerade ATA-Speicherwörter über `FFFFh`: ein seltener Sektorpfad teilt nur die zwei Speicherbytes, während der Porttransfer ein Wort bleibt. Die gewöhnlichen Wortschleifen enthalten weiterhin keinen zusätzlichen Grenzvergleich je Wort. Das ist eine funktionale Fehlerkorrektur mit 100 neuen Regressionen und acht eigenen DOS-Kaltstarts, keine neue Performance-Messung oder Doom-FPS-Zusage. Die folgenden Messwerte gehören unverändert zu ihren dokumentierten früheren Builds.

Baseline ist der vollständige 5101er-, Kandidat der vollständige **5175er-Build**. Vier getrennte Prozesse in AB/BA-Reihenfolge, je sieben Messungen nach Warmup: **14 Proben pro Variante und Dienst**, normaler .NET-10-Host ohne deaktiviertes Tiered Compilation. Kein weiterer hier gestarteter Gasttest läuft während der Messung. Die Stopwatch umfasst nur die festen BIOS-Aufrufe; Erzeugen/Booten der privaten Maschine und Prüfen der Nutzdaten liegen außerhalb. Beide Varianten verwenden dasselbe Benchmarkprogramm und jeweils ihren vollständigen DLL-Satz. Die SHA-256-Nutzdatenprüfungen stimmen überein.

| Feste tatsächliche BIOS-Arbeit | Vorher, Median | Nachher, Median | Weniger Hostzeit |
| --- | ---: | ---: | ---: |
| INT 15h/AH87, 32 × 64 KiB kopieren | 81,5531 ms | 1,7479 ms | 97,86 % |
| EDD lesen, 8 × 128 Sektoren | 88,2852 ms | 48,5286 ms | 45,03 % |
| EDD schreiben, 8 × 128 Sektoren | 86,2308 ms | 49,2665 ms | 42,87 % |

Der isolierte AH87-Durchsatzfaktor von **46,66×** entsteht durch das Bündeln der bestehenden REP-Elemente aus ROM; **1.053.344 Instruktionen und 127.678.061 virtuelle Nanosekunden** bleiben exakt erhalten. ATA führt tatsächlich weniger Firmwareinstruktionen aus: Lesen 2.199.392 → 1.374.448, Schreiben 1.980.248 → 1.380.864. Hier ändern sich die virtuellen Dauern folgerichtig. Keine Vorgabe identischer RAM-/CPU-Gesamtzustände zwischen verschiedenen Firmwareversionen. Keine Host-BIOS-/DOS-Abkürzung, kein schneller laufender virtueller Takt.

Diese Messungen betreffen BIOS-Speicherkopien und BIOS-Plattentransfers mit eigenem flüchtigem Speicherbackend, **keine entsprechende Doom-FPS-Steigerung**, keine COW-Dateisystemmessung und keine historische 486/33-Kalibrierung. Acht neue originale Doom-Regressionen mit/ohne Ton und 69 weitere kontrollierte DOS-Starts sichern den finalen Build funktional ab. Der frühere eigentliche CPU-/Doom-Paarvergleich bleibt dem unten genannten 5101er-Build zugeordnet und wurde hier nicht pauschal wiederholt.

Reproduktion: wie unten den vollständigen Benchmarkordner erstellen, dann `dotnet <Benchmarkordner>/DOSEmulator.Benchmarks.dll bios 7`. Einzelmessungen, Checksummen, virtuelle Dauer und Instruktionszahlen sind im privaten Gesamtbericht über `artifacts/tests/bios-review-release-path.txt` verlinkt. Produktiver Starter und geprüfter Kandidat besitzen denselben vollständigen Release-Satz.

## Erneute Doom-Prüfung vom 26. Oktober 2026

Baseline ist der vollständige **5025er-Build nach ANSI-/SB16-Korrektur**, Kandidat der **5101er-Build**. Ein neues Samplingprofil des originalen Doom mit CT1740-PCM/OPL3 bestätigt den Interpreter als Hauptaufwand: `Step` liegt einschließlich seiner Aufrufe bei rund 79 Prozent der ausgewerteten verwalteten Produktstacks, `FetchByte` bei rund 16 Prozent. Inlining ordnet Arbeit teilweise dem Aufrufer zu; diese Werte sind keine isolierten Kosten einzelner Quellzeilen. Speicherbus, Paging, Decoder, Zustandssicherung, Scheduler, Video, Audio und Host-Pacing wurden gegen die vorhandenen Optimierungen geprüft. Der belegte neue Ansatzpunkt ist der CPU-Pfad; die bereits geprüften Geräte-/REP-/Pufferoptimierungen werden erhalten.

- Befehlsbytes aus gewöhnlichem RAM benötigen eine gemeinsame Klassifikation und Leseoperation. ROM, partielle Geräteseiten, offener Bus und Gerätecallbacks behalten den geordneten Buspfad einschließlich Kontrollsicherung.
- Tatsächlich angeforderte 16-/32-Bit-Immediates und Displacements können innerhalb derselben linearen Seite, des CS-Limits und der 15-Byte-Befehlsgrenze eine Übersetzung teilen. Es gibt keinen spekulativen Vorabruf oder Cache dekodierter Werte. Seitengrenzen, Beobachter und bereits gesicherte Geräte-/Kontrollpfade bleiben sequenziell. Ein bereits übersetztes erstes Byte wird im Fallback genau einmal gelesen.
- Segment- und TLB-Rechteprüfungen lesen ihre Cacheeinträge per Referenz; die kleinen Prüfungen dürfen vom JIT eingebettet werden. Berechtigungen, Fehlerpriorität, TLB-Ersetzung und Pagewalks bleiben erhalten.
- Die ALU sammelt CF/OF/AF beziehungsweise SZP lokal und schreibt die zusammengehörigen EFLAGS-Bits gemeinsam. GPR-Restartsicherungen, Taskgeneration, REP-Grenzen und virtuelle Entwicklungstakte werden nicht abgeschwächt.

**Zwölf abschließende Kaltstarts mit Ton**, je drei pro Variante/Konfiguration, abwechselnd AB/BA/AB, normale .NET-10-Konfiguration auf demselben Windows-/AMD64-Host. Neue private COWs einer tatsächlich installierten DOS-/Doom-Kopie; originale EXE/WAD und die eigene 32-Tick-Demo sind unabhängig in FAT geprüft. Die Stopwatch umfasst den vollständigen Headless-Prozess samt Start, Bildaufnahmen, WAV und 8-MiB-RAM-Export. Kopieren und Oracles liegen außerhalb; während der Zeitmessung laufen keine weiteren hier gestarteten Gasttests.

| Konfiguration mit PCM/FM | Baseline, Median | Kandidat, Median | Einordnung |
| --- | ---: | ---: | --- |
| HIMEM, Referenz-VGA | 17,320 s | 17,411 s | Kein belastbarer Vorteil; Kandidat 0,52 % höher |
| EMM386/RAM mit ANSI, Referenz-VGA | 32,759 s | 29,104 s | **11,16 % weniger Hostzeit** |

Die HIMEM-Einzelwerte reichen von 17,236–19,098 s vorher und 13,600–20,726 s nachher; die Streuung erlaubt dort keine Beschleunigungszusage. EMM386/RAM liegt bei 32,017–37,152 s vorher und 28,812–29,631 s nachher. Drei Wiederholungen und eine kurze Demo begrenzen die Aussage; das sind keine allgemeinen FPS-Werte oder langen interaktiven Spielsitzungen.

**Alle 8 MiB Gast-RAM, sämtliche aufgenommenen Pixel, PCM-Bytes, SB16-/OPL3-Zustände, virtuelle Endzeiten und der gemeldete CPU-Zustand außer TLB-Hit-Aufrufstatistik stimmen je Konfiguration exakt überein.** 360 WAD-geprüfte Bilder und 19017144 Stereo-PCM-Frames über beide Varianten; bei den Kandidaten allein 180 Bilder und 9508572 Frames. Weniger Übersetzungsaufrufe verändern den diagnostischen TLB-Hit-Zähler, nicht Pagewalks, Fehler oder den architektonischen Ablauf. ANSI-Konsole wird am tatsächlichen VGA-Bild statt an einem nicht garantierten BIOS-/E9-Echo synchronisiert. Die im Gast gemessene Zeit dieser Probe bleibt ausdrücklich gleich.

**5101/5101 Regressionen**, 76 neue Fälle/Gruppen. Ein unabhängiges Zahlenoracle prüft 1179648 tatsächlich dekodierte ALU-Fälle: alle Bytepaare beider Carry-Eingänge sowie Wort-/Doppelwortgrenzen und deterministische weitere Eingaben. Fetch-Vergleiche prüfen unter anderem CS-/Seiten-/Befehlslängengrenzen, PF-Restart, RAM/MMIO, A20, Beobachter, reale/V86-Operandgrößen und Selbstmodifikation. Der reine RAM-Bytezugriff wird gegen partielle/ganze Gerätemappings geprüft, ohne diese beim erfolglosen Versuch zu berühren.

Zusätzliche Mikrobenchmarks vergleichen identische feste CPU-Schleifen mit unveränderten Prüfsummen. Getrennte Varianten und 14 Messungen pro Variante bei nur dort deaktiviertem Tiered Compilation/ReadyToRun zeigen Vorteile bei Fetch/ALU/Paging. Deren Faktoren sind ausdrücklich keine Doom-Faktoren; die normalen .NET-/Hostschwankungen werden durch die obigen Gastmessungen separat ausgewiesen. Vorläufige Zwischenbinaries, ein wegen falscher ANSI-Synchronisation verworfener Messversuch und ältere Zeitmessungen zählen nicht als Freigabe des endgültigen Kandidaten.

Reproduktion: `tests/benchmark-doom-throughput.py --help` für Ton-/RAM-/Bildvergleich; `dotnet <Benchmarkordner>/DOSEmulator.Benchmarks.dll cpu-throughput 7` für feste CPU-Arbeit. Der Benchmarkordner wird wie unten mit dem jeweils vollständigen DLL-Satz gebaut. Aktuelle Gastregressionen, Quellen-/DLL-/Medienhashes und Einzelmessungen stehen im privaten Gesamtbericht über `artifacts/tests/doom-second-performance-release-path.txt`. Die regulären Startpfade erhalten dieselben getesteten Dateien. Benutzer-Spiel-COWs bleiben ungeöffnet. Keine neue native Maus-/Windows-Eingabeabnahme; die auf später verschobene Wiederholung bleibt offen. Vier Clocks pro Step/REP-Element und die 33-MHz-Entwicklungskonfiguration bleiben unverändert.

Ergänzung 26. Oktober 2026: Die projektweite [Code-/Performanceprüfung](CODE-REVIEW-2026-10-26.md) ergänzt Debugtext-/Kapazitätscaches, Span-Kopien im PCM-Ring, kleinere Reader-Leases, wiederverwendbare WAV-Puffer, einen begrenzten Overlay-Cache und ereignisgesteuerte ATA-IRQs. Neue AB/BA-Mikromessungen und Grenzen stehen dort; sie ersetzen die folgenden historischen Doom-Messungen nicht. Der Auftrag ergänzt außerdem [vier IDE-Plätze und gespeicherte PCs](MACHINES.md). Die vier virtuellen Clocks pro Schritt/REP-Element bleiben unverändert.

Stand: 25. Oktober 2026. Zwei getrennt gemessene Optimierungspakete; das erste RAM-/Shift-Paket bleibt unten als historischer Nachweis erhalten. Historische 486-Taktkalibrierung und vollständiges M3 bleiben offen.

## Änderungen

Der Speicherbus führt normale Bytezugriffe über einen kleinen Cache aus: Pro installierter 4-KiB-RAM-Seite vermerkt ein Flag, ob dort ein Gerät liegt. Bei 8 MiB RAM sind das 2 KiB Flagdaten. Jede erfolgreiche neue Zuordnung aktualisiert den Cache; teilweise belegte Seiten bleiben konservativ auf dem ursprünglichen Bytebus. Hohe ROM-Aliase benötigen keine 4-GiB-Seitentabelle. Das beschleunigt auch den Befehlsfetch und TLB-Seitentabellenzugriffe.

Wort-/Doppelwortzugriffe verwenden Little-Endian-Span-Zugriffe ausschließlich bei vollständig vorhandenem, zusammenhängendem RAM ohne Gerätemapping in den beteiligten Seiten. A20-Faltung, RAM-Ende, uint-Adressumlauf und MMIO/ROM bleiben bei geordneten Bytezugriffen. A20 maskiert weiterhin ausschließlich Bit 20. Geräte behalten ihre einzelnen Read-/Write-Aufrufe und deren Reihenfolge. Auch eine während eines Gerätezugriffs neu eingerichtete Zuordnung wird bei den folgenden Bytes berücksichtigt. Eine sehr kleine inlinefähige RAM-Methode trennt den schnellen Bytepfad von der Mapping-Suche; eine erste Variante mit schlechterer Byteleistung wurde verworfen.

ROL/ROR/SHL/SAL/SHR/SAR verwenden direkte Host-Bitoperationen. RCL/RCR verwenden einen 9-/17-/33-Bit-Ring einschließlich Carry. Das ersetzt bis zu 31 einzelne Schleifenrunden. Count-Maskierung, Nullzähler, CF/OF/SZP und der bisherige stabile Vertrag für undefinierte Bits bleiben erhalten. Bei Count null bleibt insbesondere die bisherige Operandprüfung mit Schreibabsicht erhalten. Checks, Datenzugriffe, Debugbeobachtungen und Write-before-flags-Reihenfolge bleiben im vorhandenen Decoder-/Fault-Pfad.

CPU-Zustandssicherung, REP-Elementgrenzen, Interruptzustellung, Scheduler, VGA-Puffer und Host-Pacing sind in diesem Paket nicht geändert. Jeder Step liefert weiterhin vier Entwicklungstakte. Die höhere Hostleistung verändert die virtuelle Zeit nicht.

## Messung

Referenzhost: AMD64 Family 26 Model 68, .NET 10.0.12, Windows x64. Es sind **feste Arbeitsmengen und Stopwatch-Messungen**, kein Samplingprofil und keine Aussage über den Anteil einer einzelnen Funktion an der gesamten Doom-Laufzeit. Ein unveränderter DLL-Satz vor der Optimierung dient als Referenz. Separate Varianten mit nur neuem RAM-Bus beziehungsweise nur neuem CPU-Shift isolieren beide Änderungen. Alle Prüfsummen stimmen überein.

Die endgültigen Mikrobenchmarks verwenden neun Messungen nach Warmup, jeweils 16 Millionen Busoperationen beziehungsweise zwei Millionen CPU-/Maschinenschritte. Mediane in Millisekunden; Tiered Compilation und ReadyToRun sind nur in diesen Benchmark-Kindprozessen deaktiviert, um JIT-Phasenwechsel innerhalb der Stichproben zu vermeiden. Der kombinierte Kandidat verwendet alle aktuellen Abhängigkeits-DLLs; die getrennten Varianten kombinieren gezielt archivierte und aktuelle DLLs mit derselben öffentlichen Schnittstelle. Frühere Messungen mit normalem Tiered-PGO bleiben private Zwischenstände und sind nicht die folgende Ergebnistabelle.

| Feste Arbeitsmenge | Vorher | Optimiert | Durchsatzfaktor |
| --- | ---: | ---: | ---: |
| RAM Byte lesen | 52,06 ms | 10,95 ms | 4,75× |
| RAM Byte schreiben | 53,55 ms | 11,38 ms | 4,71× |
| RAM Wort lesen | 99,36 ms | 33,25 ms | 2,99× |
| RAM Wort schreiben | 102,22 ms | 34,71 ms | 2,95× |
| RAM Doppelwort lesen | 197,64 ms | 35,22 ms | 5,61× |
| RAM Doppelwort schreiben | 194,91 ms | 44,86 ms | 4,34× |
| PE32-Schiebeschleife, CL=31 | 224,20 ms | 177,71 ms | 1,26× |
| PE32-Speicherschleife | 222,15 ms | 191,76 ms | 1,16× |
| Gleiche Speicherschleife über PcMachine | 245,42 ms | 215,70 ms | 1,14× |

Diese CPU-Zeilen enthalten die kombinierte RAM-/Shift-Optimierung. Die getrennten Varianten und sämtliche Einzelmessungen stehen im privaten Nachweis. Prozess-/Hostschwankungen verhindern eine allgemeine Leistungszusage; die Mikrobenchmarks isolieren synthetische Arbeitsmengen.

### Tatsächlicher DOS-/Doom-Vergleich

Die eigene kurze, stumme 32-Tick-MAP01-Demo läuft mit dem originalen DOS4GW/Doom II je zweimal pro Build ohne Speichermanager und unter originalem EMM386/RAM: **acht Kaltstarts**, abwechselnd vorher/nachher beziehungsweise nachher/vorher. Beide Programme laufen dabei mit normaler .NET-Konfiguration. Jeder Lauf verwendet eine neue private Overlaykopie. Gemessen wird die ganze Headless-Prozesslaufzeit einschließlich Start und Grafikaufnahmen; nachgelagerte Oracles und das Kopieren der Medien liegen außerhalb des Zeitfensters.

| Konfiguration | Vorher, Median | Nachher, Median | Weniger Hostzeit |
| --- | ---: | ---: | ---: |
| MS-DOS ohne Manager | 18,909 s | 16,609 s | 12,16 % |
| MS-DOS mit EMM386/RAM | 30,328 s | 27,701 s | 8,66 % |

**Der vollständige gemeldete CPU-Zustand einschließlich Zählern, die Bild-/Szenenfolgen und die virtuelle Endzeit sind zwischen allen Vorher-/Nachher-Wiederholungen je Konfiguration identisch.** Die WAD-abgeleitete unabhängige Prüfung bestätigt die Szenen und DOS-Rückkehr. Das ist eine begrenzte Extender-/Grafikprobe, keine allgemeine Spiel-, Sound- oder Eingabeabnahme. Zwei Wiederholungen je Variante sind ein gezielter lokaler Vergleich, kein statistischer Test über verschiedene Rechner oder lange Spielsitzungen.

## Verifikation und Reproduktion

**2.325/2.325 Regressionen, 0 Buildwarnungen/-fehler.** 64 neue Testgruppen umfassen **546.816 Ergebnis-/Flag-Vergleiche** des tatsächlichen Shift-Decoders gegen einen getrennten bitweisen Kompatibilitätsalgorithmus, alle 256 Rohzähler, acht Operationen, drei Breiten, Register-/Speicherformen und CL-Zielregisteraliasing. Fünf weitere tatsächliche PF-Fälle prüfen Schreibrechte einschließlich Nullzähler. Der unabhängige Bytebus-Vergleich prüft A20 an/aus, ungerade/seitenteilende Zugriffe, partielle Gerätemappings, spätes Mapping, RAM-Ende mit unvollständiger letzter Seite, hohe Aliase, uint-Umlauf und MMIO-Aufrufreihenfolgen. Diese Tests behaupten keine allgemeine Bus-/MMIO- oder Steppingpräzision über den bisherigen Modellvertrag hinaus.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
dotnet build .\tests\DOSEmulator.Benchmarks\DOSEmulator.Benchmarks.csproj -c Release -p:DosAssemblyRoot=<absoluter-DLL-Ordner> -o <privater-Benchmarkordner>
dotnet <privater-Benchmarkordner>\DOSEmulator.Benchmarks.dll 2 9
```

Für die dokumentierte stabile Mikrobenchmark-Konfiguration die beiden Benchmark-Umgebungsvariablen ausschließlich für die gestarteten Kindprozesse setzen. Das Vergleichsskript `scripts/benchmark-dos-extender.ps1` nimmt `-BaselineAssemblies`, `-CandidateAssemblies`, eine vorbereitete private `-Disk`, `-RamOverlay`, `-Scenario` und den lokalen `-Game`-Ordner entgegen. Es erzeugt eigene private COWs, prüft Hashes und verwirft Läufe mit verändertem CPU-Zustand, Bildfolgen oder virtueller Zeit. Originale und Benutzer-Spielkopien werden nicht verändert.

Aktuelle Gastregressionen, konkrete Artefaktpfade und gemeinsame DLL-/Quellhashes stehen im [Implementierungsstatus](IMPLEMENTATION-STATUS.md).

## Zweites Paket: verbleibende Reviewpunkte

Die offenen Punkte aus dem zweiten Review sind im begrenzten Referenzmodell umgesetzt und gemeinsam geprüft. Baseline ist der Stand **nach BCD sowie dem ersten RAM-/Shift-Paket**, mit 2446 Tests. Alle folgenden Messungen verwenden den endgültigen Kandidaten mit 2546 Tests und vollständig passenden Abhängigkeits-DLLs.

### CPU, Speicher und Gerätegrenzen

Die kleine Sicherung der acht GPR bleibt auf dem Stack. Die große Kontrollsicherung entsteht erst für Befehlsfamilien mit möglichen Kontrollzustandsänderungen, bei Beobachtern und vor opaken Geräte-/Tabellenzugriffen. Fetchfehler, verborgene Segment-Caches, ursprünglicher ESP und bereits übernommene Hardware-Tasks behalten ihren Restartvertrag. A20 wird vor der Geräteklassifikation angewendet. Auch ein ungepagter Wort-/Doppelwortzugriff vom RAM in die nächste MMIO-Seite sichert den Zustand vor dem ersten Gerätebyte; vier gezielte Tests reproduzierten den Fehler der Zwischenversion und bestehen nach der Korrektur. Die vorsorgliche physische Diagnoseübersetzung pro Step entfällt; Diagnosen verwenden den tatsächlichen ersten Fetch.

RMW prüft Alignment und Schreibrechte zusammen mit dem Lesen. Der spätere Schreibzugriff übersetzt nach einem möglichen Gerätekallback erneut. Vollständig von einem Gerät belegte RAM-Seiten dispatchen direkt; partielle Seiten und hohe Aliase behalten die Mapping-Suche. Der VGA-Chain-4-Shortcut gilt ausschließlich für die geprüfte einfache Mode-0-Form mit voller Bitmaske, ohne Rotation/Logik/Set-Reset und mit weiter wirksamer Plane-Maske.

REP MOVS/STOS führt das erste Element normal aus und bündelt danach ausschließlich geprüfte RAM-Spannen: höchstens eine Seite pro Quelle/Ziel und 4096 Elemente, bis zur nächsten Gerätefrist oder Aufrufgrenze. Überlappung und DF verwenden die ursprüngliche Elementreihenfolge. Seiteneinträge müssen bereits vorhanden und schreibberechtigt/dirty sein; neue Seiten, Geräte, Debug/TF/SS-Schatten, Beobachter und problematische Segment-/AC-/A20-Grenzen fallen auf Step zurück. Präfix-/Opcodebytes werden erneut geprüft; Selbstmodifikation beendet den schnellen Pfad. Vier virtuelle Clocks und die bisherigen Elementzähler bleiben erhalten.

PIC-Pending wird bei tatsächlichen Leitungs-/Masken-/ICW-/EOI-/Acknowledge-Änderungen aktualisiert. Wiederholtes IRQ14-Setzen bleibt deshalb billig und kann nach ICW trotzdem eine gehaltene Leitung erneut behaupten. Scheduler-Deadline und Bruchquanten sind gecacht; periodische vier Clocks benötigen Addieren/Übertrag. Das REP-Budget endet vor Gerätefristen. Einzelbefehle behalten ihre normalen Interrupt-/Gerätegrenzen.

### Bildausgabe, Audio und Hostpacing

Wiederverwendbare VGA-Oberflächen invalidieren bei RAM-/Font-/Paletten-/CRTC-/Modus-/Blankingänderung und passender Cursor-/Textblinkphase. Die vorhandene frische Capture-API bleibt erhalten. GUI und Headless verwenden jeweils eigene Oberflächen; drei GUI-Puffer und Reader-Leases verhindern Überschreiben während der Anzeige. Die UI hält ihre Lease auch über die spätere SharpPixels-GPU-Übertragung hinaus bis zum nächsten UI-Update.

Der Audiomischer überspringt ausschließlich exakt garantierte Nullintegration ohne Filterreste. Samplephase, PCM-Nullblöcke, Zeitstempel und Listener bleiben erhalten. Speaker/SB melden diese begrenzte Eigenschaft; der opake OPL-Integrator wird weiter aufgerufen. Filtertails werden normal verarbeitet. Der Pacer wartet bei Rückstand 1 ms, bei Vorlauf abhängig vom tatsächlichen Abstand 1–16 ms. Virtuelle CPU-/Gerätezeit wird dabei nicht geändert.

### Profiling und feste Arbeitsmengen

Je ein echtes Samplingprofil ohne Manager und mit EMM386/RAM wurde vor und nach dem Paket über einen separaten `dotnet-trace`-Kindprozess aufgenommen. CPU Step/RunSteps dominieren im Ausgangsstand. Im Kandidaten rücken Fetch/Decoder und verbleibende notwendige Kontrollsicherungen nach vorn. Inlining kann Arbeit unter dem Aufrufer verbuchen; Sampleanteile sind keine isolierten Funktionskosten. Profilingläufe werden nicht als Laufzeitbenchmark benutzt. Werkzeug und Traces liegen ausschließlich in privaten Artefakten. [Microsoft-Dokumentation zu dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

Neun Stopwatch-Messungen pro fester Arbeitsmenge nach Warmup, Aufbau außerhalb des Zeitfensters; nur diese Benchmark-Kindprozesse verwenden Tiered Compilation/ReadyToRun = 0. Alle Prüfsummen stimmen überein. Derselbe AMD64/.NET-10-Referenzhost wie beim ersten Paket. Mediane:

| Feste Arbeitsmenge | Baseline | Kandidat | Durchsatzfaktor |
| --- | ---: | ---: | ---: |
| 2 Mio. PE32-Registerschritte | 199,925 ms | 93,744 ms | 2,13× |
| 2 Mio. Schritte einer RAM-REP-MOVSD-Schleife | 271,303 ms | 1,617 ms | 167,83× |
| 2 Mio. PE32-Registerschritte mit Paging | 216,910 ms | 118,902 ms | 1,82× |
| Dieselbe REP-Schleife mit Paging | 337,662 ms | 2,046 ms | 165,06× |
| 8 Mio. VGA-Chain-4-Bytes schreiben | 50,995 ms | 42,802 ms | 1,19× |
| 8 Mio. unveränderte PIC-Leitungs-/Pending-Abfragen | 104,860 ms | 9,718 ms | 10,79× |
| 8 Mio. Scheduler-Schritte à vier Clocks | 28,439 ms | 23,442 ms | 1,21× |
| 20 virtuelle Sekunden exakt stummer Speaker | 16,643 ms | 0,055 ms | 302,61× |
| 1000 unveränderte 320×200-Grafikaufnahmen | 128,964 ms | 0,162 ms | 798,04× |

REP, garantierte Stille ohne PCM-Abonnenten und unveränderte Bilder sind isolierte Spezialfälle. Ihre großen Faktoren gelten nicht für allgemeines Spielen. Bei den 1000 unveränderten Grafikaufnahmen fallen die gemessenen Allokationen von **256145208 auf 344080 Bytes**; das schließt Metadaten/Reflection und den anfänglich benötigten Pixelpuffer ein. Veränderte Bilder werden weiterhin gerastert.

### Erneuter tatsächlicher Doom-Vergleich

**Acht Kaltstarts mit normaler .NET-Konfiguration**, je zweimal pro Build und Konfiguration, Reihenfolge AB/BA, neue private COWs. Gemessen wird der gesamte Headless-Prozess samt Start, Bildaufnahmen und diesmal zusätzlich dem 8-MiB-RAM-Export. Medienkopien und nachgelagerte Oracles liegen außerhalb des Zeitfensters. Während der Zeitmessung laufen keine anderen hier gestarteten Gasttests.

| Konfiguration | Baseline, Median | Kandidat, Median | Weniger Hostzeit |
| --- | ---: | ---: | ---: |
| MS-DOS ohne Manager | 25,014 s | 18,668 s | 25,37 % |
| MS-DOS mit EMM386/RAM | 41,820 s | 33,356 s | 20,24 % |

**Alle 8 MiB Gast-RAM, der gemeldete CPU-Zustand außer TLB-Hit-Aufrufstatistik, Bild-/Szenenfolge und virtuelle Endzeit sind identisch.** Die TLB-Hit-Zahl sinkt mit EMM386 von 799056911 auf 796234636, weil unnötige Übersetzungsaufrufe entfallen; sie wird separat gespeichert. Pagewalks und sämtliche übrigen gemeldeten CPU-Zähler werden weiterhin streng verglichen. Diese Ausnahme ist ausdrücklich opt-in; das Vergleichsskript bleibt standardmäßig strikt. `-CompareGuestRam` aktiviert zusätzlich den vollständigen RAM-Vergleich. Zwei Wiederholungen je Variante, Prozess-/Hostlast und eine kurze stumme Demo begrenzen die Leistungs- und Kompatibilitätsaussage.

### Gemeinsame Abnahme und Reproduktion

**2546/2546 Regressionen, 0 Warnungen/Fehler**, 100 neue Gruppen/Fälle. Darunter 78 tatsächliche Step-/Batch-Differenzfälle für REP, vollständiger RAM-/CPU-/Zeitvergleich, DMA-Ereignisse mitten im Transfer, DF/zerstörende Überlappung, Selbstmodifikation, fehlende Seiten und Debug-/Beobachter-Fallback. Weitere Vergleiche prüfen 100000 PIC-Operationen gegen den alten separaten Algorithmus, Scheduler-Phasen/Deadlines, bitgenaue PCM-Daten/Zeiten, VGA-Modi/Font/Palette/Blink und langsame konkurrierende Pufferleser.

Auf genau den eingefrorenen Produkt-DLLs bestehen **190 Gastregressionsstarts**: 55 CPU/FDC/VDS/FORMAT, 51 FPU/IRQ13/DOS4GW und 84 Grafik-/Tonstarts, einschließlich originalem Microsoft DEBUG.EXE und EMM386. Zusammen mit Paarvergleich und zwei profilierten Kandidatenstarts: **200 Headless-Starts, davon 196 aktuell und vier absichtliche Baselines**, 2022 verworfene Gastoracles. Frisch erneut geprüft: **58350 definierte native x87-Fälle plus 36 getrennte BCD-Diagnosen**, einschließlich rationaler Wurzelintervalle und unabhängiger Konstanten-/BCD-Zertifikate. Die native Referenz bleibt testexklusiv und kein physischer 486.

Ein eigenes finales GUI-Smoke mit sechs visuell geprüften Aufnahmen bestätigt Menüs, CRT/Scale2x und fortgesetzte Bildausgabe ohne OpenGL-Fehler. Keine native Mauswiederholung oder Windows-Eingabe. Zehn geschützte Eingaben und 53 private Baselines stimmen weiter mit den ursprünglichen Hashes überein; die Benutzer-Spielkopie wurde nicht geöffnet. 61 PowerShell-Skripte/201 Checks und 62 Python-Prüfer bestehen. Quellen-/DLL-/Referenzhashes und alle konkreten Gastverzeichnisse: privater Gesamtnachweis über `artifacts/tests/review-performance-release-path.txt`.

Mikrobenchmark: `dotnet <Benchmarkordner>/DOSEmulator.Benchmarks.dll review 9`; Build wie oben gegen die jeweils vollständigen Baseline-/Kandidaten-DLL-Sätze. Gepaarter Gasttest: `scripts/benchmark-dos-extender.ps1` zusätzlich mit `-AllowTranslationCounterDifferences -CompareGuestRam`. Die gesamte Abnahme betrifft diese konservativen Optimierungen. Universelle MMIO-/Alias-/Bus-/Fault-Konkurrenzpräzision, vollständiges x87 und M3 bleiben offen. FSCALE, FXTRACT, [FPREM/FPREM1](hardware/X86-X87-REMAINDER.md), [F2XM1 mit unabhängigen mathematischen Fehlergrenzen](hardware/X86-X87-EXP2M1.md), [FYL2X/FYL2XP1 mit genauen Produktintervallen](hardware/X86-X87-LOGARITHM.md), [FPATAN mit unabhängigen Winkelgrenzen](hardware/X86-X87-ATAN.md), [FPTAN mit strenger Argumentreduktion](hardware/X86-X87-TANGENT.md), [FSIN/FCOS mit unabhängigen Intervallen](hardware/X86-X87-SIN-COS.md) und [FSINCOS mit beiden Stackzielen](hardware/X86-X87-FSINCOS.md) besitzen inzwischen getrennte M3-Teilabnahmen; [gerätegetaktetes Wort-DMA](hardware/AT-DMA16.md) besitzt ebenfalls eine getrennte Geräte-/DOS-Abnahme; der darauf aufbauende [CT1740-/DSP4.05-PCM-/CT1745-Kern](hardware/SB16-CT1740-PCM.md) besitzt inzwischen eine eigene DMA8-/DMA16-/Stereo-/IRQ-/DOS-Abnahme. Die Erweiterung des gemeinsamen Mixers erhält die bisherigen Mono-PCM-Hashes. [YMF262/OPL3](hardware/OPL3.md) besitzt inzwischen eine eigene funktionale Quad-/Stereo-/Vieroperator-/DOS-Abnahme; [ET4000AX-Banking und Legacy-SVGA](hardware/ET4000AX.md) besitzen inzwischen einen eigenen Nachweis. Das ist keine neue allgemeine Leistungs- oder unterlauffreie Host-Audiozusage. Das sind Funktionsnachweise, keine erneute Performancekalibrierung.

Die pauschalen vier Takte pro Step sind weiterhin **kein historisch kalibriertes 486/33-Profil**. Für eine solche Kalibrierung sind Befehlsgemisch, Speicher-/I/O-Zeiten, Geräteereignisse und echte Referenzprogramme gemeinsam zu betrachten. Schnellere Host-Ausführung behebt diese Modellgrenze nicht.

Primärquelle für Shift-/Rotate-Regeln und Zählermasken: Intel, **Intel486 Programmer’s Reference Manual (1992)**, RCL/RCR/ROL/ROR sowie SAL/SAR/SHL/SHR in Kapitel 26. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143). Undefinierte Resultate/Flags sind ausdrücklich nur als bisheriger stabiler Modellvertrag geprüft.

## ET4000-Funktionsausbau, 26. Oktober 2026

Der aktuelle 4934er-Build ergänzt die optionale ET4000AX-ISA-Karte, ohne das Modell mit vier virtuellen Clocks pro Step/Element umzukalibrieren oder virtuelle Gerätezeit zu überspringen. Die bisherigen VGA-/Font-/Cursorpfade bestehen 92 aktuelle Gaststarts; Referenz-VGA und ET4000 bestehen zusammen 16 kurze stumme Doom-Starts. Dazu kommen 32 eigene ET4000-Gaststarts. Die Messwerte des früheren Performancepakets sind historische Vergleichswerte, keine erneut bestätigte Laufzeitverbesserung oder 486/33-Kalibrierung.

Die zusätzliche optionale ET4000-Videodiagnose verwendet InstructionStarting, um CD10-Versuche durch einen V86-Monitor zu sehen. Damit werden die bestehenden Beobachter-Fallbacks und großen CPU-Kontrollsicherungen bewusst aktiv. Solche diagnostischen Läufe sind keine Host-Durchsatzbenchmarks. Ein bei der GUI-Abnahme gefundener WinMM-Fehler bei waveOutClose wird bei Shutdown erfasst; ein PlaybackStopped-Handshake verhindert das Freigeben vor dem bestätigten Ende des Callbackthreads. Vier gezielte Headless-Audioregressionen und zwei GUI-Läufe bestätigen FM-Ausgabe, Mute und getrennte Pegel. Der native Freigabefehler dieses Hosts bleibt dennoch offen und ist keine unterlauffreie Host-Audiozusage. Die Gast-/Headless-DLLs ändern sich durch diese abschließenden GUI-Korrekturen nicht.

## Funktionaler LSS-Nachtrag auf dem 6309er-Build

Die [LSS-Korrektur](hardware/X86-LSS-BOUNDARY.md) entfernt eine falsche zusätzliche Interruptsperre. Ein bereits pending IRQ kann dadurch eine Instruktionsgrenze früher eintreten. Das ist die beabsichtigte funktionale Änderung, keine gemessene Hostzeit-/FPS-/Chipzyklusoptimierung. Virtuelle CPU-/Zeitidentität zwischen betroffenem 6247er- und korrigiertem 6309er-Lauf wird nicht behauptet; die historischen Messungen behalten ihre Builds.
