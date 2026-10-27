# Auditkorrekturen vom 27. Oktober 2026

**8870/8870 Regressionen, normaler Release-Build mit 0 Warnungen/Fehlern.** 181 neue Auditfälle. Grundlage ist der getrennte, auf dem 8371er-Build eingefrorene Bericht aus dem Chat „Emulator-Funktionen zusammenfassen“, privat unter `artifacts/audit-2026-10-27-e9d8e39a/report.html`. Der Ausgangsstand dieses Korrekturpakets ist der vollständig archivierte 8689er-Starter samt 762 Quellen. Dessen POP-Korrektur bleibt erhalten; die übrigen konkreten Fehler waren in den betroffenen Quellen noch vorhanden. Der Auditbericht selbst bleibt unverändert.

## Konkrete Fehler

| Auditpunkt | Änderung und geprüfte Grenze |
| --- | --- |
| B1: POP mit ESP-Basis | Bereits im 8689er-Paket mit 86 Fällen und echtem negativen Vorher-Gast korrigiert; hier erhalten, nicht neu gezählt. [Vertrag](hardware/X86-POP-MEMORY.md). |
| B2: unladbare gespeicherte Schedulerzustände | Normales Cancel entfernt begrenzt angesammelte tote Queueeinträge ab 1024 neu hinzugefügten Stornierungen; lebende Time/Id-Prioritäten, Sequenz, Clockreste und Callbackreihenfolge bleiben erhalten. Save/Load verwenden dieselbe Grenze von 100000 Ereignissen/Stornierungen und dieselbe Token-/Clockprüfung. Zu große aktive Queues scheitern vor Payloadkodierung und Veröffentlichung. Erfassen selbst bereinigt/mutiert keine Queue und führt keine Callbacks aus. |
| B3: BIOS-Metadatenpuffer | AH42/43 prüfen vor dem ersten DAP-Lesen DS:SI bis FFF0h für die tatsächlich verwendeten 16 Bytes. AH48 prüft den vollständigen 26-Byte-Puffer bis FFE6h. Höhere Starts liefern AH01/CF/BDA-Fehler vor ATA und vor Caller-Pufferwirkungen. Bestehende größere DAP-Präfixgrößen bleiben kompatibel; keine vollständige 24-Byte-/EDD-3-Abnahme. Der getrennte ungerade Datensektor-/Wortportpfad bleibt erhalten. |
| B4: falsche Eingabe-Erfolgsantworten | Owner-IPC verwendet die tatsächliche KBC-Annahme. Volle/deaktivierte Pfade liefern InputNotAccepted. E0-Pakete und Ctrl+Alt+Del werden vollständig angenommen oder vollständig abgewiesen. Einzelnes scanCode bleibt kompatibel, optionales scanCodes ist auf 1–16 validierte Bytes begrenzt. Generationen und StaleInputGeneration bleiben erhalten. Abgewiesene Pakete muss der Caller gezielt erneut senden; keine unbegrenzte versteckte Retryqueue und keine Liefergarantie bei Überlast. Die GUI zeigt die Ablehnung. |
| B5: falsche Capability | GetCapabilities meldet protectedMode=true, unabhängig davon, ob der aktuelle Gast gerade im Real Mode läuft. Keine zusätzliche Pentium-/Windows-Behauptung. |
| B6: fehlender Wiederholungspfad | Der vorhandene zeitgesteuerte gehaltene Tastenpfad sendet wiederholbare Make-Codes; Modifier, Locks, unbekannte Codes und Menüaktionen werden nicht als Gastwiederholung injiziert. Menü-Pfeile nutzen ihren eigenen Wiederholungspfad. Interne Policytests und Codeintegration bestehen; keine native Windows-/Maus-/Fokusabnahme durchgeführt. |

16 Schedulerfälle enthalten zweimal eine **wirklich ausgeführte PE32-PIT-Schleife mit je 120000 OUTs** in einem vollen Produkt-PC. Die Queue bleibt danach bei höchstens 1024 Einträgen. Tatsächliches .dossav-Speichern/Laden und jeweils acht weitere ganze CPU-/RAM-/Geräte-/Schedulervergleiche sowie lebende PIT-Ereignisse bestehen. Das sind synthetische volle PCs, keine zusätzlichen DOS-Starts.

49 Eingabe-/Hostfälle prüfen unter anderem 65 Requests mit realem Gast-IN60/STOS-Drain, atomare Präfixe/Chords, Ablehnungen, ungültige Werte, Generationen, rohe KBC-Fortsetzung und die aktuelle Capability. Die neue native Wiederholung bleibt wegen der ausdrücklich verschobenen Bedienabnahme allein intern geprüft. Die weitere Mausabnahme wird nicht aufgegriffen.

## Behaltene Optimierungen

**O1:** Nur bekannte GPR-/Flags-Familien im 0F-Decoder und FF-INC/DEC sparen die große Kontrollsicherung. Kleine GPR-/EIP-/Flags-Restartkopien bleiben immer erhalten. System-/Selektor-/Task-/Callpfade bleiben konservativ; Beobachter sowie gemappte Daten, Code und Seitentabellen erzwingen weiterhin die vorhandene Sicherung. 29 CPU-Auditfälle einschließlich zusätzlicher FPU-/A20-/MMIO-/FERR-Architekturkontrollen bestehen.

Vier getrennte Prozesse in ABBA-Reihenfolge, je sieben feste Proben: **14 Proben pro Variante**, je 600000 Interpretersteps. Der alte Kern führt in jeder der drei Schleifen 300000 große Kontrollsicherungen aus, der Kandidat null. Vollständiger 2-MiB-RAM und gespeicherte CPU-Werte sind gleich, ausgenommen der ausdrücklich geänderte Diagnosezähler und ein alter Kontrollcache nur bei gesetztem Inaktivitätsflag `_controlCheckpointSaved=false`. Die Fixtures sind ungepagtes PE32. In gepagten Programmen können durch den bereits vorhandenen jetzt häufiger möglichen Immediate-RAM-Pfad auch TLB-Hit-/Altersdiagnosen abweichen; Rechte und relative LRU-Reihenfolge werden nicht gelockert.

| Feste Schleife | Baseline Median | Kandidat Median | Weniger Hostzeit in dieser Probe |
| --- | ---: | ---: | ---: |
| near Jcc | 25,04845 ms | 15,63485 ms | 37,58 % |
| MOVZX | 25,85935 ms | 18,38970 ms | 28,89 % |
| FF INC | 31,13390 ms | 23,70120 ms | 23,87 % |

Dies sind feste Mikroprogramme auf dem normalen .NET-Host, keine allgemeine Doom-FPS-, Echtzeit- oder historische CPU-Taktzusage. Host-/JIT-Streuung bleibt sichtbar; Wandzeiten unveränderter FPU-Schleifen werden nicht als Algorithmusgewinn ausgegeben.

**O2:** Ein wirklich zurückliegender Worker erhält kein zwingendes 1-ms-Sleep mehr. Bei Gleichstand/Vorsprung bleiben begrenzte Wartezeiten, Catch-up-Grenzen, Resynchronisierung, virtuelle Frequenz und Shutdown-/Kommandopump erhalten. Interne Zeitgrenzen sind geprüft, kein neuer nativer Echtzeitnachweis.

**O3:** DOSE benutzt eine vollständige Captureidentität und eine eigene Pixelrevision. Gleiche Inhalte behalten ihre bestehende Lease; Shader-/Layoutparameter werden weiterhin pro Präsentation aktualisiert. Nur geänderte versionierte Quellen werden hochgeladen. Der alte 17-Parameter-Pfad bleibt binär kompatibel und lädt mit null-Revision weiter immer hoch. Derselbe Produktionscache entscheidet im Policytest bei 10000 unveränderten Präsentationen einmal statt 10000-mal für Upload. Das ist keine gemessene GL-Bandbreite. Versionierte Uploads werden erst nach GL-Fehlerprüfung gecacht; fehlgeschlagene Allokationen/Uploads invalidieren Cache und Dimensionen, werden erneut versucht und bleiben über SourceUploadError/Notice/wechselabhängiges Trace sichtbar. Leases reichen weiterhin bis nach dem GPU-Upload. Kein Testfenster oder native GL-Abnahme.

**O5, begrenzter Teil:** OPL2/OPL3 ersetzen nur den bestehenden Null-Aktivitätszählvergleich durch kurzschließendes Prüfen und wiederkehrende Rhythmusmaskenarrays durch konstante Spans. Öffentliche Diagnosezählungen, Mathematik, Phasen, Noise, LFO, Timer, Mute, Filter und Integratoren bleiben erhalten. Zwölf ausgewählte Komponentenfortsetzungen vergleichen je 16 zukünftige volle Zustände/native Samples/PCM. Korrekte OPL2-Portwartezeiten und ihre echten Zwischenframes sind Bestandteil der Probe. Vier ABBA-Prozesse liefern je Modell **18 Proben pro Variante mit je 4096 Rhythmusschreibzugriffen**: konstant **120 Byte/OPL2-Schreibzugriff** beziehungsweise **720 Byte/OPL3-Schreibzugriff** weniger Heapallokation. Bei OPL3 sinkt die gemessene Schleife von 2949120 auf 0 Byte. Native/PCM-/Cut-/Endzustandsdigests aller zwölf Fälle und beider Wiederholungen sind zwischen Alt- und Neukern exakt gleich. Weitergehendes Vibrato-/Topologie-Umbauen bleibt offen.

**O9, begrenzter Teil:** Cold-open und Export verwenden je Schleife einen Stack-SHA-Zielpuffer. Alle 552-Byte-Records, LBAs, Checksummen, unvollständigen Enden, Cursor und späten Fehler werden weiter in derselben Reihenfolge geprüft. 17 gezielte Fälle bestehen. Vier ABBA-Prozesse, **18 Proben pro Variante/Phase**, je 8192 eigene wiederholte LBA-Records: **458752 Byte weniger Heap je Phase**, exakt **56 Byte pro Record**. Cold-open 466336→7584 Byte, Export 464064→5312 Byte. Generation-/Sektorhashes sind gleich. Gemessene Cold-open-Zeit 16,00→16,51 ms zeigt gerade keine pauschale Ladebeschleunigung; Export 49,86→46,39 ms bleibt von echter Host-I/O abhängig. Readanzahl und alle Flush(true)-Durabilitätsgrenzen sind unverändert. Blockweise Exportreads sind damit nicht umgesetzt.

## Verworfen und getrennt offen

**O4:** Drei x87-Puffervarianten beseitigten temporäre Arrays, zeigten jedoch keinen stabilen positiven Durchsatz; die gemessenen Prototypen waren in den relevanten Schleifen deutlich langsamer. Sie wurden vollständig verworfen. Die vier betroffenen x87-Quelldateien sind bytegenau zum 8689er-Stand wiederhergestellt; alle ursprünglichen x87-/Float80-Quellen bleiben unverändert. Der bekannte einfache m64-Load bleibt bei 88 Heapbytes; das ist offen. Die Messdaten und Entscheidung stehen privat in x87-rejection.json. Zusätzliche Architekturtests erhalten insbesondere die wichtige Grenze, dass ein FERR-Subscriber bei einem trotzdem committenden Präzisionsstore A20/Mapping vor dem Bytebus-Schreiben ändern kann. Ein künftiger Fastpath muss das erhalten. Keine Übernahme von Host-double.

O6 (Zeilenrasterizer), O8 (Identitätslease statt Mehrfachhash), O10 (DMA-Spezialisierung), O11 (IRQ-/Pollwarten), O12 (RAM-RMW), O13 (typisierte REP-Fills) und O14 (UInt128-Zahlenkern) bleiben Profile-/Vergleichsaufgaben, keine hier behobenen weiteren Bugs. O7-Write-back ändert den Durabilitätsvertrag und wird nicht als bloßes Entfernen eines unnötigen Flushes umgesetzt. Die weitergehenden Teile von O5/O9 bleiben ebenfalls offen. Es wird keine vollständige Optimierung aller Auditvorschläge behauptet.

## Endgültige Gäste und Quellen

**34 tatsächliche DOS-Kaltstarts, 26 volle PC-Fortsetzungen**, getrennt von den zwei synthetischen PIT-PCs und ausgewählten OPL-Proben:

| Matrix | Kaltstarts | Volle Fortsetzungen | Weitere ganze Zustands-/RAM-Vergleiche | Verfälschte Oracles |
| --- | ---: | ---: | ---: | ---: |
| Neues BIOS-Metadatengastprogramm | 8 | 0 | gesonderte RAM-/Portoracles | 624 |
| DOS/EMM386 PCM/x87-Datei | 8 | 8 | 1282 | 200 |
| Kombiniertes PCM/DMA/VDS/OPL3/MPU | 16 | 16 | 3504 | 672 |
| Originales DOS4GW/Doom | 2 | 2 | 38 | 18 |
| **Gesamt** | **34** | **26** | **4824** | **1514** |

Der neue BIOS-Gast führt 96 echte INT13-Aufrufe aus (48 erfolgreich/48 abgewiesen), 32 tatsächliche Scratch-ATA-Befehle mit 8192 Wörtern und 4608 unabhängig geprüften Metadaten-/Wächterbytes. Schreibziel ist ausschließlich eine eigene neue BIOS81h-Scratchplatte. Der begrenzte Nachweis-/Metadatenblock bleibt per DOS/AH31 bis Reset resident, keine vollständige DOS-Bereinigung wird behauptet.

Die kombinierte Audiomatrix erhält laufende DMA, rohe FM-Zustände, nichtnullige Filter, MPU-Teilframes, getrennte Quellen/Quittierungen und vollständige Zukunfts-/Mediengleichheit. **16 echte beschädigte Importablehnungen** zählen gesondert. Doom liefert 18 WAD-geprüfte Bilder, 450900 Stereo-PCM-Frames, tatsächliche DOS-Rückkehr und identische Fortsetzung; keine neue allgemeine Spiel-/Sound-/FPS-Abnahme. Im eigenen Altstarterzweig bestehen 84 der 181 neuen Fälle, 97 scheitern. Diese Zahl enthält bewusst auch fehlende neue APIs; sie ist kein Zähler von 97 eigenständigen Bugs. Der eingefrorene ursprüngliche Audit und gezielte tatsächliche Gäste bleiben die Fehlerbelege.

38 Typen/751 Instanzmitglieder/32 unveränderte Ereigniskörper. Snapshotformat 1 bleibt buildgebunden; ein echter alter eigener 8689er-Import wird vor Zweiganlage abgewiesen. Alle 64 Kandidaten-/normalen Produktdateien sind bytegleich. 659 lizenz-/kommentargeprüfte Code-/Projektdateien, 134 Python- und 92 PowerShell-Dateien. 115 geschützte Eingaben, C64 und HEAD bleiben unverändert; persönliche Spiel-COWs bleiben ungeöffnet. Keine Windows-Eingaben/Fokusversuche, Host-COM/MIDI/Drucker/Netzwerk/Mikrofonaktivierung, Aufnahme, Commits/Pushes oder Veröffentlichung.

Diagnosen einschließlich des ersten BIOS-Testgast-Clears, der fehlenden OPL2-Testwartezeiten, des blinkenden Policy-Zeitpunkts sowie aller verworfenen x87-Prototypen sind ausgeschlossen. Die endgültigen Matrizen laufen auf denselben abschließend geprüften DLLs.

Privater Gesamtnachweis: **artifacts/tests/audit-fixes-release-path.txt**. Reproduktion der neuen BIOS-Vertikale: **scripts/test-audit-bios.ps1** mit expliziten privaten Medienargumenten. Neue Regressionen: **--filter "Audit "**. Die ursprünglichen historischen Buildnachweise werden nicht umetikettiert.

M4 bleibt offen. Nächster Produktschritt ist die vollständige P54C-PC-/Profil-/Factory-/Clock-/Ownerintegration mit echtem DOS-/EMM386- und vollem SnapshotStore-Nachweis; anschließend 430FX/PIIX, S3 Trio64, ATAPI, Firmware/Manager und wirkliche Win95-RTM-Abnahme. Der stündliche Auftrag für M0–M8 läuft weiter.
