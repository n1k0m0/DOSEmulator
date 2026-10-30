; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOSEBios Trio64 banked VBE1.2-layout subset for the separate Windows95 board.
; VESA VBE2.0 revision1.1 sections4.3..4.8 and explicit1.x notes; S3 DB014-B table11-2/3/4.
; No original S3 ROM identity, VBE2 LFB interface, PLL timing or Windows acceptance is claimed.
; EBDA22h owns the last successful BIOS mode only when BDA449h contains our6Fh sentinel.

%define TVBE_RECORD_BYTES 16
%define TVBE_CURRENT_MODE 0x22

; Dispatch only the explicitly implemented real-mode/far-call ABI services.
video_vbe:
    mov al,rAL
    mov rAX,0x014f
    test al,al
    jz .controller
    cmp al,1
    je .info
    cmp al,2
    je .set
    cmp al,3
    je .get
    cmp al,5
    je tvbe_window
    ret
.controller:
    mov si,tvbe_controller
    jmp tvbe_copy
.info:
    mov bx,rCX
    call tvbe_find
    jc .done
    mov si,[cs:si+14]
    jmp tvbe_copy
.set:
    mov bx,rBX
    test bx,0x7c00              ; no VBE2 LFB flag or unspecified high mode bits
    jnz .done
    and bx,0x03ff              ; own2xx aliases are explicit OEM extensions, not reserved-mode masking
    call tvbe_find
    jc .done
    call tvbe_set
    jc .done
    mov bx,rBX
    and bx,0x03ff
    mov ax,[0x40e]
    mov es,ax
    mov [es:TVBE_CURRENT_MODE],bx
    mov byte [0x449],0x6f
    mov rAX,0x004f
    ret
.get:
    movzx bx,byte [0x449]
    cmp bl,0x6f
    jne .mode
    mov ax,[0x40e]
    mov es,ax
    mov bx,[es:TVBE_CURRENT_MODE]
    call tvbe_find
    jc .done
.mode:
    mov rBX,bx
    mov rAX,0x004f
.done:
    ret

; Resolve only one fully implemented mode before changing hardware or caller memory.
tvbe_find:
    mov si,tvbe_records
    mov cx,(tvbe_records_end-tvbe_records)/TVBE_RECORD_BYTES
.search:
    cmp bx,[cs:si]
    je .found
    add si,TVBE_RECORD_BYTES
    loop .search
    stc
    ret
.found:
    clc
    ret

; Copy one complete256-byte1.x structure only after bounding the entire far buffer.
tvbe_copy:
    mov di,rDI
    cmp di,0xff00
    ja .done
    mov es,rES
    mov cx,256
    cs rep movsb
    mov rAX,0x004f
.done:
    ret

; Unlock the actual color CRTC using the documented keys and enable enhanced-register access.
tvbe_unlock:
    mov dx,0x3d4
    mov ax,0x4838
    out dx,ax
    mov ax,0xa539
    out dx,ax
    mov al,0x40
    call vga_read_indexed
    or ah,1
    out dx,ax
    ret

; Program an actual shared-VRAM mode through guest ports; never inject pixels or synthesize a successful BIOS result.
tvbe_set:
    ; The existing93h path restores common VGA/DAC state while preserving every VRAM byte.
    mov al,0x93
    call set_video_mode
    mov dx,0x3c4
    mov ax,0x2101
    out dx,ax
    call tvbe_unlock
    mov al,0x30
    call vga_read_indexed
    cmp ah,0xe1
    jne .failed                ; absent/gated targets cannot report a working mode
    mov ax,0x0035
    out dx,ax
    mov ax,0x0931
    out dx,ax
    mov ax,0x153a
    out dx,ax
    mov ax,0x0042
    out dx,ax
    mov ax,0x0043
    out dx,ax
    mov ax,0x0051
    out dx,ax
    mov ax,0x0053
    out dx,ax
    mov ax,0x0258               ;2MiB LAW size, disabled; the actual bank window remains A0000h
    out dx,ax
    mov ax,0x005d
    out dx,ax
    mov al,0x5e
    mov ah,[cs:si+11]           ; vertical bit10 overflows belong to this mode, not its byte pitch
    out dx,ax
    mov ax,0x0069
    out dx,ax
    mov ax,0x006a
    out dx,ax
    push si
    mov si,[cs:si+12]
    mov ax,0x0011
    out dx,ax
    mov cx,25
    call vga_write_indexed_table
    pop si
    mov bx,[cs:si+6]            ; documented byte pitch /8 across CR13 and CR51
    shr bx,3
    mov al,0x13
    mov ah,bl
    out dx,ax
    mov al,0x51
    mov ah,bh
    shl ah,4
    out dx,ax
    mov al,0x50
    mov ah,[cs:si+10]           ; graphics-engine width and storage length match the scanout
    out dx,ax
    mov al,0x67
    mov ah,[cs:si+9]            ; indexed,555,565 or BGRx32 RAMDAC format
    out dx,ax
    mov dx,0x3c4
    mov ax,0x0f02
    out dx,ax
    mov ax,0x0e04
    out dx,ax
    mov dx,0x3ce
    mov ax,0x0000
    out dx,ax
    mov ax,0x0001
    out dx,ax
    mov ax,0x0003
    out dx,ax
    mov ax,0x4005
    out dx,ax
    mov ax,0x0506
    out dx,ax
    mov ax,0xff08
    out dx,ax
    mov dx,0x4ae8
    mov ax,1
    out dx,ax
    mov dx,0x3da
    in al,dx
    mov dx,0x3c0
    mov al,0x10
    out dx,al
    mov al,1
    out dx,al
    mov al,0x20
    out dx,al
    test rBH,0x80
    jnz .retained
    mov ax,0xa000
    mov es,ax
    xor bx,bx
.clear:
    mov dx,0x3d4
    mov al,0x6a
    mov ah,bl
    out dx,ax
    xor di,di
    xor ax,ax
    mov cx,0x8000
    rep stosw                  ; exactly one populated64KiB bank via normal VGA write logic
    inc bl
    cmp bl,32
    jb .clear
.retained:
    mov dx,0x3d4
    mov ax,0x006a
    out dx,ax
    mov al,0x67
    call vga_read_indexed
    cmp ah,[cs:si+9]
    jne .failed
    mov bx,[cs:si+2]
    shr bx,3
    mov [0x44a],bx
    mov word [0x44c],0xffff
    mov word [0x44e],0
    mov byte [0x462],0
    mov word [0x485],16
    mov bx,[cs:si+4]
    shr bx,4
    dec bl
    mov [0x484],bl
    mov dx,0x3c4
    mov ax,0x0101
    out dx,ax
    clc
    ret
.failed:
    stc
    ret

; Set/get one shared32-bank window; reject bad flags/banks before hardware changes.
tvbe_window:
    mov rAX,0x014f
    cmp rBL,0
    jne .done
    cmp rBH,1
    ja .done
    cmp byte [0x449],0x6f
    jne .done
    cmp rBH,1
    je .get
    cmp rDX,32
    jae .done
    call tvbe_unlock
    mov al,0x35
    call vga_read_indexed
    and ah,0xf0
    out dx,ax
    mov al,0x51
    call vga_read_indexed
    and ah,0xf3
    out dx,ax
    mov ax,rDX
    mov ah,al
    mov al,0x6a
    out dx,ax
    mov al,0x6a
    call vga_read_indexed
    cmp ah,rDL
    jne .done
    jmp .success
.get:
    call tvbe_unlock
    mov al,0x6a
    call vga_read_indexed
    mov bl,ah
    test bl,bl
    jnz .value
    mov al,0x35
    call vga_read_indexed
    mov bl,ah
    and bl,15
    mov al,0x51
    call vga_read_indexed
    and ah,12
    shl ah,2
    or bl,ah
.value:
    cmp bl,32
    jae .done
    movzx bx,bl
    mov rDX,bx
.success:
    mov rAX,0x004f
.done:
    ret

; Direct WinFuncPtr uses actual BH/BL/DX and returns with RETF, without an AX sentinel.
tvbe_window_far:
    pushf
    enter_service
    call tvbe_window
    pop es
    pop ds
    popad
    pop bp
    popf
    retf

; Own truthful1.2 data and ROM pointers, never an S3 commercial-ROM signature.
tvbe_controller:
    db 'VESA'
    dw 0x0102
    dw tvbe_oem,0xf000
    dd 0
    dw tvbe_modes,0xf000
    dw 32
    times 256-($-tvbe_controller) db 0
tvbe_oem: db BIOS_NAME,' ',BIOS_VERSION,' / own Trio64 banked subset',0
tvbe_modes: dw 0x101,0x103,0x105,0x107,0x110,0x111,0x114,0x117,0x201,0x203,0x205,0x207,0x211,0x214,0x220,0xffff

; Identifier,width,height,byte-pitch,storage-bytes,CR67,CR50,CR5E,CRTC-pointer,info-pointer.
; The existing16-byte record and pointer offsets12/14 are retained.
%macro tvbe_record 10
    dw %1,%2,%3,%4
    db %5,%6,%7,%8
    dw %9,%10
%endmacro
tvbe_records:
    tvbe_record 0x101,640,480,640,1,0,0x40,0,tvbe_crtc_640,tvbe_info_640_8
    tvbe_record 0x103,800,600,800,1,0,0x80,0,tvbe_crtc_800,tvbe_info_800_8
    tvbe_record 0x105,1024,768,1024,1,0,0,0,tvbe_crtc_1024,tvbe_info_1024_8
    tvbe_record 0x107,1280,1024,1280,1,0,0xc0,0x15,tvbe_crtc_1280,tvbe_info_1280_8
    tvbe_record 0x110,640,480,1280,2,0x30,0x50,0,tvbe_crtc_640,tvbe_info_640_555
    tvbe_record 0x111,640,480,1280,2,0x50,0x50,0,tvbe_crtc_640,tvbe_info_640_565
    tvbe_record 0x114,800,600,1600,2,0x50,0x90,0,tvbe_crtc_800,tvbe_info_800_565
    tvbe_record 0x117,1024,768,2048,2,0x50,0x10,0,tvbe_crtc_1024,tvbe_info_1024_565
    tvbe_record 0x201,640,480,640,1,0,0x40,0,tvbe_crtc_640,tvbe_info_640_8
    tvbe_record 0x203,800,600,800,1,0,0x80,0,tvbe_crtc_800,tvbe_info_800_8
    tvbe_record 0x205,1024,768,1024,1,0,0,0,tvbe_crtc_1024,tvbe_info_1024_8
    tvbe_record 0x207,1280,1024,1280,1,0,0xc0,0x15,tvbe_crtc_1280,tvbe_info_1280_8
    tvbe_record 0x211,640,480,1280,2,0x50,0x50,0,tvbe_crtc_640,tvbe_info_640_565
    tvbe_record 0x214,800,600,1600,2,0x50,0x90,0,tvbe_crtc_800,tvbe_info_800_565
    tvbe_record 0x220,640,480,2560,4,0xd0,0x70,0,tvbe_crtc_640,tvbe_info_640_32
tvbe_records_end:

; Primary documented addressing with own nominal noninterlaced raster tables, not calibrated S3 PLLs.
tvbe_crtc_640: db 0x5f,0x4f,0x50,0x82,0x54,0x80,0x0b,0x3e,0,0,0,0,0,0,0,0,0xea,0x0c,0xdf,0x50,0x60,0xe7,6,0xab,0xff
tvbe_crtc_800: db 0x7f,0x63,0x64,0x82,0x68,0x17,0x72,0xf0,0,0x60,0,0,0,0,0,0,0x58,0x0c,0x57,0x64,0x60,0x58,0x72,0xab,0xff
tvbe_crtc_1024: db 0xa1,0x7f,0x80,0x04,0x88,0x9e,0x26,0xfd,0,0x60,0,0,0,0,0,0,0x08,0x0a,0xff,0x80,0x60,0x04,0x22,0xab,0xff
; DE03FFh, total1064lines, blank start0400h, sync start0401h; CR5E15h supplies their bit10s.
tvbe_crtc_1280: db 0xce,0x9f,0xa0,0x92,0xa6,0x14,0x26,0x52,0,0x40,0,0,0,0,0,0,0x01,0x04,0xff,0xa0,0x60,0x00,0x27,0xab,0xff

; VBE1.2 reports storage bits;555 therefore has16 total bits and one unused source bit.
%macro tvbe_info 14
%1:
    dw 0x001b
    db 7,0
    dw 64,64,0xa000,0
    dw tvbe_window_far,0xf000
    dw %4,%2,%3
    db 8,16,1,%5,1,%6,0,((2097152/(%4*%3))-1),1
    db %7,%8,%9,%10,%11,%12,%13,%14,0
    times 256-($-%1) db 0
%endmacro
tvbe_info tvbe_info_640_8,640,480,640,8,4,0,0,0,0,0,0,0,0
tvbe_info tvbe_info_800_8,800,600,800,8,4,0,0,0,0,0,0,0,0
tvbe_info tvbe_info_1024_8,1024,768,1024,8,4,0,0,0,0,0,0,0,0
tvbe_info tvbe_info_1280_8,1280,1024,1280,8,4,0,0,0,0,0,0,0,0
tvbe_info tvbe_info_640_555,640,480,1280,16,6,5,10,5,5,5,0,1,15
tvbe_info tvbe_info_640_565,640,480,1280,16,6,5,11,6,5,5,0,0,0
tvbe_info tvbe_info_800_565,800,600,1600,16,6,5,11,6,5,5,0,0,0
tvbe_info tvbe_info_1024_565,1024,768,2048,16,6,5,11,6,5,5,0,0,0
tvbe_info tvbe_info_640_32,640,480,2560,32,6,8,16,8,8,8,0,8,24

; Dispatch real VGA attribute palette services used by the original MINI/VGA driver; retain existing DAC services.
tvbe_attribute_palette:
    mov al,rAL
    cmp al,0
    je .single
    cmp al,1
    je .border
    cmp al,2
    je .all
    cmp al,7
    je .single
    cmp al,8
    je .border
    cmp al,9
    je .all
    jmp video_palette
.single:
    cmp rBL,16
    jae .done
    call tvbe_attribute_save
    call tvbe_attribute_reset
    mov al,rBL
    out dx,al
    cmp rAL,7
    je .read_single
    mov al,rBH
    out dx,al
    jmp .finish
.read_single:
    inc dx
    in al,dx
    mov rBH,al
    jmp .finish
.border:
    call tvbe_attribute_save
    call tvbe_attribute_reset
    mov al,0x11
    out dx,al
    cmp rAL,8
    je .read_single
    mov al,rBH
    out dx,al
    jmp .finish
.all:
    cmp rDX,0xffef             ; the full16palette bytes plus overscan must fit the caller segment
    ja .done
    mov es,rES
    mov si,rDX
    call tvbe_attribute_save
    xor bx,bx
    mov cx,16
.next:
    call tvbe_attribute_reset
    mov al,bl
    out dx,al
    cmp rAL,9
    je .read_value
    mov al,[es:si]
    out dx,al
    jmp .advance
.read_value:
    inc dx
    in al,dx
    mov [es:si],al
.advance:
    inc si
    inc bl
    loop .next
    call tvbe_attribute_reset
    mov al,0x11
    out dx,al
    cmp rAL,9
    je .read_border
    mov al,[es:si]
    out dx,al
    jmp .finish
.read_border:
    inc dx
    in al,dx
    mov [es:si],al
.finish:
    call tvbe_attribute_reset
    mov ax,di
    out dx,al                  ; restore original attribute index and display-enable bit
    call tvbe_attribute_reset  ; a completed BIOS operation leaves the address phase ready
.done:
    ret

; Capture only the actual attribute address/display latch; caller DI remains in the normal service frame.
tvbe_attribute_save:
    mov dx,0x3c0
    in al,dx
    movzx di,al
    ret

; Reset the actual active color/mono address/data flip-flop while retaining an input AL value.
tvbe_attribute_reset:
    push ax
    mov dx,0x3cc
    in al,dx
    test al,1
    mov dx,0x3ba
    jz .status
    mov dx,0x3da
.status:
    in al,dx
    mov dx,0x3c0
    pop ax
    ret
