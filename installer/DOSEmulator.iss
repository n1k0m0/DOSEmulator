; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{D09EC9E5-8B09-4B47-9B7A-BC9A01021001}
AppName=DOSE Emulator
AppVersion={#AppVersion}
AppPublisher=Nils Kopal
AppPublisherURL=https://github.com/n1k0m0/DOSEmulator
AppSupportURL=https://github.com/n1k0m0/DOSEmulator/issues
AppUpdatesURL=https://github.com/n1k0m0/DOSEmulator/releases
DefaultDirName={localappdata}\Programs\DOSEmulator
DefaultGroupName=DOSE Emulator
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=DOSEmulator-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\branding\dose.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\DOSEmulator.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\DOSE Emulator"; Filename: "{app}\DOSEmulator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DOSE Emulator"; Filename: "{app}\DOSEmulator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DOSEmulator.exe"; Description: "{cm:LaunchProgram,DOSE Emulator}"; Flags: nowait postinstall skipifsilent
