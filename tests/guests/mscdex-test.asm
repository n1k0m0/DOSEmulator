; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original DOS/MSCDEX filesystem client for the own immutable ISO, with real driver requests.
bits 16
org 100h
; Enter the ordinary DOS COM client and retain a complete private stack and MCB.
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    cli
    mov ax,cs
    mov ss,ax
    mov sp,stack_top
    sti
    mov bx,(program_end-$$+100h+15)/16
    mov ah,4ah
    int 21h
    jc failed
    mov [proof+52],bx
    mov [proof+54],cs
    smsw ax
    and ax,1
    mov [proof+8],ax
    mov di,buffer_guard
    mov cx,16
    mov al,0d6h
    rep stosb
    mov di,buffer_end
    mov cx,16
    rep stosb
    mov ax,cs
    mov [request+16],ax
    mov dx,dta
    mov ah,1ah
    int 21h
    mov word [proof+10],1
    xor bx,bx
    mov ax,1500h
install_call:
    int 2fh
install_return:
    mov [proof+14],bx
    mov [proof+16],cx
    test bx,bx
    jz failed
    mov cx,17
    xor bx,bx
    mov ax,150bh
installed_call:
    int 2fh
installed_return:
    mov [proof+18],ax
    mov [proof+20],bx
    test ax,ax
    jz failed
    cmp bx,0adadh
    jne failed
    cmp word [proof+14],1
    jne failed
    mov bx,driver_list
    mov ax,1501h
driver_list_call:
    int 2fh
driver_list_return:
    mov ax,[driver_list+1]
    mov [proof+96],ax
    mov ax,[driver_list+3]
    mov [proof+98],ax
    xor bx,bx
    mov ax,150ch
version_call:
    int 2fh
version_return:
    mov [proof+104],bx
    cmp bh,2
    jne failed
    mov ah,52h
lists_call:
    int 21h
lists_return:
    mov [proof+106],bx
    mov [proof+108],es
    mov ax,[es:bx-2]
    mov [proof+110],ax
    push cs
    pop es
    mov word [proof+10],2
    mov dx,payload_path
    mov cx,37h
    mov ah,4eh
find_call:
    int 21h
find_return:
    jc failed
    mov ax,[dta+26]
    mov [proof+30],ax
    mov ax,[dta+28]
    mov [proof+32],ax
    mov al,[dta+21]
    mov [proof+34],al
    cmp word [proof+30],8205
    jne failed
    cmp word [proof+32],0
    jne failed
    mov ah,4fh
findnext_call:
    int 21h
findnext_return:
    jnc failed
    mov [proof+62],ax
    cmp ax,18
    jne failed
    mov dx,text_path
    mov ax,3d00h
text_open:
    int 21h
text_open_return:
    jc failed
    mov bx,ax
    mov dx,text_buffer
    mov cx,256
    mov ah,3fh
text_read:
    int 21h
text_read_return:
    jc failed
    mov [proof+28],ax
    cmp ax,197
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov word [proof+10],3
    mov dx,payload_path
    mov ax,3d00h
payload_open:
    int 21h
payload_open_return:
    jc failed
    mov [proof+22],ax
    mov word [proof+10],4
    mov bx,ax
    mov dx,buffer
    mov cx,8205
    mov ah,3fh
payload_read:
    int 21h
payload_read_return:
    jc failed
    mov [proof+26],ax
    cmp ax,8205
    jne failed
    xor bx,bx
; Independently check every original binary byte, including every sector and final short extent.
pattern:
    mov ax,bx
    imul ax,29
    mov dx,bx
    shr dx,8
    imul dx,7
    add ax,dx
    add al,53h
    cmp al,[buffer+bx]
    jne failed
    inc bx
    cmp bx,8205
    jne pattern
    mov word [proof+10],5
    mov dword [request+20],16
    call raw_read
    cmp dword [raw_buffer+1],'CD00'
    jne failed
    mov word [proof+10],6
    mov bx,[proof+22]
    mov dx,2039
    xor cx,cx
    mov ax,4200h
seek_call:
    int 21h
seek_return:
    jc failed
    mov [proof+40],ax
    mov [proof+42],dx
    cmp ax,2039
    jne failed
    test dx,dx
    jnz failed
    mov dx,seek_buffer
    mov cx,31
    mov ah,3fh
seek_read:
    int 21h
seek_read_return:
    jc failed
    mov [proof+44],ax
    cmp ax,31
    jne failed
    xor cx,cx
    xor dx,dx
    mov ax,4202h
end_seek:
    int 21h
end_seek_return:
    jc failed
    cmp ax,8205
    jne failed
    test dx,dx
    jnz failed
    mov dx,eof_byte
    mov cx,1
    mov ah,3fh
end_read:
    int 21h
end_read_return:
    jc failed
    mov [proof+46],ax
    test ax,ax
    jnz failed
    mov ax,4203h
bad_seek:
    int 21h
bad_seek_return:
    jnc failed
    mov [proof+58],ax
    cmp ax,1
    jne failed
    mov word [proof+10],7
    mov dx,copy_path
    xor cx,cx
    mov ah,3ch
copy_open:
    int 21h
copy_open_return:
    jc failed
    mov [proof+24],ax
    mov bx,ax
    mov dx,buffer
    mov cx,8205
    mov ah,40h
copy_write:
    int 21h
copy_write_return:
    jc failed
    mov [proof+56],ax
    cmp ax,8205
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov dx,text_copy_path
    mov si,text_buffer
    mov cx,197
    call file
    finit
    fldpi
    mov word [proof+10],8
    mov dword [request+20],22
    call raw_read
    mov word [proof+10],9
    mov bx,[proof+22]
    mov ah,3eh
payload_close:
    int 21h
payload_close_return:
    jc failed
    mov dx,absent_path
    mov ax,3d00h
absent_open:
    int 21h
absent_open_return:
    jnc failed
    mov [proof+36],ax
    cmp ax,2
    jne failed
    mov ax,3524h
critical_get:
    int 21h
critical_get_return:
    mov [proof+76],bx
    mov [proof+78],es
    push cs
    pop es
    mov dx,critical24
    mov [proof+80],dx
    mov [proof+82],cs
    mov ax,2524h
critical_set:
    int 21h
critical_set_return:
    mov word [proof+86],1
    mov dx,payload_path
    mov ax,3d02h
readonly_open:
    int 21h
readonly_open_return:
    pushf
    pop word [proof+64]
    and word [proof+64],1
    mov [proof+38],ax
    cmp word [proof+64],0
    jne readonly_denied
    ; MSCDEX permits this OPEN; only an actual write can establish write protection.
    mov word [proof+112],1
    mov word [proof+90],1
    mov [proof+72],ax
    mov bx,ax
    mov dx,rejected_byte
    mov cx,1
    mov word [proof+70],1
    mov word [proof+88],40h
    mov ah,40h
negative_write:
    int 21h
negative_write_return:
    jmp negative_result
readonly_denied:
    cmp ax,5
    jne failed
    ; If this DOS rejects writable OPEN, require an actual failed CREATE instead.
    mov dx,denied_path
    xor cx,cx
    mov word [proof+88],3ch
    mov ah,3ch
negative_create:
    int 21h
negative_create_return:
negative_result:
    pushf
    pop word [proof+66]
    and word [proof+66],1
    mov [proof+68],ax
    cmp word [proof+66],1
    jne failed
    test ax,ax
    jz failed
    cmp word [proof+90],0
    je negative_closed
    mov bx,[proof+72]
    mov ah,3eh
    int 21h
    jc failed
    mov word [proof+90],0
negative_closed:
    call restore_critical
    fstp tword [raw_data]
    mov dx,raw_path
    mov si,raw_data
    mov cx,10
    call file
    mov word [proof+10],10
    mov word [proof+60],1
    mov dx,proof_path
    mov si,proof
    mov cx,proof_end-proof
    call file
    mov si,done_text
    call print
    mov ax,4c00h
exit_call:
    int 21h
; Preserve the failing phase in an ordinary DOS-written result, and return a real nonzero exit status.
failed:
    push cs
    pop ds
    cmp word [proof+90],0
    je .no_open
    mov bx,[proof+72]
    mov ah,3eh
    int 21h
    mov word [proof+90],0
.no_open:
    cmp word [proof+86],0
    je .vector_restored
    call restore_critical
.vector_restored:
    mov word [proof+12],0ffffh
    sti
    mov si,fail_text
    call print
    mov ax,4c01h
    int 21h
; Return FAIL only to the original DOS critical-error caller, without invoking another DOS service.
critical24:
    nop
    inc word [cs:proof+74]
    mov al,3
critical24_iret:
    iret
; Restore the exact original INT24 far vector before the COM client can release its MCB.
restore_critical:
    push ds
    mov dx,[proof+76]
    mov ax,[proof+78]
    mov ds,ax
    mov ax,2524h
critical_restore:
    int 21h
critical_restore_return:
    pop ds
    mov word [proof+86],0
    mov word [proof+84],1
    ret
; Submit one genuine cooked/HSG read-long through the original MSCDEX dispatcher.
raw_read:
    push cs
    pop es
    mov word [request+3],0
    mov bx,request
    mov cx,17
    mov ax,1510h
raw_call:
    int 2fh
raw_return:
    mov ax,[request+3]
    mov [proof+48],ax
    test ax,8000h
    jnz failed
    test ax,100h
    jz failed
    ret
; Write exact own file bytes through ordinary DOS create/write/close services.
file:
    push cx
    push si
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    pop dx
    pop cx
    mov ah,40h
    int 21h
    jc failed
    cmp ax,cx
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    ret
; Print only through original DOS console services; no E9 marker is an acceptance substitute.
print:
    lodsb
    test al,al
    jz .done
    mov dl,al
    mov ah,2
    int 21h
    jmp print
.done:
    ret
code_end:
proof db 'MSCDP001'
    times 120-8 db 0
    db 'MSCDEND1'
proof_end:
request db 27,0,80h
    dw 0
    times 8 db 0
    db 0
    dw raw_buffer,0
    dw 1
    dd 16
    db 0,0,0
driver_list times 16 db 0
dta times 128 db 0
buffer_guard times 16 db 0
buffer times 16384 db 0
buffer_end times 16 db 0
text_buffer times 256 db 0
seek_buffer times 32 db 0
eof_byte db 0a5h
raw_buffer times 2048 db 0
raw_data times 10 db 0
payload_path db 'R:\PAYLOAD.BIN',0
text_path db 'R:\IDENT.TXT',0
absent_path db 'R:\ABSENT.BIN',0
denied_path db 'R:\DENIED.TMP',0
rejected_byte db 0a5h
copy_path db 'C:\MSCDEX.BIN',0
text_copy_path db 'C:\MSCDEX.TXT',0
proof_path db 'C:\MSCDPRF.BIN',0
raw_path db 'C:\MSCDRAW.BIN',0
done_text db 13,10,'MSCDEX-DONE',13,10,0
fail_text db 13,10,'MSCDEX-FAILED',13,10,0
    times 2048 db 0
stack_top:
    db 'MSCMETA1'
    dw 1,68
    dw proof,proof_end,code_end,program_end,request,dta,buffer_guard,buffer,buffer_end,text_buffer,seek_buffer,raw_buffer,raw_data,stack_top
    dw install_call,install_return,installed_call,installed_return,find_call,find_return,findnext_call,findnext_return
    dw text_open,text_open_return,text_read,text_read_return,payload_open,payload_open_return,payload_read,payload_read_return
    dw seek_call,seek_return,seek_read,seek_read_return,end_seek,end_seek_return,end_read,end_read_return,bad_seek,bad_seek_return
    dw raw_call,raw_return,payload_close,payload_close_return,exit_call
    dw driver_list,driver_list_call,driver_list_return,version_call,version_return
    dw lists_call,lists_return
    dw critical24,critical24_iret,readonly_open,readonly_open_return,negative_write,negative_write_return,negative_create,negative_create_return
    dw critical_restore,critical_restore_return,critical_get,critical_get_return,critical_set,critical_set_return,rejected_byte,denied_path
program_end:
