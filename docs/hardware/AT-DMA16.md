# AT: gerätegetaktetes Wort-DMA auf Kanal 5–7

Stand: 25. Oktober 2026. Dieses M3-Teilpaket ergänzt den vorhandenen 8237-Register-/Blockpfad um einzelne, vom Gerät angeforderte Wortübertragungen. Der gemeinsame Build besteht **4238/4238 Regressionen ohne Warnungen/Fehler**, darunter **133 neue Gerätefälle**. Die DMA-Grundlage allein ist keine Soundkarte. Der später darauf aufbauende [CT1740-/DSP4.05-PCM-Kern](SB16-CT1740-PCM.md) besitzt eine getrennte Teilabnahme. Vollständiges M3 bleibt offen.

## Übertragungsvertrag

`Dma8237.TryReadWord(channel, out value)` liefert ein Wort aus dem Speicher an das Gerät, `TryWriteWord(channel, value)` überträgt in Gegenrichtung. Beide akzeptieren ausschließlich Kanal 5, 6 oder 7. Ungültige Kanäle einschließlich des Kaskadenkanals 4 werden vor einem Zugriff abgewiesen. Die bisherigen Bytepfade für FDC und SB2 bleiben bestehen.

Ein erfolgreicher Aufruf bedeutet genau **einen** Wortgrant. Die Zeitpunkte bestimmt das anfordernde Gerät über den vorhandenen Scheduler. Ein maskierter Kanal oder deaktivierter zweiter Controller liefert `Blocked`; Verify, falsche Richtung, ungültiger Operationstyp und Cascade liefern `InvalidMode`. Dabei werden Speicher, Adresse, Zähler und die vorherige geräteseitige Terminal-Count-Anzeige nicht verändert. Ein fehlgeschlagener Leseaufruf gibt null zurück. Geräte dürfen dabei keine Nutzdaten verbrauchen.

Die AT-Adresse entsteht aus `(Page & FEh) << 16 | Address << 1`. Das Adressregister zählt Worte, das Count-Register enthält die verbleibende Wortzahl minus eins. FFFFh erlaubt 65536 Worte. Das niedrige Page-Bit trägt keine Adresse bei; ein Adressüberlauf bleibt innerhalb derselben 128-KiB-Seite. Inkrement und Dekrement ändern das Adressregister jeweils um eins. Wortdaten laufen in Little-Endian-Reihenfolge über den bestehenden Bytebus, einschließlich seiner ROM-/MMIO-Zuordnungen.

Beim Übergang des Zählers von null auf FFFFh entstehen Terminal Count und das entsprechende Statusbit. Ohne Auto-Init wird der Kanal maskiert; mit Auto-Init werden Basisadresse und Basiszähler wieder geladen. Das Lesen des Status löscht seine TC-Bits, nicht das Ergebnis des zuletzt gewährten Transfers. **Master Clear löscht nun auch diese geräteseitige Abschlussanzeige**; vorher konnte sie nach dem Controller-Reset noch einen alten Abschluss melden. Der Reset des zweiten Controllers verändert den ersten nicht.

Demand, Single und Block sind auf Ebene einzelner vom Gerät angeforderter Grants geprüft. Vollständige Burst-/DREQ-Haltezyklen, Prioritätsarbitration, HRQ/HLDA/HOLD, elektrische DACK-/EOP-Leitungen und Busdauer werden hierdurch nicht modelliert. Software-Requests und allgemeine Memory-to-Memory-Dienste erhalten keine neue Abnahme. Auch die gemeinsame A20-Faltung des bestehenden MemoryBus gilt vorerst weiter für DMA; eine vom CPU-A20-Gatter unabhängige originale AT-Adressverdrahtung ist hier nicht nachgewiesen.

## Geräteprüfungen

Die neuen Fälle prüfen alle drei Kanäle und beide Richtungen mit Demand/Single/Block, Auto-Init, Dekrement, Maskierung, Controller-Disable, TC und Wiederanlauf. Dazu kommen die Grenze zwischen zwei 64-KiB-Hälften, der tatsächliche 128-KiB-Umlauf, nicht adresswirksames Page-Bit null, 65536 einzelne Wortgrants, getrennte Flipflops/Statusregister beider Controller, ungültige Kanäle und Modi sowie ROM und teilweise gemapptes MMIO. Ein zusätzlicher Zeitfall prüft unveränderten Speicher vor der Gerätefrist, blockierte Anforderungen ohne Quellenverbrauch und die erste spätere erfolgreiche Übertragung.

## Tatsächlicher DOS-Gast

Der eigene `DMA16.COM` läuft in **acht Kaltstarts**: MS-DOS 6.22, FreeDOS und originales EMM386 NOEMS/RAM jeweils zweimal. Es verkleinert seine COM-Reservierung, reserviert anschließend selbst 256 KiB über DOS und wählt darin ein vollständig eigenes, ausgerichtetes 128-KiB-Fenster. Nur der Gast initialisiert die Testworte und programmiert die echten 8237-Ports. Vor DOS-Freigabe beobachtet der Prüfläufer den tatsächlichen MCB mit Eigentümer und Länge; ein frei erfundener Pufferzeiger genügt dem Oracle nicht.

Eine **ausdrücklich synthetische Testkarte** im Regressionstestprogramm erzeugt die Geräteanforderungen im Abstand von 100000 virtuellen Nanosekunden. Sie liest oder schreibt je Frist über die neuen DMA-APIs ein Wort. Sie ist weder SB16 noch ein BIOS-/DOS-Ersatz und wird im normalen Headless-Runner und in der Anwendung niemals angeschlossen. Sie besitzt keine Host-Audio-/Mikrofon-/MIDI-/Netzverbindung. Die gewählte Anforderungsrate ist ein Testparameter, keine ISA- oder SB16-Taktzusage.

- Ohne Monitor: je **48 Fälle, 54 Anforderungsblöcke, 228 Gerätefristen, 156 übertragene Worte und 42 TC-Ereignisse**. Alle Modi und auch absichtlich falsche Programmierungen werden geprüft.
- Unter EMM386: je **30 Fälle, 33 Blöcke, 153 Gerätefristen, 126 Worte und 33 TC-Ereignisse**. VDS sperrt das gesamte 128-KiB-Fenster mit `8103h/DX=002Ch` ohne Remapping oder Bounce-Puffer. DDS-Länge, physische Identitätsadresse und Buffer-ID null werden geprüft. `810Bh/810Ch` für Kanal 5, 6 und 7 sind ausgeglichen; anschließend wird tatsächlich mit `8104h` entsperrt. Neun logische VDS-Aufrufe können wegen V86-Weiterleitung und Monitorreflexion als 18 Interrupt-Eintritte erscheinen; der Bericht bewahrt beide Ebenen.

EMM386 ist kein transparenter Portdurchleiter: Ohne `810Bh` beendet es den Auto-Init-Test mit seiner eigenen Meldung „DMA-Modus nicht unterstützt“. Auch mit abgeschalteter Übersetzung werden Controller-Disable und bestimmte Nicht-Daten-Modi nicht als Rohhardwaretest abgenommen. Beim Dekrement verändert der lokale Monitor den Anfang des physischen Bereichs; deshalb liegen die strengen Roh-Dekrementfälle in der Matrix ohne Monitor. Diese beobachtete Monitorantwort wird nicht durch eine Änderung der Hardware zugedeckt. Außerdem entfernt EMM386 das nicht adresswirksame Page-Bit vor dem physischen Portschreiben, während seine virtuelle Registerantwort das ursprüngliche Bit enthält. Beide Antworten werden getrennt geprüft.

Diese VDS-Probe gilt für einen konventionellen zusammenhängenden Puffer bei 8 MiB. Sie ist keine allgemeine DMA16-/VDS-Abnahme für EMS-Bounce-Puffer, andere Manager, Windows, beliebige Speicherbelegung oder echte Soundkarten. Die Proben restaurieren Masken und geben ihren DOS-Puffer frei, verändern aber weiterhin ihre zuvor programmierten DMA-Adressen/Modi und ihre Arbeitsregister. Sie sind kein vollständiger CPU-/DMA-/Scheduler-Snapshot.

## Unabhängiger Nachweis

`tests/verify-dma16.py` verwendet ein getrenntes ganzzahliges Adress-/Zählermodell. Es vergleicht jeden tatsächlich aufgezeichneten Gerätezeitpunkt, Wert, Status und TC sowie die vom Gast gelesenen Register und alle 32 Daten-/Wachworte pro Fall. Auch Auto-Init über mehrere Blöcke, verzögerte Freigabe nach Maskierung, ungültige Anforderungen ohne Speicherwirkung, physische gegenüber virtuellen Portwerten, ausführbare Programmbytes, MCB, DDS, VDS-Aufrufe und DOS-Rückkehr werden geprüft. Insgesamt entstehen **312 Gastfälle, 1524 Gerätefristen, 1128 Worttransfers und 300 TC-Ereignisse**; **208 absichtliche Verfälschungen** scheitern. Wiederholungen sind in virtueller Zeit, Bild- und Nachweishash identisch.

Auf denselben endgültigen DLLs bestehen zusätzlich **35 bisherige Byte-DMA-Gaststarts**: sechs FreeDOS-FDC-Starts mit Schreiben/Medienwechsel, 13 originale EMM386-/VDS-/FORMAT-Starts, je zwei normale/High-Speed-SB2-Ausgabestarts, vier ADPCM- und acht ADC-/Aufnahme-/Wiedergabestarts. Zusammen mit DMA16 **43 akzeptierte Kaltstarts**. Unveränderte CPU-/x87-/Grafikpakete wurden ohne neuen Regressionsgrund nicht vollständig wiederholt; deren historische Abnahmen gelten für ihre dokumentierten Builds. Es gibt keine neue Performance-, interaktive Doom- oder native Mausabnahme.

Die Quelldaten, getesteten DLLs, regenerierten Programme, einzelnen Gastberichte und unveränderten privaten Eingaben sind im ignorierten Gesamtnachweis über `artifacts/tests/m3-dma16-release-path.txt` dokumentiert. Originale und Benutzer-Spielkopien bleiben erhalten. Keine Windows-Fenster, Fokusversuche oder nativen Eingaben.

## Reproduktion und nächster Schritt

```powershell
.\scripts\test-dma16.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow> `
  -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne die drei optionalen Managerpfade laufen die vier MS-DOS-/FreeDOS-Starts. Ohne `-SkipBuild` wird zuerst gebaut. Der Test erzeugt frische private Kopien der vorgegebenen Overlays und eine FAT12-Diskette mit ausschließlich dem eigenen Programm. Die auf dieser Grundlage inzwischen entstandene [CT1740-/DSP4.05-PCM-/CT1745-Teilimplementierung](SB16-CT1740-PCM.md) besitzt eigene echte DOS-/EMM386-Nachweise. Danach folgen YMF262/OPL3 und die übrigen M3-Abhängigkeiten. Diese getrennte Folgeabnahme verändert den historischen Umfang dieses DMA-Pakets nicht.

## Primärquellen

- IBM, [Personal Computer AT Technical Reference, März 1984](https://www.minuszerodegrees.net/manuals/IBM_5170_Technical_Reference_1502243_MAR84.pdf), System Board 1-12 bis 1-14: Wortkanäle, Adressverdrahtung, 128-KiB-Grenze und Programmierung.
- Intel, [8237A, Order 231466-005, September 1993](https://studylib.net/doc/8146290/8237a-high-performance-programmable-dma-controller), archivierter Datenblatttext: Register, Maskierung, TC, Auto-Init und Master Clear. Kein fremder Emulatorcode übernommen.
- Microsoft, [PW0519 / Q93469, Virtual DMA Services, Revision 9/92](https://jeffpar.github.io/kbarchive/kb/093/Q93469/): DDS, 128-KiB-Ausrichtung, Sperren/Entsperren und Übersetzungszähler.
