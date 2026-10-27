# Gleichzeitige CT1740-PCM-/OPL3-/MPU-Prüfung

Stand: 26. Oktober 2026. Ergänzender Gastnachweis auf dem **6247er-VM-Build**; keine Erweiterung der emulierten Chips. Die fünf Snapshot-VM-DLLs und der normale Headless-Starter bleiben gegenüber dem vollständigen [Snapshotpaket](../SNAPSHOTS.md) bytegleich. Das Prüfprogramm und seine Oracles sind neu.

## Konkreter Gastpfad

Der eigene `COMBINED.COM`-Gast reserviert selbst DOS-Speicher, richtet einen 128-KiB-ausgerichteten DMA-Bereich ein und benutzt unter originalem EMM386 die wirklichen VDS-Aufrufe 8103h/810Bh/810Ch/8104h. Er programmiert DMA8 und DMA16, den DSP und die FM-/MPU-Ports durch normale Gast-I/O. Ein kurzer 16-Komponenten-DMA8-Block und ein 16-Komponenten-DMA16-Block bleiben zunächst unquittiert. Eine ausdrückliche virtuelle MPU-Eingabe stellt die dritte Quelle bereit. Der erste physisch quittierte Karteninterrupt sieht tatsächlich **Mixer82=7 aus zwei DMA-Abschlüssen und einem vollständigen MIDI-Eingangsframe**. F2h/F3h kommen in dieser Probe nicht vor.

Ein anschließender **16,384 Sekunden** langer Stream spielt **512 Auto-init-Blöcke mit je 512 signierten 16-Bit-Stereokomponenten bei 8000 Stereo-Frames/s**. Das sind 262144 Komponenten beziehungsweise 131072 Stereo-DAC-Frames im langen Teil. Der Gast sendet nach jedem tatsächlichen Blockende drei rohe MIDI-Bytes; die virtuelle Gegenstelle liefert unabhängig davon 512 Dreierpakete. Beide Richtungen halten die vollständigen 31250-Baud-/8N1-Frames mit je 320000 ns ein. Zwei getrennt geroutete YMF262-Zweoperatorstimmen laufen gleichzeitig links/rechts weiter. Die EGT-Bits halten beide Hüllkurven bis zum ausdrücklichen Key-off; Tonhöhe und nicht verschwindende Ausgabe werden zu Beginn, in der Mitte und am Ende geprüft.

Der eigene Handler leert die MPU-Eingabe und quittiert DSP8 an +Eh sowie DSP16 an +Fh getrennt. Ein breites IN an +Fh darf keine Quelle quittieren; zusätzlich lässt jeder absichtliche +Eh-Zugriff bei laufender Wortquelle deren Bit gesetzt. D9h beendet Auto-init erst am Ende des letzten Blocks. Physische PIC-Acknowledgements, tatsächliche Handlerquellen und Master-/Slave-EOIs sind unabhängig von den Gastzählern rekonstruiert. Ein Profil benutzt IRQ5/DMA1/DMA5/P330, das andere IRQ9 über den Slave-PIC, DMA0/DMA7 und P300.

## Laufende Sicherung und Fortsetzung

Nach 5,440 Sekunden des langen Streams wird unmittelbar nach dem nächsten äußeren Paket am Owner-Schnittpunkt gespeichert. Zu diesem Zeitpunkt laufen PCM-Auto-init, FM-Hüllkurven, nichtnullige Audiofilter und ein noch nicht vollendeter MPU-Eingangsframe. Die Probe flusht den Mixer an diesem Schnittpunkt nicht.

Ein absichtlich beschädigter neuer Container wird zuerst wirklich importiert und abgewiesen. Dabei bleiben CPU/RAM/Geräte/Fristen der alten Maschine unverändert und der abgewiesene Zielzweig entsteht nicht. Anschließend wird der gültige Container auf einem zweiten PC mit frischen Journalen geladen. Beide Maschinen erhalten dieselben weiteren äußeren Pakete und laufen getrennt weiter: vollständiger CPU-/RAM-/Geräte-/Schedulervergleich nach dem Import, alle 50 ms und am Ende; zeit-/form-/bytegenaue Vergleichsdigests aller weiteren PCM-Blöcke, nativen FM-Quads, DMA-Komponenten/-Blöcke und MIDI-Frames. Beide endgültigen Zustands- und 8-MiB-RAM-Dateien sowie Mediengenerationen sind identisch.

Der Gast restauriert den eigenen Interruptvektor, PIC-Masken und VDS-Übersetzungen und gibt seinen großen DMA-Bereich frei. Nur sein eigener begrenzter Nachweisblock bleibt durch DOS/AH31 bis Reset resident. MCB-Eigentümer/-Größe, Programmbytes, DDS, vollständige DMA-Puffer, Wachen, alle ISR-Eingabebytes und der nach dem TSR-Aufruf tatsächlich durch Gastports ausgegebene DOS-Prompt werden geprüft. Ein fester Schritt-Nachlauf oder ein bloßer Vor-INT-Marker genügt dafür nicht. Das ist keine Behauptung einer vollständigen DOS-/CPU-/PIC-/Schedulerrestaurierung durch das Gastprogramm.

## Unabhängiges Oracle und Grenzen

Das Python-Oracle importiert keinen VM-Code. Es berechnet jede erwartete DMA-Komponente und jeden Blockzeitpunkt aus dem eigenen Muster, tatsächlichen DSP-Portpaketen, Samplerate und Stereoformat. Es rekonstruiert jede vollständige MPU-Framegrenze aus tatsächlichen OUT-Zeiten beziehungsweise den expliziten äußeren Paketzeiten sowie FIFO/Quelllatches und einzelne Quittierungen. Originale EMM386-NOEMS-/RAM-Ausführung muss tatsächliche PE-/Paging-/V86-Zustände und ausgeglichene VDS-Aufrufe nachweisen.

Alle Stereo-PCM-Werte werden aus den unabhängig geprüften DAC-Haltewerten und den tatsächlich beobachteten nativen FM-Quads auf 48 kHz integriert. Vier getrennte nominale -28-dB-/20-Hz-DC-Pfade, Stereoaddition und Begrenzung werden geprüft; die Toleranz ist ein PCM16-LSB. Die FM-Tonhöhen ergeben sich unabhängig aus Fnum/Block und 14318180/288 Hz; unbenutzte C/D-Busse bleiben null. Diese Rechnung prüft Integration, Filter, Pegel und Routing. Sie ist **kein zweiter unabhängiger vollständiger FM-Synthesizer** und ersetzt die getrennten [YMF262-Funktions-/YMFM-Nachweise](OPL3.md) nicht.

Die neuen Gäste benutzen eine IDE-Platte und eine eigene schreibgeschützte FAT12-Diskette. Alle sechs Medienpositionen und offenen PIO-/FDC-/DMA-Vorgänge behalten ihre getrennten bestehenden Snapshot-Gerätenachweise; hier wird keine neue Sechsmedien-Gastmatrix behauptet. PC-Dateien/Mediencheckpoints bleiben Kaltstarts. Keine neuen Chipfunktionen, Intelligent Mode, Synthese aus MIDI, Originaltreiber-, allgemeine Sound-/Spiel-/Echtzeit-/Silizium- oder Performanceabnahme. Keine Hostendpunkte oder Windows-Eingaben.

## Reproduktion

```powershell
.\scripts\test-audio-combined.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Nur eigene neue COM-/FAT12-Dateien und frische private COW-Kopien. Originale, Video und persönliche Spieljournale bleiben unberührt. Endgültige Zähler und Dateihashes: [Implementierungsstand](../IMPLEMENTATION-STATUS.md) und `artifacts/tests/m3-audio-combined-release-path.txt`. Vorläufe mit korrigierten Gast-/Oracle-/Prüfbeobachterdetails sind dort separat ausgeschlossen.

Primärvertrag: Creative, [Sound Blaster Series Hardware Programming Guide](https://ardent-tool.com/sound/Sound_Blaster_HW_Programming_Guide_1st.pdf), PDF-Seiten 26–28 (geteilte Interruptleitung und Quittierungen), 67–74 (CT1745), 107–108 (Bxh/Cxh) und 112 (D9h). Die bereits begrenzten Modelle und eigenen nominalen Fristen bleiben in [SB16-CT1740-PCM.md](SB16-CT1740-PCM.md), [MIDI-MPU401.md](MIDI-MPU401.md) und [OPL3.md](OPL3.md) definiert. Kein fremder Emulatorcode übernommen.
