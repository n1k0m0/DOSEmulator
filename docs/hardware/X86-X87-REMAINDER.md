# x87: FPREM/FPREM1, partielle Reduktion und Quotientenbits

Stand: 25. Oktober 2026. Referenzpaket für den eigenen 486-Kern. Vollständiges x87 und M3 bleiben offen. Historische Nachweise gelten für ihre jeweiligen Builds.

## Rechenkern und sichtbare Befehlsgrenzen

FPREM (`D9 F8`) und FPREM1 (`D9 F5`) ersetzen ST(0) durch den exakten Rest einer Division durch ST(1). FPREM schneidet den ganzzahligen Quotienten gegen null ab. FPREM1 wählt den nächsten geraden ganzzahligen Quotienten bei Gleichstand; dadurch kann der Rest das Vorzeichen wechseln. Bei einem Nullrest bleibt das Vorzeichen des Dividenden erhalten. PC24/53/64 und RC verändern das Ergebnis nicht. Es wird kein Precision-Fehler durch eine ungenaue Hostrechnung eingeführt.

`Float80Math.Remainder` verwendet BigInteger-Quotient und -Rest und die vorhandene explizite 80-Bit-Kodierung. Es rechnet weder über Host-double noch über den nativen Testhelfer. Bei einem normalisierten Exponentenabstand D kleiner als 64 entsteht ein vollständiger Rest mit C2=0. Der Betrag ist bei FPREM kleiner als der Betrag des Divisors, bei FPREM1 höchstens dessen Hälfte. Das gilt auch für FPREM1 bei D=−1; beispielsweise 0,75 modulo 1 ergibt −0,25. Die drei niedrigen Bits des **Quotientenbetrags** werden Q2→C0, Q1→C3 und Q0→C1 zugeordnet, unabhängig von den Vorzeichen beider Eingaben.

Bei D≥64 führt genau eine Gastinstruktion eine Teilreduktion aus und setzt C2=1. Das Referenzmodell verwendet AMDs dokumentierte Wahl **N=32+(D mod 32)** innerhalb des von Intel beschriebenen implementationsabhängigen Intervalls 32–63. Der temporäre Quotient wird bei beiden Befehlen gegen null abgeschnitten. Der Rest bleibt exakt und verliert mindestens 32 Exponentenstellen beziehungsweise wird null. C0/C1/C3 werden für diesen Teilpfad im Referenzmodell null. Der Gast muss den Befehl erneut ausführen, bis C2=0 gilt; es gibt keine versteckte Fertigrechnungsschleife im CPU-Interpreter.

Damit bleiben Gerätefristen, Interrupts, Debug-Traps und FPU-Kontextwechsel zwischen Teilreduktionen möglich. Die virtuelle Zeit pro Step wurde nicht geändert. CPU-Tests speichern und restaurieren tatsächliche partielle FPU-Abbilder zwischen Befehlen; TF erzeugt einen wirklichen Debugrahmen nach der ersten noch unvollständigen Reduktion.

## Sonderwerte, Fehler und Zustandsgrenzen

ST(1), TOP, Tags der übrigen Register und EFLAGS bleiben erhalten. Voller Stack, Größen-/Adresspräfixe und die niedrigen Signifikandbits bei PC24 sind geprüft. Quiet-/Signaling-NaNs verwenden den separat geprüften Zwei-Quellen-Payloadvertrag; Unsupported, ein unendlicher Dividend oder Divisor ±0 melden Invalid. **Divisor null erzeugt IE, keine neue ZE.** Ein unendlicher Divisor mit endlichem Dividend liefert dessen Wert, bei Pseudo-Denormal mit entsprechender Normalisierung. Denormale Quellen melden DE.

Unmaskierte IE/DE erhalten das ursprüngliche ST(0) und erzeugen keinen Teil-Commit. Maskierter Leerstack schreibt Indefinite nur nach ST(0); der Divisor und leere Quelltags bleiben nach dem dokumentierten Maskenvertrag erhalten. Ein exakter winziger Rest erzeugt bei maskiertem Underflow keine PE/UE; bei unmaskiertem Underflow wird der Ergebnisexponent um 24576 erhöht, der Ergebniswert übernommen und UE pending gemeldet. Die Ausnahmeleitung benutzt unverändert FERR/NE/MF des bestehenden Kerns.

C1/C2 werden auf den geprüften Invalid-/Denormal-/Stackausgängen gelöscht, C0/C3 im eigenen Referenzvertrag stabil erhalten. Diese Ausnahmeantworten sowie C0/C1/C3 des Teilpfads sind keine universelle historische Intel-/AMD-/Steppingzusage. Tatsächliche native Referenz, selbst formulierte Zahlengrenzen und eigene Gastproben definieren den geprüften Umfang. Widersprüchliche Tagabbilder, alle physischen Steppings sowie allgemeine Bus-/Timing-/Faultkonkurrenzen bleiben offen.

## Zahlen- und CPU-Nachweise

- **3272/3272 Regressionen, 0 Warnungen/Fehler, 330 neue CPU-Fälle.** Beide Quotientenrundungen, Vorzeichen, Halbwerte, alle acht Quotientenbitmuster, PC/RC, signed zero, größte/kleinste Exponenten, C2-Teilreduktionsketten, tatsächliches FNSAVE/FRSTOR zwischen Befehlen, genaue Denormalreste, unmaskierter Underflow, Leerstack, voller Stack und Präfixe sind geprüft. Hinzu kommen NM, LOCK/UD, tatsächlicher Fetch-PF mit Reparatur, TF nach einer partiellen Instruktion und MF-/FNCLEX-/IRETD-/WAIT-Rückkehr.
- **7972 unabhängige native Eingabefälle** prüfen einzelne Teilbefehle und vollständige Schleifen beider Varianten gegen wirkliche Host-x87. Der reine Kern und der tatsächliche Decoder stimmen in definiertem Zahlen-/Zustandsumfang, erhaltenem Divisor, Tags, Status, EFLAGS und Iterationszahl überein. Der Helfer führt dabei **138982 Remainderbefehle** aus; die längste Kette umfasst **670 Instruktionen**.
- Ein getrennt formulierter **Python-Integer-/Fraction-Nachweis zertifiziert alle 7972 Fälle**. Er berechnet den Quotienten aus rationalen Zahlen, bildet den Rest unabhängig zurück, prüft vollständige Restgrenzen beziehungsweise den Exponentenfortschritt jeder Teilstufe und kodiert die exakten Ergebnisse selbst. Er übernimmt weder den Produkt-Quantizer noch Host-float. Sonderwerte, Masken, Sticky-Status, unveränderte Quellen, alle definierten Status-/Tagantworten und die Zahl der Wiederholungen sind enthalten.

Der native Helfer nutzt FXSAVE64/FXRSTOR64 zum Sichern/Restaurieren des Hostzustands. Referenzhost ist moderner AMD-x64 Family 26 Model 68, kein physischer 486. Er wird ausschließlich zur Prüfung verwendet. Die Modellwahl der Teilreduktion ist dokumentiert; andere zulässige Prozessoren können andere Zwischenreste und Iterationszahlen liefern.

## Tatsächliche DOS-Proben und Fehlerrückkehr

**FPREM.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS je zweimal Real Mode, eigener PE und eigenes 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im wirklichen V86-/Monitorbetrieb.

Je Start werden **220 Eingaben**, **1070 tatsächliche Remainderinstruktionen**, 34 pending Umgebungen, zwölf Leerstackfälle und 428 aktive rohe 80-Bit-Register geprüft. 24 Eingaben enden absichtlich mit einem einzelnen noch partiellen Ergebnis; andere führen den echten Gastzweig bis C2=0 aus. Die längste Gastkette umfasst **305 Instruktionen**. Jeder Schritt liest FNSTSW, prüft C2 und Ausnahmezustand; ein erhaltener Zähler beweist die tatsächlichen Wiederholungen. EFLAGS werden um jeden Remainderbefehl gesondert erfasst und verglichen.

Real/V86 führt **3700 ESC**, privates PE/Paging **3747 ESC** aus. Rohe Restwerte, erhaltener Divisor, alle Bedingungsbits, Quellbytes, FPU-Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr sind unabhängig geprüft. Wiederholungen sind zeit-, bild- und nachweishashgleich; keine Gastwrites oder Resets. **694 absichtlich verfälschte Gastnachweise scheitern**, darunter Iterationszähler und MF-Abbilder.

Die acht privaten PE-/Paging-Starts erzeugen je fünf tatsächliche MF-Rahmen an WAIT nach IE, DE, UE sowie leerem ST(0)/ST(1): **40 neue MF-/IRETD-/WAIT-Rückkehrvorgänge**. Volle pending 108-Byte-Abbilder beweisen jeweils unterdrückten beziehungsweise übernommenen Ergebniswert, Quotientenbits und unveränderten Divisor. FNSAVE/FRSTOR/FNCLEX/IRETD und erfolgreiche WAIT-Wiederholung werden geprüft.

FPREM.COM restauriert die ursprünglichen vollständigen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; dies ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die übrigen Zahlenproben erfassen pending Zustände gezielt mit FNSAVE. Externe FERR-/IRQ13-Rückkehr bleibt der getrennte FPERR-Nachweis; ältere CPUX87-Proben besitzen keine volle FPU-Restaurierungszusage.

## Reproduktion und weiterer Umfang

```powershell
.\scripts\test-x87-remainder.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Das Skript erzeugt neue private Kopien, unabhängige Referenzzahlen/Integerzertifikate und drei eigene COM-Programme. Der private Gesamtnachweis steht über `artifacts/tests/m3-remainder-release-path.txt` bereit. Testprogramme und Eingabevektoren sind aus dem aktuellen Quellstand reproduzierbar; Originalmedien und Benutzer-Spiel-COWs bleiben unberührt.

Die zusätzliche gemeinsame Abnahme prüft auf denselben DLLs erneut FXTRACT, tatsächliche FPERR-Fehlerleitung und die bisherigen originalen DOS4GW-/Doom-/Kontroll-/Konfigurationspfade sowie die übrigen nativen Zahlenmatrizen. Die historische Performanceabnahme wird ohne neuen Regressionsanlass nicht als aktuelle Zeitmessung ausgegeben. Die kurze stumme Doom-Demo ist keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme.

Nächster eigener CPU-Schritt: **F2XM1**, danach FYL2X/FYL2XP1 und weitere transzendente Operationen einzeln mit unabhängigen Genauigkeits-, Zustands- und Gastnachweisen. Anschließend übrige M3-Geräte/Snapshots sowie M4–M8. Windows 95, Pentium, Netzwerk und Voodoo bleiben nicht abgenommen.

## Primärquellen

- [Intel SDM Volume 2A, FPREM/FPREM1, 3-367 bis 3-370](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): exakter Rest, Größenunabhängigkeit, vollständige/partielle Reduktion, Quotientenbits und Ausnahmen. Die Ausnahmenliste nennt Divisor null als Invalid.
- [AMD Architecture Programmer’s Manual Volume 5, Revision 3.13, 283 bis 286](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf): AMD-Primärhandbuch im Archiv, dokumentierte N-Wahl und Operationen beider Befehle. Die native Zahlenprüfung bestätigt den gewählten Referenzumfang einschließlich Vorzeichen und kleiner FPREM1-Quotienten.
