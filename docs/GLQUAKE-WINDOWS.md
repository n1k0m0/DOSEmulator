# GLQuake unter Windows 95 und Windows 98

## Bestätigte Behebung im laufenden Win98, 5. November 2026

Der konkrete schwarze Bildschirm wurde jetzt in der vom Benutzer offen
gelassenen Win98-Instanz untersucht und behoben. Der Emulator lief weiter,
aber sowohl Front- als auch Backbuffer des nativen Renderers waren vollständig
schwarz. Ein erfolgreicher Fenster-Capture allein war deshalb kein Bildnachweis.

Ursache war die Aktivierung des eigenen MiniGlide-Logos während der
MiniGL-Initialisierung. Original-GLQuake minimiert sein Gastfenster bei
Fokusverlust; danach wurde ein 0×0 großer Clientbereich zum leeren
Zeichenbereich. Das bisherige `SW_SHOW` stellte das minimierte Fenster nicht
wieder her. Die Rückkehr zum Spiel konnte Bildwechsel reaktivieren, ließ aber
den leeren Zeichenbereich bestehen. Der
[originale GLQuake-Fensterpfad](https://github.com/id-Software/Quake/blob/master/WinQuake/gl_vidnt.c)
erklärt die Minimierung; die tatsächliche Wirkung wurde unabhängig im Gast
gemessen.

Das Logo wird jetzt ohne Aktivierung und ohne Taskleisteneintrag angezeigt.
`WM_MOUSEACTIVATE` erhält `MA_NOACTIVATE`; ein bereits minimiertes eigenes
Spielfenster wird ausdrücklich mit `SW_RESTORE` wiederhergestellt.
Artwork, Anzeigedauer, Glide-Protokoll und Originalspiel bleiben erhalten.
Die einzige neue Produktquelländerung liegt in
`src/DOSEmulator.GlideGuest/miniglide_splash.c`.

Die korrigierte eigene `GLIDE2X.DLL` wurde nach normalem Spielende durch
wirkliche Windows-Dateifunktionen in der gemeldeten Win98-Maschine installiert.
Jedes Byte wurde nach Staging, Flush und Umbenennung verglichen. Die bisherige
DLL bleibt als **`D:\QUAKE\MGOLD00.DLL`** erhalten. Der anschließende direkte
Start des unveränderten **`D:\QUAKE\GLQUAKE.EXE`** zeigt in derselben laufenden
Benutzerinstanz bewegte Bilder in 640×480. Nach dem Neustart war keine weitere
Zeichenbereich-Reparatur erforderlich. Windows wurde nicht zurückgesetzt,
Laufwerk A: ist wieder leer und das Benutzerfenster bleibt offen.

Die bisherigen `QPLAY`-/`QBENCH`-Prüfungen zeigten das Logo vor dem Spiel und
unterdrückten dessen interne Wiederholung. Sie prüften diesen fehlerhaften
Direktstart deshalb nicht. Die neue Abnahme startet das Originalprogramm ohne
Starthelfer, Splash-Unterdrückung oder Bildwechsel-Beobachter. Auf einer
frischen eigenen Win98-Kopie reproduziert die alte DLL den Fehler; die neue
DLL liefert unter Win95 und Win98 sichtbare wechselnde Spielbilder, normalen
Anwendungsschluss und das jeweils bekannte reguläre Abschaltbild. Unter Win95
erfolgte das Abschalten über den tatsächlichen Gast-Taskdialog, nachdem der
erste automatisierte Shutdown-Aufruf am Desktop geblieben war. Dieser
Harnessfehler bleibt getrennt erhalten.

Ein zusätzlicher Gast mit absichtlicher Minimierung bei Fokusverlust bestätigt
auf beiden Systemen: alt ein Fokusverlust und minimierter 0×0-Client, neu kein
Fokusverlust und erhaltener 400×300-Client; auch das ausdrückliche
Wiederherstellen besteht. Alle 62 bekannten Hostproduktdateien bleiben
bytegleich. Die neue Gast-DLL hat 19.968 Bytes und SHA-256
`ff8c923e93e99178cb06a5b86f02e9de35db59432c291c4d1bb1e4ba1121f950`.
Die bestehende Win95-Benutzerinstallation wurde nicht umgestellt.

Nachweis: `artifacts/tests/quake-black-live-release-path.txt`. Die Messungen
belegen diesen Start-/Bildfehler; sie sind kein neuer allgemeiner FPS-,
1:1-Zeit-, CPU-Regressions- oder Snapshotnachweis. Keine physischen
Hosteingaben oder Hostaudioendpunkte, kein Commit/Push; Automation pausiert.

## Eigene Maschinen starten

Die getrennten Starter unter `local/launchers/`, `Start-GLQuake-Win95.cmd` und
`Start-GLQuake-Win98.cmd` öffnen eigene Windows-Kopien mit WHPX und dem
lokalen gemeinsamen Emulator-/MiniGlide-Fenster. Im Gast anschließend über
**Start / Run** `D:\QPLAY.EXE` aufrufen. Standard ist 640×480;
`D:\QPLAY.EXE 512` wählt 512×384. Der eigene Starthelfer zeigt das
MiniGlide-Logo vor dem Originalprogramm und setzt dessen Arbeitsverzeichnis.

WASD beziehungsweise Pfeiltasten bewegen den Spieler, die Maus steuert den
Blick. Linke Maustaste greift an, Leertaste beziehungsweise rechte Maustaste
springt. Escape öffnet das Spielmenü; F6/F9 speichern/laden den eigenen
Schnellspeicherstand. Die ergänzte `DEFAULT.CFG` enthält diese englisch
beschriebenen Standardbelegungen, weil das gelieferte Datenpaket keine enthält.

Beide PCs haben getrennte beschreibbare Windows- und Spieldaten-Overlays.
Die gemeinsame neue 512-MiB-FAT16-Basis enthält die Installation unter
`D:\QUAKE`, die Starthelfer und `D:\RESULTS` für Gastberichte. Bisherige
Starter, Windows-Installationen, Spiele und Originalmedien werden nicht
umgestellt. `--inspect` prüft die neuen Starter ohne Fenster oder Gaststart.
Die Teststarter deaktivieren Hostaudio; das emulierte Gast-Soundgerät bleibt
vorhanden.

Die `.cmd`-Starter starten derzeit Windows, nicht automatisch das Spiel.
Bei einer zusätzlichen Prüfung nach der Meldung eines leeren Bildes wurden
beide Windows-Versionen erneut auf frischen eigenen Plattenkopien gebootet
und `D:\QPLAY.EXE` ohne Benchmarkhelfer gestartet. Beide lieferten wechselnde
vollständige GPU-Spielbilder, das gemeinsame DOSE-Overlay, die normale
Rückkehr zum Desktop und das bekannte reguläre Abschaltbild. Der gemeldete
Anzeigefehler wurde dabei nicht reproduziert; eine Behebung dieses konkreten
Benutzerfalls wird daraus nicht abgeleitet. Diese Prüfungen verwendeten
verdeckte eigene GUI-Instanzen und virtuelle Gasttasten, keine physischen
Hosteingaben. Nachweis:
`artifacts/quake-display-20261104-42e718b9/release-validation.json`.

## Historische Anzeigeabsicherung vom 4. November 2026

Die folgenden Captureprüfungen gingen der oben beschriebenen tatsächlichen
Fehlerdiagnose voraus. Sie beheben den bestätigten Fokus-/Zeichenbereichfehler
allein nicht.

Nach der konkreten Win98-Meldung wurde zusätzlich der echte GUI-Einstieg mit
den Starterargumenten und gespeicherten Anzeigeeinstellungen geprüft. Eine
getrennte Sicherung genau der neuen GLQuake-Win98-Platten wurde ebenfalls
getestet. Der gemeldete schwarze Bildschirm war in diesen verdeckten Prüfungen
weiterhin nicht reproduzierbar. Die Ursprungsjournale wurden nur kopiert;
Gaststarts und Gastwrites erfolgten anschließend auf eigenen Kopien.

Die Bildübernahme verwendet nun `PrintWindow` mit `PW_RENDERFULLCONTENT`.
Der vorherige Aufruf mit Flags 0 kann bei verdeckten GPU-/Compositorfenstern
schwarz liefern; die entsprechende Korrektur ist auch im
[WebRTC-Fenstercapturer](https://webrtc.googlesource.com/src/+/f89110d67902e787f6745ad2b52f7f09fc808512)
begründet. Scheitert der vollständige Aufruf ausdrücklich, bleibt der alte
Aufruf als Fallback erhalten. Das ist eine gezielte Absicherung des
Anzeigepfads, kein abschließend bewiesener Auslöser des konkreten Benutzerfalls.
Die Übernahme bleibt synchron; ein direkter Austausch von GPU-Texturen wurde
damit nicht implementiert.

Nur `Start-GLQuake-Win98.cmd` verwendet den zusätzlichen lokalen GUI-Build
unter `artifacts/quake-display-20261104-42e718b9/release-product`. Die bestehende
Win98-Maschine und ihre beiden Journale bleiben zugeordnet; der Starter setzt
keine Installation oder Spieleinstellungen zurück. Win95 und die bisherigen
Unreal-/Motorhead-Starter behalten ihre Laufzeiten. Die fünf gemeinsamen
VM-Module sowie WHPX bleiben bytegleich; ausschließlich GUI-DLL/PDB ändern sich.

Auch der zweite Einstieg `quake98.dosebranch/Start-GLQuake.cmd` leitet jetzt
an genau diesen Win98-Starter weiter. Er verwies zuvor noch auf die ältere
Quake-Laufzeit ohne das neue Anzeigeprotokoll. Beide `--inspect`-Aufrufe müssen
denselben Emulator, dieselbe Maschine und dasselbe Protokollverzeichnis nennen.
Bei der erneuten schwarzen-Bild-Meldung war keine DOSE-Instanz aktiv und noch
kein Benutzer-Anzeigeprotokoll vorhanden. Der Benutzer konnte zu diesem
Zeitpunkt nicht erneut testen. Daraus folgt weder ein nachgewiesener alter
Benutzerstart noch eine bestätigte Behebung; die konkrete Fehlerdiagnose bleibt
offen. Für einen späteren erneuten Fall genügt es, das schwarze Fenster offen
zu lassen; die vorbereiteten Metadaten liefern dann den tatsächlichen
Anzeigezustand, ohne weitere Gasttasten zu senden.

Für diesen Win98-Starter zeichnet `DOSE_GLIDE_DISPLAY_LOG` zusätzlich ein
begrenztes lokales Anzeigeprotokoll unter dem privaten Workverzeichnis auf:
höchstens 240 Metadatensätze, normalerweise einmal pro Sekunde, zusätzlich
bei Statuswechseln. Warten am Desktop verbraucht diesen Vorrat nicht. Erfasst
werden eigene Bildflächengeneration, Größe, Übernahmeergebnis und Anzahl
nichtschwarzer Pixel; keine Bilder oder Gast-RAM. Jeder Start erhält eine neue
Datei. Schreibfehler deaktivieren nur das optionale Protokoll.

Neue bewegte Spielbilder, gemeinsames Overlay, normales Spielende und regulärer
Windows-Shutdown bestehen mit dem endgültigen GUI-Build unter Win98 und in
einer Win95-Gegenprobe auf eigenen Kopien. Die Anzeigeprotokolle bestehen je
acht endliche Kontrollen, darunter ausgeschaltetes Logging, Erhalt des Vorrats
am Desktop, die 240-Record-Grenze, I/O-Fehler und unveränderte Bildbytes.
Diese privaten Kontrollen sind keine zusätzlichen registrierten CPU-Fälle.
Ein anfänglich schwarzer Capture vor dem ersten Spielbild bleibt im Win98-
Protokoll erhalten; daraus wird kein anhaltender Anzeigefehler abgeleitet.
Physische Hosteingaben und die sichtbare Ausgabe beim Benutzer bleiben
eine gesonderte Bestätigung; ein verdeckter GPU-Capture ersetzt sie nicht.

## Tatsächlich geladene Dateien

Der gewünschte [Archive.org-Eintrag](https://archive.org/details/quake-complete)
liefert eine etwa 1,05-GB-ZIP-Sammlung und **kein CD-ISO**. Die vollständige
Datei liegt privat unter `artifacts/quake-20261104-9c73a621/downloads/Quake.zip`;
Größe, SHA-1 und MD5 stimmen mit der Archivmetadatenliste überein. Aus dem
Paket wird die enthaltene `id1/pak0.pak` mit 179.618.935 Bytes und 1.071
Verzeichniseinträgen verwendet. Dies ist keine behauptete bytegleiche
Original-CD-Struktur; mitgelieferte moderne Quake-Ports werden nicht gestartet.

Das getrennt gewünschte [glq1114-Paket](https://www.dosgamesarchive.com/file/quake/glq1114)
enthält das Original-Setup von 1997. Seine komprimierten Ressourcen wurden
lokal extrahiert, ohne das Setup oder Spiel auf dem Host auszuführen.
Es installiert **GLQuake 0.97 / Quake 1.09** und **3dfx MiniGL 1.1.14**.
Die originalen `GLQUAKE.EXE` und lokalen `OPENGL32.DLL` bleiben bytegleich;
die Windows-Systembibliothek wird nicht ersetzt. Der Original-MiniGL-Treiber
setzt OpenGL in Glide um; danach folgt die eigene Gast-DLL und die geprüfte
64-Bit-Host-Brücke. Das ist eine API-Übersetzung, keine neue allgemeine
OpenGL- oder vollständige SST-1-Chipabnahme.

## Gefundene Kompatibilitätsprobleme

Der erste wirkliche Start erreichte den bisher fehlenden `grClipWindow`-Pfad.
Die lokale WHPX-Brücke ergänzt diesen und die zugehörigen begrenzten
MiniGL-Aufrufe, darunter vollständige 60-Byte-Punkte, opake 312-Byte-Zustände,
20-Byte-Leistungsstatistiken und 64-Byte-Nebeltabellen. Ausgabeziele werden
vor dem nativen Aufruf vollständig auf beschreibbaren Gast-RAM geprüft;
Float-Argumente verwenden die richtige native ABI.

Zusätzlich war die bisherige Standardkonfiguration des Host-Renderers für
diesen alten Client ungeeignet: Sie meldet standardmäßig 8 MiB Bildspeicher.
Der Original-MiniGL-Code behandelt hier nur 2 beziehungsweise 4 MiB und
beließ seine maximalen Fenstermaße bei null. Das wurde unabhängig in der
eigenen Gastdiagnose beobachtet. Ein minimiertes Fenster konnte die Prüfung
umgehen und einen Timedemo ausrechnen, zeigte jedoch kaum Bildwechsel.
Solche Vorläufe sind ausdrücklich keine gültigen Spiel-FPS.

Nur die neue private Renderer-Kopie erhält deshalb `voodoo_graphics`,
2 MiB FBI, eine TMU mit 2.048 KiB und weiterhin fenstergebundene Ausgabe ohne
Host-Mausübernahme. Jetzt erkennt der Originaltreiber 640×480 als gültige
Grenze und liefert wirkliche Spielbilder. Die bisherigen Renderer-Dateien
und deren Unreal-/Motorhead-Konfiguration bleiben erhalten. Die öffentlichen
[Glide-2-Strukturen](https://raw.githubusercontent.com/sezero/glide/glide-devel-sezero/glide2x/cvg/glide/src/glide.h)
und die [Konfigurationsbeschreibung des Renderers](https://dege.freeweb.hu/dgVoodoo2/ReadmeGeneral/)
bilden den Quellenbezug; die tatsächlichen Gast- und Pixelbeobachtungen sind
zusätzliche lokale Nachweise.

## Messverfahren

Je Windows-PC und Auflösung startet der eigene Win32-Helfer das unveränderte
Spiel viermal: einmal zum Aufwärmen und dreimal zur Messung von `demo1`.
Er liest die tatsächliche Konsolen-Ausgabe aus `D:\QUAKE\ID1\QCONSOLE.LOG`,
sichert sie nach `D:\RESULTS` und wartet auf den normalen Prozessabschluss.
Kein FPS-Wert wird aus der Emulator-Titelzeile abgeleitet.

Timedemos verwenden `-nosound -nocdaudio -nojoy -nomouse`, 32-MiB-Spielheap
und die angegebene Auflösung. Die ursprünglichen Bildwechselintervalle
bleiben erhalten, das Emulatorfenster verwendet VSync. Die Ergebnisse
beschreiben diese synchronisierte Konfiguration und keinen entsperrten
Maximaldurchsatz. Native Bildwechsel einschließlich Startphase und
Frontend-Bilder werden getrennt gezählt. Ein unabhängiges laufendes
Windows-Programm bindet QPC/GetTickCount an Host-Zeitstempel; virtuelle
Schedulerzeit wird separat verglichen. Abschließend wird auch der normale
Starthelfer mit aktivem Gast-Sound getestet und Windows regulär beendet.

## Ergebnisse vom 4. November 2026

WHPX, MiniGlide über die Host-GPU, unverändertes Originalspiel und
beibehaltene Bildsynchronisierung; jeweils drei vollständige 969-Frame-Timedemos:

| Gast | Profil | 512×384: einzelne FPS | 640×480: einzelne FPS |
|---|---|---|---|
| Windows 95 OEM/950 | P54C 100 MHz, 64 MiB | 42,6 / 42,6 / 59,2 | 59,7 / 59,3 / 59,6 |
| Windows 98 SE | P55C/MMX 233 MHz, 128 MiB | 59,6 / 59,8 / 59,4 | 59,5 / 59,6 / 59,6 |

Win95 erreicht bei 512×384 somit **keine belastbar konstanten 60 FPS**.
Die Ursache der Streuung ist hier nicht abschließend profiliert. Unterschiedliche
OS-/RAM-/CPU-Profile und synchronisierte Bildwechsel erlauben außerdem keine
allgemeine Aussage zum entsperrten CPU- oder GPU-Maximum. Als normaler Start
ist deshalb der tatsächlich geprüfte 640×480-Pfad voreingestellt.

Zusätzlich bestehen je 15,12 Hostsekunden normale bewegte Demowiedergabe bei
640×480 **mit aktivem Gast-Sound**:

| Gast | native Bildwechsel/s | Frontend-Bilder/s | tatsächlich geänderte ganze Quellbilder/s | Windows-QPC/Hostzeit |
|---|---:|---:|---:|---:|
| Windows 95 | 60,01 | 59,62 | 55,38 | 100,00049 % |
| Windows 98 | 59,35 | 59,61 | 58,29 | 100,00047 % |

Je Lauf wachsen die echten Gast-DMA-Zähler um 333.396 Samples. Das belegt
aktive Soundarbeit, keine neue unabhängige PCM-/DAC-/Mixer- oder
Hostaudioabnahme. Bilder sind tatsächliche GPU-Framebuffer-Captures; die
Zähler sind API-Rückkehr- beziehungsweise Frontend-Zeitpunkte und kein
physischer Monitor-Scanoutnachweis. Während der stark abgefragten vollständigen
Benchmark-Prozessklammern einschließlich Startphase liegt das Frontend nur
bei etwa 45–46 Bildern/s. Diese Klammern bleiben separat erhalten; die obigen
normalen Wiedergabeintervalle sind keine zusätzlichen Timedemos.

Die zwölf gemessenen Windows-QPC-/Hostklammern liegen insgesamt zwischen
99,97180 und 100,25408 Prozent. Der obere Wert enthält einen Start-/Aufholrand;
er wird nicht ausgeblendet. Die beiden normalen Wiedergabeintervalle liegen
praktisch bei 1:1. Das ist ein begrenzter Lastnachweis dieser Quake-Szenen,
keine universelle Echtzeitgarantie für alle Spiele und Konfigurationen.

Originales Spielende und beide regulären Windows-Shutdowns sind belegt.
Bei Win95 wurden ein nicht ausgelöster zweiter Startbefehl und später der
automatische Shutdown-Hilfsaufruf als Harnessdiagnosen festgehalten. Die
endgültige Messreihe benutzt eine frische eigene Windows-Kopie und neutrale
virtuelle Modifikatortasten. Der abschließende Win95-Shutdown wurde über den
wirklichen Gast-Taskdialog ausdrücklich gewählt; sein vollständiges bekanntes
Abschaltbild stimmt bytegenau. Kein Task wurde erzwungen beendet und kein
Hardware-Reset verwendet. Beide GUI-Owner sind mit Exit 0 gejoint; die
historischen Harness-Exit-1-Berichte bleiben separat erhalten.

Alle installierten Bytes von Spiel, Original-MiniGL, PAK, eigener Gast-DLL,
Starthelfer und Standardbelegung wurden anschließend unabhängig aus den
FAT16-/COW-Strukturen beider PCs gelesen und gegen die neuen Basisdateien
geprüft. Beide FAT-Kopien und alle gelesenen Journalprüfsummen stimmen.
Die vollständige native Testsuite besteht; der Transportprüfer enthält
475 Kontrollen, davon 37 neu. Derselbe endgültige Prüfer scheitert am
vorherigen WHPX-Modul ausdrücklich am fehlenden MiniGL-Aufruf. Die fünf
gemeinsamen VM-Module und GUI bleiben bytegleich zum vorherigen lokalen
Anzeigestand; nur WHPX-DLL/PDB ändern sich. 49 Zustandstypen, 992 Mitglieder
und 44 Callbackkörper bleiben generatorgeprüft; 1.032 Quell-/Projektdateien,
darunter 542 C#, bestehen die Qualitätsprüfung.

Privater Gesamtnachweis und Dateien:
`artifacts/tests/quake-release-path.txt` →
`artifacts/quake-20261104-9c73a621/release-validation.json`.
Aktive native Glide-Zustände erhalten weiterhin keine neue Snapshotfreigabe.
Physische Host-Maus-/Tastatur-/Audioabnahme bleibt unverändert offen.
