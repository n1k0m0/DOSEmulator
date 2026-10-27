; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
bits 16
cpu pentium
org 1000h
jmp near start
db 'DOSEMSR1'
dw gp_handler, ud_handler, values
dd 00200e97h
values:
dq 0, 0123456789abcdefh, 0ffffffffh, 100000000h
dq 0fffffffffffffff0h, 8000000000000000h, 0fedcba9876543210h, 1

%macro prefix 1
    %if %1 & 1
        db 66h
    %endif
    %if %1 & 2
        db 67h
    %endif
%endmacro

%macro sample 2
    mov eax, [values+8*%1]
    mov edx, [values+8*%1+4]
    mov ecx, 10h
    push dword 00200e97h
    popfd
    prefix %2
    wrmsr
    pushfd
    pop esi
    mov [6000h+64*%1], eax
    mov [6004h+64*%1], edx
    mov [6008h+64*%1], esi
    prefix %2
    rdmsr
    pushfd
    pop esi
    mov [600ch+64*%1], eax
    mov [6010h+64*%1], edx
    mov [6014h+64*%1], esi
    prefix %2
    rdtsc
    pushfd
    pop esi
    mov [6018h+64*%1], eax
    mov [601ch+64*%1], edx
    mov [6020h+64*%1], esi
    movzx edi, word [5100h]
    mov [6024h+64*%1], edi
%endmacro

%macro halt_sample 1
    hlt
    rdmsr
    pushfd
    pop esi
    mov [6200h+16*%1], eax
    mov [6204h+16*%1], edx
    mov [6208h+16*%1], esi
    movzx edi, word [5100h]
    mov [620ch+16*%1], edi
%endmacro

%macro rejected 3
    mov ecx, %2
    mov eax, 0deadbeefh
    mov edx, 076543210h
    push dword 00200e97h
    popfd
    %if %3
        db 0f0h
    %endif
    %if %1 & 1
        rdmsr
    %else
        wrmsr
    %endif
    pushfd
    pop esi
    mov [6400h+16*%1], eax
    mov [6404h+16*%1], edx
    mov [6408h+16*%1], ecx
    mov [640ch+16*%1], esi
%endmacro

; Initializes private proof RAM and actually programs the virtual PIC/PIT and x87.
start:
    cli
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 9000h
    cld
    mov di, 5000h
    mov cx, 180h
    rep stosb
    mov di, 6000h
    mov cx, 600h
    rep stosb
    mov dword [5ffch], 0a5c33c5ah
    mov dword [6240h], 05a3cc3a5h
    fninit
    fldpi
    fstp tword [5120h]
    mov word [8*4], pit_handler
    mov word [8*4+2], 0
    mov word [13*4], gp_handler
    mov word [13*4+2], 0
    mov word [6*4], ud_handler
    mov word [6*4+2], 0
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
    sample 0, 0
    sample 1, 1
    halt_sample 0
    sample 2, 2
    sample 3, 3
    halt_sample 1
    sample 4, 0
    sample 5, 1
    halt_sample 2
    sample 6, 2
    sample 7, 3
    halt_sample 3
    rejected 0, 0, 0
    rejected 1, 10010h, 0
    rejected 2, 11h, 0
    rejected 3, 80000010h, 0
    rejected 4, 10h, 1
    rejected 5, 10h, 1
    mov ecx, 10h
    push dword 00200e97h
    popfd
    rdmsr
    mov [6500h], eax
    mov [6504h], edx
    cld
    mov si, message

; Publishes a bounded own result after real timer interrupts and recoverable faults.
print:
    lodsb
    test al, al
    jz finish
    out 0e9h, al
    jmp print

; Ends in a private CLI/HLT state without disabling or substituting the timer model.
finish:
    cli
    mov byte [5140h], 1
    hlt

; Counts and acknowledges only a real physical IRQ0 before returning to the halted guest.
pit_handler:
    push ax
    inc word [5100h]
    mov al, 20h
    out 20h, al
    pop ax
    iret

; Records the actual no-error-word real-mode #GP frame and skips its two-byte instruction.
gp_handler:
    push ax
    push bx
    push bp
    mov bp, sp
    mov bx, [5110h]
    shl bx, 2
    mov ax, [ss:bp+6]
    mov [6300h+bx], ax
    mov ax, [ss:bp+10]
    mov [6302h+bx], ax
    add word [ss:bp+6], 2
    inc word [5110h]
    pop bp
    pop bx
    pop ax
    iret

; Records the actual #UD restart frame and skips only the explicit illegal LOCK form.
ud_handler:
    push ax
    push bx
    push bp
    mov bp, sp
    mov bx, [5112h]
    shl bx, 2
    mov ax, [ss:bp+6]
    mov [6320h+bx], ax
    mov ax, [ss:bp+10]
    mov [6322h+bx], ax
    add word [ss:bp+6], 3
    inc word [5112h]
    pop bp
    pop bx
    pop ax
    iret

message db 'DOSE P54C MSR PIT OK', 13, 10, 0
