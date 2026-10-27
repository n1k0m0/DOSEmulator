; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS XMS client. Calls the original resident driver's far entry point.
bits 16
cpu 486
org 0x100

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    cld
    mov ax,0x4300
    int 0x2f
    cmp al,0x80
    jne failed
    mov ax,0x4310
    int 0x2f
    mov [xms_entry],bx
    mov [xms_entry+2],es
    mov eax,[xms_entry]
    mov [proof+88],eax
    mov ah,0
    call far [xms_entry]
    movzx eax,ax
    mov [proof+16],eax
    movzx eax,bx
    mov [proof+20],eax
    or dword [proof+60],1
    mov ah,7
    call far [xms_entry]
    movzx eax,ax
    mov [proof+80],eax
    mov ah,8
    call far [xms_entry]
    movzx eax,ax
    mov [proof+24],eax
    movzx eax,dx
    mov [proof+28],eax
    cmp dx,64
    jb failed
    mov dx,64
    mov ah,9
    call far [xms_entry]
    cmp ax,1
    jne failed
    mov [handle],dx
    movzx eax,dx
    mov [proof+32],eax
    or dword [proof+60],2
    mov ah,8
    call far [xms_entry]
    movzx eax,dx
    mov [proof+36],eax
    mov dx,[handle]
    mov ah,0x0c
    call far [xms_entry]
    cmp ax,1
    jne failed
    mov byte [locked],1
    movzx eax,dx
    shl eax,16
    mov ax,bx
    mov [proof+40],eax
    or dword [proof+60],4

    push cs
    pop es
    mov di,input_buffer
    mov cx,2048
    mov ax,0x1357
.fill:
    stosw
    add ax,0x1234
    loop .fill
    mov di,output_buffer
    mov cx,2048
    mov ax,0xa5a5
    rep stosw
    mov dword [move_length],4096
    mov word [move_source_handle],0
    mov word [move_source_offset],input_buffer
    mov ax,cs
    mov [move_source_offset+2],ax
    mov dx,[handle]
    mov [move_dest_handle],dx
    mov dword [move_dest_offset],0
    call move_block
    cmp ax,1
    jne failed
    mov word [move_dest_handle],0
    mov word [move_dest_offset],output_buffer
    mov ax,cs
    mov [move_dest_offset+2],ax
    mov dx,[handle]
    mov [move_source_handle],dx
    mov dword [move_source_offset],0
    call move_block
    cmp ax,1
    jne failed
    mov dword [proof+44],4096
    mov si,input_buffer
    mov di,output_buffer
    mov cx,2048
    repe cmpsw
    jne failed
    mov dword [proof+48],2048
    or dword [proof+60],8

    mov dword [move_length],4
    mov word [move_source_handle],0
    mov word [move_source_offset],tail_input
    mov ax,cs
    mov [move_source_offset+2],ax
    mov dx,[handle]
    mov [move_dest_handle],dx
    mov dword [move_dest_offset],65532
    call move_block
    cmp ax,1
    jne failed
    mov word [move_dest_handle],0
    mov word [move_dest_offset],tail_output
    mov ax,cs
    mov [move_dest_offset+2],ax
    mov dx,[handle]
    mov [move_source_handle],dx
    mov dword [move_source_offset],65532
    call move_block
    cmp ax,1
    jne failed
    mov eax,[tail_input]
    mov [proof+72],eax
    cmp eax,[tail_output]
    jne failed
    mov eax,[tail_output]
    mov [proof+76],eax
    or dword [proof+60],16

    mov dx,[handle]
    mov ah,0x0d
    call far [xms_entry]
    cmp ax,1
    jne failed
    mov byte [locked],0
    mov dx,[handle]
    mov ah,0x0a
    call far [xms_entry]
    cmp ax,1
    jne failed
    mov word [handle],0
    mov ah,8
    call far [xms_entry]
    movzx eax,ax
    mov [proof+56],eax
    cmp ax,[proof+24]
    jne failed
    movzx eax,dx
    mov [proof+52],eax
    cmp dx,[proof+28]
    jne failed
    mov ah,7
    call far [xms_entry]
    movzx eax,ax
    mov [proof+84],eax
    cmp ax,[proof+80]
    jne failed
    or dword [proof+60],32
    mov dword [proof+12],1
    jmp report
failed:
    mov [proof+92],bl
    cmp word [handle],0
    je report
    cmp byte [locked],0
    je .free
    mov dx,[handle]
    mov ah,0x0d
    call far [xms_entry]
.free:
    mov dx,[handle]
    mov ah,0x0a
    call far [xms_entry]
report:
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov ebx,eax
    add eax,input_buffer
    mov [proof+64],eax
    mov eax,ebx
    add eax,output_buffer
    mov [proof+68],eax
    mov si,proof_marker
    call debug_string
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
; Invokes the original XMS manager to move the probe's owned block contents.
move_block:
    mov si,move_length
    mov ah,0x0b
    call far [xms_entry]
    ret
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret

xms_entry dd 0
handle dw 0
locked db 0
move_length dd 0
move_source_handle dw 0
move_source_offset dd 0
move_dest_handle dw 0
move_dest_offset dd 0
tail_input dd 0x55667788
tail_output dd 0
proof_marker db 'XMS-PROOF=',0
newline db 13,10,0
success_message db 'XMS-MEMORY-PASS',13,10,'$'
failure_message db 'XMS-MEMORY-FAIL',13,10,'$'
align 4, db 0
proof:
    db 'CPXMS001'
    dd 1
    times 21 dd 0
input_buffer times 4096 db 0
output_buffer times 4096 db 0
