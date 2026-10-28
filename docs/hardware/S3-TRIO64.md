# S3 Trio64 86C764: PCI-, VGA- und VRAM-Vertikale

Stand: 27. Oktober 2026. Die erste benannte PCI-/VGA-/VRAM-/8bpp-Vertikale ist mit11312/11312 Regressionen,164 neuen Fällen und0 Warnungen/Fehlern im gemeinsamen Starter integriert. Vollständige Trio64-/Windows-95-/M4-Abnahme bleibt offen. Privater Gesamtnachweis: `artifacts/tests/m4-s3-trio64-release-path.txt`.

## Primärvertrag und eigener Adapter

Grundlage ist ausschließlich [S3 DB014-B, März1995: Trio32/Trio64 Integrated Graphics Accelerators](https://www.bitsavers.org/components/s3/DB014-B_Trio32_Trio64_Graphics_Accelerators_Mar1995.pdf), insbesondere Abschnitte6.1,11.1,11.4,13.1 und Registerkapitel14–19. Trio64V+, ViRGE und deren neue MMIO-/Videoerweiterungen gehören nicht zu diesem Adapter. Die private Quellenkopie trägt SHA256 `7528EEFE26846DA697541CCAB575610DAD3EAA7AB7DE0AC7147D02BD2DDF4277`.

Die eigene Platine `dose-430fx-piix-s3-dev-v1` ergänzt das geprüfte Resetboard um `s3-trio64-86c764-pci-2m-development`, fest an00:03.0. Diese BDF ist die eigene IDSEL-Wahl. CPU100MHz/32MiB,82437FX A2 und ursprüngliche82371FB-A1-Funktionen bleiben fest gebunden. Ältere Profile erhalten diesen Adapter nicht nachträglich.

Der Teilumfang benennt86C764/Trio64, PCI5333:8811/Klasse030000 und CR2D=88h/CR2E=11h/CR30=E1h. CR2F/PCI-Revision00h ist eine feste benannte Teilmodellwahl; daraus wird keine unbelegte historische Steppinggenauigkeit abgeleitet. Die eigene Karte besitzt2MiB FPM-DRAM am64-Bit-Speicherpfad, aufgeteilt in vier gemeinsame Ebenen von jeweils512KiB. VGA, Bank- und lineare Zugriffe verwenden dieselben Bytes und Latches.

## Drei verschiedene Ressourcenmaße

BAR0 beschreibt nach19-4 eine8MiB-PCI-Speicherressource. Nur Bits31..23 sind schreibbar; eine Größenprobe liefertFF800000h. Diese neun Bits sind mit CR59 und CR5A.7 gemeinsam. CR5A.6..0 bleiben eigenständige Positionierungsbits. CR58 wählt innerhalb dieser Ressource ein größenbündiges64KiB-/1MiB-/2MiB-/4MiB-Fenster. Die tatsächlich bestückte Speichermenge bleibt davon unabhängig2MiB.

Die eigene Boardpolitik akzeptiert den hohen Decoder nur, wenn die ganze8MiB-PCI-Ressource außerhalb sämtlicher installierter RAM-Bytes und fester Geräte-/ROM-Abbildungen liegt. Das gilt auch für momentan durchDRB/HEN verborgenesRAM. Eine Kollision hält die gesamte hohe Ressource wirkungslos; das programmierte Register bleibt lesbar. Dies ist eine ausdrücklich eigene konfliktbegrenzte Zielpolitik, keine historische Bus-Arbitrierungszusage. Innerhalb eines akzeptierten BAR wird nur das aktivierte tatsächliche Fenster angesprochen. Unbestückte VRAM-Bytes liefernFFh und verwerfen Stores; mögliche elektrische DRAM-Aliase sind nicht nachgebildet.

Die originale64KiB-Legacyausnahme beiA0000h mit aktivierterBank und passenderLAW-Position läuft getrennt über die feste VGA-Apertur. Sie erhält keinen hohen8MiB-RAMgrant. CPU-A20 und Paging/Rechte wirken vor dem physischen Ziel; IDE-Busmaster erhält über seinen bestehenden geprüften Hostpfad ausschließlich DRAM, keinen erfundenen PCI→S3-Transfer.

## Aktivierung, Fortsetzung und Grenzen

Die eigene fünfte ROM-Variante programmiert BAR und PCI-Command durch echteOUTs vorPOST, führt die originale Setup/POS-Aufwecksequenz aus und schaltet die lineare Apertur für den normalenVGA-POST zunächst ab. Die vier bisherigen ROM-Bytestrome bleiben der Vergleichsanker. Dies ersetzt keine historische S3-Option-ROM- oder PCI-BIOS-Abnahme.

Geprüft sind echte VGA-/Lock-/Bank-/LFB-/8bpp-Gastwirkungen und vollständige CPU-/RAM-/VRAM-/Geräte-/Schedulerfortsetzung bei offener DAC-Komponente, Attributphase und Geräteaktivität. Neue VBE-/High-Color-/Beschleuniger-/Hardwarecursor-/DPMS-/Genlock-/Featureconnector-/Originaltreiberpräzision ist kein Bestandteil dieser ersten Vertikale. Programmierte Taktlatches und vorhandener ungefährer Raster sind keine PLL-/Monitor-/PCI-Zeitkalibrierung. Reale unimplementierte Steuerbits werden ausdrücklich so benannt.

Danach bleiben ATAPI, passende Firmware/Manager, ursprüngliche Windows95-RTM-Installation, mehrere Starts, Gerätebetrieb, regulärer Shutdown,133MHz-Matrix und M4–M8 offen.


## Firmware-Rückweg und Zustandsvertrag

Drei tatsächliche INT10-Aufrufe auf der ersten Diagnose-ROM reproduzierten den fehlenden Enhanced→Legacy-Rückweg. Ausschließlich die neue ROM erhält nun nach erfolgreicher Validierung von03h/12h/13h einen eigenen S3-Übergang. Er beendet ADV/Bank/LAW, Geometrieüberläufe, Palettensperren, Format- und DAC-Power-/Writebypassbits durch wirklicheOUTs, erhält BAR/PCI/Wake/POS/VRAM und stellt CR38/39/40 sowie SR8 wieder her. Unsupported-Modi haben keine Adapterwirkung. NoClear und volle Callerregister/Flags/IRET sowie aktiver Mono→Color-Rückweg sind geprüft. Alle vier alten65536-Byte-ROMströme bleiben exakt gleich.

Das Bank-/LFB-Modell verwendet den bestehenden VGA-Latch-, Read-/Write-Mode-, Mapmasken- und Chain4-Pfad. Der dokumentierte2MCLK-Bypass gilt nur für Writes bei beiden gesetzten Steuerbits; Reads behalten ihre VGA-Latchadressierung. Nibbleswap wird am linearen Datenpfad umgesetzt. Historisch abgeglichen sind die benannten bankdisabled Standardfälle und banked Chain4. Nichtnullige unchained-/OddEven-Bankkomposition bleibt eine eigene begrenzte Modellpolitik; daraus folgt keine allgemeine historische Bank-/Scanout-/Speicheraliaspräzision.

45 explizite Zustandstypen/824 Instanzmitglieder und weiterhin39 unveränderte Ereigniskörper. Neue S3-Daten sind drei VGA-Steuerlatches, das feste Modellinvariant und ein PCI-Konfigurationsbytefeld. BAR-Bits liegen kanonisch ausschließlich inCR59/5A; der PCI-Readback wird daraus abgeleitet. Keine widersprüchliche zweite BAR-Kopie, keine neuen Timer oder fremden Decoderobjekte. Decoder-/Video-/PCI-/Resetpeers werden durch Referenzidentität geprüft. Format1 bleibt streng an die fünf tatsächlichen VM-Module gebunden.

43 wirklich rehashte semantisch beschädigte volle Container werden direkt und über den Owner vor Öffnung der eigenen exklusiv gesperrten Medien abgewiesen:86 Versuche, bisheriger Owner/RAM/Revision/Inputgeneration erhalten, kein Zweig.11 positive Zwischenzustände erhalten unter anderem hohenLFB, unabhängige tiefeCR5A-Bits,64KiB-Legacyausnahme, Bankfallback, deaktiviertesPCI, offeneDAC/ATC, IndexFF und gehaltenenReset. GehaltenePCI-Phasen müssen das vollständige wirkliche S3-Resetregisterbild besitzen. Frühere VGA-Profile dürfen keine fremden S3-Latches importieren.

## Endgültige echte Gäste und Nachweise

Acht neue ursprüngliche MS-DOS-/FreeDOS-/EMM386-NOEMS-/EMM386-RAM-Kaltstarts, je zweimal, und acht volle32MiB-Fortsetzungen auf denselben endgültigen Modulen. Jeder Gast schreibt zwei unabhängige2MiB-Muster über32 vollständige64KiB-Banken beziehungsweise den hohenLFB und liest jeweils über den anderen Pfad zurück:64 ganze Readbacks,4MiB unabhängig geprüfte Lesebytes und das ganze2MiB-Interleave am Schnitt pro Lauf. Tatsächliche BAR-Probe/Relokation, IOSE/MSE, Lock-/Bank-/Fenstergates und drei Gastscanouts sind gebunden:640×480/Pitch640/Start0,640×480/Pitch672/Start50000h und800×600/Pitch832/StartD0000h.24 vollständige Scanouts und8755200 unabhängig rekonstruierte Pixel; Bilder werden ausschließlich im Speicher gehasht, keine Aufnahme oder nativeGL-Abnahme.

Schnitt nach dem wirklichen eigenenNOP bei Bank17/LFB aktiv, DAC-Komponente1, offenerATC-Datenphase, aktiver512-Sample-PCM-DMA, roherx87, offenemDOS-Handle und Zukunftsereignissen.4804 weitere ganze CPU-/32MiB-RAM-/VRAM-/Geräte-/Schedulervergleichspunkte,3080 verworfene Gastoracles. Alle ursprünglichen DE01-PTEs behalten Adresse/P/RW/US; ausschließlich tatsächlicheA/D-Bits dürfen hinzukommen. Eigene MCB-/PSP-/JFT-/DDS-/Programm-/Stack-/Tabellen-/Readbackbereiche, beide hohen Aliasfenster und Original-VDS/VCPI-Ein-/Rückkehrpunkte sind vollständig gebunden. JFT ist kein voller SFT-Vertrag.

Alle acht Original-/Restore-Paare schließen die eigenen Dateien und erreichen den ursprünglichenDOS-Prompt. Danach wird jeweils ein neuer KBC-/IRQ1-/IN60-ECHO ausgeführt. Ganze Textplanes/Attribute/Font/CRTC/BDA und vollständiger Prompt mit Cursor4 werden unabhängig rekonstruiert. Sämtliche späteren Port-/IRQ-/Service-/Boundary-/NativePCM-/Framepublikationen werden auf der restaurierten Seite tatsächlich ausgeführt und verglichen. Native512DAC-/DMA-Samples pro Lauf sind unabhängig spezifiziert;74866 danach publizierte Stereo-PCM-Frames je Seite werden zeit-/anzahlgebunden und über die wirklichen Byte-Digests verglichen. Keine neue unabhängige Ganzmixrekonstruktion. Vier Wiederholungspaare stimmen in Zeit, ganzem Zustand/RAM, Ledger, Pixeln, Audio und Proof überein; jeweils nur zwei einzeln geprüfte äußere/innere Container-UUIDs werden kanonisiert, übrige Schnittmetadaten bleiben gebunden.

Gezielt acht alte486-DOS-Starts/acht volle8MiB-Fortsetzungen:1282 zusätzliche ganze Vergleiche/200 Negative, weiterhin alterAH4C-/30k-Vertrag ohne neuen Promptanspruch. Gesamt16 DOS-Kaltstarts/16 Fortsetzungen,6086 weitere ganze Vergleichspunkte und3280 negative Gastoracles. Komponenten-/CLI-/Vorläufe zählen separat. Eigener neuer CLI-Pfad setzt auch aus dem installierten Starter mit gleicherCPU/Bild/Zeit ohnePOST fort; tatsächlicher11148er-CLI-Import wird vor Zweig abgewiesen, weiterhin bestehender nichtnulliger Ausnahmeabbruch ohne behaupteten Exit1-Vertrag.

64 Kandidaten-/normalen-/Starterdateien sind bytegleich,26 gegenüber11148 geändert. Vollständiger alter Starter und28 ausdrücklich benannte Ausgangsquellen sind privat gesichert. Aktuell769 lizenz-/kommentargeprüfte Code-/Projektdateien,365 C#-/152 Python-/98 PowerShell-Dateien. Keine breite neue Quellen- oder115er-Schutzlistenhashrunde. CPU-/x87-/Float80-Quellen und500ms-/50ms-Repeatfix wurden nicht geändert. Native Repeat-/Maus-/GL-Abnahme bleibt offen. Keine persönlichen Spiel-COWs, Originalmedien, C64, Windows-Eingaben, Hostendpunkte oder Aufnahmen; HEAD und Commit-/Push-Verbot bleiben erhalten.

Die S3-CPU-A16-/Plane-Wrapantwort bei unchained-/OddEven-128KiB-Zugriff und SR4.1-Seitengrenzen ist außerhalb der geprüften Legacy03h/12h/13h-/Chain4-8bpp-Fälle nicht historisch abgenommen. CR32.6 ist eine reale256KiB-Scanout-Wrapsteuerung, deren gesetzter Zustand hier nur als Latch erhalten wird; die eigentliche Wrapwirkung bleibt ausdrücklich unimplementiert.
