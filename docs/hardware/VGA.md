# Referenz-VGA: implementierter Umfang

Die erste Grafikstufe verwendet ein eigenes VGA-Teilmodell mit 256 KiB VRAM. Sie ersetzt den bisherigen reinen Textadapter. Dies ist der getestete Entwicklungsstand, keine vollständige VGA-/SVGA- oder Spielekompatibilitätszusage. Das Konzept sieht weitere Karten und größere Speicherbestückungen vor.

## Speicher und Register

| Verbindung | Implementierung |
| --- | --- |
| Speicherfenster | A0000h–BFFFFh, über Graphics Controller auf A0000h/B0000h/B8000h begrenzt; nicht ausgewählte Bereiche liefern FFh und verändern kein RAM |
| VRAM | Vier unabhängige 64-KiB-Planes, vier beim Lesen geladene Latches |
| Adressierung | Planar, Odd/Even für Text sowie Chain-4; Sequencer Map Mask und Read Map Select |
| Schreibpfade | Write Modes 0–3, Rotate, Set/Reset, Enable Set/Reset, AND/OR/XOR und Bit Mask; Mode 1 kopiert die Latches |
| Lesepfade | Read Mode 0 mit Plane-Auswahl; Read Mode 1 mit Color Compare/Color Don't Care |
| Ports | Sequencer 3C4h/5h, GC 3CEh/Fh, Attribute Controller 3C0h/1h, DAC 3C6h–9h, Miscellaneous 3C2h/3CCh |
| CRTC | 3B4h/5h oder 3D4h/5h nach Miscellaneous; Registerschutz, Startadresse, Zeilenabstand, Cursor und Bildabmessungen im getesteten Umfang |
| Attribute | Index-/Daten-Flipflop mit Reset über Status 1, Palette/Color Select, Plane Enable, Blink und einfache horizontale Pixelverschiebung |
| DAC | 256 RGB-Einträge mit sechs Bit je Komponente, Auto-Inkrement/Index-Wrap, PEL Mask; Änderungen wirken auf die nächste Aufnahme |

Breitere Portzugriffe zerfallen in aufeinanderfolgende Byteports. Dadurch programmiert die echte CPU beispielsweise Index/Daten per OUT DX,AX. Schreibmasken verwenden die zuvor gelesenen Latches; ein Schreibzugriff lädt nicht heimlich den Zielinhalt nach. Die Logikfunktion in Write Mode 3 folgt dem IBM-Referenzmodell; Klonabweichungen sind nicht modelliert.

## Eigene Firmware und Darstellung

`firmware/video-bios.inc` führt alle Änderungen als Gastinstruktionen über IN/OUT und VRAM-Zugriffe aus. Es gibt keine Hostimplementierung von INT10.

| BIOS-Modus | Getestete Darstellung |
| --- | --- |
| 03h | 80×25 Text, im eigenen Profil 640×400 mit 8×16-Zellen und eigenem Entwicklungsfont in Plane 2 |
| 12h | 640×480 mit 16 Farben aus vier Planes |
| 13h | 320×200 mit 256 indizierten Farben, Chain-4 |

Modussetzen aktualisiert den BIOS-Datenbereich, lädt Register/DAC und löscht VRAM, sofern AL-Bit 7 nicht gesetzt ist. Die Firmware lädt beim Textmodus den eigenen Font. Sie verwendet eigene Standardpaletten; insbesondere die 256-Farben-Palette ist kein kopiertes kommerzielles VGA-ROM.

INT10 unterstützt zusätzlich Pixelsetzen/-lesen (AH=0Ch/0Dh, einschließlich XOR im planaren Modus) und DAC-Dienste AX=1010h/1012h/1015h/1017h. Die Pixelroutinen sind für die von der Firmware programmierten Modi 12h/13h ausgelegt. Bestehende Zeichen-, Cursor- und Scrollfunktionen sind weiterhin Textmodusdienste; Zeichenausgabe in Grafikmodi fehlt noch.

Scanout liest die Register und Planes. Das Ergebnis enthält Breite, Höhe und Modusgeneration. Die GUI übernimmt neue Abmessungen ohne Neustart und zeigt sie in Statusleiste/Hardwaremenü. Filter, Scale2x/3x, Seitenverhältnis und VSync bleiben Hosteinstellungen. Bereits veröffentlichte Frames behalten ihre Pixel beim Moduswechsel.

## Zeit und offene Funktionen

Status-1-Polling verwendet virtuelle Zeit: angenähert 70 Hz für die 400-Zeilen-Familie und 60 Hz für 480 Zeilen. Die Abfrage verändert den Zeitstand nicht. Es gibt noch keine aus allen CRTC-/Pixeltaktregistern abgeleiteten Rasterereignisse, exakten Blank-/Retrace-Intervalle, VRAM-Wartezyklen oder Registereffekte innerhalb einer bereits ausgegebenen Rasterzeile.

Offen sind unter anderem vollständige CRTC-Adressumschaltung/Line Compare/Split Screen, alle Fontbanks und vollständiger CP437-Font, sämtliche BIOS-Text-/Grafikfunktionen und Seiten, vollständige Mode-X-/Sondermodusabnahme, CGA/EGA-Schiebemodi, VGA-Interrupts, VBE/Banking sowie SVGA/Voodoo. Unverkettete Speicherzugriffe sind getestet; dies ersetzt keine Abnahme beliebiger direkt programmierter VGA-Modi.

## Nachweise

- `VgaTests.cs`: 16 Geräte-/Firmwaretests, unter anderem Latchquellen, alle Schreib-/Lesemodi, Logik, Speicherfenster, DAC-Wrap, CRTC-Schutz, virtuelle Retrace-Abfrage, Moduswechsel, No-Clear, Pixel-XOR und Palette bis zur Segmentgrenze.
- `tests/guests/vga-test.asm`: eigene DOS-COM-Programme, die FreeCOM tatsächlich vom FAT16-/IDE-Medium lädt. Sie prüfen Modusabfrage, Pixel-/DAC-Dienste, direkte Planeschreibzugriffe und Retrace-Polling. Ein Tastendruck oder `/A` stellt Textmodus wieder her und beendet das Programm über DOS.
- `scripts/test-vga.ps1 -SkipBuild -Gui`: je zwei unabhängige Grafik-Kaltstarts, vollständiger Pixelvergleich durch einen separaten Python-Prüfer und ein gemeinsamer DOS-Lauf mit 13h → Text → 12h → Text → weiterer DOS-Befehl. Zusätzlich je sechs OpenGL-Aufnahmen mit Menü und Hostfiltern. Der Test benötigt NASM 3.02 und Python 3; Ergebnisse liegen ausschließlich unter ignorierten `artifacts`.
- Das Vorbereitungswerkzeug akzeptiert nur die per SHA-256 bekannte minimale FreeDOS-Basis. Es fügt die eigenen COM-Dateien in freien FAT16-Clustern einer neuen Datei hinzu; Bootcode, Kernel, Shell und Startdateien bleiben erhalten. Quelle und neue Probeplatte werden nur lesend gestartet und nach den Tests erneut gehasht.

Technische Primärgrundlage: [IBM VGA/XGA Technical Reference, Mai 1992](https://bitsavers.org/pdf/ibm/pc/cards/IBM_VGA_XGA_Technical_Reference_Manual_May92.pdf), insbesondere VGA-Funktion/Schreibmodi und Register in Kapitel 2. Es wurde kein fremder Emulatorcode übernommen.
