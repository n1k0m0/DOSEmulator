# Mediencheckpoints als erster Snapshot-Teil

Stand: 26. Oktober 2026. Dies ist der Medienbaustein für die koordinierte M3-Zustandssicherung. **Ein Mediencheckpoint enthält keinen CPU-/RAM-/Geräte-/Schedulerzustand.** Seine Zweige laden als Kaltstart, ebenso wie bisher gespeicherte PCs. Die Endung ist deshalb `.dosemedia`, nicht `.dossav`.

## Tatsächlicher Umfang

`MediaCheckpointStore.Capture` läuft zwischen CPU-Schritten auf dem alleinigen Emulations-Owner. Er erfasst alle vier festen IDE-Plätze und beide eingelegten Disketten. Leere Plätze bleiben leer; schreibgeschützte RAW-Medien bleiben schreibgeschützt. Nicht dateibasierte Test-/Spezialmedien werden vor dem Erstellen eines Zielverzeichnisses abgewiesen.

Die geschützten Basisimages werden durch vollständige SHA-256-Identität und Sektoranzahl bezeichnet. Für jedes aktive COW wird der vollständige dauerhaft geschriebene DOSCOW01-Journalstand in eine neue Datei kopiert, einschließlich überschreibender älterer Datensätze. Header, alle LBAs und alle Datensatz-Prüfsummen werden dabei geprüft. Kopieren verändert weder Sektorcache noch Appendcursor oder Lease des aktiven Mediums. Der aktive PC kann danach auf seinen bisherigen Journaldaten weiterarbeiten; seine späteren Änderungen erreichen die getrennten Checkpoint-Dateien nicht.

Eine Mediengeneration ist hier eine **vollständige Journal-Kopie**, keine dünne Eltern-/Kind-Kette und keine Kopie des ganzen RAW-Basisimages. Speicherplatz und Haltezeit wachsen mit der Journalgröße; Basisprüfung und Export sind synchrone Hostarbeit ohne Fortschritt der virtuellen Gastzeit. Das ist keine zusätzliche Optimierungs- oder Latenzzusage.

## Dateiformat und Veröffentlichung

Ein neues Verzeichnis `Name.dosemedia` enthält:

- `media.json`: Version 1, `kind=DOSE-MEDIA-1`, Generation-ID, gemeinsamer virtueller Erfassungszeitpunkt, PC-Definition und alle Medienidentitäten.
- `ide0.cow` bis `ide3.cow` und `floppy0.cow`/`floppy1.cow`, soweit das jeweilige Medium tatsächlich beschreibbar war.
- Keine Originalimages, RAM-Dumps oder fremden ROMs.

Jede Medienzeile nennt Bus/Slot, Sektoranzahl, Basis-SHA-256 und gegebenenfalls den fest vorgegebenen Journal-Dateinamen, die vollständige Bytelänge und SHA-256. Relative Basispfade beziehen sich auf das endgültige Checkpoint-Verzeichnis. Die virtuelle Zeit ist nur die Identität des gemeinsamen Schnittpunkts; sie lässt sich ohne Hardwarezustand nicht fortsetzen.

Zunächst werden neue Dateien in einem frischen benachbarten temporären Verzeichnis geschrieben und mit `Flush(true)` abgeschlossen. Erst das abschließende Umbenennen veröffentlicht das gemeinsame Verzeichnis. Ein bereits vorhandenes Ziel wird niemals überschrieben. Bei Fehlern werden ausschließlich unsere eigenen temporären Dateien entfernt; aktive Medien und der bisherige PC bleiben nutzbar. Ein Prozessabbruch kann einen nicht veröffentlichten temporären Ordner hinterlassen. Es gibt noch keinen allgemeinen Stromausfall-/Dateisystem-/Mehrdatei-Transaktions- oder automatischen Garbage-Collection-Vertrag.

## Unabhängige beschreibbare Zweige

`CreateBranch` liest ein höchstens 1 MiB großes Manifest, weist doppelte/ungekannte Felder und unpassende Versionen zurück und prüft Topologie, Profil, Diskettengeometrie, sämtliche Abhängigkeiten, vollständige Basis-/Journalhashes und Journalgrenzen. Journalpfade sind auf ihre festen direkten Dateinamen beschränkt. Basis- und Generation-Dateien bleiben während Prüfung und Kopie gegen Schreiben/Löschen geöffnet.

Erst ein vollständig vorbereiteter neuer `.dosebranch`-Ordner wird veröffentlicht. Er enthält eigene vollständige Journal-Kopien sowie `branch.dospc.json`; alle Basisimages bleiben nur lesbar referenziert. Die normalen COW-Prüfungen validieren Header, Basisbindung, sämtliche LBAs und Checksummen auf den neuen privaten Kopien, bevor diese veröffentlicht werden. Zwei Zweige können sich gleichzeitig und unabhängig weiterentwickeln. Die versiegelten Checkpoint-Journale werden niemals als aktive Schreibjournale geöffnet.

„Versiegelt“ bezeichnet hier die getrennte, vollständig gehashte Generation. Es ist kein signierter oder gegen absichtliche gemeinsame Änderung von Manifest und Daten authentifizierter Container. Unveränderte Generationshashes werden bei jeder Zweigerstellung geprüft. Verschieben erfordert wie bei PC-Definitionen weiterhin vorhandene Basisimages und passende relative Pfade; das Paket ist kein portabler Betriebssystemexport.

## CLI und IPC

Nach einer tatsächlich ausgeführten Gastaktion, beispielsweise einem abgeschlossenen DOS-Dateischreibvorgang:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll `
  --machine Spiele-PC.dospc.json --scenario eigene-aktionen.json `
  --stop-text MEDIEN-PUNKT --steps 200000000 --capture-media Stand.dosemedia

dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll `
  --branch-media Stand.dosemedia --output Neuer-Zweig.dosebranch

.\run.cmd -NoBuild -Machine Neuer-Zweig.dosebranch/branch.dospc.json
```

Checkpoint und Zweig benötigen ein noch nicht vorhandenes Zielverzeichnis mit der angegebenen Endung; dessen Elternverzeichnis muss bereits existieren. Der erste Aufruf erfasst nach dem Headless-Lauf am Owner-Haltepunkt. Die beiden nächsten Aufrufe bereiten einen unabhängigen Medienzweig vor beziehungsweise starten ihn kalt.

IPC `CreateMediaCheckpoint` erhält `{"path":"...neuer.dosemedia"}`; `CreateMediaBranch` erhält `{"path":"...vorhandener.dosemedia","destination":"...neuer.dosebranch"}`. Beide laufen auf dem Owner und ersetzen den aktiven PC nicht. Fähigkeiten melden `mediaCheckpoints=true`, ausdrücklich `runningStateSnapshots=false`. `LoadMachineDefinition` kann die erzeugte PC-Datei anschließend kalt laden. Eine beschädigte Sicherung liefert eine Fehlerantwort, ohne den Owner-Pump oder die alte Maschine aufzugeben. Eine native GUI-Menü-/Mausbedienung ist hierfür noch nicht implementiert oder abgenommen.

## Konsistenzgrenzen und nächster Schritt

Der Schnittpunkt bildet die bis dahin dauerhaft abgeschlossenen Sektorschreibvorgänge ab. Die Basis bleibt unverändert, schon ausgeführte Druck-/MIDI-/Audioausgaben werden durch diesen Baustein nicht zurückgespult. Ein Dateisystem kann an einer beliebigen Geräte-/Gastbefehlsgrenze gerade mitten in einer mehrsektorigen Änderung stehen. Für einen Mediencheckpoint mit anschließendem Kaltstart muss Gastsoftware ihre Daten selbst in einen dafür geeigneten Zustand bringen. Es wird keine atomare FAT-Transaktion oder automatische DOS-Pufferleerung erfunden.

Für einen vollständigen laufenden Snapshot fehlen weiterhin:

1. Explizit versionierte CPU-/MMU-/rohe-x87-/RAM-/VRAM-/Controllerzustände einschließlich interner FIFOs, IRQ-/DMA-Phasen und bereits übernommener Taskkontexte.
2. Rekonstruierbare Schedulerereignisse mit virtueller Zeit, Taktresten und stabiler Reihenfolge; C#-Callbacks werden nicht als Dateizustand serialisiert.
3. Gemeinsame Veröffentlichung dieser Hardwareblöcke mit genau den passenden Mediengenerationen, vollständige Importprüfung vor Übernahme und Rollback auf den weiterhin nutzbaren alten PC.
4. Neue Ein-/Ausgabegenerationen für Hostqueues, UI-Reader-Leases und virtuelle Gegenstellen sowie Nachweise mitten in ATA/FDC/PCM-/Portaktivität.

Das vorhandene Medienformat ist eine geprüfte Voraussetzung, keine Abnahme dieser vier Punkte. M3 und vollständige Karten-/x87-Präzision bleiben offen. Die vorhandenen PC-Definitionen und Videos behalten ihre dokumentierte Kaltstart-/Buildzuordnung.

Reproduktion des Medienteils: `scripts/test-media-checkpoint.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>`. Alle Schreibpfade liegen in frischen privaten Kopien. Persönliche Spiel-COWs sind ausgeschlossen. Verbindliche aktuelle Ergebnisse stehen im [Implementierungsstand](IMPLEMENTATION-STATUS.md).

## Nachfolgender vollständiger Snapshot

Der inzwischen verfügbare [vollständige lokale Snapshot](SNAPSHOTS.md) ergänzt CPU, rohes x87, RAM, Geräte und Scheduler und erlaubt tatsächliche Fortsetzung. Die öffentlichen Medienbefehle dieses Dokuments bleiben unverändert reine Medienaktionen; `.dosemedia` enthält weiterhin keinen laufenden Hardwarezustand. Nur der volle `.dossav`-Container darf den bekannten unveränderlichen In-Memory-Testdatenträger als zusätzliche private RAW-Basis einschließen.

## Gekochte ISO-Basen im ATAPI-Teilumfang 11529

Ein [installiertes ATAPI-CD-ROM](hardware/ATAPI-CDROM.md) reserviert einen der vier IDE-Plätze. `opticalDiscs` beschreibt ausschließlich dessen schreibgeschützte ISO-Basis, getrennt von `hardDisks`. Die versiegelte Generation besitzt Art `cdrom`, Blockgröße 2048 und vollständigen Basishash und getrennte Blockzahl; es gibt keinen CD-Journalpfad. Höchstens vier IDE-Medien insgesamt und zwei Disketten bleiben möglich. Leere Lade und fehlendes physisches Laufwerk sind getrennt.

Neue Metadaten schreiben `sectorSize` ausdrücklich; alte unterstützte HDD-/Diskettensätze ohne dieses Feld lesen sich weiterhin als 512. Ältere Programme können neue Metadaten ablehnen. Der Medienzweig öffnet und hasht die weiterhin unveränderliche ISO unter Leseschutz, kopiert sie nicht als 512-Byte-RAW und erzeugt keinen optischen COW. Ein Mediencheckpoint bleibt ein Kaltstartvertrag: guest-ejectete Lade-/Lock-/Sensezustände gehören erst zum vollständigen Hardware-Snapshot. Keine allgemeine portable Medien- oder Stromausfalltransaktionszusage.
