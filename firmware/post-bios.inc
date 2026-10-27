; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own POST presentation. Actual guest writes reach text VRAM and diagnostic E9.
; Inventory describes configured hardware, not an invented exhaustive RAM/device test.

%macro post_enter 0
    pushad
    push ds
    push es
    xor ax,ax
    mov ds,ax
    movzx di,byte [0x451]
    imul di,160
    movzx ax,byte [0x450]
    shl ax,1
    add di,ax
    push cs
    pop ds
    mov ax,0xb800
    mov es,ax
    mov bx,0x0700
%endmacro
%macro post_leave 0
    call post_cursor
    pop es
    pop ds
    popad
    ret
%endmacro

; Shows the original colored text logo, immutable identity and real configured inventory.
post_identity:
    post_enter
    xor di,di
    mov ax,0x1f20
    mov cx,320
    rep stosw
    xor di,di
    mov bh,0x1f
    mov si,post_logo1
    call post_puts
    call post_newline
    mov si,post_logo2
    call post_puts
    call post_newline
    mov si,post_logo3
    call post_puts
    call post_newline
    mov si,post_logo4
    call post_puts
    call post_newline
    mov bh,7
    call post_newline
    mov si,post_cpu
    call post_puts
    call post_newline
    mov si,post_memory
    call post_puts
    mov eax,[cs:installed_ram_kib]
    call post_decimal
    mov si,post_memory_end
    call post_puts
    call post_newline
    mov si,post_vga
    cmp byte [cs:et4000_present],0
    je .video
    mov si,post_et4000
.video:
    call post_puts
    call post_newline
    xor bp,bp
.disk:
    cmp bp,4
    jae .floppy
    mov ax,bp
    cmp al,[cs:disk_count]
    jae .floppy
    mov si,post_disk
    call post_puts
    mov eax,0x80
    movzx edx,bp
    add eax,edx
    call post_hex_byte
    mov si,post_primary
    movzx dx,byte [cs:disk_routes+bp]
    test dl,2
    jz .channel
    mov si,post_secondary
.channel:
    call post_puts
    mov si,post_master
    test dl,1
    jz .device
    mov si,post_slave
.device:
    call post_puts
    movzx eax,bp
    shl ax,2
    mov si,ax
    mov eax,[cs:disk_capacities+si]
    shr eax,11
    call post_decimal
    mov si,post_mib
    call post_puts
    mov si,post_ro
    cmp byte [cs:disk_write_flags+bp],0
    je .access
    mov si,post_rw
.access:
    call post_puts
    call post_newline
    inc bp
    jmp .disk
.floppy:
    mov si,post_fdc
    call post_puts
    mov al,[cs:floppy_types]
    call post_floppy_type
    mov si,post_floppy_b
    call post_puts
    mov al,[cs:floppy_types+1]
    call post_floppy_type
    call post_newline
    cmp byte [cs:gameport_present],0
    je .no_gameport
    mov si,post_gameport
    call post_puts
    call post_newline
.no_gameport:
    cmp byte [cs:serial_present],0
    je .no_serial
    mov si,post_serial
    call post_puts
    call post_newline
.no_serial:
    cmp byte [cs:parallel_present],0
    je .no_parallel
    mov si,post_parallel
    call post_puts
    call post_newline
.no_parallel:
    cmp word [cs:midi_port],0
    je .no_midi
    mov si,post_midi_330
    cmp word [cs:midi_port],0x300
    jne .midi_name
    mov si,post_midi_300
.midi_name:
    call post_puts
    movzx eax,byte [cs:midi_irq]
    call post_decimal
    call post_newline
.no_midi:
    mov si,post_target_ide
    cmp byte [cs:boot_drive],0
    jne .target
    mov si,post_target_floppy
.target:
    call post_puts
    call post_newline
    call post_newline
    mov si,post_platform
    call post_puts
    call post_newline
    post_leave

; Appends one CS:SI zero-terminated progress line without per-character BIOS interrupts.
post_message:
    post_enter
    call post_puts
    call post_newline
    post_leave

; Writes one bounded inventory string with BH=attribute; DI remains a text byte offset.
post_puts:
    lodsb
    test al,al
    jz .done
    call post_character
    jmp post_puts
.done:
    ret

; Draws and mirrors one character. The fixed POST layout never exceeds the first 25 rows.
post_character:
    cmp di,4000
    jae .done
    mov ah,bh
    stosw
    out 0xe9,al
.done:
    ret

; Moves to the next 80-column row and emits a conventional CR/LF diagnostic line ending.
post_newline:
    push ax
    push dx
    push cx
    mov ax,di
    xor dx,dx
    mov cx,160
    div cx
    inc ax
    mul cx
    mov di,ax
    mov al,13
    out 0xe9,al
    mov al,10
    out 0xe9,al
    pop cx
    pop dx
    pop ax
    ret

; Prints an unsigned full 32-bit inventory value without converting through host code.
post_decimal:
    pushad
    xor cx,cx
    mov ebp,10
.divide:
    xor edx,edx
    div ebp
    push dx
    inc cx
    test eax,eax
    jnz .divide
.digit:
    pop ax
    add al,'0'
    call post_character
    loop .digit
    ; Preserve the advanced output pointer while restoring arithmetic scratch state.
    mov bp,sp
    mov [ss:bp],edi
    popad
    ret

; Emits two uppercase hexadecimal digits for the dense BIOS drive number.
post_hex_byte:
    push ax
    shr al,4
    call .digit
    pop ax
    and al,15
.digit:
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .emit
    add al,7
.emit:
    jmp post_character

; Describes every geometry admitted by the configured floppy profile.
post_floppy_type:
    push si
    mov si,post_none
    cmp al,1
    jb .print
    cmp al,5
    ja .print
    movzx si,al
    dec si
    shl si,1
    mov si,[cs:post_floppy_names+si]
.print:
    call post_puts
    pop si
    ret

; Updates page-zero BIOS/CRTC cursor only once per completed block of POST text.
post_cursor:
    push ds
    xor ax,ax
    mov ds,ax
    mov ax,di
    shr ax,1
    xor dx,dx
    div word [0x44a]
    mov dh,al
    mov [0x450],dx
    call hardware_cursor
    pop ds
    ret

post_logo1: db '  +-------------+   DOSE - DosEmulator',0
post_logo2: db '  |  > DOSE_    |   ',BIOS_NAME,' ',BIOS_VERSION,0
post_logo3: db '  |  486 / AT   |   Original firmware - 64 KiB ROM',0
post_logo4: db '  +-------------+   Copyright 2026 Nils Kopal',0
post_cpu: db 'Configured CPU: 486DX / AT development profile',0
post_memory: db 'Memory: ',0
post_memory_end: db ' KiB installed / 639 KiB conventional',0
post_vga: db 'Video: DOSE VGA',0
post_et4000: db 'Video: Tseng ET4000AX ISA / 1024 KiB',0
post_disk: db 'IDE ',0
post_primary: db 'h primary ',0
post_secondary: db 'h secondary ',0
post_master: db 'master: ',0
post_slave: db 'slave:  ',0
post_mib: db ' MiB ',0
post_ro: db '[read only]',0
post_rw: db '[read/write]',0
post_fdc: db 'Floppy: A=',0
post_floppy_b: db ' / B=',0
post_gameport: db 'Game control: IBM analog / 201h',0
post_serial: db 'COM1: National PC16550D / 3F8h IRQ4',0
post_parallel: db 'LPT1: IBM Printer Adapter / 378h IRQ7',0
post_midi_330: db 'MIDI: CT1740 MPU-UART / 330h IRQ',0
post_midi_300: db 'MIDI: CT1740 MPU-UART / 300h IRQ',0
post_none: db 'none',0
post_floppy_names: dw post_360,post_1200,post_720,post_1440,post_2880
post_360: db '360 KiB',0
post_1200: db '1200 KiB',0
post_720: db '720 KiB',0
post_1440: db '1440 KiB',0
post_2880: db '2880 KiB',0
post_target_ide: db 'Boot target: IDE drive 80h',0
post_target_floppy: db 'Boot target: floppy A:',0
post_platform: db 'POST: IVT/BDA/EBDA, PIC/PIT and A20 initialized',0
post_fdc_ready: db 'POST: floppy controller reset complete',0
post_fdc_warning: db 'POST WARNING: floppy reset timed out',0
post_ide_ready: db 'POST: IDE controllers reset',0
post_keyboard_ready: db 'POST: keyboard initialized; loading boot sector',0
