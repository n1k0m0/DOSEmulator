# x87: FYL2X/FYL2XP1, genaue Produktintervalle und tatsächliche Stackrückkehr

Stand: 25. Oktober 2026. Geprüftes Referenzpaket des eigenen 486-Kerns. Vollständiges x87 und M3 bleiben offen; historische Nachweise gelten für ihre jeweiligen Builds.

## Operationen und Zahlenvertrag

FYL2X (`D9 F1`) berechnet `ST(1) · log2(ST(0))`, FYL2XP1 (`D9 F9`) entsprechend `ST(1) · log2(1+ST(0))`. Erfolgreiche Ausführungen schreiben nach ST(1) und entfernen anschließend ST(0). Die übrigen sechs Register, deren Tags und EFLAGS bleiben erhalten. Der Rechenkern arbeitet mit dem vollen 64-Bit-Signifikanden des 80-Bit-Formats unabhängig von PC24/53/64 und verwendet die vier ausdrücklichen RC-Rundungsarten.

FYL2X benötigt eine positive endliche logarithmierte Zahl; seine Sonderwertantworten werden getrennt behandelt. Der dokumentierte endliche FYL2XP1-Bereich ist `sqrt(1/2)−1 ≤ x ≤ sqrt(2)−1`. Der Vektorprüfer bestätigt diese Grenzen für jede endliche FYL2XP1-Eingabe durch die exakte rationale Ungleichung `1/2 ≤ (1+x)^2 ≤ 2`. Außerhalb dieses Bereichs sind Resultate und erzeugte Fehler architektonisch nicht zugesichert. Die dortige mathematische Fortsetzung beziehungsweise Invalid-/Polantwort des eigenen Kerns ist kein Kompatibilitätsnachweis.

`Float80Math.Log2Multiply` rechnet ausschließlich mit Ganzzahlen und nach außen gerundeten Intervallen. Es wird weder der Logarithmus zunächst auf ein Gastregister gerundet noch das folgende Produkt gesondert verkürzt. Erst das vollständige Produkt wird gerundet. Host-double und native FPU-Befehle werden nicht zur Emulation verwendet.

Für FYL2X wird die exakte dyadische Quelle als `2^k · m` mit `1 ≤ m < 2` dargestellt. Aus `z=(m−1)/(m+1)` folgt `ln(m)=2·Σ z^(2j+1)/(2j+1)`, wobei `0 ≤ z < 1/3`. Gerichtete ganzzahlige Summen und eine geometrische Restgrenze schließen ln(m) ein; das ganze k und die vorhandenen unabhängigen ln(2)-Grenzen liefern log2(x). Exakte Zweierpotenzen benutzen einen eigenen ganzzahligen Pfad, damit eine exakt darstellbare Antwort nicht dauerhaft zwischen zwei Intervallgrenzen liegt.

Für FYL2XP1 im geprüften Bereich wird stattdessen

`ln(1+x)/x = 2/(2+x) · Σ ((x/(2+x))^(2j)/(2j+1))`

eingeschlossen. Das exakte x bleibt ein eigener Faktor des Produkts. Selbst bei `x=2^-16445` entsteht dadurch kein vorzeitiges `1+x=1` und kein künstlicher Null-Logarithmus. Die quadratischen Reihenterme haben in diesem Pfad höchstens das Verhältnis 1/9. Der Kern startet mit 192 Festkommabits und erweitert um jeweils 128 Bits, bis beide Grenzen denselben rohen Ergebniswert, dieselben Flags und dasselbe C1 liefern. Der gemeinsame ln(2)-Cache enthält ausschließlich rekonstruierbare Zahlenkonstanten.

## Sonderwerte, Ausnahmen und Stack-Commit

Signierte Null, NaNs/Unsupported, Denormal/Pseudo-Denormal, Unendlich sowie die verschiedenen Logarithmusdomänen sind getrennt geprüft. Der Zweiquellen-NaN-Vertrag bleibt mit der bestehenden Arithmetik konsistent. Ungültige negative FYL2X-Quellen sowie `0·log2(0)`, `0·log2(+∞)` und `∞·log2(1)` melden Invalid. Bei FYL2XP1 gilt das ebenfalls für unendliches ST(1) und ±0 in ST(0). Ein endlicher von null verschiedener Multiplikator am FYL2X-Pol meldet Zero Divide; die geprüfte Antwort bei unendlichem Multiplikator liefert Unendlich ohne neue ZE. Vorzeichen werden aus Multiplikator und Logarithmus bestimmt, einschließlich ±0 bei FYL2XP1.

Der begrenzte Referenzvertrag meldet bei nichtnulligen endlichen mathematischen Produkten Precision, auch bei einer exakten ganzzahligen Zweierpotenz-Antwort. Nullprodukte und Sonderwertausgänge erhalten ihre getrennten Antworten. C1 folgt der Rundung des mathematischen Produkts. Undefinierte C0/C2/C3 bleiben im Modell stabil und sind aus dem nativen Architekturvergleich ausgeschlossen.

**Unmaskiertes IE/DE/ZE erhält beide Quellen und unterdrückt den Pop. Unmaskiertes OE/UE/PE übernimmt Ergebnis und Pop.** Über-/Unterlauf benutzen die vorhandene Exponentenantwort mit −/+24576. Tininess wird nach Präzisionsrundung mit unbeschränktem Exponenten vor Denormalisierung geprüft; die dokumentierte PE-/UE-Antwort gilt auch für einen mathematisch exakten winzigen Logarithmusfall. Ein maskierter Leerstack schreibt Indefinite nach ST(1) und führt den Pop aus; ein unmaskierter Stackfehler erhält rohe Quellen, TOP und Tags.

NM, LOCK/UD, tatsächliche Fetch-PF-Reparatur, TF nach dem bereits übernommenen Pop und MF-/FNCLEX-/IRETD-/WAIT-Rückkehr sind geprüft. Die vorhandene externe FERR-/IRQ13-Leitung bleibt erhalten. Keine universelle historische Intel-/AMD-/486-Stepping-, Bus-, Zyklus- oder Fault-Konkurrenzzusage.

## Unabhängige Zahlen- und CPU-Nachweise

**3529/3529 Regressionen, 0 Warnungen/Fehler; 129 neue CPU-Fälle.** Darunter alle PC/RC, niedrige Signifikandbits, getrennte Masken/Commits, signierte Null, volle/leere Stacks, Präfixe und tatsächliche Fehler-/Wiederholungsrahmen.

**6169 Zahlen-/Zustandsfälle** vergleichen reinen Kern und tatsächlichen Decoder strikt mit einem unabhängig formulierten Python-Decimal-/Fraction-Oracle. Gerichtete dyadische Eingabegrenzen, korrekt gerundetes Decimal.ln und benachbarte Dezimalwerte schließen den Logarithmus ein. Multiplikation und binäre Kodierung erfolgen separat mit exakten Fraction-Werten. Sehr kleine FYL2XP1-Eingaben verwenden die unabhängige Integralschranke `1−|x| ≤ ln(1+x)/x ≤ 1+|x|`, statt vorzeitig 1+x zu bilden. Exakte Zweierpotenzen werden rational ausgewertet.

Die **1466 unterschiedlichen Rundungsprobleme** werden entweder exakt ausgewertet oder mit **100 und 160 Dezimalstellen** eingeschlossen. Beide Intervallenden müssen jeweils dieselbe binäre Antwort liefern, beide Präzisionen übereinstimmen. Quellbytes, Stack-/Sonderwertantworten, Masken, Tags, sticky Status und alle sechs Ausnahmeflags sind enthalten. Kein Produkt-Quantizer oder Host-float wird als Oracle übernommen.

Eine zusätzliche tatsächliche native x87-Referenz vergleicht denselben Satz. Auf dem modernen AMD-x64-Host Family 26 Model 68 unterscheiden sich **45 Werte** um höchstens **einen benachbarten darstellbaren Schritt** vom mathematisch korrekt gerundeten Ergebnis; **89 C1-Antworten** unterscheiden sich von der mathematischen Rundungsrichtung. Diese Näherungsabweichungen werden getrennt erfasst. Der Emulator folgt dem dokumentierten mathematischen Vertrag. Der native Helfer sichert/restauriert den Host mit FXSAVE64/FXRSTOR64 und ist ausschließlich Prüfwerkzeug; er ist kein physischer 486.

## Tatsächliche DOS-Proben

**FPLOG.COM besteht 16 Kaltstarts:** MS-DOS 6.22 und FreeDOS jeweils zweimal Real Mode, eigener Protected Mode und eigenes 4-KiB-Paging; originales EMM386 NOEMS/RAM jeweils zweimal im tatsächlichen V86-/Monitorbetrieb.

Je Start werden **220 Fälle beider Befehle, 72 pending Umgebungen, acht Leerstackfälle, 17 C1-Aufrundungen und 235 aktive rohe 80-Bit-Registervergleiche** geprüft. Real/V86 führt **1772 ESC**, privates PE/Paging **1855 ESC** aus. Die geringere Zahl aktiver Register nach erfolgreichen Fällen entspricht dem tatsächlich ausgeführten Pop. Ergebnis, TOP/Tags, erhaltene Register/EFLAGS, Quellbytes, FPU-Zeiger/Opcode, Wachen, Seitentabellen und saubere VER/ECHO/Prompt-Rückkehr werden unabhängig geprüft. Wiederholungen stimmen in virtueller Zeit sowie Bild- und Nachweishashes überein; keine Gastwrites oder Resets. **892 absichtlich verfälschte Gastnachweise scheitern.**

Die acht privaten PE-/Paging-Starts führen je **neun tatsächliche MF-/IRETD-/WAIT-Rückkehrvorgänge** aus: Precision nach beiden Befehlen, Overflow, Underflow, Denormal, Zero Divide, Invalid sowie leere erste und zweite Quelle. Insgesamt **72 neue Gastrückkehrvorgänge** mit vollständigen pending 108-Byte-Abbildern. Sie beweisen den jeweils bereits ausgeführten oder unterdrückten Pop, beide Operanden beziehungsweise den Ergebniswert, FRSTOR/FNCLEX/IRETD und erfolgreiche WAIT-Wiederholung.

FPLOG restauriert die vollständigen ursprünglichen 108 FPU-Bytes. Sein privater PE-TR bleibt bis Reset geladen; das ist kein vollständiger CPU-/PIC-/Scheduler-Snapshot. Die übrige Zahlenmatrix erfasst pending Zustände gezielt mit FNSAVE. Tatsächliche externe FERR-/IRQ13-Rückkehr bleibt die getrennte FPERR-Probe; ältere CPUX87-Proben besitzen keine vollständige FPU-Restaurierungszusage.

## Gemeinsamer Nachweis und Reproduktion

Auf denselben endgültigen sieben DLLs bestehen zusätzlich 16 F2XM1-, 16 FPERR-, acht originale DOS4GW-/Doom-, acht x87-Kontroll- und drei Konfigurationsstarts. Zusammen **67 Kaltstarts und 2020 verworfene Gastoracles**. Die übrigen neun nativen Zahlenmatrizen wurden ebenfalls auf dem aktuellen Decoder geprüft. Insgesamt **125390 definierte Zahlenfälle mit nativer Vergleichsreferenz**, darunter 3932 F2XM1- und 6169 Logarithmusfälle mit strikter unabhängiger mathematischer Referenz, plus 36 getrennte undefinierte BCD-Diagnosen. Die Remainderreferenz führt weiterhin 138982 einzelne Reduktionsbefehle aus.

**66 PowerShell-Skripte/216 Checks, 79 Python-Prüfer, 19 identisch regenerierte Vektor-/Include-/Native-/COM-Dateien und 407 Quellhashes** sind geprüft. Zehn geschützte Eingaben, 53 private Baselines, C64 und HEAD bleiben unverändert. Benutzer-Spiel-COW nicht geöffnet; keine nativen Fenster/Eingaben oder Commits/Pushes. Privater Gesamtnachweis über `artifacts/tests/m3-logarithm-release-path.txt`.

```powershell
.\scripts\test-x87-logarithm.ps1 -SkipBuild `
  -Disk <private-original-DOS-HDD> -Overlay <private-pristine-COW> `
  -ConfiguredDisk <private-configured-HDD> `
  -NoEmsOverlay <private-NOEMS-COW> -RamOverlay <private-RAM-COW>
```

Das Skript erzeugt neue private Kopien, unabhängige Referenzen und drei eigene COM-Varianten. Historische Performancezeiten wurden ohne neuen Regressionsanlass nicht wiederholt. Die kurze stumme Doom-Demo ist weiterhin keine allgemeine oder interaktive Spiel-/Sound-/Eingabeabnahme.

Als Nächstes folgt **FPATAN** mit Quadranten, signierten Nullen, Sonderwerten, unabhängigen Genauigkeitsgrenzen und tatsächlicher Fehlerrückkehr. Danach FPTAN/FSIN/FCOS/FSINCOS einzeln, übrige M3-Geräte und koordinierte Snapshots, anschließend M4–M8. Windows 95, Pentium, Netzwerk und Voodoo bleiben nicht abgenommen.

## Primärquellen

- [Intel SDM Volume 2A, FYL2X/FYL2XP1, 3-423 bis 3-426](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf): Operationen, Eingabebereiche, Ergebnistabellen, Status und Ausnahmen.
- [AMD Architecture Programmer’s Manual Volume 5, Revision 3.13, 332 bis 335](https://kib.kiev.ua/x86docs/AMD/AMD64/26569_APM_v5-r3.13.pdf): AMD-Primärhandbuch im Archiv; insbesondere FYL2XP1-Bereich und Ausnahmen. Der dokumentierte eigene Rundungs-/Sonderwertvertrag wird zusätzlich durch die tatsächliche native und unabhängige mathematische Referenz begrenzt.
- [Python Decimal.ln](https://docs.python.org/3/library/decimal.html#decimal.Decimal.ln): korrekte Rundung mit ROUND_HALF_EVEN. Das Oracle erweitert diese Prüfwerte nach außen und kodiert die binären Grenzen selbst.
