; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; DOSEBios: original DOSE firmware. Apache-2.0.
; All BIOS services execute as guest x86 instructions, including ATA I/O.
bits 16
org 0
cpu 486
%include "bios-identity.inc"

%define rAX word [ss:bp-4]
%define rAL byte [ss:bp-4]
%define rAH byte [ss:bp-3]
%define rCX word [ss:bp-8]
%define rCL byte [ss:bp-8]
%define rCH byte [ss:bp-7]
%define rDX word [ss:bp-12]
%define rDL byte [ss:bp-12]
%define rDH byte [ss:bp-11]
%define rBX word [ss:bp-16]
%define rBL byte [ss:bp-16]
%define rBH byte [ss:bp-15]
%define rSI word [ss:bp-28]
%define rDI word [ss:bp-32]
%define rDS word [ss:bp-34]
%define rES word [ss:bp-36]
%define rFLAGS word [ss:bp+6]
%macro enter_service 0
    push bp
    mov bp,sp
    pushad
    push ds
    push es
    cld
    xor ax,ax
    mov ds,ax
%endmacro
%macro leave_service 0
    pop es
    pop ds
    popad
    pop bp
    iret
%endmacro
%macro vector 2
    mov word [%1*4],%2
    mov word [%1*4+2],0xf000
%endmacro

; Executes reference POST and selects the configured boot device using guest instructions.
start:
    cli
    cld
    xor ax,ax
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov sp,0x7c00
%ifdef DOSE_PIIX_RESET
    ; Own early warm policy: CMOS shutdown 0Ah and BDA 1234h jointly authorize
    ; a guest-provided far vector. No POST, DRB programming or hidden DOS context
    ; restoration precedes this jump. Clear the one-shot cookies before transfer.
    in al,0x70
    mov bl,al
    and al,0x80
    or al,0x0f
    out 0x70,al
    in al,0x71
    cmp al,0x0a
    jne .reset_cold
    cmp word [0x472],0x1234
    jne .reset_cold
    xor al,al
    out 0x71,al
    mov word [0x472],0
    mov al,bl
    out 0x70,al
    jmp far [0x467]
.reset_cold:
    mov al,bl
    out 0x70,al
%endif
%ifdef DOSE_TRIO64
    ; Assign the own PCI resource and enable VGA target decoding through real PCI OUTs.
    mov dx,0xcf8
    mov eax,0x80001810
    out dx,eax
    mov dx,0xcfc
    mov eax,0xe0000000
    out dx,eax
    mov dx,0xcf8
    mov eax,0x80001804
    out dx,eax
    mov dx,0xcfc
    mov ax,3
    out dx,ax
    ; Perform the original setup/POS wake sequence before addressing ordinary VGA ports.
    mov dx,0x46e8
    mov al,0x10
    out dx,al
    mov dx,0x0102
    mov al,1
    out dx,al
    mov dx,0x46e8
    mov al,8
    out dx,al
    mov dx,0x4ae8
    xor ax,ax
    out dx,ax
    ; Keep standard legacy VGA active for POST; the guest explicitly enables its later linear window.
    mov dx,0x3c2
    mov al,0x67
    out dx,al
    mov dx,0x3d4
    mov ax,0x4838
    out dx,ax
    mov ax,0xa539
    out dx,ax
    mov ax,0x0058
    out dx,ax
    mov ax,0x0039
    out dx,ax
    mov ax,0x0038
    out dx,ax
    mov dx,0xcf8
    xor eax,eax
    out dx,eax
%endif
%ifdef DOSE_430FX_HOST
    ; Initialize every DRB boundary to the explicit single 32-MiB logical RAM extent.
    ; This executes before the BIOS advertises or accesses extended RAM.
    mov dx,0xcf8
    mov eax,0x80000060
    out dx,eax
    mov dx,0xcfc
    mov eax,0x08080808
    out dx,eax
    mov dx,0xcf8
    mov eax,0x80000064
    out dx,eax
    mov dx,0xcfc
    mov al,8
    out dx,al
    mov dx,0xcf8
    xor eax,eax
    out dx,eax
%endif
%ifdef DOSE_PIIX_IDE
    ; Assign the own board's actual BM I/O resource before enabling target decode.
    mov dx,0xcf8
    mov eax,0x80003920
    out dx,eax
    mov dx,0xcfc
    mov eax,0x0000c001
    out dx,eax
    mov dx,0xcf8
    mov eax,0x80003940
    out dx,eax
    mov dx,0xcfc
    mov eax,0x80008000
    out dx,eax
    mov dx,0xcf8
    mov eax,0x80003870
    out dx,eax
    mov dx,0xcfc
    mov al,0x0f
    out dx,al
    mov dx,0xcf8
    mov eax,0x80003904
    out dx,eax
    mov dx,0xcfc
    mov ax,3
    out dx,ax
    mov dx,0xcf8
    xor eax,eax
    out dx,eax
%endif
    xor di,di
%ifdef DOSE_NETWORK
    ; Only the optional network ROM assigns its two actual RTL8029 slots.
    ; Presence and BAR values are immutable constructor inventory, not host callbacks.
    xor esi,esi
.network_next:
    mov bx,si
    add bx,bx
    movzx ebx,word [cs:network_ports+bx]
    test bx,bx
    jz .network_skip
    mov eax,esi
    shl eax,11
    add eax,080004000h
    mov edi,eax
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    cmp eax,0802910ech
    jne .network_skip
    mov eax,edi
    add eax,10h
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    or eax,1
    out dx,eax
    mov eax,edi
    add eax,3ch
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov al,11
    out dx,al
    mov eax,edi
    add eax,4
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov ax,1
    out dx,ax
.network_skip:
    inc si
    cmp si,2
    jb .network_next
    ; The two physical INTAs share PIRQA; enable the actual level-sensitive route.
    mov dx,0cf8h
    mov eax,080003860h
    out dx,eax
    mov dx,0cfch
    mov al,11
    out dx,al
    mov dx,04d1h
    in al,dx
    or al,8
    out dx,al
    mov dx,0cf8h
    xor eax,eax
    out dx,eax
    xor di,di
%endif
    mov cx,256
.vectors:
    mov ax,iret_stub
    stosw
    mov ax,0xf000
    stosw
    loop .vectors
    vector 0x08,irq_timer
    vector 0x09,irq_keyboard
    vector 0x0e,irq_floppy
    vector 0x10,int_video
    vector 0x11,int_equipment
    vector 0x12,int_memory
    vector 0x13,int_disk
    vector 0x14,int_serial
    vector 0x15,int_system
    vector 0x16,int_keyboard
    vector 0x17,int_printer
    vector 0x18,boot_failure
    vector 0x19,boot_entry
    vector 0x1a,int_time
    vector 0x1e,floppy_parameters
    vector 0x1f,font8_data+1024
    vector 0x43,font_data
    vector 0x41,0xe060
    vector 0x46,0xe070
    vector 0x74,irq_mouse
    vector 0x75,irq_numeric_error
    mov di,0x400
    mov cx,128
    xor ax,ax
    rep stosw
    mov word [0x40e],0x9fc0
    mov word [0x410],0x0020
    mov al,[cs:floppy_count]
    test al,al
    jz .no_floppy_equipment
    dec al
    shl al,6
    or al,0x21
    mov [0x410],al
.no_floppy_equipment:
    cmp byte [cs:mouse_present],0
    je .no_mouse_equipment
    or byte [0x410],4
.no_mouse_equipment:
    cmp byte [cs:gameport_present],0
    je .no_gameport_equipment
    or word [0x410],0x1000
.no_gameport_equipment:
    cmp byte [cs:serial_present],0
    je .no_serial_equipment
    mov word [0x400],0x3f8
    mov byte [0x47c],1
    or word [0x410],0x0200
.no_serial_equipment:
    cmp byte [cs:parallel_present],0
    je .no_parallel_equipment
    mov word [0x408],0x378
    mov byte [0x478],1
    or word [0x410],0x4000
.no_parallel_equipment:
    mov word [0x413],639
    mov word [0x41a],0x1e
    mov word [0x41c],0x1e
    mov word [0x480],0x1e
    mov word [0x482],0x3e
    mov byte [0x449],3
    mov word [0x44a],80
    mov word [0x44c],4000
    mov word [0x460],0x0e0f       ; native underline for the initial 16-scanline text cell
    mov word [0x463],0x3d4
    mov byte [0x484],24
    mov word [0x485],16
    mov byte [0x487],0x60
    mov byte [0x488],0xf9
    mov byte [0x489],0x51
    mov al,[cs:disk_count]
    mov [0x475],al
.has_ide:
    mov ax,0x9fc0
    mov es,ax
    xor di,di
    xor ax,ax
    mov cx,512
    rep stosw
    mov byte [es:0],1
    mov byte [es:0x20],2           ; next text mode uses the 400-scanline family
    ; Program a conventional cascaded PIC and 18.2-Hz timer.
    mov al,0x11
    out 0x20,al
    out 0xa0,al
    mov al,8
    out 0x21,al
    mov al,0x70
    out 0xa1,al
    mov al,4
    out 0x21,al
    mov al,2
    out 0xa1,al
    mov al,1
    out 0x21,al
    out 0xa1,al
    mov al,0xfc
%ifdef DOSE_TRIO64_VBE
    ; Original VPICD inherits the POST mask; an installed FDC has IVT0Eh wired.
    ; Keep the slave cascade open for DOS drivers that unmask IRQ12 directly.
    ; Slave sources remain masked until their drivers enable them.
    pushf
    and al,0xfb
    cmp byte [cs:floppy_count],0
    je .pic_floppy_mask_ready
    and al,0xbf
.pic_floppy_mask_ready:
    popf
%endif
    out 0x21,al
    mov al,0xff
    out 0xa1,al
%ifdef DOSE_TRIO64_VBE
    ; Own Windows-board POST allocation: INTA -> PIRQA -> dedicated IRQ11.
    ; S3 DB014-B 19-5 assigns PCI3C to POST; Intel290550-002 requires ELCR level.
    ; Preserve unrelated ELCR bits, route bytes and the existing PIC masks.
    mov dx,0x4d1
    in al,dx
    or al,8
    out dx,al
    mov dx,0xcf8
    mov eax,0x80003860
    out dx,eax
    mov dx,0xcfc
    mov al,0x0b
    out dx,al
    mov dx,0xcf8
    mov eax,0x8000183c
    out dx,eax
    mov dx,0xcfc
    mov al,0x0b
    out dx,al
    ; Retain the previous cold POST CONFIG_ADDRESS end value, not a CF9 lane write.
    mov dx,0xcf8
    xor eax,eax
    out dx,eax
%endif
    mov al,0x36
    out 0x43,al
    xor al,al
    out 0x40,al
    out 0x40,al
    mov al,2
    out 0x92,al
    mov ax,3
    call set_video_mode
    ; Mode set already installs the native underline for its character height.
    ; Do not overwrite it with the old eight-scanline 06/07 shape here.
    call post_identity
    mov al,0x10
    out 0x80,al
    cmp byte [cs:floppy_count],0
    je .no_floppy_init
    call fdc_reset
    mov si,post_fdc_ready
    jnc .floppy_status
    mov si,post_fdc_warning
.floppy_status:
    call post_message
.no_floppy_init:
    ; CPU hot reset leaves powered controllers intact. POST reinitializes their boot paths.
    cmp dword [cs:disk_sectors],0
    je .no_ide_reset
    mov dx,0x3f6
    mov al,4
    out dx,al
    xor al,al
    out dx,al
    mov dx,0x376
    mov al,4
    out dx,al
    xor al,al
    out dx,al
    mov si,post_ide_ready
    call post_message
.no_ide_reset:
    mov al,0x60
    out 0x64,al
    mov al,0x45
    cmp byte [cs:mouse_present],0
    je .keyboard_command
    or al,0x20
.keyboard_command:
    out 0x60,al
    mov al,0xff
    out 0x60,al
    mov cx,64
.flush_keyboard:
    in al,0x64
    test al,1
    jz .keyboard_ready
    in al,0x60
    loop .flush_keyboard
.keyboard_ready:
    mov si,post_keyboard_ready
    call post_message
    sti
boot_entry:
    xor ax,ax
    mov es,ax
    mov di,0x7c00
    xor eax,eax
    mov cx,1
    cmp byte [cs:boot_drive],0
    je .floppy_boot
    mov dl,0x80
    call read_sectors
    jmp .boot_read
.floppy_boot:
    mov ax,0x0201
    mov bx,0x7c00
    xor dx,dx
    int 0x13
.boot_read:
    jc boot_failure
    cmp word [es:0x7dfe],0xaa55
    jne boot_signature
    mov al,0x20
    out 0x80,al
    xor ax,ax
    mov ds,ax
    mov es,ax
    mov dl,[cs:boot_drive]
    jmp 0:0x7c00
boot_signature:
    push cs
    pop ds
    mov si,signature_error
    call print_string
    jmp boot_halt
boot_failure:
    push cs
    pop ds
    mov si,disk_error
    call print_string
boot_halt:
    cli
    hlt
    jmp boot_halt
iret_stub:
    iret

; EAX=LBA, CX=count, ES:DI=buffer. All commands travel through the IDE device.
; Linear buffers may cross 64-KiB segment windows, including an odd final byte.
read_sectors:
    push bx
    mov bl,0x20
    call transfer_sectors
    pop bx
    ret
; Checks the complete BIOS range before issuing actual ATA sector verification reads.
verify_sectors:
    ; Validate the whole request before the first read; use the dynamic EBDA buffer.
    push ebp
    mov bp,sp
    push ebx
    push ecx
    push edx
    push esi
    push edi
    push es
    call disk_validate_range
    jc .done
    mov esi,eax
    movzx ebx,cx
.sector:
    call bios_ebda_segment
    mov es,ax
    mov di,0x200
    mov eax,esi
    mov cx,1
    mov dl,[ss:bp-12]
    call read_sectors
    jc .done
    inc esi
    dec ebx
    jnz .sector
    clc
.done:
    pop es
    pop edi
    pop esi
    pop edx
    pop ecx
    pop ebx
    pop ebp
    ret

; EAX=LBA, CX=count, DL=dense BIOS drive. Preserve operands, report CF only.
disk_validate_range:
    push ebx
    push edx
    movzx ebx,dl
    sub ebx,0x80
    cmp bl,[cs:disk_count]
    jae .bad
    cmp ebx,4
    jae .bad
    movzx edx,cx
    test edx,edx
    jz .bad
    add edx,eax
    jc .bad
    cmp edx,[cs:disk_capacities+ebx*4]
    ja .bad
    clc
    jmp .done
.bad:
    stc
.done:
    pop edx
    pop ebx
    ret

; DL=BIOS drive -> DX=data port, AL=LBA device/head high bits. BX is preserved.
disk_port:
    push bx
    movzx bx,dl
    sub bx,0x80
    mov al,[cs:disk_routes+bx]
    mov dx,0x1f0
    test al,2
    jz .primary
    mov dx,0x170
.primary:
    and al,1
    shl al,4
    or al,0xe0
    pop bx
    ret
; Selects the actual BIOS write path after range, route and writable-medium checks.
write_sectors:
    push bx
    mov bl,0x30
    call transfer_sectors
    pop bx
    ret
; Transfers a checked LBA range through the selected actual ATA channel and device.
transfer_sectors:
    push ebp
    mov bp,sp
    push ebx
    push edx
    push esi
    push cx
    sub sp,2                    ; words: remaining sectors in this ATA command
    mov esi,eax
    call disk_validate_range
    jc .bad
.command:
    mov cx,[ss:bp-14]
    cmp cx,256
    jbe .command_count
    mov cx,256
.command_count:
    mov [ss:bp-16],cx
    mov dl,[ss:bp-8]
    call disk_port
    add dx,6
    mov ebx,esi
    shr ebx,24
    and bl,15
    or al,bl
    out dx,al
    sub dx,4
    mov al,cl                   ; ATA sector count zero means 256 sectors
    out dx,al
    inc dx
    mov eax,esi
    out dx,al
    inc dx
    shr eax,8
    out dx,al
    inc dx
    shr eax,8
    out dx,al
    add dx,2
    mov al,[ss:bp-4]
    out dx,al
.sector:
    mov ebx,100000
.wait:
    in al,dx
    test al,al
    jz .bad
    test al,0x80
    jnz .retry
    test al,1
    jnz .bad
    test al,8
    jnz .transfer
.retry:
    dec ebx
    jnz .wait
    jmp .bad
.transfer:
    sub dx,7
    mov cx,256
    cmp byte [ss:bp-4],0x30
    je .write_sector
    ; Only an odd start beyond FE00h can split a word in this 512-byte sector.
    ; Keep the ordinary aligned/noncrossing word loop free of per-word checks.
    cmp di,0xfe00
%ifdef DOSE_APM
    jb .read_bulk
%endif
    jbe .read_words
    test di,1
    jnz .read_boundary_words
%ifdef DOSE_APM
    jmp .read_words
.read_bulk:
    pushf
    pop ax
    test ax,0x400
    jnz .read_words
    rep insw
    jmp .data_done
%endif
.read_words:
    in ax,dx
    mov [es:di],ax
    add di,2
    jnc .read_next
    mov ax,es
    add ax,0x1000
    mov es,ax
.read_next:
    loop .read_words
    jmp .data_done
.read_boundary_words:
    in ax,dx
    cmp di,0xffff
    je .read_split_word
    mov [es:di],ax
    add di,2
    loop .read_boundary_words
    jmp .data_done
.read_split_word:
    ; The ATA grant is still one word. Split only the two guest memory bytes
    ; before a word access could fault at offset FFFFh; preserve AH across ES.
    mov [es:di],al
    xor di,di
    mov bx,es
    add bx,0x1000
    mov es,bx
    mov [es:di],ah
    inc di
    loop .read_words             ; remaining words in this sector cannot cross
    jmp .data_done
.write_sector:
    cmp di,0xfe00
%ifdef DOSE_APM
    jb .write_bulk
%endif
    jbe .write_words
    test di,1
    jnz .write_boundary_words
%ifdef DOSE_APM
    jmp .write_words
.write_bulk:
    pushf
    pop ax
    test ax,0x400
    jnz .write_words
    mov ebx,esi
    mov si,di
    es rep outsw
    mov di,si
    mov esi,ebx
    jmp .data_done
%endif
.write_words:
    mov ax,[es:di]
    out dx,ax
    add di,2
    jnc .write_next
    mov ax,es
    add ax,0x1000
    mov es,ax
.write_next:
    loop .write_words
    jmp .data_done
.write_boundary_words:
    cmp di,0xffff
    je .write_split_word
    mov ax,[es:di]
    out dx,ax
    add di,2
    loop .write_boundary_words
    jmp .data_done
.write_split_word:
    ; Read both bytes before sending the single ATA word; ES carries with the
    ; same segment-wrap policy as the established even-addressed transfer path.
    mov al,[es:di]
    xor di,di
    mov bx,es
    add bx,0x1000
    mov es,bx
    mov ah,[es:di]
    out dx,ax
    inc di
    loop .write_words
.data_done:
    ; A WRITE only completes after the device has committed the full sector.
    ; Wait before issuing a new command, including the last sector of a BIOS call.
    add dx,7
    mov ebx,100000
.complete:
    in al,dx
    test al,1
    jnz .bad
    test al,0x80
    jz .completed
    dec ebx
    jnz .complete
    jmp .bad
.completed:
    inc esi
    dec word [ss:bp-14]
    dec word [ss:bp-16]
    jnz .sector
    cmp word [ss:bp-14],0
    jne .command
    clc
    jmp .done
.bad:
    stc
.done:
    lea sp,[bp-14]              ; discard the local without destroying the result CF
    pop cx
    pop esi
    pop edx
    pop ebx
    pop ebp
    ret

; Compile the same CHS translation for normal and bufferless verify services,
; without adding per-sector branches to ordinary ATA read/write transfers.
%macro fixed_chs_request 1
    movzx ebx,rDL
    sub ebx,0x80
    movzx eax,rDH
    cmp al,[cs:disk_heads+ebx]
    jae %1
    movzx eax,rCH
    movzx edx,rCL
    and edx,0xc0
    shl edx,2
    or eax,edx
    movzx edx,byte [cs:disk_heads+ebx]
    imul eax,edx
    movzx edx,rDH
    add eax,edx
    imul eax,eax,63
    movzx edx,rCL
    and edx,63
    test edx,edx
    jz %1
    dec edx
    add eax,edx
    movzx cx,rAL
%endmacro
; Dispatches implemented INT13 HDD services using dense BIOS numbers and actual ATA port transfers.
int_disk:
    cmp dl,2
    jb int_floppy
    enter_service
    and rFLAGS,0xfffe
    mov ah,rAH
    movzx ebx,rDL
    sub ebx,0x80
    cmp ebx,4
    jae .unsupported
    cmp bl,[cs:disk_count]
    jae .unsupported
    cmp ah,0
    je .success
    cmp ah,1
    je .status
    cmp ah,2
    je .chs
    cmp ah,3
    je .chs
    cmp ah,8
    je .geometry
    cmp ah,0x15
    je .type
    cmp ah,0x41
    je .extensions
    cmp ah,0x42
    je .extended_read
    cmp ah,0x43
    je .extended_read
    cmp ah,0x48
    je .parameters
    cmp ah,4
    je .verify_chs
.unsupported:
    mov rAH,1
    jmp .error
.write_protected:
    mov rAH,3
    jmp .error
.status:
    mov al,[0x474]
    mov rAH,al
    test al,al
    jnz .error
    jmp .done
.chs:
    fixed_chs_request .unsupported
    mov es,rES
    mov di,rBX
    jmp .read
.extended_read:
    mov ds,rDS
    mov si,rSI
    ; Validate the implemented 16-byte DAP prefix before its first byte read.
    cmp si,0xfff0
    ja .unsupported
    cmp byte [si],16
    jb .unsupported
    cmp dword [si+12],0
    jne .unsupported
    mov eax,[si+8]
    mov cx,[si+2]
    mov di,[si+4]
    mov es,[si+6]
.read:
    mov dl,rDL
    cmp rAH,3
    je .write
    cmp rAH,0x43
    je .write
    call read_sectors
    jc .read_error
    jmp .success
.write:
    movzx ebx,rDL
    sub ebx,0x80
    cmp byte [cs:disk_write_flags+ebx],0
    je .write_protected
    call write_sectors
    jc .read_error
    jmp .success
.verify_chs:
    fixed_chs_request .unsupported
    mov dl,rDL
    call verify_sectors
    jc .read_error
    jmp .success
.read_error:
    mov rAH,4
    jmp .error
.geometry:
    mov eax,[cs:disk_capacities+ebx*4]
    xor edx,edx
    movzx ecx,byte [cs:disk_heads+ebx]
    imul ecx,ecx,63
    div ecx
    cmp eax,1
    jae .geometry_nonzero
    mov eax,1
.geometry_nonzero:
    dec eax
    cmp eax,1023
    jbe .geometry_limit
    mov eax,1023
.geometry_limit:
    mov ch,al
    shr ax,2
    and al,0xc0
    or al,63
    mov cl,al
    mov rCX,cx
    mov al,[cs:disk_heads+ebx]
    dec al
    mov rDH,al
    mov al,[cs:disk_count]
    mov rDL,al
    jmp .success
.type:
    mov eax,[cs:disk_capacities+ebx*4]
    mov rDX,ax
    shr eax,16
    mov rCX,ax
    mov rAH,3
    jmp .done
.extensions:
    cmp rBX,0x55aa
    jne .unsupported
    mov rBX,0xaa55
    mov rCX,1
    mov rAH,0x30
    jmp .done
.parameters:
    mov ds,rDS
    mov si,rSI
    ; The input length and all 26 returned bytes must fit this real-mode segment.
    cmp si,0xffe6
    ja .unsupported
    cmp word [si],26
    jb .unsupported
    mov word [si],26
    mov word [si+2],2
    mov eax,[cs:disk_capacities+ebx*4]
    xor edx,edx
    movzx ecx,byte [cs:disk_heads+ebx]
    mov [si+8],ecx
    imul ecx,ecx,63
    div ecx
    mov [si+4],eax
    mov dword [si+12],63
    mov eax,[cs:disk_capacities+ebx*4]
    mov [si+16],eax
    mov dword [si+20],0
    mov word [si+24],512
.success:
    mov rAH,0
    xor ax,ax
    mov ds,ax
    mov byte [0x474],0
    jmp .done
.error:
    or rFLAGS,1
    mov al,rAH
    xor dx,dx
    mov ds,dx
    mov [0x474],al
.done:
    leave_service

%include "floppy-bios.inc"

clear_screen:
    push ax
    push cx
    push di
    push es
    mov ax,0xb800
    mov es,ax
    xor di,di
    mov cx,0x4000                ; initialize all eight mode-03h text pages
    mov ax,0x0720
    rep stosw
    pop es
    pop di
    pop cx
    pop ax
    ret

; Prints a zero-terminated ROM string through the reference video teletype service.
print_string:
    lodsb
    test al,al
    jz .done
    mov ah,0x0e
    int 0x10
    jmp print_string
.done:
    ret

%include "video-text-bios.inc"
%include "video-graphics-bios.inc"

; Dispatches guest video BIOS services without a host BIOS shortcut.
int_video:
    enter_service
    mov ah,rAH
    cmp ah,0x4f
    je .vbe
    cmp ah,0
    je .mode
    cmp ah,1
    je .shape
    cmp ah,2
    je .cursor_set
    cmp ah,3
    je .cursor_get
    cmp ah,5
    je .page
    cmp ah,6
    je .scroll_up
    cmp ah,7
    je .scroll_down
    cmp ah,8
    je .character_read
    cmp ah,9
    je .character_write
    cmp ah,10
    je .character_write
    cmp ah,0x0c
    je .pixel
    cmp ah,0x0d
    je .pixel
    cmp ah,0x0e
    je .tty
    cmp ah,0x0f
    je .mode_get
    cmp ah,0x10
    je .palette
    cmp ah,0x11
    je .font
    cmp ah,0x12
    je .ega
    cmp ah,0x13
    je .string
    cmp ah,0x1a
    je .display
    cmp ah,0x1b
    je .video_state
    jmp .done
.vbe:
    call video_vbe
    jmp .done
.mode:
    mov al,rAL
    call set_video_mode
    jmp .done
.pixel:
    call video_pixel
    jmp .done
.palette:
%ifdef DOSE_TRIO64_VBE
    call tvbe_attribute_palette
%else
    call video_palette
%endif
    jmp .done
.shape:
    mov cx,rCX
    call video_cursor_shape
    jmp .done
.cursor_set:
    mov dx,rDX
    mov al,rBH
    call video_cursor_position
    jc .done
    mov [si],dx
    mov al,rBH
    cmp al,[0x462]
    jne .done
    call hardware_cursor
    jmp .done
.cursor_get:
    mov al,rBH
    call video_cursor_page
    jc .done
    mov dx,[si]
    mov cx,[0x460]
    mov rDX,dx
    mov rCX,cx
    jmp .done
.page:
    mov al,rAL
    call video_text_select_page
    jmp .done
.string:
    cmp byte [0x449],3
    jne .graphics_string
    call video_text_string
    jmp .done
.graphics_string:
    call video_graphics_string
    jmp .done
.tty:
    cmp byte [0x449],3
    jne .graphics_tty
    mov al,rAL
    call teletype
    jmp .done
.graphics_tty:
    call video_graphics_tty
    jmp .done
.mode_get:
    mov al,[0x449]
    mov ah,[0x44a]
    mov rAX,ax
    mov al,[0x462]
    mov rBH,al
    jmp .done
.font:
    call video_font
    jmp .done
.ega:
    cmp rBL,0x34
    je .cursor_emulation
    cmp rBL,0x30
    je .scanlines
    cmp rBL,0x10
    jne .done
    mov rBX,3
    mov rCX,0
    jmp .done
.scanlines:
    call video_select_scanlines
    jmp .done
.cursor_emulation:
    call video_cursor_emulation
    jmp .done
.display:
    mov rAL,0x1a
    mov rBX,8
    jmp .done
.video_state:
    call video_functionality
    jmp .done
.character_read:
    cmp byte [0x449],3
    jne .graphics_character_read
    mov al,rBH
    call video_text_page
    jc .done
    mov dx,[si]
    mov al,rBH
    call video_text_position
    jc .done
    mov ax,0xb800
    mov es,ax
    mov ax,[es:di]
    mov rAX,ax
    jmp .done
.graphics_character_read:
    call video_graphics_character_read
    jmp .done
.character_write:
    cmp byte [0x449],3
    jne .graphics_character_write
    mov al,rBH
    call video_text_page
    jc .done
    mov dx,[si]
    mov al,rBH
    call video_text_position
    jc .done
    movzx ax,byte [0x484]
    inc ax
    imul ax,[0x44a]
    shl ax,1
    add ax,bx
    mov si,ax                    ; visible end, excluding inter-page padding
    mov ax,0xb800
    mov es,ax
    mov al,rAL
    mov ah,rBL
    mov cx,rCX
.write_loop:
    test cx,cx
    jz .done
    cmp di,si
    jae .done
    mov [es:di],al
    cmp rAH,10
    je .write_next
    mov [es:di+1],ah
.write_next:
    add di,2
    loop .write_loop
    jmp .done
.graphics_character_write:
    call video_graphics_character_write
    jmp .done
.scroll_up:
.scroll_down:
.scroll:
    cmp byte [0x449],3
    jne .graphics_scroll
    mov al,[0x462]
    call video_text_page
    jc .done
    mov cx,rCX
    mov dx,rDX
    ; ANSI.SYS may supply the row count, rather than the last row, for CLS.
    ; Intersect the lower/right edge with the visible page before validating
    ; the window. Never let a clipped request spill into padding/other pages.
    cmp dh,[0x484]
    jbe .scroll_row_bounded
    mov dh,[0x484]
.scroll_row_bounded:
    cmp dl,[0x44a]
    jb .scroll_column_bounded
    mov dl,[0x44a]
    dec dl
.scroll_column_bounded:
    cmp ch,dh
    ja .done
    cmp cl,dl
    ja .done
    movzx si,rBH
    mov al,rAL
    mov ah,rAH
    sub ah,6
    call video_text_scroll_region
    jmp .done
.graphics_scroll:
    call video_graphics_scroll
.done:
    leave_service

%include "video-bios.inc"
%ifdef DOSE_TRIO64
%include "trio64-bios.inc"
%endif
%ifdef DOSE_TRIO64_VBE
%include "video-trio64-vbe-bios.inc"
%else
%include "video-vbe-bios.inc"
%endif
%include "video-font-bios.inc"
%include "speaker-bios.inc"
%include "serial-bios.inc"
%include "printer-bios.inc"

; Returns the reference equipment word from the live BIOS data area.
int_equipment:
    push bp
    mov bp,sp
    push ds
    xor ax,ax
    mov ds,ax
    mov ax,[0x410]
    pop ds
    pop bp
    iret
; Returns the current conventional-memory size from the live BIOS data area.
int_memory:
    push ds
    xor ax,ax
    mov ds,ax
    mov ax,[0x413]
    pop ds
    iret

; Memory managers may relocate the EBDA and change conventional RAM size.
; All run-time EBDA users read the authoritative BDA pointer, preserving DS.
bios_ebda_segment:
    push ds
    xor ax,ax
    mov ds,ax
    mov ax,[0x40e]
    pop ds
    ret
; AT-compatible external x87 error entry. The owner of INT 02h handles the
; numeric state. Port F0 releases a pending WAIT before that handler executes.
irq_numeric_error:
    push ax
    xor al,al
    out 0xf0,al
    mov al,0x20
    out 0xa0,al
    out 0x20,al
    int 2
    pop ax
    iret

; Updates the live BIOS tick count and acknowledges the real timer interrupt.
irq_timer:
    pushad
    push ds
    xor ax,ax
    mov ds,ax
    inc dword [0x46c]
    cmp dword [0x46c],0x1800b0
    jb .no_rollover
    mov dword [0x46c],0
    inc byte [0x470]
.no_rollover:
    int 0x1c
    mov al,0x20
    out 0x20,al
    pop ds
    popad
    iret

; Reads actual KBC keyboard data and updates the BIOS key queue before PIC acknowledgement.
irq_keyboard:
    pushad
    push ds
    xor ax,ax
    mov ds,ax
    in al,0x64
    and al,0x21
    cmp al,1
    jne .irq_done
    call keyboard_read
.irq_done:
    mov al,0x20
    out 0x20,al
    pop ds
    popad
    iret

; Shared by IRQ1 and the polled mouse transaction path. Never acknowledges PIC.
; Preserves queued keyboard input while the Aux service waits for its own bytes.
keyboard_read:
    in al,0x60
    mov bl,al
    and bl,0x7f
    cmp bl,0x2a
    je .left_shift
    cmp bl,0x36
    je .right_shift
    cmp bl,0x1d
    je .control
    cmp bl,0x38
    je .alt
    test al,0x80
    jnz .done
    cmp al,0x3a
    jne .character
    xor byte [0x417],0x40
    jmp .done
.left_shift:
    mov dl,2
    jmp .modifier
.right_shift:
    mov dl,1
    jmp .modifier
.control:
    mov dl,4
    jmp .modifier
.alt:
    mov dl,8
.modifier:
    test al,0x80
    jnz .release
    or [0x417],dl
    jmp .done
.release:
    not dl
    and [0x417],dl
    jmp .done
.character:
    cmp al,0x53
    jne .translate
    mov ah,[0x417]
    and ah,0x0c
    cmp ah,0x0c
    jne .translate
    mov word [0x472],0x1234
    mov al,0xfe
    out 0x64,al                  ; real KBC reset pulse, no host reboot shortcut
    ret
.translate:
    movzx bx,al
    mov ah,al
    mov al,[cs:ascii_lower+bx]
    test byte [0x417],3
    jz .caps
    mov al,[cs:ascii_upper+bx]
.caps:
    test byte [0x417],0x40
    jz .ctrl
    ; Caps toggles alphabetic keys after Shift, leaving digits/punctuation alone.
    mov dl,[cs:ascii_lower+bx]
    cmp dl,'a'
    jb .ctrl
    cmp dl,'z'
    ja .ctrl
    xor al,32
.ctrl:
    test byte [0x417],4
    jz .enqueue
    and al,0x1f
.enqueue:
    mov bx,[0x41c]
    mov dx,bx
    add dx,2
    cmp dx,0x3e
    jb .tail_ready
    mov dx,0x1e
.tail_ready:
    cmp dx,[0x41a]
    je .done
    mov [bx+0x400],ax
    mov [0x41c],dx
.done:
    ret

; Implements the reference keyboard BIOS queue/status services.
int_keyboard:
    enter_service
    mov ah,rAH
    cmp ah,0
    je .get
    cmp ah,0x10
    je .get
    cmp ah,1
    je .peek
    cmp ah,0x11
    je .peek
    cmp ah,2
    je .shift
    cmp ah,0x12
    je .shift
    cmp ah,5
    je .store
    jmp .done
.get:
    mov bx,[0x41a]
    cmp bx,[0x41c]
    jne .get_ready
    sti
    hlt
    cli
    jmp .get
.get_ready:
    mov ax,[bx+0x400]
    mov rAX,ax
    add bx,2
    cmp bx,0x3e
    jb .head_ready
    mov bx,0x1e
.head_ready:
    mov [0x41a],bx
    jmp .done
.peek:
    or rFLAGS,0x40
    mov bx,[0x41a]
    cmp bx,[0x41c]
    je .done
    mov ax,[bx+0x400]
    mov rAX,ax
    and rFLAGS,0xffbf
    jmp .done
.shift:
    mov ax,[0x417]
    mov rAX,ax
    jmp .done
.store:
    mov bx,[0x41c]
    mov dx,bx
    add dx,2
    cmp dx,0x3e
    jb .store_tail
    mov dx,0x1e
.store_tail:
    mov rAL,1
    cmp dx,[0x41a]
    je .done
    mov ax,rCX
    mov [bx+0x400],ax
    mov [0x41c],dx
    mov rAL,0
.done:
    leave_service

; Implements reference timer/RTC BIOS services using the live tick counter and RTC ports.
int_time:
%ifdef DOSE_PCI_BIOS
    cmp ah,0b1h
    je pci_bios_int1a
%endif
    enter_service
    and rFLAGS,0xfffe
    cmp rAH,0
    jne .not_ticks
    mov dx,[0x46c]
    mov cx,[0x46e]
    mov al,[0x470]
    mov rDX,dx
    mov rCX,cx
    mov rAL,al
    mov byte [0x470],0
    jmp .done
.not_ticks:
    cmp rAH,1
    jne .not_set_ticks
    mov dx,rDX
    mov cx,rCX
    mov [0x46c],dx
    mov [0x46e],cx
    mov byte [0x470],0          ; setting time also consumes the old midnight indication
    jmp .done
.not_set_ticks:
    cmp rAH,2
    je .clock
    cmp rAH,4
    je .date
    or rFLAGS,1
    jmp .done
.clock:
    mov al,4
    out 0x70,al
    in al,0x71
    mov rCH,al
    mov al,2
    out 0x70,al
    in al,0x71
    mov rCL,al
    xor al,al
    out 0x70,al
    in al,0x71
    mov rDH,al
    mov rDL,0
    jmp .done
.date:
    mov al,0x32
    out 0x70,al
    in al,0x71
    mov rCH,al
    mov al,9
    out 0x70,al
    in al,0x71
    mov rCL,al
    mov al,8
    out 0x70,al
    in al,0x71
    mov rDH,al
    mov al,7
    out 0x70,al
    in al,0x71
    mov rDL,al
.done:
    leave_service

%include "gameport-bios.inc"

; Dispatches implemented system/memory BIOS services, including the actual protected block-move path.
int_system:
%ifdef DOSE_APM
    cmp ah,0x53
    je apm_real
%endif
    enter_service
    and rFLAGS,0xfffe
    mov ah,rAH
    cmp ah,0x87
    je int_block_move
    cmp ah,0x88
    je .extended_memory
    cmp rAX,0xe801
    je .e801
    cmp ah,0xc0
    je .configuration
    cmp ah,0xc1
    je .ebda
    cmp ah,0x24
    je .a20
    cmp ah,0xc2
    je int_ps2_service
    cmp ah,0x84
    je int_gameport
.unsupported:
    mov rAH,0x86
    or rFLAGS,1
    jmp .done
.extended_memory:
    mov ax,[cs:extended_kib]
    mov rAX,ax
    jmp .done
.e801:
    mov ax,[cs:extended_kib]
    mov rAX,ax
    mov rCX,ax
    mov rBX,0
    mov rDX,0
    jmp .done
.configuration:
    mov rES,0xf000
    mov rBX,system_configuration
    mov rAH,0
    jmp .done
.ebda:
    call bios_ebda_segment
    mov rES,ax
    jmp .done
.a20:
    mov al,rAL
    cmp al,0
    je .disable_a20
    cmp al,1
    je .enable_a20
    cmp al,2
    je .query_a20
    cmp al,3
    jne .unsupported
    mov rBX,3
    jmp .a20_success
.disable_a20:
    xor al,al
    out 0x92,al
    jmp .a20_success
.enable_a20:
    mov al,2
    out 0x92,al
    jmp .a20_success
.query_a20:
    in al,0x92
    shr al,1
    and al,1
    mov rAL,al
.a20_success:
    mov rAH,0
.done:
    leave_service

%ifdef DOSE_APM
%include "apm-bios.inc"
%endif
%include "mouse-bios.inc"
%include "memory-bios.inc"
%include "post-bios.inc"
%ifdef DOSE_PCI_BIOS
%include "pci-bios.inc"
bits 16
%endif

disk_error: db 13,10,'BOOT ERROR: no readable boot sector on selected device.',13,10,0
signature_error: db 13,10,'BOOT ERROR: sector has no 55 AA signature.',13,10,0
system_configuration: dw 8
    db 0xfc,0,1,0x70,0,0,0,0
disk_parameters: dw 65
    db 16
    dw 0
    db 0,0,0,0,0,0,0,0,0,63,0
ascii_lower:
    db 0,27,'1234567890-=',8,9,'qwertyuiop[]',13,0,'asdfghjkl;',39,96,0,92,'zxcvbnm,./',0,'*',0,' '
    times 128-($-ascii_lower) db 0
ascii_upper:
    db 0,27,'!@#$%^&*()_+',8,9,'QWERTYUIOP{}',13,0,'ASDFGHJKL:',34,126,0,124,'ZXCVBNM<>?',0,'*',0,' '
    times 128-($-ascii_upper) db 0
times 0xa000-($-$$) db 0xff
font8_data: times 2048 db 0
font14_data: times 3584 db 0
times 0xc000-($-$$) db 0xff
font_data: times 4096 db 0
times 0xe000-($-$$) db 0xff
extended_kib: dw 7168
disk_sectors: dd 65536
disk_writable: db 0
floppy_types: db 0,0
boot_drive: db 0x80
floppy_spt: db 0,0
floppy_rates: db 0,0
floppy_count: db 0
mouse_present: db 0
et4000_present: db 0
installed_ram_kib: dd 8192
gameport_present: db 0
serial_present: db 0
parallel_present: db 0
midi_port: dw 0
midi_irq: db 0
times 0xe020-($-$$) db 0xff
floppy_parameters: db 0xdf,2,0x25,2,18,0x1b,0xff,0x6c,0xf6,15,8
times 0xe030-($-$$) db 0xff
floppy_parameters_b: db 0xdf,2,0x25,2,18,0x1b,0xff,0x6c,0xf6,15,8
times 0xe040-($-$$) db 0xff
; Dense BIOS drive descriptors; host configuration patches these immutable ROM fields.
disk_capacities: dd 65536,0,0,0
disk_write_flags: db 0,0,0,0
disk_count: db 1
times 0xe058-($-$$) db 0
disk_routes: db 0,1,2,3
disk_heads: db 16,16,16,16
disk_tables: times 32 db 0

%ifdef DOSE_NETWORK
times 0xe0a0-($-$$) db 0xff
network_ports: dw 0,0
%endif
times 0xe100-($-$$) db 0xff
vga_planar_palette: times 768 db 0
vga_indexed_palette: times 768 db 0
%ifdef DOSE_PCI_BIOS
; Industry-standard simulated INT1A entry, physical 000FFE6Eh, for this variant only.
times 0xfe6e-($-$$) db 0xff
    jmp int_time
%endif
times 0xfff0-($-$$) db 0xff
    jmp 0xf000:0
times 0xfff5-($-$$) db 0xff
    db BIOS_DATE,0,0xfc,0
