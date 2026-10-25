# 486-Call-Gates und Parameter-Stacks

Historischer Abnahmestand: 25. Oktober 2026. M3-Teilpaket nach [FDC/VDS](X86-FDC-VDS.md), weiterhin keine vollständige M3-Abnahme. Der gemeinsame Release-Build besteht **1.049 Regressionen, 0 Warnungen/Fehler**; 107 neue Fälle prüfen Call-Gates und ihre Fehler-/Rückkehrpfade.

## Implementierter CPU-Pfad

CALL und JMP erkennen jetzt 16-/32-Bit-Call-Gates in GDT und LDT. Die CPU verwendet die tatsächlich gelesenen acht Descriptorbytes. Gate-DPL wird gegen CPL und RPL geprüft, bevor dessen Anwesenheit geprüft wird. Anschließend folgen Code-Selektor, Typ, Zielprivileg und Anwesenheit. Die Ziel-RPL innerhalb des Gates wird nicht als zusätzliche Eintrittsschranke verwendet. Der Offset im CALL-/JMP-Operanden wird ignoriert.

**Der Gate-Typ bestimmt die Rahmen- und Parameterbreite.** Ein 32-Bit-Gate speichert EIP/ESP auch bei einem CALL mit 16-Bit-Operand; ein 16-Bit-Gate verwendet Wörter und ignoriert sein oberes Offsetwort. Die fünf Zählbits steuern bis zu 31 kopierte Wörter beziehungsweise Doppelwörter. Bei gleichem CPL oder einem konformen Ziel erfolgt kein Stackwechsel und keine Parameterkopie. JMP kann über ein Gate am gleichen CPL beziehungsweise in konformen Code springen; ein Eintritt in einen inneren nicht konformen Ring durch JMP liefert #GP.

Bei einem inneren CALL liest die CPU den Zielstack aus dem verborgenen TR-Cache und dem tatsächlichen 16-/32-Bit-TSS. Der Rahmen enthält Rückkehr-IP/CS, die Parameter in ursprünglicher Reihenfolge und den alten Stackzeiger/SS. Gate-Breite, Code-D-Bit, Stack-B-Bit und TSS-Breite bleiben getrennte Eigenschaften. Die bereits vorhandene äußere RETF-Rückkehr findet den alten Stack hinter den kopierten Parametern und bereinigt mit ihrem Immediate beide Parameterbereiche.

Die neuen Regressionen schließen Übergänge **3→1→0→1→3** und **3→2→0→2→3**, Expand-down-Stacks, GDT-/LDT-Gates, Operandengrößenwechsel, Parameterzahlen 0/2/31, beide Stack-B-Bits und beide TSS-Formate ein. Auch das bereits dokumentierte 486-Verhalten eines 16-Bit-Interlevel-RETF auf einen 32-Bit-Außenstack bleibt geprüft. Das ist keine Zusage, dass dabei jedes obere ESP-Wort wie bei einem modernen Prozessor wiederhergestellt wird.

## Fehler und Paging

Die CPU prüft Segmente, Descriptor-Schreibrechte und benötigte Rahmenzugriffe vor dem Commit. Quellen für Parameter werden mit dem **alten CPL** gelesen; neue Ring-0/1/2-Stackzugriffe verwenden Supervisorrechte. Paging-Tests prüfen nicht identische Abbildungen, U/S, WP, einen späten Parameter-Seitenfehler, einen zweiten fehlerhaften Zielstackbereich und einen schreibgeschützten Descriptor ohne Accessed-Bit.

Ausgewählte kombinierte Fehler bestätigen die Reihenfolge: Gate-DPL vor Gate-Anwesenheit, Code-Anwesenheit vor TSS-Zugriff, Zielstack-Segmentprüfung vor Zieloffset und erste Zielrahmenzugriffe vor Parameter-Paging. Ein 486-CALL meldet beim Überlauf des neuen Stacks **#SS(neuer SS-Selektor)**; ein gleicher Privilegstack meldet #SS(0). Die Fehler-IP bleibt die CALL-/JMP-Instruktion. Die getesteten Fehler hinterlassen weder neue versteckte Segmentregister noch einen teilweise geschriebenen neuen RAM-Rahmen. Seitentabellen-A/D-Effekte und CR2 bleiben entsprechend dem bestehenden Paging-Modell erhalten.

`CallGateTransfers` zählt erfolgreich abgeschlossene CALL-/JMP-Gate-Transfers, wird beim Reset gelöscht und ist im Headless-CPU-Bericht enthalten. `PrivilegeStackSwitches` und `OuterPrivilegeReturns` enthalten weiterhin auch Interrupt-/Fault-/IRET-Wechsel. Der neue Zähler und der Kontrollzustand werden bei einem restartbaren Fehler gemeinsam wiederhergestellt.

## Eigener Gast unter beiden DOS-Systemen

`tests/guests/cpu-call-gates-test.asm` wird vom tatsächlichen DOS-Kernel als COM-Datei von einer Diskette mit ausschließlich eigenem Programm geladen. Pro Kaltstart führt der Gast neun erfolgreiche Gate-Transfers aus:

- 32-Bit-GDT-Gate mit einem 16-Bit-CALL-Operanden und einer Rückkehradresse oberhalb 64 KiB.
- Indirektes FF/3 über Gate-Index null einer echten LDT.
- Gleicher CPL, konformes Ziel und ein JMP-Gate ohne zusätzlichen Stackrahmen.
- Originale #GP-/#NP-Zustellung für ein zu restriktives beziehungsweise abwesendes Gate mit anschließender IRET-Rückkehr.
- Wechsel in 16-Bit-Benutzercode, ein Wort-Gate trotz 32-Bit-CALL-Operand und ein Doppelwort-Gate auf einem 16-Bit-Kernelstack.

RETF bereinigt die tatsächlichen Parameter. Der Gast sichert die erzeugten Rahmen, Selektoren, Flags und Stackwächter als Rohdaten. Jeder Lauf enthält acht innere Stackwechsel und acht äußere Rückkehrvorgänge einschließlich der kontrollierten IRETs. Alle Descriptor-, TSS-, Rahmen- und Pagingdaten liegen im eigenen COM-Speicherblock.

Eine zweite Variante aktiviert eigene klassische 4-KiB-Identitätsseitentabellen und CR0.WP während derselben Gate-Folge. Die Tabellen werden zur Laufzeit innerhalb des privaten Blocks auf 4 KiB ausgerichtet. Abschließend werden CR0/CR3, GDTR/IDTR, PIC-Masken und der DOS-Stack wiederhergestellt; LDTR wird entladen. **Der private TR-Cache bleibt bis zum Reset geladen.** Das ist weiterhin keine vollständige CPU-Zustandswiederherstellung oder ein koordinierter Snapshot.

`scripts/test-cpu-call-gates.ps1` prüft beide Varianten unter MS-DOS 6.22 und FreeDOS je zweimal, insgesamt **acht echte Kaltstarts**. Jede Wiederholung besitzt identische virtuelle Laufzeit, Schlussbilder und Rohdaten. Der unabhängige Prüfer liest die eigenen Assemblerbytes, tatsächliche RAM-Rahmen und Gate-/TSS-Descriptoren, kontrolliert alle 1.024 PTEs sowie das Ende beim originalen VER/ECHO/COMMAND.COM-Prompt. **33 gezielt verfälschte Nachweise** müssen scheitern, etwa falsche Parameter, gekürztes EIP, falscher CPL, Stackwächter, Gate-Zähler, Codebytes, Paging-Rechte und physische Seitenabbildungen.

```powershell
.\scripts\test-cpu-call-gates.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Medien, Dumps und Ergebnisse bleiben unter ignorierten `artifacts/call-gates-m3/`. Alle Betriebssystem-/Spiel-/ROM-Dateien bleiben unverändert; der Gast schreibt nicht auf seine Laufwerke. Es werden keine nativen Fenster, Fokusversuche oder Hosteingaben gestartet.

## Abgrenzung und nächste Arbeit

Die echten DOS-Proben verwenden Ring 3/0, eine 32-Bit-TSS und wahlweise Identitätspaging. Ring 1/2, eine 16-Bit-TSS, nicht identische Seitenabbildungen und die fehlerhaften Seitenrechte sind zusätzlich durch CPU-Regressionen geprüft. Diese kontrollierten Fälle beweisen keine allgemeine DOS-Extender-/Windows-Abnahme, jede mögliche Fehlerkonkurrenz, Stackalias-/MMIO-Seiteneffekte oder historische Bus-/Stepping-Präzision.

Inzwischen besitzen [Task-Gates, direkte Hardware-TSS-Wechsel und IRET mit NT](X86-HARDWARE-TASKS.md) eine eigene geprüfte Teilabnahme. Offen bleiben weitere 486-/Debug-/Alignment-/LOCK-/Restart-Präzision, arithmetisches Acht-mal-80-Bit-x87 und die übrigen M3-Geräte. Der neuere Hardware-Task-Nachweis beschreibt Save/Load, Busy-/Backlink-/NT-/TS-Verhalten und tatsächliche Fault-/Rückkehrtests. M4–M8 bleiben offen.

## Primärquelle

Register- und Rahmenverträge folgen dem ursprünglichen [Intel486 Programmer's Reference Manual, 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Abschnitte 6.5, 24.4.2 und CALL/JMP/RET. Der eigene Interpreter, Gast und Prüfer übernehmen keinen fremden Emulatorcode. Die 486-CALL-Darstellung unterscheidet #SS mit neuem Stackselektor ausdrücklich von #SS(0) am gleichen Privilegstack.
