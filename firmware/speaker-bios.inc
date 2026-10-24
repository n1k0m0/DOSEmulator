; Original reference BIOS BEL: about 40 ms at PIT2 divisor 1193. Guest I/O only.
speaker_bell:
    in al,0x61
    and al,15
    mov bl,al
    and al,0xfc
    out 0x61,al
    mov al,0xb6
    out 0x43,al
    mov ax,1193
    out 0x42,al
    mov al,ah
    out 0x42,al
    mov al,bl
    or al,3
    out 0x61,al
    mov cx,40
.period:
    in al,0x61
    test al,32
    jnz .period
.high:
    in al,0x61
    test al,32
    jz .high
    loop .period
    mov al,bl
    out 0x61,al
    ret
