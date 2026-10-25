# FDC und VDS unter originalem EMM386

Dieses M3-Teilpaket prüft Schreib-/Lese-/Verify-/Format-Pfade des Diskettencontrollers mit dem lokalen deutschen MS-DOS 6.22 und dessen originalem EMM386.EXE. Die Datei meldet Version 4.48. Der vorherige EMM386-Nachweis umfasste nur das Laden von Programmen über FDC. M3 insgesamt bleibt offen.

## DMA-Grenze des BIOS-Puffers

Der eigene Gast-BIOS-Code kopiert jeden Datensektor über die reservierten EBDA-Bytes 200h–3FFh. Das erhält nicht zusammenhängende, paginierte und Segmentgrenzen überschreitende Aufruferpuffer. Die CPU führt die Kopien aus; die DMA-Hardware adressiert den Speicher unabhängig von der CPU-MMU. Ein residenter V86-Monitor kann DMA-Portzugriffe abfangen und übersetzen.

Ein neuer Vorabtest schützt zusätzlich den BIOS-Puffer selbst: **Eine verlegte EBDA kann dessen DMA-Adresse über eine 64-KiB-Kanalgrenze schieben.** Der 8237 zählt dabei seine Seitenadresse nicht weiter. Vor Datentransfers prüft der BIOS-Code daher 512 Bytes, vor FORMAT die tatsächliche Länge der CHRN-Liste aus INT1E. Eine überlaufende Adresse liefert **AH=09h/CF**, bevor der BIOS-Code Aufruferdaten kopiert, einen FDC-Befehl sendet oder eine neue DMA-Adresse programmiert. Der FORMAT-Abschluss maskiert weiterhin Kanal 2. Die Statuswiederholung über AH01 verwendet den gespeicherten BDA-Status.

Das ist eine ausdrückliche Grenze des aktuellen 1-KiB-EBDA-Layouts: Ein ungeeigneter interner Puffer wird abgewiesen. Es wird weder fremder konventioneller Speicher als Ersatzpuffer benutzt noch ein nicht vorhandener DMA-Seitenübertrag erfunden. Das ist keine Zusage, dass jede beliebige EBDA-Verlagerung bedient werden kann. In den geprüften realen EMM386-Konfigurationen ist die EBDA geeignet und konventionell identisch abgebildet. Zusätzliche UMB-/Shadow-/Kartenspeicher- und anders paginierte EBDA-Konfigurationen bleiben gesondert zu prüfen.

22 neue Regressionen prüfen die letzten gültigen und ersten ungültigen Adressen sowie kurze FORMAT-Listen, die trotz einer für 512 Bytes ungeeigneten Lage passen. Sie prüfen AH02/03/04/05, Schreibschutz der Basisdateien, unveränderte Aufrufer-/Nachbarbereiche bei AH09, echte FDC-/DMA-Aktivität bei Erfolg, Daten über Aufrufer-Segmentgrenzen und die Wiederöffnung der privaten Schreibjournale. Der gemeinsame Build bestand beim Abschluss dieses Teilpakets damit 942 Regressionen. Das folgende [Call-Gate-Paket](X86-CALL-GATES.md) besitzt inzwischen 1049 Regressionen.

## Eigener VDS- und FDC-Gast

`tests/guests/fdc-vds-test.asm` verwendet die tatsächlichen INT4B-Dienste des geladenen EMM386. Es gibt dafür keine Host-VDS-Implementierung oder BIOS-/DOS-Abkürzung.

Der Gast verkleinert seinen DOS-Block, reserviert 128 KiB konventionellen Speicher und wählt einen 512-Byte-Puffer, der 16 Bytes vor einer 64-KiB-Grenze beginnt. Er prüft das VDS-Anwesenheitsbit, Version 1.0 und eine abgewiesene Versionsabfrage mit reserviertem Flag. Lock/Unlock verwenden die Originaldienste 8103h/8104h: keine automatische Seitenumordnung, 64-KiB-Ausrichtung und tatsächliche Kopien in beziehungsweise aus einem zugewiesenen VDS-Puffer.

Für jeden Schreib-/Lesevorgang programmiert der eigene Gasttreiber **FDC und DMA2 direkt mit der zurückgegebenen physischen Adresse**. 810Bh/810Ch deaktivieren und aktivieren die automatische DMA-Übersetzung um diesen Zugriff herum. Die getestete EMM386-Instanz meldet einen maximalen Puffer von 32 KiB und liefert für die kleinen Transfers physisch 00110000h. Diese Werte werden beobachtet, nicht als Emulatorantwort vorgegeben. Separate BIOS-Aufrufe prüfen danach AH03/02/04 auf demselben virtuellen Puffer.

Unter `RAM` belegt der Gast zusätzlich zwei EMS-Seiten und bildet sie in umgekehrter Reihenfolge auf benachbarte Seitenrahmenfenster ab. Der zweite 512-Byte-Puffer beginnt bei Rahmenoffset 3FF0h. Er überschreitet dadurch eine 16-KiB-Bank- und eine 4-KiB-Seitengrenze; VCPI-DE06h weist zwei tatsächlich nicht zusammenhängende physische Seiten nach. Derselbe direkte VDS-/DMA- und BIOS-Pfad muss auch hier alle 512 Musterbytes erhalten. Anschließend werden die vorherige EMS-Abbildung, der Handle und die freie Seitenmenge wiederhergestellt.

Eine CHRN-Liste über das Ende eines Aufrufersegments formatiert abschließend einen getrennten Track auf der privaten Diskette B. Alle VDS-Puffer werden entsperrt, die Übersetzungsaufrufe sind ausgeglichen, der zusätzliche DOS-Speicher wird freigegeben und der originale COMMAND.COM-Prompt wird wieder erreicht. Der Headless-Stopp kann zu diesem Zeitpunkt innerhalb eines EMM386-Interrupt-Handlers liegen; er bedeutet keine Wiederherstellung eines vollständigen CPU-/Geräteabbilds.

## Unabhängige Prüfung

`tests/verify-fdc-vds.py` liest RAM, Seitentabellen, das selbst assemblierte Programm, echte Portprotokolle und DOSCOW01-Journale. Es prüft die ausgeführten Programmbytes, Verwaltungsdaten, kopierten 512-Byte-Muster, jede FDC-Kommando-/Ergebnisfolge, die tatsächlich programmierten DMA-Adressen und **alle 2.880 Sektoren** der privaten Diskette einschließlich unberührter Nachbarn. Hashgültig verfälschte Journalinhalte müssen ebenfalls scheitern.

Die Matrix startet NOEMS und RAM jeweils zweimal kalt, vergleicht virtuelle Laufzeit und Schreibjournal und führt zusätzlich einen Gast ohne VDS aus. Dieser muss seine fehlende Schnittstelle korrekt melden und Diskette B vollständig unberührt lassen. 20 gezielt verfälschte Oracles werden abgewiesen. Die Wiederholungen schließen eine spätere allgemeine VDS-Abnahme nicht ab: Scatter/Gather, die separaten Request-/Copy-Dienste, alle Fehler-/Mehrfachsperrfälle, andere DMA-Kanäle, Busmaster und Windows sind hier nicht geprüft.

Zusätzlich führt das originale Microsoft FORMAT.COM unter NOEMS und RAM jeweils drei Kaltstarts aus: vollständiges FORMAT von 80 Zylindern mit beiden Köpfen, echte DOS-Dateierzeugung, anschließendes Lesen und Löschen nach Neustart. Die unabhängige FAT-/Protokollprüfung erhält nun die tatsächliche BDA-/EBDA-Adresse aus dem privaten RAM-Dump; die frühere feste POST-Adresse 9FC0h war als Oracle unter einem Speichermanager falsch. Basisimages und installierte Festplatten bleiben in diesen sechs Läufen unverändert.

## Reproduktion

```powershell
.\scripts\test-fdc-vds.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow>
```

Ohne `-SkipBuild` wird vorher gebaut. Das Skript erwartet die unveränderte kanonische DOS-Installation. Es erzeugt eine neue private materialisierte Basis, lässt DOS zwei neue Konfigurationen schreiben und prüft alle übrigen installierten Dateien bytegenau. Proben, RAM-Dumps, Portprotokolle, Medien und Journale bleiben unter ignorierten `artifacts/`. Kein originales Betriebssystem-/Spiel-/ROM-Image wird verändert oder veröffentlicht. Die Prüfung verwendet keine Windows-Fenster, Fokusversuche oder nativen Eingaben.

## Primärquelle

Registerverträge, DDS-Felder und Übersetzungszähler folgen Microsofts ursprünglicher Application Note PW0519, Revision September 1992, archiviert als [Q93469: Virtual DMA Services](https://jeffpar.github.io/kbarchive/kb/093/Q93469/). Es wurde kein fremder VDS-Implementierungscode übernommen.
