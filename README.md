# DOSEmulator

Modularer PC-Emulator in C#/.NET 10 für geplante 486-, Pentium- und Pentium-MMX-Systeme. Grafik und Overlay-Bedienung basieren auf einer eigenständigen SharpPixels-Kopie aus dem C64Emulator.

**FreeDOS 1.4 bootet über IDE und Diskette bis zur interaktiven Kommandozeile.** Der Gast kann in privaten Overlays Dateien anlegen, kopieren, umbenennen und löschen; Änderungen bleiben nach einem Kaltstart erhalten. Kernel, FreeCOM und Bootcode stammen unverändert aus den offiziellen Medien. Nur die FreeDOS-Startkonfiguration privater Kopien ist vereinfacht.

**Die vorhandenen deutschen MS-DOS-6.22-Disketten installieren über das originale Setup auf eine neue private Festplatte.** Sieben Kaltstarts prüfen Partitionierung, Formatierung, beide Diskettenwechsel, HDD-Boot und persistente Dateioperationen; die vom Setup erzeugten Startdateien bleiben erhalten. Auch Microsofts `FORMAT.COM` formatiert eine private 1,44-MiB-Diskette über FDC/DMA2, die nach Kaltstarts lesbar und beschreibbar ist. Das ursprüngliche Setup-Profil bleibt erhalten. Eine gesonderte private Konfiguration bootet nun das originale HIMEM.SYS mit vollständigem Speichertest und geprüften XMS-Transfers. Descriptor-Caches, PE16/PE32 und Rückkehr nach DOS sind unter beiden DOS-Systemen geprüft. Neu sind [4-KiB-Paging/TLB, V86 und originales EMM386/NOEMS](docs/hardware/X86-PAGING-V86.md) mit grundlegenden VCPI-Speicherprüfungen über IDE und FDC. [Echte VCPI-Protected-Clients und EMS-Bankwechsel](docs/hardware/X86-VCPI-EMS.md) sind mit originalem EMM386/RAM bei 4/8/16 MiB zusätzlich geprüft. [Originales DOS/4GW Professional 1.95 und Doom II](docs/hardware/X86-DOS4GW.md) spielen nun eine eigene kurze 32-Tick-Demo mit geprüften VGA-Bildern ab und kehren unter vier Speichermanager-Konfigurationen zu DOS zurück. [16-/32-Bit-Call-Gates mit TSS-Stacks, Parametern und äußerem RETF](docs/hardware/X86-CALL-GATES.md) sind zusätzlich unter beiden DOS-Systemen mit/ohne eigenes Paging geprüft. [16-/32-Bit-Hardware-Tasks und Task-Gates](docs/hardware/X86-HARDWARE-TASKS.md) prüfen zusätzlich Save/Load, Busy/Backlink/NT/TS und reparierte Fehler nach dem Task-Commit. [LOCK/CMPXCHG/XADD und Alignment-Check](docs/hardware/X86-LOCK-ALIGNMENT.md) sind ebenfalls mit echten Fehler-/Wiederholungspfaden geprüft. [486-Debugregister, Breakpoints und Einzelschritt](docs/hardware/X86-DEBUG.md) besitzen nun eigene DOS-Gastnachweise; originales Microsoft DEBUG.EXE führt zwei echte T-Schritte mit/ohne EMM386 aus. [Acht tatsächliche 80-Bit-x87-Register, Stack/Tags und Save/Restore](docs/hardware/X86-X87-STATE.md) sind nun unter MS-DOS/FreeDOS und EMM386 mit unabhängigen Rohdatenprüfungen abgenommen. [Elementare 80-Bit-Arithmetik mit reellen/Integer-Konvertierungen und expliziter Rundung](docs/hardware/X86-X87-NUMERIC.md) besteht zusätzlich 5696 unabhängige Zahlenfälle und 16 DOS-Kaltstarts. [FERR/IGNNE, Port F0/IRQ13 und tatsächliche #MF-Rückkehrgäste](docs/hardware/X86-X87-ERRORS.md) bestehen inzwischen 16 weitere Kaltstarts. [Geordnete/ungeordnete x87-Vergleiche und FTST](docs/hardware/X86-X87-COMPARE.md) bestehen zusätzlich 8922 native Referenzfälle und 16 DOS-Kaltstarts. [FRNDINT mit integralen 80-Bit-Ergebnissen](docs/hardware/X86-X87-ROUND.md) besteht zusätzlich 8160 native Zahlenfälle und 16 DOS-Kaltstarts mit 24 tatsächlichen MF-Rückkehrvorgängen. [FSQRT mit ganzzahliger Wurzel, PC24/53/64 und vier Rundungsarten](docs/hardware/X86-X87-SQRT.md) besteht zusätzlich 14176 native Zahlenfälle, 16 DOS-Kaltstarts und 24 tatsächliche MF-Rückkehrvorgänge. [Alle sieben x87-Konstanten mit RC und vollständiger 80-Bit-Präzision](docs/hardware/X86-X87-CONSTANTS.md) bestehen zusätzlich 15120 native Fälle und 16 DOS-Kaltstarts mit 24 tatsächlichen MF-Rückkehrvorgängen. Sound, längere Spielpfade, umfassende CPU-Präzision, vollständiges x87 und Windows 95 bleiben offen.

Eine optionale **PS/2-Zweitastenmaus** arbeitet über KBC-Aux/IRQ12 und eigene BIOS-Dienste. Der echte CuteMouse-Treiber ist unter FreeDOS und installiertem MS-DOS 6.22 mit Bewegung, Tasten, Entladen/Neuinstallieren sowie Text/VGA-13h/VGA-12h geprüft. Im Hardwaremenü oder mit `mouse=ps2-two-button` einschalten; dies startet die Maschine neu. Klick im Gast fängt die Hostmaus, **Strg+Alt**, Menü und Fokusverlust geben sie samt Gasttasten frei. Relative Eingabe/Freigabe sind getestet; die automatische native Abnahme prüft Gastpfad und Darstellung, keinen physischen Betriebssystem-Mausfang. Details und Grenzen: [PS/2-Maus](docs/hardware/PS2-Mouse.md).

VGA-Grafik ist jetzt für Modus 13h (320×200/256 Farben) und Modus 12h (640×480/16 Farben) mit echten DOS-COM-Programmen geprüft. Auflösung und Palette wechseln durch den Gast; das SharpPixels-Fenster übernimmt sie live. Register-/BIOS-Sonderfunktionen bleiben begrenzt.

**CP850 mit äöü/ÄÖÜ/ß und Linienzeichen wird vom echten DISPLAY.SYS/MODE aus der Gastdatei EGA.CPI geladen.** Alle 256 Glyphen sind für 8, 14 und 16 Pixel Zeichenhöhe vollständig gegen diese privaten Fontdaten geprüft. Acht VGA-Fontblöcke, A/B-Auswahl sowie 25/28/43/50 Textzeilen sind umgesetzt. Microsofts `MODE CON LINES=25/43/50` benötigt ANSI.SYS; dessen Gastpfad und Livewechsel sind in einer gesonderten privaten Konfiguration geprüft. 28 Zeilen verwendet der eigene BIOS-Test. Weitere BIOS-Funktionen sind offen. [Umfang und Reproduktion](docs/hardware/VGA.md).

**Die eigenen ROM-Ausgangsfonts decken nun alle 256 CP437-Videozeichen ab**, einschließlich echter Kleinbuchstaben, Akzenten, Symbolen, einfachen/doppelten Rahmen und Blöcken. Die selbst entworfenen 8×8/14/16-Schriften sind unter FreeDOS und MS-DOS mit acht und neun Pixel breiten VGA-Zellen geprüft. 20 Gaststarts und drei native Fenster belegen Laden, alle Bildpixel und die bytegenaue Wiederherstellung des vorherigen DOS-Fonts. Es sind eigene Referenzschriften; das Aussehen eines IBM-ROMs wird nicht nachgebildet. Die Menüschrift bleibt getrennt.

**Textseiten und Cursor sind unter MS-DOS/FreeDOS geprüft:** acht Seiten bei 80×25, vier bei den getesteten 80×50-Zeilen-Profilen, getrennte Cursorpositionen, Seitenwechsel, rechteckiges Scrollen und BIOS-Stringausgabe. Der Cursor verwendet die Zeichenfarbe und einen eigenen virtuellen Blinktakt. Die nativen 25-/50-Zeilenbilder sowie das Menü wurden visuell geprüft.

**Grafikfonts und BIOS-Zeichenausgabe in 12h/13h sind ergänzt:** tatsächliche Gastfontzeiger, planarer XOR, Vorder-/Hintergrundfarben, Scrollen, Teletype, Strings und Rücklesen aus den wirklichen Grafikpixeln. Alle 256 CP850-Glyphen sind unter MS-DOS für 8/14/16 Pixel Höhe in beiden Auflösungen pixelgenau geprüft. MS-DOS und FreeDOS wechseln mehrfach zwischen Grafik und regulärem DOS-Prompt. Drei native Fenster mit Menü/Filtern wurden geprüft. Vollständige VGA-/Windows-Kompatibilität bleibt offen.

**BIOS-Cursor-Skalierung AH12/BL34h ist ergänzt.** Die angeforderte Form bleibt über AH03 lesbar, während AH01 die entsprechenden nativen CRTC-Zeilen programmiert. Ein-/Ausschalten, unterschiedliche Zeichenhöhen, Kontrollbits und DOS-Rückkehr sind unter beiden DOS-Systemen geprüft. Die eigene Referenzregel und ihre Grenzen stehen in der [VGA-Dokumentation](docs/hardware/VGA.md).

Der PC Speaker erzeugt Ton über PIT-Kanal 2/Port 61h bis zur Windows-Audioausgabe. Optional spielt ein begrenzter SB2-DSP-Pfad 8-Bit-Mono-PCM über DMA1/IRQ5 ab, jetzt auch High-Speed-Ausgabe mit Einzel-/Auto-Init und besonderem Reset-Abbruch. Direct-ADC und normale/High-Speed-Aufnahme sind zusätzlich unter MS-DOS 6.22 und FreeDOS geprüft: echte DMA-Schreibvorgänge mit definiertem virtuellem Eingang und anschließender Wiedergabe; ein Host-Mikrofon wird nicht geöffnet. Ein OPL2-Entwicklungspfad ergänzt neun FM-Kanäle, Timer, Hüllkurven und Wellenformen; alle drei Quellen sind gleichzeitig im DOS-Gast geprüft. FM-Portzugriffe verbrauchen virtuelle ISA-Buszeit, sodass klassische Verzögerungen mit sechs/35 Statuslesezugriffen funktionieren. Ein eigener Gast prüft außerdem timergetriebenes CSM-Keying aller 18 Operatoren. **AUDIO SETTINGS** bietet Mute sowie getrennte Master-/Speaker-/Digital-/FM-Pegel. Portbasis 220h/240h und IRQ3/5/7/9 lassen sich im Hardwaremenü umstellen; DMA bleibt bei dieser SB2-Karte 1. Der Wechsel startet die Maschine mit erhaltenen Medien neu, Pegel bleiben live. Creative ADPCM (2/3/4 Bit, Einzel-/Auto-Init), alle fünf OPL2-Schlaginstrumente und ein CSM-TL-Latch sind jetzt im dokumentierten Samplezeitmodell unter MS-DOS/FreeDOS geprüft. Exakte Originalchip-Pipelines, Host-Eingänge, MIDI/Gameport und weitere Kartentypen bleiben offen.

![FreeDOS im DOSEmulator](docs/screenshots/freedos-prompt.png)

**Gastneustarts über KBC und Port 92h sind mit FreeDOS und MS-DOS 6.22 geprüft.** Im Menü **RESET / POWER** stehen Tastenkombination an den Gast, CPU-Reset mit erhaltenem RAM und Power-Cycle mit leerem RAM getrennt zur Verfügung. Gerätezeit und Medien bleiben beim CPU-Reset erhalten, Batterie-CMOS auch beim Power-Cycle. Das originale MS-DOS-Setup setzt seine Installation inzwischen mit eigenem Software-Neustart in einem Prozess fort. [Verhalten und Grenzen](docs/hardware/RESET.md).

[Gemessene RAM-/Shift-Optimierung](docs/PERFORMANCE.md): direkter RAM-Pfad und Bitoperationen; die kurze Doom-Referenzdemo benötigt lokal etwa 9–12 % weniger Hostzeit bei identischem Gastzustand und unveränderter virtueller Zeit.

## Starten

Voraussetzungen: Windows x64, **PowerShell 7.4 oder neuer** (`pwsh.exe`) und .NET SDK 10.0.401 oder kompatibler 10.0-Patchstand. Die CMD-Starter finden PowerShell über PATH, die normale Installation oder den Microsoft-Store-Alias. Ein direkter PS1-Aufruf benötigt ebenfalls PowerShell 7; bei einem älteren Host erscheint vor der Ausführung die erforderliche Mindestversion.

- `run.cmd` startet nach dem Build den Emulator mit dem eigenen Testmedium.
- **`run-doom2.cmd` öffnet den experimentellen manuellen Doom-II-Test** mit einer eigenen persistenten Kopie der bereits vorhandenen privaten DOS-/Doom-Abnahmemedien. Danach im DOS `doom2 -config doomtest.cfg -nosound` eingeben. [Anleitung und Grenzen](docs/DOOM2-TESTEN.md).
- **`run-freedos.cmd` startet FreeDOS über IDE.** `run-freedos.cmd -Writable` verwendet ein persistentes privates Overlay.
- **`run-freedos-floppy.cmd` startet FreeDOS über den FDC.** `run-freedos-floppy.cmd -Writable -TwoDrives` öffnet zwei Diskettenlaufwerke mit getrennten persistenten Overlays.
- Beim ersten Start wird das jeweilige offizielle FreeDOS-Paket geladen und anhand seiner veröffentlichten SHA-256-Prüfsumme geprüft. Basisimages bleiben immer schreibgeschützt.
- Menü: **Strg+Alt+F10**, Vollbild: **Strg+Alt+F11**.
- Im Menü mit Pfeiltasten, Enter und Esc navigieren. PS/2-Maus im Hardwaremenü einschalten; Gasttreiber muss im DOS geladen werden.
- Filter, Upscaling, Seitenverhältnis und VSync wirken auf die Hostdarstellung.
- Im Hardwaremenü: RAM, Diskettenlaufwerkstypen, Bootgerät und optionale Soundkarte ändern; diese Änderungen starten die Maschine neu. Verfügbar sind Speaker, AdLib-OPL2-Entwicklungspfad, SB2-DSP und SB2-DSP mit OPL2. **SOUND CARD RESOURCES** wählt 220h/240h und IRQ3/5/7/9 bei festem DMA1; Standard ist A220/I5/D1. FM verwendet 388h/389h und im Kombiprofil zusätzlich Basis+8/+9. Ein Laufwerkstypwechsel setzt dazu passende oder ausgeworfene Medien voraus.
- Im Medienmenü: IDE-Image mit oder ohne Overlay öffnen; Disketten A:/B: an Controller-Ruhepunkten einlegen oder auswerfen. FreeDOS erkennt den Medienwechsel. IDE-Wechsel startet die Maschine neu.
- Raw-IDE-Images lassen sich auch mit `--disk` oder Drag-and-drop öffnen. Schreibzugriffe benötigen `--overlay`; Disketten verwenden `--floppy-a`/`--floppy-b` mit optionalem `--floppy-a-overlay`/`--floppy-b-overlay`. CD-ROM/ATAPI ist noch offen.

```powershell
.\scripts\run.ps1
.\scripts\run-freedos.ps1
.\scripts\run-freedos.ps1 -Writable
.\scripts\run-freedos-floppy.ps1 -Writable -TwoDrives
.\scripts\run.ps1 -Disk 'C:\Pfad\disk.img'
.\scripts\run.ps1 -Profile '.\profiles\virtual486-dev-v1.json'
.\scripts\run.ps1 -Profile '.\profiles\virtual486-sb2-dsp-dev-v1.json' -Disk 'C:\Pfad\private-dos.img'
.\scripts\run.ps1 -Profile '.\profiles\virtual486-sb2-opl2-dev-v1.json' -Disk 'C:\Pfad\private-dos.img'
```

## Prüfen

```powershell
.\scripts\build.ps1
.\scripts\test-scripts.ps1 -Gui
.\scripts\test.ps1 -SkipBuild
.\scripts\smoke-gui.ps1 -SkipBuild
.\scripts\prepare-freedos.ps1 -Download
.\scripts\prepare-freedos-floppy.ps1 -Download
.\scripts\test-freedos.ps1 -SkipBuild
.\scripts\test-freedos-writes.ps1 -SkipBuild
.\scripts\test-freedos-floppy.ps1 -SkipBuild
.\scripts\test-floppy-format.ps1 -SkipBuild -Gui
.\scripts\test-msdos622.ps1 -SkipBuild -Gui
.\scripts\test-guest-reset.ps1 -SkipBuild -Gui -Install
# Mit einer lokal installierten DOS-Basis und dem zugehörigen privaten Overlay:
.\scripts\test-mouse.ps1 -SkipBuild -Gui
# Optional zusätzlich die installierte lokale DOS-HDD prüfen:
.\scripts\test-mouse.ps1 -SkipBuild -Gui -MsdosDisk 'C:\Pfad\private-blank-32m.img' -MsdosOverlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-msdos-format.ps1 -SkipBuild -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-vga-fonts.ps1 -SkipBuild -Gui -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-vga-text.ps1 -SkipBuild -Gui -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-vga-graphics-fonts.ps1 -SkipBuild -Gui -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-cp437-fonts.ps1 -SkipBuild -Gui -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-cursor-emulation.ps1 -SkipBuild -Gui -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-freedos-gui.ps1 -SkipBuild -WriteOverlay -Floppy
.\scripts\test-vga.ps1 -SkipBuild -Gui
.\scripts\test-speaker.ps1 -SkipBuild -Gui
.\scripts\test-sb2.ps1 -SkipBuild -Gui
.\scripts\test-sb2-highspeed.ps1 -SkipBuild -Gui
.\scripts\test-sb2-input.ps1 -SkipBuild -Disk "C:\Pfad\private-dos.img" -Overlay "C:\Pfad\pristine.cow"
.\scripts\test-sb2-resources.ps1 -SkipBuild -Gui -Disk "C:\Pfad\private-dos.img" -Overlay "C:\Pfad\pristine.cow"
.\scripts\test-opl2.ps1 -SkipBuild -Gui
.\scripts\test-opl2-csm.ps1 -SkipBuild -Gui
```

Die Regressionstests benötigen normale lokale Named-Pipe-Rechte. Der GUI-Test startet einen eigenen kurzlebigen Emulatorprozess und speichert sechs OpenGL-Screenshots unter `artifacts`. Er öffnet keinen Browser. Screenshots müssen zusätzlich visuell geprüft werden.

**876 Regressionstests bestehen.** Die FreeDOS-Abnahmen prüfen unabhängige Kaltstarts über IDE und FDC, echte KBC-/IRQ1-Eingaben, `VER`, `DIR`, `TYPE`, identische Bildhashes und unveränderte Medien. Weitere Gastläufe prüfen Schreibpersistenz, COPY/REN/DEL/RD auf IDE und zwei Disketten sowie tatsächlichen Medienwechsel im laufenden FreeDOS. Neue Tests prüfen BIOS-Diskettenformatierung, bufferlose HDD-Verifikation, VGA-Fontblöcke/-Geometrie/-Zustandsabfragen, vollständige eigene CP437-Fonts und Rahmenkanten, Textseiten/Strings/Cursor, Grafikfont-/Zeichen-/Scroll-/Lesedienste, Reset-/Power-Cycle-Verhalten und bildschirmgesteuerte Tastatur-/Medienfolgen. Die MS-DOS-Abnahme verwendet ausschließlich die vorhandenen privaten Originalmedien und einen neuen 32-MiB-Datenträger; separate Python-Prüfer kontrollieren Journale, FAT-Ketten, Systemdateien und sämtliche Text-/Grafikfont-Testbildpixel. Das originale Setup setzt seine Installation inzwischen mit seinem eigenen Software-Neustart in einem Prozess fort; KBC-/Port-92h-Hardware-Resets sind zusätzlich unter beiden DOS-Systemen geprüft.

VGA-Abnahmen vergleichen jeden Pixel beider Grafikmodi; Audioabnahmen prüfen PCM-Werte, Tonhöhe, normales und High-Speed-DMA, IRQ-/Resetverhalten, FM-Hüllkurven/Wellenformen, CSM-Impulsraten, klassische Port-Wartezugriffe, Mischung, Stille und getrennte Livepegel bis zum Windows-Provider. Die GUI begrenzt zeitliches Aufholen und verwendet einen kleinen Audiovorlauf; laufende Tonintervalle werden zusätzlich auf Pufferunter-/überlauf geprüft. Eigene COM-Gasttests benötigen NASM 3.02 und Python 3; die MS-DOS-Installation benötigt zusätzlich die lokalen Disketten. Keine Freigabe aller DOS-Anwendungen oder vollständiger Yamaha-/Creative-Chipvergleich.

Alle Start-/Testskripte verwenden standardmäßig den gemeinsamen Build unter `artifacts/m1-build`. `-OutputRoot` akzeptiert bei den entsprechenden Build-/Testskripten sowohl zum Repository relative als auch absolute Verzeichnisse; Benutzerdateipfade beziehen sich auf das aufrufende PowerShell-Verzeichnis. Hashprüfungen verwenden eine gemeinsame SHA-256-Funktion ohne Abhängigkeit von `Get-FileHash`. Fehlgeschlagene native Werkzeuge beenden das jeweilige Skript; die CMD-Starter geben Fehlercodes zurück. Damit bleibt ein bereits geöffnetes älteres Fenster aus `src/DOSEmulator/bin` unberührt. Der Name des Ausgabeverzeichnisses stammt aus der ersten M1-Abnahme.

`scripts/test-scripts.ps1 -Gui` prüft alle PowerShell-Dateien, Hash-/Pfadbehandlung, Hostauswahl einschließlich Store-Fallback, Fehlerweitergabe und einen echten selbstständig endenden `run.cmd`-Start mit privaten Einstellungen. Für denselben Starttest direkt: `run.cmd -NoBuild -SmokeTestDirectory "artifacts\mein-starttest"`; ein neues Verzeichnis verwenden. Der normale Aufruf `run.cmd` lässt das Emulatorfenster geöffnet.

Headless-Boot ohne OpenGL:

```powershell
dotnet .\artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll --help
dotnet .\artifacts\m1-build\Release\net10.0\DOSEmulator.Headless.dll --steps 100000
```

## Stand und weitere Umsetzung

- [Implementierungsstand mit Nachweisen und nächsten Schritten](docs/IMPLEMENTATION-STATUS.md)
- [Architektur- und Umsetzungskonzept M0–M8](docs/DOSEmulator-Konzept.md)
- [Private Disk-Overlays](docs/DISK-OVERLAYS.md)
- [FDC-Umfang und Grenzen](docs/hardware/FDC-82077.md)
- [VGA-Modi, Registerumfang und Grenzen](docs/hardware/VGA.md)
- [PS/2-Zweitastenmaus, CuteMouse-Gasttests und Grenzen](docs/hardware/PS2-Mouse.md)
- [PC Speaker, Hostaudio und Grenzen](docs/hardware/PC-SPEAKER.md)
- [SB2-DSP-/PCM-Pfad, gemeinsamer Mixer und Grenzen](docs/hardware/SB2-DSP.md)
- [OPL2-Timer, FM/CSM-Keying, Portzeiten und Genauigkeitsgrenzen](docs/hardware/OPL2.md)
- [Lizenzhinweise und SharpPixels-Herkunft](THIRD-PARTY-NOTICES.md)

M1 ist abgenommen. M2 besitzt die geprüfte MS-DOS-6.22-HDD-Installation, originale Diskettenformatierung, PS/2-Maus mit echtem DOS-Treiber, Gastresets, eigene vollständige CP437-Fonts sowie CP850 in mehreren Textseiten und VGA-Grafikmodi. BIOS-Cursor-Skalierung und virtuelle SB2-Aufnahme sind zusätzlich abgenommen. SB2-Port-/IRQ-Ressourcen sind jetzt einschließlich Hardwaremenü und 64 DOS-Kaltstarts geprüft. ADPCM, OPL2-Rhythmus und CSM-Pegel sind umgesetzt. Der native Windows-Eingabetest hat zudem einen GUI-Serialisierungsfehler behoben und den gesamten Mausfang-/Fokus-/DOS-Pfad erfolgreich geprüft. Die letzte kombinierte Mauswiederholung bleibt auf ausdrücklichen Benutzerwunsch bis zu einem neuen Auftrag offen. Umfang, Nachweise und Grenzen stehen in [der M2-Abnahme](docs/M2-ABNAHME.md). M3 besitzt inzwischen [Descriptor-Caches, echte PE16-/PE32-Wechsel und geschützte Fehlerpfade](docs/hardware/X86-PROTECTED.md), geprüft unter beiden DOS-Systemen. Originales HIMEM.SYS mit /TESTMEM:ON sowie XMS-Reservieren, Sperren, Kopieren und Freigeben sind in einer privaten MS-DOS-Konfiguration nachgewiesen. [LLDT/LTR und tatsächliche TSS-Interrupt-Stackwechsel mit äußerer Rückkehr](docs/hardware/X86-TSS.md) sind ebenfalls unter MS-DOS und FreeDOS nachgewiesen. [Paging/TLB, V86/I/O-Bitmap und originales EMM386 mit grundlegenden VCPI-Speicherfunktionen](docs/hardware/X86-PAGING-V86.md) sind nun geprüft. [VCPI-DE0Ch und echte EMS-Bankwechsel mit RAM](docs/hardware/X86-VCPI-EMS.md) bestehen weitere 20 Kaltstarts mit 4/8/16 MiB; der gemeinsame Build besteht jetzt 2325 Regressionen. [Originales DOS4GW und die kurze Doom-II-Referenzdemo](docs/hardware/X86-DOS4GW.md) bestehen acht weitere Starts mit/ohne HIMEM/EMM386; CPU-Erkennung und x87-Kontrollbefehle sind ergänzt. [FDC-Schreiben/Format und tatsächliche VDS-DMA-Puffer unter EMM386](docs/hardware/X86-FDC-VDS.md) bestehen zusätzlich 13 Kaltstarts, auch mit nicht zusammenhängenden EMS-Aufrufern und originalem Microsoft FORMAT. Ungeeignete interne EBDA-Puffer werden mit AH09 abgewiesen. [16-/32-Bit-Call-Gates](docs/hardware/X86-CALL-GATES.md) bestehen acht weitere MS-DOS-/FreeDOS-Kaltstarts und 33 negative Oracles. [Hardware-Tasks/Task-Gates](docs/hardware/X86-HARDWARE-TASKS.md) bestehen acht eigene DOS-Kaltstarts mit/ohne Paging, zwölf tatsächlichen Taskwechseln pro Start und 59 negativen Oracles. [LOCK und Alignment-Check](docs/hardware/X86-LOCK-ALIGNMENT.md) bestehen acht weitere DOS-Kaltstarts mit tatsächlichen UD-/AC-/PF-Rahmen und 70 negativen Oracles. [Debugregister/Breakpoints/TF](docs/hardware/X86-DEBUG.md) bestehen acht eigene Kaltstarts und 278 negative Oracles; originales Microsoft DEBUG.EXE zusätzlich sechs Starts ohne Manager und mit EMM386 NOEMS/RAM, mit korrekten Einzelschritt-Registern und 24 Gegenprüfungen. [x87-Zustand mit acht echten 80-Bit-Registern und vollständigen Save-/Restore-Abbildern](docs/hardware/X86-X87-STATE.md) besteht 16 weitere Kaltstarts unter beiden DOS-Systemen, originalem EMM386 und eigenen PE-/Paging-Varianten sowie 376 negative Oracles; der ursprüngliche FPU-Zustand wird bytegenau restauriert. [Elementare x87-Arithmetik und Zahlenkonvertierungen](docs/hardware/X86-X87-NUMERIC.md) sind mit 5696 getrennten nativen Referenzfällen und 220 Zahlenfällen je DOS-Start zusätzlich geprüft. Die echte Host-x87 dient ausschließlich der Prüfung; der Emulator rechnet mit Ganzzahlen. [FERR/IGNNE und tatsächliche IRQ13-/#MF-Fehler-/Rückkehrpfade](docs/hardware/X86-X87-ERRORS.md) besitzen nun 63 neue CPU-/Gerätefälle und 16 DOS-Kaltstarts. [FCOM/FUCOM/FTST und reelle/Integer-Speichervergleiche](docs/hardware/X86-X87-COMPARE.md) sind inzwischen mit NaNs, Leerstack, Status/Pop und 8922 unabhängigen nativen Fällen sowie 16 eigenen DOS-Kaltstarts geprüft. [FRNDINT mit allen vier Rundungsarten](docs/hardware/X86-X87-ROUND.md) ist mit 8160 nativen Zahlenfällen, 16 DOS-Kaltstarts und 24 tatsächlichen MF-/IRETD-Rückkehrvorgängen zusätzlich geprüft. [FSQRT mit ganzzahliger Wurzel, PC24/53/64 und vier Rundungsarten](docs/hardware/X86-X87-SQRT.md) besteht zusätzlich 14176 native Zahlenfälle, 16 DOS-Kaltstarts und 24 tatsächliche MF-Rückkehrvorgänge. [Alle sieben x87-Konstanten mit RC und vollständiger 80-Bit-Präzision](docs/hardware/X86-X87-CONSTANTS.md) bestehen zusätzlich 15120 native Fälle und 16 DOS-Kaltstarts mit 24 tatsächlichen MF-Rückkehrvorgängen. Allgemeine Spiel-/DOS-Extender-/VDS-Kompatibilität, umfassende CPU-/Task-/Fault-Präzision, vollständiges x87 und die übrigen M3-Geräte und M4–M8 mit Pentium, Windows 95, Netzwerk und Voodoo bleiben offen. Die autorisierte stündliche Fortsetzung bleibt aktiv.

Der private Ordner `Disks and CDs (DO NOT PUT INTO GITHUB)` sowie Betriebssystem-, Spiel- und ROM-Images sind in `.gitignore` ausgeschlossen. Originalmedien werden nicht verändert. Eigene BIOS-Firmware steht unter `firmware/reference-bios.asm` und den dort eingebundenen INC-Dateien; das reproduzierbar erzeugte C#-Image liegt bei. NASM 3.02 ist nur zum Neuübersetzen geänderter Firmwarequellen und der eigenen Gast-Testprogramme erforderlich.
