; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOSE VGA firmware, Apache-2.0. All device changes use guest IN/OUT.
; AL=03h,12h,13h, with bit 7 preserving display memory.
set_video_mode:
    cmp byte [cs:et4000_present],0
    je .vga
    push ax
    and al,0x7f
    cmp al,0x2e
    je .extended
    cmp al,0x30
    je .extended
    cmp al,0x38
    je .extended
    pop ax
    jmp .vga
.extended:
    pop ax
    call et4000_set_mode
    ret
.vga:
    push bp
    mov bp,sp
    pushad
    push ds
    push es
    mov bl,al
    and bl,0x7f
    cmp bl,3
    je .supported
    cmp bl,0x12
    je .supported
    cmp bl,0x13
    jne .done
.supported:
%ifdef DOSE_TRIO64
    call trio64_vga_reset
%endif
    cmp byte [cs:et4000_present],0
    je .standard_adapter
    call et4000_vga_reset
.standard_adapter:
    xor ax,ax
    mov ds,ax
    mov [0x449],bl
    mov di,0x450
    mov cx,8
.reset_cursors:
    mov word [di],0
    add di,2
    loop .reset_cursors
    mov word [0x44e],0
    mov byte [0x462],0
    mov word [0x460],0
    mov word [0x44a],80
    mov word [0x44c],4096
    mov byte [0x484],24
    mov word [0x485],16
    mov word [0x43*4],font_data
    mov word [0x43*4+2],0xf000
    cmp bl,3
    jne .graphics_bda
    mov word [0x460],0x0e0f       ; default 16-high mode: native bottom two scanlines
    mov ax,[0x40e]
    mov es,ax
    cmp byte [es:0x20],0
    jne .text_not200
    mov word [0x485],8
    mov word [0x43*4],font8_data
    jmp .bda_done
.text_not200:
    cmp byte [es:0x20],1
    jne .bda_done
    mov word [0x485],14
    mov word [0x43*4],font14_data
    jmp .bda_done
.graphics_bda:
    mov word [0x44c],0x9600
    mov byte [0x484],29
    cmp bl,0x12
    je .bda_done
    mov word [0x44a],40
    mov word [0x44c],0xfa00
    mov byte [0x484],24
    mov word [0x485],8
    mov word [0x43*4],font8_data
.bda_done:
    ; Clear through unchained plane writes, then load our own font into plane 2.
    mov al,0x67
    mov dx,0x3c2
    out dx,al
    mov dx,0x3c4
    mov ax,0x0300
    out dx,ax
    mov ax,0x0101
    out dx,ax
    mov ax,0x0f02
    out dx,ax
    mov ax,0x0604
    out dx,ax
    mov dx,0x3ce
    mov si,vga_gc_planar
    mov cx,9
    call vga_write_indexed_table
    mov ax,0xa000
    mov es,ax
    test byte [ss:bp-4],0x80
    jnz .no_clear
    xor di,di
    xor ax,ax
    mov cx,0x8000
    rep stosw
.no_clear:
    cmp byte [0x449],3
    jne .font_done
    mov dx,0x3c4
    mov ax,0x0402
    out dx,ax
    push ds
    mov dx,[0x485]
    push cs
    pop ds
    mov si,font_data
    cmp dx,14
    jne .font_not14
    mov si,font14_data
.font_not14:
    cmp dx,8
    jne .font_source
    mov si,font8_data
.font_source:
    xor di,di
    mov bx,256
.font_character:
    mov cx,dx
    rep movsb
    add di,32
    sub di,dx
    dec bx
    jnz .font_character
    pop ds
.font_done:
    mov si,vga_seq_text
    mov di,vga_crtc_text
    mov bx,vga_gc_text
    mov al,0x67
    cmp byte [0x449],3
    jne .graphics_tables
    cmp word [0x485],14
    jne .tables
    mov al,0xa3
    jmp .tables
.graphics_tables:
    mov si,vga_seq_planar
    mov di,vga_crtc_planar
    mov bx,vga_gc_planar
    mov al,0xe3
    cmp byte [0x449],0x12
    je .tables
    mov si,vga_seq_indexed
    mov di,vga_crtc_indexed
    mov bx,vga_gc_indexed
    mov al,0x63
.tables:
    mov dx,0x3c2
    out dx,al
    mov dx,0x3c4
    mov cx,5
    call vga_write_indexed_table
    mov si,bx
    mov dx,0x3ce
    mov cx,9
    call vga_write_indexed_table
    mov dx,0x3d4
    mov ax,0x0011
    out dx,ax
    mov si,di
    mov cx,25
    call vga_write_indexed_table
    cmp byte [0x449],3
    jne .crtc_done
    cmp word [0x485],16
    je .crtc_done
    cmp word [0x485],8
    jne .text_geometry
    mov ax,0xcf09                ; 200 logical lines, doubled to 400 VGA raster lines
    out dx,ax
.text_geometry:
    mov bh,[0x485]
    call vga_font_geometry
.crtc_done:
    mov si,vga_attr_planar
    cmp byte [0x449],0x13
    jne .attributes
    mov si,vga_attr_indexed
.attributes:
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    xor bx,bx
    mov cx,21
.attribute:
    mov al,bl
    out dx,al
    mov al,[cs:si]
    out dx,al
    inc si
    inc bl
    loop .attribute
    mov dx,0x3c6
    mov al,0xff
    out dx,al
    mov si,vga_planar_palette
    cmp byte [0x449],0x13
    jne .palette
    mov si,vga_indexed_palette
.palette:
    push ds
    push cs
    pop ds
    mov dx,0x3c8
    xor al,al
    out dx,al
    inc dx
    mov cx,768
.dac:
    lodsb
    out dx,al
    loop .dac
    pop ds
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,0x20
    out dx,al
    cmp byte [0x449],3
    jne .done
    test byte [ss:bp-4],0x80
    jnz .done
    call clear_screen
.done:
    pop es
    pop ds
    popad
    pop bp
    ret

; CS:SI table, CX count, DX indexed address/data pair. Preserves BX.
vga_write_indexed_table:
    push bx
    xor bx,bx
.register:
    mov al,bl
    mov ah,[cs:si]
    out dx,ax
    inc si
    inc bl
    loop .register
    pop bx
    ret

; AL=index, DX=address pair. AX returns the original index/data word.
vga_read_indexed:
    push bx
    mov bl,al
    out dx,al
    inc dx
    in al,dx
    mov ah,al
    mov al,bl
    dec dx
    pop bx
    ret

; Called in the INT10 service frame. Canonical BIOS modes 12h/13h only.
video_pixel:
    mov cx,rCX
    mov ax,rDX
    cmp byte [0x449],0x13
    je .indexed
    cmp byte [0x449],0x12
    jne .done
    cmp cx,640
    jae .done
    cmp ax,480
    jae .done
    imul di,ax,80
    mov bx,cx
    shr bx,3
    add di,bx
    and cl,7
    mov bl,0x80
    shr bl,cl
    mov ax,0xa000
    mov es,ax
    cmp rAH,0x0d
    je .planar_read
    mov dx,0x3c4
    in al,dx
    push ax
    mov al,2
    call vga_read_indexed
    push ax
    mov dx,0x3ce
    in al,dx
    push ax
    mov al,0
    call vga_read_indexed
    push ax
    mov al,1
    call vga_read_indexed
    push ax
    mov al,3
    call vga_read_indexed
    push ax
    mov al,5
    call vga_read_indexed
    push ax
    mov al,8
    call vga_read_indexed
    push ax
    mov dx,0x3c4
    mov ax,0x0f02
    out dx,ax
    mov dx,0x3ce
    mov ah,rAL
    and ah,15
    xor al,al
    out dx,ax
    mov ax,0x0f01
    out dx,ax
    mov ax,0x0003
    test rAL,0x80
    jz .pixel_logic
    mov ah,0x18
.pixel_logic:
    out dx,ax
    mov ax,5
    out dx,ax
    mov al,8
    mov ah,bl
    out dx,ax
    mov al,[es:di]
    mov [es:di],al
    mov cx,5
.restore_gc:
    pop ax
    out dx,ax
    loop .restore_gc
    pop ax
    out dx,al
    mov dx,0x3c4
    pop ax
    out dx,ax
    pop ax
    out dx,al
    ret
.planar_read:
    mov dx,0x3ce
    in al,dx
    push ax
    mov al,4
    call vga_read_indexed
    push ax
    mov al,5
    call vga_read_indexed
    push ax
    mov ax,5
    out dx,ax
    xor si,si
    xor cx,cx
.read_plane:
    mov dx,0x3ce
    mov al,4
    mov ah,cl
    out dx,ax
    mov al,[es:di]
    test al,bl
    jz .read_next
    mov ax,1
    shl ax,cl
    or si,ax
.read_next:
    inc cx
    cmp cx,4
    jb .read_plane
    pop ax
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,al
    mov ax,si
    mov rAL,al
    ret
.indexed:
    cmp cx,320
    jae .done
    cmp ax,200
    jae .done
    imul di,ax,320
    add di,cx
    mov ax,0xa000
    mov es,ax
    cmp rAH,0x0d
    je .indexed_read
    mov al,rAL
    mov [es:di],al
    ret
.indexed_read:
    mov al,[es:di]
    mov rAL,al
.done:
    ret

video_palette:
    cmp rAL,0x10
    je .set_single
    cmp rAL,0x15
    je .get_single
    cmp rAL,0x12
    je .set_block
    cmp rAL,0x17
    je .get_block
    ret
.set_single:
    mov dx,0x3c8
    mov al,rBL
    out dx,al
    inc dx
    mov al,rDH
    out dx,al
    mov al,rCH
    out dx,al
    mov al,rCL
    out dx,al
    ret
.get_single:
    mov dx,0x3c7
    mov al,rBL
    out dx,al
    mov dx,0x3c9
    in al,dx
    mov rDH,al
    in al,dx
    mov rCH,al
    in al,dx
    mov rCL,al
    ret
.set_block:
    mov bx,0x3c8
    jmp .block
.get_block:
    mov bx,0x3c7
.block:
    mov cx,rCX
    mov ax,rBX
    add ax,cx
    jc .done
    cmp ax,256
    ja .done
    test cx,cx
    jz .done
    mov es,rES
    mov di,rDX
    imul cx,cx,3
    mov ax,di
    dec cx
    add ax,cx
    jc .done
    inc cx
    mov dx,bx
    mov al,rBL
    out dx,al
    mov dx,0x3c9
.component:
    cmp bx,0x3c7
    je .read
    mov al,[es:di]
    out dx,al
    jmp .next
.read:
    in al,dx
    mov [es:di],al
.next:
    inc di
    loop .component
.done:
    ret

vga_seq_text: db 3,1,3,0,2
vga_seq_planar: db 3,1,15,0,6
vga_seq_indexed: db 3,1,15,0,14
vga_gc_text: db 0,0,0,0,0,0x10,0x0e,0,0xff
vga_gc_planar: db 0,0,0,0,0,0,5,15,0xff
vga_gc_indexed: db 0,0,0,0,0,0x40,5,15,0xff
vga_crtc_text: db 0x5f,0x4f,0x50,0x82,0x54,0x80,0xbf,0x1f,0,0x4f,14,15,0,0,0,0,0x9c,0x0e,0x8f,0x28,0x1f,0x96,0xb9,0xa3,0xff
vga_crtc_planar: db 0x5f,0x4f,0x50,0x82,0x54,0x80,0x0b,0x3e,0,0,0,0,0,0,0,0,0xea,0x0c,0xdf,0x28,0,0xe7,6,0xe3,0xff
vga_crtc_indexed: db 0x5f,0x4f,0x50,0x82,0x54,0x80,0xbf,0x1f,0,0x41,0,0,0,0,0,0,0x9c,0x0e,0x8f,0x28,0x40,0x96,0xb9,0xa3,0xff
vga_attr_planar: db 0,1,2,3,4,5,0x14,7,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f,0x0c,0,15,0,0
vga_attr_indexed: db 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,0x41,0,15,0,0

%include "video-et4000-bios.inc"
