# DOSE Tools for Windows 95

The current [Windows 95 and 98 guest installers](GUEST-INSTALLERS.md) provide the existing clock helper, startup selection, MiniGlide, shortcuts and removal with restoration. The file-transfer and clipboard components described below remain a design for future work.

Status: product design, updated 28 October 2026 following the user's installer/background-app/import/export proposal. The installer, guest connection and transfer UI are not implemented yet. All new installer, guest-app and DOSE transfer UI text will be **English**.

## Version 1: install once, transfer files through DOSE

DOSE Tools has three visible parts:

| Component | User experience |
| --- | --- |
| **DOSE Tools Setup** | A native Windows 95 installer installs the helper and required guest driver, creates **C:\Share**, and offers **Start DOSE Tools with Windows**. |
| **DOSE Tools** | A small background application starts with Windows, maintains the connection to DOSE, and provides a tray icon with status and **Open Share Folder**. |
| **File Transfer** in DOSE | The user browses the guest's Share folder, chooses **Import Files...** or **Export Files...**, and sees progress and per-file results. |

The first version uses explicit imports and exports. It does not require a permanently mapped host folder or automatic synchronization. The guest directory is fixed to C:\Share initially; configurable locations can follow if useful.

Files in C:\Share are ordinary files on the guest's actual disk. The user can open, edit, move or delete them in Windows Explorer and applications. They remain there when DOSE Tools is stopped. A transfer reads or writes through Windows file APIs, so Windows manages VFAT names, allocation, open handles and its own caches.

## Installer and background application

Setup is delivered on a DOSE Tools installation disc, containing only our software and its license. The user runs SETUP.EXE inside Windows 95. The initial package includes the native x86 helper, its required guest driver, startup registration and an uninstaller. It does not depend on a modern guest runtime.

Setup creates C:\Share only if it is missing. Existing contents are preserved. Application files use the guest's normal application-installation location; the installer must resolve that location rather than assume an English Windows folder layout.

Suggested setup text:

- **Welcome to DOSE Tools Setup**
- **Install DOSE Tools**
- **Shared folder: C:\Share**
- **Start DOSE Tools with Windows**
- **Launch DOSE Tools**
- **Installation complete**

The installer must report any actual restart requirement and offer **Restart Now** / **Restart Later** when needed. The current host-side ISO mount contract requires a cold start; the DOSE command that supplies the installation disc must explain that requirement instead of promising a seamless live mount.

The helper should stay quiet while idle and perform bounded work when a transfer arrives. Its tray menu offers **Open Share Folder**, **Connection Status**, **Start with Windows**, **About DOSE Tools**, and **Exit**. Closing the status window leaves the background app running; **Exit** stops it.

Uninstall removes our app, driver and startup registration, while preserving C:\Share and its contents. Reinstallation, upgrades and interrupted setup must preserve existing user data.

Windows 95 startup applications are described in the original Microsoft [Q156126](https://jeffpar.github.io/kbarchive/kb/156/Q156126/). The exact startup registration and its removal will be tested in the supplied original guest, including a normal reboot. The tray implementation must use the original shell interfaces and structure sizes. Current [Shell_NotifyIcon documentation](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona) distinguishes later shell features; modern examples are not a Windows 95 compatibility test.

## DOSE File Transfer window

The proposed DOSE menu entries are **Install DOSE Tools...** and **File Transfer...**. Installation-disc preparation and running the guest installer are separate visible steps.

The transfer window shows:

- **Guest folder: C:\Share**, with files and subfolders listed by the guest helper.
- **Import Files...** and **Import Folder...** to copy selected host data into that guest folder.
- **Export Files...** to save selected guest files or folders to a host destination chosen by the user.
- **Refresh**, **New Folder**, and **Cancel Transfer**, with actions enabled according to the actual connection state.

Import/export is copy-based. Exporting does not remove the source from the guest; importing does not remove the host source. Transfers have a queue, byte progress and a clear final result.

Status text includes **Connected**, **Waiting for DOSE Tools**, **Virtual machine is paused**, **Transferring...**, **Transfer complete**, and **Transfer failed**. If the helper is unavailable, the UI explains how to install or start it. It must not report success for an undelivered request.

Existing destination files prompt **Replace**, **Keep Both**, or **Skip**. There is no silent overwrite or automatic propagation of deletions. Locked files, full disks, read-only destinations, unavailable directories and unsupported names produce per-file errors with a retry path.

## Internal connection

The preferred connection is an explicitly optional DOSE virtual data channel, accessed by a small Windows 95 VxD and the native Win32 helper. The helper performs directory enumeration and file operations. DOSE does not independently edit FAT or an active RAW/COW filesystem.

The device contract must define resources, commands, actual errors, reset, owner-thread processing, deadlines and complete snapshot state before being offered in a profile. Existing historical profiles and advertised CPU features remain unchanged. The UI exposes file-transfer status rather than these implementation details.

A purely virtual COM link remains a possible development probe for the common protocol. It is not the selected user-facing dependency, does not open a host COM port, and would need its own original Windows COM-driver test.

The versioned protocol carries transfer IDs, connection generations, relative paths, sizes, bounded chunks, integrity checks, acknowledgements and cancellation. Host file work runs outside the CPU/device hot path. Only bounded prepared work crosses into the machine owner.

For initial transfers, the guest helper only accesses C:\Share and its descendants; the host side only accesses explicitly selected sources and destinations. Validate traversal, absolute paths, DOS device names, case collisions, code-page conversion and host path redirections. Long-name support must be tested with original VFAT; unsupported names must be reported instead of silently altered.

## Completion, interruption and snapshots

Receivers use their own temporary files, verify complete contents, close them, and only then publish the destination. Windows 95 replacement and recovery behavior must be tested with its actual APIs; modern atomic-replace APIs or an atomic whole-folder update are not assumed.

Reset, snapshot load, cold start and owner replacement revoke the old connection generation. The new session performs a fresh handshake; old chunks and acknowledgements cannot publish files. Generation validation and final host-file publication need a shared boundary so a reset cannot race the final rename.

A guest snapshot does not roll back completed host filesystem changes. Restoring it may therefore require conflict resolution or an explicit retry. Both host and guest must handle restored temporary files and old transfer IDs without mistaking them for newly authorized completion. Live host streams and opaque callbacks are outside the VM state.

Pause, cancellation, helper exit, driver unload and normal Windows shutdown each need an explicit completion/abort contract. Progress and errors must remain consistent after reconnecting.

## Later additions

File drag-and-drop into the DOSE window can use the same import queue. Text clipboard sharing and file copy/paste can follow. Direct drag-and-drop between a host application and an arbitrary guest Explorer folder is a separate later integration.

For file clipboard data, local destination files must exist before exposing their paths. Microsoft's [Shell clipboard formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard) and [Shell drag-and-drop model](https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop) provide the general model; original Windows 95 API availability and behavior still require guest tests.

Optional folder synchronization can reuse the transfer engine later. It needs a confirmed common baseline, content identity and explicit conflict handling; FAT timestamps alone are insufficient. It is not part of the first import/export release.

## Acceptance

Test installation, startup, upgrade and uninstall in fresh private Windows 95 branches. Verify that C:\Share is created without losing existing contents and that uninstall preserves those files.

Prove import/export in both directions with empty, small and large files, nested folders, long names and guest code pages. Independently compare complete destination contents. Exercise collisions, locked files, read-only media, space exhaustion, partial I/O, cancellation and reconnects.

Capture and restore the full VM during transfers, then verify both guest state and allowed host-side results. Include reset/shutdown/publication races and normal repeated Windows starts on the required 100/133-MHz profiles.

The existing [Windows storage proof](WINDOWS95-STORAGE.md) provides real Win32/VFAT evidence, but it does not implement this connection, installer or transfer UI. Host endpoints, clipboard integration and folder access have not been activated by this design. The separately deferred native mouse/focus/GL acceptance remains unchanged.
