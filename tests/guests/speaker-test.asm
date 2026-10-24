; Original real-mode DOS PC Speaker probe, Apache-2.0. /H holds 440 Hz for host/UI acceptance.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    in al,0x61
    and al,15
    mov [saved_control],al
    mov dx,banner
    call print
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.argument:
    jcxz .run
    lodsb
    and al,0xdf
    cmp al,'H'
    jne .next
    mov byte [hold],1
.next:
    loop .argument
.run:
    mov bx,2712
    call square
    cmp byte [hold],0
    je .normal
    mov dx,holding
    call print
    xor ah,ah
    int 0x16
    jmp .exit
.normal:
    mov dx,tone440
    call print
    mov cx,6
    call wait_ticks
    call silence
    mov cx,4
    call wait_ticks
    mov bx,1356
    call square
    mov dx,tone880
    call print
    mov cx,6
    call wait_ticks
    call silence
    mov cx,4
    call wait_ticks
    ; Rate-generator path, including actual output polling and read-back command.
    mov al,0xb4
    out 0x43,al
    mov ax,1193
    out 0x42,al
    mov al,ah
    out 0x42,al
    in al,0x61
    or al,3
    out 0x61,al
.low:
    in al,0x61
    test al,32
    jnz .low
.high:
    in al,0x61
    test al,32
    jz .high
    mov al,0xc8
    out 0x43,al
    in al,0x42
    and al,0x3f
    cmp al,0x34
    jne failed
    in al,0x42
    in al,0x42
    mov cx,2
    call wait_ticks
    call silence
    ; Binary one-shot must become high on its own input clock, not on a PCM callback.
    mov al,0xb0
    out 0x43,al
    mov ax,11932
    out 0x42,al
    mov al,ah
    out 0x42,al
    mov al,3
    out 0x61,al
.terminal:
    in al,0x61
    test al,32
    jz .terminal
    call silence
    mov cx,4
    call wait_ticks
    ; Direct software drive: gate low forces mode-3 OUT high, bit 1 supplies the pulses.
    mov al,0xb6
    out 0x43,al
    xor al,al
    out 0x61,al
    mov bx,256
.direct:
    xor al,2
    out 0x61,al
    mov cx,4000
.delay:
    loop .delay
    dec bx
    jnz .direct
    call silence
    mov ax,0x0e07
    int 0x10
    mov cx,4
    call wait_ticks
.exit:
    mov al,[saved_control]
    out 0x61,al
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
failed:
    call silence
    mov dx,error
    call print
    mov ax,0x4c01
    int 0x21
square:
    mov al,0xb6
    out 0x43,al
    mov ax,bx
    out 0x42,al
    mov al,ah
    out 0x42,al
    in al,0x61
    or al,3
    out 0x61,al
    ret
silence:
    in al,0x61
    and al,0xfc
    out 0x61,al
    ret
wait_ticks:
    pusha
    mov bx,cx
    xor ah,ah
    int 0x1a
    add bx,dx
.wait:
    xor ah,ah
    int 0x1a
    cmp dx,bx
    jb .wait
    popa
    ret
print:
    mov ah,9
    int 0x21
    ret
saved_control: db 0
hold: db 0
banner: db 'PC SPEAKER - REAL DOS / PIT2 / PORT 61H',13,10,'$'
holding: db 'SPEAKER-HOLD-READY',13,10,'Use AUDIO SETTINGS for live volume/mute.',13,10,'$'
tone440: db 'SPEAKER-440-HZ',13,10,'$'
tone880: db 'SPEAKER-880-HZ',13,10,'$'
done: db 'SPEAKER-DOS-RETURN-OK',13,10,'$'
error: db 'SPEAKER-FAILED',13,10,'$'
