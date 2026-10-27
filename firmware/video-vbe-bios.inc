; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOSEBios ET4000AX ISA banked interface, using VBE 1.2 data layouts.
; Bounded subset 00/01/02/03/05, not a complete VBE-conformance implementation.
; VESA VBE Core 2.0 rev 1.1 sections 4.3..4.8 and their explicit 1.x notes.

; Dispatch supported INT10/4F services inside the normal saved-register frame.
video_vbe:
    mov al,rAL
    mov rAX,0x014f
    cmp byte [cs:et4000_present],0
    je .done
    test al,al
    jz .controller
    cmp al,1
    je .mode_info
    cmp al,2
    je .set_mode
    cmp al,3
    je .get_mode
    cmp al,5
    je vbe_window
.done:
    ret
.controller:
    mov si,vbe_controller
    jmp vbe_copy_info
.mode_info:
    mov bx,rCX
    call vbe_find_mode
    jc .done
    jmp vbe_copy_info
.set_mode:
    mov bx,rBX
    test bx,0x7e00             ; reject reserved mode bits and an unavailable LFB
    jnz .done
    and bx,0x7fff
    call vbe_find_mode
    jc .done
    test rBH,0x80
    jz .set
    or al,0x80
.set:
    call set_video_mode
    mov rAX,0x004f
    ret
.get_mode:
    movzx ax,byte [0x449]
    cmp al,0x2e
    jne .get_800
    mov ax,0x101
    jmp .mode_result
.get_800:
    cmp al,0x30
    jne .get_1024
    mov ax,0x103
    jmp .mode_result
.get_1024:
    cmp al,0x38
    jne .mode_result
    mov ax,0x105
.mode_result:
    mov rBX,ax                 ; VBE 1.x does not return the memory-clear flag
    mov rAX,0x004f
    ret

; Resolve one advertised VESA mode without changing any device or caller memory.
vbe_find_mode:
    mov si,vbe_mode101
    mov al,0x2e
    cmp bx,0x101
    je .found
    mov si,vbe_mode103
    mov al,0x30
    cmp bx,0x103
    je .found
    mov si,vbe_mode105
    mov al,0x38
    cmp bx,0x105
    jne .missing
.found:
    clc
    ret
.missing:
    stc
    ret

; Copy exactly 256 immutable ROM bytes after rejecting a segment-crossing buffer.
vbe_copy_info:
    mov di,rDI
    cmp di,0xff00
    ja .done
    mov es,rES
    mov cx,256
    cs rep movsb
    mov rAX,0x004f
.done:
    ret

; Shared bank service: one 64-KiB read/write window, both hardware nibbles coupled.
vbe_window:
    mov rAX,0x014f
    cmp byte [cs:et4000_present],0
    je .done
    cmp rBL,0
    jne .done
    cmp rBH,1
    ja .done
    mov al,[0x449]
    cmp al,0x2e
    je .mode_ok
    cmp al,0x30
    je .mode_ok
    cmp al,0x38
    jne .done
.mode_ok:
    cmp rBH,1
    je .unlock
    cmp rDX,16
    jae .done
.unlock:
    ; Select the actual active decoder without changing Misc-Out or bank state.
    mov dx,0x3bf
    mov al,3
    out dx,al
    mov dx,0x3cc
    in al,dx
    test al,1
    mov dx,0x3b8
    jz .key
    mov dx,0x3d8
.key:
    mov al,0xa0
    out dx,al
    mov dx,0x3cd
    cmp rBH,1
    je .get
    mov ax,rDX
    mov ah,al
    shl al,4
    or al,ah
    out dx,al
    jmp .success
.get:
    in al,dx
    mov ah,al
    ; Reject a raw-port split mapping that this single window cannot describe.
    and al,15
    shr ah,4
    cmp al,ah
    jne .done
    xor ah,ah
    mov rDX,ax
.success:
    mov rAX,0x004f
.done:
    ret

; Far real-mode WinFuncPtr entry: BH/BL/DX are sufficient, even when AX is arbitrary.
vbe_window_far:
    pushf
    enter_service
    call vbe_window
    pop es
    pop ds
    popad
    pop bp
    popf
    retf

; Immutable controller information: no 8-bit DAC, LFB, non-VGA or VBE2 extensions.
vbe_controller:
    db 'VESA'
    dw 0x0102
    dw vbe_oem,0xf000
    dd 0
    dw vbe_modes,0xf000
    dw 16
    times 256-($-vbe_controller) db 0
vbe_modes: dw 0x101,0x103,0x105,0xffff
vbe_oem: db BIOS_NAME,' ',BIOS_VERSION,' / ET4000AX ISA 1 MiB banked subset',0

; Pack only truthful 8-bpp fields; scanline banks differ from CPU aperture pages.
%macro vbe_mode_info 4
%1:
    dw 0x001b                  ; supported, extended info, color, graphics; no TTY/LFB
    db 7,0                     ; window A read/write/relocatable; no window B
    dw 64,64,0xa000,0
    dw vbe_window_far,0xf000
    dw %2,%2,%3
    db 8,16,1,8,1,4,0,%4,1
    times 256-($-%1) db 0
%endmacro
vbe_mode_info vbe_mode101,640,480,2
vbe_mode_info vbe_mode103,800,600,1
vbe_mode_info vbe_mode105,1024,768,0
