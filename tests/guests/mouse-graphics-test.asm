; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original ID-00/CuteMouse graphics acceptance, real INT10/INT33 and VGA memory.
bits 16
org 0x100
cpu 486
    mov ax,0x13
    int 0x10
    xor ax,ax
    int 0x33
    cmp ax,0xffff
    jne failed
    mov ax,4
    mov cx,320
    mov dx,100
    int 0x33
    mov ax,1
    int 0x33
    mov si,mode13
    call marker
.button:
    mov ax,3
    int 0x33
    cmp bx,1
    jne .button
    ; Hiding must restore all original black pixels in mode13.
    mov ax,2
    int 0x33
    push ds
    mov ax,0xa000
    mov ds,ax
    xor si,si
    mov cx,64000
.pixel:
    lodsb
    test al,al
    jnz .bad_pixel
    loop .pixel
    pop ds
    mov ax,0x12
    int 0x10
    xor ax,ax
    int 0x33
    cmp ax,0xffff
    jne failed
    mov ax,4
    mov cx,320
    mov dx,240
    int 0x33
    mov ax,1
    int 0x33
    mov si,mode12
    call marker
.release:
    mov ax,3
    int 0x33
    test bx,bx
    jnz .release
    mov ax,2
    int 0x33
    mov ax,3
    int 0x10
    mov dx,done
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
.bad_pixel:
    pop ds
failed:
    mov si,error
    call marker
    mov ax,3
    int 0x10
    mov ax,0x4c01
    int 0x21
; Publishes the probe's actual output marker for the independent host observer.
marker:
    lodsb
    test al,al
    jz .end
    out 0xe9,al
    jmp marker
.end:
    ret
mode13: db 13,10,'PS2-GRAPHICS-13-READY',13,10,0
mode12: db 13,10,'PS2-GRAPHICS-12-READY',13,10,0
error: db 13,10,'PS2-GRAPHICS-FAILED',13,10,0
done: db 13,10,'PS2-GRAPHICS-RETURN-OK',13,10,'$'
