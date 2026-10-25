# 486-Debugregister, Breakpoints und Einzelschritt

Stand: 25. Oktober 2026. Eigenes M3-Teilpaket nach [LOCK/Alignment](X86-LOCK-ALIGNMENT.md). Gemeinsamer Release-Build: **1.458/1.458 Regressionen, 0 Warnungen/Fehler**, darunter **122 neue CPU-Fälle**. M3 insgesamt bleibt offen; insbesondere arithmetisches 80-Bit-x87 und die übrigen Geräte/Snapshots fehlen.

## Registervertrag des Referenzmodells

`MOV DR` über 0F21/23 überträgt immer 32 Bit, auch in 16-Bit-Code und mit Größenpräfixen. Die Mod-Bits werden ignoriert; der Decoder konsumiert keinen SIB, Displacement oder Datenoperanden. CPL0 und Real Mode sind erlaubt, andere Ringe und V86 liefern #GP. Der frühe 486 besitzt kein CR4.DE: DR4/DR5 werden als Aliase von DR6/DR7 geführt. Daraus folgt keine Pentium-/I/O-Breakpoint-Erweiterung.

DR0–3 speichern lineare Adressen. DR6 hat die festen Referenzbits FFFF0FF0h und die schreibbaren B0–B3/BD/BS/BT. Status bleibt bis zum Software-Schreiben erhalten. DR7 besitzt Bit 10 fest eins, Bit 12 wie beim 486 schreibbar und die lokalen/globalen Freigaben, LE/GE, GD sowie RW/LEN. LE/GE verändern die feste genaue Datenzuordnung dieses Modells nicht. Hardware-Taskwechsel löschen lokale Freigaben einschließlich LE, behalten globale Freigaben und Adressen; die bestehende TSS-T-Bit-Funktion setzt weiterhin BT.

RW=00 prüft den ersten Präfix-/Befehlsbytepunkt, RW=01 Schreibzugriffe und RW=11 Lese-/Schreibzugriffe. Datenlängen sind 1/2/4; die unteren DR-Adressbits werden entsprechend maskiert, und jede Überlappung mit dem tatsächlich angesprochenen Operanden zählt. Die reservierten 486-Formen RW=10 und LEN=10 erhalten keine neue Fähigkeit; sie erzeugen im Referenzmodell keinen Vergleichstreffer. Undefinierte Kombinationen sind keine allgemeine Hardwarezusage.

GD löst vor der Registerübertragung #DB/BD aus. In den geprüften Privilegkonflikten prüft der Decoder CPL vor GD; ungültiges LOCK erzeugt zuvor #UD. Jeder Debug-Eintritt löscht GD, sodass der Handler Register bearbeiten kann. Reset löscht Registeradressen, Schattenzustände und Diagnosezähler und setzt DR6/DR7 auf FFFF0FF0h/400h.

## Fault, Trap und Befehlsfortsetzung

Ausführungs-Breakpoints liefern einen **Fault vor Ausführung**: alter EIP und alte Nutzregister. Die geprüfte Implementierung validiert den ersten Codezugriff mit Segmentierung/Paging, bevor sie den Breakpoint vergleicht. Damit sind keine allgemein siliziumgenauen Prioritäten gegen jeden konkurrierenden Fetch-/Debugfehler zugesagt. Beim 486-Referenz-#DB wird RF im Rahmen nicht künstlich gesetzt; der eigene Handler setzt RF gezielt vor IRETD. Der wiederholte Befehl wird einmal ausgeführt, anschließend wird RF für gewöhnliche abgeschlossene Befehle gelöscht.

Daten-Breakpoints liefern einen **Trap nach erfolgreicher Ausführung**: gespeicherter nächster EIP, veränderte Daten und fortgeschrittene Register. RF unterdrückt diese Daten-Traps nicht. Seiten-/Segment-/Alignmentfehler verhindern dagegen den Trap des noch nicht abgeschlossenen Befehls. Debug-Traps laufen außerhalb der normalen Fault-Restaurierung; sie setzen keine schon erfolgte Änderung auf den vorherigen Checkpoint zurück. Tatsächliche Operanden werden beobachtet, implizite Supervisor-Tabellenzugriffe, MMU-Seitentabellen und physische DMA-/Hostbuszugriffe nicht.

TF wird vor dem Befehl gelesen. Ein TF-setzendes POPFD trappt erst nach dem folgenden Befehl; ein TF-löschendes POPFD kann noch den zuvor angeforderten Trap erzeugen. Daten-Treffer und BS werden im selben Rahmen zusammengeführt. INT/INTO/INT3 lösen keinen zusätzlichen Einzelschritt am neuen Handler-Eintritt aus. Ein vom neuen TSS geladenes TF wirkt erst nach dessen erstem Befehl. Die bisherige TSS-T-Bit-Teilfunktion bleibt davon getrennt.

MOV/POP SS verzögern die geprüften Daten-/TF-Ereignisse bis hinter den folgenden Befehl. Dessen Ausführungs-Breakpoint wird unterdrückt, sodass der neue ESP vor dem Handler benutzt wird. Ein POP-SS-Lesetreffer bleibt bis dahin erhalten. Für REP wird pro wirklich abgeschlossenem Element geprüft: Zähler und Zeiger sind fortgeschritten, der Rahmen zeigt bei verbleibenden Elementen auf den ersten Präfix, und RF erlaubt die Fortsetzung ohne erneuten Ausführungs-Fault. Ein Daten-Trap nach dem letzten Element zeigt auf den nächsten Befehl.

Die Headless-Diagnose enthält DR0–3/6/7 sowie getrennte Zähler `InstructionBreakpointFaults`, `DataBreakpointTraps`, `SingleStepTraps` und `GeneralDetectFaults`; TSS-T-Bit-Traps behalten ihren eigenen Zähler. Diese Werte sind Diagnose, keine historischen Taktmessungen.

## Eigene DOS-Probe

`tests/guests/cpu-debug-test.asm` läuft als **CPUDEBUG.COM**, geladen vom echten DOS-Kernel. Die Probe besitzt private Descriptoren, Interrupt-TSS/Stacks und optional eigene klassische 4-KiB-Identitätsseiten. Sie sichert/restauriert auch die ursprünglichen Debugregister. Der Debughandler deaktiviert während seiner Datenauswertung die Adressvergleiche; andernfalls würde sein Lesen der beobachteten Variable selbst rekursiv trappen.

```powershell
.\scripts\test-cpu-debug.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

**Acht Kaltstarts** prüfen MS-DOS 6.22 und FreeDOS jeweils zweimal mit/ohne Paging. Pro Start: ein GD-Fault, ein Ausführungs-Fault mit unverändertem EBX, drei Daten-Traps, zwei TF-Traps und ein wirklicher #GP für den Registerzugriff bei CPL3. Die SS-Probe aktiviert zugleich einen Ausführungs-Breakpoint am unmittelbar folgenden ESP-Befehl. REP beweist einen übertragenen ersten und nach IRETD korrekt fortgesetzten zweiten Doppelwortwert. Mit Paging folgt ein weiterer Daten-Trap, nachdem ein echter #PF dieselbe Instruktion repariert hat; RF verhindert diesen Daten-Trap nicht.

Der unabhängige Prüfer vergleicht Opcodebytes, alle sieben/acht tatsächlichen Debugrahmen, Daten/Register/Flags, zwei Stackwächter, TSS und sämtliche 1.024 PTEs. **278 verfälschte Nachweise** werden verworfen. Wiederholungen sind zeit-, bild- und nachweisgleich. CR0/CR3, GDTR/IDTR/PIC, DOS-Stack und Debugregister werden wiederhergestellt; der Gast erreicht originales VER/ECHO/Prompt. **Der private TR bleibt bis Reset geladen.** Das ist kein vollständiger CPU-Snapshot und keine direkte EMM386-Abnahme dieses privaten PE-Gastes.

## Originales Microsoft DEBUG.EXE

Eine zweite Prüfung startet das **unveränderte lokale `C:\DOS\DEBUG.EXE`**, SHA-256 `091B2A580FC37CC689E69824218F1C38D80F316EECAAA30D3517DFD70C95B437`. Keine Ersatz-EXE und kein Host-Debugger. Die Eingabe programmiert ausschließlich fünf eigene RAM-Bytes und führt originale T-/R-/Q-Kommandos aus:

```text
e 100 b8 34 12 40 90
t=100
t
r
q
```

Die beiden Einzelschritte liefern **AX=1234/IP=0103**, danach **AX=1235/IP=0104**, mit korrektem Paritätswechsel und originaler Disassemblierung INC AX/NOP. Je zwei Kaltstarts ohne Manager, mit originalem EMM386 NOEMS und mit RAM: **sechs Starts, zwölf tatsächliche TF-Traps**. FAT-/SHA-Prüfung weist die Originaldatei nach; Roh-RAM mit gegebenenfalls echter CR3-Übersetzung enthält die eigenen Befehle. **24 Verfälschungen** scheitern. Die privaten COWs bleiben byteidentisch.

```powershell
.\scripts\test-msdos-debug.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow> `
  -ConfiguredDisk <private-configured-base.img> `
  -NoEmsOverlay <verified-NOEMS.cow> -RamOverlay <verified-RAM.cow>
```

Ohne die drei letzten Angaben prüft das Skript zwei normale Kaltstarts. Die zusätzlichen Medien müssen bereits geprüfte private DOS-Konfigurationen sein. Der Monitor kann beim Berichtszeitpunkt nach der ausgegebenen DOS-Kommandozeile selbst bei CPL0 stehen; der Prüfer verlangt daher tatsächliche V86-Ein-/Austritte, konsistente Modus-/CPL-Zähler, aktive Originalkonfiguration und übersetzte Nutzdaten, keinen zufälligen V86-Schnappschuss.

## Quellen und verbleibende Grenzen

Historische Grundlage: [Intel486 Programmer’s Reference Manual, 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 9/11 und MOV-Spezifikation. Ergänzend [Intel486 Developer’s Manual, 1997](https://bitsavers.trailing-edge.com/components/intel/80486/273021-001_Embedded_Intel486_Processor_Family_Developers_Manual_199710.pdf), die ausdrücklich rückwärtskompatiblen DR4-/DR5-/Mod-Verträge im [Intel SDM, Band 2B](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf) sowie der [Intel-Vergleich der Prozessorgenerationen](https://cdrdv2-public.intel.com/868136/252046-081-sdm-change-document.pdf), insbesondere das schreibbare 486-DR7-Bit 12. Neuere CR4-/I/O-/64-Bit-Funktionen werden nicht übernommen.

Nicht allgemein abgenommen: sämtliche gleichzeitigen Debug-/Fetch-/Task-/Fault-Kombinationen, GD innerhalb eines SS-Schattens, wiederholte SS-Ladevorgänge, NMI/ICE/Buspräzision, alle impliziten Zugriffe/Statusbitkombinationen oder jeder Original-Debuggerbefehl. Die Tests belegen die beschriebenen Referenzpfade, keine vollständige Debugger-, Silizium-, DOS-Extender- oder Windows-Kompatibilität. Keine nativen Fenster/Fokusversuche/Windows-Eingaben wurden dafür gestartet; die verschobene Mauswiederholung bleibt unberührt. Alle Dumps und Betriebssystem-/Spielmedien bleiben privat und ignoriert.

Das nachfolgende [x87-Zustandspaket](X86-X87-STATE.md) prüft inzwischen acht tatsächliche 80-Bit-Register, Stack/Tags/Umgebung und Save/Restore; [elementare Arithmetik und Konvertierungen](X86-X87-NUMERIC.md) besitzen inzwischen ebenfalls eigene Zahlen-/DOS-Nachweise. Nächste Vertikale ist die FERR-/IGNNE-Fehlerleitung, danach verbleibende x87-Operationen. Host-double ist kein Präzisionsersatz. Weitere CPU-Präzision und M3-Geräte/Snapshots sowie M4–M8 bleiben offen.
