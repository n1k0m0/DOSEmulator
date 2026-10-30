DOSE APM CPU Idle Support
========================

This optional hardware INF uses the original Windows power drivers. Their files
are obtained from your Windows setup CD through the installed LAYOUT.INF.
No Microsoft driver binaries are included with DOSE.

It requires a DOSE runtime with the APM CPU-idle BIOS. It is for a Windows
installation that was originally installed without APM support. An existing
working APM installation does not need an additional device.

In Control Panel / Add New Hardware, select hardware manually, choose System
devices, then Have Disk. Choose "DOSE APM support for Windows 95" or "DOSE APM
support for Windows 98" to match the installed Windows version. Keep its original
setup CD available. If Windows asks where POWER.DRV is located, select the CD's
WIN95 or WIN98 directory. Allow the original file copy to finish, then restart
Windows normally. The two INF files are APM95.INF and APM98.INF, respectively.

Idle support lets the original Windows driver halt an idle guest CPU until a
real interrupt. It does not speed up CPU-intensive applications. DOSE's current
APM subset does not implement standby, suspend or power-off; those requests
return an error. Shut Windows down normally, then close DOSE.

Validation status and runtime requirements are documented in
docs/hardware/APM-BIOS.md. Use only the INF matching the installed Windows version.
The thin Windows 95 CD-copy installation is tested on OEM 950a. Windows 98's
original APM drivers are tested; direct installation with the thin APM98.INF is
still experimental. The prepared APM test PCs already have their original
drivers installed and do not need another APM device.
