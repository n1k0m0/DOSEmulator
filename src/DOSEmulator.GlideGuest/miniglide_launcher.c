/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Win9x program launcher for both the host bridge and the emulated renderer. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commdlg.h>
#include "miniglide_splash.h"

static char Program[MAX_PATH];
static char Command[2048];
static char Directory[MAX_PATH];

static int IsSpace(char c) { return c == ' ' || c == '\t'; }

/* Skip our own quoted or unquoted filename without consuming target arguments. */
static const char *Arguments(void)
{
    const char *at = GetCommandLineA();
    if (*at == '"') {
        ++at;
        while (*at && *at != '"') ++at;
        if (*at) ++at;
    } else {
        while (*at && !IsSpace(*at)) ++at;
    }
    while (IsSpace(*at)) ++at;
    return at;
}

static int IsSwitch(const char *arguments, const char *value)
{
    while (*value && *arguments == *value) { ++arguments; ++value; }
    if (*value) return 0;
    while (IsSpace(*arguments)) ++arguments;
    return *arguments == 0;
}

/* A classic common dialog keeps the launcher usable for arbitrary legacy demos. */
static int ChooseProgram(void)
{
    OPENFILENAMEA choice;
    int i;
    for (i = 0; i < sizeof(choice); ++i) ((BYTE *)&choice)[i] = 0;
    choice.lStructSize = OPENFILENAME_SIZE_VERSION_400;
    choice.lpstrFilter = "Windows programs (*.exe)\0*.exe\0All files (*.*)\0*.*\0\0";
    choice.lpstrFile = Program;
    choice.nMaxFile = sizeof(Program);
    choice.lpstrTitle = "MiniGlide - select a game or hardware demo";
    choice.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    return GetOpenFileNameA(&choice) != 0;
}

void __stdcall MiniGlideEntry(void)
{
    STARTUPINFOA startup;
    PROCESS_INFORMATION process;
    const char *arguments = Arguments();
    int i, quoted, length = 0, slash = -1;
    if (IsSwitch(arguments, "/preview")) {
        MiniGlideShowSplash();
        ExitProcess(0);
    }
    if (IsSwitch(arguments, "/choose")) {
        if (!ChooseProgram()) ExitProcess(0);
        lstrcpyA(Command, "\""); lstrcatA(Command, Program); lstrcatA(Command, "\"");
    } else if (!*arguments) {
        lstrcpyA(Program, "C:\\UNREAL\\SYSTEM\\UNREAL.EXE");
        lstrcpyA(Command, "\"C:\\UNREAL\\SYSTEM\\UNREAL.EXE\"");
    } else {
        if (lstrlenA(arguments) >= sizeof(Command)) goto failed;
        lstrcpyA(Command, arguments);
        quoted = *arguments == '"';
        if (quoted) ++arguments;
        while (*arguments && (quoted ? *arguments != '"' : !IsSpace(*arguments))) {
            if (length >= sizeof(Program) - 1) goto failed;
            Program[length++] = *arguments++;
        }
        Program[length] = 0;
        if (!length || (quoted && *arguments != '"')) goto failed;
    }
    for (i = 0; Program[i]; ++i) if (Program[i] == '\\' || Program[i] == '/') slash = i;
    if (slash >= 0) {
        for (i = 0; i <= slash; ++i) Directory[i] = Program[i];
        Directory[slash + 1] = 0;
    }
    if (GetFileAttributesA(Program) == INVALID_FILE_ATTRIBUTES) goto failed;
    MiniGlideShowSplash();
    /* Only this launcher and its child inherit these settings. */
    if (!SetEnvironmentVariableA("FX_GLIDE_NO_SPLASH", "1") ||
        !SetEnvironmentVariableA("MINIGLIDE_SPLASH_DONE", "1")) goto failed;
    for (i = 0; i < sizeof(startup); ++i) ((BYTE *)&startup)[i] = 0;
    for (i = 0; i < sizeof(process); ++i) ((BYTE *)&process)[i] = 0;
    startup.cb = sizeof(startup);
    if (!CreateProcessA(Program, Command, 0, 0, FALSE, 0, 0,
            Directory[0] ? Directory : 0, &startup, &process)) goto failed;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    ExitProcess(0);
failed:
    MessageBoxA(0, "The program could not be started. Specify a quoted EXE path,\r\n"
        "use /choose, or install Unreal in C:\\UNREAL.", "MiniGlide", MB_OK | MB_ICONERROR);
    ExitProcess(1);
}
