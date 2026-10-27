; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original SB2 DSP-2.01 high-speed output probe, Apache-2.0.
; /H holds high-speed DMA audio for native GUI volume/mute acceptance.
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
    mov dx,banner
    call print
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .init
    lodsb
    and al,0xdf
    cmp al,'H'
    jne .next
    mov byte [hold],1
.next:
    loop .args
.init:
    call dsp_reset
    mov al,0xe1
    call dsp_write
    call dsp_read
    cmp al,2
    jne failed
    call dsp_read
    cmp al,1
    jne failed
    ; Reserve and fill an actual DOS-owned buffer, aligned to a physical 8-KiB boundary.
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
    cmp byte [hold],0
    jne holding

    mov dx,single_marker
    call print
    mov al,0x49
    call program_dma
    mov al,224               ; 31,250 samples/s -> 976.5625-Hz square wave
    call time_constant
    mov ax,4095
    call block_size
    mov word [irq_count],0
    mov al,0xd1
    call dsp_write
    mov al,0x91
    call dsp_write
    call busy_delay
    call require_busy
    ; Intentional raw writes while busy: no command/parameter may reach the parser.
    mov dx,0x22c
    mov al,0xd0
    out dx,al
    mov al,0xd3
    out dx,al
    mov al,0xe1
    out dx,al
    mov al,0x40
    out dx,al
    mov al,131
    out dx,al
    mov al,0x48
    out dx,al
    xor al,al
    out dx,al
    out dx,al
    mov dx,0x22e
    in al,dx
    test al,0x80
    jnz failed
    mov bx,1
    call wait_irqs
    mov dx,0x22c
    in al,dx
    test al,0x80
    jnz failed
    call quiet
    mov dx,single_ok
    call print
    mov cx,4
    call wait_ticks

    mov dx,auto_marker
    call print
    mov al,0x59
    call program_dma
    mov al,233               ; 43,478.26087 samples/s, not fictional 44.1 kHz
    call time_constant
    mov ax,2047
    call block_size
    mov word [irq_count],0
    mov byte [reset_on_third],1
    mov al,0xd1
    call dsp_write
    mov al,0x90
    call dsp_write
    mov bx,3
    call wait_irqs            ; third IRQ escapes high speed through the reset port
    cmp word [irq_count],3
    jne failed
    mov al,0xd8
    call dsp_write
    call dsp_read
    cmp al,255               ; the high-speed escape must retain speaker state
    jne failed
    mov dx,auto_ok
    call print

    mov dx,constant_marker
    call print
    mov word [irq_count],0
    mov al,0x80              ; no new time constant: 32 retained 23-us periods
    call dsp_write
    mov al,31
    call dsp_write
    xor al,al
    call dsp_write
    mov bx,1
    call wait_irqs
    call quiet
    mov dx,constant_ok
    call print
    mov cx,4
    call wait_ticks

    mov dx,normal_marker
    call print
    mov al,0x59
    call program_dma
    mov al,131
    call time_constant
    mov word [irq_count],0
    mov al,0xd1
    call dsp_write
    mov al,0x1c              ; no 48h: retained 2,048-byte DSP block
    call dsp_write
    mov al,0xda
    call dsp_write
    mov bx,1
    call wait_irqs
    call quiet
    mov dx,normal_ok
    call print
    mov cx,4
    call wait_ticks

    mov dx,abort_marker
    call print
    mov al,0x59
    call program_dma
    mov al,224
    call time_constant
    mov ax,65535
    call block_size
    mov word [irq_count],0
    mov al,0xd1
    call dsp_write
    mov al,0x90
    call dsp_write
    call busy_delay
    call require_busy
    mov dx,0x22c
    mov al,0xda              ; ignored auto-init exit and speaker commands
    out dx,al
    mov al,0xd3
    out dx,al
    mov cx,2
    call wait_ticks
    cmp word [irq_count],0
    jne failed
    mov al,5
    out 0x0a,al
    call dsp_reset
    cmp word [irq_count],0
    jne failed
    call quiet
    mov dx,abort_ok
    call print
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
    mov al,0x59
    call program_dma
    mov al,233
    call time_constant
    mov ax,65535
    call block_size
    mov al,0xd1
    call dsp_write
    mov al,0x90
    call dsp_write
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
; Requires the actual DSP busy state at this controlled test boundary.
require_busy:
    mov dx,0x22c
    in al,dx
    test al,0x80
    jz failed
    ret
; Executes a bounded guest delay while the DSP remains in its observed busy phase.
busy_delay:
    push cx
    mov cx,64
.wait:
    loop .wait
    pop cx
    ret
; Sends the explicitly chosen legacy DSP sample-rate time constant.
time_constant:
    push ax
    mov al,0x40
    call dsp_write
    pop ax
    call dsp_write
    ret
; Sends the selected block length through the actual DSP interface.
block_size:
    push ax
    mov al,0x48
    call dsp_write
    pop ax
    push ax
    call dsp_write
    pop ax
    mov al,ah
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
    mov byte [reset_on_third],0
    mov al,5
    out 0x0a,al
    call dsp_reset           ; also leaves a still-active high-speed hold/failed run
    call quiet
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
    push cx
    push dx
    push ds
    push cs
    pop ds
    mov dx,0x22e
    in al,dx
    inc word [irq_count]
    cmp byte [reset_on_third],1
    jne .eoi
    cmp word [irq_count],3
    jne .eoi
    mov byte [reset_on_third],0
    call dsp_reset
.eoi:
    mov al,0x20
    out 0x20,al
    pop ds
    pop dx
    pop cx
    pop ax
    iret
; Performs the real DSP reset handshake and checks its AA reply.
dsp_reset:
    push cx
    mov dx,0x226
    mov al,1
    out dx,al
    mov cx,64
.hold:
    loop .hold
    xor al,al
    out dx,al
    call dsp_read
    cmp al,0xaa
    je .done
    mov byte [io_error],1
.done:
    pop cx
    ret
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
    jmp .done
.ready:
    mov al,ah
    out dx,al
.done:
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
    jmp .done
.ready:
    mov dx,0x22a
    in al,dx
.done:
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
    mov si,dx
.wait:
    xor ah,ah
    int 0x1a
    sub dx,si
    cmp dx,bx
    jb .wait
    popa
    ret
; Prints the probe's diagnostic text through its actual guest DOS/BIOS output service.
print:
    mov ah,9
    int 0x21
    ret
buffer_offset dw 0
old_irq dw 0,0
irq_count dw 0
saved_pic db 0
installed db 0
io_error db 0
hold db 0
reset_on_third db 0
banner db 'SB2 / REAL DOS / HIGH-SPEED OUTPUT DMA',13,10,'$'
single_marker db 'SB2-HS-SINGLE-31250',13,10,'$'
single_ok db 'SB2-HS-SINGLE-IRQ-OK',13,10,'$'
auto_marker db 'SB2-HS-AUTO-43478',13,10,'$'
auto_ok db 'SB2-HS-AUTO-THREE-IRQS-RESET-OK',13,10,'$'
constant_marker db 'SB2-HS-PRESERVED-TC',13,10,'$'
constant_ok db 'SB2-HS-PRESERVED-TC-IRQ-OK',13,10,'$'
normal_marker db 'SB2-HS-NORMAL-8000',13,10,'$'
normal_ok db 'SB2-HS-RESTORED-BLOCK-OK',13,10,'$'
abort_marker db 'SB2-HS-PARTIAL-ABORT',13,10,'$'
abort_ok db 'SB2-HS-ABORT-NO-IRQ-OK',13,10,'$'
done db 'SB2-HS-DOS-RETURN-OK',13,10,'$'
error db 'SB2-HS-FAILED',13,10,'$'
hold_marker db 'SB2-HS-HOLD-READY',13,10,'Use AUDIO SETTINGS for high-speed PCM mute/volume.',13,10,'$'
align 16
buffer times 16384 db 0
