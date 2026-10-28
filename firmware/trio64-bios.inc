; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own Trio64 mode transition. All target changes are actual guest I/O;
; PCI resources, wake/POS, VRAM and the original four ROM variants are retained.

; Leave implemented enhanced/banked addressing only after the caller has validated a supported legacy mode.
trio64_vga_reset:
    push ax
    push bx
    push cx
    push dx
    push si
    mov dx,0x3cc
    in al,dx
    test al,1
    mov dx,0x3b4
    jz .crtc_ready
    mov dx,0x3d4
.crtc_ready:
    mov al,0x38
    out dx,al
    inc dx
    in al,dx
    dec dx
    push ax
    mov ax,0x4838
    out dx,ax
    mov al,0x39
    out dx,al
    inc dx
    in al,dx
    dec dx
    push ax
    mov ax,0xa539
    out dx,ax
    mov al,0x40
    out dx,al
    inc dx
    in al,dx
    dec dx
    push ax
    or al,1
    mov ah,al
    mov al,0x40
    out dx,ax
    push dx
    mov dx,0x4ae8
    xor ax,ax
    out dx,ax
    pop dx
    mov si,trio64_legacy_crtc
    mov cx,trio64_legacy_crtc_end-trio64_legacy_crtc
.clear_crtc:
    mov al,[cs:si]
    xor ah,ah
    out dx,ax
    inc si
    loop .clear_crtc
    push dx
    mov dx,0x3c4
    mov al,8
    out dx,al
    inc dx
    in al,dx
    dec dx
    push ax
    mov ax,0x0608
    out dx,ax
    mov si,trio64_legacy_seq
    mov cx,(trio64_legacy_seq_end-trio64_legacy_seq)/2
.clear_seq:
    mov bx,[cs:si]
    mov al,bl
    out dx,al
    inc dx
    in al,dx
    and al,bh
    dec dx
    mov ah,al
    mov al,bl
    out dx,ax
    add si,2
    loop .clear_seq
    pop ax
    mov ah,al
    mov al,8
    out dx,ax
    pop dx
    pop ax
    mov ah,al
    mov al,0x40
    out dx,ax
    pop ax
    mov ah,al
    mov al,0x39
    out dx,ax
    pop ax
    mov ah,al
    mov al,0x38
    out dx,ax
    pop si
    pop dx
    pop cx
    pop bx
    pop ax
    ret

; Clear bank, geometry overflow, palette locks, linear/MMIO request and enhanced pixel format; preserve BAR position.
trio64_legacy_crtc:
    db 0x31,0x32,0x33,0x35,0x3a,0x42,0x43,0x50,0x51,0x53,0x58,0x5d,0x5e,0x67,0x69,0x6a
trio64_legacy_crtc_end:
; Clear only the implemented raw-write bypass and DAC power-down bits, retaining unrelated clock/control latches.
trio64_legacy_seq:
    db 0x0a,0x7f,0x15,0x7f,0x18,0xdf
trio64_legacy_seq_end:
