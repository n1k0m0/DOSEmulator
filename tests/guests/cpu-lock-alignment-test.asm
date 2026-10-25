; Own 486 DOS client: legal/illegal LOCK, CMPXCHG/XADD, and real #AC retries.
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
    or eax,0x40009             ; PE, AM, TS; invalid LOCK FNINIT must give UD first
    mov cr0,eax
    jmp dword C0:0x10000+kernel_entry
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
    mov dword [edi+1],0x89abcdef
    shr eax,12
    mov dword [ebx+eax*4],0    ; genuinely absent private page
    mov eax,[proof+88]
    mov cr3,eax
    mov eax,cr0
    or eax,0x80010000
    mov cr0,eax
%endif
    mov eax,cr0
    mov [proof+192],eax
    push dword S3|3
    push dword 0x10000+user_end
    push dword 0x40002
    push dword C3|3
    push dword 0x10000+user_entry
    iretd

%macro enable_ac 0
    push dword 0x40002
    popfd
%endmacro
user_entry:
    mov ax,D3
    mov ds,ax
    mov es,ax
    mov [proof+248],ss
    mov [proof+252],esp
    mov ax,cs
    and eax,3
    mov [proof+180],eax
    enable_ac
    pushfd
    pop dword [proof+184]
    smsw eax
    mov [proof+188],eax
    lock add dword [semaphore],1
    mov eax,[semaphore]
    mov [proof+128],eax
    mov ecx,2
    lock xadd [semaphore],ecx
    mov [proof+132],ecx
    mov eax,[semaphore]
    mov [proof+136],eax
    mov ebx,9
cmp_equal:
    ; NASM classifies B0/B1 as PENT; Intel's 486 PRM specifies these opcodes.
    db 0xf0,0x0f,0xb1,0x1d
    dd semaphore
    mov [proof+140],eax
    pushfd
    pop dword [proof+144]
    mov eax,[semaphore]
    mov [proof+148],eax
    mov eax,7
    mov ebx,11
cmp_unequal:
    db 0xf0,0x0f,0xb1,0x1d
    dd semaphore
    mov [proof+152],eax
    pushfd
    pop dword [proof+156]
    mov eax,[semaphore]
    mov [proof+160],eax
    mov edx,12
    xchg [semaphore],edx     ; implicit memory lock
    mov [proof+164],edx
    mov eax,[semaphore]
    mov [proof+168],eax
    lock bts dword [bitword],3
    pushfd
    pop dword [proof+620]
    mov eax,[bitword]
    mov [proof+616],eax
    lock btc dword [bitword],3
    pushfd
    pop dword [proof+628]
    mov eax,[bitword]
    mov [proof+624],eax
    lock btr dword [bitword],3
    pushfd
    pop dword [proof+636]
    mov eax,[bitword]
    mov [proof+632],eax
    mov eax,[bitword]
    mov [proof+172],eax
    lock inc dword [semaphore]
    mov eax,[semaphore]
    mov [proof+176],eax
    or dword [proof+16],1
    enable_ac
    mov eax,0x11223344
    mov ecx,0x55667788
ud_reg:
    db 0xf0
    add eax,ecx
after_ud_reg:
ud_cmp:
    db 0xf0
    cmp [unaligned+1],ecx    ; illegal LOCK beats operand AC
after_ud_cmp:
    xor eax,eax
    mov ds,ax
ud_mov:
    db 0xf0
    mov [unaligned+1],ecx    ; illegal LOCK beats null-DS GP
after_ud_mov:
    mov ax,D3
    mov ds,ax
ud_fpu:
    db 0xf0
    fninit                  ; illegal LOCK beats TS/NM
after_ud_fpu:
    or dword [proof+16],2
    enable_ac
    mov eax,0x2468ace0
ac_load:
    mov eax,[unaligned+1]
after_ac_load:
    mov [proof+196],eax
    enable_ac
    mov ecx,9
ac_xadd:
    lock xadd [unaligned+1],ecx
after_ac_xadd:
    mov [proof+200],ecx
    mov eax,[unaligned+1]
    mov [proof+204],eax
    enable_ac
    mov esi,rep_source
    mov edi,rep_destination+1
    mov ecx,2
ac_rep:
    rep movsd
after_ac_rep:
    mov [proof+208],ecx
    mov [proof+212],esi
    mov [proof+216],edi
    enable_ac
    sgdt [descriptor_good]  ; correct 486 user address is 2 MOD 4
ac_sgdt:
    sgdt [descriptor_bad]   ; 0 MOD 4: base dword at +2 is unaligned
after_ac_sgdt:
    enable_ac
    mov eax,0x13579bdf
    sub esp,7               ; misalign inside the owned stack, preserving both guards
    mov [proof+220],esp
ac_stack:
    push eax
after_ac_stack:
    pop eax
    add esp,7
    mov [proof+224],esp
%if PAGING
    enable_ac
    mov esi,[proof+116]
ac_page:
    mov eax,[esi+1]          ; AC first, then PF after handler clears AC, then retry
after_ac_page:
    mov [proof+236],eax
%else
ac_page:
after_ac_page:
%endif
    or dword [proof+16],4
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
ud_handler:
    enter_handler
    mov edi,[proof+104]
    imul edi,24
    add edi,proof+256
    mov eax,[ss:ebp+36]
    mov [edi],eax
    mov eax,[ss:ebp+40]
    mov [edi+4],eax
    mov eax,[ss:ebp+44]
    mov [edi+8],eax
    movzx eax,word [ss:ebp]
    mov [edi+12],eax
    mov eax,[ss:ebp+32]
    mov [edi+16],eax
    mov eax,[ss:ebp+28]
    mov [edi+20],eax
    mov esi,[proof+104]
    cmp esi,4
    jae unexpected
    mov eax,[ud_returns+esi*4]
    mov [ss:ebp+36],eax
    inc dword [proof+104]
    leave_handler 0
ac_handler:
    enter_handler
    mov edi,[proof+100]
    shl edi,5
    add edi,proof+384
    mov eax,[ss:ebp+36]
    mov [edi],eax
    mov eax,[ss:ebp+40]
    mov [edi+4],eax
    mov eax,[ss:ebp+44]
    mov [edi+8],eax
    mov eax,[ss:ebp+48]
    mov [edi+12],eax
    mov eax,[ss:ebp+52]
    mov [edi+16],eax
    mov eax,[ss:ebp+56]
    mov [edi+20],eax
    mov eax,[ss:ebp+32]
    mov [edi+24],eax
    mov eax,[ss:ebp+28]
    mov [edi+28],eax
    cmp dword [proof+100],3
    jne .clear
    mov eax,[descriptor_bad]
    mov [proof+228],eax
    movzx eax,word [descriptor_bad+4]
    mov [proof+232],eax
.clear:
    and dword [ss:ebp+48],~0x40000
    inc dword [proof+100]
    leave_handler 1
pf_handler:
    enter_handler
    mov esi,36
    mov edi,proof+576
    mov ecx,6
.frame:
    mov eax,[ss:ebp+esi]
    mov [edi],eax
    add edi,4
    add esi,4
    loop .frame
    mov eax,cr2
    mov [proof+240],eax
    mov [proof+600],eax
%if PAGING
    mov eax,[proof+112]
    mov ecx,eax
    shr ecx,12
    or eax,7
    mov [proof+604],eax
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
    mov eax,cs
    and eax,3
    mov [proof+244],eax
    str [proof+124]
    sldt [proof+126]
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
    or dword [proof+16],8
    cmp dword [proof+16],15
    jne .report
    cmp dword [proof+100],5+PAGING
    jne .report
    cmp dword [proof+104],4
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
marker db 'CPU-LOCK-PROOF=',0
newline db 13,10,0
success db 'CPU-LOCK-PASS',13,10,'$'
failure db 'CPU-LOCK-FAIL',13,10,'$'
symbols:
    db 'CPLAADDR'
    dd start,original_sp,proof,gdt,tss,kernel_end,user_end
    dd ud_reg,after_ud_reg,ud_cmp,after_ud_cmp,ud_mov,after_ud_mov,ud_fpu,after_ud_fpu
    dd ac_load,after_ac_load,ac_xadd,after_ac_xadd,ac_rep,after_ac_rep,ac_sgdt,after_ac_sgdt,ac_stack,after_ac_stack
    dd ac_page,after_ac_page,unaligned,rep_source,rep_destination,descriptor_good,descriptor_bad
    dd paging_storage,program_end
    dd cmp_equal,cmp_unequal
align 4,db 0
proof:
    db 'CPLA0001'
    dd 1
    times 253 dd 0
align 4,db 0
ud_returns dd 0x10000+after_ud_reg,0x10000+after_ud_cmp,0x10000+after_ud_mov,0x10000+after_ud_fpu
semaphore dd 0
bitword dd 0
unaligned db 0xa5
    dd 0x12345678
    db 0x5a
align 4,db 0
rep_source dd 0x11112222,0x33334444
rep_destination db 0xa5
    times 8 db 0
    db 0x5a
align 4,db 0
descriptor_bad times 8 db 0xa5
    dw 0xa5a5
descriptor_good times 8 db 0xa5
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
    irq ud_handler,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    dq 0
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq unexpected,0x8e
    irq pf_handler,0x8e
    dq 0
    irq unexpected,0x8e
    irq ac_handler,0x8e
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
