# 82371FB A1: CF9, INIT und getrennte Resetdomänen

Stand:27.Oktober2026. Eigene Resetvertikale auf Build 11148 mit393 neuen und insgesamt11148 bestandenen Regressionen,0 Warnungen/Fehlern. Dieser begrenzte Referenzumfang ist integriert; vollständiger historischer PIIX/430FX,Windows 95 und M4–M8 bleiben offen. Privater Gesamtnachweis: `artifacts/tests/m4-piix-reset-release-path.txt`.

## Primärquellen und Geltungsbereich

Maßgeblich sind die ungeschatteten Beschreibungen des ursprünglichen **82371FB PIIX**, nicht die PIIX3-Erweiterungen:

| Primärquelle | Relevante Stellen, gedruckte Seiten |
| --- | --- |
| [Intel 290550-002, April 1997](https://www.mouser.com/catalog/specsheets/intel%20corporation_29055002.pdf) | 1.12, S. 19: PWROK, CPURST, PCIRST#, RSTDRV und INIT; 2.0–2.1, S. 22–23: Reset und ISA-kompatible Registerzugriffe; 2.5.2.2, S. 68: Timerstatus einschließlich OUT-Reset; 2.5.4.3, S. 75–76: RC an CF9h; 3.13, S. 111: zeitliche Resetstaffelung |
| [Intel 297658-004, März 1998](https://downloads.bl4ckb0x.de/download.intel.com/design/chipsets/specupdt/29765801.pdf) | S. 16, Klarstellung 1: RCPU muss nach Softreset erneut mit null und danach eins geschrieben werden. Der Downloadname endet auf 01; der gelesene Inhalt nennt -004. |
| [Intel 290518-002, Dezember 1996](https://bitsavers.trailing-edge.com/components/intel/pci/290518-002_82430FX_82437FX_82438FX_PCIset_DATASHEET_199612.pdf) | 82437FX RST#; 3.1.1, S. 15: ausschließlich Dword-CONFADD; 4.7.1, S. 48: eigener TDP-Resetablauf |
| [Pentium Volume 1, 241428-004](https://ardent-tool.com/CPU/docs/Intel/Pentium/241428-004.pdf) | 4.3 und Tabellen 4-1/4-2, S. 4-3–4-4: RESET und INIT, insbesondere die erhaltenen INIT-Zustände |
| [Pentium Volume 3, 241430-004](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf) | 16.1 und Tabelle 16-1, S. 16-1–16-5: Integer-, System-, Debug- und FPU-Initialisierung |

Die neue eigene Validierungsplatine heißt `dose-430fx-piix-reset-dev-v1`. Sie erweitert das bestehende P54C-B5-Teilmodell mit nominal 100 MHz, genau 32 MiB installierten RAM-Bytes, 82437FX A2 an `00:00.0` sowie PIIX A1 mit Revision 02h an `00:07.0` und `00:07.1`. BDF, Takt, RAM, Fähigkeiten und Resetverdrahtung sind feste Factory-/Profilinvarianten.

Ältere IRQ- und IDE-Profile behalten ihre bisherigen Resetgrenzen. Ihre vorhandenen PC-Dateien werden nicht still in die neue Platine umgedeutet. Das neue CF9-Ziel darf ausschließlich zusammen mit der dafür gebauten vollständigen Boardverdrahtung erreichbar sein.

## CF9: gespeicherte Flanke und getrennte Porttransaktionen

| RC-Bit | Bedeutung |
| --- | --- |
| 7..3, 0 | Reserviert; DOSE liest null und ignoriert Schreiben. |
| 2, RCPU | Ein gespeicherter Übergang 0→1 löst den gewählten Reset aus. Lesen liefert immer null. |
| 1, SRST | Null wählt CPU-INIT; eins wählt den Hardreset mit CPU-/PCI-/ISA-Resetdomänen. |

RC startet mit 00h. Der unsichtbare gespeicherte RCPU-Wert ist dennoch Zustand: Ein Lesen mit zurückgegebenem Bit2=0 rearmt die Flankenerkennung nicht. Zwei aufeinanderfolgende Schreibwerte 04h lösen deshalb genau einen Softreset aus. Ein erneuter Softreset benötigt beispielsweise **00h, 04h**. Das Intel-Update stellt diese Wiederholungsregel ausdrücklich klar.

Die neue eigene Implementierung verwendet beim auslösenden Schreiben den gleichzeitig angenommenen SRST-Wert. Damit kann beispielsweise 06h bei vorher gelöschtem RCPU einen Hardreset auswählen. Die von Intel beschriebene klare Programmierfolge setzt den Typ zunächst mit RCPU=0 und schreibt anschließend RCPU=1. Hardreset setzt auch RC selbst wieder auf den Resetwert; INIT setzt die übrigen PIIX-Konfigurationsregister nicht zurück.

Für die tatsächlich ab `CF9h` beginnende Transaktion gilt in diesem DOSE-Teilumfang:

- Byte, Wort und Dword verwenden ausschließlich das niedrigste Byte für RC.
- Die übrigen Schreiblane-Bits besitzen keine Wirkung. Höhere Lesebytes sind eine begrenzte offene DOSE-Antwort; Intel definiert für solche ISA-kompatiblen Register nur das niederwertige Byte.
- Ein **Dword ab CF8h** bleibt genau ein vollständiger PCI-CONFADD-Zugriff. Sein zweites Byte darf niemals als verstecktes CF9-Schreiben wirken.
- Schmale Zugriffe ab CF8h und Zugriffe ab CFAh sind eigene ungetriebene Transaktionen. Es werden keine synthetischen Teillane-Zugriffe auf RC erzeugt.

Diese Trennung ist Teil der Bus-/Factorybindung. Ein einzelner breiter CPU-Portbefehl wird nicht nachträglich in mehrere gerätewirksame Bytebefehle zerlegt.

## INIT ist eine eigene CPU-Domäne

Ein angenommener Softreset wird an einer sicheren CPU-Befehlsgrenze wirksam. Er beginnt erneut am Resetvektor `FFFFFFF0h`, setzt aber die nach Intel erhaltenen Zustände nicht mit einem gewöhnlichen `CPU.Reset()` zurück.

| Zustand | P54C-INIT | P54C-RESET |
| --- | --- | --- |
| Integerregister | Null; EDX mit der gebundenen Modellsignatur | Gleicher Integer-Startvertrag |
| EFLAGS / EIP | 00000002h / 0000FFF0h | Gleicher Integer-Startvertrag |
| CS | Selektor F000h, Basis FFFF0000h, Limit FFFFh | Gleicher Resetvektor-Cache |
| Übrige Datensegmente | Selektor/Basis null, Limit FFFFh, definierte Startattribute | Definierte Startattribute |
| CR0 | Alte CD/NW-Bits 30/29 erhalten, sonst 00000010h | 60000010h |
| CR2 / CR3 / implementiertes CR4 | Null | Null |
| DR0–3 / DR6 / DR7 | Null / FFFF0FF0h / 00000400h | Gleiche dokumentierte Startwerte |
| GDTR / IDTR / LDTR / TR | Definierte Startbasen, Limits und Attribute gemäß Pentium-Tabelle; kein übernommener aktiver Task | Definierte Pentium-Startwerte |
| TLB / Sprungvorhersage | Ungültig | Ungültig |
| Rohe x87-Register, Control/Status/Tag, Zeiger und Fehlerzustand | **Erhalten** | Pentium-Hardresetwerte, insbesondere CW=0040h, TW=5555h, SW=0 und rohe Register/Zeiger null |
| TSC und implementierte MSRs | **Erhalten**, der virtuelle Zähler läuft mit derselben Schedulerphase weiter | TSC wird an der CPU-Resetphase neu verankert |
| Daten-/Codecache | Laut Volume 1 Tabelle 4-2 und Volume-3-Prosa erhalten | Ungültig |

Die x87-Hardresetwerte sind nicht die Werte eines ausgeführten FNINIT. Die früheren 486-Verträge werden von dieser P54C-spezifischen Trennung nicht pauschal geändert. Die genaue bereits implementierte MSR-/CR4-Teilmenge bleibt durch die [P54C-Verträge](P54C-PC.md) begrenzt.

**Quellenwiderspruch bei Caches:** Volume 3 Tabelle 16-1 nennt für INIT eine Cacheinvalidierung, während die zugehörige Prosa den Erhalt nennt. Volume 1 Tabelle 4-2 bestätigt ausdrücklich den Erhalt. DOSE verwendet diese übereinstimmende Prosa-/Volume-1-Regel. Das Teilmodell besitzt weiterhin keine simulierten historischen L1-/L2-Cachelines oder deren elektrische Resetlatenzen.

INIT verändert keine PIIX-/Hostkonfiguration, keine PIRQ-/MIRQ-/ELCR-Auswahl, keinen PIC-Servicezustand und keinen PIT-/DMA-/ATA-/Busmastercursor. Bereits aktive Geräteereignisse bleiben geplant. RAM, VRAM, RTC/CMOS, Medien und bereits veröffentlichte Audio-/Portwirkungen bleiben erhalten. Insbesondere darf INIT die geschriebene TSC-Epoche, rohe x87-Werte oder die gesamte virtuelle Zeit nicht zurücksetzen.

## Hardreset: Pins, Zeit und dieselben Bauteile

Intel beschreibt einen mindestens 1 ms langen PCIRST#-/RSTDRV-Puls und das Deassertieren von PCIRST# **1–2 ms vor CPURST**. Die eigene nominale DOSE-Zeitfolge ist:

| Phase | Dauer und Wirkung |
| --- | --- |
| Hardreset angenommen | CPU hält an; PCIRST# und RSTDRV werden aktiv. Die tatsächlich betroffenen vorhandenen Komponenten erhalten ihre jeweilige Resetantwort. |
| PCI-/ISA-Reset aktiv | Genau 1 ms virtuelle Zeit; kein CPU-Gastbefehl wird ausgeführt. |
| PCI-/ISA-Reset freigegeben | PCIRST#/RSTDRV werden freigegeben; CPURST bleibt weitere 1 ms aktiv. |
| CPU-Reset freigegeben | Frühestens nach2 ms übernimmt der Owner die Deassertion und verankert den TSC an diesem tatsächlichen Zeitpunkt; erst danach wird wieder vom hohen Alias gefetcht. |

Dies sind ausdrücklich eigene ganzzahlige Schedulerfristen, keine vermessenen FSB-/PCI-/ISA-Signalflanken. Die Plattform bildet damit den benannten funktionalen Ablauf ab; sie simuliert keinen analogen PWROK-Filter, keine Netzteilrampe und keinen physischen Einschaltvorgang.

Der Owner hält dieselbe CPU, denselben Scheduler, dieselben RAM-Arrays, Medienbackends und Geräteobjekte. Es gibt weder einen neuen kalten PC noch `Scheduler.Clear()`, Zurücksetzen von `Now` oder Wiederverwendung alter Ereignistokens. Die Boardreihenfolge nimmt wirkliche Gerätequellen zurück, stellt die PIIX-Routen zurück und setzt den PIC zuletzt zurück, damit keine bloßen Verdrahtungszwischenzustände als neue IRQ-Flanken übrig bleiben. Von außen gesetzte PIRQ-/MIRQ-Pins bleiben unabhängig gespeicherte Eingänge.

## Implementierte interne Gerätedomänen

Die neuen `ApplyPiixHardReset()`-Methoden sind explizite Owneroperationen für die neue Platine. Sie ersetzen keine frei übertragbare Resetsemantik der alten Profile.

| Komponente | Hardresetantwort dieses Teilumfangs |
| --- | --- |
| 82437FX / CONFADD | Wiederherstellung der bereits benannten Konfigurationsdefaults: DRB0–4=02h, zunächst 8 MiB dekodiert, PAM/HEN zurückgesetzt, feste MAE-Resetantwort; CONFADD null. Physisch installierte 32 MiB und ihre Bytes bleiben vorhanden. |
| PIIX Funktion 0 | Konfigurationsdefaults einschließlich PIRQ/MIRQ=80h und ELCR=0. Die äußeren gewöhnlichen ISA-/PIRQ-/MIRQ-Eingänge werden nicht als interne Register genullt. |
| PIIX Funktion 1 | Konfigurationsdefaults einschließlich des ausdrücklich gewählten MSE=1-Vertrags, IOSE/BME aus, BMIBA=1 und IDETIM=0. BM-Command/Status/PRDT und Laufcursor werden gelöscht; nur eigene geplante BM-Grants werden storniert. |
| ATA-Peers | Die Funktion-1-Methode setzt kein Laufwerk beiläufig zurück. Tatsächlicher Gerätereset und dessen Staging-/IRQ-Antwort gehören zur gesonderten Board-/RSTDRV-Verdrahtung. Bereits geschriebene Sektoren werden niemals rückgängig gemacht. |
| PIC-Paar | IMR=FFh, IRR/ISR=0, ELCR-Auswahl null, ICW-Sequenz und Auto-EOI aus. Physische Eingangspegel bleiben gespeichert. Nicht dokumentierte ICW-/Vektorinhalte benutzen die benannten eigenen Werte 08h/70h. Kein Reset erzeugt eine fiktive Interruptbestätigung. |
| PIT0 | Alter eigener Timer storniert, nicht laufend; OUT ist nach dem ausdrücklichen Timerstatus-Resetwert **niedrig**. Zähler-/Programmier-/Readbacklatches werden zurückgesetzt. Undefined Count-/Mode-Inhalte verwenden die eigenen Werte 65536 und Mode 3. |
| PIT2 / Speaker | Vor dem Umschalten wird das bisherige Signal bis zur aktuellen Zeit integriert. Pending Countload wird gezielt storniert; Gate/OUT niedrig, kein laufender Count und keine alten Latches. Bereits publizierte Audiodaten bleiben erhalten. |
| ISA-DMA | Alle Kanäle maskiert, Anforderungen/Status/Terminalcount/Bytepointer/Commands zurückgesetzt. Nicht definierte Adress-, Count-, Page- und Modeinhalte verwenden eigene Nulldefaults. Die vorhandenen acht Kanalobjekte und alle RAM-Bytes bleiben erhalten. |

Die Funktion-1-Resetmethode übernimmt den aktuellen tatsächlichen Drivepin als ihre neue Flankenbasis. Ein über die reine Funktionsoperation hinweg weiter hoher Drivepin wird nicht als neue BM-Interruptflanke erfunden. Ein späterer echter Low→High-Übergang bleibt wirksam.

Für PIT0 speichert `_resetOutputLow` ausschließlich die neue echte Reset-OUT-Antwort. Der Konstruktorwert bleibt false, sodass alte Profile ihren bestehenden Kaltstartvertrag behalten. Der Guard wird erst durch eine tatsächlich unterstützte Mode-2-/Mode-3-Programmierung oder eine vollständige Countübernahme aufgehoben. Latch/Readback, andere Counter und das erste LSB eines Zweibytecounts dürfen ihn nicht aufheben. Ohne neue Programmierung erzeugt der zurückgesetzte Timer keine alten periodischen IRQ-Pulse. Die Diagnose `InterruptPulses` bleibt monoton.

Die RSTDRV-Antwort weiterer vorhandener ISA-Peers muss der Boardowner ausdrücklich festlegen und prüfen. RTC-Batteriedaten, RAM und dauerhaft übernommene Medienänderungen werden nicht durch eine pauschale Neuerzeugung der Bauteile verloren. Nicht implementierte SMM-/SMRAM-Domänen und insbesondere der reale power-on-bezogene SMRAM-DLCK-Vertrag werden nicht als abgenommen ausgegeben.

## Firmware, Zustand und Gastgrenzen

Der hohe Reset-ROM-Alias bleibt auch bei zurückgesetztem PAM erreichbar. Nur das neue Profil besitzt den frühen Warmdispatch: CMOS0F=0Ah und BDA472=1234h gemeinsam, einmaliges Löschen beider Cookies, far Jump über0040:0067 vor vollständigem POST. CMOS70-Index/NMI bleiben erhalten. Der eigene Warmgast stellt bei Hard die32 MiB-DRBs ausdrücklich selbst per PCI-OUTs wieder her; der kalte Firmwarepfad programmiert sie vor POST. Keine unsichtbare DOS-/Monitorwiederherstellung durch den Host. Nach Hardreset muss die passende eigene Firmware die tatsächlichen frühen DRB-Schreibtransaktionen wieder ausführen, bevor sie den vollen 32-MiB-RAM-Umfang verwendet oder verspricht.

Hardware-INIT beziehungsweise Hardreset löschen die erhaltenen physischen RAM-/CMOS-Bytes nicht. Eine BDA-Warmstartmarke, ein Vektor an 0040:0067 oder ein CMOS-Shutdownbyte sind dennoch keine vom PIIX ausgeführte Firmwarefunktion. Ihr Interpretieren und ein früher Rückweg vor einem vollständigen POST gehören zum ausdrücklich implementierten DOSEBios-Vertrag. Ein eigener Firmware-Rendezvous ist kein pauschaler IBM-BIOS-Kompatibilitätsnachweis. Insbesondere darf ein voller POST mit Neuinitialisierung von IVT/BDA/PIC/PIT nicht als unveränderte DOS-/EMM386-Fortsetzung bezeichnet werden.

Die vollständige Zustandsbindung muss das unsichtbare CF9-Flankenlatch, aktuelle Resetphase, Fristen/Token und feste Boardpeers enthalten. Eine Sicherung mitten in einem Reset darf beim Laden weder erneut eine Flanke auslösen noch CPU oder Geräte vor ihrer gespeicherten Freigabe laufen lassen. Die vorhandene buildgebundene Format-1-Prüfung, Medienvorprüfung und Importablehnung vor Zielzweiganlage bleiben erhalten. Ein neuer Resetvertrag ist kein Anlass, fremde Profile, Builds oder beschädigte Zustände still zu migrieren.

## Externe Peers und Kompatibilitätsgrenzen

RSTDRV setzt nur die ausdrücklich verdrahteten eigenen Registerantworten zurück. KBC verwirft alte Queues und sperrt Annahme bis zur Freigabe; externe PS/2-Maus und analoger Gameport bleiben versorgt. FDC storniert eigene Sektor-/Seekfristen, erhält physische Zylinder/Medien und erzeugt nach Freigabe seine echte Resetantwort. Beide ATA-Kanäle erhalten auch bei vorher schon hohem SRST ein deterministisches Assertbild; alte sektorweise Commits werden storniert. SB2/SB16 stoppen alte DMA-/Command-/Samplefristen; vorher integriertes Audio und bereits übernommene Medienbytes werden niemals zurückgespult. VGA/ET4000 setzen eigene Register/Palette zurück und erhalten sämtliche VRAM-/Fontbytes und bestehende Readerleases. Dies ist eigene Boardverdrahtung, keine universelle Aussage über alle ISA-Karten.

UART erhält physische Modempins und den begonnenen äußeren RX-Termin, ignoriert aber den alten Frame über einen expliziten Generationstoken. Ungestartete äußere Frames bleiben bei Divisor0 pausiert, bis Software neu programmiert; dies ist eine funktionale Receiverclock-Grenze. MPU hält seine unabhängige äußere31250 Baud-Zeitlinie und sendet beim HardwareReset kein unaufgefordertes Command-ACK. LPT-Ausgänge setzen zurück und informieren nur den bestehenden virtuellen Drucker; bereits akzeptierte Bytes bleiben erhalten. Keine Hostgeräte werden verbunden. RTC/Batteriedaten/Index/NMI bleiben versorgt. Analoge Audio-/Filterreste dürfen weiter integrieren; alte DSP-DMA-Samplepublikationen dürfen nicht fortsetzen.

Bestehendes `Cpu.Reset()` sowie Host-/KBC-/Port92-Hotreset bleiben der bisherige DOSE-Entwicklungsvertrag. Die neuen P54C-Signal-APIs heißen `Init()`, `ResetSignal()` und `ResetSignalReleased()`; sie werden für CF9 und den benannten SCE-Shutdownpfad verwendet. Kaltstart erzeugt neue Factory-Peers und ist kein Ersatz für CF9. PWROK-Rampen, elektrische Pin-/Busflanken, SMM/SMBASE und Cachelines sind weiterhin nicht modelliert. Null-LDT/TSS-Caches bleiben trotz benannter historischer Startbytes aus Sicherheitsgründen unbenutzbar; keine universelle B5-Siliziumzusage.

## Nachweise und offene Arbeit

11148/11148, 393 neue Fälle: 90 CPU, 45 interne Domänen, 103 äußere Peer-/Fortsetzungsfälle, 126 tatsächliche Import-/Medienkontrollen und29 volle CF9-PC-Fälle.113 beschädigte Container liefern226 Ablehnungen vor Medienöffnung;13 positive Kontrollen bewahren Pending/AllAsserted/CpuOnly/ready-held, beide Arten eigener Medien, zuvor gehaltenes ATA-SRST und stornierte bekannte Historie.44 Typen/815 Mitglieder/39 explizite Ereignisse; zwei neue Releasecallbacks und der gezielt generationserweiterte alte UART-RX-Callback, alle36 anderen bisherigen Callbackkörper erhalten.

Acht endgültige eigene DOS-Kaltstarts mit24 vollständigen32 MiB-Fortsetzungen unter MS-DOS/FreeDOS/originalem EMM386 NOEMS/RAM je zweimal:6008 ganze Vergleichspunkte,16 echte Resetvektoren,24 Scratch-DMAs undacht abgebrochene Sektorcommits. Alle vier PCs je Lauf kehren über eigenen Realpfad beziehungsweise ursprünglichen DE0C-Server zum ursprünglichen DOS zurück und führen einen neuen ECHO mit tatsächlich konsumiertem Enter aus. Vollständige sichtbare Standard-VGA-Zellen/Attribute/Font/Cursor/BDA werden unabhängig geprüft. Originale VDS8103/810B/810C/8104 und DE00/DE01/DE0C sind an tatsächliche AX/BX/DX/ES:DI/CS:EIP-/VM-/PE-/PG-Punkte gebunden.2888 Negativoracles, vier stabile Wiederholungspaare; nur sechs geprüfte Container-UUIDs werden kanonisiert, alle übrigen tatsächlichen Medienmetadaten/Siegel werden verglichen. Keine alten CPU-/Gerätebilder werden zur Hard-Erholung in den ursprünglichen PC geladen.

Getrennt acht alte486-DOS-Starts/acht Fortsetzungen mit1282 weiteren8 MiB-Vergleichen/200 Negativen, historischer AH4C-/30k-Nachlauf ohne neuen Promptanspruch. Gesamt16 Starts/32 Fortsetzungen/7290 Vergleichspunkte/3088 Negativoracles. CLI-/synthetische Gerätefälle und vorläufige Sentinel-/Text-/UUID-/Präfix-/Segmentindex-Oracles zählen separat. Format1 bleibt buildgebunden; alter10755er-CLI-Import wird vor Zweiganlage abgewiesen, neuer eigener CLI-Pfad setzt ohne POST mit gleicher CPU/Bild/Zeit fort. Die bestehende CLI meldet die absichtliche InvalidDataException als nichtnulligen Prozessabbruch; kein kontrollierter Exit1 wird dafür behauptet.

Keine native Windows-/Repeat-/Maus-/GL-Abnahme, keine Aufnahme, kein Original-Doom-Start und keine neue allgemeine Spiel-/Sound-/Performancezusage. Vollständiger82371FB/430FX, weitere Resetursachen/PWROK/SMM/Buspräzision und133 MHz bleiben begrenzt. Nächster Produktschritt ist S3 Trio64, dann ATAPI/Firmware/Manager und tatsächliche Win95-RTM-Installation/mehrere Starts/stabile Geräte/Shutdown. M4–M8 und Job bleiben aktiv.
