# 486-Hardware-Tasks und Task-Gates

Stand: 25. Oktober 2026. M3-Teilpaket nach den [Call-Gates](X86-CALL-GATES.md). **M3 insgesamt bleibt offen.** Der gemeinsame Release-Build besteht **1.139 Regressionen, 0 Warnungen/Fehler**, davon 90 neue Hardware-Task-Fälle.

## Implementierter Umfang

CALL/JMP erkennen verfügbare 16-/32-Bit-TSS-Descriptoren und Task-Gates in GDT/LDT. Direkte TSS-Ziele müssen in der GDT liegen. Direkte Zugriffe prüfen TSS-DPL gegen CPL/RPL; bei einem Gate entscheidet dessen DPL, während die Ziel-TSS-DPL keinen zusätzlichen Zugangstest bildet. Die Pointer-Offsets und reservierten Gate-Offsetfelder bestimmen den Eintritt nicht. Software-INT prüft die IDT-Gate-DPL; externe Interrupts und Fehler dürfen unabhängig davon einen Task betreten.

Die CPU speichert EIP/IP, Flags, acht allgemeine Register und Segmentselektoren im **gecachten ausgehenden TSS**. Ein 32-Bit-TSS erhält volle Register und Rückkehradressen oberhalb 64 KiB, auch wenn die CALL-Operandengröße 16 Bit beträgt. Ein 16-Bit-TSS speichert Wörter und enthält weder FS/GS noch CR3. Seine oberen GPR-Hälften sind architektonisch undefiniert; das Referenzmodell lädt sie stabil mit FFFFh. Die Gastabnahme verwendet diese oberen Hälften ausdrücklich nicht als Silizium-Oracle.

CR3, LDTR, Ringstacks, T-Bit und I/O-Basis sind statische TSS-Felder; sie werden beim Auslagern nicht durch die aktuellen CPU-Werte überschrieben. Ein neuer 32-Bit-TSS lädt CR3 bei aktivem Paging und invalidiert den TLB, auch bei gleichem CR3. Ein 16-Bit-TSS behält den bisherigen Adressraum. Ohne Paging wird ein 32-Bit-PDBR gelesen, aber nicht in CR3 übernommen. Der neue LDTR wird vor lokalen Segmentdescriptoren geladen. Die Selektoren werden vor ihrer Qualifikation übernommen; ungeprüfte beziehungsweise fehlerhafte Caches bleiben unbrauchbar.

CALL/Interrupt/Exception lassen den alten TSS busy, schreiben dessen Selektor in den neuen Backlink und setzen NT im neuen Task. JMP löst die alte Busy-Markierung, ändert keinen Backlink und übernimmt das gespeicherte NT des Zieltasks. Task-IRET liest den tatsächlichen Backlink, verlangt einen busy Ziel-TSS, löscht das ausgehende Busy sowie NT im gespeicherten ausgehenden Flags-Abbild und übernimmt den früheren Task. Es liest keinen gewöhnlichen IRET-Stackrahmen und verlangt kein CPL0. Die Ziel-CS-RPL bestimmt den neuen CPL; Tests schließen Ring 1/2/3 und konformen Code ein.

Jeder übernommene Task setzt CR0.TS. Der nächste x87-Befehl liefert deshalb #NM, bis die Gastsoftware TS freigibt. Der 32-Bit-TSS-T-Schalter liefert zusätzlich einen #DB-Trap vor der ersten neuen Instruktion und setzt DR6.BT im vorhandenen Diagnosezustand. Dies implementiert noch keine allgemeine MOV-DR-/DR7-/Breakpoint-/TF-Debugarchitektur. Fehlercodes nach einem Exception-Taskgate verwenden die TSS-Breite; der 486-Doppelwortslot bewahrt sein oberes RAM-Wort.

## Fehler vor und nach dem Commit

Typ, DPL, Present, TSS-Mindestgrenze, benötigte TSS-Lesezugriffe und ausgehende Zustands-/Busy-/Backlink-Schreibseiten werden im ausgehenden Supervisor-Adressraum geprüft. Ausgewählte #GP/#NP/#TS/#PF lassen dort den alten Task restartbar. Die Mindestgrenzen im Modell sind 67h beziehungsweise 2Bh; die 16-Bit-Struktur endet einschließlich bei Byte 43.

**Nach Übernahme des neuen Tasks werden dessen Zustand und Adressraum erhalten.** Eine interne Taskgeneration verhindert, dass Step-, IRQ- oder Exception-Checkpoints den alten EIP, ESP, CPL, die Register oder Segmentcaches wiederherstellen. Fehler beim neuen LDTR, CS, SS, ES/DS/FS/GS, bei Descriptor-Paging oder am Ziel-EIP gehören zum neuen Task. Ein weiterer Fehler während der Fehlerzustellung kann einen Double Fault in diesem Kontext auslösen.

CPU-Tests prüfen unter anderem einen neuen CR3 mit fehlender LDT-Seite, unbrauchbare neue Selektoren, fehlendes CS/SS, einen zu großen EIP, WP-Schreibschutz, einen Fehler **nach Task-IRET**, einen echten Double-Fault-Task und die Reparatur mit anschließender Task-Rückkehr. Bei ungültigem neuen CS/SS verwendet der Prüfer einen eigenen Handler-Task; ein normaler gleichstufiger Fehlerhandler könnte einen unbrauchbaren neuen Stack nicht sicher verwenden.

Das ist ein kontrollierter, vorprüfender RAM-Pfad. Beliebige TSS-/GDT-/Stack-Aliase, MMIO-Seiteneffekte, jede konkurrierende Fault-Reihenfolge, sämtliche DR-/NMI-/Bus-Lock-Effekte und Stepping-Präzision sind nicht abgenommen. Der TSS-Inhalt wird vor ausgehenden Schreibzugriffen gepuffert; daraus folgt keine allgemeine Aliaspräzision. Die alte Kontextwiederherstellung nach einem bereits übernommenen neuen Task wird dagegen ausdrücklich nicht als Ersatz verwendet.

## Tatsächlicher DOS-Gast

`tests/guests/cpu-hardware-tasks-test.asm` wird als **CPUTASK.COM** von den originalen DOS-Dateisystemen geladen. Er besitzt ausschließlich eigene Tabellen, TSS-Strukturen, Stacks und optional eigene klassische 4-KiB-Identitätsseitentabellen. Die kalten Referenzprofile besitzen keinen residenten Speichermanager; dieser private PE-Gast wird nicht ungeprüft unter EMM386 gestartet.

Pro Kaltstart erfolgen **zwölf Hardware-Taskwechsel, fünf Task-IRETs und drei Taskgate-Eintritte**:

1. Ein 16-Bit-CALL-Operand aus dem 32-Bit-Bootstraptask startet einen echten Ring-3-Task mit EIP/ESP oberhalb 64 KiB und eigener LDT.
2. Dieser Task ruft über LDT-Index null und ein GDT-Gate je einen 16-Bit-Ring-0-Task auf. Task-IRET kehrt über die tatsächlichen Backlinks zum Ring-3-Task und schließlich zum Bootstraptask zurück.
3. Ein busy Direktziel und ein abwesender TSS liefern echte #GP/#NP-Rahmen am ursprünglichen CALL. Die regulären Handler setzen hinter diesen absichtlich ungültigen Instruktionen fort.
4. Ein weiterer Task wird mit ungültigem DS tatsächlich übernommen. #TS startet über das IDT einen separaten Handler-Task. Dessen Backlink, der Fehlercode und der gespeicherte EIP/EBX belegen den neuen Fehlerkontext. Der Handler repariert den gespeicherten DS; zwei echte Task-IRETs führen über den reparierten Task zurück zum Aufrufer.
5. Zwei Task-JMPs prüfen ausgelagerte Register, gelöschtes altes Busy, unveränderten Backlink und wieder aufgenommenes Bootstrap-EIP. Danach folgen kontrollierte Real-Mode-Rückkehr, ursprüngliche CR0/CR3-/GDTR-/IDTR-/PIC-Werte und das originale VER/ECHO/DOS-Prompt.

**Der private TR bleibt bis Reset geladen.** Es gibt kein LTR null, um einen ursprünglich ungültigen TR wiederherzustellen. Dieser COM-Test ist kein vollständiger CPU-Snapshot oder allgemeines Windows-/DOS-Extender-Abnahmepaket.

```powershell
.\scripts\test-cpu-hardware-tasks.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Das Skript prüft MS-DOS 6.22 und FreeDOS jeweils zweimal mit/ohne eigenes Paging: **acht Kaltstarts**, je Betriebssystem/Variante identische virtuelle Laufzeit, Schlussbilder und Rohdaten. Der unabhängige Python-Prüfer kontrolliert tatsächliche Assemblerbytes, Register-/Fehlernachweise, gespeicherte TSS-Daten und statische Felder, Busy-/Backlink-Werte, Stackwächter, alle 1.024 PTEs, echte CPU-Zähler und die reguläre DOS-Rückkehr. **59 verfälschte Nachweise werden abgelehnt.** Medien und RAM-Nachweise bleiben in ignorierten privaten `artifacts/hardware-tasks-m3/`; die Gäste schreiben keine Laufwerke und starten keine nativen Fenster oder Eingaben.

## Primärquellen und Folgearbeit

Grundlage sind Kapitel 7 und 9.9.14.1 des ursprünglichen [Intel486 Programmer's Reference Manual](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), die [ursprünglichen TSS-Felder](https://www.scs.stanford.edu/05au-cs240c/lab/i386/s07_01.htm) sowie zum Abgleich widersprüchlicher historischer Druckstellen die Task-/Commit-Beschreibung in Kapitel 8 des [Intel SDM, Band 3A](https://cdrdv2-public.intel.com/812386/253668-sdm-vol-3a.pdf). Der Interpreter, eigene Gast und Prüfer übernehmen keinen fremden Emulatorcode. Tests belegen die hier beschriebenen Verträge; sie ersetzen keinen Vergleich aller Fault-Konkurrenzen mit einem physischen 486-Stepping.

Inzwischen besitzt das [486-LOCK-/Alignment-Fehlerpaket](X86-LOCK-ALIGNMENT.md) eine eigene Teilabnahme. [Debugregister, Breakpoints und TF](X86-DEBUG.md) besitzen inzwischen ebenfalls eigene Gastnachweise; die Taskfälle prüfen nun das Löschen lokaler und Erhalten globaler Breakpoints sowie TF nach dem ersten neuen Taskbefehl. Umfassende Debug-/Restart-Präzision bleibt offen. Acht echte 80-Bit-x87-Register mit Arithmetik, Rundung/Tags/Exceptions/FERR und die übrigen M3-Geräte/Snapshots bleiben offen. M4–M8 mit Pentium, Windows 95, Netzwerk und Voodoo sind weiterhin nicht abgenommen.
