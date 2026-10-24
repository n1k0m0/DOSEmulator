# DOSEmulator

Modularer PC-Emulator in C#/.NET 10 für geplante 486-, Pentium- und Pentium-MMX-Systeme. Grafik und Overlay-Bedienung basieren auf einer eigenständigen SharpPixels-Kopie aus dem C64Emulator.

**FreeDOS 1.4 bootet über IDE und Diskette bis zur interaktiven Kommandozeile.** Der Gast kann in privaten Overlays Dateien anlegen, kopieren, umbenennen und löschen; Änderungen bleiben nach einem Kaltstart erhalten. Kernel, FreeCOM und Bootcode stammen unverändert aus den offiziellen Medien. Nur die FreeDOS-Startkonfiguration privater Kopien ist vereinfacht.

**Die vorhandenen deutschen MS-DOS-6.22-Disketten installieren jetzt über das originale Setup auf eine neue private Festplatte.** Sieben Kaltstarts prüfen Partitionierung, Formatierung, beide Diskettenwechsel, HDD-Boot und persistente Dateioperationen; die vom Setup erzeugten Startdateien bleiben erhalten. Auch Microsofts `FORMAT.COM` formatiert eine private 1,44-MiB-Diskette über FDC/DMA2, die nach Kaltstarts lesbar und beschreibbar ist. Das geprüfte Setup-Profil lädt keine HIMEM-/EMM386-Speichermanager. Vollständige Codepage-Fontdarstellung, Protected Mode und Windows 95 bleiben offen.

Eine optionale **PS/2-Zweitastenmaus** arbeitet über KBC-Aux/IRQ12 und eigene BIOS-Dienste. Der echte CuteMouse-Treiber ist unter FreeDOS und installiertem MS-DOS 6.22 mit Bewegung, Tasten, Entladen/Neuinstallieren sowie Text/VGA-13h/VGA-12h geprüft. Im Hardwaremenü oder mit `mouse=ps2-two-button` einschalten; dies startet die Maschine neu. Klick im Gast fängt die Hostmaus, **Strg+Alt**, Menü und Fokusverlust geben sie samt Gasttasten frei. Relative Eingabe/Freigabe sind getestet; die automatische native Abnahme prüft Gastpfad und Darstellung, keinen physischen Betriebssystem-Mausfang. Details und Grenzen: [PS/2-Maus](docs/hardware/PS2-Mouse.md).

VGA-Grafik ist jetzt für Modus 13h (320×200/256 Farben) und Modus 12h (640×480/16 Farben) mit echten DOS-COM-Programmen geprüft. Auflösung und Palette wechseln durch den Gast; das SharpPixels-Fenster übernimmt sie live. Register-/BIOS-Sonderfunktionen bleiben begrenzt.

Der PC Speaker erzeugt Ton über PIT-Kanal 2/Port 61h bis zur Windows-Audioausgabe. Optional spielt ein begrenzter SB2-DSP-Pfad 8-Bit-Mono-PCM über DMA1/IRQ5 ab, jetzt auch High-Speed-Ausgabe mit Einzel-/Auto-Init und besonderem Reset-Abbruch. Ein OPL2-Entwicklungspfad ergänzt neun FM-Kanäle, Timer, Hüllkurven und Wellenformen; alle drei Quellen sind gleichzeitig im DOS-Gast geprüft. FM-Portzugriffe verbrauchen virtuelle ISA-Buszeit, sodass klassische Verzögerungen mit sechs/35 Statuslesezugriffen funktionieren. Ein eigener Gast prüft außerdem timergetriebenes CSM-Keying aller 18 Operatoren. **AUDIO SETTINGS** bietet Mute sowie getrennte Master-/Speaker-/Digital-/FM-Pegel. Rhythmus, vollständige CSM-/Chipgenauigkeit, ADPCM und weitere SB2-Funktionen bleiben offen.

![FreeDOS im DOSEmulator](docs/screenshots/freedos-prompt.png)

## Starten

Voraussetzung: Windows x64 mit .NET SDK 10.0.401 oder kompatiblem 10.0-Patchstand.

- `run.cmd` startet nach dem Build den Emulator mit dem eigenen Testmedium.
- **`run-freedos.cmd` startet FreeDOS über IDE.** `run-freedos.cmd -Writable` verwendet ein persistentes privates Overlay.
- **`run-freedos-floppy.cmd` startet FreeDOS über den FDC.** `run-freedos-floppy.cmd -Writable -TwoDrives` öffnet zwei Diskettenlaufwerke mit getrennten persistenten Overlays.
- Beim ersten Start wird das jeweilige offizielle FreeDOS-Paket geladen und anhand seiner veröffentlichten SHA-256-Prüfsumme geprüft. Basisimages bleiben immer schreibgeschützt.
- Menü: **Strg+Alt+F10**, Vollbild: **Strg+Alt+F11**.
- Im Menü mit Pfeiltasten, Enter und Esc navigieren. PS/2-Maus im Hardwaremenü einschalten; Gasttreiber muss im DOS geladen werden.
- Filter, Upscaling, Seitenverhältnis und VSync wirken auf die Hostdarstellung.
- Im Hardwaremenü: RAM, Diskettenlaufwerkstypen, Bootgerät und optionale Soundkarte ändern; diese Änderungen starten die Maschine neu. Verfügbar sind Speaker, AdLib-OPL2-Entwicklungspfad, SB2-DSP und SB2-DSP mit OPL2. Der DSP verwendet fest 220h/IRQ5/DMA1, FM 388h/389h und im Kombiprofil den Alias 228h/229h. Ein Laufwerkstypwechsel setzt dazu passende oder ausgeworfene Medien voraus.
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
.\scripts\test.ps1 -SkipBuild
.\scripts\smoke-gui.ps1 -SkipBuild
.\scripts\prepare-freedos.ps1 -Download
.\scripts\prepare-freedos-floppy.ps1 -Download
.\scripts\test-freedos.ps1 -SkipBuild
.\scripts\test-freedos-writes.ps1 -SkipBuild
.\scripts\test-freedos-floppy.ps1 -SkipBuild
.\scripts\test-floppy-format.ps1 -SkipBuild -Gui
.\scripts\test-msdos622.ps1 -SkipBuild -Gui
# Mit einer lokal installierten DOS-Basis und dem zugehörigen privaten Overlay:
.\scripts\test-mouse.ps1 -SkipBuild -Gui
# Optional zusätzlich die installierte lokale DOS-HDD prüfen:
.\scripts\test-mouse.ps1 -SkipBuild -Gui -MsdosDisk 'C:\Pfad\private-blank-32m.img' -MsdosOverlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-msdos-format.ps1 -SkipBuild -Disk 'C:\Pfad\private-blank-32m.img' -Overlay 'C:\Pfad\installed-pristine.cow'
.\scripts\test-freedos-gui.ps1 -SkipBuild -WriteOverlay -Floppy
.\scripts\test-vga.ps1 -SkipBuild -Gui
.\scripts\test-speaker.ps1 -SkipBuild -Gui
.\scripts\test-sb2.ps1 -SkipBuild -Gui
.\scripts\test-sb2-highspeed.ps1 -SkipBuild -Gui
.\scripts\test-opl2.ps1 -SkipBuild -Gui
.\scripts\test-opl2-csm.ps1 -SkipBuild -Gui
```

Die Regressionstests benötigen normale lokale Named-Pipe-Rechte. Der GUI-Test startet einen eigenen kurzlebigen Emulatorprozess und speichert sechs OpenGL-Screenshots unter `artifacts`. Er öffnet keinen Browser. Screenshots müssen zusätzlich visuell geprüft werden.

**303 Regressionstests bestehen.** Die FreeDOS-Abnahmen prüfen unabhängige Kaltstarts über IDE und FDC, echte KBC-/IRQ1-Eingaben, `VER`, `DIR`, `TYPE`, identische Bildhashes und unveränderte Medien. Weitere Gastläufe prüfen Schreibpersistenz, COPY/REN/DEL/RD auf IDE und zwei Disketten sowie tatsächlichen Medienwechsel im laufenden FreeDOS. Neue Tests prüfen BIOS-Diskettenformatierung, bufferlose HDD-Verifikation und bildschirmgesteuerte Tastatur-/Medienfolgen. Die MS-DOS-Abnahme verwendet ausschließlich die vorhandenen privaten Originalmedien und einen neuen 32-MiB-Datenträger; ein separater Python-Prüfer kontrolliert Journale, FAT-Ketten und die installierten Systemdateien gegen die Quelle. Der vom Setup verlangte Neustart nach Partitionierung wird als expliziter Host-Kaltstart ausgeführt; automatische Gast-Hardwareresets sind damit nicht abgenommen.

VGA-Abnahmen vergleichen jeden Pixel beider Grafikmodi; Audioabnahmen prüfen PCM-Werte, Tonhöhe, normales und High-Speed-DMA, IRQ-/Resetverhalten, FM-Hüllkurven/Wellenformen, CSM-Impulsraten, klassische Port-Wartezugriffe, Mischung, Stille und getrennte Livepegel bis zum Windows-Provider. Die GUI begrenzt zeitliches Aufholen und verwendet einen kleinen Audiovorlauf; laufende Tonintervalle werden zusätzlich auf Pufferunter-/überlauf geprüft. Eigene COM-Gasttests benötigen NASM 3.02 und Python 3; die MS-DOS-Installation benötigt zusätzlich die lokalen Disketten. Keine Freigabe aller DOS-Anwendungen oder vollständiger Yamaha-/Creative-Chipvergleich.

Alle Start-/Testskripte verwenden den gemeinsamen Build unter `artifacts/m1-build`. Damit bleibt ein bereits geöffnetes älteres Fenster aus `src/DOSEmulator/bin` unberührt. Der Name des Ausgabeverzeichnisses stammt aus der ersten M1-Abnahme.

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

M1 ist abgenommen. M2 besitzt jetzt zusätzlich die geprüfte MS-DOS-6.22-HDD-Installation, originale Diskettenformatierung und PS/2-Maus mit echtem DOS-Treiber. Als Nächstes folgen VGA-Font-/Codepagepfad, koordinierte Gastresets und der übrige OPL2-/SB2-Umfang. Physischer Host-Mausfang bleibt als manuelle Abnahme offen. Danach folgen M3–M8 mit Protected Mode, Pentium, Windows 95, Netzwerk und Voodoo. Die autorisierte stündliche Fortsetzung bleibt aktiv.

Der private Ordner `Disks and CDs (DO NOT PUT INTO GITHUB)` sowie Betriebssystem-, Spiel- und ROM-Images sind in `.gitignore` ausgeschlossen. Originalmedien werden nicht verändert. Eigene BIOS-Firmware steht unter `firmware/reference-bios.asm` und den dort eingebundenen INC-Dateien; das reproduzierbar erzeugte C#-Image liegt bei. NASM 3.02 ist nur zum Neuübersetzen geänderter Firmwarequellen und der eigenen Gast-Testprogramme erforderlich.
