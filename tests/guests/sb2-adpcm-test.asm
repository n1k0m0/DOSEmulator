; Own Creative ADPCM integration probe. Loaded by real DOS, DMA1 / IRQ5.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    in al,0x21
    mov [saved_pic],al
    call reset_dsp
    mov al,0xe1
    call dsp_write
    call dsp_read
    cmp al,2
    jne failed
    call dsp_read
    cmp al,1
    jne failed
    xor eax,eax
    mov ax,cs
    shl eax,4
    mov ebx,eax
    add eax,buffer
    add eax,255
    and eax,0xffffff00
    mov [physical],eax
    sub eax,ebx
    mov [buffer_offset],ax
    mov di,ax
    mov byte [di-1],0x5a
    mov byte [di+128],0xa5
    mov cx,128
    mov al,19
.fill:
    stosb
    add al,73
    loop .fill
    mov di,[buffer_offset]
    mov byte [di],128
    mov ax,0x350d
    int 0x21
    mov [old_irq],bx
    mov [old_irq+2],es
    mov dx,irq5
    mov ax,0x250d
    int 0x21
    mov byte [installed],1
    in al,0x21
    and al,0xdf
    out 0x21,al
    sti
    mov al,0x40
    call dsp_write
    mov al,131
    call dsp_write
    mov al,0xd1
    call dsp_write
    mov byte [format],0
.format:
    xor bx,bx
    mov bl,[format]
    mov al,[single_commands+bx]
    mov [single_command],al
    mov al,[auto_commands+bx]
    mov [auto_command],al
    mov byte [action],0
    mov word [irq_count],0
    mov bx,64
    mov al,0x49
    call program_dma
    mov al,[single_command]
    call single_64
    mov cx,8000
.before_pause:
    loop .before_pause
    mov al,0xd0
    call dsp_write
    mov cx,1
    call wait_ticks
    cmp word [irq_count],0
    jne failed
    mov al,0xd4
    call dsp_write
    mov bx,1
    call wait_irqs
    call check_tc
    mov word [irq_count],0
    mov bx,64
    mov al,0x49
    call program_dma
    mov al,[single_command]
    dec al
    call single_64
    mov bx,1
    call wait_irqs
    call check_tc

    mov byte [action],1
    mov word [irq_count],0
    mov bx,128
    mov al,0x59
    call program_dma
    mov al,0x48
    call dsp_write
    mov al,63
    call dsp_write
    xor al,al
    call dsp_write
    mov al,[auto_command]
    call dsp_write
    mov bx,3
    call wait_irqs
    mov cx,1
    call wait_ticks
    cmp word [irq_count],3
    jne failed

    ; Queue the non-reference single-cycle form from IRQ1 of another auto stream.
    mov byte [action],2
    mov word [irq_count],0
    mov bx,128
    mov al,0x59
    call program_dma
    mov al,0x48
    call dsp_write
    mov al,31
    call dsp_write
    xor al,al
    call dsp_write
    mov al,[auto_command]
    call dsp_write
    mov bx,3
    call wait_irqs
    mov cx,1
    call wait_ticks
    cmp word [irq_count],3
    jne failed
    cmp byte [io_error],0
    jne failed
    inc byte [format]
    cmp byte [format],3
    jb .format
    mov al,0xd3
    call dsp_write
    mov si,[buffer_offset]
    cmp byte [si-1],0x5a
    jne failed
    cmp byte [si+128],0xa5
    jne failed
    cmp byte [si],128
    jne failed
    inc si
    mov cx,127
    mov al,92
.verify:
    cmp [si],al
    jne failed
    inc si
    add al,73
    loop .verify
    call cleanup
    mov dx,success
    call print
    mov ax,0x4c00
    int 0x21
failed:
    call cleanup
    mov dx,failure
    call print
    mov ax,0x4c01
    int 0x21
single_64:
    call dsp_write
    mov al,63
    call dsp_write
    xor al,al
    call dsp_write
    ret
check_tc:
    in al,8
    test al,2
    jz failed
    ret
program_dma:
    pushad
    mov ah,al
    mov al,5
    out 0x0a,al
    xor al,al
    out 0x0c,al
    mov eax,[physical]
    out 2,al
    shr eax,8
    out 2,al
    shr eax,8
    out 0x83,al
    mov ax,bx
    dec ax
    out 3,al
    mov al,ah
    out 3,al
    popad
    out 0x0b,al
    mov al,1
    out 0x0a,al
    ret
irq5:
    pushad
    push ds
    push cs
    pop ds
    mov dx,0x22e
    in al,dx
    inc word [irq_count]
    cmp byte [action],1
    jne .queue
    cmp word [irq_count],2
    jne .eoi
    mov al,0xda
    call dsp_write
    jmp .eoi
.queue:
    cmp byte [action],2
    jne .eoi
    cmp word [irq_count],1
    jne .eoi
    mov al,[single_command]
    dec al
    call dsp_write
    mov al,15
    call dsp_write
    xor al,al
    call dsp_write
.eoi:
    mov al,0x20
    out 0x20,al
    pop ds
    popad
    iret
reset_dsp:
    mov dx,0x226
    mov al,1
    out dx,al
    mov cx,128
.wait:
    loop .wait
    xor al,al
    out dx,al
    call dsp_read
    cmp al,0xaa
    je .ok
    mov byte [io_error],1
.ok:
    ret
cleanup:
    mov al,5
    out 0x0a,al
    call reset_dsp
    mov al,[saved_pic]
    out 0x21,al
    cmp byte [installed],0
    je .done
    push ds
    mov dx,[old_irq]
    mov ax,[old_irq+2]
    mov ds,ax
    mov ax,0x250d
    int 0x21
    pop ds
    mov byte [installed],0
.done:
    ret
dsp_write:
    push ax
    push cx
    push dx
    mov ah,al
    mov dx,0x22c
    mov cx,65535
.wait:
    in al,dx
    test al,128
    jz .ready
    loop .wait
    mov byte [io_error],1
    jmp .done
.ready:
    mov al,ah
    out dx,al
.done:
    pop dx
    pop cx
    pop ax
    ret
dsp_read:
    push cx
    push dx
    mov dx,0x22e
    mov cx,65535
.wait:
    in al,dx
    test al,128
    jnz .ready
    loop .wait
    mov byte [io_error],1
    mov al,255
    jmp .done
.ready:
    mov dx,0x22a
    in al,dx
.done:
    pop dx
    pop cx
    ret
wait_irqs:
    xor ah,ah
    int 0x1a
    mov si,dx
    add si,50
.wait:
    cmp word [irq_count],bx
    jae .done
    xor ah,ah
    int 0x1a
    cmp dx,si
    jae failed
    jmp .wait
.done:
    ret
wait_ticks:
    pushad
    mov bx,cx
    xor ah,ah
    int 0x1a
    mov si,dx
    add si,bx
.wait:
    xor ah,ah
    int 0x1a
    cmp dx,si
    jb .wait
    popad
    ret
print:
    mov ah,9
    int 0x21
    ret
single_commands db 0x75,0x77,0x17
auto_commands db 0x7d,0x7f,0x1f
success db 'ADPCM-4BIT-3BIT-2BIT-OK',13,10,'ADPCM-PAUSE-CONTINUE-AUTO-QUEUE-OK',13,10,'ADPCM-DOS-RETURN-OK',13,10,'$'
failure db 'ADPCM-FAILED',13,10,'$'
saved_pic db 0
installed db 0
io_error db 0
format db 0
action db 0
single_command db 0
auto_command db 0
irq_count dw 0
old_irq dd 0
buffer_offset dw 0
result_marker db 'SB2-ADPCM-RESULTS-V1'
physical dd 0
buffer times 512 db 0
