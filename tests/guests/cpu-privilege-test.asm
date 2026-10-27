; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS probe: LLDT/LTR, ring 0 -> 3 -> TSS interrupt/fault -> 3.
; All descriptors, stacks and records remain inside our own COM allocation.
; TR cannot be unloaded with LTR: after the return to real mode its cached
; private TSS remains until reset. Each acceptance run therefore cold-boots.
bits 16
cpu 486
org 0x100

C16 equ 8
D16 equ 16
C0 equ 24
D0 equ 32
S0 equ 40
C3 equ 48
D3 equ 56
S3 equ 64
LDT equ 72
TSS equ 80
CONFORMING equ 88

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
    pushf
    pop word [original_flags]
    cli
    cld
    in al,0x21
    mov [pic_master],al
    in al,0xa1
    mov [pic_slave],al
    movzx eax,byte [pic_slave]
    shl eax,8
    mov al,[pic_master]
    mov [proof+60],eax
    mov al,0xff
    out 0x21,al
    out 0xa1,al
    o32 sgdt [original_gdt]
    o32 sidt [original_idt]
    mov eax,cr0
    mov [proof+20],eax
    mov eax,[original_gdt+2]
    mov [proof+28],eax
    movzx eax,word [original_gdt]
    mov [proof+44],eax
    mov eax,[original_idt+2]
    mov [proof+36],eax
    movzx eax,word [original_idt]
    mov [proof+52],eax
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [linear_base],eax

%macro patch 2
    mov eax,[linear_base]
    add eax,%2
    mov di,%1
    call patch_base
%endmacro
    patch gdt_c16,0
    patch gdt_d16,0
    patch gdt_c0,-0x10000
    patch gdt_d0,0
    patch gdt_s0,-0x10000
    patch gdt_c3,-0x10000
    patch gdt_d3,0
    patch gdt_s3,-0x10000
    patch gdt_conforming,0
    patch gdt_ldt,ldt_a
    patch gdt_tss,tss
    patch ldt_a,cache_a
    patch ldt_b,cache_b
    mov eax,[linear_base]
    add eax,gdt
    mov [private_gdt+2],eax
    add eax,gdt_tss-gdt
    mov [proof+276],eax
    mov eax,[linear_base]
    add eax,idt
    mov [private_idt+2],eax
    mov eax,[linear_base]
    add eax,tss
    mov [proof+228],eax
    mov eax,[linear_base]
    add eax,ldt_a
    mov [proof+236],eax
    mov eax,[linear_base]
    add eax,ldt_b
    mov [proof+240],eax
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
    mov ax,S0
    mov ss,ax
    mov esp,0x10000+initial_stack_end
    mov [proof+292],esp
    ; DOS can leave real-mode NT/IOPL set. This is a stack IRET, not a task return.
    push dword 2
    popfd
    sldt [proof+256]
    str [proof+252]
    mov ax,LDT
    lldt ax
    sldt [proof+68]
    mov ax,4
    mov fs,ax
    mov eax,[fs:0]
    mov [proof+80],eax
    mov eax,[proof+240]
    mov edi,gdt_ldt
    call patch_base32
    ; LDTR still points at ldt_a, even when loading a new FS descriptor.
    mov ax,4
    mov fs,ax
    mov eax,[fs:0]
    mov [proof+84],eax
    mov ax,LDT
    lldt ax
    mov ax,4
    mov fs,ax
    mov eax,[fs:0]
    mov [proof+88],eax
    or dword [proof+16],1
    mov ax,TSS
    ltr ax
    str [proof+72]
    movzx eax,byte [gdt_tss+5]
    mov [proof+76],eax
    ; Mutate the GDT object to an absent, truncated TSS at another address.
    ; Interrupts must use the hidden TR established by the earlier LTR.
    mov eax,[linear_base]
    add eax,dummy_tss
    mov [proof+232],eax
    mov edi,gdt_tss
    call patch_base32
    mov word [gdt_tss],0
    mov byte [gdt_tss+5],9
    mov dword [proof+280],9
    or dword [proof+16],2
    mov ax,D3|3
    mov es,ax
    mov ax,CONFORMING|3
    mov gs,ax
    push dword S3|3
    push dword 0x10000+user_stack_end
    push dword 0x202
    push dword C3|3
    push dword 0x10000+user_entry
    iretd

; Patches the full descriptor base used by the controlled 32-bit privilege transition.
patch_base32:
    mov [edi+2],ax
    shr eax,16
    mov [edi+4],al
    mov [edi+7],ah
    ret

user_entry:
    ; DS and FS held ring-zero data; ES held ring-three data, GS conforming code.
    mov ax,ds
    movzx eax,ax
    mov [es:proof+100],eax
    mov ax,es
    movzx eax,ax
    mov [es:proof+104],eax
    mov ax,fs
    movzx eax,ax
    mov [es:proof+108],eax
    mov ax,gs
    movzx eax,ax
    mov [es:proof+112],eax
    mov ax,D3|3
    mov ds,ax
    mov ax,cs
    movzx eax,ax
    mov [proof+92],eax
    mov ax,ss
    movzx eax,ax
    mov [proof+96],eax
    mov [proof+116],esp
    pushfd
    pop dword [proof+124]
    mov eax,0xabcdffff
    sldt ax
    mov [proof+132],eax
    mov eax,0xdcbaeeee
    str ax
    mov [proof+136],eax
    or dword [proof+16],4
    int 0x60
after_interrupt:
    mov [proof+120],esp
    pushfd
    pop dword [proof+128]
    or dword [proof+16],8
user_cli:
    cli
after_cli:
    or dword [proof+16],16
user_forbidden_int:
    int 0x61
after_forbidden_int:
    or dword [proof+16],32
    ; POPFD at CPL3 may change CF, but must preserve IF and IOPL=0.
    push dword 0x3003
    popfd
    pushfd
    pop dword [proof+244]
    mov dword [proof+268],0x10000+user_entry
    mov dword [proof+272],0x10000+user_stack_end
    int 0x62
    db 0x0f,0x0b                ; Unreachable undefined 486 encoding.

; Validates the probe's actual protected interrupt/privilege frame before returning.
interrupt_handler:
    mov [proof+152],esp
    mov eax,[ss:esp]
    mov [proof+156],eax
    mov eax,[ss:esp+4]
    mov [proof+160],eax
    mov eax,[ss:esp+8]
    mov [proof+164],eax
    mov eax,[ss:esp+12]
    mov [proof+168],eax
    mov eax,[ss:esp+16]
    mov [proof+172],eax
    mov ax,cs
    and eax,3
    mov [proof+144],eax
    mov ax,ss
    movzx eax,ax
    mov [proof+148],eax
    inc dword [proof+140]
    ; The next transition must re-read TSS memory, retaining the cached TR base.
    mov dword [tss+4],0x10000+kernel_b_end
    mov dword [proof+216],0x10000+kernel_b_end
    iretd

gp_handler:
    push eax
    inc dword [proof+176]
    cmp dword [ss:esp+8],0x10000+user_cli
    je .cli
    cmp dword [ss:esp+8],0x10000+user_forbidden_int
    je .gate
    mov dword [proof+248],13
    pop eax
    jmp cleanup32
.cli:
    lea eax,[esp+4]
    mov [proof+204],eax
    movzx eax,word [ss:esp+4] ; Only the low error word is written by a 486.
    mov [proof+180],eax
    mov eax,[ss:esp+8]
    mov [proof+184],eax
    mov eax,[ss:esp+16]
    mov [proof+188],eax
    add dword [ss:esp+8],after_cli-user_cli
    jmp .return
.gate:
    lea eax,[esp+4]
    mov [proof+208],eax
    movzx eax,word [ss:esp+4]
    mov [proof+192],eax
    mov eax,[ss:esp+8]
    mov [proof+196],eax
    mov eax,[ss:esp+16]
    mov [proof+200],eax
    add dword [ss:esp+8],after_forbidden_int-user_forbidden_int
.return:
    pop eax
    add esp,4
    iretd

end_handler:
    mov [proof+212],esp
    or dword [proof+16],64
    jmp cleanup32
unexpected_handler:
    mov ax,D0
    mov ds,ax
    mov dword [proof+248],0xff
cleanup32:
    mov ax,D0
    mov ds,ax
    xor eax,eax
    lldt ax
    sldt [proof+260]
    str [proof+264]
    mov eax,[guard_a]
    mov [proof+220],eax
    mov eax,[guard_b]
    mov [proof+224],eax
    mov eax,[tss+4]
    mov [proof+284],eax
    jmp word C16:cleanup16

bits 16
cleanup16:
    mov ax,D16
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov sp,stack16_end
    o32 lidt [original_idt]
    mov eax,[proof+20]
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
    o32 sgdt [table_output]
    mov eax,[table_output+2]
    mov [proof+32],eax
    movzx eax,word [table_output]
    mov [proof+48],eax
    o32 sidt [table_output]
    mov eax,[table_output+2]
    mov [proof+40],eax
    movzx eax,word [table_output]
    mov [proof+56],eax
    mov eax,cr0
    mov [proof+24],eax
    mov al,[pic_master]
    out 0x21,al
    mov al,[pic_slave]
    out 0xa1,al
    xor eax,eax
    in al,0xa1
    shl eax,8
    in al,0x21
    mov [proof+64],eax
    or dword [proof+16],128
    cmp dword [proof+16],255
    jne .marked
    cmp dword [proof+248],0
    jne .marked
    cmp dword [proof+140],1
    jne .marked
    cmp dword [proof+176],2
    jne .marked
    mov dword [proof+12],1
.marked:
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
    jne .failed
    mov dx,success_message
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
.failed:
    mov dx,failure_message
    mov ah,9
    int 0x21
    mov ax,0x4c01
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
original_flags dw 0
original_gdt times 6 db 0
original_idt times 6 db 0
table_output times 6 db 0
linear_base dd 0
pic_master db 0
pic_slave db 0
real_pointer dw real_return,0
cache_a dd 0x11223344
cache_b dd 0x55667788
proof_marker db 'CPU-PRIV-PROOF=',0
newline db 13,10,0
success_message db 'CPU-PRIV-PASS',13,10,'$'
failure_message db 'CPU-PRIV-FAIL',13,10,'$'
; The independent oracle verifies the labels against actual instruction bytes.
symbol_table:
    db 'CPPRADDR'
    dd user_entry,after_interrupt,user_cli,user_forbidden_int
    dd kernel_a_end,kernel_b_end,user_stack_end,tss,gdt_tss,ldt_b
align 4,db 0
proof:
    db 'CPPR0001'
    dd 1
    times 77 dd 0
align 8,db 0
gdt:
    dq 0
gdt_c16: dw 0xffff,0
    db 0,0x9a,0,0
gdt_d16: dw 0xffff,0
    db 0,0x92,0,0
gdt_c0: dw 0xffff,0
    db 0,0x9a,0x41,0
gdt_d0: dw 0xffff,0
    db 0,0x92,0x40,0
gdt_s0: dw 0xffff,0
    db 0,0x92,0x41,0
gdt_c3: dw 0xffff,0
    db 0,0xfa,0x41,0
gdt_d3: dw 0xffff,0
    db 0,0xf2,0x40,0
gdt_s3: dw 0xffff,0
    db 0,0xf2,0x41,0
gdt_ldt: dw 7,0
    db 0,0x82,0,0
gdt_tss: dw 0x67,0
    db 0,0x89,0,0
gdt_conforming: dw 0xffff,0
    db 0,0x9e,0x40,0
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
ldt_a: dw 3,0
    db 0,0x92,0,0
ldt_b: dw 3,0
    db 0,0x92,0,0
align 4,db 0
tss:
    dd 0
    dd 0x10000+kernel_a_end
    dw S0,0
    times (0x68-12) db 0
dummy_tss times 104 db 0
%macro gate 2
    dw (%1-$$+0x10100) & 0xffff,C0
    db 0,%2
    dw (%1-$$+0x10100) >> 16
%endmacro
align 8,db 0
idt:
    times 10 dq 0
    gate unexpected_handler,0x8e ; #TS
    gate unexpected_handler,0x8e ; #NP
    gate unexpected_handler,0x8e ; #SS
    gate gp_handler,0x8e
    times (0x60-14) dq 0
    gate interrupt_handler,0xee
    gate unexpected_handler,0x8e ; deliberately forbidden INT61 at CPL3
    gate end_handler,0xee
idt_end:
align 4,db 0
stack16 times 128 db 0
stack16_end:
initial_stack times 128 db 0
initial_stack_end:
guard_a dd 0xc0dea001
kernel_a times 256 db 0
kernel_a_end:
guard_b dd 0xc0deb002
kernel_b times 384 db 0
kernel_b_end:
user_stack times 256 db 0
user_stack_end:
