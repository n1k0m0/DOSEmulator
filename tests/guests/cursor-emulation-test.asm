; Own real-DOS cursor-policy/shape probe, Apache-2.0. No OS/font bytes.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    xor ax,ax
    mov es,ax
    mov al,[es:0x487]
    mov [old_flags],al
    mov ax,[es:0x460]
    mov [old_shape],ax
    mov ax,[es:0x485]
    mov [old_points],ax
    call read_crtc
    mov [old_crtc],cx
    movzx cx,byte [0x80]
    mov si,0x81
.argument:
    jcxz .mode
    lodsb
    cmp al,'8'
    jne .fourteen
    mov word [points],8
.fourteen:
    cmp al,'1'
    jne .off
    cmp byte [si],'4'
    jne .sixteen
    mov word [points],14
.sixteen:
    cmp byte [si],'6'
    jne .off
    mov word [points],16
.off:
    cmp al,'o'
    jne .automatic
    cmp byte [si],'f'
    jne .automatic
    mov byte [enabled],0
.automatic:
    cmp al,'5'
    jne .auto_flag
    mov word [wait_ticks],5
.auto_flag:
    and al,0xdf
    cmp al,'A'
    jne .next
    mov byte [automatic],1
.next:
    loop .argument
.mode:
    mov ax,3
    int 0x10
    xor bx,bx
    mov ax,0x1114
    cmp word [points],16
    je .font
    mov ax,0x1111
    cmp word [points],14
    je .font
    mov ax,0x1112
.font:
    int 0x10
    ; First use native coordinates, then switch only the policy. Hardware
    ; must stay unchanged until the next AH01 request.
    mov bx,0x34
    mov ax,0x1201
    int 0x10
    cmp al,0x12
    jne failed
    mov ax,0x0100
    mov cx,0x0607
    int 0x10
    call read_crtc
    cmp cx,0x0607
    jne failed
    mov ax,0x1201
    sub al,[enabled]
    mov bx,0x34
    int 0x10
    cmp al,0x12
    jne failed
    call read_crtc
    cmp cx,0x0607
    jne failed
    ; Invalid selector is not reported as success and changes no INFO bits.
    xor ax,ax
    mov es,ax
    mov dl,[es:0x487]
    mov ax,0x1202
    int 0x10
    cmp ax,0x1202
    jne failed
    cmp dl,[es:0x487]
    jne failed
    mov ax,0x0100
    mov cx,0x2000
    int 0x10
    mov ax,0xb800
    mov es,ax
    xor di,di
    mov ax,400
    xor dx,dx
    div word [points]
    imul cx,ax,80
    mov ax,0x1720
    rep stosw
    mov di,164
    mov si,title
    call text
    mov di,3*160+4
    mov si,request_text
    call text
    mov di,4*160+4
    mov si,on_text
    cmp byte [enabled],1
    je .policy_text
    mov si,off_text
.policy_text:
    call text
    mov di,6*160+4
    mov si,explanation
    call text
    mov di,9*160+4
    mov si,target_text
    call text
    mov di,10*160+8
    mov ax,0x2c20
    mov cx,16
    rep stosw
    mov di,12*160+4
    mov si,shape_text
    call text
    mov di,14*160+4
    mov si,query_text
    call text
    mov di,16*160+4
    mov si,footer
    call text
    mov dx,0x0a06
    xor bx,bx
    mov ah,2
    int 0x10
    mov ax,0x0100
    mov cx,0x0607
    int 0x10
    call read_crtc
    mov [actual_shape],cx
    mov ax,0x0607
    cmp byte [enabled],0
    je .expected
    cmp word [points],8
    je .expected
    mov ax,0x0a0d
    cmp word [points],14
    je .expected
    mov ax,0x0c0f
.expected:
    cmp cx,ax
    jne failed
    mov ah,3
    xor bx,bx
    int 0x10
    cmp cx,0x0607
    jne failed
    cmp dx,0x0a06
    jne failed
    xor ax,ax
    mov es,ax
    cmp word [es:0x460],0x0607
    jne failed
    mov al,[es:0x487]
    and al,1
    xor al,1
    cmp al,[enabled]
    jne failed
    mov ax,[es:0x485]
    cmp ax,[points]
    jne failed
    push cs
    pop es
    mov di,state_table
    mov ax,0x1b00
    int 0x10
    cmp al,0x1b
    jne failed
    mov al,[state_table+0x2d]
    shr al,4
    and al,1
    cmp al,[enabled]
    jne failed
    push ds
    lds si,[state_table]
    test byte [si+10],16
    pop ds
    jz failed
    mov si,checked
    call debug
    ; Give the actual IRQ0/timer clock a few ticks before capture. This
    ; exercises stable cursor scanout beyond the shape-write instruction.
    xor ah,ah
    int 0x1a
    mov [settle_start],dx
.settle:
    xor ah,ah
    int 0x1a
    sub dx,[settle_start]
    cmp dx,[wait_ticks]
    jb .settle
    mov si,ready
    call debug
    cmp byte [automatic],1
    je .return
    xor ax,ax
    int 0x16
.return:
    mov ax,3
    int 0x10
    xor bx,bx
    mov ax,0x1114
    cmp word [old_points],16
    je .restore_font
    mov ax,0x1111
    cmp word [old_points],14
    je .restore_font
    mov ax,0x1112
.restore_font:
    int 0x10
    mov ax,0x1200
    mov al,[old_flags]
    and al,1
    mov bx,0x34
    int 0x10
    mov cx,[old_shape]
    mov ax,0x0100
    int 0x10
    ; Restore the exact old hardware shape as well as the requested BDA
    ; values: a mode-set default need not be a prior AH01 emulated request.
    mov cx,[old_crtc]
    call write_crtc
    call read_crtc
    cmp cx,[old_crtc]
    jne failed
    xor ax,ax
    mov es,ax
    mov ax,[es:0x460]
    cmp ax,[old_shape]
    jne failed
    mov al,[es:0x487]
    and al,1
    mov ah,[old_flags]
    and ah,1
    cmp al,ah
    jne failed
    mov si,restored
    call debug
    mov dx,returned
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
read_crtc:
    mov dx,0x3d4
    in al,dx
    push ax
    mov al,10
    out dx,al
    inc dx
    in al,dx
    mov ch,al
    dec dx
    mov al,11
    out dx,al
    inc dx
    in al,dx
    mov cl,al
    dec dx
    pop ax
    out dx,al
    ret
write_crtc:
    mov dx,0x3d4
    in al,dx
    push ax
    mov al,10
    mov ah,ch
    out dx,ax
    mov al,11
    mov ah,cl
    out dx,ax
    pop ax
    out dx,al
    ret
text:
    lodsb
    test al,al
    jz .done
    mov ah,0x1f
    stosw
    jmp text
.done:
    ret
debug:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug
.done:
    ret
failed:
    mov si,error
    call debug
    mov ax,0x4c01
    int 0x21

old_flags: db 0
old_shape: dw 0
old_crtc: dw 0
old_points: dw 16
automatic: db 0
settle_start: dw 0
wait_ticks: dw 9
capture_header: db 'CURSOR-STATE-V1'
points: dw 16
enabled: db 1
actual_shape: dw 0
state_table: times 64 db 0
title: db 'BIOS CURSOR / AH12 BL34',0
request_text: db 'Same legacy request: CH=06 / CL=07',0
on_text: db 'EMULATION ENABLED',0
off_text: db 'EMULATION DISABLED',0
explanation: db 'AH03 retains the requested shape; CRTC receives native rows.',0
target_text: db 'Cursor in the colored blank cell below:',0
shape_text: db 'Green: cell background / red: real hardware cursor',0
query_text: db 'INFO bit, AH03 and AH1B state/capability verified.',0
footer: db 'CURSOR-READY / ENTER RETURNS TO DOS',0
checked: db 13,10,'CURSOR-POLICY-AND-SHAPE-OK',13,10,0
ready: db 'CURSOR-READY',13,10,0
restored: db 'CURSOR-RESTORE-OK',13,10,0
returned: db 'CURSOR-DOS-RETURN-OK',13,10,'$'
error: db 'CURSOR-FAILED',13,10,0
