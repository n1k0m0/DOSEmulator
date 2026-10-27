# x87: FBLD/FBSTP, gepackte Dezimalzahlen und Ausnahmeantworten

Stand: 25. Oktober 2026. Geprüftes M3-Teilpaket; vollständiges x87 und M3 bleiben offen.

## Verhalten

DF /4 lädt zehn BCD-Bytes mit 18 Dezimalziffern exakt auf den FPU-Stack. Die niedrige Halbbyteposition enthält die Einerstelle; Bit 7 des letzten Bytes ist das Vorzeichen. Das Modell erhält negative Null und ignoriert die übrigen sieben Bits des Vorzeichenbytes. PC24/53/64 und RC verkürzen das geladene Ergebnis nicht. Ein erfolgreicher Push löscht C1, ohne neue Precision-Ausnahme. Stackoverflow setzt IE/SF/C1; maskiertes Invalid pusht Indefinite, unmaskiertes Invalid erhält die acht Register und TOP.

DF /6 rundet ST(0) gemäß RC auf eine ganze Zahl und prüft anschließend die Grenze ±999999999999999999. Die Umsetzung verwendet den ganzzahligen Quotient-/Restkern mit expliziter Rundung; weder Host-double noch der native Testhelfer dienen zur Ausführung. PC reduziert die Konvertierung nicht. Signierte Null bleibt auch nach dem Runden kleiner negativer Zahlen erhalten. Denormal/Pseudo-Denormal kann Precision verursachen; FBSTP erzeugt dafür kein zusätzliches Denormal-Flag. C1 zeigt eine Inkrementierung des gerundeten Betrags an.

Precision ist hier eine Ausnahme nach Abschluss: **Auch bei PM=0 werden alle zehn Zielbytes geschrieben und der Stack gepoppt.** Ein folgender wartender Befehl meldet den pending Fehler über die vorhandene [Fehlerleitung](X86-X87-ERRORS.md). NaNs einschließlich QNaN, Unendlich, unsupported Formate und ein zu großes gerundetes Ergebnis erzeugen dagegen Invalid. Bei IM=0 bleiben Ziel, TOP und rohe Quellbits erhalten. Maskiertes Invalid schreibt die im Referenzmodell festgelegte BCD-Indefinite-Kodierung `00000000000000C0FFFF` und poppt. Ein leerer Stack setzt zusätzlich SF. Undefinierte niedrige Indefinite-Bits sind damit eine stabile Modellwahl, kein universeller Chipvertrag.

FBLD mit Ziffern A–F ist laut Intel und AMD undefiniert. Das Modell summiert deren Halbbytewerte deterministisch, ohne zusätzliche IE. **36 solche Eingaben sind ausschließlich Diagnosen**; sie sind weder gültige BCD-Zahlen noch Belege einer allgemeinen Architekturkompatibilität. C0/C2/C3 sind für beide Befehle undefiniert und aus dem nativen Architekturvergleich ausgeschlossen. Das Modell erhält sie stabil.

Der Decoder prüft Segmentrechte/-grenzen, die Ausrichtung und alle zehn Seitenzugriffe vor numerischen oder Schreibwirkungen. Das Referenzmodell verwendet acht Byte Ausrichtung und die vorhandene Reihenfolge Segmentfehler → AC → Daten-PF; auch EM/TS und pending MF werden berücksichtigt. Ausgewählte getrennte Hostprozesse bestätigen bei aktivem AC Erfolg an Offsets 0/8 und den Windows-Alignment-Status an 1/2/4. Das ist keine historische 486-/Stepping-/Bus- oder vollständige Exception-Prioritätsabnahme. Aliase, MMIO-Teilwirkungen und physische Busarbitration bleiben die bereits dokumentierten Grenzen.

## Nachweise

- **2446/2446 Regressionen, 0 Warnungen/Fehler; 121 neue CPU-Fälle.** Exakte 18-Ziffern-Werte, beide Vorzeichen/negative Null, drei PC/vier RC, Rundungsgrenzen, Invalid/Precision, leere/volle Stacks, zehn Byte lange Operanden, Segmentfehler, AC, NM vor Operand-PF, pending MF und TF nach abgeschlossenem Push/Store/Pop sind geprüft. Neun unterschiedliche Seitengrenzen je Befehl prüfen tatsächlichen PF, unveränderte FPU/Daten und Reparatur mit IRETD und erfolgreicher Wiederholung. Der Test invalidiert bewusst die zuvor geladene TLB-Abbildung.
- **6276 definierte native Zahlen-/Zustandsfälle plus 36 getrennte Diagnosen** stimmen mit reinem Kern und tatsächlichem Decoder überein. Ein unabhängiger Python-Integer-/Fraction-Prüfer bestätigt CW, definierte SW-Bits, Tags, aktive rohe 80-Bit-Register, zehn Speicherbytes und Wachen. Enthalten sind **660 pending Precision-Antworten mit Commit**, **1032 pending Invalid-Antworten ohne Commit** und **270 gespeicherte negative Nullen**. Die Referenz führt die tatsächlichen Instruktionen auf modernem AMD-x64 Family 26 Model 68 aus und sichert/restauriert den Host mit FXSAVE64/FXRSTOR64. Sie ist kein physischer 486.
- **FPBCD.COM besteht 16 echte Kaltstarts:** MS-DOS 6.22 und FreeDOS je zweimal im Real Mode, eigenem PE und eigenem 4-KiB-Paging sowie originales EMM386 NOEMS/RAM je zweimal. Je Start **220 Fälle, 39 pending unmaskierte Zustände, 42 Stackfehler, 60 C1-Rundungs-/Overflowantworten und 634 aktive 80-Bit-Registervergleiche**. Real/V86 führt **1994 ESC**, eigenes PE/Paging **2025 ESC** aus. Die Prüfung kontrolliert Quellbytes, Code, EFLAGS, FPU-Zeiger/Opcode, BCD-Zielbytes, Wachen, Pagingtabellen und DOS-Rückkehr. **1102 absichtlich verfälschte Gastnachweise werden verworfen.**
- Die acht PE-/Paging-Starts erzeugen je **drei echte MF-Rahmen an FWAIT**: Precision nach abgeschlossenem FBSTP von 1,5, Invalid mit unterdrücktem FBSTP von 10¹⁸ und Invalid nach unterdrücktem FBLD auf einen vollen Stack. Pending 108-Byte-Abbild, erhaltene beziehungsweise gepoppte Register, abgeschlossene beziehungsweise unveränderte Zielbytes, EIP/CS/FLAGS mit RF sowie FNSAVE/FRSTOR/FNCLEX/IRETD und erfolgreiche FWAIT-Wiederholung werden unabhängig geprüft. Insgesamt **24 tatsächliche MF-Rückkehrvorgänge**.

FPBCD restauriert alle ursprünglichen 108 FPU-Bytes bytegenau. Seine privaten PE-TR bleiben bis Reset geladen. Das ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Pending Zahlenfälle werden absichtlich mit nichtwartendem FNSAVE erfasst; tatsächliche MF-Rahmen besitzen separate Proben. Externe IRQ13/FERR bleibt zusätzlich durch den FPERR-Gast geprüft. Die letzte verschobene native Mauswiederholung wird nicht berührt.

## Reproduktion

```powershell
.\scripts\test-x87-bcd.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Zusätzliche `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` liefern die volle 16er-Matrix mit den privaten Original-EMM386-Konfigurationen. Das Skript erzeugt eigene COM-/Native-Programme und private Testmedien, benutzt Overlaykopien und prüft SHA-256 von Ausgangsmedien und allen getesteten DLLs. Fenster, Fokus und Windows-Eingaben sind nicht erforderlich. Gesamtnachweis und erneut ausgeführte Regressionen: [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

## Grenzen und nächster Schritt

Keine vollständige x87-/CPU-/Fault-/Bus-/Zyklus- oder historische Siliziumabnahme. Die begrenzte stumme Doom-II-Demo bleibt ein Extender-/Grafiknachweis. Als Nächstes folgt **FSCALE** mit ST(1)-Trunkierung, exponentieller Skalierung, Sonderwerten, PC/RC und maskierten/unmaskierten OE/UE-Antworten; anschließend FXTRACT, FPREM/FPREM1 und die übrigen transzendenten Operationen jeweils mit unabhängigen Zahlen- und Gastoracles. Übrige M3-Geräte/Snapshots und M4–M8 bleiben offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, Abschnitte 15.2.2.2, 16.4 und FBLD/FBSTP auf Seiten 26-77–79: BCD-Datenformat, exakte Konvertierung, negative Null, RC und undefinierte ungültige Ziffern. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569 Rev. 3.13, FBLD/FBSTP auf Seiten 226–229: Status- und Ausnahmevertrag des Referenzhosts. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf). Eigene Hardwaremessungen ergänzen diese Angaben und ersetzen keine historische Intel486-Hardware.
