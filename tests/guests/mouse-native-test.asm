; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own native input probe. Events must reach this real INT33 via KBC/IRQ12.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
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
    mov si,ready
    call marker
.poll:
    mov ax,3
    int 0x33
    test bx,1
    jz .right
    test byte [seen],1
    jnz .right
    or byte [seen],1
    mov si,left
    call marker
.right:
    test bx,2
    jz .motion
    test byte [seen],2
    jnz .motion
    or byte [seen],2
    mov si,right
    call marker
.motion:
    cmp cx,320
    jne .moved
    cmp dx,100
    je .release
.moved:
    test byte [seen],4
    jnz .release
    or byte [seen],4
    mov si,moved
    call marker
.release:
    cmp byte [seen],7
    jne .key
    test bx,bx
    jnz .key
    or byte [seen],8
    mov si,released
    call marker
.key:
    mov ah,1
    int 0x16
    jz .poll
    xor ah,ah
    int 0x16
    cmp al,27
    jne .poll
    cmp byte [seen],15
    jne failed
    mov ax,3
    int 0x33
    test bx,bx
    jnz failed
    mov ax,2
    int 0x33
    mov ax,3
    int 0x10
    mov dx,done
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
failed:
    mov ax,3
    int 0x10
    mov dx,error
    mov ah,9
    int 0x21
    mov ax,0x4c01
    int 0x21
; Publishes the probe's actual output marker for the independent host observer.
marker:
    push ax
    push si
.next:
    lodsb
    test al,al
    jz .end
    out 0xe9,al
    jmp .next
.end:
    pop si
    pop ax
    ret
seen db 0
ready db 13,10,'NATIVE-MOUSE-READY',13,10,0
left db 13,10,'NATIVE-MOUSE-LEFT',13,10,0
right db 13,10,'NATIVE-MOUSE-RIGHT',13,10,0
moved db 13,10,'NATIVE-MOUSE-MOVED',13,10,0
released db 13,10,'NATIVE-MOUSE-RELEASED',13,10,0
done db 'NATIVE-MOUSE-DOS-RETURN-OK',13,10,'$'
error db 'NATIVE-MOUSE-FAILED',13,10,'$'
