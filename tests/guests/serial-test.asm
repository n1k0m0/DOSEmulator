; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS UART probe: only guest IN/OUT, BIOS calls and its own IRQ handler change the device.
bits 16
cpu 486
org 0x100

; Reserves private DOS memory, installs IRQ4, then runs polled BIOS/direct and interrupt-driven cases.
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
    xor ax,ax
    mov es,ax
    mov ax,[es:0x400]
    mov [bda_com],ax
    cmp ax,0x3f8
    jne failed
    mov ax,[es:0x410]
    mov [equipment],ax
    int 0x11
    mov [bios_equipment],ax
    and ax,0x0e00
    cmp ax,0x0200
    jne failed
    mov al,[es:0x47c]
    mov [old_timeout],al
    push cs
    pop es
    mov ax,0x350c
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    push cs
    pop es
    mov dx,irq_serial
    mov ax,0x250c
    int 0x21
    in al,0x21
    mov [old_pic],al
    and al,0xef
    out 0x21,al
    mov si,begin_text
    call print
    mov byte [phase],0
.bios:
    mov al,[phase]
    mov ah,al
    movzx bx,al
    shl bx,1
    mov bx,[divisors+bx]
    mov [divisor],bx
    mov byte [format],3
    shl al,5
    or al,3
    xor ah,ah
    xor dx,dx
    int 0x14
    cmp ah,0x60
    jne failed
    mov al,1
    call new_row
    mov al,[phase]
    add al,0x41
    mov [di+4],al
    mov ah,1
    xor dx,dx
    int 0x14
    test ah,0x80
    jnz failed
    mov ah,2
    int 0x14
    mov [di+5],al
    mov [di+6],ah
    cmp al,[di+4]
    jne failed
    test ah,0x80
    jnz failed
    mov ah,3
    int 0x14
    mov [di+12],ax
    inc byte [phase]
    cmp byte [phase],8
    jb .bios
    mov byte [phase],0
.direct:
    mov al,[phase]
    mov [format],al
    mov word [divisor],12
    call configure
    mov al,2
    call new_row
    mov al,[phase]
    xor al,0xe5
    mov [di+4],al
    mov dx,0x3f8
    out dx,al
    call wait_data
    mov [di+6],al
    mov dx,0x3f8
    in al,dx
    mov [di+5],al
    mov dx,0x3fa
    in al,dx
    mov [di+7],al
    inc byte [phase]
    cmp byte [phase],32
    jb .direct
    mov byte [phase],0
.fifo:
    mov byte [format],3
    mov word [divisor],12
    call configure
    mov dx,0x3fa
    mov al,[phase]
    shl al,6
    or al,1
    out dx,al
    mov dx,0x3fc
    mov al,0x0b
    out dx,al
    mov dx,0x3f9
    mov al,3
    out dx,al
    mov al,3
    call new_row
    mov ax,[rx_count]
    mov [di+12],ax
    add ax,16
    mov [expected_rx],ax
    mov dx,0x3f8
    mov cx,16
    mov al,[phase]
    shl al,5
    mov [di+4],al
.burst:
    out dx,al
    inc al
    loop .burst
    call wait_interrupt_data
    mov word [di+8],16
    mov ax,[irq_count]
    mov [di+10],ax
    inc byte [phase]
    cmp byte [phase],4
    jb .fifo
    mov byte [phase],0
.external:
    mov byte [format],3
    mov word [divisor],12
    call configure
    mov dx,0x3fa
    mov al,1
    out dx,al
    mov dx,0x3fc
    mov al,0x0b
    out dx,al
    mov dx,0x3f9
    mov al,5
    out dx,al
    mov al,4
    call new_row
    mov ax,[rx_count]
    mov [di+12],ax
    inc ax
    mov [expected_rx],ax
    mov si,ready_text
    call print
    mov al,[phase]
    call hex_byte
    mov si,newline
    call print
    mov ah,8
    int 0x21
    cmp al,13
    jne failed
    call wait_interrupt_data
    mov bx,[di+12]
    mov al,[rx_log+bx]
    mov [di+5],al
    mov al,[lsr_log+bx]
    mov [di+6],al
    mov dx,0x3fe
    in al,dx
    mov [di+7],al
    mov word [di+8],1
    mov ax,[irq_count]
    mov [di+10],ax
    inc byte [phase]
    cmp byte [phase],4
    jb .external
    mov byte [phase],0
.overrun:
    cli
    mov byte [format],3
    mov word [divisor],1
    call configure
    mov dx,0x3fa
    mov al,[phase]
    out dx,al
    mov al,5
    call new_row
    mov cx,3
    cmp byte [phase],0
    je .holding
    mov cx,18
.holding:
    mov [di+8],cx
    mov byte [send_byte],0
.send:
    push cx
    call wait_thre
    mov dx,0x3f8
    mov al,[send_byte]
    out dx,al
    inc byte [send_byte]
    pop cx
    loop .send
    call wait_temt
    mov cx,20000
.settle:
    loop .settle
    mov dx,0x3fd
    in al,dx
    mov [di+6],al
    test al,2
    jz failed
    mov cx,1
    cmp byte [phase],0
    je .drain
    mov cx,16
.drain:
    mov bx,overrun_log
    cmp byte [phase],0
    jne .drain_loop
    add bx,16
.drain_loop:
    mov dx,0x3f8
    in al,dx
    mov [bx],al
    inc bx
    loop .drain_loop
    sti
    inc byte [phase]
    cmp byte [phase],2
    jb .overrun
    mov word [divisor],12
    call configure
    mov dx,0x3fc
    mov al,0x1b
    out dx,al
    mov al,6
    call new_row
    mov dx,0x3f8
    mov al,0x96
    mov [di+4],al
    out dx,al
    call wait_data
    mov [di+6],al
    mov dx,0x3f8
    in al,dx
    mov [di+5],al
    mov dx,0x3fe
    in al,dx
    mov [di+7],al
    mov al,7
    call new_row
    mov dx,0x3f8
    mov ax,0x5aa5
    out dx,ax
    in ax,dx
    mov [di+12],ax
    cmp ax,0xffff
    jne failed
    mov ax,0x0496
    xor dx,dx
    int 0x14
    mov [di+8],ax
    cmp ax,0x8096
    jne failed
    mov ax,0x0196
    mov dx,1
    int 0x14
    mov [di+10],ax
    cmp ax,0x8096
    jne failed
    ; Local loopback deasserts the logical DSR/CTS lines without changing the virtual peer.
    mov dx,0x3fc
    mov al,0x10
    out dx,al
    xor ax,ax
    mov es,ax
    mov byte [es:0x47c],0
    mov ax,0x015a
    xor dx,dx
    int 0x14
    mov [timeout_result],ax
    test ah,0x80
    jz failed
    mov al,[old_timeout]
    mov [es:0x47c],al
    push cs
    pop es
    cli
    mov dx,0x3f9
    xor al,al
    out dx,al
    mov dx,0x3fa
    mov al,0
    out dx,al
    mov dx,0x3fc
    mov al,3
    out dx,al
    mov al,[old_pic]
    out 0x21,al
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x250c
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
    mov ax,0x4c00
    int 0x21

; Programs the selected divisor/format through DLAB and selects the polled character path.
configure:
    push ax
    push dx
    mov dx,0x3f9
    xor al,al
    out dx,al
    mov dx,0x3fb
    mov al,[format]
    or al,0x80
    out dx,al
    mov dx,0x3f8
    mov ax,[divisor]
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    mov dx,0x3fb
    mov al,[format]
    out dx,al
    mov dx,0x3fa
    xor al,al
    out dx,al
    mov dx,0x3fc
    mov al,3
    out dx,al
    pop dx
    pop ax
    ret

; Allocates one fixed 16-byte proof row; all fields are observations by the guest.
new_row:
    push ax
    push cx
    mov di,[row_count]
    shl di,4
    add di,rows
    mov cx,8
    xor ax,ax
    rep stosw
    sub di,16
    pop cx
    pop ax
    mov [di],al
    mov al,[format]
    mov [di+1],al
    mov ax,[divisor]
    mov [di+2],ax
    mov word [di+14],0xa75c
    inc word [row_count]
    ret

; Waits for data, holding-register space or full transmitter completion with a hard guest instruction limit.
wait_data:
    mov ah,1
    jmp wait_lsr
; Waits for THR/FIFO space without treating TEMT as the same signal.
wait_thre:
    mov ah,0x20
    jmp wait_lsr
; Waits until the final shift frame has completed.
wait_temt:
    mov ah,0x40
wait_lsr:
    mov ecx,1000000
    mov dx,0x3fd
.poll:
    in al,dx
    test al,ah
    jnz .ready
    dec ecx
    jnz .poll
    jmp failed
.ready:
    ret

; Waits for the installed interrupt handler, not for a host-supplied memory result.
wait_interrupt_data:
    mov ecx,2000000
.poll:
    mov ax,[rx_count]
    cmp ax,[expected_rx]
    jae .ready
    dec ecx
    jnz .poll
    jmp failed
.ready:
    ret

; Acknowledges actual UART sources in priority order and issues the real master-PIC EOI before IRET.
irq_serial:
    pushad
    push ds
    push cs
    pop ds
    inc word [irq_count]
    mov cx,128
.source:
    mov dx,0x3fa
    in al,dx
    test al,1
    jnz .done
    and al,15
    cmp al,2
    je .thre
    test al,al
    jz .modem
    cmp al,4
    je .receive
    cmp al,6
    je .receive
    cmp al,12
    jne .bad
.receive:
    mov dx,0x3fd
    in al,dx
    test al,1
    jz .next
    mov bx,[rx_count]
    cmp bx,256
    jae .bad
    mov [lsr_log+bx],al
    mov dx,0x3f8
    in al,dx
    mov [rx_log+bx],al
    inc word [rx_count]
    jmp .next
.thre:
    inc word [thre_count]
    jmp .next
.modem:
    mov dx,0x3fe
    in al,dx
    inc word [modem_count]
.next:
    loop .source
.bad:
    inc word [handler_errors]
.done:
    mov al,0x20
    out 0x20,al
    pop ds
    popad
    iret

; Prints through actual BIOS teletype; DOSEBios alone mirrors each character once to E9.
print:
    pushad
.char:
    lodsb
    test al,al
    jz .done
    mov ah,0x0e
    mov bx,7
    int 0x10
    jmp .char
.done:
    popad
    ret

; Emits the externally synchronized phase number without a host RAM marker.
hex_byte:
    pushad
    mov ah,al
    shr al,4
    call .digit
    mov al,ah
    and al,15
    call .digit
    popad
    ret
.digit:
    add al,'0'
    cmp al,'9'
    jbe .emit
    add al,7
.emit:
    push ax
    mov ah,0x0e
    mov bx,7
    int 0x10
    pop ax
    ret

; Emits an observed segment or offset in its actual four-digit hexadecimal representation.
hex_word:
    pushad
    mov bx,ax
    mov al,bh
    call hex_byte
    mov al,bl
    call hex_byte
    popad
    ret

; Makes failures explicit; no success row or final completion marker is fabricated.
failed:
    mov si,fail_text
    call print
    mov ax,0x4c01
    int 0x21

code_end:
begin_text: db 'SERIAL-BEGIN',13,10,0
ready_text: db 'SERIAL-EXT-',0
ok_text: db 'SERIAL-OK',13,10,0
fail_text: db 'SERIAL-FAILED',13,10,0
newline: db 13,10,0
proof_text: db 'SERIAL-PROOF=',0
colon: db ':',0
divisors: dw 1047,768,384,192,96,48,24,12
proof:
    db 'DOSE-SERIAL-P1!',0
    dw code_end,rows,rx_log,lsr_log,overrun_log,program_end
proof_cs: dw 0
bda_com: dw 0
equipment: dw 0
bios_equipment: dw 0
row_count: dw 0
irq_count: dw 0
rx_count: dw 0
thre_count: dw 0
modem_count: dw 0
handler_errors: dw 0
timeout_result: dw 0
complete: dw 0
old_irq: dd 0
old_pic: db 0
old_timeout: db 0
phase: db 0
format: db 0
divisor: dw 0
expected_rx: dw 0
send_byte: db 0
rows: times 52*16 db 0
rx_log: times 256 db 0
lsr_log: times 256 db 0
overrun_log: times 17 db 0
    times 32 db 0xa7
    times 4096 db 0
stack_top:
program_end:
