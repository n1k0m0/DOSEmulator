# Unreal unter Windows 98 mit Glide

## Installation

Die ursprüngliche Unreal-Windows-ISO liegt privat unter `Disks and CDs (DO NOT PUT INTO GITHUB)/unreal.iso` (435.417.088 Byte; SHA-1 `5ACD102E14C70950BB305384D5D0CBBE305D2D9E`). Quelle: <https://archive.org/details/unreal-windows-iso>. Die offizielle Version 226Final wurde aus <https://www.gamers.org/pub/unreal/official/unrealpatch226final.exe> installiert.

Die separate, beschreibbare Windows-98-Maschine liegt unter `artifacts/unreal-win98-20261102-1e0df488/win98-unreal.dosebranch/branch.dospc.json`. Sie verwendet einen P55C/MMX mit 233 MHz, 128 MiB RAM, S3 Trio64, Voodoo Graphics SST-1, Sound Blaster 16 und WHPX. Die Originalinstallation und andere Spielstände des Projekts wurden nicht verändert. Die CD im Gast enthält die unveränderten Dateien der Original-ISO und zusätzlich den Patch.

`Start-Unreal-Win98.cmd` im Projektverzeichnis startet diese Maschine mit dem dafür gebauten Emulator. Unreal liegt im Gast unter `C:\UNREAL\SYSTEM\UNREAL.EXE`. Bei Bedarf lässt es sich über *Start → Run* starten. Im Spiel öffnet Tab die Befehlszeile; `STAT FPS` zeigt die interne Framezeit an.

## Messung

Gemessen wurde mit Glide im Vollbild bei 512 × 384 in der ersten spielbaren Innenraumszene. Die Emulation lief mit annähernd 100 % Echtzeit. Die Bildrate wurde unabhängig von Unreals Anzeige aus geänderten Ausgabebildern pro Hostsekunde bestimmt.

| Einstellung | Geänderte Bilder | Zeit | Bildrate |
| --- | ---: | ---: | ---: |
| Standarddarstellung | 56 | 20,0 s | 2,80 FPS |
| *Texture Detail: Low* | 34 | 10,0 s | 3,40 FPS |
| Zusätzlich sechs Glide-Effekte aus | 59 | 20,0 s | 2,95 FPS |

Die interne `STAT FPS`-Anzeige meldete in der Spielszene typischerweise etwa 240 bis 380 ms pro Frame. Die Einzelwerte schwanken mit Kamera und Spielzustand. Rohdaten für die beiden letzten Läufe: `artifacts/unreal-win98-20261102-1e0df488/run4/lowdetail-paced.json` und `artifacts/unreal-win98-20261102-1e0df488/run4/lowfx-paced20.json`. Ein Screenshot mit interner Anzeige liegt unter `artifacts/unreal-win98-20261102-1e0df488/run4/stat-fps.png`.

Die zusätzliche Reduktion von Coronas, Detail Textures, High Detail Actors, Screen Smoothing, Shiny Surfaces und Volumetric Lighting brachte in diesem Versuch keinen Gewinn und wurde zurückgesetzt. *Texture Detail: Low* ist im Spielmenü weiterhin aktiv. Das Audio/Video-Menü bietet bei Glide keine kleinere Auflösung als 512 × 384 an; `SETRES 320x240` änderte den aktiven Modus nicht.

Die FPS-Werte stammen aus einzelnen Läufen einer bewegten Szene und sind kein kontrollierter Mehrfachvergleich. Die gezielten Voodoo-Tests und die vollständige WHPX-Testsuite bestanden. Ein breiterer Voodoo-Testfilter meldete einen Fehler in einem nicht zugehörigen Settings-Test.

## Host-GPU-Brücke

Eine separate Kopie der Windows-98-Maschine nutzt nun eine [generische Glide-2-Brücke](GLIDE-HOST-BRIDGE.md). Unreal selbst und sein `GLIDEDRV.DLL` wurden nicht angepasst. Im Gast ersetzt eine 32-Bit-`GLIDE2X.DLL` die ursprüngliche Glide-Bibliothek; der Emulator überträgt ihre Aufrufe an eine 64-Bit-Glide-Bibliothek auf dem Host, die über Direct3D rendert.

In der ersten spielbaren Innenraumszene bei 512 × 384 und 233 MHz Gasttakt wurden nach Bündelung häufiger Zustands- und Texturquellenaufrufe zunächst **356 Glide-Bildwechsel in 10,006 Sekunden** gemessen, entsprechend **35,58 FPS**. Mit der zusätzlichen Bündelung von Zeichenlisten erreichte ein späterer Lauf im hellen Innenraum **509 Bildwechsel in 10,005 Sekunden**, entsprechend **50,87 FPS**. Ein Vorwärtslauf mit der tatsächlich belegten Pfeil-nach-oben-Taste erreichte in einem dunkleren Bereich **590 Bildwechsel in 10,006 Sekunden**, entsprechend **58,97 FPS**. Rohdaten: `artifacts/glide-bridge/run-win98-final/win98-batched-static.json` und `win98-up-forward10.json`. Die Werte vergleichen verschiedene Blickrichtungen und Szenenlasten. Der Ausgangswert von 3,40 FPS beruht auf geänderten Bildern des emulierten Framebuffers, die Host-Werte auf Glide-Bildwechseln; beide Zähler sind nicht exakt gleich.

Die frühere Serie `gpu-move-segment1.json` bis `gpu-move-segment4.json` erreichte 54,14 FPS, war aber kein gesicherter Bewegungstest: `W` ist in der ausgelieferten `User.ini` unbelegt. Sie bleibt als ältere Bildratenmessung erhalten. Die [aktuelle Brückendokumentation](GLIDE-HOST-BRIDGE.md) enthält auch die Windows-95-Tests und ihre Grenzen.

[`Start-Unreal-Win98-Glide-Host.cmd`](../Start-Unreal-Win98-Glide-Host.cmd) startet die isolierte beschleunigte Maschine. Die ursprüngliche Maschine bleibt über [`Start-Unreal-Win98.cmd`](../Start-Unreal-Win98.cmd) verfügbar.

## Ergebnis und Grenze

Unreal ist installiert und im Gast tatsächlich mit Glide spielbar. Der vollständig emulierte Voodoo-Weg erreicht in der gemessenen Szene nur 3,40 FPS; der separate Host-GPU-Weg erreichte 50,87 FPS im hellen Raum und 58,97 FPS bei Vorwärtsbewegung in einen dunkleren Bereich. Das Profiling des ursprünglichen Wegs zeigte fortgesetzte WHPX-Auslastung und rund 50.000 Grafik-MMIO-Austritte pro Sekunde. Die Host-Brücke umgeht diesen Befehlspfad für Glide-Anwendungen, während die ursprüngliche SST-1-Emulation verfügbar bleibt.

Für die Originalversion 226 waren im Emulator zusätzliche Voodoo-Funktionen nötig: variable Textur-LOD-Auswahl und begrenzte Behandlung nicht endlicher Alpha-Gradienten. Die Änderungen liegen in `src/DOSEmulator.Hardware/VoodooGraphics.Raster.cs` und `src/DOSEmulator.Hardware/VoodooGraphics.cs`. Der WHPX-Adressübersetzer erhielt einen begrenzten Schnellpfad in `src/DOSEmulator.Whpx/WhpxInstructionEmulator.cs`. Die Alpha-Behandlung ist eine Entwicklungsnäherung; sie behauptet keine vollständige SST-1-Siliziumtreue.
