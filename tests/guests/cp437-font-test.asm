; Original CP437 ROM-font acceptance under real DOS, Apache-2.0.
; No OS or font bytes. Query ROM pointers, copy their packed data into this
; program's RAM, upload it through AH1110 (also with resident DISPLAY.SYS),
; verify every plane-2 byte and all 256 actual text cells, then return to DOS.
; DISPLAY.SYS can replace even the ROM-pointer query with its active CP850
; table. In that case read this reference platform's documented ROM address;
; this is a guest ROM read, never a host font injection or hook removal.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    xor ax,ax
    mov es,ax
    mov ax,[es:0x485]
    mov [old_points],ax
    mov [checked_points],ax
    mov word [verify_source],old_font_buffer
    mov byte [capture],1
    call transfer_plane
    mov byte [capture],0
    movzx cx,byte [0x80]
    mov si,0x81
.argument:
    jcxz .mode
    lodsb
    cmp al,'8'
    jne .fourteen
    mov word [points],8
.fourteen:
    cmp al,'1'
    jne .nine
    cmp byte [si],'4'
    jne .sixteen
    mov word [points],14
.sixteen:
    cmp byte [si],'6'
    jne .nine
    mov word [points],16
.nine:
    cmp al,'9'
    jne .auto
    mov byte [cell_width],9
.auto:
    and al,0xdf
    cmp al,'A'
    jne .next
    mov byte [automatic],1
.next:
    loop .argument
.mode:
    mov byte [stage],'1'
    mov ax,3
    int 0x10
    mov bx,0x0600
    mov di,0xc000
    cmp word [points],16
    je .query
    mov bx,0x0200
    mov di,0xa800
    cmp word [points],14
    je .query
    mov bx,0x0300
    mov di,0xa000
.query:
    mov ax,0x1130
    int 0x10
    push ax
    mov ax,es
    call debug_hex
    mov ax,bp
    call debug_hex
    mov ax,di
    call debug_hex
    pop ax
    mov ax,es
    cmp ax,0xf000
    jne .reference_rom
    cmp bp,di
    je .copy
.reference_rom:
    mov ax,0xf000
    mov es,ax
    mov bp,di
    mov si,substituted
    call debug
.copy:
    push ds
    mov ds,ax
    mov si,bp
    push cs
    pop es
    mov di,font_buffer
    mov cx,[es:points]
    shl cx,8
    rep movsb
    pop ds
    mov byte [stage],'2'
    mov bh,[points]
    xor bl,bl
    mov cx,256
    xor dx,dx
    mov bp,font_buffer
    mov ax,0x1110
    int 0x10
    xor ax,ax
    mov es,ax
    mov ax,[es:0x485]
    cmp ax,[points]
    jne failed
    mov al,[es:0x484]
    mov [last_row],al
    mov ax,[points]
    mov [checked_points],ax
    mov word [verify_source],font_buffer
    call transfer_plane
    mov byte [stage],'3'
    cmp byte [cell_width],9
    jne .draw
    mov dx,0x3c4
    mov ax,0x0001
    out dx,ax
.draw:
    mov ax,0x0100
    mov cx,0x2000
    int 0x10
    mov ax,0xb800
    mov es,ax
    xor di,di
    movzx cx,byte [last_row]
    inc cx
    imul cx,80
    mov ax,0x1720
    rep stosw
    mov di,4
    mov si,title
    call text
    mov di,164
    mov si,details
    call text
    mov di,324
    mov si,source
    call text
    mov cx,16
    mov di,3*160+12
    xor bx,bx
.column:
    mov al,[hex+bx]
    mov ah,0x1b
    stosw
    add di,6
    inc bx
    loop .column
    xor bx,bx
    mov di,4*160+2
.row:
    mov ax,bx
    shr ax,4
    mov si,ax
    mov al,[hex+si]
    mov ah,0x1b
    stosw
    mov ax,0x1b30
    stosw
    add di,6
    push di
    mov cx,16
.glyph:
    mov al,bl
    mov ah,0x1e
    stosw
    add di,6
    inc bx
    loop .glyph
    pop di
    add di,150
    cmp bx,256
    jb .row
    mov di,21*160+4
    mov ax,0x1fda
    mov dl,0xc4
    mov dh,0xbf
    call horizontal
    mov di,21*160+72
    mov ax,0x1fc9
    mov dl,0xcd
    mov dh,0xbb
    call horizontal
    mov di,22*160+4
    mov ax,0x1fb3
    stosw
    mov si,german
    call text
    mov di,22*160+64
    mov ax,0x1fb3
    stosw
    mov di,22*160+72
    mov ax,0x1fba
    stosw
    mov si,mixed
    call text
    mov di,22*160+132
    mov ax,0x1fba
    stosw
    mov di,23*160+4
    mov ax,0x1fc0
    mov dl,0xc4
    mov dh,0xd9
    call horizontal
    mov di,23*160+72
    mov ax,0x1fc8
    mov dl,0xcd
    mov dh,0xbc
    call horizontal
    mov di,24*160+4
    mov si,footer
    call text
    ; Read all 256 codes/attributes through BIOS AH08 at their real cells.
    xor si,si
.read_cell:
    mov dx,si
    and dl,15
    shl dl,2
    add dl,6
    mov ax,si
    shr ax,4
    add al,4
    mov dh,al
    xor bh,bh
    mov ah,2
    int 0x10
    mov ah,8
    int 0x10
    cmp al,[code]
    jne failed
    cmp ah,0x1e
    jne failed
    inc byte [code]
    inc si
    cmp si,256
    jb .read_cell
    mov ax,0x0200
    xor bx,bx
    xor dx,dx
    int 0x10
    mov si,checked
    call debug
    mov si,ready
    call debug
    cmp byte [automatic],1
    je .return
    xor ax,ax
    int 0x16
.return:
    mov ax,3
    int 0x10
    xor bx,bx
    mov ax,0x1114
    cmp word [old_points],16
    je .restore
    mov ax,0x1111
    cmp word [old_points],14
    je .restore
    mov ax,0x1112
.restore:
    int 0x10
    mov byte [stage],'4'
    mov ax,[old_points]
    mov [checked_points],ax
    mov word [verify_source],old_font_buffer
    call transfer_plane
    mov si,restored
    call debug
    mov dx,returned
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21

transfer_plane:
    mov dx,0x3c4
    in al,dx
    mov [saved_seq_index],al
    mov al,4
    out dx,al
    inc dx
    in al,dx
    mov [saved_s4],al
    dec dx
    mov ax,0x0604
    out dx,ax
    mov dx,0x3ce
    in al,dx
    mov [saved_gc_index],al
    mov al,4
    out dx,al
    inc dx
    in al,dx
    mov [saved_g4],al
    dec dx
    mov ax,0x0204
    out dx,ax
    mov al,5
    out dx,al
    inc dx
    in al,dx
    mov [saved_g5],al
    dec dx
    mov ax,0x0005
    out dx,ax
    mov al,6
    out dx,al
    inc dx
    in al,dx
    mov [saved_g6],al
    dec dx
    mov ax,0x0406
    out dx,ax
    mov ax,0xa000
    mov es,ax
    xor di,di
    mov si,[verify_source]
    mov cx,256
.glyph:
    push cx
    mov cx,[checked_points]
.scanline:
    mov al,[es:di]
    cmp byte [capture],1
    jne .compare
    mov [si],al
.compare:
    cmp al,[si]
    jne failed
    inc si
    inc di
    loop .scanline
    add di,32
    sub di,[checked_points]
    pop cx
    loop .glyph
    mov dx,0x3ce
    mov ah,[saved_g4]
    mov al,4
    out dx,ax
    mov ah,[saved_g5]
    mov al,5
    out dx,ax
    mov ah,[saved_g6]
    mov al,6
    out dx,ax
    mov al,[saved_gc_index]
    out dx,al
    mov dx,0x3c4
    mov ah,[saved_s4]
    mov al,4
    out dx,ax
    mov al,[saved_seq_index]
    out dx,al
    ret
horizontal:
    stosw
    mov al,dl
    mov cx,29
    rep stosw
    mov al,dh
    stosw
    ret
text:
    lodsb
    test al,al
    jz .done
    mov ah,0x1f
    stosw
    jmp text
.done:
    ret
debug:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug
.done:
    ret
debug_hex:
    pushad
    mov cx,4
.digit:
    rol ax,4
    mov bx,ax
    and bx,15
    mov dl,[hex+bx]
    xchg ax,dx
    out 0xe9,al
    xchg ax,dx
    loop .digit
    mov al,' '
    out 0xe9,al
    popad
    ret
failed:
    mov al,[stage]
    out 0xe9,al
    mov si,error
    call debug
    mov ax,3
    int 0x10
    mov dx,error_dos
    mov ah,9
    int 0x21
    mov ax,0x4c01
    int 0x21

old_points: dw 16
last_row: db 24
automatic: db 0
stage: db '0'
code: db 0
saved_s4: db 0
saved_g4: db 0
saved_g5: db 0
saved_g6: db 0
saved_seq_index: db 0
saved_gc_index: db 0
verify_source: dw 0
checked_points: dw 0
capture: db 0
hex: db '0123456789ABCDEF'
title: db 'ORIGINAL CP437 / REFERENCE VGA',0
details: db '256 video glyphs / 8, 14 and 16 rows',0
source: db 'Own artwork / ROM bytes + real VGA plane 2',0
german: db 'Gr',0x81,0xe1,'e: ',0x84,' ',0x94,' ',0x81,' ',0x8e,' ',0x99,' ',0x9a,' ',0xe1,0
mixed: db 'Mixed: ',0xb5,' ',0xb6,' ',0xd1,' ',0xd2,' / ',0xb0,0xb1,0xb2,0xdb,0xdc,0xdd,0xde,0xdf,0
footer: db 'CP437-FONT-READY / ENTER RETURNS',0
checked: db 13,10,'CP437-PLANE-AND-256-CELLS-OK',13,10,0
substituted: db 'CP437-DISPLAY-QUERY-SUBSTITUTED',13,10,0
restored: db 'CP437-ORIGINAL-FONT-RESTORED-OK',13,10,0
ready: db 'CP437-FONT-READY',13,10,0
returned: db 'CP437-DOS-RETURN-OK',13,10,'$'
error: db 'CP437-FONT-FAILED',13,10,0
error_dos: db 'CP437-FONT-FAILED',13,10,'$'
capture_header: db 'CP437-OWN-FONT-V1'
points: dw 16
cell_width: db 8
font_buffer: times 4096 db 0
old_font_buffer: times 8192 db 0
