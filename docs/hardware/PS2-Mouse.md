# PS/2-Maus: implementierter Umfang

Optionales Profilfeld `mouse: "ps2-two-button"`, Beispiel `profiles/virtual486-ps2-dev-v1.json`. Alte Profile behalten `none`. Das Hardwaremenü wechselt das Gerät über den vorhandenen Prepare/Commit-/Kaltstartpfad. Bewegung, Tasten und Freigabe ändern keine Maschinenkonfiguration und benötigen keinen Neustart.

## Hardware und Firmware

- Standardgerät ID 00h, zwei Tasten, drei Paketbytes. Positive X-Richtung rechts, positive Y-Richtung oben. Der Host liefert relative Counts; die Auflösung bezeichnet die virtuelle Sensoreinstellung, keine Messung der physischen Bildschirmmillimeter.
- Eigener virtueller Abtasttakt mit 10/20/40/60/80/100/200 Hz und ganzzahligem Frequenzrest. Es werden nur geänderte Bewegungs-/Tastenzustände gemeldet. Der KBC-Taktstopp hält Bewegung zurück; eine volle FIFO übernimmt kein halbes Bewegungspaket.
- Reset/BAT/ID, Defaults, Enable/Disable, Abtastrate, Auflösung, Stream/Remote, Read Data, Status, Skalierung 1:1/2:1, Wrap/Echo und Resend. Skalierung greift auf Stream-Bewegung. Bewegungsüberlauf ist ein begrenztes Zählermodell mit gesetzten Paketflags; keine Abnahme des exakten historischen Sensorverhaltens bei Überlauf.
- Der KBC markiert jedes Ausgabebyte als Tastatur/Controller oder Aux. Port 64h/Bit 5 und IRQ1/IRQ12 folgen dem vordersten FIFO-Byte; die IRQ-Leitung fällt beim Lesen ab und kann für das folgende Byte erneut steigen. Aux-Kommandos A7/A8/A9/D3/D4, Command-Byte-Bits für Takt/Interrupt und bestehender Tastatur-/A20-/Resetpfad. Controllerantworten und Tastaturbytes werden nicht als Mausbewegung interpretiert.
- Eigenes BIOS führt `INT15/AH=C2h`, AL 00h..07h, als Gastcode aus. Unterstützt werden Enable/Disable, Reset, Rate, Auflösung, ID, Initialisierung mit **drei** Paketbytes, Status/Skalierung und Far-Callback. Andere Paketlängen werden als ungültige Eingabe zurückgewiesen. Ohne Gerät meldet es AH=86h/CF. Enable ohne Callback meldet AH=05h/CF. Kein Host-INT33 und kein vom Host vorgetäuschter DOS-Maustreiber.
- IRQ12 (Vektor 74h) sammelt drei tatsächliche KBC-Bytes und ruft den Gast-Farhandler mit vier IBM-Stackwörtern auf: Status, X, Y, Z=0. Der Gast räumt seine Argumente nicht ab. Register/Segmente werden erhalten; Slave und Master erhalten EOI. PIC-Masken werden für Enable/Disable gezielt angepasst.
- EBDA 9FC0h, Offsets 10h..19h für Mausdaten, getrennt vom Storage-Bounce-Sektor 200h..3FFh. INT11 zeigt das installierte Zeigergerät. Während polled Aux-Transaktionen verarbeitet dieselbe Tastatur-Decodierung bereits wartende Tastaturbytes; IRQ1 ignoriert nachfolgende leere/Aux-Ausgaben.

Das ist eine Byte-Schnittstelle, keine elektrische PS/2-Leitung mit einzelnen Clock-/Paritybits, Pinfehlern oder vollständigen 8042-Firmwarelatenzen. Command-Antworten sind unmittelbar, die FIFO ist begrenzt. IntelliMouse/Rad, dritte Taste, serielle Maus, andere DOS-Treiber und Windows-Treiber sind nicht abgenommen. Snapshot-/Reset-Synchronisierung gehört zum noch offenen gemeinsamen Maschinenpfad.

## Hostbedienung

OpenTK-Move-Ereignisse liefern relative Deltas einschließlich Bruchteilen. Die Y-Achse wird für PS/2 invertiert; Left/Right erzeugen tatsächliche Press-/Release-Zustände. Klick innerhalb der Gastfläche fängt den Zeiger über `CursorState.Grabbed`. Strg+Alt, Menüöffnung, Fokusverlust und Maschinenwechsel geben ihn wieder frei und senden beide Gasttasten als losgelassen. Der Statusstreifen zeigt den Fangzustand. Gastauflösungen werden weiterhin über die bestehende SharpPixels-Darstellung skaliert.

IPC `SendMouseInput` nimmt begrenzte Counts X/Y -32767..32767 und Buttons 0..3 entgegen; fehlendes Gerät/ungültige Werte werden zurückgewiesen. Testszenarien können dieselbe Hardwareeingabe an tatsächlichem VGA-Text oder einem expliziten Gast-E9-Marker auslösen. GUI-Szenarien ändern keinen Gast-RAM und rufen keine DOS-Dienste auf dem Host auf.

Relative Hostzustände, Bruchteile, Achsen und Freigabe sind als reine Eingabelogik getestet. Native GUI-Abnahmen bestätigen Gerätedurchlauf und GL-Darstellung. **Der physische OS-Mausfang sowie Fokusverlust durch echte Benutzerinteraktion sind durch diese automatischen Szenarien nicht abgenommen.**

## Nachweise

`scripts/test-mouse.ps1 -SkipBuild -Gui [-MsdosDisk <private-base> -MsdosOverlay <installed-pristine.cow>]` erzeugt immer eine neue private Testdiskette. Der echte CuteMouse 2.1 beta4 wird aus dem lokalen offiziellen FreeDOS-Paket gelesen; nur unsere eigenen COM-Proben stehen im Projekt. Fremde Binärdateien bleiben unter ignorierten `artifacts`. Bestehende DOS-Installation und Originalmedien werden nicht geändert.

Die Abnahme prüft zweimal FreeDOS und zusätzlich die installierte deutsche MS-DOS-6.22-HDD: echtes Laden von CuteMouse, INT33-Reset, beide Bewegungsrichtungen, linke/rechte Press-/Releasezähler, Ausblenden, Entladen/erneutes Laden des Treibers, Wechsel in VGA 13h/12h und zurück zum regulären DOS-Prompt. Die kleine Bewegungsprobe bleibt im linearen Bereich des Treibers; dessen adaptive Beschleunigung ist keine Hardware-Counts-Referenz. VGA-13h-Hide stellt alle 64.000 schwarzen Ausgangspixel wieder her. Der unabhängige Python-Prüfer kontrolliert tatsächliche Aux-Antworten, fünf konkrete Bewegungspakete, 15 echte IRQ12/Slave-EOIs, PIC-Masken, verarbeitete Eingaben und fehlende Gastwrites. Ein weiterer Kaltstart prüft die ehrliche Nicht-Erkennung ohne Maus.

Zwei eigene native GUI-Prozesse halten je einen vom echten Treiber gezeichneten Zeiger in 320×200/256 Farben bzw. 640×480/16 Farben sichtbar; je sechs GL-Aufnahmen prüfen Menü/Darstellung. Die Gastaufnahmen werden zusätzlich visuell gelesen. Konkrete Ergebnisverzeichnisse und gemeinsame Regressionen stehen in `docs/IMPLEMENTATION-STATUS.md`.

## Primärgrundlagen

- [IBM PS/2 Hardware Interface Technical Reference, Mai 1988](https://bitsavers.trailing-edge.com/pdf/ibm/pc/ps2/Personal_System_2_Hardware_Interface_Technical_Reference_May88.pdf): Abschnitt Keyboard and Auxiliary Device Controller (Type 1), gedruckte Seiten 3–10, PDF 336–343: Status, Command Byte, Aux-Kommandos und IRQ12. Das Entwicklungsprofil übernimmt die Aux-Byte-Schnittstelle auf seiner AT-Plattform; es behauptet keinen vollständigen PS/2-Rechner oder Micro Channel.
- [IBM BIOS Interface Technical Reference, Mai 1988](https://bitsavers.trailing-edge.com/pdf/ibm/pc/ps2/15F0306_PS2_and_PC_BIOS_Interface_Technical_Reference_May88.pdf): gedruckte Seiten 2-95 bis 2-99, PDF 112–116: C2-Dienste, Fehler und Callback-Stack.
- [Cypress: Designing a USB Keyboard and PS/2 Mouse Combination Device, 2. Februar 1998](https://nagasm.org/ASL/usb/kbdps2.pdf): Hersteller-Appnote zum CY7C63413, Seiten 1–2: Betriebsmodi, Protokoll/ACK, Standardkommandos und Dreibytepakete. Sie ersetzt keine Abnahme aller Sensor-/Fehlerdetails.
- [CuteMouse-Projekt](https://cutemouse.sourceforge.net/), lokale Paketdokumentation und zugehöriger Quelltext des tatsächlich getesteten FreeDOS-Pakets: BIOS-basierter PS/2-Treiber, Gast-INT33, Beschleunigung und VGA-Zeiger. Treiber/Quellarchiv werden nicht in Projektcode übernommen.
