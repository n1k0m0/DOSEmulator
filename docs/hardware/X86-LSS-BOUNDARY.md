# M3: LSS-Interruptgrenze und vollständige Fortsetzung

Stand: 26. Oktober 2026. **6309/6309 Regressionen, 62 neue Fälle**. Der abgegrenzte LSS-Vertrag ist geschlossen; umfassende CPU-/x87-/M3-Abnahme bleibt offen. [CPU-/x87-Restprüfung](CPU-X87-CONTRACT-REVIEW.md).

## Befund und Änderung

Der bisherige `LoadFarPointer` setzte nach erfolgreichem `LSS` einen zusätzlichen `_interruptShadow=1`. Dadurch konnte ein bereits anstehender maskierbarer Interrupt erst nach der folgenden Instruktion erkannt werden. Intel beschreibt in Abschnitt 3.10 ausdrücklich, dass nach LSS keine solche Sperre besteht: Beide Ziele sind bereits gemeinsam geladen. Die gesonderten MOV-/POP-SS-Schatten bleiben erforderlich und unverändert.

Die Änderung entfernt ausschließlich diese zusätzliche Sperre. Operanden-/Adressgröße, andere Ziel-GPRs, kompletter Pointer-Preflight, Segmentvalidierung, Zugriffsrechte, #AC-/Paging-/Fault-/Taskkontext und gemeinsame Übernahme von SS und Register bleiben im bisherigen Pfad. Es gibt keine neue Geräteprüfung pro CPU-Schritt und keine gelockerte Restart-/Speicherprüfung.

Der neue Prüfsatz reproduziert auf der **hashgleichen akzeptierten 6247er-CPU-DLL** 50 Fehler bei 54 damals vorhandenen Fällen; vier MOV-/POP-SS-/Faultkontrollen bestehen. Ein zusätzlicher wirklicher FreeDOS-Kaltstart erreicht auf derselben alten CPU eine abgeschlossene LSS-Instruktion mit real pending PIT-IRQ und falscher Sperre. Hierfür werden die alten fünf VM-DLLs nur mit dem neuen Prüfmodus kombiniert; kein Ersatz-BIOS und kein synthetischer Interrupt. Dieser absichtliche negative alte Lauf ist keine positive Gastabnahme. Der vollständige alte 64-Dateien-Starter bleibt bytegleich archiviert.

## CPU- und x87-Grenzen

Die endgültigen **62 CPU-Fälle** prüfen Real, PE16, PE32 und tatsächliches gepagtes V86, jeweils Operand-/Adressgröße 16/32 und EAX/ESP/EDI als Ziel. Weitere Fälle prüfen STI → LSS, den tatsächlich auf dem neuen Stack angelegten Hardwareinterrupt-Rahmen, erhaltenen MOV-/POP-SS-Schatten vor FNOP, unzulässigen Selektor beziehungsweise Daten-PF ohne vorzeitige SS-/ESP-Übernahme sowie TF-/Datenbreakpoints nach vollständigem LSS-Commit.

Vier Fälle halten eine unmaskierte rohe x87-Invalid-Ausnahme bei CR0.NE=1 pending: Nach LSS kann zunächst der Hardwareinterrupt eintreten und mit IRETD zurückkehren. Erst das folgende FNOP löst #MF aus. FERR bleibt getrennt erhalten; IGNNE=0/1 ändert bei NE=1 diese Antwort nicht. Dies erweitert den konkret geprüften Zusammenspielvertrag, nicht den numerischen Kern.

## Echte DOS- und Snapshot-Probe

`LSSBOUND.COM` benutzt ausschließlich eigene Programm-/Stackpuffer. Der Gast maskiert IRQ0, liest dessen wirklichen PIC-IRR-Eintrag und gibt ausschließlich IRQ0 frei. `STI` schützt noch die unmittelbar folgende LSS-Instruktion. Deren Ende muss den PIT-Interrupt vor dem nächsten Gastbefehl erkennen. Die Varianten laden SP, vollständiges ESP mit nichtnulliger oberer Hälfte und AX; SS ändert dabei seine Basis. Zwei Kontrollfälle benutzen MOV SS beziehungsweise POP SS mit anschließendem SP-Setzen.

Der tatsächliche IRQ0-Handler liest IP/CS/FLAGS/SS und volles ESP vom neuen Stack, führt ein echtes EOI und IRET aus. Das unabhängige Oracle gewinnt die erwarteten Rückkehradressen aus unveränderten NASM-Bytes, nicht aus dem vom Gast behaupteten Sollwert. Es prüft auch reale Portwerte, ICW2-Zuordnung, IRR vor Freigabe und die periodischen PIT-Grenzen. Bei EMM386 sind physischer Monitor-Eintritt und reflektierter Gastframe ausdrücklich getrennt.

**Acht Kaltstarts und acht tatsächliche Fortsetzungen**, MS-DOS 6.22, FreeDOS und originales EMM386 NOEMS/RAM je zweimal: **40 Fälle/physische PIT-Acknowledgements/Handler-Rückkehrvorgänge**, darunter 24 LSS-Grenzen und 16 MOV-/POP-Kontrollen. Direkt nach dem ersten LSS liegt die Sicherung mit IF=1, Sperre=0 und unquittiertem tatsächlichem PIC-IRQ0. Ein neuer PC mit eigenen Journalen übernimmt sie. **184 weitere vollständige CPU-/8-MiB-RAM-/Geräte-/Schedulervergleiche**, zusätzlich je Anfang/Ende, und identische endgültige Mediengenerationen. Vier Wiederholungspaare sind zeit-/Zustands-/RAM-/Proofhashgleich.

Der Gast restauriert seine **108 ursprünglichen FPU-Bytes** und prüft sie erneut vollständig. Nur sein begrenzter Nachweisblock bleibt durch DOS/AH31 bis Reset resident. Programmbytes, tatsächliche Frames, MCB-Eigentümer/-Größe, Pufferwachen, restaurierter DOS-Vektor/PIC-Masken und der nachfolgende wirkliche DOS-Prompt sind geprüft. **208 verfälschte LSS-Nachweise** scheitern.

Auf demselben endgültigen Kandidaten bestehen gezielt die acht bisherigen PE-/Paging-Debug-Gäste mit 278 Negativoracles und 16 FERR-/IGNNE-/IRQ13-/#MF-Gäste mit 414 Negativoracles. Diese Wiederholung ist durch die geänderte CPU-Interruptgrenze und die erhaltenen SS-/x87-Verträge begründet. Das Gesamtpaket zählt **32 positive DOS-Kaltstarts, acht Fortsetzungen und 900 negative Gastoracles**. Andere historische Zahlen-/Doom-/Audio-/Leistungsmatrizen wurden nicht pauschal wiederholt.

## Reproduktion, Buildbindung und Grenzen

```powershell
.\scripts\test-lss-boundary.ps1 -SkipBuild -Disk <private-blank-32m.img> `
  -Overlay <installed-pristine.cow> -ConfiguredDisk <private-doom-installed.img> `
  -NoEmsOverlay <configure-NOEMS.cow> -RamOverlay <configure-RAM.cow>
```

Alle Medienzweige sind neue private Kopien; Eingaben gelangen nur über den Gast-KBC. Der alte 6247er-Build und dessen Snapshotnachweise bleiben erhalten. **Format 1 ist weiterhin an denselben VM-Build gebunden**: Neue CPU-DLL bedeutet neue Snapshot-Buildidentität, keine stille Migration alter `.dossav`. Bestehende PC-Definitionen/Mediencheckpoints bleiben Kaltstarts. Der 6309er-Gast erfasst/lädt eigene neue Container; die alten 6247er-Container werden nicht verändert.

PIC-Masken und Vektor werden restauriert. Die fünf unterdrückten BIOS-Timerdienste und aktive Scheduler-/PIC-Zustände werden vom Gast nicht vollständig zurückgespult. Dies ist keine NMI-/ICE-/Bus-/Silizium-, allgemeine Debug-/x87-/Extender-/Spiel-/Sound-/Performanceabnahme. FPU-Numerikquellen bleiben bytegleich. Keine Windows-Eingaben/Hostendpunkte, keine Commits/Pushes oder Videoänderungen.

Privater endgültiger Nachweis: **`artifacts/tests/m3-lss-boundary-release-path.txt`**. Primärgrundlage: Intel, [Intel486 Programmer’s Reference Manual (1992)](https://manuals.plus/m/d86207aaebd9b10464f2058efc61559e6141d3b072db100e89bfe17c8187e143), Abschnitt 3.10 und LGS/LSS/LDS/LES/LFS in Kapitel 26. Aktuelle Intel-SDM-Ausgaben sind ergänzende Quellen, keine unbelegte Gleichsetzung mit jedem historischen Stepping.
