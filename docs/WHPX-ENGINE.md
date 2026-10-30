# Experimenteller WHPX-Pfad

Der optionale `DOSEmulator.Whpx`-Baustein verwendet die Windows Hypervisor
Platform (WHP), auf der QEMUs WHPX-Beschleuniger beruht. Er läuft nur auf einem
Windows-x64-Host mit verfügbarer WHP-Funktion. Bestehende PC-Profile, Starter,
Datenträger und Snapshotformate wählen diesen Pfad nicht automatisch.

## Bereits eingebaut

- Ein privater, 1-MiB-großer Real-Mode-Gastspeicher wird seitenweise für einen
  WHP-Partition/VP bereitgestellt. Er öffnet keine Benutzerplatte und kein
  Hostnetzwerk.
- Gewöhnliche `IN`/`OUT`-Exits ohne String-/REP-Präfix verwenden DOSEs
  `IoPortBus` mit der tatsächlichen Portbreite. `HLT` beendet den begrenzten
  Testlauf. Unbekannte Exits und String-I/O brechen ausdrücklich ab.
- Register, RAM und von WHP gemeldete geänderte 4-KiB-Seiten lassen sich
  nach einem gestoppten Lauf lesen. Ein Host-Timeout kann einen hängenden VP
  per `WHvCancelRunVirtualProcessor` zurückholen.
- Die eigene Release-Probe führt Gast-`OUT` und `IN` bis zu einem DOSE-Port,
  prüft den tatsächlichen RAM-Wert und die Dirty-Seite und schreibt getrennt
  über den originalen DOSE-Boardport E9. Sie verwendet nur selbst erzeugte
  Instruktionen und private RAM-Puffer.

Probe: `dotnet run --project tests/DOSEmulator.Whpx.Tests -c Release`.
Ohne WHP meldet sie `SKIP`, statt den normalen Emulator zu beeinflussen.

## Grenze und nächster Integrationsschritt

Dies ist eine **getrennte Hardware-Ausführungslane**, noch kein austauschbarer
`PcMachine`-CPU-Kern. Der aktuelle WHP-Puffer ist nicht mit dem vollen DOSE-
`MemoryBus` identisch: ROM, A20, PCI-/VGA-MMIO, DMA und die Gastplatten sind
noch nicht abgebildet. PIC-IRQ, Protected Mode, x87, CPUID-/MSR-Vertrag und
vollständige CPU-/Gerätesnapshots sind ebenfalls nicht verbunden. Der
`PcMachine`-Scheduler zählt derzeit vier Clocks je emuliertem Element; WHP
liefert diesen Zähler nicht direkt. Ein normaler Windows-95/98-Start wäre mit
dieser Lane deshalb falsch und bleibt gesperrt.

Die nächste belastbare Stufe ist ein privater DOS-Test-PC mit gespiegeltem
RAM/ROM, A20 und eng emulierten MMIO-/PIC-Exits. Erst wenn dessen gesamter
Zustand nach Stop/Restart und seine Gerätetermine dem Softwarekern entsprechen,
ist ein Windows-Kaltstart sinnvoll. Für die Echtzeitfrage müssen reale
Port-/MMIO-Exit-Raten und Gastzeit/Hostzeit unter Doom und Windows-Video
gemessen werden; die schnelle reine RAM-Schleife ist dafür kein Ersatz.

API-Quellen: [WHP-Ausführung](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/hypervisor-platform),
[I/O-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/ioportaccess),
[Dirty-Bitmap](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/whvquerygparangedirtybitmap).
