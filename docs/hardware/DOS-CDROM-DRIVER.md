# DOSECD: eigener DOS-CD-ROM-Treiber und originales MSCDEX

Stand: 27. Oktober 2026. Der eigene feste DOS-CD-ROM-Treiber ist im benannten Referenzumfang mit originalem MSCDEX 2.23 integriert:11642/11642 Regressionen und79neue SYS-ABI-Fälle,0Warnungen/Fehler. Vollständige CD-Treiber-/ATAPI-/Windows-95-/M4-Abnahme bleibt offen.

## Vertrag und Auslieferung

`firmware/dose-cdrom.asm` ist ein eigener, separat geladener DOS-Character-Treiber. Er ersetzt weder DOS noch MSCDEX und greift auf kein Hostdateisystem zu. Grundlage sind der historische Microsoft-Text **MS-DOS CD-ROM Extensions 2.10 Beta**, Hardware-Dependent Device Driver Specification, Dokument `000080010-100-O00-1186` ([archivierter Microsoft-Originaltext](https://gist.github.com/abrasive/7a615e6dde0c1da962f9930cc63ee43d)), sowie die offiziellen Microsoft-DOS-Quellen [DEVDRIV.DOC](https://github.com/microsoft/MS-DOS/blob/main/v2.0/bin/DEVDRIV.DOC), [DEVSYM.INC](https://github.com/microsoft/MS-DOS/blob/main/v4.0/src/INC/DEVSYM.INC) und [IOCTL.ASM](https://github.com/microsoft/MS-DOS/blob/main/v4.0/src/DOS/IOCTL.ASM). Eine vollständige Umsetzung aller Funktionen dieser Spezifikation wird nicht behauptet.

`scripts/build-cdrom-driver.py` assembliert mit dem vorhandenen NASM 3.02, prüft den DOS-Header und die eigenen Symbolmetadaten und erzeugt `artifacts/drivers/DOSECD.SYS` samt Listing. Der reguläre Build kopiert den Treiber nach `<Build>/Release/drivers/DOSECD.SYS`. Die Testassembly enthält exakt diesen SYS als Ressource. Die separat geladenen Gastbytes sind keine neue BIOS-ROM-Abhängigkeit und kein neues VM-Zustandsmodul.

Der feste Name ist `DOSECD01`. Der Treiber verwendet ausschließlich Secondary Master an `170h/376h`, also den mitgelieferten Platz 2 des Profils `dose-430fx-piix-s3-atapi-dev-v1`. Kein automatisches Scannen anderer Kanäle, keine frei einstellbare `/D:`-Auswertung und kein beliebiges IDE-/PCI-Board werden zugesagt. Die bestehenden PCI-IOSE-/IDETIM-Gates bleiben wirksam; ein gesperrter oder fehlender Port führt zu einer begrenzten Fehlantwort.

Der 22-Byte-CD-Header enthält Attribute `C800h`, echte Far-Strategy-/Interrupt-Einstiege und eine Einheit. INIT schreibt die wirkliche residente Endadresse zurück. Alle Register und Caller-Flags werden über den synchronen Aufruf erhalten. Requestheader, IOCTL-Blöcke und Transferpuffer gehören zum benannten zusammenhängenden konventionellen Speichervertrag unter 1 MiB. Die Maschine behält ihre regulären Segment-/Pagingprüfungen.

## Implementierter Teilumfang

Unterstützt sind INIT, Open/Close, Input Flush, READ LONG mit gekochten 2048-Byte-Sektoren und HSG-Adressen, STOP ohne Audio sowie Input-IOCTL 0/6/7/8/9 und Output-IOCTL 0/1/2/5. Damit sind Headerabfrage, tatsächlicher Tray-/Lockstatus, Sektor-/Mediengröße und Medienwechsel sowie Eject/Lock/Unlock, gerätelokaler Reset und Close möglich. Erweitertes Audio, Raw/Red-Book-Lesen, Write Long, asynchroner Seek und Prefetch werden mit Fehler beantwortet. Nicht unterstütztes Interleaving wird nicht beworben; die beiden READ-LONG-Interleavefelder werden entsprechend dem historischen Microsoft-Vertrag ignoriert.

Jeder gekochte Block entsteht durch ein tatsächliches READ(10)-Paket und vollständige PIO-Wortzugriffe. Ein eigener 2048-Byte-Puffer trennt vollständigen Geräteabschluss und anschließendes Kopieren in den Caller. Der Transfercount wächst nur für vollständig gelesene und kopierte Blöcke. Ein späterer Bereichs-/Backendfehler kann somit einen geprüften Teilcount liefern. Pufferwörter an Offset `FFFFh` werden in zwei Speicherbytes mit Segmentübertrag zerlegt; ein ATA-Wort bleibt ein Wort. Es gibt keine Lockerung der CPU-Segmentlimits.

Der Treiber pollt den fest zugeordneten sekundären Kanal mit nIEN und begrenzten Schleifen. Gleichzeitiger Betrieb eines weiteren IRQ-Treibers am selben Kanal wird damit nicht zugesagt. Er installiert keinen IRQ-Handler, benötigt weder privilegiertes RDTSC noch VDS/VCPI und führt keinen Kanal-SRST aus. Der gerätelokale ATAPI-Reset bleibt vom Peer-HDD und vom PIIX-Hardreset getrennt. Der feste Pollzähler ist eigene Treiberpolitik, keine vermessene mechanische CD-Frist.

CHECK CONDITION wird über echtes REQUEST SENSE ausgewertet; Unit Attention, fehlendes Medium, ungültiger Bereich und Lesefehler erhalten getrennte DOS-Antworten. Ein installiertes leeres Laufwerk darf initialisieren. Die HSG-Mediengröße entspricht `READ CAPACITY.lastLba + 1`; die frühe uneindeutige MSF-Formulierung der alten IOCTL-8-Beschreibung wird nicht durch 150 erfundene Blöcke auf die tatsächlich lesbare Ressource addiert.

## Benutzung in einer eigenen DOS-Installation

Den eigenen SYS auf eine beschreibbare private DOS-Kopie nach `C:\DOSECD.SYS` kopieren. Originales MSCDEX wird vom Benutzer beziehungsweise aus seiner bereits vorhandenen privaten DOS-Installation gestellt und nicht mit DOSE ausgeliefert. Passende `CONFIG.SYS`-Ergänzung:

```dos
LASTDRIVE=Z
DEVICE=C:\DOSECD.SYS
```

Bei den geprüften MS-DOS-Fixtures liegt das originale Programm unter `C:\DOS\MSCDEX.EXE`. Nach dem Neustart anmelden:

```dos
C:\DOS\MSCDEX.EXE /D:DOSECD01 /L:R /M:4
DIR R:\
```

Das Testwerkzeug verändert nur frische private Images. Es erhält die ursprünglichen HIMEM-/EMM386-/Codepage-/Tastaturzeilen und prüft alle übrigen DOS-Dateien sowie die Bootsektoren auf unveränderte Bytes. Die eigene ISO enthält ausschließlich reproduzierbare eigene Daten. Hostseitiges ISO-Mounten bleibt ein Kaltstart; gastseitiges Eject/Load bleibt davon getrennt.

## Konkrete Diagnose, endgültige Nachweise und Grenzen

Der erste Original-DOS-Aufruf zeigte INIT-Länge 24 mit erfolgreicher Rückgabe. Die spätere DOS-IOCTL-Abfrage des Headers verwendete dagegen den normalen 22-Byte-Envelope. Der erste SYS verlangte dort irrtümlich 26 Bytes und antwortete `8105h`; MSCDEX fand deshalb keinen gültigen Treiber. Die enge Korrektur akzeptiert den normalen DOS-Envelope und liest keine zusätzlichen Felder. Auf denselben unveränderten fünf VM-Modulen meldet originales MSCDEX 2.23 danach tatsächlich `Laufwerk R: = Treiber DOSECD01 Gerät 0`.

Die endgültige Matrix enthält acht Kaltstarts unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM jeweils zweimal sowie16volle32-MiB-Fortsetzungen. Beide tatsächlichen Schnitte liegen im residenten SYS, bei CDB10/12 und DataIn128/2048, mit offenem originalem DOS-CDhandle und roher x87. Der unveränderte vorhandene Snapshot speichert den SYS als regulären Gast-RAM. Kein Monitor-, DOS-/MSCDEX- oder Hostdateisystemersatz.

3586 weitere ganze CPU-/RAM-/VRAM-/Geräte-/Scheduler-Vergleichspunkte, 1424 negative Gastoracles und vier ganze Wiederholungspaare bestehen. Alle späteren Port-/IRQ-/DOS-/MSCDEX-Wirkungen werden wirklich ausgeführt; insbesondere wird die beim Schnitt bereits offene1510-Rückkehr samt2048Datenbytes auf jeder der drei Maschinen unabhängig beobachtet und verglichen. Die JFT, eigene MCB-/PSP-/Puffer-/Wachenbereiche und die tatsächliche residente SYS-Arenazugehörigkeit sind gebunden; daraus folgt kein vollständiger SFT-Vertrag.

Ab Beginn des eigenen COM wurden je Lauf18echte PACKETs,13READ-Blöcke und26624CDbytes beobachtet, zusammen144/104/212992. IDENTIFY/Sense/Statusbytes zählen nicht als CD-Lesedaten; die vorherige Treiberinitialisierung wird nicht zu diesen COM-Zahlen addiert. Die eigenen Dateien enthalten8205Payloadbytes,197Textbytes,128Proofbytes und10rohe x87-Bytes; alle vier Inhalte werden je PC vollständig aus seinen tatsächlichen DOS-Dateien geprüft. Die31-Byte-Seekprobe überschreitet eine CD-Sektorgrenze. Die letzte EOF-Lesung liefert0, eine fehlende Datei Fehler2.

Originales MSCDEX2.23 erlaubt in diesen Gästen OPEN3D02 mit einem wirklichen Handle. Erst der tatsächliche AH40-Schreibversuch weist den Schutz nach: unter MS-DOS/EMM386 CF1/AX5, unter FreeDOS CF1/AX19. Die ISO bleibt hashgleich. Ein eigener vorübergehender INT24-Failvektor ist echt installiert und restauriert; in den endgültigen Läufen wurde er nicht aufgerufen, daher keine neue Zustellungsabnahme dieses Callbacks. Die ursprüngliche Open-muss-fehlschlagen-Annahme des ersten Testgasts war falsch und ist kein VM-Fehler.

Alle drei PCs je Lauf erreichen den ursprünglichen DOS-Prompt und führen anschließend einen neuen wirklichen KBC-/IRQ1-/IN60-ECHO aus. Ganze Textplanes/Attribute/CRTC/BDA, die aktive SR3-Fontbank und Glyphen sind unabhängig gebunden: bei MS-DOS/EMM386 an das originale private EGA.CPI/CP850/16, bei FreeDOS an die eigene CP437-Artwork. Tatsächlicher DAC-/Pixelmaskenkontrast und der vorhandene RGB-Cache werden geprüft; keine Aufnahme oder native GL-Abnahme. Insgesamt 17370 spätere publizierte Stereo-PCM-Frames pro Seite sind gleich; kein aktiver PCM-DMA-Gast, keine neue unabhängige Ganzmix-/Audioqualitätsabnahme.

Die79direkten CPU-/SYS-Aufruffälle prüfen zusätzlich Register/Flags, FFFE/FFFF-Pufferüberträge, wirkliche PIO-Wörter, Teilcounts, letzten/ersten ungültigen Sektor, leeres Laufwerk, Reset/Tray/Lock und Fehler. Der gemeinsame77er-Vorherprüfer liefert auf dem alten SYS69PASS/8FAIL, auf der korrigierten Fassung77PASS. Zwei abschließende Interleavekontrollen sind in der11642er-Vollsuite enthalten. Der hypothetische22-Byte-INIT-Diagnosefall wird nicht als tatsächlicher DOS6.22-Fehler bezeichnet; dessen INIT ist24Byte lang.

Alle65Kandidaten-/Normal-/Starterdateien sind bytegleich; nur Tests-DLL/PDB ändern sich gegenüber11563, derSYS ist neu. Fünf VM-Module, Headless/GUI, fünf ROMs und Format1-Identität bleiben unverändert. Der installierte tatsächliche CLI lädt einen eigenen DOS/SYS-Schnitt, speichert nach10000weiteren Schritten und setzt mit exakt gleicher CPU/Bild/Zeit/ISO wie der10001-Schritt-Direktlauf fort, ohne weiteren POST. Keine neue Zustandsfunktion oder neuer Timer; Inventar46Typen/874Member/40Ereignisse.

Assembler-/INIT-/erster fehlgeschlagener Gast und die vor präzisem FreeDOS-Fehleroracle abgebrochene Matrix sind private Diagnosen und nicht Teil der endgültigen acht Starts. Ein schreibfreies Nachprüfen alter Artefakte erzeugt keine weiteren Gaststarts. Gesamtnachweis `artifacts/tests/m4-mscdex-release-path.txt`; öffentliche Reproduktion `scripts/test-mscdex.ps1` mit den sechs expliziten privaten DOS-Eingaben. Alle weiteren Originalmedien und persönliche Spiel-COWs bleiben ungeöffnet, keine breite Schutzlisten-Hashrunde.

Als nächster Produktschritt folgen Firmware/PCI-Erkennung und Managerintegration sowie die tatsächliche Windows-95-RTM-Installation, Originaltreiber, mehrere Starts, regulärer Shutdown und volle Windows-Fortsetzung. Vollständiges ATAPI, höhere S3-Farbtiefen/Beschleunigung, die 100-/133-MHz-Matrix und M4–M8 bleiben offen.
