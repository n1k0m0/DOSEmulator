; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS client: real 16/32-bit call gates, TSS stacks and RETF parameters.
; The private TR cache intentionally remains loaded until the next cold reset.
bits 16
cpu 486
org 0x100
%ifndef PAGING
%define PAGING 0
%endif
C16 equ 8
D16 equ 16
C0 equ 24
D0 equ 32
S0 equ 40
C3 equ 48
D3 equ 56
S3 equ 64
K16 equ 72
KS16 equ 80
U16 equ 88
US16 equ 96
CG32 equ 104
CG16 equ 112
NEXT16 equ 120
BAD equ 128
ABSENT equ 136
SAME equ 144
CONFORM equ 152
CC equ 160
JUMP equ 168
TSS equ 176
LDT equ 184
FROM16 equ 192
EXIT equ 200

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
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
    pushfd
    pop dword [original_flags]
    cli
    cld
    mov eax,cr0
    mov [proof+24],eax
    mov eax,cr3
    mov [proof+32],eax
    o32 sgdt [original_gdt]
    o32 sidt [original_idt]
    mov eax,[original_gdt+2]
    mov [proof+40],eax
    movzx eax,word [original_gdt]
    mov [proof+48],eax
    mov eax,[original_idt+2]
    mov [proof+56],eax
    movzx eax,word [original_idt]
    mov [proof+64],eax
    in al,0x21
    mov [pic_master],al
    in al,0xa1
    mov [pic_slave],al
    movzx eax,byte [pic_slave]
    shl eax,8
    mov al,[pic_master]
    mov [proof+72],eax
    mov al,0xff
    out 0x21,al
    out 0xa1,al
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [proof+80],eax
%macro patch 2
    mov eax,[proof+80]
    add eax,%2
    mov di,%1
    call patch_base
%endmacro
    patch gdt+C16,0
    patch gdt+D16,0
    patch gdt+C0,-0x10000
    patch gdt+D0,0
    patch gdt+S0,-0x10000
    patch gdt+C3,-0x10000
    patch gdt+D3,0
    patch gdt+S3,-0x10000
    patch gdt+K16,0
    patch gdt+KS16,0
    patch gdt+U16,0
    patch gdt+US16,0
    patch gdt+CC,-0x10000
    patch gdt+TSS,tss
    patch gdt+LDT,ldt
    mov eax,[proof+80]
    add eax,gdt
    mov [private_gdt+2],eax
    mov eax,[proof+80]
    add eax,idt
    mov [private_idt+2],eax
    mov eax,[proof+80]
    add eax,tss
    mov [proof+84],eax
    mov dword [proof+96],PAGING
    o32 lgdt [private_gdt]
    o32 lidt [private_idt]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword C0:0x10000+kernel_entry
; Patches the explicit descriptor base bytes for this probe's allocated guest segment.
patch_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret

bits 32
kernel_entry:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov ax,S0
    mov ss,ax
    mov esp,0x10000+initial_end
    push dword 2
    popfd
    str [proof+424]
    sldt [proof+426]
    mov ax,TSS
    ltr ax
    mov ax,LDT
    lldt ax
%if PAGING
    ; Runtime alignment keeps both paging structures inside our own COM block.
    mov eax,[proof+80]
    add eax,paging_storage+4095
    and eax,0xfffff000
    mov [proof+88],eax
    mov ebx,eax
    sub ebx,[proof+80]
    mov edi,ebx
    xor eax,eax
    mov ecx,2048
    rep stosd
    mov eax,[proof+88]
    add eax,4096
    mov [proof+92],eax
    or eax,7
    mov [ebx],eax
    lea edi,[ebx+4096]
    mov eax,7
    mov ecx,1024
.pte:
    stosd
    add eax,4096
    loop .pte
    mov eax,[proof+88]
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000           ; PG and supervisor write-protect
    mov cr0,eax
%endif
    or dword [proof+16],1
    push dword S3|3
    push dword 0x10000+user32_end
    push dword 2
    push dword C3|3
    push dword 0x10000+user32
    iretd

%macro flags2 0
    push dword 2
    popfd
%endmacro
user32:
    mov ax,D3|3
    mov ds,ax
    mov es,ax
    mov [proof+228],esp
    push dword 0x89abcdef
    push dword 0x12345678
    call word CG32:0xbeef      ; 16-bit operand, gate still saves full EIP/ESP
after_gdt32:
    or dword [proof+16],2
    flags2
    push dword 0x2468ace0
    push dword 0x13579bdf
    call far [indirect_ldt]    ; genuine FF/3, LDT index zero with RPL3
after_ldt32:
    mov [proof+232],esp
    pushfd
    pop dword [proof+408]
    or dword [proof+16],4
    flags2
    call dword SAME:0xdeadbeef
after_same:
    or dword [proof+16],8
    flags2
    call dword CONFORM:0xdeadbeef
after_conform:
    or dword [proof+16],16
    jmp dword JUMP:0xdeadbeef
jumped:
    mov [proof+224],esp
    or dword [proof+16],32
    flags2
bad_call:
    call dword BAD:0
after_bad:
absent_call:
    call dword ABSENT:0
after_absent:
    or dword [proof+16],64
    flags2
    call dword NEXT16:0
    db 0x0f,0x0b

service32:
    pushfd
    pop dword [flags_scratch]
    mov edi,proof+128
    mov ebx,proof+376
    mov edx,proof+400
    cmp dword [proof+100],0
    je .record
    mov edi,proof+160
    add ebx,4
    add edx,4
.record:
    mov esi,esp
    mov ecx,6
.copy:
    mov eax,[ss:esi]
    stosd
    add esi,4
    loop .copy
    mov ax,ss
    movzx eax,ax
    mov [ebx],eax
    mov eax,[flags_scratch]
    mov [edx],eax
    inc dword [proof+100]
    mov dword [tss+4],0x10000+kernel_b_end
    push dword [flags_scratch]
    popfd
    retf 8

same32:
    mov eax,[ss:esp]
    mov [proof+192],eax
    mov eax,[ss:esp+4]
    mov [proof+196],eax
    mov ax,cs
    and eax,3
    mov [proof+384],eax
    mov ax,ss
    movzx eax,ax
    mov [proof+392],eax
    retf
conform32:
    mov eax,[ss:esp]
    mov [proof+208],eax
    mov eax,[ss:esp+4]
    mov [proof+212],eax
    mov ax,cs
    and eax,3
    mov [proof+388],eax
    mov ax,ss
    movzx eax,ax
    mov [proof+396],eax
    retf

gp_handler:
    inc dword [proof+104]
    mov [proof+260],esp
    mov eax,[ss:esp+4]
    mov [proof+236],eax
    cmp eax,0x10000+bad_call
    jne unexpected
    movzx eax,word [ss:esp]
    mov [proof+240],eax
    mov eax,[ss:esp+12]
    mov [proof+244],eax
    mov dword [ss:esp+4],0x10000+after_bad
    add esp,4
    iretd
np_handler:
    inc dword [proof+108]
    mov [proof+264],esp
    mov eax,[ss:esp+4]
    mov [proof+248],eax
    cmp eax,0x10000+absent_call
    jne unexpected
    movzx eax,word [ss:esp]
    mov [proof+252],eax
    mov eax,[ss:esp+12]
    mov [proof+256],eax
    mov dword [ss:esp+4],0x10000+after_absent
    add esp,4
    iretd

setup16:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov ax,S0
    mov ss,ax
    mov esp,0x10000+initial_end
    mov dword [tss+4],kernel16_end
    mov word [tss+8],KS16
    or dword [proof+16],128
    push dword US16|3
    push dword user16_end
    push dword 2
    push dword U16|3
    push dword user16
    iretd

bits 16
user16:
    mov ax,D3|3
    mov ds,ax
    mov es,ax
    movzx eax,sp
    mov [proof+304],eax
    push word 0x2468
    push word 0x1357
    call dword CG16:0xdeadbeef ; 32-bit operand, gate still uses word frame
after_word:
    movzx eax,sp
    mov [proof+308],eax
    pushfd
    pop dword [proof+416]
    or dword [proof+16],256
    flags2
    push dword 0x22222222
    push dword 0x11111111
    call word FROM16:0xbeef
after_from16:
    or dword [proof+16],512
    flags2
    call word EXIT:0
    db 0x0f,0x0b

service16:
    pushfd
    pop dword [proof+412]
    mov bp,sp
    xor si,si
    mov cx,6
.copy:
    mov ax,[ss:bp+si]
    mov [proof+288+si],ax
    add si,2
    loop .copy
    mov ax,cs
    and eax,3
    mov [proof+364],eax
    mov ax,ss
    movzx eax,ax
    mov [proof+368],eax
    push dword [proof+412]
    popfd
    retf 4

bits 32
service_from16:
    pushfd
    pop dword [proof+420]
    mov esi,esp
    mov edi,proof+320
    mov ecx,6
.copy:
    mov eax,[ss:esi]
    stosd
    add esi,4
    loop .copy
    mov ax,ss
    movzx eax,ax
    mov [proof+372],eax
    push dword [proof+420]
    popfd
    retf 8

unexpected:
    mov dword [proof+20],0xff
cleanup32:
    mov ax,D0
    mov ds,ax
    xor eax,eax
    lldt ax
    str [proof+112]
    sldt [proof+116]
    movzx eax,byte [gdt+CG32+5]
    mov [proof+352],eax
    movzx eax,byte [gdt+CG16+5]
    mov [proof+356],eax
    movzx eax,byte [ldt+5]
    mov [proof+360],eax
    mov eax,[guard_a]
    mov [proof+460],eax
    mov eax,[guard_b]
    mov [proof+464],eax
    mov eax,[guard_16]
    mov [proof+468],eax
    or dword [proof+16],1024
    jmp word C16:cleanup16

bits 16
cleanup16:
    mov ax,D16
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov sp,initial_end
    o32 lidt [original_idt]
    mov eax,[proof+24]
    mov cr0,eax               ; leave our identity paging before restoring CR3
    jmp far [cs:real_pointer]
real_return:
    mov eax,[cs:proof+32]
    mov cr3,eax
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
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+44],eax
    movzx eax,word [table_output]
    mov [proof+52],eax
    o32 sidt [table_output]
    mov eax,[table_output+2]
    mov [proof+60],eax
    movzx eax,word [table_output]
    mov [proof+68],eax
    mov eax,cr0
    mov [proof+28],eax
    mov eax,cr3
    mov [proof+36],eax
    mov al,[pic_master]
    out 0x21,al
    mov al,[pic_slave]
    out 0xa1,al
    xor eax,eax
    in al,0xa1
    shl eax,8
    in al,0x21
    mov [proof+76],eax
    or dword [proof+16],2048
    cmp dword [proof+16],4095
    jne .report
    cmp dword [proof+20],0
    jne .report
    mov dword [proof+12],1
.report:
    mov si,marker
    call debug_string
    mov ebx,[proof+80]
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
    push dword [original_flags]
    popfd
    mov dx,success
    cmp dword [proof+12],1
    je .print
    mov dx,failure
.print:
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
; Writes the zero-terminated proof marker through actual guest OUT instructions.
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
original_flags dd 0
original_gdt times 6 db 0
original_idt times 6 db 0
table_output times 6 db 0
pic_master db 0
pic_slave db 0
real_pointer dw real_return,0
flags_scratch dd 0
indirect_ldt dd 0xdeadbeef
    dw 7
marker db 'CPU-CALL-PROOF=',0
newline db 13,10,0
success db 'CPU-CALL-PASS',13,10,'$'
failure db 'CPU-CALL-FAIL',13,10,'$'
symbols:
    db 'CPCGADDR'
    dd start,original_sp,proof,after_gdt32,after_ldt32,after_same,after_conform
    dd bad_call,after_bad,absent_call,after_absent,after_word,after_from16
    dd kernel_a_end,kernel_b_end,kernel16_end,user32_end,user16_end,tss,gdt,ldt
    dd paging_storage,program_end
align 4,db 0
proof:
    db 'CPCG0001'
    dd 1
    times 125 dd 0

%macro code 2
    dw 0xffff,0
    db 0,%1,%2,0
%endmacro
%macro cg32 4
    dw (%1-$$+0x10100)&0xffff,%2
    db %3,%4
    dw (%1-$$+0x10100)>>16
%endmacro
align 8,db 0
gdt:
    dq 0
    code 0x9a,0
    code 0x92,0
    code 0x9a,0x41
    code 0x92,0x40
    code 0x92,0x41
    code 0xfa,0x41
    code 0xf2,0x40
    code 0xf2,0x41
    code 0x9a,0
    code 0x92,0
    code 0xfa,0
    code 0xf2,0
    cg32 service32,C0|3,2,0xec
    dw service16,K16|3
    db 2,0xe4
    dw 0xdead                 ; ignored high offset in a 16-bit gate
    cg32 setup16,C0,0,0xec
    cg32 unexpected,C0,0,0x8c
    cg32 service32,C0,0,0x6c
    cg32 same32,C3|3,31,0xec
    cg32 conform32,CC|3,31,0xec
    code 0x9e,0x41
    cg32 jumped,C3,31,0xec
    dw 0x67,0
    db 0,0x89,0,0
    dw 7,0
    db 0,0x82,0,0
    cg32 service_from16,C0,2,0xec
    cg32 cleanup32,C0,0,0xec
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
align 8,db 0
ldt:
    cg32 service32,C0,2,0xec
align 4,db 0
tss:
    dd 0
    dd 0x10000+kernel_a_end
    dw S0,0
    times (104-12) db 0
%macro irq 1
    cg32 %1,C0,0,0x8e
%endmacro
align 8,db 0
idt:
    times 8 dq 0
    irq unexpected
    dq 0
    irq unexpected
    irq np_handler
    irq unexpected
    irq gp_handler
    irq unexpected
idt_end:
align 16,db 0
initial_stack times 256 db 0
initial_end:
guard_a dd 0xc0dea001
kernel_a times 384 db 0
kernel_a_end:
    dd 0xcafea001
guard_b dd 0xc0deb002
kernel_b times 384 db 0
kernel_b_end:
    dd 0xcafeb002
guard_16 dd 0xc0dec003
kernel16 times 384 db 0
kernel16_end:
    dd 0xcafec003
user32_stack times 256 db 0
user32_end:
    dd 0xcafed004
user16_stack times 256 db 0
user16_end:
    dd 0xcafee005
align 16,db 0
paging_storage:
%if PAGING
    times 12288 db 0
%endif
program_end:
