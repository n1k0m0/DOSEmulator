# MiniGlide: Windows 95 und Motorhead, 3. November 2026

Die anschließende [Unreal-Messung unter beiden Windows-Versionen](UNREAL-MINIGLIDE-MEASUREMENTS.md)
verwendet denselben unveränderten Kandidaten. Sie ergänzt tatsächliche
Bildwechsel, ganze Bildänderungen und einen separaten Windows-Timerbefund
unter Kameradrehung. Der eng begrenzte folgende Motorhead-Nachweis bleibt
seinem eigenen Spiel- und Messabschnitt zugeordnet.

MiniGlide übersetzt die Glide-2-API zur Host-GPU. Die folgenden Ergebnisse
gehören zu diesem eigenen API-Pfad. Die ursprüngliche emulierte SST-1-Karte
und ihre [C58-Messungen](hardware/VOODOO-PERFORMANCE.md) behalten ihren
getrennten Nachweis. Bestehende Starter, Installationen und die gemeinsame
C54-Auswahl werden durch diesen lokalen Kandidaten nicht ersetzt.

## Tatsächliche Engpässe und Änderungen

Motorhead zeichnet seine Dreiecke mit drei getrennten Vertexzeigern.
Diese Aufrufe werden jetzt bei ihrem tatsächlichen Gastaufruf vollständig
kopiert und in der begrenzten Warteschlange gebündelt. Die Reihenfolge zu
Zustandswechseln, Texturtransfers, Abfragen und Pufferwechseln bleibt erhalten.
Ein später wiederverwendeter Vertexpuffer verändert kein bereits eingereihtes
Dreieck. Linien, vollständige Mipmapketten, partielle Palettentabellen und
die tatsächlichen Speichergrößenabfragen werden ebenfalls übersetzt.

Ein gültiger, von Windows noch nicht materialisierter Texturpuffer scheiterte
zuvor an der Host-Adressübersetzung. Die Gast-DLL liest die benötigten Bytes
jetzt mit normalen Gastbefehlen in einen begrenzten Transferpuffer. Damit
bearbeitet Windows seine eigenen Seitenfehler. Schreibziele werden mit
byteerhaltenden Gastzugriffen vorbereitet. Es gibt keine simulierte erfolgreiche
Adressübersetzung, keine ersetzten Seitentabellen und kein Page-Locking.

Der Host verwendet seine begrenzten Batch- und LFB-Puffer erneut. Derselbe
endgültige Prüfer benötigt für 64 vollständige LFB-Lese-/Schreibprüfungen
32.512 statt 25.840.256 verwaltete Allokationsbytes. Für 1000 maximale Batches
sind es 144.000 statt 16.530.560 Byte. Alle Nutzbytes und Callbackzahlen
werden geprüft; die unveränderte alte DLL scheitert jeweils an derselben
Allokationsgrenze.

Die eigene Renderer-Konfiguration setzt `ForceVerticalSync = false`, sofern
keine ausdrückliche Einstellung vorhanden ist. Im untersuchten Motorhead-Pfad
bremste erzwungenes Host-vSync die Aufrufverarbeitung und die sichtbare
Rennzeit. Die VSync-Einstellung des DOSE-Frontends bleibt eigenständig.
Die Implementierung verändert weder Gasttakte noch Gerätefristen.

## Motorhead unter Windows 98

Vier serielle Kaltstarts vergleichen dieselbe Laufzeit und denselben Renderer
in der Reihenfolge gebündelt / einzeln / einzeln / gebündelt. Nur die
Zeiger-Dreiecksbündelung ist im Gast über einen Compile-Schalter verschieden.
Je Lauf: normale .NET-Stufen, 20 Sekunden Rennaufwärmung und rund
35,9 Sekunden Messzeit; P55C/MMX 233 MHz, 128 MiB, ursprüngliches Spiel und
ursprünglicher SB16-Gasttreiber. Kein paralleler Benchmark und keine
Produkt-Kernbindung.

| Transport | Tatsächliche Pufferwechsel | Hostzeit | Pufferwechsel/s |
| --- | ---: | ---: | ---: |
| Einzelne Zeigerdreiecke, zwei Läufe | 2.614 | 71,8882 s | 36,36 |
| Gebündelte Zeigerdreiecke, zwei Läufe | 35.329 | 71,8559 s | 491,66 |

Die Einzelwerte liegen bei 33,52–39,21 beziehungsweise 455,53–527,81/s;
das Verhältnis der aggregierten Raten beträgt 13,52. Das sind tatsächliche
Glide-Pufferwechsel zur Host-Bibliothek, keine 492 Monitorbilder/s.
Das Frontend zeigt bis zu 60 Bilder/s. Die natürlich fortlaufenden
Attract-Rennen sind keine deterministisch identischen CPU-/Pixel-Replays.

Ein zusätzlicher endgültiger Kaltstart mit englischer Gastanzeige und
Gastfenster-Aktivierung liefert 19.387 Pufferwechsel in 35,9680145 Hostsekunden
bei 35,968 emulierten Sekunden. Die normalen Windows-Abfragen liefern
35,9496 Sekunden QPC und 35,955 Sekunden `GetTickCount`; ihr 20-ms-Beobachter
läuft innerhalb des Gasts. An beiden Messgrenzen gehört das Vordergrundfenster
zum Spiel. Der SB16 zählt 1.586.190 weitere Samples bei 44.100 Hz.
Die vollständigen Spielbilder ändern sich. Spielschluss und regulärer
Windows-Shutdown sind zusätzlich geprüft.

Das endgültige Artwork ist englisch. Nach einem erfolgreichen Glide-WinOpen
aktiviert der Helfer ausschließlich ein Fenster seines eigenen Gastprozesses.
Hosteingaben und Hostfokus werden dabei nicht verwendet.

## Neue Anzeige unter Windows 95

Der endgültige Build startet auf einer neuen Kopie des zuvor regulär
heruntergefahrenen eigenen Windows-95-Zweigs. P54C 100 MHz, 64 MiB,
Unreal 205 und dessen vorhandenes Grafikpreset bleiben erhalten. Das
512×384-Spielbild, die Statuszeile und das DOSE-Menü erscheinen gemeinsam
im echten Frontend-Backbuffer. Das DOSE-Menü hält die virtuelle Uhr an;
Fortsetzen und echte Gasttasten öffnen anschließend Unreals Menü.
Unreal beendet sich über sein eigenes Quit-Menü zum Windows-Desktop.
Danach erreicht Windows 95 seinen ursprünglichen Bildschirm zum sicheren
Ausschalten; das Frontend wird erst anschließend beendet.

Der getrennte 31,2446786-s-Szenenabschnitt liefert 31,248 emulierte Sekunden,
1386 tatsächliche Pufferwechsel, 759.341 Zeichenlistenaufrufe und
1.378.038 weitere SB16-Samples. Das entspricht 44,36 Pufferwechseln/s;
das Frontend meldet 60 Bilder/s. Szenen unterscheiden sich von den früheren
Unreal-Reihen, daher wird daraus kein zusätzlicher Codegewinn berechnet.

Dasselbe eigene Windows-Programm lädt eine gültige, zunächst unberührte
128-KiB-Textur aus `VirtualAlloc(MEM_COMMIT|MEM_RESERVE)`. Die alte Brücke
scheiterte nach dem echten BEFORE-Marker an `PageNotPresent`; die neue
erreicht den echten AFTER-Marker. Die Dateien werden nach Gastende aus dem
eigenen FAT-Overlay zurückgelesen. Ein erster zusätzlicher Start aus dem
älteren Quell-Overlay erreichte Unreals 3D-Initialisierung nicht; dieser
Diagnoselauf zählt weder als erfolgreiche Spielabnahme noch als Benchmark.

## Getrennte Testkopien

Die [neue Motorhead-Kopie](../artifacts/miniglide-win95-motorhead-20261103-8e0f3e9b/motorhead-test.dosebranch/Start-Motorhead-MiniGlide.cmd)
verwendet den eingefrorenen Kandidaten und eigene UI-Einstellungen. Im Gast
`C:\MOTOR.BAT` ausführen. Diese Kopie wurde mit dem neuen englischen Launcher
erneut kalt gestartet und bis zum echten Rennen geprüft. Beim Beenden zeigt
die Originaldemo ihren Informationsbildschirm; nach dessen Countdown bestätigt
Enter das Ende. Anschließend wurden der echte Windows-Desktop und reguläre
Shutdown geprüft. Die ursprünglichen `MOTOR.EXE`-/`SMACKW32.DLL`-Bytes und die
neu installierten eigenen Gastdateien wurden gezielt zurückgelesen.

Die [neue Windows-95-Kopie](../artifacts/miniglide-win95-motorhead-20261103-8e0f3e9b/win95-clean9.dosebranch/Start-Unreal-Win95-MiniGlide-Test.cmd)
startet denselben eingefrorenen Emulator. Im Gast `C:\MGLIDE.EXE` ausführen.
Beide neuen Starter bestehen ihre echte CMD-Pfadprüfung auch aus einem fremden
Arbeitsverzeichnis. Die bisherigen Starter und Installationen bleiben erhalten.

## Prüfgrenzen und Nachweise

Die ABI-Prüfer kontrollieren vollständige Vertex-/Textur-/Paletten-/LFB-Bytes,
Gastgrenzen, native Argumente, Warteschlangen und deren Leerung. Ein eigener
Prüfer führt die tatsächlich kompilierte x86-Gast-DLL unter WHPX aus und
vergleicht gebündelte und einzelne Dreiecke einschließlich Stackrückkehr und
wiederverwendeter Caller-Puffer.

Der eingefrorene Kandidat besteht 18.243 gemeinsame Regressionen,
76 native Gruppen einschließlich 438 Transportkontrollen und beider
Allokationsprüfer sowie drei Snapshot-Audits. Die tatsächlich kompilierte
Gast-DLL besteht vier Kontrollen je Transportmodus mit jeweils 346 vollständig
verglichenen Dreiecksdatensätzen. Quellprüfung: 1023 Dateien, davon 537 C#;
Zustandsgenerator unverändert 49 Typen, 992 Mitglieder und 44 Operationen.

Für automatisierte GUI-Prüfungen deaktiviert `--no-host-audio` ausdrücklich
den Hostendpunkt; Gast-DMA, IRQs, Synthese und RAM-Audiopuffer laufen weiter.
Die eigene Testausgabe prüft das tatsächliche OpenGL-Backbufferbild und
gesondert die emulierte VGA-Ausgabe. Eine native Maus-, Monitor-/GL- oder
Hostaudio-Abnahme wird daraus nicht abgeleitet. Aktive Host-Glide-Zustände
erhalten keine neue Snapshot-Unterstützung.

Dies belegt einen flüssigen, zeitlich passenden Motorhead-Abschnitt über
MiniGlide. Weitere Strecken, Spiele und Hardwareprofile sowie die vollständige
SST-1-/M7-Abnahme bleiben offen. Private Rohdaten, Bildnachweise und der
endgültige Prüfstand werden durch
`artifacts/tests/miniglide-win95-motorhead-release-path.txt` gebunden.
