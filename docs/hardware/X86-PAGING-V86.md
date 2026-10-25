# 486-Paging, V86 und originales EMM386

Stand: 25. Oktober 2026. Nachfolgendes M3-Teilpaket auf dem [TSS-/Privilegstand](X86-TSS.md). **M3 insgesamt ist weiterhin offen.** Die hier beschriebene Paging-/V86-Matrix verwendet 8 MiB RAM und die eigene Referenzfirmware. Die anschließende [VCPI-/EMS-Erweiterung](X86-VCPI-EMS.md) prüft zusätzlich echte Protected-Clients, EMS-Bankwechsel und 4/8/16 MiB.

## MMU und TLB

| Bereich | Implementierter und geprüfter Umfang |
| --- | --- |
| Seitentabellen | Klassische zweistufige 32-Bit-Übersetzung mit 4-KiB-Seiten. CR3 liefert die physische Verzeichnisbasis; PDE/PTE werden über den physischen Bus gelesen. CR0.PG ohne PE erzeugt #GP. Keine PSE-/PAE-/CR4- oder NX-Erweiterung. |
| Rechte | U/S und R/W beider Stufen, CPL3 einschließlich V86, Supervisor-Schreiben mit CR0.WP=0/1. Rechte werden auch bei einem TLB-Treffer geprüft. GDT/LDT/IDT/TSS und innere Interrupt-Stacks verwenden die vorgeschriebenen Supervisor-Zugriffe. |
| Seitenfehler | #PF mit dem tatsächlichen linearen Fehlerbyte in CR2 und den 486-Bits P/W/U. Kein modernes Instruction-Fetch-Bit. Segmentfehler werden vor der Seitenübersetzung behandelt. Ein Hardware-Selektorfehler kann EXT tragen; ein Seitenfehlercode wird dadurch nicht verfälscht. |
| A/D | Accessed in gelaufenen Verzeichnissen/Tabellen und Dirty beim Schreibzugriff. Der Übergang von gecachtem Lesen zu Schreiben setzt Dirty. Änderungen an Tabellen oder A/D-Bits im RAM ersetzen den Cache nicht automatisch. |
| TLB | 32 Einträge in acht Sets mit vier Wegen; deterministische LRU-Ersetzung. CR3-Schreiben, auch mit demselben Wert, und PG-Wechsel invalidieren den Cache. INVLPG verwirft die betroffene Seite ohne Lesen ihres Inhalts. Keine Behauptung einer bestimmten 486-Stepping-Ersetzung oder Cache-/Bustaktpräzision. |
| Grenzübertritte | Vollständige Zwei-Seiten-Prüfung vor RAM-/MMIO-Operandenwirkungen, RMW-Prüfung vor dem Lesen, vollständige Tabellen-/Far-Pointer-Operanden und vorbereitete Interrupt-Stacks. Geschützte Faults stellen GPRs, ESP, Selektoren und Caches am Befehlsanfang wieder her; CR2 und bereits entstandene A/D-Wirkungen bleiben erhalten. Abgeschlossene REP-Elemente bleiben abgeschlossen. Kein allgemeiner Beweis sämtlicher x86-Fault-Kombinationen. |
| Diagnose | Eigene Zähler für Walks, Treffer, Invalidierungen und Seitenfehler. Die diagnostische Adressvorschau verändert weder TLB noch A/D und liest keine MMIO-Geräte zur Vorschau. DMA bleibt ein physischer Busmaster. |

## V86 und Monitor

- IRETD aus CPL0 lädt den echten Neun-Doppelwort-Rahmen mit CS/IP, Flags, SS/ESP und ES/DS/FS/GS. Die virtuellen Segmente haben Basis `Selektor << 4`, FFFFh-Grenze und 16-Bit-Standardgrößen; Präfixe bleiben wirksam. Schreibzugriffe mit CS-Override sind im V86-Modus erlaubt. Protected-Mode-Code-Schreibrechte dürfen hier nicht angewendet werden.
- Geschützte 32-Bit-Interrupt-/Trap-Gates führen in einen nicht conforming Ring-0-Monitor über den geladenen 32-Bit-TSS. Der volle virtuelle Rahmen, gegebenenfalls mit Fehlercode, bleibt erhalten; die Datenregister sind beim Monitoreintritt ungültig. IRETD lädt die virtuelle Umgebung wieder.
- PUSHF/POPF, CLI/STI, INT n und IRET beachten die V86-IOPL-Regeln. INT3 und INTO besitzen ihre eigenen Gate-/DPL-Regeln. Portzugriffe verwenden im V86-Modus auch bei IOPL3 die TSS-I/O-Bitmap, einschließlich breiter Zugriffe und des vom 486 gelesenen zusätzlichen Bitmapbytes. Bitmapzugriffe selbst sind Supervisor-Zugriffe und können eigene Seitenfehler auslösen.
- CPU-Tests prüfen auch Hardware-IRQ-Rückkehr, Trap-Gates, fehlende Bitmapwörter/Guard-Bytes, #GP vor einem unerlaubten Portzugriff und ausbleibende INS-/OUTS-Seitenwirkungen. Task-Gates und Hardware-Taskwechsel sind dadurch nicht implementiert.

Zusätzlich umgesetzt: BT/BTS/BTR/BTC einschließlich vorzeichenbehafteter Speicher-Bitstrings, SHLD/SHRD, BSF/BSR sowie LAR/LSL/VERR/VERW und ARPL. Die Typentabellen folgen der Intel486-Beschreibung. Nicht definierte Ergebnis-/Flagfälle besitzen teilweise ein deterministisches Entwicklungsverhalten, keinen Originalchip-Nachweis. Speicher-Schiebeoperanden prüfen ihren RMW-Zugriff auch bei effektivem Zähler null; Register und Flags bleiben dabei unverändert.

## BIOS-Ergänzungen für den Originaltreiber

EMM386 benötigte den zuvor fehlenden Dienst **INT15/AH87**. Der eigene BIOS-Code validiert die beiden normalen Datendeskriptoren, kopiert bis zu 8000h Wörter mit tatsächlich ausgeführtem PE/REP MOVSW und stellt CR0, GDTR, Stack, Register und den Fast-A20-Port wieder her. Die private BIOS-GDT liegt in der EBDA. Vor der Real-Mode-Rückkehr werden die benutzten Datensegmente auf FFFFh-Grenzen geladen; bloßes POP ES im Real Mode würde eine kurze versteckte Kopiergrenze erhalten. Paritäts-/NMI-/elektrische A20-Fehler und sämtliche historischen BIOS-Spezialformen sind nicht nachgebildet.

Der Originaltreiber verschiebt außerdem die **EBDA** und aktualisiert BDA 0040:000Eh sowie die konventionelle Speichergröße. Das BIOS folgt diesen Angaben nun für HDD-Verifikation, FDC-DMA/-Kopie/-Formatdeskriptoren, PS/2-BIOS/IRQ12 und eigene AH87-Strukturen. INT12 und INT15/C1 liefern die aktuellen BDA-Werte. Der feste POST-Ausgangswert bleibt 9FC0h. Der FDC besitzt zusätzlich einen wirklichen IRQ6-Abschluss mit BDA-Flag und PIC-EOI.

Die neue Abnahme prüft die vom Originaltreiber nach 000E92h verschobene EBDA, 640 KiB konventionellen Speicher und das Laden des Clients über FDC. CPU-/Gerätetests prüfen zusätzliche Verlagerungen und erhaltene Canaries an der alten Adresse, einschließlich des PS/2-Callbacks ohne Windows-Eingaben. Dies ist keine Freigabe beliebiger EBDA-Positionen, insbesondere eines DMA-Bounce-Sektors über eine 64-KiB-Kanalgrenze. Schreib-/Format-/VDS-Fälle unter EMM386 stehen noch aus.

Die automatisierte Tastatur meldet abgewiesene Scancodes bei vom Gast abgeschaltetem Controller, Scanning oder Übersetzungsmodus nicht mehr als zugestellt. Szenarien warten auf echte Gastausgaben; es werden weder DOS-Eingaben direkt in den Tastaturring geschrieben noch native Windows-Ereignisse erzeugt.

## Eigener Paging-/V86-Gast

```powershell
.\scripts\test-cpu-paging-v86.ps1 -SkipBuild -Disk <IDE-Basis> -Overlay <pristines-COW>
```

CPUPAGE.COM liegt allein auf einer neuen eigenen FAT12-Diskette. Je zwei echte HDD-Kaltstarts unter MS-DOS 6.22 und FreeDOS prüfen:

1. Eigene PDE/PTE-Strukturen und nicht identische Datenzuordnung; zunächst alter TLB-Wert nach PTE-Änderung, danach neuer Wert nach INVLPG; physische A/D-Bits und Daten.
2. Einen Supervisor-Schreibschutzfehler mit Fehlercode 3 sowie einen fehlenden PTE mit Fehlercode 0. Der eigene Handler behebt die zweite Zuordnung und wiederholt denselben ursprünglichen Befehl erfolgreich.
3. Tatsächlichen V86-Eintritt und Ring-0-Monitor mit vier #GP-Fällen für CLI, Port-I/O und INT-Aufrufe sowie einem Benutzer-Seitenschutzfehler mit Fehlercode 5. Exakte Fault-IPs, CR2, virtuelle Segmente/Stack, RF, Datenregister-Invalidierung und IRETD-Rückkehr.
4. Kontrollierte Rückkehr zu DOS, wiederhergestellte Tabellen/CR0/CR3 und VER/ECHO/Prompt. Pro Start drei Seitenfehler und fünf echte V86-Monitorein-/austritte. Der eigene private TR bleibt wie beim TSS-Probegast bis zum Reset geladen.

Ein unabhängiger Python-Prüfer kontrolliert 256 RAM-Bytes, tatsächliche NASM-Befehlsbytes/Labels, physische Seitentabellen und Nutzdaten sowie die CPU-Diagnose. Acht verfälschte RAM-/CPU-/Programmkontrollen werden abgelehnt. Wiederholungen sind je System zeit-, bild- und nachweisgleich. Der deutsche DISPLAY.SYS-Start führt nun selbst eine BIOS-AH87-Kopie aus; deshalb besitzen diese MS-DOS-Starts einen zusätzlichen PE-Ein-/Austritt gegenüber dem FreeDOS-Start.

## Originales EMM386 mit VCPI

```powershell
.\scripts\test-emm386.ps1 -SkipBuild -Disk <IDE-Basis> -Overlay <pristines-COW>
```

Das Skript verändert ausschließlich frische private COW-Kopien, durch reguläre DOS-COPY-/ECHO-/TYPE-Befehle. CONFIG.PRE sichert die Ausgangskonfiguration; davor stehen HIMEM.SYS `/TESTMEM:ON` und EMM386.EXE `NOEMS`. Ein eigener Client wird zusätzlich regulär kopiert. Die FAT-Prüfung verlangt unveränderte Originaldateien einschließlich beider Originaltreiber, identische FATs und keine verlorenen/kreuzverknüpften Ketten. Die kanonische Installation und alle ursprünglichen Medien bleiben unverändert.

Die Originaldatei EMM386.EXE aus den vorhandenen deutschen MS-DOS-6.22-Medien hat 121.950 Bytes und SHA-256 `ED73A76CEB7365B0FBE6BFD6040C19F310703418FEB8F5254D404915D1322673`. Der Treiber nennt in seinem eigenen Banner **Version 4.48**; das ist keine aus dem Betriebssystemnamen angenommene Revisionsnummer.

Die Matrix startet den Originaltreiber kalt und lädt EMMTEST.COM über IDE und FDC. Der Client prüft PE/SMSW, VCPI 1.00, gelesenes CR0 mit PE/PG und die PIC-Abbildungen 08h/70h. Er reserviert zwei echte 4-KiB-VCPI-Seiten, kopiert eigene 4096-Byte-Muster über die residenten AH87-Dienste in beide Seiten und zurück und gibt beide Seiten frei. Die freie Menge muss vorher und danach übereinstimmen. Unabhängige Prüfung aller physischen Seiten, durch CR3 übersetzter Clientpuffer, VCPI-Ergebnisse und tatsächlicher Monitorzähler; anschließend VER/ECHO/Prompt. Hinzu kommen ein Start ohne Monitor und acht verfälschte Oracle-Kontrollen.

**Noch keine vollständige EMM386-/DOS-Extender-Abnahme:** Diese grundlegende `NOEMS`-Matrix allein prüft keinen EMS-Seitenrahmen oder VCPI-Protected-Client-Eintritt. Eine [separate neue Matrix](X86-VCPI-EMS.md) weist inzwischen DE0Ch und EMS-Bankwechsel mit `RAM` nach; dafür wurde auch das zuvor als RAM dekodierte ISA-Optionsfenster korrigiert. Ein [weiteres Teilpaket](X86-DOS4GW.md) prüft inzwischen originales DOS4GW mit einer kurzen Doom-II-Demo; allgemeine DPMI-/Spielkompatibilität bleibt offen. Hardware-Tasks/Call-Gates, Debug-/Alignment-/LOCK- und weitere Fault-/Restart-Grenzfälle, x87, restliche M3-Geräte und koordinierte Snapshots bleiben offen. Windows 95, Pentium, Netzwerk und Voodoo sind weiterhin nicht abgenommen.

Aktuelle Buildzahlen und private Nachweispfade stehen im [Implementierungsstand](../IMPLEMENTATION-STATUS.md). GUI-/Fokus-/native Mausprüfungen wurden nicht gestartet; deren aufgeschobener M2-Rest bleibt offen.

Primärgrundlagen: Intels [Intel486 Programmer’s Reference Manual, 1992](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 5/6/8/9/23 und Instruktionstabellen in Kapitel 26; [Phoenix System BIOS, INT15/AH87](https://www.vtda.org/books/Computing/Programming/SystemBIOSforIBMPC_XT_ATComputersandCompatibles_Phoenix.pdf). Die dokumentierten VCPI-Aufrufregister werden zusätzlich gegen die [öffentliche Implementierung der Jemm-Autoren](https://github.com/Baron-von-Riedesel/Jemm/blob/master/src/VCPI.ASM) geprüft; RMW-Speicherzugriffe bei Schiebezähler null gegen den [Bochs-Referenzpfad](https://github.com/bochs-emu/Bochs/blob/master/bochs/cpu/shift32.cc). Das ersetzt keine Messung an Originalsilizium. Es wurden keine fremden CPU-/Monitorquellen oder Betriebssystemdateien eingebettet.
