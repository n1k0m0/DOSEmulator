# Doom II selbst ausprobieren

Im Projektordner `run-doom2.cmd` doppelklicken oder in PowerShell ausführen:

```powershell
.\run-doom2.cmd
```

Der Starter verwendet eine Kopie des bereits geprüften privaten MS-DOS-/Doom-II-Images mit HIMEM und 8 MiB RAM. Er legt beim ersten Start eine eigene Sitzung unter `artifacts/doom2-play` an. Bei weiteren Starts bleiben Einstellungen und Spielstände im selben privaten Overlay erhalten. Die ursprünglichen DOS-/Doom-Dateien und Abnahmemedien werden nicht verändert. Die privaten Medien müssen auf diesem Rechner bereits vorhanden sein; sie gehören nicht ins Repository.

Nach dem DOS-Start bei `C:\>` eingeben:

```dos
doom2 -config doomtest.cfg -nosound
```

Das startet das originale Spiel ohne den automatischen kurzen Timedemo-Endpfad. Mit **Esc** das Spielmenü öffnen, dann **New Game**, eine Schwierigkeit wählen und **Enter** drücken. Die Testkonfiguration lässt Ton, Maus und Joystick ausgeschaltet. Die Tastatur verwendet zunächst die Standardbelegung des Spiels: Pfeiltasten zum Bewegen/Drehen, Strg zum Schießen, Leertaste für Türen/Schalter, Shift zum Rennen. Vollbild im Emulator: **Strg+Alt+F11**, Emulatormenü: **Strg+Alt+F10**.

Das ist ein **experimenteller manueller Spieltest**. Automatisch nachgewiesen sind bisher der Originalstart und eine kurze stumme Demo ohne Monster. Längere Spielverläufe, Kämpfe, Menüs, echte Eingaben und Speichern/Laden sind noch keine bestandene Spielabnahme. Unvollständige CPU-/Gerätepfade können einen Fehler auslösen; die Spielgeschwindigkeit ist nicht zugesagt. Bei einem Fehler die genaue Meldung beziehungsweise die letzte sichtbare Spielstelle festhalten.

Der Starter öffnet selbst keine Tests und sendet keine automatischen Tastatur-/Mauseingaben. Zum Prüfen der Vorbereitung ohne Fenster dient `run-doom2.cmd -PrepareOnly`.
