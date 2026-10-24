# Private Datenträger-Overlays

Das Basisimage wird ausschließlich über `ReadOnlyRawDisk` geöffnet. Gastschreiben ist nur über `CopyOnWriteDisk` möglich. Alle Änderungen stehen in einer separaten privaten `.cow`-Datei. Dieses eigene Format ist kein QCOW-Format.

## Dateiformat DOSCOW01

Alle Zahlen sind little endian. Es werden ausschließlich 512-Byte-Sektoren gespeichert.

| Bereich | Bytes | Inhalt |
| --- | --- | --- |
| Header | 8 | ASCII `DOSCOW01` |
| Header | 8 | Sektoranzahl des Basisimages als Int64 |
| Header | 32 | SHA-256 des vollständigen Basisimages |
| Je Datensatz | 8 | LBA als Int64 |
| Je Datensatz | 512 | Sektordaten |
| Je Datensatz | 32 | SHA-256 der vorangehenden 520 Datensatzbytes |

Beim Öffnen müssen Basisidentität und Kapazität stimmen. Der letzte gültige Datensatz für eine LBA gewinnt. Ein beschädigter vollständiger Datensatz wird als Fehler abgelehnt. Ein unvollständiger letzter Datensatz wird abgeschnitten; alle vorangehenden Sektoren bleiben erhalten.

Ein vollständiger Datensatz wird mit `Flush(true)` gespeichert, bevor die laufende Lesesicht auf den neuen Sektor wechselt. Bei einem Schreibfehler wird das Journal für weitere Schreibzugriffe gesperrt. Zur Wiederaufnahme muss der Datenträger vollständig geschlossen und neu geöffnet werden. Ein Stromausfall mitten in einer DOS-Dateisystemänderung kann wie bei echter Hardware mehrere zusammengehörige Sektoren betreffen. Das Journal ist keine atomare FAT-Transaktion.

## Exklusiver Zugriff und Maschinenwechsel

Die Overlaydatei erlaubt genau einen geöffneten Store. Zwei unabhängig gestartete Emulatoren können dieselbe Overlaydatei nicht gleichzeitig verwenden. Das Basisimage bleibt währenddessen gegen Host-Schreiben gesperrt.

`Reopen()` bereitet eine zweite Lease für einen Maschinenneustart vor. Sie darf Namen/Kapazität abfragen, aber noch keine Sektoren lesen oder schreiben. Erst das Freigeben der alten Maschine aktiviert die neue Lease. Scheitert die Vorbereitung, wird die zweite Lease verworfen und der bisherige Besitzer kann weiterarbeiten. Damit lässt sich eine Konfigurationsänderung prüfen, bevor die alte Maschine aufgegeben wird.

Gesamte RAM-/Geräte-Savestates und versiegelte Mediengenerationen sind noch offen. Ein Overlay allein stellt keinen vollständigen Emulatorzustand dar. Sein Dateiformat hat keine Snapshot-Verzweigungs- oder Kompaktierungsoperation.

## Verwendung und Nachweise

```powershell
.\scripts\run-freedos.ps1 -Writable
.\scripts\run-freedos-floppy.ps1 -Writable -TwoDrives
.\scripts\test-freedos-writes.ps1 -SkipBuild
.\scripts\test-freedos-floppy.ps1 -SkipBuild
```

Das Medienmenü kann neue Overlays erstellen oder bestehende wieder aufnehmen. Die ursprünglichen Datenträger werden dabei nicht überschrieben. Ein Emulatorneustart erhält die Änderungen; die eigenen Testskripte legen für jeden Abnahmelauf neue getrennte Overlays an.

Geprüft sind Basisidentität, Exklusivität, sektorweise Persistenz, beschädigte/abgebrochene Datensätze, Neustart-Lease und Rollback. Echte FreeDOS-Prozesse prüfen anschließend Dateiinhalte und Verzeichnisänderungen über mehrere Kaltstarts. Die genauen Ergebnisse stehen im [Implementierungsstand](IMPLEMENTATION-STATUS.md).
