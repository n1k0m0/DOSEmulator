; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS program. Actual hardware tasks, no host BIOS/DOS emulation shortcuts.
; All tables, TSSs, stacks and optional page tables belong to this COM block.
; The private bootstrap TR remains loaded until reset (LTR cannot unload TR).
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
MAIN equ 64
CHILD equ 72
WORDTASK equ 80
JUMPTASK equ 88
BADTASK equ 96
HANDLER equ 104
TASKGATE equ 112
S3 equ 120
LDT equ 128
ABSENT equ 136

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
    patch gdt+MAIN,tss_main
    patch gdt+CHILD,tss_child
    patch gdt+WORDTASK,tss_word
    patch gdt+JUMPTASK,tss_jump
    patch gdt+BADTASK,tss_bad
    patch gdt+HANDLER,tss_handler
    patch gdt+ABSENT,tss_absent
    patch gdt+LDT,ldt
    mov eax,[proof+80]
    add eax,gdt
    mov [private_gdt+2],eax
    mov eax,[proof+80]
    add eax,idt
    mov [private_idt+2],eax
    mov eax,[proof+80]
    add eax,tss_main
    mov [proof+84],eax
    mov dword [proof+96],PAGING
    o32 lgdt [private_gdt]
    o32 lidt [private_idt]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword C0:0x10000+main_entry
; Patches the explicit descriptor base bytes for this probe's allocated guest segment.
patch_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret

bits 32
main_entry:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ax,S0
    mov ss,ax
    mov esp,0x10000+main_stack_end
    push dword 2
    popfd
    str [proof+100]
    sldt [proof+102]
    mov ax,MAIN
    ltr ax
%if PAGING
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
    add ebx,4096
    xor ecx,ecx
.pte:
    mov eax,ecx
    shl eax,12
    or eax,7
    mov [ebx+ecx*4],eax
    inc ecx
    cmp ecx,1024
    jb .pte
    mov eax,[proof+88]
    mov [tss_main+0x1c],eax
    mov [tss_child+0x1c],eax
    mov [tss_jump+0x1c],eax
    mov [tss_bad+0x1c],eax
    mov [tss_handler+0x1c],eax
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000
    mov cr0,eax
%endif
    push dword 2
    popfd
    mov eax,0x10203040
    mov ebx,0x50607080
    ; An operand-size-16 CALL still saves our full >64-KiB EIP in a 32-bit TSS.
    call word CHILD:0xbeef
after_child:
    mov [proof+360],esp
    str [proof+364]
    pushfd
    pop dword [proof+368]
    clts
    smsw eax
    mov [proof+372],eax
busy_call:
    call MAIN:0xdeadbeef
after_busy:
np_call:
    call ABSENT:0xdeadbeef
after_np:
    call BADTASK:0xdeadbeef
after_bad:
    jmp JUMPTASK:0xdeadbeef
after_jump:
    str [proof+376]
    sldt [proof+380]
    jmp word C16:cleanup16

child_entry:
    mov [proof+128],eax
    mov [proof+132],ecx
    mov [proof+136],edx
    mov [proof+140],ebx
    mov [proof+144],esp
    mov [proof+148],ebp
    mov [proof+152],esi
    mov [proof+156],edi
    pushfd
    pop dword [proof+160]
    smsw eax
    mov [proof+164],eax
    mov [proof+168],cs
    mov [proof+170],ss
    str [proof+172]
    sldt [proof+174]
    mov [proof+176],es
    mov [proof+178],ds
    mov [proof+180],fs
    mov [proof+182],gs
    or dword [proof+16],1
    call far [indirect_ldt]
after_local:
    call word TASKGATE|3:0xbeef
after_gdt:
    mov [proof+184],esp
    sldt [proof+188]
    iretd
child_after_iret:
    ud2

jump_entry:
    str [proof+344]
    pushfd
    pop dword [proof+348]
    movzx eax,word [tss_jump]
    mov [proof+352],eax
    movzx eax,byte [gdt+MAIN+5]
    mov [proof+356],eax
    or dword [proof+16],32
    jmp MAIN:0xdeadbeef
jump_after_return:
    ud2

bad_entry:
    inc dword [proof+320]
    str [proof+324]
    mov [proof+328],ds
    pushfd
    pop dword [proof+332]
    mov [proof+336],ebx
    or dword [proof+16],16
    iretd
bad_after_iret:
    ud2

task_fault_handler:
    str [proof+288]
    mov eax,[ss:esp]
    mov [proof+292],eax
    movzx eax,word [tss_handler]
    mov [proof+296],eax
    mov eax,[tss_bad+0x20]
    mov [proof+300],eax
    mov eax,[tss_bad+0x34]
    mov [proof+304],eax
    movzx eax,word [tss_bad+0x54]
    mov [proof+308],eax
    pushfd
    pop dword [proof+312]
    mov eax,cr0
    mov [proof+316],eax
    ; Repair the ADOPTED task's saved DS, then real task-IRET reloads its state.
    mov word [tss_bad+0x54],D0
    or dword [proof+16],8
    add esp,4
    iretd
handler_after_iret:
    ud2

gp_handler:
    mov eax,[ss:esp+4]
    mov [proof+256],eax
    mov eax,[ss:esp+8]
    mov [proof+260],eax
    mov eax,[ss:esp+12]
    mov [proof+264],eax
    mov eax,[ss:esp]
    mov [proof+268],eax
    mov dword [ss:esp+4],0x10000+after_busy
    or dword [proof+16],2
    add esp,4
    iretd
np_handler:
    mov eax,[ss:esp+4]
    mov [proof+272],eax
    mov eax,[ss:esp+8]
    mov [proof+276],eax
    mov eax,[ss:esp+12]
    mov [proof+280],eax
    mov eax,[ss:esp]
    mov [proof+284],eax
    mov dword [ss:esp+4],0x10000+after_np
    or dword [proof+16],4
    add esp,4
    iretd
unexpected:
    inc dword [proof+20]
    cli
    hlt
    jmp unexpected

bits 16
word_entry:
    pushfd
    pop dword [word_flags]
    mov di,proof+192
    cmp dword [proof+340],0
    je .first
    add di,32
.first:
    mov [di],eax
    mov [di+4],ecx
    mov [di+8],esp
    mov eax,[word_flags]
    mov [di+12],eax
    smsw eax
    mov [di+16],eax
    mov [di+20],cs
    mov [di+22],ss
    str [di+24]
    sldt [di+26]
    mov ax,[tss_word]
    mov [di+28],ax
    inc dword [proof+340]
    iretw
word_after_iret:
    jmp word_entry

cleanup16:
    mov ax,D16
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov sp,main_stack_end
    o32 lidt [original_idt]
    mov eax,[proof+24]
    mov cr0,eax
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
    or dword [proof+16],64
    cmp dword [proof+16],127
    jne .report
    cmp dword [proof+340],2
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
word_flags dd 0
indirect_ldt dd 0xdeadbeef
    dw 7
marker db 'CPU-TASK-PROOF=',0
newline db 13,10,0
success db 'CPU-TASK-PASS',13,10,'$'
failure db 'CPU-TASK-FAIL',13,10,'$'
symbols:
    db 'CPTSADDR'
    dd start,original_sp,proof,gdt,ldt,tss_main,tss_child,tss_word,tss_jump,tss_bad,tss_handler
    dd main_stack_end,child_stack_end,word_stack_end,jump_stack_end,bad_stack_end,handler_stack_end
    dd after_child,after_local,after_gdt,busy_call,after_busy,np_call,after_np,after_bad,bad_entry
    dd after_jump,child_entry,word_after_iret,jump_after_return,paging_storage,program_end
    dd child_after_iret,bad_after_iret,handler_after_iret
align 4,db 0
proof:
    db 'CPTS0001'
    dd 1
    times 125 dd 0
%macro descriptor 3
    dw %1,0
    db 0,%2,%3,0
%endmacro
%macro tg 2
    dw 0xbeef,%1
    db 31,%2
    dw 0xdead
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
    descriptor 0x67,0x89,0
    descriptor 0x67,0x89,0
    descriptor 0x2b,0x81,0
    descriptor 0x67,0x89,0
    descriptor 0x67,0x89,0
    descriptor 0x67,0x89,0
    tg WORDTASK,0xe5
    descriptor 0xffff,0xf2,0x41
    descriptor 7,0x82,0
    descriptor 0x67,0x09,0
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
align 8,db 0
ldt:
    tg WORDTASK,0xe5
align 4,db 0
%macro tss32 6
    dd 0                    ; backlink
    times 6 dd 0            ; ESP0/SS0 .. ESP2/SS2
    dd 0                    ; CR3 is static and patched only for paging variant
    dd 0x10000+%1,2
    dd 0x11112222,0x33334444,0x55556666,%2,0x10000+%3,0x77778888,0x9999aaaa,0xbbbbcccc
    dw %4,0,%5,0
    dw S0,0,%6,0,0,0,0,0
    dw 0,0,0,104
%endmacro
tss_main:
    tss32 unexpected,0x50607080,main_stack_end,D0,C0,D0
tss_child:
    dd 0
    times 6 dd 0
    dd 0
    dd 0x10000+child_entry,2
    dd 0x11112222,0x33334444,0x55556666,0x12345678,0x10000+child_stack_end,0x77778888,0x9999aaaa,0xbbbbcccc
    dw D3,0,C3|3,0,S3|3,0,D3,0,0,0,0,0,LDT,0,0,104
tss_word:
    dw 0,0,0,0,0,0,0
    dw word_entry,2
    dw 0x0123,0x0456,0x0789,0x0abc,word_stack_end,0x0def,0x0321,0x0654
    dw D16,C16,D16,D16,0
tss_jump:
    tss32 jump_entry,0x87654321,jump_stack_end,D0,C0,D0
tss_bad:
    tss32 bad_entry,0xbadc0033,bad_stack_end,D0,C0,0xfff8
    ; Replace DS in the saved TSS with a selector beyond our GDT.
    ; Kept as actual initial program data for the independent oracle.
tss_handler:
    tss32 task_fault_handler,0x13572468,handler_stack_end,D0,C0,D0
tss_absent times 104 db 0
%macro irq 1
    dw (%1-$$+0x10100)&0xffff,C0
    db 0,0x8e
    dw (%1-$$+0x10100)>>16
%endmacro
align 8,db 0
idt:
    times 8 dq 0
    irq unexpected
    dq 0
    tg HANDLER,0x85
    irq np_handler
    irq unexpected
    irq gp_handler
    irq unexpected
idt_end:
align 16,db 0
%macro stack 1
    dd 0xc0de0000+%1
    times 384 db 0xa5
%endmacro
    stack 1
main_stack_end: dd 0xcafe0001
    stack 2
child_stack_end: dd 0xcafe0002
    stack 3
word_stack_end: dd 0xcafe0003
    stack 4
jump_stack_end: dd 0xcafe0004
    stack 5
bad_stack_end: dd 0xcafe0005
    stack 6
handler_stack_end: dd 0xcafe0006
align 16,db 0
paging_storage:
%if PAGING
    times 12288 db 0
%endif
program_end:
