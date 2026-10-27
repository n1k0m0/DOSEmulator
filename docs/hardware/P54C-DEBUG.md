# P54C: CR4.DE, Debugregister und I/O-Breakpoints

Stand: 27. Oktober 2026. Interner M4-Teilumfang des ausdrücklich clockgebundenen `IntelP54cB5Subset`. Die vollständige Pentium-/430FX-/Windows-95-Plattform bleibt offen. Alle auswählbaren Produkt-PCs bleiben 486; die quellenlosen älteren Konstruktoren behalten ihre bisher engere CPU-Teilmenge.

## Historischer Primärvertrag

Grundlage: Intel, [Pentium Processor Family Developer’s Manual, Volume 3, 241430-004, Juli 1995](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf). CR4: 10-8/PDF275; Fehlerprioritäten: 14-5/6/PDF364/365; Debugregister und Portvergleich: 17-4/5/PDF441/442; I/O-Traps und REP: 17-8/PDF445; Generationenunterschiede: 23-14/PDF575; CPUID: 25-75/PDF692; MOV DR: 25-229/PDF846. Die unveränderte lokale Primärkopie und ein getrennter Agentenreview sind im privaten Gesamtnachweis gehasht.

CR4.DE ist Bit 3. Bei DE=0 werden DR4/DR5 auf DR6/DR7 abgebildet; bei DE=1 erzeugen beide #UD. Der ungültige Befehl gehört zur höheren Prioritätsklasse als GD und #GP und wird deshalb vor CPL/GD geprüft. LOCK bleibt davor ungültig. CPL gegen GD bleibt die vorhandene eigene Politik: zuerst CPL. Intel bezeichnet die Reihenfolge innerhalb dieser gemeinsamen Prioritätsklasse als implementierungsabhängig; keine vermessene B5-Antwort wird behauptet.

P54C hält DR7-Bits 11/12/14/15 auf null und Bit 10 auf eins. Die Schreibmaske ist FFFF23FFh, anschließend OR 400h. Der 486 behält seine bisherige schreibbare Bit-12-Antwort und seine DR4-/DR5-Aliase. DR6, lokale/globale Freigaben, GD, Taskgrenzen und die bestehenden Daten-/TF-/SS-Zustände bleiben erhalten. MOV DR überträgt unabhängig von Größenpräfixen 32 Bit und konsumiert keinen SIB-/Displacement-Operanden.

## Tatsächliche I/O-Wirkung und Grenzen

Bei DE=1 vergleicht RW=10 CPU-IN/OUT sowie INS/OUTS nach dem tatsächlichen Portzugriff. Die 16-Bit-Portadresse wird auf 32 Bit nullerweitert; hohe Debugadressbits werden nicht abgeschnitten. LEN=1/2/4 richtet die Vergleichsadresse an ihrer Länge aus; überlappende tatsächlich übertragene Bytes zählen. Speicher-RW=01/11 und I/O-RW=10 bleiben getrennt. Host-/Gerätebuszugriffe erzeugen keine CPU-Debugtraps.

Erst nach erfolgreichem Commit erzeugt die gemeinsame Debugpipeline #DB. IN hat seinen Registerwert übernommen, OUT den Port erreicht, INS/OUTS Speicher und Zeiger geändert. REP hält pro beendetem Element an, erhält den Fortschritt und setzt bei übrigen Elementen am ersten Präfix fort. Rechte-, Bitmap-, Segment-, Seiten- und LOCK-Fehler sowie REP mit Zähler null erzeugen keinen vorgetäuschten I/O-Abschluss. TF/RF, Daten-/I/O-Kombinationen und SS-Deferral benutzen die vorhandene Fehler-/Trap-/Restartmechanik. Der bestehende Diagnosezähler `DataBreakpointTraps` zählt auch diese I/O-Traps; er ist keine historische Taktangabe.

RW=10 bei DE=0 und LEN=10 erhalten im begrenzten DOSE-Vertrag keinen Treffer; ihre Antwort wird nicht als vermessenes Siliziumverhalten ausgegeben. Bei breiten Zugriffen nahe FFFFh zählt der Komparator nullerweiterten Start plus Byteindex ohne erneuten 16-Bit-Überlauf. Diese Randpolitik ist kein Nachweis elektrischer Busaufteilung. Die zusätzliche historische P54C-Verzögerung von REP-MOVS/STOS-Speicherbreakpoints bleibt außerhalb dieser I/O-Vertikale. Keine universelle Debugger-/Task-/NMI-/ICE-/Bus-/Faultprioritäts- oder Siliziumpräzision.

## Modell und Zustand

CPUID meldet für den clockgebundenen internen Kern jetzt **0135h**: FPU, I/O-Breakpoints, TSC, TSC-MSR-Schnittstelle und CX8. Das ist keine Freigabe der übrigen P54C-MSRs, VME/PVI/PSE/MCE oder eines vollständigen Pentium-PCs. CR4 akzeptiert allein die implementierten TSD/DE-Bits (Maske 0000000Ch). Andere Bits bleiben unimplementiert; daraus folgt keine Behauptung, sie seien historisch sämtlich reserviert. Quellenlose alte Konstruktoren bleiben bei 0101h ohne CR4/TSC/MSR/DE.

CR4, DR0–3/6/7, Akkumulatoren und aufgeschobene Debugursachen liegen bereits im expliziten Rohzustand. Neue Felder, Timer und Schedulerereignisse sind nicht nötig. P54C-Importe mit unzulässigen DR7-Festbits oder nicht implementierten CR4-Bits werden vor Freigabe des vorbereiteten Zustands abgewiesen. CPU-Modell und virtuelle Frequenz bleiben feste Invarianten. Format 1 bleibt an denselben VM-Build gebunden; ältere Sicherungen benötigen ihren archivierten Starter.

## Endgültige Nachweise

**8689/8689 Regressionen, normaler Build ohne Warnungen/Fehler.** 232 neue Debugfälle und 86 gesonderte [POP-Speicherfälle](X86-POP-MEMORY.md). Real/PE16/PE32/Paging/V86, lokale/globale Slots, volle Debugadressen, echte Byte-/Wort-/Dword-Portwirkungen, IOPL/TSS-Bitmap, negative Rechte-/Seiten-/Segment-/LOCK-Fälle, REP-Fortschritt, TF/RF/SS, Reset und acht gezielt beschädigte vorbereitete CPU-Zustände sind geprüft.

**Vier endgültige eigene P54C-Resetstarts und vier tatsächliche ausgewählte Fortsetzungen**, 33/90 MHz jeweils zweimal: 36 tatsächliche I/O-Vorgänge, 28 I/O-Debugtraps, vier GD-Fault-/Retryfolgen, vier DE+GD-#UD-Rückkehrvorgänge, vier CPL3-I/O-#GP-Rückkehrvorgänge und 16 physische PIT-IRQ0-Handler/EOIs. Der Gast prüft zusätzlich acht tatsächliche korrigierte POP-Speicherwirkungen. Er besitzt private Descriptoren/TR und unveränderte rohe 80-Bit-Pi-Daten.

Schnitt bei HLT mit DE, GD, aktivem RW10-Vergleicher, laufendem PIT, zukünftigem Ereignis und nichtnulligem Taktrest. **2096 weitere vollständige ausgewählte CPU-/PIC-/PIT-/Scheduler-/ganze 2-MiB-RAM-Vergleiche**; alle zukünftigen CPU-/Port-/Timer-/IRQ-Publikationen sind Original/Restaurierung identisch. Zwei Wiederholungspaare sind RAM-/Zustands-/Zeit-/Publikationshashgleich. Der unabhängige Prüfer rekonstruiert **17172 Ledger-Einträge**, Portbreiten/Daten, Fault-/Traprahmen, tatsächliche Rückkehr/REP-Zeiger und PIT-Fristen ganzzahlig. **728 verfälschte Gastoracles scheitern.** Dies ist ein ausgewählter Komponentengraph, kein P54C-DOS-Start oder voller PC-/Mediencontainer.

Getrennt bestehen **acht endgültige 486-DOS-Kaltstarts und acht volle PC-Fortsetzungen** unter MS-DOS/FreeDOS/originalem EMM386 NOEMS/RAM je zweimal, **1282 weitere vollständige CPU-/8-MiB-RAM-/Geräte-/Schedulervergleiche**, echte PCM-DMA und x87-DOS-Dateien. **200 weitere Negativoracles**, insgesamt **928**. Die gemeinsame POP-Korrektur gilt ausdrücklich auch für 486; ausgeführte POP-Speicherpfade werden nicht als unverändert beschrieben. 38 Typen/751 Instanzmitglieder/32 unveränderte Ereigniskörper, keine neuen Felder oder Timer.

Der gesamte 8371er-Starter und seine 754 Quellen sind archiviert. Ein echter eigener alter CLI-Snapshotimport scheitert vor Zweiganlage. Die gesonderten alten POP-Vergleiche stehen im POP-Vertrag. Sieben getestete DLLs bleiben im endgültigen Paket unverändert; Kandidat und normaler Starter sind mit allen **64 Produktdateien bytegleich**. **648 lizenz-/kommentargeprüfte Code-/Projektdateien, 133 Python-Werkzeuge und 91 PowerShell-Skripte**. 115 geschützte Eingaben/C64/HEAD unverändert; persönliche Spiel-COWs ungeöffnet. Keine Windows-Eingaben, Hostendpunkte, neue Aufnahme, Commits/Pushes oder neue Performance-/Windows-Abnahme.

Diagnosegast, erste fehlerhafte Rechte-/Flag-/Reserved-Encoding-Testfixtures und die vor der konkreten POP-Korrektur abgeschlossene 8603er-Zwischenmatrix zählen getrennt. Deren Starter bleibt für den echten negativen POP-Gast eingefroren; diese Starts werden nicht den endgültigen 8689er-Zahlen zugerechnet.

Reproduktion: `scripts/test-p54c-debug.ps1 -SkipBuild`, Regressionen über `--filter "M4 DE"`, `--filter "M4 P54C DR7"` und `--filter "CPU POP memory"`. Privater Gesamtnachweis **`artifacts/tests/m4-debug-release-path.txt`**.

Nächster konkreter M4-Schritt ist die vollständige DOSE-PC-/Profil-/Factory-/Clock-/Ownerintegration des benannten P54C-Teilmodells, mit tatsächlichen DOS-/EMM386-Starts und vollständigem SnapshotStore-Container während PCM/x87/Dateiaktivität. Kein CPU-Austausch nach Konstruktion und keine pauschale Lockerung der Profil-/Snapshotinvarianten. Danach folgen 430FX/PIIX mit Ressourcen/Zustand, S3 Trio64, ATAPI, Firmware/Manager und echte Windows-95-RTM-Installation/-Starts/-Shutdown. Unbeworbene PSE/VME/MCE oder beliebige weitere MSRs ersetzen diesen Produkt-/Gastschritt nicht. M4–M8 und der Gesamtauftrag bleiben offen.
