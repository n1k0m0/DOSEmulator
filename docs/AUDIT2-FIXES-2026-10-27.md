# Zweiter Audit: konkrete Korrekturen vom 27. Oktober 2026

Erneute Prüfung auf dem 11529er-ATAPI-Ausgangsstand: N1–N6 bleiben bestätigt. Zwei zusätzliche PIO-/Eingaberandfälle sind im [11563er-Folgepaket](AUDIT2-RECHECK-2026-10-27.md) korrigiert; die folgenden Zahlen gelten weiterhin dem historischen 9792er-Build.

Der parallele Chat „Emulator-Funktionen zusammenfassen“ hat den zweiten Audit auf eingefrorenen 8942er-Dateien erstellt. Sein privater Bericht `artifacts/audit2-2026-10-27-60daa5ae/report.html` bleibt unverändert. Die Umsetzung beginnt auf dem geprüften 9125er-Hostcontrollerstand. Drei Agenten bearbeiteten getrennte CPU-, Zustands-/Medien- und Eingabebereiche; Gegenreviews und gemeinsame Abschlussprüfung erfolgten anschließend. Keine konkurrierende Änderung durch den beim Einstieg und Abschluss ruhenden Auditchat.

**9792/9792 Regressionen, 667 neue Fälle, regulärer Release-Build ohne Warnungen/Fehler.** 64 Kandidaten-/zweiten regulären Release-Dateien bytegleich. Der vollständige 9125er-Starter und seine 797 Quellen sind privat archiviert. Der neue private Gesamtnachweis wird über `artifacts/tests/audit2-fixes-release-path.txt` erreicht. Historische Abnahmen behalten ihre Buildidentität; M4–M8 bleiben offen.

## Behobene Fehler

| Befund | Tatsächliche Änderung und Grenze |
| --- | --- |
| N1, STI bei bereits gesetztem IF | Nur der Übergang IF0→IF1 erzeugt den STI-Schatten. Aufeinanderfolgende STI verlängern ihn nicht beliebig. Rechteprüfung und MOV-/POP-SS-/Debuggrenzen bleiben erhalten; beide CPU-Modelle sind betroffen. |
| N2, ENTER-Allokation | Der finale gemaskte Stackoffset wird vor BP-/SP-Commit gegen SS geprüft. Vorherige tatsächliche Pushwirkungen und deren Paging-/Fehlerreihenfolge bleiben erhalten. Kein erfundener Speicherzugriff auf den bloß reservierten Bereich. |
| N3, beschädigte importierte Fortsetzung | Aktive REP-Caches müssen aus ihren gespeicherten Präfix-/Opcode-/Längenbytes exakt dieselbe Größe, Adresse und Quelle ergeben. ATA-Offsets sind gerade; DRQ benötigt noch ein vollständiges Wort. Gültige abgeschlossene 512-Byte-Phasen und durch echte IRQs veraltete REP-Caches bleiben ladbar. |
| N4, JSON-Zahlüberlauf | Die betroffenen ganzzahligen IPC-Felder werden mit `TryGetInt32` sowie Werte-/Formprüfung vor Wirkungen gelesen. Überlauf, Exponent-/Bruchdarstellung und falsche JSON-Form liefern die eigene `InvalidRequest`-Antwort; nachfolgende Requests und Gastcode laufen weiter. Keine pauschale Unterdrückung interner Laufzeitfehler. |
| N5, Disketten A/B mit aktiven COWs | Wiederverwendung sucht das genaue Basis-/Journalpaar über beide bisherigen Slots. Neue Topologien werden vollständig vorbereitet; Fehler erhalten alte Leases, Daten, Maschine und Wiederholbarkeit. |
| N6, Tastenwiederholung | Monotone Stopwatchzeit, höchstens eine fällige Wiederholungsrunde und neue Frist ab abgeschlossenem Callback verwerfen überholten Rückstand. Nullintervall, Fokus, Menüs, Pause und Imports bleiben getrennt. Keine neue native Bedienabnahme. |

Das Gegenreview fand zusätzlich ein echtes UI-/Owner-Rennen beim Laden: Alte gehaltene Eingaben konnten erst nach der Ersetzung in die Queue gelangen und dabei den neuen Token erhalten. `MachineSession.InputGeneration` und der zusätzliche `Enqueue(request, observedInputGeneration)`-Pfad erhalten jetzt den vom UI bestätigten Token bis zur Ownerprüfung. Das UI liest einmal, verwirft lokale Holds/Maus/Repeat ohne Gast-Releases und bestätigt genau diesen Wert. Ein weiterer Wechsel in diesem Fenster führt wieder zur Abweisung. Die bestehende Einargument-API bleibt für bisherige Caller erhalten. Zwölf kontrollierte Fälle verwenden wirkliche vollständige Snapshotersetzungen, alte Make-/Break-/E0-/Maus-/CtrlAltDel-/Porteingaben, zwei aufeinanderfolgende Wechsel und frische positive Kontrollen. Native Windows-Eingaben wurden nicht verwendet.

Historische CPU-Grundlagen: Intel486 PRM 1992, ENTER 26-70/71 und Stackfault 9.9.12; Pentium-Handbuch 241430-004, ENTER 25-82/83; [Intel Specification Update 242480-024, Änderung 12, gedruckte Seite 54](https://ardent-tool.com/CPU/docs/Intel/Pentium/spec_update/242480-024.pdf). Die bestehende gemischte Operand-/Stackbreitenpolitik und historische STI/FP-Sondergrenzen sind keine neu vermessene universelle Siliziumzusage.

## Zwei begrenzte Optimierungen

`Float80Math.FromInteger` erzeugt die exakte Signifikand-/Exponentdarstellung aus dem unsigned Betrag und der führenden Nullenzahl. Alle 64 Eingabebits einschließlich `long.MinValue` bleiben erhalten; kein Host-Double und keine Änderung sonstiger x87-Rechen-/Fehler-/Storepfade. Ein unabhängiges BigInteger-Oracle prüft sämtliche 65536 signed16-Werte und 200373 weitere Int64-Grenz-/Zufallswerte. 36 tatsächliche FILD-Gruppen prüfen 16/32/64 Bit unter allen geprüften PC-/RC-Kombinationen und rohe tword-Stores.

VGA/ET4000 behalten CPU-Registerlatches und spätere VRAM-Wirkungen. Reine CPU-GC-Schreibzugriffe verwerfen den Pixelcache nicht mehr. Die tatsächlich verwendeten Displaymasken GC5=40h und GC6=01h, Blanking/Palette und geänderte wirksame CRTC-Bytes invalidieren weiterhin; geschützte beziehungsweise identische CRTC- und bloße Attributindexzugriffe tun dies nicht. Echte Speicherstores rendern danach korrekt neu.

Drei ABBA-Blöcke mit zwölf getrennten Prozessen, Tiered Compilation aus, nach Ende der hier gestarteten Gastlast: 500000 tatsächliche Integeraufrufe sparen pro Probe 31594592 Heapbytes, Kandidat null. Median der drei paarweisen Hostzeitreduktionen 97,09 Prozent. Je 1000 tatsächliche GC-/CPU-Read-/Capturezyklen einer planaren 320×200-Szene entfallen 1000 zusätzliche Render; DOSE VGA 98,05 und ET4000 98,13 Prozent weniger Hostzeit in genau dieser Probe. VGA-Allokation bleibt je 56000 Byte. Eingaben, rohe Ergebnisse, alle Pixel und sämtliche gespeicherten VGA-Felder/VRAM-Planes außer dem ausdrücklich geänderten `_pixelRevision` sind gleich. `RenderedFrames` ist eine getrennte Arbeitsdiagnose. Ein anschließender echter SetReset-/Maskenstore 11h→91h ändert den Pixel und erzeugt genau den nötigen neuen Render. Keine allgemeine FILD-/x87-/Spiel-FPS-, native GL- oder Echtzeitzusage.

P1 (größerer Fetchpfad), P4 (Audiofristenrekurrenz), P5 (weitere FM-Arbeit), P6 (gemeinsame FSINCOS-Reduktion) und zusätzliche Hash-/Export-/JSON-Ideen bleiben getrennte Profil-/Optimierungsaufgaben. Der Audiorekurrenz-Prototyp besitzt keinen vollständigen PCM-/Zustandsnachweis und wird nicht in das Produkt übernommen. Die verworfenen früheren x87-Spanversuche bleiben verworfen.

## Endgültige Gast- und Zustandsprüfung

| Tatsächlicher Pfad | Starts | Volle Fortsetzungen |
| --- | ---: | ---: |
| Neuer eigener STI/PIT/ENTER/FILD-Resetgast, 486 und P54C/430FX je zweimal | 4 | 4 |
| Bestehender 486-DOS-/EMM386-Snapshotgast | 8 | 8 |
| P54C/430FX-PAM-/PCM-/x87-/Dateigast | 8 | 8 |
| Gleichzeitiger PCM-/DMA-/VDS-/OPL3-/MPU-Gast | 16 | 16 |
| Originaler DOS4GW-/Doom-Pfad | 2 | 2 |
| x87-Zahlengast in Real/V86, PE und Paging | 16 | 0 |

Damit **50 DOS-Kaltstarts und 34 volle DOS-PC-Fortsetzungen**, getrennt vier eigene Resetstarts/Fortsetzungen. 5801 weitere ganze CPU-/RAM-/Geräte-/Schedulervergleiche der DOS-Pfade, beim neuen Resetgast weitere 216. Der Resetgast prüft 24 physische IRQ0-Handler/EOIs/IRET-Rückkehrvorgänge, vier tatsächliche #SS-/IRETD-Retries und 24 rohe FILD-Ergebnisse. Sein Fristenoracle verlangt zukünftige Timerfristen und identische Fortsetzung; es ist keine unabhängige vollständige ganzzahlige PIT-Fristenrekonstruktion.

14241 negative Gastoracles insgesamt; 25 neue echte neu gehashte REP-/ATA-Importablehnungen und 16 beschädigte Audioimporte werden getrennt gezählt. 5696 externe native x87-Vektoren und je Zahlengast 220 Fälle stimmen; der Host ist keine physische Original-486-Referenz. Doom: 18 WAD-geprüfte Bilder, 195447 Stereo-PCM-Frames und tatsächlicher DOS-Prompt. Der alte 486-Harness behält seinen historischen AH4C-/30k-Nachlaufvertrag ohne neues Promptoracle. Doom-End-RAM und PCM werden wirklich im laufenden Vergleich geprüft; es gibt dort weiterhin keine gespeicherte finale RAM-/PCM-Datei für nachträgliches Rehashing.

Zwei eigene alte 9125er-Resetgäste reproduzieren separat die falsche STI- beziehungsweise ENTER-Wirkung und zählen nicht als positive Starts. Ein echter alter 9125er-CLI-Snapshotimport scheitert vor Zweiganlage; der neue CLI-Container setzt fort. Format 1 bleibt buildgebunden, Modell-/Profil-/Frequenzbindungen bleiben streng. 40 Zustandstypen, 762 Mitglieder und 32 Ereigniskörper bleiben inventarisiert. Der reguläre 486-ROM-Bytestrom und öffentliche BIOS-ABI bleiben erhalten. Vorläufe vor dem finalen Generationstoken und Oracle-/Gastdiagnosen sind ausdrücklich ausgeschlossen.

Alle 115 geschützten Eingaben und C64 bleiben unverändert, persönliche Spiel-COWs ungeöffnet. Nur neue private Testmedien beziehungsweise Zweige; keine Hostendpunkte, Windows-Eingaben, Fokusversuche, verschobene Mausprüfung oder neue Aufnahme. Der Benutzer hat für dieses geprüfte Paket ausdrücklich Commit und Push ins bestehende private GitHub-Projekt beauftragt; die tatsächlichen Git-Revisionen und Remoteprüfung stehen im privaten Gesamtnachweis. Das ist keine allgemeine Autorisierung künftiger automatischer Veröffentlichungen.

Reproduktion: vollständige Regressionen über `scripts/test.ps1`, eigener Resetgast über `python tests/run-audit2-cpu.py --build <Buildroot>`, dazu `scripts/test-snapshots.ps1`, `scripts/test-fx-host.ps1`, `scripts/test-audio-combined.ps1` und `scripts/test-x87-numeric.ps1` mit ausdrücklich eigenen privaten Medienkopien. Die benannten unabhängigen Oracles bleiben Teil der Tests.

Nächster M4-Produktschritt bleibt 82371FB/PIIX mit tatsächlichen PCI-Ressourcen, PIRQ/ELCR, IDE-Gates und Resetdomänen samt Gast- und voller Zustandsfortsetzung. Danach S3 Trio64, ATAPI, Firmware/Manager und echte Windows-95-RTM-Installation, mehrere Starts, stabile Geräte und regulärer Shutdown. M4–M8 und der stündliche Gesamtauftrag bleiben offen und aktiv.
