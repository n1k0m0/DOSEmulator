; Own CSM level-latch probe. Timer-off intervals distinguish a latched level
; from immediately applying the register write. Only carrier 0 is audible.
bits 16
cpu 486
org 0x100
start:
    push cs
    pop ds
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0x0020
    call opl_write
    mov ax,0x0060
    call opl_write
    mov ax,0x2123
    call opl_write
    mov ax,0x0043
    call opl_write
    mov ax,0xf063
    call opl_write
    mov ax,0x0083             ; zero release keeps the post-CSM sine audible
    call opl_write
    mov ax,0x44a0
    call opl_write
    mov ax,0x12b0             ; normal KON remains off throughout
    call opl_write
    mov ax,0x01c0
    call opl_write
    mov ax,0x8008
    call opl_write
    mov ax,0x8002
    call opl_write
    mov ax,0x4104
    call opl_write
    mov cx,2
    call wait_ticks
    mov ax,0x4004             ; timer stopped; previous key pulse has ended
    call opl_write
    mov cx,1
    call wait_ticks
    mov dx,before
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x2043             ; program 24 dB; effective level must stay unchanged
    call opl_write
    mov dx,pending
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x4104             ; next timer edge latches the 24-dB change
    call opl_write
    mov dx,latched
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x4004
    call opl_write
    mov ax,0x0043             ; pending full volume while timer is stopped
    call opl_write
    mov dx,pending_again
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x0008             ; disabling CSM flushes pending level immediately
    call opl_write
    mov dx,flushed
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x0f83             ; release the previously held envelope
    call opl_write
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov cx,4
    call wait_ticks
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
opl_write:
    push ax
    push dx
    mov dx,0x388
    out dx,al
    times 6 in al,dx
    inc dx
    mov al,ah
    out dx,al
    dec dx
    times 35 in al,dx
    pop dx
    pop ax
    ret
wait_ticks:
    pusha
    mov bx,cx
    xor ah,ah
    int 0x1a
    mov si,dx
.wait:
    xor ah,ah
    int 0x1a
    sub dx,si
    cmp dx,bx
    jb .wait
    popa
    ret
print:
    mov ah,9
    int 0x21
    ret
before db 'CSM-TL-BEFORE',13,10,'$'
pending db 'CSM-TL-PENDING',13,10,'$'
latched db 'CSM-TL-LATCHED',13,10,'$'
pending_again db 'CSM-TL-PENDING-AGAIN',13,10,'$'
flushed db 'CSM-TL-FLUSHED',13,10,'$'
done db 'CSM-TL-DOS-RETURN-OK',13,10,'$'
