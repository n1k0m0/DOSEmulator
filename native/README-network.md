<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Nils Kopal -->
# Native networking dependencies

DOSE uses unmodified **libslirp4.9.3** for IPv4 NAT and the restricted host-only
gateway. Its DHCP, ARP, DNS, TCP and UDP implementations come from libslirp.
Networking is activated only by an explicit frontend Connect action; constructing
or importing a machine leaves external transports disconnected.

Run `scripts/prepare-network-runtime.py --output <new workspace directory>` to
prepare an existing pinned package cache. Add `--download` explicitly to fetch
missing exact official MSYS2 packages. The script only selects the actual Win64
DLL import closure, the corresponding package license notices and libslirp's
header. It does not run PE files, install packages/drivers or change PATH.

The prepared directory belongs at `Release/native/win-x64`. Keep its
`network-runtime.json`, actual `licenses/`, and README beside the separate DLLs.
The runtime verifies their complete identities and loads absolute paths with
`LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32`. It never calls
SetDllDirectory or uses the current directory/PATH for native lookup.

libslirp carries BSD/MIT notices. GLib, libintl and libiconv carry LGPL notices;
other selected dependencies retain the actual notices supplied in their pinned
packages. Preserve those texts and fulfill the corresponding-source obligations
when distributing the selected libraries. The generated README and manifest give
the exact binary package, SHA256 and corresponding MSYS2 source-package URLs.
Dynamic linking keeps these libraries replaceable; they are not incorporated
into a DOSE assembly or a statically linked executable.

Npcap is **not bundled or installed**. Bridged mode uses only an explicitly chosen
local adapter from an already installed `System32/Npcap/wpcap.dll`. Npcap's own
installation and redistribution terms apply. Missing Npcap produces an English
unavailability error, with no WinPcap, first-adapter, remote-capture or NAT fallback.

Primary references:

- [libslirp source and API](https://gitlab.freedesktop.org/slirp/libslirp)
- [Official MSYS2 libslirp package](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-libslirp)
- [Npcap API and installation guide](https://npcap.com/guide/)

The Win64 ABI follows the pinned package header: `SlirpConfig`192bytes,
version6 callback table104bytes, pointer-sized SOCKET and `WSAPOLLFD`16bytes.
Windows pcap packet headers use two32-bit timeval fields and are16bytes.
Real runtime/driver and guest validation is separate from preparing these files.
