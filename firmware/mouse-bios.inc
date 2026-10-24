; Original PS/2 ID-00 firmware, IBM BIOS Interface May 1988, INT15 C2.
; EBDA 10h..1Bh; the storage bounce sector at 200h..3FFh is disjoint.
%define mouse_callback 0x10
%define mouse_enabled 0x14
%define mouse_index 0x15
%define mouse_packet 0x16
%define mouse_saved_command 0x19

int_ps2_service:
    cmp byte [cs:mouse_present],0
    jne .present
    mov rAH,0x86
    jmp .error
.present:
    mov ax,0x9fc0
    mov es,ax
    mov al,rAL
    cmp al,7
    ja .invalid_function
    call mouse_begin
    cmp rAL,0
    je .enable
    cmp rAL,1
    je .reset
    cmp rAL,2
    je .rate
    cmp rAL,3
    je .resolution
    cmp rAL,4
    je .identify
    cmp rAL,5
    je .initialize
    cmp rAL,6
    je .extended
    ; AL=07 installs/removes the caller's far routine.
    mov ax,rBX
    mov [es:mouse_callback],ax
    mov dx,rES
    mov [es:mouse_callback+2],dx
    or ax,dx
    jnz .success
    jmp .disable
.enable:
    cmp rBH,1
    ja .invalid_input
    je .enable_on
.disable:
    mov al,0xf5
    call mouse_send
    jc .interface_error
    mov byte [es:mouse_enabled],0
    mov byte [es:mouse_index],0
    in al,0xa1
    or al,0x10
    out 0xa1,al
    jmp .success
.enable_on:
    cmp dword [es:mouse_callback],0
    je .no_handler
    mov al,0xf4
    call mouse_send
    jc .interface_error
    mov byte [es:mouse_enabled],1
    mov byte [es:mouse_index],0
    in al,0x21
    and al,0xfb
    out 0x21,al
    in al,0xa1
    and al,0xef
    out 0xa1,al
    jmp .success
.initialize:
    cmp rBH,3
    jne .invalid_input
    mov al,0xf6
    call mouse_send
    jc .interface_error
    jmp .reset_state
.reset:
    mov al,0xff
    call mouse_send
    jc .interface_error
    call mouse_read
    jc .interface_error
    cmp al,0xaa
    jne .interface_error
    mov rBL,al
    call mouse_read
    jc .interface_error
    mov rBH,al
.reset_state:
    mov byte [es:mouse_enabled],0
    mov byte [es:mouse_index],0
    in al,0xa1
    or al,0x10
    out 0xa1,al
    jmp .success
.rate:
    movzx bx,rBH
    cmp bl,6
    ja .invalid_input
    mov al,0xf3
    call mouse_send
    jc .interface_error
    mov al,[cs:mouse_rates+bx]
    call mouse_send
    jc .interface_error
    jmp .success
.resolution:
    cmp rBH,3
    ja .invalid_input
    mov al,0xe8
    call mouse_send
    jc .interface_error
    mov al,rBH
    call mouse_send
    jc .interface_error
    jmp .success
.identify:
    mov al,0xf2
    call mouse_send
    jc .interface_error
    call mouse_read
    jc .interface_error
    mov rBH,al
    jmp .success
.extended:
    cmp rBH,0
    je .status
    cmp rBH,2
    ja .invalid_input
    mov al,0xe6
    cmp rBH,1
    je .scaling
    inc al
.scaling:
    call mouse_send
    jc .interface_error
    jmp .success
.status:
    mov al,0xe9
    call mouse_send
    jc .interface_error
    call mouse_read
    jc .interface_error
    mov rBL,al
    call mouse_read
    jc .interface_error
    mov rCL,al
    call mouse_read
    jc .interface_error
    mov rDL,al
.success:
    mov rAH,0
    jmp .finish
.invalid_input:
    mov rAH,2
    jmp .finish_error
.no_handler:
    mov rAH,5
    jmp .finish_error
.interface_error:
    mov rAH,3
.finish_error:
    or rFLAGS,1
.finish:
    ; Restore keyboard policy; Aux interrupt/clock follow BIOS enable state.
    mov al,[es:mouse_saved_command]
    and al,0xdd
    cmp byte [es:mouse_enabled],0
    je .clock_off
    or al,2
    jmp .command
.clock_off:
    or al,0x20
.command:
    call mouse_write_command
    leave_service
.invalid_function:
    mov rAH,1
.error:
    or rFLAGS,1
    leave_service

mouse_begin:
    ; Inhibit incoming stream packets while saving/draining the output buffer.
    mov al,0xa7
    out 0x64,al
.drain:
    in al,0x64
    test al,1
    jz .empty
    test al,0x20
    jz .keyboard
    in al,0x60
    jmp .drain
.keyboard:
    pushad
    call keyboard_read
    popad
    jmp .drain
.empty:
    mov al,0x20
    out 0x64,al
    in al,0x60
    ; A7 only changed bit 5; it is restored according to enabled state.
    mov [es:mouse_saved_command],al
    or al,0x10
    and al,0xdc
    call mouse_write_command
    mov byte [es:mouse_index],0
    ret
mouse_write_command:
    push ax
    mov al,0x60
    out 0x64,al
    pop ax
    out 0x60,al
    ret
mouse_send:
    push ax
    mov al,0xd4
    out 0x64,al
    pop ax
    out 0x60,al
    call mouse_read
    jc .done
    cmp al,0xfa
    je .done
    stc
.done:
    ret
mouse_read:
    push cx
    mov cx,0xffff
.wait:
    in al,0x64
    test al,1
    jz .retry
    test al,0x20
    jnz .ready
    pushad
    call keyboard_read
    popad
.retry:
    loop .wait
    stc
    jmp .done
.ready:
    in al,0x60
    clc
.done:
    pop cx
    ret

irq_mouse:
    pushad
    push ds
    push es
    in al,0x64
    and al,0x21
    cmp al,0x21
    jne .done
    in al,0x60
    mov dx,0x9fc0
    mov ds,dx
    movzx bx,byte [mouse_index]
    test bl,bl
    jnz .byte
    test al,8
    jz .done
.byte:
    mov [mouse_packet+bx],al
    inc bl
    cmp bl,3
    jb .partial
    mov byte [mouse_index],0
    cmp byte [mouse_enabled],0
    je .done
    cmp dword [mouse_callback],0
    je .done
    movzx ax,byte [mouse_packet]
    push ax
    movzx ax,byte [mouse_packet+1]
    push ax
    movzx ax,byte [mouse_packet+2]
    push ax
    push word 0
    cld
    call far [mouse_callback]
    cli
    add sp,8
    jmp .done
.partial:
    mov [mouse_index],bl
.done:
    mov al,0x20
    out 0xa0,al
    out 0x20,al
    pop es
    pop ds
    popad
    iret
mouse_rates: db 10,20,40,60,80,100,200
