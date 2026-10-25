; Original reference-platform INT15/AH87 implementation, Apache-2.0.
; Caller supplies 48-byte table, source descriptor at 10h, destination at 18h.
; Reserved EBDA 100h..142h is disjoint from mouse/video state and FDC bounce.
; Copy uses the actual CPU's protected segments and REP MOVSW, never host RAM.
%define bm_gdt       0x100
%define bm_old_gdt   0x130
%define bm_new_gdt   0x136
%define bm_cr0       0x13c
%define bm_ss        0x140
%define bm_a20       0x142

int_block_move:
    mov cx,rCX
    cmp cx,0x8000
    ja .invalid
    test cx,cx
    jz .success
    mov si,rSI
    cmp si,0xffd0
    ja .invalid
    mov ax,rES
    mov es,ax
    ; This BIOS accepts present, ordinary data descriptors (no expand-down).
    ; A read-only source is valid; the destination must be writable.
    mov al,[es:si+0x15]
    and al,0x9c
    cmp al,0x90
    jne .invalid
    mov al,[es:si+0x1d]
    and al,0x9e
    cmp al,0x92
    jne .invalid
    movzx ebx,cx
    shl ebx,1
    dec ebx
    add si,0x10
    call .check_limit
    jc .invalid
    add si,8
    call .check_limit
    jc .invalid
    sub si,8
    ; Copy the two supplied descriptors into the BIOS-owned GDT.
    mov ax,es
    mov ds,ax
    call bios_ebda_segment
    mov es,ax
    mov di,bm_gdt+0x10
    mov cx,8
    rep movsw
    o32 sgdt [es:bm_old_gdt]
    mov eax,cr0
    ; This is the real-mode BIOS service; a V86 monitor handles its own call.
    test al,1
    jnz .invalid
    mov [es:bm_cr0],eax
    mov word [es:bm_new_gdt],47
    mov ax,es
    movzx eax,ax
    shl eax,4
    add eax,bm_gdt
    mov [es:bm_new_gdt+2],eax
    mov dword [es:bm_gdt],0
    mov dword [es:bm_gdt+4],0
    mov dword [es:bm_gdt+8],0x0000ffff
    mov dword [es:bm_gdt+12],0x00009a0f ; base F0000h, 16-bit BIOS code
    mov dword [es:bm_gdt+32],0x0000ffff
    mov dword [es:bm_gdt+36],0x00009200
    mov ax,ss
    mov [es:bm_ss],ax
    movzx eax,ax
    shl eax,4
    mov [es:bm_gdt+34],ax
    shr eax,16
    mov [es:bm_gdt+36],al
    mov dword [es:bm_gdt+40],0x0000ffff
    mov dword [es:bm_gdt+44],0x00009200
    mov ax,es
    movzx eax,ax
    shl eax,4
    mov [es:bm_gdt+42],ax
    shr eax,16
    mov [es:bm_gdt+44],al
    in al,0x92
    mov [es:bm_a20],al
    or al,2
    and al,0xfe
    out 0x92,al
    cli
    mov cx,rCX
    o32 lgdt [es:bm_new_gdt]
    mov eax,[es:bm_cr0]
    or al,1
    mov cr0,eax
    jmp 8:.protected_copy
.check_limit:
    movzx eax,word [es:si]
    movzx edx,byte [es:si+6]
    and dl,15
    shl edx,16
    or eax,edx
    test byte [es:si+6],0x80
    jz .byte_limit
    shl eax,12
    or eax,0xfff
.byte_limit:
    cmp eax,ebx
    ret
.protected_copy:
    mov ax,32
    mov ss,ax
    mov ax,16
    mov ds,ax
    mov ax,24
    mov es,ax
    xor si,si
    xor di,di
    rep movsw
    mov ax,40
    mov ds,ax
    mov es,ax ; real-mode POP ES preserves the hidden limit on a 486
    mov eax,[bm_cr0]
    mov cr0,eax
    jmp 0xf000:.real_return
.real_return:
    mov ax,[bm_ss]
    mov ss,ax
    o32 lgdt [bm_old_gdt]
    mov al,[bm_a20]
    and al,0xfe
    out 0x92,al
.success:
    mov rAH,0
    leave_service
.invalid:
    mov rAH,2
    or rFLAGS,1
    leave_service
