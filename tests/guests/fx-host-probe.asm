; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own 82437FX A2 physical PAM guest. Private DOS buffer, real VCPI entry/return, no monitor-table edits.
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
; Retain this complete COM allocation, then reserve an independent 64-KiB DOS readback buffer.
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    mov sp,stack_top
    mov [saved_sp],sp
    mov [saved_cs],cs
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    mov bx,1000h
    mov ah,48h
    int 21h
    jc failed
    mov [buffer_segment],ax
    movzx eax,ax
    shl eax,4
    mov [fx_proof+16],eax
    xor ebp,ebp
    mov bp,cs
    shl ebp,4
    mov [fx_proof+12],ebp
    pushfd
    pop dword [fx_proof+180]
    smsw ax
    and ax,1
    movzx eax,ax
    mov [fx_proof+8],eax
    xor eax,eax
    cpuid
    mov [fx_proof+40],eax
    mov [fx_proof+44],ebx
    mov [fx_proof+48],edx
    mov [fx_proof+52],ecx
    mov eax,1
    cpuid
    mov [fx_proof+56],eax
    mov [fx_proof+60],edx
    cmp eax,524h
    jne failed
    cmp edx,135h
    jne failed
    mov ah,88h
    int 15h
    jc failed
    movzx eax,ax
    mov [fx_proof+224],eax
    cmp word [fx_proof+8],0
    jne prepare_vcpi
    mov eax,cr0
    mov [fx_proof+184],eax
    mov eax,cr3
    mov [fx_proof+188],eax
    mov eax,cr4
    mov [fx_proof+192],eax
    sgdt [fx_proof+196]
    sidt [fx_proof+204]
    jmp prepare_descriptors
; Obtain actual resident mappings; all program/buffer pages must really remain conventional identity pages.
prepare_vcpi:
    mov ax,0de00h
    int 67h
    test ah,ah
    jnz failed
    lea eax,[ebp+table_storage+4095]
    and eax,0fffff000h
    mov [pd_linear],eax
    sub eax,ebp
    mov di,ax
    xor ax,ax
    mov cx,6144
    rep stosw
    mov ecx,[pd_linear]
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz failed
    mov [switch_data],edx
    mov ecx,[pd_linear]
    add ecx,4096
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz failed
    or edx,7
    mov di,[pd_linear]
    sub di,bp
    mov [di],edx
    mov ecx,[pd_linear]
    add ecx,8192
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz failed
    or edx,3
    mov di,[pd_linear]
    sub di,bp
    mov [di+4],edx
    mov edi,[pd_linear]
    add edi,4096
    sub edi,ebp
    mov si,server_descriptors
    mov ax,0de01h
    int 67h
    test ah,ah
    jnz failed
    mov [server_entry],ebx
    ; Keep every original DE01 server PTE. Physical PAM uses a separate private four-MiB alias.
    pushad
    mov esi,[pd_linear]
    add esi,4096
    sub esi,ebp
    mov edi,server_pt_backup
    mov ecx,1024
    a32 rep movsd
    popad
    mov di,[pd_linear]
    sub di,bp
    add di,8192
    mov eax,0c0003h
    mov cx,64
.pam_alias:
    mov [di],eax
    add di,4
    add eax,4096
    loop .pam_alias
    mov dword [fx_proof+220],340000h
    ; Read-only translation services establish every owned conventional page's real backing.
    mov edi,ebp
    and edi,0fffff000h
    lea ebx,[ebp+program_end+4095]
    and ebx,0fffff000h
.program_identity:
    mov ecx,edi
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz failed
    cmp edx,edi
    jne failed
    add edi,4096
    cmp edi,ebx
    jb .program_identity
    mov edi,[fx_proof+16]
    and edi,0fffff000h
    mov ebx,[fx_proof+16]
    add ebx,65536+4095
    and ebx,0fffff000h
.buffer_identity:
    mov ecx,edi
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz failed
    cmp edx,edi
    jne failed
    add edi,4096
    cmp edi,ebx
    jb .buffer_identity
; Build only own conventional descriptors and a diagnostic IDT; server descriptors remain exactly DE01's.
prepare_descriptors:
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
.gate:
    mov word [di],pm_fault
    mov word [di+2],CLIENT_CODE
    mov word [di+4],8e00h
    mov word [di+6],0
    add di,8
    loop .gate
    mov dx,fx_filename
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov [fx_handle],ax
    mov bx,ax
    mov dx,fx_proof
    mov cx,8
    mov ah,40h
    int 21h
    jc failed
    cmp ax,8
    jne failed
    mov bx,[fx_handle]
    mov ah,3eh
    int 21h
    jc failed
    mov dx,fx_filename
    mov ax,3d02h
    int 21h
    jc failed
    mov [fx_handle],ax
    fninit
    fld tword [extended]
    fldpi
    mov si,begin_text
    call print
    mov dx,226h
    mov al,1
    out dx,al
    ; Hold reset over the documented3us minimum at the fixed100MHz/four-cycle test clock.
    mov cx,128
.delay: loop .delay
    xor al,al
    out dx,al
    mov dx,22eh
.reset: in al,dx
    test al,80h
    jz .reset
    mov dx,22ah
    in al,dx
    ; Start an 8-bit 512-component PCM stream through actual ISA DMA1.
    mov al,5
    out 0ah,al
    xor al,al
    out 0ch,al
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,samples
    mov ebx,eax
    out 2,al
    mov al,ah
    out 2,al
    shr eax,16
    out 83h,al
    mov ax,511
    out 3,al
    mov al,ah
    out 3,al
    mov al,49h
    out 0bh,al
    mov al,1
    out 0ah,al
    mov al,41h
    call dsp
    mov al,1fh
    call dsp
    mov al,40h
    call dsp
    mov al,0c0h
    call dsp
    xor al,al
    call dsp
    mov al,0ffh
    call dsp
    mov al,1
    call dsp
    ; Enable one timed OPL3 voice and a timer through real byte ports.
    mov dx,222h
    mov al,5
    out dx,al
    inc dx
    mov al,1
    out dx,al
    mov si,fm_values
    mov cx,11
.fm:
    lodsw
    mov dx,220h
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    loop .fm
    ; Begin an actual UART frame and an MPU-UART frame before the snapshot cut.
    mov dx,3fbh
    mov al,80h
    out dx,al
    mov dx,3f8h
    mov al,12
    out dx,al
    inc dx
    xor al,al
    out dx,al
    mov dx,3fbh
    mov al,3
    out dx,al
    mov dx,3f8h
    mov al,69h
    out dx,al
    mov dx,331h
    mov al,3fh
    out dx,al
.mpu:
    in al,dx
    test al,80h
    jnz .mpu
    dec dx
    in al,dx
    cmp al,0feh
    jne failed
    mov al,90h
    out dx,al
    mov dx,201h
    out dx,al
    ; CPU programs both the devices and the chipset; the harness only observes.
    in al,70h
    mov [saved_rtc],al
    or al,80h
    out 70h,al
    cmp dword [fx_proof+8],0
    jne enter_vcpi
    cli
    lgdt [gdtr]
    lidt [idtr]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword CLIENT_CODE:pm_entry
; Enter the real original EMM386 server without altering any monitor page-table entry.
enter_vcpi:
    cli
    lea esi,[ebp+switch_data]
    mov ax,0de0ch
    int 67h
    jmp failed
; Resume the actual DOS process only after original PAM bytes and hidden DRAM have been restored.
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
    cmp dword [fx_proof+8],0
    jne .monitor
    lgdt [fx_proof+196]
    lidt [fx_proof+204]
.monitor:
    inc dword [fx_proof+176]
    mov al,[saved_rtc]
    out 70h,al
    push dword [fx_proof+180]
    popfd
    ; Release the DOS-owned readback area and preserve actual raw extended registers in a real file.
    mov bx,[fx_handle]
    mov ah,3eh
    int 21h
    jc failed
    mov ax,[buffer_segment]
    mov es,ax
    mov ah,49h
    int 21h
    jc failed
    push cs
    pop es
    mov word [buffer_segment],0
    fstp tword [proof+8]
    fstp tword [proof+18]
    fnstcw [proof+28]
    fnstsw [proof+30]
    mov dx,filename
    mov si,proof
    mov cx,32
    call write_file
    mov dword [fx_proof+232],1
    mov dx,fx_filename
    mov si,fx_proof
    mov cx,fx_proof_end-fx_proof
    call write_file
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
; Failure produces a real DOS error marker and never pretends to restore an interrupted protected client.
failed:
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Write exact own bytes through original DOS rather than modifying FAT or journals from the host.
write_file:
    push cx
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,si
    pop cx
    push cx
    mov ah,40h
    int 21h
    pop cx
    jc failed
    cmp ax,cx
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    ret
; Patch the explicitly owned descriptor's base bytes before loading it.
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
; Send one actual DSP byte only after its busy latch permits it.
dsp:
    push ax
    mov dx,22ch
.busy:
    in al,dx
    test al,80h
    jnz .busy
    pop ax
    out dx,al
    ret
; Use original DOS console output, including the actual post-process prompt observed by the oracle.
print:
    pushad
.next:
    lodsb
    test al,al
    jz .out
    mov dl,al
    mov ah,2
    int 21h
    jmp .next
.out:
    popad
    ret
bits 32
; Exercise actual physical PAM through CPU memory operations in a private flat client.
pm_entry:
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    cld
    inc dword [ebp+fx_proof+172]
    mov eax,cr0
    mov [ebp+fx_proof+160],eax
    mov eax,cr3
    mov [ebp+fx_proof+164],eax
    mov eax,cr4
    mov [ebp+fx_proof+168],eax
    ; Original configuration reads include complete identity/header, DRB bytes and PAM reset/firmware state.
    mov edi,ebp
    add edi,fx_proof+64
    mov ebx,80000000h
    mov ecx,16
.config:
    mov dx,0cf8h
    mov eax,ebx
    out dx,eax
    mov dx,0cfch
    in eax,dx
    stosd
    add ebx,4
    loop .config
    cmp dword [ebp+fx_proof+64],122d8086h
    jne pm_fault
    cmp dword [ebp+fx_proof+72],06000002h
    jne pm_fault
    mov dx,0cf8h
    mov eax,80000060h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+128],eax
    mov dx,0cf8h
    mov eax,80000064h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+132],eax
    mov dx,0cf8h
    mov eax,80000058h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+136],eax
    mov dx,0cf8h
    mov eax,8000005ch
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+140],eax
    ; An actual enabled configuration miss sets master-abort; only the W1C status halfword clears it.
    mov dx,0cf8h
    mov eax,80000800h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    cmp eax,0ffffffffh
    jne pm_fault
    mov dx,0cf8h
    mov eax,80000004h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+212],eax
    mov dx,0cfeh
    mov ax,2000h
    out dx,ax
    mov dx,0cfch
    in eax,dx
    mov [ebp+fx_proof+216],eax
    mov dword [ebp+fx_proof+20],0
pm_bank:
    mov ebx,[ebp+fx_proof+20]
    mov eax,0f0000h
    mov ecx,65536
    test ebx,ebx
    jz .bounds
    lea eax,[ebx-1]
    shl eax,14
    add eax,0c0000h
    mov ecx,16384
.bounds:
    mov [ebp+fx_proof+28],eax
    mov [ebp+fx_proof+32],ecx
    xor eax,eax
    call set_pam
    mov dword [ebp+fx_proof+24],0
    call readback
    ; Copy the entire real external ROM/open-bus source before enabling any shadow read.
    mov eax,2
    call set_pam
    mov esi,[ebp+fx_proof+28]
    add esi,[ebp+fx_proof+220]
    mov edi,esi
    mov ecx,[ebp+fx_proof+32]
    rep movsb
    mov dword [ebp+fx_proof+24],1
    call readback
    mov eax,1
    call set_pam
    mov dword [ebp+fx_proof+24],2
    call readback
    mov eax,2
    call set_pam
    mov eax,31h
    call fill_pattern
    cmp dword [ebp+fx_proof+20],0
    jne pm_after_cut
fx_tsc_first:
    rdtsc
    mov [ebp+fx_proof+144],eax
    mov [ebp+fx_proof+148],edx
    ; Timed virtual peers and RC pulse have future publications at the actual WE-only cut.
    mov dx,3f8h
    mov al,6ah
    out dx,al
    mov dx,330h
    mov al,40h
    out dx,al
    mov dx,201h
    out dx,al
    mov dx,378h
    mov al,5ah
    out dx,al
    mov dx,37ah
    mov al,0ch
    out dx,al
    mov al,0dh
    out dx,al
    mov al,0ch
    out dx,al
    lea esi,[ebp+cut_text]
    call debug_string32
fx_cut_instruction:
    nop
pm_after_cut:
    mov dword [ebp+fx_proof+24],3
    call readback
    mov eax,1
    call set_pam
    mov dword [ebp+fx_proof+24],4
    call readback
    ; Read-only shadow discards writes; readback must retain the first complete pattern.
    mov eax,79h
    call fill_pattern
    mov dword [ebp+fx_proof+24],5
    call readback
    mov eax,3
    call set_pam
    mov eax,79h
    call fill_pattern
    mov dword [ebp+fx_proof+24],6
    call readback
    mov eax,7
    call set_pam
    mov eax,0c7h
    call fill_pattern
    mov dword [ebp+fx_proof+24],7
    call readback
    ; Fresh cold-start hidden backing was independently checked zero before probing; restore every byte.
    mov eax,2
    call set_pam
    mov edi,[ebp+fx_proof+28]
    add edi,[ebp+fx_proof+220]
    mov ecx,[ebp+fx_proof+32]
    shr ecx,2
    xor eax,eax
    rep stosd
    xor eax,eax
    call set_pam
    mov dword [ebp+fx_proof+24],8
    call readback
    inc dword [ebp+fx_proof+20]
    cmp dword [ebp+fx_proof+20],13
    jb pm_bank
fx_tsc_last:
    rdtsc
    mov [ebp+fx_proof+152],eax
    mov [ebp+fx_proof+156],edx
    cmp dword [ebp+fx_proof+8],0
    jne return_vcpi
    jmp REAL_CODE:leave_real
; Change one actual PAM nibble, preserving sibling and reserved bits through exact byte-lane accesses.
set_pam:
    push ebx
    push ecx
    mov ecx,[ebp+fx_proof+20]
    mov bl,al
    test ecx,ecx
    jz .f
    dec ecx
    test cl,1
    jz .low
    shl bl,4
.low:
    shr ecx,1
    add ecx,5ah
    jmp .select
.f:
    mov ecx,59h
    shl bl,4
.select:
    mov edx,ecx
    and edx,3
    add edx,0cfch
    mov [ebp+pam_port],dx
    mov eax,ecx
    and eax,0fch
    or eax,80000000h
    mov dx,0cf8h
    out dx,eax
    mov dx,[ebp+pam_port]
    mov al,bl
    out dx,al
    in al,dx
    movzx eax,al
    mov [ebp+fx_proof+36],eax
    pop ecx
    pop ebx
    ret
; Write a full independent byte pattern using actual guest memory accesses.
fill_pattern:
    pushad
    mov ebx,[ebp+fx_proof+20]
    imul ebx,17
    add eax,ebx
    mov edi,[ebp+fx_proof+28]
    add edi,[ebp+fx_proof+220]
    mov ecx,[ebp+fx_proof+32]
.byte:
    stosb
    add al,13
    loop .byte
    popad
    ret
; Copy every real bank byte into a DOS-owned buffer; the host merely observes at the following own label.
readback:
    pushad
    mov esi,[ebp+fx_proof+28]
    add esi,[ebp+fx_proof+220]
    mov edi,[ebp+fx_proof+16]
    mov ecx,[ebp+fx_proof+32]
    rep movsb
bank_report:
    nop
    popad
    ret
; Send the exact E9 cut marker through actual OUT instructions from this private protected client.
debug_string32:
    lodsb
    test al,al
    jz .done
    out 0e9h,al
    jmp debug_string32
.done:
    ret
; Return using the real protected server entry and its original descriptor, not a synthetic host mode switch.
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
    call far [ebp+server_entry]
; Record an actual unexpected private-client fault, then stop before corrupting DOS state.
pm_fault:
    mov ax,FLAT_DATA
    mov ds,ax
    mov dword [ebp+fx_proof+236],0badf00dh
    cli
    hlt
    jmp $
bits 16
; Leave only the private real-mode client's PE interval; no task register was loaded on this path.
leave_real:
    ; Hidden segment limits/default stack width survive PE=0; establish real-compatible caches first.
    movzx edx,word [ebp+saved_sp]
    mov ax,REAL_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov esp,edx
    mov eax,[cs:fx_proof+184]
    mov cr0,eax
    jmp far [cs:real_jump]
real_jump dw real_return,0
saved_sp dw 0
saved_cs dw 0
saved_rtc db 0
buffer_segment dw 0
fx_handle dw 0
pam_port dw 0
pd_linear dd 0
server_entry dd 0
    dw SERVER_CODE
switch_data dd 0,0,0
    dw 0,CLIENT_TSS
    dd pm_entry
    dw CLIENT_CODE
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
gdt_end:
tss times 104 db 0
fx_proof db 'FXPROOF1'
    times 240-8 db 0
    db 'FXEND001'
fx_proof_end:
proof db 'SNAPRAW1'
    times 24 db 0
filename db 'C:\SNAP.DAT',0
fx_filename db 'C:\FXHOST.DAT',0
begin_text db 13,10,'FXHOST-BEGIN',13,10,0
cut_text db 13,10,'FXHOST-CUT',13,10,0
done_text db 13,10,'SNAPSHOT-DONE',13,10,0
failed_text db 13,10,'FXHOST-FAILED',13,10,0
extended dq 0fedcba9876543210h
    dw 4001h
fm_values dw 0120h,0123h,1040h,0043h,0f060h,0f063h,0780h,0783h,30c0h,98a0h,31b0h
align 16,db 0
samples times 512 db 0c0h
    times 2048 db 0
stack_top:
fx_metadata db 'FXMETA01'
    dw fx_proof,fx_proof_end,bank_report,fx_tsc_first,fx_tsc_last,pm_fault,program_end,fx_cut_instruction,pd_linear,table_storage,real_jump,server_pt_backup
server_pt_backup times 4096 db 0
idt times 2048 db 0
pm_stack times 4096 db 0
pm_stack_top:
table_storage times 12288+4095 db 0
program_end:
