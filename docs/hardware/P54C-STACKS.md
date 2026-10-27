# M4: P54C-Stackbreiten und Rückkehr aus schmalen Gates

Historischer 8371er-Nachweis. Aktueller Folgeschritt: [P54C-DE-/I/O-Debugging](P54C-DEBUG.md) und [gemeinsame POP-Speicherkorrektur](X86-POP-MEMORY.md) auf 8689; dessen tatsächlicher Umfang und Grenzen stehen dort.

Stand: 27. Oktober 2026. **8371/8371 Regressionen, normaler Build ohne Warnungen/Fehler.** Der interne P54C-B5-Kern erhält zwei belegte Korrekturen für Stackrückkehr und Fehlerrahmen. [MSR/TSC](P54C-MSR.md), Identifikation und CMPXCHG8B bleiben erhalten; alle auswählbaren Produkt-PCs verwenden weiterhin 486. Die vollständige Pentium-/Windows-Plattform bleibt offen.

## Primärvertrag und konkrete Korrektur

Intel, [Pentium Processor Family Developer's Manual Volume 3, 241430-004, Juli 1995](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf), 23.2.19.3/4/6, gedruckte Seiten 23-23/24 (PDF 584/585), trennt die beiden Modelle. Der lokal geprüfte Scan besitzt SHA-256 `63C27628292DE539EB6EA9089404FE7B5D1FCA9D292021A4493DCE044E9AA988`.

Bei einer 16-Bit-RETF-/IRET-Rückkehr auf einen äußeren 32-Bit-Stack setzt Pentium ESP[31:16] auf null. Der 486 setzt dort den SS-Selektor ein. DOSE benutzte bisher die 486-Antwort für beide Modelle. `RestoreOuterStackPointer` trennt nun ausdrücklich: P54C erweitert den gelesenen 16-Bit-Pointer mit null; 486 behält seinen bisherigen Selektorteil. Nachfolgende Parameterbereinigung erfolgt auf dem so restaurierten Stack. 32-Bit-Rückkehrwerte und Rückkehr auf einen 16-Bit-Stack behalten ihren Vertrag.

Der bereits vorhandene Pentium-Fehlerdword wird vollständig geschrieben. Seine Vorprüfung berücksichtigte bisher jedoch wie beim 486 nur das untere Wort. Bei einem um die 16-Bit-Stackgrenze gewickelten Rahmen kann das obere Wort auf einer anderen, nicht verfügbaren Seite liegen, während andere Rahmenteile wieder nahe Offset null liegen. Die P54C-Vorprüfung umfasst jetzt den vollständigen Fehlerdword vor dem ersten Framecommit. Der 486 prüft und schreibt weiterhin nur sein tatsächliches Fehlerwort. Kein allgemeines Segmentlimit, keine Taskepoch- oder Faultantwort wird gelockert.

Die gewöhnlichen P54C-Selektor-PUSH-/POP-Befehle verwendeten bereits die geforderte volle Operandenbreite. Die neuen Fälle prüfen ihre 16-/32-Bit-Zugriffe, obere Null-/verwerfbare Daten, Stack-Bit und Adresspräfixe. **Die historische Zwei-Byte-Präzision gewöhnlicher 486-Selektorbefehle wird durch dieses Paket nicht ergänzt:** der bestehende funktionale 486-Referenzkern verwendet dort weiterhin seinen früheren vollständigen Zugriff. Das bleibt eine gesonderte Genauigkeitsgrenze; die eingefrorene M3-Referenzabnahme ist keine universelle Siliziumzusage. Die neuen P54C-Rückkehrkorrekturen ändern diese 486-Pfade nicht.

## CPU- und Altbuildnachweis

**498 neue Fälle**, insgesamt **8371**. 192 Call-Gate-/RETF- und 128 Interrupt-/Trap-Gate-/IRET-Fälle kombinieren beide Modelle, 16/32-Bit-Gates, alte/neue Stackbreiten, 16/32-Bit-TSS, Paging und 0/2/31 Parameter. 176 gewöhnliche P54C-Selektorfälle laufen in Real Mode, PE16/PE32, Paging und gepagtem V86 einschließlich unveränderter Flags und POP-SS-Schatten. Zwei Fälle trennen den um die 16-Bit-Grenze gewickelten Fehlerrahmen: P54C lehnt die nicht vorhandene obere Fehlerwortseite vor sämtlichen Rahmenwrites ab; die weiterhin gültige 486-Wortantwort bleibt funktionsfähig.

Die gleichen neuen Tests laufen zusätzlich auf hashgleichen Kopien der archivierten **7873er-VM-DLLs**: **41 erwartete Fehlschläge bei 457 bestandenen Kontrollen**. 40 Fälle reproduzieren den falschen P54C-ESP-Teil, einer die unvollständige Vorprüfung. Nur Tests.dll/Tests.pdb werden im privaten Altstarterzweig ersetzt, die VM-DLLs bleiben bytegleich zum Archiv. Ein tatsächlicher eigener alter P54C-Resetgast beendet zwar seinen eigenen Ablauf, scheitert aber unabhängig an der historischen ESP-/RAM-Stackantwort. Ein alter 486-Kontrollgast besteht und stimmt mit dem neuen 486-Gast in RAM, CPU-/Geräte-/Schedulerzustand, Zeit und zukünftigen Publikationen überein. Diese Altbuildläufe zählen getrennt von den neuen positiven Starts und Oracle-Verfälschungen.

## Echter eigener Schutzmodus-Gast und Fortsetzung

**Vier endgültige Resetstarts und vier tatsächliche Komponentenfortsetzungen**, P54C und 486 jeweils zweimal. Ein identisches eigenes Programm richtet GDT/IDT und seinen privaten TSS selbst ein, wechselt durch tatsächliches IRET nach CPL3 und führt sechs vollständige äußere Gate-Rückwege aus. 16-Bit-CALL mit null/zwei Parametern, Interrupt-/Trap-Gates sowie zwei 32-Bit-Kontrollen benutzen beide Kernelstackbreiten. Der Gast schreibt und liest anschließend tatsächlich auf dem restaurierten äußeren Stack. Die Modellantwort wird dadurch zusätzlich an physischen RAM-Adressen sichtbar, nicht allein an einer Hostregisterabfrage.

Über die vier Originalseiten: **24 vollständige Gate-Rückwege** (16 schmal, acht breit), **16 physische PIT-IRQ0-Handler/EOIs** und **36 tatsächliche Selektorbefehle**. Die 486-RAM-Stacks und die P54C-RAM-Stacks unterscheiden sich absichtlich gemäß dem benannten Rückkehrvertrag. Rohe 80-Bit-Pi-Daten, kopierte Parameter, Originalprogramm, Segment-/Framewerte, Flags und Wachen werden unabhängig geprüft. Der eigene private TR bleibt bis Reset belegt; keine vollständige DOS-/Systembereinigung wird behauptet.

Der Schnitt liegt **innerhalb des ersten aktiven 16-Bit-Call-Gates** beim tatsächlichen HLT, mit noch ausstehender Rückkehr auf den äußeren Stack, laufendem PIT, zukünftigem Ereignis und nichtnulligem Taktrest. **2004 weitere vollständige ausgewählte CPU-/PIC-/PIT-/Scheduler-/ganze 2-MiB-RAM-Vergleiche** und alle zukünftigen CPU-/Port-/Timer-/IRQ-Publikationen sind Original/Restaurierung gleich. Zwei Wiederholungspaare sind zeit-/RAM-/Zustands-/Publikationshashgleich. Das unabhängige Oracle rekonstruiert **17408 Ledger-Einträge**, die tatsächlichen Gate-/IRQ-Frames, Rückkehradressen, Parameter und Modell-Stackwirkungen sowie die PIT-Fristen ganzzahlig. **696 Verfälschungen scheitern.** Das ist ein ausgewählter Komponentengraph; kein P54C-DOS-Start oder vollständiger Pentium-PC mit Medien.

Getrennt bestehen **acht aktuelle 486-DOS-Kaltstarts und acht volle PC-Fortsetzungen** unter MS-DOS/FreeDOS/originalem EMM386 NOEMS/RAM je zweimal, **1282 weitere vollständige CPU-/8-MiB-RAM-/Geräte-/Schedulervergleiche**, echte PCM-DMA/x87-DOS-Dateien und **200 verworfene Oracles**. Gesamtes endgültiges Paket: vier eigene Resetstarts/vier ausgewählte Fortsetzungen plus acht DOS-Kaltstarts/acht volle Fortsetzungen, **896 negative Gastoracles**. Der diagnostische P54C-Gast, anfängliche Fixture-/Oracle-Korrekturen und die gezielten Altbuildläufe sind getrennt ausgeschlossen. Ältere MSR-/TSC-/Audio-/Doom-Matrizen werden ohne neuen Befund nicht pauschal wiederholt.

## Zustand, Build und nächster Schritt

**38 Typen, 751 Instanzmitglieder, 32 unveränderte Ereigniskörper**. Keine neuen Zustandsfelder, Timer oder Hostendpunkte. Der volle 7873er-Starter und seine 748 Quellen sind archiviert; ein wirklicher alter CLI-Snapshotimport scheitert vor Zweiganlage, die Sicherung bleibt unverändert. Format 1 bleibt buildgebunden. Sieben endgültige getestete DLLs bleiben bytegleich; Kandidat und normaler Starter mit allen 64 Dateien identisch. **642 lizenz-/kommentargeprüfte Code-/Projektdateien, 132 Python-Werkzeuge, 90 PowerShell-Skripte**. Alle 115 geschützten Eingaben, C64 und HEAD unverändert; persönliche Spiel-COWs ungeöffnet. Keine Windows-Eingaben, neue Aufnahme, Commits/Pushes oder neue allgemeine Performance-/Spiel-/Sound-/Siliziumabnahme.

Reproduktion:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 Selector"
.\scripts\test-selector-stack.ps1 -SkipBuild
```

Privater Gesamtnachweis **`artifacts/tests/m4-selector-stack-release-path.txt`**. Nächster konkreter M4-Schritt: historischen CR4.DE-/DR4-/DR5-/I/O-Breakpoint-Vertrag des P54C abgrenzen und mit Rechte-/Fault-/Gast-/Fortsetzungsfällen umsetzen; die bisherigen 486-Debugantworten erhalten. Danach weitere CR4-/MSR-/Systemvoraussetzungen, tatsächliche DOS-/volle P54C-PC-Integration vor Profilfreigabe, 430FX/PIIX, S3 Trio64, ATAPI, Firmware/Manager und wirkliche Windows-95-RTM-Abnahme. M4–M8 bleiben offen.
