# M3: x87-Fehlerleitung FERR/IGNNE, IRQ13 und #MF

Stand: 25. Oktober 2026. Getestetes Teilpaket des eigenen 486DX-Referenzmodells. **M3 und vollständiges x87 bleiben offen.** Zahlenkern und Registerformate: [X86-X87-NUMERIC.md](X86-X87-NUMERIC.md), [X86-X87-STATE.md](X86-X87-STATE.md).

## Vertrag von CPU und Plattform

Die CPU trennt eine pending numerische Ausnahme von deren Zustellung. FERR wird aus den sechs Statusflags und ihren Masken bestimmt und unabhängig von CR0.NE an die Plattform gemeldet. NE=1 liefert vor dem nächsten wartenden Befehl einen Fehlerrahmen für Vektor 16. NE=0 hält denselben Befehl bei inaktivem IGNNE an seiner ersten Präfixadresse an. Register, Daten, Befehlszähler, TF und RF werden dabei nicht wie nach einer abgeschlossenen Instruktion fortgeschaltet. Hardwareinterrupts können den Wartezustand verlassen; virtuelle Geräte laufen weiter. Aktives IGNNE lässt den wartenden Befehl bei NE=0 trotz pending Ausnahme ausführen. Bei NE=1 beeinflusst es #MF nicht.

Die AT-artige Plattform verriegelt die steigende FERR-Anforderung auf IRQ13. Ein Byte-Schreiben beliebigen Inhalts nach Port F0 löscht diese Verriegelung und aktiviert IGNNE, solange FERR ansteht. Das Löschen oder Maskieren der FPU-Ausnahme nimmt FERR/IGNNE zurück. Die Verriegelung und ein bereits gesetztes PIC-IRR bleiben davon getrennt. Interrupt-Acknowledge und beide PIC-EOIs sind weitere eigene Schritte. Mehrere maskierte Fehler können im PIC zu einer Anforderung zusammenfallen.

Nichtwartend sind FNCLEX, FNINIT, FNSAVE, FNSTENV, FNSTCW und beide FNSTSW-Formen. Die alten FNENI/FNDISI/FNSETPM-Encodings sind im 486-Modell ebenfalls nichtwartende, ignorierte Kompatibilitätsbefehle. Ein vorangestelltes echtes FWAIT bleibt eine eigene Instruktion. ESC prüft CR0.EM/TS auf #NM; FWAIT prüft dagegen MP zusammen mit TS. Segment-/Fetch-/Speicherfehler konkurrieren weiterhin mit dem Befehl; die nachgewiesenen Datenpfade prüfen die pending Ausnahme vor dem Zugriff auf den Datenoperanden.

Der eigene BIOS-Eintrag für IRQ13/Vektor 75h schreibt F0, quittiert beide PICs und ruft den numerischen Eigentümer über INT 02h. Er enthält keine FPU-Instruktion und ändert keinen numerischen Zustand. DOS darf einen eigenen IRQ75-Eintrag vorschalten. Die initialen PIC-Masken bleiben erhalten; ein Gast muss IRQ13/Cascade für externe Fehler freigeben. Der Testgast installiert seinen eigenen INT02-Eigentümer und restauriert die ursprünglichen Vektoren.

CPU-Hot-Reset bleibt der bestehende Reset ohne BIST: Der Wartezustand endet, die FPU und versorgte Geräte bleiben erhalten, einschließlich einer bestehenden Fehlerverriegelung/PIC-Anforderung. Ein neues `PcMachine` erzeugt einen neuen Graphen. Das ist ein expliziter Vertrag dieser Plattform, keine Zusage für jede Hauptplatine, Resetverkabelung oder elektrische Pinfolge. Port F1/287-Reset, Pinlaufzeiten und historische FPU-Zyklen sind nicht implementiert.

## Nachweise

- **1.714/1.714 Regressionen, 0 Warnungen/Fehler; 63 neue CPU-/Gerätefälle.** Geprüft werden alle sechs restaurierten unmaskierten Flags, NE/IGNNE, Maskenwechsel, beide Fehlerleitungen, Nicht-WAIT-Kontrollen, #NM-Vorrang, FNSAVE-/FNSTENV-Seitenfehler ohne vorzeitiges Löschen, blockiertes TF/RF, echte Fehler-/Interrupt-Rahmen, IRET-Wiederholung, PIC-IRR/ISR/EOI, Hot-Reset und fortlaufende Gerätefristen.
- **FPERR.COM: 16 tatsächliche Kaltstarts.** MS-DOS 6.22 und FreeDOS jeweils zweimal im Real Mode, eigenem PE32 und eigenem Paging; originales EMM386 NOEMS und RAM je zweimal im V86-/Monitorbetrieb. Jeder Start erzeugt drei externe Fehler. IRQ13 ist zuerst maskiert; der Timer findet die angehaltene Präfixadresse und gibt IRQ13 frei. IRQ75 sieht dieselbe Adresse und den pending Status. Erst OUT F0 lässt den wartenden FNOP im numerischen Eigentümer bei weiterhin gesetztem Status fertig werden; danach folgen FNCLEX, IRET und die erfolgreiche ursprüngliche Instruktion.
- Die acht privaten PE-Starts erzeugen zusätzlich je **drei echte #MF-Rahmen mit RF**, getrennte FNCLEX-/IRETD-Rückkehr und Wiederholung. IRQ13 bleibt dabei maskiert, obwohl FERR entsteht. Ein anschließender tatsächlicher IRQ13 leert den zusammengefallenen PIC-Eintrag; das ist kein bloßer EOI. Die vier Paging-Starts erzeugen je **zwei nachfolgende Daten-PF**, reparieren dieselbe PTE und wiederholen FADD erfolgreich. Die numerische Quelle bleibt bei Zero-Divide erhalten; der anschließende Speicheroperand liefert bytegenau den 80-Bit-Wert 3.
- Insgesamt führt jeder Real-/V86-Start **51 ESC**, jeder private PE-/Paging-Start **97 ESC** aus. Ein unabhängiger Python-Prüfer vergleicht Präfix-/Rückkehradressen, ursprüngliche FLAGS, pending/gelöschte CW/SW, Timer-/IRQ-/INT02-/MF-/PF-Zähler, rohe Ergebnisbytes, den tatsächlichen IRQ75-Eintrag, restaurierte Vektoren/PIC-Masken und IRQ13-IRR/ISR. Berichtete CPU-/Plattformzähler müssen dazu passen. Wiederholungen stimmen in Bild, virtueller Zeit und Zustands-/Zahlenhash überein. VER, ECHO und DOS-Prompt werden erreicht.

Der neue Gast restauriert sein ursprüngliches **volles 108-Byte-FPU-Abbild** bytegenau. Der private PE-TR bleibt bis Reset geladen. Die restaurierten PIC-Masken und geprüften IRQ13-Bits sind kein vollständiger PIC-/CPU-/Scheduler-Snapshot. Die ältere CPUX87-Probe hat weiterhin keine vollständige FPU-Restaurierung. Die endgültigen privaten Abnahmepfade, Gegenprüfungszahlen und erneut ausgeführten Regressionen stehen im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

## Reproduktion

```powershell
.\scripts\test-x87-errors.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Für die vollständige 16er-Matrix zusätzlich `-ConfiguredDisk`, `-NoEmsOverlay`, `-RamOverlay` mit den vorhandenen privaten EMM386-Testkonfigurationen angeben. Ohne diese drei Argumente werden zwölf Starts geprüft. Das Skript assembliert eigene COM-Programme, erzeugt neue Testdisketten und kopiert vorhandene Overlays in neue private Dateien. Originale, private Ausgangskonfigurationen und getestete DLLs werden per SHA-256 auf Unverändertheit geprüft. RAM-Dumps, Medien und Spielbilder bleiben ignoriert und privat. Keine nativen Fenster oder Windows-Eingaben nötig.

## Grenzen und nächster Schritt

Dies ist keine vollständige IRQ-/NMI-/Reset-/Debug-Schatten-/Fetch-/Task-/Fault-/MMIO-/Bus- oder Originalchip-Präzision. Die Zeit pro Interpreter-Schritt bleibt angenähert. Die eigene Firmware ersetzt keinen originalen BIOS-ROM-Test. Die neue Gastmatrix prüft kontrollierte Fehler und Wiederholungen, kein beliebiges DOS-Programm und keine Windows-Kompatibilität.

[FCOM/FUCOM/FTST und Speichervergleiche](X86-X87-COMPARE.md) besitzen inzwischen eine eigene Abnahme mit unabhängigen nativen Ergebnissen und DOS-Gästen. Als Nächstes fehlen FRNDINT, FSQRT, weitere Konstanten, BCD und transzendente Operationen mit einzelnen unabhängigen Zahlen-/Zustandsoracles. Danach bleiben die übrigen M3-Geräte und Snapshots sowie M4–M8 offen. Die auf Benutzerwunsch verschobene native Mauswiederholung bleibt unverändert offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, 16.1 und 19.2.4: pending Ausnahmen, NE, IGNNE und FERR unabhängig von NE. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- Intel, **i486 Microprocessor**, 240440-002, November 1989, Signalbeschreibungen und Numerics Compatibility: FERR/IGNNE, nichtwartende Kontrollbefehle. [Intel-Datenblatt](https://bitsavers.trailing-edge.com/components/intel/80486/240440-002_i486_Microprocessor_Nov89.pdf).
- Intel, **Embedded Intel486 Processor Family Developer’s Manual**, 273021-001, Oktober 1997, 10.3.15: externe DOS-kompatible Fehlerverriegelung, Port F0 und IGNNE. Die dortige Beispielschaltung ist keine geprüfte universelle AT-Platine. [Intel-Handbuch](https://bitsavers.trailing-edge.com/components/intel/80486/273021-001_Embedded_Intel486_Processor_Family_Developers_Manual_199710.pdf).
- Intel, **82378ZB/82379AB System I/O**, 290571-001, März 1996, 3.5.6: Byte-Port F0, IRQ-/IGNNE-Quittierung. Als ergänzender späterer Plattformvergleich, nicht als nachgebildeter Chipsatz. [Intel-Datenblatt](https://www.bitsavers.org/components/intel/_dataSheets/290571-001_82378ZB_82379AB_System_IO_199603.pdf).
