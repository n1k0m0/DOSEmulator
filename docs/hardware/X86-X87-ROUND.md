# x87: FRNDINT und integrale 80-Bit-Ergebnisse

Stand: 25. Oktober 2026. Eigenes geprüftes M3-Referenzpaket; vollständiges x87 und M3 bleiben offen.

## Verhalten

FRNDINT (D9 FC) rundet ST(0) gemäß RC auf einen ganzzahligen Wert im 80-Bit-Format. Unterstützt sind nearest/even, gegen −∞, gegen +∞ und gegen null. PC24/53/64 verkürzt das Ergebnis nicht: Auch beispielsweise 2⁶³+1 behält sein niedriges Signifikandbit. Bereits integrale große Werte, beide Unendlichkeiten und vorzeichenbehaftete Nullen bleiben erhalten. Der Stack wird weder gepusht noch gepoppt, andere Register und EFLAGS bleiben unverändert.

Die Implementierung verwendet den vorhandenen exakten Ganzzahlquotienten samt Rest und eine explizite Rundungsentscheidung. Sie rechnet weder mit Host-double noch durch Aufruf der nativen Referenz. C1 zeigt die geprüfte Vergrößerung des Betrags bei der Rundung an. Nichtintegrale endliche Eingaben setzen Precision. Ein unmaskiertes Precision übernimmt im geprüften Modell das gerundete Ergebnis und meldet die pending Ausnahme. Ein unmaskiertes Denormal erhält den ursprünglichen Wert und setzt nur Denormal. Maskiertes Denormal wird gerundet und kann zusätzlich Precision setzen. Maskierte Signaling-NaNs werden mit ihrem Payload gequieted; nicht unterstützte Formate liefern Indefinite. Bei unmaskiertem Invalid bleiben die ursprünglichen rohen Bits erhalten. Quiet-NaNs allein erzeugen kein Invalid. Maskierter Leerstack erzeugt Indefinite mit IE/SF; unmaskierter Leerstack bleibt leer.

C0/C2/C3 sind für diesen Befehl architektonisch undefiniert. Das eigene Modell erhält ihre alten Werte stabil. Native und Gastoracles schließen diese undefinierten Bits ausdrücklich aus dem Architekturvergleich aus. Sie sind keine Behauptung über einen bestimmten Intel-/AMD-Chip. Vorhandene maskierte sticky Ausnahmebits bleiben erhalten.

Der Decoder verwendet die bestehende [#NM-, FERR-/IGNNE- und #MF-Fehlerleitung](X86-X87-ERRORS.md). Größen-/Adresspräfixe erzeugen keinen Speicheroperanden. Der geprüfte Seitenfehler beim Fetch des zweiten Opcodebytes erhält den ungerundeten Wert, den Status und die ursprünglichen FPU-Zeiger; nach reparierter PTE und korrekter IRETD-Rückkehr wird derselbe FRNDINT ausgeführt.

## Nachweise

- **1.932/1.932 Regressionen, 0 Warnungen/Fehler; 113 neue CPU-Fälle.** Alle vier RC, drei PC, positive/negative Halbwerte, nearest/even, negative Null, ganze 64-Bit-Werte, NaNs/Formate, Denormal, voller/leerer Stack, Präfixe, NM, tatsächliche MF-/IRETD-Rückkehr und Fetch-PF mit Befehlswiederholung sind geprüft.
- **8.160 unabhängige native Fälle** vergleichen den reinen Ganzzahlkern und den tatsächlichen Decoder mit separaten Host-x87-Instruktionen. Die Eingaben umfassen Zahlen unmittelbar an Rundungsgrenzen, hohe/niedrige Exponenten, niedrige Signifikandbits, Sonderwerte, PC/RC, die Kombinationen der IE-/DE-/PE-Masken, leere Quellen und sticky Status. CW, definierte SW-Bits einschließlich C1, Tags, alle aktiven 80-Bit-Register und EFLAGS werden geprüft. Der Helfer sichert/restauriert den Host mit FXSAVE64/FXRSTOR64. Referenzhost: moderner AMD-x64 Family 26 Model 68, **kein physischer 486**.
- **FPRND.COM besteht 16 echte Kaltstarts.** MS-DOS 6.22 und FreeDOS je zweimal im Real Mode, privatem PE32 und privatem 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im tatsächlichen V86-/Monitorbetrieb. Je Start **220 Zahlenfälle, 82 pending unmaskierte Zustände, 28 Leerstackfälle, 41 C1-Rundungen und 421 aktive 80-Bit-Registervergleiche**. Real/V86 führt **1.800 ESC** aus, privates PE/Paging **1.824 ESC**. Originale Quellbytes, CW/SW/Tags, EFLAGS, Befehls-/Datenzeiger und Opcode, Pufferwachen, Pagingtabellen und vollständige ursprüngliche 108 FPU-Bytes werden unabhängig geprüft. VER, ECHO und DOS-Prompt werden wieder erreicht, ohne Gastwrites oder Reset. Wiederholungen stimmen in virtueller Zeit, Bild und Nachweishashes überein.
- Die acht privaten PE-/Paging-Starts erzeugen je **drei wirkliche #MF-Rahmen an FWAIT**: nach FRNDINT mit unmaskiertem Precision, Denormal und Invalid. Gesicherter EIP/CS/FLAGS samt RF und pending 108-Byte-FPU-Abbild müssen passen. Der Handler speichert das pending Abbild, restauriert es, führt FNCLEX aus und kehrt über IRETD zurück. FWAIT wird erfolgreich wiederholt. Der folgende vollständige Save beweist das gerundete Ergebnis 2 beim Precisionfall und die ursprünglichen rohen Bits beim Denormal-/Signaling-NaN-Fall. Insgesamt **24 echte Gast-MF-Rückkehrvorgänge**; die CPU-Zähler müssen mit den Gastdaten übereinstimmen.
- **592 absichtlich verfälschte Nachweise** werden verworfen, einschließlich Rundungskontrollen, Status/Tags, niedrigen Ergebnisbits, FPU-Zeigern, EFLAGS, MF-Abbildern/-Rahmen, Quellbytes, Paging und Wachen. Ein Erfolgstext allein genügt nicht.

Der neue Gast restauriert sein vollständiges ursprüngliches FPU-Abbild bytegenau. Sein privater PE-TR bleibt bis Reset geladen. FPU-Restaurierung bedeutet keinen vollständigen CPU-/PIC-/Scheduler-Snapshot. Die Zahlenmatrix speichert ihre pending Zustände gezielt mit nichtwartendem FNSAVE; die zusätzlichen MF-Fälle sind gesondert tatsächlich zugestellt. Externes IRQ13/FERR besitzt weiterhin den getrennten FPERR-Gast. Ältere CPUX87-Proben restaurieren kein vollständiges FPU-Abbild.

## Reproduktion

```powershell
.\scripts\test-x87-round.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Für die gesamte 16er-Matrix zusätzlich `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` mit den vorhandenen privaten EMM386-Konfigurationen angeben. Ohne diese drei Argumente sind es zwölf Starts. Das Skript assembliert einen separaten nativen Testhelfer und eigene COM-Programme, erzeugt private Testdisketten und kopiert Ausgangs-Overlays. SHA-256-Prüfungen sichern Originale, private Ausgangskonfigurationen und getestete DLLs ab. Medien, RAM-Dumps und Spielbilder bleiben ignoriert und privat. Native Fenster oder Windows-Eingaben sind nicht erforderlich.

Abnahmepfade, erneut geprüfte Grundarithmetik/Fehlerleitung/DOS4GW und der Gesamtnachweis stehen im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md). Die vorherigen FCOM/FUCOM/FTST-Regeln und deren begrenzter Vertrag für unmaskiertes Invalid bleiben im [Vergleichspaket](X86-X87-COMPARE.md) dokumentiert.

## Grenzen und nächster Schritt

Keine vollständige x87-/CPU-/Fault-/Bus-/Zyklus- oder Originalchip-Abnahme. Undefinierte C0/C2/C3, rohe leere Registerinhalte, widersprüchlich geladene Tags und jedes historische Stepping sind nicht als Siliziumpräzision abgenommen. Die gemessenen unmaskierten Antworten gelten für das dokumentierte Referenzmodell. Die kurze stumme Doom-II-Demo ist weiterhin keine allgemeine Spiel-/Sound-/Eingabefreigabe.

FSQRT besitzt inzwischen ein eigenes [geprüftes Wurzelpaket](X86-X87-SQRT.md) mit PC24/53/64, vier RC, ganzzahliger Berechnung und tatsächlichen DOS-Gästen. Danach weitere Konstanten, BCD und transzendente Operationen sowie übriges M3 und M4–M8. Die verschobene native Mauswiederholung bleibt unverändert offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, 15.1/16.2 und FRNDINT in Kapitel 26, Seite 26-119: Rundung gemäß RC, numerische Ausnahmen und undefinierte C0/C2/C3. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569, FRNDINT: ergänzender Befehls-/Statusvertrag des Referenzhosts. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf). Die Hostprüfung ersetzt keine historische 486-Hardware.
