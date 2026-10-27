# Historischer 7489er-M4-Baustein: virtueller P54C-TSC, RDTSC und CR4.TSD

Aktueller Folgeschritt: [P54C-TSC-MSR-Schnittstelle](P54C-MSR.md) auf 7873. Die folgende Abnahme bleibt beim archivierten 7489er-Build mit CPUID=0111h. Der neue clockgebundene Kern meldet wegen RDMSR/WRMSR jetzt 0131h; Produktprofile bleiben 486.

Stand: 27. Oktober 2026. **7489/7489 Regressionen, normaler Build ohne Warnungen/Fehler.** Der interne [P54C-B5-Befehlsbaustein](P54C-B5.md) besitzt jetzt eine ausdrücklich gebundene virtuelle Taktquelle. Dies schließt diese TSC-Vertikale; ein auswählbarer vollständiger Pentium-PC und Windows 95 bleiben offen. Alle bisherigen Produktprofile verwenden weiterhin 486.

## Historischer Primärvertrag

Maßgeblich ist Intels [Pentium Processor Family Developer's Manual Volume 3, 241430-004, Juli 1995](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf): RDTSC auf Seiten 25-265/266 (PDF 882/883), MOV CR auf 25-228 (PDF 845), TSC in 26.2.2 auf 26-3 (PDF 944) sowie die Modellunterschiede in 23.2.19.4 auf 23-23/24 (PDF 584/585). Der geprüfte lokale Scan besitzt SHA-256 `63C27628292DE539EB6EA9089404FE7B5D1FCA9D292021A4493DCE044E9AA988`.

- Der Zähler hat 64 Bit, zählt virtuelle Kernclocks und liefert bei RDTSC **EDX:EAX**. 66h/67h verändern die Ergebnisbreite nicht; Flags bleiben erhalten.
- In PE darf CPL0 unabhängig von TSD lesen. CPL1–3 dürfen nur bei TSD=0 lesen; andernfalls erfolgt **#GP(0)** vor Registeränderungen.
- **Der historische P54C-Vertrag verbietet RDTSC in V86 auch bei TSD=0.** Die spätere allgemeine RDTSC-Regel darf diese ausdrücklich gedruckte V86-Ausnahme nicht ersetzen.
- Reset setzt den Zähler zurück. INIT und zusätzliche Energiespar-/Taktpins sind hier noch nicht modelliert; aus Reset folgt kein unbelegter INIT-Vertrag.
- MOV CR4 verlangt CPL0 und eine Registerform; sein Transfer bleibt 32 Bit. Im jetzigen Teilumfang sind ausschließlich 0 und **TSD/Bit2** schreibbar. Andere Erweiterungen sind **noch nicht implementiert**, auch wenn reale P54C-Prozessoren sie besitzen. Ihre Ablehnung ist keine Behauptung, diese historischen Bits seien reserviert.

Die Einleitung des historischen RDTSC-Textes bezeichnet korrekt das eigene CPUID-TSC-Bit4; dessen letzter Hinweis verweist dagegen auf das MSR-Bit5. DOSE bewirbt das getrennte TSC-Bit und lässt das noch nicht implementierte MSR-Merkmal ausdrücklich aus.

Der historische Pentium schreibt bei einem 32-Bit-Fehlerrahmen den vollständigen Fehlerdword mit oberem Nullwort. Der 486 reserviert vier Bytes und schreibt lediglich das untere Wort. Die gemeinsame Fehlerfunktion unterscheidet jetzt die Modelle; mit A5h vorbelegte Stacks prüfen den Unterschied unabhängig von zufälligen Nullbytes. Bestehende 486-Rahmen und 16-Bit-Gates behalten ihren Vertrag. Weitere Pentium-spezifische Selektor-Push-/Pop-Grenzen bleiben für den folgenden Systembaustein offen.

## Virtueller Takt und Ownergrenze

Ein zusätzlicher Konstruktor verlangt das bekannte CPU-Modell, einen `DeviceScheduler` und eine feste nichtnullige `TimestampFrequency`. Nullquelle/Nullfrequenz gehören zusammen; unvollständige Paare und eine Quelle am 486-Modell werden vor Aufbau abgewiesen. Die bestehenden Zwei-/Drei-Argument-Konstruktoren bleiben erhalten. Ohne ausdrückliche Quelle bleibt der alte interne P54C-Teilumfang bei CPUID=0101h und weist RDTSC/CR4 ab. Mit Quelle meldet er **0111h: FPU/TSC/CX8**, keine modernen Hostmerkmale oder unimplementierten MSR-/VME-/PSE-/MCE-Funktionen.

`DeviceScheduler.ClockTicks` liest ausschließlich persistierte virtuelle Zeit und den bereits vorhandenen Rest der ganzzahligen Taktumrechnung. Der Rest wird vor der Division einbezogen: bloßes Abrunden von `Now * Frequenz / 1e9` würde etwa bei 33/90 MHz einzelne TSC-Clocks verlieren. UInt128-Zwischenwerte erhalten den vollen 64-Bit-Überlaufvertrag; keine Host-Wanduhr, kein Stopwatch und keine versteckte Zeitfortschreibung. Lesen verändert weder Schedulerzeit, Cache, Token noch Ereignisse.

Gesampelt wird an einer Ownergrenze außerhalb eines laufenden Schedulercallbacks. Während Dispatch beschreibt der vorhandene Umrechnungsrest die umschließende Fortschaltung; eine solche Abfrage wird deshalb ausdrücklich abgewiesen. Auch Reset prüft diese Grenze vor der ersten CPU-Änderung. Reset verankert die neue TSC-Epoche an der laufenden Quelle und setzt TSD zurück; die versorgten Geräte und ihre Zeit werden dadurch nicht zurückgesetzt. Bei HLT zählt die ausdrücklich fortgeschaltete virtuelle Zeit weiter.

Die bestehenden vier Entwicklungstakte je CPU-Step/REP-Element bleiben erhalten. Die Tests bei 33 MHz prüfen die ganzzahlige Phasengrenze; sie sind keine Behauptung eines historischen 33-MHz-P54C. Der 90-MHz-Fall ist ebenfalls keine gemessene Befehls-/Bus-/Pipelinetaktkalibrierung. Ein konfigurierbarer Produkt-P54C, dynamischer Frequenzwechsel und sämtliche Power-Management-Pins folgen erst mit ihrem eigenen Plattformvertrag.

## Zustand und Import

Das explizite Inventar umfasst **38 Typen, 751 Instanzmitglieder und unverändert 32 Ereigniskörper**. Die CPU prüft `Model` und die feste `TimestampFrequency` als Konstruktorinvarianten. Die Quelle ist eine feste Ownerverbindung; die TSC-Epoche und CR4 sind explizite Werte. Im gemeinsamen Schnitt werden CPU und zugehöriger Scheduler gemeinsam restauriert. Es gibt keine allgemeine Delegate-/CLR-Graph-Aktivierung und keine zusätzliche JSON-Arbeit auf Timer-/Audiopfaden.

Drei neue vollständige Container-Negativfälle verändern Frequenz, CR4 beziehungsweise TSC-Epoche eines bestehenden 486-PCs bei korrekter neuer Prüfsumme. Alle liefern eine Fehlerantwort, erhalten die bisherige Maschine und erzeugen keinen Zweig. Format 1 bleibt buildgebunden: ein wirklicher eigener 7305er-CLI-Import scheitert vor Zweiganlage; die alte Sicherung bleibt unverändert. Der 64-Dateien-Starter und alle 735 Quellen von 7305 sind privat archiviert.

Ein vollständiger Produkt-P54C-Snapshot wird hier noch nicht angeboten: die bestehende Produkterfassung bleibt beim 486-PC. Der neue eigene Gast prüft einen ausdrücklich benannten **CPU-/RAM-/PIC-/PIT-/Scheduler-Komponentenschnitt** einschließlich der PIC-Unterzustände und des PIT-Speaker-Zählers. Das ist mehr als ein CPU-Abzug und bleibt enger als die noch offene P54C-Gesamtplattform mit sechs Medienpositionen.

## Ausgeführte Nachweise

**181 neue TSC-/Rechte-/Modell-/Zustandsfälle plus drei vollständige Importablehnungen**, insgesamt **7489 Regressionen**. Real Mode, PE16/PE32, Paging, CPL1–3, historische V86-Fehler, beide TSD-Werte, 66h/67h, LOCK, vollständige GPRs/Flags, Reset, HLT, 64-Bit-Wrap, reine Clockabfragen, REP-Batch-/Skalarfortschaltung, Schedulerphase und Konstruktor-/Importgrenzen sind geprüft. Die vorgefüllten Pentium-/486-Fehlerrahmen behalten ihre getrennten Antworten.

Ein eigener Reset-ROM-Gast startet tatsächlich über den Resetvektor und programmiert PIC/PIT über Gast-OUTs. **Vier endgültige Starts und vier tatsächliche Komponentenfortsetzungen**, je zweimal bei 33/90 MHz: **32 echte RDTSC-Ablesungen, 16 tatsächliche PIT-IRQ0-Acknowledgements/Handler/EOIs**, rohe 80-Bit-Pi-Daten, CPUID, TSD, DF und Speicherwachen. Die 90-MHz-Ausführung liest auch ein tatsächlich nichtnulliges oberes TSC-Dword. Der Schnitt liegt beim ersten HLT mit laufendem Timer, zukünftigem Ereignis und nichtnulligem Taktrest.

Nach dem Laden bleiben **920 weitere vollständige ausgewählte Zustands-/ganze 2-MiB-RAM-Vergleiche** sowie zukünftige CPU-, Port-, Timer- und IRQ-Ereignisse gleich. Zwei Wiederholungspaare sind zustands-/RAM-/zeit-/Publikationshashgleich. Das unabhängige Python-Oracle rekonstruiert alle **2880 Ledger-Einträge** mit eigenen Ganzzahldivisionen, tatsächliche Opcodepräfixe, PIT-Datencommit und alle 1.193.182-Hz-Fristen. **288 Verfälschungen scheitern.** Kein DOS-Boot, kein Host-TSC, kein Host-IRQ-Injektor und kein voller P54C-PC werden daraus abgeleitet.

Getrennt bestehen auf den endgültigen sieben DLLs **acht 486-DOS-Kaltstarts und acht volle PC-Fortsetzungen** unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM, jeweils zweimal. **1282 weitere vollständige CPU-/8-MiB-RAM-/Geräte-/Schedulervergleiche**, echte PCM-DMA, unabhängige rohe x87-DOS-Dateien und **200 verworfene Oracles** erhalten den bisherigen Produktvertrag. Zusammen **488 negative Gastoracles**; drei echte Importregressionen und der alte CLI-Buildimport sind getrennte Ablehnungen.

Die frühere Matrix vor den abschließenden Konstruktor-/CR4-Rechtefällen ist nicht Teil des endgültigen Test-DLL-Nachweises. Beide Versionen besitzen identische getestete VM-DLLs; die abschließende Matrix friert auch Tests.dll ein. **632 lizenz-/kommentargeprüfte Code-/Projektdateien, 130 Python-Werkzeuge, 88 PowerShell-Skripte**. 64 Kandidaten-/Starter-Dateien sind bytegleich. Alle 115 geschützten Eingaben, C64 und HEAD bleiben unverändert; persönliche Spiel-COWs ungeöffnet. Keine Windows-Eingaben, Hostverbindungen, neue Videoaufnahme, Commits/Pushes oder neue allgemeine Performance-/Spiel-/Soundzusage.

Reproduktion:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 TSC"
.\scripts\test-tsc.ps1 -SkipBuild
```

Privater Gesamtnachweis: **`artifacts/tests/m4-tsc-release-path.txt`**. Nächster konkreter Schritt: historisch begrenzte P54C-MSR-/weitere CR4-/Systemverträge, insbesondere TSC-MSR-Lesen/Schreiben und Selektorrahmen, mit eigenen Rechte-/Fault-/Fortsetzungsfällen; anschließend tatsächliche DOS-/volle P54C-PC-Integration vor Profilfreigabe. 430FX/PIIX, S3 Trio64, ATAPI, Firmware/Manager und wirkliche Windows-95-RTM-Installation/Starts/Shutdown bleiben M4-Arbeit. M5–M8 folgen in Abhängigkeitsreihenfolge.
