# DOSE AT mit P54C-B5-Teilmodell

Zusätzlich besitzt das eigene Resetprofil 11148 getrennte P54C-INIT-/CPURST-APIs mit historischer Integer-/roher x87-/TSC-Trennung und echten ursprünglichen DOS-/EMM386-Rückwegen. Bestehender Reset-/AT-Profilumfang bleibt erhalten. [CF9-/Resetvertrag](PIIX-RESET.md); keine vollständige P54C-/430FX-/Windowsabnahme.

Stand: 27. Oktober 2026. **8942/8942 Regressionen, normaler Release-Build mit 0 Warnungen/Fehlern.** Das benannte P54C-Teilmodell ist jetzt durchgängig in einen eigenen DOSE-AT-PC integriert: Profil, CLI, Hardwaremenü, Factory, Ownerwechsel, DOSEBios-Inventur und vollständige lokale Snapshots. Dieses Profil ist kein historisches 430FX-Board und keine Windows-95-Abnahme; M4 bleibt offen.

## Auswählbarer Umfang

| Eigenschaft | Vertrag |
| --- | --- |
| Plattform | `dose-p54c-at-dev-v1`, eigener DOSE AT mit den bestehenden ISA-/ATA-/FDC-/VGA-Geräten; kein verdrahteter PCI-Chipsatz. |
| CPU | `p54c-b5-subset-development`, festes `IntelP54cB5Subset`; Zielidentifikation Familie 5/Modell 2/Stepping 4, Signatur 0524h. Die begrenzten [B5](P54C-B5.md)-, [TSC](P54C-TSC.md)-, [MSR](P54C-MSR.md)-, [Stack](P54C-STACKS.md)- und [Debugverträge](P54C-DEBUG.md) bleiben maßgeblich. |
| Merkmale | Clockgebunden CPUID 0135h: FPU, DE, TSC, TSC-MSR-Schnittstelle, CX8. CR4 besitzt ausschließlich TSD/DE. Genau MSR10h ist implementiert; weitere reale P54C-MSRs liefern die dokumentierte Teilumfangsantwort #GP. PSE/VME/MCE bleiben unbeworben. |
| Zeit | Feste nominale 90 MHz. Derselbe eigene Scheduler treibt Befehle, REP, HLT, Fristen, Geräte und TSC. Vier Entwicklungstakte je Step/REP-Element bleiben erhalten; keine historische Befehlsclock-/Echtzeitzusage. Keine Host-Wanduhr/TSC/MSR/CPUID-Abfrage. |
| Konstruktion | CPU-Modell und TSC-Quelle/Frequenz werden vor Nutzung fest gebunden. Kein Austausch einer CPU nach dem Erzeugen des PCs. `UsesTimestampClock` prüft die tatsächliche Schedulerreferenz; Snapshots verlangen diese feste Verbindung. |
| Ressourcen | Vorhandene 4/8/16-MiB-RAM-, zwei ATA-/vier IDE- und zwei Floppypositionen sowie bisherige optionale Geräte/Ressourcenkollisionen. Keine automatische Verbindung zu Hostgeräten. |
| Kompatibilität | Alte Dateien/Standard-PCs bleiben 486 mit 33 MHz. Bestehende Zwei-/Drei-Argument-CPU-Konstruktoren behalten ihren bisherigen Umfang; quellenloser P54C bleibt CPUID 0101h. Eigene PC-/Profilnamen und PC-IDs bleiben erhalten. |

Die mitgelieferte Konfiguration liegt unter **profiles/dose-p54c-at-dev-v1.json** (8 MiB, DOSE VGA, CT1740). Im Hardwaremenü wechselt die CPU-Zeile das vollständige ID-/CPU-/Takt-Tupel und startet über den bestehenden Ownerpfad kalt neu. Fenster-/Status-/Profiltexte zeigen das ausgewählte Teilmodell; Headless-Berichte enthalten tatsächliches Modell, Frequenz, CR4 und TSC. Menücode und Ownerpfad sind geprüft, eine neue native Windows-/Maus-/GL-Bedienabnahme wurde nicht durchgeführt.

DOSEBios ersetzt ausschließlich für das neue Profil zwei eigene feste Inventurstrings innerhalb ihrer ursprünglichen Bytekapazitäten: Logo `P54C / AT` und `Configured CPU: P54C B5 / DOSE AT subset`. ROM-Pointer, NUL-Enden und Firmwarecode bleiben erhalten. Öffentliche BIOS-Konstruktor-ABI und der erzeugte 486-ROM bleiben bytegleich zum eingefrorenen 8870er-Stand; ein unabhängiger Altquell-Hashanker und der vollständige 64-KiB-Diff prüfen dies. Der eigene eingebaute historische 486-Bootsektor bleibt ein ausdrücklich synthetischer Testgast.

## Zustand und Ablehnung

Snapshotformat 1 bleibt an den identischen VM-Build gebunden. CPU-Modell/Frequenz sind feste Invarianten; alle 64 Bit einer gültigen WRMSR-TSC-Epoche bleiben restaurierbare Werte, keine willkürlich einzuschränkende Uhrzeit. Die Quelle wird durch die Factory an den eigenen neuen Scheduler gebunden. Erfassung/Laden erhalten Taktrest, Schedulerqueues, offene Geräteoperationen, rohe x87-Register und Audiointegratoren.

Vor jeder neuen Medienzweig-Veröffentlichung werden CPU-Modell, Frequenz, RAM und Schedulerfrequenz gegen die Definition aus dem bereits geschützten Medienmanifest geprüft. Der gleiche strikte Decoder wird für Vorprüfung und Branch verwendet. Vollständige Feld-/ROM-/Bus-/Ereignisprüfung und exakte Rückkodierung bleiben anschließend erforderlich. Zwölf wirklich manipulierte semantische IPC-Importe mit korrigierten Checksummen erhalten die aktive Maschine/Revision und lassen keinen Zielzweig zurück. Ein echter eigener 8870er-CLI-Import scheitert vor Zweiganlage; dessen Quelle bleibt unverändert.

72 neue Fälle: 31 Profil-/CLI-/Persistenz-/Ownerfälle und 41 vollständige PC-/Clock-/Reset-/REP-/HLT-/Peripherie-/Import-/ROM-Fälle. Eine eigene Sechs-Medien-Probe vergleicht vier neue RAW/COW-Platten und beide eigenen Floppys, Anfangs-/Endsektoren, 6144 unabhängige Nutzbytes und sämtliche versiegelten Journale. 38 Typen/751 Instanzmitglieder/32 Ereigniskörper bleiben erhalten; es gibt keine neuen Zustandsfelder oder Timer.

## Tatsächliche Gastnachweise

Alle folgenden endgültigen Läufe benutzen denselben abschließend geprüften DLL-Satz und ausschließlich frische private Journale. Keine Originaldatei oder persönliche Spiel-COW wird verändert.

| Nachweis | Kaltstarts | Volle Fortsetzungen | Weitere ganze CPU-/8-MiB-RAM-/Geräte-/Schedulervergleiche | Negative Gastoracles |
| --- | ---: | ---: | ---: | ---: |
| P54C-DOS: MS-DOS, FreeDOS, originales EMM386 NOEMS/RAM jeweils zweimal | 8 | 8 | 3230 | 520 |
| Getrennte originale EMM386-Monitor-Fehlercuts, drei Befehle je NOEMS/RAM zweimal | 12 | 12 | 96 | 228 |
| Bestehende 486-DOS-/EMM386-Fortsetzung | 8 | 8 | 1282 | 200 |
| Originales DOS4GW/Doom auf P54C, DOSE VGA mit Ton/ET4000 ohne Ton | 2 | 2 | 49 | 18 |

**18 positive DOS-Kaltstarts/18 volle Fortsetzungen mit tatsächlicher DOS-Rückkehr**, daneben **12 gesonderte Monitorfehlerstarts/12 Fortsetzungen ohne DOS-Rückkehranspruch**. Insgesamt 4561 weitere ganze Vergleiche der positiven Gäste, plus 96 Monitorvergleiche, zusammen 4657. 966 negative Gastoracles; die zwölf wirklichen beschädigten Importregressionen werden davon getrennt gezählt.

Der eigene positive DOS-Gast führt 48 beobachtete P54C-Befehle mit 96 unabhängig ganzzahlig rekonstruierten Clockpunkten aus. CPUID/ID/CX8 werden in allen vier Konfigurationen geprüft. Nur im tatsächlichen Real Mode schreibt er eine hohe vollständige TSC-Epoche und liest MSR/RDTSC; unter EMM386 umgeht er keine Rechte. PCM-DMA, rohe 80-Bit-Werte, offene virtuelle UART-/MPU-/Gameportfristen und echte DOS-Dateien setzen vollständig fort. Der nachfolgende DOS-Prompt wird unabhängig aus 2000 tatsächlichen Textplane-/CRTC-Zellen und BDA sowie anhand des wirklichen AH4C-Rückkehrpfads geprüft.

Die negativen V86-Gäste führen tatsächlich RDTSC/RDMSR/WRMSR aus und landen vor der ersten Monitorinstruktion am originalen EMM386-IDT13-Gate. Das Oracle rekonstruiert Paging, TSS-Stack und den vollständigen #GP(0)-V86-Rahmen. TSD bleibt dabei null, die historische V86-Ablehnung wird also nicht durch künstliches Setzen von TSD hergestellt. Danach werden je acht echte Monitorinstruktionen im vollständigen PC identisch fortgesetzt. Es wird weder ein fremder Handler ersetzt noch eine spätere reguläre DOS-Rückkehr dieser Fehlergäste behauptet.

Doom liefert 18 unabhängig WAD-geprüfte Bilder, 215320 Stereo-PCM-Frames und reguläre DOS-Rückkehr; ein tatsächlicher Gastcut wurde visuell geprüft. Das ist eine kurze DOS4GW-/Fortsetzungsreferenz, keine neue allgemeine Spiel-/Sound-/FPS-/historische Timingabnahme. Ein separater echter Headless-Speicher-/Ladepfad am eigenen eingebauten Gast setzt ohne neuen POST fort und prüft die neuen Reportfelder; er wird nicht als DOS-Start gezählt.

Der erste eigene DSP-Testgast hielt Reset bei 90 MHz zu kurz. Seine Verzögerung wurde auf mindestens 3 Mikrosekunden verlängert; die Geräteanforderung wurde nicht gelockert. Eine frühe HLT-Testannahme verlangte fälschlich wiederkehrende Speaker-Callbacks statt des analytischen Zählers und verwendet nun echten periodischen PIT0. Erfolgreiche Zwischenmatrizen vor der abschließenden POST-Inventurkorrektur sind getrennt archiviert und nicht die endgültigen Nachweise.

64 Kandidaten-/normalen Produktdateien sind bytegleich. 667 lizenz-/kommentargeprüfte Code-/Projektdateien, 136 Python- und 93 PowerShell-Dateien. Der vollständige 8870er-Starter und dessen 774 Quellen bleiben archiviert. 115 geschützte Eingaben, C64 und HEAD unverändert; persönliche Spiel-COWs ungeöffnet. Keine Windows-Eingaben, Fokusversuche, Hostendpunkte, Aufnahme, Commits/Pushes oder Veröffentlichung.

## Reproduktion und nächste Voraussetzung

```powershell
.\scripts\run.ps1 -NoBuild -Profile .\profiles\dose-p54c-at-dev-v1.json -Disk <eigene-DOS-Basis.img> -Overlay <neues-privates-Journal.cow>

dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 P54C profile"
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 P54C PC"
.\scripts\test-p54c-pc.ps1 -SkipBuild -Disk <private-Basis.img> -Overlay <private-pristine.cow> -ConfiguredDisk <private-Manager-Basis.img> -NoEmsOverlay <private-NOEMS.cow> -RamOverlay <private-RAM.cow>
```

Privater Gesamtnachweis: **artifacts/tests/m4-p54c-pc-release-path.txt**. Der nächste konkrete M4-Produktschritt ist die benannte 430FX-/PIIX-Plattform mit wirklichen PCI-Ressourcen, PAM-/Dekodierwirkungen, Interrupt-/Resetdomains, Zustand und Gastwirkungen. Danach S3 Trio64, ATAPI, Firmware/Manager und tatsächliche Win95-RTM-Installation mit mehrfachen Starts, stabilen Geräten und regulärem Shutdown. M4 und M5–M8 bleiben offen; der stündliche Gesamtauftrag läuft weiter. Die ausdrücklich verschobene native Mausabnahme bleibt unberührt.
