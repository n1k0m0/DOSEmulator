# Lokale Starter und VM-Dateien

Die rechnerbezogenen CMD-Starter liegen seit dem 5. November 2026 unter
`local/launchers/`. Dieser gesamte Ordner ist in Git ignoriert. Auch neu
angelegte CMD-/BAT-Dateien im Projektstamm werden nicht versehentlich
eingecheckt. Versionierte Build- und Startlogik bleibt unter `scripts/`.

Die vorhandenen Starter behalten ihre bisherigen Laufzeiten, Maschinen und
Einstellungen. Ihre relativen Projektpfade wurden an die neue Position
angepasst. Maschinenimages, COW-Dateien und laufende VMs werden dabei nicht
verschoben, zusammengeführt oder auf einen anderen Emulatorbuild umgestellt.
Die lokalen Starter gehören deshalb nicht zum Lieferumfang eines neuen
Git-Checkouts.

## Starten

Lokal zum Beispiel:

```powershell
.\local\launchers\run-win98.cmd -Inspect
.\local\launchers\Start-GLQuake-Win98.cmd --inspect
.\local\launchers\Start-Unreal-Win98-Glide-Host.cmd --inspect
```

Ohne private CMD-Starter bleiben die versionierten Einstiegspunkte direkt
nutzbar. Der normale Start kann eine VM öffnen; `-Inspect` prüft nur die
ausgewählten Pfade:

```powershell
.\scripts\run.ps1
.\scripts\run-freedos.ps1 -Inspect
.\scripts\start-variant.ps1 -Variant win98 -Inspect
python .\scripts\build-guest-installers.py --help
```

Die freie MiniGlide-Einrichtung besteht weiterhin aus
`scripts/glide-bridge/build-guest.cmd` und
`scripts/glide-bridge/setup.py`. Der lokale Komfortaufruf
`local/launchers/Setup-Glide-Bridge.cmd` enthält die Zuordnung der vorhandenen
privaten Unreal-Maschinen.

## Wo die Maschinen liegen

Die aktuelle rechnerbezogene Übersicht steht in **`local/VM-LOCATIONS.md`**.
Sie ordnet die geprüften Starter ihren tatsächlich ausgewählten
Maschinendefinitionen zu; es wird nicht der gesamte private Medienbestand
durchsucht. Die Standardzuordnung steht in `artifacts/current/current.json`,
separate Spielstarter können eigene festgelegte Laufzeiten und PCs verwenden.

Eine VM besteht typischerweise aus mehreren zusammengehörenden Dateien:

| Datei | Inhalt |
|---|---|
| `branch.dospc.json` beziehungsweise `*.dospc.json` | Hardware, Medienpfade und Zuordnung der beschreibbaren Overlays |
| `*.img` | Unverändertes Basisimage einer Platte oder Diskette; es kann außerhalb des VM-Ordners liegen |
| `*.cow` | Änderungen der jeweiligen VM, einschließlich installierter Programme und Spielstände |
| `frontend-data/` beziehungsweise der ausgewählte UI-Ordner | Lokale Anzeige-, Audio- und Bedienungseinstellungen |
| `*.whpxsav/` oder `*.dossav` | Gesonderter vollständiger Speicherstand, falls ausdrücklich angelegt |

Zum Sichern einer VM werden die Definition, alle referenzierten Basisimages
und sämtliche zugeordneten COW-Dateien benötigt. Ein einzelnes Basisimage
enthält die späteren Änderungen im Overlay nicht. Aktive VMs zuerst regulär
herunterfahren; eine laufende COW-Datei nicht beiläufig kopieren oder
zusammenführen. Originalmedien und persönliche Spielstände bleiben privat.

## Repository-Prüfungen

`scripts/test-scripts.ps1` erzeugt seine kleinen CMD-Teststarter selbst in
einem frischen privaten Testordner. Die Prüfung von PowerShell-Auswahl,
Argumenten und Fehlerweitergabe funktioniert dadurch auch in einem
Git-Checkout ohne `local/`. Ein nativer GUI-Smoke bleibt ausdrücklich an
`-Gui` gebunden.
