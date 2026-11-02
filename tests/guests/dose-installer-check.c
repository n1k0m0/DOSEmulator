/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Runs only inside a Win9x integration-test guest. Writes independent API results. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "expected.h"
#define REPORT "C:\\PKGTEST.INI"
#define HOME "C:\\DOSETOOLS"
static char Buffer[2048],PathName[MAX_PATH],Value[1024];
static BYTE Data[1024];
static int Bad;
#pragma function(memset)
void *__cdecl memset(void *p,int value,size_t count) { volatile BYTE *b=(volatile BYTE *)p;while(count--)*b++=(BYTE)value;return p; }
static void Put(const char *section,const char *key,DWORD value)
{
    wsprintfA(Buffer,"%lu",value); WritePrivateProfileStringA(section,key,Buffer,REPORT);
}
static DWORD Number(const char *section,const char *key) { return GetPrivateProfileIntA(section,key,0,REPORT); }
static DWORD HashBytes(const BYTE *bytes,DWORD count,DWORD hash)
{
    DWORD i;for(i=0;i<count;i++)hash=(hash^bytes[i])*16777619UL;return hash;
}
static DWORD HashFile(const char *path,DWORD *length)
{
    DWORD hash=2166136261UL,count;
    HANDLE file=CreateFileA(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,0,OPEN_EXISTING,0,0);
    *length=0;
    if(file==INVALID_HANDLE_VALUE)return 0;
    while(ReadFile(file,Data,sizeof(Data),&count,0)&&count) { hash=HashBytes(Data,count,hash);*length+=count; }
    CloseHandle(file);return hash;
}
static DWORD HashStartup(DWORD *type,DWORD *size)
{
    HKEY key; LONG result;
    *type=0;*size=sizeof(Value);
    result=RegOpenKeyExA(HKEY_LOCAL_MACHINE,"Software\\Microsoft\\Windows\\CurrentVersion\\Run",0,KEY_QUERY_VALUE,&key);
    if(!result) { result=RegQueryValueExA(key,"DOSE Time",0,type,(BYTE *)Value,size);RegCloseKey(key); }
    if(result) { *type=0;*size=0;return 0; }
    return HashBytes((BYTE *)Value,*size,2166136261UL);
}
static void Check(const char *section,const char *key,int pass)
{
    Put(section,key,pass?1:0);if(!pass)Bad++;
}
static int Has(const char *text)
{
    const char *at=GetCommandLineA();int i,n=lstrlenA(text);
    while(*at) { for(i=0;i<n&&at[i]==text[i];i++);if(i==n)return 1;++at; }return 0;
}
static void Seed(void)
{
    DWORD length,hash,type,size,written;HANDLE file;
    CreateDirectoryA("C:\\Share",0);
    file=CreateFileA("C:\\Share\\PKGKEEP.TST",GENERIC_WRITE,0,0,CREATE_NEW,0,0);
    if(file!=INVALID_HANDLE_VALUE) { WriteFile(file,"KEEP MY FILE",12,&written,0);FlushFileBuffers(file);CloseHandle(file); }
    GetSystemDirectoryA(PathName,sizeof(PathName));lstrcatA(PathName,"\\GLIDE2X.DLL");
    hash=HashFile(PathName,&length);Put("Before","GlideHash",hash);Put("Before","GlideBytes",length);
    Put("Before","GlideAttributes",GetFileAttributesA(PathName));
    hash=HashFile(HOME "\\DOSETIME.EXE",&length);Put("Before","TimeHash",hash);Put("Before","TimeBytes",length);
    Put("Before","TimeAttributes",GetFileAttributesA(HOME "\\DOSETIME.EXE"));
    hash=HashStartup(&type,&size);Put("Before","StartupHash",hash);Put("Before","StartupType",type);Put("Before","StartupSize",size);
    Put("Before","Seeded",1);
}
static void Installed(const char *section)
{
    DWORD length,hash,type,size;HMODULE glide;FARPROC version;char text[80];HKEY key=0;
    GetSystemDirectoryA(PathName,sizeof(PathName));lstrcatA(PathName,"\\GLIDE2X.DLL");
    hash=HashFile(PathName,&length);Check(section,"GlideExact",hash==EXPECT_GLIDE_HASH&&length==EXPECT_GLIDE_BYTES);
    hash=HashFile(HOME "\\DOSETIME.EXE",&length);Check(section,"TimeExact",hash==EXPECT_TIME_HASH&&length==EXPECT_TIME_BYTES);
    hash=HashFile(HOME "\\MGLIDE.EXE",&length);Check(section,"LauncherExact",hash==EXPECT_LAUNCH_HASH&&length==EXPECT_LAUNCH_BYTES);
    hash=HashFile("C:\\Share\\PKGKEEP.TST",&length);Check(section,"SharePreserved",hash==EXPECT_KEEP_HASH&&length==12);
    HashStartup(&type,&size);Check(section,"StartupRegistered",type==REG_SZ&&lstrcmpA(Value,"\"" HOME "\\DOSETIME.EXE\" /background")==0);
    Check(section,"ClockRunning",FindWindowA("DOSE.Tools.TimeSynchronization.1",0)!=0);
    Check(section,"AddRemovePrograms",RegOpenKeyExA(HKEY_LOCAL_MACHINE,"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DOSEGuestTools",0,KEY_READ,&key)==ERROR_SUCCESS);
    if(key)RegCloseKey(key);
    Check(section,"APMFiles",GetFileAttributesA(HOME "\\APM\\APM" TARGET_TEXT ".INF")!=INVALID_FILE_ATTRIBUTES);
    Check(section,"MaintenanceWritable",(GetFileAttributesA(HOME "\\DSETUP.EXE")&FILE_ATTRIBUTE_READONLY)==0);
    glide=LoadLibraryA(PathName);version=glide?GetProcAddress(glide,"_grGlideGetVersion@4"):0;
    if(version)((void (__stdcall *)(char *))version)(text);
    Check(section,"MiniGlideVersion",version&&lstrcmpA(text,"2.43 MiniGlide 1.0")==0);
    if(glide)FreeLibrary(glide);
    Put(section,"Failures",Bad);
}
static void Removed(void)
{
    DWORD hash,length,type,size;HKEY key=0;LONG result;
    GetSystemDirectoryA(PathName,sizeof(PathName));lstrcatA(PathName,"\\GLIDE2X.DLL");
    hash=HashFile(PathName,&length);Check("Removed","GlideRestored",hash==Number("Before","GlideHash")&&length==Number("Before","GlideBytes"));
    Check("Removed","GlideAttributes",GetFileAttributesA(PathName)==Number("Before","GlideAttributes"));
    hash=HashFile(HOME "\\DOSETIME.EXE",&length);Check("Removed","TimeRestored",hash==Number("Before","TimeHash")&&length==Number("Before","TimeBytes"));
    Check("Removed","TimeAttributes",GetFileAttributesA(HOME "\\DOSETIME.EXE")==Number("Before","TimeAttributes"));
    hash=HashStartup(&type,&size);Check("Removed","StartupRestored",hash==Number("Before","StartupHash")&&type==Number("Before","StartupType")&&size==Number("Before","StartupSize"));
    hash=HashFile("C:\\Share\\PKGKEEP.TST",&length);Check("Removed","SharePreserved",hash==EXPECT_KEEP_HASH&&length==12);
    result=RegOpenKeyExA(HKEY_LOCAL_MACHINE,"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DOSEGuestTools",0,KEY_READ,&key);
    Check("Removed","RegistrationRemoved",result==ERROR_FILE_NOT_FOUND);if(!result)RegCloseKey(key);
    Put("Removed","Failures",Bad);
}
static void WrongVersion(void)
{
    STARTUPINFOA startup;PROCESS_INFORMATION process;DWORD result=0;
    memset(&startup,0,sizeof(startup));memset(&process,0,sizeof(process));startup.cb=sizeof(startup);
    lstrcpyA(Buffer,"D:\\OTHER.EXE /S");
    if(CreateProcessA(0,Buffer,0,0,FALSE,0,0,0,&startup,&process)) {
        WaitForSingleObject(process.hProcess,30000);GetExitCodeProcess(process.hProcess,&result);
        CloseHandle(process.hThread);CloseHandle(process.hProcess);
    }
    Check("WrongVersion","Refused",result==2);Put("WrongVersion","ExitCode",result);
}
static void ToolsOnly(void)
{
    STARTUPINFOA startup;PROCESS_INFORMATION process;DWORD result=0;
    memset(&startup,0,sizeof(startup));memset(&process,0,sizeof(process));startup.cb=sizeof(startup);
    lstrcpyA(Buffer,"D:\\SETUP.EXE /S /tools-only /no-startup");
    if(CreateProcessA(0,Buffer,0,0,FALSE,0,0,0,&startup,&process)) {
        WaitForSingleObject(process.hProcess,30000);GetExitCodeProcess(process.hProcess,&result);
        CloseHandle(process.hThread);CloseHandle(process.hProcess);
    }
    Check("ToolsOnly","Installed",result==0);
}
static void CheckToolsOnly(void)
{
    DWORD hash,length,type,size;
    GetSystemDirectoryA(PathName,sizeof(PathName));lstrcatA(PathName,"\\GLIDE2X.DLL");
    hash=HashFile(PathName,&length);
    Check("ToolsOnly","GlideUntouched",hash==Number("Before","GlideHash")&&length==Number("Before","GlideBytes"));
    hash=HashFile(HOME "\\DOSETIME.EXE",&length);
    Check("ToolsOnly","TimeExact",hash==EXPECT_TIME_HASH&&length==EXPECT_TIME_BYTES);
    hash=HashStartup(&type,&size);Check("ToolsOnly","StartupDisabled",hash==0&&size==0);
    Check("ToolsOnly","ClockRunning",FindWindowA("DOSE.Tools.TimeSynchronization.1",0)!=0);
    Check("ToolsOnly","MaintenanceWritable",(GetFileAttributesA(HOME "\\DSETUP.EXE")&FILE_ATTRIBUTE_READONLY)==0);
    Put("ToolsOnly","Failures",Bad);
}
void __stdcall CheckEntry(void)
{
    HWND window;
    if(Has("/seed"))Seed();
    else if(Has("/readonly-maintenance"))SetFileAttributesA(HOME "\\DSETUP.EXE",FILE_ATTRIBUTE_READONLY|FILE_ATTRIBUTE_ARCHIVE);
    else if(Has("/tools-only-install"))ToolsOnly();
    else if(Has("/tools-only-check"))CheckToolsOnly();
    else if(Has("/install-ui")) {
        window=FindWindowA("#32770","DOSE Tools and MiniGlide Setup - Windows " TARGET_TEXT);
        if(window) { CheckDlgButton(window,104,BST_CHECKED);PostMessageA(window,WM_COMMAND,IDOK,0); }
    } else if(Has("/confirm-remove")) {
        window=FindWindowA("#32770","Remove DOSE Tools");if(window)PostMessageA(window,WM_COMMAND,IDYES,0);
    } else if(Has("/close-ui")) {
        window=FindWindowA("#32770","DOSE Tools and MiniGlide Setup - Windows " TARGET_TEXT);
        if(window)PostMessageA(window,WM_COMMAND,IDCANCEL,0);
    } else if(Has("/wrong"))WrongVersion();
    else if(Has("/reboot-check"))Installed("Reboot");
    else if(Has("/installed"))Installed("Installed");
    else if(Has("/removed"))Removed();
    else if(Has("/shutdown"))ExitWindowsEx(EWX_SHUTDOWN,0);
    WritePrivateProfileStringA(0,0,0,REPORT);
    ExitProcess(Bad?1:0);
}
