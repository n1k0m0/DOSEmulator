; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Real DOS probe: four BIOS disks, EDD persistence and translated CHS above 504 MiB.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    push fs
    push cs
    pop ds
    push cs
    pop es
    cld
    mov [dap+6],ds
    xor bx,bx
    mov bl,[0x80]
    cmp bl,2
    jb .arguments_done
    cmp byte [0x82],'/'
    jne .arguments_done
    mov al,[0x83]
    or al,0x20
    cmp al,'r'
    sete byte [persist]
.arguments_done:
    mov si,begin_text
    call print
    xor ax,ax
    mov fs,ax
    cmp byte [fs:0x475],4
    jne failed
.drive:
    mov bx,0x55aa
    mov ax,0x4100
    call bios
    jc failed
    cmp bx,0xaa55
    jne failed
    test cx,1
    jz failed
    mov ax,0x0800
    call bios
    jc failed
    cmp dl,4
    jne failed
    movzx bx,byte [drive]
    mov [heads+bx],dh
    mov si,parameters
    mov word [si],26
    mov ax,0x4800
    call bios
    jc failed
    cmp word [parameters+24],512
    jne failed
    cmp dword [parameters+20],0
    jne failed
    mov eax,[parameters+16]
    movzx bx,byte [drive]
    shl bx,2
    mov [capacities+bx],eax
    dec eax
    mov [dap+8],eax
    mov byte [error_stage],1
    cmp byte [persist],0
    jne .read
    call fill_pattern
    mov ax,0x4300
    mov si,dap
    call bios
    jc failed
    inc word [writes]
.read:
    mov di,buffer
    mov cx,256
    mov ax,0xcccc
    rep stosw
    mov si,dap
    mov ax,0x4200
    call bios
    jc failed
    call compare_pattern
    jc failed
    inc word [reads]
    inc byte [drive]
    cmp byte [drive],4
    jb .drive

    ; An independent CHS request reaches a sector beyond the old 16-head limit.
    mov byte [drive],3
    mov byte [error_stage],2
    mov eax,[capacities+12]
    cmp eax,1100001
    jbe failed
    mov eax,1100000
    xor edx,edx
    mov ecx,63
    div ecx
    inc dl
    mov [chs_sector],dl
    xor edx,edx
    movzx ecx,byte [heads+3]
    inc ecx
    div ecx
    mov [chs_head],dl
    mov ch,al
    shr ax,2
    and al,0xc0
    or al,[chs_sector]
    mov cl,al
    mov [chs_cylinder_sector],cx
    cmp byte [persist],0
    jne .read_chs
    call fill_pattern
    mov bx,buffer
    mov cx,[chs_cylinder_sector]
    mov dh,[chs_head]
    mov dl,0x83
    mov ax,0x0301
    int 0x13
    jc failed
    inc word [writes]
.read_chs:
    mov di,buffer
    mov cx,256
    mov ax,0xcccc
    rep stosw
    mov bx,buffer
    mov cx,[chs_cylinder_sector]
    mov dh,[chs_head]
    mov dl,0x83
    mov ax,0x0201
    int 0x13
    jc failed
    call compare_pattern
    jc failed
    inc word [reads]
    mov ax,0x0800
    mov dl,0x84
    int 0x13
    jnc failed
    mov word [complete],0xbeef
    mov byte [error_stage],0
    mov si,proof_text
    call print
    mov ax,cs
    call hex_word
    mov al,':'
    call putc
    mov ax,proof
    call hex_word
    mov si,newline
    call print
    mov si,write_ok
    cmp byte [persist],0
    je .success
    mov si,persist_ok
.success:
    call print
    pop fs
    mov ax,0x4c00
    int 0x21

; Executes INT13 on the selected dense BIOS disk and preserves the caller's flags.
bios:
    mov dl,[drive]
    add dl,0x80
    int 0x13
    ret

; Generates each component independently from its index and physical drive number.
fill_pattern:
    xor bx,bx
    mov di,buffer
.byte:
    mov ax,bx
    imul ax,ax,13
    movzx dx,byte [drive]
    imul dx,dx,0x31
    add ax,dx
    add al,7
    stosb
    inc bx
    cmp bx,512
    jb .byte
    ret

; Compares all 512 bytes after real PIO reads rather than accepting a marker alone.
compare_pattern:
    xor bx,bx
.byte:
    mov ax,bx
    imul ax,ax,13
    movzx dx,byte [drive]
    imul dx,dx,0x31
    add ax,dx
    add al,7
    cmp al,[buffer+bx]
    jne .bad
    inc bx
    cmp bx,512
    jb .byte
    clc
    ret
.bad:
    stc
    ret

; Publishes a diagnostic failure and returns a nonzero DOS errorlevel.
failed:
    mov si,failure
    call print
    pop fs
    mov ax,0x4c01
    int 0x21

; Prints through the real BIOS, which also publishes each character once to E9.
print:
    lodsb
    test al,al
    jz .done
    call putc
    jmp print
.done:
    ret
; Prints one character through the actual guest BIOS teletype service.
putc:
    push ax
    push bx
    mov ah,0x0e
    xor bx,bx
    int 0x10
    pop bx
    pop ax
    ret

; Prints exactly four hex digits for the guest-owned proof pointer.
hex_word:
    push cx
    mov cx,4
.digit:
    rol ax,4
    push ax
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .emit
    add al,7
.emit:
    call putc
    pop ax
    loop .digit
    pop cx
    ret

proof_text: db 'HD4-PROOF=',0
begin_text: db 'HD4-BEGIN',13,10,0
write_ok: db 'HD4-WRITE-OK',13,10,0
persist_ok: db 'HD4-PERSIST-OK',13,10,0
failure: db 'HD4-FAILED',13,10,0
newline: db 13,10,0
db 'HD4MUTBL'
proof: db 'HD4TEST1'
capacities: times 4 dd 0
heads: times 4 db 0
reads: dw 0
writes: dw 0
complete: dw 0
persist: db 0
drive: db 0
error_stage: db 0
chs_head: db 0
chs_sector: db 0
chs_cylinder_sector: dw 0
parameters: times 26 db 0
align 4,db 0
dap: db 16,0
dw 1,buffer,0
dq 0
buffer: times 512 db 0
