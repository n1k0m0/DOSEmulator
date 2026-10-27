; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own IBM INT15/AH84 interface. Counts are firmware polling units, not ohms.
; Called after enter_service. No host input, hidden device access or timer injection.
int_gameport:
    cmp byte [cs:gameport_present],0
    je .invalid
    cmp rDX,0
    je .switches
    cmp rDX,1
    jne .invalid
    sub sp,8
    mov si,sp
    xor eax,eax
    mov [ss:si],eax
    mov [ss:si+4],eax
    mov dx,0x201
    mov cx,4096
    out dx,al
.sample:
    in al,dx
    and al,15
    jz .measured
    test al,1
    jz .ay
    inc word [ss:si]
.ay:
    test al,2
    jz .bx
    inc word [ss:si+2]
.bx:
    test al,4
    jz .by
    inc word [ss:si+4]
.by:
    test al,8
    jz .next
    inc word [ss:si+6]
.next:
    loop .sample
    ; Open axes have no falling edge: the bounded DOSE interface returns FFFFh.
    test al,1
    jz .open_ay
    mov word [ss:si],0xffff
.open_ay:
    test al,2
    jz .open_bx
    mov word [ss:si+2],0xffff
.open_bx:
    test al,4
    jz .open_by
    mov word [ss:si+4],0xffff
.open_by:
    test al,8
    jz .measured
    mov word [ss:si+6],0xffff
.measured:
    mov ax,[ss:si]
    mov rAX,ax
    mov ax,[ss:si+2]
    mov rBX,ax
    mov ax,[ss:si+4]
    mov rCX,ax
    mov ax,[ss:si+6]
    mov rDX,ax
    add sp,8
    leave_service
.switches:
    mov dx,0x201
    in al,dx
    and al,0xf0
    mov rAL,al
    mov rAH,0
    leave_service
.invalid:
    mov rAH,0x86
    or rFLAGS,1
    leave_service
