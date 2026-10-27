; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS probe; raw writes target only BIOS 81h on a new private scratch disk.
bits 16
cpu 486
org 0x100

; Reserve our own far buffer and execute CHS/EDD pairs across segment boundaries.
start:
    push cs
    pop ds
    push cs
    pop es
    mov sp,stack_top
    mov bx,(program_end-$$+0x10f)/16
    mov ah,0x4a
    int 0x21
    jc failed
    mov bx,0x1200               ; 72 KiB: offset FFFFh + three sectors + guards
    mov ah,0x48
    int 0x21
    jc failed
    mov [allocation],ax
    mov [dap+6],ax
    mov si,begin_text
    call print
.case:
    mov si,[case_index]
    imul si,si,6
    mov ax,[cases+si]
    mov [offset],ax
    mov [dap+4],ax
    mov ax,[cases+si+2]
    mov [sectors],ax
    mov [dap+2],ax
    shl ax,9
    mov [byte_count],ax
    mov al,[cases+si+4]
    mov [api],al
    call buffer_start
    sub di,16
    mov cx,[byte_count]
    add cx,32
    mov al,0xa7
    call fill_bytes
    call buffer_start
    xor si,si
    mov cx,[byte_count]
.pattern:
    call pattern_byte
    mov [es:di],al
    call advance
    inc si
    loop .pattern
    mov byte [stage],1
    mov ax,0x4300
    call bios
    jc failed
    test ah,ah
    jnz failed
    inc word [transfers]
    call buffer_start
    mov cx,[byte_count]
    mov al,0xcc
    call fill_bytes
    mov byte [stage],2
    mov ax,0x4200
    call bios
    jc failed
    test ah,ah
    jnz failed
    inc word [transfers]
    mov byte [stage],3
    call buffer_start
    xor si,si
    mov cx,[byte_count]
.compare:
    call pattern_byte
    cmp al,[es:di]
    jne failed
    call advance
    inc si
    loop .compare
    mov ax,[byte_count]
    add [checked_bytes],ax
    mov byte [stage],4
    mov bx,[case_index]
    shl bx,5
    add bx,guards
    call buffer_start
    sub di,16
    call guard
    call buffer_start
    mov cx,[byte_count]
.skip_data:
    call advance
    loop .skip_data
    call guard
    inc word [case_index]
    cmp word [case_index],24
    jb .case
    call buffer_start
    mov bx,last_data
    mov cx,1536
.last:
    mov al,[es:di]
    mov [bx],al
    inc bx
    call advance
    loop .last
    mov ax,[allocation]
    mov es,ax
    mov ah,0x49
    int 0x21
    jc failed
    mov word [complete],0xbeef
    mov byte [stage],0
    mov si,proof_text
    call print
    mov ax,cs
    call hex_word
    mov al,':'
    call putc
    mov ax,proof
    call hex_word
    mov si,success_text
    call print
    mov ax,0x4c00
    int 0x21

; Select the same private LBA 8 through actual CHS or EDD BIOS calls.
bios:
    push ax
    call buffer_start
    pop ax
    mov dl,0x81
    cmp byte [api],0
    jne .edd
    cmp ah,0x43
    mov ah,2
    jne .chs
    inc ah
.chs:
    mov al,[sectors]
    mov bx,[offset]
    mov cx,9
    xor dh,dh
    int 0x13
    ret
.edd:
    mov si,dap
    int 0x13
    ret

; Restore the caller's allocated start; each byte walker handles its own ES carry.
buffer_start:
    mov ax,[allocation]
    mov es,ax
    mov di,[offset]
    ret

; Advance exactly one linear byte without ever accessing a word at FFFFh.
advance:
    inc di
    jnz .done
    push ax
    mov ax,es
    add ax,0x1000
    mov es,ax
    pop ax
.done:
    ret

; Fill only the explicitly counted far bytes, including any segment carry.
fill_bytes:
    mov [es:di],al
    call advance
    loop fill_bytes
    ret

; Compute data from the byte index and case number, independent of BIOS output.
pattern_byte:
    mov ax,si
    imul ax,ax,13
    mov dx,si
    shr dx,8
    add ax,dx
    mov dx,[case_index]
    imul dx,dx,31
    add ax,dx
    add al,7
    ret

; Check and retain all sixteen actual guard bytes in the private proof record.
guard:
    mov cx,16
.byte:
    mov al,[es:di]
    cmp al,0xa7
    jne failed
    mov [bx],al
    inc bx
    call advance
    loop .byte
    ret

; Emit a failure with an actual DOS errorlevel; no marker alone counts as success.
failed:
    mov si,failure_text
    call print
    mov ax,[case_index]
    call hex_word
    mov ax,0x4c01
    int 0x21

; Print an immutable own string through actual BIOS teletype instructions.
print:
    lodsb
    test al,al
    jz .done
    call putc
    jmp print
.done:
    ret

; Preserve the source/index registers around the guest BIOS text call.
putc:
    push ax
    push bx
    mov ah,0x0e
    xor bx,bx
    int 0x10
    pop bx
    pop ax
    ret

; Publish a fixed-width proof address in upper-case hexadecimal.
hex_word:
    push cx
    push dx
    mov dx,ax
    mov cx,4
.nibble:
    rol dx,4
    mov al,dl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .digit
    add al,7
.digit:
    call putc
    loop .nibble
    pop dx
    pop cx
    ret

; Immutable requests: six offsets, two sector counts, both API families.
cases:
%assign api_kind 0
%rep 2
%assign n 1
%rep 2
%rep 1
    dw 0x101,n,api_kind
    dw 0xfdff,n,api_kind
    dw 0xfe01,n,api_kind
    dw 0xfffd,n,api_kind
    dw 0xfffe,n,api_kind
    dw 0xffff,n,api_kind
%endrep
%assign n 3
%endrep
%assign api_kind 1
%endrep
begin_text db 'ATA-BOUNDARY-BEGIN',13,10,0
proof_text db 'ATA-BOUNDARY-PROOF=',0
success_text db 13,10,'ATA-BOUNDARY-OK',13,10,0
failure_text db 13,10,'ATA-BOUNDARY-FAILED case=',0
db 'ATAMUTBL'
proof db 'ATABND01'
complete dw 0
case_index dw 0
allocation dw 0
checked_bytes dw 0
transfers dw 0
stage db 0
offset dw 0
sectors dw 0
byte_count dw 0
api db 0
dap db 16,0
    dw 0,0,0
    dq 8
guards times 24*32 db 0
last_data times 1536 db 0
    times 1024 db 0
stack_top:
program_end:
