; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own raw x87 exponential test client, loaded by actual DOS.
%include "x87-logarithm-data.inc"
; No resident manager: private PE tables/stacks, optionally private 4-KiB pages.
bits 16
cpu 486
org 0x100
%ifndef PROTECTED
%define PROTECTED 0
%endif
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
TSS equ 72
%macro patch 2
    mov eax,[proof+80]
    add eax,%2
    mov di,%1
    call patch_base
%endmacro
%if PROTECTED
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
%else
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
    pushfd
    pop dword [original_flags]
    cli
    cld
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [proof+80],eax
%endif
    o32 fnsave [original_fpu]
%if PROTECTED
    patch gdt+C16,0
    patch gdt+D16,0
    patch gdt+C0,-0x10000
    patch gdt+D0,0
    patch gdt+S0,-0x10000
    patch gdt+C3,-0x10000
    patch gdt+D3,0
    patch gdt+S3,-0x10000
    patch gdt+TSS,tss
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
    push dword 2            ; DOS may leave NT set in real mode; this private PE entry must clear it
    popfd
    mov ax,TSS
    ltr ax
%if PAGING
    mov eax,[proof+80]
    add eax,paging_storage+4095
    and eax,0xfffff000
    mov [proof+88],eax
    mov edi,eax
    sub edi,[proof+80]
    xor eax,eax
    mov ecx,2048
    rep stosd
    mov eax,[proof+88]
    add eax,4096
    mov [proof+92],eax
    mov edi,eax
    sub edi,[proof+80]
    or eax,7
    mov ebx,[proof+88]
    sub ebx,[proof+80]
    mov [ebx],eax
    xor ecx,ecx
.pte:
    mov eax,ecx
    shl eax,12
    or eax,7
    mov [edi+ecx*4],eax
    inc ecx
    cmp ecx,1024
    jb .pte
    mov eax,[proof+88]
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000
    mov cr0,eax
%endif
    mov dword [proof+104],1
    mov dword [proof+96],PAGING
    mov eax,cr0
    mov [proof+100],eax
%include "x87-logarithm-body.inc"
cleanup32:
    str [proof+124]
    jmp word C16:cleanup16
unexpected:
    inc dword [proof+20]
    cli
    hlt
    jmp unexpected
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
    mov cr0,eax
    jmp far [cs:real_pointer]
%else
    mov dword [proof+104],0
    mov dword [proof+96],0
    smsw ax
    movzx eax,ax
    mov [proof+100],eax
%include "x87-logarithm-body.inc"
    jmp real_return
%endif
real_return:
%if PROTECTED
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
%endif
    o32 frstor [original_fpu]
    o32 fnsave [restored_fpu]
    o32 frstor [restored_fpu]
    push cs
    pop es
    cld
    mov si,original_fpu
    mov di,restored_fpu
    mov cx,108
    repe cmpsb
    jne .report
    mov dword [proof+236],1
    cmp dword [proof+232],VECTOR_COUNT
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
    mov ax,[original_es]
    mov es,ax
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
marker db 'X87-LOGARITHM-PROOF=',0
newline db 13,10,0
success db 'X87-LOGARITHM-PASS',13,10,'$'
failure db 'X87-LOGARITHM-FAIL',13,10,'$'
symbols:
    db 'FPLOGADR'
    dd (symbols_end-symbols_data)/4
symbols_data:
    dd start,original_sp,proof,vector_data,result_data,original_fpu,restored_fpu
    dd body_start,body_end,kernel_end,gdt,tss,paging_storage,program_end,logarithm_addresses,mf_vectors,mf_results
symbols_end:
align 16,db 0
proof:
    db 'FPLOG001'
    dd 1
    times 253 dd 0
align 16,db 0
vector_data:
    logarithm_vectors
vector_end:
align 16,db 0
result_data:
%assign SLOT 0
%rep VECTOR_COUNT
    times 116 db 0xa5
    dd 0xc0de0000+SLOT,0xcafe0000+SLOT,0xbeef0000+SLOT
%assign SLOT SLOT+1
%endrep
result_end:
%macro guarded 2
    dd 0xface0000+%2
%1:
    times %2 db 0xa5
    dd 0xcafe0000+%2
    align 16,db 0
%endmacro
guarded seed_env,28
guarded original_fpu,108
guarded restored_fpu,108
align 16,db 0
mf_vectors:
    logarithm_mf_vectors
mf_results:
%assign SLOT 0
%rep MF_COUNT
    times 228 db 0xa5
    dd 0xc0de1000+SLOT,0xcafe1000+SLOT,0xbeef1000+SLOT
    times 16 db 0xa5
%assign SLOT SLOT+1
%endrep
%if PROTECTED
%macro descriptor 3
    dw %1,0
    db 0,%2,%3,0
%endmacro
align 8,db 0
gdt:
    dq 0
    descriptor 0xffff,0x9a,0
    descriptor 0xffff,0x92,0
    descriptor 0xffff,0x9a,0x41
    descriptor 0xffff,0x92,0x40
    descriptor 0xffff,0x92,0x41
    descriptor 0xffff,0xfa,0x41
    descriptor 0xffff,0xf2,0x40
    descriptor 0xffff,0xf2,0x41
    descriptor 0x67,0x89,0
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
align 4,db 0
tss:
    dd 0,0x10000+kernel_end
    dw S0,0
    times (104-12) db 0
%macro irq 2
    dw (%1-$$+0x10100)&0xffff,C0
    db 0,%2
    dw (%1-$$+0x10100)>>16
%endmacro
align 8,db 0
idt:
    times 6 dq 0
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    dq 0
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    dq 0
    irq logarithm_mf_handler,0x8e
    irq unexpected,0x8e
    times (0x60-18) dq 0
    irq cleanup32,0xee
idt_end:
align 16,db 0
initial_stack times 256 db 0
initial_end:
    dd 0xc0de0001
kernel_stack times 512 db 0xa5
kernel_end: dd 0xcafe0001
    dd 0xc0de0002
user_stack times 256 db 0xa5
user_end: dd 0xcafe0002
align 16,db 0
paging_storage:
%if PAGING
    times 16384 db 0
%endif
%else
kernel_end:
gdt:
tss:
paging_storage:
%endif
program_end:
