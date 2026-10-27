; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS COM acceptance program, Apache-2.0. Assemble with -DTEST_MODE=0x12 or 0x13.
; FreeCOM loads this file through FAT/IDE. No host memory injection or graphics shortcuts.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.argument:
    jcxz .mode
    lodsb
    and al,0xdf
    cmp al,'A'
    jne .next_argument
    mov byte [automatic],1
.next_argument:
    loop .argument
.mode:
    mov ax,TEST_MODE
    int 0x10
    mov ah,0x0f
    int 0x10
    cmp al,TEST_MODE
    jne failed
    mov ax,0xa000
    mov es,ax
%if TEST_MODE = 0x13
    ; Program the six-bit DAC: R=i&63, G=i>>2, B=(63-i)&63.
    mov dx,0x3c8
    xor al,al
    out dx,al
    inc dx
    xor bx,bx
.palette:
    mov al,bl
    and al,63
    out dx,al
    mov al,bl
    shr al,2
    out dx,al
    mov al,63
    sub al,bl
    and al,63
    out dx,al
    inc bx
    cmp bx,256
    jb .palette
    xor di,di
    xor dx,dx
.row:
    xor bx,bx
.pixel:
    mov ax,bx
    add al,dl
    stosb
    inc bx
    cmp bx,320
    jb .pixel
    inc dx
    cmp dx,200
    jb .row
    mov ax,0x0093
    int 0x10
    ; No-clear reloads the default DAC, so install a known palette via the BIOS block service.
    push es
    push ds
    pop es
    mov dx,custom_palette
    xor bx,bx
    mov cx,256
    mov ax,0x1012
    int 0x10
    pop es
    mov ax,0x1010
    mov bx,255
    mov dh,63
    mov cx,63
    int 0x10
    mov ax,0x1015
    xor cx,cx
    xor dx,dx
    int 0x10
    cmp dx,0x3f00
    jne failed
    cmp cx,63
    jne failed
    mov cx,319
    mov dx,199
    mov ax,0x0cff
    int 0x10
    mov ah,0x0d
    int 0x10
    cmp al,255
    jne failed
%else
    ; Fill four independent planes with sixteen 40-pixel color bars.
    xor bp,bp
.plane:
    mov dx,0x3c4
    mov cx,bp
    mov ah,1
    shl ah,cl
    mov al,2
    out dx,ax
    xor di,di
    mov si,480
.row:
    xor bx,bx
.bar:
    mov ax,bx
    mov cx,bp
    shr ax,cl
    and al,1
    neg al
    mov cx,5
    rep stosb
    inc bx
    cmp bx,16
    jb .bar
    dec si
    jnz .row
    inc bp
    cmp bp,4
    jb .plane
    mov ax,0x0f02
    mov dx,0x3c4
    out dx,ax
    mov cx,639
    mov dx,479
    mov ax,0x0c03
    int 0x10
    mov ax,0x0c87
    int 0x10
    mov ah,0x0d
    int 0x10
    cmp al,4
    jne failed
%endif
    ; Poll real input-status I/O; virtual time must make this loop finish.
    mov dx,0x3da
.retrace_end:
    in al,dx
    test al,8
    jnz .retrace_end
.retrace_begin:
    in al,dx
    test al,8
    jz .retrace_begin
    mov si,ready
    call debug_string
    cmp byte [automatic],0
    jne .exit
    xor ah,ah
    int 0x16
.exit:
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
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret
automatic: db 0
%if TEST_MODE = 0x13
ready: db 'VGA13-READY',13,10,0
done: db 'VGA13-DOS-RETURN-OK',13,10,'$'
error: db 'VGA13-FAILED',13,10,'$'
custom_palette:
%assign i 0
%rep 256
    db i&63,(i>>2)&63,(63-i)&63
%assign i i+1
%endrep
%else
ready: db 'VGA12-READY',13,10,0
done: db 'VGA12-DOS-RETURN-OK',13,10,'$'
error: db 'VGA12-FAILED',13,10,'$'
%endif
