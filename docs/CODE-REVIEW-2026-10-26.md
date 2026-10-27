# Code-, Lizenz- und Performanceprüfung vom 26. Oktober 2026

Der eingeschobene Benutzerauftrag ergänzt vier IDE-Plätze, leere Festplattendateien mit MiB/GiB-Größe und gespeicherte PC-Konfigurationen. Die Bedienung steht in [MACHINES.md](MACHINES.md). Parallel wurden sämtliche Projektquellen inventarisiert, Compiler-/Analyzerbefunde eingeordnet und die betroffenen Speicher-, Medien-, Ausgabe- und Besitzpfade geprüft. Das ist eine Prüfung mit konkreten Korrekturen, keine Garantie, dass jede mögliche Programmkombination fehlerfrei ist.

## Korrigierte Befunde

- Gespeicherte PCs behielten ihre Kennung beim Sitzungsstart zunächst nicht. Identität und Name bleiben jetzt über Factory, Sitzung, Speichern, Hardware-Kaltstart und erneutes Laden erhalten; Regressionen prüfen die vollständige Folge.
- Invariante CLI-Zahlen akzeptierten ein Komma als Gruppentrenner und machten aus `1,5` stillschweigend 15. Der Größenparser erlaubt jetzt ausdrücklich Dezimalpunkt und Vorzeichen ohne Gruppentrenner; zwei echte CLI-Regressionen prüfen 1,5 MiB mit Punkt und sichere Ablehnung des Kommas vor Dateierstellung. Der lokalisierte Windows-Editor bleibt davon unabhängig.
- Vorbereitete Medienwechsel lösen bei Fehlern neue/pending Leases und erhalten die bisherige Maschine. Unabhängige Overlays können zwischen IDE-Plätzen wechseln. Gemeinsame beschreibbare Journale, doppelte Slots, Journal-/Basis-Aliase und mehrdeutige JSON-Schlüssel werden vor Übernahme abgewiesen.
- Zwei ATA-Kanäle mit Master/Slave, IRQ14/15, dichter BIOS-Nummerierung, CHS-Übersetzung und EDD-Kapazitäten besitzen tatsächliche Geräte-/DOS-Nachweise. Die LBA28-Kapazität umfasst korrekt 268435456 Sektoren. ATAPI/LBA48 sind dadurch nicht implementiert.
- Lange Overlays hielten alle Sektornutzdaten im RAM. Der vollständige Index speichert jetzt Journaloffsets; ein begrenzter Cache hält höchstens 4096 Sektoren, also 2 MiB Nutzdaten pro Platte. Cachefehler lesen an expliziten Offsets und können den Appendcursor nicht verschieben. Ein 5001-Sektoren-Test kontrolliert Verdrängung, weitere Schreibvorgänge, Dateilänge und Wiederöffnung. Prüfsummen, Flush vor Sichtbarkeit und Behandlung eines unvollständigen letzten Records bleiben erhalten.
- Unveränderte Debugausgabe erzeugte bei jeder Abfrage neue große Strings. Ihr unveränderlicher Cache wird nur bei tatsächlicher E9-Ausgabe invalidiert. Der Sektorzähler eines geöffneten schreibgeschützten Images ist ebenfalls gecacht; Sektorlesen verwendet explizite Dateioffsets.
- Der PCM-Ring kopiert Spans statt jeden Wert mit Modulooperation. Frame-Reader-Leases benötigen keine eigene Callbackclosure; Publizieren übernimmt Caller-State ohne Frameclosure. Die Lease über den späteren GPU-Upload bleibt erhalten. WAV-Kodierung verwendet wiederverwendbare Puffer.
- ATA-IRQ-Änderungen werden ereignisgesteuert übertragen, anstatt IRQ14 nach jedem CPU-Schritt neu zu setzen. Geräte und Kanäle behalten getrennte Zustände und Quittierungen.
- Fehler beim GUI-Start geben geöffnete Medien wieder frei. Hintergrund-Erstellung ist abbrechbar und wird beim Schließen abgewartet. Die Datei wird atomar veröffentlicht und niemals über eine vorhandene Datei geschrieben. Async-Fehler sind auch bei geschlossenem Menü sichtbar; der gewählte IDE-Platz nennt sein Image/Journal.
- Der reproduzierte native WinMM-Fehler bei `waveOutClose` wird mit dem neuen Standardpfad **WASAPI shared** umgangen. Ausgabe-/Mute-/Pegel-/Stop-/Freigabetests bestehen. Es wird kein Mikrofon geöffnet und kein globaler Windows-Pegel verändert. Der alte WaveOut-Diagnosepfad besitzt weiterhin keine neue fehlerfreie Freigabeabnahme.
- Drei Shaderaufrufe verwendeten umgekehrte `smoothstep`-Grenzen. Die [GLSL-Referenz von Khronos](https://github.com/KhronosGroup/OpenGL-Refpages/blob/main/gl4/smoothstep.xml) erklärt dies als undefiniert. Die lokale SharpPixels-Kopie verwendet jetzt geordnete, invertierte Übergänge. Die C64-Dateien bleiben unverändert; alle GL-Prüfaufnahmen melden `NoError`.
- Der ausdrücklich aktivierte native Maustest protokolliert jetzt abgewiesene Aufräum-Eingaben/Zeigerwiederherstellung und tatsächliche Eingabezähler. Dieser Pfad wurde entsprechend dem Benutzerwunsch **nicht ausgeführt**. Seine verschobene Wiederholungsabnahme bleibt offen; gewöhnliche GUI-Tests senden keine Windows-Eingaben.

## Messung

Vorher-/Nachher-Binaries verwenden dieselben Benchmarkquellen und denselben .NET-Host. Reihenfolge AB/BA, insgesamt 14 Messungen je Variante, getrenntes Setup/Warmup und identische Ergebnisprüfsummen. Mediane für exakt vorgegebenen Hostaufwand:

| Messung | Vorher | Nachher | Allokation vorher → nachher |
| --- | ---: | ---: | ---: |
| 10000 unveränderte Debugtextabfragen | 6,674 ms | 0,028 ms | 328160000 → 16408 Byte |
| 30000 Ringzyklen mit 960 Komponenten | 69,784 ms | 11,032 ms | 0 → 0 Byte |
| 500000 Frame-Reader-Leases | 16,409 ms | 13,104 ms | 64000000 → 20000000 Byte |
| 500000 Kapazitätsabfragen | 2,682 ms | 0,129 ms | 0 → 0 Byte |
| 4000 WAV-Blöcke | 28,817 ms | 26,643 ms | 7776376 → 2320 Byte |

Der kleine WAV-Zeitunterschied ist keine allgemeine Leistungszusage; der große Befund sind die entfallenen Allokationen. Diese isolierten Faktoren sind **keine Doom-FPS-Faktoren**. Die acht Doom-Regressionsstarts prüfen die kurze stumme Demo, keine lange/interaktive Spielabnahme. Vier virtuelle Clocks je Interpreter-Schritt/REP-Element bleiben Entwicklungstiming; keine historische 486/33-Kalibrierung wurde geändert.

## Quellabdeckung und Lizenz

Die Prüfung umfasst 498 Code-/Projektdateien: C#, PowerShell, Python, ASM/INC, C++, GLSL, CMD und MSBuild/Solution. Eigene Quellen besitzen Apache-2.0-Header; bestehende vollständige Fremd-/SharpPixels-Hinweise bleiben erhalten. `REUSE.toml` annotiert ursprüngliche Profile und Fontdaten ohne Kommentarsyntax. Firmwareheader und Header generierter Assemblerdaten werden am Generator gepflegt.

Die bereits zugeordneten YMFM-Rausch-/Schlaginstrumentformeln behalten ausdrücklich ihren BSD-3-Clause-Anteil. Die drei betroffenen OPL-Dateien tragen beide Lizenzpflichten und Aaron Giles' Copyright; vollständige Apache-/BSD-Texte liegen unter `LICENSES/`. Die neuen Header lizenzieren fremde Formeln nicht stillschweigend um.

Kommentiert sind 1774 benannte C#-Methoden/Konstruktoren/Operatoren/lokale Funktionen/Properties, 406 Pythonfunktionen, 29 echte PowerShell-Funktionsdeklarationen, 348 erkannte aufrufbare ASM-Einstiege, 28 aktive Shaderfunktionen und sechs native Referenzadapterfunktionen. Anonyme Testfälle erläutern sich durch Testnamen und umgebende Funktionen. Kommentare erklären Zweck/Seiteneffekte und ersetzen keine Hardware-Vertragsdokumente.

Beim Einfügen der C#-Kommentare wurden ausführbare Roslyn-Tokens, bei Python ASTs ohne Dokumentationsstrings verglichen: unverändert. Gegen den Arbeitsbeginn besitzen 162 bestehende C#-Dateien ausschließlich Dokumentationsänderungen, 22 beabsichtigte ausführbare Änderungen und elf neu angelegte Dateien. Der CPU-/x87-Rechenkern wurde nicht beiläufig umgebaut. Header generierter INC-Dateien verändern keine Assemblerdaten. Der normale Build endet mit null Warnungen/Fehlern.

`AnalysisMode=All` wurde zusätzlich separat ohne pauschale Suppressionsliste ausgeführt. Erster vollständiger Lauf: 154 Hinweise; nach Korrektur der unbeachteten nativen Aufräum-Rückgabe: **153 Hinweise, null Fehler**. Die übrigen Hinweise betreffen überwiegend Lokalisierung, zusätzliche Null-/API-/Event-Konventionen, Exceptionkonstruktoren und konservativ erkannte Besitz-/Async-Muster. Medien werden beim erfolgreichen Übergang vom Controller/PC gehalten; Controls gehören der Form; die GUI gibt Ressourcen in `OnUnload` frei. Pipe-Arbeit beginnt auf dem Threadpool, Dispose verwendet bereits `ConfigureAwait(false)`. Befunde wurden eingeordnet, nicht als allgemeine Laufzeitfehler oder als warnungsfreier All-Analyzer-Lauf umetikettiert. Zusätzliche Suchpfadannotationen für feste Windows-System-DLLs bleiben eine getrennte Härtungsmöglichkeit.

Wiederholung: `scripts/test-source-quality.ps1`. Roslyn stammt aus dem installierten SDK; die Prüfung öffnet keine Gastmedien. `scripts/test-scripts.ps1 -Gui` prüft 76 Skripte mit 257 Checks, Pfade/Fehlerweitergabe, die echte Größen-CLI und einen internen GUI-Start. Vollständige Regressionen: `dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll`.

## Gemeinsame Nachweise und Grenzen

**4977/4977 Regressionen** und **100 Kaltstarts** auf den dokumentierten Test-DLLs: 16 Vierplattenstarts, 24 SB16, 16 OPL3, vier OPL2/Mischstarts, sechs FreeDOS-FDC, 13 originale EMM386-/VDS-/FORMAT-Starts sowie acht kurze Doom-, acht x87-Kontroll- und drei Konfigurationsstarts; zusätzlich zwei Vierplattenstarts nach dem reinen CLI-Parserfix. 1382 gezielte verfälschte Gastnachweise werden zurückgewiesen. Die abschließende Größenparserkorrektur betrifft ausschließlich den Image-Erstellungszweig des Headless-Starters und besitzt eigene CLI-Regressionen; die fünf ausgeführten VM-Bibliotheken bleiben bytegleich. Eigene GUI-Läufe prüfen PC-Speichern/Abklemmen/Laden, drei tatsächliche offscreen gerenderte Editoren, GL-Aufnahmen, Audio/Mute/Pegel und reguläres Schließen. Die Editorfenster aktivieren sich nicht und erhalten keine Windows-Eingaben.

Es gibt keinen neuen vollständigen RAM-/CPU-/FPU-/Gerätesnapshot, keine allgemeine DOS-/Windows-/Sound-/Eingabefreigabe und keine Fehlerfreiheitserklärung für M0–M8. Unveränderte lange x87-Zahlenmatrizen und historische Performancezeiten wurden nicht pauschal wiederholt. Zehn geschützte Eingaben, 53 private Ausgangsmedien, C64 und Git-HEAD bleiben erhalten; Benutzer-Spiel-COWs wurden nicht geöffnet/gehasht. Alle neuen Gastschreibvorgänge gehen in neue private Kopien/Overlays. Kein Commit/Push und keine Medienveröffentlichung.

Privater Gesamtnachweis: `artifacts/tests/storage-review-release-path.txt`. Nach diesem Benutzerpaket ist die begrenzte eigene VBE-Firmware für ET4000 wieder der nächste funktionale M3-Schritt. M3 und M4–M8 bleiben offen.
