; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own basic INT14 COM1 firmware: real byte I/O, polled handshake and RTC-bound timeout.

; Dispatches initialization, one-byte transmit/receive and status for the installed COM1.
int_serial:
    enter_service
    cmp byte [cs:serial_present],0
    je .unsupported
    cmp rDX,0
    jne .unsupported
    mov al,rAH
    test al,al
    jz .initialize
    cmp al,1
    je .transmit
    cmp al,2
    je .receive
    cmp al,3
    je .status
.unsupported:
    mov rAH,0x80
    jmp .done
.initialize:
    movzx bx,rAL
    shr bx,5
    shl bx,1
    mov bx,[cs:serial_divisors+bx]
    mov dx,0x3f9
    xor al,al
    out dx,al                   ; polled BIOS owns no UART interrupt source
    inc dx
    out dx,al                   ; character mode, no hidden FIFO allocation
    inc dx
    mov al,0x80
    out dx,al
    mov dx,0x3f8
    mov al,bl
    out dx,al
    inc dx
    mov al,bh
    out dx,al
    mov dx,0x3fb
    mov al,rAL
    and al,0x1f
    out dx,al
    inc dx
    mov al,3
    out dx,al                   ; assert DTR/RTS, leave board IRQ gate disabled
.status:
    mov dx,0x3fd
    in al,dx
    mov rAH,al
    inc dx
    in al,dx
    mov rAL,al
    jmp .done
.transmit:
    mov si,0x3020               ; DSR+CTS and empty transmitter holding register
    call serial_wait
    jc .timeout
    mov dx,0x3f8
    mov al,rAL
    out dx,al
    mov dx,0x3fd
    in al,dx
    mov rAH,al
    jmp .done
.receive:
    mov si,0x2001               ; DSR and received data
    call serial_wait
    jc .timeout
    mov rAH,al
    mov dx,0x3f8
    in al,dx
    mov rAL,al
    jmp .done
.timeout:
    mov dx,0x3fd
    in al,dx
    or al,0x80
    mov rAH,al
.done:
    leave_service

; Waits for SI's high-byte modem mask and low-byte LSR mask without enabling IF.
; Nonzero BDA timeout units count whole RTC seconds plus the first partial second;
; frozen clocks retain an independent four-million-poll ceiling. Port70/NMI is restored.
serial_wait:
    mov bh,[0x47c]
    test bh,bh
    jz .count_ready             ; an explicitly zero timeout retains a short bounded probe
    cmp bh,255
    je .count_ready
    inc bh                     ; do not mistake the first partial second for a whole timeout unit
.count_ready:
    mov bl,0xff
    mov ecx,4000000
.poll:
    mov dx,0x3fe
    mov ax,si
    shr ax,8                    ; the high byte of SI is the modem mask
    mov di,ax
    in al,dx
    and ax,di
    cmp ax,di
    jne .again
    dec dx
    in al,dx
    mov dx,si
    test al,dl
    jnz .ready
.again:
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

serial_divisors: dw 1047,768,384,192,96,48,24,12
