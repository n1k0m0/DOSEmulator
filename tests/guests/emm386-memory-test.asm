; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original client of the resident EMM386 VCPI and INT15/AH87 interfaces.
; Two private VCPI pages are allocated, used, freed and accounted for.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    smsw ax
    mov [proof+20],ax
    test al,1
    jz failed
    mov ax,0xde00
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+24],bx
    or dword [proof+16],1
    mov ax,0xde07
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+28],ebx
    mov ax,0xde0a
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+32],bx
    mov [proof+36],cx
    mov ax,0xde03
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+40],edx
    cmp edx,2
    jb failed
    mov ax,0xde04
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+44],edx
    mov byte [allocated],1
    mov ax,0xde04
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+48],edx
    mov byte [allocated],2
    mov ax,0xde03
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+52],edx
    or dword [proof+16],2
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,input_buffer
    mov [proof+64],eax
    mov eax,[proof+64]
    mov [proof+76],eax
    shr eax,12
    mov cx,ax
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov eax,[proof+76]
    and eax,0xfff
    add eax,edx
    mov [proof+80],eax
    mov eax,[proof+64]
    add eax,output_buffer-input_buffer
    mov [proof+68],eax
    mov [proof+84],eax
    shr eax,12
    mov cx,ax
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov eax,[proof+84]
    and eax,0xfff
    add eax,edx
    mov [proof+88],eax
    mov di,input_buffer
    mov cx,2048
    mov ax,0x2468
.fill:
    stosw
    add ax,0x1357
    loop .fill
    mov eax,[proof+80]
    mov edi,copy_source
    call descriptor_base
    mov eax,[proof+44]
    mov edi,copy_destination
    call descriptor_base
    call copy_block
    jc failed
    mov eax,[proof+44]
    mov edi,copy_source
    call descriptor_base
    mov eax,[proof+48]
    mov edi,copy_destination
    call descriptor_base
    call copy_block
    jc failed
    mov eax,[proof+48]
    mov edi,copy_source
    call descriptor_base
    mov eax,[proof+88]
    mov edi,copy_destination
    call descriptor_base
    call copy_block
    jc failed
    mov si,input_buffer
    mov di,output_buffer
    mov cx,2048
    repe cmpsw
    jne failed
    mov dword [proof+72],4096
    or dword [proof+16],4
    call release_pages
    cmp byte [allocated],0
    jne failed
    mov ax,0xde03
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+56],edx
    cmp edx,[proof+40]
    jne failed
    or dword [proof+16],8
    mov dword [proof+12],1
    jmp report
failed:
    mov [proof+60],ax
    call release_pages
report:
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
; Calls the resident block-copy service with the probe's explicitly constructed descriptors.
copy_block:
    mov si,copy_table
    mov cx,2048
    mov ah,0x87
    int 0x15
    ret
; Patches the explicit descriptor base bytes for this probe's allocated guest memory.
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
; Returns the probe's owned VCPI pages to the real resident manager.
release_pages:
    cmp byte [allocated],0
    je .done
    movzx bx,byte [allocated]
    dec bx
    shl bx,2
    mov edx,[proof+44+bx]
    mov ax,0xde05
    int 0x67
    test ah,ah
    jnz .done
    dec byte [allocated]
    jmp release_pages
.done:
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
allocated db 0
marker db 'EMM-MEMORY-PROOF=',0
newline db 13,10,0
success db 'EMM-MEMORY-PASS',13,10,'$'
failure db 'EMM-MEMORY-FAIL',13,10,'$'
copy_table times 16 db 0
copy_source dw 4095,0
    db 0,0x93,0,0
copy_destination dw 4095,0
    db 0,0x93,0,0
    times 16 db 0
align 4,db 0
proof:
    db 'CPEMM001'
    dd 1
    times 29 dd 0
input_buffer times 4096 db 0
output_buffer times 4096 db 0
