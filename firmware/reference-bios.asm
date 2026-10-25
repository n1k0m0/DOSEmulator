; DOSEmulator original reference firmware. Apache-2.0.
; All BIOS services execute as guest x86 instructions, including ATA I/O.
bits 16
org 0
cpu 486

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

start:
    cli
    cld
    xor ax,ax
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov sp,0x7c00
    xor di,di
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
    vector 0x15,int_system
    vector 0x16,int_keyboard
    vector 0x17,int_printer
    vector 0x18,boot_failure
    vector 0x19,boot_entry
    vector 0x1a,int_time
    vector 0x1e,floppy_parameters
    vector 0x1f,font8_data+1024
    vector 0x43,font_data
    vector 0x41,disk_parameters
    vector 0x46,disk_parameters
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
    mov byte [0x475],1
    cmp dword [cs:disk_sectors],0
    jne .has_ide
    mov byte [0x475],0
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
    out 0x21,al
    mov al,0xff
    out 0xa1,al
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
    push cs
    pop ds
    mov si,banner
    call print_string
    xor ax,ax
    mov ds,ax
    mov al,0x10
    out 0x80,al
    cmp byte [cs:floppy_count],0
    je .no_floppy_init
    call fdc_reset
.no_floppy_init:
    ; CPU hot reset leaves powered controllers intact. POST reinitializes their boot paths.
    cmp dword [cs:disk_sectors],0
    je .no_ide_reset
    mov dx,0x3f6
    mov al,4
    out dx,al
    xor al,al
    out dx,al
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
    sti
boot_entry:
    xor ax,ax
    mov es,ax
    mov di,0x7c00
    xor eax,eax
    mov cx,1
    cmp byte [cs:boot_drive],0
    je .floppy_boot
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
; One-sector commands also handle buffers that cross a 64-KiB segment window.
read_sectors:
    push bx
    mov bl,0x20
    call transfer_sectors
    pop bx
    ret
verify_sectors:
    ; AH04 needs no caller buffer. Reuse actual ATA reads one sector at a time
    ; into reserved EBDA, preserving the ordinary read/write PIO path and timing.
    push ebx
    push ecx
    push edx
    push esi
    push edi
    push es
    mov esi,eax
    movzx edx,cx
    test edx,edx
    jz .bad
    mov ebx,eax
    add ebx,edx
    jc .bad
    cmp ebx,[cs:disk_sectors]
    ja .bad
.sector:
    call bios_ebda_segment
    mov es,ax
    mov di,0x200
    mov eax,esi
    mov cx,1
    call read_sectors
    jc .done
    inc esi
    dec edx
    jnz .sector
    clc
    jmp .done
.bad:
    stc
.done:
    pop es
    pop edi
    pop esi
    pop edx
    pop ecx
    pop ebx
    ret
write_sectors:
    push bx
    mov bl,0x30
    call transfer_sectors
    pop bx
    ret
transfer_sectors:
    push ebp
    mov bp,sp
    push ebx
    push edx
    push esi
    push cx
    mov esi,eax
    test cx,cx
    jz .bad
    movzx ebx,cx
    add ebx,eax
    jc .bad
    cmp ebx,[cs:disk_sectors]
    ja .bad
.sector:
    mov dx,0x1f6
    mov eax,esi
    shr eax,24
    and al,15
    or al,0xe0
    out dx,al
    mov dx,0x1f2
    mov al,1
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
    mov dx,0x1f7
    mov al,[ss:bp-4]
    out dx,al
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
    mov dx,0x1f0
    mov cx,256
.word:
    cmp byte [ss:bp-4],0x30
    je .write_word
    in ax,dx
    mov [es:di],ax
    jmp .advance
.write_word:
    mov ax,[es:di]
    out dx,ax
.advance:
    add di,2
    jnc .next_word
    mov ax,es
    add ax,0x1000
    mov es,ax
.next_word:
    loop .word
    ; A WRITE only completes after the device has committed the full sector.
    ; Wait before issuing a new command, including the last sector of a BIOS call.
    mov dx,0x1f7
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
    jnz .sector
    clc
    jmp .done
.bad:
    stc
.done:
    pop cx
    pop esi
    pop edx
    pop ebx
    pop ebp
    ret

; Compile the same CHS translation for normal and bufferless verify services,
; without adding per-sector branches to ordinary ATA read/write transfers.
%macro fixed_chs_request 1
    movzx eax,rCH
    movzx edx,rCL
    and edx,0xc0
    shl edx,2
    or eax,edx
    shl eax,4
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
int_disk:
    cmp dl,2
    jb int_floppy
    enter_service
    and rFLAGS,0xfffe
    mov ah,rAH
    cmp rDL,0x80
    jne .unsupported
    cmp dword [cs:disk_sectors],0
    je .unsupported
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
    cmp byte [si],16
    jb .unsupported
    cmp dword [si+12],0
    jne .unsupported
    mov eax,[si+8]
    mov cx,[si+2]
    mov di,[si+4]
    mov es,[si+6]
.read:
    cmp rAH,3
    je .write
    cmp rAH,0x43
    je .write
    call read_sectors
    jc .read_error
    jmp .success
.write:
    cmp byte [cs:disk_writable],0
    je .write_protected
    call write_sectors
    jc .read_error
    jmp .success
.verify_chs:
    fixed_chs_request .unsupported
    call verify_sectors
    jc .read_error
    jmp .success
.read_error:
    mov rAH,4
    jmp .error
.geometry:
    mov eax,[cs:disk_sectors]
    xor edx,edx
    mov ecx,1008
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
    mov rDX,0x0f01
    jmp .success
.type:
    mov eax,[cs:disk_sectors]
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
    cmp word [si],26
    jb .unsupported
    mov word [si],26
    mov word [si+2],2
    mov dword [si+4],65
    mov dword [si+8],16
    mov dword [si+12],63
    mov eax,[cs:disk_sectors]
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

int_video:
    enter_service
    mov ah,rAH
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
.mode:
    mov al,rAL
    call set_video_mode
    jmp .done
.pixel:
    call video_pixel
    jmp .done
.palette:
    call video_palette
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
    cmp ch,dh
    ja .done
    cmp cl,dl
    ja .done
    cmp dh,[0x484]
    ja .done
    cmp dl,[0x44a]
    jae .done
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
%include "video-font-bios.inc"
%include "speaker-bios.inc"

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
int_printer:
    mov ah,0x90
    iret

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
    cmp al,'a'
    jb .ctrl
    cmp al,'z'
    ja .ctrl
    sub al,32
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

int_time:
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

int_system:
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

%include "mouse-bios.inc"
%include "memory-bios.inc"

banner: db 'DOSEmulator reference BIOS - 486 AT',13,10,0
disk_error: db 13,10,'BOOT ERROR: no readable primary IDE master.',13,10,0
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
times 0xe020-($-$$) db 0xff
floppy_parameters: db 0xdf,2,0x25,2,18,0x1b,0xff,0x6c,0xf6,15,8
times 0xe030-($-$$) db 0xff
floppy_parameters_b: db 0xdf,2,0x25,2,18,0x1b,0xff,0x6c,0xf6,15,8
times 0xe100-($-$$) db 0xff
vga_planar_palette: times 768 db 0
vga_indexed_palette: times 768 db 0
times 0xfff0-($-$$) db 0xff
    jmp 0xf000:0
times 0xfff5-($-$$) db 0xff
    db '10/24/26',0,0xfc,0
