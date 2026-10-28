# Audit erneut geprüft: zwei ergänzende Korrekturen

Stand 27. Oktober 2026. Der erneut gelesene Chat „Emulator-Funktionen zusammenfassen“ bestätigt die sechs ursprünglichen Audit2-Korrekturen. Auf einer Kopie des akzeptierten 11529er-Starters wurden nochmals dieselben 694 Regressionen und alle 20 ursprünglichen Probezeilen erfolgreich ausgeführt. Die beiden mathematischen Prototypproben bleiben Äquivalenzprüfungen; es wurde keine neue Performance gemessen. Drei Agenten prüften getrennt CPU, Zustand/Medien/IPC und Eingabe/Audio; die Produktänderungen wurden unabhängig gegengeprüft.

Die zusätzliche Prüfung fand zwei neue Randfälle. Die alten Auditberichte und ihre historischen Abnahmen bleiben unverändert.

| Neuer Befund | Reproduktion und Korrektur |
| --- | --- |
| PIO-Snapshot mit widersprüchlicher aktiver Sektorphase | Ein echtes WRITE-DRQ wurde im eigenen, vollständig neu gehashten Container auf `remaining=0/lba=mediumEnd` geändert. Der 11529er-Import nahm es an; 256 wirkliche Datenportwörter und der folgende Commit lösten `ArgumentOutOfRangeException(lba)` aus. READ erzeugte `remaining=-1/lba=9/SC=FF`; rohe Zustandserfassung blieb möglich. Aktive PIO-Daten und vorbereitende Callbacks werden jetzt gemeinsam an Zähler, Flags, Buffer, Token, zukünftige Frist und versiegelte Mediengrenzen gebunden, bevor Medien geöffnet oder Zweige angelegt werden. |
| Native Wiederholung belebt verworfene gehaltene Taste | Nach dem tatsächlichen `ResetKeyRepeat(true)` behandelte der kompilierte alte KeyDown-Handler ein `IsRepeat=true` als frischen Down: zwei Downcallbacks, eine neu gehaltene Taste. Native Wiederholungsereignisse werden jetzt vor der Heldset-Verarbeitung verworfen. Der eigene monotone 500-/50-ms-Timer bleibt zuständig. |

Gültige IDENTIFY-Historie mit alten LBA-/Restzählern, abgeschlossene One-past-LBA, geänderte sichtbare Taskregister neben gelatchten Transfercursors, vollständige wartende Schreibcommits und Busy-READ mit späterem regulärem IDNF bleiben erhalten. Die Gerätelogik und ihre Callbackkörper wurden nicht geändert.

## Tatsächliche Prüfung

**11563/11563 Regressionen, 34 neue Fälle, beide Release-Builds ohne Warnungen oder Fehler.** 28 neue PIO-Fälle: auf den unveränderten fünf 11529er-VM-Modulen 15 PASS/13 FAIL, danach 28 PASS. Darin 13 volle rehashte Fehlercontainer mit je direkter und Owner-Ablehnung vor einer exklusiv gesperrten eigenen Basis, zwei dynamische alte Fehlerzeugen und 13 positive Kontrollen. Der alte Owner, RAM, Revision und Eingabegeneration bleiben nach Ablehnung erhalten; Ping und weitere echte CPU-Instruktionen funktionieren.

Sechs neue Eingabefälle führen die tatsächlichen kompilierten privaten KeyDown-/KeyUp-/Reset-Handler aus: vorher 4 PASS/2 FAIL, danach 6 PASS. Ein dynamischer verwalteter Ableger wird ohne Fensterkonstruktor alloziert und sofort von Finalisierung ausgenommen. Der einzig erlaubte OpenTK-Basisinitialisierer wurde aus PE-Metadaten exakt auf zwei parameterlose BCL-Collection-Zuweisungen geprüft; unbekannte Initialisierer bleiben gesperrt. Keine Fenster, Run-/Dispose-/Fokus-/GLFW-Aufrufe oder nativen Windows-Eingaben. Das ist Callbackprüfung, keine native Bedienabnahme und keine allgemeine Prozesssandbox.

Gezielt wegen der Importänderung erneut acht ATAPI-DOS-/Original-EMM386-Starts mit 16 vollen 32-MiB-Fortsetzungen und acht alte 486-Starts mit acht vollen 8-MiB-Fortsetzungen: **16 DOS-Kaltstarts/24 Fortsetzungen, 3316 ganze Vergleichspunkte und 2632 negative Gastoracles**. Beim ATAPI-Gast erreichen alle drei PCs den ursprünglichen DOS-Prompt und einen neuen KBC-/IRQ1-/IN60-ECHO. Der alte 486-Harness behält ausdrücklich seinen AH4C-/30k-Vertrag. ISO-, PIO-, IRQ-, Datei-, rohe-x87- und Audiooracles bleiben aktiv. Dies ersetzt keinen Original-CD-Treiber-/MSCDEX- oder Windows95-Nachweis.

Der erste Gesamtlauf im isolierten Arbeitsordner hatte allein eine dort fehlende Kopie des mitgelieferten P54C-Profils: 11562/11563. Nach Kopieren exakt dieser benannten eigenen JSON-Datei besteht die Einzelkontrolle und der vollständige endgültige Lauf. Frühere Nullable-Compilerdiagnosen und der vorsorglich blockierte unbekannte OpenTK-Initialisierer der eigenen Testfixture zählen ebenfalls nicht als positive Abnahme.

64 Kandidaten-/Normaldateien bytegleich; genau acht Dateien ändern sich zum 11529er-Starter: Runtime-DLL/PDB in beiden Frontends, Tests-DLL/PDB und SharpPixels-DLL/PDB. CPU, Core, Hardware, Contracts, Headless und alle bisherigen ROM-Bytestrome bleiben bytegleich. Inventar **46 Typen/874 Mitglieder/40 unveränderte Ereignisse**. 786 lizenz-/kommentargeprüfte Code-/Projektdateien, 377 C#/155 Python/99 PowerShell. Ausschließlich benannte Quelldeltas geprüft; keine breite Schutzlistenrunde.

Format 1 bleibt an alle fünf tatsächlichen VM-Module gebunden. Die neue Runtime ändert die Snapshotidentität; ein wirklicher alter 11529er-CLI-Import wird vor Zweiganlage abgewiesen, mit dem bestehenden nichtnulligen Ausnahmeabbruch und ohne behaupteten kontrollierten Exit 1. Neue eigene CLI-Container setzen mit gleicher CPU, Bild, Zeit und ISO fort.

Privater Gesamtnachweis: `artifacts/tests/audit2-recheck-release-path.txt`. Nur die sechs bekannten eigenen DOS-Fixtures wurden in neue private Basen/COWs kopiert; persönliche Spiel-COWs und Originalmedien bleiben ungeöffnet. HEAD bleibt `e1b0d6faeb12216a572fc3d0d0b69c0e4a0c1f0a`, keine Commits/Pushes, C64-Änderungen, Hostendpunkte oder Aufnahmen. Native Repeat-/Maus-/GL-Abnahme bleibt offen.

## Offene Performanceansätze und nächster Produktschritt

Decoderfetch, Audiofristen, nichtvibrierende FM-Phasenschritte, gemeinsame FSINCOS-Reduktion sowie Hash-/Export-/JSON-Arbeit bleiben getrennte Profilaufgaben. Der größere Fetchpfad muss auch Speicher-/MMIO-/A20-/Mappingwirkungen innerhalb desselben Steps berücksichtigen. Es gibt dafür keinen neuen Durchsatz- oder Doom-FPS-Nachweis. Die bereits abgenommenen Integer-/VGA-Optimierungen bleiben erhalten.

Der nächste konkrete M4-Produktschritt bleibt DOS-CD-ROM-Treiber mit originalem MSCDEX und echten Dateizugriffen, passende Firmware/Manager und anschließend die tatsächliche Win95-RTM-Installation mit mehreren Starts, stabilen Geräten und regulärem Shutdown. Vollständige Chips, 133 MHz und M4–M8 bleiben offen; der stündliche Auftrag bleibt aktiv.
