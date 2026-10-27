; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOSE VGA character-generator services, Apache-2.0.
; IBM PS/2 BIOS Interface May 1988, pp. 2-25..2-30; Hardware Interface,
; Video Subsystem (Type 1), pp. 46..48 and 87. No imported ROM fonts.
; Called with the INT10 enter_service frame and DS=0.
video_font:
    mov al,rAL
    cmp al,0x30
    je .query
    cmp al,0x20
    je .graphics_1f
    cmp al,0x21
    jb .alpha
    cmp al,0x24
    jbe .graphics
.alpha:
    cmp byte [0x449],3
    jne .done
    cmp al,3
    je .select
    and al,0xef
    cmp al,0
    je .user
    cmp al,1
    je .rom14
    cmp al,2
    je .rom8
    cmp al,4
    jne .done
    mov si,font_data
    mov bh,16
    jmp .rom
.rom14:
    mov si,font14_data
    mov bh,14
    jmp .rom
.rom8:
    mov si,font8_data
    mov bh,8
.rom:
    mov bl,rBL
    cmp bl,7
    ja .done
    mov cx,256
    xor dx,dx
    push cs
    pop ds
    jmp .upload
.user:
    mov bx,rBX
    cmp bl,7
    ja .done
    cmp bh,1
    jb .done
    cmp bh,32
    ja .done
    test rAL,0x10
    jz .range
    cmp bh,2                     ; a one-line 400-row viewport exceeds BDA/text-aperture capacity
    jb .done
.range:
    mov cx,rCX
    test cx,cx
    jz .done
    mov dx,rDX
    mov ax,dx
    add ax,cx
    jc .done
    cmp ax,256
    ja .done
    mov si,[ss:bp]                 ; original caller BP, not our service frame
    mov ds,rES
.upload:
    call vga_font_upload
    xor ax,ax
    mov ds,ax
    test rAL,0x10
    jz .done                      ; AL=00/01/02/04 changes only the requested font
    call vga_font_geometry        ; AL=10/11/12/14 also recalculates page 0 geometry
.done:
    ret
.select:
    mov dx,0x3c4
    in al,dx
    push ax
    mov ah,rBL
    and ah,0x3f
    mov al,3
    out dx,ax
    pop ax
    out dx,al
    ret

.graphics_1f:
    mov ax,[ss:bp]
    mov [0x1f*4],ax
    mov ax,rES
    mov [0x1f*4+2],ax
    ret
.graphics:
    cmp byte [0x449],0x12
    je .graphics_mode
    cmp byte [0x449],0x13
    jne .done
.graphics_mode:
    mov si,[ss:bp]
    mov di,rES
    mov cx,rCX
    cmp rAL,0x21
    je .graphics_height
    mov di,0xf000
    mov si,font14_data
    mov cx,14
    cmp rAL,0x22
    je .graphics_height
    mov si,font8_data
    mov cx,8
    cmp rAL,0x23
    je .graphics_height
    mov si,font_data
    mov cx,16
.graphics_height:
    cmp cx,1
    jb .done
    cmp cx,32
    ja .done
    movzx bx,rBL
    cmp bl,3
    ja .done
    test bl,bl
    jnz .graphics_fixed_rows
    movzx bx,rDL                 ; row count, not the BDA's last-row index
    test bx,bx
    jz .done
    jmp .graphics_bounds
.graphics_fixed_rows:
    dec bx
    mov bl,[cs:graphics_font_rows+bx]
.graphics_bounds:
    mov ax,bx
    mul cx
    test dx,dx
    jnz .done
    mov dx,480
    cmp byte [0x449],0x12
    je .graphics_fits
    mov dx,200
.graphics_fits:
    cmp ax,dx
    ja .done                     ; bounded DOSEBios: require a fitting grid
    mov [0x43*4],si
    mov [0x43*4+2],di
    mov [0x485],cx
    dec bx
    mov [0x484],bl
    ret

.query:
    mov al,rBH
    cmp al,0
    je .int1f
    cmp al,1
    je .int43
    mov bx,font14_data
    cmp al,2
    je .rom_pointer
    mov bx,font8_data
    cmp al,3
    je .rom_pointer
    mov bx,font8_data+1024
    cmp al,4
    je .rom_pointer
    mov bx,font_data
    cmp al,6
    jne .done                     ; no alternate nine-dot ROM font tables exist
.rom_pointer:
    mov ax,0xf000
    jmp .return_pointer
.int1f:
    mov bx,[0x1f*4]
    mov ax,[0x1f*4+2]
    jmp .return_pointer
.int43:
    mov bx,[0x43*4]
    mov ax,[0x43*4+2]
.return_pointer:
    mov rES,ax
    mov [ss:bp],bx                 ; final POP BP returns the font offset
    mov ax,[0x485]
    mov rCX,ax
    mov al,[0x484]
    mov rDL,al
    ret

graphics_font_rows: db 14,25,43

; DS:SI packed glyph bytes, CX=count, DX=first character, BL=block 0..7,
; BH=height 1..32. Preserve caller registers, including all altered VGA
; indexed registers/indices. Only height bytes of each 32-byte slot are written.
vga_font_upload:
    pushad
    push es
    mov di,bx
    and di,3
    shl di,14
    test bl,4
    jz .bank_ready
    or di,0x2000
.bank_ready:
    mov ax,dx
    shl ax,5
    add di,ax
    movzx bx,bh
    mov dx,0x3c4
    in al,dx
    push ax
    mov al,0
    call vga_read_indexed
    push ax
    mov al,2
    call vga_read_indexed
    push ax
    mov al,4
    call vga_read_indexed
    push ax
    mov ax,0x0100
    out dx,ax
    mov ax,0x0402
    out dx,ax
    mov ax,0x0604
    out dx,ax
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
    mov al,4
    call vga_read_indexed
    push ax
    mov al,5
    call vga_read_indexed
    push ax
    mov al,6
    call vga_read_indexed
    push ax
    mov al,8
    call vga_read_indexed
    push ax
    xor ax,ax
    out dx,ax                     ; Set/Reset = 0
    mov ax,1
    out dx,ax                     ; Enable Set/Reset = 0
    mov ax,3
    out dx,ax                     ; replace, no rotate
    mov ax,0x0204
    out dx,ax
    mov ax,5
    out dx,ax                     ; normal writes, no odd/even
    mov ax,0x0406
    out dx,ax                     ; A0000..AFFFF
    mov ax,0xff08
    out dx,ax
    mov ax,0xa000
    mov es,ax
.character:
    push cx
    mov cx,bx
    rep movsb                     ; a 16-bit source offset wraps within its segment
    pop cx
    add di,32
    sub di,bx
    loop .character
    mov cx,7
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
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,al
    pop es
    popad
    ret

; DS=0, BH=points, canonical color text mode 03h. Keep the selected
; 200/350/400 scanline family; changing 14 -> 8 must not derive 49 rows
; from the previous rounded 392-line viewport.
vga_font_geometry:
    movzx cx,bh
    mov [0x485],cx
    mov dx,0x3cc
    in al,dx
    and al,0xc0
    mov bx,400
    cmp al,0x80
    jne .not350
    mov bx,350
.not350:
    cmp al,0
    jne .family_ready
    mov bx,200
.family_ready:
    mov dx,0x3d4
    in al,dx
    push ax
    mov al,9
    call vga_read_indexed
    mov di,ax
    test ah,0x80
    jz .no_double_scan
    mov bx,200
.no_double_scan:
    mov ax,bx
    xor dx,dx
    div cx
    mov bx,ax
    dec al
    mov [0x484],al
    imul ax,bx,160
    mov [0x44c],ax
    mov word [0x44e],0
    mov byte [0x462],0
    imul bx,cx
    test di,0x8000
    jz .vertical_end
    shl bx,1
.vertical_end:
    dec bx                        ; vertical display end
    mov ax,cx
    dec al
    mov ah,al
    test al,al
    jz .cursor
    dec ah
.cursor:
    mov [0x460],ax
    mov dx,0x3d4
    mov al,0x11
    call vga_read_indexed
    push ax
    and ah,0x7f
    out dx,ax
    mov al,9
    call vga_read_indexed
    and ah,0xe0
    mov al,cl
    dec al
    or ah,al
    mov al,9
    out dx,ax
    mov al,10
    mov ah,[0x461]
    out dx,ax
    mov al,11
    mov ah,[0x460]
    out dx,ax
    mov al,0x12
    mov ah,bl
    out dx,ax
    mov al,7
    call vga_read_indexed
    and ah,0xbd
    test bh,1
    jz .no_bit8
    or ah,2
.no_bit8:
    test bh,2
    jz .no_bit9
    or ah,0x40
.no_bit9:
    out dx,ax
    mov ax,12
    out dx,ax
    mov ax,13
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,al
    mov dx,[0x450]
    call hardware_cursor
    ret

; INT10 AH=12h, BL=30h. This selects only the NEXT mode set.
video_select_scanlines:
    cmp rAL,2
    ja .done
    mov ax,[0x40e]
    mov es,ax
    mov al,rAL
    mov [es:0x20],al
    mov rAL,0x12
.done:
    ret

; AH=1Bh, BX=0, ES:DI=64-byte caller buffer. The static table advertises
; only our implemented modes/functions, rather than a complete commercial VGA.
; Buffer ranges crossing the caller's segment are rejected without a write.
video_functionality:
    mov rAL,0
    ; The IBM VGA state block has no 600/768-line encoding. Do not return a
    ; misleading VGA color/scanline description for our new vendor SVGA modes.
    cmp byte [0x449],0x13
    ja .done
    cmp rBX,0
    jne .done
    mov di,rDI
    cmp di,0xffc0
    ja .done
    mov es,rES
    xor ax,ax
    mov cx,32
    rep stosw
    mov di,rDI
    mov ax,video_functionality_static
    stosw
    mov ax,0xf000
    stosw
    mov si,0x449
    mov cx,30
    rep movsb
    mov al,[0x484]
    inc al
    stosb
    mov ax,[0x485]
    stosw
    mov ax,8
    stosw                         ; active VGA color / no alternate display
    mov ax,16
    cmp byte [0x449],0x13
    jne .colors
    mov ax,256
.colors:
    stosw
    mov al,1                      ; both graphics modes have one page
    cmp byte [0x449],3
    jne .pages
    call video_text_page_count
.pages:
    stosb
    mov al,2
    cmp byte [0x449],3
    jne .graphics_lines
    mov dx,0x3d4
    in al,dx
    push ax
    mov al,9
    call vga_read_indexed
    mov bl,ah
    pop ax
    out dx,al
    mov al,0
    test bl,0x80
    jnz .lines                    ; double-scanned 200-line family
    mov dx,0x3cc
    in al,dx
    and al,0xc0
    cmp al,0x80
    mov al,1
    je .lines
    mov al,2
    jmp .lines
.graphics_lines:
    mov al,3                      ; mode 12h = 480
    cmp byte [0x449],0x12
    je .lines
    xor al,al                     ; mode 13h = 200
.lines:
    stosb
    mov dx,0x3c4
    in al,dx
    push ax
    mov al,3
    call vga_read_indexed
    mov bl,ah
    mov al,ah
    and al,3
    shr ah,2
    and ah,4
    or al,ah
    mov bh,al                     ; map B, primary
    stosb
    mov al,bl
    shr al,2
    and al,3
    shr bl,3
    and bl,4
    or al,bl
    stosb                         ; map A, secondary
    mov bl,al
    cmp bh,bl
    je .single_font
    mov al,4
    call vga_read_indexed
    test ah,2
    jz .single_font
    mov byte [es:di+5],1           ; offset 32h: two fonts active
.single_font:
    pop ax
    out dx,al
    mov dx,0x3c0
    in al,dx
    mov bh,al
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,bh
    and al,0x20
    or al,0x10
    out dx,al
    inc dx
    in al,dx
    and al,8
    shl al,2
    test byte [0x487],1
    jnz .cursor_translation_disabled
    or al,0x10
.cursor_translation_disabled:
    stosb                         ; misc state: actual blink and cursor-translation policy
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,bh
    out dx,al
    mov byte [es:di+3],3           ; offset 31h: 256 KiB
    mov rAL,0x1b
.done:
    ret

video_functionality_static:
    db 0x08,0,0x0c,0,0,0,0,7     ; modes 03/12/13, 200/350/400-line text families
    db 8,2,0x54,0,0,0,1,0        ; 8 font blocks, 2 active; DAC/cursor/font load/512 chars
