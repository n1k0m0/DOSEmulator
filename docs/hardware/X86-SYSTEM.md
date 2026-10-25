# 486-Systemregister: erster M3-Baustein

Historischer erster M3-Baustein vom 24. Oktober 2026 mit 539 Tests. Die folgende Beschreibung gilt für diesen damaligen Stand. PE war darin noch gesperrt; inzwischen sind Descriptor-Caches, kontrolliertes PE16/PE32 und originales HIMEM/XMS im [nachfolgenden M3-Teilpaket](X86-PROTECTED.md) sowie [Paging/V86 und originales EMM386 mit grundlegenden VCPI-Speicherfunktionen](X86-PAGING-V86.md) geprüft. M3 insgesamt bleibt offen.

## Implementierter Vertrag

| Bereich | Verhalten dieses Entwicklungsmodells |
| --- | --- |
| GDTR/IDTR | Eigenständige Register mit 16-Bit-Bytegrenze und 32-Bit-linearer Basis. LIDT ändert die tatsächlich für Interrupts verwendete Tabelle. |
| LGDT/LIDT | Sechs Bytes aus dem dekodierten Speicheroperanden; 16-Bit-Operand lädt 24 Basisbits, 32-Bit-Operand alle 32. Adressgröße, SIB, Standardsegment und Overrides bleiben unabhängig davon. |
| SGDT/SIDT | Genau sechs Zielbytes; 16-Bit-Kompatibilitätsform speichert 24 Basisbits und eine Null als oberstes Basisbyte, 32-Bit-Form die gesamte Basis. |
| Operandprüfung | Vollständige Bereichsprüfung vor dem ersten Tabellenzugriff. Ein Übertritt über FFFFh liefert im implementierten Systemregisterpfad Gast-Interrupt 13 mit der IP des Befehlsanfangs. Ein Registeroperand statt des vorgeschriebenen Speichers liefert Interrupt 6. |
| SMSW/LMSW | MSW-Zugriff; LMSW liest unabhängig von 66h nur 16 Bits und verändert MP/EM/TS, erhält ET und könnte PE ausschließlich setzen. Der noch nicht unterstützte PE-Wechsel stoppt vor einer Mutation. SMSW in Speicher schreibt nur zwei Bytes. |
| CR0/CR2/CR3 | MOV verwendet 32-Bit-Allgemeinregister unabhängig von 66h/67h. CR2/CR3 sind vorbereitender Registerzustand; bei weiterhin ausgeschaltetem PG gibt es keine Übersetzung/TLB-Wirkung. PG ohne PE löst Gast-Interrupt 13 aus; ET bleibt beim CR0-Schreiben gesetzt. |
| CLTS/WAIT | CLTS löscht ausschließlich TS. WAIT mit MP und TS löst Interrupt 7 aus und lässt sich nach einem echten Gast-CLTS/IRET wiederholen. Das implementiert keine x87-Arithmetik oder ausstehende FPU-Fehler. |
| Reset | IDTR beginnt architekturgemäß bei Basis 0/Limit 03FFh. Für die am historischen 486 nicht definierten GDTR-/CR2-/CR3-Anfangswerte setzt das Modell reproduzierbare Defaults: GDTR 0/FFFFh, CR2/CR3 null. |

Der gültige Real-Mode-INT-/IRQ-/IRET-Pfad benutzt IDTR statt einer fest verdrahteten Adresse null. Eine nicht ausreichende IDTR-Grenze stoppt **vor** Rahmen-/Eventmutation mit einer Diagnose: Verschachtelte Fault-/Shutdown-/Triple-Fault-Behandlung fehlt noch. Auch die übrigen alten Interpreter-Segmentgrenzen sind noch keine vollständige 486-Exception-Implementierung. Reservierte CR-Bits, Cache-/TLB-Verhalten, Debug-/Testregister, Privilegprüfung, LDTR/TR und Descriptor-Caches sind nicht durch diesen Baustein abgenommen. Eine gültige, aber unimplementierte Instruktion wie INVLPG wird nicht als ausgeführter NOP angeboten.

## Nachweise

- Gemeinsamer Release-Build: **539/539 Tests**, davon 39 neue CPU-Fälle, **0 Warnungen/Fehler**. `artifacts/tests/m3-system-build.log` und `m3-system-regressions.log`. Die Tests prüfen unter anderem 24-/32-Bit-Unterschiede, unabhängige Operand-/Adressgrößen, Segment-Overrides, SIB, exakt letzte gültige Adresse, Überlauf ohne Teilzugriff, echte Gast-Faultrahmen, Rücksprung-IP, CLTS/WAIT-Retry und Erhalt der Stopdiagnose für PE.
- `scripts/test-cpu-system.ps1 -SkipBuild -Disk <private IDE-Basis> -Overlay <pristines Installations-COW>`: **vier echte HDD-Kaltstarts**, je zwei unter installiertem MS-DOS 6.22 und FreeDOS. Private Abnahme: `artifacts/cpu-system-m3/acceptance-ba6bc8565fa04d8fa3a13b8d1345ee08/acceptance-summary.json`; tatsächlich getestete DLL-Hashes in `tested-assemblies.json`.
- Eigener **CPUSYS.COM**, SHA-256 `7FE24EDCA2383DC695C10FDBC0CF84A29238606F7799B903C690DEC752841ED3`, wird regulär über DOS/FDC von einer neuen FAT12-Diskette geladen, die ausschließlich das eigene Programm enthält. Er verschiebt seine kopierte IVT, führt INT60, ungültiges SGDT, einen Tabellen-Grenzfehler, unzulässiges PG sowie WAIT/CLTS/IRET tatsächlich auf der CPU aus. Er stellt GDTR/IDTR/CR0/CR2/CR3 wieder her und kehrt über DOS zu VER/ECHO/Prompt zurück. Keine Gastschreiboperationen auf HDD oder Diskette.
- `tests/verify-cpu-system.py` prüft separat den privaten RAM-Nachweis, Register-/Interruptzähler und Rücksprung-IPs gegen Bytes des tatsächlich assemblierten eigenen Programms. Gleicher 136-Byte-Nachweis unter beiden DOS-Systemen: SHA-256 `DBAA5828DAAFFC709502BF11F4298DAEA110F21F05A34E0300631E2800930EB5`. Die beiden Starts je System sind zusätzlich zeit-/bildgleich. Vier absichtlich verfälschte RAM-Nachweise (GDT-Basis, wiederhergestelltes CR0/CR3, Fault-IP) werden vom Prüfer abgelehnt; `negative-controls.json`.
- Wegen des geänderten Interruptpfads wurden die bestehenden zwei FreeDOS-IDE- und sechs FDC-Starts mit persistenten privaten Overlays/Medienwechseln erneut ausgeführt. Der IDE-Pfad behält **438 Sektoren**, **7.272.727.272 ns** und Frame `F4330321FB9EA50D627794A2D62F2F9F0461633D02110D632C3506533B21142D`.

Die verschobene native Mauswiederholung wurde nicht gestartet. Keine Benutzer-/Originalmedien oder C64-Dateien geändert; keine Commits, Pushes oder Veröffentlichungen. RAM-Dumps können Gast-OS-Daten enthalten und bleiben ausschließlich unter dem ignorierten `artifacts/`.

## Primärquelle und nächster Schritt

Grundlage ist Intels historisches [Intel486 Programmer’s Reference Manual (1992, gescannte Originaldokumentation)](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), insbesondere Kapitel 4/10/22 und die Instruktionseinträge CLTS, LGDT/LIDT, LMSW, MOV Special Registers und SGDT/SIDT in Kapitel 26. Bei SGDT/SIDT wird die konkrete 16-Bit-Kompatibilitätsangabe auf **26-262** verwendet; die allgemeine Formulierung im LGDT-Abschnitt ist dazu widersprüchlich. Das ist keine Messung an Originalsilizium.

Der hier angekündigte Folgeschritt ist inzwischen im [Protected-Mode-/HIMEM-Teilpaket](X86-PROTECTED.md) umgesetzt. Dessen aktueller Prüfstand und Grenzen ersetzen die obige historische PE-Stopgrenze. Weiter offen sind insbesondere echte TSS-/Privilegwechsel, Paging/V86, EMM386, x87 und die übrigen M3-Geräte.
