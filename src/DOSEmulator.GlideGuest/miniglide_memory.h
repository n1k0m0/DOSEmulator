/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Ordinary guest memory accesses let Win9x resolve demand-zero/file-backed
   pages before an OUT. Host address translation cannot deliver guest #PF.
   This is bounded staging, not page locking or snapshot support. */
#ifndef MINIGLIDE_MEMORY_H
#define MINIGLIDE_MEMORY_H
static unsigned char BridgePixels[262144];
static DWORD BridgeTransferArgs[10], BridgeTransferInfo[5];

/* Fault in every covered page without changing any caller byte. */
static void BridgeTouch(void *pointer, DWORD bytes, int writable)
{
    volatile unsigned char *p = (volatile unsigned char *)pointer;
    DWORD offset = 0;
    if (!bytes) return;
    for (;;) {
        unsigned char value = p[offset];
        if (writable) p[offset] = value;
        DWORD step = 4096 - (((DWORD)pointer + offset) & 4095);
        if (step >= bytes - offset) break;
        offset += step;
    }
    unsigned char value = p[bytes - 1];
    if (writable) p[bytes - 1] = value;
}

/* Copy each actual input byte with the guest CPU, including ordinary Win9x
   paging and exception behavior; no host reads an unmaterialized input page. */
static void BridgeCopy(DWORD offset, const void *source, DWORD bytes)
{
    const volatile unsigned char *p = (const volatile unsigned char *)source;
    for (DWORD i = 0; i < bytes; ++i) BridgePixels[offset + i] = p[i];
}

/* Prepare only the external memory consumed by the named supported scalar
   call. Invalid dimensions retain the host's existing explicit rejection. */
static const DWORD *__stdcall BridgePrepare(DWORD id, const DWORD *arguments)
{
    DWORD i, bytes = 0, words = 0;
    if (id == 53) {
        BridgeTouch(GlideLfbBuffer, sizeof(GlideLfbBuffer), 1);
        BridgeTouch((void *)arguments[5], 20, 1);
    } else if (id == 54) {
        DWORD height = arguments[4], stride = arguments[5];
        if (height && stride && height <= 4194304 / stride)
            BridgeTouch((void *)arguments[6], height * stride, 1);
    } else if (id == 68 || id == 69) {
        BridgeTouch((void *)arguments[0], 148, 1);
    } else if (id == 99) {
        BridgeTouch((void *)arguments[1], 16, 0);
    } else if (id == 42) {
        BridgeTouch((void *)arguments[0], 64, 0);
    } else if (id == 85) {
        const DWORD *info = (const DWORD *)arguments[3];
        DWORD small = info[0], large = info[1], aspect = info[2], format = info[3];
        if (small > 8 || large > small || aspect > 6 || format > 15) return arguments;
        for (DWORD lod = large; lod <= small; ++lod) {
            DWORD dimension = 256 >> lod;
            DWORD width = aspect <= 3 ? dimension : dimension >> (aspect - 3);
            DWORD height = aspect >= 3 ? dimension : dimension >> (3 - aspect);
            if (!width) width = 1;
            if (!height) height = 1;
            bytes += width * height * (format >= 8 ? 2 : 1);
        }
        BridgeCopy(0, (void *)info[4], bytes);
        for (i = 0; i < 4; ++i) BridgeTransferInfo[i] = info[i];
        BridgeTransferInfo[4] = (DWORD)BridgePixels;
        for (i = 0; i < 4; ++i) BridgeTransferArgs[i] = arguments[i];
        BridgeTransferArgs[3] = (DWORD)BridgeTransferInfo;
        return BridgeTransferArgs;
    } else if (id == 86 || id == 87) {
        DWORD lod = arguments[2], aspect = arguments[4], format = arguments[5];
        if (lod > 8 || aspect > 6 || format > 15) return arguments;
        DWORD dimension = 256 >> lod;
        DWORD width = aspect <= 3 ? dimension : dimension >> (aspect - 3);
        DWORD height = aspect >= 3 ? dimension : dimension >> (3 - aspect);
        if (!width) width = 1;
        if (!height) height = 1;
        DWORD rows = height;
        if (id == 87) {
            DWORD first = arguments[8], last = arguments[9];
            if (first > last || last >= height) return arguments;
            rows = last - first + 1;
        }
        bytes = width * rows * (format >= 8 ? 2 : 1);
        BridgeCopy(0, (void *)arguments[7], bytes);
        words = id == 86 ? 8 : 10;
        for (i = 0; i < words; ++i) BridgeTransferArgs[i] = arguments[i];
        BridgeTransferArgs[7] = (DWORD)BridgePixels;
        return BridgeTransferArgs;
    } else if (id == 88 || id == 89) {
        DWORD table = arguments[1], offset = 0;
        if (table > 2) return arguments;
        bytes = table == 2 ? 1024 : 112;
        if (id == 89) {
            DWORD first = arguments[3], last = arguments[4];
            if (table == 2) {
                if (first > last || last > 255) return arguments;
                offset = first * 4; bytes = (last - first + 1) * 4;
            } else if (first != 0 || last != 11) return arguments;
        }
        BridgeCopy(offset, (void *)(arguments[2] + offset), bytes);
        words = id == 88 ? 3 : 5;
        for (i = 0; i < words; ++i) BridgeTransferArgs[i] = arguments[i];
        BridgeTransferArgs[2] = (DWORD)BridgePixels;
        return BridgeTransferArgs;
    }
    return arguments;
}
#endif
