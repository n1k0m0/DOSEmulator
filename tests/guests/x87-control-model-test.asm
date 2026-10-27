; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original early-486 identification and x87 control probe. No arithmetic claim.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    cld
    pushfd
    pop dword [proof+20]
    mov [proof+52],cs
    smsw ax
    mov [proof+60],ax
    fnstcw [proof+32]
    mov eax,[proof+20]
    xor eax,0x200000
    push eax
    popfd
    pushfd
    pop eax
    mov [proof+24],eax
    test eax,0x200000
    jnz failed
    mov eax,[proof+20]
    xor eax,0x40000
    push eax
    popfd
    pushfd
    pop eax
    mov [proof+28],eax
    xor eax,[proof+20]
    test eax,0x40000
    jz failed
    push dword [proof+20]
    popfd
    or dword [proof+16],1
    fninit
    fnstcw [proof+34]
    fnstsw [proof+36]
    cmp word [proof+34],0x37f
    jne failed
    cmp word [proof+36],0
    jne failed
    fldcw [own_control]
    fnstcw [proof+38]
    cmp word [proof+38],0xb7f
    jne failed
    mov eax,0xa55affff
    fnstsw ax
    mov [proof+40],eax
    cmp eax,0xa55a0000
    jne failed
    fnop
    fnclex
    db 0xdb,0xe4 ; 287 FSETPM is a defined no-op on 486.
    fwait
    fnstcw [proof+56]
    cmp word [proof+56],0xb7f
    jne failed
    or dword [proof+16],2
    mov ax,0x3506
    int 0x21
    mov [old_ud],bx
    mov [old_ud+2],es
    mov dx,ud_handler
    mov ax,0x2506
    int 0x21
    mov byte [vector_installed],1
ud_instruction:
    db 0x0f,0xa2 ; The selected early DX has no CPUID: a real #UD is required.
after_ud:
    cmp word [proof+44],1
    jne failed
    cmp word [proof+46],ud_instruction
    jne failed
    mov ax,cs
    cmp [proof+48],ax
    jne failed
    or dword [proof+16],4
    call restore_vector
    fldcw [proof+32]
    fnstcw [proof+54]
    mov ax,[proof+54]
    cmp ax,[proof+32]
    jne failed
    or dword [proof+16],8
    mov dword [proof+12],1
    jmp report
failed:
    mov [proof+64],eax
    call restore_vector
    fldcw [proof+32]
report:
    push dword [proof+20]
    popfd
    mov si,marker
    call debug_string
    xor ebx,ebx
    mov bx,cs
    shl ebx,4
    add ebx,proof
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
    mov dx,success
    cmp dword [proof+12],1
    je .print
    mov dx,failure
.print:
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
; Reinstalls the guest interrupt vector saved before this probe's exception test.
restore_vector:
    cmp byte [vector_installed],0
    je .done
    push ds
    lds dx,[old_ud]
    mov ax,0x2506
    int 0x21
    pop ds
    mov byte [vector_installed],0
.done:
    ret
ud_handler:
    push bp
    mov bp,sp
    push ax
    inc word [cs:proof+44]
    mov ax,[ss:bp+2]
    mov [cs:proof+46],ax
    mov ax,[ss:bp+4]
    mov [cs:proof+48],ax
    mov ax,[ss:bp+6]
    mov [cs:proof+50],ax
    add word [ss:bp+2],2
    pop ax
    pop bp
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
code_end:
old_ud dd 0
vector_installed db 0
own_control dw 0xb7f
marker db 'CPU-X87-PROOF=',0
newline db 13,10,0
success db 'CPU-X87-PASS',13,10,'$'
failure db 'CPU-X87-FAIL',13,10,'$'
align 4,db 0
proof:
    db 'CPXF0001'
    dd 1
    times 29 dd 0
    db 'CPXFADDR'
    dw proof,ud_instruction,after_ud,code_end,ud_handler
