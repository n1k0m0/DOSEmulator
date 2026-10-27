# M4-Beginn: lokaler PCI-Konfigurationsmechanismus 1

**Fortschritt vom 27. Oktober 2026:** Der [82437FX-A2-Hostcontroller](430FX-HOST.md) ist im eigenen 100-MHz-/32-MiB-Validierungsprofil mit tatsächlichen PAM-/DRAM-/Configwirkungen und voller Snapshotfortsetzung integriert (9125 Regressionen). Nur dort besitzt der Bus den festen Host `00:00.0`; reale unbeanspruchte Konfigurationszugriffe setzen dessen RMAS. Die folgenden 7121er-Zahlen und Aussagen zur damals fehlenden Verdrahtung bleiben historisch. PIIX und vollständige PCI-/Windows-Plattform bleiben offen.

Stand: 26. Oktober 2026. Nach der [M3-Referenzabnahme auf dem 7030er-Build](../M3-ACCEPTANCE.md) ist der erste **interne M4-Baustein** implementiert. Sein damaliger gemeinsamer Build besteht **7121/7121 Regressionen**, einschließlich **91 neuer PCI-Fälle**, ohne Warnungen/Fehler. Eine auswählbare Pentium-/430FX-/Windows-95-Maschine entsteht erst mit den folgenden Plattformschritten.

## Implementierter Transport

`PciConfigurationBus` und `IPciConfigurationFunction` verbinden ausdrücklich registrierte lokale Funktionen mit dem Konfigurationsfenster. Grundlage sind CONFADD/CONFDATA und ihre Adressfelder im [Intel-82430FX-/82437FX-Datenblatt, 290518-002, Dezember 1996, Seite 15](https://bitsavers.trailing-edge.com/components/intel/pci/290518-002_82430FX_82437FX_82438FX_PCIset_DATASHEET_199612.pdf).

| Zugriff | Begrenzter DOSE-Vertrag |
| --- | --- |
| CF8h, Dword | Konfigurationsadresslatch; Bit 31 aktiviert das Datenfenster. Bus 23..16, Gerät 15..11, Funktion 10..8 und Register 7..2. Reservierte Bits lesen im DOSE-Transport null; keine vermessene Siliziumantwort. |
| CFCh–CFFh | Ein Byte-, Wort- oder Doppelwortzugriff innerhalb des vollständigen Vierbytefensters wird einmal an die gewählte Funktion weitergereicht. Byte-Lanes und 256-Byte-Registergrenzen bleiben erhalten. |
| Deaktiviert/fehlende Funktion | Lesen liefert nur Einsen in der angeforderten Breite, Schreiben erreicht keine Funktion. Keine erfundenen Geräte-IDs. |
| Nicht unterstützte Breite/Fensterüberschreitung | Keine versteckten Teilzugriffe, kein Überlauf in das nächste Register oder Gerät. Ungültige API-Breiten werden vor jeder Wirkung abgewiesen. |
| Registrierung | Bus 0..255, Gerät 0..31, Funktion 0..7; doppelte oder ungültige Adressen werden vor Übernahme abgewiesen. Bereits registrierte Funktionen bleiben nutzbar. |
| ResetAddress | Löscht ausschließlich das Adresslatch. Ein künftiger Plattformreset muss die konkreten Funktionen gesondert zurücksetzen. |

Die konkrete Funktion besitzt ihre Register, Schreibmasken, BARs und Statuswirkungen selbst. Der Transport enthält keine universelle beschreibbare PCI-Headerattrappe. Die Tests verwenden ausschließlich ausdrücklich synthetische lokale Funktionen und tatsächliche x86-IN-/OUT-Instruktionen. Dafür wird kein Host-PCI-Gerät, Treiber, Netzwerk- oder anderer Hostendpunkt geöffnet.

## Grenzen und Zustand

Dieser Baustein ist noch **nicht in die auswählbaren Maschinenprofile verdrahtet**. Die vorhandenen 486-PCs behalten offene CF8h-/CFCh-Ports. Es gibt noch keine 82437FX-/PIIX-Registerkarte, keine PCI-BAR-/Command-gesteuerten MMIO-/I/O-Bereiche, INTx-Routen, PCI-BIOS-Dienste, Busmaster-/Abortstatus oder elektrische Type-0-/Type-1-Zyklen. Schmale CF8h-Zugriffe und fensterübergreifende Transaktionen besitzen noch keinen vollständigen PCI-/ISA-Passthroughpfad.

Ein nachträglich manuell auf den 486-Portbus gesetzter Transport wird von der bestehenden vollständigen Snapshot-Erfassung als zusätzliche unregistrierte Topologie **abgewiesen**. Der Test prüft unveränderten CPU-Zustand und tatsächliche weitere Ausführung. Der zusätzliche M4-Bus wird somit nicht stillschweigend aus einer als vollständig bezeichneten Sicherung weggelassen. Geräte-/Funktionzustände und Ressourcen werden bei der anschließenden Plattformintegration ausdrücklich in das Zustandsregister aufgenommen.

Format 1 bleibt an den identischen VM-Build gebunden. Die neue Hardware-DLL erzeugt eine neue Buildidentität. Ein wirklicher CLI-Ladeversuch eines eigenen 7030er-Snapshots scheitert vor Erzeugung eines Zweiges; die Quelle bleibt unverändert. Der 7030er-Starter und seine 725 Quellidentitäten sind getrennt archiviert. Neue acht DOS-/EMM386-Kaltstarts und acht tatsächliche Fortsetzungen auf dem 7121er-Build erhalten die bestehenden CPU-/rohen-FPU-/8-MiB-RAM-/DMA-/Geräte-/Schedulerverträge.

## Nachweis und Fortsetzung

Die 91 Fälle prüfen BDF-Trennung über vier Busnummern, vier Gerätenummern und drei Funktionen, alle Byte-Lanes/Breiten bis zum Register FFh, fehlende/deaktivierte Ziele, unerlaubte Zugriffe ohne Teilwirkungen, Registrierung/Rollback sowie echte CPU-IN-/OUT-Übertragungen mit 16-/32-Bit-Operanden. Dies ist ein Transport-/CPU-Testgast; er ersetzt keinen DOS-/Windows-PCI-Treiber- oder Boardnachweis.

Reproduktion der begrenzten neuen Gruppe nach dem normalen Build:

```powershell
dotnet artifacts/m1-build/Release/net10.0/DOSEmulator.Tests.dll --filter "M4 PCI"
```

Privater Gesamtnachweis: `artifacts/tests/m4-pci-foundation-release-path.txt`. Alle 7121 Regressionen, acht aktuelle DOS-Snapshotfortsetzungen, Ablehnungen, Modul-/Quell-/Release-/Medienhashes und die getrennte M3-Archividentität sind darin verzeichnet. Keine Windows-Eingaben, Hostverbindungen, Videoänderungen, Commits oder Pushes.

**Nächste M4-Vertikale:** CPU-Modellgrenzen für den konkreten P54C festlegen und implementieren; 486-Verhalten unverändert erhalten. Danach PCI-Funktionen/430FX-/PIIX-Register, explizite Ressourcen-/Zustandsintegration, S3 Trio64, ATAPI und Firmware-/Managerpfade ergänzen. Windows 95 RTM benötigt anschließend den eigenen tatsächlichen Installations-, Neustart-, Geräte- und Shutdownnachweis. M4 bleibt insgesamt offen.


Inzwischen ergänzt der [P54C-B5-Befehlsbaustein](P54C-B5.md) eine ausdrücklich interne CPU-Teilmenge mit CPUID/CMPXCHG8B und eigenem Resetgast. Aktuell sind 7305 Regressionen bestanden. Der PCI-Transport bleibt unverändert unverdrahtet; dessen 7121er-Gast-/Modulnachweise bleiben historisch. Der nächste CPU-Schritt ist TSC/RDTSC/CR4.TSD, danach die weiteren System-/Plattformverträge.
