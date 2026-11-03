/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
#ifndef MINIGLIDE_SWAP_H
#define MINIGLIDE_SWAP_H

__declspec(dllimport) void __stdcall Sleep(DWORD milliseconds);
static DWORD BridgeSwapMode;
static unsigned char BridgeHostVersion[80];

/* The existing void version request is safe on older hosts, which return zero.
   Its private return word advertises cooperative-swap protocol MGS1. */
static void BridgeDetectSwap(void)
{
    DWORD destination = (DWORD)BridgeHostVersion, capability;
    BridgeTouch(BridgeHostVersion, sizeof(BridgeHostVersion), 1);
    __asm {
        lea ecx, destination
        mov eax, 45
        mov dx, 61712
        out dx, eax
        mov capability, eax
    }
    BridgeSwapMode = capability == 0x4D475331 ? 2 : 1;
}

/* Preserve the interval and wait through Windows, so PIT IRQs and other guest
   threads continue while the host renderer is presenting. No guest ticks are
   synthesized and the API returns only after the real native swap completes. */
static void __stdcall BridgeSwap(DWORD interval)
{
    DWORD token, done;
    if (!BridgeSwapMode) BridgeDetectSwap();
    if (BridgeSwapMode == 1) {
        __asm {
            lea ecx, interval
            mov eax, 16
            mov dx, 61712
            out dx, eax
        }
        return;
    }
    __asm {
        lea ecx, interval
        mov eax, 65533
        mov dx, 61712
        out dx, eax
        mov token, eax
    }
    do {
        __asm {
            mov ecx, token
            mov eax, 65532
            mov dx, 61712
            out dx, eax
            mov done, eax
        }
        if (!done) Sleep(1);
    } while (!done);
}

#endif
