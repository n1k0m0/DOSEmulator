; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Exercise the installed DOS ANSI driver, not a host or direct BIOS CLS.
bits 16
cpu 486
org 0x100
; Fill the real video aperture, clear through DOS ANSI and verify every cell.
start:
    push cs
    pop ds
    mov ax,0x40
    mov es,ax
    movzx cx,byte [es:0x84]
    inc cx
    mov [rows],cx
    imul cx,80
    mov [cells],cx
    mov ax,0xb800
    mov es,ax
    xor di,di
    mov ax,0x1e58               ; stale yellow Xs must all disappear
    cld
    rep stosw
    mov dx,clear
    mov ah,9
    int 0x21
    xor di,di
    mov cx,[cells]
    mov ax,0x0720
.check:
    cmp [es:di],ax
    jne failed
    add di,2
    loop .check
    mov dx,caption
    mov ah,9
    int 0x21
    mov ah,3
    xor bh,bh
    int 0x10
    mov [old_shape],cx
    mov ah,1
    or ch,0x20                 ; deterministic pixels, no blinking cursor
    int 0x10
    mov si,ready
    call debug
    xor ah,ah
    int 0x16
    mov cx,[old_shape]
    mov ah,1
    int 0x10
    mov ax,0x4c00
    int 0x21
; Emit only the probe's own result through the ordinary debug port.
debug:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug
.done:
    ret
; Keep failure observable even when ANSI bypasses the debug teletype path.
failed:
    mov si,failure
    call debug
    mov ax,0x4c01
    int 0x21
clear db 27,'[0m',27,'[2J',27,'[H','$'
caption db 'ORIGINAL DOS ANSI / CLEAR VERIFIED',13,10,'NO STALE CELLS / CP850',13,10,'$'
ready db 'ANSI-CLS-READY',0
failure db 'ANSI-CLS-FAILED',0
rows dw 0
cells dw 0
old_shape dw 0
