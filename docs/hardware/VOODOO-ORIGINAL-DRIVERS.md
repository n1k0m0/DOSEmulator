# Voodoo Graphics: Originaltreiber und Glide

Stand: 1. November 2026. Lokaler Entwicklungskandidat C57; die gemeinsame
C54-Laufzeit, alle 13 bisherigen Starter, der getrennte C55-Demostarter und
vorhandene Benutzerinstallationen bleiben unverändert. M7 bleibt offen.

## Tatsächlicher Gastpfad

Eine eigene Windows-98-Testkopie verwendet das P55C-Entwicklungsprofil mit
233 MHz, 128 MiB RAM, primärer S3 und zusätzlicher SST-1 mit 2 MiB FBI und
2 MiB TMU. Das Originalpaket Voodoo Graphics 3.01 installiert unter anderem
Glide 2.48.00.0455 und seinen ursprünglichen VxD. Die 13 verwendeten
Treiberdateien werden nach dem Gastlauf bytegenau aus dem versiegelten
eigenen FAT-/COW-Stand gegen die Originaldateien geprüft. Windows, Treiber,
SDK und Spiel werden nicht öffentlich mitgeliefert.

Das originale Glide-SDK-Testprogramm TEST00 initialisiert die Karte, erkennt
FBI-Revision 2, TREX-Revision 1 und den ICS5342, öffnet den Videomodus und
schließt Glide wieder. Die unveränderte Motorhead-Glide-Demo wird durch ihren
Originalinstaller im Gast installiert. Sie erreicht Titel, Menü und die
automatische Rennszene mit Texturen, Fahrzeugen, Himmel, Nebel und Sound
über den vorhandenen SB16. Eigene Starthelfer verwenden gewöhnliche
CreateProcess-/Wait-/GetExitCode-/CloseHandle-Aufrufe; sie ersetzen keine
Glide- oder Treiberfunktion.

**C57 führt das originale Standardlogo ohne Abschaltoption aus.** Die
[genaue Logo- und Performancedokumentation](VOODOO-PERFORMANCE.md) beschreibt
die korrigierte S/T-Chipauswahl und eigene begrenzte Festkomma-Überlaufregel.
Rohes IEEE-Payload und Nichtendlichkeitsmaske bleiben erhalten. Dies ist
keine physische NaN-/Inf-Präzisionsabnahme. Die folgenden C56-Messungen
behalten ihren historischen Bezug mit `FX_GLIDE_NO_SPLASH=1`.

## Ergänzte Gerätewirkungen

Der eigene Voodoo-ROM-Pfad liefert die tatsächliche PCI-Ressource samt
IRQ-11-/PIRQA-/ELCR-Vertrag. Die elf bisherigen ROM-Nutzbilder bleiben
bytegleich. Die emulierte Karte führt keine neuen SST-1-Interrupts ein.

Die originalen Erkennungsfolgen verwenden den Revision-2-VGA-Reset-Strap,
die TREX-Revisionsausgabe, echte Speichergrößenproben und den seriellen
ICS5342-DAC mit getrennten Video-/Grafik-PLLs. Diese Werte einschließlich
Palette und Befehlswarteschlange gehören zum gespeicherten Gerätezustand.
Die Rasterablage funktioniert auch vor dem Einschalten der Videoausgabe.

Der begrenzte TMU-Pfad unterstützt nun die Formate 0/2/3/4/5/8/10/11/12/13/14,
rechteckige Texturen, feste Mip-Level 0–8, Point/Bilinear, Perspektive sowie
die ursprüngliche 256-Farben-Texturpalette. Fog-Tabelle und Alpha-/Z-Auswahl,
W-Depth-Normalisierung und Dither-Quantisierung wirken im eigenen Rasterer.
Spätere Textur-, LFB- und Palettenzugriffe bleiben hinter älteren Befehlen
geordnet. Eine begrenzte Arbeitswarteschlange ersetzt keine vollständige
physische Speicher-FIFO-/Busabnahme.

Die eigene Entwicklungszeitregel verwendet 50 Grafikclocks Setup und einen
gedeckten Dreieckskandidaten pro Grafikclock, beim Clear zwei Pixel pro Clock.
Der Originaltreiber programmiert hier ungefähr 40,025 MHz Grafikclock.
Die Pixelraten orientieren sich an der SST-1-Dokumentation; Setup, Refresh,
DRAM-/Buskonflikte und Analogverhalten sind keine geprüften Siliziumzyklen.
Transferfristen und nominale 60-Hz-Swaps bleiben erhalten. Der Owner wartet
an bestehenden Gerätefristen; Gastzeit oder CPU-Takt werden nicht übersprungen.

WHPX ergänzt die tatsächlich benötigten geschützten x87-FST/FSTP-m32-/m64-
MMIO-Zugriffe mit roher Float80-Rundung, Status, Tags und Pop. Bei m64 bleiben
es zwei wirkliche DWORD-Gerätetransfers. Jede Code-/Zielseite wird aktuell
geprüft. Die Optimierung verwendet eine Seitentranslation nur innerhalb
desselben noch nicht abgeschlossenen Befehls erneut; Seitenwechsel, aktuelle
Codebytes, Rechte, A/D-Bits und zusammenhängende physische Zielbytes bleiben
wirksam. Fehlende SIB-/Displacementbytes an einer Code-Seitengrenze werden
aus aktuellem ausführbarem Gast-RAM gelesen. Es gibt keinen dauerhaften
nativen Übersetzungscache.

## Nachweise und Leistungsgrenzen

Aktuell bestehen 18238 gemeinsame Regressionen, 72 native Standardgruppen
und drei Snapshot-Audits auf C57. 2880 zusätzliche ganze native
CPU-/2-MiB-RAM-/Parameterbytevergleiche, 30 Instruktions-/Ereignis- und
Resetgrenzen sowie 19 zustandsreine Ablehnungen sichern die neue
Ausführungshilfe. 96 benachbarte FSTP-Kontrollen und 53248 genaue
Umrechnungsvergleiche werden separat gezählt. Die gemeinsame Suite enthält
nun 197 gezielte Voodoo-Gerätefälle.

Die abschließende Messung desselben C57-Builds an derselben originalen
Rennsicherung zählt 32 gegenüber 65 abgeschlossenen Pufferwechseln je rund
32 Hostsekunden, also 1,00 gegenüber 2,03/s. Alle acht Prozesse importieren
anfangs den vollständigen gleichen CPU-/128-MiB-/Gerätezustand. Der
Zeitgleichlauf liegt bei 99,994–100 Prozent. Unterschiedlicher nativer
Fortschritt ist keine deterministische Zukunftsgleichheit.

Historischer C56-Nachweis: 18167 gemeinsame Regressionen bestehen auf dem damaligen gemeinsamen
Modulsatz; die fünf VM-Module des finalen nativen Kandidaten sind dazu
bytegleich. 67 native Standardgruppen und drei getrennte Snapshot-Audits
bestehen. Darin enthalten sind 1664 vollständige native x87-Vergleiche mit
ganzen 2-MiB-RAM-Oracles, 37 vollständig zustandsreine Ablehnungen sowie
32 zusätzliche Pagingvergleiche. Vier weitere Ablehnungen erlauben allein
die ausdrücklich geprüften architektonischen Pagewalk-A/D-Bits.

96 unabhängige Dreieckskontrollen vergleichen jeden Pixel einer 96×96-Fläche
gegen die exakte Top-left-Abdeckung, einschließlich beider Windungen,
Subpixelpositionen und Clipping. Die vollständige gemeinsame Suite enthält
126 gezielte Voodoo-Gerätefälle. Diese ersetzen keine unabhängige
Pixelrekonstruktion des Originalspiels.

Acht serielle, gespiegelte Prozesse führen jeweils 20000 Aufwärm- und
100000 gemessene tatsächliche FST-MMIO-Befehle aus. Die lokale
Seitentranslationsoptimierung benötigt dabei 37,52 Prozent weniger Hostzeit;
ganzer CPU-, RAM- und Karten-Endzustand bleiben gleich. Das ist ein eigener
fester CPU-/Geräteversuch, kein allgemeiner Spiele- oder FPS-Gewinn.

Der erste originale Rennlauf hält in einer Achtsekundenprobe 99,934 Prozent
Zeitgleichlauf, zeigt aber nur ungefähr ein geändertes Bild pro Hostsekunde.
Der frisch gebootete finale native Kandidat erreicht in einer weiteren
Achtsekundenprobe 99,931 Prozent und sieben beobachtete Bildänderungen
(0,874 pro Hostsekunde), außerdem 352800 tatsächliche DSP-Samples und
384000 publizierte Stereo-Frames im RAM. Die Rennabschnitte sind verschieden;
daraus folgt weder ein nachgewiesener FPS-Gewinn noch eine gemessene
Regression gegenüber dem ersten Lauf.
**Zeitgleichlauf allein ist keine ausreichende Spieleperformance.** Weitere
Messungen und Optimierungen werden getrennt mit ihren tatsächlichen
Bildänderungen dokumentiert; ein allgemeines 100-Prozent-3D-Prädikat gibt es
nicht. Audio wird nur im RAM geprüft, ohne Hostendpunkt oder Aufnahme.

Ein wirklicher originaler Renn-Snapshot importiert alle 128 MiB RAM, rohe
native CPU und sämtliche Geräte-/Schedulerfelder exakt. Zwei getrennte
Produktimporte vergleichen sieben vollständige Gerätefortsetzungen,
Bilddaten, 2400 spätere Stereo-Frames je Seite und sämtliche Endmedien bei
angehaltener nativer CPU. Die Warteschlange ist an diesem Schnitt leer und
wird ausdrücklich nicht als aktiver SST-Befehl ausgegeben. Tatsächliche
native Spielfortsetzung ist eine getrennte Funktionsprüfung; wie in
[Snapshots](../SNAPSHOTS.md) besteht keine deterministische Wiederholung
laufender nativer Windows-CPU-Arbeit.

Der originale SDK-Prozess liefert Exitcode 0; Motorhead liefert beim normalen
Schließen Exitcode 1. Erfolgreiche Win32-Rückgaben und originale Glide-
Shutdownmeldungen werden getrennt davon geprüft. Windows erreicht anschließend
seinen regulären sicheren Ausschaltzustand.

## Weiter offen

Weitere originale Spiele und Win95-Treiber, variable LOD-
Auswahl, vollständiges NCC-/Filter-/Blend-/Framebufferverhalten, Triple
Buffering, genaue physische FIFO-/Stall-/Bus-/Analogwirkung und ausreichende
3D-Bildrate bleiben offen. Die Originaltreiberabnahme gilt für den benannten
Windows-98-/WHPX-Pfad; sie ist keine Softwarekern-, Host-GL-, native Bedien-
oder vollständige M7-Abnahme. Voodoo2/Banshee/Voodoo3 bleiben getrennte Geräte.

Private Builds, Originaldateihashes und die genauen Gastnachweise stehen
unter `artifacts/tests/voodoo-performance-release-path.txt`; der historische
C56-Stand bleibt unter `artifacts/tests/voodoo-glide-release-path.txt`.

## Quellen

- [Originales Voodoo-Graphics-3.01-Paket](https://3dfxarchive.com/downloads/voodoo1-30100.zip), [Glide-SDK 2.43](https://3dfxarchive.com/downloads/glide_sdk-243.zip) und [Motorhead-Glide-Demo](https://motorhead.cc/downloads/demos/motordemo_3dfx.exe): unveränderte private Gastsoftware.
- [SST-1 Hardware Specification](https://bitsavers.org/components/3dfx/Voodoo1_SST-1_Spec_r1.61_199912.pdf): Register, Datenformate und veröffentlichte Pixelraten; [ICS5342-Datenblatt](https://retronn.de/ftp/docs/pc_hardware/DACs/ICS5342.pdf): serielles DAC-/PLL-Protokoll.
- [WHvTranslateGva](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvtranslategva): getrennte Execute-/Write-Prüfungen und SetPageTableBits.
