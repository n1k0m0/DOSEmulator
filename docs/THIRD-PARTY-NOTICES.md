# Quellen- und Lizenzhinweise

## YMFM: OPL-Rhythmusforschung und private Vergleichsreferenz

Die OPL2-Implementierung verwendet für das 23-Bit-Rauschpolynom und die Phase der Schlaginstrumente Formeln aus [Aaron Giles' YMFM](https://github.com/aaronsgiles/ymfm), insbesondere `src/ymfm_opl.cpp` und `src/ymfm_fm.ipp`. Die übrige Projektstruktur, Synthese, Hüllkurven-/Mischungsarithmetik und Integration sind eigener Code. Die Formeln werden hier ausdrücklich zugeordnet und ihre BSD-Lizenz mitgeführt. Historische Aussagen über die zuvor rein aus Yamaha-Unterlagen entwickelte melodische Synthese beschreiben den damaligen Stand.

Der separat gebaute YMFM-Vergleich unter `artifacts/references` gehört nicht zum Produkt. Dieser Ordner und seine Binaries werden nicht veröffentlicht. Der eigene Adapter in `tests/opl2-reference.cpp` lädt keine ROMs.

```text
BSD 3-Clause License

Copyright (c) 2021, Aaron Giles
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Creative ADPCM

Der Decoder verwendet eigene skalare Arithmetik. Format-/Befehlsgrundlage ist Creatives Hardware Programming Guide; die Schrittgrößen, 3/3/2-Packung und Blockbeobachtungen werden gegen [DOSLIBs ursprüngliche Hardwareversuche](https://github.com/joncampbell123/doslib/tree/master/hw/sndsb) geprüft. Keine DOSLIB-/DOSBox-Decoderroutine oder fremde ADPCM-Tabelle ist als Produktbestandteil eingebettet. Grenzen und Provenienz stehen in [hardware/SB2-DSP.md](hardware/SB2-DSP.md).
