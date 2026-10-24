; Original real-mode DOS OPL2 development probe, Apache-2.0.
; /H holds an FM tone. /S uses the SB2 FM alias and adds actual DMA1 PCM to the mix.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    in al,0x61
    and al,15
    mov [saved_speaker],al
    in al,0x21
    mov [saved_pic],al
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .begin
    lodsb
    and al,0xdf
    cmp al,'H'
    jne .sb
    mov byte [hold],1
.sb:
    cmp al,'S'
    jne .next
    mov word [opl_port],0x228
    mov byte [sb2],1
.next:
    loop .args
.begin:
    mov dx,banner
    call print
    ; Actual timer/flag detection, using the classic six/35 status-read recovery.
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov dx,[opl_port]
    in al,dx
    and al,0xe6
    cmp al,6
    jne failed
    mov ax,0xff02
    call opl_write
    mov ax,0x2104
    call opl_write
    mov cx,1000
.timer1:
    loop .timer1
    in al,dx
    and al,0xe0
    cmp al,0xc0
    jne failed
    mov ax,0x0004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0xff03
    call opl_write
    mov ax,0x4204
    call opl_write
    mov cx,4000
.timer2:
    loop .timer2
    in al,dx
    and al,0xe0
    cmp al,0xa0
    jne failed
    mov ax,0x0004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov dx,timers_ok
    call print
    mov ax,0x2001
    call opl_write
    xor bx,bx
.operators:
    mov al,[offsets+bx]
    add al,0x20
    mov ah,0x21
    call opl_write
    add al,0x20
    mov ah,63
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
    jb .operators
    mov ax,0x0008
    call opl_write
    mov ax,0x00bd
    call opl_write
    ; Carrier only: the modulator's zero attack rate leaves it silent.
    mov ax,0x0060
    call opl_write
    mov ax,0x0043
    call opl_write
    mov ax,0x00c0
    call opl_write
    call tone440
    cmp byte [hold],0
    jne holding
    mov dx,sine_marker
    call print
    mov cx,6
    call wait_ticks
    call stop_tone

    mov ax,0x02e3
    call opl_write
    call tone440
    mov dx,wave_marker
    call print
    mov cx,6
    call wait_ticks
    call stop_tone

    mov ax,0x00e3
    call opl_write
    mov ax,0x5a63
    call opl_write
    mov ax,0x4a83
    call opl_write
    call tone440
    mov dx,envelope_marker
    call print
    mov cx,8
    call wait_ticks
    call stop_tone

    mov ax,0xf063
    call opl_write
    mov ax,0x0f83
    call opl_write
    mov ax,0x2220
    call opl_write
    mov ax,0xf060
    call opl_write
    mov ax,0x0040
    call opl_write
    mov ax,0x0ec0
    call opl_write
    call tone440
    mov dx,fm_marker
    call print
    mov cx,6
    call wait_ticks
    call stop_tone

    mov ax,0x0060
    call opl_write
    mov ax,0x3f40
    call opl_write
    mov ax,0x00c0
    call opl_write
    mov ax,0x66a0
    call opl_write
    mov ax,0x33b0
    call opl_write
    mov al,0xb6
    out 0x43,al
    mov ax,2712
    out 0x42,al
    mov al,ah
    out 0x42,al
    in al,0x61
    or al,3
    out 0x61,al
    cmp byte [sb2],0
    je .mix_without_pcm
    call start_pcm
    mov dx,mix_pcm_marker
    jmp .mix
.mix_without_pcm:
    mov dx,mix_marker
.mix:
    call print
    mov cx,6
    call wait_ticks
    cmp byte [sb2],0
    je .stop_mix
    call stop_pcm
.stop_mix:
    mov al,[saved_speaker]
    out 0x61,al
    call stop_tone
    cmp byte [io_error],0
    jne failed
    call cleanup
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
holding:
    mov dx,hold_marker
    call print
    xor ah,ah
    int 0x16
    call cleanup
    mov ax,0x4c00
    int 0x21
failed:
    call cleanup
    mov dx,error
    call print
    mov ax,0x4c01
    int 0x21
tone440:
    mov ax,0x44a0
    call opl_write
    mov ax,0x32b0
    call opl_write
    ret
stop_tone:
    mov ax,0x12b0
    call opl_write
    mov cx,4
    call wait_ticks
    ret
cleanup:
    mov ax,0x12b0
    call opl_write
    mov ax,0x0004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov al,[saved_speaker]
    out 0x61,al
    mov al,[saved_pic]
    out 0x21,al
    ret
opl_write:
    push ax
    push cx
    push dx
    mov dx,[opl_port]
    out dx,al
    times 6 in al,dx
    inc dx
    mov al,ah
    out dx,al
    dec dx
    times 35 in al,dx
    pop dx
    pop cx
    pop ax
    ret
start_pcm:
    in al,0x21
    or al,0x20
    out 0x21,al
    call dsp_reset
    push cs
    pop es
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov ebx,eax
    add eax,buffer
    add eax,8191
    and eax,0xffffe000
    sub eax,ebx
    mov [buffer_offset],ax
    mov di,ax
    mov cx,8192
    xor bx,bx
.fill:
    mov al,64
    test bl,16
    jz .store
    mov al,192
.store:
    stosb
    inc bx
    loop .fill
    mov al,5
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov al,0x59
    out 0x0b,al
    xor eax,eax
    mov ax,cs
    shl eax,4
    movzx edx,word [buffer_offset]
    add eax,edx
    out 2,al
    mov al,ah
    out 2,al
    shr eax,16
    out 0x83,al
    mov ax,8191
    out 3,al
    mov al,ah
    out 3,al
    mov al,1
    out 0x0a,al
    mov al,0x40
    call dsp_write
    mov al,131
    call dsp_write
    mov al,0xd1
    call dsp_write
    mov al,0x48
    call dsp_write
    mov al,255
    call dsp_write
    mov al,255
    call dsp_write
    mov al,0x1c
    call dsp_write
    ret
stop_pcm:
    mov al,0xd0
    call dsp_write
    mov al,5
    out 0x0a,al
    call dsp_reset
    ret
dsp_reset:
    mov dx,0x226
    mov al,1
    out dx,al
    mov cx,256
.delay:
    loop .delay
    xor al,al
    out dx,al
    mov dx,0x22e
    mov cx,65535
.ready:
    in al,dx
    test al,0x80
    jnz .read
    loop .ready
    mov byte [io_error],1
    ret
.read:
    mov dx,0x22a
    in al,dx
    cmp al,0xaa
    je .done
    mov byte [io_error],1
.done:
    ret
dsp_write:
    push ax
    push cx
    push dx
    mov ah,al
    mov dx,0x22c
    mov cx,65535
.wait:
    in al,dx
    test al,0x80
    jz .ready
    loop .wait
    mov byte [io_error],1
    jmp .done
.ready:
    mov al,ah
    out dx,al
.done:
    pop dx
    pop cx
    pop ax
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
offsets: db 0,3,1,4,2,5,8,11,9,12,10,13,16,19,17,20,18,21
opl_port: dw 0x388
buffer_offset: dw 0
saved_speaker: db 0
saved_pic: db 0
hold: db 0
sb2: db 0
io_error: db 0
banner: db 'OPL2 / REAL DOS / TIMERS / MELODIC DEVELOPMENT',13,10,'$'
timers_ok: db 'OPL2-TIMERS-OK',13,10,'$'
sine_marker: db 'OPL2-SINE-440',13,10,'$'
wave_marker: db 'OPL2-RECTIFIED-880',13,10,'$'
envelope_marker: db 'OPL2-ENVELOPE',13,10,'$'
fm_marker: db 'OPL2-FM-FEEDBACK',13,10,'$'
mix_marker: db 'OPL2-MIX-440-660',13,10,'$'
mix_pcm_marker: db 'OPL2-MIX-250-440-660',13,10,'$'
hold_marker: db 'OPL2-HOLD-READY',13,10,'Use AUDIO SETTINGS for independent FM volume/mute.',13,10,'$'
done: db 'OPL2-DOS-RETURN-OK',13,10,'$'
error: db 'OPL2-FAILED',13,10,'$'
align 16
buffer: times 16384 db 0
