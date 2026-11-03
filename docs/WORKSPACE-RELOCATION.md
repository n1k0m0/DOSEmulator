# Projektordner verschieben

Der aktuelle lokale Projektordner ist
`K:\KI-Projekte\Anwendungsentwicklung mit KI\DOSEmulator`.
Die normalen Starter bestimmen ihren Projektpfad aus ihrem eigenen Ort und
behalten die bisher ausgewählte unveränderliche Laufzeit. Ein Umzug schaltet
keinen neuen Emulatorbuild frei und migriert keine alten Snapshots.

Die generierten MiniGlide-Starter verwenden jetzt ebenfalls ihren eigenen
Ordner als Ausgangspunkt. Neue MiniGlide-Maschinen speichern Medienpfade auf
demselben Laufwerk relativ zur Maschinendefinition; ausdrücklich ausgewählte
externe Laufwerke behalten absolute Pfade. Die vorhandenen beiden privaten
Unreal-Brückenkopien und die Hardware-Demo wurden ausschließlich an diesen
Metadaten angepasst. Hardware, Maschinenkennung, Basen und COW-Inhalte bleiben
erhalten. Die vorherigen Definitionen und Starter sind privat gesichert.

Ohne Emulatorfenster und ohne Medienzugriff prüfen:

```text
Start-Unreal-Win95-Glide-Host.cmd --inspect
Start-Unreal-Win98-Glide-Host.cmd --inspect
Start-Hardware-Demo-MiniGlide.cmd --inspect
pwsh -NoProfile -File scripts/start-variant.ps1 -Variant win95 -Inspect
pwsh -NoProfile -File scripts/start-variant.ps1 -Variant win98 -Inspect
```

## Ältere Maschinen und Nachweise

Ältere private Maschinendefinitionen, Medienmanifeste und Nachweise enthalten
teilweise absolute Pfade des früheren Projektordners. Sie werden nicht
pauschal umgeschrieben: Das würde archivierte Identitäten und strikte
Snapshotbezüge verändern. Stattdessen besteht lokal ein Windows-Junction vom
früheren Projektpfad zum aktuellen Ordner. Dort liegt keine zweite Datenkopie.
Der Link erhält auch bestehende Launcher und alte Nachweiszeiger, ohne
persönliche Spiel-COWs oder Windows-Installationen zu öffnen oder zu verändern.

Der kleine Helfer prüft oder erstellt diesen Kompatibilitätslink:

```text
pwsh -NoProfile -File scripts/repair-workspace-links.ps1 -Inspect
pwsh -NoProfile -File scripts/repair-workspace-links.ps1
```

Standard ist der frühere Desktop-Unterordner des aktuellen Windows-Benutzers.
Für einen anderen früheren Ort kann `-PreviousRoot "vollständiger alter Pfad"`
angegeben werden. Ziel ist immer der Projektordner des Skripts. Bereits
belegte Verzeichnisse oder anders gerichtete Links werden abgewiesen; der
Helfer überschreibt oder löscht nichts. Verschachtelte Quell-/Zielordner sind
ebenfalls unzulässig. Bei einem weiteren Umzug muss ein bestehender Link auf
sein dann gültiges Ziel gesondert geprüft werden; der Helfer hängt ihn nicht
stillschweigend um.

## Nachweis

`python tests/test-workspace-relocation.py` führt die tatsächlichen CMD-
Inspektionspfade vor und nach dem Verschieben einer eigenen kleinen Fixture
aus. Die Prüfung umfasst einen fremden Arbeitsordner, relative Medienpfade,
fehlende Ressourcen, problematische Batchzeichen sowie belegte und
verschachtelte Linkziele. Dabei startet kein Emulator und kein Hostfenster.
Der aktuelle private Gesamtnachweis steht in
`artifacts/tests/workspace-relocation-release-path.txt`.
