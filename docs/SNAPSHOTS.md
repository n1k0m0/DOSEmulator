# VollstÃƒÂ¤ndige lokale DOSE-Snapshots

## Zweite Auditfolge: Import- und Eingabegeneration

[Aktuelle Korrekturen und Nachweise](AUDIT2-FIXES-2026-10-27.md): aktive REP-Caches werden aus ihren eigenen gespeicherten Befehlsbytes konsistent validiert. Ein nach echtem Interrupt veralteter Cache bleibt zulässig und benutzt weiterhin den normalen Fallback. ATA-DRQ setzt einen geraden Offset mit verbleibendem vollständigem Wort voraus; abgeschlossene 512-Byte-Phasen bleiben gültig. Neue echte rehashte Fehlerimporte erhalten den alten Owner und erzeugen keinen Zielzweig. Das UI bestätigt eine neue Sessioneingabegeneration erst nach lokalem Verwerfen alter Holds/Maus/Repeats; verspätete alte Eingaben behalten den alten Token. Kein Einspeisen alter Gast-Releases nach Import. Format 1 bleibt an den identischen VM-Build gebunden; keine automatische Migration alter 9125er-Container. Inventar weiterhin 40 Typen/762 Mitglieder/32 Ereigniskörper.

## 430FX-Hostfortsetzung vom 27. Oktober 2026

Das [Host-Validierungsprofil](hardware/430FX-HOST.md) speichert zusätzlich CF8h, die vollständigen 256 Host-Konfigurationsbytes, DRAM-Top/Loch sowie alle 32 MiB einschließlich verborgenem PAM-/DRAM-Inhalt. Feste BDF-/Peer-/Firmware-/CPU-/Frequenz-/Profilbindungen werden erhalten; Config und Decoder werden vor Medienzweiganlage gegeneinander geprüft. 40 Typen/762 Mitglieder und dieselben 32 Ereigniskörper sind inventarisiert. Acht neue eigene DOS-/EMM386- und zwei DOS4GW-Fortsetzungen prüfen diesen Teilumfang. Format 1 bleibt an denselben VM-Build gebunden; alte 8942er-Sicherungen werden vor Zweiganlage abgewiesen. Die folgenden historischen Zahlen behalten ihren Buildbezug.

Die vollstÃƒÂ¤ndige Zustandssicherung verwendet einen neuen **`.dossav`-Ordner**. Sie hÃƒÂ¤lt CPU, rohe x87-Werte, RAM, die implementierten GerÃƒÂ¤te, Scheduler und passende Mediengenerationen gemeinsam fest. Eine `.dospc.json` bleibt eine PC-Konfiguration mit **Kaltstart**. Ein `.dosemedia` bleibt ein reiner [Mediencheckpoint](MEDIA-CHECKPOINTS.md).

## Bedienung

Im MenÃƒÂ¼ **SAVED MACHINES** stehen **SAVE FULL SNAPSHOTÃ¢â‚¬Â¦** und **LOAD FULL SNAPSHOTÃ¢â‚¬Â¦**. Speichern wÃƒÂ¤hlt einen neuen `.dossav`-Ordner. Laden wÃƒÂ¤hlt einen vorhandenen Snapshot und legt daneben einen neuen, unabhÃƒÂ¤ngig beschreibbaren `Resume-Ã¢â‚¬Â¦.dosebranch` an. Es ÃƒÂ¼berschreibt keine bestehenden Sicherungen oder Journale. Ein im MenÃƒÂ¼ gespeicherter PC ist durch das MenÃƒÂ¼ pausiert; nach dem Laden setzt **CONTINUE** ihn fort.

Headless:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll --machine EigenerPC.dospc.json --steps 1000000 --save-snapshot Zustand.dossav
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll --load-snapshot Zustand.dossav --snapshot-branch NeuerZweig.dosebranch --steps 1000000
```

IPC verwendet `CreateSnapshot {path}` und `LoadSnapshot {path,destination}`. Der tatsÃƒÂ¤chliche Owner verarbeitet die Befehle zwischen AusfÃƒÂ¼hrungsabschnitten. Antworten enthalten nur begrenzte Metadaten; RAM und GerÃƒÂ¤tefelder werden nicht durch die lÃƒÂ¤ngenbegrenzte Named Pipe geschickt. `GetCapabilities.runningStateSnapshots` ist wahr. Die bestehenden Medienbefehle behalten ihren engeren Vertrag.

## Konsistenter Schnittpunkt

`PcMachine.RunSteps` schÃƒÂ¼tzt den gesamten AusfÃƒÂ¼hrungsabschnitt einschlieÃƒÅ¸lich Interruptzustellung, REP-Batches, Scheduler und abschlieÃƒÅ¸ender Audiointegration vor reentrantem Speichern. Aktive Scheduler-Callbacks sind ebenfalls kein zulÃƒÂ¤ssiger Erfassungspunkt. Die ÃƒÂ¶ffentliche Store-API muss auf demselben alleinigen Emulations-Owner benutzt werden; sie ist keine allgemeine Synchronisierung fÃƒÂ¼r fremde Threads, die direkt CPU oder GerÃƒÂ¤te verÃƒÂ¤ndern.

Die Erfassung fÃƒÂ¼hrt keine Gastbefehle aus, verÃƒÂ¤ndert weder virtuelle Zeit noch Pausezustand und leert keinen angefangenen Audioblock. Synchron abgeschlossene SektorschreibvorgÃƒÂ¤nge liegen in der versiegelten Generation; noch ausstehende ATA-/FDC-Operationen liegen mit ihren Puffern und Fristen im GerÃƒÂ¤tezustand. Der ursprÃƒÂ¼ngliche PC kann anschlieÃƒÅ¸end auf seinen bisherigen Journalen weiterlaufen.

Der explizite Graph enthÃƒÂ¤lt unter anderem:

- Alle GPRs, sichtbare und versteckte Segment-/Descriptor-/LDTR-/TR-ZustÃƒÂ¤nde, Taskgeneration, CR-/Debug-/Paging-/TLB-ZustÃƒÂ¤nde, Wiederanlaufdaten, REP-Fortsetzung und Interrupt-/Debug-/SS-Schatten.
- Acht rohe 80-Bit-x87-Register mit leeren Tags, Kontroll-/Statuswort, TOP, Zeigern und getrennten FERR-/IGNNE-/IRQ13-ZustÃƒÂ¤nden. Es gibt keine Umwandlung in Host-`double`.
- Alle physischen RAM-Bytes, A20, VGA-/ET4000-VRAM, Register, DAC, Latches, Cursor, Fonts und Scanoutdaten des implementierten Modells.
- PIC, PIT und Speaker, RTC/CMOS, DMA samt KanalzustÃƒÂ¤nden, beide ATA-KanÃƒÂ¤le mit vier festen Positionen, FDC mit beiden Disketten, KBC und optionaler PS/2-Maus, Gameport, COM1 und LPT1.
- SB2-ADPCM-/PCM-/ADC- oder CT1740-DSP-/Mixer-/DMA-/IRQ-ZustÃƒÂ¤nde, OPL2/OPL3-Operatoren/HÃƒÂ¼llkurven/Phasen/Timer/Integratoren, MPU-UART und deren getrennte Quellen.
- Angefangene PCM-BlÃƒÂ¶cke einschlieÃƒÅ¸lich Integrations- und Filterresten, nominale virtuelle Drucker-/Echo-Gegenstellen, externe Eingangsqueues, laufende MIDI-/UART-Frames und deren bereits abgetastete Formate/Generationen.
- Schedulerzeit, Tokenfolge, gelÃƒÂ¶schte und noch anstehende Ereignisse, Reihenfolge gleicher Fristen sowie ganzzahlige Taktumrechnung einschlieÃƒÅ¸lich Rest und Cache.

`docs/state-schema-v1.json` benennt fÃƒÂ¼r 38 Komponententypen jedes ÃƒÂ¼berprÃƒÂ¼fte Instanzfeld beziehungsweise jede Auto-Property: gespeichert, konstruktorfest, eigenes Kind oder mit begrÃƒÂ¼ndetem Ausschluss. `docs/state-events-v1.json` benennt 32 Operationen samt typisierten, abgetasteten Parametern. Roslyn inventarisiert den tatsÃƒÂ¤chlichen Quelltext; `scripts/generate-state-code.py` erzeugt gewÃƒÂ¶hnliche typisierte Feldzugriffe und prÃƒÂ¼ft fehlende Felder, zusÃƒÂ¤tzliche Ereignisse sowie geÃƒÂ¤nderte CallbackkÃƒÂ¶rper. Private Objektgraphen, CLR-Typnamen zur Aktivierung und Delegates werden nicht aus Dateien geladen. JSON serialisiert nur die ausdrÃƒÂ¼cklich ausgewÃƒÂ¤hlten Werte.

Ereignisparameter werden erst beim Speichern kodiert. Die normalen Audio-/Timerpfade serialisieren kein JSON. ROM-/Buszuordnung, feste Verbindungen und Audiotopologie werden geprÃƒÂ¼ft. Nicht registrierte GerÃƒÂ¤te, zusÃƒÂ¤tzliche eigene Bus-/Audioquellen, ersetzte Mausverkabelung und opake ADC-/Scheduler-Callbacks werden abgewiesen. Die ausdrÃƒÂ¼cklich gewÃƒÂ¤hlten virtuellen ADC-Rampen sind rekonstruierbar.

## Dateien und Import

Ein Snapshot enthÃƒÂ¤lt:

- `snapshot.json`: Format `DOSE-PC-STATE-1`, Version, ID, Zeitpunkt, GrÃƒÂ¶ÃƒÅ¸en, SHA-256 und IdentitÃƒÂ¤t der fÃƒÂ¼nf tatsÃƒÂ¤chlich verwendeten VM-Assemblies.
- `state.json`: benannte KomponentenzustÃƒÂ¤nde und Ereignisdeskriptoren.
- `ram.bin`: vollstÃƒÂ¤ndiger RAM, einschlieÃƒÅ¸lich privater Gastdaten.
- `media.dosemedia/`: Topologie und vollstÃƒÂ¤ndig versiegelte COW-Journale. GewÃƒÂ¶hnliche RAW-Basisimages bleiben schreibgeschÃƒÂ¼tzt referenziert und mÃƒÂ¼ssen an ihrem aufgelÃƒÂ¶sten Pfad unverÃƒÂ¤ndert verfÃƒÂ¼gbar sein. Der bekannte unverÃƒÂ¤nderliche In-Memory-TestdatentrÃƒÂ¤ger wird ausnahmsweise als eigene private RAW-Basis mitgenommen.

Speichern schreibt und flusht neue Dateien in einem frischen benachbarten temporÃƒÂ¤ren Ordner. Erst das Umbenennen verÃƒÂ¶ffentlicht den gemeinsamen Snapshot. Laden schÃƒÂ¼tzt und prÃƒÂ¼ft Metadaten, GrÃƒÂ¶ÃƒÅ¸en, Hashes, exakten Runtime-Build, Profil und Medien, erzeugt getrennte Journalkopien und baut einen **neuen** Graphen. Alle Feldmengen, festen ArraygrÃƒÂ¶ÃƒÅ¸en, wichtigen Indizes, Uhr-/Tokenwerte, Bindungen und Ereignisparameter werden vor ÃƒÅ“bernahme geprÃƒÂ¼ft. Ein erneutes Kodieren muss den importierten Rohzustand exakt ergeben. Die Dateien werden nicht als Code oder beliebige .NET-Objekte interpretiert.

Erst die vollstÃƒÂ¤ndig vorbereitete Maschine ersetzt die aktive. Fehlgeschlagene Importe lassen den alten PC und seine Medien nutzbar; fehlgeschlagene neue Zweige werden entfernt. Beim Laden wird die alte RTC-Batterie ausdrÃƒÂ¼cklich **nicht** in den aufgezeichneten RTC-Zustand kopiert. Der gespeicherte Pausezustand bleibt erhalten.

## Hostgrenze und Grenzen

Bereits an Host-Ausgaben gelieferte PCM-BlÃƒÂ¶cke, MIDI-Bytes oder Druckbytes werden nicht erneut abgespielt. Host-Sinks, Dateien, Grafik-Reader-Leases, GPU-Uploads, Fokus und native gehaltene Tasten sind keine GastgerÃƒÂ¤te. Beim Maschinenwechsel leert der bestehende GUI-Pfad die Host-Audioqueue und setzt das Pacing neu auf. Wartende ÃƒÂ¤uÃƒÅ¸ere Eingaben tragen eine Sessiongeneration: Nach erfolgreicher Ersetzung werden alte Eingaben mit `StaleInputGeneration` abgewiesen. Alte lokale TastenzustÃƒÂ¤nde werden verworfen, ohne Freigaben in die restaurierte KBC-/Mausqueue einzuspritzen. Aufgezeichnete Gastqueues und Tastenbits bleiben erhalten.

Version 1 akzeptiert **denselben VM-Build**, keine beliebigen ÃƒÂ¤lteren oder zukÃƒÂ¼nftigen Builds. Basen und Journale sind lokal gebunden; dies ist kein portables Betriebssystempaket. VollstÃƒÂ¤ndige Journal-Kopien, unkomprimierter RAM und JSON benÃƒÂ¶tigen Platz und synchrone Hostzeit. Ein Prozessabbruch darf unverÃƒÂ¶ffentlichte temporÃƒÂ¤re Ordner hinterlassen. Allgemeine Stromausfall-/Dateisystemtransaktionen, automatische Bereinigung, lang laufende Wiederholungsaufzeichnung und fremde Hostendpunkte sind damit nicht abgenommen.

Die Sicherung umfasst den Zustand der tatsÃƒÂ¤chlich implementierten Modelle. Fehlende CPU-/x87-/VBE-/Sound-/Chipfunktionen werden durch Speichern nicht implementiert. Eine erfolgreiche eigene DOS-/Doom-Fortsetzung ist keine allgemeine Spiel-, Treiber-, Sound-, Silizium- oder M3-Gesamtabnahme. Die ausdrÃƒÂ¼cklich verschobene native Mauswiederholung bleibt unabhÃƒÂ¤ngig davon offen.

## Reproduktion und Nachweise

`scripts/test-snapshots.ps1` bootet MS-DOS, FreeDOS und originale EMM386-NOEMS-/RAM-Konfigurationen ausschlieÃƒÅ¸lich auf frischen privaten COW-Kopien. Der eigene Gast lÃƒÂ¤dt tatsÃƒÂ¤chlich ÃƒÂ¼ber FDC, hÃƒÂ¤lt rohe x87-Werte und GerÃƒÂ¤teaktivitÃƒÂ¤t ÃƒÂ¼ber den Schnittpunkt, schreibt danach eine echte DOS-Datei und kehrt mit AH4Ch zurÃƒÂ¼ck. Der unverÃƒÂ¤nderte Original-PC und die neu geladene Maschine werden bei jeder Fortsetzung gegen vollstÃƒÂ¤ndigen RAM, CPU, GerÃƒÂ¤te, Scheduler, PCM und versiegelte Medien geprÃƒÂ¼ft. Ein unabhÃƒÂ¤ngiger Python-Leser prÃƒÂ¼ft Hashes, Feldmengen, Ereignisse, DOSCOW-Records, FAT-Ketten und die exakten 80-Bit-Werte der tatsÃƒÂ¤chlichen DOS-Datei; verfÃƒÂ¤lschte Nachweise mÃƒÂ¼ssen scheitern.

ZusÃƒÂ¤tzliche Regressionen prÃƒÂ¼fen alle vier ATA-Positionen und sechs Medien, halbe PIO-Puffer, anstehende Sektorcommits, FDC-/DMA-Fortsetzung, alle benannten GerÃƒÂ¤teoperationen, rohe x87-Sonderpayloads, erneutes Speichern restaurierter Ereignisse, Fehler-/Rollbackpfade und alte Eingabegenerationen. MaÃƒÅ¸geblich fÃƒÂ¼r ausgefÃƒÂ¼hrte Builds, endgÃƒÂ¼ltige Zahlen und zusÃƒÂ¤tzliche Gastnachweise sind [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md) und der private Gesamtnachweis `artifacts/tests/m3-snapshots-release-path.txt`.

Der zusÃƒÂ¤tzliche [gleichzeitige PCM-/OPL3-/MPU-Gast](hardware/SB16-COMBINED.md) setzt auf denselben fÃƒÂ¼nf bytegleichen VM-DLLs auf: 16,384 Sekunden DMA16 mit wirklichen DMA-/MIDI-Interruptquellen, aktive Audiounterbrechung und anschlieÃƒÅ¸ende identische Fortsetzung. Seine 16 Starts/16 Fortsetzungen und Importablehnungen sind ein eigener ergÃƒÂ¤nzender Nachweis; die ursprÃƒÂ¼nglichen Snapshot-Gastzahlen bleiben getrennt.

Der [6309er-LSS-Nachweis](hardware/X86-LSS-BOUNDARY.md) ergÃƒÂ¤nzt neue Fortsetzungen unmittelbar vor einem wirklichen pending PIT-Interrupt. Die CPU-BuildidentitÃƒÂ¤t hat sich dabei geÃƒÂ¤ndert: Die vorherigen 6247er-Container bleiben an ihren archivierten Build gebunden; Format 1 migriert sie nicht. Der neue Gast verwendet ausschlieÃƒÅ¸lich eigene neue Container und Journale.




Der [7030er-M3-Abschluss](M3-ACCEPTANCE.md) ergÃƒÂ¤nzt eine aktuelle gemeinsame DOS-/PCM-/OPL3-/MPU-/DOS4GW-Fortsetzungsmatrix. Wegen der erweiterten DOSEBios-Firmware besitzt er eine eigene RuntimeidentitÃƒÂ¤t; der vollstÃƒÂ¤ndige Starter ist archiviert. Auch hier bleibt Format 1 buildgebunden. Die 6247er- und 6309er-Sicherungen bleiben bei ihren eigenen geprÃƒÂ¼ften Builds, ohne automatische Migration.





Der erste [interne M4-PCI-Transport](hardware/PCI-CONFIGURATION.md) wird noch nicht in auswÃƒÂ¤hlbare PCs eingebaut. Ad-hoc-Busmappings werden bei voller Erfassung ausdrÃƒÂ¼cklich abgewiesen. Die neue Hardware-DLL bindet neue Sicherungen an den 7121er-Build; alte 7030er-Container benÃƒÂ¶tigen ihren archivierten Starter. Acht zusÃƒÂ¤tzliche DOS-/EMM386-Fortsetzungen prÃƒÂ¼fen den bestehenden Zustand auf dem neuen Build. Dies erweitert das Format noch nicht um PCI-Funktionen.



Der [7305er-P54C-Befehlsbaustein](hardware/P54C-B5.md) ergÃƒÂ¤nzt eine constructorfeste CPU-Modellinvariante (747 Instanzmitglieder). Bestehende volle PC-Snapshots bleiben 486-Snapshots; der neue interne Kern besitzt einen gesonderten CPU-/RAM-Komponentennachweis. Zwei manipulierte volle Container mit verÃƒÂ¤ndertem oder unbekanntem Modell werden ohne Ersetzung der alten Maschine abgewiesen. Acht neue tatsÃƒÂ¤chliche DOS-Fortsetzungen prÃƒÂ¼fen den 486-Gesamtzustand auf diesen DLLs. Eigene alte 7121er-Container benÃƒÂ¶tigen den archivierten 7121er-Starter; Format 1 migriert sie nicht.

Der [7489er-P54C-TSC-Baustein](hardware/P54C-TSC.md) erweitert das Inventar auf 751 Mitglieder bei unverÃƒÂ¤ndert 38 Typen/32 Ereignissen. Modell und Frequenz sind CPU-Konstruktorinvarianten, die Quelle ist eine feste Ownerverbindung, TSC-Epoche/CR4 sind explizite Werte. Der eigene interne Gast setzt CPU/RAM/PIC/PIT samt Scheduler und nichtnulligem Taktrest gemeinsam fort; das ist ein ausgewÃƒÂ¤hlter Komponentengraph, kein freigegebener voller P54C-PC. Produktprofile bleiben 486. Drei neue manipulierte volle Container (Frequenz/CR4/Epoche) lassen die alte Maschine nutzbar und erzeugen keinen Zweig. Acht neue DOS-Kaltstarts/volle Fortsetzungen prÃƒÂ¼fen den unverÃƒÂ¤nderten 486-Produktumfang auf den endgÃƒÂ¼ltigen DLLs. Alte eigene 7305er-Container werden vor Zweiganlage abgewiesen und benÃƒÂ¶tigen ihren archivierten Starter; Format 1 migriert sie nicht.

Der [7873er-P54C-MSR-Baustein](hardware/P54C-MSR.md) verwendet dieselbe bereits explizite modulare TSC-Epoche fÃƒÂ¼r vollstÃƒÂ¤ndige 64-Bit-Schreibwerte. 38 Typen/751 Mitglieder/32 EreigniskÃƒÂ¶rper bleiben erhalten. Vier eigene ausgewÃƒÂ¤hlte CPU-/PIC-/PIT-/Scheduler-Komponentengraphen setzen nach tatsÃƒÂ¤chlichen WRMSR und anschlieÃƒÅ¸end mit tatsÃƒÂ¤chlichen Fehlerrahmen fort; dies bleibt enger als die offene volle P54C-PC-Integration. Acht aktuelle 486-DOS-Fortsetzungen prÃƒÂ¼fen den bestehenden vollen Produktumfang auf den neuen DLLs. Der vorherige 7489er-Starter ist archiviert; ein echter alter CLI-Import scheitert vor Zweiganlage. Format 1 migriert ihn nicht.

Der [8371er-P54C-Stack-/Gate-Baustein](hardware/P54C-STACKS.md) ergÃƒÂ¤nzt keine Zustandsfelder oder Ereignisse (38 Typen/751 Mitglieder/32 EreigniskÃƒÂ¶rper). Ein eigener P54C-/486-Gast setzt den ausgewÃƒÂ¤hlten CPU-/PIC-/PIT-/Scheduler-Graph mitten im aktiven 16-Bit-Call-Gate vor der ÃƒÂ¤uÃƒÅ¸eren StackrÃƒÂ¼ckkehr fort. Das bleibt enger als ein voller P54C-PC. Acht aktuelle DOS-/EMM386-Fortsetzungen prÃƒÂ¼fen den bestehenden vollen 486-Produktumfang auf den neuen DLLs. Der vorherige 7873er-Starter ist archiviert; ein echter alter CLI-Import scheitert vor Zweiganlage. Format 1 migriert ihn nicht.

Der [8689er-P54C-DE-/I/O-Debugbaustein](hardware/P54C-DEBUG.md) und die [gemeinsame POP-Speicherkorrektur](hardware/X86-POP-MEMORY.md) erhalten 38 Typen/751 Mitglieder/32 EreigniskÃƒÂ¶rper. DE/DR7 und aufgeschobene Debugursachen setzen im tatsÃƒÂ¤chlichen ausgewÃƒÂ¤hlten P54C-Gast fort; beschÃƒÂ¤digte vorbereitete CPU-ZustÃƒÂ¤nde mit ungÃƒÂ¼ltigen CR4-/DR7-Festbits werden abgewiesen. Acht aktuelle DOS-/EMM386-Fortsetzungen prÃƒÂ¼fen die gemeinsame 486-Korrektur mit vollen PC-/Mediencontainern. Der ganze 8371er-Starter ist archiviert; sein eigener alter Import wird vor Zweiganlage abgewiesen. Format 1 migriert keine alten Builds. Der ausgewÃƒÂ¤hlte P54C-Gast ist weiterhin kein voller P54C-PC. Der gesonderte eingefrorene 8371er-Auditbefund zu massenhaft stornierten PIT-Ereignissen und der Save-/Load-GrÃƒÂ¶ÃƒÅ¸engrenze bleibt offen; diese CPU-Korrekturen beheben ihn nicht.

Der [8870er-Auditfolgebuild](AUDIT-FIXES-2026-10-27.md) behebt den gesondert genannten stornierten-PIT-Queuefehler: begrenzte Kompaktierung wÃƒÂ¤hrend normalem Cancel erhÃƒÂ¤lt lebende Time/Id-Reihenfolge und Tokenfolge; SaveState und LoadState prÃƒÂ¼fen dieselben Clock-/Token-/100000-Eintragsgrenzen. Zu groÃƒÅ¸e lebende Queues werden vor Payloadkodierung/VerÃƒÂ¶ffentlichung abgewiesen; Erfassung selbst mutiert keine Queue. Zwei tatsÃƒÂ¤chliche 120000-OUT-PIT-PCs speichern/laden vollstÃƒÂ¤ndig und behalten Zukunftstimer. Weitere 26 tatsÃƒÂ¤chliche DOS-/Audio-/Doom-Fortsetzungen erhalten den Produktzustand. 38 Typen/751 Mitglieder/32 EreigniskÃƒÂ¶rper bleiben unverÃƒÂ¤ndert. Die neue BuildidentitÃƒÂ¤t migriert alte 8689er-Container nicht. Abgewiesene Eingabepakete bleiben Caller-Aufgabe; alte Hostereignisse werden nicht durch verdeckte Retryqueues in eine restaurierte Generation eingespielt. Native Bedienabnahme bleibt offen.


Der [8942er-P54C-PC-Nachweis](hardware/P54C-PC.md) ergÃ¤nzt erstmals volle Produkt-PC-/Medienfortsetzungen des benannten 90-MHz-Teilmodells unter tatsÃ¤chlichem DOS/EMM386 und DOS4GW. Modell/Frequenz/RAM/Scheduler werden vor Branch-VerÃ¶ffentlichung gegen die bereits geschÃ¼tzte Medienprofildefinition geprÃ¼ft; die CPU besitzt nachweislich ihren eigenen Scheduler. GÃ¼ltige vollstÃ¤ndige TSC-Epochen bleiben Werte, unbekannte Quellen zusÃ¤tzliche Felder werden abgewiesen. ZwÃ¶lf neue tatsÃ¤chliche semantische Importablehnungen erhalten die bisherige Session. Alle sechs Medienpositionen sind am neuen Profil geprÃ¼ft. 38 Typen/751 Mitglieder/32 EreigniskÃ¶rper und Format 1 bleiben erhalten; ein echter eigener 8870er-CLI-Import wird ohne Zweiganlage abgewiesen. Positive DOS-RÃ¼ckkehr und begrenzte Original-EMM386-Monitorfortsetzungen werden im Nachweis getrennt gezÃ¤hlt.
