# Windows 98 Second Edition in DOSE

## Installations- und Betriebsnachweis vom 29. Oktober 2026

Die auf ausdrücklichen Benutzerwunsch von [dieser Archivseite](https://archive.org/details/windows-98-se-isofile) geladene englische Original-ISO liegt privat unter `artifacts/Windows98/media/Windows98-SE.iso`. Ihre 655591424 Bytes stimmen mit MD5, SHA1 und CRC32 des Archivs überein. Die separat geladene Produktnummer bleibt ausschließlich in der privaten, ignorierten Datei; sie wird hier nicht wiedergegeben. Das Originalsetup hat sie angenommen.

Die eigene neue 4-GiB-Platte benutzt FAT32 und einen getrennten COW. Das Originalsetup hat partitioniert, vollständig formatiert und alle Installationsdateien kopiert. Ein unabhängiger Sektorleser prüft MBR, beide FATs, Backupbootsektor, FSInfo und ausgewählte vollständige Dateien aus versiegelten Generationen. Vorhandene Windows-95-Platten, Spielstände und Originalmedien werden nicht verändert.

| Baustein | Tatsächlich gewählte Implementierung |
| --- | --- |
| CPU / RAM | P54C-B5-Teilumfang, 133 MHz, 128 MiB |
| Board / IDE | 430FX / PIIX, eigene Windows-Firmware, 4-GiB-HDD und ATAPI-CD |
| Grafik | S3 Trio64 86C764 PCI, 2 MiB |
| Sound | SB16 CT1740 / DSP 4.05, A220/I5/D1/H5; MPU-UART 330h |
| Eingabe | PS/2-Maus, AT-Tastatur; hier ausschließlich virtuelle Gasteingaben |
| Netzwerk | RTL8029 PCI, NAT konfiguriert, Kabel für die Installation getrennt |

133 MHz und 128 MiB sind die derzeit schnellste tatsächlich implementierte Kombination. Ein MMX-Prozessor, Voodoo oder fertiger M6-Umfang wird dadurch nicht behauptet.

## Zwei tatsächliche Setup-Befunde

C20 korrigiert die im Windows-POST bisher maskierte IRQ2-Kaskade. Das originale DOS-Setup aktiviert IRQ12 am Slave direkt; mit maskierter Kaskade blockierten AUX-Daten anschließend auch die Tastatur. Die eigene Startdiskette behält originale Systemprogramme und benutzt den eigenen `DOSECD.SYS` mit originalem MSCDEX. Der zuvor negative Oak-Treiberlauf ist ein Diagnosevorlauf. Originale Windows-Programme werden nicht gepatcht. Sechs neue Firmwarefälle ergeben mit demselben Prüfer vorher 0/6, danach 6/6; C20 insgesamt 14781/14781. Siehe [POST-Vertrag](hardware/WINDOWS95-FLOPPY-IRQ.md).

Bei der originalen Abschlusskonfiguration trifft C20 anschließend auf `0F 77` an `0167:786B72D5`. Die unveränderten Gastbytes registrieren davor eine echte Win32-SEH-Kette; der normale Erfolgsarm liefert 1, der Ausnahmeweg 0. Die Hardware besitzt kein MMX. Intel beschreibt für MMX-Befehle auf Prozessoren ohne MMX eine restartbare `#UD`, ohne Fehlerwort und ohne Ausführung der Instruktion: [Intel Architecture SDM Volume 3, 1997, Exception 6 / 5-26](https://www.arl.wustl.edu/projects/archive/gigabitkits/References/SPC/Pentium_Software_Dev_Man_3_System_Programming.pdf).

C21 ergänzt ausschließlich diese fehlende EMMS-Ausnahme im Decoder. Er führt kein EMMS aus, leert keine x87-Tags und bewirbt kein MMX. 44 permanente [Fälle](../tests/DOSEmulator.Tests/Windows98MmxProbeTests.cs) prüfen Real/PE16/PE32/Ring3/V86, rohe x87-Werte und Umgebung, EM/TS-Priorität, vollständige Fault-/IRET-Frames sowie Seiten-/CS-Grenzen und den erhaltenen Featurevertrag. Derselbe endgültige Prüfer liefert auf C20 12 PASS/32 FAIL, auf C21 44 PASS.

Der C21-Build besteht **14825/14825 Regressionen, 0 Warnungen/Fehler**. Gegenüber C20 ändern sich nur CPU-DLL/PDB in beiden Frontends sowie Tests-DLL/PDB; die anderen vier VM-Module und alle neun ROM-Nutzlasten bleiben bytegleich. Zustandsgenerator 48 Typen/955 Mitglieder/43 Operationen; 453 C#-/892 Code-/Projektdateien ohne Qualitätsproblem. Der tatsächliche CLI vergleicht ganzen RAM/serialisierten Zustand/Status/Bild bei 10001 direkt gegen 10000 plus Capture/Load plus 1 gleich. Ein alter C20-Container wird vor Zweiganlage mit dem bestehenden CLR-Abbruch abgewiesen; keine Migration.

## Getrennter C22-Befund: normaler MAC-Abschluss bei Trägerverlust

Der tatsächlich kalt gestartete C21 erreicht die grafische Anmeldung, reagiert dann bei getrenntem Netzwerkkabel aber extrem träge. Zwei vollständige eigene Gastzustände binden den laufenden Reset-/Wartepfad an den Originaltreiber an Port 6000h. Der letzte vollständige Sendepuffer enthält eine echte ICMP-Routersuche, die Karte meldet TSR `10h` und TXE. Ein separater, ausdrücklich nur diagnostischer C21-Gegenversuch ändert ausschließlich virtuelle Kabelpräsenz plus passende Profilpräferenz; kein Hostbackend oder Windows-Code wird verändert. Die ausstehenden Zeitzoneneingaben werden danach verarbeitet. Diese Kopie wird niemals als installierte Benutzerplatte übernommen.

C22 korrigiert den [normalen Trägerverlustvertrag](hardware/NETWORK-CARDS.md): ohne simulierte Kollision oder FIFO-Underrun beendet CRS die MAC-Übertragung nicht vorzeitig. PTX/CRS werden gemeinsam gemeldet; bei getrenntem oder unterbrochenem Kabel wird weiterhin kein Paket an einen Hostbackend veröffentlicht. Quellen, Grenzen und die von AS-spezifischem externem Loopback getrennte Einordnung stehen im verlinkten Hardwarevertrag. Derselbe endgültige 20er-Prüfer liefert C21 2 PASS/18 FAIL und C22 20 PASS.

Der eingefrorene C22 besteht **14845/14845 Regressionen, 0 Warnungen/Fehler**, weiterhin 48/955/43 und 453 C#-/892 Code-/Projektdateien ohne Qualitätsproblem. Gegenüber C21 ändern sich ausschließlich Hardware-DLL/PDB in beiden Frontends und Tests-DLL/PDB. CPU, Core, Contracts, Runtime und alle neun ROM-Nutzlasten bleiben gleich. Neuer ganzer CLI-RAM/Zustand/Status/Bild bestehen; alte C21-Container werden vor Zweig mit dem bestehenden CLR-Abbruch abgewiesen. Es gibt keine Migration alter Windows-Snapshots.

## Originalinstallation und C22-Betriebsprüfung

Der neue C22 startet eine unabhängige Medienkopie des tatsächlichen C21-Installationsstands kalt, mit weiterhin getrenntem Kabel und deaktiviertem Hostnetzwerk. Die Originalanmeldung, der Datum-/Uhrzeitdialog mit angewandter Berliner Zeitzone sowie Systemsteuerung, Startmenü, Hilfe, Programmstartoptimierung und die vollständige Systemkonfiguration bestehen. Der anschließende normale Setup-Neustart erreicht den originalen Windows-98-Desktop. Der Benutzername ist DOSE, das Kennwort leer. Die automatisch angezeigte Begrüßung wurde in ihrem Originaldialog für weitere Starts abgeschaltet.

Eine eigene, über Windows-FDC gestartete Win32-Abfrage meldet 133722112 nutzbare physische Bytes bei tatsächlich eingebauten 128 MiB, genau einen PCI-S3-Anzeigeknoten und keinen zusätzlichen Root-Display-Knoten. Der Originaltreiber `S3 Trio32/64 PCI (732/764)` stammt aus `DXS3.INF`. Die zehn über `EnumDisplaySettingsA` angebotenen Modi sind 640×480/4 Bit, 640×480 bis 1280×1024/8 Bit, 640×480 bis 1024×768/16 Bit und 640×480/32 Bit. Dies ist eine Aufzählung angebotener Modi, keine Betriebsabnahme jedes Modus. 800×600 mit 16-Bit-Farbe wurde tatsächlich angewandt, im Windows-Rückfragedialog bestätigt und nach einem weiteren regulären Windows-Neustart beibehalten.

Die erste eigene Datei-/GDI-/Wave-Anwendung zeichnet ihre Testflächen, meldet aber bei `waveOutOpen` Fehler 2. Der Original-Gerätemanager bestätigt für die SB16 Code 10 trotz korrekter Ressourcen 220h/330h/388h, IRQ5 und DMA1/5 ohne Konflikt. Der zugehörige volle 128-MiB-Snapshotvergleich ist für die tatsächlich vorliegende Anwendung gleich, stellt aber ausdrücklich keinen erfolgreichen Sound- oder vollständigen Anwendungsnachweis dar. Der anschließende reguläre Windows-Shutdown erreicht den originalen Abschaltbildschirm. Dieser saubere eigene Medienstand ist die kalte Ausgangsbasis für C23.

## C23: tatsächlicher Reset-Selbsttest des originalen SB16-Treibers

Ein ausschließlich passiver eigener DSP-Portbeobachter mit denselben fünf C22-VM-Modulen bindet beim regulären Windows-Neustart die letzten Befehle an `0F 09`: Die bisherige Implementierung gibt keine Antwort. Die vollständige Originaldatei `SB16.VXD` und ihre tatsächlich geladenen, fixierten Daten zeigen den anschließenden Reset-Selbsttest: XBUS09=F8h, interne Bytes 0Eh=FFh und 0Fh=07h, die sechs Adressen 0Bh/23h/24h/2Bh/2Ch/2Dh=00h sowie 37h=38h. Die [originale DSP-4.05-Disassemblierung](https://github.com/S95Sedan/CT1741_DSP/blob/main/assembly/v405-8k_e51aff23.asm) bestätigt diese Initialisierung sowie die echten 0Eh-/0Fh-XBUS- und F9h-RAM-Lesebefehle. Es handelt sich um DSP-Resetdaten, nicht um eine positive CSP-Erkennung.

C23 ergänzt einen eigenen begrenzten XBUS09-Latch, den DSP-Reset auf F8h und genau diese neun resetgültigen F9-Lesebytes. Ein DSP-Befehl außerhalb der benannten Resetabfragen und XBUS-Zugriffe beendet diesen konservativen F9-Teilumfang; es wird kein allgemeiner 8052-Innenspeicher emuliert. Unbekannte F9-Adressen konsumieren ihr Parameterbyte und bleiben ohne erfundene Antwort. Der tatsächliche Legacy-Auto-init-Resetblock beträgt entsprechend der Firmware 2048 Komponenten. Der bisherige XBUS05-Latch bleibt bei DSP-Reset erhalten und wird weiterhin nur durch den PIIX-ISA-Reset gelöscht. Neue Zustandsfelder und widersprüchliche Resetdaten werden in die strenge Snapshotprüfung einbezogen.

38 permanente Fälle prüfen diese Werte, reale CPU-IN/OUT-Effekte, Grenzen, mutierende Gegenproben und fünf volle 32-MiB-Snapshot-Fortsetzungen. Derselbe endgültige Prüfer liefert auf C22 0/38 und auf C23 38/38. C23 besteht insgesamt **14883/14883 Regressionen, 0 Warnungen/Fehler**. Der Zustandsgenerator prüft jetzt 48 Typen/957 Mitglieder/43 unveränderte Callbackkörper; 454 C#-/894 Code-/Projektdateien sind qualitätsgeprüft.

## Tatsächlicher C23-Betrieb und volle Fortsetzung

Der eingefrorene C23 startet den sauberen eigenen C22-Medienstand kalt und erreicht den originalen Windows-98-SE-Desktop mit 800×600 RGB565. Die unveränderte eigene Win32-Anwendung wird über den wirklichen Windows-FDC geladen. Der originale SB16-Treiber nimmt ihre 8-kHz-Wave an; `waveOutOpen`, Prepare, Write, natürliches DONE, Unprepare und Close bestehen. Der unabhängige GDI-Prüfer vergleicht alle 94620 inneren RGB-Pixel ohne Ausschlüsse. Nach natürlichem Anwendungsschluss und regulärem Windows-Cache-Flush werden sämtliche 1027 DAT- und 512 BIN-Bytes unabhängig aus dem versiegelten FAT32/COW gelesen und geprüft.

Ein tatsächlicher aktiver Wave-Schnitt wird streng mit SnapshotStore geladen. Der anschließende Vergleich umfasst **40 Millionen Schritte je Seite, 1,203007519 virtuelle Sekunden und 18 ganze CPU-/128-MiB-RAM-/VRAM-/Geräte-/Scheduler-Vergleiche**. Alle zukünftigen Zustände, Bilder, Medien und tatsächlichen PCM-Publikationen stimmen überein; kein Zustandsfeld wird ausgeschlossen. Je Seite werden 58080 Stereo-Frames einschließlich des geprüften vorhandenen Präfixes von 364 Frames zeit- und bytegleich verglichen. Die erweiterte Schrittgrenze betrifft ausschließlich den privaten Prüfharness; dessen fünf VM-Module sind bytegleich C23. Vier einzeln gebundene Capture-UUIDs sind die einzigen kanonisierten Metadaten.

Der frühe versiegelte Datenträger am 1,2-Sekunden-Schnitt enthält noch ältere DAT-Sektoren aus dem Windows-Schreibcache. Diese frühen Dateioracles sind keine bestandene physische DAT-Abnahme. Die tatsächlichen API-Proofs und die später vollständig geflushten Enddateien werden getrennt geprüft. Eine optionale App-PTE-Abfrage im Idle-CR3 ist nicht verfügbar und wird ebenfalls nicht als bestandene App-RAM-Prüfung gezählt. Neue unabhängige DAC-Sample-, ISR-/EOI-/IRET-, Ganzmix- oder Hostaudio-Abnahmen werden aus dem PCM-Vergleich nicht abgeleitet.

## Englische DOSE Tools und Uhr

Der eigene englische Windows-98-Installer wird im tatsächlichen Gast ausgeführt. Der Erfolg, das echte `C:\Share` und jedes der 7680 Bytes von `C:\DOSETOOLS\DOSETIME.EXE` werden unabhängig aus FAT32/VFAT bestätigt. Die installierte Datei besitzt SHA-256 `F97E312B338FC353C03B20DD152D7BBBEFF2FA45E81B283CB77331B0C9CA6665`. Die Win98-Variante ändert gegenüber dem eigenen vorherigen Helfer ausschließlich dessen explizite Versionszulassung und englische Produktbezeichnung; kein Originalprogramm wird ersetzt.

Ein wirklicher einmaliger Abgleich meldet erfolgreiche, nichtnullige SetLocalTime- und WM_TIMECHANGE-Rückgaben sowie 30 ms Abstand zwischen RTC-Ziel und anschließendem Windows-Kalender. Der abschließende frische Kaltstart von der Benutzerplatte erfolgt **ohne Diskette und ohne manuellen Helferstart**. Der eingetragene Hintergrundhelfer läuft tatsächlich im Modus 2; zwei volle RAM-Erfassungen binden seine fortschreitende Sequenz 3→8, stabile originale RTC-Bytes, erhaltenen Index/NMI-/IF-Zustand und Kalenderabstände von 10/40 ms. In diesen beiden späteren Durchgängen ist kein erneutes Setzen erforderlich; deren NOT_CALLED-Werte werden nicht als neue API-Aufrufe ausgegeben.

Das Frontend liefert bei Start, Laden und Fortsetzen den aktuellen Host-Lokalkalender entsprechend der Benutzerwahl; Core und strenger Loader lesen nicht selbst Hostzeit. Die ganzsekündliche CMOS-Uhr und der 500-ms-Gasttimer sind keine kontinuierliche Hostgenauigkeits- oder 1:1-Performancegarantie. `C:\Share` ist ein gewöhnlicher Gastordner; Dateitransfer, Hostordnerfreigabe und Zwischenablage werden damit nicht implementiert. Der Erhalt eines bereits gefüllten Share-Ordners wurde hier nicht zusätzlich dynamisch abgenommen.

## Bereitstellung und Grenzen

[`run-win98.cmd`](../run-win98.cmd) startet die eigene private Installation unter `artifacts/Windows98/Windows98-SE.dosebranch` mit dem separat gepinnten C23, ohne Neubau. Alle 110 Produktdateien stimmen mit dem eingefrorenen Kandidaten überein; 14 ändern sich gegenüber C22. CPU, Core und Contracts bleiben dabei gleich. Vorhandene Windows-95-PCs und deren C19-Starter werden nicht ersetzt. Die neue Benutzerplatte startet kalt ohne Installationsdiskette, ohne Begrüßung und mit Windows Logon ohne Netzwerkanmeldeabfrage. 800×600/16 Bit bleiben erhalten. Anschließend erreicht sie erneut den originalen Abschaltbildschirm; der übergebene COW stammt aus diesem regulären Shutdown.

Der eigene CLI prüft ganzen RAM/serialisierten Zustand/Status/Bild bei 10001 direkt gegen 10000 plus Capture/Load plus 1 gleich. Ein wirklicher alter C22-Container wird vor Zweiganlage mit dem bestehenden nichtnulligen CLR-Abbruch abgewiesen; keine Migration und kein kontrollierter Exit 1 werden behauptet. Die neue Win98-Betriebsprüfung ersetzt keine erneute Win95-, ursprüngliche RTM-, vollständige M4–M8-, MMX- oder allgemeine Echtzeitabnahme. Netzwerk bleibt für diese Installation virtuell getrennt; es werden keine neuen Win98-NAT-/Bridged-/Host-only-Endpunktnachweise daraus abgeleitet.

Private Gesamtnachweise und Fortsetzung: `artifacts/tests/win98-release-path.txt` und `artifacts/tests/win98-work-path.txt`, dort `continuation.json`, `validation-c23-product.json`, `clock-autostart-c23-proof.json` und die eingefrorenen Kandidaten. Originalmedien, Produktnummer und RAM-Dumps bleiben ignoriert. Kein natives Hostfenster, keine Hosteingaben, GL-Prüfung, Endpunkte oder Aufnahmen. Die regelmäßige Automation bleibt auf Benutzerwunsch pausiert. Kein Commit/Push dieser Arbeit.
