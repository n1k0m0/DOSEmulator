# MMX und Voodoo Graphics: eigener Entwicklungsumfang

Stand: 1. November 2026. C56 ergänzt den
[originalen Windows-98-/Glide-Treiberpfad](VOODOO-ORIGINAL-DRIVERS.md).
Er erkennt den echten ICS5342, führt den originalen SDK-Test und die
Motorhead-Glide-Demo aus. Das ursprüngliche Standardlogo und ausreichende
Spielebildrate bleiben offen; die dokumentierte Treiberoption
`FX_GLIDE_NO_SPLASH=1` ist erforderlich. Der getrennte C55-Demostarter
bleibt auf seinem bisherigen Stand.

Der folgende Abschnitt dokumentiert den ursprünglichen C55-Referenzumfang;
aktuelle Ergänzungen, Zeitregel und Grenzen stehen im verlinkten C56-Nachweis.
C55 ergänzt einen separat startbaren Hardwarepfad.
Die bestehenden C54-Starter und Benutzerplatten werden dadurch nicht umgestellt.
M6 und M7 aus dem [Gesamtplan](../DOSEmulator-Konzept.md) bleiben offen.

## Start und Einstellungen

`run-voodoo-demo.cmd` startet eine eigene schreibgeschützte Testdiskette mit
MMX und Voodoo Graphics über WHPX. Sie benötigt weder Windows noch ein Spiel
oder eine Benutzerplatte. Die getrennte Laufzeitwahl liegt unter
`artifacts/mmx-voodoo-demo/runtime.json`. Der Starter prüft die sechs dort
benannten Ausführungsmodule in beiden Frontends und den Diskettenhash.
Fehlt die Auswahl, erstellt er beim ersten normalen Start einen eigenen Build.

Alternativ: `scripts/start-mmx-voodoo-demo.ps1 -MHz 166`, `-MHz 200` oder
`-MHz 233`; `-Engine software` wählt den Interpreter. `-Inspect` prüft die
vorbereitete Auswahl und erstellt die eigene PC-Definition, ohne ein Fenster
zu öffnen. Der Hardwaredialog bietet die drei MMX-Takte und einen englischen
Eintrag `3D ACCELERATOR`. Die zusätzliche Voodoo lässt die primäre S3 bestehen.
Die Demo beendet ihre CPU-Arbeit nach dem Zeichnen absichtlich mit HLT.

## MMX

Der Softwarekern implementiert die 57 ursprünglichen MMX-Mnemonics einschließlich
MOVD/MOVQ, Pack/Unpack, Sättigung, Multiplikation, Schieben und EMMS.
MM0–MM7 überlagern die acht **physischen** rohen x87-Register; es gibt keinen
zweiten, auseinanderlaufenden MMX-Speicherstand. MMX schreibt den oberen
Sign-/Exponententeil als FFFFh, setzt TOP auf null und markiert nach einem
erfolgreichen Nicht-EMMS-Befehl alle Register belegt. EMMS leert die Tags und
bewahrt die Registerbytes. FSAVE/FRSTOR verwenden weiterhin die rohe x87-Ablage.

Das eigene P55C-Profil meldet CPUID 543h und MMX-Bit 23. Es verwendet das
vorhandene eigene 430FX-/PIIX-/S3-Board mit 166, 200 oder 233 MHz und
16/32/64/128 MiB RAM. Das ist **kein implementiertes 430TX-/PIIX4-Board** und
kein vollständiges P55C-Stepping- oder Busmodell. SSE, 3DNow und spätere
MMX-Erweiterungen werden dadurch nicht angeboten. Die vorhandene eigene
Pentium-LAR-Politik für undefinierte Ergebnisbits gilt ausdrücklich auch hier.

EM/TS, ausstehende x87-Ausnahmen, LOCK, Segmentgrenzen, vollständige
Qword-Pagingvorprüfung und bestehende Alignment-/Fault-Verträge bleiben
wirksam. Der Interpreter behandelt 66/F2/F3 vor ursprünglichem MMX gemäß dem
historischen Intel-Vertrag als ignorierte Präfixe; Adress- und Segmentpräfixe
wirken. Modernes natives WHPX kann solche Bytefolgen als spätere SSE-Befehle
interpretieren: Für diesen historischen Präfixsonderfall gibt es keine native
P55C-Gleichheitszusage. Die tatsächlichen nativen ungepräfixten MMX-Vektoren
werden getrennt gegen den Softwarekern verglichen.

## Voodoo Graphics / SST-1

Die eigene Zusatzkarte besitzt PCI 121A:0001, Revision 2, BDF 00:06.0 und einen
16-MiB-BAR. Sie enthält 2 MiB FBI- und 2 MiB TMU-DRAM. Der originale SST-1
Class-Code null wird beibehalten; ein modernes VGA-Gerät wird nicht vorgetäuscht.
Die neue eigene BIOS-Variante weist den BAR zu und liefert echte PCI-BIOS-Suche
neben den bisherigen Geräten, mit oder ohne RTL8029. Die elf vorherigen
ROM-Nutzbytestrome bleiben unverändert.

Der Bus erhält vollständige WORD-/DWORD-Transfers, wenn keine RAM-, feste
Mapping-, A20- oder Decodergrenze berührt wird. Feste Mappings behalten ihre
Priorität, ohne einen dynamischen Decoder vorab abzufragen. SST-1-Register und
Texturuploads akzeptieren DWORDs, der LFB WORDs und DWORDs. Nicht unterstützte
Bytezugriffe liefern FFh beziehungsweise bleiben wirkungslos. Es gibt keinen
Host-Glide-Wrapper und keinen Zugriff auf einen Host-Grafikendpunkt.

Implementiert sind der Register-/Chipselect-Pfad, beschränkte geordnete
Befehlswarteschlange, echte FBI-/TMU-Speicherwirkungen, Fast Fill,
12.4-Dreieckssetup mit einer eigenen Pixelzentrum-/Top-left-Regel,
Gouraud-Farben, Z-Test, einfache Alpha-/Chroma-/Stipple-/Blend-/Combinerpfade,
RGB565-Ausgabe, Gamma, Buffer-Swap und Rückkehr zur S3-Durchleitung.
Der TMU-Pfad unterstützt eine quadratische RGB565-Textur mit Point Sampling,
festem ganzzahligen Mip-Level 0–8 und begrenzter Perspektivkorrektur.
Spätere LFB-/Texturuploads werden bei aktiver Arbeit hinter früheren
Zeichenbefehlen eingeordnet. So kann ein späterer Upload deren Daten nicht
rückwirkend verändern.

Die Befehlsfristen sind eine ausdrücklich eigene Entwicklungszeitregel:
1.000 ns plus 20 ns je Bildpixel, Swap an einem festen nominalen
60-Hz-Raster. Dies ist keine SST-1-Zyklenabnahme. Der Owner führt alle
Gerätewirkungen aus; es gibt keinen zusätzlichen Grafikgeräte-, Audio- oder
Plattenworker. Die vorhandenen Bild-Reader-Leases reichen weiterhin über den
GPU-Upload. Die Karte meldet den historischen INTA-Pin, erzeugt aber keine
implementierten SST-1-Interrupts.

Offen sind unter anderem ursprüngliche Glide-/Windows-3D-Treiber und Spiele,
deren DAC-/PLL-Erkennungsfolgen, vollständige Speicher-FIFO-/Stall- und
Bussemantik, alle Texturformate, variable LOD-/Filterpfade, Fog, W-Buffer,
Triple Buffering, weitere Raster-/Dither-/Analogpräzision und native
Host-GL-Abnahme. Nicht unterstützte ausgewählte Renderpfade stoppen mit einer
konkreten Diagnose; ein erfolgreiches Spiel wird daraus nicht behauptet.
LFB-Lesen zeigt bereits abgeschlossene Speicherung; vollständige Stallpräzision
während aktiver Arbeit ist nicht abgenommen. Voodoo2, Banshee und Voodoo3 sind
weiterhin eigene spätere Geräte.

## Nachweise und Fortsetzung

Das eigene 704-Byte-Gastprogramm führt CPUID, MMX-Sättigung und TMU-Uploads aus,
zeichnet einen Farbverlauf sowie eine blau-grüne Schachbretttextur und tauscht
den Buffer. Es läuft im Softwarekern und über echte native PCI-/MMIO-Exits.
Alle 640 × 480 Bildpunkte werden unabhängig aus den Setupkonstanten,
Halbebenen, Texturkoordinaten und RGB565-/Gammawerten berechnet, ohne
Pixelausschlüsse. Die echten ROM-/FDC-/Diskettenstarts bei 166 und 233 MHz
erreichen POST 1 und dasselbe Sollbild.

MMX-Fault-/Mode-/FSAVE-Grenzen, unabhängige BigInteger-Arithmetik und 1.936
tatsächliche native MMX-Vektoren ergänzen die Gerätefälle. Ein voller
Software-Snapshot und ein tatsächlicher nativer Snapshot mit aktiver
Voodoo-Warteschlange und belegten MMX-Registern setzen fort. Native Vergleiche
verwenden nacheinander getrennte Owner und selbstständig gespeicherte ganze
CPU-/RAM-/Geräte-/Schedulerwerte; keine Felder werden ausgeblendet.
Register, acht DRAM-Bänke, Gamma, Frontbuffer und wartende Befehle sind
autoritativer Zustand. Die bisherigen Snapshotgrößengrenzen bleiben erhalten.

Format 1 bleibt an die tatsächlichen fünf Softwaremodule und das native Format
an die tatsächlichen nativen Module gebunden. Alte Sicherungen werden nicht
migriert. Die neue Plattform und die neue Karte sind kein allgemeiner
100-Prozent-Echtzeitnachweis. Zusätzliche Last-, Originaltreiber- und
Spieleabnahmen sind vor einer allgemeinen Umstellung erforderlich.

Private Prüfläufe und endgültige Buildangaben:
`artifacts/tests/mmx-voodoo-release-path.txt`.

Zwei neue eigene Windows-Kaltstarts verwenden MMX/233 MHz, 64 MiB unter
Windows 95 und 128 MiB unter Windows 98, ohne zusätzliche Voodoo im
Windows-Profil. Beide erreichen den vorhandenen S3-Desktop, spielen das
eigene RLE8-/PCM-AVI über den echten Media Player und SB16 ab und fahren
regulär herunter. In den begrenzten 5-/8-Sekunden-Proben werden 101/158
verschiedene Quellbilder beobachtet, mit 55141/88200 tatsächlichen
11025-Hz-DSP-Samples und 99,946/99,930 Prozent Zeitgleichlauf. Das ist keine
Abnahme bestimmter MMX-Codec-Pfade, allgemeiner 3D-Last oder eines
Voodoo-Windows-Treibers. Die verwendeten eigenen Plattenzweige und
Shutdown-Siegel bleiben getrennt von allen Benutzerplatten.

## Primärquellen

- [Intel MMX Technology Programmer’s Reference Manual, März 1996](https://bitsavers.org/components/intel/MMX/243007-002_Intel_Architecture_MMX_Technology_Programmers_Reference_Manual_Mar1996.pdf): ursprünglicher Befehlssatz, Registeralias und Ausnahmen.
- [Originale SST-1 Hardware Specification](https://bitsavers.org/components/3dfx/Voodoo1_SST-1_Spec_r1.61_199912.pdf): PCI-/Speicherkarte, Transferbreiten, Register und Pipelinefelder. Datei- und gedruckte Revisionsbezeichnungen unterscheiden sich; die private Quellenkopie enthält den originalen gedruckten Stand.
