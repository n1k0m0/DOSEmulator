; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own basic INT17 firmware for the explicitly selected IBM LPT1 byte adapter.

; Sends one byte, initializes the peer or returns live status using actual guest I/O.
int_printer:
    enter_service
    cmp byte [cs:parallel_present],0
    je .unsupported
    cmp rDX,0
    jne .unsupported
    mov al,rAH
    test al,al
    jz .print
    cmp al,1
    je .initialize
    cmp al,2
    je .status
.unsupported:
    mov rAH,1                  ; absent/unsupported printer cannot report a fictitious ready device
    jmp .done
.initialize:
    mov dx,0x37a
    mov al,8
    out dx,al
    mov cx,512                 ; minimum INIT pulse exceeds 50 us at the fixed development clock
.init_delay:
    loop .init_delay
    mov al,12
    out dx,al
    mov cx,512                 ; bounded recovery, then report actual status even when no printer is attached
.recover_delay:
    loop .recover_delay
    jmp .status
.print:
    mov dx,0x37a
    in al,dx
    and al,0x1e
    or al,12
    movzx si,al
    out dx,al
    call printer_wait
    jc .timeout
    mov dx,0x378
    mov al,rAL
    out dx,al
    nop
    nop
    nop
    nop
    nop
    mov dx,0x37a
    mov ax,si
    or al,1
    out dx,al
    nop
    nop
    nop
    nop
    nop
    mov ax,si
    out dx,al
    nop
    nop
    nop
    nop
    nop
    call printer_wait
    jc .timeout
.status:
    mov dx,0x379
    in al,dx
    and al,0xf8
    xor al,0x48
    mov rAH,al
    jmp .done
.timeout:
    mov dx,0x379
    in al,dx
    and al,0xf8
    xor al,0x48
    or al,1
    mov rAH,al
.done:
    leave_service

; Waits for the real nonbusy input with a whole-second-plus-partial RTC bound and a frozen-clock poll ceiling.
; An explicit zero BDA timeout uses one short probe; all paths restore CMOS index and NMI selection.
printer_wait:
    mov bh,[0x478]
    test bh,bh
    jz .count_ready
    cmp bh,255
    je .count_ready
    inc bh
.count_ready:
    mov bl,0xff
    mov ecx,4000000
.poll:
    mov dx,0x379
    in al,dx
    test al,0x80
    jnz .ready
    dec ecx
    jz .failed
    test ecx,255
    jnz .poll
    mov dx,0x70
    in al,dx
    movzx edi,al
    and al,0x80
    out dx,al
    inc dx
    in al,dx
    cmp bl,al
    je .restore
    cmp bl,0xff
    je .first
    test bh,bh
    jz .restore
    dec bh
.first:
    mov bl,al
.restore:
    dec dx
    mov ax,di
    out dx,al
    test bh,bh
    jnz .poll
.failed:
    stc
    ret
.ready:
    clc
    ret
