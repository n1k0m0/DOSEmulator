; Own bounded mode-03h text services, Apache-2.0. No host BIOS shortcuts.
; IBM PS/2 BIOS Interface May 1988, pp. 2-16..2-21, 2-28, 2-37, 2-41.
; DS=0. BDA page size may be compact after a character-generator call;
; never assume that it remains the mode-set default of 4096 bytes.

; AH12/BL34: IBM BIOS Interface 2-34; Phoenix System BIOS pp. 35/245.
; BDA INFO bit 0 INHIBITS translation; preserve the other seven bits.
; The switch affects subsequent AH01 requests, not the current CRTC shape.
video_cursor_emulation:
    cmp rAL,1
    ja .done
    and byte [0x487],0xfe
    mov al,rAL
    or [0x487],al
    mov rAL,0x12
.done:
    ret

; CX=requested shape. BDA/AH03 keep the unscaled caller values.
; Own reference-platform rule: map an ordered, flag-free 0..7 interval to
; [floor(start*height/8), floor((end+1)*height/8)-1]. This preserves complete
; rows and inclusive endpoints. Native (>7), reversed and control-bit forms
; pass through; emulation is bounded to mode 03h and cell heights 8..32.
; IBM documents scaling but not its rounding algorithm: no clone equality
; with undocumented vendor cursor heuristics is claimed.
video_cursor_shape:
    mov [0x460],cx
    cmp byte [0x449],3
    jne .graphics
    test byte [0x487],1
    jnz .program
    test cx,0xf8f8
    jnz .program
    cmp ch,cl
    ja .program
    cmp word [0x485],8
    jb .program
    cmp word [0x485],32
    ja .program
    movzx ax,ch
    mul word [0x485]
    shr ax,3
    mov ch,al
    movzx ax,cl
    inc ax
    mul word [0x485]
    shr ax,3
    dec ax
    mov cl,al
    jmp .program
.graphics:
    cmp byte [0x449],0x12
    je .hide
    cmp byte [0x449],0x13
    jne .done
.hide:
    or ch,0x20                   ; no hardware cursor in graphics modes
.program:
    mov dx,[0x463]
    in al,dx
    push ax
    mov ah,ch
    mov al,10
    out dx,ax
    mov ah,cl
    mov al,11
    out dx,ax
    pop ax
    out dx,al
.done:
    ret

; AL=page. CF=0: BX=byte base, SI=BDA cursor word; DX/CX preserved.
; Our eight BIOS cursor slots and the 32-KiB B8000 aperture bound the pages.
video_text_page:
    push cx
    push dx
    cmp byte [0x449],3
    jne .invalid
    cmp al,8
    jae .invalid
    cmp word [0x44c],0
    je .invalid
    movzx cx,al
    mov si,cx
    shl si,1
    add si,0x450
    mov ax,[0x44c]
    mul cx
    test dx,dx
    jnz .invalid
    mov bx,ax
    add ax,[0x44c]
    jc .invalid
    cmp ax,0x8000
    ja .invalid
    pop dx
    pop cx
    clc
    ret
.invalid:
    pop dx
    pop cx
    stc
    ret

; AL=page, DX=row/column. CF=0: DI=absolute text byte offset,
; BX=page base, SI=cursor word. Invalid positions never alias another page.
video_text_position:
    call video_text_page
    jc .done
    cmp dh,[0x484]
    ja .invalid
    movzx di,dl
    cmp di,[0x44a]
    jae .invalid
    movzx ax,dh
    imul ax,[0x44a]
    add di,ax
    shl di,1
    mov ax,di
    add ax,2
    cmp ax,[0x44c]
    ja .invalid
    add di,bx
    clc
.done:
    ret
.invalid:
    stc
    ret

; Graphics modes retain a software page-0 cursor for later graphics services.
; They do not acquire text pages or write to the B8000 aperture.
video_cursor_page:
    cmp byte [0x449],3
    je video_text_page
    test al,al
    jnz .invalid
    cmp byte [0x449],0x12
    je .graphics
    cmp byte [0x449],0x13
    jne .invalid
.graphics:
    xor bx,bx
    mov si,0x450
    clc
    ret
.invalid:
    stc
    ret

video_cursor_position:
    call video_cursor_page
    jc .done
    cmp dh,[0x484]
    ja .invalid
    movzx ax,dl
    cmp ax,[0x44a]
    jae .invalid
    clc
.done:
    ret
.invalid:
    stc
    ret

; Return AX=number of addressable text pages (at most eight).
video_text_page_count:
    push dx
    mov ax,0x8000
    xor dx,dx
    cmp word [0x44c],0
    je .none
    div word [0x44c]
    cmp ax,8
    jbe .done
    mov ax,8
    jmp .done
.none:
    xor ax,ax
.done:
    pop dx
    ret

; AL=page, mode 03h only. Preserve the service-frame BP and CPU registers.
video_text_select_page:
    call video_text_page
    jc .done
    mov ax,si
    sub ax,0x450
    shr ax,1
    mov [0x462],al
    mov [0x44e],bx
    shr bx,1                     ; CRTC addresses character/attribute plane words
    mov dx,[0x463]
    in al,dx
    push ax
    mov ah,bh
    mov al,12
    out dx,ax
    mov ah,bl
    mov al,13
    out dx,ax
    pop ax
    out dx,al
    mov dx,[si]
    call hardware_cursor
.done:
    ret

; DS=0, DX=active page cursor coordinates. Keep the CRTC index intact.
hardware_cursor:
    push ax
    push bx
    push dx
    movzx ax,dh
    mov bx,ax
    imul bx,[0x44a]
    movzx ax,dl
    add bx,ax
    mov ax,[0x44e]
    shr ax,1
    add bx,ax
    mov dx,[0x463]
    in al,dx
    push ax
    mov ah,bh
    mov al,14
    out dx,ax
    mov ah,bl
    mov al,15
    out dx,ax
    pop ax
    out dx,al
    pop dx
    pop bx
    pop ax
    ret

; BX=page byte base, CH/CL=top/left, DH/DL=bottom/right (validated),
; AL=line count (zero or >= window height clears), AH=0 up/1 down,
; SI=blank attribute. All CPU registers and segments preserved.
video_text_scroll_region:
    push bp
    mov bp,sp
    sub sp,14
    mov [ss:bp-2],bx
    mov [ss:bp-4],ax
    mov [ss:bp-6],cx
    mov [ss:bp-8],dx
    mov [ss:bp-10],si
    mov ax,[0x44a]
    mov [ss:bp-12],ax
    pushad
    push ds
    push es
    mov ax,0xb800
    mov ds,ax
    mov es,ax
    movzx dx,byte [ss:bp-5]       ; top row
    cmp byte [ss:bp-3],0
    je .row
    movzx dx,byte [ss:bp-7]       ; down: visit bottom to top
.row:
    mov ax,dx
    imul ax,[ss:bp-12]
    movzx di,byte [ss:bp-6]       ; left column
    add di,ax
    shl di,1
    add di,[ss:bp-2]
    movzx cx,byte [ss:bp-8]       ; right column
    movzx ax,byte [ss:bp-6]
    sub cx,ax
    inc cx
    movzx ax,byte [ss:bp-4]       ; number of lines
    test ax,ax
    jz .blank
    mov bx,dx
    cmp byte [ss:bp-3],0
    jne .source_down
    add bx,ax
    movzx ax,byte [ss:bp-7]
    cmp bx,ax
    ja .blank
    jmp .copy
.source_down:
    sub bx,ax
    jc .blank
    movzx ax,byte [ss:bp-5]
    cmp bx,ax
    jb .blank
.copy:
    imul bx,[ss:bp-12]
    movzx si,byte [ss:bp-6]
    add si,bx
    shl si,1
    add si,[ss:bp-2]
    rep movsw
    jmp .next
.blank:
    mov ax,[ss:bp-10]
    mov ah,al
    mov al,' '
    rep stosw
.next:
    cmp byte [ss:bp-3],0
    jne .next_down
    inc dx
    cmp dl,[ss:bp-7]
    jbe .row
    jmp .done
.next_down:
    dec dx
    js .done
    cmp dl,[ss:bp-5]
    jae .row
.done:
    pop es
    pop ds
    popad
    mov sp,bp
    pop bp
    ret

; AL=character, AH=attribute, DI=0 keep cell attribute / 1 write it.
; BX=page base, SI=BDA cursor slot, DX=validated cursor coordinates.
; Updates that page's cursor; no hardware switch while writing hidden pages.
video_text_emit:
    pushad
    push ds
    push es
    mov cx,ax
    mov bp,si
    mov si,di
    cmp cl,13
    je .cr
    cmp cl,10
    je .lf
    cmp cl,8
    je .bs
    cmp cl,7
    je .bell
    movzx ax,dh
    imul ax,[0x44a]
    movzx di,dl
    add di,ax
    shl di,1
    add di,bx
    mov ax,0xb800
    mov es,ax
    mov [es:di],cl
    test si,si
    jz .advance
    mov [es:di+1],ch
.advance:
    inc dl
    cmp dl,[0x44a]
    jb .store
    xor dl,dl
.lf:
    inc dh
    cmp dh,[0x484]
    jbe .store
    push dx
    xor cx,cx
    mov dh,[0x484]
    mov dl,[0x44a]
    dec dl
    mov ax,1
    mov si,7
    call video_text_scroll_region
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
    mov [ds:bp],dx
    jmp .done
.bell:
    call speaker_bell
.done:
    pop es
    pop ds
    popad
    ret

; DS=0, AL=character. Text teletype uses the active page, preserves
; existing cell attributes and treats CR/LF/BS/BEL as commands.
teletype:
    pushad
    push es
    out 0xe9,al
    push ax
    mov al,[0x462]
    call video_text_page
    jc .invalid
    mov dx,[si]
    mov al,[0x462]
    call video_text_position
    jc .invalid
    pop ax
    xor di,di
    call video_text_emit
    mov dx,[si]
    call hardware_cursor
    jmp .done
.invalid:
    pop ax
    cmp al,7                     ; BEL remains useful in a graphics mode
    jne .done
    call speaker_bell
.done:
    pop es
    popad
    ret

; Called in INT10's service frame. AH13, AL=0..3, text only.
; ES:BP source offsets wrap bytewise within the supplied segment.
video_text_string:
    cmp rAL,3
    ja .done
    cmp rCX,0
    je .done
    mov al,rBH
    mov dx,rDX
    call video_text_position
    jc .done
    push word [si]
    mov [si],dx
    mov es,rES
    mov di,[ss:bp]                ; original caller BP
    mov cx,rCX
.character:
    mov al,[es:di]
    inc di
    mov ah,rBL
    test rAL,2
    jz .attribute
    mov ah,[es:di]
    inc di
.attribute:
    mov dx,[si]
    push di
    mov di,1
    call video_text_emit
    pop di
    loop .character
    pop ax
    test rAL,1
    jnz .cursor
    mov [si],ax
.cursor:
    mov al,rBH
    cmp al,[0x462]
    jne .done
    mov dx,[si]
    call hardware_cursor
.done:
    ret
