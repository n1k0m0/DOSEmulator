# DOSEBios: begrenzte ET4000AX-VBE-Schnittstelle

Stand: 26. Oktober 2026. Eigene Gastfirmware für `et4000ax-isa-1m-development`, **VBE-1.2-Datenformat mit den Funktionen 00h/01h/02h/03h/05h**. Dies ist eine geprüfte bankbasierte Teilmenge, keine vollständige VBE-1.2-Konformität oder Originaltreiber-/Windows-/ET4000-Abnahme. Die folgenden Nachweise gehören zum **5342er-Build**.

## Angebotene Fähigkeiten

| VBE-Modus | Auflösung | Format | Legacy-Modus | Weitere vollständige Bilder im 1-MiB-VRAM |
| --- | --- | --- | --- | --- |
| 0101h | 640×480 | 8 Bit, 256 Farben | 2Eh | 2 |
| 0103h | 800×600 | 8 Bit, 256 Farben | 30h | 1 |
| 0105h | 1024×768 | 8 Bit, 256 Farben | 38h | 0 |

Die Firmware verwendet ausschließlich den vorhandenen ET4000AX-/VGA-Speicher- und Portpfad. Keine Host-VRAM-Injektion, kein fremdes ROM, kein belegtes Options-ROM-Fenster C0000h–EFFFFh. Der BIOS-Code und seine Tabellen liegen im eigenen System-ROM F000h. Die Standardkarte **DOSE VGA** erhält keine VBE-Fähigkeit.

- **4F00h:** genau 256 Bytes Controllerinformationen mit `VESA`, Version `0102h`, eigenem DOSEBios-OEM-Text und realen ROM-Far-Pointern auf Text/Modusliste. `TotalMemory=16` bedeutet 16×64 KiB. Fähigkeiten bleiben null: VGA-kompatibel, sechs Bit DAC, keine erfundenen Erweiterungen. Auch eine vorbelegte `VBE2`-Signatur vergrößert die Antwort dieser 1.x-Schnittstelle nicht auf 512 Bytes.
- **4F01h:** genau 256 Bytes für einen der drei tatsächlich angebotenen Modi. Window A ist verschiebbar, lesbar und beschreibbar, 64-KiB-Größe und -Granularität, Segment A000h; Window B fehlt. Zeilenlänge entspricht der Breite, `MemoryModel=4`, 8 bpp, eine logische Ebene. `NumberOfBanks=1` beschreibt die Scanline-Speicherorganisation und ist keine Zählung der sechzehn CPU-Fenster. PhysBase/LFB-, High-Color- und TTY-Fähigkeiten bleiben null.
- **4F02h:** Moduswechsel über tatsächliche Gastregister und den bestehenden Legacy-Pfad. Bit 15 erhält das gesamte MiB; ohne dieses Bit löscht der Gast alle sechzehn Banken. Nicht angebotene Modi und reservierte Bits einschließlich LFB-Anforderung werden abgewiesen, bevor Modus/Bank/VRAM verändert werden.
- **4F03h:** tatsächlicher Modus aus der BDA, übersetzt 2Eh/30h/38h auf 0101h/0103h/0105h. Nach Rückkehr in 03h/12h/13h werden diese Legacy-Nummern geliefert. Gemäß dem begrenzten VBE-1.x-Vertrag wird das Clear-Bit 15 nicht zurückgegeben. Es gibt keinen versteckten aktuellen Modus in einer festen EBDA-Adresse.
- **4F05h:** Window A setzen/lesen, Banken 0 bis 15. Setzen koppelt beide Nibbles von 3CDh. Ein durch direkten Portzugriff absichtlich getrennter Lese-/Schreibbankzustand kann nicht durch eine einzelne RW-Window-Nummer beschrieben werden und liefert beim Get einen Fehler, ohne den Bankwert zu ändern. Der Unlock folgt dem tatsächlich aktiven Mono-/Farbdecoder; Misc-Out bleibt erhalten.

Der Mode-Info-Far-Pointer `WinFuncPtr` kann direkt mit `CALL FAR` aufgerufen werden. BH/BL/DX genügen, AX muss dabei nicht 4F05h enthalten. Der eigene Callback liefert zusätzlich AX=004Fh/014Fh, bewahrt andere Arbeitsregister einschließlich ihrer oberen Hälften sowie Callerflags/Stack und kehrt mit **RETF** zurück. INT10 bleibt ein tatsächlicher Interrupt mit dem vorhandenen Register-/IRET-Vertrag. Der Status steht in AX, nicht im Carryflag.

Info-Puffer bei ES:DI bis einschließlich `FF00h` sind erlaubt; `FF01h` bis `FFFFh` werden vor jedem Schreibzugriff abgewiesen. Ein gültiger Puffer darf also am letzten Byte des Segments enden. Größere, segmentübergreifende Strukturen werden nicht vorgetäuscht. Unbekannte Funktionen, Window B, ungültiges BH oder Bank 16 und höher liefern AX=014Fh.

**Offen bleiben insbesondere 4F04h Save/Restore, 4F06h Scanline, 4F07h Display Start, 4F08h DAC-Format, 4F09h Palette und 4F0Ah Protected-Mode-Interface.** Die noch fehlenden 1.x-Dienste sind der Grund, weshalb das angebotene Datenformat keine vollständige VBE-Konformitätszusage ist. LFB, High Color, fremde VBE-/Tseng-Treiber, alle BIOS-Zeichendienste und Raster-/Bus-/Siliziumpräzision sind ebenfalls nicht abgenommen. Die zusätzlichen Bildplätze in der Tabelle implementieren noch keinen Display-Start-Dienst.

## CPU-, Firmware- und Gastnachweise

**66 neue Regressionen, insgesamt 5342/5342**, normaler Build ohne Warnungen/Fehler. Die Fälle prüfen sämtliche Informationsbytes, reale ROM-Pointer, gültige/ungültige Puffer bis FFFFh, erhaltene Register/Flags/Schutzbytes, Modus-Clear/Preserve, alle sechzehn Banken, ungültige Aufrufe ohne Datenwirkung und tatsächlichen Far-Call/RETF. Dazu gehören willkürliches AX am Callback, getrennte Rohport-Banken und der aktive Mono-Decoder.

`scripts/test-et4000.ps1 -Vbe` besteht **32 tatsächliche Kaltstarts**: drei Auflösungen unter MS-DOS 6.22, FreeDOS sowie originalem EMM386 NOEMS/RAM, je zweimal, ergeben 24 Grafikstarts. Acht weitere Kaltstarts führen alle drei Programme mit regulärer DOS-Rückkehr aus. Eigene `VBE640.COM`, `VBE800.COM`, `VBE1024.COM`:

- Pro Ausführung 70 erfolgreiche VBE-Aufrufe, 14 absichtliche Ablehnungen, vier Moduswechsel und 16 direkte Far-Bankaufrufe. Der Gast füllt und vergleicht **das gesamte MiB** über INT10-Bankwechsel und rückwärts laufende direkte Far-Callbacks. Er erzeugt die Daten selbst und prüft alle zugehörigen Werte.
- Anschließend die bestehenden 81 ET4000-Fälle: unabhängige Lese-/Schreibnibbles, Ebenen, Latches, vollständiges Löschen/Erhalten und sichtbares Muster. Die getrennte Prüfung berechnet **jedes sichtbare Pixel** selbst, statt nur einen vom Emulator gemeldeten Hash zu übernehmen.
- Controller-/Modusbytes, Callback-/OEM-/Listenpointer im tatsächlichen ROM, ES-Grenzwachen, Befehlsbytes, RAM-Beweise, tatsächliche INT10-Versuche und 3CDh-OUTs, KBC sowie Paging-/V86-Ausführung werden unabhängig geprüft. **1056 verfälschte Oracles** scheitern. Beide Wiederholungen sind zeit-/bild-/nachweishashgleich.

Auf denselben **sechs tatsächlich von Headless geladenen VM-/Headless-DLLs** sind zusätzlich die **32 bisherigen Legacy-ET4000-Kaltstarts** und **540 verfälschte Oracles** geprüft. Der ursprüngliche Gesamtharness meldete am abschließenden Hashguard einen Fehler, weil währenddessen ausschließlich die nicht von Headless geladene Tests.dll um zwei neue Testfälle ergänzt wurde. Kein Gast-/Pixeloracle war fehlgeschlagen. Die endgültige unabhängige Prüfung hat alle 24 Grafikartefakte, acht DOS-Rückkehrberichte, unveränderten Testjournale und sechs geladenen DLL-Hashes erneut geprüft. Der private Bericht erhält den ursprünglichen Harness-Exit ausdrücklich; ein neuer CPU-Kaltstart wird für diese Artefaktprüfung nicht gezählt.

Vier weitere kurze originale Doom-II-Kaltstarts auf DOSE VGA/ET4000, jeweils ohne/mit Ton, verwenden **neue Kopien des gespeicherten Video-PCs**: je 32 Ticks, DOS-Rückkehr, zusammen **120 WAD-geprüfte Bilder** und unveränderte Journale. Die Soundvarianten erzeugen tatsächliche PCM-/FM-Ausgabe ohne nicht implementierte DSP-/Mixerzugriffe. Das ist keine allgemeine lange/interaktive Spiel-/Sound-/Echtzeitabnahme und keine neue Performancebehauptung. Die bereits abgeschlossenen historischen CPU-/x87-/FDC-/Fontmatrizen werden ohne zusätzlichen Befund nicht pauschal wiederholt.

Gesamt: **68 akzeptierte Headless-Kaltstarts, 1596 verworfene Grafik-Gastoracles**. Die drei neuen Auflösungsaufnahmen sind zusätzlich visuell geprüft; keine GUI-/Windows-Eingabe. Lizenz-/Kommentar-/Syntaxprüfung: **523 Code-/Projektdateien, 78 PowerShell-Skripte**. Originale/Baselines, beide Video-PCs und ausgelieferte Videos bleiben erhalten; persönliche Spiel-COWs werden nicht geöffnet, gehasht oder verändert. Kein Commit/Push. Privater Gesamtnachweis: **`artifacts/tests/m3-vbe-release-path.txt`**.

## Reproduktion und Fortsetzung

```powershell
.\scripts\test-et4000.ps1 -SkipBuild -Vbe -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne die drei optionalen Managerpfade werden MS-DOS/FreeDOS geprüft. Ohne `-Vbe` bleibt die bisherige Legacy-Matrix aktiv. Die erzeugte FAT12-Testdiskette enthält ausschließlich eigene Programme. Keine Benutzer-Spiel-COW als Quelle verwenden.

Als Nächstes folgen die übrigen M3-Ports **Gameport/COM/LPT**, anschließend MIDI und koordinierte Hardware-/Overlay-Snapshots. Zusätzliche VBE-/Grafik-/Chippräzision wird bei tatsächlichem Gastbedarf separat ergänzt; die offenen Dienste oben bleiben offen. Gespeicherte PCs sind weiterhin Kaltstartdefinitionen. Vollständige ET4000, vollständiges x87, M3 und M4–M8 sind nicht insgesamt abgenommen.

## Primärquelle

VESA, **VBE Core Functions Standard, Version 2.0, Revision 1.1, 18. November 1994**, Abschnitte 4.3–4.8 und ausdrücklich gekennzeichnete 1.x-Hinweise: Controller-/Modusstrukturen, Status, Modebits, Funktion 03h und direkter Window-Callback. [Archivierter Originalstandard](https://www.phatcode.net/res/221/files/vbe20.pdf). Die 1.x-Hinweise werden einzeln umgesetzt; daraus entsteht keine Behauptung vollständiger VBE 2.0. Die Tabellen und Routinen sind eigener Code. Der konkrete Speicher-/Registervertrag bleibt in [ET4000AX.md](ET4000AX.md) dokumentiert.
