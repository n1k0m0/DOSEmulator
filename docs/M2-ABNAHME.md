# M2: nutzbarer MS-DOS-Rechner im Referenzprofil

M2 wird anhand der Abnahmekriterien aus Kapitel 19 des Konzepts bewertet: MS-DOS 6.x von den Originaldisketten auf einer privaten HDD installieren, Dateien schreiben/lesen, neu starten und ausgewählte Real-Mode-Programme mit Bild und Ton ausführen. Der funktionale Referenzumfang ist implementiert; die abschließende native Mauswiederholung am letzten Build wartet noch auf Windows-Vordergrundfokus. Das ist ein definierter funktionaler Entwicklungsstand, keine vollständige Replik eines beliebigen 486-PCs oder aller Sound-Blaster-/VGA-Chips.

## Unterstützter Aufbau

- i486-Interpreter im dokumentierten Real-Mode-Umfang, 8 MiB RAM, eigene AT-Firmware, PIC/PIT/RTC/KBC und Gastresets.
- IDE/FAT16 mit persistentem privatem COW und zwei konfigurierbaren FDC-/DMA-Diskettenlaufwerken, FAT12, Formatieren und Medienwechsel.
- VGA-Textseiten, CP437/CP850, 8/14/16 Punkte, 25/28/43/50 Zeilen, BIOS-Cursor-Skalierung sowie Grafik 12h/13h mit Font-/Zeichen-/Scroll-/Pixelpfaden.
- Speaker, SB2-Mono-PCM und virtueller ADC über DMA1, normale und High-Speed-Ein-/Ausgabe, alle neun Creative-ADPCM-Ausgabestarts, Block-/Pause-/Reset-/IRQ-Pfade. Basis 220h/240h und IRQ3/5/7/9 sind konfigurierbar.
- OPL2 mit neun melodischen beziehungsweise sechs melodischen und fünf Schlaginstrument-Stimmen, Timern, Register-/Buszeiten und CSM-Key-/Total-Level-Latch im nativen Samplezeitmodell.
- PS/2-Zweitastenmaus mit echtem CuteMouse, nativer relativer Windows-Eingabe, Mausfang, Fokusverlust, erneutem Fangen und Ctrl+Alt-Freigabe.
- Gemeinsame native Grafik-/Menübedienung, Medienbrowser, Profile, getrennte Livepegel, Mute, Filter und Auflösungsscanout. Hardware-/Ressourcenwechsel erfolgen mit dem dokumentierten Neustart; nicht jede Steckkarte ist im laufenden Gast austauschbar.

## Nachweise und Reproduktion

Der gemeinsame Release-Build besteht **500 Regressionstests ohne Buildwarnungen oder Fehler**. Der aktuelle zusammengeführte private Nachweis liegt unter `artifacts/m2-release/acceptance-ac7450f8eb794314b552175edb9cf2a4/`. Er enthält die tatsächlich getesteten Assembly-Hashes, geschützte Medienidentitäten, Abnahmepfade und Grenzen. Originalmedien, kanonisches Installations-COW und C64-Projekt bleiben unverändert.

| Bereich | Nachweis |
| --- | --- |
| Originales deutsches MS-DOS 6.22 | Bereits abgenommenes Setup von Disketten 1→2→3 auf neuer 32-MiB-HDD, Neustarts, FAT-/Dateiprüfung, COMMAND.COM, SETVER, DISPLAY, COUNTRY, KEYB und gesondertes ANSI-/MODE-Profil; historische Artefakte stehen im Implementierungsstatus. |
| IDE-/FDC-Grundlage | Erneut zwei FreeDOS-IDE-Kaltstarts mit 438 ATA-Sektoren und unverändertem `F4330321…21142D`; sechs FDC-Kaltstarts mit beiden Laufwerken, COW-Schreiben und Medienwechsel. |
| VGA/Fonts/Resets | Bereits abgenommene CPU-/Gasttests, unabhängige vollständige Pixelprüfungen, nativer Scanout und Originaltreiber. Diese unveränderten Pakete werden nicht als neue Implementierung ausgegeben. |
| ADPCM | Vier Kaltstarts unter beiden DOS-Systemen. Je 1.200 komprimierte DMA-Bytes, 3.582 DAC-Samples und 24 CPU-IRQs. Unabhängiger Prüfer rekonstruiert alle PCM-Frames aus eigenen Quelldaten und tatsächlichen Portzeiten; Pause, Auto-Init, Fortsetzung und vorgemerkte Einzelblöcke sind enthalten. |
| OPL2-Rhythmus | Acht Kaltstarts, AdLib-/SB-Alias und beide DOS-Systeme, fünf einzelne Schlaginstrumente, sechs Melodiekanäle plus Schlagzeug und Rückkehr in den Melodiemodus. Separat gebautes YMFM vergleicht Tonhöhen, relative Pegel und Nulldurchgänge. Zwei native Prozesse mit zwölf GL-Aufnahmen und Windows-Audio. |
| CSM-Pegel | Vier Kaltstarts unter beiden DOS-Systemen: Pegelschreiben bei gestopptem Timer bleibt unhörbar, nächster Timerimpuls übernimmt 24 dB Dämpfung, CSM-Abschalten übernimmt den nächsten ausstehenden Pegel. Keine normalen B-Register-Keys während dieser Probe. |
| Native Maus | Windows-`SendInput` ausschließlich an nachweislich eigene Vordergrundfenster, echter OS-Fokuswechsel in ein eigenes Hilfsfenster, Prüfung des Windows-Cursorclips und echter INT33-/KBC-/IRQ12-Gastpfad. Zwei DOS-Systeme, sechs GL-Aufnahmen, reguläre Programmenden, VER/ECHO und Prompt. Keine reine Szenario-/Handler-Simulation und keine Messung einer menschlich bedienten physischen Maus. |
| Bisheriges Audio | Erneut normale/high-speed PCM-Gäste, ADC/Roundtrip, FM-Melodie und CSM-Timerprobe. Alte akzeptierte PCM-Hashes bleiben erhalten. |

Neue Reproduktionsskripte, jeweils mit `-SkipBuild`, optionalem `-OutputRoot` und den absoluten privaten Pfaden `-Disk`/`-Overlay`:

```powershell
.\scripts\test-sb2-adpcm.ps1 -Disk $privateDisk -Overlay $pristineCow
.\scripts\test-opl2-rhythm.ps1 -Gui -Disk $privateDisk -Overlay $pristineCow -ReferenceExe $independentYmfmAdapter
.\scripts\test-opl2-csm-levels.ps1 -Disk $privateDisk -Overlay $pristineCow
.\scripts\test-mouse-native.ps1 -Disk $privateDisk -Overlay $pristineCow
```

Die Variablen sind vom Aufrufer zu setzen; Originaldateien werden nicht beschrieben. Ohne `-SkipBuild` erfolgt zuerst der normale gemeinsame Build. Das native Mausprogramm bewegt den Windows-Zeiger kurz innerhalb seiner eigenen Testfenster und stellt ihn anschließend wieder her. Es verweigert Eingaben an fremde Vordergrundfenster; bei verweigertem Fokus oder Benutzereingriff kann die Abnahme ohne Freigabe fehlschlagen. Native Mausprüfungen nacheinander und ohne andere GUI-Abnahmen ausführen. Browser sind nicht beteiligt.

`tests/opl2-reference.cpp` ist nur ein eigener Adapter für eine separat bereitgestellte YMFM-Quelle. Die private Vergleichsinstallation verwendet Upstream-Commit `81aec25ccbb98f4873a255f7551ac4dadac59b4a`. Die Quelldateien, Lizenz, Compiler und Hashes sind im privaten Referenzmanifest erfasst. Der Adapter wird mit einem vorhandenen C++17-Compiler gegen `ymfm_opl.cpp`, `ymfm_adpcm.cpp` und `ymfm_pcm.cpp` gebaut; Produkt/Emulator laden oder linken diesen Vergleichscode nicht. Keine automatische Fremdquellinstallation beim normalen Projektbuild. Herkunft/Lizenz der verwendeten Rhythmusformeln: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Behobener Fehler im nativen Eingabepfad

Die erste echte Windows-Prüfung fand eine zuvor von Szenarioeingaben verdeckte Lücke: `RelativeMouseInput.Input` wurde in der GUI ohne die IPC-JSON-Optionen als `X/Y/Buttons` serialisiert; `MachineSession` erwartet `x/y/buttons`. Cursorfang und Szenario-Maus funktionierten, native Mausereignisse wurden abgewiesen. `IpcProtocol.CreateRequest` verwendet jetzt für GUI-Records und anonyme Nutzdaten dieselbe Namenskonvention. Ein zusätzlicher Regressionstest prüft die wirklichen PS/2-Bytes einschließlich Loslassen; die native DOS-Abnahme belegt den gesamten Pfad. Fokusverlust und Beenden lösen auch den OS-Fang.

## Verbindliche Grenzen

- HIMEM und begrenzte PE16-/PE32-Funktionen sind inzwischen in einem [gesonderten M3-Teilpaket](hardware/X86-PROTECTED.md) geprüft. Vollständige Privileg-/TSS-Pfade, EMM386, V86/Paging und x87 bleiben offen. Ebenso fehlen Pentium, Windows 95, Netzwerk, SVGA-/Voodoo-Karten und die übrigen M3–M8-Geräte.
- SB2 ist das erklärte Entwicklungsmodell; vollständige beliebige Treiber-/Spieleerkennung, SB-MIDI, Gameport, SBPro/SB16/MPU und reale Host-MIDI-Endpunkte sind nicht implementiert. Das allgemeine Fähigkeitsbit `soundBlaster` bleibt deshalb false. Virtueller ADC bedeutet keinen geöffneten Host-Mikrofoneingang.
- ADPCM-Schrittregeln und Blockübergänge beruhen auf dokumentiertem Creative-Format und veröffentlichten Hardwarebeobachtungen. Gerade Nicht-Referenz-/Auto-Init-Schrittneustarts und Byte-/IRQ-Latenzen sind als Referenzvertrag offengelegt. Das ist kein Vergleich aller DSP-Revisionen mit Originalsilizium.
- OPL2 bleibt eigene mathematische Synthese mit angenäherten Hüllkurven/LFO/FM-Tiefen. CSM arbeitet pro nativem Sample, nicht mit einer sechs-Masterclock-Slotpipeline. TL wird am Timerimpuls gelatcht; genaue TL/KSL-/Bus-/Slot- und Analogübergänge sind nicht siliziumgenau abgenommen. Rhythmusvergleich prüft ausgewählte Programme, keine allgemeine bitidentische Chipausgabe. `siliconExact` und das allgemeine `opl` bleiben false. Präzisionsarbeit darf daraus nicht als erledigt abgeleitet werden.
- PS/2 ist ein ID-00-Zweitasten-Byteprotokoll; elektrische Leitungszeiten, jedes physische Hostgerät und beliebige DPI-/Beschleunigungseinstellungen sind nicht vermessen.
- Die Abnahme betrifft die definierten Modi und Profile, keine vollständige DOS-/BIOS-/Windows-Kompatibilität. Alle Gasttests laufen mit schreibgeschützten Basen und privaten Kopien/COW; Images, ROMs, Treiber und Vergleichsausgaben bleiben außerhalb von Git.

Der funktionale M2-Referenzumfang ist umgesetzt; ein erfolgreicher nativer Mausnachweis liegt bereits für beide DOS-Systeme vor. Die erneute kombinierte Mausabnahme am letzten Build bleibt auf ausdrücklichen Benutzerwunsch („Später; diese Wiederholungsabnahme offen lassen“) offen. Dafür werden ohne neuen Auftrag keine weiteren Fenster oder Fokusversuche gestartet. Unabhängige M3-Vorbereitung läuft inzwischen mit getrennt nachgewiesenen Systemregistern, Descriptor-Caches, kontrolliertem PE16/PE32 und originalem HIMEM/XMS weiter; M2 bleibt bis zum beauftragten Wiederholungslauf nicht abschließend abgenommen; Originalchip-Präzision und die genannten zusätzlichen Geräte bleiben im Gesamtplan offen. Die stündliche Fortsetzung bleibt aktiv.
