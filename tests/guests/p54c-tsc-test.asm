; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
bits 16
cpu pentium
org 1000h
jmp near start
db 'DOSETSC1'
dw 5000h, 5100h, 5120h, 5140h
dd 00200e97h

%macro sample 1
    push dword 00200e97h
    popfd
    %if %1 & 1
        db 66h
    %endif
    %if %1 & 2
        db 67h
    %endif
    rdtsc
    pushfd
    pop esi
    mov [bx+5000h], eax
    mov [bx+5004h], edx
    mov [bx+5008h], esi
    movzx edi, word [5100h]
    mov [bx+500ch], edi
    add bx, 16
%endmacro

; Initializes only own RAM and programs the actual virtual PIC/PIT through guest ports.
start:
    cli
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 9000h
    cld
    mov di, 4ffch
    mov cx, 180h
    rep stosb
    mov dword [4ffch], 0a5c33c5ah
    mov dword [5080h], 05a3cc3a5h
    fninit
    fldpi
    fstp tword [5120h]
    mov word [8*4], pit_handler
    mov word [8*4+2], 0
    mov al, 11h
    out 20h, al
    out 0a0h, al
    mov al, 8
    out 21h, al
    mov al, 70h
    out 0a1h, al
    mov al, 4
    out 21h, al
    mov al, 2
    out 0a1h, al
    mov al, 1
    out 21h, al
    out 0a1h, al
    mov al, 0feh
    out 21h, al
    mov al, 0ffh
    out 0a1h, al
    mov al, 34h
    out 43h, al
    mov al, 1
    out 40h, al
    mov al, 6
    out 40h, al
    mov eax, 4
    mov cr4, eax
    mov eax, 1
    cpuid
    mov [5104h], eax
    mov [5108h], edx
    mov eax, cr4
    mov [510ch], eax
    xor bx, bx
    sample 0
    hlt
    sample 0
    sample 1
    hlt
    sample 1
    sample 2
    hlt
    sample 2
    sample 3
    hlt
    sample 3
    cld
    mov si, message

; Publishes a bounded own diagnostic message after all four genuine interrupt returns.
print:
    lodsb
    test al, al
    jz finish
    out 0e9h, al
    jmp print

; Leaves the selected component fixture halted with interrupts disabled and a RAM completion byte.
finish:
    cli
    mov byte [5140h], 1
    hlt

; Counts a physical IRQ0 and acknowledges the actual master PIC before IRET.
pit_handler:
    push ax
    inc word [5100h]
    mov al, 20h
    out 20h, al
    pop ax
    iret

message db 'DOSE P54C TSC PIT OK', 13, 10, 0
