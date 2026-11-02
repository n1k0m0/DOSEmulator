# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
#requires -Version 7.4
param([Parameter(Mandatory = $true)][string]$Output, [long]$Handle = 0)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GlideWindowCapture
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@
$window = if ($Handle -ne 0) { [IntPtr]::new($Handle) } else { [GlideWindowCapture]::FindWindow('Static', 'MiniGlide') }
if ($window -eq [IntPtr]::Zero) { throw 'The Glide host window is not open.' }
$rect = [GlideWindowCapture+Rect]::new()
if (![GlideWindowCapture]::GetWindowRect($window, [ref]$rect)) { throw 'Could not read Glide host window bounds.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw 'Glide host window has no visible area.' }
$image = [System.Drawing.Bitmap]::new($width, $height)
try {
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    try {
        $dc = $graphics.GetHdc()
        try { if (![GlideWindowCapture]::PrintWindow($window, $dc, 0)) { throw 'PrintWindow failed.' } }
        finally { $graphics.ReleaseHdc($dc) }
    }
    finally { $graphics.Dispose() }
    $image.Save([System.IO.Path]::GetFullPath($Output), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally { $image.Dispose() }
