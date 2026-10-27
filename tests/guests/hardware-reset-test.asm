; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own real DOS guest probe. No OS bytes. Apache-2.0.
; RESET.COM: flush real DOS buffers, set the BIOS warm flag, then reset via KBC.
; RESET.COM /92: the same path through the alternate CPU-reset port.
bits 16
cpu 486
org 0x100
    cld
    mov ah,0x0d
    int 0x21
    push cs
    pop ds
    xor bx,bx
    movzx cx,byte [0x80]
    mov si,0x81
.argument:
    jcxz .request
    lodsb
    cmp al,'9'
    jne .next
    cmp byte [si],'2'
    jne .next
    inc bx
.next:
    loop .argument
.request:
    mov si,marker
.print:
    lodsb
    test al,al
    jz .reset
    out 0xe9,al
    jmp .print
.reset:
    cli
    xor ax,ax
    mov ds,ax
    mov word [0x472],0x1234
    test bx,bx
    jnz .fast
    mov al,0xfe
    out 0x64,al
    jmp .failed
.fast:
    in al,0x92
    and al,0xfe
    out 0x92,al
    or al,1
    out 0x92,al
.failed:
    hlt
    jmp .failed
; Publishes the probe's actual output marker for the independent host observer.
marker: db 13,10,'GUEST-CPU-RESET-REQUEST',13,10,0
