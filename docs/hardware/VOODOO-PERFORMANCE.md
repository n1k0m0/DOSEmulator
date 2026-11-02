# Voodoo Graphics: Logo und Ausführungskosten

## C58: Motorhead-Profilierung, 2. November 2026

Der separate Entwicklungskandidat `c58-20261102-motorhead-performance-dev`
reduziert tatsächliche Hostarbeit im unveränderten originalen Spielpfad.
Alle 14 vorhandenen Starter, Benutzerinstallationen, die gemeinsame C54-Auswahl
und sämtliche bekannten C54-/C55-/C56-/C57-Produkte bleiben bytegleich.
**Flüssige Glide-Spiele bei echter 1:1-Zeit bleiben offen.** Ein korrekter
Zeitgleichlauf allein belegt keinen ausreichenden Spieldurchsatz.

### Befund und Umsetzung

Passives Sampling nach 20 Sekunden normaler JIT-Aufwärmung zeigt weiterhin
Kosten in den tatsächlich ausgeführten Gradientenbefehlen, nativen
Seitentranslationen und der Rasterarbeit. In der benannten Sampling-Kohorte
liegen die tiefsten sichtbaren Frames von `Shade`, `SampleTexture` und
`ReadTexel` bei etwa 17,2, 7,3 und 4,2 Prozent. Inlining, Überlappungen und
Profilerlast verhindern eine additive oder exakte Zyklusrechnung. Die
getrennte Zugriffszählung zeigt überwiegend echte Dreiecksparameter und
unbestückte TMU-Nullparameter; Statuspolling ist hier kein Hauptengpass.

Die beiden häufigen MemoryBus-Abfragen verwenden geordnete Schleifen ohne
die vorherigen eingefangenen LINQ-Prädikate. Der native Helfer schreibt nur
geänderte Integerregister zurück; eine Änderung der rohen x87-Gruppe schreibt
weiterhin die vollständige Gruppe in ursprünglicher ABI-Reihenfolge.

Innerhalb desselben unterstützten Befehlsblocks dürfen getrennte
Lese-/Schreib-/Execute-Translationen eine rein lesende Gerätegrenze überleben,
wenn der tatsächliche Owner-Speicherzähler unverändert bleibt. Der vollständige
partielle CPU-Zustand wird vor dem Owner-Service festgeschrieben. RAM-Schreiben,
schreibbare RAM-Leases, Firmware-Redecode und geänderte A20-/DRB-/Hole-Abbildung
verwerfen die Wiederverwendung. Fehlender vertrauenswürdiger Zähler fällt auf
das bisherige Verwerfen zurück. Jeder neue Helfereintritt und native CPU-Lauf
beginnt mit leerem Cache; Codebytes, Daten, Segmentgrenzen und Kartenvertrag
bleiben aktuell geprüft. Reset/PIC und Gerätefristen bleiben beim Owner.
Cache, Speicherzähler und Diagnosezähler sind abgeleitet und ungespeichert.

Im Rasterer wird eine Perspektivdivision nur bei sicher darstellbarem
Zähler durch dieselbe vorzeichenbehaftete 64-Bit-Division ersetzt. Breite Werte
und `Int64.MinValue / -1` behalten den bisherigen Int128-Pfad. Unbenutzte
W-/RGB-Interpolation und wirkungslose Zielpixellesen bei abgeschaltetem
Blending entfallen. Texturen, jedes Ausgabepixel, Kartenfelder, Coverage,
Schreibfolgen und virtuelle Grafikfristen bleiben gleich.

Ein größerer x87-Arithmetikversuch wurde nach schlechterer Spielmessung
vollständig zurückgenommen. `Float80Math.cs` und der native FST-Speicherpfad
besitzen wieder exakt ihre C57-Bytes. Keine neue Rundungs-/NaN-Politik, keine
übersprungenen Gastbefehle, Taktänderungen oder zusätzlichen Worker.

### Getrennte Messungen

Alle maßgeblichen Prozesse laufen seriell mit normalen .NET-Stufen,
20 Sekunden separater Opfer-Aufwärmung und frischem Messausgangszustand.
Kernbindung gilt ausschließlich für den privaten Prüfer.

| Benannte Kohorte | Ausgang | Optimiert | Aussage |
| --- | ---: | ---: | --- |
| C4 gegen C57, originales Rennen, je vier Prozesse / rund 48 s | 45 Pufferwechsel / 0,9370 pro s | 70 / 1,4577 pro s | +55,57 % in dieser Kohorte; enthält MemoryBus-/Commitänderungen |
| Dieselbe C7-DLL, nur Translationserhalt aus/an, je vier Prozesse / rund 48 s | 169 / 3,5207 pro s | 188 / 3,9162 pro s | +11,23 %; Einzelbereiche 3,333–3,666 und 3,665–4,500 |
| Gleiche tatsächliche PCI-/MMIO-Rasterszenen, C7 gegen C8, 5120 je Seite | 1,7930384 s | 1,3973766 s | 22,07 % weniger Hostzeit; ganzer Karten-/VRAM-/Textur-/Pixelzustand gleich |
| Originales Rennen C7 gegen C8, je vier Prozesse / rund 48 s | 126 / 2,6238 pro s | 131 / 2,7287 pro s | +4,00 % innerhalb deutlicher Streuung; kein belastbarer zusätzlicher allgemeiner Spielgewinn |

Jede Spielmessung verwendet denselben vollständigen originalen
128-MiB-/CPU-/Geräte-Ausgangszustand. Die Messkapseln sind keine strikten
Altproduktimporte oder Formatmigration. Native spätere CPU-/RAM-/Medienverläufe
dürfen sich mit anderem Durchsatz unterscheiden. Die Kohorten haben andere
Hostbedingungen und werden weder über ihre absoluten Raten verglichen noch
zu einem Gesamtfaktor multipliziert. Zeitgleichlauf der Cachekohorte:
99,958–100 Prozent; der letzten Raster-/Spielkohorte: 99,821–100 Prozent.
Die wenigen tatsächlichen Pufferwechsel pro Sekunde reichen weiterhin nicht
für flüssiges Spielen. Der verworfene Arithmetikversuch liefert in seiner
eigenen Kohorte 8,90 Prozent weniger Durchsatz; dessen Vorläufe sind keine
Freigaben oder aktuelle Regressionsergebnisse.

### Finale Produkt- und Gastprüfung

C58 besteht **18241 gemeinsame Regressionen, 73 native Standardgruppen und
drei separate Snapshot-Audits**, Build ohne Warnungen/Fehler. Der Prüfer
vergleicht weiterhin 2880 ganze native CPU-/2-MiB-RAM-/Parameterbytezustände,
31 Grenzfälle und 19 zustandsreine Ablehnungen. Vier neue unabhängige
Always-clear-/Reuse-Gegenproben decken lesende Grenzen, RAM-/Lease-Schreiben
und eine tatsächliche PTE-Änderung nach Wiederverwendung ab; dazu echte native
Ausführung der lesenden Kontrolle und fünf reale Mapper-Zählerkontrollen.
In dieser lesenden Kontrolle sinken tatsächliche Translationen von 106 auf 7.
Die PTE-Probe erhält die bisherige Live-Pagewalk-Politik, ohne neue physische
TLB-/Siliziumzusage. 20117 unabhängige BigInteger-Divisionskontrollen und alle
unterstützten RGB-Kombinatorabhängigkeiten sichern den Rasterpfad ab.

Eine neue eigene Windows-98-Kopie bootet auf exakt den finalen Modulen mit
P55C/MMX 233 MHz, 128 MiB, S3 und SST-1. Das originale silber-/orangefarbene
Herstellerlogo ist ohne Abschaltoption sichtbar, danach das texturierte
Motorhead-Rennen. Natürlicher Anwendungsschluss und regulärer Windows-Shutdown
bis „It's now safe to turn off your computer.“ bestehen. Aus dem versiegelten
eigenen COW sind alle 13 originalen Treiber- und 445 benannten Spieldateien
unverändert geprüft. Der eigene neue Abschlussbericht war beim aktiven Schnitt
noch nicht vorhanden und liegt erst danach mit erfolgreichen Start-/Wait-/
Handle-Rückgaben und tatsächlichem Spiel-Exitcode 1 vor. Kein neuer SDK-Exitcode
wird daraus abgeleitet.

Zwei strikte Produktimporte der tatsächlichen Rennsicherung vergleichen alle
128 MiB, native CPU-Felder und vollständige Geräte-/Schedulerzustände. Sieben
ganze Gerätefortsetzungen bei geparkter CPU, alle Pixel, 2400 spätere
zeit-/bytegleiche RAM-Stereo-Frames je Seite und sämtliche Endmedien stimmen
ohne ausgeschlossene Felder überein. Der Schnitt besitzt Queue 0 / Busy false;
dies ist kein aktiver SST-FIFO-Nachweis. Ein dritter Import über den wirklichen
GUI-Ausführungsfactory-Pfad fährt acht Sekunden tatsächlich weiter:
8029480 Helferbefehle, 1078837 Parameterstores, 19 geänderte Bilder,
384000 RAM-Stereo-Frames und 99,878 Prozent Zeitgleichlauf. Das ist funktionale
native Fortsetzung, kein deterministischer nativer Replay-Nachweis.

Der alte C57-CLI-Schnitt wird mit beobachtetem kontrolliertem Exit 1 vor
Zweiganlage abgewiesen: `WHPX: Unsupported WHP checkpoint format.` Keine
Migration. 991 Code-/Projektdateien (531 C#), Zustand 49 Typen / 992 Mitglieder /
44 Operationen, elf alte ROM-Nutzbilder und 43 alte geplante Callbackkörper
sind geprüft. Der native Speicherbeobachter ist davon getrennt. 125 separate
Produktdateien, davon 28 gegenüber C57 verändert. Keine Hostfenster, Eingaben,
GL-/Hostaudioabnahme, Aufnahmen oder vollständige M7-Abnahme. Kein Commit/Push;
Automation bleibt pausiert. Privater Nachweis:
`artifacts/tests/motorhead-profile-release-path.txt`.

## Historischer C57

Stand: 1. November 2026. Separat privat eingefrorener C57. Die bestehenden
Starter und Benutzerinstallationen bleiben unverändert. Flüssige originale
Glide-Spiele und die vollständige M7-Abnahme bleiben Zielkriterien.

## Originales Startlogo

Der Originalpfad verwendet Windows 98, das P55C-Entwicklungsprofil mit
233 MHz/128 MiB, S3 plus SST-1 und unveränderte Voodoo-3.01-/Glide-Dateien.
Das ursprüngliche Herstellerlogo wird mit gelöschtem `FX_GLIDE_NO_SPLASH`
ausgeführt. Eigene Win32-Starthelfer protokollieren den wirklichen
Prozessstart, Wait, Exitcode und das Schließen beider Handles unter jeweils
neuen Dateinamen und Kennungen. Ein älterer Bericht wird nicht als neuer
Prozessabschluss verwendet. Der neue SDK-Starthelfer wurde durch den
Host-Virenscanner isoliert. Der finale Logonachweis startet deshalb das
unveränderte TEST00 direkt mit Arbeitsverzeichnis `D:\GLIDE` und den
bewährten Argumenten `-n 30` über eine eigene reine Datendiskette.
Zwischenbilder zeigen das animierte Original-Logo; danach kehrt das SDK
natürlich zum Desktop zurück. Eine frische eigene Textkennung bestätigt
die Rückkehr von START/WAIT. Daraus wird kein neuer gemessener Win32-
SDK-Exitcode abgeleitet. Der früher in dieser Arbeitsreihe separat
gemessene SDK-Exitcode 0 bleibt seinem damaligen Kandidaten zugeordnet.
Der finale originale Spielprozess liefert beim normalen Schließen
tatsächlich Exitcode 1 mit erfolgreichen Start-/Wait-/Handle-Rückgaben.

Die S/T-Setupregister gehören zur gewählten TREX-Einheit. Ihre Chipauswahl
wird jetzt auch für die unterhalb des Texturkontrollblocks liegenden festen
und gleitenden Setupregister berücksichtigt. Andere getrennte FBI-/TMU-
Latchwirkungen sind damit nicht pauschal abgenommen.

Die originale Logo-Sequenz übergibt tatsächlich negative nichtendliche
S/T-Werte. Rohes IEEE-Payload und Nichtendlichkeitsmaske bleiben im
Registerzustand und in jedem geordneten Befehl erhalten. Nur die abgeleitete
Rastervorbereitung verwendet für diese sechs S/T-Latches die begrenzte
eigene vorzeichenabhängige Überlaufpolitik der 14.18-Darstellung. Andere
aktive nichtendliche Parameter bleiben ausdrücklich abgewiesen. Dies ist
eine dokumentierte Entwicklungsregel, keine unabhängige physische
NaN-/Inf-/Siliziumabnahme.

## Ausführung

Die Bildschirmumrechnung wird durch tatsächliche Schreibadressen im
angezeigten Frontbuffer invalidiert. Backbuffer und Hilfspuffer behalten
ihre Speicherwirkungen; eine physische Überlappung invalidiert weiterhin
das Bild. Swap, Layout, Gamma und Restaurierung behalten ihre eigenen
Invalidierungen.

Die native FST-/FSTP-Hilfe erhält rohe 80-Bit-Werte, Rundung, Status, TOP und
Tags. Für normale Speicherformate verwendet sie ganzzahlige Guard-/Sticky-
und Tie-Bits; Sonderbereiche behalten die genaue allgemeine Umrechnung.
Direkt benachbarte isolierte FSTP-Zugriffe können ohne einen erneuten
nativen CPU-Lauf ausgeführt werden.

Eine weitere begrenzte WHPX-Hilfe führt die tatsächlich vorhandenen
Vertex-, Nullparameter- und Gradientenbefehle einzeln aus. Jede Rechnung,
Quellenlesung, Flagwirkung und jeder Karten-DWORD bleibt erhalten. Kein
Glide-Aufruf wird ersetzt. Kartenbefehle, Uploads, Paletten und allgemeine
RAM-Schreibbefehle bleiben außerhalb dieser Hilfe; insbesondere bleibt
das beobachtete RAM-XCHG im nativen Pfad.

Zugelassen sind geprüfte geschützte 32-Bit-Kontexte mit tatsächlichen
normalen oder Expand-down-DS-Grenzen, aktuellen Codebytes und getrennten
Lese-/Schreib-/Execute-Übersetzungen. Debug-/Trap-/V86-, unmaskierte x87-
und reservierte Präzisionskontexte fallen zurück. Ein expliziter reiner
Parameter-Latch-Vertrag verhindert die Wiederverwendung bei opaken
Speichergeräten.

An fälligen Gerätegrenzen wird der ganze partielle native Registerzustand
zuerst festgeschrieben. Der Owner bedient Scheduler, Speicherabbild und
Reset/PIC; anschließend werden die Seitentranslationen verworfen. Eine
geänderte CPU-Generation beendet den Block ohne alten Kontext
zurückzuschreiben. Es gibt keine parallele Gastgerätezeit, keine geänderten
CPU-/Grafiktakte und keinen neuen Snapshotzustand für diese Hilfen.

## Nachweisgrenzen

Die permanenten nativen Gegenproben vergleichen vollständige CPU-Zustände,
ganze eigene 2-MiB-RAM-Bilder und jedes geordnete Parameterbyte gegen echte
native Ausführung. Die Matrix umfasst TOP, PC/RC, normales und
Expand-down-DS, Paging und opake/PIC-/Codekontrollen. Getrennte Schnitte
prüfen partielle Commitgrenzen, tatsächliche Ereignisyields, geänderte
RAM-Quellen und Reset ohne veraltete Kontextrestaurierung.

Die abschließende Spielmessung verwendet frische private Zweige derselben
originalen Rennsicherung. Beide Betriebsarten verwenden dieselbe DLL und
denselben delegierenden Kartenbeobachter. Eine separate Opferfortsetzung
wärmt die normalen .NET-Stufen auf; die Messung beginnt danach wieder am
vollen identischen Ausgangszustand. Die Zählung tatsächlicher angenommener
Swapbefehle und des offenen Swapstands liefert abgeschlossene
Pufferwechsel. Bildhashwechsel, Host-/Gastzeit, echte MMIO-Exits und
DSP-Samples werden getrennt erfasst. Affinität gilt ausschließlich für den
privaten Messprüfer.

Unterschiedlicher Durchsatz erzeugt unterschiedliche native CPU-, RAM-,
Bild- und Medienfortsetzungen. Daraus folgt keine deterministische native
Replay-Zusage. Strikte originale Produktimporte und volle Gerätevergleiche
bei geparkter CPU werden getrennt geprüft. Audio bleibt im RAM; native
Hostfenster, Eingaben, GL, Audioendpunkte und Aufnahmen werden nicht
verwendet.

## Ergebnis

| Dieselbe finale DLL / originale Rennsicherung | Ungebündelt | Gebündelt |
| --- | ---: | ---: |
| Serielle Prozesse | 4 | 4 |
| Wirklich abgeschlossene Pufferwechsel | 32 | 65 |
| Gesamte gemessene Hostzeit | 32,000675 s | 32,000981 s |
| Pufferwechsel je Hostsekunde | 1,00 | 2,03 |
| Bereich der Einzelläufe | 0,75–1,50 | 1,87–2,37 |

Das entspricht in dieser benannten Last etwa Faktor 2,03. Die gemeinsame
Zeitgleichlaufspanne ist 99,994–100 Prozent. Die Ergebnisse sind weder ein
allgemeiner C56-/C57-Windowsvergleich noch eine Zusage flüssiger Glide-
Spiele. Frühere Prototypen mit später Opt-in-Prüfung und eine private
Wiederherstellung über verschiedene native Laufzeiten bleiben ausdrücklich
explorative getrennte Kohorten.

18238 gemeinsame Regressionen, 72 native Standardgruppen und drei
Snapshot-Audits bestehen; Build ohne Warnungen/Fehler. 2880 ganze native
CPU-/2-MiB-RAM-/Parameterbytevergleiche, 30 Grenzkontrollen, 19 zustandsreine
Ablehnungen, 96 benachbarte FSTP-Kontrollen und 53248 genaue
Umrechnungsgegenproben werden getrennt gezählt. 991 Code-/Projektdateien
sind qualitätsgeprüft; die Zustandsschnittstelle bleibt 49/992/44.

Ein echter originaler Rennschnitt importiert alle 128 MiB und jedes native
CPU-/Gerätefeld exakt. Zwei unabhängige Importe vergleichen sieben volle
Gerätefortsetzungen, Bilddaten, 2400 zeit-/bytegleiche spätere Stereo-Frames
je Seite und sämtliche Endmedien bei geparkter CPU. Der Schnitt besitzt
keinen offenen SST-Befehl und wird nicht als aktive FIFO-Abnahme ausgegeben.
Eine getrennte strikte GUI-Factory-Restaurierung erreicht tatsächlich
weitere originale Rennbilder, DSP-Fortschritt und 384000 publizierte
Stereo-Frames ohne Fehler; dies ist eine funktionale native Fortsetzung.

Alle 13 Treiber- und 445 benannten originalen Spieldateien stimmen nach
regulärem Windows-Shutdown weiterhin bytegenau. Die 13 bisherigen Starter,
der separate C55-Demostarter und sämtliche bekannten C54-/C55-/C56-Produkte
bleiben erhalten. Die gemeinsame Auswahl bleibt C54. Der genaue private
Nachweis steht unter `artifacts/tests/voodoo-performance-release-path.txt`.
Weitere Spiele, ursprüngliche Win95-3D-Treiber, variable LOD-/NCC-/Filter-/
Blend-/Triple-Buffer- und physische Bus-/Analogpräzision bleiben offen.

Der erste direkte SDK-Vorlauf verwendete die abweichende Argumentfolge
`-n30` und nicht das bewährte `-n 30`; dieser Gastdiagnoselauf wurde getrennt
ausgeschlossen. Der tatsächliche finale Logonachweis verwendet die oben
angegebene ursprüngliche Arbeitsumgebung. Der gesperrte Starthelfer wird
nicht mitgeliefert; am Host-Virenschutz wurde nichts geändert.

## Quellen

- [SST-1-Spezifikation 1.61](https://bitsavers.org/components/3dfx/Voodoo1_SST-1_Spec_r1.61_199912.pdf): Chipauswahl, Setupregister und Formate.
- [WHvTranslateGva](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvtranslategva): getrennte Zugriffsrechte und Page-Table-Bits.
- [WHvSetVirtualProcessorRegisters](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvsetvirtualprocessorregisters): vollständige native Registerübergabe.
- [MAME-SST-Quellen](https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo.cpp): technische Vergleichsquelle für begrenzte vorzeichenabhängige Float-/Festkommaüberläufe; keine physische Referenz für die eigene Logo-Regel.
