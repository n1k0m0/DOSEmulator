# Historischer 7873er-M4-Baustein: P54C-MSR-Schnittstelle für den TSC

Aktueller Folgeschritt: [P54C-Stack-/Gate-Vertrag](P54C-STACKS.md) auf 8371. Der folgende MSR-Nachweis bleibt seinem eingefrorenen 7873er-Build zugeordnet; CPUID-/MSR-Teilumfang sind durch die Stackkorrektur unverändert.

Stand: 27. Oktober 2026. **7873/7873 Regressionen, normaler Build ohne Warnungen/Fehler.** Der interne, ausdrücklich clockgebundene [P54C-B5-Teilumfang](P54C-B5.md) ergänzt RDMSR/WRMSR für den vorhandenen [virtuellen TSC](P54C-TSC.md). Das ist ein weiterer CPU-Baustein; auswählbare Produkt-PCs verwenden weiterhin 486, und die vollständige Pentium-/Windows-95-Plattform bleibt offen.

## Historischer Primärvertrag und Grenzen

Grundlage ist Intels [Pentium Processor Family Developer's Manual Volume 3, 241430-004, Juli 1995](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf): RDMSR auf 25-263/264 (PDF 880/881), WRMSR auf 25-313/314 (PDF 930/931), die Bedeutung des MSR-Featurebits in 26.1 auf 26-1 (PDF 942) und der TSC-Vertrag in 26.1.2/26.2.2 auf 26-2/3 (PDF 943/944). Der lokal geprüfte Scan besitzt SHA-256 `63C27628292DE539EB6EA9089404FE7B5D1FCA9D292021A4493DCE044E9AA988`.

| Funktion | Jetzt implementierter Vertrag |
| --- | --- |
| RDMSR | `0F 32`, ECX=00000010h; vollständiger TSC nach EDX:EAX, unveränderte Flags und übrige GPRs. |
| WRMSR | `0F 30`, ECX=00000010h; vollständige 64 Bits aus EDX:EAX werden übernommen. Auch ein nichtnulliges oberes Dword und Rückwärtsschreiben sind erlaubt. |
| Rechte | Real Mode oder CPL0. CPL1–3 und V86 liefern #GP(0), unabhängig von CR4.TSD. Der TSD-Schalter für RDTSC ersetzt keine MSR-Privilegienprüfung. |
| Präfixe | 66h/67h ändern weder die 64-Bit-Wertbreite noch die 32-Bit-ECX-Auswahl. LOCK erzeugt #UD vor der MSR-Wirkung. |
| Unverfügbare Adressen | Jede andere vollständige ECX-Adresse liefert #GP(0) ohne Zähler-/GPR-Änderung. Kein Abschneiden auf CX, kein Freigeben undokumentierter hoher Aliase. |
| Identifikation | Ein ausdrücklich clockgebundener Kern meldet nun **CPUID.1:EDX=0131h: FPU/TSC/MSR/CX8**. Quellenlose bestehende Konstruktoren behalten 0101h und ihren bisherigen engeren Befehlssatz. Alle Produktprofile bleiben 486. |

Intel 26.1 beschreibt das MSR-Featurebit als Verfügbarkeit der **RDMSR-/WRMSR-Schnittstelle**. Es verspricht keinen modellübergreifend identischen Registersatz. Der jetzige eigene Teilumfang implementiert genau **MSR 10h**; insbesondere reale P54C-Maschinenprüf-, Cache-/TLB-/BTB-Testregister und CESR/CTR0/CTR1 sind **noch nicht implementiert** und werden ausdrücklich abgewiesen. Ihre Antwort wird nicht als vermessene Antwort des vollständigen B5-Siliziums ausgegeben. Diese offenen Register-/Systemverträge werden vor einer umfassenden P54C-Freigabe separat behandelt. MCE, DE, VME und PSE bleiben unbeworben.

Der historische Pentium-Vertrag beschreibt den TSC als 64-Bit-Register mit EDX:EAX-Schreibzugriff. Der neue Gast verwendet gezielt hohe Dwords, Low-Dword-Überträge und einen vollständigen 64-Bit-Überlauf. Ein späterer oder anderer Prozessortyp mit eingeschränkter TSC-Schreibbreite darf diese Modellentscheidung nicht stillschweigend ersetzen. Keine native Referenz auf einer modernen Host-CPU wird hier als B5-Messung behauptet.

## Virtuelle Epoche und Fehlergrenze

Die bereits gespeicherte `_tscEpochTicks` genügt zur Darstellung eines geschriebenen Werts: am Ownerpunkt wird die Epoche modulo 2^64 aus dem aktuellen virtuellen Quelltick minus dem vollständigen Schreibwert berechnet. Zukünftige Lesezugriffe bilden Quelltick minus Epoche. Das erhält auch negative modulare Abstände, spätere Überträge und Wrap, ohne den Scheduler, seinen Taktrest, Token oder Gerätefristen zu verändern.

Modell, Rechte und vollständige ECX-Adresse werden vor jedem Quellzugriff beziehungsweise Register-/Epochencommit geprüft. Faults kehren zum Instruktionsanfang zurück; ein nach erfolgreichem Zugriff ausgelöster TF-Trap liegt dagegen nach dem Commit. Real-Mode-#GP erhält seinen tatsächlichen Rahmen ohne zusätzliches Fehlerwort. Der schon geprüfte Pentium-/486-Unterschied bei 32-Bit-Fehlerrahmen bleibt erhalten. Reset verankert den Zähler wieder bei null an der laufenden Quelle und spult Geräte nicht zurück. INIT, SMM, physische Power-Pins, Mehrprozessor- und Bus-/Pipelinezyklen sind dadurch nicht implementiert.

Der Interpreter führt die Befehle als vollständige Owneroperationen aus. Keine Host-MSR-Befehle, kein Host-TSC, keine Wanduhr und keine künstliche Änderung der vier virtuellen Entwicklungstakte je Step. Clockzugriffe innerhalb eines aktiven Schedulercallbacks bleiben unzulässig. Das existierende explizite Inventar bleibt bei **38 Typen, 751 Instanzmitgliedern und 32 unveränderten Ereigniskörpern**; es kommen keine neuen Datenfelder oder Timer hinzu. Die feste virtuelle Quelle und die vollständige CPU-/Schedulerphase müssen gemeinsam restauriert werden.

## Endgültiger Nachweis

**384 neue Kernfälle, insgesamt 7873 Regressionen**. Tatsächliche WRMSR-/RDMSR-Bytes in Real Mode, PE16/PE32 und Paging prüfen acht volle 64-Bit-Werte, alle vier 66h-/67h-Kombinationen, reine Clockabfragen, unveränderte GPRs/Flags und TLB-Zähler. 23 abgewiesene ECX-Adressen werden in beiden Befehlen und vier Modi geprüft. Weitere Fälle prüfen CPL1–3, beide TSD-Werte, gepagtes V86, LOCK, fehlende Quelle/486, echten TF-Trap nach Commit, Reset nach hoher Zählerschreibung und rohe CPU-/Scheduler-Fortsetzung über einen Wrap. Die bisherige TSC-CPUID-Prüfung erwartet wegen der tatsächlich implementierten Schnittstelle nun 0131h; die alte 7489er-Abnahme bleibt eingefroren bei 0111h.

Ein eigener Reset-ROM-Gast führt **vier endgültige Starts und vier tatsächliche ausgewählte Komponentenfortsetzungen** aus, bei 33/90 MHz jeweils zweimal. **32 vollständige WRMSR-Schreibvorgänge, 52 erfolgreiche RDMSR- und 32 RDTSC-Ablesungen**, **16 physische PIT-IRQ0-Handler/EOIs** sowie **16 echte #GP- und acht echte #UD-Handler-Rückkehrvorgänge**. Unveränderte Gastbytes liefern die Sollmuster; echte Fehlerrahmen werden im Gast gespeichert, die fehlerhaften Instruktionen begrenzt übersprungen und die erhaltenen Register/Flags danach geprüft. Ein letzter RDMSR sichert, dass abgewiesene Schreibversuche die laufende Epoche nicht ersetzt haben. Rohe x87-Pi-Daten und RAM-Wachen bleiben erhalten.

Der Schnitt liegt beim ersten tatsächlichen HLT nach zwei echten MSR-Schreibvorgängen: nichtnullige geschriebene Epoche, laufender PIT, zukünftige Frist und nichtnulliger Taktrest. Danach bleiben **2036 weitere vollständige CPU-/PIC-/PIT-/Scheduler-/ganze 2-MiB-RAM-Vergleiche**, zukünftige CPU-/Port-/Timer-/IRQ-Ereignisse und Endzustände identisch. Zwei Wiederholungspaare sind zeit-/RAM-/Zustands-/Publikationshashgleich. Das unabhängige Python-Oracle rekonstruiert **10356 tatsächliche Ledger-Einträge**, alle Zählerwerte/Schreibepochen, 1.193.182-Hz-PIT-Fristen, Eingangs-/Rückkehradressen und Fehler-/GPR-Wirkungen ganzzahlig. **856 Verfälschungen scheitern.** Dies bleibt ein benannter Komponentengraph mit PIC-/Speaker-Unterzuständen; kein DOS-Start oder vollständiger P54C-PC mit Medien.

Wegen des neuen CPUID-MSR-Bits bestehen zusätzlich vier gezielte bisherige TSC-Resetstarts und vier Komponentenfortsetzungen auf den neuen DLLs: 920 weitere ausgewählte Zustands-/2-MiB-RAM-Vergleiche, 32 RDTSC-Ablesungen, 16 IRQ0-Handler und 288 verworfene Oracles. Es werden keine alten Berichte nachträglich dem neuen Build zugeschrieben.

Getrennt bestehen **acht tatsächliche 486-DOS-Kaltstarts und acht volle PC-Fortsetzungen** unter MS-DOS/FreeDOS/originalem EMM386 NOEMS/RAM je zweimal, mit **1282 weiteren vollständigen CPU-/8-MiB-RAM-/Geräte-/Schedulervergleichen**, echter PCM-DMA, rohen x87-DOS-Dateien und **200 verworfenen Oracles**. Alle sieben getesteten DLLs bleiben bis zum Abschluss bytegleich. Gesamtes Paket: acht eigene Resetstarts/acht ausgewählte Fortsetzungen plus acht DOS-Kaltstarts/acht volle PC-Fortsetzungen, **1344 negative Gastoracles**. Der eine Diagnose-Resetstart und sein Oracle sind ausgeschlossen. Die erste Matrix vor der abschließenden Korrektur doppelter Quell-Zeilenumbrüche ist wegen ihrer anderen DLL-Identitäten ebenfalls als Vorlauf getrennt.

Der vollständige 7489er-Starter und seine 742 Quellen sind archiviert. Ein wirklicher eigener alter 7489er-CLI-Import wird vor Zweiganlage abgewiesen und lässt die Sicherung unverändert; Format 1 bleibt buildgebunden. Die bestehenden vollen Importablehnungen bleiben Teil der aktuellen Gesamtsuite. **637 lizenz-/kommentargeprüfte Code-/Projektdateien, 131 Python-Werkzeuge und 89 PowerShell-Skripte**. Kandidat und normaler Starter sind mit 64 Dateien bytegleich. Alle 115 geschützten Eingaben, C64 und HEAD unverändert; persönliche Spiel-COWs ungeöffnet. Keine Windows-Eingaben, Hostverbindungen, neue Videoaufnahme, Commits/Pushes oder allgemeine Performance-/Spiel-/Sound-/Siliziumzusage.

Reproduktion:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 MSR"
.\scripts\test-msr.ps1 -SkipBuild
```

Privater Gesamtnachweis **`artifacts/tests/m4-msr-release-path.txt`**. Nächster konkreter Schritt: P54C-spezifische Selektor-Push-/Pop-/Rahmenbreiten und ihre Stacklimit-/Fault-/Restartgrenzen gegen den historischen Primärvertrag schließen, danach weitere CR4-/MSR-/Systemvoraussetzungen und tatsächliche DOS-/volle P54C-PC-Integration vor Profilfreigabe. 430FX/PIIX/Ressourcen, S3 Trio64, ATAPI, Firmware/Manager sowie echte Windows-95-RTM-Installation/Starts/Shutdown bleiben offen; M5–M8 folgen in Abhängigkeitsreihenfolge.
