# 486-Descriptor-Caches, PE16/PE32 und originales HIMEM

Stand: 24. Oktober 2026. **Geprüftes M3-Teilpaket; M3 insgesamt bleibt offen.** Der nachfolgende [LDTR/TR-/TSS-Interrupt-Baustein](X86-TSS.md) ist inzwischen zusätzlich geprüft; dieser Text beschreibt die vorherige PE-/HIMEM-Abnahme. Der Entwicklungsinterpreter besitzt jetzt einen tatsächlichen Protected-Mode-Eintritt, GDT-Descriptor-Caches und geschützte Interrupt-/Fehlerpfade auf derselben Privilegstufe. Die ursprüngliche MS-DOS-6.22-Installation bleibt erhalten; HIMEM wird ausschließlich in einer neuen privaten Konfiguration geladen.

## Implementierter Umfang

| Bereich | Verhalten |
| --- | --- |
| Segmentregister | Sichtbarer Selektor und getrennte verborgene Basis, effektive Grenze, Access-/Flagbits und Gültigkeit. Tabellenänderungen wirken erst beim erneuten Laden. Erfolgreiches Laden setzt das Accessed-Bit im GDT-Speicher. |
| GDT-Prüfung | Byte-/Seitengranularität, Selektorgrenze, Nullselektoren, Code-/Datentyp, Schreib-/Leserechte, Present, RPL/DPL und conforming Code. SS verlangt einen vorhandenen schreibbaren Datenbereich mit passender Privilegstufe. |
| Speicher | Speicheroperanden tragen ihr gewähltes Segment. Grenzen/Rechte werden auch für Strings, moffs, Far-Pointer, Systemtabellen und Stackzugriffe geprüft. Expand-down berücksichtigt die untere Grenze sowie die durch B bestimmte obere Grenze. |
| PE-Umschaltung | MOV CR0 und LMSW setzen PE tatsächlich. LMSW kann PE nicht löschen; MOV CR0 kann es löschen. Die Umschaltung ersetzt keine versteckten Caches. Der geprüfte Rückweg lädt vorher passende 64-KiB-Deskriptoren und springt danach zum realen CS zurück. |
| Code/Stack | CS.D bestimmt Standard-Operand-/Adressgröße; 66h/67h überschreiben sie ohne Toggle durch doppelte Präfixe. SS.B bestimmt unabhängig davon SP/ESP. EIP oberhalb FFFFh, direkte/indirekte Far-Transfers, Near-/Far-CALL/RET sowie 16-/32-Bit-Interrupt-Rückkehr sind im beschriebenen Umfang geprüft. |
| IDT | 16-/32-Bit-Interrupt- und Trap-Gates auf gleicher Privilegstufe, tatsächliche INT-/Hardware-IRQ-Wege, Fehlercodes, EXT bei den implementierten Hardware-Selektorfehlern und unterschiedliche IF-Behandlung. Task-Gates und erforderliche Stackwechsel stoppen ausdrücklich. |
| Faults | Gast-#DE/#UD/#NM/#NP/#SS/#GP mit Befehlsanfang als Rücksprungziel; geschützte Fehler erhalten passende Fehlerslots. Allgemeinregister und Rechenflags werden bei fehlgeschlagenen geschützten Instruktionen vor der Zustellung wiederhergestellt. RF wird im Fault-Rahmen gesetzt, von IRETD wiederhergestellt und nach erfolgreichem Retry gelöscht; REP behält RF bis zum Ende. |
| Verschachtelte Faults | Implementierte contributory-/Page-Fault-Kombinationen eskalieren zu #DF. Misslingt dessen Zustellung, meldet die CPU Triple Fault; der Runtime-Loop löst an der nächsten Grenze einen CPU-Reset mit erhaltenem RAM/Gerätegraph aus. Tests prüfen auch ausbleibende Teilrahmen und Resetursache. Keine elektrische Shutdown-/NMI-Pin-Simulation. |
| Diagnose | Headless meldet Modus, EIP/EFLAGS, CR0/2/3, GDTR/IDTR, Triple-Fault-Zustand und tatsächliche PE-Ein-/Austritte sowie geschützte Instruktionsschritte. Die Zeitbewertung bleibt das bisherige angenäherte Vier-Clock-Modell. |

Ein gültiger Wechsel über noch nicht implementierte Call-/Task-Gates, ein Ring-/TSS-Stackwechsel, äußerer Far-Return oder IRET nach V86 wird nicht mit Real-Mode-Semantik fortgesetzt. Paging bleibt gesperrt. LDTR/TR und Debug-/Testregister, vollständige Privilegwechsel, TSS/I/O-Bitmap, Hardware-Tasks, Single-Step-/Debug-Faults, alle Instruktions- und Fehlervorrangfälle, sämtliche Restart-Seiteneffekte und Originalchip-Timing sind nicht abgenommen. Die Real-Mode-IDT-Grenzdiagnose bleibt eine separate noch begrenzte Zustellung. Dies ist kein vollständiger 486-Kern.

## Reale PE-Gäste unter beiden DOS-Systemen

`scripts/test-cpu-protected.ps1 -SkipBuild -Disk <private IDE-Basis> -Overlay <pristines Installations-COW>` assembliert ausschließlich den eigenen **CPUPE.COM** und legt dafür eine neue reine FAT12-Testdiskette an. Vier Kaltstarts, je zwei unter MS-DOS 6.22 und FreeDOS, durchlaufen:

1. Real Mode → PE16 über LMSW/GDT/Far-JMP.
2. FS-Caching trotz geänderter GDT-Basis und anschließendes korrektes Reload.
3. PE32 mit unabhängig verzerrter CS-/SS-Basis, sodass **EIP und ESP tatsächlich oberhalb 64 KiB** liegen, während Code und Stack im eigenen DOS-Programmpuffer bleiben.
4. Arithmetik, Call/Return, 16-/32-Bit-Push/Pop, vorübergehender Zugriff auf einen gesicherten Extended-RAM-Dword, Interrupt-/Trap-Gate sowie zwei #GP, je ein #NP/#SS/#UD mit Retry/Rückkehr.
5. Rückweg über PE16 nach Real Mode, Wiederherstellung von Systemregistern, PIC-Masken und verändertem Extended-RAM-Inhalt; anschließend reguläres VER/ECHO/Prompt.

`verify-cpu-protected.py` prüft den **224-Byte-RAM-Nachweis** unabhängig vom CPU-Code gegen die Bytes des tatsächlich assemblierten eigenen Programms. Er kontrolliert fünf genaue Fault-IPs, Selektorfehler, Stackgleichheit, Tabelleninhalte, erhaltene/geänderte Cachewerte und den tatsächlichen abschließenden CPU-Zustand. Starts je Betriebssystem sind zeit-/bildgleich; die Nachweise beider Betriebssysteme müssen wegen unterschiedlicher ursprünglicher PIC-Masken nicht bytegleich sein. Alle vier Läufe ohne Medienwrites oder Reset.

## Originales MS-DOS-6.22-HIMEM und XMS

`scripts/test-himem.ps1 -SkipBuild -Disk <private IDE-Basis> -Overlay <pristines Installations-COW>` erstellt eine neue COW-Kopie. Tatsächliche DOS-Befehle sichern CONFIG.SYS nach CONFIG.PRE und stellen `DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON` vor die bisherigen Zeilen. Der unabhängige FAT-/Journalprüfer bestätigt **129 Ausgangsdateien, 130 Dateien danach, 3.074 belegte Cluster**, identische FATs und keine verlorenen/kreuzverknüpften Ketten. CONFIG.PRE ist byteidentisch zur alten Konfiguration; sämtliche anderen ursprünglichen Dateien bleiben erhalten. Originales HIMEM.SYS: **29.408 Byte**, SHA-256 `371353DB16B4E7C9688C307405693B3D35E08CEE9E44CD4884916EAFCE427348`.

Zwei Kaltstarts dieser privaten Konfiguration schließen den originalen HIMEM-Speichertest mit **„beendet.“** ab. Das originale MEM /C läuft vor dem eigenen XMSTEST.COM; danach werden nur residente VER-/ECHO-Befehle ausgeführt, damit der Prüfer die eigenen freigegebenen Programmpuffer im RAM noch untersuchen kann. Der XMS-Gast verwendet INT2F zur Erkennung und den echten Far-Einsprung des residenten Microsoft-Treibers:

- XMS **3.00**, Treiberrevision **3.10**; größte/freie Menge anfangs **7.104 KiB** bei 8 MiB Gast-RAM.
- 64 KiB reservieren und sperren; tatsächliche physische Basis **00110000h**.
- 4.096 eigene Musterbytes über XMS in den Block und zurück übertragen; alle 2.048 Wörter vergleichen. Unabhängiger Prüfer kontrolliert beide konventionellen Puffer und den Extended-RAM-Inhalt byteweise.
- Zusätzlich vier Bytes am letzten gültigen Blockoffset **65.532** schreiben und zurücklesen.
- Entsperren/freigeben; größte/freie Menge wieder 7.104 KiB und vorheriger A20-Zustand erhalten. Kein HDD-/FDC-Write in diesen Prüfläufen.

Die CPU-Diagnose erfasst je Kaltstart einen PE-Eintritt, einen Austritt und fünf im geschützten Modus begonnenen Instruktionsschritte. Die anschließenden XMS-Transfers greifen über den originalen Gasttreiber und die erhaltenen versteckten Segmentzustände auf Extended RAM zu; der Host implementiert keine XMS-/DOS-Abkürzung. Eine weitere frische Kopie der alten Konfiguration ohne HIMEM wird vom gleichen eigenen Programm korrekt als **fehlender XMS-Treiber** erkannt. Das belegt dieses konkrete HIMEM-/XMS-Profil; EMM386, andere Speichermanager und DOS-Extender sind nicht dadurch abgenommen.

## Prüfstand und Quellen

Gemeinsamer Release-Build: **595/595 Regressionen**, davon **56 neue CPU-/Runtime-Fälle**, **0 Warnungen/Fehler**. Zusätzlich vier frühere Systemregister-Gaststarts, zwei FreeDOS-IDE- und sechs FDC-Kaltstarts mit privaten Schreib-/Medienwechselpfaden. Der IDE-Pfad behält 438 Sektoren, 7.272.727.272 ns und seinen bisherigen Framehash. Alle **40 PowerShell-Skripte/138 Checks** sowie **28 Python-Testwerkzeuge** sind geprüft. Keine native Mauswiederholung, keine neuen Fenster/Fokusversuche und keine Browsersteuerung. C64 und sämtliche Originalmedien bleiben erhalten; keine Commits/Pushes.

Fünf absichtlich verfälschte PE-Nachweise (Cachewert, Fault-IP, Stack, Accessed-Bit, RF-Zustand) und vier verfälschte XMS-Nachweise (Rückkopie, Extended RAM, Blockende, freie Menge) werden verworfen. Auch der treiberlose Kontrolllauf kann den positiven XMS-Prüfer nicht bestehen. Private Nachweispfade und tatsächlich verwendete Assembly-Identitäten stehen im aktuellen [Implementierungsstand](../IMPLEMENTATION-STATUS.md). RAM-Dumps, Medien, Treiber und Vergleichsausgaben bleiben vollständig außerhalb von Git.

Architekturgrundlage: Intels [Intel486 Programmer’s Reference Manual von 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 5/6/9/10/11/22/24/26; insbesondere versteckte Segmentzustände, Modewechsel, Frames und RF-Retry. Die API-Prüfung folgt der ursprünglichen [Extended Memory Specification 3.0 von Microsoft/Lotus/Intel/AST](https://ps-2.kev009.com/basil.holloway/ALL%20PDF/Microsoft_XMS_3%5B1%5D.0_Specification.pdf), Funktionen 00h/07h/08h/09h/0Ah/0Bh/0Ch/0Dh. Keine fremden CPU-/Speichermanagerquellen oder OS-Binaries werden eingebettet.

LLDT/LTR und tatsächliche TSS-Interrupt-Stackwechsel sind jetzt separat nachgewiesen; Umfang und weiterhin fehlende Task-/Gate-Pfade stehen in [X86-TSS.md](X86-TSS.md). Als Nächstes folgen 4-KiB-Paging/TLB samt Fault-Reihenfolge und V86 mit echten Monitor-/EMM386-Tests. Danach x87 und die weiteren M3-Geräte/Snapshots nach Konzept. DOS4GW/Doom und Windows bleiben bis zu ihren wirklichen Gastprüfungen ausdrücklich offen.
