# 82371FB A1: IDE-Funktion und PCI-Busmaster-Teilumfang

Das zusätzliche eigene Resetprofil 11148 ergänzt CF9/INIT und getrennte CPU-/PCI-/ISA-/Geräteantworten einschließlich Abbruch offener IDE-Commits. Das bestehende IDE-Profil und der folgende10755er-Nachweis bleiben unverändert begrenzt. [Resetvertrag](PIIX-RESET.md).

Stand: 27. Oktober 2026. Die eigene IDE-/PCI-Busmaster-Vertikale ist im benannten Referenzumfang auf 10755/10755 Regressionen ohne Warnungen/Fehler geprüft und im gemeinsamen Starter integriert. 614 neue Fälle; vollständiger PIIX/430FX, Resetdomänen/CF9/INIT, Windows 95 und M4–M8 bleiben offen. Die folgenden Primärregeln und ausdrücklich begrenzten DOSE-Politiken sind keine universelle Siliziumzusage.

## Benannter Chip und Primärstellen

Maßgeblich ist das [Intel-Datenblatt 290550-002, April 1997](https://www.mouser.com/catalog/specsheets/intel%20corporation_29055002.pdf), ausschließlich der ungeschattete ursprüngliche **82371FB PIIX**. Die geschatteten 82371SB-/PIIX3-Erweiterungen, USB, SIDETIM und MIRQ0-IRQ0-Ausgabe gehören nicht zu diesem Teilmodell.

| Primärstelle, gedruckte Seiten | Gegenstand |
| --- | --- |
| 2.3.1–2.3.10, S. 47–52 | Funktion-1-Konfiguration, Command/Status, BMIBA und IDETIM |
| 2.2.13, S. 39; 3.8.3, S. 105 | Ursprüngliche MIRQ0/1-Routen und Sharing |
| 2.7.1–2.7.3, S. 78–80 | Busmasterregister, getrennte Interrupt-/Fehlerlatches, 4-KiB-Tabellengrenze |
| 3.5.1–3.5.3, S. 96–100 | Legacy-Ports, zwei 16-Bit-Transfers bei aktiviertem 32-Bit-PIO, PRDs und Abschlusskombinationen |

Das [Intel-Spezifikationsupdate 297658-004, März 1998](https://downloads.bl4ckb0x.de/download.intel.com/design/chipsets/specupdt/29765801.pdf), Tabelle S. vii, benennt für **A1 beide Funktionsrevisionen 02h**. S. 16, Klarstellung 3, korrigiert die Speicher-Sichtbarkeitszusage: Der erfolgreiche Abschluss ist nur bei Interrupt=1, Error=0 und Active=0 vollständig. Der Downloadname endet auf 01; Deckblatt und gelesener Inhalt nennen -004.

Für PCI-DRAM gilt zusätzlich der [82437FX-Vertrag 290518-002](https://bitsavers.trailing-edge.com/components/intel/pci/290518-002_82430FX_82437FX_82438FX_PCIset_DATASHEET_199612.pdf), insbesondere MAE S. 18, HEN S. 23, PAM S. 27–28 und die getrennte SMRAM-Grenze S. 32–33. Der vorhandene [DOSE-Hostteilumfang](430FX-HOST.md) behält seine ausdrücklich begrenzte Bank-, Cache-, Refresh- und SMM-Nachbildung.

## Eigene Platine und feste Konstruktion

Das neue eigene Profil heißt `dose-430fx-piix-ide-dev-v1`. Es bindet das vorhandene P54C-B5-Teilmodell an nominal 100 MHz und genau 32 MiB installierten RAM, den vorhandenen 82437FX A2 an `00:00.0`, PIIX A1 Funktion 0 an `00:07.0` und die neue IDE-Funktion an `00:07.1`. Gerät 7 ist die eigene IDSEL-Verdrahtung. Die beiden ATA-Kanäle und beide Laufwerke pro Kanal werden vor dem ersten Gastzugriff fest verbunden; kein nachträglicher CPU- oder Gerätewechsel ersetzt diese Factory-Bindung.

Die älteren 486-, P54C-AT-, FX-Host- und IRQ-Profile erhalten ihre bisherigen Konstruktoren und ihre bisherigen ATA-PIO-Verträge. Insbesondere aktiviert der alte `Intel82371FbIrq(Pic8259)` keine MIRQ-Peers; seine MBIRQ-Bytes bleiben die bisher ausdrücklich wirkungslosen Latches. Das neue Board wählt die separate Konstruktorinvariante `supportsMirq=true`.

## Funktion 1 und tatsächliche I/O-Ressourcen

| Konfigurationsregister | Konstruktion / feste Bits | Schreibvertrag |
| --- | --- | --- |
| 00h–03h VID/DID | `12308086h` | Fest; kein PIIX3-DID |
| 04h–05h PCICMD | `0002h` nach der unten benannten Quellenpolitik | RW `0005h`: IOSE.0, BME.2; MSE.1 fest eins |
| 06h–07h PCISTS | `0280h` | W1C `3800h`; andere feste Statusbits bleiben erhalten |
| 08h–0Bh RID/Klasse | `01018002h` | Fest, Kompatibilitätsmodus-IDE |
| 0Dh MLT | `00h` | `F0h`; Latenzbits ohne historische Durchsatzzusage |
| 0Eh HEDT | `00h` | Fest |
| 20h–23h BMIBA | `00000001h` | Basis `0000FFF0h`, Bit0 fest eins; 16 Byte I/O, ausschließlich 16-Bit-Portbasis |
| 40h/42h IDETIM | Je `0000h` | Je `B3FFh` für ursprünglichen PIIX; keine PIIX3-SIDETIM-/SITRE-Erweiterung |

**Quellenwiderspruch bei MSE:** Die aggregierte PCICMD-Resetzeile nennt 0000h, die Bitbeschreibung nennt MSE als fest eins. Das Intel-Update löst diesen Widerspruch nicht auf. DOSE wählt ausdrücklich das feste Bit und liest nach Konstruktion 0002h. Diese Wahl ist keine vermessene universelle Resetantwort.

Die ATA-Targetdekodierung benötigt **IOSE=1 und IDETIM.DE=1 des jeweiligen Kanals**. Primär sind 1F0h–1F7h und 3F6h, sekundär 170h–177h und 376h. Die überlappenden FDC-Ports bleiben bei der FDC; insbesondere ist Controlblock+3, also 3F7h/377h, laut Intel an ISA/FDC weiterzureichen. Die reservierten Controlblock-Lanes sind kein zusätzlicher ATA-Datenport.

IDETIM.TIME0 beziehungsweise TIME1 richtet sich nach dem tatsächlich gewählten Master/Slave aus Drive/Head. Ein aktivierter Dwordzugriff auf den Datenport führt zwei wirkliche Kanalworttransfers aus. Andere Taskfileregister behalten Bytebreite. IDETIM-Timing-, IORDY-, Prefetch- und Postingbits begründen keinen simulierten historischen Strobe-, FIFO- oder PCI-Clockverlauf.

BMIBA verschiebt das 16-Byte-Busmasterfenster. Die vollständige Basisprobe liest `0000FFF1h`; obere Adressbits werden nicht erfunden. IOSE schaltet auch dieses Fenster. Als begrenzte Boardpolitik haben feste ISA-Fabrikmappings Vorrang: Nur ein vollständig freies 16-Byte-Fenster wird dekodiert. Bei Kollision bleibt der BAR-Wert lesbar, ohne verdeckte Busmasterregisterwirkungen oder Wegnahme bestehender Geräteports. Die unveränderliche Fallbackdecoder-Topologie gehört zur vollen Zustandsbindung.

IOSE-/DE-Änderungen und BAR-Verschiebung betreffen Targetzugriffe. Sie setzen weder das ATA-Gerät zurück noch verwerfen sie einen laufenden DMA-Cursor. Das feste MSE-Bit ist kein Ersatz für diese I/O-Gates.

## PCI-physischer DRAM-Pfad

`MemoryBus.TryGetPhysicalRamSpan(address, length, permittedMapping, out bytes)` prüft die **gesamte** positive Spanne, Adressüberlauf, aktuelle DRB-Obergrenze, den vollständigen HEN-Lochbereich und jede überdeckte Mappinginstanz. Ausschließlich die ausdrücklich identische erlaubte Mappinginstanz darf ihre eigenen RAM-Schattenbytes durchreichen. Eine Ablehnung liefert keine Spanne und ruft keinerlei `IMemoryDevice`-Methode auf.

`Intel82437Fx.TryGetPciMemorySpan(address, length, write, out bytes)` prüft zusätzlich MAE und für jedes berührte C–F-PAM-Feld unabhängig RE beziehungsweise WE. Die zwölf C–E-Felder sind 16 KiB groß, das F-Feld 64 KiB. CE verändert diese funktionale Annahme nicht. Deaktiviertes RE/WE bedeutet **kein Host-DRAM-Target**, selbst wenn ein CPU-Lesezugriff dort das BIOS-ROM oder FFh liefern würde.

Diese Zugriffe verwenden PCI-physische Adressen. Sie übernehmen weder CPU-A20-Faltung noch Paging, Segmente oder ISA-DMA-Fenster. Fremde MMIO-/ROM-/VGA-Mappings werden nicht als RAM gelesen oder beschrieben. Die übrigen CPU-/ISA-Busmethoden bleiben unverändert. SMM/SMRAM wird damit nicht zusätzlich implementiert.

Die Spanne gilt nur für den gerade geprüften Ownerzugriff. Sie wird weder als Zustand gespeichert noch über Gastbefehle, Ereigniscallbacks oder Änderungen der Hostdekodierung hinweg behalten. Die Engine muss jeden folgenden PRD-/Datengrant erneut prüfen. Bereits ausgeführte RAM- und Medienwirkungen bleiben bei einem späteren Fehlschlag bestehen; der Pfad verspricht keine atomare Mehrsektortransaktion.

## MIRQ und getrennte Interruptursachen

Das neue Board verbindet primäres ATA-INTRQ mit dem gewöhnlichen hohen ISA14-Pin und sekundäres ATA-INTRQ mit **MIRQ0**. Die Busmaster-INT-Latches beobachten die tatsächlichen äußeren Quellen vor der PIRQ-/MIRQ-Auswahl. Die PIIX3-Möglichkeit, sekundäres INT stattdessen an IRQ15 zu beobachten, wird nicht übernommen.

`SetMirq(0..1, high)` speichert die getrennten hohen motherboardseitigen Eingänge. Auf einer Bridge ohne feste MIRQ-Fähigkeit wird der Aufruf ohne gespeicherte Anforderung abgewiesen. MBIRQ0/1 an 70h/71h behalten die Maske CFh, Reset 80h und dieselben elf gültigen IRQ-Nibbles wie PIRQ. Bit7 deaktiviert die Route. Bei aktiver gültiger Route verdrängt Bit6=0 den gewöhnlichen ISA-Eingang auch bei niedrigem MIRQ; Bit6=1 erlaubt das ODER mit ISA. Mehrere MIRQs werden zusammengeführt. Sharing erfordert die passende Levelauswahl in ELCR.

Intel verlangt bei gemeinsam gewähltem PIRQ und MIRQ ausdrücklich MBIRQ.Bit6=0. Bei der verbotenen Kombination Bit6=1 plus gleicher PIRQ-Route behält DOSE als begrenzte Fehlprogrammierungspolitik die PIRQ-Verdrängung von ISA bei. Ein MIRQ-Sharingbit kann diese bestehende PIRQ-Maske nicht aufheben. Reservierte IRQ-Nibbles bleiben gelatcht und unverbunden.

ATA-Statuslesen, BMISTA.INT-W1C und PIC-EOI sind drei getrennte Quittierungen. Ein W1C auf BM.INT während eines weiter hohen Drivepins setzt das Bit nicht sofort erneut; erst eine folgende echte Anforderungsflanke setzt es wieder. `nIEN` unterdrückt den äußeren Drivepin und erzeugt keine alternative erfundene BM-INT-Flanke.

## Busmasterregister und PRDs

Primär liegen Command/Status/PRDT bei BAR+0/+2/+4, sekundär bei +8/+Ah/+Ch. Byte-, Wort- und Dwordzugriffe werden vollständig mit ihren ursprünglichen Lanes geprüft.

| Register | Bits und Wirkung |
| --- | --- |
| BMICOM | RW `09h`, Reset 00h; Bit3=1 Drive→RAM (READ DMA C8h), Bit3=0 RAM→Drive (WRITE DMA CAh); Bit0 Start/Stop |
| BMISTA | RW Softwarefähigkeitsbits `60h`, W1C Error/Interrupt `06h`, RO Active `01h`; Fähigkeiten sind keine Transferfreigaben |
| PRDT | Bits31..2, Reset 0; niedrige zwei Bits ohne Adresswirkung |

Ein PRD ist acht Byte lang. Das erste Dword benennt die physische Datenbasis; das zweite enthält geraden Bytecount und EOT in Bit31. Count 0 bedeutet **65536 Byte**, kein leerer Transfer. Die Datenregion darf keine 64-KiB-Grenze überschreiten. Gerade Counts müssen keine Sektorvielfachen sein: Ein 512-Byte-ATA-Sektor kann etwa über 2+510 Byte in zwei PRDs laufen, ohne zusätzlichen ATA-Befehl, Zwischencommit oder Zwischeninterrupt.

Die gesamte PRD-Tabelle muss DWORD-ausgerichtet innerhalb **einer 4-KiB-Seite** liegen. Auch ein erster PRD an Offset FFCh wäre bereits zu groß. PRDs werden im Achtbyteschritt erst bei Bedarf gelesen; nach EOT findet kein vorsorglicher Folgezugriff statt. Die vereinzelt widersprechende Formulierung in 3.5.3 über auf 64 KiB ausgerichtete PRDs wird anhand des ausdrücklichen PRDT-Registers, der Achtbytestruktur und der dortigen Operationsfolge als Kontextfehler begrenzt. Sie wird nicht zu einer erfundenen Pflicht, jeden Puffer auf 64 KiB auszurichten.

Bei unzulässigen reservierten PRD-Bits nennt Intel keine eindeutige Originalfehlantwort. Die eigene DOSE-Politik ignoriert diese Bits; Basisbit0 und Countbit0 gelten als null. Der maskierte Count null bleibt 65536. Ein 64-KiB-Regionsüberlauf oder ein Tabellenüberlauf über die 4-KiB-Seite beendet nur den BM-Lauf, ohne neues Error-, INT- oder ATA-ABRT-Signal. Diese Fehlprogrammierungsantwort wird gezielt geprüft und nicht als Originalsiliziumantwort ausgegeben.

## Enger ATA-DMA-Handschlag

Die vorhandenen PIO-Laufwerke erhalten DMA nur über eine feste neue Boardfähigkeit. C8h/C9h lesen und CAh/CBh schreiben den eigenen funktionalen LBA28-DMA-Auftrag; es gibt keine Attrappe aus PIO-Portaufrufen. Die vier Opcodes haben in diesem Teilumfang denselben funktionalen Datenpfad, ohne zusätzlich vermessene historische Retry-/Timingunterschiede.

IDENTIFY meldet bei dieser Fähigkeit DMA in Wort49 und ausschließlich MDMA0-Unterstützung in Wort63. Das Selected-Bit wird als eigene enge Politik erst durch erfolgreiches SET FEATURES EFh/03h/20h gesetzt; PIO-Default/PIO0 und Gerätereset löschen es. DMA-Befehle benötigen keinen vorangegangenen SET-FEATURES-Aufruf. Andere DMA-/UDMA-Modi werden nicht beworben und nicht still akzeptiert. Diese Auswahlpolitik ist kein aus dem PIIX-Datenblatt abgeleiteter Laufwerksresetvertrag.

DMA bewegt vollständige Wörter aus beziehungsweise in den tatsächlichen ATA-Sektorpuffer. Normale Datenportzugriffe bewegen währenddessen keinen DMA-Cursor. Lesen veröffentlicht erst nach kopierten Bytes den Sektorfortschritt; Schreiben übernimmt erst eine volle 512-Byte-Stufe und committet sie anschließend am eigenen nominalen Geräteereignis. Es gibt keine künstlichen ATA-Zwischensektor-IRQs. Der tatsächliche terminale Drivepin und der BM-EOT-Abschluss bleiben getrennt. Driveauswahl während eines laufenden Auftrags ersetzt nicht das bereits gelatchte DMA-Laufwerk.

## Abschluss, Fehler und dynamische Gates

| Drive-INT | Active | Bedeutung gemäß Intel-Tabelle 8 |
| --- | --- | --- |
| 0 | 1 | Transfer läuft |
| 1 | 0 | PRD-Kapazität und tatsächlicher ATA-Transfer erschöpft; Erfolg benötigt zusätzlich Error=0 |
| 1 | 1 | Gültiger ATA-Abschluss vor dem Ende einer größeren PRD-Kapazität; Software muss Stop schreiben |
| 0 | 0 | Mit Error=1 PCI-Speicherproblem; bei Error=0 kann PRD-Kapazität kleiner als der ATA-Auftrag sein |

Zu frühes EOT wird damit nicht pauschal als PCI-Fehler, ATA-ABRT oder fertiger Driveauftrag ausgegeben. Der ATA-Kanal darf auf weitere Daten beziehungsweise Softwareerholung warten. Bei größerer PRD-Kapazität darf der tatsächliche Driveabschluss Active nicht fälschlich auf null verkürzen.

Ein fehlendes PCI-DRAM-Target setzt **Funktion-1-MAS und BM.Error**, beendet Active und erzeugt keine zusätzliche Drive-INT-Flanke, kein ATA-ABRT, kein CPU-Fault und kein Host-RMAS. Der Host ist bei diesem Transfer Target; sein bestehender Konfigurations-Masterabortvertrag gehört zu einer anderen Transaktionsrichtung. Erfolgreicher Abschluss wird erst nach den tatsächlichen sichtbaren RAM-Schreibwirkungen veröffentlicht.

BME-Clear ist eine eigene eng begrenzte dynamische Politik: Es pausiert neue PCI-Zyklen und ihren BM-Timer, behält Active und Cursor und setzt bei erneuter Freigabe fort. Stop=0 ist davon getrennt: Es verwirft den BM-Laufcursor und seine Fortsetzbarkeit, setzt aber weder das ATA-Gerät zurück noch rollt es frühere RAM- oder Sektorwirkungen zurück. Ein Richtungswechsel bei Active wird ignoriert, auch beim gleichzeitigen Stop; im Idle ist die Richtung änderbar. Intel verbietet den Wechsel während Active, beschreibt aber keine eindeutige Fehlantwort dafür.

PCI-Arbitration, der historische gemeinsame 32-Byte-Puffer, Prefetch-/Postinggenauigkeit, IORDY und echte PCI-/IDE-Zyklendauern sind keine nachgewiesene Siliziumpräzision dieses funktionalen Pfads. Der virtuelle Scheduler benötigt eine ausdrückliche eigene deterministische Kadenz; CPU-Takt und vier Clocks pro Step sind kein historisches Busclockversprechen.

## Zustand, Nachweise und offene Plattform

MIRQ-Fähigkeit ist eine Konstruktorinvariante; die beiden Assertionbits sind explizite Zustandswerte. Ein Import prüft obere Bits und verbietet aktive MIRQs bei fehlender Fähigkeit. Laden veröffentlicht keine neuen PIC-Pins. Die vollständige Boundaryprüfung vergleicht PIC-Peer, ELCR und alle aus ISA/PIRQ/MIRQ abgeleiteten Eingänge.

Die DMA-Fortsetzung muss zusätzlich Konfiguration, BAR-/Targettopologie, PRDT-/Regioncursor, volle 65536-Byte-Restwerte, Richtung/Start/Active/Fehler, Drivepin-Flankenhistorie, angebrochene ATA-Sektorpuffer, Callbackgeneration und tatsächliche zukünftige Fristen erhalten. Keine delegierte CLR-Closure oder gehaltene RAM-Spanne wird serialisiert. Snapshotformat 1 bleibt an Profil, Modell, Frequenz, Topologie und tatsächlichen VM-Build gebunden.

Neue unabhängige Fundamente stehen in `PciMasterMemoryTests.cs` und `PiixMirqTests.cs`. Sie decken physischen A20-Unterschied, vollständige Mapping-/Hole-/PAM-Grenzen, ausbleibende MMIO-Wirkungen, alle MIRQ-Routen, echte PIC-Acknowledgements/EOIs, Sharing, alte Konstruktoren und Importgrenzen ab. Die endgültigen Builds und Gastmatrizen sind im folgenden Abschnitt getrennt belegt.

Die tatsächlich ausgeführten DOS-/EMM386-Busmastergäste und `.dossav`-Fortsetzungen ergänzen die Gerätefälle. Sie verwenden frische eigene Scratchmedien und unabhängige Daten-/IRQ-/Dateioracles; der genaue Umfang folgt unten.

CF9/INIT und die getrennten Resetdomänen bleiben ausdrücklich offen; INIT wird nicht mit `CPU.Reset` gleichgesetzt. Ebenso offen bleiben vollständiger PIIX/430FX, S3 Trio64, ATAPI, passende Firmware-/Managerintegration sowie Installation, mehrfache Starts, stabile Geräte und reguläres Herunterfahren von Windows 95 RTM. M4–M8 und verschobene native Bediennachweise bleiben offen.


## Endgültiger Produktnachweis

Privater Gesamtnachweis `artifacts/tests/m4-piix-ide-release-path.txt`. Acht eigene DOS-/EMM386-Kaltstarts/16 tatsächliche volle Fortsetzungen, 50886 weitere ganze Vergleichspunkte und 2240 negative Gastoracles. Je Lauf PrimarySlave-C8/CA mit 134656 spezifizierten Bytes/4211 Grants, sechs echten IRQ14-Frames/EOIs, ausschließlich 130 Scratch-Schreibsektoren und echter DOS-Rückkehr aller drei PCs. VDS8103/8104, ganze 128 KiB samt Wachen/PTEs/MCBs/JFT, vollständige 64 KiB-DOSdatei und rohe x87-Werte werden unabhängig geprüft. Die ursprünglichen EMM386-Monitore bleiben unverändert; eigener STI/NOP/CLI-Pollzustand begrenzt die Schnitte. Weitere Kanäle/Drives/C9/CB sind separate Geräte-/volle-PC-Fälle. Acht alte 486-Starts/acht Fortsetzungen mit 1282 Vergleichen/200 Negativen zählen getrennt und behalten ihren historischen AH4C-/Nachlaufvertrag.

84 rehashte semantische Importfälle/167 direkte bzw. Owner-Versuche werden vor gesperrter eigener Medienbasis abgewiesen. Slots/Writability/Schreibextent, Callback-Referenzen und Cursors sind gebunden; READ-busy→IDNF und terminale One-past-Writes bleiben erlaubt. Auf eigenem eingefrorenem Vorherkern scheitern 30 neue Medienkontrollen bei 7 gültigen Kontrollen. 43 Typen/798 Mitglieder/37 Ereignisse, alte 32 Callbackkörper unverändert. Diagnosevorläufe und verworfene Fixtures sind keine endgültigen Starts; die jeweiligen tatsächlichen Diagnosezahlen stehen nur in privaten Logs. 64 Kandidaten-/normalen-/Starterdateien bytegleich, alter 10141er-Starter/823 Quellen archiviert. Native Eingabe/GL und die gesamte Windows-Plattform bleiben offen.
