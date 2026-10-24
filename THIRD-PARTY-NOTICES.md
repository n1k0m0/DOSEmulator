# Third-party notices

## SharpPixels

The local `src/SharpPixels` snapshot was copied from the adjacent C64Emulator project on 2026-10-24. Original copyright and Apache-2.0 notices are retained in its source and shader files. The C64Emulator working tree was not changed.

The original source includes SharpPixelsWindow, the OpenTK input compatibility adapter, shader sources and generated resource accessors. Its build project is adapted locally to this solution. A local pre-presentation frame hook supports backbuffer capture. This is a self-contained source snapshot, not a build reference to the sibling repository.

## OpenTK

OpenTK 4.9.4 is referenced by SharpPixels and retains its own license. Transitive dependencies retain their respective licenses. Distribution notices must be collected with any future installer.

## NAudio

The Windows GUI uses NAudio 2.3.0 by Mark Heath and contributors, under the MIT license according to the package metadata. It is the same package version used by the adjacent C64Emulator; the DOS host adapter is separate project code. Transitive NAudio packages retain their own notices. NAudio is not referenced by the headless/core/hardware projects. See [NAudio](https://github.com/naudio/NAudio).

## Firmware, probes and font

The development BIOS, demo boot-sector generator, VGA reference palettes, DOS COM probe sources and small development bitmap font are original project sources. No commercial BIOS ROM, IBM font ROM, DOS, Windows, driver or game media is included. The development font is deliberately incomplete and is not presented as a full VGA/CP437 font.

The reference firmware assembler source is also original Apache-2.0 code. NASM 3.02 is an optional build tool downloaded into ignored local artifacts; it is not bundled into the emulator.

## Local FreeDOS test media

The preparation scripts download the official FreeDOS 1.4 LiteUSB and FloppyEdition archives and check their published SHA-256 values. FreeDOS and FreeCOM retain their respective licenses. Archives, original images, privately derived test images, overlays and guest traces remain under ignored `artifacts` and are not repository artifacts or part of the emulator distribution. Kernel, command interpreter, MBR and volume boot code are unchanged in the test images; only FDCONFIG.SYS and FDAUTO.BAT are replaced with minimal test startup configurations.

The graphics acceptance step creates another private copy of the verified minimal IDE image and adds our original COM probes in free FAT16 clusters. Existing operating-system files and boot code remain unchanged. These derived disk images and COM build outputs also stay under ignored artifacts. The checked-in graphics screenshots show only our own generated patterns and emulator interface.
