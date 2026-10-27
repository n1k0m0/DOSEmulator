# Umgesetzter Benutzerauftrag: Maschinen, Festplatten und Codeprüfung

Stand: 26. Oktober 2026. Alle fünf folgenden Arbeitspakete sind im dokumentierten Referenzumfang umgesetzt und geprüft. Der nächste funktionale M3-Schritt ist wieder VBE für ET4000.

1. Neue leere RAW-Festplatten mit ausdrücklich angezeigten MiB/GiB erzeugen; vorhandene Dateien niemals überschreiben. Vier IDE-Plätze auf zwei Kanälen, eigene private Overlays und tatsächliche BIOS-/DOS-Prüfung ergänzen.
2. Vollständige Maschinenkonfigurationen mit Namen, Hardware und Festplatten-/Diskettenzuordnung speichern/laden, einschließlich relativer Medienpfade. Laden und Topologiewechsel bauen die Maschine kontrolliert neu auf; kein vorgetäuschter RAM-/Gerätesnapshot.
3. Alle Projektquellen systematisch auf Bugs und Performanceprobleme prüfen. Messbare Hotspots mit passenden Fehler-/Regressionsnachweisen verbessern; bestehende Präzisions- und Haltbarkeitsverträge erhalten.
4. Lizenzheader in eigenen Quellen und hilfreiche Funktionskommentare vereinheitlichen. Fremde Herkunft/Lizenzen erhalten, generierte Quellen am Generator behandeln. Dokumentationsprüfungen ergänzen.
5. Final bauen, aussagekräftige CPU-/Geräte-/Datei-/Gasttests und geeignete interne GUI-Prüfungen durchführen; Umfang, Messwerte und offene Grenzen im Implementierungsstatus festhalten.

Arbeitsnachweis: privater Zeiger `artifacts/tests/storage-review-work-path.txt`. Basis sind 4934 bestandene Regressionen und die dokumentierten ET4000-/Audio-Teilabnahmen. Alle vorhandenen Benutzeränderungen, Originalmedien, 53 privaten Ausgangsmedien und der C64Emulator bleiben erhalten. Benutzer-Spiel-COWs unter `artifacts/doom2-play` nicht öffnen oder hashen. Keine Commits/Pushes. Die verschobene native Mauswiederholung bleibt offen; keine Fokus-/Windows-Eingabeversuche.

Endstand: 4977 Regressionen, 100 DOS-Kaltstarts und 1382 verworfene Gastoracles; Code-/Lizenz-/Funktionskommentarprüfung sowie 76 Skripte/257 Checks bestanden. Maschinenidentität bleibt erhalten; dezimale CLI-Größen werden eindeutig geprüft. Interne GUI-/Editor-/WASAPI-Ausgabe-/Mute-/Pegel-/Stop-/Freigabenachweise bestehen. Historischen WinMM-Freigabefehler nicht als im alten Backend behoben darstellen. Details und Grenzen: [MACHINES.md](MACHINES.md), [CODE-REVIEW-2026-10-26.md](CODE-REVIEW-2026-10-26.md). Privater Endnachweis: `artifacts/tests/storage-review-release-path.txt`. Die verschobene native Mauswiederholung und der gesamte M3-/M4–M8-Abnahmeumfang bleiben offen.
