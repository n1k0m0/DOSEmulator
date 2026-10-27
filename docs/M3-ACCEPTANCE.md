# M3 – Abnahme des funktionalen 486-DOS-Referenzumfangs

Stand: 26. Oktober 2026. **M3 ist auf dem eingefrorenen 7030er-Build im unten festgelegten funktionalen Referenzumfang abgenommen.** Grundlage sind die Ausgänge von Phase M3 im [Konzept](DOSEmulator-Konzept.md), die bisherigen konkreten Geräteverträge und die neue gemeinsame Abschlussmatrix. Die Umsetzung erweitert den bestehenden Umfang um den tatsächlichen Gameport-BIOS-Dienst. Die bisher geprüften Grenzen der Karten werden dadurch nicht als vollständige Chipnachbildung ausgegeben.

## Verbindlicher Umfang

| Bereich | Abgenommener Referenzvertrag |
| --- | --- |
| CPU und Speicherverwaltung | Eigenes 486-Entwicklungsmodell; Real/Protected/V86, klassische 4-KiB-Seiten, Privilegien, Descriptoren, Call Gates, Hardwaretasks, Restart-/LOCK-/Alignment-/Debugverträge der gezielten Pakete. Aktuelle Gesamtsuite sowie echte DOS-/Paging-Debuggäste. |
| x87 | Alle dokumentierten 486-Befehlsfamilien mit rohen 80-Bit-Registern, Tags/TOP, Umgebungen, Masken, Fehlerleitungen und den dokumentierten Zahlen-/Rundungsverträgen. 618 zusätzliche Encodingfälle prüfen 252 Registerformen und 57 Speichergruppen jeweils in PE16/PE32. Sie ergänzen die Zahlenprüfung; sie ersetzen sie nicht. |
| Reale Speicherverwalter/Extender | Originale HIMEM-/EMM386-NOEMS-/RAM-Konfigurationen, bestehende VCPI-/EMS-/VDS-Verträge und originale DOS4GW-/Doom-Referenzfälle auf frischen Journalen. Kein Host-XMS oder eingebauter DOS-Kernel. |
| Grafik | DOSE VGA sowie ET4000AX-ISA-Referenzkarte mit 1 MiB, Legacy-/Bank-/Lese-/Schreib-/RAM-Grenzen und der vorher festgelegten VBE-Teilmenge 00/01/02/03/05 in drei 256-Farb-Auflösungen. |
| Ton | CT1740/DSP4.05/CT1745, DMA8/DMA16/VDS, nominaler OPL3-Referenzvertrag, getrennte DSP-/MPU-Quellen und Quittierungen. Eigener gleichzeitiger 16,384-Sekunden-Gast mit echten DMA-Abschlüssen und MIDI-Frames. |
| Anschlüsse | Nominaler IBM-Gameport einschließlich neuem INT15/AH84, PC16550D-COM1/INT14, IBM-SPP-LPT1/INT17 und CT1740-MPU-UART mit ausdrücklich virtuellen Gegenstellen. |
| Zustand und Medien | Vollständige lokale CPU-/rohe-FPU-/RAM-/VRAM-/Geräte-/Scheduler-Snapshots einschließlich Fristen, Quellen, Integratoren und vier IDE-/zwei Diskettenpositionen. Neue versiegelte Generationen, Importprüfung vor Übernahme und nutzbarer alter PC bei Fehlern. Format 1 bleibt an denselben VM-Build und lokale Basen gebunden. |

Die 72 zusätzlichen SS-Vertragsfälle und acht erweiterte reale Debuggäste prüfen synchrone GD-Faults vor der Registerübertragung, echte IRET-Wiederanläufe und sechs neue GD-/SS-Rahmen pro Gast. Der bisherige Zahlenkern und die CPU-Ausführung bleiben unverändert. Aufeinanderfolgende SS-Ladevorgänge folgen der expliziten begrenzten DOSE-Politik; Intel garantiert für portable Software nur die erste Sperre. Daraus wird keine gemessene historische Steppingantwort abgeleitet.

## Gemeinsamer Abschlussnachweis

Der neue Kandidat besteht **7030/7030 Regressionen**, normaler Build ohne Warnungen/Fehler. Auf denselben sechs Produkt-DLLs bestehen **50 DOS-Kaltstarts und 26 tatsächliche Snapshot-Fortsetzungen**:

- Acht Gameport-/AH84-Starts unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM, jeweils zweimal: 392 tatsächliche BIOS-Aufrufe, zusätzlich die bisherigen direkten Portmessungen und alle Tastenbits. Jeder Firmware-Messwert wird aus den beobachteten IN-/OUT-Zeitpunkten unabhängig rekonstruiert.
- Acht PE-/Paging-Debugstarts: alte Register-/REP-/RF-/Privilegprüfungen plus 48 neue tatsächliche GD-/SS-Ausnahmerahmen.
- Acht vollständige DOS-/x87-/PCM-Snapshotstarts mit acht Fortsetzungen und unabhängigen tatsächlichen DOS-Dateien.
- 16 gleichzeitige PCM-/DMA-/VDS-/OPL3-/MPU-Starts mit 16 Fortsetzungen, echten gemeinsamen DMA-/MIDI-IRQ-Quellen und wirklichen Importablehnungen.
- Zwei originale DOS4GW-/Doom-Starts mit zwei Fortsetzungen, DOSE VGA mit Ton und ET4000 ohne Ton: 18 unabhängig WAD-geprüfte Bilder und reguläre DOS-Rückkehr nach 32 Ticks.
- Vier gezielte aktuelle 1024×768-VBE-Vollbildprüfungen und vier Drei-Modus-DOS-Rückkehrstarts. `test-et4000.ps1 -Vbe -Targeted` nennt diese begrenzte Matrix ausdrücklich; die historische vollständige Auflösungsmatrix wird nicht neu etikettiert.

Die Matrix verwirft **2588 verfälschte Gastoracles**; die 16 wirklichen beschädigten Snapshotimporte werden getrennt gezählt. Original-/Restaurierungsvergleiche umfassen CPU, alle 8 MiB RAM, Geräte, Scheduler, Audio und Endmedien. Ein unverdeckter Doom-Gastcut wurde visuell geprüft. Keine nativen Windows-Eingaben oder Hostendpunkte wurden dafür geöffnet.

Der reine Prüffehler im älteren Gameportfall war ein nicht gesetzter DX-Unterfunktionswert: Der neue Dienst wies ihn korrekt ab. Der endgültige Test setzt DX=0 und endet am tatsächlichen Rückkehrpunkt. Zwei vorherige Gesamtläufe und die frühen reinen Assembler-/Fixturevorläufe sind keine bestandene Abschlussmatrix. Die zusätzlichen Testregistrierungen ändern nur Tests.dll/Tests.pdb; die während der Gastmatrizen verwendeten sechs Produkt-DLLs bleiben bytegleich.

Privater Gesamtnachweis: `artifacts/tests/m3-completion-release-path.txt`. Der Bericht enthält die genauen Artefaktpfade, Ergebnisse, Modul-/Quell-/Medienidentitäten und die eingefrorenen Release-Dateien. 115 geschützte Eingaben und HEAD bleiben unverändert; persönliche Spieljournale werden nicht geöffnet. Keine Commits, Pushes oder Veröffentlichungen.

## Getrennte Erweiterungen und M4

M3 ist eine funktionale Referenzabnahme. Vollständige historische CPU-/x87-/Karten-/Bus-/Siliziumpräzision, undefinierte/reservierte ESC-Aliase und alle denkbaren konkurrierenden Fehler sind weiterhin getrennte Genauigkeitsarbeit. Ebenso bleiben zusätzliche VBE-Dienste 04/06/07/DAC/Palette, High Color/LFB und Originaltreiber, EPP/ECP/Druckwerk, weitere COM-Ports, MPU Intelligent Mode/Synthese und Hostgeräte außerhalb des oben unverändert benannten Kartenumfangs. Dafür gibt es keine neue Freigabe.

Der gesondert verschobene native Bediennachweis wird durch diese Abnahme nicht ersetzt. Eine lange allgemeine Spiel-/Sound-/Echtzeit- oder Performancezusage ist damit ebenfalls nicht verbunden.

**M4 beginnt anschließend mit PCI-Konfigurationsmechanismus 1.** Danach folgen die konkrete P54C-CPU, 430FX-/PIIX-Plattform, S3 Trio64, ATAPI, Firmware-/Treiber- und Managerpfade. M4 endet erst mit der tatsächlichen Windows-95-RTM-Installation, wiederholten Starts, stabiler Gerätebedienung und regulärem Herunterfahren. Ein PCI-Transport allein ist keine bestandene Pentium- oder Windows-95-Plattform.
