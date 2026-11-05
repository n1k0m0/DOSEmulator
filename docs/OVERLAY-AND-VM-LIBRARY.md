# Gemeinsames Overlay und VM-Bibliothek

Stand: 4. November 2026. Die VM-Bibliothek und der folgende Assistent sind ein
konkreter Entwurf, noch keine implementierten Menüfunktionen. Der getrennte
lokale Umsetzungsschritt verbessert zunächst die gemeinsame Glide-Anzeige.

## Bedienmodell

Eine feste linke Navigation ersetzt die langen Listen aus Hardwarewerten,
Medienaktionen und Speichervorgängen. Rechts stehen die Werte der gewählten
Kategorie mit einer kurzen Erklärung und konkreten Auswahllisten. Die Oberfläche
bleibt englisch. Oben stehen VM-Name, Betriebszustand und aktive Anzeige.

| Bereich | Englische Einträge | Zweck |
| --- | --- | --- |
| Virtual machines | New VM, Open VM, Recent VMs | Maschinen anlegen und auswählen |
| Hardware | System, Graphics, Sound, Network, Other devices | Zusammengehörige Komponenten bearbeiten |
| Storage | Hard disks, CD-ROM, Floppy drives | Images und private Schreiboverlays zuordnen |
| Display & audio | Scaling, Filter, VSync, Volume | Hostdarstellung live einstellen |
| Saved states | Save state, Load state, Media checkpoint | Laufenden Zustand und Medienkopie unterscheiden |
| Power | Resume, Restart, Power off | Explizite Aktionen auf die ausgewählte VM |

Beispiel für die feste Struktur:

```text
DOSE                    Windows 98 - Games             PAUSED
Virtual machines        Hardware / System
Hardware                Platform     [Pentium MMX PCI       v]
  System                CPU clock    [233 MHz               v]
  Graphics              Memory       [128 MiB               v]
  Sound                 CPU engine   [WHPX                  v]
  Network
Storage                 2 pending changes - restart required
Display & audio         [Discard]       [Apply and restart]
Saved states
Power
```

`System` zeigt Platine, CPU, zulässigen Takt, RAM und CPU-Backend. `Graphics`
trennt die primäre 2D-Karte, die zusätzliche Voodoo-Karte und die
MiniGlide-Hostbeschleunigung. Monitorauflösungen bleiben Einstellungen des
Gastbetriebssystems. Für ein fest zur Platine gehörendes Gerät steht ein
sichtbarer Grund direkt daneben, statt einen scheinbar wirkungslosen Klick
anzubieten. Soundkarte und Port/IRQ/DMA gehören auf dieselbe Seite.

Jede Netzwerkkarte erhält ihre eigene Karte mit Modell, NAT/Host-only/Bridged,
MAC und Verbindungszustand. Nur für Bridged erscheint die Hostadapterauswahl;
nur für Host-only der LAN-Name. Eine Anzeige des tatsächlichen Backendfehlers
bleibt neben der Verbindung sichtbar.

Hardwareänderungen bleiben ein gemeinsamer Entwurf. `Apply and restart`
validiert die gesamte Ressourcenkombination und startet erst dann kalt neu;
`Discard` verwirft nur die Änderungen. Die vorhandene Owner-Revision und
Fehlerbehandlung bleiben erhalten. Lautstärke, Filter und Skalierung tragen
stattdessen die Kennzeichnung `Applies immediately`.

## Neue VM in fünf Schritten

1. **Name and location:** einen neuen VM-Ordner mit eindeutiger Identität wählen.
2. **Hardware preset:** DOS / Windows 95 / Windows 98; die tatsächlich
   unterstützten Hardwaretupel anbieten. Das Preset installiert noch kein OS.
3. **Storage:** neue leere Platte mit Kapazitätsauswahl, oder ausdrücklich
   gewähltes bestehendes Basisimage mit eigenem neuen Schreiboverlay.
4. **Installation media:** optionale ISO und Bootdiskette auswählen,
   Bootreihenfolge und benötigte Gasttreiber-/Tools-Medien anzeigen.
5. **Review and create:** Name, Platine, CPU, RAM, Backend, Datenträger und
   Netzwerk zusammenfassen; anschließend `Create` oder `Create and start`.

Das Erzeugen ist abbrechbar und schreibt zunächst in einen neuen temporären
VM-Ordner. Erst ein vollständiger Erfolg trägt die Maschine in die Bibliothek
ein. Vorhandene Ziele werden nicht überschrieben. Ein neuer Computer verweist
niemals auf das aktive Schreibjournal eines anderen Computers.

Der Ordner verwendet die vorhandenen Verträge statt eines zweiten VM-Formats:

```text
My Windows 98/
  machine.dospc.json      Hardware, stabile Identität und Medienzuordnungen
  ui.json                Hostdarstellung und gewünschtes CPU-Backend dieser VM
  disks/
    system.img           geschützte Basis
    system.cow           eigener beschreibbarer Stand
  snapshots/             vollständige Zustände mit tatsächlicher Buildbindung
```

Die Bibliothek speichert ausdrücklich angelegte oder geöffnete PC-Pfade und
deren Anzeigenamen. Sie durchsucht keine bestehenden Medien-/Spielordner.
`Open VM` öffnet eine vorhandene Definition; `Duplicate VM` erzeugt über den
Mediencheckpointpfad einen unabhängigen neuen Schreibzweig. Das Kopieren allein
einer `.dospc.json` mit demselben `.cow` ist keine unabhängige VM.

## Speichern eindeutig benennen

| Aktion | Was sie speichert oder lädt | Startverhalten |
| --- | --- | --- |
| Save configuration | Name, Hardware und Medienzuordnungen | Nächster Kaltstart |
| Open VM | Vorhandene Konfiguration | Kaltstart nach ausdrücklicher Auswahl |
| Save state / Load state | CPU, RAM, Geräte, Scheduler und gebundene Medien | Vollständige Fortsetzung auf passendem Build |
| Media checkpoint | Datenträgerstand und Bindungen | Unabhängiger Kaltstartzweig |

Bei aktivem externem MiniGlide-Renderer bleibt ein vollständiger Zustand
weiterhin nicht speicherbar. Das Overlay soll `Save state` in diesem Fall mit
`Close the MiniGlide game before saving state` deaktiviert erklären. Das Menü
selbst, Hardwareansicht und Darstellungsoptionen bleiben erreichbar. Ein
fehlgeschlagener Speicherversuch darf keinen CPU-Abbruch verursachen.

## Ein sichtbares Fenster für VGA und Glide

Die Gastanzeige und das Overlay behalten denselben DOSE-Viewport. Ein
Glide-Aufruf schaltet ausschließlich dessen Bildquelle um. Beim Schließen
kehrt er zur aktuellen VGA-/S3-Ausgabe zurück. Menü, Fehlermeldungen,
Skalierung und Eingabezuordnung werden über denselben UI-Pfad gezeichnet.

Der lokale Implementierungsschritt ersetzt die heuristische Fenstersuche
durch eine explizit veröffentlichte aktive Bildfläche. Erst ein erfolgreiches
`grSstWinOpen` veröffentlicht sie; Close, Shutdown und Fehler ziehen sie zurück.
Jedes erneute Open erhält eine neue Generation, auch bei gleichem HWND.
Ein alter Close kann keine neuere Bildfläche löschen.

Die Hostbibliothek benötigt weiterhin eine technische HWND-Bildfläche. Sie
wird von Beginn an außerhalb des sichtbaren Desktops und mit
`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` angelegt. Die normale Hostdarstellung
erscheint ausschließlich im DOSE-Fenster. Eigene Fensterprozeduren schützen
Position und die erweiterten Stile auch während der Providerinitialisierung.
Eine einmal geöffnete Menüseite benutzt das letzte Glide-Bild als ruhigen
Hintergrund, statt für jedes Menübild den Provider synchron abzufragen.

Das ist weiterhin eine Bildkopie in den gemeinsamen GPU-Pfad, noch kein
gemeinsam genutztes Direct3D-/OpenGL-Texturobjekt. Direkte GPU-Texturübergabe
bleibt ein getrennter späterer Optimierungsschritt, der eine geeignete
Schnittstelle der Hostbibliothek benötigt. Ein vorhandenes fremdes
Glide-Backend wird dadurch nicht pauschal als kompatibel erklärt.

Primärregeln: Microsoft dokumentiert die
[erweiterten Fensterstile](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles),
änderbare [Fensterpositionen](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-windowposchanging)
und [Styleänderungen](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-stylechanging).

## Geprüfter lokaler Anzeigeschritt

Zwei neue eigene Kaltstarts unter Windows 95 und Windows 98 erreichen jeweils
Desktop, echtes Unreal/Glide und wieder den Desktop. Hauptmenü, Videooptionen,
Hardwareansicht und gespeicherte Maschinen werden durch den gemeinsamen
Overlaypfad geöffnet. Tatsächliche logische Menütasten wechseln die Seiten.
Bei geöffnetem Menü bleiben virtuelle Zeit und Glide-Hintergrund unverändert,
während die GUI weiter Frames zeichnet. 50.220 Pixel aus sechs wirklichen
GL-Ausgaben entsprechen unabhängig dem vollständigen Titelraster der eigenen
5×7-Schrift. Beide Gäste beenden das Spiel und Windows regulär.

Die begrenzten Anzeigeintervalle liefern unter Win95 57,72 / 58,99 / 57,92
GUI-Bilder/s für Desktop / laufendes Spiel / zurückgekehrten Desktop; unter
Win98 58,30 / 58,99 / 57,72. Die Statusabfragen sind in diesen Zeitfenstern
enthalten. Das ist eine Prüfung der gemeinsamen Anzeige, kein ABBA-Nachweis
einer neuen allgemeinen Spielbeschleunigung oder universellen 1:1-Ausführung.

80 native Prüfgruppen bestehen, einschließlich 18 neuer tatsächlicher
Win32-Fenster-/Generations-/Fehler-/Lebensdauerkontrollen. Der neue
`SWP_NOSENDCHANGING`-Fall und eine Nachsicherung nach der Providerbehandlung
halten auch die im ersten Win95-Versuch beobachtete Rückverschiebung auf
`0,0` fern. Ein zunächst vollständig ausgeblendetes HWND war für diesen
Provider nicht renderfähig; die endgültige technische Fläche trägt deshalb
`WS_VISIBLE`, bleibt jedoch außerhalb des Desktops und ohne Taskleistenfenster.
Die drei vorherigen Gastversuche sind getrennte Vorläufe, keine endgültige
Abnahme dieses Kandidaten.

Der tatsächliche GUI-Ownercheck mit Pause/Fortsetzen, vollständigem Snapshot,
Reset und Enginewechsel besteht ebenfalls. Der Zustandsvertrag bleibt
49 Typen / 992 geprüfte Member / 44 Callbackkörper. 1030 Code-/Projektdateien,
darunter 542 C#, sind qualitätsgeprüft. Beide Produktbuilds melden 0 Warnungen
und Fehler. Die fünf gemeinsamen VM-Module bleiben bytegleich; unter den
sechs Modulen ändert sich ausschließlich WHPX. Seine Snapshot-Buildidentität
ändert sich entsprechend, ohne Formatmigration. Der bisherige gemeinsame
18.243er-Testnachweis wird nicht als erneut ausgeführt ausgegeben.

Zum eigenen Ausprobieren gibt es getrennte, aus sauberen Shutdowns stammende
Testmaschinen unter `local/launchers/` mit `Start-Unreal-Win95-Unified-Overlay-Test.cmd` und
`Start-Unreal-Win98-Unified-Overlay-Test.cmd`. Die bisherigen Starter und
Benutzerinstallationen bleiben unverändert. Es wurden keine physischen
Hosteingaben, Fokusversuche oder Host-Audio-/Netzendpunkte für diese Prüfungen
aktiviert. Die native Maus-/Vollbild-/Monitorabnahme bleibt separat.

Privater Gesamtnachweis: `artifacts/tests/unified-overlay-release-path.txt`.

## Reihenfolge der weiteren Umsetzung

1. Gemeinsame Glide-Bildfläche, Start/Close und echtes Overlay mit frischen
   Win95-/Win98-Gästen prüfen; bestehende Benutzerinstallationen erhalten.
2. Dieselben bestehenden Hardwareaktionen in die oben gezeigten Kategorien
   und direkte Auswahllisten überführen. Änderungen und Neustartbedarf bleiben
   in der festen Fußleiste sichtbar.
3. VM-Bibliothek und den Assistenten auf den vorhandenen Speicher-/Checkpoint-
   Verträgen implementieren, einschließlich Abbruch und unabhängiger Overlays.

Der Auftrag zum vorherigen Commit/Push ist als `07a6d1c` abgeschlossen. Neue
Entwurfs- und Implementierungsarbeit bleibt lokal; die Automation bleibt pausiert.
