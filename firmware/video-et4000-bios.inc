; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own ET4000AX ISA reference firmware, Apache-2.0. No commercial option ROM.
; Tseng Data Book 1990 pp. 84/85, 95/96, 128, 130, 135..144.
; Legacy AL=2Eh/30h/38h: banked 640x480/800x600/1024x768, 256 colors.
; Bit 7 preserves all VRAM. No VBE or high-color interface is advertised here.

et4000_vga_reset:
    push ax
    push dx
    mov dx,0x3c2
    mov al,0x67                 ; All modes offered by this firmware use the color CRTC decoder.
    out dx,al
    mov dx,0x3bf
    mov al,3
    out dx,al
    mov dx,0x3d8
    mov al,0xa0
    out dx,al
    mov dx,0x3cd
    xor al,al
    out dx,al
    mov dx,0x3d4
    mov ax,0x0011
    out dx,ax
    mov ax,0x0033
    out dx,ax
    mov ax,0x0035
    out dx,ax
    mov ax,0xc036
    out dx,ax
    mov ax,0x0b37
    out dx,ax
    mov dx,0x3c4
    mov ax,0xbc07
    out dx,ax
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,0x16
    out dx,al
    xor al,al
    out dx,al
    mov al,0x20
    out dx,al
    pop dx
    pop ax
    ret

et4000_set_mode:
    push bp
    mov bp,sp
    pushad
    push ds
    push es
    mov bl,al
    and bl,0x7f
    mov ax,0x93                 ; VGA common registers/DAC, never erase the existing buffer here.
    call set_video_mode
    xor ax,ax
    mov ds,ax
    mov [0x449],bl
    mov word [0x44a],80
    mov word [0x485],16
    mov word [0x43*4],font_data
    mov word [0x43*4+2],0xf000
    mov byte [0x484],29
    mov word [0x44c],0xffff      ; one graphics page; no text page or character-service contract.
    mov si,et_crtc_640
    cmp bl,0x2e
    je .table
    mov word [0x44a],100
    mov byte [0x484],36
    mov si,et_crtc_800
    cmp bl,0x30
    je .table
    mov word [0x44a],128
    mov byte [0x484],47
    mov si,et_crtc_1024
.table:
    mov dx,0x3d4
    mov ax,0x0011
    out dx,ax
    mov cx,25
    call vga_write_indexed_table
    ; The ET4000 uses the full eight pixels per character in these 256-color modes.
    ; ATC10 bit6 is zero (VGA13h uses one and halves horizontal output).
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,0x10
    out dx,al
    mov al,1
    out dx,al
    mov al,0x20
    out dx,al
    test byte [ss:bp-4],0x80
    jnz .done
    mov ax,0xa000
    mov es,ax
    xor bx,bx
.clear_bank:
    mov dx,0x3cd
    mov al,bl
    out dx,al
    xor di,di
    xor ax,ax
    mov cx,0x8000
    rep stosw
    inc bl
    cmp bl,16
    jb .clear_bank
.done:
    mov dx,0x3cd
    xor al,al
    out dx,al
    pop es
    pop ds
    popad
    pop bp
    ret

; Noninterlaced reference scanout, 6-bit DAC. Raster timing remains approximate.
; Standard CRTC fields plus 80/100/128 groups of four pixels per scanline.
et_crtc_640: db 0x5f,0x4f,0x50,0x82,0x54,0x80,0x0b,0x3e,0,0x40,0,0,0,0,0,0,0xea,0x0c,0xdf,0x50,0x60,0xe7,6,0xab,0xff
et_crtc_800: db 0x7f,0x63,0x64,0x82,0x68,0x17,0x72,0xf0,0,0x60,0,0,0,0,0,0,0x58,0x0c,0x57,0x64,0x60,0x58,0x72,0xab,0xff
et_crtc_1024: db 0xa1,0x7f,0x80,0x04,0x88,0x9e,0x26,0xfd,0,0x60,0,0,0,0,0,0,0x08,0x0a,0xff,0x80,0x60,0x04,0x22,0xab,0xff
