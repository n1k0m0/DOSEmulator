; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own CF9/INIT/reset client. DOS/VDS/VCPI remain the original resident software.
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
; Reserve the complete own COM and a guarded aligned 128-KiB DMA window in a separate DOS block.
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    mov sp,real_stack_top
    mov [saved_sp],sp
    mov [saved_cs],cs
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc dos_failed
    pushfd
    pop dword [reset_proof+128]
    smsw ax
    and ax,1
    movzx eax,ax
    mov [reset_proof+8],eax
    xor ebp,ebp
    mov bp,cs
    shl ebp,4
    mov [reset_proof+12],ebp
    mov eax,1
    cpuid
    mov [reset_proof+36],eax
    mov [reset_proof+40],edx
    cmp eax,524h
    jne dos_failed
    cmp edx,135h
    jne dos_failed
    mov bx,4002h
    mov ah,48h
    int 21h
    jc dos_failed
    mov [allocation],ax
    mov [reset_proof+172],ax
    push es
    dec ax
    mov es,ax
    mov ax,[es:1]
    mov [reset_proof+174],ax
    mov ax,[es:3]
    mov [reset_proof+176],ax
    pop es
    movzx eax,word [allocation]
    shl eax,4
    add eax,20000h
    and eax,0fffe0000h
    mov [reset_proof+16],eax
    mov [dds+12],eax
    shr eax,4
    mov [window],ax
    mov [dds+8],ax
    cmp dword [reset_proof+8],0
    je tables_real
    mov di,dds
    mov dx,2ch
    mov ax,8103h
vds_lock:
    int 4bh
    jc dos_failed
    inc word [reset_proof+178]
    cmp word [dds+10],0
    jne dos_failed
    mov eax,[dds+12]
    cmp eax,[reset_proof+16]
    jne dos_failed
    mov bx,1
    xor dx,dx
    mov ax,810bh
vds_disable:
    int 4bh
    jc dos_failed
    inc word [reset_proof+182]
    jmp tables_vcpi
; Real DOS keeps its original system-register values for the final explicit software return.
tables_real:
    mov eax,cr0
    mov [reset_proof+132],eax
    mov eax,cr3
    mov [reset_proof+136],eax
    mov eax,cr4
    mov [reset_proof+140],eax
    sgdt [reset_proof+144]
    sidt [reset_proof+152]
    jmp descriptors
; Obtain original server descriptors and preserve every returned PTE; no resident monitor table is modified.
tables_vcpi:
    mov ax,0de00h
    int 67h
    test ah,ah
    jnz dos_failed
    lea eax,[ebp+table_storage+4095]
    and eax,0fffff000h
    mov [pd_linear],eax
    sub eax,ebp
    mov di,ax
    xor ax,ax
    mov cx,4096
    rep stosw
    mov ecx,[pd_linear]
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz dos_failed
    mov [switch_data],edx
    mov ecx,[pd_linear]
    add ecx,4096
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz dos_failed
    or edx,7
    mov di,[pd_linear]
    sub di,bp
    mov [di],edx
    mov edi,[pd_linear]
    add edi,4096
    sub edi,ebp
    mov si,server_descriptors
    mov ax,0de01h
    int 67h
    test ah,ah
    jnz dos_failed
    mov [server_entry],ebx
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
    int 67h
    test ah,ah
    jnz dos_failed
    cmp edx,edi
    jne dos_failed
    add edi,4096
    cmp edi,ebx
    jb identity_program
    mov edi,[reset_proof+16]
    sub edi,4096
    mov ebx,[reset_proof+16]
    add ebx,131072+4096
identity_window:
    mov ecx,edi
    shr ecx,12
    mov ax,0de06h
    int 67h
    test ah,ah
    jnz dos_failed
    cmp edx,edi
    jne dos_failed
    add edi,4096
    cmp edi,ebx
    jb identity_window
; Build only own descriptors, TSS, stack and fault gates using ordinary guest stores.
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
make_gate:
    mov word [di],pm_fault
    mov word [di+2],CLIENT_CODE
    mov word [di+4],8e00h
    mov word [di+6],0
    add di,8
    loop make_gate
    mov si,dds
    mov di,reset_proof+184
    mov cx,8
    rep movsw
    xor ax,ax
    mov es,ax
    mov eax,[es:467h]
    mov [reset_proof+160],eax
    mov ax,[es:472h]
    mov [reset_proof+164],ax
    push cs
    pop es
    in al,70h
    mov [reset_proof+167],al
    mov al,8fh
    out 70h,al
    in al,71h
    mov [reset_proof+166],al
    mov al,[reset_proof+167]
    out 70h,al
    in al,21h
    mov [reset_proof+169],al
    in al,0a1h
    mov [reset_proof+170],al
    mov dx,proof_file
    xor cx,cx
    mov ah,3ch
    int 21h
    jc dos_failed
    mov bx,ax
    mov dx,reset_proof
    mov cx,8
    mov ah,40h
    int 21h
    jc dos_failed
    cmp ax,8
    jne dos_failed
    mov ah,3eh
    int 21h
    jc dos_failed
    mov dx,proof_file
    mov ax,3d02h
    int 21h
    jc dos_failed
    mov [file_handle],ax
    ; Allow the last own Enter-break to drain before querying the actual KBC command byte.
    xor ah,ah
    int 1ah
    mov bx,dx
keyboard_drain_tick:
    sti
    hlt
    xor ah,ah
    int 1ah
    sub dx,bx
    cmp dx,2
    jb keyboard_drain_tick
    cli
    mov al,20h
    out 64h,al
    in al,60h
    mov [reset_proof+168],al
    mov ax,[window]
    mov es,ax
    xor di,di
    mov cx,8000h
    mov ax,0a5a5h
    rep stosw
    mov ax,es
    add ax,1000h
    mov es,ax
    xor di,di
    mov cx,8000h
    mov ax,0a5a5h
    rep stosw
    mov ax,[window]
    dec ax
    mov es,ax
    xor di,di
    mov cx,16
    mov al,0d6h
    rep stosb
    mov ax,[window]
    add ax,2000h
    mov es,ax
    xor di,di
    mov cx,16
    mov al,0d6h
    rep stosb
    push cs
    pop es
    fninit
    fld tword [extended]
    fldpi
    ; Real ISA DMA1 and CT1740 PCM, still active when both reset requests are captured.
    mov dx,226h
    mov al,1
    out dx,al
    mov cx,128
pcm_reset_delay:
    loop pcm_reset_delay
    xor al,al
    out dx,al
    mov dx,22eh
pcm_reset_ready:
    in al,dx
    test al,80h
    jz pcm_reset_ready
    mov dx,22ah
    in al,dx
    mov al,5
    out 0ah,al
    xor al,al
    out 0ch,al
    lea eax,[ebp+samples]
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
    mov al,[reset_proof+167]
    or al,80h
    out 70h,al
    cli
    mov al,0ffh
    out 21h,al
    out 0a1h,al
    cmp dword [reset_proof+8],0
    jne enter_vcpi
    lgdt [gdtr]
    lidt [idtr]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword CLIENT_CODE:pm_entry
; Enter through the actual original resident VCPI server.
enter_vcpi:
    lea esi,[ebp+switch_data]
    mov ax,0de0ch
vcpi_enter:
    int 67h
    jmp dos_failed
; Install a descriptor base using only stores into the own GDT.
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
; Send a real DSP command only after its documented busy state clears.
dsp:
    push ax
    mov dx,22ch
dsp_busy:
    in al,dx
    test al,80h
    jnz dsp_busy
    pop ax
    out dx,al
    ret
; This actual real-mode entry is reached only by the explicitly armed early DOSE warm dispatch.
rm_reset_stub:
    cli
    cld
    mov ax,cs
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov esp,real_stack_top
    inc dword [reset_proof+24]
    mov ebp,[reset_proof+12]
    push ds
    xor ax,ax
    mov ds,ax
    cmp word [472h],0
    pop ds
    jne rm_failed
    in al,70h
    mov bl,al
    mov al,8fh
    out 70h,al
    in al,71h
    mov bh,al
    mov al,bl
    out 70h,al
    test bh,bh
    jnz rm_failed
    mov di,reset_proof+200
    cmp dword [reset_proof+20],1
    je cookie_record
    add di,2
cookie_record:
    mov word [di],1
    ; Clear busy only in the own retained TSS descriptor before a legitimate new LTR.
    and byte [tss_descriptor+5],0fdh
    lgdt [gdtr]
    lidt [idtr]
    mov eax,[client_cr4]
    mov cr4,eax
    mov eax,[client_cr3]
    mov cr3,eax
    mov eax,[client_cr0]
    mov cr0,eax
    jmp dword CLIENT_CODE:pm_resume
rm_failed:
    mov dword [reset_proof+32],0bad0001h
    cli
    hlt
    jmp $
bits 32
; The client runs on its own GDT/IDT/tables and private conventional stack.
pm_entry:
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    mov eax,cr0
    mov [ebp+client_cr0],eax
    mov eax,cr3
    mov [ebp+client_cr3],eax
    mov eax,cr4
    mov [ebp+client_cr4],eax
    inc dword [ebp+reset_proof+224]
    clts
    ; A CF8 dword whose second byte contains RCPU must remain one configuration transaction.
    mov dx,0cf8h
    mov eax,80000400h
    out dx,eax
    xor eax,eax
    out dx,eax
    call setup_ide
    mov dx,0cf9h
    xor al,al
    out dx,al
    mov ecx,10h
    mov edx,12345678h
    mov eax,0ffffffe0h
    wrmsr
    mov dword [ebp+reset_proof+20],1
    call arm_cookie
    mov eax,[ebp+reset_proof+16]
    mov edx,80000400h
    call one_prd
    mov eax,8
    mov cl,2
    mov bl,0c8h
    call start_dma
    mov esi,[ebp+reset_proof+16]
    mov ecx,10000000
wait_first_store:
    cmp word [esi],0a5a5h
    jne request_soft
    loop wait_first_store
    jmp pm_fault
request_soft:
tsc_before:
    rdtsc
    mov [ebp+reset_proof+104],eax
    mov [ebp+reset_proof+108],edx
    mov dx,0cf9h
    mov al,4
soft_out:
    out dx,al
    jmp pm_fault
; Re-enter using the own retained descriptors after actual INIT or RESET, never a host CPU restore.
pm_resume:
    mov ebp,[cs:reset_proof+12]
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    cmp dword [ebp+reset_proof+8],0
    je resume_no_tss
    mov ax,CLIENT_TSS
    ltr ax
resume_no_tss:
    clts
    inc dword [ebp+reset_proof+224]
    cmp dword [ebp+reset_proof+20],1
    je after_soft
    cmp dword [ebp+reset_proof+20],2
    je after_hard
    jmp pm_fault
after_soft:
tsc_soft_after:
    rdtsc
    mov [ebp+reset_proof+112],eax
    mov [ebp+reset_proof+116],edx
    fstp tword [ebp+reset_proof+44]
    fstp tword [ebp+reset_proof+54]
    fnstcw [ebp+reset_proof+64]
    fnstsw [ebp+reset_proof+66]
    fld tword [ebp+extended]
    fldpi
    mov dx,0cf9h
    in al,dx
    test al,4
    jnz pm_fault
    mov eax,0abcdef04h
same_high_out:
    out dx,eax
    mov ecx,256
same_high_delay:
    loop same_high_delay
    cmp dword [ebp+reset_proof+24],1
    jne pm_fault
    mov dword [ebp+reset_proof+204],1
    call wait_dma
soft_done:
    nop
    mov dword [ebp+reset_proof+212],1
    ; New own write data must never reach media once the subsequent hard reset cancels its pending commit.
    mov edi,[ebp+reset_proof+16]
    xor esi,esi
    mov ecx,1024
fill_write:
    mov eax,esi
    imul eax,29
    add al,5bh
    stosb
    inc esi
    loop fill_write
    mov dx,0cf9h
    xor al,al
    out dx,al
    mov dword [ebp+reset_proof+20],2
    call arm_cookie
    mov eax,[ebp+reset_proof+16]
    mov edx,80000400h
    call one_prd
    mov eax,32
    mov cl,2
    mov bl,0cah
    call start_dma
    mov ecx,10000000
write_ready:
    mov dx,3f6h
    in al,dx
    test al,8
    jnz write_wait_commit
    loop write_ready
    jmp pm_fault
write_wait_commit:
    mov ecx,10000000
write_busy:
    in al,dx
    test al,80h
    jnz request_hard
    loop write_busy
    jmp pm_fault
request_hard:
    mov dx,0cf9h
    mov ax,6
hard_out:
    out dx,ax
    jmp pm_fault
after_hard:
tsc_hard_after:
    rdtsc
    mov [ebp+reset_proof+120],eax
    mov [ebp+reset_proof+124],edx
    fnstenv [ebp+reset_proof+76]
    call recover_board
    mov dword [ebp+reset_proof+208],1
    mov eax,[ebp+reset_proof+16]
    mov edx,80000200h
    call one_prd
    mov eax,32
    mov cl,1
    mov bl,0c8h
    call start_dma
    call wait_dma
    mov esi,[ebp+reset_proof+16]
    lea edi,[ebp+hard_data]
    mov ecx,512
    rep movsb
    mov dword [ebp+reset_proof+216],1
hard_done:
    nop
    mov dx,3f6h
    xor al,al
    out dx,al
    mov eax,80003904h
    mov ebx,3
    call config_word
    mov eax,[ebp+reset_proof+160]
    mov [467h],eax
    mov ax,[ebp+reset_proof+164]
    mov [472h],ax
    in al,70h
    mov bl,al
    mov al,8fh
    out 70h,al
    mov al,[ebp+reset_proof+166]
    out 71h,al
    mov al,bl
    out 70h,al
    mov dword [ebp+reset_proof+20],3
    cmp dword [ebp+reset_proof+8],0
    jne return_vcpi
    jmp word REAL_CODE:leave_real
; Acknowledge only the real selected drive and stop only the now-completed busmaster.
wait_dma:
    mov ecx,10000000
wait_dma_active:
    mov dx,0c002h
    in al,dx
    test al,1
    jz wait_dma_device
    loop wait_dma_active
    jmp pm_fault
wait_dma_device:
    mov dx,3f6h
    in al,dx
    test al,80h
    jz wait_dma_ack
    loop wait_dma_device
    jmp pm_fault
wait_dma_ack:
    mov dx,1f7h
    in al,dx
    test al,1
    jnz pm_fault
    mov dx,0c000h
    xor al,al
    out dx,al
    ret
; Program a literal eight-byte physical PRD in the own locked window's final page.
one_prd:
    mov edi,[ebp+reset_proof+16]
    add edi,1f000h
    mov [edi],eax
    mov [edi+4],edx
    ret
; Start only a real primary-slave ATA C8/CA operation through actual task/BM ports.
start_dma:
    mov [ebp+dma_lba],eax
    mov [ebp+dma_count],cl
    mov [ebp+dma_command],bl
    mov dx,0c000h
    xor al,al
    out dx,al
    add dx,2
    mov al,66h
    out dx,al
    add dx,2
    mov eax,[ebp+reset_proof+16]
    add eax,1f000h
    out dx,eax
    mov dx,1f6h
    mov al,0f0h
    out dx,al
    inc dx
    in al,dx
    mov dx,1f2h
    mov al,[ebp+dma_count]
    out dx,al
    mov eax,[ebp+dma_lba]
    inc dx
    out dx,al
    shr eax,8
    inc dx
    out dx,al
    shr eax,8
    inc dx
    out dx,al
    inc dx
    shr eax,8
    and al,15
    or al,0f0h
    out dx,al
    inc dx
    mov al,[ebp+dma_command]
    out dx,al
    mov dx,0c000h
    mov al,1
    cmp byte [ebp+dma_command],0c8h
    jne dma_start
    or al,8
dma_start:
    out dx,al
    ret
; Arm only the explicitly own DOSE firmware rendezvous; CF9 itself never interprets CMOS or BDA.
arm_cookie:
    pushad
    movzx eax,word [ebp+saved_cs]
    shl eax,16
    or eax,rm_reset_stub
    mov [467h],eax
    mov word [472h],1234h
    in al,70h
    mov bl,al
    mov al,8fh
    out 70h,al
    mov al,0ah
    out 71h,al
    mov al,bl
    out 70h,al
    popad
    ret
; Bootstrap only the new board resources through ordinary guest configuration and ATA instructions.
setup_ide:
    mov eax,80003920h
    mov ebx,0c001h
    call config_dword
    mov eax,80003940h
    mov ebx,80008000h
    call config_dword
    mov eax,80003870h
    mov ebx,0fh
    call config_byte
    mov eax,80003904h
    mov ebx,7
    call config_word
    mov dx,3f6h
    mov al,2
    out dx,al
    mov dx,1f6h
    mov al,0f0h
    out dx,al
    mov dx,1f1h
    mov al,3
    out dx,al
    inc dx
    mov al,20h
    out dx,al
    mov dx,1f7h
    mov al,0efh
    out dx,al
    in al,dx
    test al,1
    jnz pm_fault
    ret
; Hard reset recovery is explicit software initialization, not restoration of old device snapshots.
recover_board:
    mov eax,80000060h
    mov ebx,08080808h
    call config_dword
    mov eax,80000064h
    mov ebx,8
    call config_byte
    call setup_ide
    mov al,11h
    out 20h,al
    out 0a0h,al
    mov al,8
    out 21h,al
    mov al,70h
    out 0a1h,al
    mov al,4
    out 21h,al
    mov al,2
    out 0a1h,al
    mov al,1
    out 21h,al
    out 0a1h,al
    mov al,0ffh
    out 21h,al
    out 0a1h,al
    mov al,36h
    out 43h,al
    xor al,al
    out 40h,al
    out 40h,al
    mov al,60h
    out 64h,al
    mov al,[ebp+reset_proof+168]
    out 60h,al
    mov al,0aeh
    out 64h,al
    mov al,0f4h
    out 60h,al
    in al,60h
    cmp al,0fah
    jne pm_fault
    ret
; Issue an actual complete mechanism-one configuration dword.
config_dword:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,eax
    ret
; Issue an actual complete mechanism-one configuration word.
config_word:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,ax
    ret
; Issue one actual mechanism-one byte lane without hidden partial transactions.
config_byte:
    mov dx,0cf8h
    out dx,eax
    mov edx,eax
    and edx,3
    add dx,0cfch
    mov eax,ebx
    out dx,al
    ret
; Return through the original VCPI server, using only retained original descriptors and own client state.
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
; An unexpected own-client fault remains explicit and cannot be counted as a reset/DOS success.
pm_fault:
    mov ax,FLAT_DATA
    mov ds,ax
    mov dword [ebp+reset_proof+32],0badf00dh
    cli
    hlt
    jmp $
bits 16
; Re-establish ordinary real-mode caches before clearing PE on the non-VCPI path.
leave_real:
    movzx edx,word [ebp+saved_sp]
    mov ax,REAL_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    mov esp,edx
    mov eax,[cs:reset_proof+132]
    mov cr0,eax
    jmp far [cs:real_jump]
; Continue the original DOS process only after its original monitor or real-mode address environment is live.
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
    cmp dword [reset_proof+8],0
    jne release_vds
    lgdt [reset_proof+144]
    lidt [reset_proof+152]
release_vds:
    inc dword [reset_proof+228]
    cmp word [reset_proof+178],0
    je release_dos
    mov bx,1
    xor dx,dx
    mov ax,810ch
vds_enable:
    int 4bh
    jc dos_failed
    mov di,dds
    xor dx,dx
    mov ax,8104h
vds_unlock:
    int 4bh
    jc dos_failed
    inc word [reset_proof+180]
release_dos:
    mov al,[reset_proof+169]
    out 21h,al
    mov al,[reset_proof+170]
    out 0a1h,al
    mov al,[reset_proof+167]
    out 70h,al
    push dword [reset_proof+128]
    popfd
    mov bx,[file_handle]
    mov ah,3eh
    int 21h
    jc dos_failed
    mov dx,data_file
    mov si,hard_data
    mov cx,512
    call write_file
    mov dword [reset_proof+28],1
    mov dx,proof_file
    mov si,reset_proof
    mov cx,reset_proof_end-reset_proof
    call write_file
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc dos_failed
    push cs
    pop es
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
; Preserve actual own proof bytes using DOS create/write/close, never a host FAT writer.
write_file:
    push cx
    xor cx,cx
    mov ah,3ch
    int 21h
    jc dos_failed
    mov bx,ax
    mov dx,si
    pop cx
    push cx
    mov ah,40h
    int 21h
    pop cx
    jc dos_failed
    cmp ax,cx
    jne dos_failed
    mov ah,3eh
    int 21h
    jc dos_failed
    ret
; Print only through the real DOS console.
print:
    pushad
print_next:
    lodsb
    test al,al
    jz print_done
    mov dl,al
    mov ah,2
    int 21h
    jmp print_next
print_done:
    popad
    ret
; Report a real DOS setup/return failure; no host screen injection substitutes for this path.
dos_failed:
    push cs
    pop ds
    sti
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
code_end:
saved_sp dw 0
saved_cs dw 0
file_handle dw 0
allocation dw 0
window dw 0
client_cr0 dd 0
client_cr3 dd 0
client_cr4 dd 0
dma_lba dd 0
dma_count db 0
dma_command db 0
pd_linear dd 0
server_entry dd 0
    dw SERVER_CODE
switch_data dd 0,0,0
    dw 0,CLIENT_TSS
    dd pm_entry
    dw CLIENT_CODE
real_jump dw real_return,0
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
dds dd 20000h,0
    dw 0,0
    dd 0
reset_proof db 'RSTPROF1'
    times 248-8 db 0
    db 'RSTEND01'
reset_proof_end:
hard_data times 512 db 0
extended dq 0fedcba9876543210h
    dw 4001h
proof_file db 'C:\RESET.DAT',0
data_file db 'C:\RSTDATA.DAT',0
done_text db 13,10,'SNAPSHOT-DONE',13,10,'PIIX-RESET-DONE',13,10,0
failed_text db 13,10,'PIIX-RESET-FAILED',13,10,0
align 16,db 0
samples times 512 db 0c0h
    times 2048 db 0
real_stack_top:
    times 4096 db 0
pm_stack_top:
    times 2048 db 0
idt:
    times 2048 db 0
table_storage:
    times 8192+4095 db 0
server_pt_backup:
    times 4096 db 0
metadata:
    db 'RESETM01'
    dw 1,32
    dw reset_proof,reset_proof_end,code_end,program_end,rm_reset_stub,pm_resume,real_return,soft_out
    dw same_high_out,hard_out,soft_done,hard_done,vcpi_enter,vcpi_return,vds_lock,vds_unlock
    dw dds,file_handle,gdt,gdtr,idt,idtr,tss,pd_linear,server_pt_backup,real_stack_top,pm_stack_top,window
    dw tsc_before,tsc_soft_after,tsc_hard_after,vds_disable
program_end:
