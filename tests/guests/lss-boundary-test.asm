; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own DOS caller: actual pending PIT IRQ0 across STI/LSS and distinct MOV/POP SS controls.
bits 16
cpu 486
org 100h
jmp start

; Save DOS/FPU/vector/masks before using only stacks inside this own COM allocation.
start:
    push cs
    pop ds
    mov [self],cs
    mov [old_ss],ss
    mov [old_sp],esp
    pushfd
    pop dword [old_flags]
    mov ax,3508h
    int 21h
    mov [old_vector],bx
    mov [old_vector+2],es
    mov dx,isr
    mov ax,2508h
    int 21h
    cli
    in al,21h
    mov [old_master],al
    in al,0a1h
    mov [old_slave],al
    mov al,0ffh
    out 21h,al
    out 0a1h,al
    mov al,0fdh
    out 21h,al
    o32 fnsave [saved_fpu]
    mov si,ready_text
    call debug
    mov ax,cs
    call hex4
    mov al,13
    out 0e9h,al
    mov al,10
    out 0e9h,al
    sti
    mov ah,0
    int 16h
    cli
    mov word [case_id],0
case_loop:
    mov ax,cs
    mov ss,ax
    mov esp,stack_a
    add ax,10h
    mov [new_ss],ax
    mov bx,[case_id]
    shl bx,5
    mov word [rows+bx+10],0beefh
    mov dword [rows+bx+28],0facecafeh
    mov word [row_seen],0
    mov word [new16],stack_b-100h
    mov word [new16+2],ax
    mov dword [new32],12340000h+stack_b-100h
    mov [new32+4],ax
    mov dx,after16
    cmp word [case_id],0
    je case_expected
    mov dx,after32
    cmp word [case_id],1
    je case_expected
    mov dx,before_fnop
    cmp word [case_id],2
    je case_expected
    mov dx,after_mov
    cmp word [case_id],3
    je case_expected
    mov dx,after_pop
case_expected:
    mov [rows+bx],dx
    mov al,0ah
    out 20h,al
    mov ecx,8000000
wait_pending:
    in al,20h
    test al,1
    jnz case_armed
    dec ecx
    jnz wait_pending
    jmp failed
case_armed:
    mov al,0feh
    out 21h,al
    mov ax,[new_ss]
    cmp word [case_id],0
    je case_lss16
    cmp word [case_id],1
    je case_lss32
    cmp word [case_id],2
    je case_other
    cmp word [case_id],3
    je case_mov
    push ax
    sti
    pop ss
    mov sp,stack_b-100h
after_pop:
    jmp case_returned
case_mov:
    sti
    mov ss,ax
    mov sp,stack_b-100h
after_mov:
    jmp case_returned
case_lss16:
    sti
    lss sp,[new16]
after16:
    jmp case_returned
case_lss32:
    sti
    lss esp,[new32]
after32:
    jmp case_returned
case_other:
    mov esp,stack_b-100h
    sti
    lss ax,[new16]
before_fnop:
    fnop
case_returned:
    cli
    mov ax,cs
    mov ss,ax
    mov esp,stack_a
    mov al,0ffh
    out 21h,al
    cmp word [row_seen],1
    jne failed
    mov bx,[case_id]
    shl bx,5
    mov ax,[rows+bx]
    cmp [rows+bx+2],ax
    jne failed
    mov ax,[new_ss]
    cmp [rows+bx+8],ax
    jne failed
    mov eax,stack_b-100h
    cmp word [case_id],1
    jne case_check_sp
    or eax,12340000h
case_check_sp:
    cmp [rows+bx+12],eax
    jne failed
    inc word [completed]
    inc word [case_id]
    cmp word [case_id],5
    jb case_loop
    o32 frstor [saved_fpu]
    o32 fnsave [verified_fpu]
    o32 frstor [verified_fpu]
    mov si,saved_fpu
    mov di,verified_fpu
    mov cx,108
    push cs
    pop es
    repe cmpsb
    jne failed
    mov al,[old_master]
    out 21h,al
    mov al,[old_slave]
    out 0a1h,al
    mov ax,[old_ss]
    mov ss,ax
    mov esp,[old_sp]
    push dword [old_flags]
    popfd
    push ds
    mov dx,[old_vector]
    mov ax,[old_vector+2]
    mov ds,ax
    mov ax,2508h
    int 21h
    pop ds
    mov word [passed],1
    mov si,done_text
    call debug
    ; Retain only the own proof/code block until reset, so the shell cannot reuse its evidence.
    mov dx,(program_end-$$+10fh)/16
    mov ax,3100h
    int 21h

; A failing own caller never counts as an accepted DOS run.
failed:
    cli
    mov ax,cs
    mov ss,ax
    mov esp,stack_a
    mov si,fail_text
    call debug
    mov ax,4c01h
    int 21h

; Read the actual IRQ frame from the newly committed stack, acknowledge IRQ0 and return with IRET.
isr:
    push bp
    mov bp,sp
    pushad
    push ds
    push cs
    pop ds
    mov bx,[case_id]
    shl bx,5
    mov ax,[ss:bp+2]
    mov [rows+bx+2],ax
    mov ax,[ss:bp+4]
    mov [rows+bx+4],ax
    mov ax,[ss:bp+6]
    mov [rows+bx+6],ax
    mov ax,ss
    mov [rows+bx+8],ax
    mov eax,esp
    add ax,42
    mov [rows+bx+12],eax
    inc word [rows+bx+16]
    inc word [row_seen]
    inc word [interrupts]
    mov al,20h
    out 20h,al
    pop ds
    popad
    pop bp
    iret

; Print only own bytes through the ordinary debug port; no host RAM or register injection.
debug:
    pushad
.next:
    lodsb
    test al,al
    jz .out
    out 0e9h,al
    jmp .next
.out:
    popad
    ret

; Publish the actual DOS code segment as four hexadecimal ASCII digits.
hex4:
    pushad
    mov bx,ax
    mov cx,4
.digit:
    rol bx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .emit
    add al,7
.emit:
    out 0e9h,al
    loop .digit
    popad
    ret

ready_text: db 'LSS-READY=',0
done_text: db 'LSS-DONE',13,10,0
fail_text: db 'LSS-FAILED',13,10,0
proof: db 'LSSPR001'
self: dw 0
old_ss: dw 0
old_sp: dd 0
old_vector: dw 0,0
old_master: db 0
old_slave: db 0
passed: dw 0
completed: dw 0
interrupts: dw 0
old_flags: dd 0
case_id: dw 0
row_seen: dw 0
new_ss: dw 0
new16: dw 0,0
new32: dd 0
       dw 0
rows: times 5*32 db 0
saved_fpu: times 108 db 0
verified_fpu: times 108 db 0
guard: dw 0a55ah
times 1024 db 0
stack_a:
times 1024 db 0
stack_b:
program_end:
