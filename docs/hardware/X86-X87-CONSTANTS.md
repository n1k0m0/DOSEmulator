# x87: Konstanten mit RC und vollständigen 80-Bit-Ergebnissen

Stand: 25. Oktober 2026. Geprüftes M3-Teilpaket; vollständiges x87 und M3 bleiben offen.

## Verhalten

Der Decoder unterstützt alle sieben Konstantenbefehle D9 E8–EE: FLD1, FLDL2T, FLDL2E, FLDPI, FLDLG2, FLDLN2 und FLDZ. Die fünf zuvor fehlenden Befehle laden log₂10, log₂e, π, log₁₀2 und ln 2. RC bestimmt das beobachtbare 64-Bit-Signifikandenergebnis; PC24/53/64 verkürzt diese Konstanten nicht. Weder ein neues Precision-Flag noch eine Precision-Ausnahme entsteht. Ein erfolgreicher Push setzt C1 auf null. +0 und +1 sind exakt, EFLAGS und die bisherigen Stackwerte bleiben erhalten.

Ein voller Stack erzeugt IE/SF mit C1=1. Bei maskiertem Invalid wird TOP dekrementiert und Indefinite geschrieben; bei unmaskiertem Invalid bleiben TOP und die acht Register erhalten. Nichtwartendes FNSAVE kann den pending Zustand erfassen. Ein folgender wartender Befehl folgt der vorhandenen [FERR-/IGNNE-/MF-Fehlerleitung](X86-X87-ERRORS.md). C0/C2/C3 sind undefiniert; die Architekturoracles schließen sie aus. Das eigene Modell erhält sie stabil. Der geprüfte maskierte sticky ZE-Zustand bleibt erhalten.

Die Emulation verwendet Ganzzahlkonstanten mit den geprüften unteren Nachbarn und nearest-Inkrementen. Sie ruft weder Host-Gleitkommafunktionen noch den nativen Testhelfer auf. Das bildet den beobachtbaren RC-Vertrag des Referenzmodells ab und behauptet keine Rekonstruktion sämtlicher interner 66-Bit-ROM-/Guardbits eines Originalchips. Insbesondere darf eine mathematisch auf 66 Bits gerundete Konstante nicht pauschal nochmals auf 64 Bits gerundet werden: Eine solche Doppelrundung kann das niedrige Ergebnisbit ändern. Der unabhängige mathematische Prüfer leitet die äußeren Ergebnisse direkt ab.

## Nachweise

- **2.261/2.261 Regressionen, 0 Warnungen/Fehler; 168 neue CPU-Fälle.** Alle Befehle, drei PC, vier RC, unmaskiertes Precision ohne neue PE, Stackoverflow/Commit, umlaufende freie Slots, Größen-/Adresspräfixe, NM vor Zustandseffekten, tatsächlicher MF mit Slot-Reparatur/IRETD/Wiederholung, Fetch-PF und TF nach dem tatsächlich abgeschlossenen Push sind geprüft.
- **15.120 unabhängige native Fälle** vergleichen den tatsächlichen Decoder und die reinen Konstanten mit separaten Host-x87-Instruktionen. Stacktiefen 0–8, Masken, C1-Vorbelegung, unterschiedliche undefinierte Vergleichsbits und sticky ZE sind enthalten. CW, definierte SW-Bits, Tags, alle aktiven rohen 80-Bit-Register und EFLAGS stimmen überein. Darunter **1.680 Overflows, davon 840 unmaskiert**. FXSAVE64/FXRSTOR64 sichert/restauriert den Host. Referenzhost ist moderner AMD-x64 Family 26 Model 68, **kein physischer 486**.
- Ein zusätzlicher unabhängiger Prüfer leitet ln/log-Konstanten mit Python-Decimal und π mit **16 atan(1/5) − 4 atan(1/239)** ab. Die bei **100 und 160 Dezimalstellen** gewonnenen äußeren Konstanten stimmen überein und bestätigen alle nativen Fälle samt Stack-/Ausnahmeregeln. Er verwendet kein Host-float, keine Produkttabelle und behauptet keine formale Rekonstruktion des internen Chip-ROMs.
- **FPCONST.COM besteht 16 echte Kaltstarts:** MS-DOS 6.22 und FreeDOS je zweimal im Real Mode, privaten PE32 und privaten 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im V86-/Monitorbetrieb. Je Start **220 Fälle, 56 Stackoverflows, 28 pending unmaskierte Zustände und 1.018 aktive 80-Bit-Registervergleiche**. Real/V86 **2.178 ESC**, privates PE/Paging **2.223 ESC**. Jeder Befehl hat eigene tatsächliche Opcodeadressen; die RAM-Prüfung kontrolliert Quellbytes, CW/SW/Tags, sämtliche aktiven Register, EFLAGS, Zeiger/Opcode, Wachen und Pagingtabellen. Wiederholungen stimmen in Zeit, Bild und Nachweishashes überein. Alle Starts erreichen VER/ECHO/DOS-Prompt ohne Gastwrites oder Reset.
- Die acht privaten PE-/Paging-Starts erzeugen je **drei tatsächliche MF-Rahmen an FWAIT** nach unmaskiertem Stackoverflow durch FLDL2T/FLDPI/FLDLN2. TOP und alle acht ursprünglichen Register müssen erhalten bleiben. Der Handler erfasst/restauriert das vollständige pending 108-Byte-Abbild, führt FNCLEX aus und kehrt mit IRETD zurück; FWAIT wird erfolgreich wiederholt. EIP/CS/FLAGS mit RF, CW, C1/IE/SF/ES/B, Tags, Opcode und die nach der Quittierung gespeicherten Bytes sind unabhängig geprüft. Insgesamt **24 tatsächliche Gast-MF-Rückkehrvorgänge**.
- **2.536 absichtlich verfälschte Gastnachweise** werden verworfen: jede Kombination aus Befehl/PC/RC, definierte Status-/Tagbits, niedrige Ergebnisbits, EFLAGS, Zeiger, MF-Abbilder/-Rahmen, Quellbytes, Wachen, Paging und CPU-Zähler. Ein Erfolgstext genügt nicht.

Der neue Gast restauriert alle ursprünglichen 108 FPU-Bytes bytegenau. Private PE-TR bleiben bis Reset geladen. Das ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Zahlenfälle erfassen pending Zustände mit nichtwartendem FNSAVE; die drei MF-Fälle werden gesondert tatsächlich zugestellt. Externe IRQ13/FERR besitzt weiterhin den getrennten FPERR-Gast. Ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierung.

## Reproduktion

```powershell
.\scripts\test-x87-constants.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Für die gesamte 16er-Matrix zusätzlich `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` mit den privaten EMM386-Konfigurationen angeben; sonst zwölf Starts. Das Skript erzeugt eigene COM-/Native-Programme und private Testmedien, benutzt private Overlaykopien und prüft SHA-256 von Ausgangsmedien und getesteten DLLs. Native Fenster oder Windows-Eingaben sind nicht erforderlich. Gesamtnachweis und erneut ausgeführte Regressionen: [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

## Grenzen und nächster Schritt

Keine vollständige x87-/CPU-/Fault-/Bus-/Zyklus- oder historische Siliziumabnahme. Undefinierte Bedingungsbits, rohe leere Registerinhalte, interne ROM-Bits und alle historischen Steppings sind nicht allgemein abgenommen. Die kurze stumme Doom-II-Demo bleibt eine begrenzte Extender-/Grafikprobe.

Als Nächstes folgen FBLD/FBSTP mit gepacktem BCD, Vorzeichen, Rundung, Grenzen und Speicher-/Ausnahmeantworten samt unabhängigen Zahlen-/DOS-Gastoracles. Weitere/transzendente Operationen, übrige M3-Geräte/Snapshots und M4–M8 bleiben offen. Die auf später verschobene native Mauswiederholung bleibt unangetastet.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, Abschnitt 17.6 und Kapitel 26, Seiten 26-103–104: Konstantenbefehle, interner 66-Bit-Wert, RC und unterdrücktes Precision. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569, FLD1/FLDL2E/FLDL2T/FLDLG2/FLDLN2/FLDPI/FLDZ: Status-/Ausnahmevertrag des Referenzhosts. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf). Die Hostprüfung ersetzt keine historische 486-Hardware.
