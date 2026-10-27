# CT1740: optionaler MPU-401-UART-Transport

Stand: 26. Oktober 2026. **84 neue Fälle, 6079/6079 Regressionen**, eigener begrenzter CT1740-MPU-UART-Vertrag. Der Transport ist unabhängig von einem Synthesizer oder Host-MIDI-Endpunkt. M3 und die vollständige Soundkarte bleiben offen.

Primärgrundlage ist Creative, [Sound Blaster Series Hardware Programming Guide](https://www.phatcode.net/res/243/files/sbhwpg.pdf), Kapitel 5, gedruckte Seiten 5-5 bis 5-10, sowie 2-5 zur geteilten IRQ-Quelle. Er beschreibt die beiden Portpaare, Statusbits, Reset/UART-Kommandos, FEh-Antwort und getrennte Quittierungen. Die nominale MIDI-Framierung ist zusätzlich im Herstellerdatenblatt [SMSC LPC47M112, Abschnitt 17.2](https://dtsheet.com/doc/240106/smsc-lpc47m112) dokumentiert; dessen FIFO-/Resetbesonderheiten werden **nicht** auf CT1740 übertragen. Kein Fremdbackend, DSP-ROM oder Roland-Mikrocode ist Teil der Ausführung.

## Auswahl und Ressourcen

`midiInterface=ct1740-mpu401-uart-development` aktiviert ausdrücklich das MPU-UART-Interface der bereits gewählten `sb16-ct1740-dsp405-development`. `midiBasePort=768` beziehungsweise `816` wählt 300h beziehungsweise 330h. Standard und alte PCs bleiben bei `none`; die Soundkartenwahl allein aktiviert keine MIDI-Schnittstelle. Das eigene Startprofil heißt `profiles/virtual486-midi-dev-v1.json`.

Die Schnittstelle teilt den aktuellen CT1740-IRQ, einschließlich softwareseitiger Mixer80-Umwahl und Abschaltung. AT-IRQ9 entspricht der IRQ2-Auswahl des Kartenregisters. LPT1/IRQ7-Kollisionen werden bei der Profilübernahme weiterhin abgewiesen. Breite I/O ist ungetrieben und hat keine versteckte Bytewirkung; zusätzliche Adressaliase sind nicht zugesagt.

Hardwaremenü und gespeicherte PC-Definition enthalten Auswahl und Port. Ein Wechsel ist ein Kaltstart; der Rückwechsel zu einer anderen Soundkarte deaktiviert die CT1740-MIDI-Auswahl. Die angezeigte BLASTER-Zeile ergänzt bei aktivierter Schnittstelle `P300` oder `P330`. Benutzer-Startdateien werden nicht verändert. Gespeicherte PCs enthalten keine laufenden Frames, FIFO-Inhalte oder Ausgabecursor und bleiben Kaltstartdefinitionen.

## Register, Befehle und IRQ

| Port | Lesen | Schreiben |
| --- | --- | --- |
| Basis | Nächstes Eingabebyte oder FEh-Kommandobestätigung | MIDI-Ausgabebyte im UART-Modus |
| Basis+1 | Bit 7: keine Eingabe; Bit 6: keine freie Ausgabeannahme | FFh Reset; 3Fh Enter UART |

Ein gesetztes Ready-Statusbit bedeutet jeweils **nicht bereit**. Die undefinierten niedrigen Statusbits bleiben im Modell auf 3Fh, leeres Datenlesen liefert FFh. FFh auf dem **Datenport** ist ein gewöhnliches MIDI-Byte und setzt die Schnittstelle nicht zurück. Alle 256 Werte, Running-Status-/SysEx-/Realtime-Bytes werden ohne Interpretation transportiert. Das Produkt ist kein MIDI-Parser oder Synthesizer.

Reset verlässt UART, entfernt interne Eingaben und noch nicht vollständig gesendete Ausgaben und liefert FEh. Enter UART liefert FEh und eine Eingabe-IRQ-Quelle. Weitere Kommandos werden ohne erfundene Intelligent-Mode-Antwort ignoriert. Nach Reset ist keine Intelligent-Mode-Ausführung implementiert. Dieser Creative-Vertrag ist keine Zusage für anders reagierende originale Roland-MPUs oder spätere Chips.

CT1745-Mixer82-Bit 2 folgt der ungelesenen MPU-Eingabequelle. **Status-/Mixer82-Lesen und DSP-Quittierungen +Eh/+Fh konsumieren keine MPU-Daten.** MPU-Datenlesen entnimmt genau ein Byte; weitere ungelesene IRQ-Daten halten die Quelle aktiv. DSP8, DSP16, MPU-Quelle, kombinierte Kartenleitung, PIC-IRR/ISR, Interrupt-Acknowledge und EOIs sind getrennt. DSP-Reset und Mixer-Reset löschen den MPU-Zustand nicht. Ein MPU-Reset quittiert keine DSP-Quelle.

## Nominale Fristen und Puffer

Beide seriellen Richtungen besitzen unabhängige vollständige **8N1-Frames bei 31250 Baud**, exakt **320000 ns je Byte** im nominalen Modell. Erst nach dem Stopbit wird ein Ausgangsbyte veröffentlicht beziehungsweise eine Eingabe zum Host bereitgestellt. Host-Holdingregister und laufender Ausgangsshifter sind getrennt: ein Byte kann auf dem Draht und ein weiteres im Holdingregister stehen. Ein voller Holdingplatz setzt Statusbit 6; weitere Schreibversuche werden gezählt und verworfen.

Die Host-Eingabewarteschlange hält **16 Bytes einschließlich Antworten**. Bei Überlauf bleiben ältere Bytes erhalten, neue werden verworfen und diagnostisch gezählt. Das sind explizite DOSE-Pufferregeln, keine vermessenen CT1740-FIFO-Tiefen. Ebenso sind **500000 ns Reset**, **10000 ns Enter UART** und **2000 ns ignorierter Hostbefehl** nominale Servicefristen; der Creative-Leitfaden spezifiziert diese Zeiten nicht. Keine Mikrocontroller-/Kristallphasen-/elektrische-/Optokoppler-/Buspräzision wird behauptet.

Ein Reset kann die äußere Leitung nicht zurückspulen: bereits gestartete äußere Frames laufen weiter. Ein Frame wird nur zugestellt, wenn an Anfang und Ende UART aktiv ist und keine Resetgeneration dazwischenliegt. Spätere Frames tasten ihren Modus am eigenen Anfang ab. CPU-Hot-Reset erhält versorgte Karte und Fristen; Kaltladen erzeugt leere Komponenten und äußere Queues. Es gibt keine zusätzliche Geräteprüfung pro CPU-Schritt.

## Virtuelle Gegenstelle und Diagnose

IPC **`SendMidiInput`** oder ein Szenariofeld **`midi`** erhält `{"data":[144,60,127]}`. Ein vollständiges Paket enthält 1..4096 Bytewerte; höchstens 4096 äußere Frames dürfen einschließlich eines laufenden Frames warten. Bereich/Kapazität werden vor jeder Wirkung geprüft, Arrays kopiert und auf dem Ownerthread übernommen. Es werden keine fertigen Gastregister, IRQ-Zähler oder RAM-Beweise gesetzt.

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll `
  --profile profiles/virtual486-midi-dev-v1.json `
  --disk <eigene-basis.img> --overlay <neues-privates.cow> `
  --midi-output <neue-rohbytes.bin> --midi-io-trace <neue.jsonl>
```

Die Ausgabe enthält ausschließlich vollständig gesendete Rohbytes, keine SMF-Datei und keinen Ton. Bestehende Dateien werden nicht überschrieben. Der Trace beobachtet echte Ports, vollständige Ein-/Ausgabeframes, äußere Quellpakete und physische Karten-IRQ-Acknowledgements. **Kein Host-MIDI, Host-COM, Netzwerk, Mikrofon oder Synthesizer wird geöffnet.** SoundFont/MT-32/GM-Klangerzeugung, Intelligent Mode und die separaten alten SB-MIDI-DSP-Befehle bleiben offen.

DOSEBios zeigt die **konfigurierten** MIDI-Port-/IRQ-Ressourcen im POST, ohne eine MIDI-BDA-Belegung oder einen BIOS-MIDI-Dienst zu erfinden. Die ROM-Felder sind E018h/E01Ah. Nach einem CPU-Hot-Reset zeigt die Inventur weiterhin das Startprofil, nicht eine inzwischen vom Gast geänderte Mixer80-Einstellung.

## Nachweise und Grenzen

84 Geräte-/Integrationsfälle prüfen Fristgrenzen, alle Rohwerte an beiden Portbasen, getrennte Holding-/Shifterzustände, Überlauf, unabhängige Richtungen, Resetgenerationen, Beobachter-Reset, Ressourcen, geteilte IRQ-Quellen, Warm-/Kaltstart, IPC, PC-Definition und POST. Gesamtsuite **6079/6079**, normaler Build ohne Warnungen/Fehler.

**MIDI.COM besteht 32 endgültige Kaltstarts**: vier Ressourcenprofile an A220/I5/P330, A240/I10/P300, A280/I9/P330 und A260/I7/P300 unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM, jeweils zweimal. **12288 Ausgangs- und 5152 Eingangsframes**, insgesamt **17440 vollständige Framegrenzen**, werden unabhängig mit ganzzahliger Zeit geprüft. **4608 Eingaben** werden angenommen, **512** überlaufen, **32** werden während Reset verworfen. **4128 physische IRQ-Acknowledgements/Handler-Rückkehrvorgänge** enthalten 4096 reguläre Eingabebytes und 32 UART-Kommandobestätigungen. Alle 32 Handler für gleichzeitig pending Quellen sehen tatsächlich Mixer82=7. **5056 verfälschte MIDI-Nachweise** werden abgewiesen.

Die gemeinsamen DSP-Quellen werden hier ausdrücklich durch echte **F2/F3-Softwareanforderungen** erzeugt, nicht als DMA-Blockabschlüsse ausgegeben. 24 zusätzliche bestehende SB16-PCM/DMA/VDS-Kaltstarts prüfen den alten Pfad mit ausgeschalteter MPU und 732 Verfälschungen. Acht LPT-, vier VBE- und vier kurze originale Doom-Starts prüfen die gemeinsamen Firmware-/Portpfade. Das ist keine lange kombinierte PCM-/MIDI-/OPL-/Originaltreiber-/Sound-/Echtzeitabnahme.

Die eigene Probe synchronisiert äußere Pakete über echte KBC-/INT16-Bestätigungen. Die Diagnosemarker allein sind keine Frist: ein 10000-Schritte-Hostbatch kann sonst über die Meldung hinauslaufen. Das unabhängige Oracle verwendet tatsächliche Quell-/OUT-Zeitpunkte, alle Registerwerte, Bytes, Fristen, Status-/IRQ-Prioritäten, Programm-/RAM-/MCB-/Schutzbytes, echte Paging-/V86-Ausführung und DOS-Rückkehr. Nach Wiederherstellung von Vektor/PIC-Masken bleibt der begrenzte Nachweisblock über DOS/AH31 bis Reset resident; keine vollständige DOS-/CPU-/PIC-/Scheduler-/Speicherwiederherstellung.

Reproduktion:

```powershell
.\scripts\test-midi.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne Managerpfade werden nur MS-DOS/FreeDOS geprüft. Alle Gastschreibpfade verwenden frische private Journale; die FAT12-Disketten enthalten nur unsere Programme. Private Gesamtevidenz: **`artifacts/tests/m3-midi-release-path.txt`**. Originale, Benutzer-Spieljournale, Video und C64 bleiben unberührt. Kein Commit/Push oder neue native Bedienungsabnahme. Nächster M3-Schritt sind koordinierte Hardware-/Overlay-Snapshots; vollständige Karten, vollständiges x87 und M3 bleiben offen.

## Ergänzung: echte DMA-Quellen und gemeinsame Fortsetzung

Der eigene [kombinierte Gast](SB16-COMBINED.md) prüft inzwischen die zuvor offene Kombination mit tatsächlichen DMA8-/DMA16-Blockabschlüssen, OPL3, bidirektionalen MPU-Frames und vollständigem Snapshot. Er verwendet keine F2/F3-Softwareanforderungen. Sein begrenzter 16,384-Sekunden-Lauf verändert den MPU-Vertrag nicht und ist keine Intelligent-Mode-/Synthesizer-/Host-/Originaltreiber-/allgemeine Echtzeitabnahme. Die historischen MIDI.COM-Zähler bleiben unverändert.
