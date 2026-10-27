; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own CT1740 MPU-UART transport/shared-IRQ probe; no host MIDI or synthesizer is involved.
bits 16
cpu 486
org 0x100
%ifndef MPUBASE
%define MPUBASE 0x330
%endif
%ifndef SBBASE
%define SBBASE 0x220
%endif
%ifndef SBIRQ
%define SBIRQ 5
%endif
%if SBIRQ < 8
%define VECTOR (8+SBIRQ)
%else
%define VECTOR (0x70+SBIRQ-8)
%endif

; Reserve a real DOS block, own the interrupt vector, and mask the shared request until three sources are pending.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    mov sp,stack_top
    mov bx,(program_end-$$+0x10f)/16
    mov ah,0x4a
    int 0x21
    jc failed
    mov [proof_cs],cs
    mov ax,0x3500+VECTOR
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    push cs
    pop es
    mov dx,irq_midi
    mov ax,0x2500+VECTOR
    int 0x21
    in al,0x21
    mov [old_master],al
    in al,0xa1
    mov [old_slave],al
%if SBIRQ < 8
    in al,0x21
    or al,1<<SBIRQ
    out 0x21,al
%else
    in al,0xa1
    or al,1<<(SBIRQ-8)
    out 0xa1,al
%endif
    sti
    mov dx,MPUBASE
    mov al,0x90
    out dx,al                   ; deliberate data outside UART mode must not reach the wire
    call dsp_reset
    mov al,0xff
    call mpu_command
    call ack
    mov al,0x3f
    call mpu_command
    call ack
    mov al,0xac
    call mpu_command            ; unsupported intelligent command must not invent a response
    call wait_two_ticks
    mov dx,MPUBASE+1
    in al,dx
    test al,0x80
    jz failed
    xor di,di
.transmit:
    mov ax,di
    call record_transmit
    inc di
    cmp di,256
    jb .transmit
    call wait_two_ticks
    mov dx,MPUBASE
    mov ax,0xa55a
    out dx,ax
    in ax,dx
    mov [wide_result],ax
    cmp ax,0xffff
    jne failed
    mov al,0x96
    call mpu_data
    mov al,0xff
    call mpu_command            ; abort this in-flight frame before its stop bit
    call ack
    mov al,0x3f
    call mpu_command
    call ack
    mov si,fill_text
    call print
    call wait_enter
    call wait_two_ticks         ; the outer peer sends 32 complete frames into a 16-byte buffer
    xor di,di
.fill:
    call mpu_read
    mov [fill_data+di],al
    cmp al,[fill_index]
    jne failed
    inc byte [fill_index]
    inc di
    cmp di,15
    jb .fill
    mov al,0xf2
    call dsp_command
    mov al,0xf3
    call dsp_command
    call wait_two_ticks
    call mixer_read
    mov [triple_before],al
    cmp al,7
    jne failed
    mov dx,MPUBASE+1
    in al,dx                    ; status must not acknowledge any source
    mov dx,SBBASE+4
    mov al,0x82
    out dx,al
    inc dx
    mov al,0xff
    out dx,al                   ; mixer82 is read-only
    mov dx,SBBASE+0x0f
    in ax,dx                    ; unsupported wide read cannot acknowledge IRQ16
    call mixer_read
    cmp al,7
    jne failed
    mov dx,SBBASE+0x0e
    in al,dx
    call mixer_read
    mov [triple_after_e],al
    cmp al,6
    jne failed
    call mpu_read
    mov [fill_data+15],al
    cmp al,15
    jne failed
    call mixer_read
    mov [triple_after_data],al
    cmp al,2
    jne failed
    mov dx,SBBASE+0x0e
    in al,dx
    call mixer_read
    cmp al,2
    jne failed
    mov dx,SBBASE+0x0f
    in al,dx
    call mixer_read
    mov [triple_after_f],al
    test al,al
    jnz failed
    mov si,cancel_text
    call print
    call wait_enter
    mov al,0xff
    call mpu_command            ; the just-started outer frame must finish on its wire but be ignored after reset
    call ack
    mov al,0x3f
    call mpu_command
    call ready_input            ; keep this UART ACK pending together with two actual DSP software requests
    mov al,0xf2
    call dsp_command
    mov al,0xf3
    call dsp_command
    call wait_two_ticks
    call mixer_read
    cmp al,7
    jne failed
%if SBIRQ < 8
    in al,0x21
    and al,0xff-(1<<SBIRQ)
    out 0x21,al
%else
    in al,0x21
    and al,0xfb
    out 0x21,al
    in al,0xa1
    and al,0xff-(1<<(SBIRQ-8))
    out 0xa1,al
%endif
    mov ecx,1000000
.shared_wait:
    cmp word [rx_count],1
    je .shared_done
    loop .shared_wait
    jmp failed
.shared_done:
    cmp word [shared_check],1
    jne failed
    mov si,irq_text
    call print
    call wait_enter
    xor di,di
.duplex:
    mov ax,255
    sub ax,di
    call record_transmit
    inc di
    cmp di,128
    jb .duplex
    mov ecx,2000000
.receive_wait:
    cmp word [rx_count],129
    je .received
    loop .receive_wait
    jmp failed
.received:
    call wait_two_ticks
    mov al,0xff
    call mpu_command
    call ack
    cmp word [handler_errors],0
    jne failed
    call mixer_read
    test al,al
    jnz failed
    cli
    mov al,[old_master]
    out 0x21,al
    mov al,[old_slave]
    out 0xa1,al
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x2500+VECTOR
    int 0x21
    push cs
    pop ds
    sti
    mov word [complete],0x600d
    mov si,proof_text
    call print
    mov ax,cs
    call hex_word
    mov si,colon
    call print
    mov ax,proof
    call hex_word
    mov si,newline
    call print
    mov si,ok_text
    call print
    mov dx,(program_end-$$+0x10f)/16
    mov ax,0x3100               ; retain real DOS-owned proof memory until reset
    int 0x21

; Read one live CT1745 source register without acknowledging it.
mixer_read:
    mov dx,SBBASE+4
    mov al,0x82
    out dx,al
    inc dx
    in al,dx
    ret

; Write a ready MPU data byte and preserve the caller's loop registers.
mpu_data:
    push bx
    push ecx
    mov bl,al
    mov dx,MPUBASE+1
    mov ecx,2000000
.wait:
    in al,dx
    test al,0x40
    jz .ready
    loop .wait
    jmp failed
.ready:
    dec dx
    mov al,bl
    out dx,al
    pop ecx
    pop bx
    ret

; Store only the source byte actually submitted through a normal data-port write.
record_transmit:
    push bx
    mov bx,[tx_count]
    mov [tx_data+bx],al
    inc word [tx_count]
    pop bx
    jmp mpu_data

; Send a real command after polling its output-ready bit, never assuming a fixed CPU delay.
mpu_command:
    push bx
    push ecx
    mov bl,al
    mov dx,MPUBASE+1
    mov ecx,2000000
.wait:
    in al,dx
    test al,0x40
    jz .ready
    loop .wait
    jmp failed
.ready:
    mov al,bl
    out dx,al
    pop ecx
    pop bx
    ret

; Wait for an actual input-ready state without consuming its byte.
ready_input:
    push ecx
    mov dx,MPUBASE+1
    mov ecx,2000000
.wait:
    in al,dx
    test al,0x80
    jz .ready
    loop .wait
    jmp failed
.ready:
    pop ecx
    ret

; Consume exactly one real MPU data register byte.
mpu_read:
    call ready_input
    dec dx
    in al,dx
    ret

; Check a genuine command ACK and count the bytes handled outside the installed IRQ routine.
ack:
    call mpu_read
    cmp al,0xfe
    jne failed
    inc word [polled_acks]
    ret

; Perform the original DSP reset handshake and read its independent AAh response.
dsp_reset:
    mov dx,SBBASE+6
    mov al,1
    out dx,al
    mov cx,64
.hold:
    loop .hold
    xor al,al
    out dx,al
    mov dx,SBBASE+0x0e
    mov ecx,2000000
.wait:
    in al,dx
    test al,0x80
    jnz .ready
    loop .wait
    jmp failed
.ready:
    mov dx,SBBASE+0x0a
    in al,dx
    cmp al,0xaa
    jne failed
    ret

; Submit a ready actual DSP command; F2/F3 raise width-specific diagnostic IRQ latches without DMA.
dsp_command:
    push bx
    push ecx
    mov bl,al
    mov dx,SBBASE+0x0c
    mov ecx,1000000
.wait:
    in al,dx
    test al,0x80
    jz .ready
    loop .wait
    jmp failed
.ready:
    mov al,bl
    out dx,al
    pop ecx
    pop bx
    ret

; Synchronize an actual outer packet with a guest KBC/INT16 acknowledgement rather than the host's batch boundary.
wait_enter:
    pushad
.key:
    xor ah,ah
    int 0x16
    cmp al,13
    jne .key
    popad
    ret

; Wait across a partial plus a complete BIOS tick, allowing real timer/device interrupts to run.
wait_two_ticks:
    pushad
    xor ah,ah
    int 0x1a
    mov bx,dx
.wait:
    hlt
    xor ah,ah
    int 0x1a
    sub dx,bx
    cmp dx,2
    jb .wait
    popad
    ret

; Drain each actual shared IRQ source through its own port, record evidence, acknowledge the PIC and IRET.
irq_midi:
    pushad
    push ds
    push cs
    pop ds
    mov bx,[irq_count]
    cmp bx,129
    jae .error
    shl bx,2
    call mixer_read
    mov [irq_rows+bx],al
    mov cl,al
    cmp al,7
    jne .not_shared
    inc word [shared_check]
.not_shared:
    test cl,4
    jz .dsp8
.input:
    mov dx,MPUBASE+1
    in al,dx
    mov [irq_rows+bx+1],al
    test al,0x80
    jnz .dsp8
    dec dx
    in al,dx
    mov di,[rx_count]
    cmp di,129
    jae .error
    mov [rx_rows+di],al
    inc word [rx_count]
    inc byte [irq_rows+bx+3]
    jmp .input
.dsp8:
    test cl,1
    jz .dsp16
    mov dx,SBBASE+0x0e
    in al,dx
.dsp16:
    test cl,2
    jz .finish
    mov dx,SBBASE+0x0f
    in al,dx
.finish:
    call mixer_read
    mov [irq_rows+bx+2],al
    inc word [irq_count]
    jmp .eoi
.error:
    inc word [handler_errors]
.eoi:
    mov al,0x20
%if SBIRQ >= 8
    out 0xa0,al
%endif
    out 0x20,al
    pop ds
    popad
    iret

; Emit real guest teletype text, with E9 mirrored only by DOSEBios.
print:
    pushad
.next:
    lodsb
    test al,al
    jz .done
    mov ah,0x0e
    mov bx,7
    int 0x10
    jmp .next
.done:
    popad
    ret

; Print an actual segment/offset through the guest BIOS in hexadecimal.
hex_word:
    pushad
    mov di,ax
    mov cx,4
.digit:
    rol di,4
    mov ax,di
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .emit
    add al,7
.emit:
    mov ah,0x0e
    mov bx,7
    int 0x10
    loop .digit
    popad
    ret

; Mark a real guest failure without synthesizing a successful return or changing host state.
failed:
    mov si,failed_text
    call print
    mov ax,0x4c01
    int 0x21

code_end:
fill_text: db 'MPU-FILL',13,10,0
cancel_text: db 'MPU-CANCEL',13,10,0
irq_text: db 'MPU-IRQ',13,10,0
proof_text: db 'MPU-PROOF=',0
ok_text: db 'MPU-OK',13,10,0
failed_text: db 'MPU-FAILED',13,10,0
newline: db 13,10,0
colon: db ':',0
proof: db 'DOSE-MPU-P1',0,0,0,0,0
    dw code_end,tx_data,rx_rows,irq_rows,guard,program_end
proof_cs: dw 0
sb_base: dw SBBASE
mpu_base: dw MPUBASE
irq_num: dw SBIRQ
complete: dw 0
irq_count: dw 0
handler_errors: dw 0
polled_acks: dw 0
wide_result: dw 0
triple_before: db 0
triple_after_e: db 0
triple_after_data: db 0
triple_after_f: db 0
shared_check: dw 0
tx_count: dw 0
rx_count: dw 0
old_irq: dd 0
old_master: db 0
old_slave: db 0
fill_index: db 0
fill_data: times 16 db 0
tx_data: times 384 db 0
rx_rows: times 129 db 0
irq_rows: times 129*4 db 0
guard: times 32 db 0xa7
    times 4096 db 0
stack_top:
program_end:
