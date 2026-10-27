# SB16: CT1740 / DSP 4.05, PCM- und CT1745-Kern

Stand: 25. Oktober 2026. Dieses M3-Teilpaket implementiert einen konkreten **Non-PnP-PCM-Referenzpfad der CT1740 mit CT1741-DSP 4.05 und CT1745-Mixer**. Der gemeinsame Build besteht **4464/4464 Regressionen ohne Warnungen/Fehler**, davon **226 neue Geräte-/Audio-/PIC-Fälle**. Dies beschreibt die damalige PCM-Teilabnahme. [YMF262/OPL3](OPL3.md) samt CT1745-FM-Pegeln besitzt inzwischen eine getrennte Folgeabnahme; MPU-401, CSP und weitere Mixer-/Kompatibilitätspfade bleiben offen. Vollständiges M3 ist nicht abgenommen.

## Nachtrag: optionaler MPU-UART, 26. Oktober 2026

Der **6079er-Build** ergänzt eine getrennt aktivierte [CT1740-MPU-UART-Schnittstelle](MIDI-MPU401.md) an 300h/330h. Mixer82-Bit 2 und der kombinierte Karten-IRQ sind vorhanden, MPU-Datenlesen bleibt von +Eh/+Fh getrennt; DSP-/Mixerreset löschen die MPU nicht. `sb16Mpu401=true` bezeichnet diesen begrenzten optionalen UART-Vertrag, `sb16FullCard=false` bleibt. Die damaligen Capability-Angaben im historischen PCM-Abschnitt unten gelten für dessen 4464er-Build. Ein Synthesizer, Intelligent Mode, alter SB-MIDI-DSP-Satz, CSP, vollständige Eingangsmatrix/Analog-/FIFO-/Originalchippräzision und lange kombinierte Soundpfade bleiben offen.

## Modell und Anschluss

Das Profil heißt `sb16-ct1740-dsp405-development`. Es wird im Hardwaremenü als **SB16 CT1740 / DSP4.05 PCM** angeboten. Die Referenzrevision ist bewusst festgelegt: Der Creative-Leitfaden beschreibt die DSP-4.xx-Schnittstelle; die gesonderte Untersuchung einer CT1740 Revision 4 belegt deren DSP 4.05. Daraus folgt keine Zusage für jede CT1740-Revision oder spätere SB16-/PnP-Karten.

Konfigurierbar sind Basis 220h/240h/260h/280h, IRQ 5/7/9/10, DMA8 0/1/3 und DMA16 5/6/7. Das Standardprofil verwendet **A220 I5 D1 H5**. Das Ressourcenmenü zeigt die passende DOS-Zeile, etwa `SET BLASTER=A220 I5 D1 H5 T6`. Ein automatischer Eintrag in Benutzer-Startdateien erfolgt nicht. Karten- und Hostressourcenwechsel durchlaufen den vorhandenen geprüften Prepare-/Commit-/Neustartpfad; Hostpegel bleiben live einstellbar. Ein Rückwechsel zu SB2 normalisiert dort unzulässige Ressourcen. Die IPC-Fähigkeiten unterscheiden `sb16Pcm=true` von den weiterhin falschen `sb16FullCard`, `sb16Opl3` und `sb16Mpu401`.

Die Gastports sind Basis+4/+5 für Mixerindex/-daten, +6 für DSP-Reset, +Ah für Antwortdaten, +Ch für Busy/Kommandos, +Eh für Antwortstatus/8-Bit-Quittierung und +Fh für 16-Bit-Quittierung. Breite Portzugriffe erhalten keine stillschweigende neue Bytezerlegung. Die Antwortwarteschlange ist auf 64 Bytes begrenzt. Reset liefert AA, E1h die Version 4.05. Die verwendeten Reset-/Kommandofristen stammen aus dem vorhandenen Entwicklungsmodell, nicht aus einer vermessenen DSP-ROM- oder elektrischen Timingreferenz.

## DSP, DMA und Zeit

Implementiert sind die geraden generischen Bxh-/Cxh-Kommandos mit 16-/8-Bit-PCM, Ein-/Ausgabe, Mono/Stereo, signed/unsigned und Single-/Auto-Init. Der programmierte Blockwert zählt Samplekomponenten minus eins; ein 16-Bit-Block mit FFFFh umfasst 65536 Worte. Der FIFO-Anforderungsbitwert wird gespeichert und diagnostisch ausgegeben. Er aktiviert **keinen** bereits präzise modellierten Hardware-Prefetch-FIFO oder ISA-Burstbetrieb.

41h und 42h setzen getrennte Ausgabe-/Eingaberaten von 5000 bis 44100 Hz. Der Scheduler trägt den ganzzahligen Rest weiter: Ohne DMA-Stall liegt die n-te Framefrist bei `Start + floor(n × 10^9 / Rate)`. Stereo erhält pro Frame zwei Komponenten; ein blockiertes rechtes Sample wird später fortgesetzt, ohne nochmals links zu lesen. Die Zeitkonstante 40h behält ihren exakten Mikrosekundenabstand; im Stereoformat umfasst ein Frame zwei solche Komponentenintervalle. Gerätezeit wird nicht an den Host-Audiotakt angepasst.

Jede erfolgreiche DMA-Anforderung verwendet den wirklichen 8237-Controller. Kanal 5–7 liefert beziehungsweise übernimmt genau ein Wort. Fehlt die hohe DMA-Auswahl im gastbeschreibbaren Mixerregister 81h, wird ein 16-Bit-Sample über zwei aufeinanderfolgende DMA8-Grants übertragen. Ein nach dem ersten Byte blockierter Kanal bewahrt die halbe Komponente, einschließlich eines bereits erzeugten virtuellen ADC-Werts. Zähler, Maskierung, Auto-Init und TC bleiben Aufgaben des Controllers. Das ist keine neue elektrische Busarbitration oder unabhängige originale AT-A20-Verdrahtung.

D0h/D4h und D5h/D6h pausieren/setzen jeweils nur das passende Format fort. Die verbleibende Frist bleibt erhalten. DAh/D9h beenden Auto-Init nach dem aktuellen passenden Block. Die bisher benötigten Legacy-Kommandos 10h/20h/14h/24h/1Ch/2Ch/40h/48h sind ebenfalls vorhanden. D1h/D3h verändern auf diesem DSP-4.xx-Modell nur den mit D8h lesbaren Speakerstatus; CT1745 bestimmt den PCM-Pegel. Andere Befehle werden sichtbar als nicht implementiert gezählt.

Der Referenzkern besitzt **einen aktiven Halbduplex-Datenstrom**. Ein neuer Transfer ersetzt dessen aktiven Vorgänger. Zwei erhaltene IRQ-Quellen bedeuten keine gleichzeitige unabhängige 8-/16-Bit-Voll­duplex-Karte. ADC verwendet ausschließlich einen optionalen virtuellen Eingang nach der noch nicht modellierten externen Eingangsmatrix; ohne Quelle entsteht digitale Stille. Host-Mikrofon, MIDI und Netzwerk werden niemals dafür geöffnet.

## Mixer und gemeinsame Audioausgabe

Der CT1745-Teilumfang umfasst Master links/rechts 30h/31h, Voice 32h/33h, Ausgabegewinn 41h/42h, Legacy-Volumenaliase 22h/04h, Reset 00h und Ressourcen-/IRQ-Register 80h/81h/82h. Native Volumenfelder verwenden fünf Bits und nominell 2-dB-Schritte, Ausgabegewinn zwei Bits mit 6-dB-Schritten. Reset setzt die nativen Master-/Voice-Level auf 24 und verändert DSP oder Boardressourcen nicht. DSP-Reset erhält umgekehrt den Mixer.

Die Legacy-Nibbles werden nach der nominellen −60…0-dB-Tabelle auf die ungeraden nativen Level abgebildet. Das ist ein ausdrücklich begrenzter Pegelvertrag, kein vermessener bitgenauer CT1745-Alias-/Analogvertrag. Unimplementierte Mixerregister lesen FFh und zählen einen Diagnosezugriff. FM-/CD-/Line-/Mikrofonmatrix, Tone Controls und analoge Filter sind dadurch nicht implementiert.

Der gemeinsame 48-kHz-Mixer besitzt jetzt getrennte linke/rechte Quellen. Gastpegeländerungen integrieren beide Zeitabschnitte auch innerhalb eines einzelnen Hostsamples, anschließend folgen der vorhandene 20-Hz-DC-Filter und die getrennten Hostpegel. Bestehende monoaurale Speaker-/SB2-/OPL2-Pfade behalten ihren bisherigen Rechenweg; opake FM-Integratoren und Filterreste werden nicht pauschal übersprungen. Hardwarepegel dürfen nominell verstärken, Hostpegel bleiben auf 0…100 Prozent begrenzt.

## IRQ-Vertrag und Beobachtung

8- und 16-Bit-Blockabschlüsse setzen getrennte Latches. Mixer 82h liest beide, ohne sie zu quittieren. Basis+Eh löscht ausschließlich die 8-Bit-, Basis+Fh ausschließlich die 16-Bit-Quelle. Eine falsche Breitenquittierung lässt die andere Quelle und den gemeinsamen IRQ-Pegel stehen. Ressourcen 80h/81h sind bei dieser Non-PnP-Referenz gastbeschreibbar; ungültige Mehrfachauswahlen bleiben wirkungslos und werden gezählt. Eine neue IRQ-Auswahl verändert keine bereits im PIC verriegelten IRR-/ISR-Bits.

Der neue ausschließlich beobachtende PIC-Hook zeichnet **physisch angenommene IRQ-Leitung, aktuellen Vektor und Kaskaden-Spurious-Status** auf. Das war für EMM386 erforderlich: Der normale IRQ5-Vektor 0Dh ist zugleich die Nummer von #GP; ein Vergleich allein mit CPU-Vektoren zählt dessen Monitorfehler fälschlich als Audiounterbrechungen. EMM386 kann außerdem den PIC umprogrammieren. Der SB16-Trace beobachtet deshalb das tatsächliche PIC-Acknowledge. Der unabhängige Prüfer rekonstruiert den erwarteten Vektor aus den wirklichen ICW-Schreibzugriffen. Die bisherige SB2-Traceform bleibt erhalten.

## Eigener tatsächlicher DOS-Gast

`SB16.COM` läuft in **24 Kaltstarts**: drei Ressourcenpläne, MS-DOS 6.22, FreeDOS und originales EMM386 NOEMS/RAM, jeweils zweimal:

| Port | IRQ | DMA8 | DMA16 |
| --- | ---: | ---: | ---: |
| 220h | 5 | 1 | 5 |
| 240h | 10 | 3 | 6 |
| 280h | 9 | 0 | 7 |

Der Gast reserviert und initialisiert seine eigenen DOS-Puffer, programmiert DSP/Mixer/8237 über echte IN/OUT-Befehle und installiert seine eigenen DOS-IRQ-Handler. Unter EMM386 sperrt echtes VDS ein eigenes zusammenhängendes 128-KiB-Fenster mit `8103h/DX=002Ch`, ohne Remapping/Bounce-Puffer. DDS, physische Identität, Buffer-ID null und MCB-Eigentümer/-Größe werden geprüft. `810Bh/810Ch` für beide verwendeten DMA-Kanäle sowie `8104h` sind ausgeglichen. Kein Host schreibt Transferdaten oder Controllerregister stellvertretend für den Gast.

Je Start prüfen **13 Fälle** 8-/16-Bit-PCM, signed/unsigned, Mono/Stereo, virtuelle Aufnahme, Single-/Auto-Init, asymmetrische Mixerpegel, 5000/8000/11025/22050/44100 Hz und den 16-Bit-Pfad über DMA8. Bei jedem IRQ liest der Handler die Quelle, quittiert absichtlich die falsche Breite, kontrolliert deren Wirkungslosigkeit und quittiert dann richtig. Zusätzlich erzeugt der Gast bei CLI zwei wirklich abgeschlossene Streams; ein gemeinsamer Handler sieht danach beide Quellen und löscht sie nacheinander. Auto-Init wird durch Gastbefehle nach dem dritten Block beendet.

Pro Start entstehen **15 tatsächliche Transfers, 322 Samplekomponenten, 19 logische Blockanforderungen und 18 tatsächliche Handler-Rückkehrvorgänge**, davon 274 Ausgabe-/48 Eingabekomponenten und 161 DMA8-/177 DMA16-Grants. Über alle Starts: **312 reguläre Gastfälle, 7728 Komponenten, 456 Blockanforderungen, 432 Handler-Rückkehrvorgänge, 3864 Byte- und 4248 Wortgrants**. DOS gibt den Puffer wieder frei und kehrt über VER/ECHO zur Kommandozeile zurück. Vektor und PIC-Masken werden restauriert, DMA-Arbeitsregister und DSP-/Mixerzustand nicht als vollständiger Snapshot versprochen. Die HDD-COWs bleiben unverändert.

## Unabhängige Prüfung und Grenzen

`tests/verify-sb16.py` importiert keinen Emulatorkern. Es berechnet Quellmuster, virtuelle ADC-Werte, Formatsignierung, Kanalfolge, Zeitfristen, DMA-Adressen/-Counts/TC, Block-/IRQ-Zahlen sowie beide Audioausgänge getrennt. Es prüft tatsächliche Portzugriffe, ausführbare Programmbytes, Quellen/Wachen, MCB/DDS/VDS und die Rückkehr. **5107658 Stereo-PCM-Frames** werden unabhängig zeitintegriert und einschließlich DC-Filter, Gastpegeln und Float32-/PCM16-Ausgabe verglichen; auf diesem Referenzhost beträgt die größte Abweichung **0 LSB**. Links/rechts vertauschte oder genullte, neu gehashte WAVs sowie manipulierte Daten, Zeiten, Zähler und RAM-Nachweise scheitern: **732 verworfene SB16-Oracles**.

Auf denselben endgültigen sieben DLLs bestehen erneut acht Wort-DMA-, 35 Byte-DMA-/FDC-/VDS-/FORMAT-/SB2-/ADPCM-/ADC- und 20 OPL2-/CSM-/Rhythmus-Kaltstarts. Die erneute Audioabnahme ist durch den erweiterten gemeinsamen Mixer begründet. Insgesamt **87 akzeptierte DOS-Kaltstarts und 960 verworfene Gastoracles**. Vorhandene Mono-PCM-Hashes bleiben gegenüber den gespeicherten Vorgängerabnahmen gleich. Unveränderte CPU-/x87-/Grafikmatrizen und historische Performancezeiten wurden ohne neuen Grund nicht pauschal wiederholt.

72 PowerShell-Skripte/234 Checks und 99 Python-Werkzeuge sind geprüft. Ein eigenes internes GUI-Smoke mit sechs visuell geprüften Aufnahmen bestätigt das neue Kartenlabel sowie bestehende Menü-/CRT-/Scale2x-Ausgabe ohne OpenGL-Fehler. Es verwendet keine Windows-Eingaben und ist **keine** Abnahme aller vier Ressourcenbedienelemente, nativer Eingabepfade oder interaktiven Spielens. Die verschobene native Mauswiederholung bleibt unverändert offen.

Quell-/DLL-Hashes, regenerierte Programme, Originalschutz und konkrete Berichte: privater Gesamtnachweis über `artifacts/tests/m3-sb16-release-path.txt`. Originalmedien, C64, HEAD und Benutzer-Spiel-COW bleiben erhalten. Keine Commits/Pushes. Weder diese Proben noch die ältere stumme Doom-Demo belegen allgemeine SB16-/Spiel-/DOS-Extender-/Windows-Kompatibilität. OPL3, MPU/CSP, vollständiger Mixer, weitere DSP-/Legacy-Pfade, FIFO-/Analog-/Originalchip-Timing und allgemeines M3 bleiben offen.

## Reproduktion und Fortsetzung

```powershell
.\scripts\test-sb16.ps1 -SkipBuild `
  -Disk <private-blank-32m.img> -Overlay <installed-pristine.cow> `
  -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne die drei Managerpfade laufen zwölf MS-DOS-/FreeDOS-Kaltstarts. Ohne `-SkipBuild` wird zuerst gebaut. Alle Proben erzeugen neue private COWs und Disketten mit ausschließlich eigenem Gastcode. Der nächste konkrete Geräteschritt ist **YMF262/OPL3** mit festgelegten Takt-/Register-/Stereo-/Vieroperator-Verträgen, Anbindung an die CT1740-FM-Ports und unabhängigen Geräte-/DOS-Audionachweisen. Danach weitere SB16-Mixer-/DSP-Lücken und reale Programmbefunde, ET4000, Gameport/COM/LPT, MIDI und koordinierte Snapshots. Die vollständige Karte und M3 benötigen eigene spätere Abnahmen.

## Quellen

- Creative, [Sound Blaster Series Hardware Programming Guide](https://ardent-tool.com/sound/Sound_Blaster_HW_Programming_Guide_1st.pdf), PDF-Seiten 26–28, 54–58, 67–74, 107–110: DSP-4.xx-PCM, Non-PnP-Ressourcen, getrennte IRQ-Quittierung und CT1745-Pegeltabellen. Die Implementierung folgt dem oben begrenzten eigenen Referenzvertrag, kein fremder Emulatorcode.
- Creative, [Sound Blaster Model Number List](https://support.creative.com/kb/ShowArticle.aspx?sid=10846): CT1740/CT1748-Modellfamilie.
- Phil's Computer Lab, [physische CT1740-Revision-4-Untersuchung](https://www.philscomputerlab.com/ct1740.html): konkrete Karte mit DSP 4.05. Eine eigene Hardwareuntersuchung als Revisionsbeleg, kein Nachweis der analogen Genauigkeit unseres Modells.
- IBM-/Intel-/Microsoft-Primärquellen für Wort-DMA und VDS sind im vorangehenden [AT-DMA16-Teilpaket](AT-DMA16.md) verlinkt.

Die später ergänzte YMF262-/OPL3-Folgeabnahme und ihr aktueller Build sind in [OPL3.md](OPL3.md) dokumentiert. Die historischen Zähler dieses PCM-Pakets bleiben seinem damaligen Build zugeordnet.

## Nachtrag: originale Doom-Erkennung, F2h/F3h und Mixer 0Eh

Der [gezielte Folgeschritt vom 26. Oktober](M3-ANSI-SB16-FINDINGS.md) ergänzt F2h/F3h als 8-/16-Bit-Diagnoseanforderungen über die bestehenden getrennten Kartenlatches. Mixer82 und die Quittierungen +Eh/+Fh bleiben unabhängig von PIC-Acknowledge/EOIs. Softwareanforderungen zählen in `Irq8Requests`/`Irq16Requests` mit; zusätzliche `SoftwareIrq8Requests`/`SoftwareIrq16Requests` unterscheiden sie von wirklichen PCM-Blöcken. Sie erzeugen keine Samples, DMA-Grants oder `BlockCompleted`-Ereignisse. Der gewöhnliche Command-Scheduler bleibt Entwicklungstiming, kein vermessener DSP-ROM-Taktvertrag. Der geprüfte Diagnosevertrag ersetzt keine vollständige Nachbildung aller internen DMA-Steuerwirkungen des DSP.

Creative führt 0Eh mit Stereo-/Filterbits im **CT1345**-Plan, aber nicht im **CT1745**-Plan auf. Für diesen einzelnen bekannten Altsoftwarezugriff gilt deshalb ausdrücklich: stabiler reservierter FFh-Lesewert, Schreiben ohne Wirkung und separater Zähler `LegacyOutputControlAccesses`. Dies ist die bisherige wirkungslose Datenantwort mit präziserer Diagnose, kein neu implementierter SB-Pro-Stereoschalter oder Analogfilter. Der FFh-Wert ist ein Referenzvertrag, kein gemessener Vertrag undefinierter Originalchipbits. Übrige unbekannte Register zählen weiterhin `UnsupportedMixerAccesses`.

`scripts/test-sb16-diagnostic.ps1` prüft reale Handler mit denselben Basis-/Managerparametern wie `test-sb16.ps1`. 16 Kaltstarts mit IRQ5 und IRQ10 unter MS-DOS, FreeDOS und EMM386 NOEMS/RAM erzeugen 80 tatsächliche Handler-Rückkehrvorgänge, 80 F2h- und 32 F3h-Anforderungen ohne PCM-/DMA-Wirkung. Auf demselben Build bestehen erneut alle 24 PCM-/DMA-/VDS-Gäste. Die originale Doom-Erkennung bestätigt nun zwei tatsächliche F2h-IRQs vor dem ersten PCM-Start und wählt C6h mit Stereomodus 20h. Allgemeine Sound-/Echtzeit-/Siliziumkompatibilität bleibt offen.

## Ergänzender gemeinsamer Gastnachweis

[SB16-COMBINED.md](SB16-COMBINED.md) prüft inzwischen auf den unveränderten 6247er-VM-DLLs einen längeren gleichzeitigen DMA16-/OPL3-/MPU-Gast mit echten DMA8-/DMA16-/MIDI-Quellen, VDS und vollständiger Snapshot-Fortsetzung. Die dortigen Zahlen ergänzen diesen historischen PCM-Nachweis. Weitere DSP-/FIFO-/Analog-/Originaltreiberpräzision bleibt offen.
