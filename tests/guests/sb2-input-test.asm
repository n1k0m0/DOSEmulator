; Original real-DOS SB2 ADC/DMA1/IRQ5 and capture/playback probe, Apache-2.0.
; A220 I5 D1. Default expects diagnostic virtual-time ramp; /S expects an
; unconnected midpoint input. No OS, driver, ROM, font or sound data.
bits 16
cpu 486
org 0x100
%ifndef SB_BASE
%define SB_BASE 0x220
%endif
%ifndef SB_IRQ
%define SB_IRQ 5
%endif
%if SB_IRQ >= 8
%define SB_VECTOR (0x70 + SB_IRQ - 8)
%else
%define SB_VECTOR (8 + SB_IRQ)
%endif
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    in al,0x21
    mov [saved_pic],al
%if SB_IRQ >= 8
    in al,0xa1
    mov [saved_slave],al
%endif
    movzx cx,byte [0x80]
    mov si,0x81
.args:
    jcxz .reset
    lodsb
    and al,0xdf
    cmp al,'S'
    jne .next
    mov byte [midpoint],1
.next:
    loop .args
.reset:
%ifdef SB_RESOURCE_TEST
    call test_fm_alias
%endif
    call reset_dsp
    mov al,0xe1
    call dsp_write
    call dsp_read
    cmp al,2
    jne failed
    call dsp_read
    cmp al,1
    jne failed
    mov al,0x20
    call dsp_write
    call dsp_read
    mov [direct_sample],al
    cmp byte [midpoint],0
    je .allocate
    cmp al,128
    jne failed
.allocate:
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov ebx,eax
    add eax,buffer
    add eax,127
    and eax,0xffffff80
    mov [dma_physical],eax
    sub eax,ebx
    mov [buffer_offset],ax
    mov ax,0x3500 + SB_VECTOR
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    mov dx,irq5
    mov ax,0x2500 + SB_VECTOR
    int 0x21
    mov byte [installed],1
%if SB_IRQ >= 8
    mov al,[saved_slave]
    and al,0xff ^ (1 << (SB_IRQ - 8))
    out 0xa1,al
    mov al,[saved_pic]
    and al,0xff ^ (1 << 2)
%else
    mov al,[saved_pic]
    and al,0xff ^ (1 << SB_IRQ)
%endif
    out 0x21,al

    ; Each phase uses a real 128-byte DMA buffer, fenced with sentinels.
    mov al,0x45
    call prepare_buffer
    mov al,131
    call time_constant
    mov dx,single_begin
    call print
    mov al,0x24
    call dsp_write
    mov al,127
    call dsp_write
    xor al,al
    call dsp_write
    mov bx,1
    call wait_irqs
    xor di,di
    call check_and_save
    mov dx,single_ok
    call print

    mov al,0x55
    call prepare_buffer
    mov byte [auto_action],1
    call block_64
    mov dx,auto_begin
    call print
    mov al,0x2c
    call dsp_write
    mov bx,4
    call wait_irqs
    mov di,128
    call check_and_save
    mov dx,auto_ok
    call print

    mov al,0x45
    call prepare_buffer
    mov al,188                 ; 14,705.882 Hz, DSP 2.01 input range 13..15 kHz
    call time_constant
    mov al,0x48
    call dsp_write
    mov al,127
    call dsp_write
    xor al,al
    call dsp_write
    mov dx,high_single_begin
    call print
    mov al,0x99
    call dsp_write
    mov bx,1
    call wait_irqs
    mov di,256
    call check_and_save
    mov dx,high_single_ok
    call print

    mov al,0x55
    call prepare_buffer
    mov byte [auto_action],2
    call block_64
    mov dx,high_auto_begin
    call print
    mov al,0x98
    call dsp_write
    mov bx,4
    call wait_irqs
    mov di,384
    call check_and_save
    mov dx,high_auto_ok
    call print

    ; Play the actual last ADC buffer through the real output DMA/DAC.
    ; Reprogramming DMA changes its direction; the recorded bytes stay put.
    mov al,0x49
    call program_dma
    mov word [irq_count],0
    mov byte [auto_action],0
    mov al,131
    call time_constant
    mov al,0xd1
    call dsp_write
    mov dx,play_begin
    call print
    mov al,0x14
    call dsp_write
    mov al,127
    call dsp_write
    xor al,al
    call dsp_write
    mov bx,1
    call wait_irqs
    mov al,0xd3
    call dsp_write
    mov dx,play_ok
    call print
    cmp byte [io_error],0
    jne failed
    call cleanup
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
failed:
    call cleanup
    mov dx,error
    call print
    mov ax,0x4c01
    int 0x21

prepare_buffer:
    push ax
    push cs
    pop es
    mov di,[buffer_offset]
    mov byte [es:di-1],0x5a
    mov cx,128
    mov al,0xcc
    rep stosb
    mov byte [es:di],0xa5
    mov word [irq_count],0
    mov byte [auto_action],0
    pop ax
    call program_dma
    ret
program_dma:
    mov [dma_mode],al
    mov al,5
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov eax,[dma_physical]
    out 2,al
    mov al,ah
    out 2,al
    shr eax,16
    out 0x83,al
    mov al,127
    out 3,al
    xor al,al
    out 3,al
    mov al,[dma_mode]
    out 0x0b,al
    in al,8                     ; clear the old terminal-count status
    mov al,1
    out 0x0a,al
    ret
check_and_save:
    push di
    mov si,[buffer_offset]
    cmp byte [si-1],0x5a
    jne failed
    cmp byte [si+128],0xa5
    jne failed
    in al,8
    test al,2
    jz failed
    mov cx,128
    cmp byte [midpoint],1
    jne .ramp
.silence:
    cmp byte [si],128
    jne failed
    inc si
    loop .silence
    jmp .save
.ramp:
    mov bl,[si]
    inc si
    dec cx
    mov byte [varied],0
.delta:
    mov al,[si]
    mov bh,al
    sub al,bl
    cmp al,1
    ja failed
    or [varied],al
    mov bl,bh
    inc si
    loop .delta
    cmp byte [varied],1
    jne failed
.save:
    pop di
    add di,captured
    push cs
    pop es
    mov si,[buffer_offset]
    mov cx,128
    rep movsb
    ret
time_constant:
    push ax
    mov al,0x40
    call dsp_write
    pop ax
    call dsp_write
    ret
block_64:
    mov al,0x48
    call dsp_write
    mov al,63
    call dsp_write
    xor al,al
    call dsp_write
    ret
reset_dsp:
    mov dx,SB_BASE + 6
    mov al,1
    out dx,al
    mov cx,64
.delay:
    loop .delay
    xor al,al
    out dx,al
    call dsp_read
    cmp al,0xaa
    jne .bad
    ret
.bad:
    mov byte [io_error],1
    ret
cleanup:
    mov al,5
    out 0x0a,al
    call reset_dsp
    mov al,[saved_pic]
    out 0x21,al
%if SB_IRQ >= 8
    mov al,[saved_slave]
    out 0xa1,al
%endif
    cmp byte [installed],0
    je .done
    push ds
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x2500 + SB_VECTOR
    int 0x21
    pop ds
    mov byte [installed],0
.done:
    ret
irq5:
    pushad
    push ds
    push cs
    pop ds
    mov dx,SB_BASE + 0x0e
    in al,dx
    inc word [irq_count]
    cmp byte [auto_action],1
    jne .high
    cmp word [irq_count],3
    jne .eoi
    mov al,0xda
    call dsp_write
    jmp .eoi
.high:
    cmp byte [auto_action],2
    jne .eoi
    cmp word [irq_count],4
    jne .eoi
    call reset_dsp             ; actual high-speed abort, preserving DSP parameters
.eoi:
    mov al,0x20
%if SB_IRQ >= 8
    out 0xa0,al
%endif
    out 0x20,al
    pop ds
    popad
    iret
dsp_write:
    push ax
    push cx
    push dx
    mov ah,al
    mov dx,SB_BASE + 0x0c
    mov cx,65535
.wait:
    in al,dx
    test al,128
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
dsp_read:
    push cx
    push dx
    mov dx,SB_BASE + 0x0e
    mov cx,65535
.wait:
    in al,dx
    test al,128
    jnz .ready
    loop .wait
    mov byte [io_error],1
    mov al,255
    jmp .done
.ready:
    mov dx,SB_BASE + 0x0a
    in al,dx
.done:
    pop dx
    pop cx
    ret
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
print:
    mov ah,9
    int 0x21
    ret

%ifdef SB_RESOURCE_TEST
; Original timer/alias test. Register address goes through the relocated SB
; alias and its data through 389h, proving both port pairs share one chip.
test_fm_alias:
    mov bx,0x6004
    call fm_write
    mov dx,SB_BASE + 8
    in al,dx
    and al,0xe0
    jnz failed
    mov dx,SB_BASE + 8
    mov al,2
    out dx,al
    mov cx,6
.address_wait:
    in al,dx
    loop .address_wait
    mov dx,0x389
    mov al,255
    out dx,al
    mov dx,SB_BASE + 8
    mov cx,35
.data_wait:
    in al,dx
    loop .data_wait
    mov bx,0x2104
    call fm_write
    mov dx,0x388
    mov cx,256
.overflow_wait:
    in al,dx
    loop .overflow_wait
    and al,0xe0
    cmp al,0xc0
    jne failed
    mov bx,4
    call fm_write
    mov bx,0x8004
    call fm_write
    mov dx,SB_BASE + 8
    in al,dx
    and al,0xe0
    jnz failed
    mov dx,fm_alias_ok
    call print
    ret
fm_write:
    mov dx,SB_BASE + 8
    mov al,bl
    out dx,al
    mov cx,6
.address_wait:
    in al,dx
    loop .address_wait
    inc dx
    mov al,bh
    out dx,al
    dec dx
    mov cx,35
.data_wait:
    in al,dx
    loop .data_wait
    ret
fm_alias_ok: db 'SB2-RESOURCE-FM-ALIASES-OK',13,10,'$'
%endif

old_irq: dw 0,0
irq_count: dw 0
saved_pic: db 0
%if SB_IRQ >= 8
saved_slave: db 0
%endif
installed: db 0
io_error: db 0
auto_action: db 0
dma_mode: db 0
buffer_offset: dw 0
varied: db 0
capture_header: db 'SB2-ADC-RESULTS-V1'
dma_physical: dd 0
direct_sample: db 0
midpoint: db 0
captured: times 512 db 0
single_begin: db 'ADC-SINGLE-BEGIN',13,10,'$'
single_ok: db 'ADC-SINGLE-128-OK',13,10,'$'
auto_begin: db 'ADC-AUTO-BEGIN',13,10,'$'
auto_ok: db 'ADC-AUTO-256-OK',13,10,'$'
high_single_begin: db 'ADC-HIGH-SINGLE-BEGIN',13,10,'$'
high_single_ok: db 'ADC-HIGH-SINGLE-128-OK',13,10,'$'
high_auto_begin: db 'ADC-HIGH-AUTO-BEGIN',13,10,'$'
high_auto_ok: db 'ADC-HIGH-AUTO-256-OK',13,10,'$'
play_begin: db 'ADC-PLAYBACK-BEGIN',13,10,'$'
play_ok: db 'ADC-PLAYBACK-128-OK',13,10,'$'
done: db 'ADC-DOS-RETURN-OK',13,10,'$'
error: db 'ADC-FAILED',13,10,'$'
align 128
buffer: times 512 db 0
