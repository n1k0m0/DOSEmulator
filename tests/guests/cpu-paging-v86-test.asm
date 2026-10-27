; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS guest: 4-KiB MMU/TLB, restartable faults and a real V86 monitor.
; Descriptors, page tables, data and stacks belong to this COM allocation.
bits 16
cpu 486
org 0x100
C16 equ 8
D16 equ 16
C0 equ 24
D0 equ 32
FLAT equ 40
TSS equ 48
ALIAS equ 0x300000
VMDATA equ 0x60000

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    mov [old_sp],sp
    mov ax,ss
    mov [old_ss],ax
    mov ax,es
    mov [old_es],ax
    mov ax,fs
    mov [old_fs],ax
    mov ax,gs
    mov [old_gs],ax
    pushf
    pop word [old_flags]
    cli
    cld
    in al,0x21
    mov [old_pic],al
    in al,0xa1
    mov [old_pic+1],al
    mov al,0xff
    out 0x21,al
    out 0xa1,al
    mov ax,cs
    mov [real_pointer+2],ax
    mov [vm_cs],ax
    movzx eax,ax
    shl eax,4
    mov [proof+200],eax
    mov edi,gdt_c16
    call patch_base
    mov edi,gdt_d16
    call patch_base
    mov edi,gdt_c0
    call patch_base
    mov edi,gdt_d0
    call patch_base
    add eax,tss
    mov edi,gdt_tss
    call patch_base
    mov eax,[proof+200]
    add eax,gdt
    mov [new_gdt+2],eax
    mov eax,[proof+200]
    add eax,idt
    mov [new_idt+2],eax
    o32 sgdt [old_gdt]
    o32 sidt [old_idt]
    mov eax,cr0
    mov [proof+20],eax
    mov eax,cr3
    mov [proof+28],eax
    o32 lgdt [new_gdt]
    o32 lidt [new_idt]
    mov eax,cr0
    or al,1
    mov cr0,eax
    jmp dword C0:kernel_entry
; Patches the explicit descriptor base bytes for this probe's allocated guest segment.
patch_base:
    mov [di+2],ax
    push eax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    pop eax
    ret

bits 32
kernel_entry:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov esp,kernel_stack_end
    mov ax,FLAT
    mov fs,ax
    push dword 2
    popfd
    mov eax,[proof+200]
    add eax,page_space+4095
    and eax,0xfffff000
    mov [proof+36],eax
    mov edi,eax
    add eax,4096
    mov [proof+40],eax
    add eax,4096
    mov [proof+44],eax
    mov dword [fs:eax],0x11223344
    add eax,4096
    mov [proof+48],eax
    mov dword [fs:eax],0x55667788
    mov ecx,1024
    xor eax,eax
.clear_dir:
    mov [fs:edi],eax
    add edi,4
    loop .clear_dir
    mov edi,[proof+40]
    mov eax,7
    mov ecx,1024
.identity:
    mov [fs:edi],eax
    add edi,4
    add eax,4096
    loop .identity
    mov edi,[proof+36]
    mov eax,[proof+40]
    or al,7
    mov [fs:edi],eax
    mov edi,[proof+40]
    mov eax,[proof+44]
    or al,7
    mov [fs:edi+(ALIAS>>12)*4],eax
    mov eax,[proof+36]
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000
    mov cr0,eax
    mov eax,[fs:ALIAS]
    mov [proof+52],eax
    mov edi,[proof+40]
    mov eax,[proof+48]
    or al,7
    mov [fs:edi+(ALIAS>>12)*4],eax
    mov eax,[fs:ALIAS]
    mov [proof+56],eax
    invlpg [fs:ALIAS]
    mov eax,[fs:ALIAS]
    mov [proof+60],eax
    mov dword [fs:ALIAS],0xabcdef78
    mov eax,[fs:ALIAS]
    mov [proof+64],eax
    mov eax,[fs:edi+(ALIAS>>12)*4]
    mov [proof+68],eax
    mov edi,[proof+36]
    mov eax,[fs:edi]
    mov [proof+72],eax
    or dword [proof+16],1
    ; Supervisor CR0.WP fault must neither mutate data nor lose restart state.
    mov edi,[proof+40]
    and dword [fs:edi+(ALIAS>>12)*4],~2
    invlpg [fs:ALIAS]
wp_fault:
    mov dword [fs:ALIAS],0xbad0bad0
after_wp:
    mov dword [fs:edi+(ALIAS>>12)*4],0
    invlpg [fs:ALIAS]
missing_fault:
    mov eax,[fs:ALIAS]
after_missing:
    mov [proof+204],eax
    or dword [proof+16],2
    ; Ring-zero TSS stack and an all-denied 486 I/O bitmap (including guard).
    mov ax,TSS
    ltr ax
    mov edi,[proof+40]
    mov eax,[proof+48]
    or al,3
    mov [fs:edi+(VMDATA>>12)*4],eax
    invlpg [fs:VMDATA]
    push dword 0             ; GS
    movzx eax,word [vm_cs]
    push eax                ; FS
    push dword 0x6000        ; DS
    push eax                ; ES
    push eax                ; SS
    push dword vm_stack_end
    push dword 0x20002       ; VM=1, IF=0, IOPL=0
    push eax                ; CS
    push dword vm_entry
    iretd

pf_handler:
    pushad
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov ax,FLAT
    mov fs,ax
    inc dword [proof+76]
    mov eax,cr2
    mov edx,[ss:esp+32]
    mov ebx,[ss:esp+36]
    cmp dword [proof+76],1
    jne .missing
    mov [proof+80],eax
    mov [proof+84],edx
    mov [proof+88],ebx
    mov dword [ss:esp+36],after_wp
    jmp .return
.missing:
    cmp dword [proof+76],2
    jne .virtual
    mov [proof+92],eax
    mov [proof+96],edx
    mov [proof+100],ebx
    mov edi,[proof+40]
    mov eax,[proof+48]
    or al,7
    mov [fs:edi+(ALIAS>>12)*4],eax
    invlpg [fs:ALIAS]
    jmp .return
.virtual:
    mov [proof+104],eax
    mov [proof+108],edx
    mov [proof+112],ebx
    mov edi,[proof+40]
    or dword [fs:edi+(VMDATA>>12)*4],4
    invlpg [fs:VMDATA]
.return:
    popad
    add esp,4
    iretd

gp_handler:
    pushad
    ; Capture the actual monitor entry before replacing its nulled data caches.
    xor eax,eax
    mov ax,ds
    xor edx,edx
    mov dx,es
    or eax,edx
    mov dx,fs
    or eax,edx
    mov dx,gs
    or eax,edx
    mov dx,D0
    mov ds,dx
    mov es,dx
    mov [proof+180],eax
    mov dx,FLAT
    mov fs,dx
    inc dword [proof+120]
    mov eax,[ss:esp+36]
    cmp eax,vm_cli
    je .cli
    cmp eax,vm_in
    je .port
    cmp eax,vm_int
    je .int
    cmp eax,vm_exit
    je .exit
    mov dword [proof+212],0xbad
    jmp kernel_done
.cli:
    mov [proof+124],eax
    lea esi,[esp+44] ; VM flags, ESP, SS, ES, DS, FS, GS
    mov edi,proof+140
    mov ecx,7
    rep movsd
    mov eax,[ss:esp+40]
    mov [proof+168],eax
    xor eax,eax
    mov ax,ss
    mov [proof+172],eax
    lea eax,[esp+32]
    mov [proof+176],eax
    inc dword [ss:esp+36]
    jmp .return
.port:
    mov [proof+128],eax
    mov byte [ss:esp+28],0x5a ; emulate a byte returned to the virtual IN
    add dword [ss:esp+36],2
    jmp .return
.int:
    mov [proof+132],eax
    add dword [ss:esp+36],2
.return:
    popad
    add esp,4
    iretd
.exit:
    mov [proof+136],eax
    mov esp,kernel_stack_end
    or dword [proof+16],4
kernel_done:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov eax,[proof+20]
    or al,1
    mov cr0,eax
    mov eax,[proof+28]
    mov cr3,eax
    jmp word C16:leave_protected

bits 16
vm_entry:
vm_cli:
    cli
vm_in:
    in al,0x61
    movzx eax,al
    mov [es:proof+192],eax
vm_read:
    mov ax,[0]
    movzx eax,ax
    mov [es:proof+116],eax
vm_int:
    int 0x60
vm_exit:
    int 0x61
    ud2

leave_protected:
    mov ax,D16
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov eax,[proof+20]
    mov cr0,eax
    jmp far [real_pointer]
real_return:
    mov ax,cs
    mov ds,ax
    mov ax,[old_ss]
    mov ss,ax
    mov sp,[old_sp]
    mov ax,[old_es]
    mov es,ax
    mov ax,[old_fs]
    mov fs,ax
    mov ax,[old_gs]
    mov gs,ax
    o32 lgdt [old_gdt]
    o32 lidt [old_idt]
    mov eax,cr0
    mov [proof+24],eax
    mov eax,cr3
    mov [proof+32],eax
    mov al,[old_pic]
    out 0x21,al
    mov al,[old_pic+1]
    out 0xa1,al
    cmp dword [proof+16],7
    jne .marked
    cmp dword [proof+76],3
    jne .marked
    cmp dword [proof+120],4
    jne .marked
    cmp dword [proof+212],0
    jne .marked
    mov dword [proof+12],1
.marked:
    mov si,marker
    call debug_string
    mov ebx,[proof+200]
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
    push word [old_flags]
    popf
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

old_sp dw 0
old_ss dw 0
old_es dw 0
old_fs dw 0
old_gs dw 0
old_flags dw 0
old_pic dw 0
old_gdt times 6 db 0
old_idt times 6 db 0
vm_cs dw 0
real_pointer dw real_return,0
marker db 'CPU-PAGING-PROOF=',0
newline db 13,10,0
success db 'CPU-PAGING-PASS',13,10,'$'
failure db 'CPU-PAGING-FAIL',13,10,'$'
symbols db 'CPPGADDR'
    dd wp_fault,after_wp,missing_fault,after_missing,vm_cli,vm_in,vm_read,vm_int,vm_exit
    dd proof,page_space,kernel_stack_end,vm_stack_end,tss
align 4,db 0
proof:
    db 'CPPG0001'
    dd 1
    times 61 dd 0
align 8,db 0
gdt:
    dq 0
gdt_c16: dw 0xffff,0
    db 0,0x9a,0,0
gdt_d16: dw 0xffff,0
    db 0,0x92,0,0
gdt_c0: dw 0xffff,0
    db 0,0x9a,0x40,0
gdt_d0: dw 0xffff,0
    db 0,0x92,0x40,0
gdt_flat: dw 0xffff,0
    db 0,0x92,0xcf,0
gdt_tss: dw tss_end-tss-1,0
    db 0,0x89,0,0
gdt_end:
new_gdt dw gdt_end-gdt-1
    dd 0
new_idt dw idt_end-idt-1
    dd 0
%macro gate 1
    dw %1,C0
    db 0,0x8e
    dw 0
%endmacro
align 8,db 0
idt:
    times 13 dq 0
    gate gp_handler
    gate pf_handler
idt_end:
tss:
    dd 0,kernel_stack_end
    dw D0,0
    times 0x66-12 db 0
    dw 0x68
    times 8193 db 0xff
tss_end:
align 4,db 0
kernel_stack times 512 db 0
kernel_stack_end:
vm_stack times 256 db 0
vm_stack_end:
page_space times (4*4096+4095) db 0
