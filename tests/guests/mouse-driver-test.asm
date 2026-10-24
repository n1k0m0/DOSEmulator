; Original acceptance program. Uses the real resident DOS mouse driver's INT33.
; Movement/buttons enter only through the emulated PS/2 device and IRQ12.
bits 16
org 0x100
cpu 486
    push cs
    pop ds
    xor ax,ax
    int 0x33
    cmp byte [0x83],'n'
    je absent
    cmp ax,0xffff
    jne failed
    cmp bx,2
    jne failed
    mov ax,4
    mov cx,320
    mov dx,100
    int 0x33
    mov ax,1
    int 0x33
    mov dx,ready
    call message
.first:
    mov ax,3
    int 0x33
    cmp bx,1
    jne .first
    mov ax,0x0b
    int 0x33
    mov si,1
    cmp cx,4
    jne failed
    cmp dx,3
    jne failed
    mov ax,5
    xor bx,bx
    int 0x33
    mov si,2
    cmp bx,1
    jne failed
    mov dx,left_ok
    call message
.second:
    mov ax,3
    int 0x33
    cmp bx,2
    jne .second
    mov ax,0x0b
    int 0x33
    mov si,3
    cmp cx,-2
    jne failed
    cmp dx,-1
    jne failed
    mov ax,6
    xor bx,bx
    int 0x33
    mov si,4
    cmp bx,1
    jne failed
    mov ax,5
    mov bx,1
    int 0x33
    mov si,5
    cmp bx,1
    jne failed
    mov dx,right_ok
    call message
.release:
    mov ax,3
    int 0x33
    test bx,bx
    jnz .release
    mov ax,6
    mov bx,1
    int 0x33
    mov si,6
    cmp bx,1
    jne failed
    mov ax,2
    int 0x33
    mov dx,release_ok
    call message
    ; Verify unload/reinstall in the command shell separately. Leave a normal prompt.
    mov ax,0x4c00
    int 0x21
absent:
    cmp ax,0xffff
    je failed
    mov dx,absent_ok
    call message
    mov ax,0x4c00
    int 0x21
failed:
    push ax
    push bx
    push cx
    push dx
    push si
    mov dx,error
    call message
    pop ax
    call hexword
    pop ax
    call hexword
    pop ax
    call hexword
    pop ax
    call hexword
    pop ax
    call hexword
    mov ax,0x4c01
    int 0x21
hexword:
    mov bp,ax
    mov di,4
.digit:
    rol bp,4
    mov dx,bp
    and dl,15
    add dl,'0'
    cmp dl,'9'
    jbe .print
    add dl,7
.print:
    mov ah,2
    int 0x21
    dec di
    jnz .digit
    mov dl,' '
    mov ah,2
    int 0x21
    ret
message:
    push ax
    mov ah,9
    int 0x21
    pop ax
    ret
ready: db 13,10,'PS2-DRIVER-READY',13,10,'$'
left_ok: db 'PS2-LEFT-MOTION-OK',13,10,'$'
right_ok: db 'PS2-RIGHT-MOTION-OK',13,10,'$'
release_ok: db 'PS2-DRIVER-RETURN-OK',13,10,'$'
error: db 13,10,'PS2-DRIVER-FAILED',13,10,'$'
absent_ok: db 13,10,'PS2-ABSENT-OK',13,10,'$'
