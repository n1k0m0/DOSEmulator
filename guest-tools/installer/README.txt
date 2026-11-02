DOSE Tools and MiniGlide - guest installers
=========================================

Mount the matching DOSE-Tools-Win95.iso or DOSE-Tools-Win98.iso as a CD in DOSE.
Start Windows, open the CD in My Computer, and double-click SETUP.EXE.
Alternatively, copy the matching standalone Setup-DOSE-Win95.exe or
Setup-DOSE-Win98.exe into the guest and run it there.

Select the components, then click Install. The installers are English, native
Win32 programs and need no additional runtime. They refuse the wrong version.

DOSE Tools: installs C:\DOSETOOLS\DOSETIME.EXE and creates C:\Share if needed.
The background helper synchronizes the Windows clock with the emulated RTC.
Select "Start clock synchronization with Windows" for automatic startup.
This package currently provides time synchronization, not host file transfer,
shared host folders or clipboard sharing. Existing C:\Share files are retained.

MiniGlide: installs GLIDE2X.DLL in the actual Windows System directory and
C:\DOSETOOLS\MGLIDE.EXE. Start > Programs > DOSE Tools contains a logo preview
and, if C:\UNREAL\SYSTEM\UNREAL.EXE is installed, a game shortcut.
Other Glide 2 games can load the system DLL directly. The DOSE host must have
the Glide bridge enabled and its host rendering library configured. The guest
installer does not install or configure software on the host.

APM installation files: optional INF and instructions in C:\DOSETOOLS\APM.
This component prepares a manual Add New Hardware installation; it does not
automatically add another power-management device. Keep the original Windows
setup CD available if its drivers are requested. An existing working APM
installation needs no additional device. See the installed APM guide.

Close running Glide games before installation, updates or removal.
Run Setup again to add or update selected components. Unchecked components
already installed by an earlier run are retained. To remove the package, use
Add/Remove Programs or Start > Programs > DOSE Tools > Uninstall.
The same maintenance window is available as C:\DOSETOOLS\DSETUP.EXE.

First pre-install copies are kept in C:\DOSETOOLS\BACKUP and restored during
removal. The last installed files are retained there as *.LAST. Backups,
C:\Share, the maintenance program and its recovery record are kept after
removal. Unrelated files are never recursively deleted. A previous DOSE Time
startup entry is restored when the clock component is removed.

The guest programs and installer script are own Apache-2.0 software; see
LICENSE.TXT. The installer is built for IA32 and the Windows 9x Win32 API.
No Windows files, game files, original graphics drivers or host renderer are
included. Technical ABI identifiers and required source/license notices remain.
