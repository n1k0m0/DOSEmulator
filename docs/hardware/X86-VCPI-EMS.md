# VCPI-Protected-Client und echte EMS-Bankwechsel

Stand: 25. Oktober 2026. M3-Teilpaket nach [Paging/V86](X86-PAGING-V86.md). **M3 insgesamt bleibt offen.** Release-Build beim Abschluss dieses Teilpakets: **880/880 Regressionen, 0 Warnungen/Fehler**. Der nachfolgende [DOS4GW-/Doom-II-Referenzpfad](X86-DOS4GW.md) besitzt inzwischen 920 Regressionen. Die bisherigen 876 Fälle bleiben erhalten; vier zusätzliche Fälle prüfen das ISA-Speicherloch bei 4/8/16 MiB und die CPU-Seitenübersetzung eines EMS-Fensters auf Extended RAM.

## Korrigierter physischer Adressraum

Der Originaltreiber EMM386 4.48 konnte mit `RAM` zunächst keinen EMS-Seitenrahmen finden und wartete mit einer deutschen Warnung auf eine Taste. Ursache war die Plattform: Die installierte RAM-Bytefolge wurde auch zwischen C0000h und EFFFFh als beschreibbarer physischer Speicher dekodiert. Diesen Bereich untersuchte EMM386 entsprechend als belegtes Adapter-RAM.

Das Referenzboard dekodiert in diesem unbestückten ISA-Option-Speicherfenster jetzt keinen DRAM. `OpenBusMemory` liefert FFh und verwirft Schreibzugriffe; die Diagnose kann den Bereich ohne Nebenwirkung ansehen. VGA A0000h–BFFFFh und System-ROM ab F0000h bleiben eigene Geräte. Dies ist eine festgelegte Referenzboard-Regel, keine Vermessung elektrischer Buszustände oder beliebiger historischer Chipsätze. Option-ROMs, Kartenspeicher und Shadow-RAM benötigen später eigene Mappings.

**EMM386 richtet den EMS-Seitenrahmen danach mit einfachem `RAM` selbst ein.** Es gibt weder erzwungene `I=`-/`FRAME=`-Ausnahmen noch eine Host-EMS-Implementierung. Seine echten PDE/PTE-Änderungen bilden Extended RAM auf das virtuelle Fenster ab. Ein direkter physischer DMA-Zugriff auf das unbestückte Fenster erhält dadurch weiterhin kein RAM.

Die andere Speicherbelegung verändert auch die residente EMM386-/EBDA-Anordnung. Die alten NOEMS-Bild-/Zeit-/Adresswerte sind deshalb historische Nachweise. Der neue NOEMS-Lauf prüft erneut die dynamische EBDA; beim 8-MiB-Profil liegt sie jetzt bei physisch 0000D3B0h. Das BIOS folgt weiterhin der BDA-Adresse und keiner angenommenen festen Lage. Die unveränderte FreeDOS-IDE-Abnahme bleibt dagegen zeit-, bild- und sektoridentisch.

## VCPI-Protected-Client

Der eigene `VCPIPM.COM` benutzt ausschließlich die residenten Originaldienste:

1. Zwei 4-KiB-Seiten in V86 reservieren, freie Menge erfassen und die physischen Adressen der konventionellen Tabellen über DE06h ermitteln.
2. Mit DE01h die tatsächlichen gemeinsamen Seitentabelleneinträge, drei Serverdeskriptoren und den Far-Einsprung beziehen. Eigene GDT, IDT, verfügbare TSS, CR3 und einen Stack im gemeinsamen konventionellen Bereich vorbereiten.
3. Über INT67/DE0Ch in eigenen 32-Bit-Ring-0-Code eintreten. Tatsächlich geladene CR0/CR3, GDTR/IDTR, TR/LDTR, CS/SS, ESP und Flags aufzeichnen. Der Client besitzt dabei Paging und einen eigenen linearen Datenbereich ab 00400000h.
4. Über den gelieferten Far-Einsprung auch im Protected Mode DE03/04/05 verwenden: eine dritte Seite reservieren, drei vollständige Seiten direkt durch die eigene MMU beschreiben und lesen, die dritte Seite wieder freigeben und die freie Menge vergleichen.
5. Mit einem wirklichen 32-Bit-Far-Call und dem vollständigen V86-Rückkehrrahmen DE0Ch aufrufen. Der Originalserver stellt seine Umgebung wieder her und erreicht den V86-Client. Den Eintritt einschließlich verfügbar gemachtem eigenem TSS ein zweites Mal ausführen, beide ursprünglichen Seiten freigeben und bei unveränderter freier Menge zu DOS zurückkehren.

Die Serverdeskriptoren werden verwendet, wie der Originaltreiber sie liefert. Insbesondere ist sein Code hier **USE16**; ein 32-Bit-Far-Rahmen erzwingt keinen CS.D=1-Deskriptor. Auch der gelieferte Server-Datenselektor wird nicht durch einen angenommenen flachen Hostselektor ersetzt. Es gibt keine besondere CPU-Erkennung des Testprogramms oder Host-Modusumschaltung.

Pro positivem Start werden zwei echte Client-Eintritte und zwei Rückkehrpfade geprüft. Ein unabhängiger Prüfer vergleicht ausführbare NASM-Bytes, die gespeicherten Systemregister, tatsächliche physische PDE/PTE-/A-D-Werte, drei verschiedene Nutzdatenseiten und alle 12.288 Nutzbytes. Die abschließende CPU befindet sich wieder im residenten Monitor; VER und ECHO erreichen den echten DOS-Prompt. Das ist keine Behauptung einer Wiederherstellung aller unbeobachteten CPU-/Debug-/x87-Zustände. Die älteren eigenen PE/TSS-Proben behalten ihre dokumentierte private-TR-Grenze.

## EMS-Referenzpfad

`EMSBANK.COM` prüft die Original-LIM-Schnittstelle, keine neue Host-API:

- EMS 4.0, automatisch gewählter 64-KiB-Seitenrahmen bei E000h; acht logische 16-KiB-Seiten reservieren.
- Alle acht vollständigen Seiten mit unterschiedlichen Mustern füllen; für jede ihrer vier Teilseiten die tatsächliche physische Adresse über VCPI erfassen.
- Rückwärts in dasselbe Fenster zurückwechseln und den ganzen Seiteninhalt vergleichen. Bereits gelesene Zuordnungen dürfen nicht als veralteter TLB-Inhalt weiterwirken.
- Vier gleichzeitige Zuordnungen sichern, anschließend dieselbe logische Seite auf alle vier Slots abbilden. Ein Schreibzugriff im zweiten Slot muss am Seitenende über alle vier Aliase sichtbar sein.
- Das veränderte eigene Nutzwort zurücksetzen, die gesicherte eigene Vier-Slot-Abbildung über AH48h wiederherstellen und alle vier ganzen Seiten vergleichen. Danach den Handle freigeben; die freie Menge muss dem Anfangswert entsprechen.

Pro Start: **24 Mapping-Aufrufe, zwölf vollständige Seitenprüfungen, 128 KiB unabhängig geprüfte Nutzdaten in 32 verschiedenen physischen 4-KiB-Seiten**. Die Anfangs-/Endfreimengen betragen in diesem Profil 169/425/936 EMS-Seiten bei 4/8/16 MiB. Das ist kein vollständiger EMS-4.0-Test aller Funktionen, Fehlerfälle oder vorheriger fremder Handle-Zustände. DOS=UMB/DEVICEHIGH und beliebige UMB-Belegungen sind nicht abgenommen.

## Reproduktion und Nachweise

```powershell
.\scripts\test-vcpi-ems.ps1 -SkipBuild -Disk <IDE-Basis> -Overlay <pristines-COW>
```

Das Skript verwendet ausschließlich neue private COWs. DOS selbst kopiert die beiden eigenen Programme und schreibt zwei Konfigurationen mit originalem HIMEM `/TESTMEM:ON`, EMM386 `NOEMS` beziehungsweise `RAM` und byteidentischer Sicherung der ursprünglichen CONFIG.SYS. FAT-/Journalprüfung: 129 ursprüngliche Dateien, 131 mit den beiden eigenen Clients, 132 nach der Konfigurationssicherung; alle anderen Dateien und beide Originaltreiber bleiben byteidentisch. Die eigenen FAT12-Testdisketten enthalten ausschließlich jeweils den eigenen Client.

Die neue Matrix besteht aus **20 positiven Kaltstarts**:

| RAM | EMM386 | Eigene Clients / Ladepfad | Starts |
| --- | --- | --- | --- |
| 8 MiB | NOEMS | VCPI, IDE und FDC, jeweils zweimal | 4 |
| 8 MiB | RAM | VCPI und EMS, IDE und FDC, jeweils zweimal | 8 |
| 4 MiB | RAM | VCPI und EMS über FDC, jeweils zweimal | 4 |
| 16 MiB | RAM | VCPI und EMS über FDC, jeweils zweimal | 4 |

Wiederholungen je Kombination sind zeit-, bild- und nachweisgleich. Zwei weitere echte Kaltstarts erkennen fehlendes VCPI ohne Monitor sowie fehlenden EMS-Seitenrahmen mit NOEMS korrekt. **16 korrumpierte Oracle-Kontrollen** müssen scheitern, unter anderem bei falschem TR/CR3, Rückkehrzähler, Freimengen, Seitenzuordnung, Nutzdaten, NASM-Befehlsbytes und fehlenden TLB-Invalidierungen. Alle positiven Clients und negativen Verfügbarkeitskontrollen schreiben keine Medien.

Zusätzlich bestehen erneut vier eigene Paging-/V86-Kaltstarts, vier ursprüngliche AH87-/VCPI-Speicherläufe, zwei FreeDOS-IDE- und sechs FDC-Kaltstarts einschließlich privater Writes/Medienwechsel außerhalb EMM386. Alle 44 PowerShell-Skripte/150 Prüfungen und 34 Python-Werkzeuge sind geprüft. Die Nachweispfade stehen im [Implementierungsstand](../IMPLEMENTATION-STATUS.md).

## Offene Abhängigkeiten

Ein nachfolgendes [Teilpaket](X86-DOS4GW.md) prüft originales DOS4GW mit einer kurzen Doom-II-Demo und eigenen x87-Kontrolltests. Ein weiteres [FDC-/VDS-Teilpaket](X86-FDC-VDS.md) prüft inzwischen FDC-Schreiben/Lesen/Verify/Format und tatsächliche VDS-Puffer unter EMM386 NOEMS/RAM, auch mit nicht zusammenhängenden EMS-Aufrufern. Ein überlaufender interner EBDA-Puffer liefert AH09. Beliebige EBDA-/VDS-/DMA-Konfigurationen sowie allgemeine Spiel-/DPMI-/DOS-Extender-Kompatibilität bleiben offen. Das folgende [Call-Gate-Paket](X86-CALL-GATES.md) prüft inzwischen 16-/32-Bit-Gates, TSS-Stacks und Parameter-Rückkehr. Ebenfalls offen: weitere 486-/Fault-/Restart-/EFLAGS-/Debug-/Alignment-/LOCK-Fälle, Hardware-Tasks/Task-Gates, echtes x87-80-Bit-Verhalten und die übrigen M3-Geräte/Snapshots. Pentium, Windows 95, Netzwerk und Voodoo gehören weiterhin zu den nachfolgenden offenen Meilensteinen.

Originalmedien und kanonisches Installations-COW wurden nicht verändert. Keine Commits, Pushes, GUI-Fokusversuche oder native Mauswiederholung. Der ausdrücklich verschobene M2-Mausrest bleibt offen.

Primärgrundlagen: [öffentliche VCPI-Implementierung der Jemm-Autoren](https://github.com/Baron-von-Riedesel/Jemm/blob/master/src/VCPI.ASM), insbesondere DE01h/DE0Ch und der geschützte Far-Rückkehrvertrag; die [LIM Expanded Memory Specification 4.0, Oktober 1987, Dokument 300275-005](https://github.com/OpenRakis/Spice86/wiki/LIM-4.0-Specs-%28EMS%29) der ursprünglichen Herausgeber Lotus/Intel/Microsoft, als Dokumentabschrift. Diese Quellen dienen den Aufrufverträgen; es wurden keine fremden Monitor-/CPU-Implementierungen oder Betriebssystemdateien in den Emulator übernommen.
