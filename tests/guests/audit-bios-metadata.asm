; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS probe: raw requests use only BIOS81h on the harness's fresh private scratch disk.
bits 16
cpu 486
org 100h

; Reserves private metadata and resident proof memory before issuing bounded real INT13 requests.
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    mov sp,stack_top
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    mov bx,1100h
    mov ah,48h
    int 21h
    jc failed
    mov [allocation],ax
    mov ax,cs
    mov [owner_segment],ax
    mov di,data_buffer
    xor si,si
    mov cx,512

; Fills an independently predictable private ATA payload without external media bytes.
.pattern:
    mov ax,si
    xor al,5ah
    stosb
    inc si
    loop .pattern
    mov si,begin_text
    call print

; Builds one request across physical segment ends without making an illegal guest word access.
case_loop:
    mov bx,[case_index]
    shl bx,2
    mov al,[cases+bx]
    mov [function],al
    mov ax,[cases+bx+1]
    mov [offset],ax
    mov es,[allocation]
    mov di,ax
    sub di,8
    mov cx,48
    mov al,7dh
.fill:
    mov [es:di],al
    call advance
    loop .fill
    mov es,[allocation]
    mov di,[offset]
    mov si,request_template
    mov cx,32
.template:
    lodsb
    mov [es:di],al
    call advance
    loop .template
    mov es,[allocation]
    mov di,[offset]
    cmp byte [function],48h
    jne .data_request
    mov byte [es:di],26
.data_request:
    cmp byte [function],42h
    jne .call
    ; Only a valid read clears its destination; rejected requests must preserve it.
    cmp word [offset],0fff0h
    ja .call
    mov di,data_buffer
    push es
    push cs
    pop es
    mov al,0cch
    mov cx,512
    rep stosb
    pop es
.call:
    mov word [request_template+6],cs
    ; Refresh the actual packet's buffer segment after copying its fixed fields.
    ; The two bytes are written separately so even invalid metadata can be built safely.
    mov ax,cs
    mov di,[offset]
    add di,6
    mov es,[allocation]
    jnc .segment_low
    mov bx,es
    add bx,1000h
    mov es,bx
.segment_low:
    mov [es:di],al
    call advance
    mov [es:di],ah
    mov si,[offset]
    mov ah,[function]
    xor al,al
    mov dx,81h
    mov [before_sp],sp
    mov ds,[allocation]
    stc
    std
    int 13h
    mov [cs:returned_ax],ax
    mov [cs:returned_si],si
    mov [cs:returned_ds],ds
    mov [cs:returned_sp],sp
    pushf
    pop bp
    mov [cs:returned_flags],bp
    cld
    push cs
    pop ds
    mov bx,[case_index]
    shl bx,2
    mov dx,[returned_flags]
    and dx,1
    cmp dl,[cases+bx+3]
    jne failed
    mov ax,[returned_ax]
    cmp ah,dl
    jne failed
    test al,al
    jnz failed
    test word [returned_flags],400h
    jz failed
    mov ax,[returned_ds]
    cmp ax,[allocation]
    jne failed
    mov ax,[returned_si]
    cmp ax,[offset]
    jne failed
    mov ax,[returned_sp]
    cmp ax,[before_sp]
    jne failed
    mov bx,[case_index]
    shl bx,6
    add bx,rows
    mov al,[function]
    mov [bx],al
    mov [bx+1],dl
    mov ax,[returned_ax]
    mov [bx+2],ax
    mov ax,[offset]
    mov [bx+4],ax
    mov ax,[returned_ds]
    mov [bx+6],ax
    mov ax,[returned_si]
    mov [bx+8],ax
    mov ax,[returned_sp]
    mov [bx+10],ax
    mov ax,[returned_flags]
    mov [bx+12],ax
    xor ax,ax
    mov es,ax
    mov al,[es:474h]
    mov [bx+14],al
    mov es,[allocation]
    mov di,[offset]
    sub di,8
    add bx,16
    mov cx,48
.capture:
    mov al,[es:di]
    mov [bx],al
    inc bx
    call advance
    loop .capture
    inc word [case_index]
    cmp word [case_index],12
    jb case_loop
    mov si,data_buffer
    xor bx,bx
    mov cx,512
.readback:
    lodsb
    mov dl,bl
    xor dl,5ah
    cmp al,dl
    jne failed
    inc bx
    loop .readback
    mov word [complete],0beefh
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
    mov dx,(program_end-$$+10fh)/16
    mov ax,3100h
    int 21h

; Advances a physical byte pointer while retaining the byte being copied or filled.
advance:
    inc di
    jnz .done
    push ax
    mov ax,es
    add ax,1000h
    mov es,ax
    pop ax
.done:
    ret

; Reports an actual DOS failure instead of publishing an accepted proof.
failed:
    cld
    push cs
    pop ds
    mov si,failure_text
    call print
    mov ax,4c01h
    int 21h

; Emits immutable own diagnostic strings through guest BIOS teletype calls.
print:
    lodsb
    test al,al
    jz .done
    call putc
    jmp print
.done:
    ret

; Preserves the text source and payload registers around the real BIOS output service.
putc:
    push ax
    push bx
    mov ah,0eh
    xor bx,bx
    int 10h
    pop bx
    pop ax
    ret

; Publishes one fixed-width private proof address without host injection.
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

db 'ABCASES1'
cases:
db 43h
dw 100h
db 0
db 43h
dw 0fff0h
db 0
db 43h
dw 0fff1h
db 1
db 43h
dw 0ffffh
db 1
db 42h
dw 100h
db 0
db 42h
dw 0fff0h
db 0
db 42h
dw 0fff1h
db 1
db 42h
dw 0ffffh
db 1
db 48h
dw 100h
db 0
db 48h
dw 0ffe6h
db 0
db 48h
dw 0ffe7h
db 1
db 48h
dw 0ffffh
db 1
begin_text db 'AUDIT-BIOS-BEGIN',13,10,0
proof_text db 'AUDIT-BIOS-PROOF=',0
success_text db 13,10,'AUDIT-BIOS-OK',13,10,0
failure_text db 13,10,'AUDIT-BIOS-FAILED',13,10,0
db 'ABMUTBL1'
proof db 'ABMETA01'
complete dw 0
case_index dw 0
allocation dw 0
owner_segment dw 0
rows times 12*64 db 0
before_sp dw 0
returned_ax dw 0
returned_si dw 0
returned_ds dw 0
returned_sp dw 0
returned_flags dw 0
function db 0
offset dw 0
request_template db 16,0
    dw 1,data_buffer,0
    dq 8
    times 16 db 7dh
data_guard_before times 16 db 0a7h
data_buffer times 512 db 0
data_guard_after times 16 db 0a7h
    times 1024 db 0
stack_top:
program_end:
