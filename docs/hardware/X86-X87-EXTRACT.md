# x87: FXTRACT und tatsächlicher Zwei-Ergebnis-Stackwechsel

Stand: 25. Oktober 2026. Referenzpaket für den eigenen 486-Kern. Vollständiges x87 und M3 bleiben offen. Historische Nachweise anderer Pakete gelten für ihre dokumentierten Builds.

## Zahlen und Stack

FXTRACT (`D9 F4`) zerlegt das ursprüngliche ST(0) exakt in Signifikand und Exponent. Der Exponent ersetzt das alte ST(0); anschließend wird der Signifikand gepusht. Damit enthält das neue ST(0) den vorzeichenbehafteten Signifikanden mit wahrem Exponenten null, ST(1) den ursprünglichen ganzzahligen Exponenten als vollständige 80-Bit-Zahl. TOP wird einmal dekrementiert. Die übrigen Register und EFLAGS bleiben erhalten.

`Float80Math.Extract` verwendet Ganzzahlbits und die vorhandene exakte Integerkonvertierung. PC24/53/64 und alle vier RC verändern beide Ergebnisse nicht; niedrige Signifikandbits bleiben erhalten. Denormals werden normalisiert, wobei der Exponent bis −16445 reichen kann. Pseudo-Denormals werden mit dem effektiven kleinsten normalen Exponenten behandelt und melden DE. Es gibt keinen Host-double-Ersatz und keinen nativen Ausführungspfad im Emulator.

Maskierter Zero-Divide liefert bei ±0 den unveränderten signierten Nullsignifikanden und −∞ als Exponenten. Bei ±∞ bleibt das Vorzeichen des Signifikanden erhalten, der Exponent ist +∞. Quiet-NaNs werden in beide Ergebnisse übernommen; Signaling-NaNs werden bei Maskierung mit erhaltenem Payload gequieted. Unsupported-Formate liefern zweimal Indefinite. IE, DE und ZE haben getrennte maskierte beziehungsweise unmaskierte Antworten. Eine unmaskierte Operandausnahme erhält TOP, Tags und sämtliche rohen Register; es entsteht kein halber Push. FXTRACT erzeugt weder Precision noch Overflow/Underflow als numerische Ausnahmen. Vorhandenes maskiertes sticky OE bleibt erhalten.

Quellregister und neuer Stackplatz werden vor beiden Änderungen geprüft. Ein fehlendes Quellregister gewinnt im festgelegten Referenzvertrag auch gegenüber einem gleichzeitig belegten Pushplatz: IE/SF mit C1=0. Andernfalls meldet der belegte Pushplatz IE/SF mit C1=1. Bei maskiertem Stackfehler werden beide Ergebnisse Indefinite und der Push wird abgeschlossen. Unmaskiert bleiben alle Register, Tags und TOP erhalten. Erfolgreiche numerische Ausführung löscht C1. Undefinierte C0/C2/C3 bleiben im Modell stabil erhalten und sind aus dem nativen Architekturvergleich ausgeschlossen.

## CPU- und unabhängige Zahlenprüfung

- **2942/2942 Regressionen, 0 Warnungen/Fehler, 187 neue CPU-Fälle.** PC/RC, höchste/niedrigste Exponenten, niedrige Bits, Sonderwerte, alle Stacktiefen 0–8, freie Zielplätze, leere Quellen und innere Löcher sind geprüft. Dazu gehören die vollständige Erhaltung roher Bits bei unmaskierten Fehlern, Präfixe, NM vor Zustandsänderung, LOCK/UD, tatsächlicher Fetch-PF mit Reparatur, TF nach dem abgeschlossenen Zwei-Ergebnis-Commit und MF-/FNCLEX-/IRETD-/WAIT-Rückkehr. Ein FXTRACT/FSCALE/FSTP-ST(1)-Rundweg erhält die Originalzahl und die übrigen Register.
- **13815 tatsächliche native Zahlen-/Zustandsfälle** vergleichen den reinen Kern und den wirklichen Decoder mit einem getrennten Host-x87-Helfer. Definierte CW-/SW-/Tagantworten und alle aktiven rohen 80-Bit-Register stimmen überein. Enthalten sind 1704 Quell-Unterläufe, 168 Stacküberläufe sowie Null, beide Unendlich-Vorzeichen, Quiet-/Signaling-NaNs, Unsupported, Denormal/Pseudo-Denormal und zufällige volle Signifikanden.
- Ein getrennt formulierter **Python-Integer-/Fraction-Nachweis zertifiziert denselben vollständigen Vektorsatz**. Er berechnet den physischen Stack, beide Commit-/Abbruchantworten, Tags und Ausnahmezustände unabhängig. Bei **9808 endlichen Zahlenfällen** rekonstruiert er außerdem die ursprüngliche signierte rationale Zahl exakt aus dem berechneten Signifikanden und 2 hoch Exponent. Er verwendet weder Host-float noch Produktcode oder die FSCALE-Implementierung.

Der native Helfer sichert/restauriert den Host über FXSAVE64/FXRSTOR64. Der Referenzhost ist moderner AMD-x64 Family 26 Model 68, kein physischer 486. Sonderwert-/Stackpriorität und C1-Antworten sind ein ausdrücklich begrenzter, geprüfter Referenzvertrag; daraus folgt keine universelle Intel-/AMD-/historische Stepping-, Bus- oder Zykluszusage. Widersprüchlich geladene Tags und undefinierte rohe Inhalte leerer Originalchip-Register sind nicht allgemein abgenommen.

## Tatsächliche DOS-Gäste und Fehlerrahmen

**FPEXTR.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS je zweimal im Real Mode, privatem PE und privatem 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start werden **220 Zahlen-/Stackfälle, 46 pending Umgebungen, 32 Stackfehler, acht C1-Stacküberläufe und 1054 aktive rohe Register** geprüft. Real/V86 führt **2280 ESC**, privates PE/Paging **2326 ESC** aus. Beide Ergebnisse, erhaltene Register, Quelldaten, CW/SW/Tags, EFLAGS, Befehls-/Datenzeiger samt Opcode, Wachen und Pagingtabellen werden unabhängig ausgewertet. Wiederholungen sind zeit-, bild- und nachweishashgleich. Alle Starts erreichen VER/ECHO/Prompt; keine Gastwrites oder Resets. **676 absichtlich verfälschte Gastnachweise scheitern.**

Die acht privaten PE-/Paging-Starts erzeugen zusätzlich je **fünf tatsächliche MF-Rahmen an FWAIT** nach ZE, DE, Invalid, Quell-Unterlauf und Stacküberlauf. Das volle pending 108-Byte-Abbild beweist erhaltene Quellen, TOP, Tags und fehlende Teil-Commits. Bei der leeren Quelle bleibt der Datenzeiger null. EIP/CS/FLAGS mit RF, FNSAVE/FRSTOR/FNCLEX/IRETD und die erfolgreiche WAIT-Wiederholung werden geprüft: insgesamt **40 tatsächliche MF-Rückkehrvorgänge**.

FPEXTR restauriert die vollständigen ursprünglichen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; dies ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die übrige Zahlenmatrix erfasst pending Zustände gezielt mit nichtwartendem FNSAVE. Externe FERR-/IRQ13-Rückkehr bleibt der getrennte FPERR-Nachweis. Ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Reproduktion und Grenzen

```powershell
.\scripts\test-x87-extract.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Das Skript legt neue private Testkopien an, erzeugt Eingabevektoren, unabhängige native Referenz, Integerzertifikat und drei eigene DOS-Programme, prüft echte Gastzustände und hält die getesteten DLL-Hashes fest. Die COM-Programme und Referenzzahlen werden aus aktuellem Quellstand reproduzierbar erzeugt. Originalmedien und Benutzer-Spiel-COWs bleiben unberührt.

Maßgeblicher privater Gesamtnachweis: `artifacts/tests/m3-extract-release-path.txt`. Auf denselben sieben DLLs werden das benachbarte FSCALE-Paket, tatsächliche FPERR-Fehlerleitung und originale DOS4GW-/Doom-/Kontroll-/Konfigurationspfade geprüft; ältere Zahlenmatrizen besitzen zusätzlich frische native Decodervergleiche. Die historische Performanceabnahme wird ohne neuen Anlass nicht als aktuelle Zeitmessung ausgegeben. Die kurze stumme Doom-Demo ist keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme.

Nächster eigener CPU-Schritt: **FPREM/FPREM1**, einschließlich vollständiger und partieller Reduktion, C2/Quotientenbits, Sonderwerten, Stack-/Ausnahmeantworten und eigenen tatsächlichen DOS-Gästen. Danach weitere x87-Operationen und übrige M3-Pakete. Windows 95, Pentium, Netzwerk und Voodoo sind weiterhin nicht abgenommen.

## Primärquellen

- [Intel SDM Volume 2A, FXTRACT, 3-421/3-422](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): Opcode, Reihenfolge der beiden Ergebnisse, Nullantwort, dokumentierte Ausnahmen und undefinierte Bedingungsbits.
- [AMD Architecture Programmer’s Manual Volume 5, Revision 3.13, FXTRACT, 330/331](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf): AMD-Primärhandbuch im Archiv, Stack-/Zero-Divide-/Denormal-Vertrag. Die tatsächliche native Referenz und unabhängigen Zustandsoracles konkretisieren den dokumentierten eigenen Modellumfang.
