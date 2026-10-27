; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual DOS handlers for CT1740 software IRQs; no synthetic DMA completion.
bits 16
cpu 486
org 0x100
%ifndef SBBASE
%define SBBASE 0x220
%endif
%ifndef SBIRQ
%define SBIRQ 5
%endif
%if SBIRQ < 8
%define VECTOR (SBIRQ+8)
%else
%define VECTOR (SBIRQ+0x68)
%endif
; Save the real DOS vector/PIC masks and exercise both software IRQ sources.
start:
    push cs
    pop ds
    mov ax,0x3500+VECTOR
    int 0x21
    mov [old_vector],bx
    mov [old_vector+2],es
    mov dx,handler
    mov ax,0x2500+VECTOR
    int 0x21
    in al,0x21
    mov [master_mask],al
    in al,0xa1
    mov [slave_mask],al
    cli
%if SBIRQ < 8
    in al,0x21
    and al,~(1 << SBIRQ)
    out 0x21,al
%else
    and al,~(1 << (SBIRQ-8))
    out 0xa1,al
    in al,0x21
    and al,0xfb
    out 0x21,al
%endif
    mov dx,SBBASE+6
    mov al,1
    out dx,al
    mov cx,100
.reset_delay:
    loop .reset_delay
    xor al,al
    out dx,al
    mov dx,SBBASE+0xe
    mov cx,65535
.reset_ready:
    in al,dx
    test al,0x80
    jnz .reset_data
    loop .reset_ready
    jmp failed
.reset_data:
    mov dx,SBBASE+0xa
    in al,dx
    cmp al,0xaa
    jne failed
    mov al,0x80
    call mixer_read
    mov [resource],al
    mov al,0x0e
    call mixer_read
    mov [legacy_before],al
    and al,0xfd
    mov ah,al
    mov al,0x0e
    call mixer_write
    mov al,0x0e
    call mixer_read
    mov [legacy_after],al
    sti
    mov al,0xf2
    call send
    mov bx,1
    call wait_count
    mov al,0xf3
    call send
    mov bx,2
    call wait_count
    cli
    mov al,0xf2
    call send
    mov al,0xf3
    call send
    sti
    mov bx,3
    call wait_count
    cli
    mov al,0xf2
    call send
    mov al,0xf2
    call send
    sti
    mov bx,4
    call wait_count
    cli
    mov ax,0x0080
    call mixer_write            ; keep card latch with no routed IRQ
    mov al,0xf2
    call send
    mov al,0x82
    call mixer_read
    cmp al,1
    jne failed
    mov ah,[resource]
    mov al,0x80
    call mixer_write
    sti
    mov bx,5
    call wait_count
    cmp byte [error],0
    jne failed
    mov word [completed],0x55aa
    mov si,ready
    call debug
    jmp cleanup
; Wait for real interrupt delivery with an actual BIOS-tick timeout.
wait_count:
    push ax
    push cx
    push dx
    xor ah,ah
    int 0x1a
    mov cx,dx
.wait:
    cmp [handler_count],bx
    jae .done
    push cx
    xor ah,ah
    int 0x1a
    pop cx
    sub dx,cx
    cmp dx,4
    jb .wait
    jmp failed
.done:
    pop dx
    pop cx
    pop ax
    ret
; Send one command after busy clears and wait for its actual processing.
send:
    push ax
    push cx
    push dx
    mov ah,al
    mov dx,SBBASE+0xc
    mov cx,65535
.ready:
    in al,dx
    test al,0x80
    jz .write
    loop .ready
    jmp failed
.write:
    mov al,ah
    out dx,al
    mov cx,65535
.processed:
    in al,dx
    test al,0x80
    jz .done
    loop .processed
    jmp failed
.done:
    pop dx
    pop cx
    pop ax
    ret
; Read a mixer register without acknowledging a source.
mixer_read:
    push dx
    mov dx,SBBASE+4
    out dx,al
    inc dx
    in al,dx
    pop dx
    ret
; Write AH to the mixer register in AL.
mixer_write:
    push dx
    mov dx,SBBASE+4
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    pop dx
    ret
; The real IRQ handler records card sources before and after width-specific
; acknowledgment; both EOIs and IRET execute as guest instructions.
handler:
    push ax
    push bx
    push dx
    push ds
    push cs
    pop ds
    mov bx,[handler_count]
    cmp bx,5
    jae .unexpected
    mov al,0x82
    call mixer_read
    mov [sources+bx],al
    mov dx,SBBASE+0xf
    cmp al,2
    jne .ack_first
    dec dx                     ; wrong-width ACK when only IRQ16 is set
.ack_first:
    in al,dx
    mov al,0x82
    call mixer_read
    mov [intermediate+bx],al
    mov dx,SBBASE+0xe
    in al,dx
    inc dx
    in al,dx
    mov al,0x82
    call mixer_read
    mov [cleared+bx],al
    inc word [handler_count]
    jmp .eoi
.unexpected:
    inc byte [error]
.eoi:
    mov al,0x20
%if SBIRQ >= 8
    out 0xa0,al
%endif
    out 0x20,al
    pop ds
    pop dx
    pop bx
    pop ax
    iret
; Keep the source/vector/PIC cleanup observable; no full DSP snapshot promised.
cleanup:
    cli
    mov ah,[legacy_before]
    mov al,0x0e
    call mixer_write
    mov al,[master_mask]
    out 0x21,al
    mov al,[slave_mask]
    out 0xa1,al
    lds dx,[old_vector]
    mov ax,0x2500+VECTOR
    int 0x21
    push cs
    pop ds
    sti
    mov si,returned
    call debug
    mov ax,0x4c00
    cmp word [completed],0x55aa
    je .exit
    inc al
.exit:
    int 0x21
; Expose failure independently of ANSI's console fast path.
failed:
    push cs
    pop ds
    mov si,failure
    call debug
    jmp cleanup
; Emit only our own diagnostic marker through the existing byte debug port.
debug:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug
.done:
    ret
ready db 'SB16-DIAG-PASS',0
returned db 'SB16-DIAG-RETURN',0
failure db 'SB16-DIAG-FAILED',0
old_vector dd 0
master_mask db 0
slave_mask db 0
resource db 0
proof db 'SB16DIAG-PROOF01'
completed dw 0
handler_count dw 0
legacy_before db 0
legacy_after db 0
error db 0
sources times 5 db 0
intermediate times 5 db 0
cleared times 5 db 0
