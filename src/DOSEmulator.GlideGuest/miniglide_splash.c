/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Original MiniGlide artwork, drawn with Windows 95-compatible GDI. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "miniglide_splash.h"

static int SplashShown;

#define SPLASH_WIDTH 480
#define SPLASH_HEIGHT 220

/* MSVC may lower aggregate initialization to memset even without the CRT. */
#pragma function(memset)
void *__cdecl memset(void *destination, int value, size_t count)
{
    volatile BYTE *current = (volatile BYTE *)destination;
    while (count--) *current++ = (BYTE)value;
    return destination;
}

#pragma function(memcpy)
void *__cdecl memcpy(void *destination, const void *source, size_t count)
{
    volatile BYTE *out = (volatile BYTE *)destination;
    const volatile BYTE *in = (const volatile BYTE *)source;
    while (count--) *out++ = *in++;
    return destination;
}

static void Fill(HDC dc, int left, int top, int right, int bottom, COLORREF color)
{
    RECT bounds;
    HBRUSH brush;
    bounds.left = left;
    bounds.top = top;
    bounds.right = right;
    bounds.bottom = bottom;
    brush = CreateSolidBrush(color);
    FillRect(dc, &bounds, brush);
    DeleteObject(brush);
}

static void DrawMark(HDC dc)
{
    POINT upper[] = {{54, 139}, {69, 72}, {91, 111}, {112, 72}, {128, 139},
                     {112, 139}, {105, 108}, {91, 135}, {77, 108}, {70, 139}};
    POINT lower[] = {{47, 151}, {132, 151}, {124, 160}, {39, 160}};
    HBRUSH brush = CreateSolidBrush(RGB(39, 215, 207));
    HPEN pen = CreatePen(PS_SOLID, 1, RGB(39, 215, 207));
    HGDIOBJ oldBrush = SelectObject(dc, brush);
    HGDIOBJ oldPen = SelectObject(dc, pen);
    Polygon(dc, upper, sizeof(upper) / sizeof(upper[0]));
    Polygon(dc, lower, sizeof(lower) / sizeof(lower[0]));
    SelectObject(dc, oldPen);
    SelectObject(dc, oldBrush);
    DeleteObject(pen);
    DeleteObject(brush);
}

static void DrawWords(HDC dc)
{
    HFONT title = CreateFontA(-50, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
        ANSI_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
        DEFAULT_PITCH | FF_SWISS, "Arial");
    HFONT subtitle = CreateFontA(-15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        ANSI_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
        DEFAULT_PITCH | FF_SWISS, "Arial");
    HGDIOBJ oldFont = SelectObject(dc, title);
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(239, 250, 255));
    TextOutA(dc, 153, 74, "MiniGlide", 9);
    SelectObject(dc, subtitle);
    SetTextColor(dc, RGB(122, 165, 180));
    TextOutA(dc, 156, 132, "3D GRAFIKBESCHLEUNIGUNG", sizeof("3D GRAFIKBESCHLEUNIGUNG") - 1);
    SelectObject(dc, oldFont);
    DeleteObject(title);
    DeleteObject(subtitle);
}

static LRESULT CALLBACK SplashWindow(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == WM_PAINT) {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(window, &paint);
        Fill(dc, 0, 0, SPLASH_WIDTH, SPLASH_HEIGHT, RGB(9, 20, 35));
        Fill(dc, 0, 0, SPLASH_WIDTH, 5, RGB(39, 215, 207));
        Fill(dc, 0, 196, SPLASH_WIDTH, SPLASH_HEIGHT, RGB(16, 37, 52));
        Fill(dc, 156, 158, 430, 160, RGB(39, 215, 207));
        DrawMark(dc);
        DrawWords(dc);
        EndPaint(window, &paint);
        return 0;
    }
    return DefWindowProcA(window, message, wparam, lparam);
}

void __stdcall MiniGlideShowSplash(void)
{
    HINSTANCE instance = GetModuleHandleA(0);
    WNDCLASSA klass;
    HWND window;
    DWORD began;
    MSG message;
    char inherited[2];
    int i;

    if (SplashShown) return;
    SplashShown = 1;
    /* The launcher already drew the same artwork before starting this process. */
    if (GetEnvironmentVariableA("MINIGLIDE_SPLASH_DONE", inherited, sizeof(inherited)) == 1 && inherited[0] == '1') {
        SetEnvironmentVariableA("MINIGLIDE_SPLASH_DONE", 0);
        return;
    }

    for (i = 0; i < sizeof(klass); ++i) ((BYTE *)&klass)[i] = 0;
    klass.lpfnWndProc = SplashWindow;
    klass.hInstance = instance;
    klass.hCursor = LoadCursorA(0, IDC_ARROW);
    klass.lpszClassName = "MiniGlideSplash";
    if (!RegisterClassA(&klass)) return;
    window = CreateWindowExA(WS_EX_TOPMOST, klass.lpszClassName, "MiniGlide",
        WS_POPUP | WS_BORDER,
        (GetSystemMetrics(SM_CXSCREEN) - SPLASH_WIDTH) / 2,
        (GetSystemMetrics(SM_CYSCREEN) - SPLASH_HEIGHT) / 2,
        SPLASH_WIDTH, SPLASH_HEIGHT, 0, 0, instance, 0);
    if (!window) { UnregisterClassA(klass.lpszClassName, instance); return; }
    ShowWindow(window, SW_SHOWNORMAL);
    UpdateWindow(window);
    began = GetTickCount();
    while ((DWORD)(GetTickCount() - began) < 1400) {
        while (PeekMessageA(&message, 0, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageA(&message);
        }
        Sleep(10);
    }
    DestroyWindow(window);

    UnregisterClassA(klass.lpszClassName, instance);
}
