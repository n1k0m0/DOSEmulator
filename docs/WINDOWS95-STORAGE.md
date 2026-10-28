# Windows 95: CD-Lesen und VFAT-Dateioperationen

Stand: 28. Oktober 2026. Zusätzlicher tatsächlicher Gastnachweis auf dem unveränderten **V12-/12589er-Build**, bei festen **100/133 MHz und 32 MiB**. Die bereitgestellte deutsche OEM-Revision-A-/950-Familie bleibt von ursprünglichem RTM und der gesamten M4-Abnahme getrennt. Der private Gesamtnachweis steht in `artifacts/tests/m4-win95-storage-release-path.txt`.

## Eigene Dateien durch originale Windows-APIs

Das neue eigene [DOSEIO95-Programm](../tests/guest/windows95-storage-probe.asm) ist ein klassisches PE32 mit 19 KERNEL32-Imports. Es läuft ausschließlich im Gast, ohne GUI, Audio oder asynchrone Arbeit. Die [Medienvorbereitung](../scripts/create-windows95-storage-media.py) liefert eine schreibgeschützte FAT12-Probediskette sowie eine ISO9660-CD mit ausschließlich eigenem 64-Byte-Marker und **262157 Nutzbytes**. Die ISO besitzt 160 echte 2048-Byte-Blöcke, kein Joliet und keine Betriebssystemdateien.

In zwei frischen privaten HDD-Zweigen wird Windows tatsächlich kalt gestartet. Das Programm wird normal über virtuelle Gasttasten von A: aufgerufen. Beide Taktraten bestehen:

- `GetLogicalDrives`/`GetDriveTypeA` und vollständige Markersuche; tatsächlich gefundenes CD-Laufwerk **R:**.
- Neue Anlage von `C:\DOSEIO`, echte ReadFile-/WriteFile-Kopie nach `C:\DOSEIO\Long File Name 95.bin`, Flush, Close, Reopen und vollständiges erneutes Lesen aller **262157 Bytes**.
- Fünf tatsächliche Seek-Aufrufe und vier Slice-Lesevorgänge, einschließlich Übergängen über 2048 und 65536 sowie EOF. Eine fehlende Datei liefert wirklich Fehler 2.
- CD-Schreibablehnung: Der tatsächliche GENERIC_WRITE-Open liefert auf beiden Takten `FFFFFFFFh` und Fehler 5. Es wird kein nicht ausgeführtes WriteFile als beobachtet ausgegeben. Der Gast und Prüfer beherrschen zusätzlich den ausdrücklich getrennten Fall eines erfolgreichen Open mit anschließend abgewiesenem tatsächlichem WriteFile.
- Schreiben einer eigenen 197-Byte-Schwesterdatei, Rename, Enumeration und Delete; die vollständige Hauptdatei bleibt erhalten.

Pro Lauf bestätigt der unabhängige Dateiprüfer **66 tatsächliche APIrecords**, neun Quell-Lese-, neun Kopier-Schreib- und neun Vollreadback-Aufrufe, **36 Guardchecks ohne Fehler**, Terminal 1 / Phase 99 / Flags `00003FFF` / Sequenz 7. Der SHA-256 der vollständigen Hauptdatei ist `C60379B0720AB0EB747D6F85EDF31F6237CFC347501868EB864CF8B245DC95C0`, FNV-1a `0C078EBE`.

Ein eigener FAT16-Leser prüft beide identischen FAT-Kopien, die vollständige Clusterkette und **zwei wirkliche VFAT-LFN-Einträge** für den exakten langen Namen. Ordinalreihenfolge, Prüfsumme, Typ, Nullcluster und UTF-16-Abschluss/Padding werden geprüft. Der tatsächlich vergebene Kurzname `LONGFI~1.BIN` wird aus dem Verzeichniseintrag abgeleitet und an die echten WIN32_FIND_DATAA-Bytes gebunden; er ist keine allgemeine Aliaszusage. Hauptdatei, eigener 4096-Byte-Proof und Verzeichnis verwenden 19 disjunkte eigene Cluster. Die Prüfer lesen nur diese ausgewählten eigenen Dateien auf versiegelten privaten HDDs.

Die Slicebytes selbst stehen nicht im Proof. Ihre vollständigen Vergleiche sind durch den eingefrorenen tatsächlich ausgeführten Gastcode und rohe Positions-/Count-/Handle-Ergebnisse gebunden; der externe Prüfer vergleicht zusätzlich die gesamte persistierte Hauptdatei. Der vorherige abgeschlossene Proofwriter ist belegt. Ein ExitProcess-Rückgabewert oder neuer Windows-Shutdown wird daraus nicht abgeleitet.

## Vollständige Fortsetzung während des CD-Lesens

Je Lauf wird bei der wirklichen eigenen Kopie ein vollständiger Snapshot erfasst: frische aktuelle App-CR3 `00301000h`, eigene Phase 2 / Terminal 0 / Offset 0, tatsächliches READ(10) von **LBA 22, einem Block**, fertig übertragener 12-Byte-CDB und noch zukünftiges Geräteereignis. Capture erfolgt erst nach Rückkehr des normalen Owner-Schritts, außerhalb aller Port-/IRQ-Callbacks. Für diesen Nachweis wurde der CDB12-Schnitt tatsächlich erreicht; der ebenfalls vorbereitete DataIn-Kandidat wird nicht zusätzlich als ausgeführter Schnitt gezählt.

| CPU | Ganze CPU-/32-MiB-RAM-/VRAM-/Geräte-/Schedulervergleiche | Schritte pro Seite | Tatsächliche virtuelle Fortsetzung | Gleiche Stereo-Frames pro Seite |
| --- | ---: | ---: | ---: | ---: |
| 100 MHz | 18 | 40000000 | 1,600000000 s | 76800 |
| 133 MHz | 18 | 40000000 | 1,203007519 s | 57600 |

Beide Original-/Restaurierungsseiten bestehen die vollständigen Datei-/API-/VFAT-Prüfungen auf ihren echten Endmedien. Keine Zustandsfelder werden ausgeschlossen; nur ausdrücklich validierte Capture-Identitäten werden für Medienvergleiche kanonisiert. Es wird keine Zustandsgleichheit zwischen den Taktraten behauptet. Die Stereo-Frames dieses Storage-Schnitts sind tatsächlich Nullsamples; das ist eine Zustands-/Publikationsprüfung und kein zusätzlicher aktiver Soundnachweis.

Die Originale laufen danach noch bis zur festgelegten Grenze weiter und veröffentlichen je einen zusätzlichen finalen Mediensiegelstand; auch diese Enddateien bestehen. Pro Takt zählen genau ein Kaltstart und eine volle Restaurierung. Je Lauf werden insgesamt 1,96 Milliarden Original- und 40 Millionen restaurierte Schritte ausgeführt. Das sind begrenzte Gasttests, kein allgemeiner Langzeit- oder Leistungsnachweis.

Die letzte während der Beobachtung noch lesbare App-RAM-Metadatenstelle zeigte Phase 6. Diese wird nach späterer Entabbildung nicht als Fehler oder Abschluss umgedeutet. Die endgültigen tatsächlich geschriebenen Dateien belegen unabhängig Phase 99 / Terminal 1. Ebenso ist das im ersten `scope.json` als `{}` serialisierte ValueTuple kein Laufzeitgrenzenbeleg; die tatsächlichen Schnittberichte nennen LBA-Grenzen 22 bis ausschließlich 151 explizit und sind an die vollständige eigene ISO gebunden.

## Beobachteter CD-Treiberweg und Grenzen

Die vorbereiteten Startdateien enthalten den eigenen DOSECD.SYS und originales MSCDEX. Tatsächlich erfolgen die 129 lückenlosen Payload-READ10-Anforderungen für LBA 22–150 und ihre **132096 Stage2-PIO-Wortlesevorgänge im V86-Modus bei CPL 3**. Der CD-Zugriff dieses Nachweises benutzt damit beobachtete V86-I/O-Arbeit; eine native CDFS-/IOS-Modulzuordnung ist weiterhin nicht nachgewiesen. IRQ15-ACKs und die gezielt erfassten passenden Vektornachrichten sind null; daraus wird keine zusätzliche Treiber- oder Interruptfolge erfunden.

Insgesamt werden pro Lauf 133 vollständig konsumierte CD-Blöcke und 273458 nützliche PIO-Antwortbytes beobachtet. Darin stecken **272384 echte gekochte CD-Lesebytes**; Identify-/Sense-/sonstige Antwortbytes werden nicht als weitere CD-Nutzbytes bezeichnet. Die vollständige Ziel-Dateiprüfung zählt unabhängig genau 262157 Dateibytes. Native CDFS/IOS sowie die passenden ursprünglichen IDE-/CD-Treiberpfade bleiben nächste konkrete M4-Arbeit.

Alle 65 gemeinsamen und 65 eingefrorenen V12-Produktdateien bleiben unverändert; keine neue VM-Buildidentität und kein erneuter Vollsuite-Lauf daraus. Die **12589er-Regressionsabnahme** bleibt erhalten. Zwei eigene private Harnessbuilds bestehen ohne Warnungen/Fehler, fünf tatsächliche VM-Module werden vor/nach jedem Lauf geprüft. 66 synthetische Negativoracles der unabhängigen ISO-/FAT-/LFN-/Proofprüfer bleiben separat gezählt, nicht als neue registrierte Regressionen. Die Quellprüfung umfasst jetzt 832 Code-/Projektdateien, darunter 404 C#, 163 Python und 102 PowerShell, ohne Probleme.

Originalmedien, persönliche Spiel-COWs und die ausgelieferten Benutzer-PCs werden nicht geöffnet oder verändert. Keine Hosteingaben, Fenster, Fokusversuche, Aufnahmen oder Endpunkte; keine C64-Änderungen, Commits oder Pushes. Die verschobene native Maus-/Repeat-/GL-Prüfung bleibt offen. M4–M8 und der stündliche Gesamtauftrag bleiben aktiv.
