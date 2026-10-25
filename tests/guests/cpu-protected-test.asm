; Own 486 DOS probe: real -> PE16 -> PE32 above 64KiB -> PE16 -> real.
; Both biased CS32 and SS32 address only this program's allocated COM buffer.
; A temporary extended-RAM dword and PIC masks are saved and restored.
bits 16
cpu 486
org 0x100

CODE16 equ 8
DATA16 equ 16
CODE32 equ 24
DATA32 equ 32
STACK32 equ 40
FLAT equ 48
CACHE equ 56
ABSENT_DATA equ 64
ABSENT_STACK equ 72

start:
    push cs
    pop ds
    mov [original_sp],sp
    mov ax,ss
    mov [original_ss],ax
    mov ax,es
    mov [original_es],ax
    mov ax,fs
    mov [original_fs],ax
    mov ax,gs
    mov [original_gs],ax
    mov ax,cs
    mov [real_pointer+2],ax
    movzx eax,ax
    mov [proof+200],eax
    pushf
    pop word [original_flags]
    cli
    cld
    in al,0x21
    mov [pic_master],al
    in al,0xa1
    mov [pic_slave],al
    mov al,0xff
    out 0x21,al
    out 0xa1,al
    o32 sgdt [original_gdt]
    o32 sidt [original_idt]
    mov eax,cr0
    mov [proof+140],eax
    mov eax,[original_gdt+2]
    mov [proof+148],eax
    movzx eax,word [original_gdt]
    mov [proof+164],eax
    mov eax,[original_idt+2]
    mov [proof+156],eax
    movzx eax,word [original_idt]
    mov [proof+172],eax
    movzx eax,byte [pic_slave]
    shl eax,8
    mov al,[pic_master]
    mov [proof+192],eax

    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [linear_base],eax
    mov di,gdt_code16
    call patch_base
    mov eax,[linear_base]
    mov di,gdt_data16
    call patch_base
    mov eax,[linear_base]
    sub eax,0x10000
    mov di,gdt_code32
    call patch_base
    mov eax,[linear_base]
    mov di,gdt_data32
    call patch_base
    mov eax,[linear_base]
    sub eax,0x10000
    mov di,gdt_stack32
    call patch_base
    mov eax,[linear_base]
    add eax,cache_a
    mov di,gdt_cache
    call patch_base
    mov eax,[linear_base]
    mov di,gdt_absent_data
    call patch_base
    mov eax,[linear_base]
    sub eax,0x10000
    mov di,gdt_absent_stack
    call patch_base
    mov eax,[linear_base]
    add eax,gdt
    mov [private_gdt+2],eax
    mov eax,[linear_base]
    add eax,idt
    mov [private_idt+2],eax
    add eax,gdt_cache-idt
    mov [proof+208],eax

    o32 lgdt [private_gdt]
    o32 lidt [private_idt]
    mov ax,1
    lmsw ax
    jmp CODE16:protected16

patch_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret

protected16:
    mov ax,DATA16
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov sp,stack16_end
    mov eax,cr0
    mov [proof+20],eax
    or dword [proof+16],1
    mov ax,CACHE
    mov fs,ax
    mov eax,[fs:0]
    mov [proof+28],eax
    cmp eax,0x11111111
    jne failed16
    mov eax,[linear_base]
    add eax,cache_b
    mov di,gdt_cache
    call patch_base
    mov eax,[fs:0]
    mov [proof+32],eax
    cmp eax,0x11111111
    jne failed16
    mov ax,CACHE
    mov fs,ax
    mov eax,[fs:0]
    mov [proof+36],eax
    cmp eax,0x22222222
    jne failed16
    movzx eax,byte [gdt_cache+5]
    mov [proof+188],eax
    or dword [proof+16],2
    jmp dword CODE32:0x10000+protected32

bits 32
protected32:
    mov ax,DATA32
    mov ds,ax
    mov ax,STACK32
    mov ss,ax
    mov esp,0x10000+stack32_end
    mov ax,FLAT
    mov es,ax
    mov eax,cr0
    mov [proof+24],eax
    mov eax,cs
    mov [proof+180],eax
    mov eax,ss
    mov [proof+184],eax
    mov eax,0x12345678
    add eax,0x11111111
    mov [proof+40],eax
    cmp eax,0x23456789
    jne failed32
    mov [proof+56],esp
    push eax
    o16 push ax
    o16 pop dx
    pop ebx
    mov [proof+60],esp
    cmp ebx,eax
    jne failed32
    cmp dx,ax
    jne failed32
    mov [proof+64],esp
    call record_eip
    mov [proof+68],esp
    mov eax,[es:0x200000]
    mov [proof+48],eax
    mov byte [extended_dirty],1
    mov dword [es:0x200000],0xdecafbad
    mov eax,[es:0x200000]
    mov [proof+44],eax
    cmp eax,0xdecafbad
    jne failed32
    or dword [proof+16],4
    ; Hardware PIC is masked. These INTs test IF/IRETD entirely inside the guest.
    sti
    int 0x60
    int 0x61
    cli
    cmp dword [proof+80],0x200
    jne failed32
    cmp dword [proof+84],0
    jne failed32
    or dword [proof+16],8

    xor ax,ax
    mov gs,ax
    mov dword [gp_resume],0x10000+null_return
null_test:
    mov eax,[gs:0]
null_return:
    mov ax,0xfff8
    mov dword [gp_resume],0x10000+selector_return
selector_test:
    mov gs,ax
selector_return:
    mov ax,ABSENT_DATA
absent_data_test:
    mov ds,ax
    mov ax,ABSENT_STACK
absent_stack_test:
    mov ss,ax
invalid_test:
    db 0x0f,0x01,0xc0
invalid_return:
    cmp dword [proof+72],1
    jne failed32
    cmp dword [proof+76],1
    jne failed32
    cmp dword [proof+88],2
    jne failed32
    cmp dword [proof+108],1
    jne failed32
    cmp dword [proof+120],1
    jne failed32
    cmp dword [proof+132],1
    jne failed32
    cmp dword [proof+216],0
    jne failed32
    or dword [proof+16],16
    mov dword [proof+12],1
    jmp word CODE16:protected16_return
failed32:
    mov dword [proof+12],0
    jmp word CODE16:protected16_return
record_eip:
    mov eax,[ss:esp]
    mov [proof+212],eax
    ret
trap_handler:
    push eax
    pushfd
    pop eax
    and eax,0x200
    mov [proof+80],eax
    inc dword [proof+72]
    pop eax
    iretd
interrupt_handler:
    push eax
    pushfd
    pop eax
    and eax,0x200
    mov [proof+84],eax
    inc dword [proof+76]
    pop eax
    iretd
gp_handler:
    push eax
    movzx eax,word [ss:esp+4] ; 486 leaves the high error-slot word unwritten.
    cmp dword [proof+88],0
    jne .selector
    mov [proof+92],eax
    mov eax,[ss:esp+8]
    mov [proof+100],eax
    cmp eax,0x10000+null_test
    je .resume
    jmp .bad
.selector:
    mov [proof+96],eax
    mov eax,[ss:esp+8]
    mov [proof+104],eax
    cmp eax,0x10000+selector_test
    je .resume
.bad:
    mov dword [proof+216],1
.resume:
    mov eax,[gp_resume]
    mov [ss:esp+8],eax
    inc dword [proof+88]
    pop eax
    add esp,4
    iretd
np_handler:
    push eax
    movzx eax,word [ss:esp+4]
    mov [proof+112],eax
    mov eax,[ss:esp+8]
    mov [proof+116],eax
    cmp eax,0x10000+absent_data_test
    je .ok
    mov dword [proof+216],1
.ok:
    or byte [gdt_absent_data+5],0x80
    inc dword [proof+108]
    pop eax
    add esp,4
    iretd
ss_handler:
    push eax
    movzx eax,word [ss:esp+4]
    mov [proof+124],eax
    mov eax,[ss:esp+8]
    mov [proof+128],eax
    cmp eax,0x10000+absent_stack_test
    je .ok
    mov dword [proof+216],1
.ok:
    or byte [gdt_absent_stack+5],0x80
    inc dword [proof+120]
    pop eax
    add esp,4
    iretd
ud_handler:
    push eax
    mov eax,[ss:esp+4]
    mov [proof+136],eax
    cmp eax,0x10000+invalid_test
    je .ok
    mov dword [proof+216],1
.ok:
    mov dword [ss:esp+4],0x10000+invalid_return
    inc dword [proof+132]
    pop eax
    iretd

bits 16
failed16:
    mov dword [proof+12],0
protected16_return:
    mov ax,DATA16
    mov ds,ax
    mov ax,FLAT
    mov es,ax
    cmp byte [extended_dirty],0
    je .not_dirty
    mov eax,[proof+48]
    mov [es:dword 0x200000],eax
    mov eax,[es:dword 0x200000]
    mov [proof+52],eax
.not_dirty:
    mov ax,DATA16
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov sp,stack16_end
    o32 lidt [original_idt]
    mov eax,[proof+140]
    mov cr0,eax
    jmp far [cs:real_pointer]
real_return:
    mov ax,[cs:original_ss]
    mov ss,ax
    mov sp,[cs:original_sp]
    mov ax,cs
    mov ds,ax
    mov ax,[original_es]
    mov es,ax
    mov ax,[original_fs]
    mov fs,ax
    mov ax,[original_gs]
    mov gs,ax
    o32 lgdt [original_gdt]
    o32 sidt [table_output]
    mov eax,[table_output+2]
    mov [proof+160],eax
    movzx eax,word [table_output]
    mov [proof+176],eax
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+152],eax
    movzx eax,word [table_output]
    mov [proof+168],eax
    mov eax,cr0
    mov [proof+144],eax
    movzx eax,word [real_pointer+2]
    mov [proof+204],eax
    mov al,[pic_master]
    out 0x21,al
    mov al,[pic_slave]
    out 0xa1,al
    xor eax,eax
    in al,0xa1
    shl eax,8
    in al,0x21
    mov [proof+196],eax
    or dword [proof+16],32
    mov si,proof_marker
    call debug_string
    mov eax,[linear_base]
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
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret

original_sp dw 0
original_ss dw 0
original_es dw 0
original_fs dw 0
original_gs dw 0
original_flags dw 0
original_gdt times 6 db 0
original_idt times 6 db 0
table_output times 6 db 0
linear_base dd 0
pic_master db 0
pic_slave db 0
extended_dirty db 0
gp_resume dd 0
real_pointer dw real_return,0
cache_a dd 0x11111111
cache_b dd 0x22222222
proof_marker db 'CPU-PE-PROOF=',0
newline db 13,10,0
success_message db 'CPU-PE-PASS',13,10,'$'
failure_message db 'CPU-PE-FAIL',13,10,'$'
align 4, db 0
proof:
    db 'CPPE0001'
    dd 1
    times 53 dd 0
align 8, db 0
gdt:
    dq 0
gdt_code16: dw 0xffff,0
    db 0,0x9a,0,0
gdt_data16: dw 0xffff,0
    db 0,0x92,0,0
gdt_code32: dw 0xffff,0
    db 0,0x9a,0xcf,0
gdt_data32: dw 0xffff,0
    db 0,0x92,0xcf,0
gdt_stack32: dw 0xffff,0
    db 0,0x92,0xcf,0
gdt_flat: dw 0xffff,0
    db 0,0x92,0xcf,0
gdt_cache: dw 3,0
    db 0,0x92,0,0
gdt_absent_data: dw 0xffff,0
    db 0,0x12,0xcf,0
gdt_absent_stack: dw 0xffff,0
    db 0,0x12,0xcf,0
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
align 8, db 0
%macro gate 2
    dw (%1-$$+0x10100) & 0xffff,CODE32
    db 0,%2
    dw (%1-$$+0x10100) >> 16
%endmacro
idt:
    times 6 dq 0
    gate ud_handler,0x8e
    times 4 dq 0
    gate np_handler,0x8e
    gate ss_handler,0x8e
    gate gp_handler,0x8e
    times (0x60-14) dq 0
    gate trap_handler,0x8f
    gate interrupt_handler,0x8e
idt_end:
align 4, db 0
stack16 times 256 db 0
stack16_end:
stack32 times 512 db 0
stack32_end:
