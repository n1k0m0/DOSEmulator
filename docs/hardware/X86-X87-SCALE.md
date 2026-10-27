# x87: FSCALE, volle Signifikanden und Exponentengrenzen

Stand: 25. Oktober 2026. Abgenommenes M3-Referenzpaket; vollständiges x87 und M3 bleiben offen.

## Verhalten und Umsetzung

D9 FD ersetzt ST(0) durch ST(0) × 2^trunc(ST(1)). Der Skalenwert wird unabhängig von RC gegen null abgeschnitten. TOP, ST(1), die übrigen Register und EFLAGS bleiben erhalten. PC24/53/64 verkürzt das Ergebnis nicht. Die Umsetzung verwendet ausschließlich explizite 80-Bit-Werte und Ganzzahlen; kein Host-double und keine Ausführung über den nativen Testhelfer.

Das normale Ergebnis erhält alle 64 Signifikandbits. Der eigene Kern verändert den binären Exponenten und verwendet bei notwendiger Denormalisierung die vorhandene exakte Quotient-/Rest-Rundung mit RC und voller Präzision. C1 zeigt die Inkrementierung des Betrags an. C0/C2/C3 sind undefiniert; das Modell erhält sie stabil und schließt sie aus dem nativen Architekturvergleich aus. Erfolgreiche exakte Ergebnisse löschen C1. Bereits maskiertes sticky ZE bleibt erhalten; FSCALE erzeugt keine neue Zero-Divide-Ausnahme.

Der Decoder behandelt beide benötigten Stackplätze vor numerischen Wirkungen. Ein fehlendes ST(0) oder ST(1) liefert IE/SF/C1=0. Maskiert wird nur ST(0) mit Indefinite gefüllt; unmaskiert bleiben Daten und Tags erhalten. Es gibt keinen Pop. Unsupported-Formate, NaNs und die Sonderpaare Null mit positiv unendlichem Skalenwert beziehungsweise Unendlich mit negativ unendlichem Skalenwert besitzen getrennte Invalid-/Commitantworten. Die NaN-Auswahl folgt dem bereits separat geprüften Zwei-Quellen-Vertrag. Ein unmaskierter Denormalfehler erhält die ursprünglichen ST(0)-Bits; bei Maskierung wird weitergerechnet. Pseudo-Denormal kann dabei normalisiert werden, auch bei Skaleninteger null.

Die gemeinsame RequireFpu-/Fetch-/Debug-/Fehlerleitung bleibt erhalten. EM/TS meldet NM vor FPU-Änderungen; pending native Fehler werden vor der Ausführung behandelt. Die geprüften Präfixe ändern die Registerform nicht. LOCK ist ungültig und liefert UD vor Operand-/Zeigerwirkungen. Ein über eine fehlende Codeseite geteilter Opcode wird nach tatsächlichem PF und IRETD-Reparatur wiederholt; TF beobachtet das abgeschlossene Ergebnis.

## Überlauf, Unterlauf und Präzision

Bei maskiertem Overflow hängt Unendlich gegenüber größtem endlichem Wert von Vorzeichen und RC ab; OE/PE werden gesetzt. Unmaskierter Overflow beziehungsweise Underflow verschiebt den Ergebnisexponenten um −24576 beziehungsweise +24576. Ein weiterhin nicht darstellbares Ergebnis wird zu signiertem Unendlich beziehungsweise Null. Dieser zweite Grenzfall ist für FSCALE wesentlich: ein einfaches Abschneiden auf 15 Exponentenbits wäre falsch. Die Primärquelle beschreibt diese Antworten ausdrücklich für FSCALE; die beobachteten Flags und C1-Antworten des Referenzhosts sind zusätzlich unabhängig geprüft.

Bei maskiertem Underflow liefert der Kern die passend gerundete Denormalzahl oder Null und erzeugt UE/PE bei Genauigkeitsverlust. Unmaskierter Underflow reagiert auch auf ein exaktes winziges Ergebnis. Ein unmaskierter Precision-Fehler übernimmt das abgeschlossene gerundete Ergebnis; ein folgender wartender Befehl meldet den pending Zustand. In den geprüften doppelten Exponentengrenzfällen werden zusätzlich PE und das beobachtete C1 für Unendlich beziehungsweise Null erzeugt. IE/DE unterdrücken dagegen den Ergebnis-Commit, wenn die jeweilige Ausnahme unmaskiert ist.

Der ganzzahlige Skalenwert wird im Produkt bei ±65536 begrenzt. Diese Grenze liegt bereits außerhalb jeder für ein endliches erweitertes Eingabeformat wiedergewinnbaren Exponentenlage, auch nach der 24576-Verschiebung. Dadurch entstehen keine riesigen Zwischenverschiebungen für einen ST(1)-Wert nahe 2^16383. Das unabhängige Python-Oracle verwendet einen beliebig großen Integer-Skalenwert und prüft die Exponentenungleichungen vor jeder Bitverschiebung; es übernimmt diese Begrenzung nicht.

## Zahlen- und CPU-Nachweise

- **2755/2755 Regressionen, 0 Warnungen/Fehler; 209 neue CPU-Fälle.** RC/PC, niedrige Signifikandbits, Abschneiden positiver/negativer Halbwerte, beide Vorzeichen, normale und doppelte Exponentengrenzen, IE/DE/OE/UE/PE, sticky ZE, leere/volle Stacks und erhaltenes ST(1) sind geprüft. Hinzu kommen Präfixe, LOCK/UD, NM, echter Fetch-PF mit Reparatur, TF nach Abschluss und fünf tatsächliche MF-/FNCLEX-/IRETD-/WAIT-Rückkehrfälle.
- **35152 native Zahlen-/Zustandsfälle** stimmen zwischen reinem Kern, tatsächlichem Decoder und separatem Host-x87-Helfer überein. Derselbe Vektorsatz ist zusätzlich durch ein unabhängig formuliertes Python-Integer-Oracle vollständig zertifiziert: Signifikanden, Exponenten, verworfene Bits, RC/C1, Masken, Status/Tags, Quellen und Stack. Enthalten sind **1193 doppelte Overflow- und 1270 doppelte Underflowfälle**, 9997 normale exakte Fälle, 5756 Aufrundungen und 10275 pending unmaskierte Zustände.
- Die native Referenz führt wirklich FSCALE aus und sichert/restauriert den Host mit FXSAVE64/FXRSTOR64. Referenzhost ist moderner AMD-x64 Family 26 Model 68, kein physischer 486. Daraus folgt keine universelle historische Intel-/AMD-/Steppingzusage. Der Helfer ist ausschließlich Testcode.

## Tatsächlicher DOS-Gast

**FPSCALE.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS je zweimal im Real Mode, eigenem PE und eigenem 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start werden **220 Zahlenfälle**, 66 pending Umgebungen, 24 Leerstackfälle, 26 C1-Aufrundungen und 420 aktive rohe 80-Bit-Register geprüft. Real/V86 führt **1795 ESC**, privates PE/Paging **1840 ESC** aus. Befehls-/Datenzeiger samt Opcode, Quellbytes, EFLAGS, Status/Tags, Wachen, Pagingtabellen und DOS-Rückkehr werden unabhängig verglichen. Wiederholungen sind zeit-, bild- und nachweishashgleich; keine Gastwrites oder Resets. **706 absichtlich verfälschte Gastnachweise werden verworfen.**

Die acht privaten PE-/Paging-Starts erzeugen je **fünf echte MF-Rahmen an FWAIT** nach Overflow, Underflow, Precision, Denormal und Invalid. Das volle pending 108-Byte-Abbild beweist getrennt den übernommenen gewickelten Exponenten, den abgeschlossenen gerundeten Denormalwert und die bei DE/IE unveränderten Operanden. ST(1), EIP/CS/FLAGS mit RF, FNSAVE/FRSTOR/FNCLEX/IRETD und erfolgreiche WAIT-Wiederholung werden geprüft. Insgesamt **40 tatsächliche MF-Rückkehrvorgänge**.

FPSCALE restauriert alle ursprünglichen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; keine vollständige CPU-/PIC-/Scheduler-Wiederherstellung. Die normalen Zahlenfälle erfassen pending Zustände gezielt mit nichtwartendem FNSAVE. Der getrennte aktuelle FPERR-Gast prüft weiterhin externe IRQ13/FERR; die neue Zahlenmatrix ist keine zusätzliche allgemeine Fehlerleitungsabnahme.

Auf denselben endgültigen sieben DLLs bestehen außerdem erneut **16 BCD- und 16 FPERR-Starts sowie acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts**. Insgesamt **67 Kaltstarts, 2236 verworfene Gastoracles und 93502 definierte native x87-Fälle plus 36 getrennte BCD-Diagnosen**. 62 PowerShell-Skripte/204 Checks und 65 Python-Prüfer bestehen. Der vorherige Performancevergleich bleibt Nachweis seines 2546er-Builds; für FSCALE wurden seine 200 Starts und Zeitmessungen nicht pauschal wiederholt.

## Reproduktion und Grenzen

```powershell
.\scripts\test-x87-scale.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

`-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` ergänzen die vollständige 16er-Matrix. Das Skript erzeugt eigene COM-/Native-Programme und private Testmedien, verwendet Overlaykopien und prüft die Ausgangsmedien/DLLs. Vektoren, Includes, Nativecode und alle drei COM-Varianten sind aus den aktuellen Quellen zusätzlich identisch regeneriert. Gesamtnachweis über `artifacts/tests/m3-scale-release-path.txt`, konkrete Details im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

Zehn geschützte Eingaben und 53 private Baselines bleiben unverändert. Benutzer-Spiel-COW und C64Emulator wurden nicht verändert. Keine nativen Fenster/Fokusversuche/Windows-Eingaben, Commits oder Pushes. Die verschobene Mauswiederholung bleibt offen. Keine vollständige x87-/M3-/Task-/Fault-/Bus-/Zyklus- oder historische Siliziumabnahme. Die kurze stumme Doom-II-Probe bleibt ein begrenzter Extender-/Grafiknachweis.

Als Nächstes folgt **FXTRACT**, danach FPREM/FPREM1 und weitere/transzendente Operationen jeweils mit unabhängigen Zahlen- und Gastoracles. Übrige M3-Geräte/Snapshots und M4–M8 bleiben offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, FSCALE auf Seite 26-124 und Abschnitte 16.2.5.1/16.2.5.2: Abschneiden des Skalenwerts und Grenzantworten nach der 24576-Anpassung. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569 Rev. 3.13, FSCALE auf Seiten 295–296: Befehl, erhaltene Flags sowie Status-/Ausnahmen des Referenzhosts. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf).
