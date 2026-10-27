; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own real-DOS VGA graphics font acceptance, Apache-2.0.
; -DTEST_MODE=0x12 or 0x13. No ROM/OS/font bytes are included.
; Snapshot the font that the guest's DISPLAY.SYS actually loaded in plane 2,
; then use only BIOS graphics services with this private conventional-RAM copy.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    xor ax,ax
    mov es,ax
    mov al,[es:0x484]
    mov [old_last],al
    mov ax,[es:0x485]
    mov [old_points],ax
    mov ax,[es:0x7c]
    mov [old_1f],ax
    mov ax,[es:0x7e]
    mov [old_1f+2],ax
    mov si,0x81
    movzx cx,byte [0x80]
.argument:
    jcxz .font
    lodsb
    cmp al,'a'
    jne .eight
    mov byte [automatic],1
.eight:
    cmp al,'8'
    jne .fourteen
    mov word [requested],0x1112
.fourteen:
    cmp al,'1'
    jne .next_argument
    cmp byte [si],'4'
    jne .sixteen
    mov word [requested],0x1111
.sixteen:
    cmp byte [si],'6'
    jne .next_argument
    mov word [requested],0x1114
.next_argument:
    loop .argument
.font:
    mov ax,0x0083
    int 0x10
    xor bx,bx
    mov ax,[requested]
    int 0x10
    xor ax,ax
    mov es,ax
    mov ax,[es:0x485]
    mov [points],ax
    cmp ax,8
    je .snapshot
    cmp ax,14
    je .snapshot
    cmp ax,16
    jne failed
.snapshot:
    call snapshot_font
    mov ax,TEST_MODE
    int 0x10
    mov ax,TEST_MODE
%if TEST_MODE = 0x12
    mov word [columns],80
    mov ax,480
%else
    mov word [columns],40
    mov ax,200
%endif
    xor dx,dx
    div word [points]
    mov [rows],al
    mov dl,al
    mov cx,[points]
    push cs
    pop es
    mov bp,font_buffer
    xor bx,bx
    mov ax,0x1121
    int 0x10
    mov bx,0x0100
    mov ax,0x1130
    int 0x10
    mov ax,es
    push cs
    pop dx
    cmp ax,dx
    jne failed
    cmp bp,font_buffer
    jne failed
    cmp cx,[points]
    jne failed
    mov ax,0x1120
    push cs
    pop es
    mov bp,empty_table
    int 0x10
    xor bx,bx
    mov ax,0x1130
    int 0x10
    cmp bp,empty_table
    jne failed
    call first_q_pixel
    mov dx,0x0103
    call cursor
    mov bx,5
%if TEST_MODE = 0x13
    ; Two full-byte DAC indices, independently of their standard palette colors.
    mov ax,0x1010
    mov bx,201
    mov dx,0x3f00
    xor cx,cx
    int 0x10
    mov bx,37
    xor dx,dx
    mov cx,63
    int 0x10
    mov bx,37*256+201
%endif
    mov ax,0x0951
    mov cx,1
    int 0x10
    mov al,'Q'
    call read_match
    call sample_pixel
%if TEST_MODE = 0x12
    cmp al,5
    jne failed
    mov bx,0x83
    mov ax,0x0951
    mov cx,1
    int 0x10
    call sample_pixel
    cmp al,6
    jne failed
    mov ax,0x0951
    mov bx,0x83
    mov cx,1
    int 0x10
    call sample_pixel
    cmp al,5
    jne failed
%else
    cmp al,201
    jne failed
    mov cx,24
    mov dx,[points]
    mov ax,0x0d00
    xor bx,bx
    int 0x10
    cmp al,37
    jne failed
%endif
    ; A third color is direct framebuffer evidence, never a cached character.
    mov cx,[sample_x]
    mov dx,[sample_y]
    mov ax,0x0c0d
    int 0x10
    xor bx,bx
    mov ax,0x0800
    int 0x10
    cmp al,0
    jne failed
    ; Repetition is bounded by the current row, even for CX=FFFFh.
    mov dh,2
    mov dl,[columns]
    dec dl
    call cursor
    mov ax,0x0951
    mov bx,14
    mov cx,0xffff
    int 0x10
    mov cx,[sample_x]
    sub cx,24
    mov ax,[points]
    shl ax,1
    mov dx,[sample_y]
    add dx,ax
    mov ax,0x0d00
    xor bx,bx
    int 0x10
    cmp al,0
    jne failed
    ; Both vertical directions, rectangular copy and opaque blank color.
    mov dx,0x0301
    call cursor
    mov bx,3
    mov cx,1
    mov ax,0x0941
    int 0x10
    mov dx,0x0401
    call cursor
    mov bx,4
    mov cx,1
    mov ax,0x0942
    int 0x10
    mov ax,0x0601
    mov bh,5
    mov cx,0x0301
    mov dx,0x0402
    int 0x10
    mov dx,0x0301
    call cursor
    mov al,'B'
    call read_match
    mov ax,0x0701
    mov bh,6
    mov cx,0x0301
    mov dx,0x0402
    int 0x10
    mov dx,0x0401
    call cursor
    mov al,'B'
    call read_match
    mov ax,[points]
    imul ax,3
    mov dx,ax
    mov cx,8
    xor bx,bx
    mov ax,0x0d00
    int 0x10
    cmp al,6
    jne failed
    call clear
    ; Teletype must wrap and scroll pixels, not only its software cursor.
    mov dh,[rows]
    dec dh
    mov dl,[columns]
    dec dl
    call cursor
    mov bx,0x070e
    mov ax,0x0e41
    int 0x10
    xor bx,bx
    mov ah,3
    int 0x10
    cmp dl,0
    jne failed
    mov al,[rows]
    dec al
    cmp dh,al
    jne failed
    dec dh
    mov dl,[columns]
    dec dl
    call cursor
    mov al,'A'
    call read_match
    ; AH13 graphics forms 0/1 include CR/LF/BS and optional cursor movement.
    push cs
    pop es
    mov bp,controls
    mov cx,controls_end-controls
    xor dx,dx
    mov bx,14
    mov ax,0x1301
    int 0x10
    xor bx,bx
    mov ah,3
    int 0x10
    cmp dx,0x0101
    jne failed
    mov dx,0x0100
    call cursor
    mov al,'C'
    call read_match
    mov si,checked
    call debug
    call clear
    mov si,title
    mov dx,2
    mov bl,14
    call puts
    mov ax,[points]
    aam
    add ax,0x3030
    mov [description_points],ah
    mov [description_points+1],al
    mov al,[rows]
    aam
    add ax,0x3030
    mov [description_rows],ah
    mov [description_rows+1],al
    mov si,description
    mov dx,0x0102
    mov bl,11
    call puts
    mov si,features
    mov dx,0x0202
    mov bl,7
    call puts
    xor bp,bp
.sheet:
    mov ax,bp
    xor dx,dx
    mov cx,32
    div cx
%if TEST_MODE = 0x12
    shl dl,1
%endif
    add dl,2
    mov dh,al
    add dh,3
    call cursor
    mov ax,bp
    xor dx,dx
    mov cx,15
    div cx
    mov bx,dx
    inc bx
    mov ax,bp
    mov ah,9
    mov cx,1
    int 0x10
    mov ax,bp
    call read_match
    inc bp
    cmp bp,256
    jb .sheet
    mov si,ready
    mov dh,[rows]
    dec dh
    mov dl,2
    mov bl,14
    call puts
    xor dx,dx
    call cursor
    mov si,all_checked
    call debug
    mov si,ready
    call debug
    cmp byte [automatic],0
    jne finish
    xor ax,ax
    int 0x16
finish:
    mov bx,0x30
    mov ax,0x1202
    cmp byte [old_last],42
    jne .family
    mov ax,0x1201
.family:
    int 0x10
    mov ax,3
    int 0x10
    cmp word [old_points],16
    je .vector
    xor bx,bx
    mov ax,0x1112
    cmp word [old_points],8
    je .restore_font
    mov ax,0x1111
.restore_font:
    int 0x10
.vector:
    mov es,[old_1f+2]
    mov bp,[old_1f]
    mov ax,0x1120
    int 0x10
    mov si,returned
    call debug
    mov al,[exit_code]
    mov ah,0x4c
    int 0x21
failed:
    mov si,failure
    call debug
    mov byte [exit_code],1
    jmp finish
; Positions the cursor through the real guest video BIOS.
cursor:
    xor bh,bh
    mov ah,2
    int 0x10
    ret
; Invokes the real video BIOS to clear the selected graphics test surface.
clear:
    mov ax,0x0600
    xor bx,bx
    xor cx,cx
    mov dh,[rows]
    dec dh
    mov dl,[columns]
    dec dl
    int 0x10
    ret
; Reads an actual graphics BIOS pixel for the independent glyph oracle.
sample_pixel:
    mov cx,[sample_x]
    mov dx,[sample_y]
    xor bx,bx
    mov ax,0x0d00
    int 0x10
    ret
; Reads the selected reference glyph pixel through the real graphics BIOS path.
first_q_pixel:
    mov ax,'Q'
    imul ax,[points]
    add ax,font_buffer
    mov si,ax
    xor di,di
    mov cx,[points]
.row:
    lodsb
    test al,al
    jnz .bits
    inc di
    loop .row
    jmp failed
.bits:
    mov bx,24
.bit:
    shl al,1
    jc .found
    inc bx
    jmp .bit
.found:
    mov [sample_x],bx
    add di,[points]
    mov [sample_y],di
    ret
; AH08 returns the first matching bitmap, including duplicate or complementary
; glyphs. Uniform cells cannot reveal the original code. Check pixel shape.
read_match:
    pushad
    mov [wanted],al
    xor bx,bx
    mov ah,8
    int 0x10
    movzx si,al
    imul si,[points]
    add si,font_buffer
    movzx di,byte [wanted]
    imul di,[points]
    add di,font_buffer
    mov al,[si]
    xor al,[di]
    cmp al,0
    je .polarity
    cmp al,0xff
    jne failed
.polarity:
    mov dl,al
    mov cx,[points]
.row:
    mov al,[si]
    xor al,[di]
    cmp al,dl
    jne failed
    inc si
    inc di
    loop .row
    popad
    ret
; Prints a zero-terminated string through the actual guest output service.
puts:
    pushad
    push es
    mov bp,si
    mov di,si
    xor cx,cx
.length:
    cmp byte [di],0
    je .draw
    inc di
    inc cx
    jmp .length
.draw:
    push cs
    pop es
    xor bh,bh
    mov ax,0x1300
    int 0x10
    pop es
    popad
    ret
; Writes one actual guest debug-port byte for the independent evidence trace.
debug:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug
.done:
    ret
; Copies actual VGA font-plane bytes into the probe's own saved-state buffer.
snapshot_font:
    pushad
    push ds
    push es
    mov dx,0x3c4
    in al,dx
    push ax
    mov al,4
    out dx,al
    inc dx
    in al,dx
    push ax
    mov al,6
    out dx,al
    mov dx,0x3ce
    in al,dx
    push ax
    mov al,4
    call save_gc
    mov al,5
    call save_gc
    mov al,6
    call save_gc
    mov ax,0x0204
    out dx,ax
    mov ax,5
    out dx,ax
    mov ax,0x0406
    out dx,ax
    mov ax,0xa000
    mov ds,ax
    push cs
    pop es
    xor si,si
    mov di,font_buffer
    mov bx,256
.glyph:
    mov cx,[cs:points]
    rep movsb
    add si,32
    sub si,[cs:points]
    dec bx
    jnz .glyph
    pop ax
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,ax
    pop ax
    out dx,al
    mov dx,0x3c4
    pop ax
    mov ah,al
    mov al,4
    out dx,ax
    pop ax
    out dx,al
    pop es
    pop ds
    popad
    ret
; Keep the context below our return address: read/save an indexed GC pair.
save_gc:
    pop bx
    out dx,al
    mov ah,al
    inc dx
    in al,dx
    dec dx
    xchg al,ah
    push ax
    push bx
    ret
requested: dw 0x1114
points: dw 16
old_points: dw 16
old_last: db 24
old_1f: dd 0
rows: db 0
columns: dw 0
sample_x: dw 0
sample_y: dw 0
wanted: db 0
automatic: db 0
exit_code: db 0
controls: db 'A',13,10,'B',8,'C'
controls_end:
title: db 'VGA GRAPHICS FONT / BIOS',0
description: db 'MODE '
%if TEST_MODE = 0x12
    db '12'
%else
    db '13'
%endif
    db ' / 8x'
description_points: db '00'
    db ' / '
description_rows: db '00'
    db ' ROWS',0
features: db 'SCROLL / READBACK OK',0
ready: db 'GFX-FONT-READY',0
checked: db 'GFX-FONT-SERVICES-OK',13,10,0
all_checked: db 'GFX-FONT-256-GLYPHS-OK',13,10,0
failure: db 'GFX-FONT-FAILED',13,10,0
returned: db 'GFX-FONT-DOS-RETURN-OK',13,10,0
empty_table: times 1024 db 0
font_buffer: times 8192 db 0
