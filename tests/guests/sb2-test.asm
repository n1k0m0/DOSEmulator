; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS SB2 PCM probe, Apache-2.0. No host audio or DOS-service shortcuts.
; /H holds 250-Hz DMA audio for native GUI acceptance. Requires A220 I5 D1.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    in al,0x21
    mov [saved_pic],al
    in al,0x61
    and al,15
    mov [saved_speaker],al
    mov dx,banner
    call print
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .reset
    lodsb
    and al,0xdf
    cmp al,'H'
    jne .next
    mov byte [hold],1
.next:
    loop .args
.reset:
    mov dx,0x226
    mov al,1
    out dx,al
    mov cx,256
.reset_delay:
    loop .reset_delay
    xor al,al
    out dx,al
    call dsp_read
    cmp al,0xaa
    jne failed
    mov al,0xe1
    call dsp_write
    call dsp_read
    cmp al,2
    jne failed
    call dsp_read
    cmp al,1
    jne failed
    mov al,0xd8
    call dsp_write
    call dsp_read
    test al,al
    jnz failed
    mov al,0x10
    call dsp_write
    mov al,128
    call dsp_write

    ; Align an actual DOS-owned buffer to 8192 physical bytes, so it never crosses a DMA page.
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

    mov ax,0x350d
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    mov dx,irq5
    mov ax,0x250d
    int 0x21
    mov byte [installed],1
    in al,0x21
    and al,0xdf
    out 0x21,al
    sti
    mov al,0xd1
    call dsp_write
    mov al,0xd8
    call dsp_write
    call dsp_read
    cmp al,255
    jne failed
    cmp byte [hold],0
    jne holding

    mov dx,single_marker
    call print
    mov al,0x49
    call program_dma
    mov al,131
    call time_constant
    mov word [irq_count],0
    call single
    mov cx,3
    call wait_ticks
    mov al,0xd0
    call dsp_write
    mov cx,2
    call wait_ticks
    cmp word [irq_count],0
    jne failed
    mov al,0xd4
    call dsp_write
    mov bx,1
    call wait_irqs
    call quiet
    mov dx,single_ok
    call print
    mov cx,4
    call wait_ticks

    mov dx,auto_marker
    call print
    mov byte [auto_exit],1
    call auto_start
    mov bx,4
    call wait_irqs
    call quiet
    cmp word [irq_count],4
    jne failed
    mov dx,auto_ok
    call print
    mov cx,4
    call wait_ticks

    mov dx,rate_marker
    call print
    mov al,0x49
    call program_dma
    mov al,165
    call time_constant
    mov al,0xd1
    call dsp_write
    mov word [irq_count],0
    call single
    mov bx,1
    call wait_irqs
    call quiet
    mov cx,4
    call wait_ticks

    mov dx,mix_marker
    call print
    mov al,0xb6
    out 0x43,al
    mov ax,2712
    out 0x42,al
    mov al,ah
    out 0x42,al
    in al,0x61
    or al,3
    out 0x61,al
    call auto_start
    mov bx,4
    call wait_irqs
    call quiet
    mov al,[saved_speaker]
    out 0x61,al
    mov cx,4
    call wait_ticks
    cmp byte [io_error],0
    jne failed
    call cleanup
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21

holding:
    mov byte [auto_exit],0
    call auto_start
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

; Starts the programmed single-cycle byte-DMA DSP transfer.
single:
    mov al,0x14
    call dsp_write
    mov al,255
    call dsp_write
    mov al,31
    call dsp_write
    ret
; Starts the programmed auto-initialized byte-DMA stream through the real DSP command port.
auto_start:
    mov al,0x59
    call program_dma
    mov al,131
    call time_constant
    mov al,0xd1
    call dsp_write
    mov word [irq_count],0
    mov al,0x48
    call dsp_write
    mov al,255
    call dsp_write
    mov al,7
    call dsp_write
    mov al,0x1c
    call dsp_write
    ret
; Sends the explicitly chosen legacy DSP sample-rate time constant.
time_constant:
    push ax
    mov al,0x40
    call dsp_write
    pop ax
    call dsp_write
    ret
; Programs the actual byte/word DMA controller with the probe's owned physical buffer.
program_dma:
    push eax
    mov ah,al
    mov al,5
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov al,ah
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
    pop eax
    ret
; Stops the controlled DSP stream before releasing this probe's sound/IRQ resources.
quiet:
    mov al,0xd3
    call dsp_write
    ret
; Releases this probe's acquired resources and restores its explicitly saved handler/control state.
cleanup:
    mov al,0xd0
    call dsp_write
    call quiet
    mov al,5
    out 0x0a,al
    mov al,[saved_speaker]
    out 0x61,al
    cmp byte [installed],0
    je .done
    mov al,[saved_pic]
    out 0x21,al
    push ds
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x250d
    int 0x21
    pop ds
.done:
    ret
; Acknowledges the actual byte-DMA sound interrupt and sends the required PIC EOI.
irq5:
    push ax
    push dx
    push ds
    push cs
    pop ds
    mov dx,0x22e
    in al,dx
    inc word [irq_count]
    cmp byte [auto_exit],1
    jne .eoi
    cmp word [irq_count],3
    jne .eoi
    mov al,0xda
    call dsp_write
.eoi:
    mov al,0x20
    out 0x20,al
    pop ds
    pop dx
    pop ax
    iret
; Polls DSP readiness before writing one real command/parameter byte.
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
    jmp .out
.ready:
    mov al,ah
    out dx,al
.out:
    pop dx
    pop cx
    pop ax
    ret
; Polls DSP readiness with a bounded timeout before reading the actual reply byte.
dsp_read:
    push cx
    push dx
    mov dx,0x22e
    mov cx,65535
.wait:
    in al,dx
    test al,0x80
    jnz .ready
    loop .wait
    mov byte [io_error],1
    mov al,255
    jmp .out
.ready:
    mov dx,0x22a
    in al,dx
.out:
    pop dx
    pop cx
    ret
; Waits for the expected actual IRQ-handler count with a bounded guest timeout.
wait_irqs:
    xor ah,ah
    int 0x1a
    mov si,dx
    add si,50
.wait:
    cmp word [irq_count],bx
    jae .done
    xor ah,ah
    int 0x1a
    cmp dx,si
    jae failed
    jmp .wait
.done:
    ret
; Waits for actual BIOS timer ticks rather than advancing virtual time from the host.
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
; Prints the probe's diagnostic text through its actual guest DOS/BIOS output service.
print:
    mov ah,9
    int 0x21
    ret

buffer_offset: dw 0
old_irq: dw 0,0
irq_count: dw 0
saved_pic: db 0
saved_speaker: db 0
installed: db 0
io_error: db 0
auto_exit: db 0
hold: db 0
banner: db 'SB2 DSP 2.01 / REAL DOS / DMA1 / IRQ5',13,10,'$'
single_marker: db 'SB2-SINGLE-250-HZ',13,10,'$'
single_ok: db 'SB2-SINGLE-IRQ-OK',13,10,'$'
auto_marker: db 'SB2-AUTO-250-HZ',13,10,'$'
auto_ok: db 'SB2-AUTO-FOUR-IRQS-OK',13,10,'$'
rate_marker: db 'SB2-RATE-10989-343-HZ',13,10,'$'
mix_marker: db 'SB2-MIX-250-440-HZ',13,10,'$'
hold_marker: db 'SB2-HOLD-READY',13,10,'Use AUDIO SETTINGS for independent PCM volume/mute.',13,10,'$'
done: db 'SB2-DOS-RETURN-OK',13,10,'$'
error: db 'SB2-FAILED',13,10,'$'
align 16
buffer: times 16384 db 0
