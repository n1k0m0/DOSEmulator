# Windows 95: laufender Installations- und Gastnachweis

Ergänzender aktueller Nachweis: [Windows-Dateien, VFAT und CD-Lesen](WINDOWS95-STORAGE.md) besteht bei 100/133 MHz mit unveränderten V12-Modulen, vollständigen eigenen 262157-Byte-Dateien und je einem ganzen aktiven CD-Snapshot-Future. Der beobachtete CD-PIO läuft im V86-Modus; native CDFS/IOS ist damit weiterhin offen. Die folgenden historischen Installations-/Soundnachweise und die ursprüngliche RTM-/M4-Grenze bleiben erhalten.

Stand: 28. Oktober 2026. Der lokal abgenommene deutsche OEM-Vertikalstand **V12 mit 12589/12589 Regressionen bei 0 Warnungen und 0 Fehlern ist in den gemeinsamen Starter `artifacts/m1-build` übernommen**. Die 195 unveränderten geschützten Produktdateien beziehen sich auf den vorausgegangenen historischen Kandidatenbuild, nicht auf die anschließende bewusste Starterübernahme. Der tatsächliche 100-MHz-Gast besteht native S3-/Datei-/GDI-/Wave-Probe, reguläres Anwendungsschließen und Windows-Shutdown; aktive DMA-Fortsetzung ist separat auf einem strikten Desktopimport geprüft. Der 133-MHz-Kaltstart samt nativer Probe und normalem Windows-Neustart besteht ebenfalls; lange aktive Audiofortsetzungen bei beiden Raten erreichen natürliches WAVE-DONE auf beiden Seiten. Auch der reguläre 133-MHz-Shutdown ist jetzt belegt. Alle 65 Starterdateien sind nun bytegleich zum eingefrorenen V12-Bündel; 26 haben sich gegenüber 11819 geändert. Das alte 65-Dateien-Bündel bleibt archiviert, und für die Übernahme wurde nicht neu gebaut. **Ursprüngliche RTM- und M4-/M5–M8-Gesamtabnahme bleiben offen.**

Dieser Arbeitsstand ergänzt die historischen Aussagen in [Windows-95-Bereitschaft](WINDOWS95-READINESS.md) und den [S3-Entwicklungsvertrag](hardware/S3-WIN95-DEVELOPMENT.md). Frühere Abnahmen behalten ihre dort genannten Builds und Grenzen.

## Tatsächlicher V12-Gastbetrieb bei 100 MHz

Der neue reguläre Kaltstart mit installiertem A erreicht den **originalen nativen S3-Desktop in 800×600 RGB565** und sichtbares Lautsprechersymbol. `A:\DOSE95.EXE /wave` führt seine echten Windows-Aufrufe aus. Der [Datei-/API-Prüfer](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-wave-file-api-verified.json) bestätigt alle 1027 Nutzdateibytes, den eigenen 512-Byte-Proof, natürliches WAVE-Ende bei `00001FFF` / Sequenz 169 und den vorherigen abgeschlossenen Schreibversuch. Der [getrennte GDI-Prüfer](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-wave-file-api-gdi-verified.json) bestätigt **94620 innere RGB-Pixel, null Ausschlüsse**. Während der Anforderung werden 8000 zusätzliche native DMA8-Samples und 16 Block-/IRQ-Abschlüsse beobachtet; kein nicht implementierter DSP-Befehl tritt hinzu.

Die Anwendung [schließt regulär](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-wave-closed-file-api-verified.json) mit Phase 6 / `00007FFF` / Sequenz 184. Danach führt der originale Windows-Beenden-Dialog bis „Sie können den Computer jetzt ausschalten“. Der [gesicherte Gastbericht](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-native-regular-shutdown.json) zeigt wieder PE=0, und der [eigene Shutdown-Medienstand](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-native-regular-shutdown.dosemedia/media.json) ist versiegelt. Das belegt diesen 100-MHz-Gastlauf und seinen regulären Shutdown.

Die [WAVE-DONE-Fortsetzung](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-s3/v12-native-wave-done-continuation/result.json) besteht **18 ganze CPU-/32-MiB-RAM-/VRAM-/Geräte-/Schedulervergleiche**, je 320000 Schritte sowie 480 gleiche tatsächlich publizierte Stereo-PCM-Frames nach dem Wiedergabeende.

Der getrennte [Audio-Witness](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12/run-1/result.json) ist ausdrücklich ein **strikter Import des erreichten V12-Desktops**, kein weiterer Kaltstart. Er bindet alle **8000 Mono-U8-DMA-/DAC-Samples bei 8000 Hz**, exakt 125000 ns je Sample und den vollständigen Quelldigest `D0E1C9D0871A6E4838DF28002AEDAEA326B681F085F96821CB5BAC6F4F12FBB9`. Er belegt 16 Blockabschlüsse / 16 IRQ5-Quittierungen. Im ganzen begrenzten Witness-Zeitfenster sind **1919520 tatsächlich publizierte Float-Stereo-Frames**, 174710 nichtnullige Float-Komponenten, kein NaN/Inf sowie echte Clock-/Bytehashbindung erfasst. Seine [aktive DMA-Fortsetzung](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12/run-1/active-native-continuation/result.json) besteht zusätzlich 18 ganze Vergleiche, je 320000 Schritte und 480 gleiche zukünftige Stereo-Frames bei einem Schnitt mit noch laufender DMA und echter zukünftiger Samplefrist.

Die nativen Quelldaten sind unabhängig geprüft; die gesamten gemischten Floatbytes und Veröffentlichungszeiten sind tatsächlich verglichen. Es gibt **keine neue vollständige unabhängige Mixer-/Filter-/Resamplingrekonstruktion und keinen Host-Audiotest**. Der optionale Current-App-RAM-Oracle fand im Idle-CR3 keine App-PTE: Daraus wird weder eine bestandene aktuelle App-/WAVEHDR-RAM-Prüfung noch ein Produktfehler abgeleitet. Die separaten Datei-/GDI-/API-Nachweise und vollständigen Hardware-/RAM-Fortsetzungen bleiben gültig. Die nachstehende 133-MHz-Probe und die längeren aktiven Schnitte ergänzen diese ersten Nachweise; Ursprüngliches RTM, weitere Formate und M4 bleiben offen; der nachstehende 133-MHz-Shutdown ist inzwischen zusätzlich belegt.

## 133-MHz-Kaltstart und natürliche Audio-Enden auf beiden Seiten

Auch das feste **133-MHz-/32-MiB-Profil** führt nun einen wirklichen kalten Windows-Start bis zum nativen **800×600-RGB565-Desktop** aus. Die [Win32-Probe](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-133-fixed/v12-133-wave-file-api-gdi-verified.json) bestätigt **1027 vollständige DAT-Bytes**, den eigenen **512-Byte-BIN-Proof**, natürliches WAVE-DONE und **94620 exakte RGB-Pixel ohne Ausschlüsse**. Die 100- und 133-MHz-Starts verwenden ihre bei kalter Konstruktion gebundenen Profile; es gibt keinen laufenden CPU-/Takt-/ROM-Tausch.

Ein über den originalen Windows-Dialog angeforderter [normaler Neustart](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-133-fixed/v12-133-after-restart.json) erreicht den Desktop erneut. Der Bericht bindet **POST-Zähler 2, 133000000 Hz, keinen Gastfehler und HardwareResetCount 0**. Dies ist der tatsächliche OS-Neustartpfad, keine CF9-/INIT-Behauptung. **Auch der reguläre 133-MHz-Shutdown besteht jetzt:** Nach dem originalen Windows-Dialog erscheint der ursprüngliche Ausschaltbildschirm. Der [gesicherte Bericht](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-133-fixed/v12-133-native-regular-shutdown.json) zeigt bei 81,837894736 s PE=0, 320×400, POST-Zähler 2, HardwareResetCount 0 und keinen Gastfehler; der [Medienstand](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/live-win95-v12-native-133-fixed/v12-133-native-regular-shutdown.dosemedia/media.json) ist regulär versiegelt. Damit sind die beiden benannten Windows-Gast-Shutdowns bei 100 und 133 MHz getrennt belegt.

Die beiden längeren aktiven Wave-Schnitte verwenden jeweils einen tatsächlichen laufenden PC auf einem strikten Desktopimport und eine einzelne vollständige Restaurierung. Jeweils **40000000 normale Schritte pro Seite** und **18 vollständige CPU-/32-MiB-RAM-/VRAM-/Geräte-/Schedulervergleiche** bestehen. Das sind Fortsetzungen, keine zusätzlich gezählten Kaltstarts.

| CPU | Virtuelle Fortsetzung | Gleiche publizierte Stereo-Frames pro Seite | Eigene Enddateien auf beiden Seiten |
| --- | ---: | ---: | --- |
| 100 MHz | 1,600000000 s | 76800 | natürliches WAVE-DONE, `00001FFF`, Sequenz 31 |
| 133 MHz | 1,203007519 s | 57600 | natürliches WAVE-DONE, `00001FFF`, Sequenz 24 |

Die vollständigen Berichte für [100 MHz](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12-long/run-1/result.json) und [133 MHz](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12-133-long/run-1/result.json) vergleichen PCM-Bytes, Clock und Medien jeweils innerhalb desselben Takts. Die zusätzlich unabhängig geprüften Enddateien beider Seiten liegen für [100 MHz](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12-long/run-1/both-sides-natural-done-summary.json) und [133 MHz](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/audio-witness-v12-133-long/run-1/both-sides-natural-done-summary.json) vor. Beide Original-/Restaurierungsseiten führen den echten File-/API-Pfad bis zum natürlichen Ende aus; ein bloß identischer noch aktiver Wartezustand genügt dafür nicht.

Die Grenzen bleiben erhalten: keine behauptete Bytegleichheit zwischen 100 und 133 MHz, kein Hostaudio, keine neue vollständige unabhängige Mixer-/Filterrekonstruktion, kein ursprüngliches RTM und kein M4-Gesamtabschluss. Die privaten Produkt-/Lizenzangaben werden nicht in diese Dokumentation übernommen.

## Übernommener S3-Stand V12 und tatsächlicher V10-Fehler

Der tatsächliche V10-Kaltstart auf einem frischen Zweig des ursprünglichen S3-Installationsstands erreicht mit installiertem A den Original-S3-Desktop in **800×600 RGB565**. Die eigene Win32-Probe liefert dort alle 1027 Datenbytes und 94620 unabhängig geprüfte innere RGB-Pixel. Das ist ein konkreter Datei-/GDI-Nachweis dieses Builds, keine allgemeine stabile S3- oder Soundabnahme. Auch die 18 ganzen Desktop-/Anwendungsfortsetzungsvergleiche können einen identischen noch beschäftigten Grafiktransfer erhalten; sie belegen allein keinen fortschreitenden Nachrichtenpumpen- oder Schließvorgang.

Der ursprüngliche Root24-Kaltstartablauf wurde mit den eingefrorenen **alten V10-Modulen** anhand seiner gespeicherten tatsächlichen Eingaben und Schrittzahlen wiederholt. Nach **624695047 Schritten / 24,987801880 virtuellen Sekunden** bindet der private V6-Prüfer **16 tatsächlich abgeschlossene WORD-Stores** der Original-S3-Routine für einen 16×16-Monochromtransfer `55B3h`. Kein fehlgeschlagener oder bloß vorab gefetchter Store wird mitgezählt. Die alte Engine steht danach aktiv bei **128 von 256 Pixeln**, ohne Gastfehler und ohne offene Bytegruppe. Die WORD-Breite geht bisher zwischen CPU und dem byteweisen festen MMIO-Ziel verloren. Die geltende DWORD-Zeilenpaddingregel bleibt korrekt und wird nicht gelockert.

V12 ergänzt den im [S3-Entwicklungsvertrag](hardware/S3-WIN95-DEVELOPMENT.md#begrenzte-mmio-word-zeilenreste-auf-v12) begrenzten unteren MMIO-WORD-Zeilenrest als eigene DOSE-Kompatibilitätspolitik. Alle **27 S3-WORD-Fälle** bestehen; derselbe eingefrorene Harness liefert auf alten V11-Modulen **7 PASS / 20 FAIL**. Getrennt bestehen **47 Core-WORD-Transaktionsfälle**. Die vollständige V12-Suite besteht 12589 Fälle, die Quellenprüfung 829 Code-/Projektdateien einschließlich 404 C#-Dateien. Der Zustand bleibt bei 46 Typen, 894 Mitgliedern und 40 Scheduleroperationen; diese WORD-Politik fügt selbst kein Feld und keinen Timer hinzu.

Diese Komponentennachweise sind inzwischen durch den oben getrennt beschriebenen tatsächlichen V12-100-MHz-Gastlauf ergänzt. Allgemeine Langzeitstabilität und die übrigen M4-Pflichtnachweise bleiben offen; der 133-MHz-Kaltstart und normale Windows-Neustart sind oben getrennt belegt. Der historische V6-Ursachennachweis führt selbst keine spätere Maus-/AltF4-/Snapshotfolge aus, nachdem seine 16 echten Stores gebunden sind.

## Originalmedium und eigene Installation

Die verwendete private Quelle ist die deutsche **Windows-95-OEM-Revision-A-/950-Familie**. Sie ist weder ein unverändertes ursprüngliches RTM-Medium noch OSR2. Ein Erfolg mit dieser Quelle ersetzt die ausdrücklich beauftragte ursprüngliche RTM-Abnahme nicht. Das originale Setup hat die bereitgestellte Produktangabe angenommen; deren Inhalt und persönliche Angaben werden hier nicht veröffentlicht.

Die Installation schreibt ausschließlich auf eine neu erzeugte private **1-GiB-Festplatte**. Originales DOS-FDISK und FORMAT haben darauf eine aktive FAT16-Partition ab LBA63 mit 2096577 Sektoren und 16-KiB-Clustern angelegt. Die unabhängige Prüfung des vorbereiteten DOS-Stands bestätigt beide FAT-Kopien und die acht geprüften DOS-/Startdateien ohne verlorene oder mehrfach belegte Cluster. Das ist der Nachweis dieses Vorbereitungsstands, keine pauschale Integritätsaussage über alle späteren Windows-Dateien.

Der eigentliche Gast führt das normale originale Setup mit Hardwareerkennung und Dateikopie aus. Das Setup und seine Erkennung wurden nicht durch eigene Dienste ersetzt. Betriebssystemcode wurde nicht zur Umgehung eines Fehlers gepatcht. Der anschließende Neustart erreichte tatsächlich erneut den DOSEBios-POST. Die laufende Installation wurde danach über die weitere Hardwarekonfiguration und normale Windows-Neustarts fortgesetzt.

## Tatsächlich erreichter V9-Desktop mit installiertem A

Der neue Kaltstart `live-win95-v9-firstboot-fixed` verwendet das eigene 100-MHz-/32-MiB-Windows-Profil mit **physischem Laufwerk A als 1,44-MiB-Laufwerk**, einer eigenen schreibgeschützten FAT12-Probediskette und der originalen privaten Windows-CD. Er beginnt auf einem neuen privaten Festplattenzweig am ursprünglichen regulären Setup-Neustart. Ein alter V7-/V8-Hardwarezustand wurde dafür nicht importiert.

Das originale Setup schloss seine weitere Konfiguration ab und führte einen wirklichen normalen Neustart bis zum neuen BIOS-POST aus. Der folgende Start erreichte den **vollständigen Windows-Desktop mit installiertem A**. Der frühere no-A-Kontrolllauf ist damit keine Voraussetzung mehr für diesen konkret nachgewiesenen Start. Der nun bestückte A-Pfad ist jedoch keine pauschale Abnahme jedes Leer-Medien-, Wechsel- oder Fehlerfalls.

Die Windows-Firmware gibt seit V8 beim POST IRQ6 nur bei installiertem physischem Diskettenlaufwerk frei: Mastermaske BCh statt FCh, ohne Laufwerk weiterhin FCh. Der bestehende IVT0Eh-Handler, seine BDA-Meldung und die tatsächliche PIC-Quittierung bleiben erhalten. Die ursprüngliche VPICD-Maskenübernahme war unabhängig am Originalcode und tatsächlichen Zustand gebunden; der Fix ist keine globale Unmaskierung im CPU- oder PIC-Kern.

V9 ergänzt die [begrenzte P54C-LAR-Kompatibilitätspolitik](hardware/P54C-LAR.md): Erfolgreiches 32-Bit-LAR erhält die rohen Descriptorbits an den von Intel undefinierten Ergebnispositionen 19:16. Damit bewahrt die wirkliche Original-KRNL386-Rechtefolge den ersten Huge-Bitmap-Descriptor 147DFh statt ihn auf 47DFh zu verkürzen. Dies ist eine ausdrücklich benannte DOSE-Politik, keine gemessene B5-Silizium- oder neue Intel-Normzusage. Der 486-Pfad, LSL, 16-Bit-Ziele sowie Rechte-, Paging-, MMIO-, Cache- und Segmentfehlergrenzen bleiben getrennt geprüft.

Auf V9 öffnet sich nun auch der **originale Grafikkarten-Auswahldialog**, an dessen vorherigem VGA-Bitmap-Aufbau der V7-Lauf die RUNDLL32-Schutzverletzung zeigte. Der tatsächliche Dialogaufbau bestätigt diesen Fortschritt; er ist noch kein Nachweis eines geladenen nativen S3-Treibers oder höherer Farbmodi. Die LAR-Änderung enthält keine Firmwareänderung: Alle sieben erzeugten ROM-Quelldateien sind zum eingefrorenen V8-Stand bytegleich.

## Historischer V7-Kontrolllauf ohne A

Der gezielte Kontrolllauf `live-win95-v7-no-drive-a` verwendet das eigene 100-MHz-/32-MiB-Profil mit **physischem Diskettenlaufwerk A auf `none`**. Aus demselben privaten Installationsstand erreichte er die Zeitzonenabfrage, den Abschluss der Konfiguration, einen normalen Neustart und den wirklichen Windows-Desktop. `control desk.cpl` öffnete anschließend die Anzeigeeigenschaften und deren Einstellungen.

Das Entfernen des physischen A-Laufwerks ist eine ausdrücklich benannte Diagnosekonfiguration. Es belegt keinen reparierten Leer-Medien-Pfad und keine Abnahme der Konfiguration mit installiertem leerem A. Eine vollständige 100-/133-MHz-Startmatrix liegt nicht vor.

Dieser V7-Desktop lief mit **640 × 480 und 16 Farben über den originalen VGA-Treiber**. Die installierten Dateien S3.DRV, S3.VXD und SUPERVGA.DRV stimmen bytegenau mit den privat extrahierten Originaldateien der CD überein. Das Bootprotokoll belegt jedoch VGA.DRV; der native S3-Treiberbetrieb ist damit nicht abgenommen.

Die eigene V7-Firmware weist S3-INTA beim echten POST über PIRQA IRQ11 zu. Der tatsächliche Gastzustand zeigt InterruptLine 11, InterruptPin 1, PIRQA 11 und ELCR-Bit 11. Die sechs älteren ROM-Payloads bleiben bytegleich. Diese Ressourcenzuweisung behebt den zuvor fehlenden POST-Eintrag; sie erklärt für sich allein keinen weiteren Devloader-Fehler.

## Volle Fortsetzung am historischen V7-Desktop

Ein tatsächlicher vollständiger V7-Snapshot des laufenden Desktops wurde in einen neuen PC auf eigenen Medienzweigen geladen. Original und Restaurierung führten anschließend jeweils 320000 weitere CPU-Schritte aus. **18 vollständige Vergleiche** umfassen CPU, alle 32 MiB RAM, VRAM, Geräte und Scheduler einschließlich Anfang und Ende; kein erfasstes Hardwarezustandsfeld wurde für diesen Vergleich ausgelassen. Die Medienidentität wird beim Vergleich nur um die neue Capture-ID normalisiert.

Je Seite wurden **480 Stereo-PCM-Frames** mit gleichen tatsächlichen Bytes und Veröffentlichungszeiten verglichen. Das ist ein begrenzter Fortsetzungsnachweis am erreichten Desktop. Es ist keine Windows-Soundtreiber-, Wiedergabe-, Host-Audio- oder neue unabhängige Syntheseabnahme. Ebenso ersetzt die Fortsetzung keinen weiteren Kaltstart oder regulären Shutdown.

## Eigene Win32-Datei- und GDI-Probe im Gast

Ein weiterer privater no-A-Kaltstart mit unverändertem V7 führte **DOSE95.EXE tatsächlich als Win32-Anwendung im originalen Windows-Gast** aus. Die dafür eingelegte eigene Probe-CD enthält keine Original-Betriebssystemdateien. Die Anwendung fragt echte Windows-API-Ergebnisse ab, schreibt ihre eigene Datei, schließt und öffnet sie erneut und zeichnet drei Farbflächen über GDI in ihr eigenes Fenster.

Der unabhängige Prüfer liest ausschließlich den benannten versiegelten Mediencheckpoint und die tatsächliche Bilddatei. Er bestätigt alle **1027 Bytes von `C:\DOSE95.DAT`**, einschließlich unabhängig berechnetem FNV1a `8467F7B2` und SHA256 `A167946C121A849D0CB8B81BFDF798C013AEAF41D31D37D6762D1CA89A1E5DF2`. Beide FAT-Kopien stimmen überein; die beiden eigenen Nachweisdateien besitzen getrennte Clusterketten.

Der persistierte eigene 512-Byte-Nachweisblock hat Sequenz 111, enthält die wirklichen API-Rückgaben und meldet Windows 4.0/Build 950/Win9x-Plattform 1. Der unmittelbar vorherige vollständige Nachweisschreibversuch ist mitgeprüft. `GetSystemMetrics` und `GetDeviceCaps` stimmen bei 640 × 480 und 1 Bit je 4 Ebenen überein, entsprechend 16 Farben.

Das echte Clientrechteck misst 392 × 253 Pixel, sein Bildschirmursprung liegt bei (36,55). Aus diesen Gastangaben prüft der unabhängige PPM-Oracle **94620 innere Pixel** exakt als Rot/Grün/Blau: 31374/31374/31872 Pixel, ohne ausgeschlossene Cursorpixel. Dies belegt die benannten Win32-/Datei-/GDI-Wirkungen im ursprünglichen VGA-Pfad; es ist keine native S3-Treiberabnahme.

Für diesen Lauf war Wave nicht angefordert. Der Bericht setzt `audioOutputAccepted=false`; es wurde keine tatsächliche PCM-Quelle für eine Audioabnahme übergeben. Auch eine volle PC-Fortsetzung dieser eigenen Probe ist darin noch nicht abgenommen. Der getrennte Desktop-Fortsetzungsnachweis oben und dessen 480 gleiche PCM-Frames werden nicht als Ersatz dafür gezählt.

## Begrenzter V9-DOS-/EMM386-Fortsetzungsnachweis

Die Änderung des P54C-Verhaltens wurde zusätzlich auf genau den sechs bekannten eigenen DOS-Eingabekopien kontrolliert. Der unveränderte PCI-BIOS-/MSCDEX-Harness besteht **vier DOS-Kaltstarts**: MS-DOS, FreeDOS und originales EMM386 jeweils mit NOEMS und RAM, je einmal. Seine zwei bestehenden Schnitte pro Start ergeben **acht wirkliche volle 32-MiB-Fortsetzungen**, **796 weitere vollständige Zustands-/RAM-Vergleiche** und **2106 negative Oracles**.

Die Kontrollen verwenden originales MSCDEX und den eigenen CD-Treiber, wirkliche 16-/32-Bit-PCI-Firmwareaufrufe sowie vollständige CPU-/Geräte-/Schedulerzustände. Original und beide Restaurierungen kehren tatsächlich nach DOS zurück und führen ein neues ECHO aus. Die unabhängigen Datei-, ROM-, VCPI-, Stack-, Medien- und Fortsetzungsprüfer wurden für die neue LAR-Politik nicht gelockert.

Dies ist ein begrenzter Kontrolllauf bei 100 MHz. Er wiederholt weder die frühere achtfache Determinismusmatrix noch alle historischen Geräte-/Spielepakete. Er ersetzt insbesondere keine V9-Windows-Desktopfortsetzung, Sound- oder S3-Abnahme. Die oben genannten 18 Desktopvergleiche und 480 PCM-Frames bleiben ausdrücklich V7 zugeordnet.

Snapshotformat 1 bleibt buildgebunden. Ein tatsächlicher V8-Import in V9 wird vor Zweiganlage abgewiesen; es erfolgt keine automatische Migration der alten Container.

## Zusätzlicher alter 486-DOS-Kontrollumfang

Auf den unveränderten V12-Modulen besteht der historische 486-Kontrollharness mit **acht echten Kaltstarts und acht vollständigen 8-MiB-Fortsetzungen** unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM je zweimal. Der [Kontrollbericht](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/v12-legacy-486-control/bounded-check.json) enthält **1282 weitere ganze CPU-/RAM-/Geräte-/Schedulervergleiche**, zusätzlich 16 Anfangs-/Endvergleiche und **200 negative Oracles**. Der alte Rückkehrvertrag bleibt unverändert: echte dekodierte INT21/AH4C-Anforderungen, `SNAPSHOT-DONE` und 30000 Nachlaufschritte mit Audio.Flush. Dies wird ausdrücklich **nicht als zertifizierter abschließender DOS-Prompt oder neues ECHO** ausgegeben. Die Windows- und 486-Nachweise bleiben getrennt gezählt.

## Konkrete offene Abnahmen

- **Diskettenbetrieb:** Der tatsächliche V9-Start mit installiertem A und eigener eingelegter Probediskette besteht. Weitere leere oder wechselnde Medien und ihre regulären Windows-Fehler-/Rückkehrpfade benötigen eigene Gastnachweise. Die getrennten 82077AA-Statusbitbefunde und die bestehende nominale Fristpolitik werden dadurch nicht pauschal abgenommen.
- **Native S3-Funktion:** Der benannte V12-100-MHz-Kaltstart besteht mit Original-S3-Treiber in 800×600 RGB565, eigener Datei-/GDI-/Wave-Probe, regulärem Anwendungsschließen und Windows-Shutdown. Auch der 133-MHz-Kaltstart samt nativer Probe und normalem OS-Neustart besteht. Der anschließende 133-MHz-Shutdown besteht ebenfalls. Weitere Modi, Beschleuniger-/Cursorfälle und allgemeiner längerer Betrieb bleiben getrennt offen.
- **Windows-Audio:** Die historischen V7-/V9-Fehlversuche behalten ihren damaligen Buildbezug. V12 besteht den benannten natürlichen Wave-DONE-/Schließpfad und den getrennten strikten Audio-Witness mit allen 8000 Quelldaten, 16 IRQ5-Quittierungen und aktiver voller DMA-Fortsetzung. Weitere Formate und unabhängige Ganzmix-/Hostaudioabnahme sind damit nicht zugesagt.
- **Gesamtabnahme:** Die deutsche OEM-Revision-A-Quelle, der tatsächliche 100-MHz-Betrieb und sein regulärer Shutdown sind benannt belegt. Ursprüngliches RTM-Medium, wiederholter längerer stabiler Betrieb sowie die übrigen M4-Pflichtnachweise bleiben offen; die benannten Starts und langen aktiven Fortsetzungen sind getrennt belegt. Die stündliche Weiterentwicklung endet dadurch nicht.

Dieser Stand ist deshalb **kein sauberer Windows-95- oder M4-Abschluss**. Die bestehende stündliche Fortsetzung des Gesamtauftrags bleibt aktiv. Der ausdrücklich verschobene native Maus-/Fokusnachweis und die übrigen nativen Bedien-/GL-Grenzen werden dadurch nicht aufgehoben.

## Private prüfbare Belege

Alle folgenden Pfade liegen unter dem privaten Arbeitsordner
`artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/`.
Die Medien und Zustandscontainer bleiben privat; sie enthalten keine zur Veröffentlichung bestimmten Betriebssystemkopien oder Lizenzangaben.

| Beleg | Aussage und Grenze |
| --- | --- |
| [Lokale V12-Starterübernahme](../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/v12-common-starter-promotion.json) | Alle 65 Dateien bytegleich zum eingefrorenen V12, 26 Änderungen gegenüber 11819; altes 65-Dateien-Bündel archiviert, kein Neubau. Lokal abgenommene OEM-Vertikale, keine vollständige RTM-/M4-Abnahme. |
| `live-win95-v12-native-133-fixed/v12-133-native-regular-shutdown.json` und gleichnamige `.dosemedia` | Regulärer 133-MHz-Windows-Shutdown bis Ausschaltbild, PE=0/320×400, POST2, HardwareResetCount0 und regulär versiegelte Medien. |
| `v12-legacy-486-control/bounded-check.json` | 8 alte DOS-Kaltstarts / 8 volle8MiB-Restaurierungen / 1282 weitere ganze Vergleiche / 200 negative Oracles; alter AH4C-/30000-Schritt-Vertrag, kein finales Promptoracle. |
| `live-win95-v12-native-133-fixed/v12-133-wave-file-api-gdi-verified.json` und `v12-133-after-restart.json` | Echter 133-MHz-Kaltstart, Datei-/API-/94620-Pixel-Probe und normaler Windows-Neustart bei POST2/HardwareResetCount0; anschließender regulärer Shutdown separat belegt. |
| `audio-witness-v12-long/run-1/result.json` und `audio-witness-v12-133-long/run-1/result.json` | Je 40M Schritte pro Seite / 18 ganze Vergleiche; 76800 bzw. 57600 gleiche Stereo-Frames in den aktiven langen Fortsetzungen. |
| Die jeweiligen `both-sides-natural-done-summary.json` | Unabhängige echte Enddatei-/API-Prüfung von Original und Restaurierung: natürliches WAVE-DONE bei beiden Takten. |
| `live-win95-v12-native-s3/v12-wave-file-api-verified.json`, `v12-wave-file-api-gdi-verified.json` und `v12-wave-closed-file-api-verified.json` | Nativer 100-MHz-Gast: alle Datei-/API-/94620-Pixel-Belege, natürliches Wave-DONE und reguläres Schließen; RAM- und Audiowitness getrennt. |
| `live-win95-v12-native-s3/v12-native-wave-done-continuation/result.json` | 18 ganze Vergleiche / 320000 Schritte je Seite / 480 gleiche PCM-Frames nach WAVE-DONE. |
| `audio-witness-v12/run-1/result.json` und `active-native-continuation/result.json` | Strikter Desktopimport, alle 8000 nativen Payloadsamples, ganze PCM-Clock/-Bytes und eigene aktive DMA-Fortsetzung; kein zweiter Kaltstart oder Hostaudio. |
| `live-win95-v12-native-s3/v12-native-regular-shutdown.json` und `v12-native-regular-shutdown.dosemedia` | Originaler Windows-Beenden-Pfad bis zum Ausschaltbild, PE=0, regulär versiegelte private Medien. |
| `win95-build-v12.log`, `full-tests-v12-terminal/artifacts/tests/results.json` und `win95-build-v12-output-guard.json` | V12: 0 Warnungen/Fehler, 12589/12589 Regressionen, 195 geschützte Produkte unverändert; der benannte 100-MHz-Gastnachweis ist oben getrennt belegt. |
| `source-quality-v12/summary.json` und `v12-state-check.json` | 829 Code-/Projektquellen, davon 404 C#, ohne Befund; 46 Typen/894 Mitglieder/40 Operationen. |
| `s3-word-transfer-trace-v10-root24-v6/run-1/result.json` und `cause-result.json` | Alter V10: 16 echte ursprüngliche WORD-Stores, 32 Datenbytes, danach aktiver Pixelcursor 128/256; unabhängige Ursachenbindung, keine V12-Gastabnahme. |
| `s3-word-tail-before-probe/before-exit.json` | Identische eingefrorene 27 WORD-Fälle auf alten V11-Modulen: 7 bestanden, 20 verworfen. |
| `continuation.json` | Fortsetzungsstand, gemeinsamer Starter und nächste konkrete Arbeit. |
| `win95-build-v9.log` und `full-tests-v9-terminal/run.log` | Historischer V9-Build ohne Warnungen/Fehler und 12401 bestandene Regressionen. |
| `targeted-lar-v9/run.log` | 501 gezielte bestandene Windows-Fälle einschließlich aller 117 neuen LAR-Kontrollen. |
| `lar-v9-dos-check/bounded-check.json` und `lar-v9-dos-check/acceptance/acceptance.json` | Genau vier DOS-Kaltstarts, acht volle 32-MiB-Fortsetzungen, 796 ganze Vergleiche und 2106 negative Oracles auf den gepinnten V9-Modulen; keine wiederholte achtfache Matrix. |
| `live-win95-v9-firstboot-fixed/v9-configured-normal-restart.json` und `live-win95-v9-firstboot-fixed/v9-configured-normal-restart.dosemedia` | Wirklicher normaler Setup-Neustart mit installiertem A bis zum BIOS-POST; neuer versiegelter Medienstand. |
| `live-win95-v9-firstboot-fixed/v9-post-configuration-desktop.json` und `live-win95-v9-firstboot-fixed/v9-post-configuration-desktop.png` | Vollständiger originaler V9-Desktop bei 100 MHz/32 MiB mit installiertem A und eigener Probediskette. |
| `live-win95-v9-firstboot-fixed/v9-original-graphics-chooser.json` und `live-win95-v9-firstboot-fixed/v9-original-graphics-chooser.png` | Tatsächlich geöffnete originale Grafikkartenauswahl; noch keine native S3-Treiberabnahme. |
| `v9-seven-roms-unchanged.json` und `v9-rejects-v8-timezone-result.json` | Sieben ROM-Quellen zu V8 bytegleich; tatsächlicher V8-Import wird ohne Zweiganlage abgewiesen. |
| `win95-build-v7.log` und `full-tests-v7-terminal/run.log` | Historischer V7-Build ohne Warnungen/Fehler und 12262 bestandene Regressionen. |
| `staged-dos-independent-check.json` | Original-DOS-Vorbereitung der neuen 1-GiB-/FAT16-Platte. |
| `live-setup2/setup-first-restart-51.json` | Tatsächlicher neuer POST nach dem ersten originalen Setup-Neustart. |
| `live-win95-v7-no-drive-a/` | Benannte Zeitzonen-, Desktop- und Anzeigeeigenschaften-Beobachtungen der Diagnosekonfiguration. |
| `live-win95-v7-no-drive-a/compare-real-windows-desktop/result.json` | Eine volle Restaurierung, 18 ganze Vergleiche und 480 gleiche PCM-Frames je Seite. |
| `live-win95-v7-probe-cd-absolute/own-probe-open-verified.json` | Tatsächliche eigene Win32-/Datei-/API-/GDI-Probe; 1027 ganze Datenbytes, 512-Byte-Proof und 94620 exakte innere Farbflächenpixel. Audio und eigene volle Fortsetzung bleiben offen. |
| `live-win95-v7-probe-cd-absolute/own-probe-open.dosemedia` | Ausschließlich benannter versiegelter Medienstand der eigenen Probe; kein Lesen des aktiven COW durch den unabhängigen Oracle. |
| `v7-actual-resource-check.json` und `v7-six-old-roms-unchanged.json` | Wirklicher IRQ-/ELCR-Zustand und unveränderte sechs ältere ROM-Payloads. |
| `v7-S3-selected-setup-log.txt` und `s3-review/v7-installed-driver-readonly-review.json` | Weiterer Devloader-Fehler, originale installierte Dateidentitäten und tatsächlicher VGA-Bootpfad. |
| `startup-io-trace/run1/` und `vfd-irq-review/irq-runtime-review.json` | Historischer begrenzter V7-Starttrace und unabhängig gebundene ursprüngliche VPICD-Bootmasken-/IRQ6-Politik. |
| `live-win95-v7-no-drive-a/desktop-display-config-crash.dossav` | Sicher erfasster Stand des Grafikdialogfehlers; späterer Kontext, keine vorweggenommene Fehlerstellenzuordnung. |
