# 82437FX A2: begrenzter DOSE-Hostcontroller

Aktueller zusätzlicher Teilumfang: [PIIX-Funktion1 mit realem IDE-/PCI-Busmasterpfad](PIIX-IDE.md), Build 10755, separates eigenes Profil und acht DOS-/EMM386-Starts/16 Fortsetzungen. Das reine Hostprofil und seine historische Abnahme bleiben erhalten. CF9/INIT/Resetdomänen und vollständige 430FX-/Windows-Plattform bleiben offen.

Aktueller Folgeschritt: Das getrennte [PIIX-IRQ-Validierungsprofil](PIIX-IRQ.md) ergänzt auf Build 10141 eine ursprüngliche Fn0-/PIRQ-/ELCR-Vertikale mit acht DOS-/EMM386-Fortsetzungen. Das hier beschriebene reine Hostprofil und seine 9125er-Abnahme bleiben unverändert eingegrenzt. Fn1/IDE/CF9/INIT und die vollständige Plattform bleiben offen.

Stand: 27. Oktober 2026. Der implementierte Produktpfad verbindet einen benannten 82437FX-A2-Hostcontroller mit dem bestehenden P54C-Teilmodell, echten CPU-Zugriffen auf PCI-Konfigurationsports, DRAM-Dekodierung und PAM-Shadow-RAM. Der geprüfte Release-Build besteht 9125/9125 Regressionen ohne Warnungen/Fehler. Daraus folgt keine Abnahme eines vollständigen 430FX-/PIIX-Boards oder von Windows 95; M4 bleibt offen.

## Primärvertrag und feste Plattform

Grundlage ist das [Intel-Datenblatt 290518-002, Dezember 1996](https://bitsavers.trailing-edge.com/components/intel/pci/290518-002_82430FX_82437FX_82438FX_PCIset_DATASHEET_199612.pdf): CONFADD/CONFDATA in Abschnitt 3.1, Konfigurationsregister in Abschnitt 3.2, insbesondere PAM 3.2.14 und DRB 3.2.15. Das [Intel-Spezifikationsupdate 297733-001](https://ftp.pcrebuilding.altervista.org/9/download/5ae1a4e11cf5e_297733-001_Intel_430FX_PCIset_Specification_Update.pdf), Tabelle „Component Identification via Programming Interface (TSC)“, ordnet A2 der Revision 02h zu. Die dort verkürzte Geräte-ID `122h` wird nicht übernommen: Das Datenblatt nennt ausdrücklich `122Dh`.

| Eigenschaft | Implementierter Vertrag |
| --- | --- |
| Profil | `dose-430fx-host-dev-v1`, ausdrücklich eine eigene DOSE-Validierungsplatine für den Hostcontroller-Teilumfang. |
| CPU | `p54c-b5-subset-development`, bei Konstruktion festes `IntelP54cB5Subset`; dieselben begrenzten [P54C-Verträge](P54C-PC.md) mit CPUID 0135h, CR4.TSD/DE und genau TSC-MSR10h. |
| Clock | Nominale virtuelle 100 MHz; CPU und TSC verwenden den eigenen PC-Scheduler. Keine historische Befehlsclock-, FSB-, Speicherlatenz- oder Echtzeitkalibrierung. |
| RAM | Genau 32 MiB installierte physische Speicherbytes. Nach frischer Konstruktion dekodiert der Host zunächst 8 MiB. Unsichtbare Bytes bleiben vorhanden und werden vollständig gesichert. |
| PCI-Funktion | Genau `00:00.0`, Intel `8086:122D`, Revision `02`, Klasse `060000`, Header-Typ `00`. Weitere BDFs werden nicht erfunden. |
| Übrige Geräte | Die bisherigen DOSE-AT-Geräte mit ihren geprüften Ressourcen; die zwei bestehenden ATA-Kanäle sind weiterhin die eigenen bisherigen Controller. Es entsteht dadurch kein PIIX-IDE-Gerät. |
| Auswahl | Profil-ID, CPU, 100 MHz und 32 MiB bilden ein untrennbares validiertes Tupel. Alte 486-/33-MHz- und P54C-AT-/90-MHz-Profile behalten ihre bisherigen Auswahlgrenzen und erhalten keinen PCI-Hostcontroller. |

Factory und Owner erzeugen das vollständige neue Hardwaretupel vor dessen Übernahme. Ein CPU-Austausch nach Konstruktion oder die alleinige Übernahme einer neuen Taktzahl ist kein zulässiger Profilwechsel. Das Hardwaremenü hält CPU und RAM dieses Hostprofils fest. Der hier freigegebene Umfang gilt ausschließlich für diese eigene Host-Validierungsplatine; eine vollständige historische Boardfreigabe bleibt offen.

## Konfigurationszugriffe und Status

CF8h übernimmt ausschließlich vollständige Dword-Zugriffe. Bit 31 aktiviert das Datenfenster; Bus, Gerät, Funktion und Register entsprechen Mechanismus 1. Das gespeicherte Adresslatch besitzt die Maske `80FFFFFCh`. Reservierte CONFADD-Bits lesen im bestehenden DOSE-Transport null; dies ist keine vermessene Siliziumantwort.

CFCh–CFFh akzeptiert vollständige Byte-, Wort- und Dword-Transaktionen innerhalb des Vierbytefensters. Die gewählte Funktion erhält genau einen Zugriff mit den ursprünglichen Byte-Lanes. Ungültige API-Breiten, unvollständige Fensterzugriffe und Konfigurationszugriffe über Byte FFh hinaus führen zu keinen versteckten Teilwirkungen. Eine abgewiesene direkte Funktions-API-Transaktion verändert keine Register oder DRAM-Dekodierung.

| Register | Reset | Schreibbare Bits und Wirkung |
| --- | --- | --- |
| 00h–03h VID/DID | `122D8086h` | Fest, schreibgeschützt. |
| 04h–05h PCICMD | `0006h` | Nur Bit 1/MAE beschreibbar (`0002h`). BME/Bit 2 ist fest eins; übrige Bits fest null. MAE ist die spätere PCI-Master-Annahmefreigabe und schaltet CPU-RAM nicht ab. |
| 06h–07h PCISTS | `0200h` | Bits 13/RMAS und 12/RTAS sind W1C (`3000h`); DEVSEL ist fest medium (`0200h`). Schreiben von null erhält einen gesetzten Fehler. |
| 08h–0Bh RID/PI/SUBC/BCC | `06000002h` | Fest. |
| 0Dh MLT | `00h` | Maske `F8h`; keine modellierte PCI-Zeitscheibe. |
| 0Fh BIST | `00h` | Keine BIST-Ausführung, Schreiben ohne Wirkung. |
| 50h PCON | `00h` | Maske `EFh`; lesbare Steuerlatches ohne modellierte Busarbitration oder Bursting-Latenz. |
| 52h CC | `02h` | Maske `FBh`; gewählte Cache-Straps ohne bestückten L2. Keine simulierten Cachelines, KEN#-/NA#- oder Flush-Zeitabläufe. |
| 57h DRAMC | `01h` | Maske `CFh`; HEN 7..6 steuert die unten beschriebenen CPU-Löcher. EDO-/Refreshfelder bleiben Latches. |
| 58h DRAMT | `00h` | Maske `7Fh`; lesbare Timingfelder ohne zusätzliche Speichertakte. |
| 59h PAM0 | `00h` | Maske `70h`, nur das obere Attributfeld für F0000h–FFFFFh. |
| 5Ah–5Fh PAM1–6 | je `00h` | Je Maske `77h`, zwei unabhängige Attributfelder. |
| 60h–64h DRB0–4 | je `02h` | Je Maske `3Fh`; DRB4 bestimmt die dekodierte RAM-Obergrenze. |
| 68h DRT/DAT | `00h` | Maske `1Fh`; DRAM-Zeilentypen ohne getrennte EDO-/Page-Mode-Timings. |
| 72h SMRAM | `02h` | In dieser Teilmenge bewusst nurlesbar. Die realen SMRAME-/DOPEN-/DCLS-/DLCK-Funktionen sind unimplementiert, nicht als reservierte Hardwarebits umzudeuten. |

Andere Konfigurationsbytes lesen null und ignorieren Schreibzugriffe in dieser begrenzten Implementierung. Schreibmasken und W1C werden je tatsächlich ausgewählter Byte-Lane angewendet; bei einer breiten Transaktion wird eine erforderliche DRAM-Dekodieränderung erst nach den angenommenen Registerbytes veröffentlicht.

Ein vollständiger aktivierter Konfigurations-Lese- oder Schreibzugriff auf eine nicht registrierte BDF liefert offene Lesebits beziehungsweise erreicht kein Ziel und setzt beim festen Host RMAS. Die reale CPU-Portwirkung löst diesen Status aus. Deaktivierte Konfiguration, schmale CF8h-Zugriffe und unvollständige Datenfenstertransaktionen setzen RMAS nicht. RTAS ist als W1C-/Zustandsbit enthalten, besitzt in diesem Teilumfang aber keinen erzeugenden PCI-Target-Abort-Pfad. Elektrische PCI-Zyklen und allgemeine PCI-Fehlerpräzision sind nicht abgenommen.

## PAM und ROM

Der Hostcontroller besitzt das feste CPU-Mapping C0000h–FFFFFh. Shadow-RAM verwendet die entsprechenden bereits vorhandenen physischen Bytes in `Memory.Ram`; es gibt keinen zweiten Shadowpuffer. Der F-ROM-Inhalt wird bei Konstruktion als unveränderliche eigene 64-KiB-Kopie gebunden. Die hohen Reset-ROM-Aliase bleiben getrennte unveränderliche ROM-Mappings.

| Registerfeld | Physischer Bereich |
| --- | --- |
| PAM0 oberes Nibble | F0000h–FFFFFh, ein 64-KiB-Bereich |
| PAM1 unten/oben | C0000h–C3FFFh / C4000h–C7FFFh |
| PAM2 unten/oben | C8000h–CBFFFh / CC000h–CFFFFh |
| PAM3 unten/oben | D0000h–D3FFFh / D4000h–D7FFFh |
| PAM4 unten/oben | D8000h–DBFFFh / DC000h–DFFFFh |
| PAM5 unten/oben | E0000h–E3FFFh / E4000h–E7FFFh |
| PAM6 unten/oben | E8000h–EBFFFh / EC000h–EFFFFh |

Jedes Nibble enthält RE/Bit 0, WE/Bit 1 und CE/Bit 2. RE wählt für CPU-Lesezugriffe das entsprechende verfügbare DRAM; WE erlaubt unabhängig davon dessen Änderung. RE=0 liest im F-Bereich das unveränderliche ROM und im hier unbestückten C–E-Bereich FFh. WE=0 verändert die Shadowbytes nicht. Damit ist der tatsächliche Kopierpfad ROM lesen bei RE=0/WE=1, in Shadow-RAM schreiben und anschließend RE=1/WE=0 auswählen implementiert.

CE bleibt lesbar, verändert aber weder die Byteauswahl noch virtuelle Laufzeiten. Laut Intel soll CE bei RE=0 nicht gesetzt werden. Solche Attributkombinationen bleiben in DOSE gelatcht; ihre RE-/WE-Wirkungen folgen der beschriebenen begrenzten Politik und sind keine zusätzliche Siliziumzusage. Falls RE verfügbares DRAM verlangt, dessen Adresse jedoch außerhalb der aktuellen DRB4-Dekodierung liegt, liefert DOSE FFh und verwirft entsprechende WE-Schreibzugriffe. Auch dieses Verhalten bei Fehlprogrammierung ist ausdrücklich die eigene begrenzte Antwort.

Die PCI-seitig weitergereichten C–E-Adressen besitzen in dieser Platine keine zusätzlichen Speicherziele. Sie lesen FFh und verwerfen Schreibzugriffe, ohne dafür einen zusätzlichen RMAS-Pfad zu behaupten. Es gibt keine automatische BAR-Anbindung, Option-ROM-Registrierung, versteckte Kopie fremder Firmware oder allgemeine PCI-Memory-Routing-Abnahme. A0000h–BFFFFh bleibt beim bisherigen VGA-Mapping; SMM/SMRAM ist dort nicht implementiert.

## DRAM-Obergrenze und Löcher

DRB4 gilt in Einheiten von 4 MiB. Die CPU-Dekodierobergrenze ist `min(DRB4 * 4 MiB, 128 MiB, installierte Bytes)`. Im festen 32-MiB-Produktprofil kann sie deshalb höchstens 32 MiB betragen. Der Resetwert 02h ergibt zunächst 8 MiB. Herabsetzen löscht keine Speicherbytes; Zugriffe auf nicht dekodiertes gewöhnliches RAM lesen FFh und schreiben keine Daten. Wiederanheben macht dieselben gespeicherten Bytes sichtbar. Mapped Devices/ROM behalten ihre ausdrückliche Priorität.

DRB0–3 sind korrekt maskierte, gespeicherte Registerlatches. Eine Simulation einzelner RAS-Zeilen, SIMM-Bänke oder Aliasierungen bei widersprüchlichen Reihenfolgen ist nicht enthalten. Intel verlangt DRB-Bit 0 für korrekte Programmierung auf null; DOSE erhält es als Teil des dokumentierten Sechsbitregisters und verwendet auch bei einer solchen Fehlprogrammierung die genannte lineare DRB4-Obergrenze. Es wird keine universelle Antwort auf fehlerhafte Banktabellen behauptet.

| DRAMC.HEN 7..6 | CPU-DRAM-Loch |
| --- | --- |
| `00b` | keines |
| `01b` | 80000h–9FFFFh, 512–640 KiB |
| `10b` | F00000h–FFFFFFh, 15–16 MiB |
| `11b` | bei Intel reserviert; in DOSE gelatcht mit ausdrücklich eigener Rückfallantwort „kein Loch“ |

Ein Loch verbirgt die betroffenen DRAM-Bytes, liest im hier unbestückten weitergereichten Bereich FFh und verwirft Schreibzugriffe. Es remappt keine Bytes oberhalb der installierten Kapazität. RAM-Spans und schnelle breite RAM-Zugriffe dürfen das Loch oder den dekodierten Top nicht umgehen. Die vorhandene A20-Faltung geschieht vor der CPU-Adressdekodierung. Physische Speicherzugriffe künftiger PCI-Master benötigen einen eigenen, davon getrennten Pfad.

## Firmware und Resetdomänen

Die eigene Host-Firmware wird aus `dose-bios.asm` mit `DOSE_430FX_HOST=1` als separate 64-KiB-Variante gebaut. Sie führt nach dem Setzen von Real-Mode-Segmenten und Stack, noch vor Vektoren, BDA und POST, folgende echte Gast-OUTs aus:

1. CF8h bekommt `80000060h`, anschließend CFCh das Dword `08080808h` für DRB0–3.
2. CF8h bekommt `80000064h`, anschließend CFCh das Byte `08h` für DRB4 und damit 32 MiB CPU-DRAM.
3. CF8h wird mit einem Dword null deaktiviert.

Die ROM-Inventur nennt ausdrücklich `P54C B5 / 430FX host subset`. Die bestehenden öffentlichen `CreateBios`-/`CreateBiosForDisks`-Signaturen bleiben erhalten. Der bisherige Default-ROM-Bytestrom bleibt unverändert; nur die explizite Hostplatine verwendet die neue Variante. Ein Vergleich der generierten Quellenmetadaten allein wäre dafür kein ausreichendes Byteoracle.

Ein CPU-only Hotreset erhält versorgten Hostcontroller, PAM, Status, DRB, Loch, Konfigurationsadresslatch, physisches RAM, Geräte und Scheduler. CPU-eigene CR4-/TSC-Resetantworten folgen dem bestehenden P54C-Vertrag. Die anschließend erneut laufende Host-Firmware programmiert die DRB-Werte wieder auf 32 MiB; das ist ein ausgeführter Firmwarepfad und kein stiller zusätzlicher Controllerreset. Bei absichtlich verändertem lesbarem F-Shadow bestimmt dessen Inhalt auch nach dem Sprung aus dem hohen Resetalias die weitere Ausführung.

Ein tatsächlicher Kaltstart/PowerCycle konstruiert neue Host-/PCI-Instanzen und setzt RAM-/Register-/Latchwerte gemäß den obigen Defaults zurück. CF9h, PIIX-Resetsteuerung und zusätzliche historische Resetdomänen sind nicht implementiert. Die Methode `ResetAddress` bleibt ausschließlich das explizite Zurücksetzen des PCI-Adresslatches; sie setzt keinen Hostcontroller zurück.

## Vollständiger Zustand und Ablehnungen

Das Produktgraph besitzt für dieses Profil genau den festen Hostcontroller und seinen PCI-Bus. Die Speicherpeer-Referenz und die genaue registrierte BDF/Objektreferenz werden vor Erfassung geprüft. Zusätzliche PCI-Funktionen oder eine vom Host gelöste RAM-Obergrenze/Lochkonfiguration können nicht still aus einer vollständigen Sicherung verschwinden.

Gesichert werden die 256 Konfigurationsbytes, CF8h, der Decoderzustand sowie alle installierten 32 MiB RAM einschließlich unsichtbarer und geshadowter Bytes. Die immutable F-ROM-Identität ist eine Konstruktorinvariante. CPU-Modell/-Frequenz, Profil, ROM, Buszuordnungen, Schedulerquelle und die übrigen Komponenten behalten ihre bestehenden festen Bindungen. Es gibt keine neuen Hosttimer oder Hostclockzugriffe.

Vor Veröffentlichung eines Importzweiges werden Profil/CPU/Frequenz/RAM, feste Konfigurationsbits und die genaue Beziehung zwischen DRB4/DRAMC und gespeicherter Decoderobergrenze/Loch geprüft. Der vollständig neue PC muss anschließend alle typisierten Felder, immutable Firmwareidentitäten, Ressourcen, zukünftigen Schedulerereignisse und die exakte Rückkodierung bestehen. Fehler erhalten die aktive Maschine und lassen keinen Zielzweig zurück. Snapshotformat 1 bleibt an den identischen VM-Build gebunden; alte Sicherungen werden nicht still migriert.

## Nachweise und noch offene Plattform

Privater Gesamtnachweis: **artifacts/tests/m4-430fx-host-release-path.txt**. 9125/9125 Regressionen, 0 Warnungen/Fehler und 183 neue Fälle (87 Decoder-/96 Host-PC-Fälle). CPU-Hotreset, tatsächlicher PowerCycle, frühe DRB-Firmware-OUTs und echter PE32-Farcall/RETF aus vom Gast geschriebenem PAM-Code einschließlich anschließender RO-Abwehr sind geprüft.

Acht endgültige FXHOST.COM-Kaltstarts/acht volle 32-MiB-Fortsetzungen unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM je zweimal liefern 966 weitere ganze Zustands-/RAM-Vergleiche, 936 vollständige Bankreadbacks/18874368 tatsächliche Lesebytes, 3176 tatsächliche PCI-Zugriffe und 16 ganzzahlig rekonstruierte RDTSC-Werte. Bei VCPI bleiben alle gelieferten DE01-Einträge erhalten; nur eine eigene zusätzliche PDE/PT bildet physisches PAM an linearen Adressen 00400000h–0043FFFFh ab. Der unabhängige Leser prüft MCB-/PSP-Eigentum, vollständigen COM-/Tabellen-/Stackumfang, Buffergrenzen, jeden Alias und jede REP-Endposition. Der Schnitt besitzt F-WE-only, aktives PCM, rohe x87, eine offene eigene DOS-Datei und zukünftige Gerätefristen. Alle acht Gäste erreichen nach tatsächlich ausgeführtem AH4C einen folgenden DOS-Prompt. 12776 Gastoracles werden absichtlich verfälscht und verworfen.

Acht getrennte bisherige 486-DOS-Fortsetzungen behalten den historischen AH4C-/Nachlaufvertrag (kein neues Promptoracle), zwei echte DOS4GW-/Doom-Fortsetzungen auf dieser 100-MHz-/32-MiB-Platine besitzen DOS-Prompt, 18 WAD-geprüfte Bilder und 195447 Stereo-PCM-Frames. Zusammen 18 DOS-Kaltstarts/18 volle Fortsetzungen, 2297 weitere ganze Vergleiche und 12994 negative Gastoracles. 22 zusätzliche semantisch beschädigte volle Importe sowie ein tatsächlicher alter CLI-Buildimport werden getrennt von Gastoracles gezählt. Vorläufe vor Real-Cache-/AH-Observer-/VCPI-Alias-/Oraclekorrekturen bleiben ausgeschlossen. Keine native Eingabe, Hostendpunkte, neue Aufnahme oder allgemeine Leistungs-/Spiel-/Siliziumzusage.

64 Kandidaten-/normalen Produktdateien sind bytegleich, 678 Code-/Projektdateien lizenz-/kommentargeprüft; 40 Typen/762 Mitglieder/32 unveränderte Ereigniskörper. Der komplette 8942er-Starter und seine 784 Quellen sind archiviert. Alle 115 geschützten Eingaben, C64 und HEAD bleiben unverändert; persönliche Spiel-COWs ungeöffnet.

Der nächste Plattformumfang umfasst 82371FB/PIIX mit konkreten PCI-Ressourcen, PIRQ/ELCR, zusätzlichen Resetdomänen und späterem PCI-Busmaster-Speicherpfad. Danach bleiben S3 Trio64, ATAPI, Firmware-/Managerpfad und die tatsächliche Windows-95-RTM-Installation mit mehreren Starts, stabilen Geräten und regulärem Shutdown erforderlich. SMM, vollständige Cache-/Refresh-/Bankpräzision und nicht implementierte CPU-Systemmerkmale werden durch diese Host-Vertikale nicht als erledigt erklärt. M4 bis M8 und der autorisierte stündliche Gesamtauftrag bleiben offen.
