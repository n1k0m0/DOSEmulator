/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Optional Unreal shortcut; the generic DLL supplies the MiniGlide splash. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "miniglide_splash.h"

static int PreviewRequested(const char *command)
{
    const char preview[] = "/preview";
    while (*command) {
        int i = 0;
        while (preview[i] && command[i] == preview[i]) ++i;
        if (!preview[i]) return 1;
        ++command;
    }
    return 0;
}

void __stdcall MiniGlideEntry(void)
{
    STARTUPINFOA startup;
    PROCESS_INFORMATION process;
    int i;
    if (PreviewRequested(GetCommandLineA())) {
        MiniGlideShowSplash();
        ExitProcess(0);
    }
    for (i = 0; i < sizeof(startup); ++i) ((BYTE *)&startup)[i] = 0;
    for (i = 0; i < sizeof(process); ++i) ((BYTE *)&process)[i] = 0;
    startup.cb = sizeof(startup);
    if (!CreateProcessA("C:\\UNREAL\\SYSTEM\\UNREAL.EXE", 0, 0, 0, FALSE, 0, 0,
            "C:\\UNREAL\\SYSTEM", &startup, &process)) {
        MessageBoxA(0, "Unreal wurde unter C:\\UNREAL\\SYSTEM nicht gefunden.",
            "MiniGlide", MB_OK | MB_ICONERROR);
        ExitProcess(1);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    ExitProcess(0);
}
