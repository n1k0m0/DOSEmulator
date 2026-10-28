; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own PIIX function-one DMA client. Only primary-slave scratch media receives ATA writes.
bits 16
cpu pentium
org 100h
; Reserve this complete DOS process and a private aligned/locked physical DMA window.
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
    mov [proof+48],eax
    mov [proof+52],edx
    cmp eax,524h
    jne failed
    cmp edx,135h
    jne failed
    mov bx,4002h
    mov ah,48h
    int 21h
    jc failed
    mov [allocation],ax
    mov [proof+16],ax
    push es
    dec ax
    mov es,ax
    mov ax,[es:1]
    mov [proof+56],ax
    mov ax,[es:3]
    mov [proof+58],ax
    pop es
    movzx eax,word [allocation]
    shl eax,4
    add eax,20000h
    and eax,0fffe0000h
    mov [proof+20],eax
    mov edx,eax
    shr eax,4
    mov [window],ax
    mov [proof+18],ax
    add edx,1f000h
    mov [prdt],edx
    mov word [dds+8],ax
    mov eax,[proof+20]
    mov [dds+12],eax
    cmp word [proof+8],0
    je dma_window_locked
    xor ax,ax
    mov es,ax
    test byte [es:47bh],20h
    push cs
    pop es
    jz failed
    mov di,dds
    mov dx,2ch
    mov ax,8103h
vds_lock:
    int 4bh
    jc failed
    inc word [proof+60]
    mov word [proof+14],1
    cmp word [dds+10],0
    jne failed
    mov eax,[dds+12]
    cmp eax,[proof+20]
    jne failed
dma_window_locked:
    mov si,dds
    mov di,proof+96
    mov cx,8
    rep movsw
    mov ax,[window]
    mov es,ax
    xor di,di
    mov ax,0a5a5h
    mov cx,8000h
    rep stosw
    mov ax,es
    add ax,1000h
    mov es,ax
    xor di,di
    mov ax,0a5a5h
    mov cx,8000h
    rep stosw
    ; Explicit owned 16-byte guards surround the complete 128-KiB DMA/PRD window.
    mov ax,[window]
    dec ax
    mov es,ax
    xor di,di
    mov cx,16
    mov al,0d6h
    rep stosb
    mov ax,[window]
    add ax,2000h
    mov es,ax
    xor di,di
    mov cx,16
    mov al,0d6h
    rep stosb
    push cs
    pop es
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
    mov ax,3576h
    int 21h
    mov [proof+68],bx
    mov [proof+70],es
    push cs
    pop es
    mov dx,irq14
    mov ax,2576h
    int 21h
    cli
    in al,21h
    mov [proof+24],al
    in al,0a1h
    mov [proof+25],al
    ; Drain the real already-latched boot PIO edge before counting any DMA IRQ.
    mov dx,1f6h
    mov al,0e0h
    out dx,al
    inc dx
    in al,dx
    dec dx
    mov al,0f0h
    out dx,al
    inc dx
    in al,dx
    mov al,[proof+25]
    and al,0bfh
    out 0a1h,al
    mov al,[proof+24]
    and al,0fbh
    out 21h,al
    mov ah,0
    int 1ah
    mov bx,dx
drain_loop:
    sti
    hlt
drain_return:
    mov ah,0
    int 1ah
    sub dx,bx
    cmp dx,2
    jb drain_loop
    cli
    ; Read the actual function, original gates and the fixed firmware BM BAR.
    mov eax,80003900h
    call cfg_read
    mov [proof+36],eax
    cmp eax,12308086h
    jne failed
    mov eax,80003908h
    call cfg_read
    mov [proof+40],eax
    cmp eax,01018002h
    jne failed
    mov eax,80003904h
    call cfg_read
    mov [proof+26],ax
    cmp ax,3
    jne failed
    mov eax,80003940h
    call cfg_read
    mov [proof+28],eax
    test eax,8000h
    jz failed
    test eax,80000000h
    jz failed
    mov eax,80003920h
    call cfg_read
    mov [proof+32],eax
    cmp eax,0000c001h
    jne failed
    mov eax,80003870h
    call cfg_read
    mov [proof+44],al
    cmp al,0fh
    jne failed
    ; IOSE really hides the legacy task port; the disabled command must have no effect.
    mov eax,80003904h
    mov bx,2
    call cfg_word
    mov dx,1f7h
    in al,dx
    mov [proof+45],al
    cmp al,0ffh
    jne failed
    mov al,0ech
    out dx,al
    mov eax,80003904h
    mov bx,3
    call cfg_word
    ; IDETIM.DE independently hides the primary task/control decode.
    mov eax,80003940h
    mov ebx,[proof+28]
    and ebx,0ffff7fffh
    call cfg_dword
    mov dx,1f7h
    in al,dx
    mov [proof+46],al
    cmp al,0ffh
    jne failed
    mov eax,80003940h
    mov ebx,[proof+28]
    call cfg_dword
    ; Select the actual supported multiword DMA mode zero on scratch, before the measured transfers.
    mov dx,1f1h
    mov al,3
    out dx,al
    inc dx
    mov al,20h
    out dx,al
    mov dx,1f7h
    mov al,0efh
    out dx,al
    sti
    hlt
    cli
    mov dx,1f7h
    in al,dx
    mov [proof+82],al
    cmp al,50h
    jne failed
    mov dx,0c000h
    in al,dx
    mov [proof+76],al
    add dx,2
    in al,dx
    mov [proof+77],al
    mov al,66h
    out dx,al
    mov eax,80003904h
    mov bx,7
    call cfg_word
    ; PCM, raw x87 and virtual ports remain live through both real DMA cuts.
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
    ; Read two real sectors with a descriptor boundary inside the first sector.
    call scatter_prds
    mov word [proof+10],1
    mov dword [lba],8
    mov byte [sector_count],2
    mov byte [ata_command],0c8h
    call ready
    call issue
    call wait_irq
    call done
    call stop
    call save_scatter
    call check_scatter_read
    jc failed
    ; Write distinct own bytes through the same noncontiguous physical regions.
    call fill_scatter
    call scatter_prds
    mov word [proof+10],2
    mov dword [lba],32
    mov byte [sector_count],2
    mov byte [ata_command],0cah
    call ready
    call issue
    call wait_irq
    call done
    call stop
    ; Read back the actual scratch backend using another true READ DMA.
    call clear_scatter
    call scatter_prds
    mov word [proof+10],3
    mov byte [ata_command],0c8h
    call ready
    call issue
    call wait_irq
    call done
    call stop
    call check_scatter_write
    jc failed
    ; A literal zero PRD count denotes 65536 bytes, not an empty region.
    call full_prd
    mov word [proof+10],4
    mov dword [lba],128
    mov byte [sector_count],128
    mov byte [ata_command],0c8h
    call ready
    call issue
    call wait_irq
    call done
    call stop
    call check_full
    jc failed
    call full_prd
    mov word [proof+10],5
    mov dword [lba],512
    mov byte [ata_command],0cah
    call ready
    call issue
    call wait_irq
    call done
    call stop
    ; nIEN hides only the drive output. A completed transfer cannot fake a BM interrupt edge.
    call quiet_prd
    mov word [proof+10],6
    mov dword [lba],8
    mov byte [sector_count],1
    mov byte [ata_command],0c8h
    mov dx,3f6h
    mov al,2
    out dx,al
    call ready
    call issue
    mov ecx,4000000
.quiet_wait:
    mov dx,0c002h
    in al,dx
    test al,1
    jz .quiet_complete
    dec ecx
    jnz .quiet_wait
    jmp failed
.quiet_complete:
    mov [proof+47],al
    and al,7
    jnz failed
    mov dx,3f6h
    in al,dx
    cmp al,50h
    jne failed
    xor al,al
    out dx,al
    call wait_irq
    call done
    call stop
    cli
    mov word [proof+10],7
    call ready
    mov al,[proof+25]
    out 0a1h,al
    mov al,[proof+24]
    out 21h,al
    mov dx,0c000h
    mov al,[proof+76]
    out dx,al
    add dx,2
    mov al,[proof+77]
    and al,60h
    or al,6
    out dx,al
    mov eax,80003904h
    mov bx,[proof+26]
    call cfg_word
    mov dx,[proof+68]
    mov ax,[proof+70]
    mov ds,ax
    mov ax,2576h
    int 21h
    push cs
    pop ds
    cmp word [proof+60],0
    je vds_unlocked
    push cs
    pop es
    mov di,dds
    xor dx,dx
    mov ax,8104h
vds_unlock:
    int 4bh
    jc failed
    inc word [proof+62]
vds_unlocked:
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
    mov dx,read_file
    call write_full_file
    mov dx,scatter_file
    mov si,scatter_copy
    mov cx,1024
    call file
    mov dx,fpu_file
    mov si,raw_fpu
    mov cx,32
    call file
    mov word [proof+64],1
    mov dx,proof_file
    mov si,proof
    mov cx,proof_end-proof
    call file
    mov ax,[allocation]
    mov es,ax
    mov ah,49h
    int 21h
    jc failed
    push cs
    pop es
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
; All failures remain visible and cannot count as an actual DMA or DOS return proof.
failed:
    push cs
    pop ds
    sti
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Publish only a completed actual own OUT, never a host controller mutation.
ready:
    mov ax,[proof+10]
    add al,0b0h
ready_out:
    out 0e9h,al
    ret
; Mark complete DMA data while the original physical window and descriptor cursor are still present.
done:
    mov ax,[proof+10]
    add al,0c0h
done_out:
    out 0e9h,al
    ret
; Program actual selected primary-slave LBA28 and true ATA DMA command, then start the real PCI master.
issue:
    cli
    mov dx,0c000h
    xor al,al
    out dx,al
    add dx,2
    mov al,66h
    out dx,al
    add dx,2
    mov eax,[prdt]
    out dx,eax
    mov dx,1f6h
    mov al,0f0h
    out dx,al
    inc dx
    in al,dx
    mov dx,1f2h
    mov al,[sector_count]
    out dx,al
    mov eax,[lba]
    inc dx
    out dx,al
    shr eax,8
    inc dx
    out dx,al
    shr eax,8
    inc dx
    out dx,al
    inc dx
    shr eax,8
    and al,15
    or al,0f0h
    out dx,al
    inc dx
    mov al,[ata_command]
    out dx,al
    mov dx,0c000h
    mov al,1
    cmp byte [ata_command],0c8h
    jne .start
    or al,8
.start:
    out dx,al
    ret
; Keep DMA waits in the original own real/V86 client, without entering the resident HLT monitor.
; STI protects exactly the NOP; an IRQ can be accepted only at wait_return before CLI.
wait_irq:
irq_wait_loop:
    sti
    nop
wait_return:
    cli
    mov ax,[proof+10]
    cmp [proof+12],ax
    jb irq_wait_loop
    ret
; Stop only the busmaster cursor after the real drive completion, never reset the DOS channel.
stop:
    mov dx,0c000h
    xor al,al
    out dx,al
    ret
; Read the selected actual PCI dword through mechanism one.
cfg_read:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    ret
; Write an actual selected PCI word while preserving the full address transaction.
cfg_word:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov ax,bx
    out dx,ax
    ret
; Write an actual selected PCI dword for the independent legacy decode gate.
cfg_dword:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,eax
    ret
; Build two real eight-byte PRDs in the locked final 4-KiB page, neither data region crosses 64 KiB.
scatter_prds:
    mov ax,[window]
    add ax,1000h
    mov es,ax
    mov di,0f000h
    mov eax,[proof+20]
    add eax,100h
    stosd
    mov eax,510
    stosd
    mov eax,[proof+20]
    add eax,9000h
    stosd
    mov eax,80000202h
    stosd
    push cs
    pop es
    ret
; One actual zero-count descriptor spans a complete aligned 64-KiB transfer.
full_prd:
    mov ax,[window]
    add ax,1000h
    mov es,ax
    mov di,0f000h
    mov eax,[proof+20]
    stosd
    mov eax,80000000h
    stosd
    push cs
    pop es
    ret
; Keep the final nIEN read separate from the complete 64-KiB readback file.
quiet_prd:
    mov ax,[window]
    add ax,1000h
    mov es,ax
    mov di,0f000h
    mov eax,[proof+20]
    add eax,10100h
    stosd
    mov eax,80000200h
    stosd
    push cs
    pop es
    ret
; Preserve every byte of the first scatter READ DMA before the guest prepares the write source.
save_scatter:
    push ds
    mov ax,[window]
    mov ds,ax
    push cs
    pop es
    mov si,100h
    mov di,scatter_copy
    mov cx,510
    rep movsb
    mov si,9000h
    mov cx,514
    rep movsb
    pop ds
    ret
; Fill the two source regions with one independent logical pattern across the split descriptor boundary.
fill_scatter:
    mov ax,[window]
    mov es,ax
    mov di,100h
    xor si,si
    mov cx,510
    call .region
    mov di,9000h
    mov cx,514
    call .region
    push cs
    pop es
    ret
.region:
    mov ax,si
    mov dx,si
    shr dx,8
    imul dx,11
    imul ax,29
    add ax,dx
    add al,5bh
    stosb
    inc si
    loop .region
    ret
; Clear only the exact two readback regions without changing guard bytes or descriptors.
clear_scatter:
    mov ax,[window]
    mov es,ax
    mov di,100h
    mov cx,510
    mov al,0a5h
    rep stosb
    mov di,9000h
    mov cx,514
    rep stosb
    push cs
    pop es
    ret
; Compare the first actual disk bytes with their source LBA/byte identity.
check_scatter_read:
    push ds
    push cs
    pop ds
    mov si,scatter_copy
    xor di,di
    mov cx,1024
.next:
    mov ax,di
    mov dx,di
    shr dx,9
    add dx,8
    imul dx,13
    and ax,511
    mov bx,ax
    shr bx,8
    imul bx,7
    imul ax,17
    add ax,bx
    add ax,dx
    add al,31h
    cmp [si],al
    jne .bad
    inc si
    inc di
    loop .next
    pop ds
    clc
    ret
.bad:
    pop ds
    stc
    ret
; Verify the backend roundtrip of the actual noncontiguous WRITE DMA.
check_scatter_write:
    mov ax,[window]
    mov es,ax
    mov di,100h
    xor si,si
    mov cx,510
    call .region
    jc .bad
    mov di,9000h
    mov cx,514
    call .region
    jc .bad
    push cs
    pop es
    clc
    ret
.region:
    mov ax,si
    mov dx,si
    shr dx,8
    imul dx,11
    imul ax,29
    add ax,dx
    add al,5bh
    cmp [es:di],al
    jne .bad
    inc si
    inc di
    loop .region
    clc
    ret
.bad:
    push cs
    pop es
    stc
    ret
; Check all 65536 actual DMA bytes, including each sector's independent source LBA contribution.
check_full:
    mov ax,[window]
    mov es,ax
    xor di,di
    xor cx,cx
.next:
    mov ax,di
    mov dx,di
    shr dx,9
    add dx,128
    imul dx,13
    and ax,511
    mov bx,ax
    shr bx,8
    imul bx,7
    imul ax,17
    add ax,bx
    add ax,dx
    add al,31h
    cmp [es:di],al
    jne .bad
    inc di
    loop .next
    push cs
    pop es
    clc
    ret
.bad:
    push cs
    pop es
    stc
    ret
; Persist exactly 64 KiB through two nonzero actual DOS writes before releasing its owned allocation.
write_full_file:
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    push ds
    mov ax,[window]
    mov ds,ax
    xor dx,dx
    mov cx,8000h
    mov ah,40h
    int 21h
    jc .bad
    cmp ax,8000h
    jne .bad
    mov dx,8000h
    mov cx,8000h
    mov ah,40h
    int 21h
    jc .bad
    cmp ax,8000h
    jne .bad
    pop ds
    mov ah,3eh
    int 21h
    jc failed
    ret
.bad:
    pop ds
    jmp failed
; Use actual DOS create/write/close for the own bounded proof files.
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
; Wait for the actual CT1740 busy latch before each guest DSP byte.
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
; Print using the real DOS console, without any host text or keyboard injection into the result.
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
; Genuine IRQ14 frame, separately observable ATA ACK, BM edge-latch W1C and ordered PIC EOIs.
irq14:
    pushad
    push ds
    push cs
    pop ds
    mov bp,sp
    cmp word [proof+10],0
    jne .dma
    mov dx,1f7h
    in al,dx
    inc word [proof+80]
    jmp irq_eoi
.dma:
    mov bx,[proof+12]
    cmp bx,6
    jae irq_failure
    shl bx,5
    add bx,proof+128
    mov word [bx],14
    mov ax,[proof+10]
    mov [bx+2],ax
    mov ax,[proof+12]
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
    mov dx,0c002h
    in al,dx
    mov [bx+16],al
    test word [proof+10],1
    jz .bm_first
    mov dx,1f7h
    in al,dx
    mov [bx+17],al
    mov dx,0c002h
    in al,dx
    mov [bx+18],al
    mov al,64h
    out dx,al
    in al,dx
    mov [bx+19],al
    jmp .committed
.bm_first:
    mov al,64h
    out dx,al
    in al,dx
    mov [bx+18],al
    mov dx,3f6h
    in al,dx
    mov [bx+20],al
    mov dx,1f7h
    in al,dx
    mov [bx+17],al
    mov dx,0c002h
    in al,dx
    mov [bx+19],al
.committed:
    inc word [proof+12]
irq_commit:
    nop
irq_eoi:
    mov al,20h
    out 0a0h,al
    out 20h,al
    pop ds
    popad
irq_iret:
    iret
; A seventh DMA interrupt remains an explicit bounded guest failure.
irq_failure:
    mov word [proof+66],0ffffh
    cli
    hlt
    jmp $
code_end:
saved_flags dw 0
file_handle dw 0
allocation dw 0
window dw 0
prdt dd 0
lba dd 0
sector_count db 0
ata_command db 0
align 4,db 0
dds dd 20000h,0
    dw 0,0
    dd 0
proof db 'PIIXIDE1'
    times 128-8 db 0
    times 6*32 db 0
    db 'IDEEND01'
proof_end:
proof_file db 'C:\PIIXIDE.DAT',0
fpu_file db 'C:\IDE-FPU.DAT',0
scatter_file db 'C:\IDE-SCAT.DAT',0
read_file db 'C:\IDE-READ.DAT',0
raw_fpu db 'SNAPRAW1'
    times 24 db 0
extended dq 0fedcba9876543210h
    dw 4001h
fm_values dw 0120h,0123h,1040h,0043h,0f060h,0f063h,0780h,0783h,30c0h,98a0h,31b0h
done_text db 13,10,'SNAPSHOT-DONE',13,10,'PIIX-IDE-DONE',13,10,0
failed_text db 13,10,'PIIX-IDE-FAILED',13,10,0
scatter_copy times 1024 db 0
align 16,db 0
samples times 512 db 0c0h
    times 2048 db 0
stack_top:
    db 'IDEMETA1'
    dw proof,proof_end,code_end,program_end,ready_out,done_out,irq14,irq_commit,irq_iret,wait_return,raw_fpu,stack_top
    dw allocation,dds,prdt,scatter_copy,file_handle,window,drain_return,vds_lock,vds_unlock
program_end:
