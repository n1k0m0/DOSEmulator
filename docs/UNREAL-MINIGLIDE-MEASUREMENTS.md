# Unreal mit MiniGlide: Messung vom 3. November 2026

Der unveränderte, eingefrorene MiniGlide-Kandidat 9 mit Gast-DLL und Launcher 3
wurde unter Windows 95 und Windows 98 auf neuen eigenen Sicherheitskopien
gemessen. Der gemeinsame Starterstand und alle bisherigen Installationen
bleiben erhalten. Es gibt keinen neuen Emulatorbuild und keine neue Vollsuite;
die bereits bestandenen 18.243 Regressionen und 76 nativen Gruppen behalten
ihren ursprünglichen Buildbezug.

Die FPS sind tatsächliche Glide-Pufferwechsel je **Hostsekunde**. Zusätzlich
wird das ganze im Frontend eingelesene Spielbild byteweise mit seinem Vorgänger
verglichen. So lassen sich wiederholte Bilder von geänderten Bildquellen
unterscheiden. Das Frontend zeichnet in allen sechs Lastfällen rund 59,7-mal/s.
Diese Werte sind keine physische Monitor-/Scanout-Abnahme.

## Konfigurationen und Ergebnisse

Windows 95: Unreal 205, P54C 100 MHz, 64 MiB, bestehendes Grafikpreset mit
niedrigen Texturdetails. Windows 98 SE: Unreal 226, P55C/MMX 233 MHz, 128 MiB,
bestehendes Preset mit höheren Texturdetails, Detailtexturen, Coronas und
glänzenden Oberflächen. Beide verwenden MiniGlide bei 512×384. Damit sind
die Ergebnisse Messungen der vorhandenen Konfigurationen; ein isolierter
Betriebssystemvergleich oder ein neuer Vorher-/Nachher-Codegewinn wird daraus
nicht berechnet.

| Lastfall | Win95: Pufferwechsel/s | Win98: Pufferwechsel/s | Win95: geänderte Bildquellen/s | Win98: geänderte Bildquellen/s |
| --- | ---: | ---: | ---: | ---: |
| Kameraflug, Intro | 51,77 | 48,13 | 39,00 | 42,60 |
| Vortex2, feste Blickrichtung | 32,72 | 58,58 | 32,49 | 57,57 |
| Vortex2, fortlaufende Kameradrehung | 54,58 | 59,29 | 42,85 | 49,56 |

Je Gast und Lastfall: drei aufeinanderfolgende Abschnitte von rund 20 Sekunden,
insgesamt 18 Abschnitte und rund 360 Sekunden Messzeit. Je Betriebssystem ein
tatsächlicher Kaltstart für die Hauptmessung, 20 Sekunden JIT-/Szenenaufwärmung
vor dem Intro und zehn Sekunden nach dem echten Levelstart. Die drei Abschnitte
sind keine unabhängigen Neustarts derselben eingefrorenen Kamera. Die Drehung
erfolgt durch eine gehaltene echte virtuelle Gast-Pfeiltaste. Bilder belegen
den tatsächlichen Level und veränderte Blickrichtungen.

Die längsten Abstände zwischen geänderten Bildquellen liegen in der Win95-
Startszene bei 149,4 ms, sonst zwischen 66,6 und 83,4 ms. Die 99-Prozent-
Quantile liegen je Szene bei etwa 33,4–51,4 ms. Durchschnittliche FPS allein
belegen daher keine gleichmäßige 60-FPS-Ausgabe.

Der Kameraflug und beide Spielszenen behalten aktive ursprüngliche SB16-
Gast-DMA. Die tatsächlichen Samplezähler laufen mit ungefähr 44.100 Samples
je virtueller Sekunde weiter. Für die automatisierte Messung sind Hostaudio
und Netzwerkendpunkte deaktiviert; beide Gäste werden seriell ausgeführt.
Im Win98-Test ist nur in der neuen Kopie die Netzwerkkarte entfernt. Es gibt
keine Audio-/Videoaufnahme und keine Windows-Hosteingaben oder Fokusmanipulation.

## Virtuelle Zeit und Windows-Timer

Das Verhältnis von Schedulerzeit zu Hostzeit beträgt über die Hauptabschnitte
99,95–100,01 Prozent. Der zusätzliche normale Windows-QPC-/Tick-Beobachter
unter Win98 zeigte dagegen bereits in den bewegten Szenen geringere Zuwächse.
Da ein Hintergrundprozess verspätet ausgeführt werden kann, wurde dieser
Befund separat mit an die tatsächliche Gastausführung gekoppelten Zeitpunkten
geprüft.

Der kleine eigene Win32-Helfer liest `QueryPerformanceCounter` und
`GetTickCount`. Zwei wirkliche `IN E9`-Befehle umschließen jeden solchen
Lesevorgang. Ein passiver Portbeobachter bindet diese beiden Grenzen an den
tatsächlichen virtuellen und Hostzeitpunkt; der vorhandene E9-Lesepfad liefert
sein unverändertes Open-Bus-Byte und verändert keinen Gerätezustand. Der
Beobachter liest ausschließlich den eigenen 88-Byte-Proofdatensatz aus dem
wirklichen nativen Gast-RAM. Diese eigene Kontrolle verwendet keine weitere
CPU-Instruktionsbeobachtung und keine geänderte Produkt-DLL.

| Separate Uhrenkontrolle | Reale/virtuelle Sekunden | Windows-QPC-Sekunden | Anteil Windows-Zähler |
| --- | ---: | ---: | ---: |
| Win95, Desktop | 10,4632 | 10,4494 | 99,87 % |
| Win95, feste Spielansicht | 20,2117 | 20,1765 | 99,83 % |
| Win95, Kameradrehung | 20,3498 | 11,4339 | **56,19 %** |
| Win98, Desktop | 10,2968 | 10,2838 | 99,87 % |
| Win98, feste Spielansicht | 20,2629 | 20,0720 | 99,06 % |
| Win98, Kameradrehung | 20,2239 | 16,5525 | **81,85 %** |

Die beiden Grenzen der entscheidenden Lesevorgänge liegen jeweils nur etwa
16 Mikrosekunden auseinander. `GetTickCount` bestätigt die Abstände unabhängig
von der QPC-Einheit. Die mehrsekündige Differenz beim Drehen lässt sich dadurch
nicht mit verspätetem Ablesen eines Hintergrunddatensatzes erklären.

Dies ist ein nachgewiesener Rückstand der Windows-Timer unter dieser Last.
Die interne Simulationszeit von Unreal wurde dabei nicht direkt ausgelesen;
die Prozentwerte sind daher keine Messung seines Spieltempos. Kalender-/RTC-
Synchronisierung und Emulations-SPEED-Anzeige beantworten jeweils andere
Zeitfragen. Der allgemeine Nachweis echter 1:1-Zeit für Windows-Anwendungen
unter Last bleibt offen. Nächster konkreter Prüfpunkt ist die PIT-/IRQ0-
Fortschreibung und Zustellung über synchrone native Glide-Arbeit hinweg;
eine konkrete Ursache oder Reparatur wird aus dieser Messung noch nicht
abgeleitet. Takte, Gerätefristen und Zeitsteuerung wurden nicht verändert.

## Verfahren, Grenzen und Nachweise

Der private Messprüfer leitet jeden erfassten Swapaufruf mit unverändertem
Argument und Rückgabewert an dieselbe Hostbibliothek weiter. Ein eigener
tatsächlicher unmanaged Kontrollaufruf prüft Argumente, Rückgaben, genaue
Zähler, ganze Bildänderungen und die Wiederherstellung der Funktionsadresse.
Jeder Hauptabschnitt vergleicht seine aufgezeichneten Swaps gegen den
unveränderten tatsächlichen Produkt-Aufrufzähler. Zeitpunkte verbleiben während
der Messung in begrenzten RAM-Puffern. Bilder und Ergebnisdateien entstehen
außerhalb der jeweiligen Messintervalle. Die Auswertung bildet Quantile aus
den tatsächlichen aufeinanderfolgenden Zeitabständen innerhalb jedes Intervalls;
zwischen getrennten Intervallen werden keine künstlichen Abstände eingefügt.

Die Uhrenkontrollen sind zwei zusätzliche eigene Kaltstarts und werden nicht
in die FPS-Kohorte eingerechnet. Alle vier erfolgreichen Gäste erreichen
Anwendungsschluss und den originalen Windows-Bildschirm zum sicheren
Ausschalten. Ein erster zusätzlicher Win95-Start über den Mess-Batch-/Helferweg
erreichte keine 3D-Ausgabe und bleibt ein ausgeschlossener Vorlauf. Die
Hauptmessung erfolgte danach über den zuvor geprüften direkten Win32-Start.
Der private Controller wurde außerdem auf den tatsächlichen Unreal-
Schließaufruf, deutsche Y/Z-Scancodes und kurzzeitige Dateifreigabekonflikte
abgestimmt; die gültigen Introabschnitte wurden im selben laufenden Gast
erhalten. Keine dieser Prüferkorrekturen ist eine Emulatorreparatur.

Die von Motorhead verwendete Zeiger-Dreiecksbündelung begründet keinen
entsprechenden Gewinn für Unreal: hier werden bereits geordnete Vertexlisten
verwendet. Auch LFB-Lock-/Read-/Unlock-Aufrufe treten in diesen sechs
Hauptabschnitten nicht auf. Ihr gepoolter Transferpuffer erklärt diese FPS
somit nicht. Frühere Motorhead- und ursprüngliche SST-1-Ergebnisse behalten
ihren eigenen Build- und Messbezug.

Private Rohdaten, ganze Bilder, Zähler, gekoppelte Uhren-Lesegrenzen und
Erhaltungsnachweise sind über
`artifacts/tests/unreal-measurement-release-path.txt` gebunden. Alte Starter,
Installationen und persönliche Spiel-COWs bleiben unverändert. Die Automation
bleibt pausiert; dieser Messauftrag umfasst keinen Commit oder Push.
