# DOSEBios: IdentitÃ¤t, POST und Reviewkorrekturen

Die zusätzliche sechste [PCI-BIOS-/BIOS32-ROMvariante](PCI-BIOS.md) ist auf ihrem festen eigenen Profil mit11819 Regressionen und echten DOS-/Original-EMM386-/FAR-Aufrufen integriert. INT1A/B1, FE6E und BIOS32 greifen auf die wirklichen PCI-Ressourcen zu. Alle fünf bisherigen ROM-Bytestrome bleiben unverändert. PnP/APM, vollständige Firmwarekonformität und Windows 95 bleiben offen.

## Eigene PIIX-IDE-Variante vom 27. Oktober 2026

Nur `dose-430fx-piix-ide-dev-v1` erhält zusätzlich zum eigenen 430FX-DRB-POST wirkliche PCI-OUTs: Fn1-BMIBA=C001h, IDETIM=80008000h, Fn0-MBIRQ0=0Fh und Fn1-Command=0003h; danach CF8=0. Somit sind PIO-Gates geöffnet, der Gast muss PCI-Master BME selbst freigeben. Originale Default- und reine FX-Host-ROMbytes bleiben gegenüber10141 exakt gleich; öffentlicher BIOS-ABI bleibt erhalten. [IDE-Vertrag und Gastnachweis](PIIX-IDE.md), Build 10755. Keine fertige PCI-BIOS-/Windows-Firmware-, CF9-/INIT- oder historische Gesamtplatinenzusage.

## Eigene 430FX-Hostvariante vom 27. Oktober 2026

Das [Host-Validierungsprofil](430FX-HOST.md) erhält eine getrennt assemblierte eigene `DOSE_430FX_HOST`-Variante. Vor Vektoren/BDA/POST führt der Gast tatsächliche CF8h-/CFCh-OUTs aus, programmiert alle fünf DRBs auf 08h und damit 32 MiB und deaktiviert das Konfigurationslatch wieder. Die Inventur nennt P54C B5 / 430FX host subset. Der bisherige 65536-Byte-Default-ROM-Bytestrom bleibt bytegleich (SHA256 `353CFA9B957CF6433F8F2B92EFA1BD33F90CDE2FA1C3BC4D63D60AA87A56FC67`); öffentliche BIOS-Builder bleiben ABI-kompatibel. Dies ist keine PIIX-/PCI-BIOS-/historische Boardfirmware-/Windows-Abnahme.

## Metadaten-Pufferkorrektur vom 27. Oktober 2026

Der [8870er-Auditfolgebuild](../AUDIT-FIXES-2026-10-27.md) prÃ¼ft AH42/43-DAP vollstÃ¤ndig vor dem ersten Lesen (SI hÃ¶chstens FFF0h fÃ¼r die verwendeten 16 Bytes) und AH48-Puffer vor Lesen/Schreiben (SI hÃ¶chstens FFE6h fÃ¼r 26 Bytes). Fehler liefern AH01/CF/BDA474=1 vor ATA oder Caller-Pufferwirkungen. 58 FirmwarefÃ¤lle und acht tatsÃ¤chliche DOS-/FreeDOS-/EMM386-GÃ¤ste mit 96 Aufrufen/8192 Scratch-ATA-WÃ¶rtern bestehen. GrÃ¶ÃŸere DAP-PrÃ¤fixgrÃ¶ÃŸen behalten ihren bisherigen Teilvertrag. Die getrennten ungeraden Datensektor-/Wortportpfade bleiben erhalten. Neue Firmwareadressen/IVT-RAM und zusÃ¤tzliche Vergleiche begrÃ¼nden keine vollstÃ¤ndige RAM-/ZeitidentitÃ¤t mit frÃ¼heren ROMs.

Stand: 26. Oktober 2026, **DOSEBios 0.3.0-dev** fÃ¼r **DOSE (DosEmulator)**. EigenstÃ¤ndige Apache-2.0-Firmware, keine Nachbildung einer fremden BIOS-Marke oder eines fremden ROMs. Name, Version und reproduzierbares ROM-Datum stehen gemeinsam in `firmware/bios-identity.inc`; der Build erzeugt daraus auch die Ã¶ffentlichen `DOSEBios`-Konstanten. Quelle ist `firmware/dose-bios.asm`, erzeugtes ROM `artifacts/firmware/dose-bios.bin`, Laufzeitklasse `src/DOSEmulator.Runtime/DOSEBios.cs`. Die Firmware bleibt genau 64 KiB groÃŸ.

## MIDI-Inventurnachtrag

Der **6079er-Build** zeigt den ausdrÃ¼cklich konfigurierten CT1740-MPU-UART im POST an. E018h enthÃ¤lt den Port 300h/330h oder null; E01Ah den konfigurierten Karten-IRQ. Es wird weder ein MIDI-BDA-Eintrag noch ein nicht vorhandener BIOS-MIDI-Dienst erfunden. Die Anzeige beschreibt das Startprofil, nicht spÃ¤tere GastÃ¤nderungen von Mixer80. Ein eigener kombinierter POST-Start mit Gameport/COM1/LPT1/MIDI besitzt 256000 unabhÃ¤ngig geprÃ¼fte Pixel und eine visuell geprÃ¼fte Aufnahme. [Transportvertrag und tatsÃ¤chliche DOS-Nachweise](MIDI-MPU401.md). FrÃ¼here Nachweise unten behalten ihre Builds.

## LPT1-/INT17-Nachtrag

Der **5995er-Build** ergÃ¤nzt die optionale IBM Printer Adapter 6361507 an 378h/IRQ7 mit ROM-Auswahl E017h, BDA-LPT1/Timeout, INT11-Portanzahl und POST-Inventur. Eigene INT17/AH00â€“02-Firmware fÃ¼hrt Daten-/Status-/INIT-/STROBE-Zugriffe im Gast aus. Nicht unterstÃ¼tzte Dienste/Ports liefern AH01 vor GerÃ¤tekontakt; eine fehlende Karte meldet keine erfundene Druckbereitschaft mehr. AH00 wartet begrenzt auf Non-Busy, erhÃ¤lt Register/Flags und CMOS-Index/NMI. AH01 meldet nach begrenztem INIT und Erholung den tatsÃ¤chlichen Status, ohne auf einen nicht angeschlossenen Drucker zu warten. Die nominale Senke besitzt eigene dokumentierte Fristen; kein Hostdrucker. [PrimÃ¤rquellen, Schaltplan-/IRQ-Vertrag, Gastnachweise und Grenzen](PARALLEL-PRINTER.md). Die frÃ¼heren Nachweise unten behalten ihre jeweiligen Builds.

## COM1-/INT14-Nachtrag

Der **5688er-Build** enthÃ¤lt eine optionale PC16550D-COM1-Karte mit ROM-Auswahl E016h, BDA-Basis 3F8h, INT11-Portanzahl und POST-Inventur. Eigene INT14/AH00â€“03-Firmware fÃ¼hrt tatsÃ¤chliche Registerzugriffe aus; nicht unterstÃ¼tzte Dienste/Ports liefern AH80 vor GerÃ¤tekontakt. Polled Senden benÃ¶tigt DSR/CTS und THRE, Empfang DSR und DR. RTC-Timeouts unterscheiden das erste Teilintervall von ganzen Sekunden und erhalten CMOS-Index/NMI; ein unabhÃ¤ngiges Poll-Limit bleibt. Die echte FreeDOS-Reproduktion und der RTC-Grenztest gehÃ¶ren zu 200 neuen Regressionen und acht endgÃ¼ltigen UART-Gaststarts. [Genauer Register-, BIOS-, Zeit- und Abnahmevertrag](SERIAL-16550.md). Ohne Karte bleiben BDA-Portanzahl und Inventur unverÃ¤ndert; die bisherigen Nachweise unten bleiben historisch.

## Gameport-Nachtrag

Der **5488er-Build** kann die ausdrÃ¼cklich konfigurierte eigenstÃ¤ndige IBM-Analogkarte in BDA-/INT11-Equipment-Bit 12 und der POST-Inventur ausweisen. Die neue ROM-Konfiguration verwendet E015h; vorhandene RAM-/Laufwerks-/Grafikfelder und dynamische EBDA bleiben erhalten. Ohne konfigurierte Karte entstehen weder Inventurzeile noch Bit. **INT15/AH84 bleibt mit AH=86h/CF unimplementiert**, keine erfundene Positionsermittlung durch den Host. Der geprÃ¼fte direkte Gast-OUT-/IN-Pfad und seine Grenzen stehen in [GAMEPORT.md](GAMEPORT.md). Die folgenden BIOS-Nachweise bleiben ihren historischen Builds zugeordnet.

## ET4000-VBE-Nachtrag

Der 5342er-Build ergÃ¤nzt eigene Gastfirmware in `firmware/video-vbe-bios.inc`: VBE-1.2-Datenformat mit Controller-/Modusinformation, Set/Get und Window A fÃ¼r 640Ã—480/800Ã—600/1024Ã—768 bei 256 Farben. Der direkte ROM-Callback fÃ¼hrt einen tatsÃ¤chlichen Far-Call/RETF aus. **GeprÃ¼fte Teilmenge 00h/01h/02h/03h/05h, keine vollstÃ¤ndige VBE-KonformitÃ¤t**; Save/Restore/Scanline/Display Start und Erweiterungen bleiben offen. [Vertrag, PrimÃ¤rquelle und tatsÃ¤chliche DOS-Nachweise](ET4000-VBE.md). Die folgenden historischen BIOS-/ATA-Nachweise behalten ihre dokumentierten Builds.

## Reviewnachtrag: ungerader ATA-Puffer bei Offset FFFFh

Der zuletzt offene Randfall aus dem parallelen Review ist im **5276er-Build** behoben. Ein 16-Bit-Speicherzugriff auf `ES:FFFF` Ã¼berschritt das normale Segmentlimit, bevor die bisherige Schleife ihren SegmentÃ¼bertrag ausfÃ¼hren konnte. Eine neue Regression reproduziert die fehlende BIOS-RÃ¼ckkehr am unverÃ¤nderten archivierten 5176er-BIOS.

Bei einem ungeraden Start oberhalb `FE00h` prÃ¼ft nun ein gesonderter Sektorpfad den einen mÃ¶glichen GrenzÃ¼bertritt. Lesen speichert AL am alten `ES:FFFF`, trÃ¤gt ES um `1000h` weiter und speichert AH am neuen `ES:0000`; Schreiben liest diese beiden Bytes vor dem gemeinsamen `OUT DX,AX`. Am ATA-Port entsteht weiterhin genau ein Worttransfer. Die verbleibenden SektorwÃ¶rter gehen unmittelbar wieder durch die gewÃ¶hnliche Wortschleife. Gerade beziehungsweise nicht grenzÃ¼berschreitende Sektoren benÃ¶tigen keinen zusÃ¤tzlichen Vergleich je Wort. Die bestehende ES-/A20-/Speicherbuspolitik bleibt erhalten; keine Lockerung von CPU-Segmentgrenzen oder allgemeine neue HMA-/MMIO-/Alias-/FaultprÃ¤zision.

**100 zusÃ¤tzliche Regressionen** prÃ¼fen EDD-Lesen/-Schreiben mit 1/2/127/128/255/256/257 Sektoren und acht Ausrichtungen sowie CHS mit Grenzpuffern, Schutzbytes, DF und Register-/Stack-/ES-Erhaltung. **5276/5276** FÃ¤lle bestehen, normaler Build mit null Warnungen/Fehlern. Der vollstÃ¤ndige lokale IPC-Test benÃ¶tigt auf diesem Windows-Host einen berechtigten Named-Pipe-Zugriff; der entsprechende Gesamtlauf ist bestanden.

Eigene `tests/guests/ata-boundary-test.asm`/`ATABND.COM`, reproduzierbar mit `scripts/test-ata-boundary.ps1`: DOS-Speicherreservierung, sechs Offsets, ein/drei Sektoren, CHS und EDD. Nur eine neue private 2-MiB-Platte an BIOS `81h` wird roh beschrieben. Acht endgÃ¼ltige MS-DOS-/FreeDOS-/EMM386-NOEMS-/RAM-Kaltstarts prÃ¼fen **192 FÃ¤lle**, **384 Befehle**, **196608 tatsÃ¤chliche ATA-WÃ¶rter** und **6144 tatsÃ¤chliche Schutzbytes**; der unabhÃ¤ngige Python-PrÃ¼fer kontrolliert auch Programm, Paging, alle PIO-Werte und die ausgefÃ¼hrte DOS-RÃ¼ckkehr. **72 VerfÃ¤lschungen** werden abgewiesen. Zwei zusÃ¤tzliche kurze originale Doom-Starts ohne/mit Ton liefern **60 WAD-geprÃ¼fte Bilder** und DOS-RÃ¼ckkehr. Kandidaten-/Startordner sind Ã¼ber **64 Release-Dateien** bytegleich, Originale/Baselines und fertige Videos unverÃ¤ndert. Privater Bericht: `artifacts/tests/ata-boundary-release-path.txt`. Die folgenden Start-/Performancezahlen bleiben ihren jeweiligen historischen Builds zugeordnet.

## Startanzeige

Der echte Gast zeichnet ein eigenes blaues Textlogo mit Name und Version in VGA-Textspeicher. Danach folgen konfigurierte CPU, tatsÃ¤chlich eingestellte RAM-Menge, konventioneller Speicher, VGA-/ET4000-Modell, alle angeschlossenen IDE-PlÃ¤tze samt BIOS-Nummer, Master/Slave, MiB und Schreibschutz, Diskettengeometrien und Bootziel. Die Meldungen beschreiben die Konfiguration und die ausgefÃ¼hrten Initialisierungsschritte; es wird kein vollstÃ¤ndiger Speicher- oder Hardwaretest vorgetÃ¤uscht.

Die erste Logozeile zeigt `DOSE - DosEmulator`, die zweite `DOSEBios 0.3.0-dev`. Die eigene Standardgrafik heiÃŸt DOSE VGA und verwendet `dose-vga-development`; die frÃ¼here Kennung wird beim Laden bestehender PCs normalisiert. Technische Projekt-/Assemblypfade bleiben kompatibel. Der neue Namensstand besitzt 5176 bestandene Regressionen und 38 DOS-Kaltstarts, eigenes vollstÃ¤ndiges POST-Pixeloracle und sechs visuell geprÃ¼fte GUI-Aufnahmen. Nachweis: `artifacts/tests/dose-branding-release-path.txt`. Die folgenden ausfÃ¼hrlichen Review-/Performancezahlen gehÃ¶ren zum davor geprÃ¼ften 5175er-Build.

Fortschrittszeilen kennzeichnen IVT/BDA/EBDA und Plattforminitialisierung, FDC-Reset, IDE-Reset und Tastatur-/Bootsektorstart. Ein FDC-Timeout erscheint als Warnung; ein fehlender oder nicht signierter Bootsektor hat einen eigenen Fehlertext. Alle Zeichen gehen zusÃ¤tzlich an Diagnoseport E9. POST-Codes 10h und 20h bleiben erhalten. Es gibt keine kÃ¼nstliche Wartezeit oder neue Taste zum Weiterstarten; ein Betriebssystem darf die Anzeige anschlieÃŸend Ã¼berschreiben. Ein neuer Kaltstart Ã¼ber den normalen Starter verwendet die ausgelieferten Dateien.

Ein eigener signierter CLI/HLT-Bootsektor hÃ¤lt den tatsÃ¤chlichen Endzustand zur PrÃ¼fung an. Das unabhÃ¤ngige Pixeloracle vergleicht **256.000 Pixel**, 2.000 gerenderte Textzellen und den Unterstrichcursor gegen die ursprÃ¼nglichen Glyphenzeichnungen, eine explizite Konfigurationsfixture und die GrÃ¶ÃŸen der neu angelegten eigenen Testplatten. BDA, Bootsektor, E9-Ausgabe, ROM-Datum und acht absichtliche VerfÃ¤lschungen werden zusÃ¤tzlich geprÃ¼ft. Die RAM-Datei enthÃ¤lt nur Backing-RAM: ROM und VGA-MMIO werden ausdrÃ¼cklich nicht als deren Nullbytes interpretiert. Die Aufnahme ist visuell geprÃ¼ft; keine Windows-Eingaben oder fremden Medien.

## Konkrete Reviewbefunde

- **Caps Lock und Shift:** Buchstaben verwenden beide ZustÃ¤nde als XOR. Caps+Shift liefert Kleinbuchstaben; Zahlen, Satzzeichen und Ctrl behalten ihre getrennten Regeln. Die tatsÃ¤chliche BIOS-Tastaturverarbeitung ist geprÃ¼ft.
- **INT 1Ah/AH=01h:** Setzen der Tickzahl lÃ¶scht nun auch den MitternachtszÃ¤hler in BDA 0040:0070. Das entspricht dem dokumentierten BIOS-Vertrag. [RBIL: Set System Time](https://fd.lod.bz/rbil/interrup/bios/1a01.html).
- **REP aus ROM:** Die vorhandene begrenzte RAM-Datenbeschleunigung darf jetzt Befehlsbytes aus einem vollstÃ¤ndig zusammenhÃ¤ngenden, unverÃ¤nderlichen eigenen ROM lesen. Ein ROM-GerÃ¤t kopiert seine Eingabe privat. RAM/MMIO-/ROM-Ende, A20-Faltung, Code-/Seitengrenzen und GerÃ¤tecallbacks erzwingen weiterhin den Fallback; ein fehlgeschlagener Versuch berÃ¼hrt kein GerÃ¤t. Daten bleiben auf gewÃ¶hnliches RAM beschrÃ¤nkt. Rechte, Selbstmodifikation, DF/Ãœberlappung, Debug/Beobachter, Faults, GerÃ¤tefristen und vier virtuelle Clocks je Element bleiben erhalten. Der reale BIOS-AH87-MOVSW-Pfad nutzt dies ohne Host-BIOS-AbkÃ¼rzung.
- **ATA-PIO:** Ein BIOS-Aufruf bÃ¼ndelt bis zu 256 Sektoren je tatsÃ¤chlichem ATA-Befehl, mit weiterem Befehl fÃ¼r den Rest. Count 00h bezeichnet 256 Sektoren. Pro Sektor bleiben DRQ-/BSY-/FehlerprÃ¼fung und tatsÃ¤chliche Wort-I/O bestehen; Schreiben wartet auf Commit. Getrennte Lese-/Schreibschleifen vermeiden die Richtungsentscheidung pro Wort. Der gesamte LBA-Bereich wird vorher geprÃ¼ft, ES:DI trÃ¤gt beim 64-KiB-Ãœbergang. [T13/1321D, ATA-Draft Revision 3](https://www.seagate.com/support/disc/manuals/ata/d1153r17.pdf).
- **FDC:** Erfolg ist weiterhin ein unmittelbarer MSR-Poll. Nur ein wartender Pfad liest alle 256 erfolglosen Polls die echte RTC-Sekunde. Zwei Sekundenwechsel begrenzen einen gewÃ¶hnlichen Stillstand auf ungefÃ¤hr ein bis zwei virtuelle Sekunden zuzÃ¼glich Abtastung. CMOS-Index und NMI-Bit bleiben erhalten, PIT und Interruptpolitik werden nicht umprogrammiert. Bei eingefrorener/fehlender RTC bleibt die bisherige Grenze von vier Millionen Polls als RÃ¼ckfall. ZusÃ¤tzlich wurde ein tatsÃ¤chlicher Fehler behoben: Zwischenfehler von `fdc_send` brechen Lese-/Schreib-/Reset-/Seek-Sequenzen sofort ab, statt nach einem Timeout weitere Kommandobytes zu senden und erneut zu warten.

## Nachweise und Grenzen

**5175/5175 Regressionen**, 74 neue FÃ¤lle/Gruppen, normaler Build mit null Warnungen/Fehlern. Neue FÃ¤lle prÃ¼fen unter anderem ROM-Code mit Paging und Richtungs-/Breitenvarianten, Pagefault nach bereits abgeschlossenen REP-Elementen, Fristen/Beobachterfallback, Caps/Ctrl, Uhrzustand und POST. ATA liest und schreibt 1/2/127/128/255/256/257 Sektoren mit zwei Pufferausrichtungen; Bereichsfehler erzeugen keinen ATA-Befehl. Ein tatsÃ¤chlich fehlschlagendes Backend nach zwei Sektoren prÃ¼ft CF und den sofortigen Abbruch. Ein angehaltener FDC prÃ¼ft den Timeout unter binÃ¤rer und BCD-RTC samt CMOS-/NMI-Erhaltung.

Auf demselben endgÃ¼ltigen DLL-Satz bestehen **77 kontrollierte DOS-Kaltstarts**: vier HIMEM einschlieÃŸlich fehlendem Treiber, 26 VCPI/EMS einschlieÃŸlich vier Konfigurations- und zwei VerfÃ¼gbarkeitskontrollen, 13 FDC/VDS/FORMAT einschlieÃŸlich einer VerfÃ¼gbarkeitskontrolle, 16 Vierplatten-/Persistenz-, vier Paging/V86-, sechs FreeDOS-FDC- und acht originale Doom-Starts. Vier dieser 77 sind die ausdrÃ¼cklich erwarteten Treiber-/Dienst-Negativkontrollen. **204 verfÃ¤lschte Gastoracles** und **acht zusÃ¤tzliche POST-VerfÃ¤lschungen** werden verworfen.

Die acht Doom-Starts kombinieren HIMEM/EMM386-RAM, VGA/ET4000 und ohne/mit PCM/FM. Sie liefern **244 WAD-geprÃ¼fte Bilder**, **6.762.726 Stereo-PCM-Frames**, tatsÃ¤chliche DOS-RÃ¼ckkehr und danach acht vollstÃ¤ndige CP850-ANSI-CLS-Bilder. Die verwendeten frischen Journalkopien bleiben bytegleich zu ihren Eingaben. Das ist eine kurze 32-Tick-Regression, keine neue lange oder native interaktive Spielabnahme. Eine allgemeine FPS-Zusage folgt daraus nicht.

Die getrennten AB/BA-Mikromessungen samt festen Arbeitsmengen stehen in [PERFORMANCE.md](../PERFORMANCE.md). ATA fÃ¼hrt absichtlich weniger echte Firmwareinstruktionen aus; deshalb wird zwischen altem und neuem BIOS keine identische virtuelle Gesamtzeit oder identisches vollstÃ¤ndiges RAM behauptet. Der AH87-Kopierbenchmark behÃ¤lt hingegen exakt seine Instruktionszahl und virtuelle Dauer. Modellgrenzen fÃ¼r CPU, GerÃ¤te, RTC-/BusprÃ¤zision und M3 bleiben bestehen. Die verschobene native Mauswiederholung bleibt offen.

77 PowerShell-Skripte/251 Checks sowie die Lizenz-/KommentarprÃ¼fung aller **514 Code-/Projektdateien** bestehen. Der vollstÃ¤ndige getestete Release-Satz ist hashgleich im normalen Startordner. Originalmedien, Original-Doom-Settings/-SpielstÃ¤nde und 53 private Baselines bleiben unverÃ¤ndert; persÃ¶nliche Spiel-COWs werden nicht geÃ¶ffnet. Keine Commits/Pushes. Private Quellen-/DLL-/Medienhashes, Messungen und Gastberichte: `artifacts/tests/bios-review-release-path.txt`.

## Reproduktion

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
dotnet build .\tests\DOSEmulator.Benchmarks\DOSEmulator.Benchmarks.csproj -c Release -p:DosAssemblyRoot=<vollstÃ¤ndiger-DLL-Ordner> -o <Benchmarkordner>
dotnet <Benchmarkordner>\DOSEmulator.Benchmarks.dll bios 7
```

Das Benchmarkprogramm verwendet ausschlieÃŸlich eigene flÃ¼chtige Medien. `tests/verify-bios-post.py --help` beschreibt die expliziten Fixture-/PPM-/RAM-/Report-/ROM-Eingaben; die archivierte private Fixture enthÃ¤lt nur eigene leere Testplatten und den eigenen CLI/HLT-Bootsektor. Die fertige Installationsaufnahme wird nicht ungefragt neu aufgenommen.


## P54C-Inventur im 8942er-Produkt-PC

Der eigene [DOSE-AT-P54C-PC](P54C-PC.md) ersetzt vor dem ROM-Mapping ausschließlich seine zwei eigenen Logo-/CPU-Inventurstrings innerhalb der unveränderten Bytekapazitäten und NUL-Grenzen. POST/E9/VGA zeigen P54C B5 / DOSE AT subset. Firmwarecode, ROM-Pointer, Version und öffentliche BIOS-Konstruktor-ABI bleiben erhalten. Das gewöhnliche 486-ROM ist gegen einen unabhängig aus fünf eingefrorenen 8870er-Quellen erstellten 64-KiB-Hashanker bytegleich geprüft. Kein fremdes ROM oder versteckter CPU-Dienst.
