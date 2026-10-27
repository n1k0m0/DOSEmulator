; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original 486/VDS/FDC client. Only private scratch drive B is modified.
; Calls the resident manager's INT4B API, never a host DMA/BIOS shortcut.
bits 16
cpu 486
org 0x100
%ifndef EMS
%define EMS 0
%endif

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cli
    mov ax,cs
    mov ss,ax
    mov sp,stack_end
    sti
    mov ds,ax
    mov es,ax
    cld
    mov bx,(program_end-$$+0x100+15)/16
    mov ah,0x4a
    int 0x21
    jc failed
    mov dword [proof+24],1
    xor ax,ax
    mov es,ax
    mov al,[es:0x47b]
    test al,0x20
    jz failed
    mov ax,[es:0x40e]
    mov [proof+64],ax
    push cs
    pop es
    mov ax,0x8102
    xor dx,dx
    int 0x4b
    jc failed
    mov [proof+28],ax
    mov [proof+30],bx
    mov [proof+32],cx
    mov [proof+34],dx
    mov [proof+36],si
    shl dword [proof+36],16
    mov [proof+36],di
    cmp ax,0x0100
    jne failed
    or dword [proof+16],1
    ; A rejected request must not consume a buffer or change disable counts.
    mov ax,0x8102
    mov dx,1
    int 0x4b
    jnc failed
    cmp al,0x10
    jne failed
    or dword [proof+16],2
    mov dword [proof+24],2
    mov bx,0x2000
    mov ah,0x48
    int 0x21
    jc failed
    mov [allocation],ax
    mov [proof+40],ax
    ; Select a 512-byte region straddling a physical 64-KiB boundary.
    movzx eax,ax
    shl eax,4
    mov edx,eax
    add eax,0x10000
    and eax,0xffff0000
    sub eax,16
    sub eax,edx
    mov [proof+44],eax
    mov [dds+4],eax
    mov ax,[allocation]
    mov [dds+8],ax
    mov word [record],proof+80
    mov byte [sector],1
    call exercise_region
    jc failed
    mov di,proof+256
    call capture_region
    or dword [proof+16],4

%if EMS
    mov dword [proof+24],3
    mov ah,0x41
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+48],bx
    mov ah,0x42
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+52],bx
    mov bx,2
    mov ah,0x43
    int 0x67
    test ah,ah
    jnz failed
    mov [handle],dx
    mov [proof+50],dx
    mov byte [ems_allocated],1
    mov ah,0x47
    int 0x67
    test ah,ah
    jnz failed
    mov byte [map_saved],1
    mov ax,0x4400
    mov bx,1
    int 0x67
    test ah,ah
    jnz failed
    mov ax,0x4401
    xor bx,bx
    mov dx,[handle]
    int 0x67
    test ah,ah
    jnz failed
    mov cx,[proof+48]
    shr cx,8
    add cx,3
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+56],edx
    inc cx
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+60],edx
    sub edx,[proof+56]
    cmp edx,4096
    je failed                 ; this case must really be noncontiguous
    mov ax,[proof+48]
    mov [dds+8],ax
    mov dword [dds+4],0x3ff0
    mov word [record],proof+112
    mov byte [sector],2
    call exercise_region
    jc failed
    mov di,proof+768
    call capture_region
    or dword [proof+16],8
    call release_ems
    jc failed
    mov ah,0x42
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+54],bx
    cmp bx,[proof+52]
    jne failed
    or dword [proof+16],16
%endif

    ; Format a separate track using a CHRN list crossing a caller segment end.
    mov dword [proof+24],4
    mov ax,[allocation]
    mov es,ax
    mov di,0xfffe
    mov bx,1
.ids:
    mov al,3
    call put_byte
    mov al,1
    call put_byte
    mov al,bl
    call put_byte
    mov al,2
    call put_byte
    inc bx
    cmp bx,19
    jb .ids
    mov ax,[allocation]
    mov es,ax
    mov bx,0xfffe
    mov cx,0x0301
    mov dx,0x0101
    mov ax,0x0512
    int 0x13
    jc failed
    inc dword [proof+168]
    ; Reset DPT comes from the firmware: this track must contain F6 fill.
    or dword [proof+16],32
    call release_dos
    jc failed
    or dword [proof+16],64
    mov dword [proof+12],1
    jmp report

; DDS's virtual region is the caller buffer. VDS returns a distinct physical
; bounce buffer. Disable translation only around actual DMA2 hardware access.
exercise_region:
    call fill_region
    mov byte [operation],0x45
    call vds_transfer
    jc .bad
    call clear_region
    mov byte [operation],0x46
    call vds_transfer
    jc .bad
    call check_region
    jc .bad
    ; Also exercise the firmware's own CPU copy/bounce path on this mapping.
    mov byte [operation],3
    call bios_transfer
    jc .bad
    call clear_region
    mov byte [operation],2
    call bios_transfer
    jc .bad
    call check_region
    jc .bad
    mov byte [operation],4
    call bios_transfer
    ret
.bad:
    stc
    ret

; Locks the probe-owned buffer and balances actual VDS channel/transfer/unlock services.
vds_transfer:
    mov dword [dds],512
    mov word [dds+10],0
    push cs
    pop es
    mov di,dds
    mov dx,0x18                ; no remap; enforce 64-KiB alignment
    cmp byte [operation],0x45
    jne .lock
    or dx,2                   ; copy the caller's data before DMA read
.lock:
    mov ax,0x8103
    int 0x4b
    jc .bad
    mov byte [locked],1
    cmp word [dds+10],0
    je .bad                   ; both test regions must need real buffering
    mov si,dds
    mov di,[record]
    mov cx,8
    rep movsw
    add word [record],16
    mov bx,2
    xor dx,dx
    mov ax,0x810b
    int 0x4b
    jc .bad
    mov byte [disabled],1
    inc dword [proof+152]
    call hardware_transfer
    jc .bad
    mov bx,2
    xor dx,dx
    mov ax,0x810c
    int 0x4b
    jc .bad
    mov byte [disabled],0
    inc dword [proof+156]
    push cs
    pop es
    mov di,dds
    xor dx,dx
    cmp byte [operation],0x46
    jne .unlock
    mov dx,2                   ; copy actual DMA-written data to virtual region
.unlock:
    mov ax,0x8104
    int 0x4b
    jc .bad
    mov byte [locked],0
    inc dword [proof+148]
    clc
    ret
.bad:
    stc
    ret

; Executes the requested real BIOS floppy transfer using this probe's own buffer.
bios_transfer:
    mov ax,[dds+8]
    mov es,ax
    mov bx,[dds+4]
    mov cx,0x0200
    mov cl,[sector]
    add cl,2
    mov dx,1
    mov ah,[operation]
    mov al,1
    int 0x13
    jc .bad
    cmp byte [operation],3
    jne .read
    inc dword [proof+160]
.read:
    cmp byte [operation],2
    jne .done
    inc dword [proof+164]
.done:
    clc
.bad:
    ret

; Own port driver; IRQ6 remains owned by the original BIOS/EMM386 monitor.
hardware_transfer:
    mov ax,0
    mov dx,1
    int 0x13
    jc .bad
    mov dx,0x3f7
    xor al,al
    out dx,al
    mov dx,0x3f2
    mov al,0x2d
    out dx,al
    mov al,0x0f
    call send
    jc .bad
    mov al,1
    call send
    mov al,2
    call send
    jc .bad
    mov ecx,6000000
    mov dx,0x3f4
.seek:
    in al,dx
    test al,15
    jz .sense
    dec ecx
    jnz .seek
    jmp .bad
.sense:
    mov al,8
    call send
    call receive
    jc .bad
    and al,0xc0
    jnz .bad
    call receive
    jc .bad
    cmp al,2
    jne .bad
    mov al,6
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov al,0x4a
    cmp byte [operation],0x45
    je .mode
    mov al,0x46
.mode:
    out 0x0b,al
    mov eax,[dds+12]
    out 4,al
    shr eax,8
    out 4,al
    shr eax,8
    out 0x81,al
    mov al,0xff
    out 5,al
    mov al,1
    out 5,al
    mov al,2
    out 0x0a,al
    mov al,[operation]
    call send
    mov al,1
    call send
    mov al,2
    call send
    xor al,al
    call send
    mov al,[sector]
    call send
    mov al,2
    call send
    mov al,[sector]
    call send
    mov al,0x1b
    call send
    mov al,0xff
    call send
    jc .bad
    mov di,proof+192
    mov bx,7
.result:
    call receive
    jc .bad
    mov [di],al
    inc di
    dec bx
    jnz .result
    test byte [proof+192],0xc0
    jnz .bad
    cmp word [proof+193],0
    jne .bad
    mov al,6
    out 0x0a,al
    clc
    ret
.bad:
    stc
    ret

; Waits for controller readiness and records one actual FDC command byte.
send:
    push eax
    push ecx
    push dx
    mov ecx,6000000
    mov dx,0x3f4
.wait:
    in al,dx
    and al,0xc0
    cmp al,0x80
    je .ready
    dec ecx
    jnz .wait
    pop dx
    pop ecx
    pop eax
    stc
    ret
.ready:
    pop dx
    pop ecx
    pop eax
    push dx
    mov dx,0x3f5
    out dx,al
    pop dx
    clc
    ret

; Receives and records an actual bounded-time FDC result byte.
receive:
    push ecx
    push dx
    mov ecx,6000000
    mov dx,0x3f4
.wait:
    in al,dx
    and al,0xc0
    cmp al,0xc0
    je .ready
    dec ecx
    jnz .wait
    pop dx
    pop ecx
    stc
    ret
.ready:
    inc dx
    in al,dx
    pop dx
    pop ecx
    clc
    ret

; Initializes the probe's own transfer pattern and guard bytes.
fill_region:
    mov ax,[dds+8]
    mov es,ax
    mov di,[dds+4]
    mov cx,512
    mov al,0x31
.fill:
    call put_byte
    add al,0x17
    loop .fill
    ret
; Clears only the probe's own guarded transfer region.
clear_region:
    mov ax,[dds+8]
    mov es,ax
    mov di,[dds+4]
    mov cx,512
    xor al,al
.clear:
    call put_byte
    loop .clear
    ret
; Compares the actual guarded transfer region byte by byte.
check_region:
    mov ax,[dds+8]
    mov es,ax
    mov di,[dds+4]
    mov cx,512
    mov al,0x31
.check:
    cmp [es:di],al
    jne .bad
    call advance
    add al,0x17
    loop .check
    clc
    ret
.bad:
    stc
    ret
; Copies the actual DMA result into the guest proof without changing the controller.
capture_region:
    push ds
    push cs
    pop es
    mov ax,[dds+8]
    mov si,[dds+4]
    mov ds,ax
    mov cx,512
.copy:
    mov al,[si]
    stosb
    inc si
    jnz .next
    mov ax,ds
    add ax,0x1000
    mov ds,ax
.next:
    loop .copy
    pop ds
    ret
; Stores the next observed protocol byte in the bounded guest proof record.
put_byte:
    mov [es:di],al
; Advances the guest's explicit transfer-buffer pointer without bypassing actual device I/O.
advance:
    inc di
    jnz .done
    push ax
    mov ax,es
    add ax,0x1000
    mov es,ax
    pop ax
.done:
    ret

; Restores the probe's saved EMS mappings and releases its owned handle.
release_ems:
    cmp byte [ems_allocated],0
    je .done
    mov dx,[handle]
    cmp byte [map_saved],0
    je .free
    mov ah,0x48
    int 0x67
    test ah,ah
    jnz .bad
    mov byte [map_saved],0
.free:
    mov dx,[handle]
    mov ah,0x45
    int 0x67
    test ah,ah
    jnz .bad
    mov byte [ems_allocated],0
.done:
    clc
    ret
.bad:
    stc
    ret
; Releases only this probe's explicitly allocated DOS memory block.
release_dos:
    mov ax,[allocation]
    test ax,ax
    jz .done
    mov es,ax
    mov ah,0x49
    int 0x21
    jc .bad
    mov word [allocation],0
.done:
    clc
.bad:
    ret
failed:
    mov [proof+20],eax
    mov [proof+68],edx
    cmp byte [disabled],0
    je .unlock
    mov ax,0x810c
    mov bx,2
    xor dx,dx
    int 0x4b
.unlock:
    cmp byte [locked],0
    je .release
    push cs
    pop es
    mov di,dds
    mov ax,0x8104
    xor dx,dx
    int 0x4b
.release:
    call release_ems
    call release_dos
report:
    mov si,marker
    call debug_string
    xor ebx,ebx
    mov bx,cs
    shl ebx,4
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

allocation dw 0
handle dw 0
record dw 0
ems_allocated db 0
map_saved db 0
locked db 0
disabled db 0
operation db 0
sector db 1
align 4,db 0
dds times 16 db 0
marker db 'FDC-VDS-PROOF=',0
newline db 13,10,0
success db 'FDC-VDS-PASS',13,10,'$'
failure db 'FDC-VDS-FAIL',13,10,'$'
align 4,db 0
proof:
    db 'CPVDS001'
    dd 1
    times 61 dd 0
    times 1024 db 0
    db 'CPVDADDR'
    dw start,allocation,proof,dds,ems_allocated,map_saved,locked,disabled
align 16,db 0
    times 1024 db 0
stack_end:
program_end:
