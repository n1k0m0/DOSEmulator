# IBM-Gameport: nominaler Analogpfad

Stand: 26. Oktober 2026, **5488er-Build**. Konkretes funktionales Modell des eigenständigen **IBM Game Control Adapter 6361493**, gewählt durch `gameport=ibm-game-control-development`. Standardprofile und ältere gespeicherte PCs behalten `gameport=none`. Die Karte wird nicht automatisch durch die SB16-Auswahl eingeschaltet und öffnet keinen Host-Gamepad-, Mikrofon-, MIDI- oder Netzwerkzugang.

## Gerätevertrag

Die Karte besitzt vier Widerstandseingänge und vier Schalter. Ein tatsächliches Byte-OUT startet gleichzeitig alle vier virtuellen Impulse; der geschriebene Datenwert wählt keine einzelne Achse. Der Referenzpfad verwendet die im IBM-Handbuch 6361493 angegebene Nominaldauer:

`t = 24,2 µs + 0,011 µs × Widerstand in Ohm`

Das entspricht exakt `24200 + 11 × R` Nanosekunden ohne Gleitkommarundung. Der erlaubte Widerstand ist 0 bis 100000 Ohm; Null ist ein angeschlossener Eingang mit endlicher Mindestdauer. `null` ist ein offener Stromkreis, der nach dem Trigger nicht abläuft. Vier Eingänge laufen unabhängig ab. Beim Erreichen der jeweiligen Grenze wird das Bit null; Lesen löst keinen neuen Impuls aus und verbraucht keine zusätzliche virtuelle Zeit.

| Bit | Bedeutung |
| --- | --- |
| 0 / 1 | Joystick A, X / Y |
| 2 / 3 | Joystick B, X / Y |
| 4 / 5 | Joystick A, Taste 1 / 2 |
| 6 / 7 | Joystick B, Taste 1 / 2 |

Die oberen vier Bits sind aktiv niedrig. Schalteränderungen werden beim nächsten IN direkt sichtbar; das Modell erfindet keine Entprellung. Widerstände werden für die laufende Messung am OUT abgetastet. Eine Änderung während des laufenden Impulses verändert diesen nicht rückwirkend. Ein neues OUT startet die vier nominalen Impulse mit den dann aktuellen Widerständen neu. Das ist der begrenzte funktionale Vertrag, keine elektrische Simulation von bewegten Potentiometern, Kondensatoren, Bauteiltoleranzen oder sämtlichen NE558-Triggerdetails.

Der originale Decoder betrachtet A9..A0. Im 16-Bit-I/O-Raum sind daher alle **64 Aliase von 0201h bis FE01h in Schritten von 0400h** derselbe Adapter. Nachbarports bleiben unbestückt. Der geprüfte Zugriff ist **Byte-I/O**. Wort-/Doppelwortzugriffe werden als nicht unterstützte, ungetriebene Zugriffe behandelt und starten keine versteckten Bytekonversionen; daraus folgt keine allgemeine ISA-Zyklus-/AEN-/HOLD-/DMA-/Wide-I/O-Abnahme.

Ein neuer kalter Adapter startet ohne laufende Konversion und ohne angeschlossene Achsen, mit offenen Schaltern. Vor dem ersten Trigger sind die Timerbits im Modell null; für undefinierte elektrische Einschaltzustände wird keine Originalchip-Zusage gemacht. Nach einem Trigger bleiben offene Achsen hoch. Der CPU-Hot-Reset setzt diese versorgte Karte, ihren Eingabezustand oder ihren Triggerzeitpunkt nicht zurück. Ein Kaltstart erzeugt eine neue Karte und verwirft den flüchtigen externen Eingabezustand.

Die Implementierung benötigt keine Timer-Callbacks pro Impuls oder Poll-Schritt: IN vergleicht die aktuelle virtuelle Zeit mit dem gespeicherten Triggerzeitpunkt. Es gibt keine IRQ- oder DMA-Leitung und keinen Host-Zeit-/CPU-Takt-Schätzwert. Das bestehende CPU-Entwicklungstiming bleibt ausdrücklich keine historische 486/33-Kalibrierung.

## Profil, BIOS und externe Eingaben

Das Hardwaremenü bietet die Karte über den bestehenden Neustartpfad an. Profil und gespeicherte PC-Konfiguration enthalten die Adapterauswahl. Ein gespeicherter PC ist weiterhin eine Kaltstartdefinition; aktuelle Joystickwerte oder ein laufender Impuls sind dadurch kein gespeicherter Hardware-Snapshot.

DOSEBios setzt für die konfigurierte Karte **BDA-Equipment-Bit 12**, liefert es über INT11 und nennt die Karte in der POST-Inventur. Ohne Karte bleiben Bit und Inventurzeile aus. Im historischen 5488er-Build war INT15/AH84 noch nicht implementiert. Der unten ergänzte 7030er-Vertrag implementiert jetzt DX0/DX1 als tatsächliche Firmware; die früheren Zahlen und Negativkontrollen gehören weiterhin ihrem historischen Build.

`SetGameportInput` setzt über die normale emulationseigene IPC-Warteschlange ein unveränderliches Objekt:

```json
{"axOhms":0,"ayOhms":25000,"bxOhms":50000,"byOhms":100000,"buttons":5}
```

Das sind äußere Widerstände/Schalter, keine Portregister oder fertigen Messwerte. Die Gastsoftware muss selbst OUT/IN ausführen. `buttons` enthält vier gedrückte Schalter in A1/A2/B1/B2-Reihenfolge. Ungültige Werte werden vor einer Änderung vollständig abgewiesen. Ein Szenarioschritt kann dasselbe Objekt als `gameport` mit tatsächlichem VGA-/E9-Wartekriterium und einer Gasttaste verwenden. **Host-Gamepad-Zuordnung, Deadzones, Benutzerkalibrierung und native Joystickbedienung bleiben offen.**

Headless bietet `--gameport-io-trace <neue.jsonl>` als rein beobachtende Diagnose. Sie zeichnet die tatsächlichen dekodierten I/O-Zugriffe samt virtueller Zeit und E9-Marker auf. Die Abschlussmeldung unterscheidet äußere Eingaben, Trigger und Reads. Ein anliegender analoger Zustand ersetzt keine Gast-Portmessung.

Bei der IPC-Erweiterung ist außerdem die veraltete VBE-Fähigkeitsmeldung korrigiert: `GetCapabilities` nennt die vorhandenen Funktionen 00h/01h/02h/03h/05h und meldet weiterhin `fullVbeConformance=false`. Die ausgewählte Standardgrafik DOSE VGA erhält dadurch keine VBE-Karte; die konkrete Instanz bleibt im normalen Maschinenbericht erkennbar.

## Nachweise

**146 neue Geräte-/Firmware-/Profil-/IPC-/Szenariofälle, insgesamt 5488/5488**, normaler Build ohne Warnungen/Fehler. Geprüft sind alle Achsen mit 0/1/100/49999/50000/99999/100000 Ohm jeweils unmittelbar vor, genau an und nach der Grenze; alle sechzehn Schalterkombinationen; 64 Adressaliase und Nachbarports; unabhängige Fremdereignisse; unveränderte virtuelle Zeit beim Lesen; Widerstandsabtastung versus laufende Schalter; Wiedertriggern; offene Leitungen; ungültige Eingaben ohne Teiländerung; Hot-Reset; BDA/INT11/POST; ehrliches AH84-Fehlen; alte/gespeicherte PCs und IPC-/Szenario-Pfade.

Eigene **GAMEPORT.COM** besteht acht endgültige Kaltstarts unter MS-DOS 6.22, FreeDOS und originalem EMM386 NOEMS/RAM, jeweils zweimal. Je Start 24 äußere Zustände mit asymmetrischen Achsen, Randwerten, offenen Leitungen, beiden Joysticks und allen Tastenbits; zwei tatsächliche OUT-/IN-Messungen sowie eine späte Messung je Zustand. Insgesamt **192 Zustände, 384 echte Trigger und 108320 tatsächliche Byte-INs**. Vier Portadressen 0201h/0601h/0A01h/FE01h werden wirklich im Gast benutzt. Der Gast prüft seine Rücklesewerte und Schutzbytes und kehrt regulär zu VER/ECHO/DOS zurück. Keine Host-Timer-/Register-/RAM-Injektion.

`verify-gameport.py` berechnet jeden gemessenen Portwert aus dem wirklichen OUT-Zeitpunkt und den unabhängig vorgegebenen Widerständen; jeder Übergang, alle Poll-Zähler und die späten offenen Leitungen werden geprüft. Dazu kommen wirkliche unveränderte COM-Bytes, BDA/INT11, AH84-Fehler, übersetzte RAM-Beweise, Schutzbytes, KBC-Eingaben, tatsächliches V86/Paging, leere Schreibzähler und unveränderte Testjournale. **188 verfälschte Oracles** scheitern, einschließlich nur eingegebener statt ausgeführter DOS-Rückkehr. Wiederholungen besitzen gleiche virtuelle Zeit sowie Bild-/Proofhashes. Zwei frühere Probe-Diagnoseläufe mit fehlerhafter Textspiegelung beziehungsweise zu kleinem probeneigenem DOS-Stack zählen nicht als diese endgültige Matrix; beide betrafen ausschließlich den Testgast.

Auf denselben endgültigen DLLs bestehen vier gezielte VBE-Grafikkaltstarts unter den vier Betriebssystem-/Managerkonfigurationen, verteilt auf alle drei Auflösungen: vollständige Pixel-/ROM-/Bank-/RAM-Prüfung und **352 zusätzliche verworfene Oracles**. Vier kurze originale Doom-II-Kaltstarts auf DOSE VGA/ET4000 ohne/mit Ton liefern **120 WAD-geprüfte Bilder**, je 32 Ticks, tatsächliche DOS-Rückkehr und unveränderte Testjournale. Diese Proben begründen keine allgemeine Spiel-/Sound-/Gamepad- oder neue Performanceabnahme.

Gesamt **16 akzeptierte Headless-Kaltstarts, 540 verworfene Gastoracles**, Lizenz-/Kommentar-/Syntaxprüfung aller **529 Code-/Projektdateien und 79 PowerShell-Skripte**. Unveränderte x87-/CPU-/FDC-/übrige Audiomatrizen und historische Performancemessungen werden nicht pauschal wiederholt. Keine GUI-/Windows-Eingaben; der neue Menüpunkt besitzt dadurch keine zusätzliche native Bedienungsabnahme. Originalmedien, Baselines, beide Video-PCs und ausgelieferte Videos bleiben erhalten; persönliche Spiel-COWs werden nicht geöffnet. Kein Commit/Push. Privater Gesamtnachweis **`artifacts/tests/m3-gameport-release-path.txt`**.

```powershell
.\scripts\test-gameport.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne die drei Managerpfade werden nur MS-DOS/FreeDOS geprüft. Die FAT12-Testdiskette enthält ausschließlich das eigene Programm. Benutzer-Spieljournale sind keine Testeingaben.

Die nächste COM-Vertikale ist inzwischen als begrenzter [PC16550D-COM1-Vertrag](SERIAL-16550.md) separat geprüft. Weitere Reihenfolge: LPT, MIDI und koordinierte Hardware-/Overlay-Snapshots. Zusätzliche Gameport-/AH84-/Hostmapping-Arbeit bleibt ausdrücklich offen. M3 insgesamt ist nicht abgenommen.

## Primärquellen

IBM, **Game Control Adapter, Hardware Reference Library, 6361493**, gedruckte Seiten 1, 3–7 und Schaltbild 9: Widerstandseingänge, Tasten, Decoder, Nominalgleichung und fehlende IRQ-/DMA-Verbindung. [Archiviertes Herstellerhandbuch](https://www.minuszerodegrees.net/oa/OA%20-%20IBM%20Game%20Control%20Adapter.pdf). Die Triggerbeschreibung auf Seite 5 enthält einen widersprüchlichen IN-Satz; der konkrete Vertrag folgt der eindeutigen OUT-Trigger-/IN-Lesebeschreibung auf Seiten 3–4. Microsoft, **KB61806, BIOS Equipment-List Word**, bestätigt BDA/INT11 und Bit 12; [archivierter Originalartikel](https://www.betaarchive.com/wiki/index.php/Microsoft_KB_Archive/61806). Eigener Code, keine kopierte BIOS-/Treiberimplementierung.


## BIOS-Ergänzung und M3-Abschluss, 7030er-Build

Die eigene Firmware implementiert den IBM-Vertrag INT15/AH84 aus *IBM Personal System/2 and Personal Computer BIOS Interface Technical Reference, Mai 1988*, Seiten 2-83/2-84 (private Referenz-PDF, digitale Seiten 100/101). DX=0 liest genau ein Byte an 201h und liefert die aktiven niedrigen Schalter in AL7..4, AL3..0/AH null, ohne Trigger. DX=1 startet tatsächlich alle vier Achsen und liefert AX/BX/CX/DX als unabhängige Pollzähler. Die Zähler sind Firmwareeinheiten und keine Widerstände oder historischen Mikrosekunden.

Nach höchstens 4096 echten INs endet die Messung. Noch hohe offene Achsen liefern FFFFh; endliche Achsen behalten ihre gemessenen Zähler. Ein gültiger Aufruf liefert CF=0, auch wenn eine Achse offen ist. Fehlende Karte oder DX außerhalb 0/1 liefert AH86/CF=1 vor I/O. Andere Register, hohe GPR-Hälften, Segmente, Stack und übrige Flags bleiben erhalten. CMOS/NMI/RTC werden dafür nicht angefasst. Es gibt keine neue Hostzuordnung oder elektrische Kalibrierung.

31 neue Firmwarefälle und acht endgültige MS-DOS-/FreeDOS-/EMM386-NOEMS-/RAM-Starts prüfen 392 tatsächliche BIOS-Aufrufe, 192 äußere Zustände und insgesamt 576 Trigger einschließlich der bisherigen direkten Messungen. Der unabhängige Prüfer berechnet jeden Portwert aus dem realen OUT-/IN-Zeitpunkt und jeden BIOS-Zähler aus der wirklichen Schleife; 956 Verfälschungen scheitern. CLI und IPC melden AH84 jetzt wahrheitsgemäß. Die [M3-Gesamtabnahme](../M3-ACCEPTANCE.md) und `artifacts/tests/m3-completion-release-path.txt` enthalten die aktuellen Buildidentitäten. Hostgamepad-/Kalibrierungs-/Chippräzision bleibt getrennt.
