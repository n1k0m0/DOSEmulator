; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original PC/AT floppy BIOS services, Apache-2.0.
; All transfers pass through FDC ports and DMA2; one bounce sector uses reserved EBDA.
; The polling transfer path also has a real IRQ6 completion handler.
; Windows-board POST unmasks IRQ6 for an installed drive; older POST variants
; retain their polling-path mask, which a V86 monitor may later change.
irq_floppy:
    push ax
    push ds
    xor ax,ax
    mov ds,ax
    or byte [0x43e],0x80
    mov al,0x20
    out 0x20,al
    pop ds
    pop ax
    iret

; Programs DMA2's address/page for the current EBDA bounce buffer; callers precheck its 64-KiB boundary separately.
fdc_dma_bounce:
    push eax
    call bios_ebda_segment
    movzx eax,ax
    shl eax,4
    add eax,0x200
    out 4,al
    shr eax,8
    out 4,al
    shr eax,8
    out 0x81,al
    pop eax
    ret

; CX=actual DMA byte count. A relocated EBDA need not be 64-KiB safe.
; Reject a wrapping bounce region before copying caller data or starting I/O.
; The 8237 page latch deliberately does not carry when its address wraps.
fdc_check_bounce:
    push eax
    call bios_ebda_segment
    movzx eax,ax
    shl eax,4
    add eax,0x200
    movzx eax,ax
    dec cx
    add ax,cx
    inc cx                 ; INC preserves the carry returned by ADD.
    pop eax
    ret

; Dispatches the implemented real FDC/DMA floppy services and their BIOS error responses.
int_floppy:
    enter_service
    and rFLAGS,0xfffe
    movzx bx,rDL
    cmp byte [cs:floppy_types+bx],0
    je .unsupported
    mov ah,rAH
    test ah,ah
    jz .reset
    cmp ah,1
    je .status
    cmp ah,2
    je .transfer
    cmp ah,3
    je .transfer
    cmp ah,4
    je .transfer
    cmp ah,5
    je .format
    cmp ah,8
    je .geometry
    cmp ah,0x15
    je .type
    cmp ah,0x16
    je .change
    cmp ah,0x17
    je .set_type
    cmp ah,0x18
    je .set_media
.unsupported:
    mov rAH,1
    jmp .error
.reset:
    call fdc_reset
    jc .timeout
    jmp .success
.status:
    mov al,[0x441]
    mov rAH,al
    test al,al
    jnz .error
    jmp .done
.geometry:
    mov ch,79
    cmp byte [cs:floppy_types+bx],1
    jne .geometry_cylinder
    mov ch,39
.geometry_cylinder:
    mov cl,[cs:floppy_spt+bx]
    mov rCX,cx
    mov al,[cs:floppy_types+bx]
    mov rBL,al
    mov al,[cs:floppy_count]
    mov rDL,al
    mov rDH,1
    mov rES,0xf000
    mov ax,floppy_parameters
    test bx,bx
    jz .geometry_pointer
    mov ax,floppy_parameters_b
.geometry_pointer:
    mov rDI,ax
    jmp .success
.type:
    mov rAH,2
    jmp .done
.change:
    mov dx,0x3f2
    in al,dx
    and al,0xfc
    or al,bl
    out dx,al
    mov dx,0x3f7
    in al,dx
    test al,0x80
    jz .success
    mov rAH,6
    jmp .error
.set_type:
    ; Only the configured native raw geometry is supported, not lower-density
    ; media in another drive. AH17 has no encoding for 1.44/2.88 MiB (use AH18).
    mov al,[cs:floppy_types+bx]
    cmp al,1
    je .type_360
    cmp al,2
    je .type_1200
    cmp al,3
    jne .unsupported
    mov al,4
    jmp .check_type
.type_360:
    mov al,1
    jmp .check_type
.type_1200:
    mov al,3
.check_type:
    cmp al,rAL
    jne .unsupported
    ; Capture the change line before recalibration clears it. IBM AH17 reports
    ; 06h once for changed media; repeating the request then succeeds.
    mov dx,0x3f2
    in al,dx
    and al,0xfc
    or al,bl
    out dx,al
    mov dx,0x3f7
    in al,dx
    push ax
    xor al,al
    call fdc_format_seek
    pop cx
    jc .transfer_error
    test cl,0x80
    jz .success
    mov rAH,6
    jmp .error
.set_media:
    mov al,79
    cmp byte [cs:floppy_types+bx],1
    jne .check_media
    mov al,39
.check_media:
    cmp al,rCH
    jne .media_type_error
    mov al,[cs:floppy_spt+bx]
    cmp al,rCL
    jne .media_type_error
    xor al,al
    call fdc_format_seek
    jc .transfer_error
    mov rES,0xf000
    mov ax,floppy_parameters
    test bx,bx
    jz .media_pointer
    mov ax,floppy_parameters_b
.media_pointer:
    mov rDI,ax
    jmp .success
.media_type_error:
    mov rAH,0x0c
    jmp .error
.format:
    mov al,80
    cmp byte [cs:floppy_types+bx],1
    jne .format_cylinder
    mov al,40
.format_cylinder:
    cmp rCH,al
    jae .unsupported
    cmp rDH,2
    jae .unsupported
    ; CL is not used for format. The active INT1E table supplies N/SC/GPL/D.
    mov cx,rCX
    mov dx,rDX
    mov es,rES
    mov di,rBX
    call floppy_format
    jc .transfer_error
    jmp .success
.transfer:
    movzx ecx,byte [cs:floppy_spt+bx]
    movzx eax,rCH
    cmp eax,80
    jae .unsupported
    cmp rDH,2
    jae .unsupported
    test rCL,0xc0
    jnz .unsupported
    cmp rCL,1
    jb .unsupported
    cmp rCL,cl
    ja .unsupported
    shl eax,1
    movzx edx,rDH
    add eax,edx
    imul eax,ecx
    movzx edx,rCL
    dec edx
    add eax,edx
    movzx cx,rAL
    test cx,cx
    jz .unsupported
    cmp cx,128
    ja .unsupported
    movzx edx,byte [cs:floppy_spt+bx]
    imul edx,edx,160
    cmp byte [cs:floppy_types+bx],1
    jne .capacity
    shr edx,1
.capacity:
    movzx edi,cx
    add edi,eax
    cmp edi,edx
    ja .unsupported
    mov es,rES
    mov di,rBX
    mov dl,rDL
    mov bl,0x46
    cmp rAH,3
    jne .not_write
    mov bl,0x45
.not_write:
    cmp rAH,4
    jne .not_verify
    mov bl,0x44
.not_verify:
    call floppy_sectors
    jc .transfer_error
    jmp .success
.timeout:
    mov al,0x80
.transfer_error:
    mov rAH,al
.error:
    or rFLAGS,1
    mov al,rAH
    mov [0x441],al
    jmp .done
.success:
    mov rAH,0
    mov byte [0x441],0
.done:
    leave_service

; Poll actual FDC status. Only a slow path samples RTC seconds, preserving
; the caller's CMOS index/NMI bit and AH. Two second transitions bound an
; ordinary stuck wait to 1..2 seconds plus sampling jitter; a frozen/unmapped
; RTC retains the old four-million-poll hard limit. No STI/HLT/PIT reprogramming.
%macro fdc_poll_status 2
    push ebx
    push edi
    mov ebx,0x02ff             ; BH=second transitions left, BL=uninitialized
    mov ecx,4000000
    mov dx,0x3f4
%%wait:
    in al,dx
    and al,%1
    cmp al,%2
    je %%ready
    dec ecx
    jz %%timeout
    test ecx,255
    jnz %%wait
    push ax
    mov dx,0x70
    in al,dx
    movzx edi,al
    and al,0x80                ; select seconds without changing NMI masking
    out dx,al
    inc dx
    in al,dx
    cmp bl,al
    je %%clock_done
    cmp bl,0xff
    je %%clock_first
    dec bh
%%clock_first:
    mov bl,al
%%clock_done:
    dec dx
    mov ax,di
    out dx,al
    mov dx,0x3f4
    pop ax
    test bh,bh
    jnz %%wait
%%timeout:
    stc
    jmp %%done
%%ready:
    clc
%%done:
    pop edi
    pop ebx
%endmacro

; Bounded FIFO polling preserves all registers except AL on receive.
fdc_send:
    push ax
    push ecx
    push dx
    fdc_poll_status 0xc0,0x80
    jnc .ready
    pop dx
    pop ecx
    pop ax
    stc
    ret
.ready:
    pop dx
    pop ecx
    pop ax
    push dx
    mov dx,0x3f5
    out dx,al
    pop dx
    clc
    ret
; Waits for an FDC result byte with a bounded guest timeout.
fdc_receive:
    push ecx
    push dx
    fdc_poll_status 0xc0,0xc0
    jnc .ready
    pop dx
    pop ecx
    stc
    ret
.ready:
    inc dx
    in al,dx
    pop dx
    pop ecx
    clc
    ret
; Polls seek completion through actual FDC sense-interrupt commands.
fdc_wait_seek:
    push ecx
    push dx
    fdc_poll_status 15,0
    jnc .ready
    pop dx
    pop ecx
    stc
    ret
.ready:
    pop dx
    pop ecx
    clc
    ret
; Resets the real controller and consumes the required sense-interrupt results.
fdc_reset:
    push ax
    push cx
    push dx
    mov dx,0x3f2
    xor al,al
    out dx,al
    mov al,0x0c
    out dx,al
    mov cx,4
.sense:
    mov al,8
    call fdc_send
    jc .bad
    call fdc_receive
    jc .bad
    call fdc_receive
    jc .bad
    loop .sense
    mov al,3
    call fdc_send
    jc .bad
    mov al,0xdf
    call fdc_send
    jc .bad
    mov al,2
    call fdc_send
    jc .bad
    clc
    jmp .done
.bad:
    stc
.done:
    pop dx
    pop cx
    pop ax
    ret

; EAX=LBA, CX=count, DL=drive, BL=46h read /45h write /44h verify, ES:DI=buffer.
floppy_sectors:
    push bp
    mov bp,sp
    push ebx
    push ecx
    push edx
    push esi
    push edi
    push ds
    push es
    mov esi,eax
    mov cx,512
    call fdc_check_bounce
    jc .dma_boundary
    sti
.sector:
    movzx bx,byte [ss:bp-12]
    mov dx,0x3f7
    mov al,[cs:floppy_rates+bx]
    out dx,al
    mov cl,bl
    mov al,0x10
    shl al,cl
    or al,bl
    or al,0x0c
    mov dx,0x3f2
    out dx,al
    ; Position the head and acknowledge the seek through SENSE INTERRUPT.
    mov eax,esi
    movzx ecx,byte [cs:floppy_spt+bx]
    shl ecx,1
    xor edx,edx
    div ecx
    push ax
    mov al,0x0f
    call fdc_send
    jc .seek_failed
    mov al,bl
    call fdc_send
    jc .seek_failed
    pop ax
    call fdc_send
    jc .timeout
    call fdc_wait_seek
    jc .timeout
    mov al,8
    call fdc_send
    jc .timeout
    call fdc_receive
    jc .timeout
    call fdc_receive
    jc .timeout
    cmp byte [ss:bp-4],0x45
    jne .prepare_dma
    call .copy_buffer
.prepare_dma:
    mov al,6
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov al,0x46
    cmp byte [ss:bp-4],0x45
    jne .dma_direction
    mov al,0x4a
.dma_direction:
    out 0x0b,al
    call fdc_dma_bounce
    mov al,0xff
    out 5,al
    mov al,1
    out 5,al
    mov al,2
    out 0x0a,al
    ; Convert LBA to one CHS sector; DMA terminal count ends each command.
    mov eax,esi
    movzx ecx,byte [cs:floppy_spt+bx]
    xor edx,edx
    div ecx
    inc dl
    mov bh,dl
    mov ecx,2
    xor edx,edx
    div ecx
    mov ch,al
    mov cl,dl
    shl cl,2
    or cl,bl
    mov al,[ss:bp-4]
    cmp al,0x44
    jne .command
    mov al,0x46
.command:
    call fdc_send
    jc .timeout
    mov al,cl
    call fdc_send
    jc .timeout
    mov al,ch
    call fdc_send
    jc .timeout
    mov al,cl
    shr al,2
    call fdc_send
    jc .timeout
    mov al,bh
    call fdc_send
    jc .timeout
    mov al,2
    call fdc_send
    jc .timeout
    mov al,bh
    call fdc_send
    jc .timeout
    mov al,0x1b
    call fdc_send
    jc .timeout
    mov al,0xff
    call fdc_send
    jc .timeout
    call fdc_receive
    jc .timeout
    mov bh,al
    call fdc_receive
    jc .timeout
    mov bl,al
    mov cx,5
.result:
    call fdc_receive
    jc .timeout
    loop .result
    test bh,0xc0
    jnz .device_error
    cmp byte [ss:bp-4],0x46
    jne .next
    call .copy_buffer
.next:
    inc esi
    dec word [ss:bp-8]
    jnz .sector
    clc
    jmp .done
.seek_failed:
    pop ax                      ; discard the saved cylinder before aborting
.timeout:
    mov al,0x80
    stc
    jmp .done
.dma_boundary:
    mov al,9
    stc
    jmp .done
.device_error:
    mov al,4
    test bl,2
    jz .not_protected
    mov al,3
.not_protected:
    stc
.done:
    pop es
    pop ds
    pop edi
    pop esi
    pop edx
    pop ecx
    pop ebx
    pop bp
    ret
.copy_buffer:
    push ds
    push esi
    call bios_ebda_segment
    mov ds,ax
    mov si,0x200
    mov cx,512
.byte:
    cmp byte [ss:bp-4],0x45
    je .copy_write
    mov al,[si]
    mov [es:di],al
    jmp .copy_advance
.copy_write:
    mov al,[es:di]
    mov [si],al
.copy_advance:
    inc si
    inc di
    jnz .copy_next
    mov ax,es
    add ax,0x1000
    mov es,ax
.copy_next:
    loop .byte
    pop esi
    pop ds
    ret

; BL=drive, AL=cylinder. Select native rate/motor, seek (recalibrate at zero),
; drain SENSE INTERRUPT and check ready through SENSE DRIVE STATUS.
; Returns CF/AL=80h timeout/not ready or 40h seek failure. Preserves BX/CX/DX.
fdc_format_seek:
    push cx
    push dx
    mov ch,al
    mov dx,0x3f7
    mov al,[cs:floppy_rates+bx]
    out dx,al
    mov cl,bl
    mov al,0x10
    shl al,cl
    or al,bl
    or al,0x0c
    mov dx,0x3f2
    out dx,al
    mov al,0x0f
    test ch,ch
    jnz .seek_command
    mov al,7
.seek_command:
    call fdc_send
    jc .timeout
    mov al,bl
    call fdc_send
    jc .timeout
    test ch,ch
    jz .wait
    mov al,ch
    call fdc_send
    jc .timeout
.wait:
    call fdc_wait_seek
    jc .timeout
    mov al,8
    call fdc_send
    jc .timeout
    call fdc_receive
    jc .timeout
    mov cl,al
    call fdc_receive
    jc .timeout
    test cl,0xc0
    jnz .seek_error
    cmp al,ch
    jne .seek_error
    mov al,4
    call fdc_send
    jc .timeout
    mov al,bl
    call fdc_send
    jc .timeout
    call fdc_receive
    jc .timeout
    test al,0x20
    jz .timeout
    clc
    jmp .done
.seek_error:
    mov al,0x40
    stc
    jmp .done
.timeout:
    mov al,0x80
    stc
.done:
    pop dx
    pop cx
    ret

; CH=cylinder, DH=head, DL=drive, ES:DI=CHRN descriptors.
; Copy the active INT1E descriptor list into the reserved EBDA bounce sector,
; so an unaligned/cross-64K caller buffer never wraps the DMA address.
; SC comes from INT1E, as in later AT-compatible BIOSes; AL is preserved.
floppy_format:
    push bp
    mov bp,sp
    push bx
    push cx
    push dx
    push si
    push di
    push ds
    push es
    sub sp,4               ; -16 count, -18 GPL, -17 fill
    mov si,[0x78]
    mov ax,[0x7a]
    mov ds,ax
    cmp byte [si+3],2
    jne .invalid
    xor ax,ax
    mov al,[si+4]
    test al,al
    jz .invalid
    cmp al,[cs:floppy_spt+bx]
    ja .invalid
    mov [ss:bp-16],ax
    mov ax,[si+7]
    mov [ss:bp-18],ax
    mov cx,[ss:bp-16]
    shl cx,2
    call fdc_check_bounce
    jc .dma_boundary
    call bios_ebda_segment
    mov ds,ax
    mov si,0x200
    mov cx,[ss:bp-16]
    shl cx,2
.copy:
    mov al,[es:di]
    mov [si],al
    inc si
    inc di
    jnz .copy_next
    mov ax,es
    add ax,0x1000
    mov es,ax
.copy_next:
    loop .copy
    mov cx,[ss:bp-4]
    mov al,ch
    sti
    call fdc_format_seek
    jc .done
    mov al,6
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov al,0x4a            ; DMA2 memory -> device, single, no auto-init
    out 0x0b,al
    call fdc_dma_bounce
    mov ax,[ss:bp-16]
    shl ax,2
    dec ax
    out 5,al
    mov al,ah
    out 5,al
    mov al,2
    out 0x0a,al
    mov al,0x4d            ; MFM FORMAT TRACK
    call fdc_send
    jc .timeout
    mov ax,[ss:bp-6]
    shl ah,2
    or al,ah
    call fdc_send
    jc .timeout
    mov al,2
    call fdc_send
    jc .timeout
    mov al,[ss:bp-16]
    call fdc_send
    jc .timeout
    mov al,[ss:bp-18]
    call fdc_send
    jc .timeout
    mov al,[ss:bp-17]
    call fdc_send
    jc .timeout
    call fdc_receive
    jc .timeout
    mov bh,al
    call fdc_receive
    jc .timeout
    mov bl,al
    mov cx,5
.result:
    call fdc_receive
    jc .timeout
    loop .result
    test bh,0xc0
    jnz .device_error
    clc
    jmp .done
.invalid:
    mov al,1
    stc
    jmp .done
.dma_boundary:
    mov al,9
    stc
    jmp .done
.timeout:
    mov al,0x80
    stc
    jmp .done
.device_error:
    mov al,4
    test bh,8
    jz .not_ready_checked
    mov al,0x80
    jmp .error
.not_ready_checked:
    test bl,2
    jz .not_protected
    mov al,3
    jmp .error
.not_protected:
    test bl,0x10
    jz .error
    mov al,8
.error:
    stc
.done:
    pushf
    push ax
    mov al,6
    out 0x0a,al
    pop ax
    popf
    lea sp,[bp-14]         ; discard locals without changing returned CF
    pop es
    pop ds
    pop di
    pop si
    pop dx
    pop cx
    pop bx
    pop bp
    ret
