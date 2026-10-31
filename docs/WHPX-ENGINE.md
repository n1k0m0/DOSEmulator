# Experimenteller WHPX-PC

`DOSEmulator.Whpx` enthält zwei getrennte WHP-Ausführungspfade. Die ursprüngliche
`WhpxRealModeMachine` bleibt ein privater 1-MiB-Test. Die neue `WhpxPcSession`
verbindet einen eigens erzeugten `PcMachine` mit `WhpxMappedMachine`. Kein
Windows-Starter und kein bestehendes PC-Profil wählt diesen Pfad automatisch;
`PcMachine.RunSteps` verwendet unverändert den Softwarekern. Eine aktive
WHPX-Sitzung reserviert ihren PC, sodass `RunSteps` nicht gleichzeitig laufen kann.

## Angebundener PC-Zustand

- Gewöhnliches, vom 430FX dekodiertes RAM wird als WHP-GPA gemappt. Die von WHP
  gemeldeten geänderten Seiten fließen vor jedem Gerätecallback in `MemoryBus.Ram`
  zurück. Speicherzugriffe der DOSE-Geräte markieren betroffene Seiten für die
  Rückrichtung. Vor jedem neuen Laufabschnitt werden auch direkte Besitzeränderungen
  am RAM abgeglichen. Zusammenhängende GPA-/Hostbereiche werden in einem WHP-Aufruf
  gemappt; A20- oder DRAM-Änderungen ersetzen nur die betroffenen Bereiche.
- BIOS-ROM und PAM-Fenster sind lesbar und ausführbar. PAM-Schreibzugriffe gehen
  über den bestehenden `MemoryBus`; geänderte PAM-Konfiguration und Shadow-RAM-
  Seiten aktualisieren die WHP-Sicht. VGA und dynamische S3-MMIO-Fenster bleiben
  ungemappt und laufen über Microsofts WHP-Instruktions-Emulator zum DOSE-Bus.
  A20- und DRAM-Dekodierungswechsel setzen betroffene GPA-Seiten neu.
- Portzugriffe verwenden den vorhandenen `IoPortBus`. Für `REP INS`/`OUTS` werden
  Port- und Speicherzugriffe durch den WHP-Instruktions-Emulator einzeln
  weitergereicht; eine Anweisung ist auf 65.536 Portoperationen begrenzt.
- Der vorhandene kaskadierte 8259 liefert die IRQ-Vektoren. Die Lane injiziert
  sie nur bei gesetztem IF und ohne Interrupt-Schatten. Ein angehaltener Gast
  kann bis zum nächsten `DeviceScheduler`-Termin laufen und durch dessen IRQ
  geweckt werden.
- `RunUntilVirtualTime` zählt die Dauer der WHP-Gastausführung auf die virtuelle
  Gerätezeit und lässt Hostarbeit für I/O und MMIO aus. Diese Echtzeit-Semantik
  ist nicht identisch mit den deterministischen CPU-Zyklen von `RunSteps`.
- `WhpxPcSession.Capture` speichert an einer gestoppten Grenze den vollständigen
  DOSE-Gerätezustand, Scheduler, RAM und die für diese Lane erfassten WHP-Register. `Resume` prüft und
  lädt den Gerätezustand in einen frisch erzeugten, gleich konfigurierten PC und
  setzt dann den WHP-Prozessor fort. `WhpxSnapshotStore` veröffentlicht zusätzlich
  einen eigenen `.whpxsav`-Ordner mit geprüftem WHP-CPU-Sidecar und einer
  vollständigen verschachtelten `.dossav`-Mediengeneration. Das normale
  `.dossav`-Format und dessen Software-CPU-Bedeutung bleiben unverändert.

## Geprüft

`dotnet run --project tests/DOSEmulator.Whpx.Tests -c Release` führt auf einem
Windows-x64-Host mit WHP eigene kurze Gastprogramme aus: RAM/ROM, VGA-artige
MMIO-Lese- und Schreibzugriffe, Port-I/O, `REP INSB`, A20-Aliasing,
zeitgesteuerten PIC-IRQ und dessen `IRET`. Ohne WHP meldet der Runner `SKIP`.

Mit `DOSE_WHPX_BOOT_PROBE=1` läuft der eingebaute Demo-PC bis über POST und
Bootsektor; danach wird ein gestoppter Vollgeräte-Checkpoint in einen neuen
Demo-PC geladen und die Gastzeit weitergeführt. Die Probe erstellt außerdem
einen eigenen `.whpxsav`-Stand, lädt dessen Medienzweig und CPU-Zustand und
verwirft einen absichtlich beschädigten CPU-Sidecar vor dem Medienzweig.
Mit `DOSE_WHPX_WIN_PROBE=1` durchläuft das Windows-95-Boardprofil mit der
**eigenen synthetischen Demo-Platte** POST und den Bootsektor. Auf dem
Entwicklungshost erreichte die Probe POST-Code `30h` und `RIP=7C48h`; danach
wurde auch dessen WHP-/Geräte-/RAM-Checkpoint auf einem neuen PC geprüft.
Es wird dabei kein Windows-Image gestartet.

Mit `DOSE_WHPX_REAL_BOOT_FLOPPY=<absoluter Pfad>` wird optional eine vorhandene,
echte Windows-98-Startdiskette **nur lesend** eingebunden; ihre Änderungen gehen in
ein eigenes kurzlebiges Overlay. Die Probe verlangt das tatsächliche „Microsoft
Windows 98 CD-ROM Startup Menu“ im VGA-Textspeicher, versiegelt diesen Zustand
als `.whpxsav`, lädt ihn mit eigenem Medienzweig und führt den Gast weiter.
`DOSE_WHPX_REAL_BOOT_STEPS` begrenzt optional die Anzahl der 100-ms-Laufscheiben
(Standard: 20, maximal 200). Auf dem Entwicklungshost erreichte die Probe das
Menü nach echten FDC-Sektorlesungen und bestand die Snapshot-Fortsetzung.
Das Menü gehört zur Startdiskette; ein installierter Windows-95/98-Kernel oder
Desktop wurde über WHPX damit noch nicht geprüft.

Der komplette Release-Build und die 314 bestehenden Snapshot-Regressionen des
Softwarepfads liefen nach der Integration ohne Fehler.

## Verbleibende Grenze

Ein Start von Windows 95/98 über WHPX ist noch nicht freigegeben. Vorher müssen
insbesondere CPUID/MSR/P54C-Vertrag, weitere CPU-Zustände einschließlich
APIC/XSAVE, TSC-Frequenz gegenüber dem nominellen
100/133-MHz-Gerätetakt, Reset-/Triple-Fault-Domänen und die Fortsetzung eines
echten Windows-Gasts aus dem neuen Medien-Snapshot geprüft werden. Die WHP-Eigenschaft für eine
eigene Prozessorfrequenz wurde auf diesem Host beim Partitionsaufbau abgewiesen;
die Lane verwendet deshalb die tatsächliche WHP-Ausführungsdauer. Auch ein
Vergleich der Video-/MMIO-Exit-Rate mit einem laufenden Windows-Gast fehlt.
Ein zusätzlich untersuchter Real-Mode-Zugriff auf einen ungemappten Bereich
oberhalb des installierten RAM lieferte von WHP keine gültige lineare Adresse;
der Instruktions-Emulator lehnte ihn ab. Solche Exits bleiben explizite Fehler,
bis ein korrekter, allgemeiner Fallback für die jeweilige CPU-Betriebsart geprüft ist.
Bis diese Befunde vorliegen, bleiben die getesteten Windows-Starter auf dem
Software-CPU-Pfad.

WHP-Referenzen: [GPA-Mapping](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvmapgparange),
[Dirty-Bitmap](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvquerygparangedirtybitmap),
[I/O- und MMIO-Instruktions-Emulator](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-instruction-emulator/funcs/whvemulatortryemulation).
