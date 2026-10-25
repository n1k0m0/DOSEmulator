# Reset der Referenzplattform

Stand: 24. Oktober 2026. `Virtual486AT-v1` unterscheidet CPU-Reset, vom Gast verarbeitete Tastatur-Neustarts und Ausschalten/Einschalten.

| Auslöser | Verhalten |
| --- | --- |
| Port 92h, Bit 0 von 0 auf 1 | CPU-Reset am nächsten Instruktionsende. Das Bit bleibt gesetzt, bis Software es löscht. Dieselbe Eins erneut zu schreiben löst keinen weiteren Reset aus. |
| KBC FEh bzw. Fx-Maske mit Bit 0 gleich 0 | CPU-Reset mit modelliertem 6-µs-Puls. Geräte und virtuelle Zeit laufen währenddessen weiter. |
| KBC D1h, Ausgangsport Bit 0 gleich 0 | RESET bleibt als Pegel aktiv. Die CPU bleibt bis zur Freigabe angehalten; Status `ResetHeld`, kein CPU-Fault. |
| IPC `HardwareReset` / Menüeintrag | CPU-Reset auf dem Emulationsbesitzer zwischen Ausführungsblöcken. Der Pausezustand bleibt erhalten. |
| IPC `SendCtrlAltDel` / Menüeintrag | Tatsächliche Make-/Breakcodes durch KBC/IRQ1; BIOS oder Gasttreiber bestimmen die Reaktion. |
| Power-Cycle / Profilwechsel | Neuer CPU-/Gerätegraph, leerer RAM und neue Gerätezeitbasis. Batterie-CMOS und bereits verstrichene RTC-Zeit bleiben erhalten. |

Beim CPU-Reset bleiben RAM, VGA/Fonts/VRAM, PIC, DMA, Controller-/Audiooperationen, RTC/NVRAM, Schedulerfristen und Medien samt exklusiven Overlay-Leases erhalten. Angenommene ATA-Schreibaufträge und FDC-DMA laufen weiter. Der vorhandene CPU-Zustand einschließlich verborgenem CS, CR0, Präfix-/Interruptschatten und HLT wird auf den Entwicklungs-486-Resetvektor gesetzt. Erst die danach ausgeführten **Gastinstruktionen des POST** initialisieren PIC/PIT/VGA neu, setzen IDE über SRST zurück und initialisieren/leeren den Tastaturpfad. Ein Reset leert keine Betriebssystem-Schreibcaches; die eigene DOS-Probe ruft vorher den echten DOS-Pufferflush auf.

Der Batterieerhalt gilt für Maschinenwechsel innerhalb derselben Sitzung. Dauerhafte CMOS-Dateien über Programmneustarts, vollständige RTC-Register-/Shutdownpfade und weitere Resetquellen sind noch offen.

Die eigene Referenzplatine decodiert ihr ROM zusätzlich bei `FFEF0000h`, damit der Bootstrap auch mit maskierter Adressleitung 20 erreichbar bleibt. Dies ist unser dokumentierter Decodervertrag. Vollständige A20-Quellenarbitration, elektrische CPU-/KBC-Zeiten, weitere durch Fx-Masken angesprochene Ausgangspins und Triple-Fault-/Protected-Mode-Resets bleiben offen. Der 6-µs-Puls beginnt an der sicheren Erkennungsgrenze, nicht in einer simulierten 8042-Mikroinstruktion.

## Firmware und Diagnose

Die eigene IRQ1-Firmware erkennt Strg+Alt+Entf, schreibt `1234h` nach `0040:0072` und erzeugt den KBC-Puls. Ein residenter Treiber darf anders reagieren: **Das originale MS-DOS-KEYB startet bei dieser Tastenkombination per Software ins BIOS neu**, ohne zusätzlichen Hardwarepuls. Auch das originale Setup durchläuft seinen Neustart als Softwareeinstieg ins BIOS. Neue Bootbanner werden deshalb nicht als Hardware-Resets gezählt.

`MachineStatus.Reset` meldet Hardwareanzahl, letzte Quelle, virtuellen Zeitpunkt, vorher gelesenes Warmstartwort, Haltezustand und den Zähler eigener POST-10h-Ausgaben. Letzterer ist eine Referenzfirmware-Diagnose am vorhandenen Port 80h, kein universeller BIOS-Detektor. Hardware-Resets invalidieren vorbereitete Session-Konfigurationspläne. Nach Power-Cycle beginnen die Hardwarezähler der neuen Maschine bei null. Die begrenzte E9-Diagnosehistorie bleibt bei CPU-Reset erhalten.

Szenarien können `minimumResetCount` und `minimumPost10Count` zusätzlich zum Bildschirm-/Debugmerkmal verlangen. So gelangen Eingaben nicht in einen noch sichtbaren Prompt des vorherigen Boots. Headless-Stopmarker werden bei Szenarien erst nach vollständiger Eingabe akzeptiert; eine teilweise getippte ECHO-Zeile genügt nicht. `--bios-trace` zeichnet auch INT 19h auf.

## Abnahme

`scripts/test-guest-reset.ps1 -SkipBuild -Gui` verwendet neue private Overlays und eine neue Diskette mit der eigenen **106-Byte-RESET.COM**. Zwei unabhängige FreeDOS-Läufe durchlaufen je zwei Hardware-Resets; zwei MS-DOS-Läufe zusätzlich den vom originalen KEYB verarbeiteten Tastatur-Neustart. Ein nativer MS-DOS-Prozess prüft denselben Pfad mit sechs GL-Aufnahmen und Rückkehr zur Kommandozeile. Unabhängige FAT-/Journalprüfungen belegen die gespeicherte RESET.TXT, identische FATs, keine verlorenen/kreuzverknüpften Cluster und alle 688 FreeDOS- bzw. 129 MS-DOS-Originaldateien unverändert.

Mit `-MsdosDisk` und `-MsdosOverlay` lässt sich eine andere lokale Kopie derselben unveränderten Setup-Installation angeben; die Eingabe erwartet deren originales KEYB-GR-Startprofil. Ohne diese Parameter dient die im Status dokumentierte kanonische Installation als schreibgeschützte Quelle.

Mit `-Install` läuft das originale deutsche Drei-Disketten-Setup zusätzlich **in einem einzigen Headless-Prozess** auf einer neuen leeren Platte, einschließlich Eingabetaste am Neustartdialog: zwei POST-Durchläufe, kein Hardwarepuls, zwei Medienwechsel und 129 installierte Dateien. Kein API-Power-Cycle oder Austausch der Maschine. Dies korrigiert die frühere Annahme, der explizite Host-Kaltstart sei für diesen Setup-Pfad erforderlich; zuvor war nur dieser Weg abgenommen.

Regressionen prüfen außerdem die Grenze nach OUT, Resetvektor bei abgeschaltetem A20, Register-/RAM-/CMOS-Erhalt, gehaltenes RESET, gleichzeitige Quellen, tatsächlich abgeschlossene ATA-Schreibaufträge/FDC-DMA, exklusiven Overlaybesitz, Batterieerhalt beim Power-Cycle und ungültig gewordene Konfigurationspläne. Aktuelle Nachweise: [Implementierungsstand](../IMPLEMENTATION-STATUS.md). Keine Abnahme weiterer Mainboards, vollständiger 486-Zyklusgenauigkeit oder Windows.

## Primärquellen

- [AMD ÉlanSC400/410 User's Manual, Abschnitt 4.9, S. 4-39](https://www.amd.com/content/dam/amd/en/documents/archived-tech-docs/user-guides/21030.pdf): CPU-Reset gegenüber erhaltenen Legacy-Registern, Port-92h-Flanke und gespeichertes Resetbit. Die Referenzplattform übernimmt diese begrenzte Semantik; sie ist keine Élan-Nachbildung.
- [HP Vectra System BIOS Technical Reference, Februar 1988, Tabelle 5-19, S. 5-54](https://www.bitsavers.org/pdf/hp/vectra/45945-90012_Vectra_System_BIOS_Technical_Reference_Manual_Feb1988.pdf): KBC-Ausgangsport, ungefähr 6 µs lange Fx-Pulse und Unterschied zum dauerhaft abgesenkten RESET-Pegel.
- [Intel486 DX Datenblatt, 240440-006, A20M-Signalbeschreibung](https://ardent-tool.com/CPU/docs/Intel/486/datasheets/240440-006.pdf): Anforderungen an A20M um RESET. Unsere zusätzliche ROM-Decodierung ist keine aus dem Datenblatt abgeleitete allgemeine PC-Eigenschaft.
