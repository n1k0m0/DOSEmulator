# x87: FSIN/FCOS, Argumentreduktion und unabhängige Intervalle

Stand: 25. Oktober 2026. Geprüftes Referenzpaket des eigenen 486-Kerns. FSINCOS, vollständiges x87 und M3 bleiben offen; historische Nachweise gelten für ihre dokumentierten Builds.

## Operation und Rechenkern

FSIN (`D9 FE`) beziehungsweise FCOS (`D9 FF`) ersetzt ST(0) durch den Sinus beziehungsweise Cosinus des Arguments in Radiant. TOP und die übrigen sieben Register bleiben erhalten. Sinus erhält das Vorzeichen von ±0; Cosinus liefert für beide Nullen exakt +1. Bei endlichen Beträgen **ab 2^63** bleiben Quelle und Tags unverändert, C2 wird gesetzt und es entsteht kein neuer numerischer Fehler. NaNs, Unendlich und Unsupported haben eigene Antworten vor der Bereichsprüfung.

`Float80Math.SineCosine` rechnet ausschließlich mit BigInteger-Intervallen, voller 80-Bit-Präzision unabhängig von PC24/53/64 und ausdrücklich angewandten vier RC. Kein Host-double und keine native Ausführung im Emulator.

Die Argumentreduktion verwendet die bereits geprüfte Machin-π-Einschließung und ein an beiden Intervallenden eindeutiges nächstes Vielfaches von π/2. Der Rest liegt in [−π/4,π/4]; die vier Quadranten bestimmen Vorzeichen und die Wahl von Sinus/Cosinus. Nach außen gerundete Reihen für sin(r)/r und cos(r) liefern die Ergebnisgrenzen. Cosinus wird über die Betragsgrenzen seines monotonen Asts eingeschlossen. Vor unklarer Reduktion, verschwindender Restgrenze oder nichtpositiver Betragsgrenze wird die Präzision erweitert. Start sind 256 Festkommabits, Erweiterung um jeweils 128 Bits, bis beide Grenzen dieselben rohen Bits, Flags und C1 liefern.

Für `0<|x|<2^-64` werden die strikten rationalen Grenzen `x−x³/6 < sin(x) < x−x³/6+x⁵/120` und `1−x²/2 < cos(x) < 1−x²/2+x⁴/24` für den positiven Betrag ausgewertet. Sie erhalten die Rundungsrichtung auch beim kleinsten Denormal und an der Grenze zur minimalen Normalzahl. Negative Sinusantworten werden gespiegelt; Cosinus ist gerade. Der gemeinsame FPTAN-Reihenkern bleibt unverändert.

## Status und Fehler

Endliche in Bereich liegende Nichtnullargumente melden im Referenzvertrag PE, gegebenenfalls DE/UE. Unmaskiertes IE/DE erhält Quelle/TOP/Tags; PE/UE übernimmt die Antwort. UE benutzt die vorhandene Exponentenantwort +24576. Die Tininess-Prüfung vor Denormalisierung bleibt erhalten. Keine neuen ZE/OE; maskiertes sticky ZE bleibt erhalten. Maskierter Quell-Unterlauf schreibt Indefinite an das bestehende ST(0), unmaskierter erhält die rohen Register. Der Stack wird nie bewegt. Ein voller Stack beziehungsweise ein belegter hypothetischer Pushplatz behindert die beiden Befehle deshalb nicht.

C1 folgt der mathematischen Rundung. **C2 wird ausdrücklich geprüft**, undefinierte C0/C3 sind aus dem Architekturvergleich ausgeschlossen. Bei C2=1 ist C1 im Modell stabil null; dies ist keine Zusage für undefinierte Chipbits. Quiet-/Signaling-NaNs, Unsupported, ±∞, Denormal/Pseudo-Denormal, alle Quadranten und Nachbarn von π/4, π/2 und π sind geprüft.

**3975/3975 Regressionen, 0 Warnungen/Fehler; 196 neue CPU-Fälle.** Dazu gehören alle PC/RC/Masken, voller/leerer Stack, erhaltene andere Register, Präfixe, NM, LOCK/UD, tatsächlicher Fetch-PF mit Reparatur ohne vorzeitige Änderung, TF nach dem Commit und echte MF-/FNCLEX-/IRETD-/WAIT-Rückkehr. Die vorhandene FERR-/IRQ13-Leitung bleibt erhalten. Keine allgemeine Bus-/Fault-Konkurrenz-/Siliziumzusage.

## Unabhängige mathematische Prüfung

**9410 Zahlen-/Zustandsfälle** vergleichen reinen Kern und tatsächlichen Decoder strikt mit einem getrennten Python-Integer-/Fraction-Oracle. Dieses reduziert um ein ganzes Vielfaches von π und spiegelt an π/2; π stammt unabhängig aus `4 atan(1)` mit rationalen Quadratwurzel-/Halbwinkelgrenzen. Einzelne rationale Potenzen und Fakultäten mit alternierendem Rest liefern kleine Winkel. Sinus-/Cosinus-Doppelwinkelformeln, nach außen gerundet, liefern die endgültigen Antworten. Das unterscheidet sich von produktiver π/2-Reduktion und Festkommarekurrenz.

**1724 unterschiedliche Rundungsprobleme** sind bei **320 und 448 Binärstellen** unabhängig eingeschlossen. Beide Enden und beide Präzisionen müssen in selbst kodierten 80-Bit-Werten, Flags und C1 übereinstimmen. Kein Host-float oder Produktcode im mathematischen Oracle. Auch die extrem kleinen Antworten verwenden strikte rationale Grenzen an binären Rundungsgrenzen.

Die zusätzliche tatsächliche moderne AMD-x64-Referenz bestätigt Status, C2, Tags und erhaltene Register in allen 9410 Fällen. Unter **3820 berechneten Antworten mit |x|≤1 Radiant** weichen **drei Werte** um höchstens **einen benachbarten darstellbaren Schritt** ab. Unter **3040 größeren numerischen Argumenten** weichen **1900 native Werte** von der mathematischen Antwort ab; dafür wird keine allgemeine native Genauigkeitsgrenze behauptet. **414 C1-Antworten** unterscheiden sich von der mathematischen Richtung. Die native Referenz ist ausschließlich ein Prüfwerkzeug mit FXSAVE64/FXRSTOR64; der moderne AMD-Host ist kein physischer 486. Der mathematische Rundungsvertrag bildet keine begrenzte interne historische Chip-π-Konstante nach.

## Tatsächliche DOS-Gäste

**FPSIN.COM besteht 16 Kaltstarts und prüft beide Befehle:** MS-DOS 6.22 und FreeDOS jeweils zweimal im Real Mode, privaten Protected Mode und privaten 4-KiB-Paging; originales EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start **220 Fälle, 72 pending Umgebungen, acht Quell-Unterläufe, 81 C1-Aufrundungen und 432 aktive rohe Registervergleiche**, darunter vier C2-Antworten ohne Registeränderung. Real/V86 **1782 ESC**, privates PE/Paging **1856 ESC**. Ergebnis, TOP/Tags, erhaltene Register/EFLAGS, Quellen, Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr sind unabhängig geprüft. Wiederholungen stimmen in virtueller Zeit, Bild- und Nachweishashes überein; keine Gastwrites/Resets. **874 absichtlich verfälschte Gastnachweise scheitern.**

Die acht privaten PE-/Paging-Starts erzeugen je acht tatsächliche MF-/IRETD-/WAIT-Rückkehrvorgänge: PE nach beiden Befehlen, UE nach Sinus, DE nach beiden Befehlen, IE und je ein Quell-Unterlauf nach beiden Befehlen. Zusammen **64 neue Gastrückkehrvorgänge**, mit vollen pending 108-Byte-Abbildern, übernommener beziehungsweise unterdrückter Antwort und tatsächlichem FRSTOR/FNCLEX/IRETD samt WAIT-Wiederholung.

Der Gast restauriert seine ursprünglichen vollständigen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die übrige Zahlenmatrix erfasst pending Zustände gezielt mit FNSAVE; externe FERR-/IRQ13-Rückkehr bleibt die getrennte FPERR-Probe. Ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Gemeinsamer Nachweis und Reproduktion

Auf denselben endgültigen sieben DLLs bestehen zusätzlich 16 FPTAN-, 16 FPERR-, acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts. Zusammen **67 Kaltstarts und 2116 verworfene Gastoracles**. Zwölf übrige Zahlenmatrizen sind frisch gegen den aktuellen Decoder geprüft. Zusammen **143256 definierte Zahlenfälle mit nativer Vergleichsreferenz**, darunter 3932 F2XM1-, 6169 Logarithmus-, 3752 FPATAN-, 4704 FPTAN- und 9410 FSIN-/FCOS-Fälle mit strikter unabhängiger Mathematik, plus 36 getrennte undefinierte BCD-Diagnosen. Die Remainderreferenz führt 138982 Einzelbefehle aus.

**69 PowerShell-Skripte/225 Checks, 91 Python-Prüfer, 20 identisch regenerierte Vektor-/Include-/Native-/COM-Dateien und 440 Quellhashes** sind geprüft. Zehn geschützte Eingaben, 53 private Baselines, C64 und HEAD bleiben unverändert. Benutzer-Spiel-COW nicht geöffnet; keine nativen Fenster/Eingaben oder Commits/Pushes. Gesamtnachweis über `artifacts/tests/m3-sincos-release-path.txt`.

```powershell
.\scripts\test-x87-sincos.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Historische Performancezeiten wurden nicht pauschal wiederholt. Die kurze stumme 32-Tick-Doom-Demo bleibt ein begrenzter Extender-/Bildnachweis; keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme. Vier Clocks pro Step bleiben Entwicklungstiming, keine historische 486/33-Kalibrierung.

Als Nächstes **FSINCOS mit beiden tatsächlichen Zielen, Bereich/C2 und getrennten Stack-/Commit-/Fehlerantworten**. Danach übrige M3-Geräte/Snapshots und M4–M8. Vollständiges x87 und M3 bleiben offen; Windows 95, Pentium, Netzwerk und Voodoo bleiben nicht abgenommen.

## Primärquelle

[Intel SDM Volume 2A](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf), **FCOS 3-331 bis 3-332, FSIN 3-381 bis 3-382**: Operation, Bereich/C2, Nullantworten, Status und Ausnahmen sowie beschränkte Genauigkeit endlicher interner π-Reduktion. Numerische Rundung, ausgewählte UE-Antworten und native Näherungsgrenzen sind zusätzlich durch die dokumentierten unabhängigen Oracles geprüft.
