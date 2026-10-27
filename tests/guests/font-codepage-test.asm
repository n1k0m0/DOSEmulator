; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS font acceptance sheet, Apache-2.0. This file contains no font data.
; The real DISPLAY.SYS/MODE must have loaded the selected font from guest EGA.CPI.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    movzx cx,byte [0x80]
    mov si,0x81
.size_argument:
    jcxz .metadata
    lodsb
    cmp al,'1'
    jne .next_size
    cmp byte [si],'4'
    jne .next_size
    ; MS-DOS MODE supports 25/43/50 lines. Also exercise the BIOS 8x14
    ; path directly; resident DISPLAY.SYS supplies its prepared CP850 font.
    mov ax,0x0083
    int 0x10
    mov ax,0x1111
    xor bx,bx
    int 0x10
    jmp .metadata
.next_size:
    loop .size_argument
.metadata:
    xor ax,ax
    mov es,ax
    mov cx,[es:0x460]
    mov [old_cursor],cx
    mov al,[es:0x484]
    mov [last_row],al
    mov ax,0x0100
    mov cx,0x2000
    int 0x10
    mov ax,0xb800
    mov es,ax
    xor di,di
    movzx cx,byte [last_row]
    inc cx
    imul cx,80
    mov ax,0x0720
    rep stosw
    mov di,4
    mov si,title
    call text
    mov di,164
    mov si,explanation
    call text
    xor bx,bx
    mov di,3*160+4
.glyph_row:
    push di
    mov cx,16
.glyph:
    mov al,bl
    mov ah,7
    stosw
    add di,6
    inc bx
    loop .glyph
    pop di
    add di,160
    cmp bx,256
    jb .glyph_row
    mov di,20*160+4
    mov si,umlauts
    call text
    mov di,21*160+4
    mov ax,0x07da
    stosw
    mov al,0xc4
    mov cx,40
    rep stosw
    mov al,0xbf
    stosw
    mov di,22*160+4
    mov ax,0x07b3
    stosw
    mov si,box_text
    call text
    mov di,22*160+86
    mov ax,0x07b3
    stosw
    mov di,23*160+4
    mov ax,0x07c0
    stosw
    mov al,0xc4
    mov cx,40
    rep stosw
    mov al,0xd9
    stosw
    mov di,24*160+4
    mov si,source
    call text
    mov si,ready
.debug:
    lodsb
    test al,al
    jz .argument
    out 0xe9,al
    jmp .debug
.argument:
    xor cx,cx
    mov cl,[0x80]
    mov si,0x81
.next_argument:
    jcxz .wait
    lodsb
    and al,0xdf
    cmp al,'A'
    je .return
    loop .next_argument
.wait:
    xor ax,ax
    int 0x16
.return:
    mov ax,0x0100
    mov cx,[old_cursor]
    int 0x10
    mov ax,0x0600
    mov bh,7
    xor cx,cx
    mov dh,[last_row]
    mov dl,79
    int 0x10
    xor dx,dx
    mov ah,2
    xor bh,bh
    int 0x10
    mov dx,returned
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
; Prints the explicit guest text through the real DOS/BIOS path.
text:
    lodsb
    test al,al
    jz .done
    mov ah,7
    stosw
    jmp text
.done:
    ret
old_cursor: dw 0
last_row: db 24
title: db 'VGA FONT / ORIGINAL DOS DISPLAY.SYS + MODE',0
explanation: db '256 GLYPHS / CP850 / ESC OR ENTER RETURNS TO DOS',0
umlauts: db 'CP850: ',0x84,' ',0x94,' ',0x81,' ',0x8e,' ',0x99,' ',0x9a,' ',0xe1,0
box_text: db 'DOS-Zeichensatz: ',0x84,0x94,0x81,' / ',0x8e,0x99,0x9a,' / ',0xe1,0
source: db 'Guest font source: C:\DOS\EGA.CPI',0
ready: db 13,10,'FONT-CP-READY',13,10,0
returned: db 'FONT-DOS-RETURN-OK',13,10,'$'
