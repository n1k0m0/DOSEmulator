; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own resumable DOS program: raw extended state, active sound/ports, private file writes and real DOS return.
bits 16
cpu 486
org 100h
; Enter an own DOS process, retain real stacks and prepare resumable device operations.
start:
    push cs
    pop ds
    mov sp,stack_top
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    fninit
    fld tword [extended]
    fldpi
    mov si,begin_text
    call print
    mov dx,226h
    mov al,1
    out dx,al
    mov cx,32
.delay: loop .delay
    xor al,al
    out dx,al
    mov dx,22eh
.reset: in al,dx
    test al,80h
    jz .reset
    mov dx,22ah
    in al,dx
    ; Start an 8-bit 512-component PCM stream through actual ISA DMA1.
    mov al,5
    out 0ah,al
    xor al,al
    out 0ch,al
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,samples
    mov ebx,eax
    out 2,al
    mov al,ah
    out 2,al
    shr eax,16
    out 83h,al
    mov ax,511
    out 3,al
    mov al,ah
    out 3,al
    mov al,49h
    out 0bh,al
    mov al,1
    out 0ah,al
    mov al,41h
    call dsp
    mov al,1fh
    call dsp
    mov al,40h
    call dsp
    mov al,0c0h
    call dsp
    xor al,al
    call dsp
    mov al,0ffh
    call dsp
    mov al,1
    call dsp
    ; Enable one timed OPL3 voice and a timer through real byte ports.
    mov dx,222h
    mov al,5
    out dx,al
    inc dx
    mov al,1
    out dx,al
    mov si,fm_values
    mov cx,11
.fm:
    lodsw
    mov dx,220h
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    loop .fm
    ; Begin an actual UART frame and an MPU-UART frame before the snapshot cut.
    mov dx,3fbh
    mov al,80h
    out dx,al
    mov dx,3f8h
    mov al,12
    out dx,al
    inc dx
    xor al,al
    out dx,al
    mov dx,3fbh
    mov al,3
    out dx,al
    mov dx,3f8h
    mov al,69h
    out dx,al
    mov dx,331h
    mov al,3fh
    out dx,al
.mpu:
    in al,dx
    test al,80h
    jnz .mpu
    dec dx
    in al,dx
    cmp al,0feh
    jne failed
    mov al,90h
    out dx,al
    mov dx,201h
    out dx,al
    mov si,cut_text
    call print
    xor ax,ax
    int 16h
    ; Force a later tick boundary while the saved PCM, RC pulse and frame deadlines run.
    mov ah,0
    int 1ah
    mov bx,dx
.wait:
    mov ah,0
    int 1ah
    sub dx,bx
    cmp dx,3
    jb .wait
    fstp tword [proof+8]
    fstp tword [proof+18]
    fnstcw [proof+28]
    fnstsw [proof+30]
    mov dx,filename
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,proof
    mov cx,32
    mov ah,40h
    int 21h
    jc failed
    cmp ax,32
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
failed:
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Send one DSP command only after its real busy latch permits the byte.
dsp:
    push ax
    mov dx,22ch
.busy:
    in al,dx
    test al,80h
    jnz .busy
    pop ax
    out dx,al
    ret
; Print an own string through DOS; DOSEBios mirrors the resulting console bytes once to E9.
print:
    pushad
.next:
    lodsb
    test al,al
    jz .out
    mov dl,al
    mov ah,2
    int 21h
    jmp .next
.out:
    popad
    ret
begin_text: db 13,10,'SNAPSHOT-BEGIN',13,10,0
cut_text: db 13,10,'SNAPSHOT-CUT',13,10,0
done_text: db 13,10,'SNAPSHOT-DONE',13,10,0
failed_text: db 13,10,'SNAPSHOT-FAILED',13,10,0
filename: db 'C:\SNAP.DAT',0
extended: dq 0fedcba9876543210h
          dw 4001h
fm_values: dw 0120h,0123h,1040h,0043h,0f060h,0f063h,0780h,0783h,30c0h,98a0h,31b0h
proof: db 'SNAPRAW1'
       times 24 db 0
align 16
samples: times 512 db 0c0h
         times 2048 db 0
stack_top:
program_end:
