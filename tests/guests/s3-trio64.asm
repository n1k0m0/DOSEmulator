; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own Trio64 PCI/bank/LFB client. DOS/VDS/VCPI remain the original resident software.
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
    pop dword [s3_proof+128]
    smsw ax
    and ax,1
    movzx eax,ax
    mov [s3_proof+8],eax
    mov dword [s3_proof+220],0e0000000h
    mov dword [s3_proof+232],0e0800000h
    test eax,eax
    jz .aliases_ready
    mov dword [s3_proof+220],400000h
    mov dword [s3_proof+232],800000h
.aliases_ready:
    xor ebp,ebp
    mov bp,cs
    shl ebp,4
    mov [s3_proof+12],ebp
    mov eax,1
    cpuid
    mov [s3_proof+36],eax
    mov [s3_proof+40],edx
    cmp eax,524h
    jne dos_failed
    cmp edx,135h
    jne dos_failed
    mov bx,4002h
    mov ah,48h
    int 21h
    jc dos_failed
    mov [allocation],ax
    mov [s3_proof+172],ax
    push es
    dec ax
    mov es,ax
    mov ax,[es:1]
    mov [s3_proof+174],ax
    mov ax,[es:3]
    mov [s3_proof+176],ax
    pop es
    movzx eax,word [allocation]
    shl eax,4
    add eax,20000h
    and eax,0fffe0000h
    mov [s3_proof+16],eax
    mov [dds+12],eax
    shr eax,4
    mov [window],ax
    mov [dds+8],ax
    cmp dword [s3_proof+8],0
    je tables_real
    mov di,dds
    mov dx,2ch
    mov ax,8103h
vds_lock:
    int 4bh
    jc dos_failed
    inc word [s3_proof+178]
    cmp word [dds+10],0
    jne dos_failed
    mov eax,[dds+12]
    cmp eax,[s3_proof+16]
    jne dos_failed
    mov bx,1
    xor dx,dx
    mov ax,810bh
vds_disable:
    int 4bh
    jc dos_failed
    inc word [s3_proof+182]
    jmp tables_vcpi
; Real DOS keeps its original system-register values for the final explicit software return.
tables_real:
    mov eax,cr0
    mov [s3_proof+132],eax
    mov eax,cr3
    mov [s3_proof+136],eax
    mov eax,cr4
    mov [s3_proof+140],eax
    sgdt [s3_proof+144]
    sidt [s3_proof+152]
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
    ; Own PDE1/2 map only the two physical LFB candidates; every original DE01 PTE remains intact.
    mov edi,[pd_linear]
    sub edi,ebp
    mov eax,[pd_linear]
    add eax,8192
    or eax,7
    mov [edi+4],eax
    add eax,4096
    mov [edi+8],eax
    mov edi,[pd_linear]
    add edi,8192
    sub edi,ebp
    mov eax,0e0000007h
    mov ecx,512
.lfb_old_pt:
    mov [edi],eax
    add eax,4096
    add edi,4
    loop .lfb_old_pt
    add edi,2048
    mov eax,0e0800007h
    mov ecx,512
.lfb_new_pt:
    mov [edi],eax
    add eax,4096
    add edi,4
    loop .lfb_new_pt
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
    mov edi,[s3_proof+16]
    sub edi,4096
    mov ebx,[s3_proof+16]
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
    mov di,s3_proof+184
    mov cx,8
    rep movsw

    in al,70h
    mov [s3_proof+167],al
    in al,21h
    mov [s3_proof+169],al
    in al,0a1h
    mov [s3_proof+170],al
    mov dx,proof_file
    xor cx,cx
    mov ah,3ch
    int 21h
    jc dos_failed
    mov bx,ax
    mov dx,s3_proof
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
    ; Actual original BIOS mode13 is only the VGA starting point; S3 modes below are real register programming.
    mov ax,13h
bios_graphics:
    int 10h
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
    mov [s3_proof+168],al
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

    mov al,[s3_proof+167]
    or al,80h
    out 70h,al
    cli
    mov al,0ffh
    out 21h,al
    out 0a1h,al
    cmp dword [s3_proof+8],0
    jne enter_vcpi
    lgdt [gdtr]
    lidt [idtr]
    mov eax,cr0
    or eax,1
    mov cr0,eax
    jmp dword CLIENT_CODE:pm_entry
; Enter through the unchanged original resident VCPI server.
enter_vcpi:
    lea esi,[ebp+switch_data]
    mov ax,0de0ch
vcpi_enter:
    int 67h
    jmp dos_failed
; Program only the base of one own descriptor.
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
bits 32
; Both DOS paths now execute on the own protected client; original DE01 tables remain unchanged.
pm_entry:
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    clts
    inc dword [ebp+s3_proof+224]
    mov eax,80001800h
    call pci_read
    mov [ebp+s3_proof+80],eax
    cmp eax,88115333h
    jne pm_fault
    mov eax,80001808h
    call pci_read
    mov [ebp+s3_proof+84],eax
    and eax,0ffffff00h
    cmp eax,03000000h
    jne pm_fault
    mov eax,80001810h
    call pci_read
    mov [ebp+s3_proof+88],eax
    cmp eax,0e0000000h
    jne pm_fault
    mov eax,80001804h
    call pci_read
    mov [ebp+s3_proof+96],ax
    cmp ax,3
    jne pm_fault
    mov eax,80001804h
    mov ebx,1
    call pci_word
    mov eax,80001810h
    mov ebx,0ffffffffh
    call pci_dword
    call pci_read_bar
    mov [ebp+s3_proof+92],eax
    cmp eax,0ff800000h
    jne pm_fault
    mov eax,80001810h
    mov ebx,0e0000000h
    call pci_dword
    mov eax,80001804h
    mov ebx,3
    call pci_word
    ; Actual PCI I/O gate and original wake/POS sequence, never a host wake shortcut.
    mov dx,46e8h
    mov al,10h
    out dx,al
    mov dx,102h
    mov al,1
    out dx,al
    mov dx,46e8h
    mov al,8
    out dx,al
    mov dx,3c2h
    mov al,67h
    out dx,al
    mov ax,4838h
    call crtc_write
    mov ax,0a039h
    call crtc_write
    mov al,2dh
    call crtc_read
    mov [ebp+s3_proof+98],al
    cmp al,88h
    jne pm_fault
    mov al,2eh
    call crtc_read
    mov [ebp+s3_proof+99],al
    cmp al,11h
    jne pm_fault
    mov al,2fh
    call crtc_read
    mov [ebp+s3_proof+100],al
    cmp al,[ebp+s3_proof+84]
    jne pm_fault
    mov al,30h
    call crtc_read
    mov [ebp+s3_proof+101],al
    cmp al,0e1h
    jne pm_fault
    ; Explicit VGA pass-through state: no set/reset, rotate, logical op, odd/even or pixel bit mask.
    mov dx,3c4h
    mov ax,0f02h
    out dx,ax
    mov ax,0e04h
    out dx,ax
    mov ax,0101h
    out dx,ax
    mov dx,3ceh
    xor ax,ax
    out dx,ax
    mov ax,1
    out dx,ax
    mov ax,3
    out dx,ax
    mov ax,4005h
    out dx,ax
    mov ax,0ff08h
    out dx,ax
    mov ax,0506h
    out dx,ax
    mov ax,3140h
    call crtc_write
    mov dx,4ae8h
    mov ax,1
    out dx,ax
    mov ax,0931h
    call crtc_write
    mov ax,103ah
    call crtc_write
    mov ax,0051h
    call crtc_write
    mov ax,0035h
    call crtc_write
    mov ax,006ah
    call crtc_write
    mov ax,0058h
    call crtc_write
    mov ax,0067h
    call crtc_write
    ; Zero CR6A selects the old bank fields. A nonzero CR6A takes priority over contradictory old fields.
    mov ax,0135h
    call crtc_write
    mov ax,0451h
    call crtc_write
    mov byte [0a0000h],0b1h
    mov ax,116ah
    call crtc_write
    mov al,[0a0000h]
    mov [ebp+s3_proof+120],al
    cmp al,0b1h
    jne pm_fault
    mov ax,0f35h
    call crtc_write
    mov ax,016ah
    call crtc_write
    mov byte [0a0000h],0c2h
    mov ax,0035h
    call crtc_write
    mov ax,0051h
    call crtc_write
    mov al,[0a0000h]
    mov [ebp+s3_proof+121],al
    cmp al,0c2h
    jne pm_fault
    ; Every byte is written through the actual shared-bank A0000 aperture before any LFB reads.
    mov dword [ebp+s3_proof+20],1
    xor ebx,ebx
bank_fill:
    call select_bank
    mov edi,0a0000h
    call fill_first
    inc ebx
    cmp ebx,32
    jb bank_fill
    mov ax,1258h
    call crtc_write
    mov dword [ebp+s3_proof+20],2
    xor ebx,ebx
lfb_read_first:
    mov esi,[ebp+s3_proof+220]
    mov eax,ebx
    shl eax,16
    add esi,eax
    call copy_verify_first
    mov [ebp+s3_proof+24],ebx
    call report_readback
    add dword [ebp+s3_proof+212],65536
    inc ebx
    cmp ebx,32
    jb lfb_read_first
    ; The second pattern is written only through the high LFB, then read through every real legacy bank.
    mov dword [ebp+s3_proof+20],3
    xor ebx,ebx
lfb_fill_second:
    mov edi,[ebp+s3_proof+220]
    mov eax,ebx
    shl eax,16
    add edi,eax
    call fill_second
    inc ebx
    cmp ebx,32
    jb lfb_fill_second
    mov ax,0058h
    call crtc_write
    mov dword [ebp+s3_proof+20],4
    xor ebx,ebx
bank_read_second:
    call select_bank
    mov esi,0a0000h
    call copy_verify_second
    mov [ebp+s3_proof+24],ebx
    call report_readback
    add dword [ebp+s3_proof+216],65536
    inc ebx
    cmp ebx,32
    jb bank_read_second
    mov dword [ebp+s3_proof+20],5
    mov ax,1258h
    call crtc_write
    ; A larger BAR resource is distinct from the2MiB LAW. Relocation uses another8MiB-aligned free address.
    mov eax,80001810h
    mov ebx,0e0800000h
    call pci_dword
    mov esi,[ebp+s3_proof+220]
    cmp byte [esi],0ffh
    jne pm_fault
    mov byte [esi],0aah
    mov esi,[ebp+s3_proof+232]
    cmp byte [esi],5bh
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    mov eax,80001810h
    mov ebx,0e0000000h
    call pci_dword
    mov esi,[ebp+s3_proof+232]
    cmp byte [esi],0ffh
    jne pm_fault
    mov esi,[ebp+s3_proof+220]
    cmp byte [esi],5bh
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    ; PCI memory disable blocks both active high LFB and legacy memory without changing stored pixels.
    mov eax,80001804h
    mov ebx,1
    call pci_word
    cmp byte [esi],0ffh
    jne pm_fault
    mov byte [esi],0ach
    cmp byte [0a0000h],0ffh
    jne pm_fault
    mov byte [0a0000h],0adh
    mov eax,80001804h
    mov ebx,3
    call pci_word
    cmp byte [esi],5bh
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    ; Exercise the memory gate again while legacy memory is genuinely enabled, independently from the LAW suppressing it.
    mov ax,0058h
    call crtc_write
    xor ebx,ebx
    call select_bank
    cmp byte [0a0000h],5bh
    jne pm_fault
    mov eax,80001804h
    mov ebx,1
    call pci_word
    cmp byte [0a0000h],0ffh
    jne pm_fault
    mov byte [0a0000h],0afh
    mov eax,80001804h
    mov ebx,3
    call pci_word
    cmp byte [0a0000h],5bh
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    mov ax,1258h
    call crtc_write    ; With IOSE off, ordinary VGA port writes cannot disturb memory-enabled scanout/register state.
    mov eax,80001804h
    mov ebx,2
    call pci_word
    mov dx,3cch
    in al,dx
    cmp al,0ffh
    jne pm_fault
    mov dx,3d4h
    mov ax,0031h
    out dx,ax
    mov eax,80001804h
    mov ebx,3
    call pci_word
    mov al,31h
    call crtc_read
    cmp al,9
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    ; Non-64KiB LFB enable disables the legacy window. A rejected legacy store must leave LFB data unchanged.
    cmp byte [0a0000h],0ffh
    jne pm_fault
    mov byte [0a0000h],0aeh
    cmp byte [esi],5bh
    jne pm_fault
    inc dword [ebp+s3_proof+200]
    call palette
    call mode640
    mov ax,0051h
    call crtc_write
    mov ax,0069h
    call crtc_write
    mov ax,000ch
    call crtc_write
    mov ax,000dh
    call crtc_write
    mov dword [ebp+s3_proof+68],0
    call report_frame
    ; Change both stride and high display start; pixels still come from the complete guest-written second pattern.
    mov ax,5413h
    call crtc_write
    mov ax,0169h
    call crtc_write
    mov ax,400ch
    call crtc_write
    mov ax,000dh
    call crtc_write
    call start_pcm
    fninit
    fld tword [ebp+extended]
    fldpi
tsc_before:
    rdtsc
    mov [ebp+s3_proof+104],eax
    mov [ebp+s3_proof+108],edx
    mov ax,116ah
    call crtc_write
    mov dx,3c8h
    mov al,37h
    out dx,al
    inc dx
    mov al,63
    out dx,al
    mov dx,3dah
    in al,dx
    mov dx,3c0h
    mov al,30h
    out dx,al
    mov dword [ebp+s3_proof+20],6
    mov dword [ebp+s3_proof+208],1
cut_point:
    nop
    ; Consume precisely the saved ATC-data and DAC-component phases, with no index rewrite hiding a lost state.
    mov dx,3c0h
    mov al,1
    out dx,al
    mov dx,3c9h
    mov al,13
    out dx,al
    mov al,29
    out dx,al
    mov dword [ebp+s3_proof+20],7
    mov dword [ebp+s3_proof+68],1
    call report_frame
    ; A second actual extended geometry makes a static640-pixel or fixed-stride renderer fail.
    mov ax,6301h
    call crtc_write
    mov ax,4007h
    call crtc_write
    ; CRTC12's data is57h; use the ordinary literal word below.
    mov ax,5712h
    call crtc_write
    mov ax,6813h
    call crtc_write
    mov ax,8050h
    call crtc_write
    mov ax,0369h
    call crtc_write
    mov ax,400ch
    call crtc_write
    mov ax,000dh
    call crtc_write
    mov dword [ebp+s3_proof+20],8
    mov dword [ebp+s3_proof+68],2
    call report_frame
tsc_after:
    rdtsc
    mov [ebp+s3_proof+112],eax
    mov [ebp+s3_proof+116],edx
    fstp tword [ebp+raw_data+8]
    fstp tword [ebp+raw_data+18]
    fnstcw [ebp+raw_data+28]
    fnstsw [ebp+raw_data+30]
    lea esi,[ebp+raw_data+8]
    lea edi,[ebp+s3_proof+44]
    mov ecx,24
    rep movsb
    ; Keep the owned sample buffer alive until the real DMA1 terminal-count and DSP completion have committed.
    mov ecx,10000000
wait_pcm:
    in al,8
    test al,2
    jnz pcm_completed
    loop wait_pcm
    jmp pm_fault
pcm_completed:
    mov dx,22eh
    in al,dx    ; Explicitly leave ordinary VGA memory organization before invoking the original BIOS/DOS path again.
    mov ax,0058h
    call crtc_write
    mov ax,006ah
    call crtc_write
    mov ax,0035h
    call crtc_write
    mov ax,0051h
    call crtc_write
    mov ax,0069h
    call crtc_write
    mov ax,0031h
    call crtc_write
    mov ax,003ah
    call crtc_write
    mov ax,0050h
    call crtc_write
    mov dx,4ae8h
    xor ax,ax
    out dx,ax
    mov dword [ebp+s3_proof+20],9
    cmp dword [ebp+s3_proof+8],0
    jne return_vcpi
    jmp word REAL_CODE:leave_real
; Select a single shared64KiB bank through the documented new address field.
select_bank:
    mov ax,bx
    mov ah,al
    mov al,6ah
    call crtc_write
    ret
; Fill one complete bank with pattern1 using CPU writes to the selected actual memory target.
fill_first:
    xor edx,edx
.first_row:
    mov eax,ebx
    imul eax,17
    lea eax,[eax+edx*2]
    add eax,edx
    mov ecx,256
.first_byte:
    stosb
    inc al
    loop .first_byte
    inc edx
    cmp edx,256
    jb .first_row
    ret
; Fill one complete LFB bank with a different independently specified byte pattern.
fill_second:
    xor edx,edx
.second_row:
    mov eax,ebx
    imul eax,29
    lea eax,[eax+edx*8]
    sub eax,edx
    add al,5bh
    mov ecx,256
.second_byte:
    stosb
    add al,5
    loop .second_byte
    inc edx
    cmp edx,256
    jb .second_row
    ret
; Copy actual LFB bytes into owned DOS RAM, then compare every byte against pattern1 in the executing guest.
copy_verify_first:
    mov edi,[ebp+s3_proof+16]
    mov ecx,16384
    rep movsd
    mov edi,[ebp+s3_proof+16]
    xor edx,edx
.first_check_row:
    mov eax,ebx
    imul eax,17
    lea eax,[eax+edx*2]
    add eax,edx
    mov ecx,256
.first_check_byte:
    cmp [edi],al
    jne pm_fault
    inc edi
    inc al
    loop .first_check_byte
    inc edx
    cmp edx,256
    jb .first_check_row
    ret
; Copy actual legacy-bank bytes into owned DOS RAM, then compare every byte against pattern2 in the executing guest.
copy_verify_second:
    mov edi,[ebp+s3_proof+16]
    mov ecx,16384
    rep movsd
    mov edi,[ebp+s3_proof+16]
    xor edx,edx
.second_check_row:
    mov eax,ebx
    imul eax,29
    lea eax,[eax+edx*8]
    sub eax,edx
    add al,5bh
    mov ecx,256
.second_check_byte:
    cmp [edi],al
    jne pm_fault
    inc edi
    add al,5
    loop .second_check_byte
    inc edx
    cmp edx,256
    jb .second_check_row
    ret
; This NOP observes only the already completed entire real guest readback.
report_readback:
readback_commit:
    nop
    inc dword [ebp+s3_proof+72]
    ret
; The harness hashes actual scanout pixels only in memory at this exact completed guest configuration.
report_frame:
frame_commit:
    nop
    ret
; Program a concrete640x480 indexed Enhanced mode with640-byte pitch.
mode640:
    mov ax,7911h
    and ah,7fh
    call crtc_write
    mov ax,4f01h
    call crtc_write
    mov ax,0207h
    call crtc_write
    mov ax,0009h
    call crtc_write
    mov ax,0df12h
    call crtc_write
    mov ax,5013h
    call crtc_write
    mov ax,4050h
    call crtc_write
    ret
; Program a complete6-bit RGB ramp through actual DAC writes.
palette:
    mov dx,3c8h
    xor al,al
    out dx,al
    inc dx
    xor ecx,ecx
.palette_entry:
    mov eax,ecx
    and al,63
    out dx,al
    mov eax,ecx
    shr al,2
    out dx,al
    mov eax,ecx
    imul eax,7
    and al,63
    out dx,al
    inc ecx
    cmp ecx,256
    jb .palette_entry
    ret
; Start a genuine512-component ISA-DMA1 CT1740 stream immediately before the snapshot, after the large VRAM loops.
start_pcm:
    mov dx,226h
    mov al,1
    out dx,al
    mov ecx,128
.pcm_reset_delay:
    loop .pcm_reset_delay
    xor al,al
    out dx,al
    mov dx,22eh
.pcm_ready:
    in al,dx
    test al,80h
    jz .pcm_ready
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
    mov dx,22ch
.pcm_command_commit:
    in al,dx
    test al,80h
    jnz .pcm_command_commit
    ret
; Wait for a real DSP command slot before writing one byte.
dsp:
    push eax
    mov dx,22ch
.busy:
    in al,dx
    test al,80h
    jnz .busy
    pop eax
    out dx,al
    ret
; Read one actual CRTC byte through the active color decoder.
crtc_read:
    mov dx,3d4h
    out dx,al
    inc dx
    in al,dx
    ret
; Write one actual adjacent CRTC index/data pair.
crtc_write:
    mov dx,3d4h
    out dx,ax
    ret
; Read one complete actual configuration dword at the caller's BDF/register.
pci_read:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    ret
; Read the one actual BAR, not a cached software address.
pci_read_bar:
    mov eax,80001810h
    jmp pci_read
; Write one complete mechanism-one configuration word.
pci_word:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,ax
    ret
; Write one complete mechanism-one configuration dword.
pci_dword:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,eax
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
    mov dword [ebp+s3_proof+32],0badf00dh
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
    mov eax,[cs:s3_proof+132]
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
    cmp dword [s3_proof+8],0
    jne release_vds
    lgdt [s3_proof+144]
    lidt [s3_proof+152]
release_vds:
    inc dword [s3_proof+228]
    cmp word [s3_proof+178],0
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
    inc word [s3_proof+180]
release_dos:
    mov al,[s3_proof+169]
    out 21h,al
    mov al,[s3_proof+170]
    out 0a1h,al
    mov al,[s3_proof+167]
    out 70h,al
    push dword [s3_proof+128]
    popfd
    mov bx,[file_handle]
    mov ah,3eh
    int 21h
    jc dos_failed
    mov dx,data_file
    mov si,raw_data
    mov cx,32
    call write_file
    mov dword [s3_proof+28],1
    mov dx,proof_file
    mov si,s3_proof
    mov cx,s3_proof_end-s3_proof
    call write_file
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc dos_failed
    push cs
    pop es
    mov ax,3
bios_text:
    int 10h
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
s3_proof db 'S3PROOF1'
    times 248-8 db 0
    db 'S3END001'
s3_proof_end:
raw_data db 'S3RAW001'
    times 24 db 0
extended dq 0fedcba9876543210h
    dw 4001h
proof_file db 'C:\TRIO.DAT',0
data_file db 'C:\S3RAW.DAT',0
done_text db 13,10,'SNAPSHOT-DONE',13,10,'S3-TRIO64-DONE',13,10,0
failed_text db 13,10,'S3-TRIO64-FAILED',13,10,0
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
    times 16384+4095 db 0
server_pt_backup:
    times 4096 db 0
metadata:
    db 'S3META01'
    dw 1,32
    dw s3_proof,s3_proof_end,code_end,program_end,readback_commit,frame_commit,cut_point,pm_entry
    dw real_return,vcpi_enter,vcpi_return,vds_lock,vds_disable,vds_enable,vds_unlock,dds
    dw file_handle,gdt,gdtr,idt,idtr,tss,pd_linear,server_pt_backup,real_stack_top,pm_stack_top
    dw window,raw_data,tsc_before,tsc_after,wait_pcm,pm_fault
program_end:
