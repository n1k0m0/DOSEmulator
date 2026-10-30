# SB16: DSP-4.05-Identifikation und unbestückter ASP/XBUS-Teilpfad

**29. Oktober 2026, Win98-Stand C23:** Der originale Win98-SE-Treiber verlangt zusätzlich einen tatsächlichen DSP-Reset-Selbsttest. Der neue begrenzte XBUS09-Latch und die neun ausschließlich resetgültigen F9-RAM-Lesebytes sowie der Firmware-Resetblock von 2048 Komponenten sind in [WINDOWS98-INSTALLATION.md](../WINDOWS98-INSTALLATION.md) beschrieben. 38 neue Fälle bestehen, derselbe Prüfer scheitert auf C22 in allen 38 Fällen; C23 insgesamt 14883/14883. Der tatsächliche Win98-Kaltstart nimmt danach die eigene 8-kHz-Wave über den Originaltreiber an und erreicht natürliches DONE/Unprepare/Close. Die aktive volle 128-MiB-Fortsetzung besteht 18 ganze Vergleiche und 58080 zeit-/bytegleiche spätere Stereo-Frames je Seite einschließlich des gebundenen Präfixes. Dies ist keine neue unabhängige DAC-Sample-/ISR-/EOI-/IRET-/Ganzmix-/Hostaudio-Abnahme. Alle folgenden V12-Zahlen behalten ihren historischen Buildbezug; beliebige MCU-RAM-Zugriffe, CSP und zusätzliche ASIC-Pinwirkungen bleiben offen.

Stand: 28. Oktober 2026. Der geprüfte **V12-Build mit 12589/12589 Regressionen, 0 Warnungen/Fehlern** enthält den E3- und den begrenzten ASP-Abwesenheits-/ASIC05-Readbackpfad. Auf den beiden eigenen Windows-95-Entwicklungsprofilen mit 100 beziehungsweise 133 MHz und 32 MiB führt der originale Windows-95-RTM-Treiber eine echte Win32-waveOut-Wiedergabe aus: vollständige native DMA-/DAC-Samplefolge, interne PCM-Publikationen und aktive volle PC-Fortsetzung sind für die unten benannten Fälle nachgewiesen. Es wurde kein Host-Audioendpunkt geöffnet und keine PCM-/WAV-Aufnahme erstellt. Der ältere E3-Einzelnachweis bleibt seinem V11-Build mit **12478/12478 Regressionen** zugeordnet; die folgenden historischen Fehlerbeschreibungen gelten ihren Vorher-Builds.

## Historische E3-Antwort

Der benannte CT1740-/CT1741-DSP-4.05-Teilpfad liefert 44 ASCII-Bytes und ein abschließendes Nullbyte:

```text
COPYRIGHT (C) CREATIVE TECHNOLOGY LTD, 1992.
```

Gesamtlänge einschließlich `00h`: **45 Bytes**. SHA-256: `DA0C255934B45E000FC445A9464B8A16C27154438D91984328C46E75FF0EF29C`. Kein CR/LF oder weiteres Byte wird angehängt.

Creative führt E3 nicht im vorhandenen [Hardware Programming Guide](https://ardent-tool.com/sound/Sound_Blaster_HW_Programming_Guide_1st.pdf) auf. Die konkrete Grundlage ist die [Disassemblierung des Original-DSP-4.05](https://github.com/S95Sedan/CT1741_DSP/blob/main/assembly/v405-8k_e51aff23.asm): `table_ident_cmds`, `cmd_dsp_copyright` und die benachbarten Versionsbytes 4,5 binden Antwort und Revision. Der Handler überträgt auch das Nullbyte über den Hostdaten-Handshake. Kein fremdes DSP-ROM wird eingebunden.

Im V9-Kaltstart funktionierten bereits Reset/AA und IRQ5/DMA1/5; anschließend akzeptierte der Kern E3 als unbekannten Befehl und alle 512 Statusabfragen blieben leer. Der ursprüngliche Windows-Treiber prüft genau diese Identifikationsfolge. Sein damaliger Callback-Rücksprung wurde nicht im Porttrace erfasst und wird nicht nachträglich als beobachtet ausgegeben. Privater Beleg: `artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/sb16-presence-review-v9/e3-native-cause.json`.

## Konkreter ASP-Test und vollständige Parameterpakete

Der V11-Porttrace belegt nach gelesener E3-Folge die Bytes `05 00 00 03 04 FC 04 FA 0F 83`. Danach erfolgen 97861683 leere Statusabfragen an 22Eh. Ein gesonderter tatsächlicher Snapshot bindet den wartenden Reader an 79 bytegleiche Originalbefehlsbytes und den Rückweg in Objekt 1:8562 des originalen SB16.VXD. Es ist der erste Datenread des ASP-Tests ab 84C8. Der Reader lädt lediglich CX, benutzt jedoch ein 32-Bit-LOOP; hohe ursprüngliche ECX-Bits erklären die lange Wartezeit bei fehlender Antwort. CPU und Gastcode werden dafür nicht geändert.

Das Original-DSP-4.05 legt folgende Paketgrenzen und MOVX-Ziele fest. Nur die unten benannte DOSE-Zielantwort wird damit ergänzt:

| DSP-Befehl | Parameterbytes | Originalziel | Begrenzte DOSE-Wirkung |
| --- | ---: | --- | --- |
| 03h | 0 | XBUS80 lesen | Genau ein FFh-Antwortbyte |
| 04h | 1 | XBUS82 schreiben | Ganzes Parameterbyte konsumieren, unverbundene Wirkung verwerfen |
| 05h | 2 | XBUS80, danach 81 schreiben | Beide Parameter konsumieren, unverbundene Wirkungen verwerfen |
| 0Eh | 2 | Adresse, Schreibwert | Ziel 05h wirklich speichern; 80h–83h verwerfen |
| 0Fh | 1 | Adresse lesen | Ziel 05h wirklich zurücklesen; 80h–83h liefern je ein FFh |

Die feste Antwort FFh und verworfene Writes an **80h–83h** sind eine ausdrücklich eigene DOSE-Policy für den unbestückten externen ASP-Teil. Sie sind kein gemessener CT1740-, CT1746- oder CSP-Siliziumvertrag. 0Eh/0Fh konsumieren auch bei anderen Adressen ihr vollständiges Paket, erhöhen einmal `UnsupportedCommands`, setzen `LastUnsupportedCommand` auf den Befehl und erzeugen keine Antwort oder fremde Registerwirkung. Weitere ASP-Befehle, Uploads, Versionen und Synthese bleiben unimplementiert.

Der gebundene Originaltest liest vier Ausgangsbytes über 0F83, schreibt deren Komplement über 0E83 und prüft den anschließenden Readback. Ständiges FFh mit verworfenen Writes besteht diese Probe ausdrücklich nicht. Der statisch gebundene Abwesenheitszweig löscht sein ASP-Bit, führt den echten DSP-Resetpfad aus und liefert EAX=1; der Aufrufer löscht daraufhin sein ASP-Capabilitybit und setzt die Initialisierung fort. Dies beschreibt den statisch gebundenen Abwesenheitszweig, keinen gesondert beobachteten registergenauen Durchlauf aller ASP-Testzweige. Der unten belegte V12-Wavepfad erreicht anschließend echte Treiberarbeit; es wird weiterhin keine positive CSP-Erkennung erfunden.

## Opaques ASIC05-Readbackbyte und Resetdomänen

Der anschließende Original-Reset verwendet zusätzlich `0F 05` und `0E 05 (alt|02h)`. XBUS05 gehört deshalb nicht zu den als unbestückt benannten ASP-Zielen. Sein DOSE-Teilmodell besitzt ein eigenes Byte `_xbus05Control`: 0Eh schreibt den tatsächlichen Wert, 0Fh liest ihn wieder. Beliebige Werte bleiben als Readback erhalten; sie ersetzen keine implementierten DMA-, IRQ- oder CSP-Steuerwirkungen.

Im Original-DSP verändern DEh/DFh Bit 0 dieses MOVX-Ziels. Daraus folgt keine hier nachgewiesene Wirkung von Bit 1 oder des gesamten ASIC-Registers. DEh/DFh werden durch diesen Schritt nicht zusätzlich implementiert. Die eigene Readbackfunktion lockert keine bestehenden PCM-, DMA-, Quellen- oder Quittierungsverträge.

| Domäne | Eigene ASIC05-Policy |
| --- | --- |
| Kalte Konstruktion/Power | 00h |
| PIIX-ISA-Hardreset | 00h |
| DSP-Reset über Basis+6 | Latch erhalten; bisherige DSP-/Transfer-/FIFO-Resetwirkungen bleiben erhalten |
| Mixerreset oder alleiniger CPU-Reset/INIT | Latch erhalten |
| Vollständiger Snapshot | Exaktes Byte und sämtliche bestehenden Parser-/FIFO-/Callbackzustände fortsetzen |

Diese Resetwerte und das Preserven sind ausdrücklich eigene DOSE-Festlegungen. Die Originalfirmware schreibt 05h in ihren betrachteten Startpfaden nicht unmittelbar neu; das beweist weder einen universellen physischen Resetwert noch unveränderte ASIC-Pins. Der echte [PIIX-Resetpfad](../../src/DOSEmulator.Hardware/SoundBlaster16.Reset.cs) setzt das Byte auf null. Die Importprüfung muss dieses Nullbild auch während gehaltenem PIIX-Reset verlangen. Der [Snapshotvertrag](../SNAPSHOTS.md) bleibt exakt modell-/profil-/buildgebunden; alte vollständige Container werden nicht migriert.

## Bestehende Transportgrenzen

[SoundBlaster16.cs](../../src/DOSEmulator.Hardware/SoundBlaster16.cs) verwendet weiterhin die vorhandene nominelle Kommandofrist von 1 µs und dieselbe begrenzte 64-Byte-Antwortqueue mit Drop-new. Ein FFh-Antwortbyte besitzt dabei echten Read-ready-Status und wird tatsächlich aus der Queue gelesen; es ist nicht bloß der leere Datenportwert. Es gibt keinen neuen Timer, Callbackkörper oder Hostendpunkt. E1 bleibt 4.05; DSP8-, DSP16- und MPU-Quellen behalten ihre getrennten Quittierungen. Reservierte Mixerantworten FF/FE werden nicht für eine andere Kartenidentität verändert.

Die 37 neuen ASP-Fälle im geprüften V12-Stand decken vollständige und partielle 04h/05h/0Eh/0Fh-Pakete, kommandogleiche Parameter, Busy-/Resetgrenzen, tatsächlichen 05h-Readback, die negative ASP-Komplementprobe, getrennte IRQ-Quellen sowie vollständige Fortsetzungen einschließlich INIT/PIIX-Hardreset ab. Der historische private Vertragsentwurf mit Originalmodul-, Frame- und Tracebindung bleibt unter `artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/sb16-presence-review-v9/v13-absent-asp-draft.json` erhalten; dessen damaliger Freigabestand ersetzt die folgenden tatsächlichen V12-Nachweise nicht.

## V12: tatsächliche Wiedergabe durch den Windows-95-Treiber

Die beiden festen Profile `dose-430fx-piix-s3-win95-dev-v1` und `dose-430fx-piix-s3-win95-133-dev-v1` verwenden P54C mit 100 beziehungsweise 133 MHz, 32 MiB RAM, S3 Trio64 sowie CT1740 an 220h/IRQ5/DMA1+5. Aus ihren eigenen vollständig gesicherten Windows-95-RTM-Desktops, Build 950, startet die normale virtuelle KBC-Eingabe die eigene Win32-Probe `a:\dose95.exe /wave`. Es gibt keinen Host-FS-Aufruf oder injizierten DSP-/RAM-Zustand. Diese vier Witness-Läufe sind Fortsetzungen bestehender Desktops, keine vier zusätzlichen Windows-Kaltstarts.

Die Probe benutzt die ursprünglichen Win32-waveOut-APIs mit einem echten WAVEHDR und **8000 unsigned 8-Bit-Mono-Samples bei 8000 Hz**. Alle 8000 tatsächlich gewährten DMA8-Komponenten und ihre DAC-Werte stimmen mit der unabhängig aus [windows95-probe.asm](../../tests/guest/windows95-probe.asm) rekonstruierten Folge überein: `11 + 6 * (phase < 40 ? phase : 79-phase)`, `phase=i%80`, 100 ganze Perioden. Jeder native Sampleabstand beträgt exakt **125000 ns**. SHA-256 der 8000 Rohbytes: `D0E1C9D0871A6E4838DF28002AEDAEA326B681F085F96821CB5BAC6F4F12FBB9`; SHA-256 aller 8000 vorzeichenbehafteten DAC-Werte als Int16 Little Endian: `EBDE891F707FC0A931BDDA2EC765DA61AEC5B4F5B2EFD34ED0154B5783A2FFFD`. Beide Frequenzprofile liefern dieselben Payload-/DAC-Hashes. Auch die vollständigen 136000 Byte aus Ereigniszeit, Transferindex, Rohwert, DAC-Wert und Flags lassen sich unabhängig rekonstruieren und treffen die tatsächlichen Ereignisdigests.

`TransferStarted` belegt die aktive Rate 8000, Mono/8 Bit/unsigned, Auto-init, angefordertes FIFO und 500 Komponenten je Block auf DMA1. Je Witness entstehen **16 wirkliche Blockabschlüsse und 16 physische IRQ5-PIC-Acknowledgements**, ohne spurious ACK. Die gespeicherte Masterbasis 50h bindet die ACKs an Vektor 55h. Das ist kein vollständiger ISR-/IRET-Nachweis. Nach der Welle ist die Karte in den langen Endzuständen inaktiv: `RemainingSamples=0`, Sampletoken 0, IRQ-Quellen 0 und `UnsupportedCommands=0`. Die anschließend wieder gelesene Default-Rate 22050 ist keine Aussage über die zuvor aktiv beobachtete Wellenrate.

## Interner PCM- und vollständiger Fortsetzungsnachweis

Die Mixer-Publikationen enthalten endliche 48-kHz-Stereo-Floats. Die Witnesses prüfen jeden tatsächlichen Blockzeitpunkt, die importierten Teilpuffer und die exakte Beziehung zwischen erzeugten und publizierten Frames. Bei 100 MHz enthalten sie 174710, bei 133 MHz 174386 von null verschiedene Float-Komponenten. Diese Zahlen zählen beide Kanäle, nicht Stereo-Frames. Es werden nur Zähler, Zeiten, skalare Statistiken und Digests ausgegeben; Samplewerte werden für die unabhängige native Payloadprüfung ausschließlich im Prozessspeicher gehalten. Normale Snapshot-Gerätepuffer bleiben Bestandteil des privaten vollständigen Zustands.

| Witness | Publizierte Stereo-Frames auf der Originalseite insgesamt | Beidseitig verglichene Zukunft | Bytegleiche Stereo-Frames je Seite in dieser Zukunft |
| --- | ---: | ---: | ---: |
| 100 MHz, kurz | 1919520 | 0,012800000 s | 480 |
| 133 MHz, kurz | 1442880 | 0,009624061 s | 480 |
| 100 MHz, lang | 1843200 | 1,600000000 s | 76800 |
| 133 MHz, lang | 1385760 | 1,203007519 s | 57600 |

Jeder aktive Schnitt erfolgt erst nach einem echten `RunSteps`-Owner-Rückweg, mit noch laufendem DMA und strikt zukünftigem Sampleereignis. Bei 100 MHz sind zwei Samples übertragen und 498 im aktuellen Block übrig; bei 133 MHz ist ein Sample übertragen und 499 übrig. Die kurzen Witnesses prüfen die vollständige Original-Payload und je 18 ganze Zustandsvergleiche, setzen die restaurierte Seite jedoch nur die oben genannten rund 13 beziehungsweise 10 ms fort. Sie behaupten deshalb allein keinen restaurierten Wellenabschluss.

Die zwei zusätzlichen langen Witnesses erfassen jeweils erneut einen tatsächlichen aktiven vollen Snapshot und importieren ihn auf einen frischen Zweig. Danach laufen **40 Millionen normale Schritte pro Seite**, verteilt auf 16 gleiche Abschnitte. Anfang, 16 Abschnittsenden und Ende ergeben je **18 vollständige Vergleiche von CPU, allen 32 MiB RAM, VRAM, Geräten, Scheduler und Scanout**, ohne ausgeschlossene Zustandsfelder. Sämtliche danach publizierten PCM-Bytes, Blockzeiten und Framezahlen sind auf beiden Seiten gleich. Die Endmedien sind bis auf die tatsächlich verschiedenen Capture-UUIDs identisch. Der größere Vergleichsrahmen ist ausschließlich ein privater Prüfhelper-Fork; Produkt-/Controllerlimits und der buildgebundene [Snapshotvertrag](../SNAPSHOTS.md) bleiben unverändert.

Auf **beiden Seiten beider langen Fortsetzungen** prüft der unabhängige FAT-/Datei-Checker den tatsächlich versiegelten IDE0-Stand: beide FAT-Kopien, alle 1027 eigenen DAT-Dateibytes und die echte gespeicherte DOSE95.BIN stimmen. Die Proofs melden `waveApi=done`, Phase 4 und Flags `00001FFF`; beide Seiten eines Profils besitzen denselben Proofhash. Der NASM-Pfad setzt Natural-DONE erst nach echtem WHDR_DONE und vor einem erzwungenen Reset. Das gesonderte ABORT-Bit ist nicht gesetzt; Open/Prepare/Write/Unprepare/Close sind erfolgreich. Ein späteres Cleanup wird nicht als Ersatz für natürliche Wiedergabe gewertet. Die Langprüfung umfasst damit die restliche Welle und den tatsächlichen API-/Dateiabschluss auch auf der restaurierten Seite.

Private Ergebnisse liegen unter `artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/` in `audio-witness-v12/run-1`, `audio-witness-v12-133/run-1`, `audio-witness-v12-long/run-1` und `audio-witness-v12-133-long/run-1`. Maßgeblich sind jeweils `result.json` und `active-native-continuation/result.json`; bei den langen Zeugen zusätzlich `both-sides-natural-done-summary.json` sowie `originalMedia-wave-done-file-api.json` und `restoredMedia-wave-done-file-api.json`. Ein unabhängiger schreibfreier Abschlussreview hat Payload-/DAC-/Ereignishashes, alle PCM-Blockzeitpunkte, die finalen ganzen RAM-/Zustandshashes und beide benannten versiegelten Journalhashes je Langlauf erneut geprüft. Die Datei-/API-Reports allein beanspruchen keinen PCM- oder sichtbaren GDI-Nachweis; diese Aussagen stammen aus den getrennten tatsächlichen Witnesses.

## Reichweite und weiterhin getrennte Abnahmen

Für den 100-MHz-V12-Gast ist zusätzlich ein regulär über Windows bedienter Shutdown dokumentiert (`live-win95-v12-native-s3/v12-native-regular-shutdown.json` samt vorhandenem Bild). Der 133-MHz-Gast erreicht nach einem Windows-Warmneustart den zweiten tatsächlichen POST (`live-win95-v12-native-133-fixed/v12-133-after-restart.json`, `post10Count=2`) und anschließend ebenfalls den regulären Abschaltbildschirm: `live-win95-v12-native-133-fixed/v12-133-native-regular-shutdown.json` samt PNG, bei 81,837894736 s Gastzeit, PE=0, ohne Gastfehler oder zusätzlich gezählten Hardware-Reset. Diese Ergebnisse gelten ihren eigenen privaten Medienständen und sind keine universelle Start-/Shutdown-Matrix.

Der Nachweis umfasst den beschriebenen originalen Win95-Treiberpfad und die eigene 8-kHz-Mono-Win32-Welle im internen PCM-Modell. Er ist keine unabhängige Rekonstruktion des gesamten Mixers, der Filter-/OPL-/Analogkette, keine Host-/Lautsprecherabnahme und keine neue universelle CT1740-/ASIC-/CSP-/FIFO-/Siliziumzusage. ADC/Hostaufnahme, CSP-Programme, andere Raten/Formate, weitere Programme und sämtliche Meilenstein-Kriterien bleiben nach ihren eigenen Verträgen zu prüfen. Der Gesamtstand von M4 steht weiterhin in [IMPLEMENTATION-STATUS.md](../IMPLEMENTATION-STATUS.md).
