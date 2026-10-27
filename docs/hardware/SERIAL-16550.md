# COM1: National PC16550D mit virtueller Gegenstelle

Stand: 26. Oktober 2026. Historischer Paketnachweis auf dem **5688/5688-Build, ohne Warnungen/Fehler**; der aktuelle Gesamtstand steht in [IMPLEMENTATION-STATUS.md](../IMPLEMENTATION-STATUS.md). Konkreter funktionaler Vertrag eines 16550A-kompatiblen **National PC16550D**, kein beliebiger moderner UART und keine elektrische oder vollständige serielle PC-Abnahme. Primärgrundlagen sind das [National-Datenblatt, Juni 1995](https://www.scs.stanford.edu/10wi-cs140/pintos/specs/pc16550d.pdf), Abschnitte 8.0–8.10, und für den grundlegenden INT14-Aufrufvertrag das [PhoenixBIOS-4.0-Herstellerhandbuch](https://bitsavers.org/pdf/phoenix/PhoenixBIOS_4.0_Users_Manual_2000.pdf), gedruckte Seiten 51–52. Eigener Code, keine fremde BIOS-/Treiberimplementierung. Neuere Autoflow-Funktionen anderer UARTs werden nicht auf dieses Modell übertragen.

## Auswahl und Umfang

`serialPort=pc16550d-com1-development` aktiviert **COM1, 3F8h–3FFh, IRQ4, 1.843.200 Hz**. `none` bleibt Standard, auch beim Laden alter PCs. `profiles/virtual486-serial-dev-v1.json` ist ein Startprofil. Hardwaremenü und gespeicherte PCs enthalten die Auswahl; der Wechsel verwendet den vorhandenen Kaltstartpfad. Es gibt derzeit keinen zweiten COM-Port oder Ressourcen-/Hostporteditor. Eine gespeicherte PC-Definition speichert weder laufende Schieberegister noch externe Zeichen oder Modempegel als Hardware-Snapshot.

Der Kern besitzt getrennte Empfangs-/Sendeschiebeframes, Holdingregister beziehungsweise zwei **16-Byte-FIFOs**, DLAB-Aliase, IER/IIR/FCR/LCR/MCR/LSR/MSR und Scratch. Registerquittierungen sind getrennt: IIR bestätigt nur die tatsächlich gelieferte THRE-Quelle, LSR die sichtbaren Fehler, RBR entnimmt ein Zeichen, MSR löscht Modemdeltas. Empfangsfehler gehören zum jeweiligen FIFO-Kopf; die Fehlerzusammenfassung kann bereits einen späteren fehlerhaften Eintrag anzeigen. Im Holdingbetrieb ersetzt Überlauf das alte Zeichen, im FIFO bleiben alte Einträge erhalten und das neue Zeichen entfällt.

IIR priorisiert Leitungsfehler, Empfangsschwelle, Vier-Zeichen-Timeout, THRE und Modemstatus. Schwellen sind 1/4/8/14 Zeichen. **THRE und TEMT sind verschiedene Zustände**; der kurze FIFO-Burst besitzt eine eigene verzögerte Leeranzeige. FIFO-Reset entfernt Warteschlangeneinträge, aber keine bereits laufenden Schiebeframes. Das FCR-DMA-Modusbit wird gespeichert; es gibt dadurch keine UART-DMA-Anbindung. Schreibzugriffe auf ein volles TX-Holding/FIFO werden im begrenzten Modell verworfen und diagnostisch gezählt; für undefinierte physische Überschreibeffekte wird nichts versprochen.

Der PC-Boardvertrag schaltet die IRQ4-Leitung über das **gespeicherte MCR.OUT2-Bit** frei. Dies ist ausdrücklich eine Softwaremodellregel auch im Loopback, keine vollständige Nachbildung eines bestimmten Karten-Gatters oder des äußeren OUT2-Pins. PIC-IRR/ISR, UART-Quellen und EOI bleiben getrennt. Die Leitung wird bei Zustandswechseln aktualisiert; ein wartender Port benötigt keine zusätzliche Prüfung pro CPU-Schritt.

## Virtuelle Zeit und Leitungen

Die Bitzeit folgt dem programmierten Teiler und der nominalen Kristallfrequenz. Frames enthalten Start, 5–8 Datenbits, optional Parität und 1/1,5/2 Stopbits. Ganze Kristallzyklen werden mit unabhängig getragenem Rest in Nanosekunden umgerechnet. Ein ruhender Sender beginnt im Modell nach einer nominalen Bitzeit; fortlaufende Frames schließen unmittelbar aneinander an. Format, Teiler und Loopbackverbindung werden am Frameanfang abgetastet. Änderungen erzeugen keine rückwirkend veränderten halben Frames. Teiler null hält neue Arbeit an; dies und anfänglich null gesetzte undefinierte DLL-/Scratch-/Leer-RBR-Inhalte sind stabile Modellentscheidungen.

CTS/DSR/RI/DCD sind logische äußere Eingänge. Modemdeltas bleiben bis MSR-Lesen stehen; RI meldet die abfallende aktive Flanke. Loopback ersetzt sie durch RTS/DTR/OUT1/OUT2 und trennt äußeren Empfang. Der lokale Breakpfad liefert einmal Null mit BI/FE pro zusammenhängendem Break; er erzeugt keine endlose Nullzeichenfolge. **Keine automatische CTS-Flusssteuerung**, keine echte RXD-Abtastung, Paritätsbitberechnung/-beschädigung, False-Start-Erkennung, unabhängige Empfangstaktquelle, RS232-Pegel oder vollständige AC-/16x-Phasenpräzision. Die gelieferten äußeren Zeichen tragen ausdrücklich vorgegebene PE/FE/BI-Metadaten. Das ersetzt keinen elektrischen Fehlernachweis.

CPU-Hot-Reset erhält die versorgte Karte, Eingänge und laufende Fristen. Ein echter Kaltstart erzeugt eine neue leere Instanz. Byte-I/O ist geprüft; breite I/O-Zugriffe sind ungetrieben und lösen keine versteckten Bytezugriffe aus. OUT2, FIFO- und Signalleitungsdetails sind begrenzte Referenzverträge, keine universelle 16550-/Stepping-/Buszusage.

## DOSEBios und äußere Eingaben

Die neue ROM-Auswahl liegt bei E016h. DOSEBios zeigt COM1 im POST, setzt BDA 0040:0000 auf 3F8h und den seriellen Zähler in INT11/BDA, andere COM-Einträge bleiben null. **INT14/AH00–03 für DX=0** initialisiert, sendet/empfängt ein Zeichen und liefert Status über wirkliche Gast-IN/OUT. AH00 bietet die acht Standardraten 110 bis 9600 Baud, deaktiviert FIFO/IER, setzt DTR/RTS und lässt die Board-IRQ-Freigabe aus. Senden wartet auf DSR/CTS und THRE, Empfang auf DSR und DR. Fehlende Ports/weitere Dienste liefern vor Portkontakt AH80; andere Register/Flags bleiben im Modell erhalten. PS/2-Erweiterungen AH04/05 sind nicht implementiert.

Polled-Warten erhält IF sowie CMOS-Index/NMI. Nichtnullige BDA-Timeouts zählen ganze RTC-Sekunden **zusätzlich zum ersten Teilintervall**; null bedeutet eine kurze begrenzte Probe. Unabhängig gilt eine harte Grenze von vier Millionen Polls für eingefrorene Uhren/ungewöhnliche Werte. Das ist keine Zusage für jede IBM-Pollschleife oder jedes große Timeout. Der echte FreeDOS-Gast deckte beim Aufbau auf, dass das erste Teilintervall sonst langsame gültige Zeichen vorzeitig als Timeout abwies. Der eigene Grenztest beginnt zehn Millisekunden vor einer RTC-Sekunde und empfängt das vollständige 300-Baud-Zeichen erst danach.

IPC **`SendSerialInput`** beziehungsweise ein Szenariofeld `serial` erhält beispielsweise:

```json
{"data":[65,66],"errors":0,"cts":true,"dsr":true,"ri":false,"dcd":true}
```

Dies stellt äußere komplette Frames und optional geänderte logische Pins bereit, keine RBR-/FIFO-/Gast-RAM-Injektion. `errors` erlaubt PE=4, FE=8 und BI=16. Höchstens 4096 ausstehende externe Frames; ungültige Pakete werden vollständig vor einer Änderung verworfen, Arrays vor Übernahme kopiert. Die Ausführung erfolgt auf dem Ownerthread. Das Programm muss Baudrate/Format und Empfang selbst einrichten.

Headless **`--serial-echo`** verbindet ausdrücklich eine rein virtuelle Gegenstelle: CTS/DSR/DCD sind aktiv, jedes vollständig gesendete Zeichen wird erst nach einem weiteren vollständigen Empfangsframe verfügbar. Ohne diese Option ist keine Gegenstelle verbunden. `--serial-io-trace <neue.jsonl>` beobachtet tatsächliche Ports, abgeschlossene TX-Frames mit abgetasteten Parametern, E9 sowie physische PIC-IRQ4-Acknowledges. Die Beobachtung verändert keine UART-Register. **Kein Host-COM, Netzwerk, Modem, MIDI oder Hostmikrofon wird geöffnet.**

## Nachweise und Reproduktion

**200 neue CPU-/Geräte-/Integrationsfälle**, einschließlich 96 expliziter TX-Format-/Teilergrenzen, RX-Abtastung, FIFO-/Fehler-/Quittierungsprioritäten, Modem-/Loopbackkombinationen, Break, Überlauf, TX-Füllung, getragenen Takten, Warm-/Kaltstart, IPC, BDA und tatsächlichen INT14-Rückkehrpfaden. Gesamtsuite **5688/5688** auf dem endgültigen Build. Lizenz-/Kommentar-/Skriptprüfung besitzt einen gesonderten Nachweis.

**SERIAL.COM besteht acht endgültige DOS-Kaltstarts** unter MS-DOS 6.22, FreeDOS und originalem EMM386 NOEMS/RAM je zweimal. Je Start 52 Fälle, acht echte BIOS-Raten, 32 direkte Formate, vier FIFO-Schwellen, vier äußere Fehlerzeichen, beide Überlaufarten, Loopback, breite I/O-Ablehnung und BIOS-Negativ-/Timeoutantworten. Der eigene Handler liest wirkliche IIR/LSR/RBR, quittiert den PIC und kehrt zurück. Insgesamt **416 Fälle, 1008 TX- und 1040 RX-Frames, 576 physische IRQ4-Acknowledgements/Handleraufrufe**, darunter 544 ISR-gelesene Datenbytes. Das sind keine aus CPU-Vektor 0Ch geratenen IRQ-Zähler.

Ein unabhängiger Python-Integerprüfer rekonstruiert alle 1000 äußeren TX-Framedauern aus tatsächlichen THR-/DLAB-/LCR-Zugriffen und getragenen Kristallresten, prüft nachgelagerte Echo-Empfangsgrenzen, sämtliche RBR-Daten, Firmware-/FIFO-/Fehlerzeilen und Handlerbeweise. Er kontrolliert ausgeführte Programmbytes, Schutzbytes, BDA/INT11, echte Paging-/V86-Pfade und reguläre DOS-Rückkehr. **628 verfälschte UART-Nachweise scheitern**; Wiederholungen sind in virtueller Zeit und Proof identisch. Die UART-/CPU-/PIC-/Scheduler-Gesamtzustände werden damit nicht vollständig restauriert.

Auf denselben endgültigen VM-DLLs bestehen vier kombinierte Gameport-/COM1-Kaltstarts und vier gezielte VBE-Grafikstarts unter den vier Gast-/Managerkonfigurationen. Der Gameportgast läuft mit installierter, unverbundener COM1-Karte; der Gast darf sie initialisieren, ohne dass eine Gegenstelle oder Nutzdaten entstehen. Vier kurze originale Doom-II-Starts auf DOSE VGA/ET4000 ohne/mit Ton liefern 120 WAD-geprüfte Bilder, je 32 Ticks und DOS-Rückkehr. Historische Leistungszeiten und unveränderte CPU-/x87-/Audiomatrizen werden ohne neuen Befund nicht pauschal wiederholt. Dies ist keine allgemeine Terminal-/Maus-/Modem-/Doom-/Sound- oder M3-Abnahme.

```powershell
.\scripts\test-serial.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne Managerpfade werden nur MS-DOS/FreeDOS geprüft. Alle Gastläufe verwenden frische private Journale; die FAT12-Diskette enthält ausschließlich das eigene Programm. Originalmedien, Video, persönliche Spieljournale und C64 bleiben unberührt. Kein Commit/Push. Privater Gesamtnachweis: **`artifacts/tests/m3-serial-release-path.txt`**. Nächste M3-Vertikale ist ein konkret begrenzter **LPT/SPP-Druckerport mit virtueller Senke**, danach MIDI und koordinierte Hardware-/Overlay-Snapshots. Vollständige Karten, vollständiges x87 und M3 bleiben offen.
