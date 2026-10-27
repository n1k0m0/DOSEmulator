; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS text-page/cursor acceptance, Apache-2.0. Contains no font/OS bytes.
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
    mov ax,[es:0x460]
    mov [old_shape],ax
    mov ax,[es:0x485]
    mov [points],al
    mov al,[es:0x484]
    inc al
    mov [rows],al
    mov ax,[es:0x44c]
    mov [page_bytes],ax
    push cs
    pop es
    mov di,state
    xor bx,bx
    mov ax,0x1b00
    int 0x10
    cmp al,0x1b
    jne failure
    mov al,[state+0x29]
    cmp al,1
    jb failure
    cmp al,8
    ja failure
    mov [pages],al
    dec al
    mov [selected],al
    xor cx,cx
    mov cl,[0x80]
    mov si,0x81
.argument:
    jcxz .begin
    lodsb
    cmp al,'b'
    jne .automatic_argument
    mov byte [staggered],1
.automatic_argument:
    cmp al,'a'
    jne .page_argument
    mov byte [automatic],1
.page_argument:
    cmp al,'0'
    jb .next_argument
    cmp al,'7'
    ja .next_argument
    sub al,'0'
    cmp al,[pages]
    jae failure
    mov [selected],al
.next_argument:
    loop .argument
.begin:
    mov ax,0x0100
    mov cx,0x2000
    int 0x10
    mov byte [page],0
.test_page:
    mov dx,0x0203
    call cursor
    mov ah,9
    mov al,'X'
    mov bh,[page]
    mov bl,0x1e
    mov cx,3
    int 0x10
    mov ah,10
    mov al,'Y'
    mov cx,2
    int 0x10
    mov ax,0x1e59
    call read_check
    mov ah,3
    int 0x10
    cmp dx,0x0203
    jne failure
    mov ah,5
    mov al,[page]
    int 0x10
    mov ah,0x0f
    int 0x10
    cmp bh,[page]
    jne failure
    xor ax,ax
    mov es,ax
    mov al,[page]
    mul word [page_bytes]
    cmp ax,[es:0x44e]
    jne failure
    shr ax,1
    mov [crtc_start],ax
    mov dx,0x3d4
    mov al,12
    out dx,al
    inc dx
    in al,dx
    cmp al,[crtc_start+1]
    jne failure
    dec dx
    mov al,13
    out dx,al
    inc dx
    in al,dx
    cmp al,[crtc_start]
    jne failure
    push cs
    pop es
    mov bp,pairs
    mov cx,(pairs_end-pairs)/2
    mov dx,0x0402
    mov bh,[page]
    mov ax,0x1303
    int 0x10
    mov ah,3
    int 0x10
    cmp dx,0x0501
    jne failure
    mov dx,0x0500
    call cursor
    mov ax,0x1a59
    call read_check
    mov dx,0x0a0a
    call cursor
    mov ax,0x0941
    mov bl,0x1e
    mov cx,3
    int 0x10
    mov dx,0x0b0a
    call cursor
    mov ax,0x0942
    mov bl,0x2f
    int 0x10
    mov ax,0x0601
    mov bh,0x30
    mov cx,0x0a0a
    mov dx,0x0b0c
    int 0x10
    mov dx,0x0a0a
    call cursor
    mov ax,0x2f42
    call read_check
    mov ax,0x0701
    mov bh,0x40
    mov cx,0x0a0a
    mov dx,0x0b0c
    int 0x10
    mov dx,0x0b0a
    call cursor
    mov ax,0x2f42
    call read_check
    mov dx,0x0a0a
    call cursor
    mov ax,0x4020
    call read_check
    mov dx,0x084f
    call cursor
    mov ax,0x0920
    mov bl,0x4e
    mov cx,1
    int 0x10
    mov bx,0x0700                 ; teletype must still use the active page
    mov ax,0x0e5a
    int 0x10
    mov bh,[page]
    mov ah,3
    int 0x10
    cmp dx,0x0900
    jne failure
    mov dx,0x084f
    call cursor
    mov ax,0x4e5a
    call read_check
    mov ah,5
    mov al,[pages]                ; one past the final page must not switch
    int 0x10
    mov ah,0x0f
    int 0x10
    cmp bh,[page]
    jne failure
    inc byte [page]
    mov al,[page]
    cmp al,[pages]
    jb .test_page
    mov si,checked
    call debug
    mov al,[selected]
    mov [page],al
    add al,'0'
    mov [description_page],al
    mov al,[pages]
    add al,'0'
    mov [description_pages],al
    mov al,[rows]
    aam
    add ax,0x3030
    mov [description_rows],ah
    mov [description_rows+1],al
    mov ah,5
    mov al,[page]
    int 0x10
    mov ax,0x0600
    mov bh,0x17
    xor cx,cx
    mov dh,[rows]
    dec dh
    mov dl,79
    int 0x10
    push cs
    pop es
    mov bp,title
    mov cx,title_end-title
    mov dx,0x0102
    mov bl,0x1f
    call string
    mov bp,description
    mov cx,description_end-description
    mov dx,0x0302
    mov bl,0x1e
    call string
    mov bp,explanation
    mov cx,explanation_end-explanation
    mov dx,0x0502
    mov bl,0x17
    call string
    mov bp,colors
    mov cx,(colors_end-colors)/2
    mov dx,0x0702
    mov bh,[page]
    mov ax,0x1302
    int 0x10
    mov dx,0x0902
    call cursor
    mov ax,0x093d
    mov bh,[page]
    mov bl,0x1e
    mov cx,60
    int 0x10
    mov dx,0x0b02
    call cursor
    mov ax,0x0954
    mov bl,0x1b
    mov cx,4
    int 0x10
    mov ax,0x0a65                 ; lowercase e, attributes retained
    int 0x10
    mov bp,ready
    mov cx,ready_end-ready
    mov dh,[rows]
    sub dh,2
    mov dl,2
    mov bl,0x1e
    call string
    mov dx,0x0d05
    call cursor
    mov ax,0x0920
    mov bl,0x2c
    mov cx,1
    int 0x10
    mov cl,[points]
    dec cl
    mov ch,cl
    dec ch
    mov ax,0x0100
    int 0x10
    cmp byte [staggered],0
    je .ready
    ; Own busy delay, keeping headless batches from jumping many timer ticks
    ; through an idle INT16. Sample the other half of the cursor's blink.
    mov bx,32
.blink_delay:
    mov cx,65535
.busy:
    loop .busy
    dec bx
    jnz .blink_delay
.ready:
    mov si,ready
    call debug
    cmp byte [automatic],0
    jne .return
    xor ax,ax
    int 0x16
.return:
    mov ax,0x0500
    int 0x10
    mov ax,0x0600
    mov bh,7
    xor cx,cx
    mov dh,[rows]
    dec dh
    mov dl,79
    int 0x10
    mov byte [page],0
    xor dx,dx
    call cursor
    mov ax,0x0100
    mov cx,[old_shape]
    int 0x10
    mov si,returned
    call debug
    mov ax,0x4c00
    int 0x21
; Positions the cursor through the real guest video BIOS.
cursor:
    mov ah,2
    mov bh,[page]
    int 0x10
    ret
; Reads back and byte-compares the previously formatted/written private sectors.
read_check:
    push ax
    mov ah,8
    mov bh,[page]
    int 0x10
    pop dx
    cmp ax,dx
    jne failure
    ret
; Calls the actual video BIOS string-output service with the explicit test buffer.
string:
    mov bh,[page]
    mov ax,0x1300
    int 0x10
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
failure:
    mov si,failed
    call debug
    mov ax,0x4c01
    int 0x21
page: db 0
pages: db 0
selected: db 0
automatic: db 0
staggered: db 0
rows: db 0
points: db 0
page_bytes: dw 0
old_shape: dw 0
crtc_start: dw 0
state: times 64 db 0
pairs: db 'P',0x2e,'A',0x0e,'G',0x0b,'E',0x0f,13,7,10,7,'X',0x2c,8,7,'Y',0x1a
pairs_end:
title: db 'TEXT PAGES / BIOS INT10'
title_end:
description: db 'PAGE: '
description_page: db '0'
    db ' / PAGES: '
description_pages: db '0'
    db ' / ROWS: '
description_rows: db '00'
description_end:
explanation: db 'Hidden pages keep their own cursor and attributes.'
explanation_end:
colors: db 'C',0x1c,'O',0x1e,'L',0x1a,'O',0x1b,'R',0x1d
colors_end:
ready: db 'TEXT-PAGES-READY'
ready_end: db 0
checked: db 'TEXT-PAGES-CHECKS-OK',13,10,0
returned: db 'TEXT-PAGES-DOS-RETURN-OK',13,10,0
failed: db 'TEXT-PAGES-FAILED',13,10,0
