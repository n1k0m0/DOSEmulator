# 82371FB A1: begrenzte PIRQ-/ELCR-Vertikale

Die neue eigene Resetplatine 11148 verwendet PCICMD.SCE ausschließlich für ihren modellierten tatsächlichen CPU-Shutdown→INIT-Pfad. Alte IRQ-/IDE-Profile behalten die bisherige Latch-/Hotresetgrenze. [Getrennte Resetdomänen](PIIX-RESET.md). Die folgenden IRQ-Abnahmedaten gehören weiterhin ihrem benannten historischen Build.

Aktueller zusätzlicher Teilumfang: Das separate [IDE-Profil](PIIX-IDE.md) aktiviert ursprüngliche MIRQ0/1-Fähigkeit und Funktion1. Das hier historisch abgegrenzte IRQ-Profil behält seine PIO-Kanäle und wirkungslosen MBIRQ-Latches; keine stille Umdeutung seines10141er-Vertrags. Build 10755 prüft die neue Integration separat; CF9/INIT/Resetdomänen bleiben offen.

Stand: 27. Oktober 2026. Dieses Paket integriert einen begrenzten Ausschnitt der benannten PCI-Funktion 0 und ihre PIRQ-/ELCR-Auswahl mit dem vorhandenen DOSE-PIC-Paar. Es ist ein weiterer M4-Produktschritt nach dem [82437FX-Hostcontroller](430FX-HOST.md). IDE-Funktion 1, CF9, INIT und ein vollständiges historisches PIIX-/430FX-/Windows-95-System bleiben offen.

## Primärvertrag und eigene Platine

Primärquelle ist das [Intel-Datenblatt 290550-002, April 1997](https://www.mouser.com/catalog/specsheets/intel%20corporation_29055002.pdf). Maßgeblich sind die ungeschatteten Beschreibungen des ursprünglichen 82371FB: Abschnitte 2.2.1–2.2.13, 2.5.3.9–2.5.3.10 und 3.8.2–3.8.3. Die geschatteten PIIX3-Erweiterungen werden nicht übernommen. Das [Intel-Spezifikationsupdate 297658-004, März 1998](https://downloads.bl4ckb0x.de/download.intel.com/design/chipsets/specupdt/29765801.pdf), Tabelle auf Seite vii, ordnet dem 82371FB A1 beide Funktionsrevisionen 02h zu. Der Downloadname endet auf 01; Deckblatt und Inhalt des gelesenen Dokuments nennen ausdrücklich -004.

| Eigenschaft | Vertrag dieses Teilumfangs |
| --- | --- |
| Neue eigene Platine | `dose-430fx-piix-irq-dev-v1`, weiterhin eine begrenzte DOSE-Validierungsplatine |
| CPU und RAM | Festes vorhandenes P54C-B5-Teilmodell, nominal 100 MHz, genau 32 MiB installierte Bytes und vorhandener 82437FX A2 |
| Neue Funktion | Genau `00:07.0`, Intel `8086:122E`, Revision `02`, Klasse `060100` |
| Adressentscheidung | Gerät 7 ist die eigene Board-/IDSEL-Verdrahtung; die Teilenummer allein legt diese BDF nicht fest |
| PIC | Das neue Board wählt ausdrücklich das optionale pegelgeeignete vorhandene PIC-Paar |
| Vorhandene Profile | Die bisherigen 486-, P54C-AT- und reinen FX-Hostprofile behalten ihre bisherigen Bauteile und Auswahlgrenzen |
| Fehlende Funktionen | Kein Ziel an `00:07.1` oder Funktionen 2–7, keine erfundene IDE-/USB-Identität |

Die beiden bisherigen ATA-Kanäle bleiben in diesem Paket die vorhandenen eigenen DOSE-Controller. Ihre funktionierenden Ports sind kein PIIX-IDE-Nachweis. Auch die ursprüngliche Multifunktionskennung 80h der tatsächlich nachgebildeten Funktion 0 ersetzt keinen noch fehlenden Funktion-1-Vertrag.

## Konfigurationsbytes und Grenzen

Der vorhandene [Mechanismus-1-Transport](PCI-CONFIGURATION.md) liefert eine vollständige Byte-, Wort- oder Dword-Transaktion mit ihren ursprünglichen Lanes. Die Funktion prüft Breite und den vollständigen Bereich 00h–FFh vor jeder Wirkung; unzulässige direkte API-Aufrufe werfen eine Argumentausnahme. Eine gültige breite Schreibtransaktion übernimmt zuerst ihre Registerbytes und veröffentlicht anschließend die kombinierte PIRQ-Auswahl einmal. Es entstehen keine Zwischenrouten durch versteckte Byte-Portzugriffe.

| Register | Konstruktion | Schreibbare Bits und tatsächlicher Umfang |
| --- | --- | --- |
| 00h–03h VID/DID | `122E8086h` | Fest |
| 04h–05h PCICMD | `0007h` | Nur `0008h`; SCE ist ein lesbares Latch. PCI-Shutdown-Sonderzyklen sind nicht implementiert |
| 06h–07h PCISTS | `0200h` | W1C `3800h` für MA/RTA/STA; Nullschreiben erhält die Bits. In diesem Teilumfang existiert kein erzeugender ISA-PCI-Master-/Target-Abort-Pfad |
| 08h–0Bh RID/Klasse | `06010002h` | Fest |
| 0Eh HEDT | `80h` | Kein unabhängiges Schreibregister; Bit 7 folgt MSTAT.2 |
| 4Eh XBCS | `03h` | Maske `F7h`, in diesem Paket ausdrücklich nur Latches ohne neue KBC-/RTC-/ROM-/FERR-/Mauswirkung |
| 60h–63h PIRQ A–D | je `80h` | Maske `8Fh`, echte Auswahl und Pinwirkung wie unten |
| 6Ah–6Bh MSTAT | `0004h` | Nur `0004h`; gewählte Straps: normales ISA-DMA, ISA-Taktteiler 4. Bit 2 verändert HEDT.7 |
| 70h–71h MBIRQ0/1 | je `80h` | Maske `CFh`, nur Latches; MIRQ-Peers und deren Sharing sind noch nicht implementiert |

Der MSTAT-Resetwert ergibt sich aus dem dokumentierten Default von Bit 2 und den ausdrücklich gewählten Boardstraps; er ist kein universeller strapunabhängiger Chipwert. Die PIIX3-Bits für USB, APIC und IRQ0-Ausgabe gehören nicht zum ursprünglichen PIIX.

Andere Bytes lesen in dieser begrenzten Implementierung null und ignorieren Schreiben. Darunter befinden sich auch echte, noch unimplementierte PIIX-Steuerregister wie IORT, TOM, MBDMA, PCSC und SMI-/Clocksteuerung. Deren Nullantwort ist weder die historische Resetantwort noch eine Umdeutung in reservierte Hardware. Ebenso sind die oben gelatchten XBCS-/MBIRQ-Bits keine nachgewiesene Funktion. Allgemeine reservierte Siliziumantworten werden durch diese eigene Nullpolitik nicht zugesagt.

Unbekannte BDFs bleiben offen und benutzen den bestehenden Host-RMAS-Pfad. Sie setzen keine erfundenen PIIX-Fehler. Das Speichern zulässiger MA/RTA/STA-Werte und anschließende echte W1C-Schreiben sind getrennt von einem noch nicht vorhandenen erzeugenden PCI-Fehlerpfad.

## PIRQ-Auswahl und Quellen

`SetPirq(pin, asserted)` benennt PIRQA bis PIRQD mit 0 bis 3. `asserted=true` bedeutet die logische Anforderung des elektrisch aktiv niedrigen PCI-Pins. Die API wird nur auf dem PC-Owner von kontrollierten virtuellen Quellen verwendet; sie verbindet keine Hostinterrupts oder Geräte.

| PIRQ-Byte | Bedeutung |
| --- | --- |
| Bit 7 = 1 | Routing deaktiviert |
| Bit 7 = 0 | Die gültige IRQ-Auswahl in Bits 3..0 ist aktiv |
| Bits 6..4 | Lesen null; Schreiben ohne Wirkung |
| Gültige Nibbles | 3, 4, 5, 6, 7, 9, 10, 11, 12, 14, 15 |
| Reservierte Nibbles | 0, 1, 2, 8, Dh bleiben gelatcht, erzeugen in DOSE aber keine Route |

Die letzte Antwort bei Fehlprogrammierung ist eine ausdrückliche eigene Politik. Sie behauptet keine vermessene Reaktion eines Originalchips auf reservierte Werte.

Jede gültige aktivierte PIRQ-Route verdrängt den gewöhnlichen ISA-Pin desselben IRQ bereits durch ihre Auswahl, auch wenn der PCI-Pin gerade nicht anfordert. Die verdrängte ISA-Leitung bleibt separat gespeichert. Deaktivieren oder Wegverlegen der letzten solchen Route macht ihren aktuellen Zustand wieder sichtbar. Alle vier PIRQs dürfen denselben IRQ auswählen; die normalisierten Anforderungen werden mit ODER zusammengeführt. Eine einzelne Freigabe darf die Anforderung eines anderen PCI-Pins nicht löschen.

`SetIsaLine(irq, high)` hält die gewöhnlichen hohen ISA-Eingänge unabhängig von der Routenauswahl. IRQ2 ist die private PIC-Kaskade und wird ebenso wie Nummern außerhalb 0–15 abgewiesen. `PirqAssertions`, `IsaLines` und `Elcr` sind lesbare Diagnosen der gespeicherten Quellen.

Je API-Pin gibt es in dieser Vertikale genau einen gespeicherten äußeren Pegel. Sollten später mehrere Geräte denselben physischen PIRQ teilen, muss ihr Board-Owner die Gerätequellen vor diesem Pin konsistent zusammenführen. Dieses Paket besitzt noch keine reale PCI-Karte und behauptet keinen solchen zusätzlichen Karten-/Quellen-/INTx-Nachweis. MIRQ-/ISA-Sharing bleibt ausdrücklich unverdrahtet.

## ELCR und tatsächliche PIC-Wirkung

ELCR liegt ausschließlich an den Byteports 4D0h und 4D1h. Beide starten mit 00h. Die Masken sind unten `F8h` und oben `DEh`, also zusammen `DEF8h` in der Konvention Bit N = IRQ N. IRQ0, IRQ1, IRQ2, IRQ8 und IRQ13 bleiben flankengesteuert. Gültige Wort-/Dwordzugriffe sind in dieser DOSE-Vertikale ungetrieben und wirkungslos; sie führen keine versteckten Bytezugriffe aus. Benachbarte Byteports sind offen.

Der Wert 0 an einem erlaubten ELCR-Platz wählt die steigende Flanke, der Wert 1 den hohen internen Anforderungspegel. Die elektrisch aktiv niedrigen PIRQs werden vor diesem PIC-Eingang normalisiert. Die Routenauswahl setzt ELCR nicht automatisch: Intel verlangt, dass Software den für PCI gewählten IRQ auf Level stellt.

Ein unverändert hoher Flankeninput erzeugt nach EOI keine neue Flanke. Ein weiterhin hoher Levelinput wird nach passendem EOI erneut angefordert; eine PIC-Maske kann dies verzögern, verwirft die äußere Quelle aber nicht. Deassertieren löscht eine noch nicht bestätigte Levelanforderung, niemals einen bereits gesetzten ISR-Eintrag. Die echte Slave-/Master-Kaskade und ihre Quittierungen bleiben getrennt. Eine bereits gelatchte Master-Kaskadenflanke kann nach Zurückziehen einer Slaveanforderung weiterhin zum dokumentierten spurious IRQ15 führen; der Router darf sie nicht pauschal wegputzen.

Das pegelgeeignete PIC-Paar behält den ELCR-Vertrag auch über seine ICW-Initialisierung. ICW1.LTIM ersetzt beim PIIX die ELCR-Auswahl nicht. Der parameterlose alte PIC-Konstruktor bleibt erhalten und aktiviert diesen neuen Modus nicht implizit. Weitere 8259-Modi und elektrische PCI-/ISA-/Synchronisationstakte sind dadurch nicht allgemein abgenommen.

## Zustand, Reset und offene Plattform

Gesichert werden die 256 Konfigurationsbytes, der ELCR-Wert und beide getrennten äußeren Quellenbestände zusammen mit dem vollständigen PIC-, CPU-, RAM-, Geräte- und Schedulerzustand. Der Konstruktorpeer ist fest. Am vollständigen Haltepunkt muss dieselbe PIC-Instanz pegelgeeignet sein; Levelmaske und zusammengeführte physische Eingänge müssen exakt zur Bridge passen. Die private Kaskade bleibt Eigentum des PIC-Paares.

Ein Import prüft feste Registerbits, die MSTAT-/Headerbeziehung, die erlaubte ELCR-Maske, vier gültige PIRQ-Assertionbits und den ausgeschlossenen äußeren IRQ2. Das Laden der Bridge veröffentlicht keine neuen Pins und erzeugt keine Flanken. Die vollständige neue Maschine erhält die gespeicherten PIC-Latches; die übergreifenden Beziehungen werden vor ihrer Übernahme geprüft. Es gibt keinen neuen Scheduler-Callback, Hosttimer oder Hostclockzugriff. Format 1 bleibt buildgebunden.

Ein CPU-only Hotreset erhält diese versorgte Bridge und den bisherigen Geräte-/IRQ-Zustand. Eine neue kalte Factory erzeugt die oben genannten Konstruktorwerte. Eine allgemein verwendbare Bridge-Resetmethode wird bewusst nicht als historische Resetdomäne angeboten.

CF9 und seine Trennung von Dword-CF8 bleiben offen. Laut Intel benötigt RCPU eine 0→1-Flanke und liest trotzdem null; Softreset verwendet INIT, Hardreset CPURST/PCIRST#/RSTDRV und andere Domänen. Die vorhandenen CPU-Reset-/PowerCycle-APIs sind keine automatische Umsetzung dieser Regeln. Ebenso bleibt der Quellenwiderspruch der zukünftigen IDE-Funktion sichtbar: PCICMD-Reset 0000h gegenüber der Beschreibung von MSE als fest eins. Die eindeutigen späteren IOSE-/IDETIM-Gates dürfen dadurch nicht ersetzt werden.

Nach dieser Vertikale folgen die wirkliche PIIX-IDE-/Ressourcen-/Resetintegration und der PCI-Busmaster-Speicherpfad, danach S3 Trio64, ATAPI, Firmware-/Managerpfad und die tatsächliche Windows-95-RTM-Abnahme. Die Pflichtnachweise für M4 bis M8 bleiben offen.

## Nachweise

Die neue Gerätefamilie steht in `tests/DOSEmulator.Tests/PiixIrqBridgeTests.cs`. Sie prüft unabhängige A1-Headerbytes, Registerlanes und W1C, alle gültigen und reservierten PIRQ-Auswahlen, echte physische PIC-Acknowledgements/EOIs, geteilte Quellen, ISA-Verdrängung und -Rückkehr, Levelmasken sowie Lese-/Ablehnungsfreiheit von Teilwirkungen.

Der endgültige Release besteht **10141/10141 Regressionen** mit 0 Warnungen/Fehlern; 322 neue Fälle: 122 PIC-, 167 Bridge- und 33 ganze PC-/Owner-/Importfälle. Die optionale Levelauswahl erhält gehaltene Pins bei ICW1, reassertiert nach EOI/AutoEOI und behandelt die fixe Masterkaskade ausdrücklich. Moduswechsel sind eigene Politik: neu zu Level geschaltete niedrige Pins verlieren alte ungequittierte Edge-IRR, hohe werden ohne Callback ausgewertet; Level→Edge erzeugt keine Flanke und erhält ISR/IRR. Die alten Konstruktoren behalten ihren Edgepfad.

Acht endgültige eigene PIRQIRQ.COM-DOS-Kaltstarts und acht volle 32-MiB-Fortsetzungen unter MS-DOS/FreeDOS/originalem EMM386 NOEMS/RAM je zweimal bestehen. **590 weitere ganze CPU-/RAM-/Geräte-/Schedulervergleiche**, 64 ursprüngliche Handler/Frames für IRQ10/11, 128 geordnete EOIs, 96 kontrollierte virtuelle Pinwechsel, 208 echte PCI- und 80 ELCR-Zugriffe. Pro Lauf werden die sieben nach dem Schnitt liegenden Handler zusätzlich auf dem restaurierten PC wirklich ausgeführt und ihre Publikationen verglichen. Geteilte A/B-Quelle, weiter gehaltener Pegel, idle aktive Route gegen ISA10, Rückkehr des schon hohen ISA-Pins, C an IRQ11 und Umrouten des gehaltenen D-Pins werden durch echte Gastwirkungen geprüft. Der Schnitt liegt nach dem ersten Handlerzählercommit vor EOI mit aktivem PCM, roher x87, offenem tatsächlichem AH3D-Handle und zukünftigen PIT-/PCM-/UART-/MPU-Ereignissen. Der lebende Handle/JFT-Eintrag ist keine vollständige SFT-Semantik; die konventionellen Seiten werden durch originale Tabellen gelesen und inhaltlich verglichen, ohne zusätzliche exakte PTE-/Rechtebitidentitätszusage.

Die unabhängigen Oracles rekonstruieren 448 gespeicherte virtuelle Clockpunkte ganzzahlig, prüfen unveränderte eigene Befehle, MCB/PSP/private Stack-/Framebereiche, tatsächliche Rückkehradressen/Flags/EOIs, alle benannten PCI-/ELCR-Zugriffe, eigene DOS-Dateien und rohe x87-Werte. **1848 verfälschte Gastoracles** werden verworfen. Originales EMM386 bleibt aktiv und reflektiert die realen PIC-Anforderungen in eigene V86-DOS-Handler; kein ersetzter Monitor/IRQ-Handler oder Privileginjektor. Alle acht Gäste erreichen einen tatsächlichen nachfolgenden DOS-Prompt. Vier Wiederholungspaare sind vollständig Zustands-/RAM-/IRQ-/PCM-/Dateihashgleich. Audio ist durch echte DMA und vollständige Original-/Restaurierungsidentität geprüft, kein zusätzlicher unabhängiger Synthesizer.

Getrennt acht erhaltene 486-DOS-/EMM386-Kaltstarts und acht volle 8-MiB-Fortsetzungen bestehen mit 1282 weiteren ganzen Vergleichen und 200 negativen Oracles; deren historischer AH4C-/30k-Nachlaufvertrag wird nicht als neuer Promptnachweis ausgegeben. Gesamt 16 DOS-Kaltstarts/16 Fortsetzungen, 1872 weitere ganze Vergleiche, 2048 negative Gastoracles. 15 neue echte rehashte Containerfehler werden jeweils direkt und über den Session-Owner vor Medienöffnung abgewiesen; alte Maschine/Revision/Generation bleiben erhalten und kein Zweig entsteht. Ein wirklicher alter 9792/9819-CLI-Buildimport zählt separat. Neuer tatsächlicher CLI-Speicher-/Ladepfad am eigenen Testsektor ist erfolgreich und zählt nicht als zusätzlicher DOS-Start.

41 explizite Zustandstypen/770 Mitglieder/32 unveränderte Ereigniskörper. Kein neuer Scheduler-Callback oder Formatwechsel. 64 Kandidaten-/normalen Produktdateien sind bytegleich und im gemeinsamen Starter installiert; 31 ändern sich gegenüber 9819. Ganze 9819er-Binaries/810 Quellen sind privat archiviert. CPU-/Core-/x87-/Float80-Quellen und Default-ROM sind unverändert. Aktuell 823 Quellhashes, 700 lizenz-/kommentargeprüfte Code-/Projektdateien, 143 Python/95 PowerShell. Es wurden ausschließlich sechs bekannte eigene frühere DOS-Fixtures und frische Basen/COWs verwendet; persönliche COWs und Originalmedien blieben ungeöffnet. Die historische 115er-Schutzliste wurde nicht neu pauschal gehasht. C64 wurde nicht bearbeitet; DOSE-HEAD bleibt der bisherige menschlich autorisierte e1b0d6f. Keine neuen Commits/Pushes, nativen Eingaben, Hostendpunkte oder Aufnahmen.

Reproduktion: `scripts/test-piix-irq.ps1` mit expliziten Disk-/Overlay-/FreeDOS-/EMM386-Pfaden und neuem Ausgabeziel; nur eigene frische Kopien. Vollständiger privater Gesamtnachweis: **artifacts/tests/m4-piix-irq-release-path.txt**. Frühere Diagnose-/Fixturestarts sind darin ausdrücklich von der endgültigen 8er-Matrix getrennt. Der synthetische Pinpeer ist keine Originalkarte, kein Gasttreiber- oder Win95-Nachweis. M4–M8 und die native Bedienabnahme bleiben offen.
