# YMF262 / OPL3 im CT1740-Referenzprofil

## Begrenzte Verwaltungsoptimierung vom 27. Oktober 2026

Das [8870er-Auditfolgepaket](../AUDIT-FIXES-2026-10-27.md) ersetzt ausschließlich Null-Aktivitätszählung durch kurzschließende Prüfung und Rhythmusmaskenarrays durch konstante Spans in OPL2/OPL3. Mathematik, Noise, Phasen, LFO, Timer, Mute, opake Filter/Integratoren und öffentliche Diagnosezahlen bleiben erhalten. Zwölf ausgewählte Komponentenfälle und vier Alt-/Neuprozesse prüfen bitgleiche native/PCM-/Rohzustände; 16 aktuelle kombinierte DOS-/DMA-/VDS-/OPL3-/MPU-Fortsetzungen ergänzen die Produktprüfung. Das erweitert keine Chip-/Originaltreiberpräzision. Historische Zahlen unten behalten ihre Builds.

Stand: 25. Oktober 2026. Das Profil `sb16-ct1740-dsp405-development` verbindet den DSP-4.05-PCM-Kern jetzt mit einem **eigenen funktionalen YMF262-Modell**. Referenzvertrag: Yamaha 4MF262A6, 14.318.180 Hz, ein Chipframe pro 288 Masterclocks. Implementiert sind zwei Registerbänke, 18 Zweioperator-Kanäle, sechs Vieroperator-Paare, acht Wellenformen, Rhythmus und vier interne Ausgangsbusse. Hüllkurven, FM-Tiefe, LFO, Slotpipeline und Analogausgabe bleiben Näherungen. Dies ist keine vollständige SB16-/Originalchip-/M3-Abnahme.

## Ports und Registervertrag

388h–38Bh und Boardbasis+0…3 erreichen denselben Chip. Basis+8/+9 ist ein zusätzlicher Alias des unteren Portpaars. Ein **gemeinsamer neun Bit breiter Adresslatch** wird von beiden Adressports und allen Boardaliasen verwendet; beide Datenports schreiben an diesen zuletzt gewählten Latch. Mixer und DSP bleiben getrennte Geräte. Der FM-Timerpin ist nicht mit DSP-IRQ oder PIC verbunden.

Im OPL2-Betrieb adressiert ein oberer Adresszugriff die **untere Registerbank**. Die Ausnahme 05h erreicht weiterhin 105h zum Einschalten von NEW. Nach NEW=1 ist die obere Bank zugänglich. Ein getrennter nativer Referenzlauf hat diese Aliasierung aufgezeigt; ein tatsächlicher DOS-Fall prüft fünf solche Zugriffe. Das ist keine zweite OPL2-Karte.

104h wählt die Paare 0+3, 1+4, 2+5, 9+12, 10+13 und 11+14, nullbasiert gezählt. Der erste Kanal bestimmt Frequenz, Block und Key aller vier Operatoren. Beide CNT-Bits bestimmen die Verbindung; Feedback gehört zum ersten Operator. **Alle resultierenden Träger verwenden die Ausgangsmaske des ersten Kanals.** Die Verbindung des zweiten C-Registers bleibt wirksam. Die Referenzprüfung unterscheidet dies durch unterschiedliche Masken beider Kanalhälften.

Effektive Wellenform und Ausgangsmaske werden bei ihren Registerschreibzugriffen gewählt: vier Formen/A-B im Kompatibilitätsbetrieb, acht Formen/vier Maskenbits bei NEW. Ein späteres NEW=0 löscht nicht pauschal zuvor programmierte Stimmen, Masken oder Paarverbindungen. Vollständige elektrische oder undokumentierte Moduswechselpräzision ist dadurch nicht abgenommen. Testbits werden diagnostiziert; YMF262 besitzt hier **keine CSM-Keyquelle**. Der vorhandene geprüfte YM3812-CSM-Pfad bleibt separat erhalten.

Jeder gemappte FM-Zugriff durchläuft den vorhandenen **750-ns-ISA-Bytepfad**, einschließlich tatsächlicher IN/OUT-Befehle und Zugriffe über verschiedene Adress-/Datenaliaspaare. Es gibt keine vom OPL2 übernommene künstliche Datenpause. Das ist keine vollständige Pin-/IOCHRDY-/Arbitrationssimulation; breite Zugriffe werden nicht erfunden in Registerbytes zerlegt.

Chipframe n liegt bei `ceil(n × 288 × 10^9 / 14318180)` Nanosekunden. Timer verwenden 1152/4608 Masterclocks pro Einheit, 8-Bit-Carry/Reload, Masken, Start/Stop und Flagreset. Der geprüfte Vertrag zählt `256−Preset` Einheiten; die gedruckte Kurzformel `255−Preset` im Yamaha-Datenblatt wird nicht als Nullzeitüberlauf bei FFh umgesetzt. Host-Audiocallbacks treiben weder Chip noch Timer an.

## Synthese, vier Busse und CT1745

Der neue Kern verwendet die vorhandenen **eigenen OPL2-Referenztabellen** für Multiplikatoren, KSL und angenäherte Envelope-Zeiten; die bisherige OPL2-Ausführung wurde nicht verändert. Phasen, Hüllkurven und Verbindungsgraphen werden je nativem Chipframe ausgewertet. Die vier Vieroperator-Verbindungen besitzen die hörbaren Träger 4; 2+4; 1+4; beziehungsweise 1+3+4. Gerätefälle isolieren jeden davon in allen sechs Paaren.

Zusätzliche Formen sind verdoppelter Sinus beziehungsweise dessen Betrag mit anschließender Pause, Rechteck und exponentielle Sägeform. Letztere verwendet im mathematischen Modell `2^(−i/32)` je Tabellenhälfte. Die Vergleichsprüfung hat einen zunächst zu flachen Abfall aufgedeckt; Pegel und Oberwellen der korrigierten Form sind ausdrücklich geprüft. Dies ist keine bitgenaue originale Log-Sinus-/Exponential-ROM-Tabelle.

Rhythmus ersetzt nur die drei unteren Rhythmuskanäle; die zweite Bank bleibt melodisch nutzbar. Rausch-/Schlaginstrumentphasen verwenden denselben zugeordneten Forschungsvertrag wie der eigene OPL2-Pfad. Herkunft und BSD-Hinweise stehen in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Ein fremder Synthesecore ist nicht in das Produkt eingebettet.

A/B/C/D bleiben intern getrennt. Das CT1740-Referenzboard verbindet **A mit links und B mit rechts; C/D bleiben am Board unverbunden**. Es findet keine automatische Quad-zu-Stereo-Summierung statt. Der gemeinsame Mixer integriert beide verbundenen Quellen, ohne den Chip für rechts erneut weiterzuschalten. Unterschiedlich große Host-Arbeitsscheiben liefern identische Roh- und PCM-Frames. Opaque Integratoren und Filterreste werden nicht pauschal als Stille übersprungen.

CT1745 erhält FM-Level 34h/35h und Legacy-Alias 26h. FM folgt auch Master 30h/31h und Ausgabegewinn 41h/42h; Voice 32h/33h bleibt getrennt. Gastpegeländerungen wirken innerhalb eines Hostsamples vor dem DC-Filter, der zusätzliche Host-FM-Pegel verändert keine Gastregister. DSP-Reset löscht den FM-Chip nicht. Volle Eingangsmatrix, Klangregler und Analogfilter bleiben offen.

## Nachweis

Der gemeinsame Build besteht **4873/4873 Regressionen ohne Warnungen/Fehler**, davon **409 neue Geräte-/Integrationsfälle** gegenüber dem vorherigen PCM-Build. Geprüft werden alle 18 Kanäle mit allen 16 Ausgangsmasken, sechs Paare mit vier Verbindungen und je vier isolierten Operatoren, alle Wellenformen, Frequenz-/Keybesitz, Kompatibilitätsalias, gemeinsamer Latch, Timer, Rhythmus, DSP-Unabhängigkeit, CT1745-Pegel und Host-Arbeitsscheiben.

`OPL3.COM` enthält ausschließlich eigene Daten und **74 Fälle**: 18 Kanäle, 16 Ausgangsmasken, 24 Vieroperator-Konfigurationen, acht Wellenformen, fünf Schlaginstrumente, OPL2-Kompatibilität über den oberen Port, zwei verschiedene gleichzeitige Stereotöne und aktives FM bei DSP-Reset/Versionsabfrage/direkter PCM-Ausgabe. Während eines Tons wird außerdem der rechte FM-Pegel verändert. Jeder Abschnitt wartet über echte BIOS-Ticks; kein Host ersetzt Gastcode, Portzugriffe oder Instrumentdaten.

Der RAM-Nachweis prüft Programm-/Tabellenbytes, DSP 4.05, Timerflags, 74 abgeschlossene Fälle und unveränderte PIC-Masken. Danach folgen saubere DOS-Rückkehr und VER/ECHO/Prompt. Die Probe programmiert den Soundchip bewusst um und verspricht keinen vollständigen CPU-/Geräte-Snapshot.

Die Abnahmematrix umfasst **16 Kaltstarts**: Basis 220h/280h unter MS-DOS 6.22, FreeDOS und originalem EMM386 NOEMS/RAM, jeweils zweimal. Der Monitor wird anhand tatsächlicher V86-/Paging-Ausführung geprüft. Neue private HDD-COWs bleiben unverändert. Wiederholungen stimmen in virtueller Zeit, Bild, PCM-, Quad- und Proofhash überein. Zwischenbuilds ersetzen keine Abnahme des endgültigen Aliasfixes.

`tests/opl3-reference.cpp` ist ein eigener Adapter für separat gebautes BSD-YMFM, Upstream `81aec25ccbb98f4873a255f7551ac4dadac59b4a`. Er erhält ausschließlich tatsächliche FM-OUTs, besitzt eigene Timer und liefert vier eigene Rohkanäle. Bibliothek und Binary bleiben private Prüfwerkzeuge, niemals Ausführungsbackend des Emulators; keine fremden ROMs oder Instrumentbanken werden geladen.

Der unabhängige Python-Prüfer importiert keine Emulatorassembly. Er prüft Programm/RAM/Portabläufe, Quellen/Paarverbindungen, Masken und mathematische Tonhöhe. Ausgewählte native Tonhöhen dürfen **0,5 Prozent**, relative Pegel **fünf Prozent** und normalisierte Wellenform-Oberwellen **0,08** von der separaten Referenz abweichen. Nicht gewählte Ausgänge müssen exakt still sein. Dies sind funktionale Grenzen für die eigenen Proben, keine allgemeine Zusage für beliebige Feedback-/LFO-/Instrumentprogramme oder Originalsilizium.

Zusätzlich wird die gesamte 48-kHz-Stereodatei unabhängig aus den vier beobachteten Rohkanälen, A/B-Verdrahtung, tatsächlichen Mixer-/DSP-Übergängen, Zeitflächen, DC-Filter und Float32-/PCM16-Format rekonstruiert: höchstens **ein LSB** Abweichung. Der separate native Vergleich schützt dabei die Klangquelle; die Rekonstruktion allein wäre kein unabhängiger Synthesebeweis. Gegenproben verfälschen Berichte, Programm/RAM, Quadkanäle, Datenlängen und gültig formatierte PCM-Kanäle. Konkrete Summen und Artefakte stehen im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

24 bisherige SB16-PCM-/DMA-/VDS-Gaststarts werden auf denselben endgültigen DLLs wiederholt und ihre PCM-Hashes mit der Vorgängerabnahme verglichen. Ein zusätzlicher interner GUI-DOS-Lauf besitzt sechs visuell geprüfte GL-Aufnahmen und aktive Windows-Audioausgabe, ohne Windows-Eingaben. Die Audiozählung enthält auch Start-/Menüpausen und meldet Unterläufe; dies ist keine neue unterlauffreie Echtzeitabnahme. Die verschobene native Mauswiederholung wird nicht berührt. Unveränderte CPU-/x87-/FDC-/OPL2-Matrizen und frühere Performancezeiten werden ohne neuen Anlass nicht pauschal wiederholt. Keine allgemeine oder interaktive Doom-/Soundkartenabnahme.

## Reproduktion und Fortsetzung

```powershell
.\scripts\run.ps1 -NoBuild -Profile .\profiles\virtual486-sb16-dev-v1.json -Disk <private-dos.img> -Overlay <private-dos.cow>

.\scripts\test-opl3.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow> `
  -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow> `
  -ReferenceExe <separat-gebauter-opl3-reference.exe>
```

Ohne Managerpfade laufen acht MS-DOS-/FreeDOS-Kaltstarts. Ohne `-SkipBuild` wird zuerst gebaut. Der eigene C++-Adapter wird nach dem vorhandenen OPL2-Verfahren gegen privat bereitgestelltes YMFM gebaut; keine automatische Installation/Netzverbindung. Headless `--opl3-native-out <neue-datei.s16>` exportiert für Diagnosezwecke vier signierte 16-Bit-Werte je nativem Chipframe.

Die erste [ET4000AX-Bank-/Legacy-SVGA-Vertikale](ET4000AX.md) ist inzwischen gesondert geprüft; ihr nächster Ausbau ist die eigene begrenzte VBE-Firmware. Verbleibende SB16-/reale Programmfälle bleiben eigene offene Pakete, danach Gameport/COM/LPT/MIDI und koordinierte Snapshot-/Overlay-Generationen. Vollständiges M3, Windows 95, Pentium, Netzwerk und Voodoo bleiben offen. Host-Mikrofon, MIDI und Netzwerk werden nicht automatisch geöffnet; Benutzer-Spiel-COWs werden für diese Tests nicht verwendet.

## Quellen

- Yamaha, [YMF262, 4MF262A6, November 1994](https://map.grauw.nl/resources/sound/yamaha_ymf262.pdf), gelesene vollständige Seiten 1, 5–8, 11–18 mit Register-/Verbindungsdiagrammen und Zeitangaben. Hersteller-PDF und gerenderte Diagramme bleiben private Referenzartefakte.
- Creative, [Sound Blaster Series Hardware Programming Guide](https://ardent-tool.com/sound/Sound_Blaster_HW_Programming_Guide_1st.pdf), SB16-Portplan PDF-Seite 122 und CT1745-FM-Level. Numerische Näherungen und Boardverdrahtung sind oben als eigener begrenzter Vertrag beschrieben.
- [YMFM](https://github.com/aaronsgiles/ymfm) als separat zugeordnete Forschung/Testreferenz; Herkunft und BSD-Lizenz in [den Lizenzhinweisen](../THIRD-PARTY-NOTICES.md).

## Ergänzung: laufendes FM bei PCM/MIDI und Snapshot

Der neue [kombinierte Gastnachweis](SB16-COMBINED.md) hält zwei unabhängige FM-Routen über 16,384 Sekunden gleichzeitig mit tatsächlichem DMA16-PCM und bidirektionalem MPU-UART aktiv. Er prüft Tonhöhen/Dauerton/Routing, sämtliche integrierten Stereo-PCM-Werte und identische Snapshotfortsetzung auf den unveränderten VM-DLLs. Beobachtete native FM-Quads sind dabei eine Integrationsquelle, kein neuer unabhängiger vollständiger Synthesizer. Die bisherigen YMFM-/Chipfunktionsgrenzen und historischen Nachweise bleiben bestehen.
