; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own LPT1 DOS probe: /p selects manual external-pin tests; default drives a timed virtual raw-byte peer.
bits 16
cpu 486
org 0x100

; Reserves own DOS memory, discovers real BDA/INT11 state and installs an actual IRQ7 handler.
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
    mov al,[0x83]
    or al,0x20
    cmp al,'p'
    jne .mode_ready
    mov byte [mode],1
.mode_ready:
    xor ax,ax
    mov es,ax
    mov ax,[es:0x408]
    mov [bda_lpt],ax
    cmp ax,0x378
    jne failed
    mov ax,[es:0x410]
    mov [equipment],ax
    int 0x11
    mov [bios_equipment],ax
    and ax,0xc000
    cmp ax,0x4000
    jne failed
    mov al,[es:0x478]
    mov [old_timeout],al
    push cs
    pop es
    mov ax,0x350f
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    push cs
    pop es
    mov dx,irq_printer
    mov ax,0x250f
    int 0x21
    in al,0x21
    mov [old_pic],al
    and al,0x7f
    out 0x21,al
    cmp byte [mode],0
    jne pin_tests
    mov si,begin_sink
    call print
    xor dx,dx
    mov ax,0x015a
    int 0x17
    mov [init_result],ax
    cmp ax,0x905a
    jne failed
    mov byte [phase],0
.bios:
    mov al,[phase]
    xor ah,ah
    xor dx,dx
    int 0x17
    cmp ah,0x90
    jne failed
    inc byte [phase]
    cmp byte [phase],16
    jb .bios
    mov dx,0x37a
    mov al,0x1c
    out dx,al
    mov word [expected_irqs],1
    call wait_irq
    mov word [phase_word],0
.direct:
    call ready
    mov dx,[phase_word]
    and dx,127
    mov ax,dx
    and ax,1
    shl ax,2
    shr dx,1
    shl dx,10
    add dx,0x378
    add dx,ax
    mov di,[phase_word]
    mov bx,di
    shl bx,1
    mov [used_ports+bx],dx
    mov ax,di
    out dx,al
    nop
    nop
    nop
    nop
    nop
    mov dx,0x37a
    mov al,0x1d
    out dx,al
    nop
    nop
    nop
    nop
    nop
    mov al,0x1c
    out dx,al
    inc word [expected_irqs]
    call wait_irq
    mov dx,[used_ports+bx]
    in al,dx
    mov [readbacks+di],al
    cmp al,[phase_word]
    jne failed
    inc word [phase_word]
    cmp word [phase_word],256
    jb .direct
    mov dx,0x37a
    mov al,12
    out dx,al
    mov dx,0x378
    mov ax,0xa55a
    out dx,ax
    in ax,dx
    mov [wide_result],ax
    cmp ax,0xffff
    jne failed
    in al,dx
    cmp al,0xff
    jne failed
    ; A valid asserted pulse is aborted by physical INIT before the byte can commit.
    mov al,0x55
    out dx,al
    mov cx,8
.setup:
    loop .setup
    mov dx,0x37a
    mov al,13
    out dx,al
    mov cx,8
.pulse:
    loop .pulse
    mov al,8
    out dx,al
    mov cx,512
.reset:
    loop .reset
    mov al,12
    out dx,al
    call ready
    mov ah,5                  ; actual DOS printer API takes the character in DL
    mov dl,0x5a
    int 0x21
    jmp finish

; Samples every combination of physical external status pins and the selected-input wired OR.
pin_tests:
    mov si,begin_pins
    call print
    mov dx,0x37a
    mov al,0x1c
    out dx,al
    mov word [expected_irqs],1
    call wait_irq
    mov byte [phase],0
.state:
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
    movzx di,byte [phase]
    shl di,3
    add di,pin_rows
    mov al,[phase]
    mov [di],al
    mov dx,0x378
    out dx,al
    in al,dx
    mov [di+1],al
    mov dx,0x37a
    mov al,0x1c
    cmp byte [phase],32
    jb .select
    mov al,0x14
.select:
    out dx,al
    in al,dx
    mov [di+2],al
    dec dx
    in al,dx
    mov [di+3],al
    xor dx,dx
    mov ax,0x025a
    int 0x17
    mov [di+4],ah
    mov byte [di+5],0xa7
    mov ax,[irq_count]
    mov [di+6],ax
    inc byte [phase]
    cmp byte [phase],33
    jb .state
    mov si,timeout_text
    call print
    mov ah,8
    int 0x21
    xor ax,ax
    mov es,ax
    mov byte [es:0x478],0
    pushf
    cli
    mov dx,0x70
    mov al,0x8b
    out dx,al
    xor dx,dx
    mov ax,0x0096
    int 0x17
    mov [timeout_result],ax
    mov dx,0x70
    in al,dx
    mov [cmos_result],al
    popf
    mov al,[old_timeout]
    mov [es:0x478],al
    push cs
    pop es
    cmp word [timeout_result],0x1196
    jne failed
    cmp byte [cmos_result],0x8b
    jne failed

; Checks unsupported calls, restores IRQ ownership and retains the bounded proof block for subsequent DOS COPY.
finish:
    mov ax,0x035a
    xor dx,dx
    int 0x17
    mov [invalid_function],ax
    cmp ax,0x015a
    jne failed
    mov ax,0x005a
    mov dx,1
    int 0x17
    mov [invalid_port],ax
    cmp ax,0x015a
    jne failed
    cli
    mov dx,0x37a
    mov al,12
    out dx,al
    mov al,[old_pic]
    out 0x21,al
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x250f
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
    mov ax,0x3100             ; DOS retains the proof instead of lending it to FreeCOM's next COPY buffer
    int 0x21

; Polls the actual BUSY input, leaving all scratch indices and output bytes intact.
ready:
    pushad
    mov ecx,2000000
    mov dx,0x379
.poll:
    in al,dx
    test al,0x80
    jnz .done
    dec ecx
    jnz .poll
    jmp failed
.done:
    popad
    ret

; Waits for a real handler increment; a host-supplied ready flag cannot satisfy this condition.
wait_irq:
    pushad
    mov ecx,2000000
.poll:
    mov ax,[irq_count]
    cmp ax,[expected_irqs]
    jae .done
    dec ecx
    jnz .poll
    jmp failed
.done:
    popad
    ret

; Observes live status, sends the master PIC EOI and executes a real IRET for every delivered IRQ7.
irq_printer:
    pushad
    push ds
    push cs
    pop ds
    mov bx,[irq_count]
    cmp bx,512
    jae .overflow
    mov dx,0x379
    in al,dx
    mov [irq_status+bx],al
    inc word [irq_count]
    jmp .done
.overflow:
    inc word [handler_errors]
.done:
    mov al,0x20
    out 0x20,al
    pop ds
    popad
    iret

; Writes actual BIOS teletype characters; only DOSEBios performs E9 mirroring.
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

; Emits one observed phase number in hexadecimal without writing host markers into RAM.
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

; Emits the real segment or offset of the resident proof, not a host-guessed address.
hex_word:
    pushad
    mov bx,ax
    mov al,bh
    call hex_byte
    mov al,bl
    call hex_byte
    popad
    ret

; Leaves an explicit failure marker and nonzero DOS return code without fabricating a completed proof.
failed:
    mov si,failed_text
    call print
    mov ax,0x4c01
    int 0x21

code_end:
begin_sink: db 'LPT-SINK-BEGIN',13,10,0
begin_pins: db 'LPT-PINS-BEGIN',13,10,0
ready_text: db 'LPIN-READY-',0
timeout_text: db 'LPIN-TIMEOUT',13,10,0
proof_text: db 'LPT-PROOF=',0
ok_text: db 'LPT-OK',13,10,0
failed_text: db 'LPT-FAILED',13,10,0
newline: db 13,10,0
colon: db ':',0
proof: db 'DOSE-LPT-P1',0,0,0,0,0
    dw code_end,readbacks,used_ports,irq_status,pin_rows,program_end
proof_cs: dw 0
bda_lpt: dw 0
equipment: dw 0
bios_equipment: dw 0
mode: db 0
phase: db 0
complete: dw 0
irq_count: dw 0
handler_errors: dw 0
init_result: dw 0
wide_result: dw 0
timeout_result: dw 0
invalid_function: dw 0
invalid_port: dw 0
phase_word: dw 0
expected_irqs: dw 0
old_irq: dd 0
old_pic: db 0
old_timeout: db 0
cmos_result: db 0
readbacks: times 256 db 0
used_ports: times 256 dw 0
irq_status: times 512 db 0
pin_rows: times 33*8 db 0
    times 32 db 0xa7
    times 4096 db 0
stack_top:
program_end:
