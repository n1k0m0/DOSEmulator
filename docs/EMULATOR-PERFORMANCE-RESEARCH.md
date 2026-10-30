# Wege zum 1:1-Echtzeitziel

Stand: 30. Oktober 2026. Diese Notiz ordnet öffentlich dokumentierte Techniken
den gemessenen DOSE-Lasten zu. Sie ist ein Forschungs- und Messplan, keine
Behauptung, dass ein neuer Pfad bereits implementiert oder abgenommen ist.

## Ausgangspunkt

Der eingefrorene C45 erreicht mit Produktions-Pacer bei Doom II/133 MHz 46,305 %,
bei Windows-95-Videowiedergabe 33,001 % und bei Windows 98 25,088 % Gastzeit pro
Hostzeit. Je nach Last fehlen damit grob das 2,2- bis 4-Fache an Durchsatz.
Leerlauf und einzelne Windows-Arbeitslasten erreichen bereits etwa 100 %;
allgemeines 1:1 bei Boot, Video und längerer Spiel-Last bleibt offen. Siehe
[Performance](PERFORMANCE.md). Der laufende C46-Versuch ist hier nicht als
fertiger Produktstand eingerechnet.

## Priorisierte Technik

1. **Echte Lasten vollständig profilieren.** Für jeden reproduzierbaren
   Kaltstart und einen längeren stabilen Abschnitt CPU-Decode, RAM/TLB,
   VGA/GDI, Audio, Scheduler und Host-Ausgabe getrennt messen. Dazu die
   tatsächlich ausgeführten Fast-/Fallback-Elemente, Blocklängen,
   Übersetzungskosten, Cache-Treffer und Invalidierungen zählen. Unter .NET
   sind `dotnet-trace`-Samples ein Einstieg; Microsoft weist auf Safe-Point-Bias
   und fehlende native Stacks hin. Auf Windows deshalb bei strittigen Hotspots
   zusätzlich ETW/Visual-Studio-CPU-Profiling verwenden. Erst nach dieser
   Aufteilung wird der nächste Engpass gewählt.

2. **Heiße Blöcke länger im schnellen Pfad halten.** QEMUs TCG versieht
   übersetzte Blöcke mit den gültigen CPU-Kontextannahmen und verkettet
   passende Folgeblöcke direkt. Das spart Rückkehr zum zentralen Dispatch.
   Für DOSE wäre dies ein begrenzter, gestufter Versuch: kalte/seltene Pfade
   bleiben beim Referenzkern; nur oft ausgeführte, stabile Blöcke werden
   spezialisiert. Der bereits gemessene CLR-JIT-Prototyp war trotz Gewinn im
   eigenen Mikro-Loop unter Win95/98 langsamer und deckte dort nur 0,0483 %
   beziehungsweise 9,5876 % der Elemente ab. Erst Zulassungsquote und
   Lebensdauer verbessern, dann einen nativen Translator oder eine weitere
   JIT-Stufe bewerten. QEMU dokumentiert zugleich die nötige Auflösung von
   Verkettungen bei Codeänderung, Paging und Interrupt-Freigabe.

3. **RAM-Zugriffe von Gerätezugriffen trennen.** QEMUs Software-MMU hält
   pro vCPU einen TLB-Fast-Pfad für gewöhnliches RAM; MMIO, Rechte-/Dirty-
   Tracking und Codeänderungen gehen in den langsamen Pfad. Für DOSE ist ein
   entsprechender direkter Einseiten-RAM-Pfad mit strikter Seiten-, Rechte-,
   Alias- und Snapshot-Prüfung aussichtsreich. C46 untersucht bereits eine
   enge Variante. Die Abnahme muss dieselbe Ausnahmepriorität und denselben
   gesamten Maschinenzustand wie der Referenzpfad erhalten.

4. **Hardware-Virtualisierung gesondert prüfen.** Windows Hypervisor Platform
   kann reale x86-Gastbefehle ausführen und bei Speicher-/I/O-Ereignissen an
   den VMM zurückgeben. DOSE hat dafür nur einen begrenzten CPU-Machbarkeitstest.
   Ein echter WHPX-Modus müsste Geräte-Exits, Interrupts, historischen
   486/Pentium-CPU-Vertrag, die vier-Clock-Zeitrechnung und vollständige
   Snapshots neu verbinden. Er ist daher ein separates Architekturprojekt,
   keine unmittelbare Abkürzung für den laufenden Interpreter. Auch CPUID
   benötigt bei Bedarf einen expliziten Exit und ein eigenes Gast-Ergebnis.

## Mess- und Abnahmeregel

Jeden Kandidaten mit unverändertem Gasttakt und derselben Arbeit auf frischen
privaten Medienkopien gegen den eingefrorenen Vorgänger vergleichen: serielle
ABBA/BAAB-Läufe, Warmup, ganze CPU/RAM/VRAM/Geräte/Scheduler-Grenzen,
spätere Bild-/PCM-Daten und reguläres Gastende. Nicht nur ein frühes
Zehn-Sekunden-Fenster messen, sondern auch Boot, längere Doom-Szene und
Windows-Video. `100 %` bedeutet für eine benannte Last auf einem benannten Host
Gastzeit mindestens gleich Hostzeit; eine Garantie für jede Last und jeden
Host folgt daraus nicht. QEMU unterscheidet ausdrücklich Instruktionszählung
von zyklusgenauer Emulation; ein künstlich angepasster Gasttakt wäre kein
Durchsatzgewinn.

## Primärquellen

- [QEMU: Translator Internals](https://www.qemu.org/docs/master/devel/tcg.html) — Kontextschlüssel, Blockverkettung, Code-Invalidierung, Software-MMU und Exception-Rückführung.
- [QEMU: Multi-threaded TCG](https://www.qemu.org/docs/master/devel/multi-thread-tcg.html) — TLB-Fast-Pfad und langsame RAM-/MMIO-Fälle.
- [QEMU: TCG Instruction Counting](https://www.qemu.org/docs/master/devel/tcg-icount.html) — virtuelle Zeit und Grenze zur Zyklusgenauigkeit.
- [DOSBox Staging: CPU](https://www.dosbox-staging.org/0.83/manual/system/cpu/) — dynamischer Kern für rechenintensive DOS-Programme.
- [Microsoft: Windows Hypervisor Platform](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/hypervisor-platform) und [CPUID-Exit](https://learn.microsoft.com/en-us/virtualization/api/hypervisor-platform/funcs/cpuidaccess) — Ausführung und VMM-Exits.
- [Microsoft: .NET CPU-Profiling](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-highcpu) — Grenzen von `dotnet-trace` und Profiling-Alternativen.
