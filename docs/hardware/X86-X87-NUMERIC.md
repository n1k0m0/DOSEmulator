# x87: elementare 80-Bit-Arithmetik und Zahlenkonvertierungen

Stand: 25. Oktober 2026. Geprüftes M3-Teilpaket auf dem vorhandenen [80-Bit-Zustandsmodell](X86-X87-STATE.md). **M3 und die vollständige x87 sind weiterhin offen.** Die nachfolgende [Fehlerleitungsabnahme](X86-X87-ERRORS.md) ergänzt inzwischen ausgewählte FERR-/IGNNE-/IRQ13- und #MF-Gastpfade. Numerische Vergleichsbefehle, BCD, Quadratwurzel, Ganzzahlrundung und transzendente Funktionen besitzen weiterhin keine Implementierung beziehungsweise Abnahme.

## Rechenkern

`Float80Math` berechnet endliche Werte als vorzeichenbehaftete Ganzzahl mit binärem Exponenten. Division behält zusätzlich einen exakten ganzzahligen Nenner. `BigInteger` führt Addition/Subtraktion, Multiplikation, Quotient und Rest ohne vorherige Host-Gleitkommarundung aus. Erst das Zielquantum bestimmt die gerundeten Bits. Auch das unterste Signifikandbit von `1 + 2^-63` bleibt wirksam. Im Emulator gibt es keine Aufrufe der nativen Referenz und keine Umrechnung über `double` oder `float`.

Die Präzisionskontrolle wählt für die elementaren Registerergebnisse 24, 53 oder 64 Signifikandbits bei weiterhin erweitertem Exponentenbereich. Die reservierte PC-Kodierung 01 erhält im Referenzmodell deterministisch 64 Bits und wird nicht als historische Hardwarezusage getestet. RC wählt nächstgelegen mit gerader Tie-Auflösung, abwärts, aufwärts oder gegen null. C1 meldet die Erhöhung des gerundeten Signifikandenbetrags; Ausnahmeflags bleiben sticky. Direkte Datenübertragungen und Integer-/IEEE-Laden verwenden keine verkürzte PC-Präzision. Speicherformate bestimmen beim Speichern ihre eigene Präzision.

Neu implementierte Befehlsgruppen:

| Bereich | Umfang |
| --- | --- |
| Grundrechenarten | FADD/FADDP, FSUB/FSUBR/FSUBP/FSUBRP, FMUL/FMULP, FDIV/FDIVR/FDIVP/FDIVRP |
| Registerformen | D8, DC und DE mit ST(0)/ST(i), Zielaliasen und dem tatsächlichen Pop nach abgeschlossenem Ergebnis |
| Reelle Speicheroperanden | m32real und m64real einschließlich umgekehrter Subtraktion/Division |
| Ganzzahlige Speicheroperanden | FIADD/FISUB/FISUBR/FIMUL/FIDIV/FIDIVR für signierte m16-/m32-Integer |
| Reelle Konvertierungen | FLD m32/m64, FST/FSTP m32/m64, alle vier RC-Modi, NaN-Nutzlasten, signierte Null, Normal-/Denormalzahlen und Unendlich |
| Integer-Konvertierungen | FILD m16/m32/m64, FIST m16/m32 und FISTP m16/m32/m64; auch `-2^63` ist exakt |

Vorhandene m80-Transfers, Stack-/Tag-/Umgebungsbefehle bleiben erhalten. Der Decoder prüft Speicheroperanden über Segmentierung, Ausrichtung und vollständige Seitenvorprüfung. Fehlende Seiten konsumieren kein Register und bewirken keine frühen Teilwrites. Datenzugriffe benutzen die vorhandene Debugbeobachtung. Das ist keine allgemeine MMIO-/Alias-/Siliziumfehlerreihenfolge.

## Sonderwerte und Ausnahmen

Die gewählten Referenzregeln unterscheiden Quiet-/Signaling-NaNs, Unendlich, signierte Null, Denormalzahlen und nicht unterstützte Rohformate. Ein SNaN wird bei numerischer Verwendung gequieted und löst IE aus. Zwei NaNs werden nach den dokumentierten Quiet-/Signaling- und Signifikandregeln ausgewählt; nicht unterstützte Formate und ungültige Kombinationen erzeugen maskiert den erweiterten Indefinite-Wert. Numerische Konvertierung ist von den zuvor geprüften unveränderten m80-Rohtransfers getrennt.

Alle sechs numerischen Flags sind geprüft: IE, DE, ZE, OE, UE, PE. Ungültige Operation und Zero-Divide haben Vorrang vor Denormal-Operand; maskiertes DE kann mit einem nachfolgenden Rundungsflag zusammentreffen. Unmaskiertes IE/DE/ZE einer Rechenoperation erhält die Quellen und unterdrückt Ziel/Pop. FLD einer reellen Denormalzahl normalisiert und pusht dagegen auch bei unmaskiertem DM; SNaN/IE kann den Push unterdrücken. Präzisionsfehler liefern den gerundeten Wert auch unmaskiert.

Maskiertes Overflow liefert je nach RC und Vorzeichen Unendlich oder die größte endliche Zahl. Maskiertes Underflow liefert eine Denormalzahl oder null und setzt UE nur bei Genauigkeitsverlust zusammen mit PE. Für die x87-Referenz wird Tininess nach Präzisionsrundung mit unbeschränktem Exponenten und vor der Denormalisierung ausgewertet. Ein eigener Grenzfall belegt deshalb UE+PE, obwohl die abschließende Denormalrundung wieder die kleinste Normalzahl erreicht; bei verkürzter Präzision kann derselbe Fall zuvor normal gerundet sein.

Unmaskiertes Register-OE/UE liefert das gerundete Ergebnis mit um 24.576 verschobenem Exponenten. Unmaskiertes OE/UE eines Speicher-Stores erhält Speicher und Stack. Integerwerte außerhalb des Zielbereichs liefern maskiert das entsprechende Integer-Indefinite und IE. Unter NE=1 bleibt die numerische Ausnahme zunächst pending; CPU-Tests prüfen einen tatsächlichen #MF am folgenden wartenden Befehl/FWAIT und das anschließende Löschen. Das nachfolgende [Fehlerleitungspaket](X86-X87-ERRORS.md) ergänzt inzwischen das kontrollierte Warten unter NE=0, Port F0/IRQ13/IGNNE und tatsächliche #MF-/IRQ-Rückkehrgäste.

## Unabhängige Zahlenreferenz

`tests/x87-native-reference.asm` und das gesonderte Projekt `tests/X87.NativeReference` sind ausschließlich Testwerkzeuge. Ein eigener kleiner Windows-x64-Aufruf führt dieselben grundlegenden Operationen auf der tatsächlichen Host-x87-FPU aus. Er sichert den ursprünglichen Hostzustand mit FXSAVE64, erfasst die Testwerte mit nichtwartendem FNSAVE und restauriert den Hostzustand mit FXRSTOR64 einschließlich der vollen 64-Bit-FPU-Zeiger. Dadurch können auch pending unmaskierte Zustände ohne nachfolgendes FWAIT untersucht werden. Die normale Emulatorausführung lädt diesen Helfer nicht; FXSAVE wird dem 486-Gast dadurch nicht hinzugefügt.

**5.696 deterministische Zahlenfälle** vergleichen sowohl den reinen Ganzzahlkern als auch die tatsächlich ausgeführten CPU-Decoder-/Speicher-/Stackpfade mit dieser unabhängigen Referenz. Sie enthalten alle zwölf gültigen PC-/RC-Kombinationen, Zufallsfälle mit festem Seed, tiefe Exponenten, C1, exakte und ungenaue Unterläufe, Overflow, Sonderwerte, NaN-Auswahl, Integergrenzen, umgekehrte Speicheroperationen und ausgewählte unmaskierte Ausnahmen. Verglichen werden Ergebnisbits, Kontroll-/Status-/Tagwort, Commit/Pop und Speicherbytes. Host-Gleitkommazahlen werden auch im Austauschformat nicht verwendet: Signifikand/Exponent und Integerbits sind Hexstrings.

Referenzhost dieser Abnahme: **AuthenticAMD, Family 26, Model 68, Stepping 0, Windows x64**, .NET 10.0.12. Native-Code- und Eingabehash stehen im privaten Bericht. **Dieser Prozessor ist kein physischer 486.** Der Vergleich belegt die gewählten kompatiblen Referenzpfade; er ersetzt keine Abnahme historischer Steppings, undefinierter Bits, sämtlicher Fehlerkonkurrenzen oder ursprünglicher FPU-Zyklen. Der Interpreter behält seine angenäherte Schrittzeit; historische Rechengeschwindigkeit ist nicht nachgewiesen.

## Echte DOS-Zahlenproben

```powershell
.\scripts\test-x87-numeric.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Mit den drei privaten optionalen Argumenten `-ConfiguredDisk`, `-NoEmsOverlay`, `-RamOverlay` prüft das Skript **16 Kaltstarts**: MS-DOS 6.22/FreeDOS jeweils zweimal im Real Mode, eigenem PE32 und eigenem Paging, EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb. Alle Ausgangsmedien bleiben schreibgeschützt; private COWs werden bytegenau auf Unverändertheit geprüft.

**FPNUM.COM**, vom echten DOS über FDC geladen, führt je Start **220 numerische Fälle mit 1.216 ESC-Befehlen** aus. Sie decken alle 15 definierten Testarten ab: Registerarithmetik, reelle/integer Lade-/Speicherkonvertierungen sowie vier Speicherarithmetikformate. Die eigene Probe erfasst nach jedem Fall ein tatsächliches vollständiges FPU-Abbild und gegebenenfalls die konvertierten Speicherbytes. Sie erreicht Original-VER/ECHO/Prompt. Der Prüfer übersetzt unter EMM386 die tatsächlichen Seitentabellen und kontrolliert beim privaten Paging sämtliche 1.024 PTEs.

Pro Start stimmen alle sechs Flags, 30 Umgebungen mit pending unmaskierter Ausnahme, 40 C1-Rundungsfälle und **190 tatsächlich belegte 80-Bit-Register** mit der unabhängigen Referenz überein. Jeder belegte Slot wird geprüft; Rohbits leerer Slots sind nicht über Prozessoren hinweg zugesagt. Wächter verhindern unbemerkte zu breite Stores. **319 absichtlich verfälschte Nachweise** scheitern. Zeit, Bild, Nachweis und numerischer Digest sind je Wiederholung identisch. Native-Code-/Eingabehash werden zusätzlich kontrolliert.

Die Probe sichert und restauriert den ursprünglichen vollständigen 108-Byte-FPU-Zustand und vergleicht ihn bytegenau. Private PE-Varianten behalten ihren TR bis Reset; **kein vollständiger CPU-Snapshot**. Die 30 gespeicherten pending Umgebungen sind keine IRQ13-/FERR- oder #MF-Gastabnahme: Der nächste Befehl ist gezielt nichtwartendes FNSAVE. Tatsächliche #MF-/Retry-Rahmen unter NE=1 werden in den separaten CPU-Tests erzeugt.

Zum Zeitpunkt der numerischen Teilabnahme bestand der gemeinsame Build **1651/1651 Regressionen, 0 Warnungen/Fehler**, darunter **103 neue CPU-/Zahlenfälle**. Wegen gemeinsamer FPU-/Decoderpfade bestehen das bisherige 16-Start-Zustandspaket und der originale DOS4GW-/x87-Kontrollpfad auf denselben DLLs erneut. Genaue aktuelle Abnahmeverzeichnisse stehen im [Implementierungsstand](../IMPLEMENTATION-STATUS.md).

## Weitere M3-Arbeit und Quellen

Die nachfolgende [Fehlerleitungsvertikale](X86-X87-ERRORS.md) ist inzwischen mit eigenen Ausnahme-/Rückkehrgästen geprüft. Nächster Schritt: fehlende x87-Operationen wie FCOM/FUCOM/FTST, FRNDINT, FSQRT, weitere Konstanten, BCD und transzendente Funktionen mit eigenen unabhängigen Oracles. Keine vollständige FPU-, allgemeine DOS-/Spiel-/Windows-Kompatibilität ableiten. SB16/OPL3, ET4000, Ports, MIDI, koordinierte Snapshots und M4–M8 bleiben offen.

Primärgrundlagen: Intels [486 Programmer’s Reference Manual](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 15/16/19 und Befehlsverträge, insbesondere Präzision/Rundung, NaN-Auswahl sowie Vorher-/Nachher-Ausnahmen; Intels [SDM Band 2A](https://cdrdv2-public.intel.com/812383/253666-sdm-vol-2a.pdf), elementare Rechen-/Konvertierungsbefehle und kompatible FLD-Denormalregeln. Es wurde kein fremder SoftFloat-/Emulatorcode übernommen. Das Originalkonzept nennt SoftFloat als möglichen Kandidaten; dieses Teilpaket verwendet einen eigenen ganzzahligen Kern mit unabhängiger nativer Referenz.
