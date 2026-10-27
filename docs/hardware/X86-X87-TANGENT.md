# x87: FPTAN, strenge Argumentreduktion und zwei Stackziele

Stand: 25. Oktober 2026. Geprüftes Referenzpaket des eigenen 486-Kerns. Vollständiges x87 und M3 bleiben offen; historische Nachweise gelten für ihre dokumentierten Builds.

## Operation und Rechenkern

FPTAN (`D9 F2`) ersetzt ST(0) durch seinen Tangens und legt einen zusätzlichen Wert auf den Stack. Bei endlichen numerischen Antworten ist das neue ST(0) exakt 1, der Tangens steht danach in ST(1). Die übrigen Register und EFLAGS bleiben erhalten. Das Argument wird in Radiant interpretiert; für endliche Beträge **ab 2^63** bleibt es samt TOP/Tags unverändert und C2 wird gesetzt. Es gibt dann keinen Push und keinen neuen numerischen Fehler.

`Float80Math.Tangent` verwendet ausschließlich BigInteger-Intervalle. PC24/53/64 verkürzen die volle 80-Bit-Antwort nicht; die vier RC werden ausdrücklich angewendet. Kein Host-double oder nativer Emulationspfad.

Der Produktkern bestimmt ein eindeutiges nächstes Vielfaches von π/2 an beiden Grenzen einer π-Einschließung. Der Rest liegt zwischen −π/4 und π/4. Nach außen gerundete Reihen für sin(r)/r und cos(r) liefern tan(r); für ungerade Vielfache wird −cot(r) benutzt. Die π-Grenze stammt aus der Machin-Identität `16 atan(1/5)−4 atan(1/239)`. Vor unklarer Reduktion, verschwindender Restgrenze oder möglichem Nullnenner wird die Präzision erweitert. Start sind 256 Festkommabits, Erweiterung jeweils um 128 Bits, bis beide Enden denselben rohen Wert, dieselben Flags und C1 liefern.

Für `0<|x|<2^-64` bleiben die strikten rationalen Grenzen `x+x³/3 < tan(x) < x+x³` für den positiven Betrag erhalten. Die untere Grenze folgt aus tan(x)>x und der Ableitung tan²(x); die obere aus sin(x)≤x und cos(x)≥1−x²/2. Dadurch geht die Richtung selbst beim kleinsten Denormal nicht durch vorzeitige Festkommarundung verloren. Die negative Antwort wird gespiegelt. Dies ist ein eigener mathematischer Rundungsvertrag, keine Nachbildung einer begrenzten internen Chip-π-Konstante.

## Status, Sonderwerte und Fehler

Geprüft sind ±0, Denormal/Pseudo-Denormal, NaNs/Unsupported, ±∞, Vorzeichenwechsel und Nachbarn von π/4, π/2, π sowie große Argumente bis unmittelbar unter 2^63. Numerische Nichtnullantworten melden PE; gegebenenfalls kommen DE/UE hinzu. Unmaskiertes DE/IE erhält beide Ziele, TOP und Tags. Unmaskiertes PE/UE übernimmt den Tangens und den Push; UE benutzt die vorhandene Exponentenantwort +24576. Keine neuen ZE/OE; ein maskiertes sticky ZE bleibt erhalten. C1 folgt der mathematischen Rundung. Undefinierte C0/C3 bleiben im Modell stabil erhalten und sind aus dem Architekturvergleich ausgeschlossen; **C2 wird ausdrücklich geprüft**.

Stackvorprüfung erfolgt vor der Bereichsprüfung. Ein belegter Pushplatz liefert deshalb auch bei einem Argument außerhalb des Zahlenbereichs einen Stackfehler. Fehlt zugleich die Quelle, hat Unterlauf im Referenzvertrag Vorrang. Maskierte Stackfehler schreiben zwei Indefinite-Werte und führen einen Push aus; unmaskierte Fehler erhalten alle rohen Register. Ein ruhiger NaN wird auf beide Ziele kopiert. Maskierte Invalid-Antworten auf ∞/Unsupported liefern zweimal Indefinite, SNaN zweimal den Quiet-NaN. Der NaN-/Stackvertrag ist zusätzlich an tatsächlicher nativer Ausführung geprüft, ohne universelle historische Steppingzusage.

**3779/3779 Regressionen, 0 Warnungen/Fehler; 108 neue CPU-Fälle.** Die Fälle prüfen auch einen tatsächlich vollen Stack, Präfixe, NM, LOCK/UD, Fetch-PF mit Reparatur ohne vorzeitigen Push, TF nach beiden Ergebnissen sowie tatsächliche MF-/FNCLEX-/IRETD-/WAIT-Rückkehr. Die vorhandene FERR-/IRQ13-Leitung bleibt erhalten.

## Unabhängige mathematische Prüfung

**4704 Zahlen-/Zustandsfälle** vergleichen reinen Kern und tatsächlichen Decoder strikt mit einem getrennten Python-Integer-/Fraction-Oracle. Dieses reduziert mit einem ganzen Vielfachen von π und spiegelt an π/2. π wird unabhängig durch `4 atan(1)` mit rationalen Quadratwurzel-/Halbwinkelgrenzen eingeschlossen. Kleine Winkel werden durch einzelne rationale Potenzen und Fakultäten mit alternierendem Rest ausgewertet; die Tangens-Doppelwinkelformel liefert den endgültigen Winkel. Das unterscheidet sich von produktiver π/2-Reduktion und Festkommarekurrenz.

**862 unterschiedliche Rundungsprobleme** werden bei **320 und 448 Binärstellen** unabhängig eingeschlossen. Beide Grenzen und beide Präzisionen müssen in selbst kodierten 80-Bit-Werten, Flags und C1 übereinstimmen. Kein Host-float und kein Produktcode im mathematischen Oracle. Die kleinen Argumente behalten ebenfalls strikte rationale Grenzen an binären Rundungsgrenzen.

Die zusätzliche tatsächliche moderne AMD-x64-Referenz bestätigt Status, C2, Tags, Push-/Fehlerverhalten und erhaltene Register in allen 4704 Fällen. Unter den **1882 numerischen Antworten mit |x|≤1 Radiant** unterscheiden sich **299 Werte** um höchstens **einen benachbarten darstellbaren Schritt**. Unter **1652 größeren numerischen Argumenten** weichen **1314 native Werte** von der mathematischen Antwort ab; dafür wird ausdrücklich keine allgemeine native Genauigkeitsgrenze behauptet. **518 C1-Antworten** unterscheiden sich von der mathematischen Richtung. Das passt zur dokumentierten Einschränkung endlicher interner π-Reduktion, ersetzt aber keinen Genauigkeitsbeweis. Native Ausführung ist ausschließlich ein gesondertes Prüfwerkzeug mit FXSAVE64/FXRSTOR64; der moderne AMD-Host ist kein physischer 486.

## Tatsächliche DOS-Gäste

**FPTAN.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS jeweils zweimal im Real Mode, privaten Protected Mode und privaten 4-KiB-Paging; originales EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start **220 Fälle, 65 pending Umgebungen, 18 Stackfehler, 51 C1-Aufrundungen und 623 aktive rohe Registervergleiche**. Davon prüfen **16 Fälle C2 ohne Push**, **183 Fälle zwei tatsächlich übernommene Ziele**. Real/V86 führt **1800 ESC**, privates PE/Paging **1857 ESC** aus. Beide Ergebnisse, TOP/Tags, erhaltene Register/EFLAGS, Quellen, Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr sind unabhängig geprüft. Wiederholungen stimmen in virtueller Zeit, Bild und Nachweishashes überein; keine Gastwrites/Resets. **814 absichtlich verfälschte Gastnachweise scheitern**, darunter Verfälschungen beider Ergebnisregister.

Acht private PE-/Paging-Starts liefern je sechs tatsächliche MF-/IRETD-/WAIT-Rückkehrvorgänge nach PE/UE/DE/IE, leerer Quelle und belegtem Pushplatz bei einem Argument außerhalb des Bereichs: insgesamt **48 neue Gastrückkehrvorgänge**. Vollständige pending 108-Byte-Abbilder zeigen jeweils den übernommenen oder unterdrückten Push sowie FRSTOR/FNCLEX/IRETD und erfolgreiche WAIT-Wiederholung.

Der Gast restauriert seine ursprünglichen vollständigen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die Zahlenmatrix erfasst pending Zustände gezielt mit FNSAVE; tatsächliche externe FERR-/IRQ13-Rückkehr bleibt die getrennte FPERR-Probe. Ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Gemeinsamer Nachweis und Reproduktion

Auf denselben endgültigen sieben DLLs bestehen zusätzlich 16 FPATAN-, 16 FPERR-, acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts: zusammen **67 Kaltstarts und 1990 verworfene Gastoracles**. Elf übrige Zahlenmatrizen sind frisch gegen den aktuellen Decoder geprüft. Zusammen **133846 definierte Zahlenfälle mit nativer Vergleichsreferenz**, darunter 3932 F2XM1-, 6169 Logarithmus-, 3752 FPATAN- und 4704 FPTAN-Fälle mit strikter unabhängiger Mathematik, plus 36 getrennte undefinierte BCD-Diagnosen. Die Remainderreferenz führt 138982 Einzelbefehle aus.

**68 PowerShell-Skripte/222 Checks, 87 Python-Prüfer, 20 identisch regenerierte Vektor-/Include-/Native-/COM-Dateien und 429 Quellhashes** sind geprüft. Zehn geschützte Eingaben, 53 private Baselines, C64 und HEAD bleiben unverändert. Benutzer-Spiel-COW nicht geöffnet; keine nativen Fenster/Eingaben oder Commits/Pushes. Gesamtnachweis über `artifacts/tests/m3-tangent-release-path.txt`.

```powershell
.\scripts\test-x87-tangent.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Historische Performancezeiten wurden nicht pauschal wiederholt; die kurze stumme 32-Tick-Doom-Demo bleibt ein begrenzter Extender-/Bildnachweis. Keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme. Keine physische 486-/Stepping-/Silizium-/Timingzusage.

Als Nächstes **FSIN/FCOS mit ausdrücklichem C2 und unabhängig eingeschlossenen Ergebnissen**, anschließend FSINCOS mit zwei Zielen und getrennten Stack-/Commitantworten. Danach übrige M3-Geräte/Snapshots sowie M4–M8. Windows 95, Pentium, Netzwerk und Voodoo bleiben nicht abgenommen.

## Primärquelle

[Intel SDM Volume 2A, FPTAN, 3-371 bis 3-372](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): Operation, Bereich/C2, Stackwirkung, Status und Ausnahmen sowie begrenzte Genauigkeit interner π-Reduktion. Der eigene mathematische Rundungsvertrag und die gemessenen Grenzen zur nativen Näherung werden zusätzlich durch unabhängige Oracles dokumentiert.
