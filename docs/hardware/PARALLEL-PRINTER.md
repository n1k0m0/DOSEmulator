# LPT1: IBM Printer Adapter 6361507 und virtuelle Byte-Senke

Stand: 26. Oktober 2026. Eigener funktionaler Vertrag einer konkreten **IBM Printer Adapter 6361507**, kein universeller SPP-/IEEE1284-/Druckervertrag. Primärquelle ist die [ursprüngliche IBM-Optionsdokumentation samt Schaltplan](https://www.minuszerodegrees.net/oa/OA%20-%20IBM%20Printer%20Adapter.pdf), insbesondere die gedruckten Seiten 2–4 und 8–10. Für die grundlegende INT17-Schnittstelle dient zusätzlich das [PhoenixBIOS-Herstellerhandbuch](https://bitsavers.org/pdf/phoenix/PhoenixBIOS_4.0_Users_Manual_2000.pdf), gedruckte Seite 64. Kein fremdes ROM oder Druckertreiber wurde übernommen.

## Auswahl, Ressourcen und Register

`parallelPort=ibm-printer-adapter-development` aktiviert **LPT1, 378h, IRQ7**. Standard und alte gespeicherte PCs behalten `none`. Hardwaremenü und PC-Definition speichern die Karte; der Wechsel ist ein Kaltstart. Das Profil `profiles/virtual486-parallel-dev-v1.json` enthält die Auswahl. Eine gleichzeitig aktive SB2-/SB16-Karte auf IRQ7 wird vor Übernahme abgewiesen; andere zulässige Audioressourcen bleiben verwendbar.

Die Hardwareauswahl allein verbindet keinen Drucker. Ein gespeicherter PC enthält weder äußere Pinpegel noch einen laufenden Handshake oder einen Dateiausgabecursor. CPU-Hot-Reset erhält die versorgte Karte und ihre Fristen; Kaltladen erzeugt leere Latches und keine Gegenstelle. Das ersetzt keinen koordinierten Hardware-/Overlay-Snapshot.

| Offset | Lesen | Schreiben |
| --- | --- | --- |
| +0 | Acht Datenlatches | Acht tatsächliche Datenleitungen |
| +1 | Live-Status: invertiertes BUSY, ACK, PE, SELECT, ERROR | Keine Wirkung |
| +2 | Fünf Steuerlatches; obere Bits im Modell hoch | STROBE, AUTOFEED, INIT, SELECT-IN, IRQ-Freigabe |
| +3 | Ungetriebener Buswert | Keine Wirkung |

Der originale Schaltplan dekodiert **A9..A3**, lässt **A2 und A10..A15** unbeschaltet. Deshalb gehören auch 37Ch–37Fh und die 64 hohen Adressgruppen derselben Karte: **128 Aliase pro Register**. Die Nachbarbereiche bleiben offen. Unterstützt ist Byte-I/O; breite Zugriffe liefern den ungetriebenen Wert und bewirken keine versteckten Bytezugriffe. Es gibt keine PS/2-Richtungsschaltung, EPP, ECP, DMA oder weitere LPT-Karte.

STROBE, AUTOFEED und SELECT-IN sind gegenüber ihren Steuerbits invertiert; INIT ist nicht invertiert. Datenlesen liest die Ausgangslatches, keinen bidirektionalen Datenbus. **Status-Bit 4 enthält die ODER-Verknüpfung von äußerem SELECT und physischem SELECT-IN** gemäß Schaltplan. Undefinierte niedrige Statusbits, hohe Steuerbits und unverbundene Eingänge bleiben im Modell stabil hoch. Das sind begrenzte Bus-/Pullup-Konventionen, keine elektrische Toleranz- oder AEN-Abnahme.

## ACK, IRQ7 und virtueller Handshake

Die gedruckte IBM-Beschreibung und der originale Schaltplan widersprechen sich bei der IRQ-Flanke: Der Text beschreibt die fallende ACK-Flanke, der Schaltplan führt ACK über einen **nichtinvertierenden LS125** auf IRQ7. DOSE folgt ausdrücklich dem Schaltplan: **ACK high UND Steuerbit 4** ergibt die aktive IRQ7-Leitung. Bereits das Freigeben bei hohem ACK kann eine erste steigende IRQ-Flanke erzeugen. Statuslesen quittiert nichts; Pinpegel, Kartenfreigabe, PIC-IRR/ISR, tatsächliches Interrupt-Acknowledge und PIC-EOI bleiben getrennt. Andere Kartenrevisionen oder historische elektrische Übergänge werden damit nicht zugesagt.

`--parallel-sink` verbindet ausdrücklich eine eigene **synthetische Rohbyte-Senke**. Sie interpretiert keine Druckersprache und erzeugt weder Seiten noch Textlayout. AUTOFEED wird protokolliert, fügt aber keine Bytes hinzu. Es wird kein Hostdrucker oder Windows-Spooler geöffnet. Der veröffentlichte Peer-Vertrag lautet:

| Grenze | Nominaler Vertrag |
| --- | --- |
| Daten vor STROBE | Mindestens 500 ns stabil |
| Aktiver niedriger STROBE | Mindestens 500 ns |
| Daten nach STROBE-Freigabe | Mindestens 500 ns stabil; danach genau ein Byte-Commit |
| ACK nach Commit | Nach 10000 ns niedrig, weitere 5000 ns später hoch und BUSY frei |
| INIT-Freigabe | 50000 ns Erholung; INIT low bricht noch nicht angenommene Arbeit ab |

Zeitpunkte stammen aus dem Gerätescheduler. Ein ruhender Port benötigt keine zusätzliche Pollprüfung pro CPU-Schritt. BUSY, fehlende Auswahl, zu kurze Pulse, instabile Daten und Reset können einen Strobe ablehnen. Bereits ausgegebene Bytes lassen sich durch Reset nicht zurücknehmen. Die Fristen sind **eigene nominale Peer-Parameter**, keine vermessenen Zeiten eines physischen IBM-/Epson-/Centronics-Druckwerks. Papierstau, Seitendruck, Raster, Tinte, mechanische Bereitschaft und elektrische Leitungseffekte sind nicht implementiert.

## DOSEBios und äußere Gegenstellen

ROM-Auswahl **E017h**, BDA **0040:0008=0378h**, LPT1-Timeout **0040:0078=1**, LPT-Anzahl in BDA/INT11 und eine eigene POST-Inventurzeile kennzeichnen die Karte. Die übrigen LPT-Basen bleiben null. Eigene Gastfirmware führt tatsächliche **INT17/AH00–02, DX=0** aus: Byte senden, INIT und Live-Status. AL, übrige Register und Flags bleiben im dokumentierten Modell erhalten. Nicht unterstützte Ports/Dienste und die fehlende Karte liefern AH01 vor Portkontakt; die frühere unbegründete Ready-Antwort des unimplementierten Stubs entfällt.

AH00 wartet auf tatsächliches Non-Busy, schreibt Daten und einen gültigen Strobe und wartet erneut auf Non-Busy. Fünf NOPs sichern Setup-/Puls-/Hold-Abstände im ausschließlich unterstützten 33-MHz-Entwicklungsprofil. Nichtnullige BDA-Fristen zählen ganze RTC-Sekunden zusätzlich zum ersten Teilintervall; null ist eine kurze begrenzte Probe. Eine unabhängige Obergrenze von vier Millionen Polls verhindert Endloswarten bei eingefrorener Uhr. IF sowie CMOS-Index/NMI werden erhalten. AH01 führt einen begrenzten INIT-/Erholungspuls aus und meldet danach den tatsächlichen Status, auch bei unverbundener Karte; es wartet nicht zusätzlich auf eine erfundene Druckbereitschaft. Diese Schleifen sind keine historische BIOS-Zykluskalibrierung.

Ohne Peer ändern IPC **`SetParallelInput`** oder das Szenariofeld **`parallel`** nur äußere physische Pegel auf dem Emulationsthread:

```json
{"parallel":{"busyHigh":false,"ackHigh":true,"paperEndHigh":false,"selectedHigh":true,"errorHigh":true}}
```

`null` beziehungsweise fehlende Felder erhalten die betreffende Leitung. Eine verbundene virtuelle Senke besitzt ihre Eingänge exklusiv; manuelle Updates werden dann vollständig abgewiesen. Eingaben setzen keine fertigen Gaststatuswerte, IRQ-Zähler oder RAM-Beweise.

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Headless.dll `
  --profile profiles/virtual486-parallel-dev-v1.json `
  --disk <eigene-basis.img> --overlay <neues-privates.cow> `
  --parallel-output <neue-rohbytes.bin> --parallel-io-trace <neue.jsonl> `
  --parallel-sink
```

Dateiausgabe und Trace verwenden ausschließlich neue Dateien. Der Trace beobachtet tatsächliche IN/OUT, externe Pinflanken, angenommene Bytes und physische PIC-IRQ7-Acknowledges. Ohne `--parallel-output` gibt es keinen Byte-Dateicursor; ohne `--parallel-sink` keine automatische Gegenstelle. Die virtuell gerahmte COM-Gegenstelle und die LPT-Senke sind parameterlose CLI-Schalter, auch am Ende der Befehlszeile.

## Geräte- und Gastnachweise

**307 neue Geräte-/BIOS-/Integrationsfälle, insgesamt 5995/5995 Regressionen**, normaler Build ohne Warnungen/Fehler. Geprüft sind sämtliche Aliase, alle Steuer-/Statuskombinationen, Setup-/Puls-/Hold-Grenzen, IRQ-Freigabe/PIC-Trennung, Reset vor Commit und innerhalb eines Annahmebeobachters, IPC-Exklusivität, IRQ7-Ressourcenkollision, Warm-/Kaltstart, BDA/POST und tatsächliche INT17-Rückkehr mit Register-/Flag-/Timeout-/CMOS-Erhaltung.

`tests/guests/parallel-test.asm` wird als eigene **LPT.COM** ausgeführt. Der Gast entdeckt BDA/INT11, reserviert seinen DOS-Block, installiert einen echten IRQ7-Handler, programmiert Daten/Steuerung und quittiert den PIC. Er gibt 16 Bytes durch INT17, alle 256 Bytewerte durch die 128 originalen Registeraliase und ein Zeichen durch DOS/AH05 aus. Breite I/O bleibt wirkungslos; INIT bricht einen noch nicht abgeschlossenen Strobe tatsächlich ab. Anschließend führen die originalen DOS-Kommandos `ECHO >PRN` und `COPY /B ... PRN /B` die Ausgabe fort. Eine getrennte Pinvariante prüft alle 32 Eingangskombinationen, das SELECT-IN-ODER, reale steigende ACK-IRQ-Flanken und Busy-Timeout.

Die eigene FAT12-Diskette enthält ausschließlich LPT.COM und **LPTDATA.TXT**. COPY erhält eine tabulatorfreie eigene Datei: FreeDOS expandiert Tabs bei gekochter PRN-Ausgabe anhand seiner globalen Konsolenspalte; `COPY /B` ist allein keine Zusage eines Raw-PRN-Handles. Dieser originale DOS-Vertrag wurde nicht im Emulator umgangen. Alle 256 Rohwerte sind weiterhin unabhängig am direkten Gerätepfad geprüft. Der Zusammenhang ist zusätzlich im [originalen FreeDOS-`chario.c`](https://raw.githubusercontent.com/FDOS/kernel/master/kernel/chario.c), `cooked_write`/`update_scr_pos`, nachvollziehbar.

Nach Wiederherstellung von Handlervektor und PIC-Maske bleibt der begrenzte Testblock über **DOS/INT21/AH31** resident. Dadurch kann FreeCOMs späterer COPY-Puffer den Nachweis nicht wiederverwenden. Das unabhängige Oracle prüft auch tatsächlichen MCB-Eigentümer und Blockgröße. Dieser Testgast beansprucht Speicher bis Reset; vollständige Speicherfreigabe oder CPU-/DOS-/PIC-/Scheduler-Wiederherstellung und Drucken auf physischer Hardware werden nicht behauptet.

**16 endgültige LPT-Kaltstarts** unter MS-DOS, FreeDOS und originalem EMM386 NOEMS/RAM prüfen je Senke und manuelle Pins zweimal: **15384 tatsächliche Rohbytes**, **2088 physische IRQ7-Acknowledgements/Handler-Rückkehrvorgänge**, **272 äußere Pinzustände** und **acht wirkliche INIT-Abbrüche**. Je Sinkstart werden genau 1923 Bytes aus BIOS, direkten Ports, DOS/AH05, ECHO und der eigenen 1635-Byte-COPY-Datei angenommen. **832 verfälschte LPT-Nachweise** werden abgewiesen. Wiederholungen sind in virtueller Zeit, Bild, Ausgabe und Proof identisch; die Testjournale bleiben unverändert. Frühere Diagnose-/Fixtureläufe und die Matrix vor der abschließenden CLI-Schalterkorrektur zählen nicht als endgültige Starts.

Vier COM1- und vier VBE-Kaltstarts prüfen die gemeinsamen Firmware-/Portpfade mit 628 beziehungsweise 352 weiteren Verfälschungen; vier kurze originale Doom-Starts auf DOSE VGA/ET4000 ohne/mit Ton liefern 120 WAD-geprüfte Bilder und DOS-Rückkehr. **Gesamt 28 akzeptierte DOS-Kaltstarts, 1812 verworfene Gastoracles** auf denselben sieben DLLs. Ein zusätzlicher eigener POST-Kaltstart mit Gameport/COM1/LPT1 besitzt 256000 unabhängig geprüfte Pixel, zwölf negative Kontrollen und eine visuell geprüfte Aufnahme. 545 lizenz-/kommentargeprüfte Code-/Projektdateien und 81 PowerShell-Skripte/265 Checks bestehen. Keine GUI-/Windows-Eingabe oder neue native Ressourcen-/Mausabnahme; keine neue allgemeine Drucker-/Spiel-/Sound-/Performanceabnahme. Historische CPU-/x87-/Audiomatrizen wurden ohne zusätzlichen Befund nicht pauschal wiederholt.

Reproduktion mit frischen privaten Journalkopien:

```powershell
.\scripts\test-parallel.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Ohne die optionalen Managerpfade werden nur MS-DOS und FreeDOS geprüft. Originale, Video, persönliche Spieljournale und C64 bleiben unberührt. Der private Gesamtnachweis wird über **`artifacts/tests/m3-lpt-release-path.txt`** referenziert. Zusätzliche Karten-/Drucker-/Hostpräzision bleibt offen; als nächste M3-Vertikale folgt MIDI mit ausdrücklich virtueller Gegenstelle, anschließend koordinierte Hardware-/Overlay-Snapshots. Vollständiges x87 und M3 bleiben insgesamt offen.
