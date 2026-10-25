# x87: 80-Bit-Zustand, Stack und Speicherabbilder

Stand: 25. Oktober 2026. Geprüftes M3-Teilpaket des frühen 486DX-Referenzprofils. Das nachfolgende [Numerikpaket](X86-X87-NUMERIC.md) ergänzt inzwischen elementare Arithmetik und reelle/Integer-Konvertierungen. Das spätere [Fehlerleitungspaket](X86-X87-ERRORS.md) ergänzt inzwischen ausgewählte FERR-/IGNNE-/IRQ13- und #MF-Gastpfade. **Die vollständige x87 und M3 sind insgesamt nicht abgenommen.**

## Register und Befehle

`Float80Value` speichert einen expliziten 64-Bit-Signifikanden einschließlich Integerbit und ein 16-Bit-Vorzeichen-/Exponentenfeld. Es gibt acht physische Register; TOP bildet ST(0) bis ST(7) darauf ab. Leere ist ein eigener physischer Zustand. Die vollständigen Tags werden aus Leere und tatsächlichem Zahlenformat gebildet. Speicherabbilder enthalten die Register dagegen in **logischer Reihenfolge ab ST(0)**. Kein `double`, `float` oder Host-FPU-Zustand ersetzt die 80 Bits.

Neu umgesetzt:

| Bereich | Befehle und Vertrag |
| --- | --- |
| Rohdaten | FLD m80, FSTP m80 mit allen zehn Bytes, auch niederwertigem Signifikandbit, signierter Null und NaN-Nutzlasten |
| Stack | FLD ST(i), FXCH, FST/FSTP ST(i), FFREE, FINCSTP/FDECSTP; Quellwert vor Änderung von TOP sichern |
| Einfache Registeroperationen | FCHS, FABS, FXAM, FLD1, FLDZ; keine daraus abgeleitete allgemeine Arithmetikfreigabe |
| Umgebung | FLDENV/FNSTENV, Kontroll-/Status-/Tagwort, letzter arithmetischer beziehungsweise Daten-/Stackbefehl, Opcode und Zeiger |
| Vollständiges Abbild | FRSTOR/FNSAVE mit allen acht Rohregistern; FNSAVE initialisiert anschließend die FPU |

Die bisherigen FNINIT/FNCLEX/FLDCW/FNSTCW/FNSTSW/FNOP/FSETPM- und EM-/TS-#NM-Pfade bleiben erhalten. Kontrollbefehle überschreiben die gespeicherten Befehls-/Datenzeiger nicht. FNOP speichert seinen ersten Präfixbytepunkt als Befehlszeiger. FNINIT/FNSAVE leeren die Tags und setzen den Kontrollzustand zurück; die nun leeren Register behalten im Referenzmodell ihre Rohbits. Diese stabilen Bits sind keine Zusage über undefinierte Registerinhalte jedes Originalchips.

## Speicherformate und Modi

Die Operandengröße wählt 14 beziehungsweise 28 Bytes Umgebung und 94 beziehungsweise 108 Bytes vollständigen Zustand. Adressgröße und Segmentwahl bleiben davon getrennt.

| Modus / Operandengröße | Befehls- und Datenzeiger |
| --- | --- |
| Real/V86, 16 Bit | gepackte lineare 20-Bit-Adressen, Opcode im oberen Befehlszeigerwort |
| Real/V86, 32 Bit | dieselben linearen 20-Bit-Adressen, obere vier Adressbits in Bits 28–31 der getrennten oberen Felder |
| Protected Mode, 16 Bit | 16-Bit-Offsets und CS-/Datenselektor; kein Opcodefeld |
| Protected Mode, 32 Bit | volle 32-Bit-Offsets, CS-/Datenselektor und elf Opcodebits |

Die 16-Bit-PE-Umgebung kann einen zuvor größeren Offset nur gekürzt speichern. Beim Laden dieses Formats wählt das Referenzmodell für den nicht enthaltenen Opcode null. Reservierte Wortfelder der breiten Umgebung werden nach dem dokumentierten Referenzlayout geschrieben; konkrete reservierte Bits und über Modusgrenzen geladene Zeiger sind keine allgemeine Stepping-Zusage. Die eigenen Gäste sichern ihren ursprünglichen Zustand vor PE im Real-Format und restaurieren ihn erst nach der Real-Mode-Rückkehr.

FNSTENV speichert die vorherige Umgebung und maskiert danach die sechs live wirksamen Ausnahmen. FLDENV/FRSTOR übernehmen TOP und Leer-Tags; nichtleere Tagtypen folgen den tatsächlichen Rohwerten. Ein getesteter Save/Restore mit TOP=7 unterscheidet physische Tags ausdrücklich von der logischen Reihenfolge der acht gespeicherten Werte.

## Fehler und Speicherwirkungen

Maskierte Stacküber-/unterläufe setzen IE/SF und C1 entsprechend dem geprüften Fall und verwenden den erweiterten Indefinite-Wert `FFFF:C000000000000000`. Ein unmaskierter leerer FSTP verändert weder Zielbytes noch TOP; die Ausnahme bleibt zunächst pending. Mit CR0.NE=1 liefert der nächste wartende Befehl beziehungsweise FWAIT #MF. FNSTENV/FNSAVE und die bisherigen nichtwartenden Kontrollbefehle können den Zustand ohne diesen vorgelagerten #MF speichern oder löschen. **Diese Ausnahmefälle sind CPU-Regressionen; die DOS-Gastmatrix prüft maskierte Stackfehler.** Weitere numerische Ausnahmen sind inzwischen im [Numerikpaket](X86-X87-NUMERIC.md), ausgewählte NE=0-Warte-/IRQ13-/IGNNE- und NE=1/#MF-Gastpfade im [Fehlerleitungspaket](X86-X87-ERRORS.md) geprüft.

Blocktransfers prüfen Segmentgrenze, Ausrichtung und sämtliche Seiten, bevor Rohregister oder Datenbytes übernommen werden. Die physischen Byteadressen werden vor der Datenwirkung festgehalten. Die geprüften RAM-Seitenfehler lassen FPU-Zustand und erstes Seitenteil unverändert; Seitentabellen-A/D-Wirkungen werden dadurch nicht zurückgerollt. Das ist keine allgemeine Zusage atomarer MMIO-/Alias-/Teil-Schreib- oder Siliziumfehlerreihenfolge. Der historische 486-TEMPREAL-Operand verlangt bei aktivem #AC **acht Byte Ausrichtung**; Umgebungen verwenden zwei oder vier. Die vorhandene Referenzreihenfolge Segmentfehler → #AC → Daten-#PF bleibt erhalten.

Der neue Speicherpfad meldet tatsächliche Datenzugriffe an die Debugarchitektur. Ein geprüfter FNSAVE-Datenbreakpoint trappt nach gespeichertem Registerabbild und FPU-Initialisierung. CPU-RESET ohne BIST erhält weiterhin den FPU-Zustand; FNINIT ist davon getrennt.

## Echte DOS-Probe und unabhängiger Nachweis

`tests/guests/x87-state-test.asm` und die gemeinsame `x87-state-body.inc` erzeugen **FPSTATE.COM**. Die Real/V86-Variante läuft ohne private CR-/Descriptoränderungen auch unter originalem Microsoft EMM386. Zwei weitere Varianten verwenden privaten PE32-Code, wahlweise mit eigenen klassischen Identitätsseiten. Alle Varianten werden vom echten DOS-Kernel über FDC geladen, führen 51 tatsächliche ESC-Befehle aus und kehren zu originalem VER/ECHO/Prompt zurück.

```powershell
.\scripts\test-x87-state.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Zusammen mit `-ConfiguredDisk <private-Konfigurationsbasis> -NoEmsOverlay <NOEMS.cow> -RamOverlay <RAM.cow>` entstehen **16 Kaltstarts**, je zweimal:

- MS-DOS 6.22 und FreeDOS im Real Mode, im eigenen Protected Mode und mit eigenem Paging.
- Originales EMM386 NOEMS und RAM im tatsächlichen V86-/Monitorbetrieb.

Die Gastwerte enthalten unter anderem `1 + 2^-63`, negative Null, einen negativen normalen Wert, größte Normalzahl, kleinste Denormalzahl, Unendlich und NaN-Nutzlasten. Das Python-Oracle liest die tatsächlichen zehn Bytes und berechnet die erwarteten Registerpositionen, Tags und Bitmuster unabhängig ohne Gleitkommaoperationen. Es prüft alle acht Register in beiden Saveformaten, sämtliche vier Umgebungsformate, Opcode-/Zeigerfelder, veränderten Stack und geleerten Drain, beide Konstanten, Stackfehler und Pufferwächter. Unter EMM386 übersetzt es die echten Seitentabellen; bei privatem Paging kontrolliert es alle 1.024 PTEs. Auch die ausgeführten NASM-Bytes werden mit RAM verglichen.

**376 absichtlich verfälschte Nachweise** scheitern, darunter niederwertige Registerbits in jeder Slotposition, Umgebung, Zeiger, Tags, Wächter, Opcodebytes, CPU-Bericht und PTE. Wiederholungen sind zeit-, bild-, Nachweis- und Abbildhash-gleich. Der ursprüngliche vollständige 108-Byte-FPU-Zustand wird gesichert, nach DOS-Rückkehr restauriert und erneut bytegenau verglichen. Damit besitzt dieser neue Gast eine vollständige geprüfte FPU-Wiederherstellung. Die ältere CPUX87-Kontrollprobe besitzt diese Zusage weiterhin nicht. **Die privaten PE-Varianten behalten ihren TR bis Reset; das ist kein vollständiger CPU-Snapshot.**

**90 neue CPU-Regressionen**, insgesamt **1548/1548**, ergänzen die Gäste: Rohwerte einschließlich Signaling-NaN/Unnormal, Stack-Aliase, FXAM, Tags, alle Speicherformate, maskierte/unmaskierte Stackfehler, FWAIT/#MF, EM/TS, Seitenübertritte, #AC und Debug-Trap nach FNSAVE. Wegen der geänderten FPU- und Speicherpfade werden die bisherigen Hardware-Task-/#NM- und DOS4GW-/x87-Kontrollgäste auf denselben DLLs zusätzlich geprüft. Private Einzel-/Gesamtnachweise und deren genaue Zahlen stehen im [Implementierungsstand](../IMPLEMENTATION-STATUS.md).

## Nächster Umfang und Primärquellen

Das nachfolgende [Numerikpaket](X86-X87-NUMERIC.md) prüft inzwischen elementare Addition/Subtraktion/Multiplikation/Division sowie m32-/m64-/Integer-Konvertierungen mit ganzzahligem Kern, expliziter Rundung/Präzisionskontrolle und eigenen DOS-Zahlenproben. Nächste Vertikale ist die modellgerechte FERR-/IGNNE-Leitung. Numerische Vergleiche, BCD, Quadratwurzel, Ganzzahlrundung und weitere Arithmetik bleiben offen. Lange Spiel-/Soundpfade, Originalchip-Timing und Windows bleiben gesonderte Abnahmen.

Primärgrundlagen: Intels [486 Programmer’s Reference Manual](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 15 (Register/Tags/Opcode/Umgebungsformate), Tabelle 9-6 (TEMPREAL-Ausrichtung) und Befehlsverträge; Intels [Software Developer’s Manual, Band 2A](https://cdrdv2-public.intel.com/812383/253666-sdm-vol-2a.pdf), FLD/FST/FSAVE/FRSTOR und Legacy-Umgebungen. Die späteren Handbücher ersetzen keine Prüfung historischer Steppings. Es wurde kein fremder FPU-/Emulatorcode übernommen.
