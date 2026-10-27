; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own read-only IDE bootloader; loads only the adjacent original audit kernel.
bits 16
org 7c00h
%ifndef PAYLOAD_SECTORS
%error PAYLOAD_SECTORS is required
%endif
; Enter from the real DOSEBios boot transfer and read the own second-sector payload.
start:
 cli
 xor ax,ax
 mov ds,ax
 mov ss,ax
 mov sp,7c00h
 mov [boot_drive],dl
 mov ax,0800h
 mov es,ax
 xor bx,bx
 mov ax,0200h+PAYLOAD_SECTORS
 mov cx,2
 xor dh,dh
 mov dl,[boot_drive]
 int 13h
 jc failed
 jmp 0800h:0
; Emit a real failure marker instead of claiming a loaded guest.
failed:
 mov si,message
.loop:
 lodsb
 test al,al
 jz .halt
 out 0e9h,al
 jmp .loop
.halt:
 cli
 hlt
 jmp .halt
boot_drive db 0
message db 'AUDIT2-LOAD-FAILED',0
times 510-($-$$) db 0
dw 0aa55h
