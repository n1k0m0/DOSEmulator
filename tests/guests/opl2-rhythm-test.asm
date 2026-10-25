; Own OPL2 five-instrument probe. /S exercises the Sound Blaster FM alias.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .begin
    lodsb
    and al,0xdf
    cmp al,'S'
    jne .next
    mov word [opl_port],0x228
.next:
    loop .args
.begin:
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0x2001
    call opl_write
    mov ax,0x0008
    call opl_write
    mov ax,0x20bd
    call opl_write
    xor bx,bx
.slots:
    mov al,[offsets+bx]
    add al,0x20
    mov ah,0x21
    call opl_write
    add al,0x20
    mov ah,16
    call opl_write
    add al,0x20
    mov ah,0xf0
    call opl_write
    add al,0x20
    mov ah,0x0f
    call opl_write
    add al,0x60
    xor ah,ah
    call opl_write
    inc bx
    cmp bx,18
    jb .slots
    xor bx,bx
.channels:
    mov al,[channels+bx]
    add al,0xa0
    mov ah,[frequencies+bx]
    call opl_write
    add al,0x10
    mov ah,0x12
    call opl_write
    add al,0x10
    mov ah,1
    call opl_write
    inc bx
    cmp bx,9
    jb .channels
    xor bx,bx
.drums:
    mov dx,[markers+bx]
    call print
    mov ah,[drums+bx]
    mov al,0xbd
    call opl_write
    mov cx,8
    call wait_ticks
    mov ax,0x20bd
    call opl_write
    mov cx,5
    call wait_ticks
    add bx,2
    cmp bx,10
    jb .drums
    mov dx,mix_marker
    call print
    xor bx,bx
.mix_on:
    mov ax,bx
    add al,0xb0
    mov ah,0x32
    call opl_write
    inc bx
    cmp bx,6
    jb .mix_on
    mov ax,0x3fbd
    call opl_write
    mov cx,8
    call wait_ticks
    xor bx,bx
.mix_off:
    mov ax,bx
    add al,0xb0
    mov ah,0x12
    call opl_write
    inc bx
    cmp bx,6
    jb .mix_off
    mov ax,0x00bd
    call opl_write
    mov cx,5
    call wait_ticks
    mov dx,melody_marker
    call print
    mov ax,0x32b8
    call opl_write
    mov cx,8
    call wait_ticks
    mov ax,0x12b8
    call opl_write
    mov cx,6
    call wait_ticks
    mov dx,success
    call print
    mov ax,0x4c00
    int 0x21
opl_write:
    push ax
    push cx
    push dx
    mov dx,[opl_port]
    out dx,al
    mov cx,6
.address_wait:
    in al,dx
    loop .address_wait
    inc dx
    mov al,ah
    out dx,al
    dec dx
    mov cx,35
.data_wait:
    in al,dx
    loop .data_wait
    pop dx
    pop cx
    pop ax
    ret
wait_ticks:
    pushad
    mov bx,cx
    xor ah,ah
    int 0x1a
    mov si,dx
    add si,bx
.wait:
    xor ah,ah
    int 0x1a
    cmp dx,si
    jb .wait
    popad
    ret
print:
    mov ah,9
    int 0x21
    ret
opl_port dw 0x388
offsets db 0,3,1,4,2,5,8,11,9,12,10,13,16,19,17,20,18,21
channels db 0,1,2,3,4,5,6,7,8
frequencies db 0x44,0x50,0x60,0x70,0x80,0x90,0x44,0x70,0x96
; Word-strided flags match the message pointer table.
drums dw 0x30,0x21,0x28,0x24,0x22
markers dw bass,hat,snare,tom,cymbal
bass db 'RHYTHM-BASS-DRUM',13,10,'$'
hat db 'RHYTHM-HI-HAT',13,10,'$'
snare db 'RHYTHM-SNARE',13,10,'$'
tom db 'RHYTHM-TOM',13,10,'$'
cymbal db 'RHYTHM-CYMBAL',13,10,'$'
mix_marker db 'RHYTHM-SIX-MELODY-PLUS-FIVE-DRUMS',13,10,'$'
melody_marker db 'RHYTHM-MELODIC-MODE-RESTORED',13,10,'$'
success db 'RHYTHM-DOS-RETURN-OK',13,10,'$'
