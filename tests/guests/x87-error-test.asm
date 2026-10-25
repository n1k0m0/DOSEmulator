; Own 486 numeric-error client: blocked external waits, AT F0/INT02 chain,
; native MF/IRETD, and (optionally) a later repaired operand PF.
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
TSS equ 48
%macro patch 2
    mov eax,[proof+80]
    add eax,%2
    mov di,%1
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
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
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [proof+80],eax
    smsw ax
    mov [proof+100],ax
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
    o32 fnsave [original_fpu]
%if PROTECTED
    mov eax,cr0
    mov [proof+24],eax
    mov eax,cr3
    mov [proof+32],eax
    o32 sgdt [original_gdt]
    o32 sidt [original_idt]
    patch gdt+C16,0
    patch gdt+D16,0
    patch gdt+C0,-0x10000
    patch gdt+D0,0
    patch gdt+S0,-0x10000
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
    o32 lgdt [private_gdt]
    o32 lidt [private_idt]
    mov eax,cr0
    and eax,~0x20
    or eax,1
    mov cr0,eax
    jmp dword C0:0x10000+kernel_entry
bits 32
kernel_entry:
    mov ax,D0
    mov ds,ax
    mov es,ax
    mov ax,S0
    mov ss,ax
    mov esp,0x10000+kernel_end
    push dword 2
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
    mov edi,[proof+88]
    sub edi,[proof+80]
    mov eax,[proof+88]
    add eax,4096
    mov [proof+92],eax
    or eax,7
    mov [edi],eax
    add edi,4096
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
%define data
%define bpreg ebp
%define spreg esp
%define frame_ip 4
%define frame_cs 8
%define frame_flags 12
%define ireturn iretd
%else
    xor ax,ax
    mov es,ax
    mov eax,[es:8*4]
    mov [old_timer],eax
    mov eax,[es:0x75*4]
    mov [old_numeric],eax
    mov eax,[es:2*4]
    mov [old_int2],eax
    ; Read through the guest memory bus: ROM is intentionally absent from
    ; private raw-DRAM dumps. Capture this own BIOS entry for the oracle.
    mov si,[old_numeric]
    mov ax,[old_numeric+2]
    mov es,ax
    mov di,proof+176
    mov cx,15
.bios_bytes:
    mov al,[es:si]
    mov [di],al
    inc si
    inc di
    loop .bios_bytes
    xor ax,ax
    mov es,ax
    mov word [es:8*4],timer_entry
    mov [es:8*4+2],cs
    mov word [es:0x75*4],numeric_entry
    mov [es:0x75*4+2],cs
    mov word [es:2*4],int2_entry
    mov [es:2*4+2],cs
    mov ax,ds
    mov es,ax
%define data cs:
%define bpreg bp
%define spreg sp
%define frame_ip 2
%define frame_cs 4
%define frame_flags 6
%define ireturn iret
%endif
body_start:
    mov byte [phase],1
body_phase:
    cli
    fninit
    mov al,0xff
    out 0x21,al
    out 0xa1,al
    cmp byte [phase],1
    je body_invalid
%if PROTECTED
    cmp byte [phase],4
    je body_invalid
%endif
    fldcw [zero_control]
    fldz
    fld1
divide:
    fdiv st0,st1
    jmp body_armed
body_invalid:
    fldcw [invalid_control]
empty_add:
    fadd st0,st0
body_armed:
    call row_pointer
    mov al,[phase]
    cmp al,1
    je body_external_wait
    cmp al,2
    je body_external_nop
    cmp al,3
    je body_external_memory
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    cmp byte [phase],4
    je body_native_wait
    cmp byte [phase],5
    je body_native_nop
    call prepare_operand
    pushf
    pop word [esi+50]
native_memory:
    fadd dword [edi]
    jmp body_resumed
body_native_wait:
    pushf
    pop word [esi+50]
native_wait:
    fwait
    jmp body_resumed
body_native_nop:
    pushf
    pop word [esi+50]
native_nop:
    db 0x66
    fnop
    jmp body_resumed
%endif
body_external_wait:
    call enable_timer
    pushf
    pop word [esi+50]
    sti
external_wait:
    fwait
    jmp body_resumed
body_external_nop:
    call enable_timer
    pushf
    pop word [esi+50]
    sti
external_nop:
    db 0x66
    fnop
    jmp body_resumed
body_external_memory:
    call prepare_operand
    call enable_timer
    pushf
    pop word [esi+50]
    sti
external_memory:
%if PROTECTED
    fadd dword [edi]
%else
    fadd dword [operand]
%endif
body_resumed:
    cli
    call row_pointer
    fnstsw [esi+36]
    fnstcw [esi+38]
    cmp byte [phase],3
    je body_store
%if PROTECTED
    cmp byte [phase],6
    jne body_next
%else
    jmp body_next
%endif
body_store:
    fstp tword [esi+40]
body_next:
    inc byte [phase]
%if PROTECTED
    cmp byte [phase],7
%else
    cmp byte [phase],4
%endif
    jb body_phase
%if PROTECTED
    ; FNCLEX/F0 do not erase an already latched edge in the PIC IRR.
    ; Three masked native errors coalesce into one actual IRQ13 to drain.
    mov al,0xfb
    out 0x21,al
    mov al,0xdf
    out 0xa1,al
    sti
    nop
body_drain:
    cmp dword [proof+148],1
    je body_drained
    hlt
    jmp body_drain
body_drained:
    cli
%endif
    ; Confirm both IRQ13 IRR and ISR are clear after the actual delivery/EOIs.
    mov al,0x0a
    out 0x20,al
    out 0xa0,al
    in al,0x20
    mov [proof+164],al
    in al,0xa0
    mov [proof+165],al
    mov al,0x0b
    out 0x20,al
    out 0xa0,al
    in al,0x20
    mov [proof+166],al
    in al,0xa0
    mov [proof+167],al
    mov al,0x0a
    out 0x20,al
    out 0xa0,al
    mov dword [proof+8],1
    jmp cleanup

enable_timer:
    mov al,0xfa
    out 0x21,al
    mov al,0xff
    out 0xa1,al
    ; Return with IF=0. STI must be directly before the tested instruction.
    ret
prepare_operand:
%if PROTECTED
    mov edi,0x300000
    sub edi,[proof+80]
    mov dword [edi],0x40000000
%if PAGING
    mov ebx,[proof+92]
    sub ebx,[proof+80]
    mov dword [ebx+768*4],0
    invlpg [edi]
%endif
%endif
    ret
row_pointer:
    movzx esi,byte [data phase]
    dec esi
    shl esi,6
    add esi,rows
    ret
%macro frame_value 2
%if PROTECTED
    mov eax,[bpreg+%2]
%else
    movzx eax,word [bpreg+%2]
%endif
    mov [data esi+%1],eax
%endmacro
timer_entry:
    push bpreg
    mov bpreg,spreg
    pushad
    call row_pointer
    frame_value 0,frame_ip
    frame_value 4,frame_cs
    frame_value 8,frame_flags
    inc dword [data esi+52]
    inc dword [data proof+128]
    mov al,0xfb
    out 0x21,al
    mov al,0xdf
    out 0xa1,al
%if PROTECTED
    mov al,0x20
    out 0x20,al
%endif
    popad
    pop bpreg
%if PROTECTED
    iretd
%else
    jmp far [cs:old_timer]
%endif
numeric_entry:
    push bpreg
    mov bpreg,spreg
    pushad
%if PROTECTED
    cmp byte [phase],7
    je .drain
%endif
    call row_pointer
    frame_value 12,frame_ip
    frame_value 16,frame_cs
    frame_value 20,frame_flags
    fnstsw [data esi+24]
    fnstcw [data esi+26]
    inc dword [data esi+56]
    inc dword [data proof+132]
%if PROTECTED
    xor al,al
    out 0xf0,al
    call numeric_owner
    jmp .eoi
.drain:
    inc dword [proof+148]
    xor al,al
    out 0xf0,al
.eoi:
    mov al,0x20
    out 0xa0,al
    out 0x20,al
    popad
    pop bpreg
    iretd
%else
    popad
    pop bpreg
    jmp far [cs:old_numeric]
int2_entry:
    pushad
    call row_pointer
    inc dword [cs:proof+136]
    call numeric_owner
    popad
    iret
%endif
numeric_owner:
    fnstsw [data esi+28]
    fnstcw [data esi+30]
    ; This is a waiting ESC while the exception remains pending. Only the
    ; actual OUT F0 by the IRQ75 BIOS/platform handler lets it complete.
    fnop
    fnstsw [data esi+32]
    fnclex
    fnstsw [data esi+34]
    inc dword [data esi+60]
    ret
%if PROTECTED
mf_entry:
    push ebp
    mov ebp,esp
    pushad
    call row_pointer
    frame_value 12,frame_ip
    frame_value 16,frame_cs
    frame_value 20,frame_flags
    fnstsw [esi+24]
    fnstcw [esi+26]
    fnstsw [esi+28]
    fnstcw [esi+30]
    inc dword [esi+56]
    inc dword [esi+60]
    inc dword [proof+140]
    fnclex
    fnstsw [esi+34]
    fnop
    fnstsw [esi+32]
    xor al,al
    out 0xf0,al
    popad
    pop ebp
    iretd
pf_entry:
    push ebp
    mov ebp,esp
    pushad
    inc dword [proof+144]
    mov eax,[ebp+4]
    mov [proof+152],eax
    mov eax,[ebp+8]
    mov [proof+156],eax
    mov eax,cr2
    mov [proof+160],eax
    mov eax,[proof+92]
    sub eax,[proof+80]
    mov dword [eax+768*4],0x300007
    invlpg [edi]
    popad
    pop ebp
    add esp,4
    iretd
unexpected:
    inc dword [proof+20]
    cli
    hlt
    jmp unexpected
%endif
cleanup:
    cli
    fninit
%if PROTECTED
    str [proof+124]
    jmp word C16:cleanup16
bits 16
cleanup16:
    mov ax,D16
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov sp,kernel_end
    o32 lidt [original_idt]
    mov eax,[proof+24]
    mov cr0,eax
    jmp far [cs:real_pointer]
%endif
real_return:
%if PROTECTED
    mov eax,[cs:proof+32]
    mov cr3,eax
    o32 lgdt [cs:original_gdt]
    mov eax,cr0
    mov [cs:proof+28],eax
    mov eax,cr3
    mov [cs:proof+36],eax
%else
    xor ax,ax
    mov es,ax
    mov eax,[old_timer]
    mov [es:8*4],eax
    mov eax,[old_numeric]
    mov [es:0x75*4],eax
    mov eax,[old_int2]
    mov [es:2*4],eax
%endif
    mov ax,[cs:original_ss]
    mov ss,ax
    mov sp,[cs:original_sp]
    push cs
    pop ds
    push cs
    pop es
    o32 frstor [original_fpu]
    o32 fnsave [restored_fpu]
    o32 frstor [restored_fpu]
    mov si,original_fpu
    mov di,restored_fpu
    mov cx,108
    cld
    repe cmpsb
    jne .report
    mov dword [proof+12],1
.report:
    mov al,[pic_master]
    out 0x21,al
    mov al,[pic_slave]
    out 0xa1,al
    xor eax,eax
    in al,0xa1
    shl eax,8
    in al,0x21
    mov [proof+76],eax
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
    mov ax,[original_fs]
    mov fs,ax
    mov ax,[original_gs]
    mov gs,ax
    push dword [original_flags]
    popfd
    mov dx,success
    cmp dword [proof+8],1
    jne .fail
    cmp dword [proof+12],1
    je .print
.fail:
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
code_end:
original_sp dw 0
original_ss dw 0
original_es dw 0
original_fs dw 0
original_gs dw 0
original_flags dd 0
original_gdt times 6 db 0
original_idt times 6 db 0
pic_master db 0
pic_slave db 0
real_pointer dw real_return,0
old_timer dd 0
old_numeric dd 0
old_int2 dd 0
phase db 0
marker db 'X87-ERROR-PROOF=',0
newline db 13,10,0
success db 'X87-ERROR-PASS',13,10,'$'
failure db 'X87-ERROR-FAIL',13,10,'$'
symbols:
    db 'FPERADDR'
    dd (symbols_end-symbols_data)/4
symbols_data:
    dd start,code_end,proof,rows,original_fpu,restored_fpu,external_wait,external_nop,external_memory
%if PROTECTED
    dd native_wait,native_nop,native_memory,gdt,tss,paging_storage
%else
    times 6 dd 0
%endif
    dd program_end,divide,empty_add,old_timer,old_numeric,old_int2,phase,operand
symbols_end:
align 16,db 0
proof:
    db 'FPER0001'
    times 62 dd 0
rows times 6*64 db 0
original_fpu times 108 db 0xa5
restored_fpu times 108 db 0x5a
invalid_control dw 0x37e
zero_control dw 0x37b
operand dd 0x40000000
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
    descriptor 0xffff,0x92,0xcf
    descriptor 0xffff,0x92,0x41
    descriptor 0x67,0x89,0
gdt_end:
private_gdt dw gdt_end-gdt-1
    dd 0
private_idt dw idt_end-idt-1
    dd 0
tss:
    dd 0,0x10000+kernel_end
    dw S0,0
    times (104-12) db 0
%macro gate 1
    dw (%1-$$+0x10100)&0xffff,C0
    db 0,0x8e
    dw (%1-$$+0x10100)>>16
%endmacro
align 8,db 0
idt:
%assign vector 0
%rep 0x76
%if vector==8
    gate timer_entry
%elif vector==0x75
    gate numeric_entry
%elif vector==16
    gate mf_entry
%elif vector==14
    gate pf_entry
%else
    gate unexpected
%endif
%assign vector vector+1
%endrep
idt_end:
align 16,db 0
kernel_stack times 1024 db 0xa5
kernel_end:
paging_storage:
%if PAGING
    times 12288 db 0
%endif
%endif
program_end:
