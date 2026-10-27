# x87: FPATAN, alle Quadranten und unabhängige Winkelintervalle

Stand: 25. Oktober 2026. Geprüftes Referenzpaket des eigenen 486-Kerns. Vollständiges x87 und M3 bleiben offen; historische Nachweise gelten für ihre jeweiligen Builds.

## Operation und Rechenkern

FPATAN (`D9 F3`) berechnet den Winkel `atan2(ST(1),ST(0))`, schreibt ihn nach ST(1) und entfernt ST(0). Die Vorzeichen beider Quellen bestimmen den Quadranten; die Antwort hat das Vorzeichen von ST(1). ±0, Achsen und ±∞ besitzen eigene Antworten. Insbesondere entstehen bei den Paaren 0/0 und ∞/∞ keine neuen Invalid-/Zero-Divide-Fehler. Die Beschränkung früherer 80287 gehört nicht zum 486-Referenzmodell.

`Float80Math.ArcTangent` arbeitet ausschließlich mit nach außen gerundeten BigInteger-Intervallen. PC24/53/64 verkürzen die volle 80-Bit-Antwort nicht; die vier RC werden ausdrücklich angewendet. Es gibt keinen Host-double- oder nativen Emulationspfad.

Aus den exakten dyadischen Beträgen wird `r=min(|x|,|y|)/max(|x|,|y|)` gebildet. Für `r≤1/2` begrenzt die alternierende Reihe `atan(r)/r = 1−r²/3+r⁴/5−…` den Winkel. Größere r werden durch `atan(r)=π/4−atan((1−r)/(1+r))` auf einen Betrag höchstens 1/3 reduziert. Spiegelung an π/2 beziehungsweise π und das ursprüngliche y-Vorzeichen liefern die übrigen Quadranten. π wird selbst durch `16 atan(1/5)−4 atan(1/239)` eingeschlossen. Der Cache enthält ausschließlich rekonstruierbare Konstanten, keinen Gastzustand.

Für sehr kleine positive Winkel bleibt r ein eigener exakter rationaler Faktor. Die strikten Schranken `r−r³/3 < atan(r) < r−r³/3+r⁵/5` vermeiden bei exakten binären Rundungsgrenzen ein künstliches Ergebnis gleich r. Das ist unter anderem bei den kleinsten Normal-/Denormalzahlen und großen Exponentenabständen wichtig. Die übrige Rechnung startet mit 192 Festkommabits und erweitert um jeweils 128 Bits, bis beide Grenzen denselben rohen Wert, dieselben Flags und dasselbe C1 liefern.

## Status, Sonderwerte und Fehler

Geprüft sind alle Quadranten, signierte Achsen, ±0, ±∞, NaNs/Unsupported und Denormal/Pseudo-Denormal. Der gemeinsame Zweiquellen-NaN-Vertrag bleibt erhalten. Leere Quellen und unmaskiertes IE/DE erhalten beide rohen Operanden, TOP und Tags ohne Pop. Maskierter Leerstack schreibt Indefinite nach ST(1) und entfernt ST(0). Nichtnullige numerische Winkel melden PE; unmaskiertes PE/UE übernimmt Ergebnis und Pop. Der Underflow-Exponent wird dabei wie im vorhandenen Kern um 24576 angehoben. Es entstehen keine neuen ZE/OE; ein bereits maskiertes sticky ZE bleibt erhalten.

C1 beschreibt die Rundung des mathematischen Winkels. Undefinierte C0/C2/C3 bleiben im Modell stabil erhalten und sind aus dem nativen Architekturvergleich ausgeschlossen. Strenge Grenzen unterhalb eines exakten binären r unterscheiden beispielsweise `atan(minimum-normal)` von seinem Eingang: nächste Rundung kann minimum-normal liefern, Abrundung bereits den benachbarten Denormalwert samt UE/PE. Die Tininess-Prüfung nach Präzisionsrundung und vor Denormalisierung bleibt erhalten.

**3671/3671 Regressionen, 0 Warnungen/Fehler; 142 neue CPU-Fälle.** Dazu gehören PC/RC und Masken in vier Quadranten, niedrige Signifikandbits, voller/leerer Stack, Größen-/Adresspräfixe, tatsächlicher NM, LOCK/UD, reparierter Fetch-PF, TF nach dem Pop und echte MF-/FNCLEX-/IRETD-/WAIT-Rückkehr. Die bestehende FERR-/IRQ13-Leitung bleibt erhalten. Keine universelle historische Stepping-, Bus-, Zyklus- oder Fault-Konkurrenzzusage.

## Unabhängiger mathematischer Nachweis

**3752 Zahlen-/Zustandsfälle** vergleichen reinen Kern und tatsächlichen Decoder strikt mit einem getrennten Python-Integer-/Fraction-Oracle. Dieses benutzt wiederholte Halbwinkelreduktion `atan(t)=2 atan(t/(1+sqrt(1+t²)))`, statt der produktiven Addition an π/4. Ganzzahlige Quadratwurzeln liefern rationale untere/obere Grenzen. Einzelne rationale Reihenpotenzen werden unabhängig ausgewertet; gerichtete Division und ein alternierender Rest begrenzen jeden Auswertungsschritt. Die unabhängige π-Identität lautet `π=4 atan(1)`.

**920 unterschiedliche Winkel-/Rundungsprobleme** werden exakt als Null oder mit **256 und 384 Binärstellen** eingeschlossen. Beide Intervallenden müssen dieselbe selbst kodierte 80-Bit-Antwort samt C1, Masken und Exponentenantwort liefern; beide Präzisionen müssen übereinstimmen. Die Reihen verwenden kein Host-float und keinen Produktcode. Gemeinsam genutzte Testhilfen für rohe 80-Bit-Kodierung bleiben von der produktiven Kodierung getrennt.

Eine zusätzliche tatsächliche native Host-x87 vergleicht denselben Satz. Auf modernem AMD-x64 Family 26 Model 68 unterscheiden sich **zehn Werte** um höchstens **einen benachbarten darstellbaren Schritt**, **sechs C1-Antworten** von der mathematischen Richtung. Diese Näherungsabweichungen sind getrennt erfasst. Der native Helfer sichert/restauriert seinen Host über FXSAVE64/FXRSTOR64 und dient ausschließlich der Prüfung; er ist kein physischer 486.

## Tatsächliche DOS-Gäste und Rückkehr

**FPATAN.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS jeweils zweimal Real Mode, eigener Protected Mode und eigenes 4-KiB-Paging; originales EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start werden **220 Fälle, 50 pending Umgebungen, sechs Leerstackfälle, 104 C1-Aufrundungen und 234 aktive rohe Registervergleiche** geprüft. Real/V86 führt **1772 ESC**, privates PE/Paging **1828 ESC** aus. Ergebnis, TOP/Tags, übrige Register/EFLAGS, Quellen, Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr sind unabhängig geprüft. Wiederholungen stimmen in virtueller Zeit sowie Bild- und Nachweishashes überein; keine Gastwrites/Resets. **748 absichtlich verfälschte Gastnachweise scheitern.**

Die acht privaten PE-/Paging-Starts erzeugen je sechs tatsächliche MF-/IRETD-/WAIT-Rückkehrvorgänge nach PE/UE/DE/IE und leerer erster/zweiter Quelle: insgesamt **48 neue Gastrückkehrvorgänge**. Vollständige pending 108-Byte-Abbilder beweisen jeweils den übernommenen oder unterdrückten Pop, Ergebnis beziehungsweise Quellen sowie FRSTOR/FNCLEX/IRETD und erfolgreiche WAIT-Wiederholung.

Der Gast restauriert seine vollständigen ursprünglichen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die übrige Zahlenmatrix erfasst pending Zustände gezielt mit FNSAVE. Tatsächliche externe FERR-/IRQ13-Rückkehr bleibt die getrennte FPERR-Probe; ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Gemeinsamer Nachweis und Reproduktion

Auf denselben endgültigen sieben DLLs bestehen zusätzlich 16 FYL2X-/FYL2XP1-, 16 FPERR-, acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts. Zusammen **67 Kaltstarts und 2068 verworfene Gastoracles**. Mit den frisch ausgeführten übrigen zehn Zahlenmatrizen **129142 definierte Zahlenfälle mit nativer Vergleichsreferenz**, darunter 3932 F2XM1-, 6169 Logarithmus- und 3752 FPATAN-Fälle mit strikter mathematischer Referenz, plus 36 getrennte undefinierte BCD-Diagnosen. Die Remainderreferenz führt weiterhin 138982 Einzelbefehle aus.

**67 PowerShell-Skripte/219 Checks, 83 Python-Prüfer, 20 identisch regenerierte Vektor-/Include-/Native-/COM-Dateien und 418 Quellhashes** sind geprüft. Zehn geschützte Eingaben, 53 private Baselines, C64 und HEAD bleiben unverändert. Benutzer-Spiel-COW nicht geöffnet; keine nativen Fenster/Eingaben oder Commits/Pushes. Gesamtnachweis über `artifacts/tests/m3-atan-release-path.txt`.

```powershell
.\scripts\test-x87-atan.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Originalmedien und Benutzer-Spielstände bleiben unberührt. Historische Performancezeiten wurden nicht pauschal wiederholt; die kurze stumme Doom-Demo ist keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme.

Als Nächstes folgt **FPTAN** mit Bereichsgrenze/C2, echtem zusätzlichem Stackeintrag, strenger Argumentreduktion und eigenen Fehler-/Gastproben. Danach FSIN/FCOS/FSINCOS, übrige M3-Geräte und koordinierte Snapshots, anschließend M4–M8. Windows 95, Pentium, Netzwerk und Voodoo bleiben nicht abgenommen.

## Primärquelle

[Intel SDM Volume 2A, FPATAN, 3-365 bis 3-366](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): Vierquadrantenoperation, Sonderwerttabelle, Beschränkung früherer Coprozessoren, Status und Ausnahmen. Der eigene mathematische Rundungsvertrag und die gemessenen Grenzen zur nativen Näherung werden zusätzlich durch unabhängige Oracles dokumentiert.
