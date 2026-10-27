; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own raw-port ET4000AX 1-MiB / DOS BIOS probe. Apache-2.0.
; Assemble with -DTEST_MODE=0x2e/0x30/0x38. /A returns after producing the frame.
bits 16
cpu 486
org 0x100
%ifndef TEST_MODE
%define TEST_MODE 0x2e
%endif
%ifdef TEST_VBE
    jmp start
%include "vbe-test-body.inc"
%endif
; Retain byte-identical legacy calls unless this variant explicitly exercises VBE.
%macro test_mode_set 0
%ifdef TEST_VBE
    push bx
    mov bx,TEST_VBE
    test al,0x80
    jz %%set
    or bx,0x8000
%%set:
    mov ax,0x4f02
    int 0x10
    cmp ax,0x004f
    jne failed
    inc word [vbe_mode_sets]
    mov ax,0x4f03
    int 0x10
    cmp ax,0x004f
    jne failed
    cmp bx,TEST_VBE
    jne failed
    pop bx
%else
    int 0x10
%endif
%endmacro
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
%ifdef TEST_VBE
    mov sp,vbe_stack_end
%endif
    cld
    push cs
    pop ds
    xor cx,cx
    mov cl,[0x80]
    mov si,0x81
.args:
    jcxz .begin
    lodsb
    and al,0xdf
    cmp al,'A'
    jne .next_arg
    mov byte [automatic],1
.next_arg:
    loop .args
.begin:
%ifdef TEST_VBE
    call vbe_probe
%endif
    mov ax,TEST_MODE
    test_mode_set
    mov ah,0x0f
    int 0x10
    mov [actual_mode],ax
    cmp al,TEST_MODE
    jne failed
    mov ax,0xa000
    mov es,ax
    ; Fill ALL sixteen banks before reading any. A short backing store cannot pass.
    xor bx,bx
.fill_bank:
    mov dx,0x3cd
    mov al,bl
    out dx,al
    call bank_word
    xor di,di
    mov cx,0x8000
    rep stosw
    inc bl
    cmp bl,16
    jb .fill_bank
    xor bx,bx
.check_bank:
    call select_split
    call bank_word
    xor di,di
    mov cx,0x8000
    repe scasw
    jne failed
    inc word [bank_count]
    mov si,bx
    shl si,1
    mov ax,[es:0]
    mov [bank_values+si],ax
    inc bl
    cmp bl,16
    jb .check_bank
    ; Different read and write nibbles remain independent even in one access series.
    xor bx,bx
.split:
    call select_split
    mov ax,bx
    add ax,0x6d40
    mov [es:0x1234],ax
    mov si,bx
    shl si,1
    call bank_word
    cmp [es:0x1234],ax           ; reads the original source bank, not the write bank
    jne failed
    mov al,bl
    xor al,15
    mov ah,al
    shl al,4
    or al,ah
    mov dx,0x3cd
    out dx,al
    mov ax,[es:0x1234]
    mov [split_values+si],ax
    mov cx,bx
    add cx,0x6d40
    cmp ax,cx
    jne failed
    push bx
    xor bl,15
    call bank_word
    mov [es:0x1234],ax
    pop bx
    inc word [split_count]
    inc bl
    cmp bl,16
    jb .split
    ; Planar addressing uses PL0/1 above a 64-KiB plane, not 16-KiB chain4 banks.
    mov dx,0x3c4
    mov ax,0x0604
    out dx,ax
    mov dx,0x3ce
    mov ax,5
    out dx,ax
    xor bx,bx
.planar_fill:
    mov dx,0x3cd
    mov ax,bx
    shr al,2
    out dx,al
    mov cx,bx
    and cx,3
    mov ah,1
    shl ah,cl
    mov al,2
    mov dx,0x3c4
    out dx,ax
    mov al,bl
    add al,0x21
    mov [es:0],al
    xor al,0x5a
    mov [es:0xffff],al
    inc bx
    cmp bx,16
    jb .planar_fill
    xor bx,bx
.planar_read:
    mov ax,bx
    shr al,2
    shl al,4
    mov dx,0x3cd
    out dx,al
    mov ax,bx
    and ax,3
    mov ah,al
    mov al,4
    mov dx,0x3ce
    out dx,ax
    mov al,[es:0]
    mov ah,[es:0xffff]
    mov si,bx
    shl si,1
    mov [planar_values+si],ax
    mov cl,bl
    add cl,0x21
    cmp al,cl
    jne failed
    xor cl,0x5a
    cmp ah,cl
    jne failed
    inc word [planar_count]
    inc bx
    cmp bx,16
    jb .planar_read
    ; Read all four latches from group zero; mode 1 copies them into group two.
    mov dx,0x3cd
    xor al,al
    out dx,al
    mov al,[es:0]
    mov al,2
    out dx,al
    mov dx,0x3c4
    mov ax,0x0f02
    out dx,ax
    mov dx,0x3ce
    mov ax,0x0105
    out dx,ax
    mov [es:0x3210],al
    mov dx,0x3cd
    mov al,0x20
    out dx,al
    xor bx,bx
.latch_read:
    mov dx,0x3ce
    mov ah,bl
    mov al,4
    out dx,ax
    mov al,[es:0x3210]
    mov [latch_values+bx],al
    mov cl,bl
    add cl,0x21
    cmp al,cl
    jne failed
    inc bx
    cmp bx,4
    jb .latch_read
    inc word [latch_count]
    ; A fresh BIOS mode must erase every byte, including the last VRAM bank.
    mov ax,TEST_MODE
    test_mode_set
    xor bx,bx
.clear_check:
    mov dx,0x3cd
    mov al,bl
    shl al,4
    out dx,al
    xor di,di
    xor ax,ax
    mov cx,0x8000
    repe scasw
    jne failed
    inc word [clear_count]
    inc bx
    cmp bx,16
    jb .clear_check
    ; Deterministic visible pattern over the entire MiB, then actual no-clear reload.
    xor bx,bx
.draw_bank:
    mov dx,0x3cd
    mov al,bl
    out dx,al
    mov dx,bx
    shl dl,4
    add dl,bl                    ; 17*bank
    xor di,di
    xor cx,cx                    ; LOOP executes 65536 bytes
.draw:
    mov ax,di
    add al,ah
    add al,ah
    add al,ah
    add al,dl
    stosb
    loop .draw
    inc bx
    cmp bx,16
    jb .draw_bank
    mov ax,TEST_MODE | 0x80
    test_mode_set
    xor bx,bx
.preserve_bank:
    mov dx,0x3cd
    mov al,bl
    shl al,4
    out dx,al
    mov dx,bx
    shl dl,4
    add dl,bl
    xor di,di
    xor cx,cx
.preserve:
    mov ax,di
    add al,ah
    add al,ah
    add al,ah
    add al,dl
    cmp al,[es:di]
    jne failed
    inc di
    loop .preserve
    mov si,bx
    shl si,2
    mov ax,[es:0]
    mov [preserve_values+si],ax
    mov ax,[es:0xfffe]
    mov [preserve_values+si+2],ax
    inc word [preserve_count]
    inc bx
    cmp bx,16
    jb .preserve_bank
    ; Palette is also guest I/O. No text/DOS console writes can modify the picture.
    mov dx,0x3c8
    xor al,al
    out dx,al
    inc dx
    xor bx,bx
.palette:
    mov al,bl
    and al,63
    out dx,al
    mov al,bl
    shr al,2
    out dx,al
    mov al,bl
    imul ax,ax,7
    and al,63
    out dx,al
    inc bx
    cmp bx,256
    jb .palette
    mov dx,0x3cd
    xor al,al
    out dx,al
    in al,dx
    mov [final_bank],al
    test al,al
    jne failed
    ; Wait for one real status-port retrace transition, not host wall time.
    mov dx,0x3da
.retrace_end:
    in al,dx
    test al,8
    jnz .retrace_end
.retrace_begin:
    in al,dx
    test al,8
    jz .retrace_begin
    mov word [complete],0xbeef
    mov si,proof_message
    call debug
    mov ax,cs
    call hex
    mov al,':'
    out 0xe9,al
    mov ax,proof
    call hex
    mov si,ready
    call debug
    cmp byte [automatic],0
    jne .exit
    xor ah,ah
    int 0x16
.exit:
    mov ax,3
    int 0x10
    mov dx,done
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
failed:
    mov word [error_code],1
    mov ax,3
    int 0x10
    mov dx,error
    mov ah,9
    int 0x21
    mov ax,0x4c01
    int 0x21
; Selects the actual ET4000 bank for the next aperture word.
bank_word:
    mov al,bl
    add al,0x10
    mov ah,bl
    xor ah,0xa5
    ret
; Programs independent ET4000 read/write banks through the real segment-select port.
select_split:
    mov dx,0x3cd
    mov al,bl
    shl al,4
    mov ah,bl
    xor ah,15
    or al,ah
    out dx,al
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
; Emits a hexadecimal diagnostic value through the probe's output path.
hex:
    push cx
    push dx
    mov dx,ax
    mov cx,4
.digit:
    rol dx,4
    mov al,dl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .out
    add al,7
.out:
    out 0xe9,al
    loop .digit
    pop dx
    pop cx
    ret
proof_message: db 'ET4000-PROOF=',0
ready: db 13,10,'ET4000-READY',13,10,0
done: db 'ET4000-DOS-RETURN-OK',13,10,'$'
error: db 'ET4000-FAILED',13,10,'$'
mutable_start: db 'ETMUTABL'
automatic: db 0
proof: db 'ET4K0001'
    dw 1,TEST_MODE
actual_mode: dw 0
bank_count: dw 0
split_count: dw 0
planar_count: dw 0
latch_count: dw 0
clear_count: dw 0
preserve_count: dw 0
final_bank: dw 0
error_code: dw 0
complete: dw 0
bank_values: times 16 dw 0
split_values: times 16 dw 0
planar_values: times 16 dw 0
latch_values: times 4 db 0
preserve_values: times 16 dd 0
%ifdef TEST_VBE
%include "vbe-test-data.inc"
%endif
