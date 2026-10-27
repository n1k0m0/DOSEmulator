; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
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
%if IRQ < 8
%define VECTOR (IRQ+8)
%else
%define VECTOR (IRQ+68h)
%endif
%assign DMA8ADDR (DMA8*2)
%assign DMA16ADDR (0c0h+(DMA16-4)*4)
%if DMA8=0
%define DMA8PAGE 87h
%elif DMA8=1
%define DMA8PAGE 83h
%else
%define DMA8PAGE 82h
%endif
%if DMA16=5
%define DMA16PAGE 8bh
%elif DMA16=6
%define DMA16PAGE 89h
%else
%define DMA16PAGE 8ah
%endif
jmp start
%include "sb16-vectors.inc"

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    mov bx,1000h
    mov ah,4ah
    int 21h
    jc fail_allocate
    mov bx,4000h
    mov ah,48h
    int 21h
    jc fail_allocate
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
    jz vds_ready
    mov ax,8103h
    mov dx,2ch
    mov di,dds
    int 4bh
    jc fail_vds
    inc word [vds_locks]
    movzx eax,word [window]
    shl eax,4
    cmp [dds+12],eax
    jne fail_vds
    cmp word [dds+10],0
    jne fail_vds
    mov bx,DMA8
    mov ax,810bh
    xor dx,dx
    int 4bh
    jc fail_vds
    inc word [vds_disables]
    mov bx,DMA16
    mov ax,810bh
    xor dx,dx
    int 4bh
    jc fail_vds
    inc word [vds_disables]
vds_ready:
    mov ax,3500h+VECTOR
    int 21h
    mov [saved_offset],bx
    mov [saved_segment],es
    push ds
    pop es
    mov dx,irq_handler
    mov ax,2500h+VECTOR
    int 21h
    in al,21h
    mov [saved_master],al
    in al,0a1h
    mov [saved_slave],al
%if IRQ < 8
    mov al,[saved_master]
    and al,(0ffh ^ (1<<IRQ))
    out 21h,al
%else
    mov al,[saved_master]
    and al,0fbh
    out 21h,al
    mov al,[saved_slave]
    and al,(0ffh ^ (1<<(IRQ-8)))
    out 0a1h,al
%endif
    call reset_dsp
    mov al,0e1h
    call send_dsp
    call read_dsp
    mov [dsp_version+1],al
    call read_dsp
    mov [dsp_version],al
    mov al,80h
    call read_mixer
    xor ah,ah
    mov [irq_resource],ax
    mov al,81h
    call read_mixer
    mov [dma_resource],ax
    mov si,case_table
    mov di,records
case_loop:
    mov [record_pointer],di
    mov word [case_irqs],0
    mov word [source_or],0
    mov byte [joint],0
    call initialize_buffer
    mov al,81h
    mov ah,(1<<DMA8)|(1<<DMA16)
    cmp word [si+12],0
    je normal_resource
    mov ah,1<<DMA8
normal_resource:
    call write_mixer
    mov ax,0f830h
    call write_mixer
    mov ax,0f831h
    call write_mixer
    mov ax,0f832h
    call write_mixer
    mov ax,0f833h
    call write_mixer
    cmp word [si+14],0
    je gain_ready
    mov ax,0e831h
    call write_mixer
    mov ax,0d833h
    call write_mixer
gain_ready:
    mov al,41h
    call send_dsp
    mov ax,[si+8]
    mov al,ah
    call send_dsp
    mov al,[si+8]
    call send_dsp
    mov al,42h
    call send_dsp
    mov ax,[si+8]
    mov al,ah
    call send_dsp
    mov al,[si+8]
    call send_dsp
    mov al,0d3h              ; DSP4 speaker flag must not gate the CT1745 PCM path
    call send_dsp
    call program_dma
    mov al,[si]
    call send_dsp
    mov al,[si+2]
    call send_dsp
    mov ax,[si+4]
    dec ax
    push ax
    call send_dsp
    pop ax
    mov al,ah
    call send_dsp
    mov word [timeout_ticks],0
wait_case:
    mov ax,[case_irqs]
    cmp ax,[si+10]
    jae case_finished
    hlt
    inc word [timeout_ticks]
    cmp word [timeout_ticks],300
    jb wait_case
    jmp fail_irq
case_finished:
    mov ax,[completed]
    stosw
    mov ax,[case_irqs]
    stosw
    mov ax,[source_first]
    stosw
    mov ax,[source_wrong]
    stosw
    mov ax,[source_after]
    stosw
    mov ax,[source_or]
    stosw
    cmp word [dma_word],0
    je snapshot_byte_dma
    mov dx,DMA16ADDR
    xor al,al
    out 0d8h,al
    call read_register
    stosw
    add dx,2
    call read_register
    stosw
    in al,0d0h
    xor ah,ah
    stosw
    in al,0d0h
    stosw
    jmp snapshot_mixer
snapshot_byte_dma:
    mov dx,DMA8ADDR
    xor al,al
    out 0ch,al
    call read_register
    stosw
    inc dx
    call read_register
    stosw
    in al,8
    xor ah,ah
    stosw
    in al,8
    stosw
snapshot_mixer:
    mov al,30h
    call read_mixer
    xor ah,ah
    stosw
    mov al,31h
    call read_mixer
    stosw
    mov al,32h
    call read_mixer
    stosw
    mov al,33h
    call read_mixer
    stosw
    mov ax,[fs:1ffeh]
    stosw
    mov ax,[fs:2080h]
    stosw
    push si
    mov si,2000h
    mov cx,64
copy_sample:
    mov ax,[fs:si]
    stosw
    add si,2
    loop copy_sample
    pop si
    inc word [completed]
    add si,16
    cmp word [completed],CASE_COUNT
    jb case_loop

    ; Two real DMA completions while interrupts are disabled; one shared IRQ sees both sources.
    cli
    mov byte [joint],1
    mov si,scratch_row
    mov ax,((1<<DMA8)|(1<<DMA16))*256+81h
    call write_mixer
    mov word [si],0c0h          ; scratch row is outside the immutable case table
    mov word [si+2],0
    mov word [si+4],1
    mov word [si+6],1
    mov word [si+10],1
    mov word [si+12],0
    call program_dma
    mov al,0c0h
    call send_dsp
    xor al,al
    call send_dsp
    call send_dsp
    call send_dsp
    mov bx,1
    call wait_sources
    mov word [si],0b0h
    call program_dma
    mov al,0b0h
    call send_dsp
    mov al,10h
    call send_dsp
    xor al,al
    call send_dsp
    call send_dsp
    mov bx,3
    call wait_sources
    sti
wait_joint:
    cmp word [joint_seen],3
    je joint_finished
    hlt
    jmp wait_joint
joint_finished:
    call reset_dsp
    cli
    mov al,[saved_master]
    out 21h,al
    mov al,[saved_slave]
    out 0a1h,al
    sti
    push ds
    mov dx,[saved_offset]
    mov ax,[saved_segment]
    mov ds,ax
    mov ax,2500h+VECTOR
    int 21h
    pop ds
    cmp word [vds_locks],0
    je vds_released
    mov ax,810ch
    mov bx,DMA8
    xor dx,dx
    int 4bh
    jc fail_vds
    inc word [vds_enables]
    mov ax,810ch
    mov bx,DMA16
    xor dx,dx
    int 4bh
    jc fail_vds
    inc word [vds_enables]
    push ds
    pop es
    mov di,dds
    mov ax,8104h
    xor dx,dx
    int 4bh
    jc fail_vds
    inc word [vds_unlocks]
vds_released:
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc fail_allocate
    jmp report

; Observes and separately acknowledges actual DSP byte/word IRQ sources before both required EOIs.
irq_handler:
    pushad
    push ds
    push cs
    pop ds
    inc word [irq_total]
    mov al,82h
    call read_mixer
    xor ah,ah
    cmp byte [joint],0
    jne joint_handler
    mov [source_first],ax
    or [source_or],ax
    test byte [active_command],10h
    jnz handler_word
    mov dx,PORT+0fh
    in al,dx
    mov al,82h
    call read_mixer
    mov [source_wrong],ax
    mov dx,PORT+0eh
    in al,dx
    jmp handler_acked
handler_word:
    mov dx,PORT+0eh
    in al,dx
    mov al,82h
    call read_mixer
    mov [source_wrong],ax
    mov dx,PORT+0fh
    in al,dx
handler_acked:
    mov al,82h
    call read_mixer
    mov [source_after],ax
    inc word [case_irqs]
    cmp word [case_irqs],2
    jne handler_eoi
    test byte [active_command],4
    jz handler_eoi
    mov al,0dah
    test byte [active_command],10h
    jz handler_exit_auto
    mov al,0d9h
handler_exit_auto:
    call send_dsp
    jmp handler_eoi
joint_handler:
    mov [joint_seen],ax
    mov dx,PORT+0eh
    in al,dx
    mov al,82h
    call read_mixer
    xor ah,ah
    mov [joint_after8],ax
    mov dx,PORT+0fh
    in al,dx
    mov al,82h
    call read_mixer
    mov [joint_after16],ax
handler_eoi:
%if IRQ >= 8
    mov al,20h
    out 0a0h,al
%endif
    mov al,20h
    out 20h,al
    pop ds
    popad
    iret

; Programs the actual byte/word DMA controller with the probe's owned physical buffer.
program_dma:
    mov ax,[si]
    mov [active_command],al
    mov word [dma_word],0
    mov bx,[si+6]
    test ax,10h
    jz program_byte
    cmp word [si+12],0
    je program_word
    shl bx,1                 ; 16-bit samples through DMA8 count two bytes each
program_byte:
    mov al,4|DMA8
    out 0ah,al
    in al,8
    xor al,al
    out 0ch,al
    out DMA8ADDR,al
    mov al,20h
    out DMA8ADDR,al
    dec bx
    mov ax,bx
    out DMA8ADDR+1,al
    mov al,ah
    out DMA8ADDR+1,al
    mov ax,[window]
    shr ax,12
    out DMA8PAGE,al
    mov al,48h|DMA8
    test byte [si],8
    jz byte_direction
    mov al,44h|DMA8
byte_direction:
    test byte [si],4
    jz byte_mode
    or al,10h
byte_mode:
    out 0bh,al
    mov al,DMA8
    out 0ah,al
    ret
program_word:
    mov word [dma_word],1
    mov al,4|(DMA16-4)
    out 0d4h,al
    in al,0d0h
    xor al,al
    out 0d8h,al
    mov dx,DMA16ADDR
    out dx,al
    mov al,10h
    out dx,al
    add dx,2
    dec bx
    mov ax,bx
    out dx,al
    mov al,ah
    out dx,al
    mov ax,[window]
    shr ax,12
    out DMA16PAGE,al
    mov al,48h|(DMA16-4)
    test byte [si],8
    jz word_direction
    mov al,44h|(DMA16-4)
word_direction:
    test byte [si],4
    jz word_mode
    or al,10h
word_mode:
    out 0d6h,al
    mov al,DMA16-4
    out 0d4h,al
    ret
; Fills the probe-owned PCM buffer and guards before programming DMA.
initialize_buffer:
    mov word [fs:1ffeh],0a55ah
    mov word [fs:2080h],05aa5h
    push di
    mov di,2000h
    test word [si],10h
    jz initialize_byte
    mov ax,[completed]
    inc ax
    imul ax,1234h
    mov cx,64
initialize_word_loop:
    mov [fs:di],ax
    add di,2
    add ax,4093
    loop initialize_word_loop
    pop di
    ret
initialize_byte:
    mov ax,[completed]
    inc ax
    imul ax,13
    mov cx,128
initialize_byte_loop:
    mov [fs:di],al
    inc di
    add al,37
    loop initialize_byte_loop
    pop di
    ret
; Polls the real DSP before sending the next command/parameter byte.
send_dsp:
    push ax
    mov dx,PORT+0ch
send_wait:
    in al,dx
    test al,80h
    jnz send_wait
    pop ax
    out dx,al
    ret
; Polls DSP readiness with a bounded timeout before reading the actual reply.
read_dsp:
    mov dx,PORT+0eh
read_wait:
    in al,dx
    test al,80h
    jz read_wait
    mov dx,PORT+0ah
    in al,dx
    ret
; Writes the selected actual CT1745 mixer register.
write_mixer:
    mov dx,PORT+4
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    ret
; Reads the requested actual CT1745 mixer register.
read_mixer:
    mov dx,PORT+4
    out dx,al
    inc dx
    in al,dx
    ret
; Reads the selected actual DMA/VGA register under its byte-pointer/index contract.
read_register:
    in al,dx
    mov ah,al
    in al,dx
    xchg al,ah
    ret
; Resets the actual DSP and requires its ready reply within the bounded guest poll.
reset_dsp:
    mov dx,PORT+6
    mov al,1
    out dx,al
    mov cx,64
reset_hold:
    in al,dx
    loop reset_hold
    xor al,al
    out dx,al
    call read_dsp
    cmp al,0aah
    jne fail_dsp
    ret
; Waits for the expected separately acknowledged DSP IRQ sources with a bounded timeout.
wait_sources:
    mov cx,0ffffh
wait_sources_loop:
    mov al,82h
    call read_mixer
    xor ah,ah
    cmp ax,bx
    je sources_ready
    loop wait_sources_loop
    jmp fail_irq
sources_ready:
    ret
fail_allocate:
    mov word [error],1
    jmp report
fail_vds:
    mov word [error],2
    jmp report
fail_dsp:
    mov word [error],3
    jmp report
fail_irq:
    mov word [error],4
report:
    mov si,marker
    mov dx,0e9h
    call debug_string
    mov ax,ds
    call debug_hex
    mov al,':'
    out dx,al
    mov ax,proof
    call debug_hex
    mov si,newline
    call debug_string
    mov dx,returned
    mov ah,9
    int 21h
    mov ax,4c00h
    int 21h
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz debug_end
    out dx,al
    jmp debug_string
debug_end: ret
; Writes the requested value as hexadecimal guest debug-port evidence.
debug_hex:
    push bx
    push cx
    mov bx,ax
    mov cx,4
hex_loop:
    rol bx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe hex_out
    add al,7
hex_out:
    out dx,al
    loop hex_loop
    pop cx
    pop bx
    ret
record_pointer: dw 0
case_irqs: dw 0
source_first: dw 0
source_wrong: dw 0
source_after: dw 0
source_or: dw 0
dma_word: dw 0
timeout_ticks: dw 0
active_command: db 0
joint: db 0
; Publishes the probe's actual output marker for the independent host observer.
marker: db 'SB16-PROOF=',0
newline: db 13,10,0
returned: db 'SB16 PCM probe returned',13,10,'$'
align 2
proof:
    db 'SB16PCM1'
    dw 1,CASE_COUNT,PORT,IRQ,DMA8,DMA16
allocation: dw 0
window: dw 0
mcb_owner: dw 0
mcb_size: dw 0
completed: dw 0
error: dw 0
; Queries and records the actual DSP version response.
dsp_version: dw 0
irq_resource: dw 0
dma_resource: dw 0
irq_total: dw 0
saved_offset: dw 0
saved_segment: dw 0
saved_master: db 0
saved_slave: db 0
joint_seen: dw 0
joint_after8: dw 0
joint_after16: dw 0
vds_locks: dw 0
vds_unlocks: dw 0
vds_disables: dw 0
vds_enables: dw 0
dds: dd 20000h,0
    dw 0,0
    dd 0
    dw 0beefh
records: times CASE_COUNT*RECORD_WORDS dw 0
    dw 0faceh
scratch_row: times 8 dw 0
