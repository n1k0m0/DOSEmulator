; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own 486 DOS client: actual debug faults, committed traps, SS deferral and REP restart.
; No resident manager: private PE tables/stacks, optionally private 4-KiB pages.
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
TSS equ 72
%macro patch 2
    mov eax,[proof+80]
    add eax,%2
    mov di,%1
    call patch_base
%endmacro
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
    mov eax,dr0
    mov [proof+128],eax
    mov eax,dr1
    mov [proof+132],eax
    mov eax,dr2
    mov [proof+136],eax
    mov eax,dr3
    mov [proof+140],eax
    mov eax,dr6
    mov [proof+144],eax
    mov eax,dr7
    mov [proof+148],eax
    xor eax,eax
    mov dr7,eax
    mov dr6,eax
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

    str [proof+120]
    sldt [proof+122]
    mov ax,TSS
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
    add eax,8192
    mov [proof+112],eax
    mov edi,eax
    sub edi,[proof+80]
    mov [proof+116],edi
    mov dword [edi],0x89abcdef
    shr eax,12
    mov dword [ebx+eax*4],0    ; genuinely absent private page
    mov eax,[proof+88]
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000
    mov cr0,eax
%endif

    call ss_contracts
    mov eax,cr0
    mov [proof+192],eax
    mov eax,0x2000
    mov dr7,eax
gd_read:
    mov eax,dr0
after_gd:
    mov [proof+200],eax
    mov eax,[proof+80]
    add eax,exec_inc
    mov dr0,eax
    mov eax,[proof+80]
    add eax,watched
    mov dr1,eax
    inc eax
    mov dr2,eax
    mov eax,[proof+80]
    add eax,rep_destination
    mov dr3,eax
    mov eax,0xd5f00059
    mov dr7,eax
    or dword [proof+16],1
    push dword S3|3
    push dword 0x10000+user_end
    push dword 2
    push dword C3|3
    push dword 0x10000+user_entry
    iretd

user_entry:
    mov ax,D3
    mov ds,ax
    mov es,ax
    mov ebx,7
    mov ecx,0x2468
    mov esi,0x10203040
    mov edi,0x50607080
    mov eax,0x13579bdf
exec_inc:
    inc ebx
after_exec:
    mov [proof+204],ebx
data_read:
    mov eax,[watched]
after_read:
    mov [proof+208],eax
data_write:
    mov dword [watched],0x11223344
after_write:
    pushfd
    or dword [ss:esp],0x100
    popfd
tf_inc:
    inc ebx
after_tf:
    mov [proof+212],ebx
    mov ax,S3|3
    pushfd
    or dword [ss:esp],0x100
    popfd
ss_load:
    mov ss,ax
ss_next:
    mov esp,0x10000+user_end
ss_after:
    mov [proof+216],esp
gp_read:
    mov eax,dr0
after_gp:
    mov esi,rep_source
    mov edi,rep_destination
    mov ecx,2
rep_copy:
    rep movsd
after_rep:
    mov [proof+220],ecx
    mov [proof+224],esi
    mov [proof+228],edi
%if PAGING
    mov edi,[proof+116]
page_read:
    mov eax,[edi]
after_page:
    mov [proof+232],eax
%else
page_read:
after_page:
%endif
    or dword [proof+16],2
    int 0x60
    ud2

%macro enter_handler 0
    pushad
    push ds
    mov ax,D0
    mov ds,ax
    mov ebp,esp
%endmacro
%macro leave_handler 1
    pop ds
    popad
%if %1
    add esp,4
%endif
    iretd
%endmacro
; Synchronous GD faults retain the just-loaded SS; the real handler returns to retry MOV DR.
; Consecutive SS loads use the bounded DOSE policy; software must not rely on a portable second shadow.
ss_contracts:
    mov [ss_proof+12],esp
    mov dword [ss_proof+24],S0
    mov dword [ss_proof+28],0xc0de5584
    mov word [idt+8],(0x10100+ss_handler-$$)&0xffff
    mov word [idt+14],(0x10100+ss_handler-$$)>>16
    mov eax,0x2000
    mov dr7,eax
    mov edx,S0
    mov ss,dx
ss_gd_read:
    mov eax,dr0
    mov [ss_proof+16],eax
    mov eax,0x2000
    mov dr7,eax
    mov eax,0x12345678
    push dword S0
    pop ss
ss_gd_write:
    mov dr0,eax
    mov eax,dr0
    mov [ss_proof+20],eax
    xor eax,eax
    mov dr0,eax
    mov dr6,eax
    mov dr7,eax
%macro ss_pair 3
    mov edx,S0
%if %2
    push dword S0
%endif
%if %1
    push dword S0
%endif
    pushfd
    or dword [ss:esp],0x100
    popfd
%if %1
    pop ss
%else
    mov ss,dx
%endif
%if %2
    pop ss
%else
    mov ss,dx
%endif
    mov esp,[ss_proof+12]
%3:
%endmacro
    ss_pair 0,0,ss_after_mm
    ss_pair 0,1,ss_after_mp
    ss_pair 1,0,ss_after_pm
    ss_pair 1,1,ss_after_pp
    mov word [idt+8],(0x10100+db_handler-$$)&0xffff
    mov word [idt+14],(0x10100+db_handler-$$)>>16
    xor eax,eax
    mov dr6,eax
    mov dr7,eax
    ret
; Capture the actual frame and unexecuted EAX, then clear only TF/DR6 before the real IRETD.
ss_handler:
    enter_handler
    mov edi,[ss_proof+8]
    cmp edi,6
    jae unexpected
    shl edi,5
    add edi,ss_proof+32
    mov eax,dr6
    mov [edi],eax
    mov eax,dr7
    mov [edi+4],eax
    mov eax,[ss:ebp+36]
    mov [edi+8],eax
    mov eax,[ss:ebp+40]
    mov [edi+12],eax
    mov eax,[ss:ebp+44]
    mov [edi+16],eax
    mov eax,[ss:ebp+32]
    mov [edi+20],eax
    lea eax,[ebp+48]
    mov [edi+24],eax
    xor eax,eax
    mov ax,ss
    mov [edi+28],eax
    inc dword [ss_proof+8]
    xor eax,eax
    mov dr6,eax
    and dword [ss:ebp+44],~0x100
    leave_handler 0

db_handler:
    enter_handler
    mov esi,[proof+100]
    cmp esi,8
    jae unexpected
    imul edi,esi,48
    add edi,proof+256
    mov eax,dr6
    mov [edi],eax
    mov eax,dr7
    mov [edi+4],eax
    mov [proof+180],eax
    and eax,0xffffff00       ; debugger reads the watched value with comparators disabled
    mov dr7,eax
    mov eax,[ss:ebp+36]
    mov [edi+8],eax
    mov eax,[ss:ebp+40]
    mov [edi+12],eax
    mov eax,[ss:ebp+44]
    mov [edi+16],eax
    xor eax,eax
    test byte [ss:ebp+40],3
    jz .same
    mov eax,[ss:ebp+48]
.same:
    mov [edi+20],eax
    mov eax,[ss:ebp+32]
    mov [edi+24],eax
    mov eax,[ss:ebp+20]
    mov [edi+28],eax
    mov eax,[ss:ebp+28]
    mov [edi+32],eax
    mov eax,[ss:ebp+8]
    mov [edi+36],eax
    mov eax,[ss:ebp+4]
    mov [edi+40],eax
    mov eax,[watched]
    mov [edi+44],eax
    inc dword [proof+100]
    xor eax,eax
    mov dr6,eax
    cmp esi,1
    jbe .fault
    cmp esi,4
    je .single
    cmp esi,5
    je .ss
    cmp esi,6
    je .rep
    jmp .done
.fault:
    or dword [ss:ebp+44],0x10000 ; 486 #DB fault doesn't synthesize RF
    jmp .done
.single:
    mov eax,[proof+80]
    add eax,ss_next
    mov dr0,eax
.ss:
    and dword [ss:ebp+44],~0x100
    jmp .done
.rep:
%if PAGING
    mov eax,[proof+112]
    mov dr3,eax
    mov eax,0xf5f00059
%else
    mov eax,0xd5f00019
%endif
    mov [proof+180],eax
.done:
    mov eax,[proof+180]
    mov dr7,eax
    leave_handler 0
gp_handler:
    enter_handler
    cmp dword [proof+104],0
    jne unexpected
    mov edi,proof+640
    mov esi,36
    mov ecx,6
.copy:
    mov eax,[ss:ebp+esi]
    mov [edi],eax
    add edi,4
    add esi,4
    loop .copy
    mov dword [ss:ebp+40],0x10000+after_gp
    inc dword [proof+104]
    leave_handler 1
pf_handler:
    enter_handler
    cmp dword [proof+108],0
    jne unexpected
    mov edi,proof+672
    mov esi,36
    mov ecx,6
.copy:
    mov eax,[ss:ebp+esi]
    mov [edi],eax
    add edi,4
    add esi,4
    loop .copy
    mov eax,cr2
    mov [proof+696],eax
%if PAGING
    mov eax,[proof+112]
    mov ecx,eax
    shr ecx,12
    or eax,7
    mov edi,[proof+92]
    sub edi,[proof+80]
    mov [edi+ecx*4],eax
    mov esi,[proof+116]
    invlpg [esi]
%else
    jmp unexpected
%endif
    inc dword [proof+108]
    leave_handler 1
cleanup32:
    mov ax,D0
    mov ds,ax
    mov es,ax
    or dword [proof+16],4
    str [proof+124]
    sldt [proof+126]
    xor eax,eax
    mov dr7,eax
    mov eax,[proof+128]
    mov dr0,eax
    mov eax,[proof+132]
    mov dr1,eax
    mov eax,[proof+136]
    mov dr2,eax
    mov eax,[proof+140]
    mov dr3,eax
    mov eax,[proof+144]
    mov dr6,eax
    mov eax,[proof+148]
    mov dr7,eax
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

    mov eax,dr0
    mov [proof+152],eax
    mov eax,dr1
    mov [proof+156],eax
    mov eax,dr2
    mov [proof+160],eax
    mov eax,dr3
    mov [proof+164],eax
    mov eax,dr6
    mov [proof+168],eax
    mov eax,dr7
    mov [proof+172],eax
    or dword [proof+16],8
    cmp dword [proof+16],15
    jne .report
    cmp dword [proof+100],7+PAGING
    jne .report
    cmp dword [proof+104],1
    jne .report
    cmp dword [proof+108],PAGING
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
marker db 'CPU-DEBUG-PROOF=',0
newline db 13,10,0
success db 'CPU-DEBUG-PASS',13,10,'$'
failure db 'CPU-DEBUG-FAIL',13,10,'$'
symbols:
    db 'CPDBADDR'
    dd (symbol_end-symbol_data)/4
symbol_data:
    dd start,original_sp,proof,gdt,tss,kernel_end,user_end
    dd gd_read,after_gd,exec_inc,after_exec,data_read,after_read,data_write,after_write
    dd tf_inc,after_tf,ss_load,ss_next,ss_after,gp_read,after_gp,rep_copy,after_rep
    dd page_read,after_page,watched,rep_source,rep_destination,paging_storage,program_end
symbol_end:
    db 'SSGDADDR'
    dd ss_proof,ss_gd_read,ss_gd_write,ss_after_mm,ss_after_mp,ss_after_pm,ss_after_pp,initial_end
align 4,db 0
proof:
    db 'CPDB0001'
    dd 1
    times 253 dd 0
align 4,db 0
ss_proof: db 'SSGD0001'
    times 216 db 0
watched dd 0x89abcdef
    dd 0xc0de0101
rep_source dd 0x11112222,0x33334444
rep_destination times 2 dd 0
    dd 0xc0de0102
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
    dq 0
    irq db_handler,0x8e
    times 4 dq 0
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    dq 0
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq gp_handler,0x8e
    irq pf_handler,0x8e
    dq 0
    irq unexpected,0x8e
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
program_end:
