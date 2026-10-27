; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original real-mode DOS timer/CSM-keying probe, Apache-2.0.
; /S selects the SB2 FM alias. No host synthesis or interrupt shortcuts.
bits 16
cpu 486
org 0x100
; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .begin
    lodsb
    and al,0xdf
    cmp al,'S'
    jne .next
    mov word [opl_port],0x228
.next:
    loop .args
.begin:
    mov dx,banner
    call print
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0x0001
    call opl_write
    mov ax,0x0008
    call opl_write
    mov ax,0x00bd
    call opl_write
    ; Nine additive channel pairs, 18 independently keyed harmonic sine slots.
    xor bx,bx
.operators:
    mov al,[offsets+bx]
    add al,0x20
    mov ah,[multiples+bx]
    call opl_write
    add al,0x20
    mov ah,32                 ; 24 dB headroom per operator
    call opl_write
    add al,0x20
    mov ah,0xf0
    call opl_write
    add al,0x20
    mov ah,0x0f
    call opl_write
    inc bx
    cmp bx,18
    jb .operators
    xor bx,bx
.channels:
    mov ax,bx
    add al,0xa0
    mov ah,0x44
    call opl_write
    add al,0x10
    mov ah,0x12               ; block 4, F-number 580, all KON bits zero
    call opl_write
    add al,0x10
    mov ah,1
    call opl_write
    inc bx
    cmp bx,9
    jb .channels
    mov ax,0x8008
    call opl_write
    mov ax,0xff03
    call opl_write
    mov ax,0x0204             ; timer 2 must NOT cause a CSM pulse
    call opl_write
    mov dx,timer2_marker
    call print
    mov cx,4
    call wait_ticks
    mov dx,[opl_port]
    in al,dx
    and al,0xe0
    cmp al,0xa0
    jne failed
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0x8002             ; 128 * 288 master clocks per pulse
    call opl_write
    mov ax,0x4104             ; timer 1 runs while its status flag is masked
    call opl_write
    mov dx,slow_marker
    call print
    mov cx,8
    call wait_ticks
    mov dx,[opl_port]
    in al,dx
    and al,0xe0
    jnz failed
    mov ax,0x8004             ; IRQ flag reset must not stop/retrigger keying
    call opl_write
    mov dx,reset_marker
    call print
    mov cx,2
    call wait_ticks
    mov ax,0xc002             ; next reload halves the period
    call opl_write
    mov dx,fast_marker
    call print
    mov cx,8
    call wait_ticks
    mov ax,0x6004
    call opl_write
    mov dx,stop_marker
    call print
    mov cx,4
    call wait_ticks
    mov ax,0x0008
    call opl_write
    ; Return to normal melodic keys using the same chip/oscillator/host mixer.
    mov ax,0x0060             ; silent modulator
    call opl_write
    mov ax,0x0043             ; carrier at full level
    call opl_write
    mov ax,0x2123             ; held melodic envelope, multiple 1
    call opl_write
    mov ax,0x32b0
    call opl_write
    mov dx,melodic_marker
    call print
    mov cx,6
    call wait_ticks
    call cleanup
    mov cx,4
    call wait_ticks
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
failed:
    call cleanup
    mov dx,error
    call print
    mov ax,0x4c01
    int 0x21
; Releases this probe's acquired resources and restores its explicitly saved handler/control state.
cleanup:
    mov ax,0x6004
    call opl_write
    mov ax,0x8004
    call opl_write
    mov ax,0x0008
    call opl_write
    mov ax,0x12b0
    call opl_write
    ret
; Writes an actual FM register with the required address/data port sequence.
opl_write:
    push ax
    push dx
    mov dx,[opl_port]
    out dx,al
    times 6 in al,dx
    inc dx
    mov al,ah
    out dx,al
    dec dx
    times 35 in al,dx
    pop dx
    pop ax
    ret
; Waits for actual BIOS timer ticks rather than advancing virtual time from the host.
wait_ticks:
    pusha
    mov bx,cx
    xor ah,ah
    int 0x1a
    mov si,dx
.wait:
    xor ah,ah
    int 0x1a
    sub dx,si
    cmp dx,bx
    jb .wait
    popa
    ret
; Prints the probe's diagnostic text through its actual guest DOS/BIOS output service.
print:
    mov ah,9
    int 0x21
    ret
opl_port dw 0x388
offsets db 0,3,1,4,2,5,8,11,9,12,10,13,16,19,17,20,18,21
multiples db 1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9
banner db 'OPL2 CSM keying development probe',13,10,'$'
timer2_marker db 'OPL2-CSM-TIMER2-SILENT',13,10,'$'
slow_marker db 'OPL2-CSM-SLOW',13,10,'$'
reset_marker db 'OPL2-CSM-RESET-FLAG',13,10,'$'
fast_marker db 'OPL2-CSM-FAST',13,10,'$'
stop_marker db 'OPL2-CSM-STOPPED',13,10,'$'
melodic_marker db 'OPL2-CSM-MELODIC-RETURN',13,10,'$'
done db 'OPL2-CSM-KEYING-DOS-OK',13,10,'$'
error db 'OPL2-CSM-FAILED',13,10,'$'
