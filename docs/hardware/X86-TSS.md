# 486-LDTR/TR und Interrupt-Privilegwechsel

Historisches TSS-Teilpaket vom 24. Oktober 2026 mit 675 Tests auf dem [PE-/HIMEM-Stand](X86-PROTECTED.md). Dieser Text beschreibt seine damalige Abnahme. Inzwischen sind zusätzlich [Paging, V86 und originale EMM386-/VCPI-Speicherpfade](X86-PAGING-V86.md) geprüft. Auch [16-/32-Bit-Call-Gates](X86-CALL-GATES.md) sind inzwischen in einem eigenen Teilpaket geprüft. **M3 insgesamt ist weiterhin offen.**

## Umgesetzter Umfang

| Bereich | Implementiertes und geprüftes Verhalten |
| --- | --- |
| Systemselektoren | `SLDT`, `STR`, `LLDT`, `LTR` über Register und Speicher. Zwei-Byte-Operanden erhalten die oberen Registerhälften auch bei Operandpräfixen. Real Mode liefert #UD; LLDT/LTR verlangen CPL0 vor einem Operandenlesezugriff. SLDT/STR sind auch bei CPL3 verwendbar. |
| LDTR | Getrennter sichtbarer Selektor und verborgener Systemcache. GDT-Typ/Present/Grenze, lokale Selektoren und Null-LDTR geprüft. LDT-Eintrag null ist über TI=1 verwendbar. Tabellenänderungen ändern vorhandene Caches erst beim Reload; LLDT null macht bestehende Datenregister nicht nachträglich ungültig. |
| TR | Verfügbarer 16-/32-Bit-TSS wird geladen, im Speicher busy markiert und getrennt gecacht. Kein automatischer Taskwechsel oder Setzen von CR0.TS. Falscher Typ, busy, fehlendes Present, lokale/null/zu große Selektoren erhalten Gastfehler. Laden eines anderen TR löscht das alte Busy-Bit nicht. |
| Innerer Stack | Interrupt-/Trap-Gates lesen SPn/SSn oder ESPn/SSn aus dem gecachten TSS für die Zielstufe 0, 1 oder 2. TSS-Speicheränderungen wirken beim nächsten Übergang; GDT-Änderungen am geladenen TSS ändern dessen Cache nicht. Stackselektor darf die geladene LDT verwenden. |
| Rahmen | 16-/32-Bit-Gate bestimmt die Slotgröße, SS.B unabhängig davon die Stackadressgröße. Ein Ringwechsel sichert zusätzlich den alten SS und SP/ESP. Ein 32-Bit-Gate sichert den ganzen alten ESP auch bei SS.B=0. Fehlercode erweitert den Rahmen; RF bleibt im Fehlerrahmen erhalten. Gleichstufige und conforming Ziele lesen keinen neuen TSS-Stack. |
| Rückkehr | IRET/IRETD und RETF zu äußeren Stufen validieren Rückkehrcode, den vollständigen Rahmen und den neuen Stackselektor vor dem Wechsel. RETF imm berücksichtigt Parameter auf beiden Stacks. IF/IOPL werden nach der ausführenden alten Stufe behandelt. Unzulässige DS-/ES-/FS-/GS-Caches werden beim äußeren Wechsel ungültig; geeignete gecachte Daten und conforming Code bleiben erhalten. |
| Faults | #TS für ungültige TSS-/neue Stackselektoren, #SS für abwesende Stacksegmente einschließlich äußerer Rückkehr oder unzureichende Stackgrenze. Ausgewählte Fehlerkombinationen prüfen Vorrang gegenüber einem zu großen Zieloffset, ausbleibende Teilrahmen und EXT bei Hardware-Selektorfehlern. |
| 486-Mischformen | Ein 32-Bit-Fehlerslot reserviert vier Bytes und schreibt nur das niedrige Fehlerwort; die oberen zwei RAM-Bytes bleiben erhalten. IRETD zu SS.B=0 übernimmt nur SP und behält die obere ESP-Hälfte des inneren Stacks. 16-Bit-RETF/IRET zu SS.B=1 setzt beim 486 den neuen SS in die obere ESP-Hälfte. Regressionsfälle unterscheiden diese Verhalten von einer allgemeinen Pentium-Annahme. |
| Diagnose/Reset | Headless zeigt LDTR/TR mit Systemcache sowie tatsächliche innere Stackwechsel und äußere Rückkehrzähler. CPU-Reset invalidiert beide Systemcaches und setzt die Zähler zurück; RAM-Busy-Bits werden dadurch nicht umgeschrieben. |

Die 16-Bit-TSS-/Gate-Mischformen, Ring 1/2 und ausgewählte fehlerhafte TSS-/Rückkehrpfade sind CPU-Regressionen. Der nachfolgende echte DOS-Gast verwendet einen 32-Bit-TSS und Ring 0/3. Diese Unterscheidung bleibt Teil des Abnahmeumfangs.

## Eigener Gast unter MS-DOS und FreeDOS

```powershell
.\scripts\test-cpu-privilege.ps1 -SkipBuild `
  -Disk <private-IDE-Basis> -Overlay <pristines-Installations-COW>
```

Das Skript assembliert ausschließlich **CPUPRIV.COM** aus eigenen Quellen und legt eine neue reine FAT12-Probediskette an. Je zwei unabhängige HDD-Kaltstarts unter MS-DOS 6.22 und FreeDOS prüfen:

1. LDTR laden, dessen GDT-Basis ändern, trotzdem weiter die alte LDT verwenden und anschließend nach LLDT-Reload die zweite LDT lesen.
2. LTR/Busy-Bit; danach den GDT-TSS auf eine abwesende, verkürzte Struktur an anderer Adresse ändern. Die wirklichen Interrupts verwenden weiterhin den zuvor geladenen TR-Cache.
3. IRETD von Ring 0 nach 3 mit tatsächlichem EIP/ESP oberhalb FFFFh innerhalb der eigenen COM-Allokation; Datenregister-Invalidierung, erlaubtes SLDT/STR und IF-Rückkehr.
4. INT60 in den Ring-0-TSS-Stack mit fünf geprüften Rahmenslots, Änderung von TSS.ESP0 und IRETD zurück nach Ring 3. Die folgenden Übergänge verwenden tatsächlich den zweiten Stack.
5. #GP bei CLI sowie beim verbotenen INT61; genaue Befehls-IPs, Fehler 0/030Ah, RF und sechs Rahmenslots. Der eigene Handler setzt nach den absichtlichen Fehlern fort. POPFD bei Ring 3 kann IF/IOPL nicht erhöhen.
6. INT62 nach Ring 0, kontrollierte PE16-/Real-Mode-Rückkehr, LDTR null, CR0/GDTR/IDTR/PIC-Wiederherstellung und reguläres VER/ECHO/Prompt.

Jeder Lauf zählt **vier echte innere Stackwechsel und vier äußere Rückkehrvorgänge**. Der unabhängige Python-Prüfer kontrolliert den 320-Byte-RAM-Nachweis gegen eigene assemblierte Programmlabels und tatsächliche Opcodebytes, die privaten TSS-/GDT-/LDT-Speicherwerte und den abschließenden CPU-Bericht. Zehn gezielte Verfälschungen von Cachewert, Ringselektor, Rücksprung-IP, Fehlercode, Stack, TSS-Speicher, Guard, tatsächlichen CPU-Zählern/TR-Cache oder Programmadresse werden abgelehnt. Wiederholungen je Betriebssystem sind zeit-, bild- und nachweisgleich. Keine HDD-/FDC-Writes oder Resets in diesen vier Läufen.

**TR-Restzustand:** Die kalten Referenzstarts besitzen zunächst einen ungültigen TR. Diesen Zustand kann der Gast nach LTR nicht durch LTR null wiederherstellen. Nach Rückkehr zu DOS bleibt deshalb der private TR-Cache bis zum Reset geladen; der Prüfer verlangt genau diesen Zustand. Die Tests starten jeweils kalt und verwenden keine fremde oder residente TSS-Struktur. Dies ist keine vollständige Rücksetzung sämtlicher CPU-Zustände durch das COM-Programm und kein Snapshot-Nachweis.

## Nachweise und Grenzen

Gemeinsamer Release-Build **675/675 Regressionen**, **80 neue Fälle** seit dem 595er-PE-Stand, 0 Warnungen/Fehler. Die vier bisherigen PE-Gaststarts, das originale HIMEM.SYS mit /TESTMEM:ON/XMS einschließlich treiberlosem Kontrollstart, zwei FreeDOS-IDE- und sechs FDC-Kaltstarts bestehen erneut. Dies ist wegen der gemeinsamen Segment-/Interrupt-/Rückkehrpfade eine Regression des vorhandenen Umfangs. IDE bleibt bei 438 Sektoren, 7.272.727.272 ns und dem bisherigen Bildhash. 41 PowerShell-Skripte/141 Checks und 29 Python-Werkzeuge geprüft. Die alten und neuen eigenen geschützten Fehlerhandler lesen ausdrücklich das niedrige Fehlerwort. Private Pfade und DLL-Identitäten: [Implementierungsstand](../IMPLEMENTATION-STATUS.md).

Hardware-Taskwechsel einschließlich Task-Gates/Backlink/NT-Rückkehr und Call-Gates waren zum Abschluss dieses historischen Teilpakets ausdrückliche Stopgrenzen; Call-Gates sind inzwischen im oben verlinkten Folgepaket ergänzt. LAR/LSL/VERR/VERW, I/O-Bitmap, Paging, V86, vollständige Debug-/Alignment-/Restart-/Fehlervorrangfälle, x87 und Originalchip-Timing sind nicht als fertig ausgewiesen. Die Zeitbewertung bleibt das angenäherte Vier-Clock-Modell. Kein originales EMM386, DOS4GW/Doom oder Windows ist hiermit abgenommen. Grafik/Bedienung und C64-Quellen wurden in diesem Paket nicht verändert; die verschobene native Mauswiederholung bleibt offen und wurde nicht gestartet.

Primärgrundlage: Intels [Intel486 Programmer’s Reference Manual, 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 6/7/9 sowie LLDT/LTR/SLDT/STR und INT/IRET/RET in Kapitel 26. Die separat gehosteten ursprünglichen Intel-Beschreibungen von [LLDT](https://www.scs.stanford.edu/05au-cs240c/lab/i386/LLDT.htm), [LTR](https://www.scs.stanford.edu/05au-cs240c/lab/i386/LTR.htm) und [Interruptverfahren](https://www.scs.stanford.edu/05au-cs240c/lab/i386/s09_06.htm) dienen als zusätzliche lesbare Referenz. Keine fremden CPU-Quellen oder OS-Binaries werden eingebettet.

Die 1992er IRET-Pseudocodeangabe #NP für einen abwesenden Rückkehr-SS widerspricht der späteren [Intel486-Schutzbeschreibung von 1995, Abschnitt 6](https://bitsavers.org/components/intel/80486/241731-002_Intel486_Microprocessors_and_Related_Products_Jan95.pdf); implementiert ist #SS. Die speziellen Fehlerwort-/16-Bit-Rückkehrfälle folgen Intels [Architekturvergleich, Abschnitte 17.30.2 und 17.30.4](https://www.intel.com/content/dam/support/us/en/documents/processors/pentium4/sb/25366821.pdf). Das erhaltene obere ESP-Wort bei IRETD zu einem 16-Bit-Stack beschreibt auch der verantwortliche Windows-Entwickler in seinem [Bericht zum Windows-95-Workaround](https://devblogs.microsoft.com/oldnewthing/20160404-00/?p=93261/). Diese ausgewählten Korrekturen sind keine allgemeine Silizium- oder Windows-Abnahme.

Als Nächstes folgen 4-KiB-Paging/TLB und V86 mit tatsächlichen Monitor-/EMM386-Prüfungen. Weitere CPU-Instruktionen, Gate-/Task-Pfade, x87 und die übrigen M3-Pakete bleiben Bestandteil des Gesamtauftrags.
