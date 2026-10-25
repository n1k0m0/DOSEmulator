# x87: FSQRT mit ganzzahliger Wurzel und expliziter Rundung

Stand: 25. Oktober 2026. Geprüftes M3-Referenzpaket; vollständiges x87 und M3 bleiben offen.

## Verhalten

FSQRT (D9 FA) ersetzt ST(0) durch seine Quadratwurzel. PC24/53/64 bestimmt die Ergebnispräzision, RC wählt nearest/even, −∞, +∞ oder gegen null. TOP, die übrigen Register und EFLAGS bleiben erhalten. Beide signierten Nullen behalten ihre Bits, +∞ bleibt +∞. Negative Zahlen einschließlich −∞ und negativer Denormals liefern Invalid; −0 bleibt gültig. Quiet-NaNs behalten ihren Payload, Signaling-NaNs werden bei maskiertem Invalid gequieted. Nicht unterstützte Formate liefern maskiert Indefinite. Unmaskiertes Invalid oder Denormal erhält die ursprünglichen rohen Operandbits. Unmaskiertes Precision übernimmt das gerundete Ergebnis und meldet pending PE. Maskierte sticky Flags bleiben erhalten.

Der Rechenkern arbeitet ausschließlich mit BigInteger. Eine ganzzahlige Newton-Iteration ermittelt die abgerundete Wurzel eines skalierten Radikanden. Exakte Quadrate und ein Vergleich mit dem quadrierten Mittelpunkt entscheiden anschließend über Inexact, nearest/even und die gerichtete Rundung. Es gibt eine Rundung auf die gewählte Präzision. Weder Host-double noch der native Testhelfer sind Teil der Emulation. Die Wurzel jedes positiven endlichen 80-Bit-Operanden liegt im normalen endlichen Exponentenbereich; FSQRT erzeugt daraus kein Overflow oder Underflow.

C1 bezeichnet die geprüfte Vergrößerung des positiven Ergebnisses bei der Rundung. C0/C2/C3 sind undefiniert; das Modell erhält sie stabil, die Architekturoracles schließen sie ausdrücklich aus. Der Decoder verwendet die vorhandenen [NM-/FERR-/IGNNE-/MF-Pfade](X86-X87-ERRORS.md). Maskierter Leerstack erhält Indefinite mit IE/SF, unmaskierter bleibt leer. Größen-/Adresspräfixe erzeugen keinen Speicheroperanden. Ein Fetch-PF vor dem zweiten Opcodebyte lässt FPU-Operand, Status und Zeiger unverändert; nach PTE-Reparatur und IRETD wird FSQRT wiederholt.

## Nachweise

- **2.093/2.093 Regressionen, 0 Warnungen/Fehler; 161 neue CPU-Fälle.** Feste Wurzeln und sqrt(2) mit bekannten niedrigen Signifikandbits prüfen PC/RC, Masken, signierte Null, Sonderformate, Denormal/Pseudo-Denormal, vollen/leeren Stack, Präfixe, NM, echte MF-/FNCLEX-/IRETD-Rückkehr und Fetch-PF mit erfolgreicher Wiederholung.
- **14.176 unabhängige native Fälle** vergleichen Rechenkern und tatsächlichen Decoder mit separaten Host-x87-Instruktionen. Enthalten sind exakte Quadrate, Rundungsmittelpunkte und Nachbarn, niedrige Signifikandbits, extreme Exponenten, alle drei PC/vier RC, IE-/DE-/PE-Maskenkombinationen, NaNs/unsupported, leere Quellen und sticky Status. CW, definierte SW-Bits/C1, Tags, alle aktiven 80-Bit-Register und EFLAGS stimmen überein. FXSAVE64/FXRSTOR64 sichert/restauriert den Testhost. Referenz ist moderner AMD-x64 Family 26 Model 68, **kein physischer 486**.
- Ein zusätzliches **unabhängiges rationales Intervalloracle** bestätigt **6.251 positive endliche Ergebnisse**, darunter **1.824 exakte Wurzeln** und **1.545 Aufrundungen**, sowie **278 abgebrochene Denormaloperationen**. Es berechnet keine Wurzel: Es prüft die quadrierten Grenzen zwischen benachbarten darstellbaren Ergebnissen, exakte Mittelpunktgleichheit, gerade Tie-Bits und C1/DE/PE. Python-Fraction benutzt ausschließlich Ganzzahlen. Sonderwerte und Leerstack bleiben durch die native und die CPU-Matrix getrennt geprüft.
- **FPSQRT.COM besteht 16 echte Kaltstarts.** MS-DOS 6.22 und FreeDOS je zweimal Real Mode, privater PE32 und privates 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im V86-/Monitorbetrieb. Je Start **220 Zahlenfälle, 77 pending Zustände, 26 Leerstackfälle, 28 C1-Aufrundungen und 424 aktive 80-Bit-Registervergleiche**. Real/V86 führt **1.795 ESC**, privates PE/Paging **1.819 ESC** aus. Originale Quellbytes, CW/SW/Tags, EFLAGS, Befehls-/Datenzeiger samt Opcode, Wachen und Pagingtabellen werden geprüft. Wiederholungen stimmen in Zeit, Bild und Nachweishashes überein; alle erreichen VER/ECHO/DOS-Prompt ohne Gastwrites oder Reset.
- Die acht privaten PE-/Paging-Starts erzeugen zusätzlich je **drei wirkliche MF-Rahmen an FWAIT** nach FSQRT mit unmaskiertem Precision, Denormal und Invalid. Gesicherte EIP/CS/FLAGS samt RF und volles pending 108-Byte-Abbild werden geprüft. Der Handler speichert/restauriert das pending Abbild, quittiert mit FNCLEX und kehrt über IRETD zurück; FWAIT wird wiederholt. Ergebnis sqrt(2), die ursprünglichen Denormalbits und der ursprüngliche Wert −1 sind bytegenau geprüft. Insgesamt **24 tatsächliche Gast-MF-Rückkehrvorgänge**; Gastdaten und CPU-Zähler müssen übereinstimmen.
- **592 absichtlich verfälschte Gastnachweise** werden verworfen: Präzision/Rundung, definierte Statusbits, Tags, niedrige 80-Bit-Bits, EFLAGS, Zeiger/Opcode, MF-Abbilder/-Rahmen, Code/Quellbytes, Wachen, Paging und CPU-Zähler. Ein Erfolgstext genügt nicht.

FPSQRT restauriert seine vollständigen ursprünglichen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen. Das ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die Zahlenmatrix erfasst pending Zustände mit nichtwartendem FNSAVE; die drei MF-Fälle sind gesondert tatsächlich zugestellt. Externe FERR-/IRQ13-Fehler besitzen weiterhin den getrennten FPERR-Gast. Ältere CPUX87-Proben restaurieren kein vollständiges FPU-Abbild.

## Reproduktion

```powershell
.\scripts\test-x87-sqrt.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Für die gesamte 16er-Matrix zusätzlich `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` mit den privaten EMM386-Konfigurationen angeben. Ohne diese drei Argumente sind es zwölf Starts. Das Skript assembliert den separaten nativen Helfer und eigene COM-Programme, prüft die rationalen Zertifikate und verwendet ausschließlich private Medien/Overlaykopien. SHA-256 sichert Ausgangskonfigurationen und getestete DLLs. Kein natives Fenster oder Windows-Eingabe ist erforderlich.

Abnahmepfade, erneut geprüfte FRNDINT-/Fehlerleitungs-/DOS4GW-Pfade und der Gesamtnachweis stehen im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

## Grenzen und nächster Schritt

Die gemessenen unmaskierten Antworten gelten für das dokumentierte Referenzmodell. Keine vollständige x87-/CPU-/Fault-/Bus-/Zyklus- oder historische Siliziumabnahme. Undefinierte Bedingungsbits, rohe leere Registerinhalte und alle Originalchip-Steppings sind nicht allgemein abgenommen. Die kurze stumme Doom-II-Demo bleibt eine begrenzte Extender-/Grafikprobe.

Die übrigen x87-Konstanten besitzen inzwischen ein eigenes [geprüftes Konstantenpaket](X86-X87-CONSTANTS.md). Als Nächstes folgen BCD und weitere/transzendente Operationen mit eigenen Oracles. Übrige M3-Geräte/Snapshots und M4–M8 bleiben offen. Die verschobene native Mauswiederholung bleibt unverändert offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, Kapitel 26, FSQRT, Seite 26-129: Opcode, Wurzel aus ST(0), signierte Null und Flags/Ausnahmen. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569, FSQRT, Seiten 301–302: ergänzender Status-/Ausnahmevertrag des Referenzhosts. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf). Das ersetzt keine historische 486-Hardware.
