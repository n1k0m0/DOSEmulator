# Eigene PCI-BIOS- und BIOS32-Firmware

Stand 27. Oktober 2026. Das zusätzliche Profil `dose-430fx-piix-s3-atapi-pcibios-dev-v1` erhält eine eigene sechste DOSEBios-Variante. **Im benannten Referenzumfang integriert:11819/11819 Regressionen,177 neue Aufruffälle, 0 Warnungen/Fehler.** Die bisherigen fünf ROM-Bytestrome und Profile bleiben erhalten. Windows 95 und die vollständige M4-Abnahme sind damit noch nicht nachgewiesen.

## Historischer Vertrag

Grundlage ist **PCI BIOS Specification Revision 2.1, 26. August 1994**, einschließlich des enthaltenen BIOS32 Service Directory Proposal Revision 0.4. [PCI-SIG: Revision und Datum](https://pcisig.com/PCIConventional/Specs/Firmware/Bios_2.1), [archivierter Originaltext](https://www-online.kek.jp/~yasu/Parallel-CAMAC/bios21spec.pdf). Die vollständige private Quellenkopie hat SHA256 `8C829F34BE7FE21BDECDEDFC576703BDD8BFE223CBFD0317E5B8A2D5AED0E6BB`.

Die Firmware ist eigener Gastcode. Sie greift mit echten IN/OUT auf die vorhandenen 82437FX-, ursprünglichen 82371FB- und S3-Funktionen zu. Sie ersetzt weder Konfigurationsregister noch Ressourcenmasken oder Gerätewirkungen durch fertige Hostantworten. Das neue Profil hält P54C-B5-Teilumfang, nominale 100 MHz, 32 MiB und den bestehenden S3-/ATAPI-Aufbau fest.

| Einstieg | Begrenzter eigener Vertrag |
| --- | --- |
| Real Mode / V86 | `INT 1Ah`, AH=B1h. Der eigene Handler benutzt den Callerstack und kehrt mit IRET zurück. Andere bisherige INT1A-Dienste bleiben erreichbar. |
| Geschützter 16-Bit-Aufruf | PUSHF und FAR CALL auf physisch `000FFE6Eh`; eigener Sprung zum INT1A-Handler. Code- und Stacksegment sind 16 Bit, mindestens 1024 Byte Stack. |
| BIOS32-Verzeichnis | Genau ein 16-Byte-ausgerichteter `_32_`-Header im System-ROM: Revision 0, Länge 1, gültige Summenprüfung, fünf reservierte Nullbytes und wirklicher 32-Bit-FAR-Einstieg. |
| BIOS32 `$PCI` | EAX=`49435024h`, EBX=0 liefert AL=0 sowie Basis, Länge und Offset des wirklichen PCI-Service. Unbekannte ID liefert AL=80h, unbekannte BL-Funktion AL=81h. |
| Geschützter 32-Bit-Service | Echter FAR CALL/RETF, sowohl mit flacher Basis 0 als auch mit ROM-Basis F0000h. Ausführung funktioniert mit execute-only CS und read-only DS; das ROM schreibt nur auf den eigenen Callerstack beziehungsweise ausdrücklich übergebene Ausgabepuffer. |

Der Originalvertrag verlangt beim BIOS32-Aufruf reservierte obere EBX-Bits null. DOSE ignoriert sie als ausdrücklich eigene Kompatibilitätserweiterung; ihre Nichtbeachtung wird nicht als historische Normpflicht ausgegeben. Die benutzte Spezifikation definiert für B101 **keinen EDI-Rückgabewert**. EDI bleibt erhalten; der 32-Bit-Einstieg wird tatsächlich über BIOS32 ermittelt.

Für die 16-Bit-Routingdienste B10E/B10F verlangen die Abschnitte 4.2.2/4.2.3 ausdrücklich DS mit physischer Basis F0000h und 64-KiB-Limit. Der normative DOS-Gast stellt diesen Wert her und restauriert danach seinen ursprünglichen DS. DOSEs 16-Bit-Tabellenkopie unterstützt zusätzlich andere DS-Basen durch unmittelbare Stores aus denselben zwölf Tabellenkonstanten. Vier getrennte Kompatibilitätsfälle prüfen diese eigene Erweiterung, einschließlich execute-only CS und read-only DS im PE16. Die ursprüngliche COM-DS-Probe verletzte den historischen Aufrufvertrag; ihre Nulltabelle ist kein nachgewiesener Fehler eines normgerechten Originalcallers.

Die kritische Konfigurationsarbeit erhält das wirkliche CF8-Latch, sperrt maskierbare Interrupts und restauriert danach Latch und Callerflags. Bei 16 Bit erfolgt die Rückkehr durch IRET. Beim 32-Bit-POPFD/RETF-Epilog besteht bei zuvor gesetztem IF eine mögliche Instruktionsgrenze zwischen Flagsrestaurierung und Rückkehr. Hierfür wird keine atomare Unterbrechungs- oder NMI-/SMM-/Buspräzision behauptet. Es gibt kein zusätzliches STI und keine synthetische Task-/IRETD-Rückkehr.

## Dienste und echte Ressourcenwirkungen

| Dienst | Wirkung |
| --- | --- |
| B101 Installation Check | AH=0, AL=01h (Mechanismus 1), BX=0210h, CL=0 und EDX=`PCI `. Keine Special-Cycle-Werbung. |
| B102 / B103 Find | Vendor/Device beziehungsweise untere 24 Klassenbits werden aus echten Konfigurationsreads der vier festen Funktionen 00:00.0, 00:03.0, 00:07.0 und 00:07.1 geprüft. Kein allgemeiner Bridgescan, keine synthetischen IDs. |
| B108/B109/B10A | Byte-/Word-/Dword-Read über CF8/CFC. DI muss innerhalb der 256 Bytes liegen und zur Breite ausgerichtet sein; ungültige Register werden vor dem Datenportzugriff abgewiesen. |
| B10B/B10C/B10D | Entsprechender echter Write; BAR-, Command-, W1C- und Dekodierwirkungen bleiben den vorhandenen Geräten überlassen. Fehlende BDFs behalten die tatsächliche Open-Bus-/RMAS-Antwort. |
| B10E IRQ Routing Options | Drei gepackte 16-Byte-Einträge für die eigenen Geräte 0, 3 und 7. Zu kleiner Puffer erhält nur die erforderliche Länge 48 und Fehler89h; Daten und Wachen bleiben unberührt. |
| B10F Set PCI IRQ | Für S3 00:03.0, CL=0Ah (INTA), CH=0Bh wird PIRQA wirklich auf ISA11 und ELCR11 auf Level gesetzt. Andere Routen werden vor partiellen Writes mit AH=88h abgewiesen. |
| Sonstige Funktionen | AH=81h/CF. Insbesondere B106 erzeugt keinen vorgetäuschten Special Cycle. |

Die Ressourcenkarte ist eine **eigene begrenzte Boardentscheidung**, keine nachgebildete historische Hauptplatine. Alle drei Geräteeinträge bezeichnen aufgelötete Komponenten mit Slot0; nur S3 INTA ist angeschlossen: Link60h, Bitmap0800h. Die übrigen Pins sind null. Die beiden PIIX-Funktionen gehören zum selben Geräteeintrag. Host und PIIX erhalten keinen erfundenen INTA-Anschluss.

Die PIRQ-Resetrouten bleiben zunächst deaktiviert. Die Firmware vergibt IRQ11 erst auf tatsächliche B10F-Anforderung. Sie verändert dabei weder die PIC-Programmierung noch S3 InterruptLine: Laut Primärvertrag aktualisiert der Caller das Line-Register und die PIC-Masken. Der B10E-BX-Rückgabewert enthält ausschließlich die tatsächlich konfigurierte eigene dedizierte PCI-IRQ11-Zuordnung. Fremde direkte PIRQ-Programmierungen auf gemeinsame ISA-Ressourcen werden nicht als exklusive PCI-IRQs ausgegeben. Daraus folgt noch keine S3-Interruptproduktion oder Treiberabnahme.

## Integration und Zustand

Das neue Profil ist eine zusätzliche feste Plattformauswahl. Definition, Factory, CLI, Kaltstart und Hardwareanzeige verwenden dasselbe Profil. Ein neu eingeführtes konfigurierbares Firmwarefeld oder nachträglicher CPU-/Chipsatztausch ist nicht erforderlich. Das bestehende ATAPI-Profil behält seine bisherige ROM-Auswahl.

Die Firmware fügt keine Hostcallbacks, Timer oder neuen Gerätezustandsfelder hinzu. Ein laufender Service liegt als gewöhnlicher CPU-/RAM-/Stack-/CF8-/Gerätezustand im vollständigen Snapshot. Die neue Runtime und Contracts ändern jedoch die buildgebundene Format1-Identität. Alte Sicherungen werden nicht still migriert; ein tatsächlicher CLI-Ablehnungsnachweis und eine neue CLI-Fortsetzung gehören zur Freigabe.

Ein BIOS32-Header ist keine PnP-/APM-Signatur und kein Windows-Erkennungsnachweis. PnP-BIOS, ISA-PnP für die vorgesehenen späteren Karten sowie APM/SMM behalten ihren gesonderten Umfang. Der DOS-CD-Pfad benutzt weiterhin den [eigenen SYS und originales MSCDEX](DOS-CDROM-DRIVER.md); die Firmware erfindet keinen CD-Bootdienst.

## Prüfweg und offene Abnahme

Die direkten CPU-Aufruffälle prüfen Real Mode, simulierten INT-/16-Bit-Far-Aufruf, V86 mit echtem TSS/LTR und I/O-Bitmap, PE16 sowie PE32 mit zwei Segmentbasen. Register, Flags, Stackwachen, wirklicher CF8-Erhalt, Konfigurationsbreiten, BAR-Wirkung, Fehler und Ressourcenrouten werden unabhängig erwartet. Die eigenen V86-Fixturegrenzen werden vor dem ersten echten Caller-Return geprüft; ein nachfolgender privilegierter HLT wird nicht als V86-Erfolg gezählt.

Der eigene DOS-Gast verwendet unter originalem EMM386 dessen echte VCPI-Aufrufe und alle 1024 gelieferten PTEs. Er ersetzt den Monitor nicht. Vorgesehen sind zwei vollständige 32-MiB-Schnitte während wirklicher 16- und 32-Bit-Firmware-Konfigurationsaufrufe, bei offenem DOS-Dateihandle und roher x87, anschließend tatsächliche DOS-Dateien, CD-Zugriff, DOS-Rückkehr und ein neuer KBC-/IRQ1-ECHO auf allen drei PCs. Unabhängige Negativoracles und vollständige Wiederholungspaare sind vor der Produktübernahme erforderlich.

Reproduktion nach dem normalen Build mit sechs ausdrücklich gewählten privaten DOS-Fixtures:

```powershell
scripts/test-pci-bios.ps1 -Disk <own-msdos.img> -Overlay <own-msdos.cow> `
  -FreeDosDisk <own-freedos.img> -ConfiguredDisk <own-emm386.img> `
  -NoEmsOverlay <own-noems.cow> -RamOverlay <own-ram.cow>
```

Nur frische eigene Basen/Journale sind Testziele. Originalmedien, persönliche Spiel-COWs und C64 bleiben unberührt. Keine nativen Windows-Eingaben, Fokus-/Maus-/GL-Prüfung, Aufnahme, Hostendpunkte, Commits oder Pushes. Nach dem Firmware-/Managerpfad bleiben die tatsächliche Win95-RTM-Installation mit normaler Erkennung, Originaltreiber, höhere S3-Farbtiefen und benötigte Beschleunigerwirkungen, mehrere Starts, Sound/Maus/IDE, regulärer Shutdown und volle Windows-Fortsetzung offen. Die 100-/133-MHz-/32-MiB-Matrix und M4–M8 werden nicht verkleinert.

## Endgültiger Nachweis

Acht endgültige Kaltstarts (MS-DOS, FreeDOS, originales EMM386 NOEMS/RAM je zweimal) mit 16 echten vollständigen Fortsetzungen bestehen. 1592 ganze CPU-/32-MiB-RAM-/VRAM-/Geräte-/Scheduler-Vergleiche und 4212 negative Gastoracles, vier vollständige Wiederholungspaare. Je Lauf tatsächlicher 16-Bit-Cut bei Phase 2/Case 99 und 32-Bit-Cut bei Phase 6/Case 199, offener DOS-Datei und roher x87. Alle drei PCs kehren zu ursprünglichem DOS zurück und führen danach einen neuen wirklichen KBC-/IRQ1-/IN60-ECHO aus. Die ganze vorherige und spätere Serviceantwort wird auf jeder Maschine separat beobachtet, ohne gemeinsame mutable Beobachterinstanz.

280 wirkliche BIOS16- und 280 BIOS32-Serviceaufrufe, 24 BIOS32-Abfragen. Die drei Dateien je PC enthalten 512 Proofbytes, 10 rohe x87-Bytes und 197 echte R:-Dateibytes. Originales MSCDEX 2.23 und die übrigen DOS-Dateien bleiben erhalten. Ganze Textplanes/Fontbänke/Original-CPI-/eigene Artwork-Glyphen, BDA/CRTC/Cursor 4 und tatsächlicher DAC-/Pixelmaskenkontrast sind gebunden, keine Aufnahme/GL-Abnahme. 9842 danach publizierte Stereo-PCM-Frames pro Seite sind tatsächlich gleich; kein neuer aktiver PCM-DMA-/Ganzmix-/Audioqualitätsnachweis.

Das originale EMM386 liefert für FF000h eine eigene physische Spiegelung (in der geprüften NOEMS-Konfiguration 15B000h). Die ganze 4096-Byte-Seite bleibt gegenüber dem früheren Schnitt unverändert; gegenüber dem eigenen Erwartungs-ROM unterscheiden sich ausschließlich die bereits vom Monitor gepatchten vier Operandenbytes FFFF1h..FFFF4h des erhaltenen EA-Farjumps. Alle übrigen 4092 Bytes und die anderen 15 ROM-Seiten bleiben exakt gebunden. Sämtliche 1024 Original-DE01-PTEs bleiben erhalten, nur tatsächliche zusätzliche A/D-Bits sind erlaubt. Keine Monitorersetzung oder pauschale Identitätsabbildung.

Vier eigene DS-Kompatibilitätsfälle liefern auf eingefrorenen Vorhermodulen 0/4 und nach Erweiterung 4/4; die 173 Norm-/übrigen Aufruffälle bleiben positiv. Die erste COM-DS-Annahme bei B10E/F und der eigene unvollständig gesetzte ECX-Zähler vor a32-REP waren Testgastfehler. Assembler-/Fixture-/Monitor-/Aliasdiagnosen und der kontrolliert beendete eigene fehlerhafte NOEMS-Prozess zählen nicht zu den endgültigen acht Starts. Reine schreibfreie Nachprüfungen erzeugen keine zusätzlichen Gaststarts.

Alle 65 Produktdateien sind zwischen Kandidat, unabhängigem Normalbuild und gemeinsamem Starter gleich. 18 ändern sich gegenüber 11642; CPU/Core und fünf alte ROMs bleiben bytegleich. Der neue Runtime-/Contractsstand besitzt eine andere Format1-Buildidentität. Tatsächlicher alter 11642er-CLI-Import scheitert vor Zweig mit bestehendem nichtnulligem Ausnahmeabbruch, kein kontrollierter Exit 1 behauptet. Die installierte neue CLI setzt denselben eigenen DOS-/Firmwarezustand bei gleicher CPU/Bild/Zeit/ISO ohne weiteren POST fort. Inventar 46 Typen/874 Member/40 Callbackkörper unverändert; keine neuen Timer.

Privater Gesamtnachweis: artifacts/tests/m4-pci-bios-release-path.txt. Die vier Cut-UUIDs werden einzeln geprüft und ausschließlich innerhalb dieser Cut-Metadaten für Wiederholung kanonisiert; übrige Cut-Metadaten bleiben vollständig erhalten. Die kompletten drei Endmedienarrays einschließlich Basis-/Journalhashes, Größen, Bindung und virtueller Zeit werden zusätzlich zwischen allen Seiten und Wiederholungen verglichen. Keine breite Schutzlisten-/Quellenhashrunde, persönliche Spiel-COWs bleiben ungeöffnet. Windows 95 und M4–M8 bleiben offen.

Bei der expliziten Starterübernahme verweigerte der Baselineguard zunächst jeden Write: Alle 65 Starterdateien entsprachen bereits exakt dem fertig geprüften Kandidaten. Der Zeitpunkt beziehungsweise Auslöser dieser vorherigen Outputaktualisierung wird nicht zugeordnet. Die abschließende Reconciliation prüfte sämtliche 65 Dateien gegen Kandidat und Normalbuild sowie das vollständige 11642er-Archiv; sie schrieb keine Dateien erneut. Unterschiedliche Datei-IDs und fehlende Hardlinks/Release-Junctions sind separat festgehalten. Vor weiteren Builds den tatsächlichen gemeinsamen Output erneut prüfen.
