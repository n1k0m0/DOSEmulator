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
  gemappt; A20- oder DRAM-Änderungen ersetzen nur die betroffenen Bereiche. Die
  Abfragebereiche der Dirty-Bitmap werden beim Mappingwechsel aufgebaut und für
  die einzelnen WHP-Exits wiederverwendet.
- BIOS-ROM und PAM-Fenster sind lesbar und ausführbar. PAM-Schreibzugriffe gehen
  über den bestehenden `MemoryBus`; geänderte PAM-Konfiguration und Shadow-RAM-
  Seiten aktualisieren die WHP-Sicht. VGA und dynamische S3-MMIO-Fenster bleiben
  ungemappt und laufen über Microsofts WHP-Instruktions-Emulator zum DOSE-Bus.
  A20- und DRAM-Dekodierungswechsel setzen betroffene GPA-Seiten neu.
- Portzugriffe verwenden den vorhandenen `IoPortBus`. Für `REP INS`/`OUTS` werden
  Port- und Speicherzugriffe durch den WHP-Instruktions-Emulator einzeln
  weitergereicht; eine Anweisung ist auf 65.536 Portoperationen begrenzt. Wenn
  WinHvEmulation bei einem geschützten 16-Bit-`REP OUTSW` oder `REP INSW` vor
  dem ersten Portcallback einen internen Fehler meldet, prüft die Lane
  alle RAM-Adressen vor dem ersten Gerätezugriff über die WHP-Seitentabellen.
  Ein bereits erfolgter Lesezugriff auf das erste Quellwort ist zulässig,
  sofern er gewöhnliches RAM betraf und kein Portzugriff stattgefunden hat.
  Danach führt sie jedes Wort genau einmal aus und setzt SI/DI, CX und IP fort.
  Nach bereits erfolgten Portzugriffen
  wird kein zweiter Versuch ausgeführt, damit Geräteseiteneffekte nicht doppelt
  auftreten.
- Für eine kurze BIOS-`REP MOVSB`-Folge nach VGA behandelt die Lane auch den
  paginierten V86-Modus. Sie prüft Quelle und Ziel vollständig und übersetzt
  die Gastadressen vor dem Schreiben; ein nicht gemapptes VGA-GPA wird anhand
  der vorhandenen Gastseitentabellen validiert. Andere nicht emulierbare
  MMIO-Anweisungen bleiben explizite Fehler.
- Der Windows-95-Lader verwendet außerdem `REP MOVSW/MOVSD` mit 32-Bit-Indizes
  jenseits der 64-KiB-Grenze eines Real-Mode-Segmentcaches. Der Softwarekern
  lässt diese RAM-Kopien zu; reale x86-Hardware erzeugt #GP. Die WHPX-Lane
  übernimmt diese enge Kompatibilität nur für die geprüften Befehlsfolgen mit
  gewöhnlichem RAM auf beiden Seiten und stellt andere Ausnahmen unverändert zu.
- Der vorhandene kaskadierte 8259 liefert die IRQ-Vektoren. Die Lane injiziert
  sie nur bei gesetztem IF und ohne Interrupt-Schatten. Wenn Windows IF nur kurz
  zwischen POPF und CLI setzt, meldet WHP das zustellbare Interruptfenster;
  die Benachrichtigung wird nach dem Exit bei Bedarf erneut aktiviert. Ein angehaltener Gast
  kann bis zum nächsten `DeviceScheduler`-Termin laufen und durch dessen IRQ
  geweckt werden. Der ownerseitige HLT-Zustand wird auch im Snapshot gespeichert.
- PC-Sitzungen fangen CPUID, MSR und RDTSC(P) ab. Der 486-Pfad liefert #UD für
  CPUID/RDTSC/MSR; P54C meldet dieselben Blätter wie der DOSE-Softwarekern.
  Sein RDTSC und MSR 10h verwenden den nominellen Profiltakt (einschließlich
  WRMSR-Epoche), unbekannte MSRs lösen #GP aus. RDTSCP ist für P54C #UD.
- `RunUntilVirtualTime` zählt die Dauer der WHP-Gastausführung auf die virtuelle
  Gerätezeit und lässt Hostarbeit für I/O und MMIO aus. Diese Echtzeit-Semantik
  ist nicht identisch mit den deterministischen CPU-Zyklen von `RunSteps`.
  Während eines aktiven WHP-Laufs fordert ein 2-ms-Einzelschuss dessen Abbruch an;
  die Windows-Timerauflösung wird für diesen Zeitraum auf 1 ms angefordert.
  So werden die 200-Hz- und späteren 1-kHz-PIT-Ereignisse des installierten
  Windows 98 nicht durch lange Host-Zeitscheiben zusammengezogen.
  `WhpxPcSession.Start(pc, virtualTimeScale)` kann die gemessene WHP-Laufzeit
  experimentell mit einem Faktor von 1 bis 64 auf die Gerätezeit abbilden.
  Der Windows-95-Start benötigt auf dem Entwicklungshost den Faktor 64; der
  Standard bleibt 1. Der Faktor ist Teil des WHPX-CPU-Checkpoints und wird
  beim Laden wiederhergestellt.
- Die APM-BIOS-Varianten übertragen normale IDE-Sektoren mit `REP INSW` und
  `ES REP OUTSW` statt 256 getrennten WHP-Port-Exits. DF- und 64-KiB-Grenzfälle
  behalten den geprüften Wortpfad; andere BIOS-Varianten behalten ihre
  bisherige ROM-Payload.
- `WhpxPcSession.Capture` speichert an einer gestoppten Grenze den vollständigen
  DOSE-Gerätezustand, Scheduler, RAM und die für diese Lane erfassten WHP-Register. `Resume` prüft und
  lädt den Gerätezustand in einen frisch erzeugten, gleich konfigurierten PC und
  setzt dann den WHP-Prozessor fort. `WhpxSnapshotStore` veröffentlicht zusätzlich
  einen eigenen `.whpxsav`-Ordner (Format 3) mit geprüftem WHP-CPU-Sidecar und einer
  vollständigen verschachtelten `.dossav`-Mediengeneration. Das normale
  `.dossav`-Format und dessen Software-CPU-Bedeutung bleiben unverändert.

## Geprüft

`dotnet run --project tests/DOSEmulator.Whpx.Tests -c Release` führt auf einem
Windows-x64-Host mit WHP eigene kurze Gastprogramme aus: RAM/ROM, VGA-artige
MMIO-Lese- und Schreibzugriffe, Port-I/O, `REP INS`/`OUTS`, A20-Aliasing,
zeitgesteuerten PIC-IRQ und dessen `IRET`, P54C-CPUID/TSC-MSR/RDTSC,
486-#UD, unbekannten MSR-#GP und HLT-/TSC-Snapshot mit PIC-Wecksignal.
CPUID und RDTSC sind auch in einem 32-Bit-Protected-Mode-Gastprogramm geprüft.
Ein 200-Hz-PIT-Gasttest prüft die wiederholte IRQ-Zustellung während laufender CPU.
Ein geschützter 16-Bit-Gasttest prüft `REP OUTSW` mit nicht seitenbündiger
DS-Basis, Segmentgrenze und dem letzten CX-Element.
Ohne WHP meldet der Runner `SKIP`.

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
Das Menü gehört zur Startdiskette und prüft keinen installierten Windows-Kernel.

Mit `DOSE_WHPX_INSTALLED_BRANCH=<absoluter Pfad zur branch.dospc.json>` kann
eine installierte PC-Arbeitskopie isoliert diagnostiziert werden. Die Probe
kopiert deren COW-Journal in einen eigenen temporären Testordner und entfernt
diesen danach; die Quellarbeitskopie wird nur gelesen. Die Kopie benötigt
vorübergehend Platz in Höhe des Journals. `DOSE_WHPX_INSTALLED_STEPS` begrenzt
die Anzahl der 100-ms-Laufscheiben; `DOSE_WHPX_INSTALLED_ENTER_AT` und
`DOSE_WHPX_INSTALLED_SECOND_ENTER_AT` senden optional die Eingabetaste an
Einrichtungsdialoge. `DOSE_WHPX_INSTALLED_TIME_SCALE=64` wählt für den
Windows-95-Test die explizite Zeitrate; der Standard ist 1. Der Test kann
am Ende einen vollständigen WHPX-Snapshot speichern, neu laden und fortsetzen.

Eine installierte Windows-95-Kopie startete kalt mit WHPX, wurde an einem
unveränderten Checkpoint bei etwa 96 Sekunden Gastzeit fortgesetzt und
erreichte bei rund 490 Sekunden den originalen 640×480-Hardware-Assistenten
für einen PCI-Ethernet-Controller. Bis dahin wurden 11.692 IDE-Sektoren gelesen;
es gab weder Hardware-Reset noch Windows-Schutzfehler. Ein in diesem grafischen
Zustand neu gespeicherter Format-3-Snapshot wurde unverändert geladen und fünf
weitere Zeitscheiben ausgeführt; das Bild blieb pixelgleich. Der Assistent
wartet auf eine Treiberauswahl und ist noch kein abgenommener Desktop.

Eine installierte Windows-98-SE-Kopie erreichte mit der aktuellen Laufzeitfassung
in einem frischen Kaltstart nach etwa 71 Sekunden Gastzeit den originalen
800×600-Desktop mit Startleiste, Symbolen und Mauszeiger. Zwei ursprüngliche
Datum/Uhrzeit-Dialoge wurden dabei per Eingabetaste geschlossen. Der Gast las
35.701 IDE-Sektoren, passierte den zuvor fehlerhaften geschützten `REP OUTSW`
und bestand das Speichern, unveränderte Laden und Fortsetzen eines vollständigen
WHPX-/Medien-/RAM-/VGA-Snapshots. Ein unveränderter Zwischenstand unmittelbar
vor der Video-Portphase wurde zusätzlich bei etwa 62 Sekunden gesichert.

Der komplette Release-Build und alle 14.970 Software-Regressionen, darunter
314 Snapshot-Regressionen, liefen nach der Integration ohne Fehler.

## Verbleibende Grenze

Die oben beschriebenen installierten Kopien sind im experimentellen WHPX-Pfad
gestartet. Ein allgemeiner Windows-Starter über WHPX ist noch nicht freigegeben:
Weitere CPU-Zustände einschließlich APIC/XSAVE, andere Ringwechsel- und
V86-Abläufe sowie Reset-/Triple-Fault-Domänen sind noch nicht vollständig
geprüft. Die WHP-Eigenschaft für eine
eigene Prozessorfrequenz wurde auf diesem Host beim Partitionsaufbau abgewiesen;
RDTSC/MSR melden deshalb einen eigenen nominellen Zähler aus der virtuellen
Gerätezeit. Die Gerätezeit basiert auf der gemessenen WHP-Ausführungsdauer
und einem expliziten Faktor; sie ist nicht zyklusidentisch zum Softwarekern.
Der für Windows 95 verwendete Faktor 64 ist ein Befund dieses Hosts, keine
portable CPU-Frequenzkalibrierung. VGA-MMIO und der BIOS-IDE-Pollpfad sind
noch deutlich langsamer als Echtzeit.
Ein zusätzlich untersuchter Real-Mode-Zugriff auf einen ungemappten Bereich
oberhalb des installierten RAM lieferte von WHP keine gültige lineare Adresse;
der Instruktions-Emulator lehnte ihn ab. Solche Exits bleiben explizite Fehler,
bis ein korrekter, allgemeiner Fallback für die jeweilige CPU-Betriebsart geprüft ist.
Bis diese Befunde vorliegen, bleiben die getesteten Windows-Starter auf dem
Software-CPU-Pfad.

WHP-Referenzen: [GPA-Mapping](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvmapgparange),
[Dirty-Bitmap](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvquerygparangedirtybitmap),
[I/O- und MMIO-Instruktions-Emulator](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-instruction-emulator/funcs/whvemulatortryemulation),
[GVA-Übersetzung](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvtranslategva),
[Interruptfenster](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/interruptwindow),
[Windows-Timerauflösung](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod),
[CPUID-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/cpuidaccess),
[MSR-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/msraccess),
[RDTSC-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/rdtsc).
Zur Ausnahmezustellung: [WHP-Exception-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/virtualprocessorexception).
Für Segmentgrenzen und V86-I/O: [Intel SDM, Band 2B](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf).
