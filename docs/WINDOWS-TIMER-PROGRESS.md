# Windows-Zeitgeber während nativer MiniGlide-Ausgabe

Stand: 3. November 2026. Dieser Folgeschritt untersucht den tatsächlichen
[Unreal-Zeitgeberbefund](UNREAL-MINIGLIDE-MEASUREMENTS.md) auf eigenen
Sicherheitskopien. Kalender-/RTC-Synchronisierung bleibt ein anderer Vertrag.
Der gemeinsame C54-Starter, frühere MiniGlide-Produkte und Benutzerplatten
werden dadurch nicht umgestellt. Die Automation bleibt pausiert.

## Nachgewiesene Ursache des großen Rückstands

Der ursprüngliche Hostaufruf von `grBufferSwap` läuft synchron auf dem
Emulations-Owner. Im bewegten Bild wartet die Host-Grafikbibliothek dabei
teilweise mehr als 30 ms. Währenddessen kann der native Gast keine einzige
Instruktion ausführen. Nach dem Aufruf holt die vorhandene Echtzeituhr die
vergangene Hostzeit samt sämtlichen fälligen Geräteereignissen nach.

Der PIT erzeugt diese Impulse tatsächlich. Der korrekt als Flankenlatch
modellierte PIC besitzt aber keine unbegrenzte Warteschlange für IRQ0.
Mehrere Impulse vor der nächsten CPU-Zustellung können daher zusammenfallen.
Windows zählt entsprechend weniger Timerinterrupts und weniger verstrichene
Zeit, obwohl die Scheduler-/Hostzeitquote weiterhin ungefähr 100 Prozent ist.
Die SPEED-Anzeige misst diese Schedulerquote; sie ist kein unabhängiger Beweis
für den Fortschritt aller Windows-Zeitgeber.

Eine neue private Diagnose wiederholt den ursprünglichen Fehler und zählt
direkt an den echten E9-Lesegrenzen:

| Unveränderter C9, Kameradrehung | Host-/virtuelle Zeit | Windows-QPC | PIT-Impulse | IRQ0-ACKs |
| --- | ---: | ---: | ---: | ---: |
| Win95 / Unreal 205 | 20,0142 s | 11,2193 s | 23321 | 13073 |
| Win98 / Unreal 226 | 20,4959 s | 13,7389 s | 20499 | 13741 |

In den zugehörigen Diagnosefenstern verbringt der ursprüngliche native
Bildwechsel insgesamt etwa 10,45 beziehungsweise 8,18 Hostsekunden im
aufrufenden Thread. Die beiden Fenstergrenzen von Aufrufstatistik und
Windows-Timerabfrage sind getrennt dokumentiert. Sie werden nicht als
nanosekundengleiche Messpunkte ausgegeben.

## Kooperativer Bildwechsel

Der neue Host kann genau einen nativen Bildwechsel an einen schlafenden
Worker übergeben. Der Auftrag enthält nur den bereits auf dem Owner gelesenen
Intervallwert und den aufgelösten nativen Funktionszeiger. Der Worker greift
weder auf Gast-RAM oder Register noch auf PIC, PIT, DMA oder Scheduler zu.
Der vollständige ursprüngliche Intervallwert bleibt erhalten.

Die eigene Win9x-DLL wartet mit echten Windows-`Sleep(1)`-Aufrufen und
geprüften Fertigmeldungen. Dadurch können Windows und seine Interrupt- und
Threadverarbeitung weiterlaufen. `grBufferSwap` kehrt zum Spiel erst zurück,
wenn der wirkliche native Aufruf beendet ist. Vorherige Batches werden wie
bisher geleert; spätere Rendereraufrufe laufen erst nach dem angenommenen
Bildwechsel. Es werden keine Ticks injiziert, Interruptlatches erweitert,
CPU-Takte geändert oder Zeitfunktionen des Betriebssystems ersetzt.

Das eigene Portprotokoll ergänzt `FFFDh` für die Annahme und `FFFCh` für die
Fertigmeldung. Nicht wiederverwendete Tokens binden genau einen Auftrag;
falsche, verbrauchte und nach Reset veraltete Tokens werden abgewiesen.
Ein Reset beendet angenommene Hostarbeit und verwirft deren Rückmeldung,
bevor der native CPU-Kontext ersetzt wird. Dispose joint den Worker vor
Freigabe der Hostbibliothek. Ein im externen Grafiktreiber dauerhaft
hängender nativer Aufruf erhält damit keine neue erzwungene Abbruchgarantie.

Die DLL fragt die Erweiterung einmal über den bisherigen, sicheren
Versionsaufruf ab. Dessen 80 öffentliche Versionsbytes bleiben gleich;
das private Rückgabewort der ansonsten void-API meldet `MGS1` (`4D475331h`).
Ein älterer Host meldet null und die neue DLL verwendet dort den bisherigen
synchronen Pfad. Alte DLLs bleiben am neuen Host ebenfalls synchron nutzbar.
Die Korrektur benötigt somit einen passenden neuen Host und die neue DLL.

## Begrenzte Texturkopien

Die längere C1-Serie belegt nach dem großen Fix weiterhin einzelne kurze
Restlücken. Im festen Win95-Szenenfenster entstehen etwa 1,88 GB verwaltete
Allokationen in rund 20 Sekunden; die originalen Level-/Teilzeilenuploads
laufen sehr häufig. Daraus folgt noch keine vollständige Ursachenbindung
jeder verbleibenden Zeitlücke.

C2 verwendet für diese Uploads einen eigenen, nur vom Owner benutzten
Pixelpuffer. Seine größte unterstützte Levelbelegung ist 128 KiB. Jeder
Aufruf prüft und kopiert sämtliche angeforderten aktuellen Gastbytes erneut;
Adressen, Rechte, Dimensionen, Teilzeilen, Reihenfolge und native Rückgaben
behalten ihren bisherigen Vertrag. Die Wiederverwendung beginnt erst nach
Rückkehr des vorigen nativen Verbrauchers. Der Swapworker benutzt diesen
Puffer nicht. Es entsteht kein Cache alter Gastdaten oder Seitentranslationen.

Der gleiche endgültige Prüfer meldet für 512 große/kleine Teiluploadkontrollen
auf C1 23.309.464 allokierte Bytes und Exit 1, auf C2 187.032 Bytes und PASS.
Er prüft jedes Nutzbyte, ursprüngliche Zeilenparameter, Rückgaben und
unveränderte Gastwachen. Die gemessene Heapentlastung ist kein eigenständiger
Nachweis einer bestimmten Spielebildrate.

Die tatsächliche C2-Spielmessung allokiert in der festen Win95-Ansicht immer
noch rund 1,86 GB in 20 Sekunden. Die kleine gezielte Heapentlastung beseitigt
die restlichen Zeitlücken nicht. Der Großteil dieser Allokationen hat andere,
hier nicht vollständig aufgelöste Quellen.

## Chronologische Zustellung nach HLT

Ein zusätzlicher, ausschließlich lesender Hostbeobachter bindet die
Restlücken an tatsächliche native HLT-Ausgänge: Windows wartet mit IF=1,
während die Hostfortsetzung stellenweise erst ungefähr 30 ms später kommt.
Das bisherige Nachholen aller überfälligen Geräteereignisse erzeugt dann
erneut mehrere PIT-Flanken, bevor die CPU den ersten IRQ bearbeiten kann.
Die Hauptlücken der untersuchten C2-Abschnitte haben dabei keinen neuen
Gen2-GC und keine entsprechende Folge synchroner Swapaufrufe.

C3 behandelt zunächst diese bekannte HLT-Wartezeit chronologisch. Der unveränderte
Scheduler läuft bis zum ersten zustellbaren PIC-Interrupt; danach erhält
die native CPU Gelegenheit, diesen tatsächlich zu bearbeiten, bevor weitere
überfällige Flanken entstehen. Wirklich verbrauchte native Ausführungs- und
Instruktionshilfezeit wird weiter berechnet. IF, Interruptschatten, bereits
eingetragene native Ereignisse und PIC-Masken bleiben wirksam. Ein mit IF=0
haltender Gast wird dadurch nicht künstlich aufgeweckt. Tatsächlich mit
CLI gesperrte Ausführung behält das normale Zusammenfallen von Flanken.
Es entsteht weder eine zweite IRQ-Warteschlange noch eine nachträgliche
Windows-Tickkorrektur. Explizite Pause und Reset verwerfen nur die abgeleitete
Host-Nachholbuchhaltung; gespeicherte Geräte- und CPU-Felder bleiben vollständig.

Zehn erste native Kontrollen verwenden die wirklichen Windows-PIT-Perioden
1024/1193, beide P54C-/P55C-Profile, echte IRQ0-Handler mit EOI/IRET und eine
begrenzte 15-ms-Hostpause bei tatsächlichem HLT. C3 besteht alle Kontrollen.
Derselbe endgültige Prüfer scheitert auf C2 mit 62 PIT-Impulsen, aber nur
37 IRQ0-ACKs. Getrennte IF-/PIC-Maskierungen liefern keine erfundenen ACKs;
der ausgeführte CLI-Fall verliert weiterhin erwartungsgemäß mehrere Flanken.

Ein wirklicher C3-Win95-Kaltstart zeigt jedoch weiterhin einen Drehabschnitt
mit 98,635 Prozent und 322 zusammengefallenen Flanken. Die anderen beiden
Drehabschnitte liegen bei ungefähr 100 Prozent. Die zusätzliche Beobachtung
belegt auch verspätete Rückkehr aus `WHvRunVirtualProcessor`, bevor die
Owner-Buchhaltung den neuen HLT-Zustand gesetzt hat. Ein bloßes Wiederholen
der bisher guten Abschnitte wäre kein Nachweis für die verbliebene Grenze.

## Ausführungszeit und Host-Rückkehr unterscheiden

C5 verwendet das vom installierten Windows bereitgestellte, 16 Byte große
VP-Laufzeitset von
[`WHvGetVirtualProcessorCounters`](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvgetvirtualprocessorcounters).
Die tatsächliche API-Signatur einschließlich `CounterSet` ist auch mit dem
installierten Windows-SDK und dem
[Microsoft-Header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WinHvPlatform.h)
abgeglichen. Zwei aufeinanderfolgende Werte begrenzen tatsächlich zugeordnete
native CPU-Laufzeit; reine verspätete Hostfortsetzung wird nicht als ausgeführter
Gastcode berechnet. Der letzte Wert wird nur zwischen gestoppten Owneraufrufen
weiterverwendet; Reset und CPU-Restaurierung beginnen mit einer neuen Abfrage.
Er ist abgeleitete Hostbuchhaltung und kein hinzugefügtes Snapshotfeld.

Der Owner stellt überfällige Ereignisse chronologisch zu und lässt die CPU
zwischen zustellbaren Interrupts laufen. Echte native Ausführung sowie eine
unteilbare Instruktionshilfe verbrauchen weiter Zeit. Ein wirklich blockierter
MMIO-Buszugriff oder gehaltenes RESET kann hingegen keinen Interrupt bedienen:
Hier laufen die ursprünglichen Gerätefristen uneingeschränkt weiter, bis die
bestehende Transaktion wieder fortgesetzt werden darf. Die bisherigen ganzen
SST-1-Warte-/Import-/Zukunftskontrollen bestehen weiterhin.

Bei fehlenden oder unbrauchbaren optionalen Laufzeitzählern verwendet der Owner
konservativ das bisherige gemessene Hostintervall für native Ausführung. Eine
gleich gute Kompensation verspäteter nativer Rückkehr wird für solche älteren
Hosts nicht zugesagt. In den neuen echten Gastnachweisen wird die tatsächlich
aktive Zählerunterstützung separat geprüft.

Der endgültige native Prüfer ergänzt nun auch eine 15-ms-Verzögerung direkt
nach wirklicher HLT-Rückkehr und eine lange, vollständig nativ ausgeführte
CLI-Schleife. Damit werden tatsächliche gesperrte CPU-Zeit und bloßes Hostwarten
unabhängig unterschieden. Es bleiben die originalen PIT-Perioden, PIC-Latches,
CPU-Profile, Gerätereihenfolgen, RAM- und Registerwirkungen erhalten.

## Tatsächliche C5-Windows-Folgeprüfung

Zwei neue eigene Kaltstarts verwenden unveränderte Spielprogramme und
Konfigurationen aus den erlaubten eigenen Referenzkopien:
Win95/Unreal205/P54C100/64MiB sowie Win98/Unreal226/P55C233/128MiB, jeweils
SB16 und S3, MiniGlide mit 512×384. Nach Leerlauf und fester Vortex2-Ansicht
folgen pro Gast drei ungefähr 20-sekündige Kameradrehungen.

| Gast / C5-Fenster | Windows-QPC / Hostzeit | Echte native Pufferwechsel/s |
| --- | ---: | ---: |
| Win95, feste Ansicht | 100,000 % | 27,28 |
| Win95, Drehung 1 / 2 / 3 | 100,000 / 99,970 / 100,000 % | 51,94 / 51,42 / 51,45 |
| Win98, feste Ansicht | 100,000 % | 48,69 |
| Win98, Drehung 1 / 2 / 3 | 100,000 / 100,000 / 100,000 % | 57,47 / 57,41 / 57,37 |

Zwei wirkliche Gast-`IN E9`-Instruktionen begrenzen jede originale
QPC-/GetTickCount-Abfrage. Der schreibfreie Beobachter verknüpft beide Enden
mit tatsächlichen Host- und Schedulerzeitpunkten; die spätere IPC-Abfrage
bestimmt den Messpunkt nicht. Die gepaarten Werte begrenzen Win95s schwächsten
Drehabschnitt auf 99,97039–99,97060 Prozent. Dort fehlen etwa 6 ms in
20,25 Hostsekunden und sieben PIT-Flanken fallen zusammen. Die übrigen fünf
Drehabschnitte haben an den geprüften Grenzen keine zusammengefallenen
IRQ0-Flanken. Kleine Werte oberhalb 100 Prozent und die gröbere Tick-Auflösung
werden mit vollständigen Zeitklammern und ungerundeten Rohdaten aufbewahrt.

Alle Lastabschnitte übertragen echte Gast-SB16-Samples; die Hostausgabe ist
deaktiviert. Tatsächliche Zählerunterstützung ist an beiden Fenstergrenzen
geprüft. Native Rückgaben werden am wirklichen Ende des Rendereraufrufs
atomar gezählt. Die Werte sind keine isolierte FPS-Beschleunigungsquote, kein
physischer Monitor-/Audio-Nachweis und keine direkte Messung von Unreals
interner Simulationszeit. Spielversionen und Grafikpresets unterscheiden sich.

Beide Spiele schließen natürlich, beide Windows-Gäste erreichen den regulären
sicheren Shutdown, dessen ganze VGA-Bildbytes gegen die vorhandenen Sollbilder
geprüft sind. Beide eigenen Owner enden mit Exit 0. Live-MiniGlide-Snapshots
werden in beiden Gästen vor Erfassung beziehungsweise Zweiganlage ausdrücklich
abgewiesen; die aktive Echtzeituhr bleibt dabei erhalten.

Die neuen Teststarter liegen ausschließlich in den privaten
`fixed95.dosebranch`-/`fixed98.dosebranch`-Sicherheitskopien. Sie binden den
eingefrorenen lokalen `candidate-5/Release` und die installierte Gast-DLL4.
Normale Starter, Originale und Benutzerinstallationen behalten ihren Stand.

## Prüfung und gegenwärtige Grenzen

Die beiden ersten privaten Swap-Prototypen erreichen unter Kameradrehung
99,996 Prozent beziehungsweise 99,990 Prozent Windows-QPC-/Hostzeit und
anschließend regulären Anwendungsschluss und Windows-Shutdown.
Die vollständige längere C1-Serie wird ausdrücklich mitgeführt: Win95
98,543 / 99,937 / 99,975 Prozent, Win98 100,000 / 99,985 / 98,714 Prozent
in je drei Drehabschnitten. Auch einzelne Desktop-/Ladeabschnitte haben
Restlücken. Die guten Einzelabschnitte ersetzen keine allgemeine
1:1-Garantie unter jeder Hostlast.

Der vollständige C2 besteht 18.243 gemeinsame Regressionen, 78 native Gruppen,
darunter 54 neue Swap-/Lebensdauer-/IRQ-Kontrollen und die zusätzliche
Texturallokationskontrolle, sowie drei Snapshot-Audits. Die echte kompilierte
Gast-DLL besitzt getrennte aktuelle-/ältere-Host-Kontrollen. Die
C2-Windows-Folgeprüfung mit gekoppelten QPC-/Tickabfragen und zusätzlicher
schreibfreier Hostlückenbeobachtung bleibt als Zwischenstand erhalten:
Win95 99,983 / 98,505 / 99,983 Prozent, Win98 98,695 / 99,995 / 99,941 Prozent.
Diese Restlücken sind kein abgeschlossener C2-1:1-Nachweis.

C5 besteht mit 0 Buildwarnungen/Fehlern, 18.243/18.243 gemeinsamen Regressionen,
79 nativen Gruppen einschließlich 54 Swap-/Lebensdauer-/IRQ-Kontrollen,
14 tatsächlicher Timer-/Maskierungs-/nativer CLI-Kontrollen und begrenzter
Texturallokationsprüfung sowie drei Snapshot-Audits. Der endgültige Prüfer
scheitert auf dem echten alten C3 mit kontrolliertem Exit 1 und 69 PIT-Impulsen
bei nur 51 IRQ0-ACKs. Die kompilierte Gast-DLL besteht 24 stdcall-/Portkontrollen
mit 2076 vollständig verglichenen Dreiecksrecords: neuer Gast/neuer Host,
neuer Gast/alter C9-Host und alter Gast/neuer Host, jeweils skalar und gebündelt.
Die Quellprüfung besteht für 1026 Code-/Projektdateien (540 C#), Zustand
weiterhin 49/992/44. C-/Headerlizenzen und generierte ABI bleiben separat geprüft.

Eine zusätzliche frische eigene 8-MiB-DOS-/HIMEM-Kopie bootet den originalen
erweiterten Speichertest, startet originales Doom II im Protected Mode ohne
Sound, zeigt tatsächlichen Bildfortschritt, beendet das Spiel regulär und
besteht neue KBC-ECHOs davor und danach. Der Owner endet mit Exit 0.
Eine erste feste Zehn-Sekunden-Initialisierungsgrenze war zu kurz und bleibt
ausdrücklich ein Vorlauf; die endgültige Kontrolle wartet begrenzt auf die
ursprüngliche R_Init-Fertigstellung. Daraus folgt keine neue Doom-Bildraten-,
Sound- oder vollständige Snapshot-Abnahme.

Aktive externe Rendererzustände und offene Rückmeldungen sind keine
vollständigen VM-Speicherstände. Sowohl die In-Memory-Erfassung als auch
SnapshotStore weisen sie jetzt ausdrücklich vor dem Speichern zurück.
Nach wie vor gibt es keine Migration alter nativer Speicherstände auf eine
andere WHPX-DLL. Softwareformat und Zustandsgraph behalten 49 Typen,
992 geprüfte Mitglieder und 44 Operationen. Die fünf VM-Module sind zwischen
C1 bis C5 bytegleich; die WHPX-DLL ändert sich. Gegenüber dem früheren C9
besitzen auch neu gebaute unveränderte VM-Quellen andere tatsächliche
Buildbytes; alte Builds und ihre Sicherungen bleiben erhalten.

125 eigene endgültige Produktdateien sind eingefroren; die genau 116 bekannten
C9-Produktdateien bleiben vollständig bytegleich. Die 14 versionierten normalen
Starter behalten ihren HEAD-Text. Es gibt exakt 19 benannte aktuelle Quell-/
Dokumentprüfungen mit fünf neuen Pfaden, keine breite Original-/Schutzmedienrunde.

Private Arbeit: `artifacts/windows-timers-20261103-93fa0553`.
Originalmedien, persönliche Spiel-COWs und bestehende Benutzer-PCs bleiben
außerhalb dieser Prüfung. Hostaudio und Netzwerk sind ausgeschaltet;
Gast-SB16-DMA und ausschließlich virtuelle Gasteingaben bleiben davon getrennt.
Es werden keine Audio-/Videoaufnahmen erstellt. Der Befund ist keine
ursprüngliche RTM-, vollständige S3-/SST-1-, allgemeine Spiele- oder
M4–M8-Gesamtabnahme.
