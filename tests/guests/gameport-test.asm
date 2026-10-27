; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own external-input probe. No guest data, timer result or port register is injected by the host.
bits 16
cpu 486
org 0x100

; Execute 24 external resistance/button states using two actual byte-OUT/IN pulse measurements each.
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
    xor ax,ax
    mov es,ax
    mov ax,[es:0x410]
    mov [equipment],ax
    int 0x11
    mov [bios_equipment],ax
    test ax,0x1000
    jz failed
    mov ax,0x8400
    xor dx,dx
    int 0x15
    mov [ah84],ax
    pushf
    pop ax
    mov [ah84_flags],ax
    mov si,begin_text
    call print
.phase:
    mov si,ready_text
    call print
    mov ax,[phase]
    call hex_byte
    mov si,newline
    call print
    mov ah,8                    ; actual DOS console read: external input is applied before the acknowledgement key
    int 0x21
    cmp al,13
    jne failed
    mov di,[phase]
    shl di,4
    add di,bios_rows
    mov ax,0x8400
    xor dx,dx
    int 0x15
    mov [di],ax
    pushf
    pop word [di+2]
    jc failed
    mov ax,0x8400
    mov dx,1
    int 0x15
    mov [di+4],ax
    mov [di+6],bx
    mov [di+8],cx
    mov [di+10],dx
    pushf
    pop word [di+12]
    jc failed
    mov word [di+14],0xbeef
    mov di,[phase]
    shl di,4
    add di,rows
    mov si,[phase]
    shl si,1
    mov dx,[ports+si]
    mov [di],dx
    mov ax,[phase]
    mov [di+12],ax
    mov byte [di+7],0xa7
    pushf
    cli
    mov byte [pass],0
.pass:
    mov al,[phase]
    out dx,al
    xor bx,bx
    mov cx,10000
.poll:
    in al,dx
    test bx,bx
    jnz .not_first
    mov si,2
    cmp byte [pass],0
    je .first
    mov si,4
.first:
    add di,si
    mov [di],al
    sub di,si
.not_first:
    inc bx
    mov si,[phase]
    mov ah,[connected+si]
    and ah,al
    jz .finished
    loop .poll
    popf
    jmp failed
.finished:
    mov si,3
    cmp byte [pass],0
    je .last
    mov si,5
.last:
    add di,si
    mov [di],al
    sub di,si
    mov si,8
    cmp byte [pass],0
    je .count
    mov si,10
.count:
    add di,si
    mov [di],bx
    sub di,si
    inc byte [pass]
    cmp byte [pass],2
    jb .pass
    mov cx,32768                ; real guest work, so open circuits are sampled again well after nominal full-scale expiry
.delay:
    loop .delay
    in al,dx
    mov [di+6],al
    mov si,[phase]
    cmp al,[late_expected+si]
    jne .bad
    mov word [di+14],0xbeef
    popf
    inc word [phase]
    cmp word [phase],24
    jb .phase
    mov word [done],0xbeef
    mov si,proof_text
    call print
    mov ax,cs
    call hex_word
    mov al,':'
    call put
    mov ax,proof
    call hex_word
    mov si,ok_text
    call print
    mov ax,0x4c00
    int 0x21
.bad:
    popf
    jmp failed

; Report only actual guest execution failures, retaining the proof and phase for independent diagnosis.
failed:
    mov si,failed_text
    call print
    mov ax,[phase]
    call hex_word
    mov si,newline
    call print
    mov ax,0x4c01
    int 0x21

; Write a guest string through actual INT10 teletype; DOSEBios itself mirrors each character once to E9.
print:
    lodsb
    test al,al
    jz .done
    call put
    jmp print
.done:
    ret

; Preserve the caller's loop, address and value registers across ordinary BIOS teletype output.
put:
    pushad
    mov ah,0x0e
    mov bx,7
    int 0x10
    popad
    ret

; Print the word in AX as four uppercase hexadecimal characters without changing DX or DI.
hex_word:
    push ax
    mov al,ah
    call hex_byte
    pop ax
    call hex_byte
    ret

; Print the low byte through the same actual guest output path.
hex_byte:
    push ax
    shr al,4
    call digit
    pop ax
    and al,15
    call digit
    ret

; Convert one hexadecimal digit with no host formatting dependency.
digit:
    cmp al,10
    jb .number
    add al,7
.number:
    add al,'0'
    call put
    ret

begin_text: db 'GAMEPORT-BEGIN',13,10,0
ready_text: db 'GAMEPORT-READY-',0
proof_text: db 'GAMEPORT-PROOF=',0
ok_text: db 13,10,'GAMEPORT-OK',13,10,0
failed_text: db 13,10,'GAMEPORT-FAILED phase=',0
newline: db 13,10,0
ports: dw 0x201,0x601,0xfe01,0xa01,0x201,0x601,0xfe01,0xa01
    times 16 dw 0x201
connected: db 0,15,15,15,15,3,10,15
    times 16 db 15
late_expected: db 0xff,0xa0,0x50,0,0xf0,0xcc,0x35,0xf0
    db 0xf0,0xe0,0xd0,0xc0,0xb0,0xa0,0x90,0x80,0x70,0x60,0x50,0x40,0x30,0x20,0x10,0
    db 'GPMUTABL'
proof: db 'GPORT001'
done: dw 0
phase: dw 0
equipment: dw 0
bios_equipment: dw 0
ah84: dw 0
ah84_flags: dw 0
guard_before: times 16 db 0xa7
rows: times 24*16 db 0
guard_after: times 16 db 0xa7
pass: db 0
    db 'GPBIOS01'
bios_rows: times 24*16 db 0
    times 2048 db 0             ; DOS console/BIOS/monitor calls need their own real guest stack below the reserved block end
stack_top:
program_end:
