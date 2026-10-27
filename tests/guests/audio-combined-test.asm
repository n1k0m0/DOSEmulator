; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own sustained PCM/OPL3/MPU-UART workload with real DMA block IRQ sources and guest-owned VDS memory.
bits 16
cpu 486
org 100h
%ifndef PORT
%define PORT 220h
%endif
%ifndef IRQ
%define IRQ 5
%endif
%ifndef DMA8
%define DMA8 1
%endif
%ifndef DMA16
%define DMA16 5
%endif
%ifndef MPU
%define MPU 330h
%endif
%if IRQ < 8
%define VECTOR (IRQ+8)
%else
%define VECTOR (IRQ+68h)
%endif
%assign WADDR (0c0h+(DMA16-4)*4)
%assign BADDR (DMA8*2)
%if DMA8=0
%define BPAGE 87h
%elif DMA8=1
%define BPAGE 83h
%else
%define BPAGE 82h
%endif
%if DMA16=5
%define WPAGE 8bh
%elif DMA16=6
%define WPAGE 89h
%else
%define WPAGE 8ah
%endif
jmp start

; Reserve an aligned own DMA window, lock it through real VDS where present and install a real shared handler.
start:
    cld
    push cs
    pop ds
    mov sp,stack_top
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    mov bx,4000h
    mov ah,48h
    int 21h
    jc failed
    mov [allocation],ax
    dec ax
    mov es,ax
    mov ax,[es:1]
    mov [mcb_owner],ax
    mov ax,[es:3]
    mov [mcb_size],ax
    mov ax,[allocation]
    add ax,1fffh
    and ax,0e000h
    mov [window],ax
    mov fs,ax
    mov [dds+8],ax
    xor ax,ax
    mov es,ax
    test byte [es:47bh],20h
    push ds
    pop es
    jz .locked
    mov ax,8103h
    mov dx,2ch
    mov di,dds
    int 4bh
    jc failed
    inc word [vds_locks]
    movzx eax,word [window]
    shl eax,4
    cmp [dds+12],eax
    jne failed
    cmp word [dds+10],0
    jne failed
    mov ax,810bh
    mov bx,DMA8
    xor dx,dx
    int 4bh
    jc failed
    inc word [vds_disables]
    mov ax,810bh
    mov bx,DMA16
    xor dx,dx
    int 4bh
    jc failed
    inc word [vds_disables]
.locked:
    mov ax,3500h+VECTOR
    int 21h
    mov [old_irq],bx
    mov [old_irq+2],es
    mov dx,handler
    mov ax,2500h+VECTOR
    int 21h
    in al,21h
    mov [old_master],al
    in al,0a1h
    mov [old_slave],al
    cli
    call mask_irq
    mov word [fs:0ffeh],0a55ah
    mov word [fs:1010h],05aa5h
    mov word [fs:3ffeh],0a55ah
    mov word [fs:4400h],05aa5h
    mov cx,16
    mov di,1000h
    mov al,17
.bytes:
    mov [fs:di],al
    add al,13
    inc di
    loop .bytes
    mov cx,512
    mov di,4000h
    mov ax,1234h
.words:
    mov [fs:di],ax
    add ax,4093
    add di,2
    loop .words
    call reset_dsp
    mov al,3fh
    mov dx,MPU+1
    out dx,al
.uart:
    in al,dx
    test al,80h
    jnz .uart
    dec dx
    in al,dx
    cmp al,0feh
    jne failed
    mov al,41h
    call dsp
    mov al,1fh
    call dsp
    mov al,40h
    call dsp
    mov si,fm_table
    mov cx,24
.fm:
    lodsw
    call fm_write
    loop .fm
    ; First a true DMA8 block, then a true DMA16 block, held pending together with outer MPU data.
    call dma_byte
    mov al,0c0h
    call dsp
    xor al,al
    call dsp
    mov al,15
    call dsp
    xor al,al
    call dsp
    mov bl,1
    call wait_sources
    xor bp,bp
    call dma_word
    mov al,0b0h
    call dsp
    mov al,30h
    call dsp
    mov al,15
    call dsp
    xor al,al
    call dsp
    mov bl,3
    call wait_sources
    mov si,joint_text
    call debug
    mov bl,7
    call wait_sources
    mov [joint_pending],al
    mov dx,PORT+0fh
    in ax,dx
    mov [wide_result],ax
    call mixer
    cmp al,7
    jne failed
    call unmask_irq
    sti
.joint:
    cmp word [joint_handlers],1
    je .joint_done
    hlt
    jmp .joint
.joint_done:
    cmp word [joint_first],7
    jne failed
    cmp word [count8],1
    jne failed
    cmp word [count16],1
    jne failed
    cmp word [rx_count],1
    jne failed
    mov si,long_text
    call debug
    mov byte [phase],1
    mov bp,1
    call dma_word
    mov al,0b6h
    call dsp
    mov al,30h
    call dsp
    mov al,0ffh
    call dsp
    mov al,1
    call dsp
.long:
    mov ax,[sent_blocks]
    cmp ax,[long_blocks]
    jb .send
    cmp word [sent_blocks],512
    jae .finished
    hlt
    jmp .long
.send:
    inc word [sent_blocks]
    mov al,90h
    call midi
    mov ax,[sent_blocks]
    and al,7fh
    call midi
    mov ax,[sent_blocks]
    imul ax,17
    add al,3
    and al,7fh
    call midi
    jmp .long
.finished:
    cmp word [long_blocks],512
    jne failed
    cmp word [handler_errors],0
    jne failed
    cmp word [rx_count],1537
    jne failed
    cmp word [fs:0ffeh],0a55ah
    jne failed
    cmp word [fs:1010h],05aa5h
    jne failed
    cmp word [fs:3ffeh],0a55ah
    jne failed
    cmp word [fs:4400h],05aa5h
    jne failed
    mov ax,00b0h
    call fm_write
    mov ax,00b1h
    call fm_write
    cli
    mov al,[old_master]
    out 21h,al
    mov al,[old_slave]
    out 0a1h,al
    sti
    push ds
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,2500h+VECTOR
    int 21h
    pop ds
    cmp word [vds_locks],0
    je .unlocked
    mov ax,810ch
    mov bx,DMA8
    xor dx,dx
    int 4bh
    jc failed
    inc word [vds_enables]
    mov ax,810ch
    mov bx,DMA16
    xor dx,dx
    int 4bh
    jc failed
    inc word [vds_enables]
    push ds
    pop es
    mov di,dds
    mov ax,8104h
    xor dx,dx
    int 4bh
    jc failed
    inc word [vds_unlocks]
.unlocked:
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc failed
    mov byte [pass],1
    mov si,done_text
    call debug
    ; Keep only the own proof/code block resident so FreeCOM cannot reuse its evidence before the oracle reads it.
    mov dx,(program_end-$$+10fh)/16
    mov ax,3100h
    int 21h

; Terminate a failed probe with an unmistakable debug marker; failed runs never count as acceptance.
failed:
    mov si,fail_text
    call debug
    mov ax,4c01h
    int 21h

; Poll actual source latches without acknowledging them, bounded against a missing completion.
wait_sources:
    mov ecx,4000000
.wait:
    call mixer
    and al,7
    cmp al,bl
    je .done
    dec ecx
    jnz .wait
    jmp failed
.done:
    ret

; Read the CT1745 source register only; the access never consumes DSP or MPU data.
mixer:
    mov dx,PORT+4
    mov al,82h
    out dx,al
    inc dx
    in al,dx
    ret

; Submit an actual ready DSP byte, preserving all loop and DMA setup registers.
dsp:
    push bx
    push ecx
    mov bl,al
    mov dx,PORT+0ch
    mov ecx,1000000
.busy:
    in al,dx
    test al,80h
    jz .ready
    loop .busy
    jmp failed
.ready:
    mov al,bl
    out dx,al
    pop ecx
    pop bx
    ret

; Complete a real reset handshake; no guest response register is injected by the host.
reset_dsp:
    mov dx,PORT+6
    mov al,1
    out dx,al
    mov cx,64
.hold:
    loop .hold
    xor al,al
    out dx,al
    mov dx,PORT+0eh
.ready:
    in al,dx
    test al,80h
    jz .ready
    mov dx,PORT+0ah
    in al,dx
    cmp al,0aah
    jne failed
    ret

; Write a raw MIDI byte through the MPU holding register and its real ready state.
midi:
    push bx
    mov bl,al
    mov dx,MPU+1
.busy:
    in al,dx
    test al,40h
    jnz .busy
    mov al,bl
    dec dx
    out dx,al
    inc word [tx_count]
    pop bx
    ret

; Write one lower-bank FM register through normal ISA-timed address/data bytes.
fm_write:
    mov dx,PORT
    cmp al,5
    jne .selected
    add dx,2
.selected:
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    ret

; Program a 16-byte single-cycle byte-DMA buffer at physical aligned window+1000h.
dma_byte:
    mov al,4|DMA8
    out 0ah,al
    xor al,al
    out 0ch,al
    out BADDR,al
    mov al,10h
    out BADDR,al
    mov al,15
    out BADDR+1,al
    xor al,al
    out BADDR+1,al
    mov ax,[window]
    shr ax,12
    out BPAGE,al
    mov al,48h|DMA8
    out 0bh,al
    mov al,DMA8
    out 0ah,al
    ret

; Program the aligned word buffer at window+4000h; BP chooses single-cycle or auto-init ring count.
dma_word:
    mov al,4|(DMA16-4)
    out 0d4h,al
    xor al,al
    out 0d8h,al
    mov dx,WADDR
    out dx,al
    mov al,20h
    out dx,al
    add dx,2
    mov ax,15
    test bp,bp
    jz .count
    mov ax,511
.count:
    out dx,al
    mov al,ah
    out dx,al
    mov ax,[window]
    shr ax,12
    out WPAGE,al
    mov al,48h|(DMA16-4)
    test bp,bp
    jz .mode
    or al,10h
.mode:
    out 0d6h,al
    mov al,DMA16-4
    out 0d4h,al
    ret

; Mask only the configured card line while retaining PIT/KBC and unrelated interrupt masks.
mask_irq:
%if IRQ < 8
    in al,21h
    or al,1<<IRQ
    out 21h,al
%else
    in al,0a1h
    or al,1<<(IRQ-8)
    out 0a1h,al
%endif
    ret

; Unmask the actual card and cascade as required; no synthetic interrupt is invoked.
unmask_irq:
%if IRQ < 8
    in al,21h
    and al,0ffh^(1<<IRQ)
    out 21h,al
%else
    in al,21h
    and al,0fbh
    out 21h,al
    in al,0a1h
    and al,0ffh^(1<<(IRQ-8))
    out 0a1h,al
%endif
    ret

; Drain real MPU and DSP sources separately, verify wrong-width ACK behavior, issue true EOIs and IRET.
handler:
    pushad
    push ds
    push cs
    pop ds
    inc word [irq_count]
    call mixer
    mov cl,al
    cmp byte [phase],0
    jne .midi
    inc word [joint_handlers]
    xor ah,ah
    mov [joint_first],ax
.midi:
    test cl,4
    jz .byte
.read:
    mov dx,MPU+1
    in al,dx
    test al,80h
    jnz .byte
    dec dx
    in al,dx
    mov bx,[rx_count]
    cmp bx,1537
    jae .error
    mov [rx_data+bx],al
    inc word [rx_count]
    jmp .read
.byte:
    test cl,1
    jz .word
    mov dx,PORT+0eh
    in al,dx
    inc word [count8]
.word:
    test cl,2
    jz .finish
    cmp byte [phase],0
    je .ack
    ; This wrong-width acknowledgment must leave the live word source asserted.
    mov dx,PORT+0eh
    in al,dx
    call mixer
    test al,2
    jz .error
.ack:
    mov dx,PORT+0fh
    in al,dx
    inc word [count16]
    cmp byte [phase],0
    je .finish
    inc word [long_blocks]
    cmp word [long_blocks],511
    jne .finish
    mov al,0d9h
    call dsp
.finish:
    call mixer
    and al,7
    jz .eoi
    mov cl,al
    jmp .midi
.error:
    inc word [handler_errors]
.eoi:
    mov al,20h
%if IRQ >= 8
    out 0a0h,al
%endif
    out 20h,al
    pop ds
    popad
    iret

; Emit only own markers to E9; avoiding a duplicate BIOS mirror keeps the execution oracle unambiguous.
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

joint_text: db 'COMB-JOINT',13,10,0
long_text: db 'COMB-LONG',13,10,0
done_text: db 'COMB-DONE',13,10,0
fail_text: db 'COMB-FAILED',13,10,0
; NEW is deliberately written through the upper pair once before the lower-bank setup below.
fm_table: dw 0105h,2120h,2123h,2040h,0043h,0f060h,0f063h,0f80h,0f83h,10c0h,98a0h,31b0h
          dw 2121h,2124h,2041h,0044h,0f061h,0f064h,0f81h,0f84h,20c1h,55a1h,32b1h,0001h
proof: db 'COMBPRO1'
allocation: dw 0
window: dw 0
mcb_owner: dw 0
mcb_size: dw 0
old_irq: dw 0,0
old_master: db 0
old_slave: db 0
phase: db 0
pass: db 0
joint_pending: dw 0
wide_result: dw 0
joint_handlers: dw 0
joint_first: dw 0
count8: dw 0
count16: dw 0
long_blocks: dw 0
sent_blocks: dw 0
irq_count: dw 0
handler_errors: dw 0
rx_count: dw 0
tx_count: dw 0
vds_locks: dw 0
vds_unlocks: dw 0
vds_disables: dw 0
vds_enables: dw 0
dds: dd 20000h,0
     dw 0,0
     dd 0
rx_data: times 1537 db 0
guard: dw 0faceh
       times 2048 db 0
stack_top:
program_end:
