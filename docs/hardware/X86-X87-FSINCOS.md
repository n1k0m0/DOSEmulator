# x87: FSINCOS, beide Ergebnisse und atomarer Stackwechsel

Stand: 25. Oktober 2026. Geprüftes Referenzpaket des eigenen 486-Kerns. Vollständiges x87 und M3 bleiben offen; historische Nachweise gelten für ihre dokumentierten Builds.

## Operation und Rechenkern

FSINCOS (`D9 FB`) ersetzt das alte ST(0) durch den Sinus und legt den Cosinus darüber auf den Stack. Danach stehen **Cosinus in neuem ST(0), Sinus in ST(1)**. Die übrigen sechs Register und EFLAGS bleiben erhalten. Für ±0 sind die Antworten exakt +1 und die ursprüngliche signierte Null. Bei endlichen Beträgen **ab 2^63** bleiben Quelle/TOP/Tags unverändert und C2 wird gesetzt, ohne neuen numerischen Fehler oder Push.

`Float80Math.SinCosPair` verwendet für beide Komponenten den bestehenden, unveränderten ganzzahligen FSIN-/FCOS-Intervallkern. Jede Komponente wird mit voller 80-Bit-Präzision unabhängig von PC und den vier RC getrennt eingeschlossen. Beide Ergebnisse werden vor einem gemeinsamen architektonischen Commit berechnet. Der Emulator benutzt weder Host-double noch native x87 zur Ausführung. Die beiden Skalarberechnungen sind keine Zusage historischer FSINCOS-Laufzeit; die bisherigen vier Entwicklungstakte pro Step bleiben unverändert.

Eindeutige nächste π/2-Reduktion mit Machin-π-Grenzen, Quadrantenwahl, nach außen gerundete Reihen und strikte rationale Grenzen für sehr kleine Argumente bleiben erhalten. Insbesondere werden sin(x)<x und cos(x)<1 bei winzigen positiven x nicht durch vorzeitige Rundung ausgelöscht. Der neue Befehl ändert weder den FSIN-/FCOS- noch den FPTAN-Rechenkern.

## Status, zwei Ziele und Fehler

Vor der Berechnung werden Quelle und neuer physischer Pushplatz geprüft. Ein Stackfehler hat deshalb auch vor einem außerhalb des Bereichs liegenden Argument Vorrang; fehlende Quelle gewinnt im Referenzvertrag vor gleichzeitig belegtem Pushplatz. Maskierte Stackfehler schreiben zweimal Indefinite und führen einen Push aus. Unmaskierte Stackfehler erhalten alle rohen Register, TOP und Tags. Quiet-NaN wird auf beide Ziele kopiert, maskiertes SNaN auf beide Quiet-NaNs, Unendlich/Unsupported auf beide Indefinite-Werte.

Unmaskiertes IE/DE unterdrückt **beide** Zieländerungen und den Push. PE/UE übernimmt beide Ergebnisse und genau einen Push; die Sinuskomponente verwendet bei unmaskiertem UE die vorhandene Exponentenantwort +24576, während der Cosinus normal bleibt. Die Ausnahmeflags beider Komponenten werden vereinigt. Keine neuen ZE/OE; maskiertes sticky ZE bleibt erhalten.

**C1 folgt der Sinus-Rundung**, nicht der Rundung des gepushten Cosinus oder dem OR beider Rundungsbits. Eigene Fälle mit 1/4 Radiant und negativen gerichteten Rundungen unterscheiden diese Antworten ausdrücklich. Die gewählte C1-Antwort ist durch unabhängige Mathematik, CPU-Fälle und tatsächliche native FSINCOS-Ausführung abgesichert, aber kein universeller historischer Steppingvertrag. **C2 wird ausdrücklich geprüft**, undefinierte C0/C3 bleiben ausgeschlossen. Bei C2=1 ist C1 im Modell stabil null.

**4105/4105 Regressionen, 0 Warnungen/Fehler; 130 neue CPU-Fälle.** Geprüft sind auch PC/RC/Masken, voller/leerer Stack, erhaltene übrige Register, unabhängige Komponentenrundung, beide UE-Antworten, Präfixe, NM, LOCK/UD, tatsächlicher Fetch-PF mit Reparatur ohne vorzeitigen Push, TF nach beiden Commits sowie echte MF-/FNCLEX-/IRETD-/WAIT-Rückkehr. Die bestehende FERR-/IRQ13-Leitung bleibt erhalten.

## Unabhängige mathematische und native Prüfung

**4721 Zahlen-/Zustandsfälle** vergleichen reinen Paarkern und tatsächlichen Decoder strikt mit einem getrennten Python-Integer-/Fraction-Oracle. Die Zahleneinschließung verwendet ganze π-Reduktion, unabhängiges π=4 atan(1) mit rationalen Quadratwurzel-/Halbwinkelgrenzen, einzelne rationale Potenzen/Fakultäten und Sinus-/Cosinus-Doppelwinkel. Der neue Zustandsoracle prüft beide rohen Antworten, die physische Stackbelegung, Fehlerpriorität und C1 aus der Sinuskomponente.

**1740 unterschiedliche Komponenten-Rundungsprobleme** sind bei **320 und 448 Binärstellen** eingeschlossen. Beide Enden und Präzisionen müssen in selbst kodierten Werten/Flags/C1 übereinstimmen. Kein Host-float oder Produktcode im mathematischen Oracle. Die geprüften Skalargrenzen werden wiederverwendet; die Paarausführung und der Stack-/Fehlervertrag werden separat geprüft.

Eine getrennte tatsächliche moderne AMD-x64-FSINCOS-Referenz bestätigt Status/C2/Tags und erhaltene Register in allen 4721 Fällen. Unter **3796 Komponenten bei |x|≤1 Radiant** unterscheiden sich **drei Werte** um höchstens **einen benachbarten darstellbaren Schritt**. Unter **3304 größeren Komponenten** weichen **1894 native Werte** von der mathematischen Antwort ab; **190 C1-Antworten** unterscheiden sich von der mathematischen Richtung. Keine allgemeine native Genauigkeitsgrenze für große Argumente. Die Referenz sichert/restauriert den Host mit FXSAVE64/FXRSTOR64 und ist kein physischer 486. Der mathematische Rundungsvertrag bildet keine begrenzte historische interne π-Konstante nach.

## Tatsächliche DOS-Gäste

**FPSCS.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS jeweils zweimal im Real Mode, privaten Protected Mode und privaten 4-KiB-Paging; originales EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start **220 Fälle, 73 pending Umgebungen, 18 Stackfehler, 71 C1-Aufrundungen und 631 aktive rohe Registervergleiche**, darunter **acht C2-Antworten ohne Push und 191 übernommene Ergebnispaare**. Real/V86 **1800 ESC**, privates PE/Paging **1878 ESC**. Beide Ergebnisse, TOP/Tags, übrige Register/EFLAGS, Quellen, Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr sind unabhängig geprüft. Wiederholungen stimmen in virtueller Zeit, Bild- und Nachweishashes überein; keine Gastwrites/Resets. **910 absichtlich verfälschte Gastnachweise scheitern**, einschließlich beider Ergebnisregister.

Die acht privaten PE-/Paging-Starts liefern je acht tatsächliche MF-/IRETD-/WAIT-Rückkehrvorgänge nach PE/UE/DE, Invalid durch Unendlich beziehungsweise SNaN, leerer Quelle, leerer Quelle bei gleichzeitig belegtem Pushplatz und Stacküberlauf bei einem Argument außerhalb des Bereichs. Zusammen **64 neue Gastrückkehrvorgänge**, mit vollen pending 108-Byte-Abbildern, übernommenem beziehungsweise unterdrücktem Ergebnispaar und tatsächlichem FRSTOR/FNCLEX/IRETD samt WAIT-Wiederholung.

Der Gast restauriert seine ursprünglichen vollständigen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die Zahlenmatrix erfasst pending Zustände gezielt mit FNSAVE; tatsächliche externe FERR-/IRQ13-Rückkehr bleibt die getrennte FPERR-Probe. Ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Gemeinsamer Nachweis und Reproduktion

Auf denselben endgültigen sieben DLLs bestehen zusätzlich 16 FSIN-/FCOS-, 16 FPERR-, acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts. Zusammen **67 Kaltstarts und 2212 verworfene Gastoracles**. Dreizehn übrige Zahlenmatrizen sind frisch gegen den aktuellen Decoder geprüft. Zusammen **147977 definierte Zahlenfälle mit nativer Vergleichsreferenz**, darunter 3932 F2XM1-, 6169 Logarithmus-, 3752 FPATAN-, 4704 FPTAN-, 9410 FSIN-/FCOS- und 4721 FSINCOS-Fälle mit strikter unabhängiger Mathematik, plus 36 getrennte undefinierte BCD-Diagnosen. Die Remainderreferenz führt 138982 Einzelbefehle aus.

**70 PowerShell-Skripte/228 Checks, 95 Python-Prüfer, 20 identisch regenerierte Vektor-/Include-/Native-/COM-Dateien und 451 Quellhashes** sind geprüft. Zehn geschützte Eingaben, 53 private Baselines, C64 und HEAD bleiben unverändert. Benutzer-Spiel-COW nicht geöffnet; keine nativen Fenster/Eingaben oder Commits/Pushes. Gesamtnachweis über `artifacts/tests/m3-fsincos-release-path.txt`.

```powershell
.\scripts\test-x87-fsincos.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Historische Performancezeiten wurden nicht pauschal wiederholt. Die kurze stumme 32-Tick-Doom-Demo bleibt ein begrenzter Extender-/Bildnachweis; keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme. Keine physische 486-/Silizium-/Timingzusage. Die einzelnen x87-Referenzpakete ersetzen keine universelle FPU-/CPU-/DOS-Extender-Abnahme.

Als nächste Gerätevertikale folgt **gerätegetaktetes 16-Bit-DMA auf Kanal 5–7 als Grundlage für ein konkretes Non-PnP-SB16-Modell**, anschließend SB16/OPL3, ET4000, Ports/MIDI und koordinierte Snapshots. Die vorhandene DMA-Register-/Blocktransfergrundlage besitzt bereits hohe Kanäle; das ist keine SB16-Abnahme. Host-Mikrofon/MIDI/Netzwerk bleiben inaktiv. M3 und M4–M8 bleiben offen.

## Primärquelle

[Intel SDM Volume 2A, FSINCOS 3-383 bis 3-384](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): Operation, Ergebnisreihenfolge, Bereich/C2, Stackeffekte, Ausnahmen und begrenzte Genauigkeit interner π-Reduktion. Die PDF-Sonderwerttabelle besitzt verwirrende Spaltenbeschriftungen; der hier geprüfte Vertrag folgt der beschriebenen Operation mit Cosinus im neuen ST0 und Sinus im neuen ST1. C1-Zuordnung und ausgewählte Commit-/Fehlerantworten sind zusätzlich durch getrennte native und eigene Gastoracles dokumentiert.
