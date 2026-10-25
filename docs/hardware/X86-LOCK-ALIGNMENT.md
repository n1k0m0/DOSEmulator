# 486-LOCK, CMPXCHG/XADD und Alignment-Check

Stand: 25. Oktober 2026. M3-Teilpaket nach [Hardware-Tasks](X86-HARDWARE-TASKS.md). Gemeinsamer Release-Build: **1.336/1.336 Regressionen, 0 Warnungen/Fehler**, darunter 197 neue CPU-Fälle. **M3 insgesamt bleibt offen.**

## LOCK und Austauschbefehle

Der Decoder erkennt F0 als LOCK-Präfix. Er erlaubt die dokumentierten schreibenden Speicherformen von ADD/ADC/SUB/SBB/AND/OR/XOR, INC/DEC/NOT/NEG, BTS/BTR/BTC, XCHG, XADD und CMPXCHG. Registerziele, reine Vergleiche/Bit-Tests und andere Instruktionen liefern #UD, bevor deren Datenoperanden gelesen oder verändert werden. LOCK prüft beim 486 kein IOPL; ein Ring-3-Programm kann die erlaubten Formen auch mit IOPL0 verwenden. Operanden-/Adressgrößen und Segment-Overrides bleiben wirksam.

XADD und die Standard-CMPXCHG-Encodings 0F B0/B1 sind für Bytes, Wörter und Doppelwörter ergänzt. XADD sichert effektive Adresse und beide Eingangswerte vor dem Registertausch; auch Registeralias und AH/AL sind geprüft. CMPXCHG bildet Flags aus Akkumulator minus Ziel und aktualisiert je nach Vergleich das Ziel oder den Akkumulator. **Auch beim ungleichen Vergleich erfolgt ein Ziel-Schreibzugriff.** Schreibrechte werden deshalb vor Vergleich, MMIO-Lesen und Registeränderungen geprüft. Eine CPU-Regression zählt bei einem erfolglosen Doppelwortvergleich vier tatsächliche Geräte-Schreibbytes mit dem alten Wert.

Ein Speicher-XCHG ist auch ohne F0 implizit gesperrt. `LockedMemoryInstructions` zählt erfolgreich abgeschlossene explizite beziehungsweise solche impliziten Operationen, `InvalidLockPrefixes` tatsächlich verworfene Präfixe. Der Zähler enthält auch XCHGs des Betriebssystems; der Gastprüfer setzt ihn nicht mit einer ausschließlich eigenen Programmstatistik gleich. Reset löscht die Diagnosewerte.

Das vorhandene Einprozessor-Scheduling unterbricht eine solche Speicheroperation nicht zwischen den CPU-Schritten. **Dies ist kein taktgenauer LOCK#-/HOLD-/Cache-/DMA-Arbitrations- oder Mehrprozessornachweis.** Auch mehrere Präfixe derselben Präfixgruppe und sämtliche historischen Stepping-Encodings sind nicht allgemein abgenommen. Eine Instruktion über 15 Bytes liefert jetzt einen Gast-#GP statt eines Host-Abbruchs.

## Ausrichtungsfehler

#AC wird bei CPL3 erzeugt, wenn **CR0.AM und EFLAGS.AC** gesetzt sind. Es gilt auch für V86. Daten- und Stackwörter werden auf zwei, Doppelwörter auf vier Byte geprüft; Bitstrings verwenden die im 486-Handbuch genannte Vier-Byte-Grenze. Entscheidend ist die **lineare Adresse einschließlich Segmentbasis**. Ein reiner Befehlsholzugriff wird dadurch nicht zu einem ausrichtungspflichtigen Datenzugriff.

Real Mode und CPL0/1/2 erzeugen damit keinen #AC. Implizite Supervisorzugriffe auf GDT, IDT, LDT, TSS und I/O-Bitmap bleiben ebenfalls ausgenommen. Die Tests verwenden dafür absichtlich ungerade GDT-/TSS-Basen. DMA bleibt ein physischer Buszugriff. Die bereits unterstützten x87-Kontrollwortzugriffe verwenden ihre Wortausrichtung; daraus folgt noch kein 80-Bit-x87-Kern oder vollständiger x87-Speicherformatnachweis.

Der Fehlerrahmen enthält **Fehlercode null**, den Anfang der fehlerhaften Instruktion und RF/AC im gespeicherten Flags-Wort. Der Ring-0-Handler kann das gespeicherte AC löschen und per IRETD genau diese Instruktion neu starten. Die ausgewählten LOAD/STORE/RMW-/Stack-/REP-/INS-Pfade prüfen ihre benötigten Zugriffe vor Datenänderungen beziehungsweise Port-Eingabe. Bei einem Fehler bleiben die geprüften Register, REP-Zähler und Speicherwächter erhalten.

### Referenz-Reihenfolge und 486-Sonderfall

Für die hier geprüften Datenzugriffe folgt das Referenzmodell der Tabelle 9-2 des ursprünglichen 486-Programmierhandbuchs: **Segmentfehler vor #AC vor Daten-#PF**. Ein ungültiger LOCK-Operand meldet bereits #UD. Diese Wahl wird explizit geprüft: dieselbe ungerade Adresse in einer fehlenden Seite liefert erst #AC, nach Löschen von AC einen echten #PF und nach Reparatur der PTE die ursprünglichen Nutzdaten. CR2 und die Daten-Seitenübersetzung werden durch den davor erzeugten #AC nicht als Seitenfehler verändert.

Neuere Intel-Texte ordnen mehrere Ausführungsfehler einer gemeinsamen, innerhalb der Klasse implementierungsabhängigen Gruppe zu. Die vorstehende Reihenfolge ist eine dokumentierte **Referenzentscheidung**, keine Abnahme aller physischen 486-Steppings. Die konkurrierenden Fehler benötigen vor einer solchen Zusage einen zusätzlichen Hardwarevergleich. Gleiches gilt für mehrteilige Alias-/MMIO-Zugriffe und alle Teil-Schreibeffekte.

SGDT/SIDT sind ein wichtiger historischer Sonderfall: Der 486 verwendet ein Wort und danach ein Doppelwort bei Adresse +2. Der geeignete Benutzerpuffer liegt daher bei **2 MOD 4**. Ein bei 0 MOD 4 liegender Puffer löst im Modell #AC aus. Der vorprüfende Referenzpfad kontrolliert beide Felder vor dem ersten Schreibzugriff und hinterlässt bei diesem Fehler keine teilweise geschriebene Struktur; das ist ausdrücklich keine Behauptung über jede reale Bus-/Teil-Schreibfolge.

## Eigener Gast unter beiden DOS-Systemen

`tests/guests/cpu-lock-alignment-test.asm` wird als **CPULOCK.COM** vom tatsächlichen MS-DOS-/FreeDOS-Kernel geladen. Das Programm besitzt eigene Descriptoren, einen Interrupt-TSS, Stacks und optional eigene klassische 4-KiB-Identitätsseiten. Es löscht beim privaten PE-Eintritt auch NT; das originale DOS darf NT zuvor im Real Mode gesetzt haben.

Pro Kaltstart prüft der Gast:

1. **Zehn eigene erfolgreiche gesperrte Speicheränderungen**: unter anderem XADD, gleiches/ungleiches CMPXCHG, implizites XCHG sowie Zwischenwerte und Carry für BTS/BTC/BTR bei Ring 3 und IOPL0.
2. **Vier echte #UD-Rahmen** für ungültige Präfixe: Register-ADD, Speicher-CMP mit gleichzeitig ungerader Adresse, MOV mit gleichzeitig null DS sowie FNINIT bei gesetztem TS. Unerlaubte Operanden- oder x87-Abkürzungen würden damit die falsche Exception beziehungsweise Nutzdatenänderung erzeugen.
3. **Fünf wirkliche #AC-/IRETD-Wiederholungen**: ungerader LOAD, gesperrtes XADD, REP MOVSD, SGDT und PUSH innerhalb des privaten Stacks. Ein geeigneter SGDT-Puffer besteht daneben mit aktivem AC. Der Handler sichert Rahmen und Register, löscht ausschließlich das gespeicherte AC und wiederholt den ursprünglichen Befehl. Beide Stackwächter bleiben erhalten.
4. Mit Paging folgt ein **sechster #AC und ein #PF** an derselben Instruktion. Eine tatsächlich fehlende private Seite wird durch den Gastfehlerhandler abgebildet und mit INVLPG invalidiert. Erst danach wird ihr eigener Datenwert gelesen.
5. Kontrollierte Rückkehr nach DOS mit ursprünglichen CR0/CR3, GDTR/IDTR, PIC-Masken und DOS-Stack sowie originalem VER/ECHO/Prompt.

Je Start erfolgen zehn beziehungsweise zwölf innere Interrupt-Stackwechsel und ebenso viele äußere Rückkehrvorgänge einschließlich des anfänglichen Ring-3-Eintritts. **Der private TR bleibt bis Reset geladen.** Es wird kein ursprünglich ungültiger TR mit LTR null restauriert; dies ist kein vollständiger CPU-Snapshot. Der private PE-Gast wird nicht ungeprüft unter einem residenten EMM386 gestartet.

```powershell
.\scripts\test-cpu-lock-alignment.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Das Skript besteht **acht Kaltstarts**, MS-DOS 6.22 und FreeDOS jeweils zweimal mit/ohne Paging. Der unabhängige Prüfer kontrolliert assemblierte Instruktionen, echte Nutzdaten/Fehlerrahmen und Gegenfehler, Register und REP-Fortsetzung, Descriptoren, Stackwächter, sämtliche 1.024 PTEs und die reguläre DOS-Rückkehr. **70 verfälschte Nachweise werden verworfen.** Die Wiederholungen sind zeit-, Bild- und nachweisgleich. Die Gäste schreiben keine Laufwerke; alle Images/RAM-Nachweise liegen privat in ignorierten `artifacts/lock-alignment-m3/`. Keine nativen Fenster, Fokusversuche oder Windows-Eingaben.

## Primärquellen und Grenzen

Die Opcodeverträge und historischen Alignment-Fälle stehen im [Intel486 Programmer's Reference Manual, 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 9/13 und den LOCK-/CMPXCHG-/XADD-Beschreibungen. Die [Intel486-Entwicklerdokumentation, 1997](https://bitsavers.trailing-edge.com/components/intel/80486/273021-001_Embedded_Intel486_Processor_Family_Developers_Manual_199710.pdf) dokumentiert ebenfalls die impliziten Supervisorzugriffe und den Pseudo-Descriptor-Sonderfall. Die [Intel SDM-Prioritätstabelle](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-3a-part-1-manual.pdf) dient zur Abgrenzung der nicht zugesagten Stepping-Präzision.

NASM klassifiziert das Standard-CMPXCHG B0/B1 als PENT und bietet daneben die alten A6/A7-Formen an ([NASM-Instruktionsdaten](https://github.com/netwide-assembler/nasm/blob/master/x86/insns.dat)). Der Gast verwendet deshalb die expliziten, vom 486-Programmierhandbuch beschriebenen B1-Bytes; der unabhängige Prüfer verlangt genau diese Bytes. Das ist keine neue Pentium-CPU oder allgemeine Abnahme früher A-Stepping-Encodings. Es wurde kein fremder Emulatorcode übernommen.

Inzwischen besitzt das [486-Debugregister-/Breakpoint-/TF-Paket](X86-DEBUG.md) eine eigene Referenzabnahme einschließlich originalem Microsoft DEBUG.EXE. Offen bleiben umfassende Debug-/Restart-/Task-/Alias-/MMIO-/Buspräzision und ein arithmetischer Acht-mal-80-Bit-x87-Kern mit Rundung/Tags/Exceptions/FERR. Ebenso offen: die übrigen M3-Geräte/Snapshots und M4–M8 mit Pentium, Windows 95, Netzwerk und Voodoo.
