; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own 1.44-MiB hardware-demo boot sector. Loads only the adjacent original client through BIOS INT 13h.
bits 16
org 7c00h
    jmp short start
    nop
    db 'DOSE3D  '
    dw 512
    db 1
    dw 1
    db 2
    dw 224
    dw 2880
    db 0f0h
    dw 9
    dw 18
    dw 2
    dd 0,0
    times 62-($-$$) db 0
; Loads the adjacent client through the real BIOS FDC path before transferring to its fixed entry.
start:
    cli
    xor ax,ax
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov sp,7c00h
    sti
    mov bx,1000h
    mov ax,0200h+CLIENT_SECTORS
    mov cx,2
    xor dx,dx
    int 13h
    jc failed
    jmp 0:1000h
failed:
    cli
    hlt
    jmp failed
    times 510-($-$$) db 0
    dw 0aa55h
