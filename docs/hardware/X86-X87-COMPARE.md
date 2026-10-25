# x87: geordnete und ungeordnete Vergleiche

Stand: 25. Oktober 2026. Geprüftes M3-Referenzpaket für den eigenen 486DX-Kern; vollständiges x87 und M3 bleiben offen.

## Befehle und Zustandsvertrag

Implementiert sind FCOM/FCOMP ST(i), FCOMPP, FUCOM/FUCOMP ST(i), FUCOMPP und FTST. Hinzu kommen FCOM/FCOMP mit m32real/m64real und FICOM/FICOMP mit signierten m16int/m32int. Die dokumentierten Opcodes werden direkt im CPU-Decoder ausgeführt. Vergleiche verändern keine EFLAGS und erzeugen keinen gerundeten numerischen Ergebniswert. Die Ganzzahlrechnung vergleicht die tatsächlichen 64 Signifikandbits und Exponenten, einschließlich niedriger Bits, Vorzeichen, Null und Unendlichkeit. Der Emulator verwendet weder Host-double noch den nativen Testhelfer zur Ausführung.

C0/C2/C3 zeigen größer, kleiner, gleich oder ungeordnet an. C1 wird gelöscht. Die Vorzeichen beider Nullen sind für die Gleichheit unerheblich. FCOM/FICOM/FTST melden bei Quiet- und Signaling-NaNs Invalid; FUCOM meldet Invalid bei Signaling-NaNs und nicht unterstützten Formaten, während ein Quiet-NaN allein ungeordnet ohne Invalid liefert. Nicht unterstützte rohe 80-Bit-Formate und leere Quellregister sind ebenfalls erfasst. Leere Register werden beim Vergleich nicht durch einen künstlichen Ergebniswert ersetzt.

Statusantwort und Stackbewegung sind getrennt. Maskiertes Invalid liefert ungeordnet und führt den vorgesehenen Pop aus. Unmaskiertes Invalid liefert im festgelegten Referenzmodell ebenfalls ungeordnet und unterdrückt den Pop. Getrennte Vorbelegungen 0, C0, C3 und C0/C2/C3 prüfen diese Antwort ausdrücklich. Ein unmaskierter Denormalfehler liefert im geprüften Referenzmodell bereits die Relation, unterdrückt aber ebenfalls den Pop. Das ist unter anderem mit vorher gesetzten C0/C1/C2/C3 und Pop-zweimal-Formen geprüft. NaN-/Format-/Leerstack-Prioritäten gehen dem Denormalpfad voraus. Präzisions- und Rundungswahl ändern die Relation nicht.

Speicherformen lesen den vollständigen Operanden über die bestehenden Segment-, Alignment- und Pagingprüfungen. Die neu geprüften Seitenfehler verändern weder Stack noch Status oder rohe Register. Unmaskierte numerische Ausnahmen werden über die bestehende [Fehlerleitung](X86-X87-ERRORS.md) gemeldet. Ein CPU-Fall prüft den tatsächlichen aufgeschobenen #MF an WAIT, FNCLEX im Handler, IRETD und die erfolgreiche Wiederholung von WAIT bei erhaltenem Stack.

## Unabhängige Nachweise

- **1.819/1.819 Regressionen, 0 Warnungen/Fehler; 98 neue CPU-Fälle.** Dazu gehören alle acht ST(i)-Quellen beider Familien mit/ohne Pop, Alias ST(0), signed Relationen, NaN-/Maskenunterschiede, Leerstack, alle acht Speicherformen mit echten Seitenfehlerrahmen und der #MF-Rückkehrfall.
- **8.922 native Referenzfälle** vergleichen den echten Decoder und bei Registerformen zusätzlich den reinen Ganzzahlkern mit tatsächlichen Host-x87-Instruktionen. Die Eingaben umfassen niedrige Signifikandbits, ±0, ±1, Grenzwerte, Denormals/Pseudodenormals, ±Unendlich, Quiet-/Signaling-NaNs, nicht unterstützte Formate, leere Quellen, Masken und ausgewählte PC-/RC-Einstellungen. Geprüft werden CW/SW/Tags, alle aktiven 80-Bit-Register und unveränderte EFLAGS. Der eigene Helfer sichert/restauriert den Host über FXSAVE64/FXRSTOR64. Referenzhost: moderner AMD-x64 Family 26 Model 68; **kein physischer 486**.
- **FPCMP.COM: 16 echte Kaltstarts.** MS-DOS 6.22 und FreeDOS jeweils zweimal im Real Mode, privatem PE32 und privatem 4-KiB-Paging; originales EMM386 NOEMS/RAM je zweimal im tatsächlichen V86-/Monitorbetrieb. Je Start werden **220 Vergleichsfälle und 1.836 ESC** ausgeführt. Die 15 Befehlsformen enthalten zusammen 65 pending unmaskierte Zustände, 56 Leerstackfälle und sieben Quiet-NaN-Antworten ohne Invalid. Der Prüfer vergleicht **273 aktive 80-Bit-Register**, CW/SW/Tags, beide Ausnahmeflags, sämtliche Relationen, Befehls-/Datenzeiger samt Opcode, unveränderte EFLAGS, Quellbytes und Pufferwachen unabhängig mit den nativen Ergebnissen und dem assemblierten Gast.
- Der Gast erfasst pending numerische Zustände absichtlich mit nichtwartendem FNSAVE. Er restauriert seine **vollständigen ursprünglichen 108 FPU-Bytes** bytegenau und erreicht anschließend VER, ECHO und den DOS-Prompt. Wiederholungen stimmen in virtueller Zeit, Bild und Nachweishashes überein. **538 absichtlich verfälschte Nachweise** werden verworfen. Speicher- und Gerätezustände werden nicht aus einem Erfolgstext allein abgeleitet.

Die private PE-Probe behält ihren TR bis Reset. Die vollständige FPU-Restaurierung ist keine vollständige CPU-/PIC-/Scheduler-Wiederherstellung. Die Vergleichsmatrix ist keine neue IRQ13-/FERR-/#MF-Gastabnahme; diese besitzt den getrennten FPERR-Gast. Frühere CPUX87-Proben restaurieren weiterhin kein vollständiges FPU-Abbild.

## Reproduktion

```powershell
.\scripts\test-x87-compare.ps1 -SkipBuild -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Für alle 16 Starts zusätzlich `-ConfiguredDisk`, `-NoEmsOverlay` und `-RamOverlay` mit den bestehenden privaten EMM386-Testkonfigurationen angeben. Ohne diese drei Argumente sind es zwölf Starts. Das Skript assembliert den separaten nativen Testhelfer und eigene COM-Programme, erzeugt neue Testdisketten und kopiert die Ausgangs-Overlays. Originale, private Ausgangskonfigurationen und getestete DLLs werden mit SHA-256 auf Unverändertheit geprüft. Sämtliche Medien, RAM-Dumps und Spielbilder bleiben ignoriert und privat. Native Fenster oder Windows-Eingaben sind nicht erforderlich.

Die endgültigen Abnahmepfade, zusätzlich erneut geprüften Zahlen-/Fehlerleitungs-/DOS4GW-Pakete und der Gesamtnachweis stehen im [Implementierungsstatus](../IMPLEMENTATION-STATUS.md).

## Grenzen und Fortsetzung

Insbesondere ist die Statusantwort bei unmaskiertem Invalid als eigener Referenzvertrag festgelegt; keine pauschale Übertragung auf andere Intel-/AMD-Generationen. Keine Zusage für sämtliche historischen 486-Steppings, undefinierte leere Registerbits, undokumentierte Alias-Opcodes, widersprüchlich geladene Tagwörter, jede MMIO-/Fault-Konkurrenz oder Silizium-/Zykluspräzision. Die FPU-Zeiger werden für die geprüften eigenen Gastformen ausgewertet. Die kurze stumme Doom-II-Referenzdemo bleibt ein begrenzter Extender-/Grafiktest; interaktives Spielen, lange Spielpfade und Sound sind gesondert abzunehmen.

[FRNDINT](X86-X87-ROUND.md) besitzt inzwischen eine eigene Abnahme einschließlich tatsächlicher MF-Rückkehrgäste. Als Nächstes folgen FSQRT, weitere Konstanten, BCD und transzendente Operationen mit getrennten unabhängigen Zahlenoracles. Die übrigen M3-Geräte/Snapshots sowie M4–M8 bleiben offen. Die auf Benutzerwunsch verschobene native Mauswiederholung bleibt unverändert offen.

## Primärquellen

- Intel, **Intel486 Programmer’s Reference Manual (1992)**, 15.1, 16.2, 17.4 sowie FCOM/FICOM/FTST/FUCOM in Kapitel 26: Statusbits, numerische Prioritäten, Speicherformen und NaN-Unterschiede. [Digitalisierte Intel-Ausgabe](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143).
- AMD, **AMD64 Architecture Programmer’s Manual, Volume 5**, 26569, FCOM/FICOM/FTST/FUCOM: ergänzender Vertrag des verwendeten Referenzhosts, einschließlich ungeordnetem Invalid-Ergebnis. [AMD-Handbuch](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf). Die gemessene Hostreferenz ersetzt keine 486-Hardware.
