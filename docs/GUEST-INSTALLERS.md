# DOSE Tools und MiniGlide im Windows-Gast installieren

Es gibt zwei eigenständige, englischsprachige Win32-Installer. Sie laufen direkt unter Windows 95 beziehungsweise Windows 98. Ein zusätzlicher Gast-Runtime oder ein Host-Installationsskript wird dafür nicht benötigt.

Die folgenden Paketdateien liegen lokal nach dem [Neubau](#neubau) unter `artifacts/guest-installers/`. Generierte EXEs, CD-Images und private Prüfnachweise werden nicht im Git-Repository veröffentlicht.

| Gast | CD-Image | Eigenständiger Installer |
| --- | --- | --- |
| Windows 95 | [DOSE-Tools-Win95.iso](../artifacts/guest-installers/DOSE-Tools-Win95.iso) | [Setup-DOSE-Win95.exe](../artifacts/guest-installers/Setup-DOSE-Win95.exe) |
| Windows 98 / 98 SE | [DOSE-Tools-Win98.iso](../artifacts/guest-installers/DOSE-Tools-Win98.iso) | [Setup-DOSE-Win98.exe](../artifacts/guest-installers/Setup-DOSE-Win98.exe) |

## Installation

Die passende ISO im emulierten CD-Laufwerk einlegen. Im Windows-Gast unter **Arbeitsplatz / My Computer** die CD öffnen und **SETUP.EXE** starten. Alternativ die passende eigenständige EXE in den Gast kopieren und dort ausführen. Der Installer prüft die tatsächliche Windows-Version und weist die jeweils andere Version zurück.

Die gewünschten Komponenten auswählen und **Install** anklicken:

| Auswahl | Wirkung im Gast |
| --- | --- |
| DOSE Tools | Installiert `C:\DOSETOOLS\DOSETIME.EXE`, startet die Zeitsynchronisierung und legt `C:\Share` bei Bedarf an. Bestehende Dateien im Share bleiben erhalten. Der derzeit vorhandene Helfer bietet Zeitsynchronisierung; Dateitransfer und Zwischenablage sind noch nicht enthalten. |
| Start DOSE Tools with Windows | Registriert den Zeithelfer für den automatischen Windows-Start. Lässt sich für die Installation abwählen. |
| MiniGlide | Installiert `GLIDE2X.DLL` in das tatsächliche Windows-Systemverzeichnis und `C:\DOSETOOLS\MGLIDE.EXE` für die Logo-Vorschau. Wenn Unreal unter `C:\UNREAL` vorhanden ist, entsteht auch eine Spielverknüpfung. |
| APM installation files | Legt die zur Windows-Version passenden eigenen INF- und Anleitungstexte in `C:\DOSETOOLS\APM` ab. Die eigentliche Geräteinstallation erfolgt manuell nach Anleitung mit den originalen Windows-Dateien von der Windows-CD. Ein bereits funktionierendes APM-Gerät benötigt keine weitere Installation. |

Unter **Start → Programme / Programs → DOSE Tools** stehen Hilfe, Zeithelfer, Share-Ordner, MiniGlide-Vorschau und Deinstallation bereit. Der Installer bleibt als `C:\DOSETOOLS\DSETUP.EXE` verfügbar, um Komponenten später hinzuzufügen oder zu entfernen. Bereits installierte, bei einem weiteren Lauf abgewählte Komponenten bleiben installiert.

MiniGlide benötigt den [konfigurierten Host-Glide-Pfad](GLIDE-HOST-BRIDGE.md). Der Gastinstaller verändert keine Host-Konfiguration. Die CD enthält eigene Gastprogramme und Lizenztexte; Windows-, Spiel- und Host-Renderer-Dateien werden nicht mitgeliefert.

## Aktualisierung und Entfernen

Vor dem Austausch der Glide-DLL laufende Glide-Spiele schließen. Der Installer beendet den vorhandenen eigenen Zeithelfer vor dessen Austausch. Schreibgeschützte Dateien werden zuerst gesichert und für den Austausch beschreibbar gemacht.

Die jeweils erste vorherige Datei liegt unter `C:\DOSETOOLS\BACKUP`. Erneute Installationen überschreiben diese Sicherung nicht. Auch die bisherigen Dateiattribute und der vollständige vorherige Autostartwert einschließlich Registry-Typ werden gesichert.

**Remove…** im Installer, **Uninstall** im Startmenü oder der Eintrag unter **Software / Add/Remove Programs** stellt die vorherigen Dateien, Attribute und den alten Autostartwert wieder her. Neu hinzugefügte Komponentendateien und Verknüpfungen werden entfernt. `C:\Share`, Sicherungen, der Wartungsinstaller und seine Wiederherstellungsdaten bleiben erhalten. Die beim Entfernen angetroffenen Dateien werden zusätzlich als `*.LAST` gesichert. Es wird kein Verzeichnis rekursiv gelöscht.

## Neubau

[`Build-Guest-Installers.cmd`](../Build-Guest-Installers.cmd) baut beide EXEs und beide ISOs nach `artifacts/guest-installers/`. Voraussetzungen auf dem Host: Python 3 mit `pycdlib`, MSVC mit x86-Compiler und Windows SDK sowie der vorhandene NASM unter `artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe`. Die Gastprogramme verwenden IA32 ohne SSE und die PE-Mindestversion 4.0. Es gibt keine zusätzliche Installer-Engine oder Gast-CRT-Abhängigkeit.

Quellen: [Installer](../guest-tools/installer/installer.c), [Oberfläche](../guest-tools/installer/installer.rc), [Paketbau](../scripts/build-guest-installers.py), [gemeinsamer Zeithelfer mit Versionsauswahl](../guest-tools/windows95/dose-time.asm). Der standardmäßige Zeithelferbau bleibt Windows 95; `--windows-version 98` erzeugt die passende Windows-98-Variante.

## Gastprüfung

[`test-guest-installers.py`](../scripts/test-guest-installers.py) erstellt für `--windows-version 95` beziehungsweise `98` jeweils eine eigene beschreibbare Maschinenkopie. Die Komponentendateien werden ausschließlich durch den tatsächlich im Gast ausgeführten Installer installiert. Ein eigenes Prüfprogramm vergleicht die installierten Dateien über Windows-Datei-APIs mit den erwarteten Bytes, prüft Version, Autostart und laufenden Zeithelfer sowie eine zu erhaltende Share-Datei. Nach regulärem Shutdown folgen Kaltstart, erneute Prüfung und Deinstallation. Die protokollierten Ergebnisse werden anschließend unabhängig aus FAT16 beziehungsweise FAT32 gelesen. `--upgrade-from` erlaubt zusätzlich eine Aktualisierung einer bereits geprüften privaten Testkopie mit absichtlich schreibgeschütztem Wartungsinstaller.

Beide vorhandenen Windows-Systeme bestehen Installation, regulären Shutdown, Kaltstart mit Autostart und Deinstallation. Die fertigen Paketdateien bestehen außerdem Aktualisierung, die Auswahl nur des Zeithelfers ohne MiniGlide und ohne Autostart, anschließende vollständige Installation sowie erneute Wiederherstellung. Die Version des jeweils anderen Windows wird mit Exitcode 2 abgewiesen. Ein unabhängiger PE-Abgleich bestätigt x86, Mindestversion 4.0 und ausschließlich klassische Windows-Importe; die eigenen Installerobjekte enthalten keine SSE-Registerzugriffe.

- [Oberfläche unter Windows 95](../artifacts/guest-installers/Setup-Windows95.png), [Installation unter Windows 98](../artifacts/guest-installers/Setup-Windows98.png)
- [Zusammengefasster Prüfnachweis mit den Hashes der fertigen EXEs](../artifacts/guest-installers/validation.json)
- Windows 95: [Installation/Kaltstart/Entfernen](../artifacts/guest-installers/test-win95-5ca18382/guest-reboot-remove.ini), [fertiger Installer mit Aktualisierung und Komponentenauswahl](../artifacts/guest-installers/test-win95-73351a9f/guest-upgrade.ini)
- Windows 98: [Installation/Kaltstart/Entfernen](../artifacts/guest-installers/test-win98-47d4de28/guest-reboot-remove.ini), [fertiger Installer mit Aktualisierung und Komponentenauswahl](../artifacts/guest-installers/test-win98-606042b7/guest-upgrade.ini)
