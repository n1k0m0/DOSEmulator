; Original BIOS format probe loaded by real DOS, Apache-2.0.
; Only use with the script's private 1.44-MiB scratch floppies (base fill 39h).
; /V verifies persisted formatted sectors; /R checks read-only format rejection.
bits 16
cpu 486
org 0x100
start:
    cld
    push cs
    pop ds
    push cs
    pop es
    mov bx,(program_end-$$+0x100+15)/16
    mov ah,0x4a
    int 0x21
    jc failed
    mov bx,0x1010
    mov ah,0x48
    int 0x21
    jc failed
    mov [buffer_seg],ax
    mov ax,0x351e
    int 0x21
    mov [old_vector],bx
    mov [old_vector+2],es
    mov byte [installed],1
    mov dx,banner
    call print
    mov si,0x81
    xor cx,cx
    mov cl,[0x80]
.args:
    jcxz .ready
    lodsb
    and al,0xdf
    cmp al,'V'
    jne .check_readonly
    mov byte [mode],1
.check_readonly:
    cmp al,'R'
    jne .next
    mov byte [mode],2
.next:
    loop .args
.ready:
    mov byte [stage],1
    mov ax,0x1800
    mov cx,0x4f12
    xor dx,dx
    int 0x13
    jc failed
    cmp ah,0
    jne failed
    ; Install a DOS-owned writable copy of the returned native DPT via DOS.
    push ds
    mov ax,es
    mov ds,ax
    mov si,di
    push cs
    pop es
    mov di,dpt
    mov cx,11
    rep movsb
    pop ds
    cmp byte [dpt+3],2
    jne failed
    cmp byte [dpt+4],18
    jne failed
    mov dx,dpt
    mov ax,0x251e
    int 0x21
    mov byte [stage],2
    ; Unsupported density must fail without replacing ES:DI.
    mov ax,0x1234
    mov es,ax
    mov di,0x5678
    mov ax,0x1800
    mov cx,0x2709
    xor dx,dx
    int 0x13
    jnc failed
    cmp ah,0x0c
    jne failed
    mov ax,es
    cmp ax,0x1234
    jne failed
    cmp di,0x5678
    jne failed
    cmp byte [mode],1
    je verify
    mov byte [stage],3
    mov byte [cylinder],3
    mov byte [head],0
    mov byte [drive],0
    mov byte [count],18
    mov byte [stride],1
    call descriptors
    call format
    pushf
    cmp byte [mode],2
    jne .writable
    popf
    jmp readonly
.writable:
    popf
    jc failed
    cmp ah,0
    jne failed
    mov dx,a_ok
    call print
    mov byte [stage],4
    mov ax,0x1800
    mov cx,0x4f12
    mov dx,1
    int 0x13
    jc failed
    mov byte [cylinder],79
    mov byte [head],1
    mov byte [drive],1
    mov byte [count],9
    mov byte [stride],2
    mov byte [dpt+4],9
    mov byte [dpt+7],0x57
    mov byte [dpt+8],0xa7
    call descriptors
    call format
    jc failed
    cmp ah,0
    jne failed
    mov dx,b_ok
    call print
    mov byte [stage],5
    ; Corrupt only the last descriptor. No partial track writes are acceptable.
    mov ax,[buffer_seg]
    add ax,0x1000
    mov es,ax
    mov byte [es:34],1     ; N of the ninth CHRN, starting at original ES:FFFF
    call format
    jnc failed
    cmp ah,4
    jne failed
    mov ah,1
    int 0x13
    jnc failed
    cmp ah,4
    jne failed
    mov dx,invalid_ok
    call print
verify:
    mov byte [stage],6
    mov byte [drive],0
    mov byte [head],0
    mov byte [cylinder],3
    mov byte [expected],0xf6
    mov byte [sector],1
.a:
    call read_check
    inc byte [sector]
    cmp byte [sector],19
    jb .a
    ; Adjacent track sector must still contain the scratch base sentinel.
    mov byte [cylinder],2
    mov byte [head],1
    mov byte [sector],18
    mov byte [expected],0x39
    call read_check
    mov byte [stage],7
    mov byte [drive],1
    mov byte [cylinder],79
    mov byte [sector],1
.b:
    mov byte [expected],0x39
    test byte [sector],1
    jnz .check_b
    mov byte [expected],0xa7
.check_b:
    call read_check
    inc byte [sector]
    cmp byte [sector],19
    jb .b
    mov dx,verify_ok
    call print
    jmp success
readonly:
    jnc failed
    cmp ah,3
    jne failed
    mov ah,1
    xor dx,dx
    int 0x13
    jnc failed
    cmp ah,3
    jne failed
    mov byte [cylinder],3
    mov byte [head],0
    mov byte [drive],0
    mov byte [sector],1
    mov byte [expected],0x39
    call read_check
    mov dx,readonly_ok
    call print
success:
    call restore
    mov dx,done
    call print
    mov ax,0x4c00
    int 0x21
failed:
    mov [error_ah],ah
    call restore
    mov dx,error
    call print
    mov al,[stage]
    call hex
    mov dl,'/'
    mov ah,2
    int 0x21
    mov al,[error_ah]
    call hex
    mov dx,newline
    call print
    mov ax,0x4c01
    int 0x21
restore:
    cmp byte [installed],0
    je .done
    push ds
    mov dx,[old_vector]
    mov ax,[old_vector+2]
    mov ds,ax
    mov ax,0x251e
    int 0x21
    pop ds
.done:
    ret
; Write reversed/interleaved IDs into a DOS-allocated buffer at ES:FFFF.
; Every byte is stored with explicit segment normalization at the wrap.
descriptors:
    mov es,[buffer_seg]
    mov di,0xffff
    xor cx,cx
    mov cl,[count]
    mov bl,[count]
.id:
    mov al,[cylinder]
    call put_byte
    mov al,[head]
    call put_byte
    mov al,bl
    mul byte [stride]
    call put_byte
    mov al,2
    call put_byte
    dec bl
    loop .id
    ret
put_byte:
    mov [es:di],al
    inc di
    jnz .done
    mov ax,es
    add ax,0x1000
    mov es,ax
.done:
    ret
format:
    mov es,[buffer_seg]
    mov bx,0xffff
    mov ch,[cylinder]
    mov cl,0xff            ; deliberately unused for format
    mov dh,[head]
    mov dl,[drive]
    mov ax,0x0500          ; SC supplied by the active INT1E DPT
    int 0x13
    ret
read_check:
    push cs
    pop es
    mov bx,read_buffer
    mov ch,[cylinder]
    mov cl,[sector]
    mov dh,[head]
    mov dl,[drive]
    mov ax,0x0201
    int 0x13
    jc failed
    mov di,read_buffer
    mov al,[expected]
    mov cx,512
    repe scasb
    jne failed
    ret
print:
    mov ah,9
    int 0x21
    ret
hex:
    push ax
    shr al,4
    call .nibble
    pop ax
    and al,15
.nibble:
    add al,'0'
    cmp al,'9'
    jbe .digit
    add al,7
.digit:
    mov dl,al
    mov ah,2
    int 0x21
    ret
buffer_seg dw 0
old_vector dw 0,0
installed db 0
mode db 0
stage db 0
error_ah db 0
cylinder db 0
head db 0
drive db 0
sector db 0
count db 0
stride db 0
expected db 0
dpt times 11 db 0
banner db 'BIOS FORMAT / REAL DOS / DMA2 / PRIVATE SCRATCH MEDIA',13,10,'$'
a_ok db 'FORMAT-A-FULL-TRACK-OK',13,10,'$'
b_ok db 'FORMAT-B-INTERLEAVED-IDS-OK',13,10,'$'
invalid_ok db 'FORMAT-INVALID-DESCRIPTOR-OK',13,10,'$'
verify_ok db 'FORMAT-READBACK-PERSISTENCE-OK',13,10,'$'
readonly_ok db 'FORMAT-WRITE-PROTECT-OK',13,10,'$'
done db 'BIOS-FORMAT-DOS-RETURN-OK',13,10,'$'
error db 'BIOS-FORMAT-FAILED stage/AH=', '$'
newline db 13,10,'$'
align 16
read_buffer times 512 db 0
program_end:
