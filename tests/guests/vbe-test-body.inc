; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS VBE caller, included before the immutable ET4000 probe body.

; Query actual firmware structures, reject unsupported requests and compare all MiB via both bank entries.
vbe_probe:
    pushad
    push es
    push fs
    mov si,vbe_begin
    call debug
    push cs
    pop es
    mov di,vbe_guard_before
    mov cx,(vbe_guard_after+16-vbe_guard_before)/2
    mov ax,0xa7a7
    rep stosw
    mov di,vbe_controller_info
    mov dword [es:di],'VBE2'
    mov ax,0x4f00
    int 0x10
    call vbe_success
    cmp dword [vbe_controller_info],'VESA'
    jne failed
    cmp word [vbe_controller_info+4],0x0102
    jne failed
    cmp dword [vbe_controller_info+10],0
    jne failed
    cmp word [vbe_controller_info+18],16
    jne failed
    mov ax,[vbe_controller_info+16]
    cmp ax,0xf000
    jne failed
    mov fs,ax
    mov si,[vbe_controller_info+14]
    cmp dword [fs:si],0x01030101
    jne failed
    cmp dword [fs:si+4],0xffff0105
    jne failed
    mov di,vbe_mode_info
    mov cx,0x101
.info:
    mov ax,0x4f01
    int 0x10
    call vbe_success
    cmp word [es:di],0x1b
    jne failed
    cmp word [es:di+2],7
    jne failed
    cmp dword [es:di+4],0x00400040
    jne failed
    cmp dword [es:di+8],0xa000
    jne failed
    cmp word [es:di+14],0xf000
    jne failed
    add di,256
    add cx,2
    cmp cx,0x105
    jbe .info
    ; Invalid mode info and unsupported functions must leave the caller buffer unchanged.
    mov di,vbe_rejected_info
    mov cx,0x106
    mov ax,0x4f01
    int 0x10
    call vbe_failure
    mov cx,0x4101
    mov ax,0x4f01
    int 0x10
    call vbe_failure
    mov bx,0x4101
    mov ax,0x4f02
    int 0x10
    call vbe_failure
    mov si,vbe_unsupported
    mov cx,6
.unsupported:
    lodsb
    mov ah,0x4f
    int 0x10
    call vbe_failure
    loop .unsupported
    ; Use byte accesses near FFFFh; our stack is deliberately below these bytes.
    mov di,0xff01
    mov cx,255
    mov al,0x9d
    rep stosb
    mov di,0xff01
    mov ax,0x4f00
    int 0x10
    call vbe_failure
    mov cx,0x101
    mov ax,0x4f01
    int 0x10
    call vbe_failure
    mov cx,255
    mov al,0x9d
    repe scasb
    jne failed
    mov word [vbe_boundary_bytes],255
    mov bx,TEST_VBE
    mov ax,0x4f02
    int 0x10
    call vbe_success
    inc word [vbe_mode_sets]
    mov ax,0x4f03
    int 0x10
    call vbe_success
    cmp bx,TEST_VBE
    jne failed
    mov [vbe_current],bx
    xor bx,bx
    mov dx,16
    mov ax,0x4f05
    int 0x10
    call vbe_failure
    mov bx,1
    xor dx,dx
    mov ax,0x4f05
    int 0x10
    call vbe_failure
    mov bx,0x200
    mov ax,0x4f05
    int 0x10
    call vbe_failure
    mov ax,0xa000
    mov es,ax
    mov word [vbe_bank],0
.fill:
    xor bx,bx
    mov dx,[vbe_bank]
    mov ax,0x4f05
    int 0x10
    call vbe_success
    mov bx,0x100
    mov dx,0xffff
    mov ax,0x4f05
    int 0x10
    call vbe_success
    cmp dx,[vbe_bank]
    jne failed
    mov si,dx
    shl si,1
    mov [vbe_bank_values+si],dx
    mov ax,dx
    add ax,0xb000
    xor di,di
    mov cx,0x8000
    rep stosw
    inc word [vbe_bank]
    cmp word [vbe_bank],16
    jb .fill
    mov word [vbe_bank],15
.read:
    mov dx,[vbe_bank]
    xor bx,bx
    mov ax,0xdead               ; VBE 1.x far entry accepts BH/BL/DX, not an AX sentinel
    call far [vbe_mode_info+12]
    call vbe_success
    mov bx,0x100
    mov ax,0x4f05
    int 0x10
    call vbe_success
    cmp dx,[vbe_bank]
    jne failed
    mov si,dx
    shl si,1
    mov [vbe_far_values+si],dx
    mov ax,dx
    add ax,0xb000
    mov [vbe_word_values+si],ax
    xor di,di
    mov cx,0x8000
    repe scasw
    jne failed
    inc word [vbe_compared_banks]
    dec word [vbe_bank]
    jns .read
    push cs
    pop es
    mov di,vbe_guard_before
    mov cx,16
    mov al,0xa7
    repe scasb
    jne failed
    mov di,vbe_guard_after
    mov cx,16
    repe scasb
    jne failed
    mov di,vbe_rejected_info
    mov cx,256
    repe scasb
    jne failed
    mov word [vbe_complete],0xbeef
    mov si,vbe_end
    call debug
    pop fs
    pop es
    popad
    ret

; Count only an actual successful return; all other statuses fail the guest.
vbe_success:
    cmp ax,0x004f
    jne failed
    inc word [vbe_success_count]
    ret

; Count the explicit unsupported/rejected status rather than a stale original AX.
vbe_failure:
    cmp ax,0x014f
    jne failed
    inc word [vbe_failure_count]
    ret

vbe_begin db 'VBE-BEGIN',13,10,0
vbe_end db 'VBE-END',13,10,0
vbe_unsupported db 4,6,7,8,9,10
