; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own Windows95/98 Winsock1.1 UDP/TCP witness. NEVER execute this PE on the host.
; NETWORK_PORT identifies only an explicitly bound controlled host-loopback echo server.
; Guest destination10.0.2.2 is the configured DOSE gateway/host alias. No Internet target.
; C:\DOSENET.BIN is CREATE_NEW, exactly2048B. Header and all returned payload bytes persist.
; Header32-bit offsets16 phase,20flags,24WSAStartup,28WSAerror,32UDPsend,36select,
; 40UDPrecv,44TCPconnect,48TCPsent,52TCPreceived,56TCPeof,60port; UDP64..320,TCP336..1362.
; End phase99 means complete257/1027-byte echo plus naturalTCP EOF; guard failures at1400.
BITS 32
ORG 0
%ifndef NETWORK_PORT
%error Define NETWORK_PORT for the controlled loopback server.
%endif
%if NETWORK_PORT < 1024 || NETWORK_PORT > 65535
%error Controlled server port must be1024..65535.
%endif
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
db 'MZ'
dw ((0x200+section_file_size)&0x1ff),((0x200+section_file_size+0x1ff)>>9),0,4,0,0xffff,0,0xb8,0,0,0,0x40,0
times 0x3c-($-$$) db 0
dd pe_header
BITS 16
push cs
pop ds
mov dx,0x0e
mov ah,9
int 0x21
mov ax,0x4c01
int 0x21
db 'DOSE Network Check requires Windows 95.',13,10,'$'
times 0x80-($-$$) db 0
BITS 32
pe_header:
db 'PE',0,0
dw 0x014c,1
dd 0,0,0
dw 0x00e0,0x0103
dw 0x010b
db 0,0
dd section_file_size,0,0
dd RVA(entry),0x1000,0x1000
dd IMAGE_BASE,0x1000,0x200
dw 4,0,0,0,4,0
dd 0,((section_end-section_start+0x1fff)&~0xfff),0x200,0
dw 2,0
dd 0x10000,0x1000,0x10000,0x1000,0,16
dd 0,0
dd RVA(import_directory),import_directory_end-import_directory
times 14 dq 0
db '.text',0,0,0
dd section_end-section_start,0x1000,section_file_size,0x200
dd 0,0
dw 0,0
dd 0xe0000060
times 0x200-($-$$) db 0
section_start:


; Execute this PE only on original Win9x and use its real Winsock and file APIs.
entry:
    cld
    call [VA(iat_GetVersion)]
    test eax,0x80000000
    jz unsupported
    cmp al,4                     ; Win95 4.0 and Win98 4.10 retain separate minor versions.
    jne unsupported
    push 0
    push 0x80
    push 1
    push 0
    push 0
    push 0x40000000
    push VA(path)
    call [VA(iat_CreateFileA)]
    cmp eax,-1
    je unsupported
    mov [VA(handle)],eax
    mov edi,VA(payload)
    xor ecx,ecx
.fill:
    imul eax,ecx,29
    add eax,7
    stosb
    inc ecx
    cmp ecx,1027
    jb .fill
    mov dword [VA(proof)+16],1
    call report
    push VA(wsa)
    push 0x0101
    call [VA(iat_WSAStartup)]
    mov [VA(proof)+24],eax
    test eax,eax
    jnz failure
    or dword [VA(proof)+20],1
    mov dword [VA(proof)+16],2
    push 17
    push 2
    push 2
    call [VA(iat_socket)]
    mov [VA(udp_socket)],eax
    cmp eax,-1
    je failure
    push 16
    push VA(address)
    push 0
    push 257
    push VA(payload)
    push eax
    call [VA(iat_sendto)]
    mov [VA(proof)+32],eax
    cmp eax,257
    jne failure
    or dword [VA(proof)+20],2
    mov eax,[VA(udp_socket)]
    mov [VA(read_fds)+4],eax
    call readable
    mov [VA(proof)+36],eax
    cmp eax,1
    jne failure
    push 0
    push 0
    push 0
    push 257
    push VA(proof)+64
    push dword [VA(udp_socket)]
    call [VA(iat_recvfrom)]
    mov [VA(proof)+40],eax
    cmp eax,257
    jne failure
    or dword [VA(proof)+20],4
    mov dword [VA(proof)+16],3
    call report
    push dword [VA(udp_socket)]
    call [VA(iat_closesocket)]
    mov dword [VA(udp_socket)],-1
    push 6
    push 1
    push 2
    call [VA(iat_socket)]
    mov [VA(tcp_socket)],eax
    cmp eax,-1
    je failure
    push 16
    push VA(address)
    push eax
    call [VA(iat_connect)]
    mov [VA(proof)+44],eax
    test eax,eax
    jnz failure
    or dword [VA(proof)+20],8
.send:
    mov ebx,[VA(proof)+48]
    mov ecx,1027
    sub ecx,ebx
    jz .receive
    add ebx,VA(payload)
    push 0
    push ecx
    push ebx
    push dword [VA(tcp_socket)]
    call [VA(iat_send)]
    test eax,eax
    jle failure
    add [VA(proof)+48],eax
    jmp .send
.receive:
    mov eax,[VA(tcp_socket)]
    mov [VA(read_fds)+4],eax
    call readable
    cmp eax,1
    jne failure
    mov ebx,[VA(proof)+52]
    mov ecx,1027
    sub ecx,ebx
    jz .eof
    add ebx,VA(proof)+336
    push 0
    push ecx
    push ebx
    push dword [VA(tcp_socket)]
    call [VA(iat_recv)]
    test eax,eax
    jle failure
    add [VA(proof)+52],eax
    jmp .receive
.eof:
    push 0
    push 1
    push VA(eof_byte)
    push dword [VA(tcp_socket)]
    call [VA(iat_recv)]
    mov [VA(proof)+56],eax
    test eax,eax
    jnz failure
    or dword [VA(proof)+20],0x10
    push dword [VA(tcp_socket)]
    call [VA(iat_closesocket)]
    mov dword [VA(tcp_socket)],-1
    mov esi,VA(wsa_guard)
    mov ecx,16
.guards:
    cmp byte [esi],0xa5
    je .guard_ok
    inc dword [VA(proof)+1400]
.guard_ok:
    inc esi
    loop .guards
    cmp dword [VA(proof)+1400],0
    jne failure
    mov dword [VA(proof)+16],99
    jmp finish
failure:
    call [VA(iat_WSAGetLastError)]
    mov [VA(proof)+28],eax
    or dword [VA(proof)+20],0x80000000
finish:
    call report
    cmp dword [VA(tcp_socket)],-1
    je .udp
    push dword [VA(tcp_socket)]
    call [VA(iat_closesocket)]
.udp:
    cmp dword [VA(udp_socket)],-1
    je .cleanup
    push dword [VA(udp_socket)]
    call [VA(iat_closesocket)]
.cleanup:
    call [VA(iat_WSACleanup)]
    push dword [VA(handle)]
    call [VA(iat_CloseHandle)]
    push 0
    push VA(title)
    mov eax,VA(failure_text)
    cmp dword [VA(proof)+16],99
    jne .message
    mov eax,VA(success_text)
.message:
    push eax
    push 0
    call [VA(iat_MessageBoxA)]
    push 0
    call [VA(iat_ExitProcess)]
unsupported:
    push 97
    call [VA(iat_ExitProcess)]
; Bound the waits through the real Windows select API; never use host timers in guest code.
readable:
    mov dword [VA(read_fds)],1
    mov dword [VA(wait_time)],10
    mov dword [VA(wait_time)+4],0
    push VA(wait_time)
    push 0
    push 0
    push VA(read_fds)
    push 0
    call [VA(iat_select)]
    ret
; Seek/write/flush every entire bounded proof through Windows, including error stages.
report:
    pushad
    push 0
    push 0
    push 0
    push dword [VA(handle)]
    call [VA(iat_SetFilePointer)]
    push 0
    push VA(written)
    push 2048
    push VA(proof)
    push dword [VA(handle)]
    call [VA(iat_WriteFile)]
    test eax,eax
    jz .bad
    cmp dword [VA(written)],2048
    jne .bad
    push dword [VA(handle)]
    call [VA(iat_FlushFileBuffers)]
    test eax,eax
    jnz .done
.bad:
    or dword [VA(proof)+20],0x40000000
.done:
    popad
    ret
align 4,db 0
proof: db 'DOSENET1'
    dd 1,2048,0,0,0xffffffff,0,0xffffffff,0xffffffff,0xffffffff,0xffffffff,0,0,0xffffffff,NETWORK_PORT
    times 2048-($-proof) db 0
payload: times 1027 db 0
wsa: times 512 db 0
wsa_guard: times 16 db 0xa5
read_fds: dd 1,0
    times 252 db 0
wait_time: dd 10,0
address: dw 2
    db NETWORK_PORT>>8,NETWORK_PORT&255,10,0,2,2
    times 8 db 0
udp_socket: dd -1
tcp_socket: dd -1
handle: dd 0
written: dd 0
eof_byte: db 0
path: db 'C:\DOSENET.BIN',0
title: db 'DOSE Network Check',0
success_text: db 'PASS: real Windows TCP/IP, UDP and TCP echo completed.',0
failure_text: db 'Network check failed. The bounded DOSENET.BIN report contains the actual API results.',0
%macro LOOKUP 1
    dd RVA(name_ %+ %1)
%endmacro
%macro IAT 1
iat_ %+ %1: dd RVA(name_ %+ %1)
%endmacro
%macro NAME 1
align 2,db 0
name_ %+ %1: dw 0
    db %str(%1),0
%endmacro
%macro KERNEL_IMPORTS 1
    %1 GetVersion
    %1 CreateFileA
    %1 SetFilePointer
    %1 WriteFile
    %1 FlushFileBuffers
    %1 CloseHandle
    %1 ExitProcess
%endmacro
%macro USER_IMPORTS 1
    %1 MessageBoxA
%endmacro
%macro SOCKET_IMPORTS 1
    %1 WSAStartup
    %1 WSAGetLastError
    %1 WSACleanup
    %1 socket
    %1 connect
    %1 send
    %1 recv
    %1 sendto
    %1 recvfrom
    %1 select
    %1 closesocket
%endmacro
align 4,db 0
import_directory:
    dd RVA(lookup_kernel),0,0,RVA(dll_kernel),RVA(iat_kernel)
    dd RVA(lookup_user),0,0,RVA(dll_user),RVA(iat_user)
    dd RVA(lookup_socket),0,0,RVA(dll_socket),RVA(iat_socket_list)
    times 5 dd 0
import_directory_end:
lookup_kernel: KERNEL_IMPORTS LOOKUP
    dd 0
iat_kernel: KERNEL_IMPORTS IAT
    dd 0
lookup_user: USER_IMPORTS LOOKUP
    dd 0
iat_user: USER_IMPORTS IAT
    dd 0
lookup_socket: SOCKET_IMPORTS LOOKUP
    dd 0
iat_socket_list: SOCKET_IMPORTS IAT
    dd 0
dll_kernel: db 'KERNEL32.DLL',0
dll_user: db 'USER32.DLL',0
dll_socket: db 'WSOCK32.DLL',0
    KERNEL_IMPORTS NAME
    USER_IMPORTS NAME
    SOCKET_IMPORTS NAME
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
