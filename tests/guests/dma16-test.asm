; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
bits 16
cpu 486
org 100h
jmp start
%include "dma16-vectors.inc"

; Runs this original DOS probe, capturing actual device/CPU results before its checked DOS return.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    mov bx,1000h                 ; retain the complete COM segment and its stack
    mov ah,4ah
    int 21h
    jc allocation_failed
    mov bx,4000h                 ; 256 KiB contains an aligned, entirely owned 128-KiB window
    mov ah,48h
    int 21h
    jc allocation_failed
    mov [allocation],ax
    add ax,1fffh
    and ax,0e000h
    mov [window_segment],ax
    mov fs,ax
    add ax,1000h
    mov gs,ax
    mov ax,[window_segment]
    mov [dds+8],ax
    xor ax,ax
    mov es,ax
    test byte [es:47bh],20h
    push ds
    pop es
    jz vds_ready
    mov ax,8102h
    xor dx,dx
    int 4bh
    jc vds_failed
    cmp ax,0100h
    jne vds_failed
    mov [vds_version],ax
    mov ax,8103h
    mov dx,2ch                 ; 128-KiB alignment, no allocation and no remapping
    mov di,dds
    int 4bh
    jc vds_failed
    inc word [vds_locks]
    cmp word [dds+10],0
    jne vds_failed
    movzx eax,word [window_segment]
    shl eax,4
    cmp [dds+12],eax            ; this bounded conventional buffer must really be identity mapped
    jne vds_failed
    mov bx,5
disable_translation:
    mov ax,810bh
    xor dx,dx
    int 4bh
    jc vds_failed
    inc word [vds_disables]
    bts word [disabled_mask],bx
    inc bx
    cmp bx,8
    jb disable_translation
vds_ready:
    mov dx,300h
    in al,dx
    cmp al,0d6h
    jne fixture_failed
    mov dx,0deh
    in al,dx
    and ax,0fh
    mov [original_mask],ax
    mov al,5
    out 0d4h,al
    mov al,6
    out 0d4h,al
    mov al,7
    out 0d4h,al
    mov si,case_table
    mov di,records
case_loop:
    call initialize_samples
    mov al,[si]
    and al,3
    or al,4
    out 0d4h,al
    in al,0d0h                 ; clear previous TC status, separate from the grant indication
    xor al,al
    out 0d0h,al
    out 0d8h,al
    mov dx,[si]
    sub dx,4
    shl dx,2
    add dx,0c0h
    mov [address_port],dx
    mov ax,[si+4]
    out dx,al
    mov al,ah
    out dx,al
    add dx,2
    mov ax,[si+6]
    out dx,al
    mov al,ah
    out dx,al
    mov bx,[si]
    sub bx,5
    mov dl,[page_ports+bx]
    xor dh,dh
    mov ax,[window_segment]
    shr ax,12
    or al,[si+14]
    out dx,al
    mov al,[si+2]
    mov ah,[si]
    and ah,3
    or al,ah
    out 0d6h,al
    test word [si+12],1
    jnz keep_masked
    mov al,[si]
    and al,3
    out 0d4h,al
keep_masked:
    test word [si+12],2
    jz controller_ready
    mov al,4
    out 0d0h,al
controller_ready:
    call run_fixture
    cmp word [si+16],0
    je take_snapshot
    xor al,al
    out 0d0h,al
    mov al,[si]
    and al,3
    out 0d4h,al
    call run_fixture
take_snapshot:
    mov ax,[completed]
    stosw
    mov dx,[address_port]
    call read_register
    stosw
    add dx,2
    call read_register
    stosw
    mov bx,[si]
    sub bx,5
    xor dx,dx
    mov dl,[page_ports+bx]
    xor ax,ax
    in al,dx
    stosw
    mov dx,0deh
    in al,dx
    and ax,0fh
    stosw
    in al,0d0h
    xor ah,ah
    stosw
    in al,0d0h
    stosw
    mov dx,30ah
    in ax,dx
    stosw
    add dx,2
    in ax,dx
    stosw
    add dx,2
    in ax,dx
    stosw
    mov dx,302h
    in ax,dx
    stosw
    call snapshot_samples
    mov al,[si]
    and al,3
    or al,4
    out 0d4h,al
    xor al,al
    out 0d0h,al
    inc word [completed]
    add si,20
    cmp word [completed],CASE_COUNT
    jb case_loop
    mov dx,0d4h
    mov al,[original_mask]       ; restore masks without pretending to restore write-only DMA modes
    mov dx,0deh
    out dx,al
    cmp word [vds_version],0
    je release_allocation
    mov bx,5
enable_translation:
    mov ax,810ch
    xor dx,dx
    int 4bh
    jc vds_failed
    inc word [vds_enables]
    btr word [disabled_mask],bx
    inc bx
    cmp bx,8
    jb enable_translation
    push ds
    pop es
    mov di,dds
    mov ax,8104h
    xor dx,dx
    int 4bh
    jc vds_failed
    inc word [vds_unlocks]
release_allocation:
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc free_failed
    jmp print_proof
allocation_failed:
    mov word [error],1
    jmp print_proof
fixture_failed:
    mov word [error],2
    jmp print_proof
timeout_failed:
    mov word [error],3
    jmp print_proof
free_failed:
    mov word [error],4
    jmp print_proof
vds_failed:
    mov word [error],5
print_proof:
    mov dx,0e9h
    mov si,proof_message
    call debug_string
    mov ax,ds
    call debug_hex
    mov al,':'
    out dx,al
    mov ax,proof
    call debug_hex
    mov si,newline
    call debug_string
    mov dx,done_message
    mov ah,9
    int 21h
    mov ax,4c00h
    int 21h

; Starts one explicitly timed synthetic DMA16 source/sink case through the test-only fixture port.
run_fixture:
    mov dx,301h
    mov al,[si]
    out dx,al
    inc dx
    mov ax,[si+8]
    out dx,ax
    add dx,2
    mov ax,[si+10]
    out dx,ax
    mov dx,300h
    mov al,[si+18]
    out dx,al
    mov dx,308h
    mov cx,0ffffh
wait_fixture:
    in al,dx
    test al,80h
    jz fixture_finished
    loop wait_fixture
    pop ax
    jmp timeout_failed
fixture_finished:
    ret
; Reads the selected actual DMA/VGA register under its byte-pointer/index contract.
read_register:
    xor al,al
    out 0d8h,al
    in al,dx
    mov ah,al
    in al,dx
    xchg al,ah
    ret
; Fills explicit source/sink sample words and guards for the DMA16 fixture.
initialize_samples:
    push si
    mov si,positions
    mov cx,POSITION_COUNT
initialize_next:
    lodsw
    mov bx,ax
    xor ax,8000h
    call write_sample
    loop initialize_next
    pop si
    ret
; Copies actual DMA source/sink samples into the independent guest proof record.
snapshot_samples:
    push si
    mov si,positions
    mov cx,POSITION_COUNT
snapshot_next:
    lodsw
    mov bx,ax
    call read_sample
    stosw
    loop snapshot_next
    pop si
    ret
; Writes the explicit source/sink sample word into the probe-owned DMA16 buffer.
write_sample:
    push si
    mov si,bx
    shl si,1
    test bx,8000h
    jnz write_high
    mov [fs:si],ax
    pop si
    ret
write_high:
    mov [gs:si],ax
    pop si
    ret
; Reads one controlled source/sink sample word into the guest DMA16 proof.
read_sample:
    push si
    mov si,bx
    shl si,1
    test bx,8000h
    jnz read_high
    mov ax,[fs:si]
    pop si
    ret
read_high:
    mov ax,[gs:si]
    pop si
    ret
; Writes the zero-terminated proof marker through actual guest OUT instructions.
debug_string:
    lodsb
    test al,al
    jz debug_end
    out dx,al
    jmp debug_string
debug_end:
    ret
; Writes the requested value as hexadecimal guest debug-port evidence.
debug_hex:
    push cx
    push bx
    mov bx,ax
    mov cx,4
hex_next:
    rol bx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe hex_output
    add al,7
hex_output:
    out dx,al
    loop hex_next
    pop bx
    pop cx
    ret
page_ports: db 8bh,89h,8ah
address_port: dw 0
proof_message: db 'DMA16-PROOF=',0
newline: db 13,10,0
done_message: db 'DMA16 probe returned',13,10,'$'
align 2
proof:
    db 'D16-PROOF-v1',0,0,0,0
    dw 1,CASE_COUNT
allocation: dw 0
window_segment: dw 0
error: dw 0
completed: dw 0
original_mask: dw 0
    dw 0a55ah
records: times CASE_COUNT*RECORD_WORDS dw 0
    db 'D16VDS01'
vds_version: dw 0
vds_locks: dw 0
vds_unlocks: dw 0
vds_disables: dw 0
vds_enables: dw 0
disabled_mask: dw 0
dds: dd 20000h,0
    dw 0,0
    dd 0
    dw 05aa5h
