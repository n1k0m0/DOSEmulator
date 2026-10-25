# Originales DOS/4GW und Doom II

Stand: 25. Oktober 2026. M3-Referenzpfad nach [VCPI/EMS](X86-VCPI-EMS.md), **keine vollständige M3-Abnahme**. Release-Build bei Abschluss dieses Teilpakets: **920/920 Regressionen, 0 Warnungen/Fehler**; das nachfolgende [FDC-/VDS-Paket](X86-FDC-VDS.md) besitzt 942 Regressionen; 40 neue Fälle für CPU-Erkennung und x87-Kontrollbefehle.

## Tatsächlich ausgeführter Gast

Im privaten Medienverzeichnis des Benutzers liegen DOOM2.EXE und DOOM2.WAD. Die unveränderte EXE startet ihren gebundenen **DOS/4GW Professional 1.95**, lädt das Spiel im 32-Bit-Protected-Mode, reserviert seinen DPMI-Zonenspeicher, liest die WAD-Datei und zeichnet den ersten Level in **320×200 mit VGA-Palette**. Der Extender und seine DOS-/DPMI-/VCPI-Dienste laufen als Gastinstruktionen. Es gibt keine Host-DPMI-Abkürzung und keinen ersetzten Doom-Port.

Die Referenz verwendet eine eigene kleine Demo mit 32 Bewegungs-/Dreh-Ticks im ursprünglichen Demoformat: MAP01, ein Spieler, keine Monster, kein Sound, keine Hostmaus und kein Joystick. Nach dem Demo-Ende meldet der Originalcode `timed 32 gametics ...`, beendet sich über seinen regulären Timedemo-Endpfad und erreicht VER/ECHO sowie den echten MS-DOS-6.22-Prompt. Das ist ein begrenzter ausführbarer Spielnachweis; Kämpfe, längere Levels, Sound, physische Eingaben, Menüs, Speichern/Laden und allgemeine Spielkompatibilität sind daraus nicht abzuleiten.

Die Quelldateien bleiben unverändert und privat:

| Datei | Bytes | SHA-256 |
| --- | --- | --- |
| DOOM2.EXE | 709.905 | B8020523561A5AD9706E009A52D61C578F37FAAFD85AC471962308406292CE27 |
| DOOM2.WAD | 14.604.584 | 10D67824B11025DDD9198E8CFC87CA335EE6E2D3E63AF4180FA9B8A471893255 |

## Behobene CPU-Grenzen

Der erste Originalstart stoppte in der FPU-Erkennung des Extenders. Hinzugekommen sind **FNINIT, FNCLEX, FLDCW, FNSTCW, FNSTSW in Speicher/AX, FNOP und der auf dem 486 wirkungslose 287-Befehl FSETPM**. Kontroll-/Status-/Tagwort werden tatsächlich geführt; erfolgreiche ESC-Ausführungen besitzen einen Diagnosezähler. FNSTSW AX erhält das obere EAX-Wort. EM/TS liefern #NM vor den Operandenwirkungen. Speicherzugriffe laufen durch Segment-/MMU-Prüfung, einschließlich ausbleibender Teilwrites und unverändertem Kontrollwort bei einem zweiten fehlerhaften Byte. Ein CPU-Reset ohne BIST behält den vorhandenen FPU-Kontrollzustand bei; FNINIT initialisiert ihn ausdrücklich.

**Die Kontrollbefehle sind kein arithmetischer x87-Kern.** Das spätere [x87-Zustandspaket](X86-X87-STATE.md) ergänzt inzwischen acht tatsächliche 80-Bit-Register, Stack/Tags und Umgebung/Save/Restore mit eigenen DOS-Gastnachweisen. [Elementare Arithmetik, Konvertierungen und Rundungs-/Ausnahmeantworten](X86-X87-NUMERIC.md) sind inzwischen ebenfalls geprüft. Vollständiges x87 und FERR/IGNNE-Verdrahtung bleiben offen; noch nicht implementierte gültige x87-Befehle stoppen ausdrücklich diagnostisch. Die 32-Tick-Demo benötigt nur die ursprünglichen Kontrollbefehle: 15 ESC-Ausführungen ohne Manager beziehungsweise mit HIMEM, 17 mit EMM386. Ein erfolgreicher FPU-Erkennungstest bedeutet deshalb keine allgemeine FPU-Kompatibilität.

Anschließend deckte die originale CPU-Erkennung einen weiteren Modellfehler auf: EFLAGS.ID ließ sich umschalten, obwohl das ausgewählte frühe 486DX-Modell mit Reset-Signatur 0400h kein CPUID besitzt. Das Modell hält ID und die nicht vorhandenen VIF/VIP-Bits nun null, erhält aber das 486-AC-Bit. POPFD, IRETD und V86-Eintritt verwenden dieselbe Modellmaske. Ein direkt ausgeführtes CPUID erzeugt echten #UD statt eines Host-Stops; die originale DOS/4GW-Erkennung wählt nun selbst den passenden 486-Pfad. Das implementiert weder spätere 486-/Pentium-CPUID-Modelle noch die noch offene vollständige Alignment-Prüfung.

## Prüfmatrix

```powershell
.\scripts\test-dos-extender.ps1 -SkipBuild -Disk <IDE-Basis> -Overlay <pristines-COW> -Game <privates-Doom-2-Verzeichnis>
```

Das Skript erzeugt ein **neues privates FAT16-Image** aus der nachgewiesenen Installation. Nur DOOM2.EXE/WAD sowie eigene DOOMTEST.CFG/FASTDEMO.LMP kommen hinzu. Der unabhängige FAT-Prüfer verlangt unveränderte Originaldateien, identische FAT-Kopien, korrekte Ketten und keine verlorenen Cluster. Die benutzereigenen Einstellungen und Spielstände werden weder übernommen noch verändert. DOS selbst schreibt anschließend drei private Konfigurationen mit CONFIG.PRE-Sicherung: HIMEM `/TESTMEM:ON`, HIMEM plus EMM386 `NOEMS`, HIMEM plus EMM386 `RAM`.

**Acht Doom-Kaltstarts bei 8 MiB**, jeweils zwei je Konfiguration:

| Konfiguration | Ergebnis | Real-Ticks des emulierten Gasttimers |
| --- | --- | --- |
| Ursprüngliches DOS-Profil ohne Speichermanager | 32 Game-Ticks, VGA-Spielszene, DOS-Rückkehr | 192 |
| HIMEM /TESTMEM:ON | 32 Game-Ticks, VGA-Spielszene, DOS-Rückkehr | 193 |
| HIMEM + EMM386 NOEMS | 32 Game-Ticks, VGA-Spielszene, Monitor-/DOS-Rückkehr | 193 |
| HIMEM + EMM386 RAM | 32 Game-Ticks, VGA-Spielszene, Monitor-/DOS-Rückkehr | 193 |

Dies sind Zeitwerte im deterministischen Entwicklungsmodell, keine Messung historischer 486-Leistung oder Host-FPS. Wiederholungen sind pro Konfiguration zeit-, Schlussbild- und Spielszenenfolgen-gleich. Während jedes Prüflaufs bleiben Basisimage und COW byteidentisch: **keine ATA-/FDC-Writes, kein Hardware-Reset, kein Seitenfehler**. Der originale Timedemo-Endpfad speichert keine Defaults. Die Nachprüfung vergleicht weiterhin alle DOS- und Spieldateien sowie den eigenen Demo-Inhalt.

Der eigene **CPUX87.COM** wird außerdem in acht Kaltstarts von einer ausschließlich eigenen FAT12-Probe geladen: je zweimal unter ursprünglichem MS-DOS, FreeDOS, EMM386/NOEMS und EMM386/RAM. Er prüft ID-/AC-Verhalten, FPU-Kontroll-/Statuswerte, EAX-Teilerhalt, den eigenen #UD-Rahmen bei absichtlichem CPUID, wiederhergestellten INT6-Vektor und Kontrollwort sowie DOS-Rückkehr. Ein unabhängiger Prüfer kontrolliert tatsächliche NASM-Bytes und übersetzten RAM; pro Start 13 ESC-Ausführungen. Der Test initialisiert Status/Tags und behauptet keine Wiederherstellung eines vollständigen x87-Zustands.

## Bildnachweis und Grenzen

`--graphics-capture <neues privates Verzeichnis>` zeichnet geänderten Grafik-Scanout in 100-ms-Abständen der Gastzeit auf, begrenzt auf 128 PPM-Dateien. Der Bericht enthält Abmessungen, Hash, Zeit und CPU-Modus. Aufnahmen können einen gerade gezeichneten Frame zeigen; sie sind kein Signal des Spiels für eine abgeschlossene Präsentation. Der optionale Pfad verändert keine Gastzeit, Geräte oder nativen Eingaben.

Der separate Python-Prüfer liest **STBAR und PLAYPAL direkt aus der lokalen WAD**, dekodiert das spaltenweise Patchformat, berechnet Gamma-0-/6-Bit-DAC-Farben und vergleicht **3.591 feste HUD-Pixel exakt**. Veränderliche Zahlen, Waffenanzeige, Gesicht und Schlüssel sind aus dieser Hintergrundmaske ausgeschlossen. Zusätzlich müssen sämtliche Farben aus der tatsächlichen Palette stammen und mehrere unterschiedliche Spielfeldansichten existieren. Pro Lauf bestehen 30 beziehungsweise 31 solcher Szenenbilder; ein ausgewähltes wirkliches 320×200-Spielbild wurde visuell geprüft. Damit sind weder alle Renderer-Pixel noch alle VGA-Spezialformen als originalgetreu abgenommen.

**14 korrumpierte Oracle-Kontrollen** werden abgelehnt: acht für Doom, sechs für den eigenen CPU-Client. Dazu zählen fehlende Demo-Ticks/Rückkehr, falsche CPU-/ESC-Zähler, falsches Kontrollwort/Fehler-IP/Befehlsbyte sowie fehlende, falsch gehashte und intern konsistent neu gehashte schwarze Bilder. Die Bildprüfung hängt somit nicht allein von einem PASS-Text oder vom selbst gemeldeten Bildhash ab.

Die vorherigen 20 VCPI-/EMS- und vier Paging-/V86-Kaltstarts bestehen mit dem gemeinsamen Build erneut. Die CPU-/Gerätesuite besitzt 920 Fälle; 45 PowerShell-Skripte/153 Checks und 38 Python-Werkzeuge sind geprüft. Private Einzel- und Gesamtnachweise stehen im [Implementierungsstand](../IMPLEMENTATION-STATUS.md). Keine GUI-/Fokusversuche, native Mauswiederholung, Commits, Pushes oder Veröffentlichung von Medien/Spielbildern.

## Nächste M3-Arbeit

Das nachfolgende [FDC-/VDS-Teilpaket](X86-FDC-VDS.md) prüft inzwischen Schreiben, Lesen, Verify, Format und echte physische VDS-Puffer unter EMM386 NOEMS/RAM. Ungeeignete interne EBDA-Pufferlagen werden mit AH09 abgewiesen. Call-Gates, Hardware-Tasks, LOCK/Alignment und Debugregister besitzen inzwischen weitere eigene Teilabnahmen. Auch x87-Zustand/Save/Restore und [elementare 80-Bit-Arithmetik](X86-X87-NUMERIC.md) sind nun geprüft. Nächste Vertikale ist die FERR-/IGNNE-Fehlerleitung mit tatsächlichen Ausnahmegästen, danach weitere x87-Operationen. SB16/OPL3, ET4000, Gameport/COM/LPT, MIDI, koordinierte Snapshots und M4–M8 bleiben offen. Die auf später verschobene native M2-Mauswiederholung bleibt ohne neue Aufforderung unangetastet.

Primärgrundlagen: Intels [486 Programmer’s Reference Manual](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Kapitel 17/19/25 und Kontrollbefehle in Kapitel 26; Intels [Application Note 485, CPUID-Erkennung](https://www.scss.tcd.ie/jeremy.jones/CS4021/processor-identification-cpuid-instruction-note.pdf); die von id Software veröffentlichten Quellen zu [Demoformat/-Ende](https://github.com/id-Software/DOOM/blob/master/linuxdoom-1.10/g_game.c), [Gamma/Spalten-Patches](https://github.com/id-Software/DOOM/blob/master/linuxdoom-1.10/v_video.c) und [HUD-Belegung](https://github.com/id-Software/DOOM/blob/master/linuxdoom-1.10/st_stuff.c). Sie dienen Format- und Verhaltensverträgen; Spiel-/Extendercode oder WAD-Daten wurden nicht in den Emulator übernommen.
