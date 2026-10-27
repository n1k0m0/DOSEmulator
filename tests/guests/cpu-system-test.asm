; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own real-mode 486 system-register probe. No OS bytes or host-side BIOS hooks.
; Save/restore all changed system state; run only on a private acceptance machine.
bits 16
cpu 486
org 0x100

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    push cs
    pop es
    pushf
    pop word [original_flags]
    cli
    cld
    o32 sgdt [original_gdt]
    o32 sidt [original_idt]
    mov eax,cr0
    mov [proof+64],eax
    mov eax,cr2
    mov [original_cr2],eax
    mov [proof+120],eax
    mov eax,cr3
    mov [original_cr3],eax
    mov [proof+124],eax
    ; This DOS probe expects the conventional real-mode IVT, not a V86 monitor.
    cmp dword [original_idt+2],0
    jne failed
    cmp word [original_idt],0x3ff
    jne failed
    mov eax,[original_idt+2]
    mov [proof+72],eax
    movzx eax,word [original_idt]
    mov [proof+80],eax
    mov eax,[original_gdt+2]
    mov [proof+88],eax
    movzx eax,word [original_gdt]
    mov [proof+96],eax

    lgdt [table_input]          ; 24-bit load, independent of 32-bit address form
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+16],eax
    cmp eax,0x345678
    jne failed
    o32 lgdt [table_input]      ; full linear base, not a segment-relative value
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+20],eax
    cmp eax,0x9a345678
    jne failed
    movzx eax,word [table_output]
    mov [proof+28],eax
    cmp ax,0x1357
    jne failed
    sgdt [table_output]        ; compatibility store must clear the top base byte
    mov eax,[table_output+2]
    mov [proof+24],eax
    cmp eax,0x345678
    jne failed

    ; Copy and relocate the actual guest IVT. No synthetic host interrupt dispatch.
    push ds
    xor ax,ax
    mov ds,ax
    xor si,si
    mov di,private_ivt
    mov cx,512
    rep movsw
    pop ds
    mov word [private_ivt+0x60*4],interrupt60
    mov word [private_ivt+6*4],invalid_opcode
    mov word [private_ivt+13*4],general_protection
    mov word [private_ivt+7*4],device_unavailable
    mov ax,cs
    mov [private_ivt+0x60*4+2],ax
    mov [private_ivt+6*4+2],ax
    mov [private_ivt+13*4+2],ax
    mov [private_ivt+7*4+2],ax
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,private_ivt
    mov [private_idt+2],eax
    o32 lidt [private_idt]
    int 0x60
    cmp dword [proof+32],1
    jne failed

invalid_test:
    db 0x0f,0x01,0xc0          ; SGDT register is #UD on a 486
invalid_return:
    cmp dword [proof+36],1
    jne failed
    mov word [gp_resume],boundary_return
boundary_test:
    sgdt [es:0xfffb]           ; complete 6-byte operand must fault before writes
boundary_return:
    cmp dword [proof+40],1
    jne failed
    mov eax,[proof+64]
    or eax,0x80000000          ; PG=1, PE=0 is an actual #GP, not a supported mode
    mov word [gp_resume],paging_return
paging_test:
    mov cr0,eax
paging_return:
    cmp dword [proof+40],2
    jne failed
    mov eax,cr0
    cmp eax,[proof+64]
    jne failed

    mov eax,0xabcdef08
    mov cr2,eax
    o16 mov ebx,cr2
    mov [proof+48],ebx
    cmp ebx,eax
    jne failed
    mov eax,0x34567018
    o16 mov cr3,eax
    mov ebx,cr3
    mov [proof+52],ebx
    cmp ebx,eax
    jne failed
    o32 lmsw [msw_input]       ; still r/m16, changes only MP/EM/TS, not ET
    mov eax,cr0
    mov [proof+56],eax
    mov ebx,[proof+64]
    and ebx,~0x0e
    or ebx,0x0e
    cmp eax,ebx
    jne failed

    mov eax,[proof+64]
    and eax,~4                ; no x87 emulator bit needed for this WAIT test
    or eax,0x0a               ; MP+TS make WAIT raise #NM
    mov cr0,eax
wait_test:
    wait                      ; handler CLTS and IRET retry THIS instruction
    mov eax,cr0
    mov [proof+60],eax
    and ebx,~0x0e
    or ebx,2
    cmp eax,ebx
    jne failed
    cmp dword [proof+44],1
    jne failed
    cmp dword [handler_error],0
    jne failed
    mov dword [proof+12],1
    jmp restore
failed:
    mov dword [proof+12],0
; Restores only this probe's explicitly saved interrupt/controller state.
restore:
    mov eax,[proof+64]
    mov cr0,eax
    mov eax,[original_cr2]
    mov cr2,eax
    mov eax,[original_cr3]
    mov cr3,eax
    o32 lgdt [original_gdt]
    o32 lidt [original_idt]
    mov eax,cr0
    mov [proof+68],eax
    mov eax,cr2
    mov [proof+128],eax
    mov eax,cr3
    mov [proof+132],eax
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+92],eax
    movzx eax,word [table_output]
    mov [proof+100],eax
    o32 sidt [table_output]
    mov eax,[table_output+2]
    mov [proof+76],eax
    movzx eax,word [table_output]
    mov [proof+84],eax

    mov si,proof_marker
    call debug_string
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,proof
    mov ebx,eax
    mov cx,8
.hex:
    rol ebx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .digit
    add al,7
.digit:
    out 0xe9,al
    loop .hex
    mov si,newline
    call debug_string
    push word [original_flags]
    popf
    cmp dword [proof+12],1
    jne .bad
    mov dx,success_message
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
.bad:
    mov dx,failure_message
    mov ah,9
    int 0x21
    mov ax,0x4c01
    int 0x21

; Records and returns from the probe's actual software interrupt frame.
interrupt60:
    inc dword [cs:proof+32]
    iret
invalid_opcode:
    push eax
    push bp
    mov bp,sp
    movzx eax,word [ss:bp+6]
    mov [cs:proof+104],eax
    cmp ax,invalid_test
    je .ok
    mov dword [cs:handler_error],1
.ok:
    mov word [ss:bp+6],invalid_return
    inc dword [cs:proof+36]
    pop bp
    pop eax
    iret
general_protection:
    push eax
    push bp
    mov bp,sp
    movzx eax,word [ss:bp+6]
    cmp dword [cs:proof+40],0
    jne .paging
    mov [cs:proof+108],eax
    cmp ax,boundary_test
    je .ok
    jmp .bad
.paging:
    mov [cs:proof+112],eax
    cmp ax,paging_test
    je .ok
.bad:
    mov dword [cs:handler_error],1
.ok:
    mov ax,[cs:gp_resume]
    mov [ss:bp+6],ax
    inc dword [cs:proof+40]
    pop bp
    pop eax
    iret
device_unavailable:
    push eax
    push bp
    mov bp,sp
    movzx eax,word [ss:bp+6]
    mov [cs:proof+116],eax
    cmp ax,wait_test
    je .ok
    mov dword [cs:handler_error],1
.ok:
    inc dword [cs:proof+44]
    clts
    pop bp
    pop eax
    iret
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret

original_flags dw 0
original_cr2 dd 0
original_cr3 dd 0
original_gdt times 6 db 0
original_idt times 6 db 0
table_input dw 0x1357
    dd 0x9a345678
table_output times 6 db 0
private_idt dw 0x3ff
    dd 0
msw_input dw 0xfffe
gp_resume dw 0
handler_error dd 0
proof_marker db 'CPU-SYSTEM-PROOF=',0
newline db 13,10,0
success_message db 'CPU-SYSTEM-PASS',13,10,'$'
failure_message db 'CPU-SYSTEM-FAIL',13,10,'$'
align 4, db 0
proof:
    db 'CPSYS001'
    dd 1
    times 31 dd 0
align 4, db 0
private_ivt times 1024 db 0
