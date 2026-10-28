; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own bounded Win32 RTC/API probe. Assemble as PE; never execute on the host.
BITS 32
ORG 0
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
db 'This application requires Win32 Windows.',13,10,'$'
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
; Check actual IOPL before touching CMOS; an unavailable path is an explicit result.
entry:
    cld
    pushfd
    pop eax
    mov [VA(proof_flags)],eax
    push dword VA(before)
    call [VA(iat_GetLocalTime)]
    mov eax,[VA(proof_flags)]
    and eax,0x3000
    cmp eax,0x3000
%ifndef CLOCK_PROBE_DIRECT_IO
    jne write_proof
%endif
    in al,0x70
    mov [VA(saved_index)],al
    mov al,0x0b
    call rtc_raw
    mov [VA(status_b)],al
    mov [VA(proof_status_b)],eax
    mov ebp,8
.retry:
    mov al,0
    call rtc_value
    mov [VA(target)+12],ax
    mov al,2
    call rtc_value
    mov [VA(target)+10],ax
    mov al,4
    call rtc_value
    mov [VA(target)+8],ax
    mov al,7
    call rtc_value
    mov [VA(target)+6],ax
    mov al,8
    call rtc_value
    mov [VA(target)+2],ax
    mov al,9
    call rtc_value
    mov [VA(target)],ax
    mov al,0x32
    call rtc_value
    imul eax,100
    add [VA(target)],ax
    mov al,0
    call rtc_value
    cmp ax,[VA(target)+12]
    je .stable
    dec ebp
    jnz .retry
    jmp .restore_index
.stable:
    mov dword [VA(proof_rtc_read)],1
.restore_index:
    mov al,[VA(saved_index)]
    out 0x70,al
    cmp dword [VA(proof_rtc_read)],1
    jne write_proof
    push dword VA(target)
    call [VA(iat_SetLocalTime)]
    mov [VA(proof_set_result)],eax
    test eax,eax
    jnz .read_after
    call [VA(iat_GetLastError)]
    mov [VA(proof_set_error)],eax
.read_after:
    push dword VA(after)
    call [VA(iat_GetLocalTime)]
    jmp write_proof

; Preserve the caller's NMI index bit; return one binary/BCD field zero extended.
rtc_raw:
    mov ah,[VA(saved_index)]
    and ah,0x80
    or al,ah
    out 0x70,al
    in al,0x71
    movzx eax,al
    ret
; Convert one CMOS field from the advertised BCD or binary representation.
rtc_value:
    call rtc_raw
    test byte [VA(status_b)],4
    jnz .done
    mov edx,eax
    and eax,15
    shr edx,4
    and edx,15
    imul edx,10
    add eax,edx
.done:
    ret

; Publish the actual raw API evidence through Windows' own file APIs.
write_proof:
    push dword 0
    push dword 0x80
    push dword 2
    push dword 0
    push dword 1
    push dword 0x40000000
    push dword VA(proof_path)
    call [VA(iat_CreateFileA)]
    mov ebx,eax
    cmp eax,-1
    je .exit
    push dword 0
    push dword VA(written)
    push dword 128
    push dword VA(proof)
    push ebx
    call [VA(iat_WriteFile)]
    push ebx
    call [VA(iat_FlushFileBuffers)]
    push ebx
    call [VA(iat_CloseHandle)]
.exit:
    push dword 0
    call [VA(iat_ExitProcess)]

align 4,db 0
proof:
    db 'DOSECLK1'
    dd 1,128
proof_flags:dd 0
proof_set_result:dd 0xffffffff
proof_set_error:dd 0
proof_status_b:dd 0
before:times 8 dw 0
target:times 8 dw 0
after:times 8 dw 0
proof_rtc_read:dd 0
times 128-($-proof) db 0
proof_path:db 'C:\DOSECLK.BIN',0
saved_index:db 0
status_b:db 0
align 4,db 0
written:dd 0
import_directory:
    dd RVA(import_lookup),0,0,RVA(import_dll),RVA(import_iat)
    times 5 dd 0
import_directory_end:
import_lookup:
%macro LOOKUP 1
    dd RVA(name_ %+ %1)
%endmacro
%macro IAT 1
iat_ %+ %1:dd RVA(name_ %+ %1)
%endmacro
%macro NAME 1
align 2,db 0
name_ %+ %1:dw 0
    db %str(%1),0
%endmacro
%macro IMPORTS 1
    %1 GetLocalTime
    %1 SetLocalTime
    %1 GetLastError
    %1 CreateFileA
    %1 WriteFile
    %1 FlushFileBuffers
    %1 CloseHandle
    %1 ExitProcess
%endmacro
    IMPORTS LOOKUP
    dd 0
import_iat:
    IMPORTS IAT
    dd 0
import_dll:db 'KERNEL32.DLL',0
    IMPORTS NAME
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
