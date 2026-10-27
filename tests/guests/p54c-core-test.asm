; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own reset-vector CPU guest: no DOS/BIOS shortcut, device injector or host endpoint.
bits 16
cpu pentium
org 0x1000

; Establish independent guest registers, raw FPU and identity before the exchange matrix.
start:
    cli
    xor ax,ax
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov sp,0x9000
    fninit
    fldpi
    fstp tword [raw_pi]
    pushfd
    pop eax
    mov edx,eax
    xor eax,0x200000
    push eax
    popfd
    pushfd
    pop eax
    xor eax,edx
    test eax,0x200000
    jz failed
    xor eax,eax
    cpuid
    mov [identity],eax
    mov [identity+4],ebx
    mov [identity+8],edx
    mov [identity+12],ecx
    mov eax,1
    cpuid
    mov [identity+16],eax
    mov [identity+20],ebx
    mov [identity+24],ecx
    mov [identity+28],edx
    mov word [row_index],0

next_row:
    mov ax,[row_index]
    shr ax,2
    shl ax,4
    mov bp,vectors
    add bp,ax
    mov eax,[bp]
    mov edx,[bp+4]
    mov [work_value],eax
    mov [work_value+4],edx
    test word [row_index],1
    jz matched_input
    xor eax,1
matched_input:
    mov ebx,[bp+8]
    mov ecx,[bp+12]
    test word [row_index],2
    jz unlocked
    push dword 0x200c97
    popfd
    lock cmpxchg8b [work_value]
    jmp record
unlocked:
    push dword 0x200c97
    popfd
    cmpxchg8b [work_value]
record:
    pushfd
    pop esi
    mov di,[row_index]
    shl di,5
    add di,rows
    mov [di+8],eax
    mov [di+12],edx
    mov [di+16],ebx
    mov [di+20],ecx
    mov [di+24],esi
    mov eax,[work_value]
    mov edx,[work_value+4]
    mov [di],eax
    mov [di+4],edx
    mov ax,[guard_before]
    mov [di+28],ax
    mov ax,[guard_after]
    mov [di+30],ax
    inc word [row_index]
    cmp word [row_index],32
    jb next_row
    mov byte [done],0xa5
    cld
    mov si,message
    mov dx,0xe9
emit:
    lodsb
    test al,al
    jz stopped
    out dx,al
    jmp emit
failed:
    mov byte [done],0x5a
stopped:
    cli
    hlt
    jmp stopped

message: db 'DOSE P54C CORE DONE',10,0
align 4,db 0
vectors:
    dq 0x0000000000000000,0xffffffffffffffff
    dq 0xffffffffffffffff,0x0000000000000000
    dq 0x8000000000000000,0x7fffffffffffffff
    dq 0x0000000080000000,0xffffffff7fffffff
    dq 0x0123456789abcdef,0xfedcba9876543210
    dq 0xaaaaaaaa55555555,0x55555555aaaaaaaa
    dq 0x0000000100000000,0x00000000ffffffff
    dq 0xffffffffffff0000,0x0000000000010000

metadata: db 'P54META1'
    dw writable_start,vectors,8,identity,rows,32,raw_pi,done
writable_start:
identity: times 32 db 0
raw_pi: times 10 db 0
row_index: dw 0
done: db 0
align 8,db 0
    times 6 db 0
guard_before: dw 0xa55a
work_value: dq 0
guard_after: dw 0x5aa5
rows: times 32*32 db 0
