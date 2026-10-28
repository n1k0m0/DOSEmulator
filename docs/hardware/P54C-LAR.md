# P54C: deterministische LAR-Kompatibilitätspolitik

Stand: 28. Oktober 2026. Die enge LAR-Kompatibilitätspolitik ist auf dem eingefrorenen V9-Kandidaten mit **12401/12401 Regressionen** geprüft. Der ursprüngliche Windows-Kernelpfad öffnet nun die Grafikkartenauswahl statt des bisherigen RUNDLL32-GP. Dieser Vertrag beschreibt die deterministische Antwort des DOSE-Modells `IntelP54cB5Subset`; er behauptet weder ein Intel-B5-Erratum noch eine vermessene Antwort echter Pentium-Hardware.

## Historischer Intel-Vertrag

Maßgeblich ist Intels [Pentium Processor Family Developer's Manual, Volume 3, 241430-004, Juli 1995](https://ftpmirror.your.org/pub/misc/bitsavers/pdf/intel/pentium/241430-004_Pentium_Processor_Family_Developers_Manual_Volume_3_Jul95.pdf), LAR auf den gedruckten Seiten 25-199/200, PDF-Seiten 816/817. Die bereits lokal archivierte Primärkopie wurde hierfür direkt gelesen.

Intel beschreibt das Ergebnis aus dem oberen Descriptor-Doppelwort mit folgenden Masken:

| Zieloperand | Historische Schreibweise |
| --- | --- |
| 16 Bit | FF00h |
| 32 Bit | 00FxFF00h |

Das `x` bezeichnet die Bits 19:16, die den oberen vier Limitbits des Descriptors entsprechen. Intel erklärt ihre LAR-Ergebniswerte ausdrücklich für **undefiniert**. Die 16-Bit-Form übernimmt nur die unteren 16 Ergebnisbits. Sichtbarkeit, zulässiger Descriptortyp und ZF entscheiden weiterhin, ob ein Ergebnis geladen wird.

Der spätere offizielle [Intel-SDM-Gegencheck, 253666-082US, Dezember 2023](https://cdrdv2-public.intel.com/812383/253666-sdm-vol-2a.pdf#page=692), LAR 3-582, bestätigt dieselbe Undefiniertheit. Er ersetzt nicht die historische Grundlage des benannten P54C-Teilmodells.

Eine Nullantwort in Bits 19:16 ist daher kein nachgewiesener Architekturverstoß. Aus dem Handbuch folgt ebenso wenig eine garantierte Antwort aller B5-Prozessoren.

## Gewählte DOSE-Politik

Bei einem erfolgreichen **32-Bit-LAR im P54C-Teilmodell** übernimmt DOSE zusätzlich zum bisherigen Rechteergebnis:

```text
DescriptorHigh & 000F0000h
```

Damit liefert das Teilmodell deterministisch den tatsächlichen oberen rohen Limitnibble zurück. Dies ist eine ausdrücklich gewählte Kompatibilitätspolitik innerhalb des von Intel undefinierten Ergebnisfelds. Sie gilt für dieses CPU-Modell, nicht ausschließlich für die beiden Windows-Profile.

Die [Implementierung](../../src/DOSEmulator.Cpu.X86/X86Cpu.DescriptorQueries.cs) verändert nur diese vier Ergebnisbits. Das bestehende DOSE-486-Modell behält dort seine Nullantwort. 16-Bit-LAR, LSL, Selektor-/Rechteprüfungen, ZF-Fehlerantworten und die bestehenden Speicherfaultpfade bleiben unverändert. LAR schreibt selbst keinen Descriptor um und repariert keine zuvor beschädigten Gasttabellen. CPUID-Merkmale oder zusätzliche CPU-Funktionen werden nicht freigegeben.

## Tatsächlicher Windows-Anlass

Im ursprünglichen KRNL386-Pfad wird ein LAR-Rechtewert um acht Bit verschoben, anschließend AVL gesetzt und der Wert über den DPMI-0009-Rückschreibpfad in den Descriptor übernommen. Die beobachteten Selektoren 0CA7h und 0CA6h bezeichnen dabei denselben LDT-Slot.

Der ursprüngliche Descriptor besitzt das rohe Limit 147DFh. Mit der bisherigen DOSE-Nullantwort ergibt die konkrete Kette:

| Schritt | Beobachteter bisheriger Pfad | Sollantwort der neuen Politik |
| --- | ---: | ---: |
| LAR-Ergebnis | 0000F300h | 0001F300h |
| Nach Verschiebung und Setzen von AVL | 10F3h | 11F3h |
| Limit nach derselben Rückschreibung | 47DFh | 147DFh |

Die Nullantwort löschte in dieser tatsächlichen Gastoperation den oberen Limitnibble. Eine spätere Segmentgrenzverletzung war dann die korrekte CPU-Antwort auf den verkürzten Descriptor. Die Korrektur lockert diese Grenze nicht; sie erhält den für diesen ursprünglichen Rechte-Rückschreibpfad benötigten Nibble bereits im LAR-Ergebnis.

Der private Vorherbeleg steht unter [target-gp.json](../../artifacts/m4-win95-7d00832aeeeb4b0fb2e179e89257ffde/gp-display-trace-v3/run-1/target-gp.json), `authorizedTargetedLdtWatch.lastChanges[6]`. Die gezielte Beobachtung enthält sieben Änderungen ohne Lücke und bindet die tatsächliche Rückschreibung an den Kernelpfad. Originalmodule und Gast-RAM bleiben privat; die öffentliche Dokumentation enthält keine kopierten OS-Codefenster.

## Prüfgrenze

[Windows95LarCompatibilityTests](../../tests/DOSEmulator.Tests/Windows95LarCompatibilityTests.cs) enthält 117 Fälle für alle 16 Limitnibbles, Byte-/Pagegranularität, 16-/32-Bit-Code mit Operandenpräfix, den tatsächlichen LAR-/AVL-Rückschreibablauf und anschließende segmentbegrenzte Kopien über 64 KiB. Weitere Kontrollen erhalten das 486-Ergebnis, 16-Bit-Registerhälften, LSL, Rechte-/ZF-/Pagefault-/MMIO-Wirkungen und den weiterhin korrekten GP bei einem wirklich auf 47DFh begrenzten ES. Auf den eingefrorenen alten V8-Modulen bestehen 40 dieser 117 Fälle, 77 scheitern. V9 besteht alle 117 sowie den kombinierten gezielten Lauf mit insgesamt 501 Fällen und die vollständigen 12401 Regressionen.

Der tatsächliche V9-Gast (Runner 20) mit physisch vorhandenem 1,44-MB-Laufwerk A und der eigenen schreibgeschützten Testdiskette `windows95-own-probe-v2.img` erreicht die vollständige Systemkonfiguration durch das ursprüngliche Setup, dessen normalen Neustart und anschließend den Desktop. Auf demselben ursprünglichen Grafikkarten-Ausführungspfad öffnet sich nun der Auswahldialog. Dies belegt den konkreten Fortschritt nach der Korrektur; native S3-/Sound-Funktion, wiederholter stabiler Betrieb und regulärer Shutdown sind damit noch nicht abgenommen. M4 bleibt offen.

Die neue CPU-Assembly ändert die strenge [Format-1-Buildidentität](../SNAPSHOTS.md), ohne alte Container still zu migrieren. Die V8-Vorherkontrolle, die V9-Regressionen und der tatsächliche V9-Windows-Gast bleiben jeweils an ihre benannten Builds gebunden.
