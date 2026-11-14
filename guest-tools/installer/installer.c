/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Classic ANSI Win32 only. Built for IA32, without a C runtime. */
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <shlobj.h>

#define HOME "C:\\DOSETOOLS"
#define STATE HOME "\\DOSEPKG.INI"
#define BACKUP HOME "\\BACKUP"
#define RUNKEY "Software\\Microsoft\\Windows\\CurrentVersion\\Run"
#define UNKEY "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DOSEGuestTools"
#define PRODUCT "DOSE Guest Tools"

static HINSTANCE Instance;
static HWND Dialog;
static char System[MAX_PATH], Menu[MAX_PATH], Self[MAX_PATH];
static char ErrorText[512], Text[1024], Hex[2049];
static BYTE Bytes[1024];
static int Quiet, TimeSelected=1, StartupSelected=1, GlideSelected=1, LauncherSelected=1, ApmSelected;

#pragma function(memset)
void *__cdecl memset(void *p, int value, size_t count)
{
    volatile BYTE *at=(volatile BYTE *)p;
    while(count--) *at++=(BYTE)value;
    return p;
}

static int Same(const char *a, const char *b) { return lstrcmpA(a,b)==0; }
static int Exists(const char *path) { return GetFileAttributesA(path)!=INVALID_FILE_ATTRIBUTES; }
static int SamePath(const char *a,const char *b)
{
    char first[MAX_PATH],second[MAX_PATH];
    if(GetShortPathNameA(a,first,sizeof(first)) && GetShortPathNameA(b,second,sizeof(second)))
        return lstrcmpiA(first,second)==0;
    return lstrcmpiA(a,b)==0;
}
static int Path(char *out, const char *base, const char *suffix)
{
    if(lstrlenA(base)+lstrlenA(suffix)>=MAX_PATH) return 0;
    lstrcpyA(out,base); lstrcatA(out,suffix); return 1;
}
static int Fail(const char *message, const char *path)
{
    lstrcpynA(ErrorText,message,250);
    if(path) { lstrcatA(ErrorText,"\r\n"); lstrcpynA(ErrorText+lstrlenA(ErrorText),path,MAX_PATH); }
    return 0;
}
static int Put(const char *section,const char *key,const char *value)
{
    return WritePrivateProfileStringA(section,key,value,STATE) || Fail("Could not save the installation record.",STATE);
}
static void Get(const char *section,const char *key,char *value,DWORD size)
{
    GetPrivateProfileStringA(section,key,"",value,size,STATE);
}
static int Recorded(const char *key)
{
    char value[8]; Get("Originals",key,value,sizeof(value)); return value[0]!=0;
}
static int Backup(const char *target,const char *tag)
{
    char name[MAX_PATH]; DWORD attributes;
    if(Recorded(tag)) return 1;
    if(!Exists(target)) return Put("Originals",tag,"0");
    attributes=GetFileAttributesA(target);
    Path(name,BACKUP,"\\"); lstrcatA(name,tag); lstrcatA(name,".BAK");
    if(!CopyFileA(target,name,TRUE)) return Fail("Could not back up this file. Close applications using it.",target);
    wsprintfA(Text,"%lu",attributes);
    return Put("Attributes",tag,Text) && Put("Originals",tag,"1");
}
static int WriteBytes(const char *target,const void *data,DWORD length)
{
    DWORD written=0,attributes=GetFileAttributesA(target); int okay;
    if(attributes!=INVALID_FILE_ATTRIBUTES && (attributes&FILE_ATTRIBUTE_READONLY)) {
        attributes&=~FILE_ATTRIBUTE_READONLY;
        if(!SetFileAttributesA(target,attributes?attributes:FILE_ATTRIBUTE_NORMAL))
            return Fail("Could not clear the read-only attribute for installation.",target);
    }
    HANDLE file=CreateFileA(target,GENERIC_WRITE,0,0,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,0);
    if(file==INVALID_HANDLE_VALUE) return Fail("Could not write this file. Close applications using it.",target);
    okay=WriteFile(file,data,length,&written,0) && written==length && FlushFileBuffers(file);
    CloseHandle(file);
    return okay || Fail("Could not finish writing this file. Backups have been retained.",target);
}
static int Extract(int id,const char *target,const char *tag)
{
    HRSRC resource=FindResourceA(Instance,MAKEINTRESOURCEA(id),RT_RCDATA);
    HGLOBAL loaded=resource?LoadResource(Instance,resource):0;
    const void *data=loaded?LockResource(loaded):0;
    DWORD size=resource?SizeofResource(Instance,resource):0;
    if(!data || !size) return Fail("The installer payload is incomplete.",target);
    return Backup(target,tag) && WriteBytes(target,data,size);
}
static int RegText(const char *key,const char *name,const char *value)
{
    HKEY handle; LONG result=RegCreateKeyExA(HKEY_LOCAL_MACHINE,key,0,0,0,KEY_SET_VALUE,0,&handle,0);
    if(result==ERROR_SUCCESS) {
        result=RegSetValueExA(handle,name,0,REG_SZ,(const BYTE *)value,lstrlenA(value)+1);
        RegCloseKey(handle);
    }
    return result==ERROR_SUCCESS || Fail("Could not write the Windows registration.",key);
}
static int BackupStartup(void)
{
    static const char digits[]="0123456789ABCDEF";
    HKEY key; DWORD type=0,size=sizeof(Bytes),i; LONG result;
    Get("Startup","Saved",Text,sizeof(Text)); if(Same(Text,"1")) return 1;
    result=RegOpenKeyExA(HKEY_LOCAL_MACHINE,RUNKEY,0,KEY_QUERY_VALUE,&key);
    if(result==ERROR_SUCCESS) {
        result=RegQueryValueExA(key,"DOSE Time",0,&type,Bytes,&size); RegCloseKey(key);
    }
    if(result==ERROR_FILE_NOT_FOUND) return Put("Startup","Existed","0") && Put("Startup","Saved","1");
    if(result!=ERROR_SUCCESS || size>sizeof(Bytes)) return Fail("Could not preserve the previous clock startup setting.",0);
    for(i=0;i<size;i++) { Hex[i*2]=digits[Bytes[i]>>4]; Hex[i*2+1]=digits[Bytes[i]&15]; }
    Hex[size*2]=0;
    wsprintfA(Text,"%lu",type);
    return Put("Startup","Type",Text) && Put("Startup","Data",Hex) &&
        Put("Startup","Existed","1") && Put("Startup","Saved","1");
}
static void StopClock(void)
{
    int tries;
    for(tries=0;tries<20;tries++) {
        HWND clock=FindWindowA("DOSE.Tools.TimeSynchronization.1",0);
        if(!clock) return;
        PostMessageA(clock,WM_CLOSE,0,0); Sleep(100);
    }
}
static void StartClock(void)
{
    WinExec("\"" HOME "\\DOSETIME.EXE\" /background",SW_HIDE);
}
static int Shortcut(const char *name,const char *target,const char *arguments,const char *tag)
{
    char filename[MAX_PATH]; WCHAR wide[MAX_PATH];
    IShellLinkA *link=0; IPersistFile *persist=0; HRESULT result;
    Path(filename,Menu,"\\"); lstrcatA(filename,name); lstrcatA(filename,".lnk");
    if(!Backup(filename,tag)) return 0;
    result=CoCreateInstance(&CLSID_ShellLink,0,CLSCTX_INPROC_SERVER,&IID_IShellLinkA,(void **)&link);
    if(SUCCEEDED(result)) {
        result=IShellLinkA_SetPath(link,target);
        if(SUCCEEDED(result)) result=IShellLinkA_SetArguments(link,arguments?arguments:"");
        if(SUCCEEDED(result)) result=IShellLinkA_SetWorkingDirectory(link,HOME);
        if(SUCCEEDED(result)) result=IShellLinkA_QueryInterface(link,&IID_IPersistFile,(void **)&persist);
        if(SUCCEEDED(result)) {
            MultiByteToWideChar(CP_ACP,0,filename,-1,wide,MAX_PATH);
            result=IPersistFile_Save(persist,wide,TRUE); IPersistFile_Release(persist);
        }
        IShellLinkA_Release(link);
    }
    return SUCCEEDED(result) || Fail("Could not create this Start menu shortcut.",filename);
}
static int Prepare(void)
{
    HKEY key; DWORD size=sizeof(Menu),type;
    CreateDirectoryA(HOME,0); CreateDirectoryA(BACKUP,0);
    if(!Exists(BACKUP)) return Fail("Could not create the tools and backup directories.",HOME);
    Get("Package","Product",Text,sizeof(Text));
    if(Text[0] && !Same(Text,PRODUCT)) return Fail("An unrelated installation record already exists.",STATE);
    if(!Text[0] && Exists(HOME "\\DSETUP.EXE")) return Fail("An unrelated maintenance program already exists. Rename it before installation.",HOME "\\DSETUP.EXE");
    if(!Put("Package","Product",PRODUCT)) return 0;
    if(!Put("Package","Removed","0")) return 0;
    if(RegOpenKeyExA(HKEY_CURRENT_USER,"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders",0,KEY_QUERY_VALUE,&key)!=ERROR_SUCCESS)
        return Fail("Could not locate the Windows Programs folder.",0);
    type=REG_SZ;
    if(RegQueryValueExA(key,"Programs",0,&type,(BYTE *)Menu,&size)!=ERROR_SUCCESS) { RegCloseKey(key); return Fail("Could not read the Programs folder.",0); }
    RegCloseKey(key);
    if(lstrlenA(Menu)>MAX_PATH-80) return Fail("The Programs folder path is too long.",0);
    lstrcatA(Menu,"\\DOSE Tools"); CreateDirectoryA(Menu,0);
    if(!Put("Package","Menu",Menu)) return 0;
    if(!SamePath(Self,HOME "\\DSETUP.EXE")) {
        if(Exists(HOME "\\DSETUP.EXE")) SetFileAttributesA(HOME "\\DSETUP.EXE",FILE_ATTRIBUTE_NORMAL);
        if(!CopyFileA(Self,HOME "\\DSETUP.EXE",FALSE)) return Fail("Could not install the maintenance program.",HOME "\\DSETUP.EXE");
        SetFileAttributesA(HOME "\\DSETUP.EXE",FILE_ATTRIBUTE_NORMAL);
    }
    if(!Extract(13,HOME "\\DOSEHELP.TXT","HELP") || !Extract(16,HOME "\\LICENSE.TXT","LICENSE")) return 0;
    if(!RegText(UNKEY,"DisplayName","DOSE Tools and MiniGlide (Windows " TARGET_TEXT ")") ||
       !RegText(UNKEY,"DisplayVersion","1.0") || !RegText(UNKEY,"Publisher","DOSE") ||
       !RegText(UNKEY,"UninstallString","\"" HOME "\\DSETUP.EXE\" /uninstall")) return 0;
    return Shortcut("Read Me",HOME "\\DOSEHELP.TXT",0,"SHHELP") &&
        Shortcut("Uninstall",HOME "\\DSETUP.EXE","/uninstall","SHREMOVE");
}
static void Status(const char *text)
{
    if(Dialog) { SetDlgItemTextA(Dialog,105,text); UpdateWindow(Dialog); }
}
static int Install(void)
{
    char file[MAX_PATH]; HKEY key;
    if(!TimeSelected && !GlideSelected && !LauncherSelected && !ApmSelected) return Fail("Select at least one component.",0);
    Status("Preparing installation and backups...");
    if(!Prepare()) return 0;
    if(TimeSelected) {
        Status("Installing DOSE Tools..."); StopClock();
        if(!BackupStartup() || !Extract(10,HOME "\\DOSETIME.EXE","TIME")) return 0;
        CreateDirectoryA("C:\\Share",0);
        if(!Exists("C:\\Share") || !(GetFileAttributesA("C:\\Share")&FILE_ATTRIBUTE_DIRECTORY)) return Fail("Could not create the Share folder.","C:\\Share");
        if(StartupSelected) { if(!RegText(RUNKEY,"DOSE Time","\"" HOME "\\DOSETIME.EXE\" /background")) return 0; }
        else if(RegOpenKeyExA(HKEY_LOCAL_MACHINE,RUNKEY,0,KEY_SET_VALUE,&key)==ERROR_SUCCESS) {
            RegDeleteValueA(key,"DOSE Time"); RegCloseKey(key);
        }
        if(!Put("Installed","Time","1") || !Put("Installed","Startup",StartupSelected?"1":"0") ||
           !Shortcut("Start Clock Synchronization",HOME "\\DOSETIME.EXE","/background","SHCLOCK") ||
           !Shortcut("Share Folder","C:\\Share",0,"SHSHARE")) return 0;
        if(!Extract(17,HOME "\\DOSEIMPORT.EXE","IMPORT") || !Put("Installed","Import","1") ||
           !Shortcut("Import File from DOSE","C:\\DOSETOOLS\\DOSEIMPORT.EXE",0,"SHIMPORT")) return 0;
    }
    if(GlideSelected) {
        Status("Installing MiniGlide..."); Path(file,System,"\\GLIDE2X.DLL");
        if(!Extract(11,file,"GLIDE") || !Put("Installed","Glide","1")) return 0;
    }
    if(LauncherSelected) {
        if(!Extract(12,HOME "\\MGLIDE.EXE","LAUNCH") || !Put("Installed","Launcher","1") ||
           !Shortcut("MiniGlide Preview",HOME "\\MGLIDE.EXE","/preview","SHPREVIEW") ||
           !Shortcut("Launch Glide Program",HOME "\\MGLIDE.EXE","/choose","SHCHOOSE")) return 0;
        if(Exists("C:\\UNREAL\\SYSTEM\\UNREAL.EXE") && !Shortcut("Unreal with MiniGlide",HOME "\\MGLIDE.EXE",0,"SHUNREAL")) return 0;
        if(Exists("C:\\PROGRA~1\\DIGITA~1\\MOTORH~1\\MOTOR.EXE") &&
           !Shortcut("Motorhead with MiniGlide",HOME "\\MGLIDE.EXE","\"C:\\PROGRA~1\\DIGITA~1\\MOTORH~1\\MOTOR.EXE\"","SHMOTOR")) return 0;
    }
    if(ApmSelected) {
        Status("Preparing the manual APM installation files..."); CreateDirectoryA(HOME "\\APM",0);
        if(!Extract(14,HOME "\\APM\\APM" TARGET_TEXT ".INF","APM") ||
           !Extract(15,HOME "\\APM\\README.TXT","APMHELP") || !Put("Installed","APM","1") ||
           !Shortcut("APM Installation Guide",HOME "\\APM\\README.TXT",0,"SHAPM")) return 0;
    }
    WritePrivateProfileStringA(0,0,0,STATE);
    if(TimeSelected) StartClock();
    Status("Installed. Open Start > Programs > DOSE Tools. The CD may be removed.");
    return 1;
}
static int Restore(const char *target,const char *tag)
{
    char original[8],name[MAX_PATH];
    Get("Originals",tag,original,sizeof(original)); if(!original[0]) return 1;
    Path(name,BACKUP,"\\"); lstrcatA(name,tag); lstrcatA(name,".LAST");
    if(Exists(target) && !CopyFileA(target,name,FALSE)) return Fail("Could not preserve the current file. Close applications using it.",target);
    if(original[0]=='1') {
        Path(name,BACKUP,"\\"); lstrcatA(name,tag); lstrcatA(name,".BAK");
        if(Exists(target)) SetFileAttributesA(target,FILE_ATTRIBUTE_NORMAL);
        if(!CopyFileA(name,target,FALSE)) return Fail("Could not restore the previous file. Backups have been retained.",target);
        if(!SetFileAttributesA(target,GetPrivateProfileIntA("Attributes",tag,FILE_ATTRIBUTE_NORMAL,STATE)))
            return Fail("Could not restore the previous file attributes.",target);
    } else if(Exists(target) && !DeleteFileA(target)) return Fail("Could not remove this file. Close applications using it.",target);
    return 1;
}
static int RestoreShortcut(const char *name,const char *tag)
{
    char path[MAX_PATH]; Path(path,Menu,"\\"); lstrcatA(path,name); lstrcatA(path,".lnk"); return Restore(path,tag);
}
static int Nibble(char c) { return c>='A'?c-'A'+10:c-'0'; }
static int Remove(void)
{
    char file[MAX_PATH]; HKEY key; DWORD type,size,i; LONG result;
    Get("Package","Product",Text,sizeof(Text));
    if(!Same(Text,PRODUCT)) return Fail("No DOSE Tools installation record was found. No files were removed.",0);
    Status("Restoring pre-install files...");
    if(Recorded("TIME")) StopClock();
    Get("Package","Menu",Menu,sizeof(Menu));
    Path(file,System,"\\GLIDE2X.DLL");
    if(!Restore(file,"GLIDE") || !Restore(HOME "\\MGLIDE.EXE","LAUNCH") ||
       !Restore(HOME "\\DOSETIME.EXE","TIME") || !Restore(HOME "\\DOSEIMPORT.EXE","IMPORT") ||
       !Restore(HOME "\\APM\\APM" TARGET_TEXT ".INF","APM") ||
       !Restore(HOME "\\APM\\README.TXT","APMHELP") || !Restore(HOME "\\DOSEHELP.TXT","HELP") ||
       !Restore(HOME "\\LICENSE.TXT","LICENSE")) return 0;
    if(!RestoreShortcut("Read Me","SHHELP") || !RestoreShortcut("Uninstall","SHREMOVE") ||
       !RestoreShortcut("Start Clock Synchronization","SHCLOCK") || !RestoreShortcut("Share Folder","SHSHARE") ||
       !RestoreShortcut("Import File from DOSE","SHIMPORT") ||
       !RestoreShortcut("MiniGlide Preview","SHPREVIEW") || !RestoreShortcut("Unreal with MiniGlide","SHUNREAL") ||
       !RestoreShortcut("APM Installation Guide","SHAPM") || !RestoreShortcut("Launch Glide Program","SHCHOOSE") ||
       !RestoreShortcut("Motorhead with MiniGlide","SHMOTOR")) return 0;
    Get("Startup","Saved",Text,sizeof(Text));
    if(Same(Text,"1")) {
        result=RegCreateKeyExA(HKEY_LOCAL_MACHINE,RUNKEY,0,0,0,KEY_SET_VALUE,0,&key,0);
        if(result!=ERROR_SUCCESS) return Fail("Could not restore the previous clock startup setting.",0);
        Get("Startup","Existed",Text,sizeof(Text));
        if(Same(Text,"1")) {
            Get("Startup","Data",Hex,sizeof(Hex)); size=(DWORD)lstrlenA(Hex)/2;
            type=GetPrivateProfileIntA("Startup","Type",REG_SZ,STATE);
            for(i=0;i<size;i++) Bytes[i]=(BYTE)((Nibble(Hex[i*2])<<4)|Nibble(Hex[i*2+1]));
            result=RegSetValueExA(key,"DOSE Time",0,type,Bytes,size);
        } else result=RegDeleteValueA(key,"DOSE Time");
        RegCloseKey(key);
        if(result!=ERROR_SUCCESS && result!=ERROR_FILE_NOT_FOUND) return Fail("Could not restore the clock startup setting.",0);
        if(Same(Text,"1") && Exists(HOME "\\DOSETIME.EXE")) StartClock();
    }
    RegDeleteKeyA(HKEY_LOCAL_MACHINE,UNKEY);
    RemoveDirectoryA(Menu); RemoveDirectoryA(HOME "\\APM");
    /* Keep the maintenance EXE and first backups for recovery and reinstallation. */
    Put("Package","Removed","1");
    Status("Removed. Share, backups and the maintenance tool are kept.");
    return 1;
}
static int HasSwitch(const char *value)
{
    const char *text=GetCommandLineA(); int n=lstrlenA(value),i;
    while(*text) {
        if(*text==' ' || *text=='\t') {
            ++text; for(i=0;i<n && text[i];i++) {
                char actual=text[i],expected=value[i];
                if(actual>='a' && actual<='z') actual=(char)(actual-('a'-'A'));
                if(expected>='a' && expected<='z') expected=(char)(expected-('a'-'A'));
                if(actual!=expected) break;
            }
            if(i==n && (text[i]==0 || text[i]==' ' || text[i]=='\t')) return 1;
        } else ++text;
    }
    return 0;
}
static INT_PTR CALLBACK SetupDialog(HWND window,UINT message,WPARAM wparam,LPARAM lparam)
{
    int okay;
    (void)lparam;
    if(message==WM_INITDIALOG) {
        Dialog=window; SetWindowTextA(window,"DOSE Tools and MiniGlide Setup - Windows " TARGET_TEXT);
        CheckDlgButton(window,101,BST_CHECKED); CheckDlgButton(window,102,BST_CHECKED); CheckDlgButton(window,103,BST_CHECKED);
        CheckDlgButton(window,107,BST_CHECKED);
        Get("Package","Product",Text,sizeof(Text));
        EnableWindow(GetDlgItem(window,106),Same(Text,PRODUCT) && !GetPrivateProfileIntA("Package","Removed",0,STATE));
        if(HasSwitch("/uninstall")) PostMessageA(window,WM_COMMAND,106,0);
        return TRUE;
    }
    if(message==WM_CLOSE) { EndDialog(window,0); return TRUE; }
    if(message!=WM_COMMAND) return FALSE;
    if(LOWORD(wparam)==IDCANCEL) { EndDialog(window,0); return TRUE; }
    if(LOWORD(wparam)==101) {
        EnableWindow(GetDlgItem(window,102),IsDlgButtonChecked(window,101)==BST_CHECKED); return TRUE;
    }
    if(LOWORD(wparam)!=IDOK && LOWORD(wparam)!=106) return FALSE;
    if(LOWORD(wparam)==106 && MessageBoxA(window,"Remove the installed components and restore their pre-install files?\r\nC:\\Share and backups will be kept.","Remove DOSE Tools",MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION)!=IDYES) return TRUE;
    EnableWindow(GetDlgItem(window,IDOK),FALSE); EnableWindow(GetDlgItem(window,106),FALSE);
    SetCursor(LoadCursorA(0,IDC_WAIT));
    TimeSelected=IsDlgButtonChecked(window,101)==BST_CHECKED;
    StartupSelected=IsDlgButtonChecked(window,102)==BST_CHECKED;
    GlideSelected=IsDlgButtonChecked(window,103)==BST_CHECKED;
    LauncherSelected=IsDlgButtonChecked(window,107)==BST_CHECKED;
    ApmSelected=IsDlgButtonChecked(window,104)==BST_CHECKED;
    okay=LOWORD(wparam)==106?Remove():Install();
    SetCursor(LoadCursorA(0,IDC_ARROW));
    EnableWindow(GetDlgItem(window,IDOK),TRUE);
    Get("Package","Product",Text,sizeof(Text));
    EnableWindow(GetDlgItem(window,106),Same(Text,PRODUCT) && !GetPrivateProfileIntA("Package","Removed",0,STATE));
    if(!okay) { Status("Operation incomplete. Original backups have been retained."); MessageBoxA(window,ErrorText,"DOSE Tools",MB_OK|MB_ICONSTOP); }
    return TRUE;
}
void __stdcall SetupEntry(void)
{
    DWORD version=GetVersion(); int result;
    Instance=GetModuleHandleA(0); Quiet=HasSwitch("/S");
    if(!(version&0x80000000) || LOBYTE(LOWORD(version))!=4 || HIBYTE(LOWORD(version))!=TARGET_MINOR) {
        if(!Quiet) MessageBoxA(0,"This installer is for Windows " TARGET_TEXT " only. Run the matching package inside the Windows guest.","DOSE Tools Setup",MB_OK|MB_ICONSTOP);
        ExitProcess(2);
    }
    GetSystemDirectoryA(System,sizeof(System)); GetModuleFileNameA(0,Self,sizeof(Self));
    CoInitialize(0);
    if(Quiet) {
        ApmSelected=HasSwitch("/apm");
        if(HasSwitch("/tools-only")) { GlideSelected=0; LauncherSelected=0; }
        if(HasSwitch("/launcher-only")) { TimeSelected=0; StartupSelected=0; GlideSelected=0; ApmSelected=0; }
        if(HasSwitch("/no-startup")) StartupSelected=0;
        result=HasSwitch("/uninstall")?Remove():Install();
    } else result=DialogBoxParamA(Instance,MAKEINTRESOURCEA(100),0,SetupDialog,0)!=-1;
    CoUninitialize(); ExitProcess(result?0:1);
}
