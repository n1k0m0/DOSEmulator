/* SPDX-License-Identifier: Apache-2.0 */
/* Copyright 2026 Nils Kopal */
/* Explicit, bounded Win9x host-to-guest import over the configured NAT NIC. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock.h>

#pragma function(memcpy)
void *__cdecl memcpy(void *destination,const void *source,size_t length)
{
    BYTE *out=(BYTE *)destination; const BYTE *in=(const BYTE *)source;
    while(length--) *out++=*in++;
    return destination;
}

static BYTE Packet[65536], Header[16];
static char Target[MAX_PATH], Partial[MAX_PATH];
static HWND CodeEdit;
static char CodeInput[20];
static int CodeAccepted;

/* This helper reads a one-time code from its command line and never saves it itself. */
static int HexDigit(char c)
{
    if(c>='0'&&c<='9') return c-'0';
    if(c>='A'&&c<='F') return c-'A'+10;
    if(c>='a'&&c<='f') return c-'a'+10;
    return -1;
}
static int ParseCode(const char *p,BYTE *code)
{
    int i,high,low;
    while(*p==' '||*p=='\t') p++;
    for(i=0;i<8;i++) {
        if(!p[0]||!p[1]) return 0;
        high=HexDigit(p[0]); low=HexDigit(p[1]); p+=2;
        if(high<0||low<0) return 0;
        code[i]=(BYTE)((high<<4)|low);
    }
    while(*p==' '||*p=='\t') p++;
    return *p==0;
}
static int ReadCode(BYTE *code)
{
    const char *p=GetCommandLineA();
    if(*p=='"') { p++; while(*p&&*p!='"') p++; if(*p) p++; }
    else while(*p&&*p!=' '&&*p!='\t') p++;
    while(*p==' '||*p=='\t') p++;
    return *p?ParseCode(p,code):-1;
}
/* The installed Start-menu shortcut opens this guest-only input window. */
static LRESULT CALLBACK CodeWindowProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam)
{
    switch(message) {
    case WM_CREATE:
        CreateWindowExA(0,"STATIC","Enter the one-time code shown in DOSE:",
            WS_CHILD|WS_VISIBLE,18,16,390,20,window,0,GetModuleHandleA(0),0);
        CodeEdit=CreateWindowExA(WS_EX_CLIENTEDGE,"EDIT","",WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_AUTOHSCROLL,
            18,42,390,24,window,(HMENU)101,GetModuleHandleA(0),0);
        SendMessageA(CodeEdit,EM_LIMITTEXT,16,0);
        CreateWindowExA(0,"BUTTON","Import",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_DEFPUSHBUTTON,
            228,78,85,27,window,(HMENU)102,GetModuleHandleA(0),0);
        CreateWindowExA(0,"BUTTON","Cancel",WS_CHILD|WS_VISIBLE|WS_TABSTOP,
            323,78,85,27,window,(HMENU)103,GetModuleHandleA(0),0);
        return 0;
    case WM_COMMAND:
        if(LOWORD(wparam)==102 || LOWORD(wparam)==IDOK) {
            int i;
            GetWindowTextA(CodeEdit,CodeInput,sizeof(CodeInput));
            if(lstrlenA(CodeInput)!=16) {
                MessageBoxA(window,"Enter exactly 16 hexadecimal characters.","DOSE Import",MB_OK|MB_ICONEXCLAMATION);
                SetFocus(CodeEdit); return 0;
            }
            for(i=0;i<16;i++) if(HexDigit(CodeInput[i])<0) {
                MessageBoxA(window,"Use only hexadecimal digits 0-9 and A-F.","DOSE Import",MB_OK|MB_ICONEXCLAMATION);
                SetFocus(CodeEdit); return 0;
            }
            CodeAccepted=1; DestroyWindow(window); return 0;
        }
        if(LOWORD(wparam)==103 || LOWORD(wparam)==IDCANCEL) {
            DestroyWindow(window); return 0;
        }
        break;
    case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcA(window,message,wparam,lparam);
}
static int PromptCode(BYTE *code)
{
    WNDCLASSA kind={0}; MSG message; HWND window;
    kind.lpfnWndProc=CodeWindowProc; kind.hInstance=GetModuleHandleA(0);
    kind.hbrBackground=(HBRUSH)(COLOR_BTNFACE+1); kind.lpszClassName="DOSEImportCode";
    if(!RegisterClassA(&kind)) return 0;
    window=CreateWindowExA(WS_EX_DLGMODALFRAME,"DOSEImportCode","DOSE Import",
        WS_CAPTION|WS_SYSMENU|WS_VISIBLE,CW_USEDEFAULT,CW_USEDEFAULT,440,145,
        0,0,GetModuleHandleA(0),0);
    if(!window) return 0;
    ShowWindow(window,SW_SHOW); UpdateWindow(window);
    SetForegroundWindow(window); SetActiveWindow(window); SetFocus(CodeEdit);
    while(GetMessageA(&message,0,0,0)>0) {
        if(!IsDialogMessageA(window,&message)) { TranslateMessage(&message); DispatchMessageA(&message); }
    }
    return CodeAccepted && ParseCode(CodeInput,code);
}

/* The protocol uses little-endian integers independently of host C layout. */
static DWORD U32(const BYTE *p) { return (DWORD)p[0]|((DWORD)p[1]<<8)|((DWORD)p[2]<<16)|((DWORD)p[3]<<24); }
static WORD U16(const BYTE *p) { return (WORD)(p[0]|((WORD)p[1]<<8)); }
static void Put32(BYTE *p,DWORD n) { p[0]=(BYTE)n;p[1]=(BYTE)(n>>8);p[2]=(BYTE)(n>>16);p[3]=(BYTE)(n>>24); }
/* IEEE CRC-32 checks the entire received file before its guest name is published. */
static DWORD Crc(const BYTE *p,DWORD length)
{
    DWORD crc=0xffffffffUL,i; int bit;
    for(i=0;i<length;i++) { crc^=p[i]; for(bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)?0xedb88320UL:0); }
    return crc^0xffffffffUL;
}
/* Refuse device names, traversal and locale-sensitive host filenames. */
static int SafeName(const BYTE *p,WORD length)
{
    WORD i; char stem[65]; WORD stemLength=0;
    if(length<1 || length>64 || p[0]=='.' || p[length-1]=='.') return 0;
    for(i=0;i<length;i++) {
        BYTE c=p[i];
        if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'||c=='-'||c=='.')) return 0;
        if(c!='.' && stemLength==i) stem[stemLength++]=(char)(c>='a'&&c<='z'?c-('a'-'A'):c);
    }
    stem[stemLength]=0;
    if(!lstrcmpA(stem,"CON")||!lstrcmpA(stem,"PRN")||!lstrcmpA(stem,"AUX")||!lstrcmpA(stem,"NUL")) return 0;
    if(stemLength==4 && ((stem[0]=='C'&&stem[1]=='O'&&stem[2]=='M')||
       (stem[0]=='L'&&stem[1]=='P'&&stem[2]=='T')) && stem[3]>='0'&&stem[3]<='9') return 0;
    return 1;
}
/* Receive exactly one bounded frame, distinguishing EOF from a complete response. */
static int Receive(SOCKET s,BYTE *p,int length)
{
    int done=0,n;
    while(done<length) { n=recv(s,(char *)p+done,length-done,0); if(n<=0) return 0; done+=n; }
    return 1;
}
/* Send one fixed-size request or acknowledgement without exposing guest pointers to the host. */
static int Send(SOCKET s,DWORD opcode,DWORD detail)
{
    BYTE request[16]={'D','O','S','E','T','F','0','2',0,0,0,0,0,0,0,0}; int done=0,n;
    Put32(request+8,opcode); Put32(request+12,detail);
    while(done<16) { n=send(s,(const char *)request+done,16-done,0); if(n<=0) return 0; done+=n; }
    return 1;
}
/* One explicit invocation imports at most one new, fully checked file into C:\Share. */
void WINAPI TransferEntry(void)
{
    WSADATA wsa; SOCKET s=INVALID_SOCKET; struct sockaddr_in address; BYTE code[8];
    DWORD length,fileLength,crc,written,attempt,partialSeed; WORD nameLength; HANDLE output=INVALID_HANDLE_VALUE; int timeout=30000;
    int okay=0,started=0,partial=0,codeStatus; DWORD stage=1,win32Error=0; int winsockError=0;
    char detailMessage[160];
    const char *message="DOSE import failed. Check the connected NAT card and host transfer request.";
    codeStatus=ReadCode(code);
    if(codeStatus<0) codeStatus=PromptCode(code);
    if(!codeStatus) { message="Enter the 16-character import code shown by DOSE."; goto end; }
    if(WSAStartup(0x0101,&wsa)) goto end; started=1;
    stage=2;
    s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP); if(s==INVALID_SOCKET) goto end;
    setsockopt(s,SOL_SOCKET,SO_RCVTIMEO,(const char *)&timeout,sizeof(timeout));
    setsockopt(s,SOL_SOCKET,SO_SNDTIMEO,(const char *)&timeout,sizeof(timeout));
    address.sin_family=AF_INET; address.sin_port=htons(28595); address.sin_addr.s_addr=inet_addr("10.0.2.2");
    if(connect(s,(struct sockaddr *)&address,sizeof(address))==SOCKET_ERROR || !Send(s,1,8)) goto end;
    {
        int done=0,n;
        while(done<8) { n=send(s,(const char *)code+done,8-done,0); if(n<=0) goto end; done+=n; }
    }
    stage=3;
    if(!Receive(s,Header,16) || Header[0]!='D'||Header[1]!='O'||Header[2]!='S'||Header[3]!='E'||
       Header[4]!='T'||Header[5]!='F'||Header[6]!='0'||Header[7]!='2') goto end;
    if(U32(Header+8)==1) { message="No file is queued for this VM."; goto end; }
    if(U32(Header+8)!=0 || (length=U32(Header+12))<11 || length>65536 || !Receive(s,Packet,(int)length)) goto end;
    stage=4;
    nameLength=U16(Packet); fileLength=U32(Packet+2); crc=U32(Packet+6);
    if(fileLength>60000 || length!=10+(DWORD)nameLength+fileLength || !SafeName(Packet+10,nameLength)) goto end;
    stage=5;
    if(!CreateDirectoryA("C:\\Share",0) && GetLastError()!=ERROR_ALREADY_EXISTS) goto end;
    lstrcpyA(Target,"C:\\Share\\");
    {
        WORD i; int prefix=lstrlenA(Target);
        for(i=0;i<nameLength;i++) Target[prefix+i]=(char)Packet[10+i];
        Target[prefix+nameLength]=0;
    }
    if(GetFileAttributesA(Target)!=INVALID_FILE_ATTRIBUTES) { message="The target file already exists in C:\\Share."; goto end; }
    stage=6;
    /* A stale partial from a reset or snapshot must not block a later import. */
    partialSeed=GetTickCount() ^ GetCurrentProcessId();
    for(attempt=0;attempt<32;attempt++) {
        wsprintfA(Partial,"%s.D%08lX",Target,partialSeed+attempt);
        output=CreateFileA(Partial,GENERIC_WRITE,0,0,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,0);
        if(output!=INVALID_HANDLE_VALUE) break;
        if(GetLastError()!=ERROR_FILE_EXISTS && GetLastError()!=ERROR_ALREADY_EXISTS) goto end;
    }
    if(output==INVALID_HANDLE_VALUE) goto end; partial=1;
    stage=7;
    if(!WriteFile(output,Packet+10+nameLength,fileLength,&written,0) || written!=fileLength ||
       !FlushFileBuffers(output) || Crc(Packet+10+nameLength,fileLength)!=crc) goto end;
    CloseHandle(output); output=INVALID_HANDLE_VALUE;
    stage=8;
    if(!MoveFileA(Partial,Target)) goto end; partial=0;
    stage=9;
    okay=Send(s,3,0); message=okay?"File imported into C:\\Share.":"File imported, but acknowledgement failed.";
end:
    if(!okay) { win32Error=GetLastError(); if(started) winsockError=WSAGetLastError(); }
    if(output!=INVALID_HANDLE_VALUE) CloseHandle(output);
    if(partial) DeleteFileA(Partial);
    if(s!=INVALID_SOCKET) { if(!okay) Send(s,4,stage); closesocket(s); }
    if(started) WSACleanup();
    if(!okay && message[0]=='D') {
        wsprintfA(detailMessage,"DOSE import failed at stage %lu (WinSock %d, Windows %lu).",stage,winsockError,win32Error);
        message=detailMessage;
    }
    MessageBoxA(0,message,"DOSE Import - NAT",MB_OK|(okay?MB_ICONINFORMATION:MB_ICONEXCLAMATION));
    ExitProcess(okay?0:1);
}
