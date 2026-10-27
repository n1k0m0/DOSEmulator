; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original LIM EMS client: eight 16-KiB pages, real remapping and aliasing.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov [proof+56],eax
    mov ah,0x40
    int 0x67
    test ah,ah
    jnz failed
    mov ah,0x46
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+24],al
    mov ah,0x41
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+28],bx
    cmp bx,0xc000
    jb failed
    cmp bx,0xe000
    ja failed
    mov ah,0x42
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+32],bx
    mov [proof+34],dx
    mov bx,8
    mov ah,0x43
    int 0x67
    test ah,ah
    jnz failed
    mov [handle],dx
    mov [proof+36],dx
    mov byte [allocated],1
    mov ah,0x42
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+38],bx
    or dword [proof+16],1
    ; Fill all logical pages through slot zero, recording every 4-KiB backing.
    xor bx,bx
.logical:
    push bx
    mov al,0
    call map_page
    jc failed
    mov ax,[proof+28]
    mov es,ax
    xor di,di
    mov cx,4096
    movzx eax,bx
    imul eax,eax,0x11111111
    add eax,0x12345678
    mov ebx,eax
.fill:
    stosd
    add eax,0x01020305
    loop .fill
    pop bx
    push bx
    shl bx,4
    mov si,proof+64
    add si,bx
    mov cx,[proof+28]
    shr cx,8
    ; Frame segment / 256 = first 4-KiB linear page number.
    mov bx,4
.physical:
    push cx
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov [si],edx
    add si,4
    pop cx
    inc cx
    dec bx
    jnz .physical
    pop bx
    inc bx
    cmp bx,8
    jb .logical
    or dword [proof+16],2
    ; Reverse bank order; each map invalidates a previously cached translation.
    mov bx,7
.reverse:
    mov al,0
    call map_page
    jc failed
    call check_page
    jc failed
    dec bx
    jns .reverse
    or dword [proof+16],4
    ; Four simultaneous slots; save, replace and restore their actual mappings.
    xor bx,bx
.slots:
    mov ax,bx
    call map_page
    jc failed
    inc bx
    cmp bx,4
    jb .slots
    mov dx,[handle]
    mov ah,0x47
    int 0x67
    test ah,ah
    jnz failed
    mov byte [saved_map],1
    xor ax,ax
.alias:
    push ax
    mov bx,7
    call map_page
    jc failed
    pop ax
    inc ax
    cmp ax,4
    jb .alias
    ; Slot 1 write must be visible through all four aliases, at the page end.
    mov ax,[proof+28]
    mov es,ax
    mov dword [es:0x7ffc],0xa55a3cc3
    mov si,0x3ffc
    mov cx,4
.alias_check:
    cmp dword [es:si],0xa55a3cc3
    jne failed
    add si,0x4000
    loop .alias_check
    ; Restore page seven's final dword, keeping all eight full pages verifiable.
    mov eax,(0x12345678+7*0x11111111+4095*0x01020305) & 0xffffffff
    mov [es:0x3ffc],eax
    mov dx,[handle]
    mov ah,0x48
    int 0x67
    test ah,ah
    jnz failed
    mov byte [saved_map],0
    mov byte [check_slots],1
    xor bx,bx
.restored:
    call check_page
    jc failed
    inc bx
    cmp bx,4
    jb .restored
    or dword [proof+16],8
    call release_handle
    cmp byte [allocated],0
    jne failed
    mov ah,0x42
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+40],bx
    cmp bx,[proof+32]
    jne failed
    or dword [proof+16],16
    mov dword [proof+12],1
    jmp report
failed:
    mov [proof+20],eax
    mov [proof+44],bx
    call release_handle
report:
    push cs
    pop es
    mov si,marker
    call debug_string
    xor ebx,ebx
    mov bx,cs
    shl ebx,4
    add ebx,proof
    mov cx,8
.hex:
    rol ebx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .digit
    add al,7
.digit:
    out 0xe9,al
    loop .hex
    mov si,newline
    call debug_string
    mov dx,success
    cmp dword [proof+12],1
    je .print
    mov dx,failure
.print:
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
; Maps the explicitly selected EMS page through the actual memory-manager service.
map_page:
    ; AL = frame slot, BX = logical page. Preserve the requested operands.
    push ax
    push bx
    mov dx,[handle]
    mov ah,0x44
    int 0x67
    test ah,ah
    pop bx
    pop ax
    jnz .error
    inc dword [proof+48]
    clc
    ret
.error:
    stc
    ret
; Compares the actual EMS window bytes against the independently initialized page pattern.
check_page:
    ; For BX 0..3, use the matching physical slot; otherwise use slot zero.
    push bx
    push es
    mov ax,[proof+28]
    ; After restore, use each simultaneous slot; during reverse use slot zero.
    cmp byte [check_slots],0
    je .segment
    mov dx,bx
    shl dx,10
    add ax,dx
.segment:
    mov es,ax
    movzx eax,bx
    imul eax,eax,0x11111111
    add eax,0x12345678
    xor si,si
    mov cx,4096
.check:
    cmp [es:si],eax
    jne .bad
    add si,4
    add eax,0x01020305
    loop .check
    inc dword [proof+52]
    pop es
    pop bx
    clc
    ret
.bad:
    pop es
    pop bx
    stc
    ret
; Releases the probe's actual EMS allocation through the resident manager.
release_handle:
    cmp byte [allocated],0
    je .done
    mov dx,[handle]
    cmp byte [saved_map],0
    je .free
    mov ah,0x48
    int 0x67
.free:
    mov ah,0x45
    int 0x67
    test ah,ah
    jnz .done
    mov byte [allocated],0
.done:
    ret
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret
handle dw 0
allocated db 0
saved_map db 0
check_slots db 0
marker db 'EMS-BANK-PROOF=',0
newline db 13,10,0
success db 'EMS-BANK-PASS',13,10,'$'
failure db 'EMS-BANK-FAIL',13,10,'$'
align 4,db 0
proof:
    db 'CPEMS001'
    dd 1
    times 61 dd 0
    db 'CPEMADDR'
    dw start,handle,proof,map_page,check_page
