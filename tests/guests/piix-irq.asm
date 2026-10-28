; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own PIIX function-zero IRQ route probe. Pins are supplied only by a controlled virtual owner peer.
bits 16
cpu pentium
org 100h
; Reserve this complete DOS process, its frame records, DMA data and private stack.
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
    pushf
    pop word [saved_flags]
    smsw ax
    and ax,1
    mov [proof+8],ax
    mov eax,1
    cpuid
    mov [proof+56],eax
    mov [proof+60],edx
    cmp eax,524h
    jne failed
    cmp edx,135h
    jne failed
    mov dx,proof_file
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,proof
    mov cx,8
    mov ah,40h
    int 21h
    jc failed
    cmp ax,8
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov dx,proof_file
    mov ax,3d02h
    int 21h
    jc failed
    mov [file_handle],ax
    mov ax,3572h
    int 21h
    mov [proof+40],bx
    mov [proof+42],es
    mov ax,3573h
    int 21h
    mov [proof+44],bx
    mov [proof+46],es
    push cs
    pop es
    mov dx,irq10
    mov ax,2572h
    int 21h
    mov dx,irq11
    mov ax,2573h
    int 21h
    cli
    in al,21h
    mov [proof+18],al
    in al,0a1h
    mov [proof+19],al
    mov dx,4d0h
    in al,dx
    mov [proof+20],al
    mov al,0ffh
    out dx,al
    in al,dx
    mov [proof+22],al
    cmp al,0f8h
    jne failed
    inc dx
    in al,dx
    mov [proof+21],al
    mov al,0ffh
    out dx,al
    in al,dx
    mov [proof+23],al
    cmp al,0deh
    jne failed
    mov al,[proof+21]
    or al,0ch
    out dx,al
    dec dx
    mov al,[proof+20]
    out dx,al
    mov dx,0cf8h
    mov eax,80003800h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+28],eax
    cmp eax,122e8086h
    jne failed
    mov dx,0cf8h
    mov eax,80003808h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+32],eax
    cmp eax,06010002h
    jne failed
    mov dx,0cf8h
    mov eax,8000380ch
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+36],eax
    and eax,00ff0000h
    cmp eax,00800000h
    jne failed
    mov dx,0cf8h
    mov eax,80003860h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [proof+24],eax
    cmp eax,80808080h
    jne failed
    mov eax,80800a0ah
    call routes
    mov al,[proof+19]
    and al,0f3h
    out 0a1h,al
    mov al,[proof+18]
    and al,0fbh
    out 21h,al
    fninit
    fld tword [extended]
    fldpi
    mov dx,226h
    mov al,1
    out dx,al
    ; Hold reset over the documented3us minimum at the fixed100MHz/four-cycle test clock.
    mov cx,128
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
    cli
    mov ax,1
    call phase
wait1:
    sti
    hlt
wait1_return:
    cmp word [proof+12],2
    jb wait1
    cli
    mov ax,2
    call phase
wait2:
    sti
    hlt
wait2_return:
    cmp word [proof+12],4
    jb wait2
    cli
    mov ah,0
    int 1ah
    mov [proof+48],dx
    mov bx,dx
    mov ax,3
    call phase
wait3:
    sti
    hlt
    mov ah,0
    int 1ah
    mov [proof+50],dx
    sub dx,bx
    cmp dx,2
    jb wait3
    cli
    cmp word [proof+12],4
    jne failed
    mov eax,80808080h
    call routes
    mov ax,4
    call phase
wait4:
    sti
    hlt
wait4_return:
    cmp word [proof+12],5
    jb wait4
    cli
    mov eax,800b8080h
    call routes
    mov ax,5
    call phase
wait5:
    sti
    hlt
wait5_return:
    cmp word [proof+14],1
    jb wait5
    cli
    mov eax,0a808080h
    call routes
    mov ax,6
    call phase
wait6:
    sti
    hlt
wait6_return:
    cmp word [proof+12],6
    jb wait6
    cli
    mov eax,0b808080h
    call routes
    mov al,[proof+19]
    and al,0f7h
    out 0a1h,al
wait7:
    sti
    hlt
wait7_return:
    cmp word [proof+14],2
    jb wait7
    cli
    mov ax,7
    call phase
    mov al,[proof+19]
    or al,0ch
    out 0a1h,al
    mov eax,[proof+24]
    call routes
    mov dx,4d0h
    mov al,[proof+20]
    out dx,al
    inc dx
    mov al,[proof+21]
    out dx,al
    mov al,[proof+19]
    out 0a1h,al
    mov al,[proof+18]
    out 21h,al
    mov dx,[proof+40]
    mov ax,[proof+42]
    mov ds,ax
    mov ax,2572h
    int 21h
    push cs
    pop ds
    mov dx,[proof+44]
    mov ax,[proof+46]
    mov ds,ax
    mov ax,2573h
    int 21h
    push cs
    pop ds
    mov word [proof+52],1
    push word [saved_flags]
    popf
    fstp tword [raw_fpu+8]
    fstp tword [raw_fpu+18]
    fnstcw [raw_fpu+28]
    fnstsw [raw_fpu+30]
    mov bx,[file_handle]
    mov ah,3eh
    int 21h
    jc failed
    mov word [proof+54],1
    mov dx,proof_file
    mov si,proof
    mov cx,proof_end-proof
    call file
    mov dx,fpu_file
    mov si,raw_fpu
    mov cx,32
    call file
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
; A failed own precondition is reported as a real DOS failure, never accepted as a successful IRQ result.
failed:
    sti
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Commit one phase then expose only an actual OUT instruction to the controlled owner peer.
phase:
    mov [proof+10],ax
    add al,0a0h
phase_out:
    out 0e9h,al
    ret
; Program all four actual route lanes in one PCI data transaction and observe the bus readback.
routes:
    push eax
    mov dx,0cf8h
    mov eax,80003860h
    out dx,eax
    pop eax
    mov dx,0cfch
    out dx,eax
    in eax,dx
    ret
; Wait for a real CT1740 busy latch before issuing each DSP byte.
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
; Preserve exact own bytes using actual DOS create/write/close operations.
file:
    push cx
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,si
    pop cx
    push cx
    mov ah,40h
    int 21h
    pop cx
    jc failed
    cmp ax,cx
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    ret
; Print through the real DOS console and its BIOS mirror without host-side output injection.
print:
    pushad
.next:
    lodsb
    test al,al
    jz .done
    mov dl,al
    mov ah,2
    int 21h
    jmp .next
.done:
    popad
    ret
; Actual IRQ10 entry, complete small return frame, slave/master EOI and guest IRET.
irq10:
    pushad
    push ds
    push cs
    pop ds
    mov bp,sp
    mov bx,[proof+16]
    cmp bx,8
    jae irq_failure
    shl bx,4
    add bx,proof+64
    mov word [bx],10
    mov ax,[proof+10]
    mov [bx+2],ax
    mov ax,[proof+16]
    inc ax
    mov [bx+4],ax
    mov ax,[ss:bp+34]
    mov [bx+6],ax
    mov ax,[ss:bp+36]
    mov [bx+8],ax
    mov ax,[ss:bp+38]
    mov [bx+10],ax
    lea ax,[bp+34]
    mov [bx+12],ax
    mov ax,ss
    mov [bx+14],ax
    inc word [proof+16]
    inc word [proof+12]
irq10_commit:
    nop
    cmp word [proof+10],6
    jne irq10_eoi
    in al,0a1h
    or al,4
    out 0a1h,al
irq10_eoi:
    mov al,20h
    out 0a0h,al
    out 20h,al
    pop ds
    popad
irq10_iret:
    iret
; Actual IRQ11 entry, complete small return frame, slave/master EOI and guest IRET.
irq11:
    pushad
    push ds
    push cs
    pop ds
    mov bp,sp
    mov bx,[proof+16]
    cmp bx,8
    jae irq_failure
    shl bx,4
    add bx,proof+64
    mov word [bx],11
    mov ax,[proof+10]
    mov [bx+2],ax
    mov ax,[proof+16]
    inc ax
    mov [bx+4],ax
    mov ax,[ss:bp+34]
    mov [bx+6],ax
    mov ax,[ss:bp+36]
    mov [bx+8],ax
    mov ax,[ss:bp+38]
    mov [bx+10],ax
    lea ax,[bp+34]
    mov [bx+12],ax
    mov ax,ss
    mov [bx+14],ax
    inc word [proof+16]
    inc word [proof+14]
irq11_commit:
    nop
irq11_eoi:
    mov al,20h
    out 0a0h,al
    out 20h,al
    pop ds
    popad
irq11_iret:
    iret
; A ninth handler is a real guest failure and intentionally cannot pass the host's bounded completion checks.
irq_failure:
    mov word [proof+54],0ffffh
    cli
    hlt
    jmp $
code_end:
saved_flags dw 0
file_handle dw 0
proof db 'PIIXIRQ1'
    times 56-8 db 0
    dd 0,0
    times 8*16 db 0
    db 'IRQEND01'
proof_end:
proof_file db 'C:\PIRQIRQ.DAT',0
fpu_file db 'C:\PIRQFPU.DAT',0
raw_fpu db 'SNAPRAW1'
    times 24 db 0
extended dq 0fedcba9876543210h
    dw 4001h
fm_values dw 0120h,0123h,1040h,0043h,0f060h,0f063h,0780h,0783h,30c0h,98a0h,31b0h
done_text db 13,10,'SNAPSHOT-DONE',13,10,'PIIX-IRQ-DONE',13,10,0
failed_text db 13,10,'PIIX-IRQ-FAILED',13,10,0
align 16,db 0
samples times 512 db 0c0h
    times 2048 db 0
stack_top:
    db 'PIXMETA1'
    dw proof,proof_end,code_end,program_end,phase_out,irq10,irq10_commit,irq10_iret,irq11,irq11_commit,irq11_iret
    dw wait1_return,wait2_return,wait4_return,wait5_return,wait6_return,wait7_return,raw_fpu,stack_top
program_end:
