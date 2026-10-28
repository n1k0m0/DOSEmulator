# Windows 95: vorhandene Grundlage und offene Integration

Historischer Abgleich vom 27. Oktober 2026 auf dem unveränderten akzeptierten11563er-Starter. Inzwischen ist der [eigene DOS-CD-Treiber mit originalem MSCDEX 2.23](hardware/DOS-CDROM-DRIVER.md) mit11642Tests integriert; die fünf VM-Module und der folgende CPU-/Firmware-/Geräteabgleich bleiben bytegleich11563. Das vom Benutzer beigefügte Gemini-Review wurde durch drei Agenten gegen CPU, Firmware, Geräte und historische Microsoft-Dokumentation geprüft. Es ist keine neue Windows-Abnahme. **Windows 95 RTM ist weiterhin nicht tatsächlich installiert und abgenommen; M4–M8 bleiben offen.**

Der spätere [eigene PCI-BIOS-/BIOS32-Firmwarepfad](hardware/PCI-BIOS.md) ist inzwischen auf dem zusätzlichen festen Profil mit 11819 Tests, acht DOS-/Original-EMM386-Starts und 16 vollen Fortsetzungen integriert. Dies ändert die VM-Buildidentität; die folgenden 11563er-Prüfzahlen bleiben historisch. Die eigentliche Windows-Installation/Originaltreiberabnahme bleibt offen.

## CPU und Speicher

CR2, #PF-P/W/U-Codes, Ring0/3- und V86-Rechte, CR0.WP/TLB, TSS-Ringstacks, I/O-Bitmap sowie CALL/JMP-/Gate-/NT-IRET-Taskwechsel sind implementiert und gezielt geprüft. Alle genannten Befehle besitzen Decoderpfade: INVLPG, CLTS, SMSW, LMSW, CMPXCHG, XADD, BSF, BSR und CPUID. Ein manueller Kontextwechsel ist Gastsoftware über diese Architekturzustände, kein zusätzlich fehlender CPU-Befehl.

Das Produktprofil bleibt ausdrücklich P54C B5 subset mit CPUID0135h und CR4.TSD/DE. PSE/VME/MCE und weitere unimplementierte MSRs werden nicht nachträglich beworben. Aus der Checkliste folgt weder die Notwendigkeit eines vollständigen Pentium-Modells vor dem ersten Windows-Start noch ein neuer konkreter Fehler. Die Behauptung eines unvermeidlichen VMM32-Hängers benötigt eine tatsächliche fehlerhafte Instruktion oder einen Fehlerrahmen. DOS-/EMM386-/DOS4GW-Erfolge ersetzen die noch fehlenden echten Windows-Ring3-/V86-/Paging-/Treiberfälle nicht.

Belege: [Paging](../src/DOSEmulator.Cpu.X86/X86Cpu.Paging.cs), [Hardwaretasks](../src/DOSEmulator.Cpu.X86/X86Cpu.HardwareTasks.cs), [Systembefehle](../src/DOSEmulator.Cpu.X86/X86Cpu.System.cs), [Pagingtests](../tests/DOSEmulator.Tests/PagingTests.cs) und [Tasktests](../tests/DOSEmulator.Tests/HardwareTaskTests.cs).

## Firmware, Ressourcen und Datenträger

| Schnittstelle | Aktueller Stand und Folge |
| --- | --- |
| PCI-Konfiguration | Mechanism1 über CF8/CFC ist vorhanden; der echte POST programmiert Host/PIIX/S3 durch OUTs. |
| PCI-BIOS/BIOS32 | Auf dem zusätzlichen PCI-BIOS-Profil inzwischen echte INT1A-/FE6E-/BIOS32-/FAR-Dienste mit Config- und benannten Ressourcenwirkungen. Ältere Profile behalten ihre bisherige Firmware. Der konkrete DOS-/VCPI-Nachweis ist kein Windows-Erkennungsnachweis. |
| PnP-BIOS/ISA-PnP | Gegenwärtig nicht implementiert. System-PnP-BIOS und die Isolation/Ressourcenvergabe konkreter ISA-PnP-Karten sind unterschiedliche Schnittstellen. Sie werden nicht durch einen bloßen Portdecoder oder eine Signatur ersetzt. ISA-PnP bleibt für die später vorgesehenen PnP-Karten erforderlich. |
| APM | Installation Check INT15/5300 liefert AH86/CF; keine APM-Werbung. APM bleibt laut Konzept profilabhängig. Ein regulärer Windows-Shutdown muss unabhängig davon tatsächlich nachgewiesen werden; eine automatische Stromabschaltung ist eine eigene Hardwarewirkung. |
| Festplatten | Übersetztes CHS mit 16/32/64/128/255 Köpfen und AH41/42/43/48 sind bereits vorhanden. Es handelt sich um eine benannte Teilmenge, keine vollständige EDD3- oder sämtliche AH41–48-Abnahme. Kapazität, DAP, Segmentgrenzen und ganze ATA-Wirkungen werden geprüft. |

Microsoft beschreibt ausdrücklich eine Legacy-Erkennung ohne PnP-BIOS. Ein vollständiges PnP-BIOS ist deshalb keine allgemeine Installationsvoraussetzung. [Historische Microsoft-Dokumentation zur Erkennung](https://techshelps.github.io/MSDN/DNWIN95/HTML/S6F26.HTM). Dass APM nach einer Windows-95-Installation fehlen kann, ist ebenfalls dokumentiert. [Microsoft Q135136](https://jeffpar.github.io/kbarchive/kb/135/Q135136/).

Die 504-MiB-Grenze entsteht aus der unübersetzten gemeinsamen CHS-Grenze. Microsoft nennt unter anderem Geometrieübersetzung oder LBA als Wege darüber hinaus. Daraus folgt kein pauschaler Zwang zu allen EDD-Funktionen und keine zwangsläufige Dateisystemkorruption bei jeder größeren Platte. Bei uns existieren Übersetzung und die genannten erweiterten Dienste bereits. [Microsoft Q126855](https://ftp.zx.net.nz/pub/archive/ftp.microsoft.com/MISC/KB/en-us/126/855.HTM).

## CD, Grafik, Maus und DMA

- **ATAPI:** Der ursprüngliche SFF-8020i-PIO-Teilumfang enthält READ10/12, echte 2048-Byte-Blöcke, IRQ, Sense/UA und volle Fortsetzung. Der beim ursprünglichen Abgleich noch fehlende DOS-CD-Treiber-/MSCDEX-Pfad ist inzwischen [mit einem eigenen festen SYS und originalem MSCDEX 2.23](hardware/DOS-CDROM-DRIVER.md) im benannten Referenzumfang geprüft. Vollständiges ATAPI und der originale Windows-CD-Treiber bleiben offen.
- **Grafik:** S3 besitzt PCI/Bank/LFB/8bpp, aber noch keine High-Color-/Hardwarecursor-/Beschleuniger-/Originaltreiberabnahme. Insbesondere liefert der Renderer für nicht unterstützte Enhancedformate derzeit Schwarz. VBE2/3 allein behebt das nicht. ET4000 besitzt bereits eine begrenzte VBE1.2-Schnittstelle mit 800×600 und 1024×768 in 8bpp. VBE-Funktionen und Farbtiefen dürfen nur passend zum jeweiligen Adapter angeboten werden.
- **PS/2:** KBC-Quellmarkierung, IRQ12, Standardkommandos, Dreibytepakete, Stream/Remote, virtuelle Abtastfristen und FIFO-Backpressure existieren. Elektrische Bit-/Paritypräzision und vollständige 8042-Latenzen sind nicht modelliert. Eine Windows-Treiberverträglichkeit muss am echten Gast gezeigt werden. Der ausdrücklich verschobene native Maus-/Fokusnachweis bleibt unberührt.
- **8237:** Auto-Init mit Basisreload, TC, Masken und Byte-/Wortkanälen ist vorhanden. Vollständige Zweicontroller-Kaskadenarbitration/HRQ/HLDA bleibt begrenzt. ATAPI-PIO benutzt diesen Controller nicht; PIIX-IDE-Busmaster ist ein eigener PCI-Pfad. Diese Grenzen bleiben offen und werden nicht allein aus der allgemeinen Checkliste als konkreter Setupfehler ausgegeben.

Microsoft dokumentiert kartenspezifische Windows-Treiber wie VGA.DRV/S3.DRV und unterschiedliche adapterabhängige Bedingungen für hohe Modi. **Folgerung für unseren Plan:** VBE2/3 ist keine pauschale Windows-95-SVGA-Pflicht; entscheidend ist der gewählte Treiber mit seinen tatsächlichen Register-, Modus- und Beschleunigeranforderungen. Die höheren S3-Farbtiefen und vom Treiber benötigte 2D-Funktion bleiben verbindliche offene Produktarbeit. [Microsoft Q124267](https://jeffpar.github.io/kbarchive/kb/124/Q124267/).

## Setup und Abnahmereihenfolge

Die Empfehlung `setup.exe /i` wird nicht als Windows-95-Vertrag übernommen. Microsoft dokumentiert `/p` als Übergabe an den Detection Manager: `/p b` schaltet auf Nachfrage vor jedem Erkennungsmodul, `/p i` unterdrückt die PnP-BIOS-Meldung, `/p l=<n>` steuert die Protokollierung. Das sind gezielte Diagnoseoptionen, keine normale Abnahme mit abgeschalteter Erkennung. [Microsoft Q128400](https://jeffpar.github.io/kbarchive/kb/128/Q128400/).

1. Den bereits erreichten DOS-/HIMEM-/Original-EMM386-/DOS4GW-Stand erhalten. DOS unter EMM386 ist ein V86-Gast unter einem Protected-Mode-Monitor; daraus wird kein Protected-Mode-DOS-Kernel oder Windows-Nachweis abgeleitet.
2. Der benannte feste DOS-CD-ROM-/Original-MSCDEX-Pfad ist inzwischen tatsächlich geprüft: Laufwerksbuchstabe R:, ISO-Verzeichnis-/Datei-/Seek-/Kopierzugriffe, echte Schreibablehnung sowie16volle Fortsetzungen. Weiterer CD-Treiber-/ATAPI-Umfang bleibt getrennt; [Vertrag und Nachweise](hardware/DOS-CDROM-DRIVER.md).
3. Die eigene PCI-BIOS-/BIOS32-Firmware ist mit echten DOS-/Original-Manageraufrufen integriert. Den zusammenhängenden DOS-/HIMEM-/EMM386-/CD-/Firmwarepfad nun für die tatsächliche RTM-Installation prüfen; eine zusätzliche Windows-Manager-/Treiberabnahme folgt erst im echten Windows-Gast.
4. Windows 95 RTM auf neuen privaten Medien tatsächlich installieren. Den Standard-VGA-Start als ersten Diagnosepunkt behandeln; er erfüllt noch nicht die vollständige S3-/SVGA-Abnahme. Erkennung mit tatsächlichen Logs und bei Fehlern CS:EIP/Opcode/CR0/CR2/CR3/CPL/VM/Fehlerrahmen festhalten.
5. Originale Treiber, höhere S3-Modi/Farbtiefen und benötigte Beschleunigerwirkungen, Sound, Maus und IDE unter Windows prüfen; mehrere reguläre Starts und Shutdowns sowie volle Fortsetzung bei Windows-Geräteaktivität nachweisen. Die ursprüngliche 100-/133-MHz-/32-MiB-Matrix bleibt offen/verbindlich.

Windows 3.11 Enhanced Mode kann bei einem konkreten Befund eine zusätzliche Diagnose sein. Er wird nicht als neuer obligatorischer Umweg oder Ersatz für den Win95-Nachweis eingeführt. Es werden keine M4–M8-Kriterien verkleinert.

## Nachweis dieses Abgleichs

Auf den unveränderten 11563er-Modulen erneut **178 gezielte Firmware-/PCI-Regressionen**, alle bestanden: fünf CHS-/EDD-Kapazitäten, 112 ATA-Bündel-/Segmentfälle, 58 Metadaten-Grenzfälle, zwei tatsächliche CPU-CF8/CFC-Fälle und ein tatsächlicher S3-POSTfall. Diese Zahlen werden nicht als neue Vollsuite oder DOS-Starts gezählt.

Zusätzlich zwölf eigene kontrollierte BIOS-Serviceproben nach wirklichem eigenem POST/Demo: 486/DOSE VGA, aktuelles P54C-/PIIX-/S3-/ATAPI-Profil und ET4000. Die Proben betreten den echten Real-Mode-BIOS-Service, führen seine Gastinstruktionen und IRET zum eigenen HLT aus. Beobachtet: PCI-BIOS nicht angeboten, APM AH86/CF, VBE nur bei ET4000 als Version0102h/AX004F und EDD-Check AH30/BXAA55/CX1/CF0. In E0000h–FFFFFh wurde auf den eigenen ROM-/Open-Bus-Abbildungen keine BIOS32-/PnP-Signatur gefunden. Dies sind synthetische Servicekontrollen, kein DOS-CD-Treiber- oder Windows-Start.

Privater Gesamtnachweis: `artifacts/tests/win95-readiness-release-path.txt`. Produktcode, alle 64 Release-Dateien, ROMs und Snapshotidentität bleiben unverändert. Keine Originalmedien oder persönlichen Spiel-COWs geöffnet; keine Windows-Eingaben/Fokusversuche, Aufnahmen, Hostendpunkte, C64-Änderungen, Commits oder Pushes. Der stündliche M0–M8-Gesamtauftrag bleibt aktiv.
