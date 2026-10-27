; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own CT1740/YMF262 port, 18-channel, 4-op, waveform, quad routing and DSP coexistence probe.
bits 16
cpu 486
org 100h
%ifndef PORT
%define PORT 220h
%endif
    jmp start
%include "opl3-cases.inc"
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    in al,21h
    mov [saved_pic],al
    in al,0a1h
    mov [saved_pic+1],al
    mov dx,388h
    in al,dx
    mov [initial_status],al
    test al,6
    jnz failure
    mov byte [bank],0
    mov byte [ports],0
    mov ax,6004h
    call opl_write
    mov ax,8004h
    call opl_write
    mov ax,0ff02h
    call opl_write
    mov ax,0104h
    call opl_write
    mov cx,10000
.timer1:
    mov dx,388h
    in al,dx
    test al,40h
    jnz .timer1_ok
    loop .timer1
    jmp failure
.timer1_ok:
    mov [timer1_status],al
    mov ax,0004h
    call opl_write
    mov ax,8004h
    call opl_write
    mov ax,0ff03h
    call opl_write
    mov ax,0204h
    call opl_write
    mov cx,10000
.timer2:
    mov dx,388h
    in al,dx
    test al,20h
    jnz .timer2_ok
    loop .timer2
    jmp failure
.timer2_ok:
    mov [timer2_status],al
    mov ax,0004h
    call opl_write
    mov ax,8004h
    call opl_write
    mov dx,388h
    in al,dx
    mov [final_timer],al
    call dsp_reset
    call dsp_version
    mov bx,30h
.volume:
    mov dx,PORT+4
    mov al,bl
    out dx,al
    inc dx
    mov al,0f8h
    out dx,al
    inc bx
    cmp bx,36h
    jb .volume
    mov si,case_table
.case:
    mov al,[si+24]
    mov [ports],al
    call clear_all
    mov dx,PORT+4
    mov al,35h
    out dx,al
    inc dx
    mov al,0f8h
    out dx,al
    cmp word [si+2],5
    jne .new_on
    mov byte [bank],1
    mov ax,0005h
    call opl_write
    mov byte [compat_alias],1
.new_on:
    mov bx,[si+4]
    cmp word [si+2],2
    je .pair
    cmp word [si+2],4
    je .drum
    call tone
    cmp word [si+2],6
    jne .configured
    mov bx,9
    call tone
    mov byte [bank],1
    mov ax,20c0h
    call opl_write
    mov ax,36b0h
    call opl_write
    jmp .configured
.pair:
    mov byte [bank],1
    mov cx,[si+6]
    mov ah,1
    shl ah,cl
    mov al,4
    call opl_write
    mov bx,[si+4]
    shl bx,1
    mov ax,[si+10]
    and ax,1
    add bx,ax
    cmp word [si+10],2
    jb .slot
    add bx,6
.slot:
    call unmute
    mov bx,[si+4]
    call channel_bank
    mov al,bl
    add al,0c0h
    mov ah,[si+12]
    shl ah,4
    mov cx,[si+8]
    shr cx,1
    and cl,1
    or ah,cl
    call opl_write
    add al,3
    mov ah,20h
    mov cx,[si+8]
    and cl,1
    or ah,cl
    call opl_write
    mov bx,[si+4]
    call key_channel
    mov bx,[si+4]
    add bx,3
    call channel_bank
    mov al,bl
    add al,0a0h
    mov ah,0ffh
    call opl_write
    add al,10h
    mov ah,3fh
    call opl_write
    jmp .configured
.drum:
    mov bx,12
.drum_slots:
    call unmute
    inc bx
    cmp bx,18
    jb .drum_slots
    mov bx,6
.drum_channels:
    mov byte [bank],0
    mov al,bl
    add al,0a0h
    mov ah,44h
    call opl_write
    mov al,bl
    add al,0b0h
    mov ah,12h
    call opl_write
    mov al,bl
    add al,0c0h
    mov ah,30h
    call opl_write
    inc bx
    cmp bx,9
    jb .drum_channels
    mov ax,00bdh
    mov ah,[si+20]
    or ah,20h
    call opl_write
.configured:
    mov dx,case_marker
    call e9_string
    mov ax,[si]
    call hex_byte
    call e9_newline
    cmp word [si+2],7
    jne .hold
    call dsp_reset
    call dsp_version
    mov al,10h
    call dsp_write
    mov al,98h
    call dsp_write
.hold:
    cmp word [si+2],1
    jne .wait
    cmp word [si+12],15
    jne .wait
    mov cx,1
    call wait_ticks
    mov dx,PORT+4
    mov al,35h
    out dx,al
    inc dx
    mov al,0d8h
    out dx,al
.wait:
    mov cx,[si+22]
    call wait_ticks
    mov dx,end_marker
    call e9_string
    mov ax,[si]
    call hex_byte
    call e9_newline
    inc word [completed]
    add si,ROW_BYTES
    cmp word [completed],CASE_COUNT
    jb .case
    call clear_all
    call dsp_reset
    mov cx,2
    call wait_ticks
    in al,21h
    mov [end_pic],al
    in al,0a1h
    mov [end_pic+1],al
    jmp finish
failure:
    mov word [error],1
finish:
    mov dx,proof_marker
    call e9_string
    mov ax,cs
    call hex_word
    mov al,':'
    out 0e9h,al
    mov ax,proof
    call hex_word
    call e9_newline
    mov dx,returned
    mov ah,9
    int 21h
    mov ax,4c00h
    int 21h
; Turns off the controlled OPL3 voices before programming the next independent case.
clear_all:
    pushad
    mov byte [compat_alias],0
    mov byte [bank],1
    mov ax,0105h
    call opl_write
    mov ax,0004h
    call opl_write
    mov byte [bank],0
    mov ax,00bdh
    call opl_write
    xor bx,bx
.channels:
    push bx
    call channel_bank
    mov al,bl
    add al,0b0h
    xor ah,ah
    call opl_write
    pop bx
    inc bx
    cmp bx,18
    jb .channels
    mov byte [bank],0
    xor bx,bx
.operators:
    mov al,[offsets+bx]
    add al,20h
    mov ah,21h
    call opl_write
    add al,20h
    mov ah,63
    call opl_write
    add al,20h
    mov ah,0f0h
    call opl_write
    add al,20h
    mov ah,0fh
    call opl_write
    add al,60h
    xor ah,ah
    call opl_write
    inc bx
    cmp bx,18
    jb .operators
    cmp byte [bank],0
    jne .done
    mov byte [bank],1
    xor bx,bx
    jmp .operators
.done:
    popad
    ret
; BX 0..35 -> bank and operator offset. The caller's BX is preserved.
unmute:
    pushad
    mov byte [bank],0
    cmp bx,18
    jb .slot
    sub bx,18
    mov byte [bank],1
.slot:
    mov al,[offsets+bx]
    add al,40h
    xor ah,ah
    call opl_write
    add al,0a0h
    mov ah,[si+14]
    call opl_write
    popad
    ret
; Selects the register bank containing the requested OPL3 channel.
channel_bank:
    mov byte [bank],0
    cmp bx,9
    jb .done
    sub bx,9
    mov byte [bank],1
.done:
    ret
; Programs the controlled actual FM operator/channel registers.
tone:
    pushad
    shl bx,1
    inc bx
    call unmute
    popad
    pushad
    call channel_bank
    mov al,bl
    add al,0c0h
    mov ah,[si+12]
    shl ah,4
    call opl_write
    popad
    call key_channel
    ret
; Changes the selected real OPL3 channel's key state.
key_channel:
    pushad
    call channel_bank
    mov al,bl
    add al,0a0h
    mov ah,[si+16]
    call opl_write
    add al,10h
    mov ah,[si+18]
    shl ah,2
    or ah,22h
    call opl_write
    popad
    ret
; Writes an actual FM register with the required address/data port sequence.
opl_write:
    pushad
    cmp byte [compat_alias],0
    je .normal
    cmp byte [bank],0
    jne .normal
    mov dx,38ah
    jmp .address
.normal:
    mov dx,388h
    cmp byte [ports],0
    je .base
    mov dx,PORT
    cmp byte [ports],2
    jne .base
    cmp byte [bank],0
    jne .base
    add dx,8
.base:
    cmp byte [bank],0
    je .address
    add dx,2
.address:
    out dx,al
    in al,dx
    cmp byte [ports],2
    jne .data
    mov dx,388h
.data:
    inc dx
    mov al,ah
    out dx,al
    dec dx
    in al,dx
    popad
    ret
; Polls DSP readiness before writing one real command/parameter byte.
dsp_write:
    pushad
    mov ah,al
    mov dx,PORT+0ch
    mov cx,10000
.wait:
    in al,dx
    test al,80h
    jz .ready
    loop .wait
    mov word [error],2
.ready:
    mov al,ah
    out dx,al
    popad
    ret
; Polls DSP readiness with a bounded timeout before reading the actual reply byte.
dsp_read:
    push cx
    push dx
    mov dx,PORT+0eh
    mov cx,10000
.wait:
    in al,dx
    test al,80h
    jnz .ready
    loop .wait
    mov word [error],3
.ready:
    mov dx,PORT+0ah
    in al,dx
    pop dx
    pop cx
    ret
; Performs the real DSP reset handshake and checks its AA reply.
dsp_reset:
    pushad
    mov dx,PORT+6
    mov al,1
    out dx,al
    mov cx,32
.delay:
    in al,dx
    loop .delay
    xor al,al
    out dx,al
    call dsp_read
    cmp al,0aah
    je .done
    mov word [error],4
.done:
    popad
    ret
; Queries and records the actual DSP version response.
dsp_version:
    pushad
    mov al,0e1h
    call dsp_write
    call dsp_read
    mov ah,al
    call dsp_read
    mov [dsp_ver],ax
    popad
    ret
; Waits for actual BIOS timer ticks rather than advancing virtual time from the host.
wait_ticks:
    pushad
    mov bx,cx
    xor ah,ah
    int 1ah
    mov si,dx
    add si,bx
.wait:
    xor ah,ah
    int 1ah
    cmp dx,si
    jb .wait
    popad
    ret
; Emits the original zero-terminated string on the guest proof port.
e9_string:
    pushad
    mov bx,dx
.next:
    mov al,[bx]
    inc bx
    test al,al
    jz .done
    out 0e9h,al
    jmp .next
.done:
    popad
    ret
; Emits the high and low bytes of the supplied word as hexadecimal proof.
hex_word:
    push ax
    mov al,ah
    call hex_byte
    pop ax
; Emits both hexadecimal nibbles of the supplied byte.
hex_byte:
    push ax
    shr al,4
    call hex_digit
    pop ax
; Converts one hexadecimal nibble to its ASCII proof character.
hex_digit:
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .out
    add al,7
.out:
    out 0e9h,al
    ret
; Emits an actual CR/LF pair on the guest proof port.
e9_newline:
    push ax
    mov al,13
    out 0e9h,al
    mov al,10
    out 0e9h,al
    pop ax
    ret
offsets: db 0,3,1,4,2,5,8,11,9,12,10,13,16,19,17,20,18,21
immutable_end:
bank: db 0
ports: db 0
compat_alias: db 0
proof_marker: db 'OPL3-PROOF=',0
case_marker: db 'OPL3-CASE=',0
end_marker: db 'OPL3-END=',0
returned: db 'OPL3 probe returned',13,10,'$'
proof:
    db 'OPL3P001'
    dw 1,CASE_COUNT,PORT
completed: dw 0
error: dw 0
dsp_ver: dw 0
initial_status: dw 0
timer1_status: dw 0
timer2_status: dw 0
final_timer: dw 0
saved_pic: dw 0
end_pic: dw 0
    dw 0beefh
