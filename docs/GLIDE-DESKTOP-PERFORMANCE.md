# MiniGlide-Starter: flüssiger Windows-Desktop

Stand: 3. November 2026. Der Bericht zu 5–7 FPS bei
`Start-Unreal-Win98-Glide-Host.cmd` betrifft tatsächliches Ruckeln von Maus
und Fenstern. Der Fix liegt in der GUI-Fenstersuche.

## Ursache

Ohne aktives Spiel findet die GUI kein MiniGlide-Ausgabefenster und wiederholt
die Suche bei jedem Bild. Bisher wurde für jedes eigene Top-Level-Fenster erst
`GetWindowTextW` und danach dessen Klasse abgefragt. Die Titelabfrage kann dem
besitzenden Thread synchron `WM_GETTEXT` schicken und auf dessen Antwort warten.
Das ist auch für Fenster derselben Anwendung relevant; Microsoft beschreibt
diese [GetWindowTextW-Semantik](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowtextw).

Eine eigene Windows-98-Referenzkopie reproduziert mit der tatsächlich vom
Starter verwendeten alten Laufzeit 119 GUI-Bilder in 12,1296 Hostsekunden:
9,8107 FPS und Bildabstände über 200 ms. Das passive .NET-Sampling zeigt den
GUI-Thread fast durchgehend im Callback von `FindOwnGlideWindow`. Die
Samplinganteile über alle Threads sind keine exakte CPU-Zyklenrechnung.
Die gesonderte Gate-Wartezeit einer Statusabfrage ist ebenfalls kein direktes
Maß für die normale Eingabelatenz.

## Änderung und Nachweis

Die Suche prüft weiterhin zuerst den Prozessbesitzer. Danach schließt sie
unpassende Fensterklassen aus, bevor sie einen Fenstertitel liest. Nur die
bisher zugelassene Klasse `Static` wird auf den Namen `MiniGlide` geprüft.
Die Kriterien für das tatsächliche Rendererfenster bleiben gleich.

Der gleiche endgültige Prüfer benutzt eigene unsichtbare Win32-Fenster:
Ein nicht zum Renderer gehörendes Hilfsfenster zählt wirkliche
`WM_GETTEXT`-Nachrichten, während gültige Klasse/Titel und geschlossenes
Rendererfenster getrennt kontrolliert werden. Die alte unveränderte GUI
scheitert mit einer unerwünschten Titelabfrage und Exit 1. Beide neuen
GUI-Pakete bestehen alle sieben Kontrollen ohne Titelabfrage an dieses
Hilfsfenster. Es werden keine DosWindow-Instanzen, Hosteingaben oder
Audioendpunkte für diesen Komponentenprüfer erzeugt.

| Tatsächliche eigene Win98-Prüfung | GUI-Bilder/s |
| --- | ---: |
| Alte Starterlaufzeit, ruhiger Desktop | 9,81 |
| GUI-Fix auf denselben sechs alten VM-Modulen | 60,18 |
| GUI-Fix auf der aktuellen Zeitfix-Laufzeit | 58,98 |
| Aktuelle Laufzeit, Desktop nach natürlichem Spielende | 58,88 |

Die ersten beiden Werte zählen echte FrameReady-Ereignisse in eigenen
Zeitfenstern; die aktuellen Werte zählen die vollständigen Statusgrenzen
einschließlich deren Diagnoseabfragen. Auf dem ruhigen Desktop zeigt die
aktuelle Fassung überwiegend 60 FPS. Das ist keine erfundene FPS-Anzeige und
keine isolierte Instruktionsbeschleunigungsquote. Die virtuelle Tastatureingabe
in den originalen Windows-Dialog sowie beide regulären Shutdowns bestehen.
Die serielle direkte Tastaturinjektion des privaten Harness hält teilweise
selbst das Owner-Gate; daraus wird keine native Mauslatenz abgeleitet.

Für die alte Diagnose-GUI wurde ausschließlich im privaten Kontrollharness
der Host-Audioinitialisierer auf einen leeren Provider begrenzt, da dieser
alte Konstruktor noch keinen Abschalter besitzt. Die originalen Dateien
bleiben archiviert und unverändert; die bereitgestellte lokale GUI enthält
diese Binäranpassung nicht. Beide Vergleichsseiten haben keine tatsächlichen
Audio-Callbacks oder Endpunkte. Gast-Soundhardware und Hostausgabe bleiben
getrennte Verträge.

Die aktuelle Zeitfix-Fassung mit der eigenen Gast-DLL4 erreicht in drei
zusätzlichen tatsächlichen Unreal-Kameradrehungen 57,55–57,92 native
Pufferwechsel/s und 100,000 / 99,985 / 100,000 Prozent Windows-QPC-/Hostzeit.
Zwei reale Gast-Portzugriffe begrenzen wie im
[Zeitgebernachweis](WINDOWS-TIMER-PROGRESS.md) die ursprüngliche API-Abfrage.
Vor und nach dem Spiel bleiben die Desktop-Anzeige und Zeitfortschreibung
flüssig. Natürliches Spielende und regulärer Windows-Shutdown bestehen.
Das ist kein universeller 100-Prozent- oder nativer Eingabe-/Monitor-Nachweis.

## Bereitstellung

Der genannte Root-Starter ruft weiterhin seine bisherige Windows-Maschine auf.
Nur der Frontendpfad des privaten untergeordneten `Start-Glide.cmd` zeigt nun
auf `artifacts/desktop-fps-20261103-5be26247/legacy-fixed-product`.
Von den 61 bisherigen Laufzeitdateien ändern sich dort ausschließlich
`DOSEmulator.dll` und `DOSEmulator.pdb`. Alle sechs tatsächlichen VM-Module
bleiben bytegleich zur bisherigen Laufzeit; es gibt keine Änderung ihrer
Snapshotidentität oder Migration. Der zweite geprüfte GUI-Build bindet
unverändert die sechs Module des eingefrorenen aktuellen Zeitfix-Kandidaten.

Die alte Laufzeit bleibt erhalten. Maschinendefinition, UI-Einstellungen,
Root-Starter und sämtliche COW-/Platteninhalte werden durch die Bereitstellung
nicht geändert. Für die Tests werden nur die bekannten eigenen Referenzen in
frische eigene COWs kopiert; die Benutzer-COW wird weder geöffnet noch gehasht.
Der wirkliche Root-`--inspect`-Aufruf besteht ohne Gaststart. Ein bereits
laufender Prozess verwendet seine geladenen Module weiter; die neue GUI
gilt beim nächsten Start.

Beide vorhandenen kompilierten GUI-Owner-/Menü-/Snapshot-Selbstprüfungen
bestehen ohne Fensterkonstruktor oder Endpunkte. Die Quellprüfung bleibt
bei 1026 Code-/Projektdateien (540 C#) fehlerfrei. Die unveränderten aktuellen
VM-Module behalten ihre bereits bestandenen 18.243 gemeinsamen Regressionen
und 79 nativen Gruppen; diese Suiten werden hier nicht als erneut ausgeführt
ausgegeben. Die alte Starterlaufzeit behält ihren eigenen Buildbezug.

Der vorherige Zeitgeberstand ist als `82d7d00` committed und auf `origin/main`
geprüft. Dieser anschließende GUI-Fix bleibt lokal. Automation bleibt pausiert.
Privater Gesamtnachweis: `artifacts/tests/desktop-fps-release-path.txt`.
