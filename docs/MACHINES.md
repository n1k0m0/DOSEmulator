# Festplatten und gespeicherte PCs

## Optionale parallele Bildaufbereitung

**Ctrl+Alt+F10 → VIDEO SETTINGS → MULTICORE VIDEO → ON/OFF** schaltet im privaten C14-Frontend den zusätzlichen Grafikworker live um. Die Einstellung wird automatisch gespeichert und ist zunächst **OFF**. Bei größeren Bildern können zwei Hostthreads getrennte Bildhälften umrechnen; Cursor und Veröffentlichung folgen erst nach Abschluss. Die eigentliche Gast-CPU und Gerätezeit bleiben unverändert. Kleine Bilder behalten ihren bisherigen Pfad. [Messwerte und Grenzen](hardware/HOST-THREADING.md): ungefähr45Prozent weniger reine Umrechnungszeit in der RGB565-Probe, bislang kein belastbarer Gesamt-FPS-Gewinn. Ein Neustart mit dem neuen privaten Build ist nötig; laufende Instanzen werden nicht ersetzt.

## Debuganzeige ein- und ausblenden

**Ctrl+Alt+F10 → VIDEO SETTINGS → DEBUG OVERLAY → ON/OFF** schaltet die beiden unteren Statuszeilen sofort um. Die Einstellung wird automatisch unter `showDebugOverlay` in der lokalen `settings.json` gespeichert. Bei älteren Einstellungen bleibt die Anzeige zunächst eingeschaltet.

Ohne Anzeige steht auch der bisher reservierte 40-Pixel-Bereich dem Gastbild zur Verfügung; die Klickfläche folgt derselben Geometrie. Menü, Fehlermeldungen und die Geschwindigkeitsanzeige im Fenstertitel bleiben erreichbar. Die ausgeblendete Leiste erzeugt keine Texte oder Bitmap-Zeichenaufrufe; bei ansonsten ruhiger Oberfläche entfallen nach dem einmaligen Löschen auch ihre laufenden Overlay-Uploads und der zusätzliche Zeichenpass. Ein allgemeiner FPS-/Echtzeitgewinn ist daraus nicht gemessen.

Dies ist eine reine Frontendeinstellung. Die fünf VM-Module und die Snapshotidentität des verwendeten Builds ändern sich dadurch nicht. Der neue private Frontendbuild ist über `artifacts/tests/debug-overlay-work-path.txt` dokumentiert; eine laufende Instanz lädt Änderungen erst beim nächsten Start.

## Zusätzliches PIIX-IDE-Validierungsprofil

`profiles/dose-430fx-piix-ide-dev-v1.json` wählt die [eigene IDE-/Busmaster-Platine](hardware/PIIX-IDE.md): P54C B5 subset/100 MHz/32 MiB, 82437FX A2 und 82371FB A1 an 00:07.0/1. Factory, Ownerwechsel, CLI/Hardwaremenü und volle Snapshots verwenden feste Peers. DOSEBios programmiert DRBs, BMIBA=C000h, beide IDETIM.DE und MIRQ0→IRQ15, schaltet IOSE an und lässt BME für den Gast aus. Alte Profile bleiben unverändert eingegrenzt. Die acht DOS-/EMM386-Läufe mit je zwei Fortsetzungen auf Build 10755 belegen diesen Teilumfang; Resetdomänen/CF9/INIT und vollständiger Windows-PC bleiben offen.

## Zusätzliches PIIX-IRQ-Validierungsprofil

`profiles/dose-430fx-piix-irq-dev-v1.json` wählt die [eigene PIRQ-/ELCR-Platine](hardware/PIIX-IRQ.md) mit festem P54C B5/100 MHz/32 MiB, 82437FX A2 und begrenzter ursprünglicher PIIX-Funktion0 an `00:07.0`. CLI `--profile` und gespeicherte PC-Definitionen konstruieren das vollständige Tupel; CPU/RAM bleiben im Menü fest. Acht neue DOS-/EMM386-Starts mit voller Fortsetzung sind auf dem 10141er-Build geprüft. Alte Profile erhalten diese Komponente nicht. Die zwei bestehenden ATA-Kanäle sind weiter die bisherigen DOSE-Controller; dies ist kein PIIX-IDE-, CF9-/INIT- oder vollständiger historischer Windows-PC-Nachweis.

## Kurze Tastendrücke beim Batchstart

Die [Repeatkorrektur](KEYBOARD-REPEAT.md) wartet vor der ersten Wiederholung 500 ms; der erste Tastendruck wird sofort geliefert. Erst längeres Halten wiederholt mit 50 ms Abstand. Damit erzeugt der geprüfte 120-ms-Druck im MS-DOS-/FreeDOS-Prompt genau ein Zeichen statt drei. `run-doom2.cmd` verwendet denselben aktualisierten Starter. Eine laufende Instanz verwendet ihre bereits geladenen Dateien; die Korrektur gilt beim neuen Start. Die persönliche Doom-Sitzung und Spielstände werden dafür nicht geändert.

## Aktuelle Medien- und Eingabekorrekturen

Der [zweite Auditfolgebuild](AUDIT2-FIXES-2026-10-27.md) kann beschreibbare Disketten A/B zwischen den Slots einer neu geladenen eigenen PC-Definition tauschen: das exakte bestehende Basis-/COW-Paar wird über beide alten Slots wiederverwendet. Vollständige Vorbereitung und Rollback erhalten bei Fehlern die laufende Maschine und Journale. Falsche Ganzzahl-IPC-Payloads liefern kontrollierte Fehlerantworten, nachfolgende Requests bleiben nutzbar. Gehaltene Eingaben nach einem vollständigen Import tragen den vom UI bestätigten Generationstoken; alte Eingaben werden abgewiesen. Der monotone Wiederholungspfad holt lange Hostpausen nicht als Eingabeschwall nach. Native Windows-/Maus-/Repeat-Bedienabnahme bleibt getrennt offen.

## Zusätzliches Host-Validierungsprofil

`profiles/dose-430fx-host-dev-v1.json` wählt den [82437FX-A2-Hostteilumfang](hardware/430FX-HOST.md) mit festem P54C-B5-Teilmodell, 100 MHz und 32 MiB. Über CLI `--profile` beziehungsweise eine eigene gespeicherte PC-Definition wird dieses vollständige geprüfte Tupel konstruiert; gespeicherte PCs bleiben Kaltstarts. Im Hardwaremenü sind CPU/RAM dieser Platine fest markiert, unveränderte Auswahl fordert keinen Neustart an. Die bisherigen 486-/P54C-AT-Auswahlen bleiben getrennt. PIIX, vollständiges historisches 430FX und Windows 95 sind weiterhin offen; native Bedienung ist nicht neu abgenommen.

Im Emulator Ã¶ffnet **Strg+Alt+F10** das MenÃ¼. Unter **MEDIA â†’ HARD DISKS / CREATE IMAGEâ€¦** stehen vier IDE-PlÃ¤tze und **CREATE BLANK DISK IMAGEâ€¦** bereit. Die GrÃ¶ÃŸe lÃ¤sst sich in MiB oder GiB eingeben; das sind ausdrÃ¼cklich 1024er-Einheiten. Beispielsweise sind 512 MiB = 0,5 GiB. UnterstÃ¼tzt werden ganze 512-Byte-Sektoren von 1 MiB bis 128 GiB.

Die neue RAW-Datei enthÃ¤lt zunÃ¤chst ausschlieÃŸlich Nullen. Auf NTFS wird sie nach MÃ¶glichkeit als Sparse-Datei angelegt: ihre logische KapazitÃ¤t ist sofort vorhanden, unbeschriebene Bereiche benÃ¶tigen wenig tatsÃ¤chlichen Speicherplatz. Andere Dateisysteme verwenden explizite Nullschreibzugriffe. Eine vorhandene Zieldatei wird niemals Ã¼berschrieben; Abbruch und Fehler entfernen nur die neu angelegte temporÃ¤re Datei. AnschlieÃŸend die Datei an einem IDE-Platz mit **ATTACH WITH PRIVATE OVERLAYâ€¦** anschlieÃŸen. Partitionieren und Formatieren erfolgt mit den Werkzeugen des Gastbetriebssystems, beispielsweise DOS FDISK und FORMAT. Die Erstellung allein installiert kein Betriebssystem.

| Platz | Anschluss | ATA-Ports | IRQ |
| --- | --- | --- | --- |
| 0 | Primary Master | 1F0h / 3F6h | 14 |
| 1 | Primary Slave | 1F0h / 3F6h | 14 |
| 2 | Secondary Master | 170h / 376h | 15 |
| 3 | Secondary Slave | 170h / 376h | 15 |

Leere PlÃ¤tze sind erlaubt. Das BIOS nummeriert belegte PlÃ¤tze in dieser Reihenfolge dicht als 80h, 81h usw. und startet beim IDE-Boot von der ersten vorhandenen Platte. Ein Topologiewechsel startet den Gast neu. Die Basisimages bleiben schreibgeschÃ¼tzt; jede beschreibbare Platte benÃ¶tigt ein eigenes `.cow`-Overlay. Zwei laufende PCs kÃ¶nnen dasselbe beschreibbare Journal nicht gleichzeitig Ã¶ffnen. Bei einem fehlgeschlagenen Medienwechsel bleibt der bisherige PC benutzbar.

Unter **SAVED MACHINES â†’ SAVE THIS PCâ€¦** werden Name, stabile PC-Kennung, Hardwareprofil, vier IDE-Zuordnungen, zwei Diskettenzuordnungen und die privaten Overlaypfade in einer `.dospc.json` gespeichert. **LOAD PCâ€¦** lÃ¤dt die Konfiguration und startet den PC kalt. Relative Pfade beziehen sich auf den Ordner der PC-Datei. Zum Umziehen einer Maschine auch die referenzierten Images und Overlays mitnehmen und ihre relativen Verzeichnisse beibehalten. Die Konfigurationsdatei enthÃ¤lt keine Betriebssystemdateien und keinen laufenden RAM-/CPU-/GerÃ¤tesnapshot. Der inzwischen verfÃ¼gbare [Mediencheckpoint](MEDIA-CHECKPOINTS.md) sichert getrennte Journalgenerationen und erzeugt eigene Kaltstartzweige. VollstÃ¤ndige lokale **CPU-/RAM-/GerÃ¤te-/Scheduler-Snapshots** sind separat Ã¼ber SAVE/LOAD FULL SNAPSHOT verfÃ¼gbar; siehe [Snapshotvertrag](SNAPSHOTS.md).

## CPU-Auswahl

Die CPU-Zeile unter MACHINE / HARDWARE wählt entweder den bisherigen 486/33-MHz-PC oder den eigenen [DOSE AT mit P54C-B5-Teilmodell und 90 MHz](hardware/P54C-PC.md). Die Auswahl ändert CPU/Plattformkennung/Takt gemeinsam und führt einen kalten Ownerwechsel mit erhaltenen Medien aus. Alte PC-Dateien und Standard bleiben 486; eigene PC-Namen und IDs bleiben erhalten. Eine .dospc.json bleibt auch beim P54C ein Kaltstart. Das Profil ist keine historische 430FX-/Windows-95-Freigabe; die native Bedienabnahme bleibt getrennt.

## Kommandozeile

Nach `scripts/build.ps1` eine neue Platte erzeugen:

```powershell
$runner = '.\artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll'
dotnet $runner --create-disk 'D:\DOS-PCs\Spiele\daten.img' --size 1.5 --unit GiB
```

Die Dezimalzahl verwendet im CLI einen Punkt, ohne Tausendertrennzeichen. Ein Komma wird abgewiesen, damit beispielsweise `1,5` nicht versehentlich zu 15 wird. Der Windows-GrÃ¶ÃŸeneditor verwendet die lokale Zahlendarstellung. Das Zielverzeichnis muss bereits existieren. Beide Wege verwenden dieselbe KapazitÃ¤tsvalidierung.

Eine eigene Installation mit einer zusÃ¤tzlichen Platte verbinden und als PC speichern:

```powershell
dotnet $runner --disk 'D:\DOS-PCs\Spiele\system.img' --overlay 'D:\DOS-PCs\Spiele\system.cow' `
  --disk1 'D:\DOS-PCs\Spiele\daten.img' --overlay1 'D:\DOS-PCs\Spiele\daten.cow' `
  --save-machine 'D:\DOS-PCs\Spiele\Spiele-PC.dospc.json' --machine-name 'Mein Spiele-PC'
.\scripts\run.ps1 -NoBuild -Machine 'D:\DOS-PCs\Spiele\Spiele-PC.dospc.json'
```

`--disk2/--overlay2` und `--disk3/--overlay3` belegen die Ã¼brigen PlÃ¤tze; `--profile` Ã¼bernimmt das Hardwareprofil. `--save-machine` speichert und beendet den Headless-Aufruf. Ohne `--overlayN` ist das betreffende Basisimage nur lesbar. `--machine` kann nicht gleichzeitig mit abweichenden Hardware-/Medienoptionen verwendet werden. Die GUI und Headless verwenden dieselbe Ladeimplementierung.

## Nachweis und Grenzen

Die GerÃ¤te-/BIOS-Tests prÃ¼fen alle vier AnschlÃ¼sse, LÃ¼cken, getrennte IRQs, CHS-/EDD-Geometrie und die LBA28-Grenze. Ein eigener COM-Gast prÃ¼ft auf tatsÃ¤chlich vier angeschlossenen Platten unter MS-DOS 6.22, FreeDOS und originalem EMM386 NOEMS/RAM reale INT13-Schreib-/LesevorgÃ¤nge und Persistenz Ã¼ber Kaltstarts. Die grÃ¶ÃŸte dabei tatsÃ¤chlich beschriebene Platte ist 1536 MiB groÃŸ; ein zusÃ¤tzlicher CHS-Zugriff liegt oberhalb der alten 504-MiB-Grenze. Der unabhÃ¤ngige PrÃ¼fer kontrolliert sÃ¤mtliche PIO-Datenworte, Controller-/Laufwerksauswahl und Gast-RAM-Beweise. Der PC-Editor ist zusÃ¤tzlich intern ohne Windows-Eingaben geprÃ¼ft; die nach Benutzerwunsch verschobene native Mauswiederholung wird dadurch nicht ersetzt.

128 GiB ist die emulierte LBA28-Dateigrenze, keine allgemeine DOS- oder Dateisystemfreigabe. Insbesondere DOS 6.22 hat eigene FAT16-/Partitionsgrenzen; groÃŸe Images benÃ¶tigen passende Gastwerkzeuge und gegebenenfalls mehrere Partitionen. LBA48, ATAPI/CD-ROM und eine allgemeine Windows-Abnahme sind hiermit nicht implementiert. Der erste Overlaystart prÃ¼ft die vollstÃ¤ndige SHA-256-IdentitÃ¤t des Basisimages; bei sehr groÃŸen Dateien kann das Laden entsprechend dauern. Der Overlay-Nutzdaten-Cache ist auf 4096 Sektoren begrenzt, der vollstÃ¤ndige Sektorindex wÃ¤chst mit der Zahl geÃ¤nderter Sektoren.

Eigene Wiederholung mit privaten Testmedien: `scripts/test-machine-storage.ps1 -SkipBuild -Gui -Disk <Basis> -Overlay <Installations-COW>`, optional mit `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay`. Das Skript erzeugt neue Testimages/Overlays in ignorierten Artefakten. Die ursprÃ¼nglichen Basen werden nicht beschrieben.


## Optionaler analoger Gameport

`gameport=ibm-game-control-development` oder die Gameport-Auswahl im HardwaremenÃ¼ aktiviert eine eigenstÃ¤ndige IBM-Analogkarte nach Kaltstart. Alte gespeicherte PCs ohne dieses Feld behalten `none`; die SB16-Auswahl aktiviert die Karte nicht automatisch. Die Auswahl wird im PC gespeichert, aktuelle Ã¤uÃŸere Joystickwerte und laufende Impulse dagegen nicht: ein gespeicherter PC bleibt eine Kaltstartdefinition.

Es gibt noch keine Host-Gamepad-Zuordnung. FÃ¼r kontrollierte virtuelle Eingaben bieten IPC `SetGameportInput` und Szenarien vier WiderstÃ¤nde (`axOhms`, `ayOhms`, `bxOhms`, `byOhms`, jeweils 0..100000 oder `null`) sowie `buttons=0..15`. Diese Eingaben verÃ¤ndern keine Gastregister oder fertigen Messwerte; der Gast muss die Karte tatsÃ¤chlich abfragen. [Hardwarevertrag, Gastnachweise und offene Grenzen](hardware/GAMEPORT.md).

## Optionaler virtueller COM1-Port

`serialPort=pc16550d-com1-development` oder COM1 im HardwaremenÃ¼ aktiviert nach Kaltstart einen National PC16550D an 3F8h/IRQ4. Alte PCs ohne dieses Feld behalten `none`. Die Auswahl gehÃ¶rt zur gespeicherten PC-Definition; FIFO-/Schieberegister und Ã¤uÃŸere Zeichen/Pins werden beim Kaltladen verworfen. Es gibt keinen automatisch geÃ¶ffneten Hostport.

IPC `SendSerialInput` beziehungsweise Szenarien mit `serial` liefern vollstÃ¤ndige Ã¤uÃŸere Frames und optionale logische CTS/DSR/RI/DCD-Werte. Headless `--serial-echo` verbindet auf ausdrÃ¼ckliche Auswahl eine getaktete virtuelle Gegenstelle. Gastsoftware muss Baud/Format und Ports selbst programmieren; DOSEBios bietet die begrenzten INT14-Dienste 00â€“03. COM2, Host-COM, Terminal-/Modem-/serielle MauskompatibilitÃ¤t sind dadurch nicht abgenommen. [Konkreter Vertrag und DOS-Nachweise](hardware/SERIAL-16550.md).

## Optionaler virtueller LPT1-Port

`parallelPort=ibm-printer-adapter-development` oder LPT1 im HardwaremenÃ¼ aktiviert nach Kaltstart den ausgangsgerichteten IBM Printer Adapter 6361507 an 378h/IRQ7 samt originalen Adressaliasen. Alte PCs ohne Feld behalten `none`. Eine gleichzeitig aktive SB2-/SB16-Karte auf IRQ7 wird vor Ãœbernahme abgewiesen. Die PC-Definition speichert die Karte, keine Pinpegel, laufenden Handshakes oder Dateiausgabecursor.

Die Auswahl verbindet keinen Drucker. Headless `--parallel-sink` schlieÃŸt ausdrÃ¼cklich eine nominale virtuelle Rohbyte-Senke an; `--parallel-output <neue.bin>` speichert angenommene Bytes. Ohne Senke bieten IPC `SetParallelInput` beziehungsweise Szenarien mit `parallel` Ã¤uÃŸere BUSY-/ACK-/PE-/SELECT-/ERROR-Pegel. DOSEBios stellt INT17/AH00â€“02 bereit. Es werden keine Hostdrucker geÃ¶ffnet; EPP/ECP, bidirektionale Daten, Seitendruck und native Ressourcenbedienung sind damit nicht abgenommen. [Konkreter Register-/Handshake-/BIOS-Vertrag und DOS-Nachweise](hardware/PARALLEL-PRINTER.md).

## Optionaler CT1740-MPU-UART

Bei gewÃ¤hlter SB16 schaltet `midiInterface=ct1740-mpu401-uart-development` beziehungsweise MIDI im HardwaremenÃ¼ ihren MPU-UART ein. Der separate Ressourcenwert `midiBasePort=768|816` bedeutet 300h/330h; IRQ und Mixer82-Bit 2 gehÃ¶ren zur Soundkarte. Alte PCs bleiben bei `none`. Der RÃ¼ckwechsel zu einer anderen Soundkarte deaktiviert die MPU-Auswahl. Die BLASTER-Anzeige ergÃ¤nzt den passenden P-Wert; Benutzer-Startdateien bleiben unverÃ¤ndert.

Auswahl und Port werden gespeichert, laufende Frames und Ã¤uÃŸere Daten beim Kaltladen verworfen. `SendMidiInput`/Szenarien liefern nur vollstÃ¤ndige Ã¤uÃŸere Bytes; Headless `--midi-output <neue.bin>` speichert tatsÃ¤chlich gesendete Bytes. Es entsteht kein Ton, Host-MIDI-Endpunkt oder Intelligent Mode. [Konkreter Zeit-/Puffer-/IRQ-Vertrag und DOS-Nachweise](hardware/MIDI-MPU401.md). Der [vollstÃ¤ndige lokale Snapshot](SNAPSHOTS.md) erhÃ¤lt dagegen laufende Frames, Queues, Generationen und passende Medien.

## Zusätzliches ATAPI-PIO-Entwicklungsprofil

`profiles/dose-430fx-piix-s3-atapi-dev-v1.json` installiert den [benannten SFF-8020i-PIO-Teilumfang](hardware/ATAPI-CDROM.md) auf dem festen S3/430FX/PIIX-Board. `atapiSlot` reserviert 0..3, mitgeliefert 2/Secondary Master. Die installierte Lade darf leer bleiben. Ein gespeicherter PC fügt `opticalDiscs: [{ "slot": 2, "imagePath": "D:/Privat/Install.iso" }]` seiner vorhandenen Definition hinzu. HDDs dürfen den reservierten Platz nicht doppelt belegen; ein impliziter Demo-HDD0-Fallback wird bei CD0 unterdrückt.

CLI: `--profile profiles/dose-430fx-piix-s3-atapi-dev-v1.json --cdrom <eigene.iso>`. Das Medienmenü bietet OPTICAL DRIVE und MOUNT/REMOVE ISO mit ausdrücklichem Neustart an. IPC `SetOpticalDisc {path}` beziehungsweise ohne Pfad bereitet denselben kalten Ownerwechsel vor; fehlerhafte ISO-/Konfigurationsvorbereitung lässt den bisherigen PC nutzbar. Eine ISO bleibt stets schreibgeschützt, ohne COW oder physisches Hostlaufwerk. Gastseitiges virtuelles START/STOP/Eject/Lock bleibt davon getrennt. Kein nativer Menübediennachweis, kein DOS-CD-Treiber-/MSCDEX- oder Win95-Abnahmeersatz.
