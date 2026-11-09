# DOSE Windows installer and updates

The Windows x64 installer is built with `scripts/build-installer.ps1` and Inno Setup 6. The numeric version is read from `VERSION`; the published GUI assembly and setup must report that version. The release asset is named `DOSEmulator-<version>-win-x64-setup.exe` and its GitHub tag is `v<version>`.

The application source and public installer assets are both published in `n1k0m0/DOSEmulator`. Installed copies query that repository's GitHub Releases without a GitHub token. Do not add VM directories, original disks, CDs, games, snapshots, user settings or private fixtures to the installer.

The installer publishes a self-contained .NET 10 GUI, own embedded firmware, required managed libraries, pinned native NAT/Host-only libraries and their notices. It installs for the current Windows user under `%LOCALAPPDATA%\Programs\DOSEmulator`. VM folders and host settings under `%APPDATA%\DOSEmulator` are outside the installer directory. It does not bundle a VM or original operating system. Optional Npcap and external Glide host libraries remain separate.

The published GUI contains the current `main` WHPX engine, software fallback, MiniGlide presentation and Glide host bridge. The separate compatible Glide-2 host DLL and the guest's own Glide components remain optional external inputs. Portable VM launchers retain their pinned emulator copy until that VM is deliberately upgraded; installing or updating the standalone application does not silently rewrite them.

Only an installer-created `dose-installed.txt` enables the startup check. The check starts in the background after launch, fetches the latest public release, compares numeric versions, asks before downloading, verifies the GitHub asset SHA-256 digest, then starts Setup after the emulator has closed and released media. If GitHub is unavailable, normal startup continues. The updater never stores a GitHub token.

To release a new version: update `VERSION`, run the complete product checks, build the installer in a clean release workspace, inspect its payload and SHA-256 digest, push reviewed source to `main`, then create the corresponding GitHub Release in the same repository with the setup asset. Keep earlier releases available for rollback. Versions 0.1.0 and 0.1.1 checked the former separate release repository, so those installed copies need one manual update to 0.1.2. From 0.1.2 onward, the startup checker uses the main repository.
