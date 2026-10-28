; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS PCI BIOS and actual BIOS32 client; original VCPI server/PTEs are retained.
bits 16
cpu pentium
org 100h
%define CLIENT_CODE 8
%define FLAT_DATA 10h
%define CLIENT_TSS 18h
%define SERVER_CODE 20h
%define SERVER_DATA 28h
%define REAL_CODE 38h
%define REAL_DATA 40h
%define BIOS_CODE 48h
%define P proof
; Load a concrete call and leave every result to the real firmware, never a host substitute.
%macro Q 7
    mov word [P+14],%7
    mov eax,%1
    mov ebx,%2
    mov ecx,%3
    mov edx,%4
    mov esi,%5
    mov edi,%6
%if __BITS__ = 16
    call bios16
%else
    call bios32
%endif
%endmacro
; Require the documented successful CF/AH pair immediately after the real call.
%macro GOOD 0
%if __BITS__ = 16
    jc dos_failed
    test ah,ah
    jnz dos_failed
%else
    jc pm_fault
    test ah,ah
    jnz pm_fault
%endif
%endmacro
; Require one concrete historical error without treating an arbitrary failure as success.
%macro ERROR 1
%if __BITS__ = 16
    jnc dos_failed
    cmp ah,%1
    jne dos_failed
%else
    jnc pm_fault
    cmp ah,%1
    jne pm_fault
%endif
%endmacro
; Select the original16/32-bit routing envelope exactly as the BIOS2.1 interface requires.
%macro ROUTE 2
    mov word [P+14],%1
    mov eax,0b10eh
    xor ebx,ebx
    xor ecx,ecx
    xor edx,edx
    xor esi,esi
%if __BITS__ = 16
    mov word [route16],%2
    mov di,route16
    call bios16
%else
    mov word [ebp+route32],%2
    lea edi,[ebp+route32]
    call bios32
%endif
%endmacro
; Run the same independent identity, error, width and actual S3 resource checks through both real entry mechanisms.
%macro COMMON 1
    Q 0b101h,0,0,0,0,0c0de1234h,%1+1
    GOOD
    cmp bx,0210h
    jne FAIL
    cmp cl,0
    jne FAIL
    cmp edx,020494350h
    jne FAIL
    cmp edi,0c0de1234h
    jne FAIL
    Q 0b102h,0,0122dh,08086h,0,0,%1+2
    GOOD
    cmp bx,0
    jne FAIL
    Q 0b102h,0,08811h,05333h,0,0,%1+3
    GOOD
    cmp bx,18h
    jne FAIL
    Q 0b102h,0,0122eh,08086h,0,0,%1+4
    GOOD
    cmp bx,38h
    jne FAIL
    Q 0b102h,0,01230h,08086h,0,0,%1+5
    GOOD
    cmp bx,39h
    jne FAIL
    Q 0b102h,0,0122dh,08086h,1,0,%1+6
    ERROR 86h
    Q 0b102h,0,0122dh,0ffffh,0,0,%1+7
    ERROR 83h
    Q 0b103h,0,060000h,0,0,0,%1+8
    GOOD
    cmp bx,0
    jne FAIL
    Q 0b103h,0,030000h,0,0,0,%1+9
    GOOD
    cmp bx,18h
    jne FAIL
    Q 0b103h,0,060100h,0,0,0,%1+10
    GOOD
    cmp bx,38h
    jne FAIL
    Q 0b103h,0,010180h,0,0,0,%1+11
    GOOD
    cmp bx,39h
    jne FAIL
    Q 0b103h,0,0ffffffh,0,0,0,%1+12
    ERROR 86h
    Q 0b108h,0,0a5a5a5a5h,0,0,0,%1+13
    GOOD
    cmp cl,86h
    jne FAIL
    Q 0b109h,0,0a5a5a5a5h,0,0,0,%1+14
    GOOD
    cmp cx,8086h
    jne FAIL
    Q 0b10ah,0,0a5a5a5a5h,0,0,0,%1+15
    GOOD
    cmp ecx,122d8086h
    jne FAIL
    Q 0b109h,0,0a5a5a5a5h,0,0,1,%1+16
    ERROR 87h
    Q 0b10ah,0,0a5a5a5a5h,0,0,2,%1+17
    ERROR 87h
    Q 0b108h,0,0a5a5a5a5h,0,0,100h,%1+18
    ERROR 87h
    Q 0b10ah,0f8h,0,0,0,0,%1+19
    GOOD
    cmp ecx,0ffffffffh
    jne FAIL
    Q 0b106h,0,0,0,0,0,%1+20
    ERROR 81h
    Q 0b109h,18h,0,0,0,4,%1+21
    GOOD
    cmp cx,3
    jne FAIL
    Q 0b10ch,18h,2,0,0,4,%1+22
    GOOD
    mov dx,3cch
    in al,dx
    cmp al,0ffh
    jne FAIL
    Q 0b10dh,18h,0ffffffffh,0,0,10h,%1+23
    GOOD
    Q 0b10ah,18h,0,0,0,10h,%1+24
    GOOD
    cmp ecx,0ff800000h
    jne FAIL
    Q 0b10dh,18h,0e0000000h,0,0,10h,%1+25
    GOOD
    Q 0b10ch,18h,3,0,0,4,%1+26
    GOOD
    mov dx,3cch
    in al,dx
    cmp al,67h
    jne FAIL
    Q 0b10ah,18h,0,0,0,10h,%1+27
    GOOD
    cmp ecx,0e0000000h
    jne FAIL
    ROUTE %1+28,0
    ERROR 89h
    ROUTE %1+29,64
    GOOD
    cmp bx,0
    jne FAIL
    Q 0b10fh,18h,0b0ah,0,0,0,%1+30
    GOOD
    ROUTE %1+31,64
    GOOD
    cmp bx,0800h
    jne FAIL
    Q 0b10fh,18h,0b0bh,0,0,0,%1+32
    ERROR 88h
    mov dx,4d1h
    in al,dx
    test al,8
    jz FAIL
    Q 0b10bh,38h,80h,0,0,60h,%1+33
    GOOD
    mov al,[P+131]
    mov dx,4d1h
    out dx,al
    test word [P+218],2000h
    jnz %%abort_preserved
    Q 0b10ch,0,2000h,0,0,6,%1+34
    GOOD
%%abort_preserved:
%endmacro
; Reserve every own instruction/table/stack byte before any firmware or protected-mode call.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    mov ax,cs
    mov ss,ax
    mov sp,real_stack_top
    mov [saved_sp],sp
    mov [saved_cs],cs
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc dos_failed
    mov [proof+166],bx
    mov [proof+164],cs
    xor ebp,ebp
    mov bp,cs
    shl ebp,4
    mov [proof+16],ebp
    pushfd
    pop dword [proof+124]
    smsw ax
    and ax,1
    mov [proof+8],ax
    mov ax,cs
    mov [route16+4],ax
    lea eax,[ebp+route_buffer]
    mov [route32+2],eax
    mov eax,1
    cpuid
    mov [proof+168],eax
    mov [proof+172],edx
    cmp eax,524h
    jne dos_failed
    cmp edx,135h
    jne dos_failed
    mov dx,0cf8h
    in eax,dx
    mov [proof+24],eax
    mov eax,80000004h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+216],eax
    mov dx,0cf8h
    mov eax,80003860h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+220],eax
    mov dx,0cf8h
    mov eax,80001804h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+224],eax
    mov dx,0cf8h
    mov eax,80001810h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+228],eax
    mov dx,0cf8h
    mov eax,8000f8a0h
    out dx,eax
    mov [proof+28],eax
    mov dx,4d1h
    in al,dx
    mov [proof+131],al
    mov di,route_buffer
    mov cx,64
    mov al,0a5h
    rep stosb
    mov dx,proof_file
    xor cx,cx
    mov ah,3ch
    int 21h
    jc dos_failed
    mov bx,ax
    mov dx,proof
    mov cx,8
    mov ah,40h
    int 21h
    jc dos_failed
    mov ah,3eh
    int 21h
    jc dos_failed
    mov dx,proof_file
    mov ax,3d02h
    int 21h
    jc dos_failed
    mov [file_handle],ax
    mov [proof+160],ax
    finit
    fldpi
    mov word [proof+10],1
%define FAIL dos_failed
    COMMON 0
    mov word [proof+10],2
    Q 0b10ah,0,0,0,0,0,99
    GOOD
    cmp ecx,122d8086h
    jne dos_failed
    mov word [proof+10],3
    mov ax,0e000h
    mov fs,ax
    xor di,di
; Scan real guest ROM bytes at exactly the BIOS32-defined16-byte alignment.
directory_scan:
    cmp dword [fs:di],'_32_'
    jne .next
    cmp byte [fs:di+8],0
    jne .next
    cmp byte [fs:di+9],1
    jne .next
    push di
    mov cx,16
    xor ax,ax
.sum:
    add al,[fs:di]
    inc di
    loop .sum
    pop di
    test al,al
    jnz .next
    xor eax,eax
    mov ax,fs
    shl eax,4
    movzx edx,di
    add eax,edx
    mov [proof+40],eax
    mov eax,[fs:di+4]
    mov [proof+44],eax
    mov [directory_far],eax
    push di
    mov si,di
    mov di,proof+48
    mov cx,16
.copy:
    mov al,[fs:si]
    mov [di],al
    inc si
    inc di
    loop .copy
    pop di
    jmp directory_found
.next:
    add di,16
    jnc directory_scan
    mov ax,fs
    cmp ax,0f000h
    je dos_failed
    mov ax,0f000h
    mov fs,ax
    jmp directory_scan
; Obtain original server descriptors and all1024 original PTEs using only original VCPI services.
directory_found:
    push cs
    pop fs
    cmp word [proof+8],0
    je real_tables
    mov ax,0de00h
vcpi_present:
    int 67h
    test ah,ah
    jnz dos_failed
    mov [proof+144],bx
    lea eax,[ebp+table_storage+4095]
    and eax,0fffff000h
    mov [pd_linear],eax
    mov [proof+152],eax
    mov edi,eax
    sub edi,ebp
; Address-size32 selects the entire ECX; discard the previous PCI reply's upper count bits.
    xor ax,ax
    mov ecx,4096
    a32 rep stosw
    mov ecx,[pd_linear]
    shr ecx,12
    mov ax,0de06h
vcpi_pd:
    int 67h
    test ah,ah
    jnz dos_failed
    mov [switch_data],edx
    mov ecx,[pd_linear]
    add ecx,4096
    shr ecx,12
    mov ax,0de06h
vcpi_pt:
    int 67h
    test ah,ah
    jnz dos_failed
    or edx,7
    mov edi,[pd_linear]
    sub edi,ebp
    mov [edi],edx
    mov edi,[pd_linear]
    add edi,4096
    sub edi,ebp
    mov si,server_descriptors
    mov ax,0de01h
vcpi_interface:
    int 67h
    test ah,ah
    jnz dos_failed
    mov [server_entry],ebx
    mov [proof+148],ebx
    pushad
    mov esi,[pd_linear]
    add esi,4096
    sub esi,ebp
    mov edi,server_pt_backup
    mov ecx,1024
    a32 rep movsd
    popad
    mov edi,ebp
    and edi,0fffff000h
    lea ebx,[ebp+program_end+4095]
    and ebx,0fffff000h
identity_program:
    mov ecx,edi
    shr ecx,12
    mov ax,0de06h
vcpi_identity:
    int 67h
    test ah,ah
    jnz dos_failed
    cmp edx,edi
    jne dos_failed
    add edi,4096
    cmp edi,ebx
    jb identity_program
    jmp descriptors
; Real DOS saves only its own genuine preexisting system registers for the explicit return.
real_tables:
    mov eax,cr0
    mov [proof+96],eax
    mov eax,cr3
    mov [proof+100],eax
    mov eax,cr4
    mov [proof+104],eax
    sgdt [proof+108]
    sidt [proof+116]
; Construct a private GDT/IDT/TSS, preserving every resident monitor descriptor and mapping.
descriptors:
    mov eax,ebp
    mov di,code_descriptor
    call descriptor_base
    mov eax,ebp
    mov di,real_descriptor
    call descriptor_base
    mov eax,ebp
    mov di,real_data_descriptor
    call descriptor_base
    lea eax,[ebp+tss]
    mov di,tss_descriptor
    call descriptor_base
    lea eax,[ebp+gdt]
    mov [gdtr+2],eax
    lea eax,[ebp+idt]
    mov [idtr+2],eax
    lea eax,[ebp+gdtr]
    mov [switch_data+4],eax
    lea eax,[ebp+idtr]
    mov [switch_data+8],eax
    mov word [tss+102],104
    mov ax,[saved_cs]
    mov [real_jump+2],ax
    mov di,idt
    mov cx,256
.gates:
    mov word [di],pm_fault
    mov word [di+2],CLIENT_CODE
    mov word [di+4],8e00h
    mov word [di+6],0
    add di,8
    loop .gates
    in al,70h
    mov [proof+128],al
    in al,21h
    mov [proof+129],al
    in al,0a1h
    mov [proof+130],al
    cli
    mov al,[proof+128]
    or al,80h
    out 70h,al
    mov al,0ffh
    out 21h,al
    out 0a1h,al
    mov word [proof+10],4
    cmp word [proof+8],0
    jne enter_vcpi
    lgdt [gdtr]
    lidt [idtr]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword CLIENT_CODE:pm_entry
; Enter only through the actual original VCPI server.
enter_vcpi:
    lea esi,[ebp+switch_data]
    mov ax,0de0ch
vcpi_enter:
    int 67h
    jmp dos_failed
; Set only a descriptor's own four base bytes.
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
; Call the genuine16-bit BIOS, then preserve its flags/results while checking exact CF8 restoration.
bios16:
    inc word [cs:proof+20]
    push ds
    cmp ax,0b10eh
    je .rom_data
    cmp ax,0b10fh
    jne .call
.rom_data:
    ; PCI BIOS2.1 routing services require the real F0000h/64KiB data segment.
    push ax
    mov ax,0f000h
    mov ds,ax
    pop ax
.call:
bios16_call:
    int 1ah
bios16_return:
    pushfd
    push eax
    push edx
    mov dx,0cf8h
    in eax,dx
    cmp eax,8000f8a0h
    jne dos_failed
    pop edx
    pop eax
    popfd
    pop ds
    ret
bits 32
%undef P
%define P ebp+proof
%undef FAIL
%define FAIL pm_fault
; Execute real BIOS32 directory and service FAR calls on own correctly mapped32-bit selectors.
pm_entry:
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    clts
    inc dword [ebp+proof+136]
    mov word [ebp+proof+10],5
    mov eax,049435024h
    xor ebx,ebx
    mov ecx,13579bdfh
    mov edx,2468ace0h
    mov esi,55667788h
    mov edi,0c0de1234h
directory_call:
    call far [ebp+directory_far]
directory_return:
    mov [ebp+proof+64],eax
    mov [ebp+proof+68],ebx
    mov [ebp+proof+72],ecx
    mov [ebp+proof+76],edx
    test al,al
    jnz pm_fault
    cmp ebx,0f0000h
    jne pm_fault
    cmp ecx,10000h
    jne pm_fault
    add edx,ebx
    mov [ebp+pci_far],edx
    mov eax,012345678h
    xor ebx,ebx
missing_directory_call:
    call far [ebp+directory_far]
missing_directory_return:
    mov [ebp+proof+80],eax
    cmp al,80h
    jne pm_fault
    mov eax,049435024h
    mov ebx,1
reserved_directory_call:
    call far [ebp+directory_far]
reserved_directory_return:
    mov [ebp+proof+84],eax
    cmp al,81h
    jne pm_fault
    mov word [ebp+proof+10],6
    Q 0b10ah,39h,0,0,0,0,199
    GOOD
    cmp ecx,12308086h
    jne pm_fault
    mov word [ebp+proof+10],7
    COMMON 100
    fstp tword [ebp+raw_data]
    mov word [ebp+proof+10],8
    cmp word [ebp+proof+8],0
    jne return_vcpi
    jmp word REAL_CODE:leave_real
; Call the real32-bit PCI service, retaining all genuine outputs and carry for the caller.
bios32:
    inc word [ebp+proof+22]
bios32_call:
    call far [ebp+pci_far]
bios32_return:
    pushfd
    push eax
    push edx
    mov dx,0cf8h
    in eax,dx
    cmp eax,8000f8a0h
    jne pm_fault
    pop edx
    pop eax
    popfd
    ret
; Return through the original server and its supplied descriptors, never a host CPU restore.
return_vcpi:
    movzx eax,word [ebp+saved_cs]
    push eax
    push eax
    push eax
    push eax
    push eax
    movzx ecx,word [ebp+saved_sp]
    push ecx
    push dword 0
    push eax
    push dword real_return
    mov ax,SERVER_DATA
    mov ds,ax
    mov ax,0de0ch
vcpi_return:
    call far [ebp+server_entry]
; Make any unexpected protected-client exception a visible bounded failure, never a successful return.
pm_fault:
    mov ax,FLAT_DATA
    mov ds,ax
    mov dword [ebp+proof+200],0badf00dh
    cli
    hlt
    jmp $
bits 16
%undef P
%define P proof
; Rebuild ordinary16-bit caches before restoring the original real CR0 environment.
leave_real:
    movzx edx,word [ebp+saved_sp]
    mov ax,REAL_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov esp,edx
    mov eax,[cs:proof+96]
    mov cr0,eax
    jmp far [cs:real_jump]
; Resume original DOS only after the original real or VCPI environment is active again.
real_return:
    push cs
    pop ds
    push cs
    pop es
    mov ax,cs
    mov ss,ax
    movzx esp,word [saved_sp]
    mov fs,ax
    mov gs,ax
    cmp word [proof+8],0
    jne .restored
    lgdt [proof+108]
    lidt [proof+116]
.restored:
    inc dword [proof+140]
    mov dx,0cf8h
    mov eax,[proof+24]
    out dx,eax
    mov al,[proof+129]
    out 21h,al
    mov al,[proof+130]
    out 0a1h,al
    mov al,[proof+128]
    out 70h,al
    push dword [proof+124]
    popfd
    mov bx,[file_handle]
    mov ah,3eh
    int 21h
    jc dos_failed
    mov word [proof+10],9
    mov dx,cd_path
    mov ax,3d00h
cd_open:
    int 21h
cd_open_return:
    jc dos_failed
    mov bx,ax
    mov dx,cd_buffer
    mov cx,256
    mov ah,3fh
cd_read:
    int 21h
cd_read_return:
    jc dos_failed
    mov [proof+162],ax
    cmp ax,197
    jne dos_failed
    mov ah,3eh
    int 21h
    jc dos_failed
    mov dx,cd_file
    mov si,cd_buffer
    mov cx,197
    call write_file
    mov dx,raw_file
    mov si,raw_data
    mov cx,10
    call write_file
    mov word [proof+10],10
    mov dx,proof_file
    mov si,proof
    mov cx,512
    call write_file
    mov si,done_text
    call print
    mov ax,4c00h
exit_call:
    int 21h
; Write actual original DOS files without any host FAT or RAM injection.
write_file:
    push cx
    push si
    xor cx,cx
    mov ah,3ch
    int 21h
    jc dos_failed
    mov bx,ax
    pop dx
    pop cx
    mov ah,40h
    int 21h
    jc dos_failed
    cmp ax,cx
    jne dos_failed
    mov ah,3eh
    int 21h
    jc dos_failed
    ret
; Emit text solely via original DOS console services.
print:
    lodsb
    test al,al
    jz .done
    mov dl,al
    mov ah,2
    int 21h
    jmp print
.done:
    ret
; Preserve a clear own error phase and return a genuine nonzero DOS status.
dos_failed:
    push cs
    pop ds
    mov word [proof+12],0ffffh
    sti
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
code_end:
saved_sp dw 0
saved_cs dw 0
file_handle dw 0
pd_linear dd 0
server_entry dd 0
    dw SERVER_CODE
switch_data dd 0,0,0
    dw 0,CLIENT_TSS
    dd pm_entry
    dw CLIENT_CODE
real_jump dw real_return,0
directory_far dd 0
    dw BIOS_CODE
pci_far dd 0
    dw BIOS_CODE
route16 dw 0,route_buffer,0
route32 dw 0
    dd 0
    dw FLAT_DATA
route_buffer times 64 db 0a5h
gdtr dw gdt_end-gdt-1
    dd 0
idtr dw 2047
    dd 0
align 8,db 0
gdt dq 0
code_descriptor dw 0ffffh,0
    db 0,9bh,40h,0
flat_descriptor dw 0ffffh,0
    db 0,93h,0cfh,0
tss_descriptor dw 103,0
    db 0,89h,0,0
server_descriptors times 24 db 0
real_descriptor dw 0ffffh,0
    db 0,9bh,0,0
real_data_descriptor dw 0ffffh,0
    db 0,93h,0,0
bios_descriptor dw 0ffffh,0
    db 0,9bh,0cfh,0
gdt_end:
tss times 104 db 0
proof db 'PCIBIO01'
    times 504-8 db 0
    db 'PCIBEND1'
proof_end:
raw_data times 10 db 0
cd_buffer times 256 db 0
proof_file db 'C:\PCIBIOS.DAT',0
raw_file db 'C:\PCIBRAW.DAT',0
cd_file db 'C:\PCIBCD.TXT',0
cd_path db 'R:\IDENT.TXT',0
done_text db 13,10,'PCI-BIOS-DONE',13,10,0
failed_text db 13,10,'PCI-BIOS-FAILED',13,10,0
    times 2048 db 0
real_stack_top:
    times 4096 db 0
pm_stack_top:
idt:
    times 2048 db 0
table_storage:
    times 8192+4095 db 0
server_pt_backup:
    times 4096 db 0
metadata:
    db 'PCIBMET1'
    dw 1,40
    dw proof,proof_end,code_end,program_end,bios16_call,bios16_return,bios32_call,bios32_return,directory_call,directory_return
    dw missing_directory_call,missing_directory_return,reserved_directory_call,reserved_directory_return,vcpi_present,vcpi_pd,vcpi_pt,vcpi_interface
    dw vcpi_identity,vcpi_enter,vcpi_return,pm_entry,real_return,real_stack_top,pm_stack_top,pd_linear,server_pt_backup,gdt,gdtr,idt,idtr,tss
    dw route16,route32,route_buffer,directory_far,pci_far,file_handle,raw_data,cd_read
program_end:
