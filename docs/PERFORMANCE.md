# Performance: RAM-Bus und Schiebebefehle

Stand: 25. Oktober 2026. Erstes gemessenes Optimierungspaket. Historische 486-Taktkalibrierung und vollständiges M3 bleiben offen.

## Änderungen

Der Speicherbus führt normale Bytezugriffe über einen kleinen Cache aus: Pro installierter 4-KiB-RAM-Seite vermerkt ein Flag, ob dort ein Gerät liegt. Bei 8 MiB RAM sind das 2 KiB Flagdaten. Jede erfolgreiche neue Zuordnung aktualisiert den Cache; teilweise belegte Seiten bleiben konservativ auf dem ursprünglichen Bytebus. Hohe ROM-Aliase benötigen keine 4-GiB-Seitentabelle. Das beschleunigt auch den Befehlsfetch und TLB-Seitentabellenzugriffe.

Wort-/Doppelwortzugriffe verwenden Little-Endian-Span-Zugriffe ausschließlich bei vollständig vorhandenem, zusammenhängendem RAM ohne Gerätemapping in den beteiligten Seiten. A20-Faltung, RAM-Ende, uint-Adressumlauf und MMIO/ROM bleiben bei geordneten Bytezugriffen. A20 maskiert weiterhin ausschließlich Bit 20. Geräte behalten ihre einzelnen Read-/Write-Aufrufe und deren Reihenfolge. Auch eine während eines Gerätezugriffs neu eingerichtete Zuordnung wird bei den folgenden Bytes berücksichtigt. Eine sehr kleine inlinefähige RAM-Methode trennt den schnellen Bytepfad von der Mapping-Suche; eine erste Variante mit schlechterer Byteleistung wurde verworfen.

ROL/ROR/SHL/SAL/SHR/SAR verwenden direkte Host-Bitoperationen. RCL/RCR verwenden einen 9-/17-/33-Bit-Ring einschließlich Carry. Das ersetzt bis zu 31 einzelne Schleifenrunden. Count-Maskierung, Nullzähler, CF/OF/SZP und der bisherige stabile Vertrag für undefinierte Bits bleiben erhalten. Bei Count null bleibt insbesondere die bisherige Operandprüfung mit Schreibabsicht erhalten. Checks, Datenzugriffe, Debugbeobachtungen und Write-before-flags-Reihenfolge bleiben im vorhandenen Decoder-/Fault-Pfad.

CPU-Zustandssicherung, REP-Elementgrenzen, Interruptzustellung, Scheduler, VGA-Puffer und Host-Pacing sind in diesem Paket nicht geändert. Jeder Step liefert weiterhin vier Entwicklungstakte. Die höhere Hostleistung verändert die virtuelle Zeit nicht.

## Messung

Referenzhost: AMD64 Family 26 Model 68, .NET 10.0.12, Windows x64. Es sind **feste Arbeitsmengen und Stopwatch-Messungen**, kein Samplingprofil und keine Aussage über den Anteil einer einzelnen Funktion an der gesamten Doom-Laufzeit. Ein unveränderter DLL-Satz vor der Optimierung dient als Referenz. Separate Varianten mit nur neuem RAM-Bus beziehungsweise nur neuem CPU-Shift isolieren beide Änderungen. Alle Prüfsummen stimmen überein.

Die endgültigen Mikrobenchmarks verwenden neun Messungen nach Warmup, jeweils 16 Millionen Busoperationen beziehungsweise zwei Millionen CPU-/Maschinenschritte. Mediane in Millisekunden; Tiered Compilation und ReadyToRun sind nur in diesen Benchmark-Kindprozessen deaktiviert, um JIT-Phasenwechsel innerhalb der Stichproben zu vermeiden. Der kombinierte Kandidat verwendet alle aktuellen Abhängigkeits-DLLs; die getrennten Varianten kombinieren gezielt archivierte und aktuelle DLLs mit derselben öffentlichen Schnittstelle. Frühere Messungen mit normalem Tiered-PGO bleiben private Zwischenstände und sind nicht die folgende Ergebnistabelle.

| Feste Arbeitsmenge | Vorher | Optimiert | Durchsatzfaktor |
| --- | ---: | ---: | ---: |
| RAM Byte lesen | 52,06 ms | 10,95 ms | 4,75× |
| RAM Byte schreiben | 53,55 ms | 11,38 ms | 4,71× |
| RAM Wort lesen | 99,36 ms | 33,25 ms | 2,99× |
| RAM Wort schreiben | 102,22 ms | 34,71 ms | 2,95× |
| RAM Doppelwort lesen | 197,64 ms | 35,22 ms | 5,61× |
| RAM Doppelwort schreiben | 194,91 ms | 44,86 ms | 4,34× |
| PE32-Schiebeschleife, CL=31 | 224,20 ms | 177,71 ms | 1,26× |
| PE32-Speicherschleife | 222,15 ms | 191,76 ms | 1,16× |
| Gleiche Speicherschleife über PcMachine | 245,42 ms | 215,70 ms | 1,14× |

Diese CPU-Zeilen enthalten die kombinierte RAM-/Shift-Optimierung. Die getrennten Varianten und sämtliche Einzelmessungen stehen im privaten Nachweis. Prozess-/Hostschwankungen verhindern eine allgemeine Leistungszusage; die Mikrobenchmarks isolieren synthetische Arbeitsmengen.

### Tatsächlicher DOS-/Doom-Vergleich

Die eigene kurze, stumme 32-Tick-MAP01-Demo läuft mit dem originalen DOS4GW/Doom II je zweimal pro Build ohne Speichermanager und unter originalem EMM386/RAM: **acht Kaltstarts**, abwechselnd vorher/nachher beziehungsweise nachher/vorher. Beide Programme laufen dabei mit normaler .NET-Konfiguration. Jeder Lauf verwendet eine neue private Overlaykopie. Gemessen wird die ganze Headless-Prozesslaufzeit einschließlich Start und Grafikaufnahmen; nachgelagerte Oracles und das Kopieren der Medien liegen außerhalb des Zeitfensters.

| Konfiguration | Vorher, Median | Nachher, Median | Weniger Hostzeit |
| --- | ---: | ---: | ---: |
| MS-DOS ohne Manager | 18,909 s | 16,609 s | 12,16 % |
| MS-DOS mit EMM386/RAM | 30,328 s | 27,701 s | 8,66 % |

**Der vollständige gemeldete CPU-Zustand einschließlich Zählern, die Bild-/Szenenfolgen und die virtuelle Endzeit sind zwischen allen Vorher-/Nachher-Wiederholungen je Konfiguration identisch.** Die WAD-abgeleitete unabhängige Prüfung bestätigt die Szenen und DOS-Rückkehr. Das ist eine begrenzte Extender-/Grafikprobe, keine allgemeine Spiel-, Sound- oder Eingabeabnahme. Zwei Wiederholungen je Variante sind ein gezielter lokaler Vergleich, kein statistischer Test über verschiedene Rechner oder lange Spielsitzungen.

## Verifikation und Reproduktion

**2.325/2.325 Regressionen, 0 Buildwarnungen/-fehler.** 64 neue Testgruppen umfassen **546.816 Ergebnis-/Flag-Vergleiche** des tatsächlichen Shift-Decoders gegen einen getrennten bitweisen Kompatibilitätsalgorithmus, alle 256 Rohzähler, acht Operationen, drei Breiten, Register-/Speicherformen und CL-Zielregisteraliasing. Fünf weitere tatsächliche PF-Fälle prüfen Schreibrechte einschließlich Nullzähler. Der unabhängige Bytebus-Vergleich prüft A20 an/aus, ungerade/seitenteilende Zugriffe, partielle Gerätemappings, spätes Mapping, RAM-Ende mit unvollständiger letzter Seite, hohe Aliase, uint-Umlauf und MMIO-Aufrufreihenfolgen. Diese Tests behaupten keine allgemeine Bus-/MMIO- oder Steppingpräzision über den bisherigen Modellvertrag hinaus.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
dotnet build .\tests\DOSEmulator.Benchmarks\DOSEmulator.Benchmarks.csproj -c Release -p:DosAssemblyRoot=<absoluter-DLL-Ordner> -o <privater-Benchmarkordner>
dotnet <privater-Benchmarkordner>\DOSEmulator.Benchmarks.dll 2 9
```

Für die dokumentierte stabile Mikrobenchmark-Konfiguration die beiden Benchmark-Umgebungsvariablen ausschließlich für die gestarteten Kindprozesse setzen. Das Vergleichsskript `scripts/benchmark-dos-extender.ps1` nimmt `-BaselineAssemblies`, `-CandidateAssemblies`, eine vorbereitete private `-Disk`, `-RamOverlay`, `-Scenario` und den lokalen `-Game`-Ordner entgegen. Es erzeugt eigene private COWs, prüft Hashes und verwirft Läufe mit verändertem CPU-Zustand, Bildfolgen oder virtueller Zeit. Originale und Benutzer-Spielkopien werden nicht verändert.

Aktuelle Gastregressionen, konkrete Artefaktpfade und gemeinsame DLL-/Quellhashes stehen im [Implementierungsstatus](IMPLEMENTATION-STATUS.md).

## Bewertung der übrigen genannten Kandidaten

| Kandidat | Befund und nächster sinnvoller Schritt |
| --- | --- |
| Protected-Mode-Checkpoints | Tatsächlicher Kopieraufwand für GPR und Kontrollzustand; Register liegen im Stack, nicht in acht neuen Heapobjekten. Selektive Sicherungen benötigen eine Klassifikation aller Seiteneffekte, Fetch-/Datenfaults, TF/SS und bereits übernommenen Hardware-Tasks. Zuerst getrennt profilieren, danach lazy Sicherungen mit derselben Fault-/Retry-Matrix vergleichen. |
| REP MOVS/STOS | Ein Interpreterdurchlauf je Element ist vorhanden und bewahrt die derzeit geprüften Interrupt-/Debug-/Restart-Grenzen. Ein schneller Pfad benötigt begrenzte RAM-/Seitenspannen, DF/Überlappung, A20, Debug-/Fault-Abbruch und ein Budget bis zum nächsten Geräteereignis. Erst als eigener nachgewiesener Schritt bündeln. |
| IRQ/Scheduler | Wiederholtes IRQ14-Setzen und PIC-Kaskadensuchen sowie Clock-Division/Event-Queue je Step sind vorhanden. Pending-Caches müssen auch ICW/EOI/Masken, Flanken, Reset und IRQ13 berücksichtigen. Ereignisbudgets dürfen keine CPU-/Gerätegrenzen verschieben. Dieser Anteil ist hier nicht separat gemessen. |
| Scanout/Host-Pacing | Capture rastert und alloziert jedes Bild. Das veröffentlichte Frame wird über Volatile an den UI-Thread übergeben; Wiederverwendung braucht deshalb Besitz-/Lebensdauerregeln. Der Pacer schläft bei bis zu 20 ms Rückstand derzeit 16 ms. Pooling/Dirty-Scanout und feinere Warteplanung getrennt prüfen, einschließlich Blink/Cursor/Palette/Moduswechsel und Audio. |

Die pauschalen vier Takte pro Step sind weiterhin **kein historisch kalibriertes 486/33-Profil**. Für eine solche Kalibrierung sind Befehlsgemisch, Speicher-/I/O-Zeiten, Geräteereignisse und echte Referenzprogramme gemeinsam zu betrachten. Schnellere Host-Ausführung behebt diese Modellgrenze nicht.

Primärquelle für Shift-/Rotate-Regeln und Zählermasken: Intel, **Intel486 Programmer’s Reference Manual (1992)**, RCL/RCR/ROL/ROR sowie SAL/SAR/SHL/SHR in Kapitel 26. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143). Undefinierte Resultate/Flags sind ausdrücklich nur als bisheriger stabiler Modellvertrag geprüft.
