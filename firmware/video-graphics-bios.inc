; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own VGA 12h/13h character services, Apache-2.0.
; IBM PS/2 BIOS Interface May 1988, pp. 2-18..2-20, 2-29/30, 2-37.
; All glyphs are read from the actual INT43 guest pointer. No shadow text map.

; Save every changed VGA data register AND both index latches on the stack.
; The caller owns general registers/segments and enters with DS=0.
%macro graphics_enter 0
    mov dx,0x3c4
    in al,dx
    push ax
    mov al,2
    call vga_read_indexed
    push ax
    mov al,4
    call vga_read_indexed
    push ax
    mov ax,0x0f02
    out dx,ax
    mov ax,0x0604
    cmp byte [0x449],0x13
    jne %%memory
    mov ah,0x0e
%%memory:
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
    out dx,ax
    mov ax,1
    out dx,ax
    mov ax,3
    out dx,ax
    mov ax,4
    out dx,ax
    mov ax,0x0205                ; planar write mode 2 expands a four-bit color
    cmp byte [0x449],0x13
    jne %%mode
    mov ah,0x40                  ; indexed chain-4, write/read mode 0
%%mode:
    out dx,ax
    mov ax,0x0506
    out dx,ax
    mov ax,0xff08
    out dx,ax
%endmacro
%macro graphics_leave 0
    mov dx,0x3ce
    mov cx,7
%%gc:
    pop ax
    out dx,ax
    loop %%gc
    pop ax
    out dx,al
    mov dx,0x3c4
    pop ax
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,al
%endmacro

; DS=0. CF=0: AX=rows, BX=columns, CX=points, DX=pixel height.
; DOSEBios services support fitting eight-wide cells in canonical modes.
video_graphics_geometry:
    mov bx,80
    mov dx,480
    cmp byte [0x449],0x12
    je .mode
    cmp byte [0x449],0x13
    jne .invalid
    mov bx,40
    mov dx,200
.mode:
    cmp bx,[0x44a]
    jne .invalid
    mov cx,[0x485]
    cmp cx,1
    jb .invalid
    cmp cx,32
    ja .invalid
    movzx ax,byte [0x484]
    inc ax
    imul ax,cx
    cmp ax,dx
    ja .invalid
    movzx ax,byte [0x484]
    inc ax
    clc
    ret
.invalid:
    stc
    ret

; DS=0, AL=character, BX=background/foreground (background only for 13h),
; DX=row/column. Every caller register/segment is preserved.
video_graphics_glyph:
    push bp
    mov bp,sp
    sub sp,16
    pushad
    push ds
    push es
    mov [ss:bp-2],bx
    mov [ss:bp-4],dx
    mov [ss:bp-6],al
    call video_graphics_geometry
    jc .done
    mov dx,[ss:bp-4]
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    mov [ss:bp-8],cx
    mov al,[0x449]
    mov [ss:bp-12],al
    mov ax,[0x43*4+2]
    mov [ss:bp-14],ax
    movzx ax,byte [ss:bp-6]
    imul ax,cx
    add ax,[0x43*4]
    mov si,ax                    ; packed glyph source offset wraps in segment
    movzx ax,dh
    imul ax,cx
    mov cx,80
    movzx di,dl
    cmp byte [ss:bp-12],0x12
    je .address
    mov cx,320
    shl di,3
.address:
    imul ax,cx
    add di,ax
    graphics_enter
    mov ax,0xa000
    mov es,ax
    mov ax,[ss:bp-14]
    mov ds,ax
    mov cx,[ss:bp-8]
    cmp byte [ss:bp-12],0x13
    je .indexed_row
    test byte [ss:bp-2],0x80
    jz .planar_row
    mov ax,0x1803
    out dx,ax
.planar_row:
    lodsb
    mov bl,al
    test byte [ss:bp-2],0x80
    jnz .planar_foreground
    mov ax,0xff08
    out dx,ax
    mov al,[es:di]
    mov byte [es:di],0            ; replace mode also clears glyph background
.planar_foreground:
    mov ah,bl
    mov al,8
    out dx,ax
    mov al,[es:di]               ; real VGA latch load before the masked write
    mov al,[ss:bp-2]
    mov [es:di],al
    add di,80
    loop .planar_row
    jmp .restore
.indexed_row:
    lodsb
    mov ah,al
    mov bl,8
.indexed_pixel:
    mov al,[ss:bp-1]
    shl ah,1
    jnc .indexed_store
    mov al,[ss:bp-2]
.indexed_store:
    stosb
    dec bl
    jnz .indexed_pixel
    add di,312
    loop .indexed_row
.restore:
    graphics_leave
.done:
    pop es
    pop ds
    popad
    mov sp,bp
    pop bp
    ret

; DS=0, AX=direction/line count (AH 0 up, 1 down), BL=blank color,
; CX=top/left and DX=bottom/right in character cells. All registers preserved.
; Planar copies use actual read latches/write mode 1; no host pixel copying.
video_graphics_scroll_region:
    push bp
    mov bp,sp
    sub sp,24
    pushad
    push ds
    push es
    mov [ss:bp-2],ax
    mov [ss:bp-4],cx
    mov [ss:bp-6],dx
    mov [ss:bp-8],bx
    call video_graphics_geometry
    jc .done
    mov ax,[ss:bp-4]
    mov dx,[ss:bp-6]
    cmp ah,dh
    ja .done
    cmp al,dl
    ja .done
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    movzx ax,byte [ss:bp-3]
    imul ax,cx
    mov [ss:bp-14],ax            ; top raster row
    movzx ax,byte [ss:bp-5]
    inc ax
    imul ax,cx
    dec ax
    mov [ss:bp-16],ax            ; inclusive bottom raster row
    movzx ax,byte [ss:bp-2]
    imul ax,cx
    mov [ss:bp-20],ax            ; displacement in raster rows
    movzx ax,byte [ss:bp-4]
    mov [ss:bp-18],ax            ; left byte (one per planar character)
    movzx ax,byte [ss:bp-6]
    sub ax,[ss:bp-18]
    inc ax
    mov [ss:bp-12],ax            ; bytes per window scanline
    mov word [ss:bp-10],80
    mov al,[0x449]
    mov [ss:bp-24],al
    cmp al,0x13
    jne .start
    shl word [ss:bp-18],3
    shl word [ss:bp-12],3
    mov word [ss:bp-10],320
.start:
    mov ax,[ss:bp-14]
    cmp byte [ss:bp-1],0
    je .current
    mov ax,[ss:bp-16]
.current:
    mov [ss:bp-22],ax
    graphics_enter
    mov ax,0xa000
    mov ds,ax
    mov es,ax
.scanline:
    mov ax,[ss:bp-22]
    imul ax,[ss:bp-10]
    add ax,[ss:bp-18]
    mov di,ax
    mov cx,[ss:bp-12]
    mov ax,[ss:bp-20]
    test ax,ax
    jz .blank
    mov bx,[ss:bp-22]
    cmp byte [ss:bp-1],0
    jne .source_down
    add bx,ax
    cmp bx,[ss:bp-16]
    ja .blank
    jmp .copy
.source_down:
    sub bx,ax
    jc .blank
    cmp bx,[ss:bp-14]
    jb .blank
.copy:
    imul bx,[ss:bp-10]
    add bx,[ss:bp-18]
    mov si,bx
    cmp byte [ss:bp-24],0x13
    je .copy_bytes
    mov ax,0x0105
    out dx,ax
.copy_bytes:
    rep movsb
    jmp .next
.blank:
    cmp byte [ss:bp-24],0x13
    je .blank_bytes
    mov ax,0x0205
    out dx,ax
.blank_bytes:
    mov al,[ss:bp-8]
    rep stosb
.next:
    mov ax,[ss:bp-22]
    cmp byte [ss:bp-1],0
    jne .next_down
    inc ax
    cmp ax,[ss:bp-16]
    ja .restore
    jmp .store_next
.next_down:
    dec ax
    js .restore
    cmp ax,[ss:bp-14]
    jb .restore
.store_next:
    mov [ss:bp-22],ax
    jmp .scanline
.restore:
    graphics_leave
.done:
    pop es
    pop ds
    popad
    mov sp,bp
    pop bp
    ret

; DS=0, AL=character, BL=foreground, DX=cursor. Background is black.
; Control characters act on the software cursor, scrolling its text viewport.
video_graphics_emit:
    pushad
    xor bh,bh
    cmp al,13
    je .cr
    cmp al,10
    je .lf
    cmp al,8
    je .bs
    cmp al,7
    je .bell
    call video_graphics_glyph
    inc dl
    cmp dl,[0x44a]
    jb .store
    xor dl,dl
.lf:
    inc dh
    cmp dh,[0x484]
    jbe .store
    push dx
    mov ax,1
    xor bx,bx
    xor cx,cx
    mov dh,[0x484]
    mov dl,[0x44a]
    dec dl
    call video_graphics_scroll_region
    pop dx
    mov dh,[0x484]
    jmp .store
.cr:
    xor dl,dl
    jmp .store
.bs:
    test dl,dl
    jz .store
    dec dl
.store:
    mov [0x450],dx
    jmp .done
.bell:
    call speaker_bell
.done:
    popad
    ret

; The following wrappers use INT10's outer enter_service frame.
video_graphics_character_write:
    call video_graphics_geometry
    jc .done
    cmp byte [0x449],0x13
    je .indexed_page
    cmp rBH,0
    jne .done
    jmp .cursor
.indexed_page:
    cmp rAH,0x0a
    jne .cursor                   ; AH09 uses BH as indexed background color
    cmp rBH,0
    jne .done                     ; AH0A alias: page 0, black background
.cursor:
    mov dx,[0x450]
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    movzx ax,dl
    sub bx,ax
    mov cx,rCX
    cmp cx,bx
    jbe .count
    mov cx,bx                    ; IBM specifies repetitions on the same row
.count:
    jcxz .done
    mov bx,rBX
    mov al,rAL
.character:
    call video_graphics_glyph
    inc dl
    loop .character
.done:
    ret

video_graphics_scroll:
    mov al,rAL
    mov ah,rAH
    sub ah,6
    movzx bx,rBH
    mov cx,rCX
    mov dx,rDX
    call video_graphics_scroll_region
    ret

video_graphics_tty:
    mov al,rAL
    out 0xe9,al
    call video_graphics_geometry
    jc .done
    mov dx,[0x450]
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    mov bx,rBX
    mov al,rAL
    call video_graphics_emit
.done:
    ret

video_graphics_string:
    cmp rAL,1                    ; attribute pairs are alpha-only in IBM AH13
    ja .done
    cmp rBH,0
    jne .done
    cmp rCX,0
    je .done
    call video_graphics_geometry
    jc .done
    mov dx,rDX
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    push word [0x450]
    mov [0x450],dx
    mov es,rES
    mov di,[ss:bp]
    mov cx,rCX
    mov bx,rBX
.character:
    mov al,[es:di]
    inc di
    mov dx,[0x450]
    call video_graphics_emit
    loop .character
    pop ax
    test rAL,1
    jnz .done
    mov [0x450],ax
.done:
    ret

; Normalize actual pixel colors to one bit per pixel. A cell with more than
; two colors cannot be an opaque BIOS glyph. First-color polarity is resolved
; when comparing the entire bitmap with each font candidate.
%macro graphics_collect_pixel 0
    shl byte [ss:bp-20],1
    cmp word [ss:bp-14],0xffff
    jne %%known
    mov [ss:bp-14],al
    mov byte [ss:bp-13],0
    jmp %%done
%%known:
    cmp al,[ss:bp-14]
    je %%done
    or byte [ss:bp-20],1
    cmp word [ss:bp-16],0xffff
    jne %%second
    mov [ss:bp-16],al
    mov byte [ss:bp-15],0
    jmp %%done
%%second:
    cmp al,[ss:bp-16]
    jne .unrecognized
%%done:
%endmacro

; DS=0, DX=row/column. AX=first matching glyph code or zero if unrecognized.
; Every other register/segment is preserved. At most 32 normalized glyph rows
; are held on the guest stack; there is no retained character/attribute cache.
video_graphics_read_glyph:
    push bp
    mov bp,sp
    sub sp,60
    pushad
    push ds
    push es
    mov [ss:bp-2],dx
    mov word [ss:bp-28],0
    call video_graphics_geometry
    jc .done
    mov dx,[ss:bp-2]
    cmp dh,[0x484]
    ja .done
    cmp dl,bl
    jae .done
    mov [ss:bp-4],cx
    mov ax,[0x43*4]
    mov [ss:bp-6],ax
    mov ax,[0x43*4+2]
    mov [ss:bp-8],ax
    mov al,[0x449]
    mov [ss:bp-12],al
    movzx ax,dh
    imul ax,cx
    mov cx,80
    movzx di,dl
    cmp byte [ss:bp-12],0x12
    je .address
    mov cx,320
    shl di,3
.address:
    imul ax,cx
    add di,ax
    mov word [ss:bp-14],0xffff
    mov word [ss:bp-16],0xffff
    graphics_enter
    mov ax,0xa000
    mov es,ax
    xor si,si
.row:
    mov byte [ss:bp-20],0
    mov cx,8
    cmp byte [ss:bp-12],0x13
    je .indexed_pixel
    mov ax,4
    out dx,ax
    mov al,[es:di]
    mov [ss:bp-24],al
    mov ax,0x0104
    out dx,ax
    mov al,[es:di]
    mov [ss:bp-23],al
    mov ax,0x0204
    out dx,ax
    mov al,[es:di]
    mov [ss:bp-22],al
    mov ax,0x0304
    out dx,ax
    mov al,[es:di]
    mov [ss:bp-21],al
    mov bl,0x80
.planar_pixel:
    xor al,al
    test [ss:bp-24],bl
    jz .plane1
    or al,1
.plane1:
    test [ss:bp-23],bl
    jz .plane2
    or al,2
.plane2:
    test [ss:bp-22],bl
    jz .plane3
    or al,4
.plane3:
    test [ss:bp-21],bl
    jz .color
    or al,8
.color:
    graphics_collect_pixel
    shr bl,1
    loop .planar_pixel
    add di,80
    jmp .sampled
.indexed_pixel:
    mov al,[es:di]
    inc di
    graphics_collect_pixel
    loop .indexed_pixel
    add di,312
.sampled:
    mov al,[ss:bp-20]
    mov [ss:bp+si-60],al
    inc si
    cmp si,[ss:bp-4]
    jb .row
    graphics_leave
    mov ax,[ss:bp-8]
    mov ds,ax
    xor di,di
.candidate:
    mov ax,di
    imul ax,[ss:bp-4]
    add ax,[ss:bp-6]
    mov bx,ax
    xor si,si
    xor dx,dx
    mov al,[bx]
    cmp al,[ss:bp-60]
    je .compare
    not al
    cmp al,[ss:bp-60]
    jne .next_candidate
    mov dl,0xff
.compare:
    mov al,[bx]
    xor al,dl
    cmp al,[ss:bp+si-60]
    jne .next_candidate
    inc bx
    inc si
    cmp si,[ss:bp-4]
    jb .compare
    mov [ss:bp-28],di
    jmp .done
.next_candidate:
    inc di
    cmp di,256
    jb .candidate
    jmp .done
.unrecognized:
    graphics_leave
.done:
    pop es
    pop ds
    popad
    mov ax,[ss:bp-28]
    mov sp,bp
    pop bp
    ret

video_graphics_character_read:
    cmp rBH,0
    jne .done
    mov dx,[0x450]
    call video_graphics_read_glyph
    mov rAX,ax                   ; AH is undefined for graphics; reference returns 0
.done:
    ret
