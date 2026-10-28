; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own SFF8020i PIO client. The optical medium is immutable; only fresh own DOS files are written.
bits 16
cpu pentium
org 100h
; Enter from original DOS, reserve own complete memory and exercise the real secondary PACKET port path.
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
    pop word [proof+12]
    smsw ax
    and ax,1
    mov [proof+8],ax
    mov eax,1
    cpuid
    mov [proof+16],eax
    mov [proof+20],edx
    cmp eax,524h
    jne failed
    cmp edx,135h
    jne failed
    mov bx,4002h
    mov ah,48h
    int 21h
    jc failed
    mov [allocation],ax
    mov [proof+24],ax
    push es
    dec ax
    mov es,ax
    mov ax,[es:1]
    mov [proof+26],ax
    mov ax,[es:3]
    mov [proof+28],ax
    pop es
    movzx eax,word [allocation]
    shl eax,4
    add eax,20000h
    and eax,0fffe0000h
    mov [proof+32],eax
    shr eax,4
    mov [window],ax
    mov [dds+8],ax
    mov edx,[proof+32]
    mov [dds+12],edx
    cmp word [proof+8],0
    je locked
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
    inc word [proof+36]
    cmp word [dds+10],0
    jne failed
    mov eax,[dds+12]
    cmp eax,[proof+32]
    jne failed
    mov bx,1
    xor dx,dx
    mov ax,810bh
vds_disable:
    int 4bh
    jc failed
    inc word [proof+40]
locked:
    mov si,dds
    mov di,proof+48
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
    mov ax,[window]
    dec ax
    mov es,ax
    xor di,di
    mov al,0d6h
    mov cx,16
    rep stosb
    mov ax,[window]
    add ax,2000h
    mov es,ax
    xor di,di
    mov al,0d6h
    mov cx,16
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
    mov ax,3577h
    int 21h
    mov [proof+64],bx
    mov [proof+66],es
    push cs
    pop es
    mov dx,irq15
    mov ax,2577h
    int 21h
    cli
    in al,21h
    mov [proof+68],al
    in al,0a1h
    mov [proof+69],al
    and al,7fh
    out 0a1h,al
    mov al,[proof+68]
    and al,0fbh
    out 21h,al
    mov dx,176h
    mov al,0b0h
    out dx,al
    inc dx
    in al,dx
    dec dx
    mov al,0a0h
    out dx,al
    inc dx
    in al,dx
    mov ah,0
    int 1ah
    mov bx,dx
drain:
    sti
    hlt
drain_return:
    mov ah,0
    int 1ah
    sub dx,bx
    cmp dx,2
    jb drain
    cli
    mov eax,80003900h
    call cfg_read
    mov [proof+72],eax
    cmp eax,12308086h
    jne failed
    mov eax,80003940h
    call cfg_read
    mov [proof+76],eax
    test eax,80000000h
    jz failed
    mov eax,80003904h
    call cfg_read
    mov [proof+80],ax
    cmp ax,3
    jne failed
    mov eax,80003870h
    call cfg_read
    mov [proof+82],al
    cmp al,0fh
    jne failed
    ; IDENTIFY PACKET uses real 16-bit PIO while nIEN prevents an unowned boot-edge count.
    mov dx,376h
    mov al,2
    out dx,al
    mov dx,177h
    mov al,0a1h
    out dx,al
    call poll_drq
    mov ax,[window]
    mov es,ax
    xor di,di
    mov dx,170h
    mov cx,256
    rep insw
    mov ecx,4000000
identify_wait:
    mov dx,376h
    in al,dx
    test al,88h
    jz identify_complete
    dec ecx
    jnz identify_wait
    jmp failed
identify_complete:
    mov dx,177h
    in al,dx
    mov [proof+83],al
    xor al,al
    mov dx,376h
    out dx,al
    mov word [received],512
    mov word [proof+10],0
identify_done:
    nop
    ; Genuine PACKET commands. INQUIRY does not consume the reset unit attention.
    call clear_cdb
    mov byte [cdb],12h
    mov byte [cdb+4],36
    mov word [proof+10],1
    call packet
    call clear_cdb
    mov byte [cdb],3
    mov byte [cdb+4],18
    mov word [proof+10],2
    call packet
    mov ax,[window]
    mov es,ax
    mov al,[es:2]
    and al,15
    mov [proof+84],al
    cmp al,6
    jne failed
    cmp byte [es:12],29h
    jne failed
    push cs
    pop es
    call clear_cdb
    mov word [proof+10],3
    call packet
    call clear_cdb
    mov byte [cdb],25h
    mov word [proof+10],4
    call packet
    mov ax,[window]
    mov es,ax
    cmp dword [es:0],07f000000h
    jne failed
    cmp dword [es:4],00080000h
    jne failed
    push cs
    pop es
    call clear_cdb
    mov byte [cdb],43h
    mov byte [cdb+8],20
    mov word [proof+10],5
    call packet
    call clear_cdb
    mov byte [cdb],0ffh
    mov byte [want_error],1
    mov word [proof+10],6
    call packet
    call clear_cdb
    mov byte [cdb],3
    mov byte [cdb+4],18
    mov word [proof+10],7
    call packet
    mov ax,[window]
    mov es,ax
    cmp byte [es:2],5
    jne failed
    cmp byte [es:12],20h
    jne failed
    push cs
    pop es
    call clear_cdb
    mov byte [cdb],28h
    mov byte [cdb+5],128
    mov byte [cdb+8],1
    mov byte [want_error],1
    mov word [proof+10],8
    call packet
    call clear_cdb
    mov byte [cdb],3
    mov byte [cdb+4],18
    mov word [proof+10],9
    call packet
    mov ax,[window]
    mov es,ax
    cmp byte [es:2],5
    jne failed
    cmp byte [es:12],21h
    jne failed
    push cs
    pop es
    ; Read actual PVD and directory bytes through PACKET, then use their real extents.
    call clear_cdb
    mov byte [cdb],28h
    mov byte [cdb+5],16
    mov byte [cdb+8],1
    mov word [proof+10],10
    call packet
    mov ax,[window]
    mov es,ax
    cmp word [received],2048
    jne failed
    cmp dword [es:0],30444301h
    jne failed
    cmp word [es:4],3130h
    jne failed
    cmp byte [es:6],1
    jne failed
    cmp word [es:128],2048
    jne failed
    mov eax,[es:158]
    mov [proof+88],eax
    cmp eax,20
    jne failed
    push cs
    pop es
    call clear_cdb
    mov byte [cdb],28h
    mov byte [cdb+5],20
    mov byte [cdb+8],1
    mov word [proof+10],11
    call packet
    mov ax,[window]
    mov es,ax
    xor bx,bx
find_record:
    movzx dx,byte [es:bx]
    test dx,dx
    jz found_records
    cmp byte [es:bx+32],13
    jne .text
    cmp dword [es:bx+33],04c594150h
    jne .text
    cmp dword [es:bx+37],02e44414fh
    jne .text
    cmp dword [es:bx+41],03b4e4942h
    jne .text
    cmp byte [es:bx+45],'1'
    jne .text
    mov eax,[es:bx+2]
    mov [proof+92],eax
    mov eax,[es:bx+10]
    mov [proof+96],eax
.text:
    cmp byte [es:bx+32],11
    jne .next
    cmp dword [es:bx+33],04e454449h
    jne .next
    cmp dword [es:bx+37],058542e54h
    jne .next
    cmp word [es:bx+41],03b54h
    jne .next
    cmp byte [es:bx+43],'1'
    jne .next
    mov eax,[es:bx+2]
    mov [proof+100],eax
    mov eax,[es:bx+10]
    mov [proof+104],eax
.next:
    add bx,dx
    cmp bx,2048
    jb find_record
found_records:
    cmp dword [proof+92],22
    jne failed
    cmp dword [proof+96],8205
    jne failed
    cmp dword [proof+100],21
    jne failed
    cmp dword [proof+104],197
    jne failed
    push cs
    pop es
    fninit
    fld tword [extended]
    fldpi
    call pcm_start
    ; Original P54C rejects RDTSC in V86 even when TSD is clear; preserve the original DOS monitor.
    cmp word [proof+8],0
    jne no_tsc_before
tsc_before:
    rdtsc
tsc_saved:
    mov [proof+112],eax
    mov [proof+116],edx
no_tsc_before:
    call clear_cdb
    mov byte [cdb],28h
    mov eax,[proof+92]
    bswap eax
    mov [cdb+2],eax
    mov byte [cdb+8],5
    mov word [proof+10],12
    call packet
    mov ax,[window]
    mov es,ax
    xor di,di
    mov cx,8205
check_payload:
    mov ax,di
    imul ax,29
    mov dx,di
    shr dx,8
    imul dx,7
    add ax,dx
    add al,53h
    cmp [es:di],al
    jne failed
    inc di
    loop check_payload
    mov dx,payload_file
    mov cx,8205
    call window_file
    push cs
    pop es
    call clear_cdb
    mov byte [cdb],0a8h
    mov byte [cdb+5],21
    mov byte [cdb+9],1
    mov word [byte_limit],1024
    mov word [proof+10],13
    call packet
    mov dx,text_file
    mov cx,197
    call window_file
    ; nIEN suppresses both command-ready and completion; AltStatus leaves the pending completion intact.
    call clear_cdb
    mov word [proof+10],14
    mov ax,[proof+44]
    mov [proof+120],ax
    mov dx,376h
    mov al,2
    out dx,al
    call begin_packet
    call poll_drq
    mov dx,170h
    mov si,cdb
    mov cx,6
    rep outsw
    mov ecx,4000000
quiet_poll:
    mov dx,376h
    in al,dx
    test al,80h
    jnz .more
    test al,8
    jz quiet_complete
.more:
    dec ecx
    jnz quiet_poll
    jmp failed
quiet_complete:
    mov [proof+122],al
    mov ax,[proof+44]
    cmp ax,[proof+120]
    jne failed
    mov dx,376h
    xor al,al
    out dx,al
    call next_irq
    cmp byte [last_reason],3
    jne failed
    test byte [last_status],9
    jnz failed
    mov word [received],0
command14_done:
    nop
    mov word [proof+10],15
    mov ecx,10000000
wait_pcm:
    in al,8
    test al,2
    jnz pcm_done
    dec ecx
    jnz wait_pcm
    jmp failed
pcm_done:
    mov dx,22eh
    in al,dx
    ; V86 uses the independently observed saved scheduler/TSC source, never a privilege bypass.
    cmp word [proof+8],0
    jne no_tsc_after
tsc_after:
    rdtsc
    mov [proof+128],eax
    mov [proof+132],edx
no_tsc_after:
    fstp tword [raw_data+8]
    fstp tword [raw_data+18]
    fnstcw [raw_data+28]
    fnstsw [raw_data+30]
    cli
    mov al,[proof+68]
    out 21h,al
    mov al,[proof+69]
    out 0a1h,al
    mov dx,[proof+64]
    mov ax,[proof+66]
    mov ds,ax
    mov ax,2577h
    int 21h
    push cs
    pop ds
    push cs
    pop es
    cmp word [proof+36],0
    je unlocked
    mov bx,1
    xor dx,dx
    mov ax,810ch
vds_enable:
    int 4bh
    jc failed
    mov di,dds
    xor dx,dx
    mov ax,8104h
vds_unlock:
    int 4bh
    jc failed
    inc word [proof+38]
unlocked:
    push word [proof+12]
    popf
    mov bx,[file_handle]
    mov ah,3eh
    int 21h
    jc failed
    mov dx,raw_file
    mov si,raw_data
    mov cx,32
    call file
    mov word [proof+136],1
    mov dx,proof_file
    mov si,proof
    mov cx,proof_end-proof
    call file
    mov dx,irq_file
    mov si,irq_records
    mov cx,[proof+44]
    shl cx,5
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
; Fail visibly and preserve an explicit proof code; no marker alone passes the host oracle.
failed:
    push cs
    pop ds
    mov word [proof+138],0ffffh
    sti
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Reset precisely the twelve-byte own command, the even PIO limit and expected error policy.
clear_cdb:
    push es
    push cs
    pop es
    mov di,cdb
    xor ax,ax
    mov cx,6
    rep stosw
    pop es
    mov byte [want_error],0
    mov word [byte_limit],512
    ret
; Issue actual PACKET with PIO features and caller-selected maximum phase byte count.
begin_packet:
    mov dx,176h
    mov al,0a0h
    out dx,al
    mov dx,171h
    xor al,al
    out dx,al
    mov dx,174h
    mov ax,[byte_limit]
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    mov dx,177h
    mov al,0a0h
    out dx,al
    ret
; Read actual alternate status with a finite guest poll bound, without acknowledging INTRQ.
poll_drq:
    mov ecx,4000000
.loop:
    mov dx,376h
    in al,dx
    test al,80h
    jnz .more
    test al,1
    jnz failed
    test al,8
    jnz .ready
.more:
    dec ecx
    jnz .loop
    jmp failed
.ready:
    ret
; Run one real interrupt-driven 12-byte command and consume all actual data phases.
packet:
    mov word [received],0
    call begin_packet
    call next_irq
    cmp byte [last_reason],1
    jne failed
    test byte [last_status],8
    jz failed
    mov dx,170h
    mov si,cdb
    mov cx,5
    rep outsw
    cmp word [proof+10],12
    jne packet_last
    ; Actual IOSE and secondary IDETIM disable hide both ports and ignore a sixth command word.
    mov eax,80003904h
    mov bx,2
    call cfg_word
    mov dx,177h
    in al,dx
    mov [proof+140],al
    cmp al,0ffh
    jne failed
    mov dx,170h
    mov ax,0deadh
    out dx,ax
    mov eax,80003904h
    mov bx,3
    call cfg_word
    mov eax,80003940h
    mov ebx,[proof+76]
    and ebx,7fffffffh
    call cfg_dword
    mov dx,376h
    in al,dx
    mov [proof+141],al
    cmp al,0ffh
    jne failed
    mov dx,170h
    mov ax,0deadh
    out dx,ax
    mov eax,80003940h
    mov ebx,[proof+76]
    call cfg_dword
    inc word [proof+142]
cdb_partial:
    nop
packet_last:
    mov dx,170h
    mov ax,[cdb+10]
    out dx,ax
packet_phase:
    call next_irq
    cmp byte [last_reason],3
    je packet_done
    cmp byte [last_reason],2
    jne failed
    test byte [last_status],8
    jz failed
    mov cx,[last_count]
    test cx,1
    jnz failed
    cmp cx,[byte_limit]
    ja failed
    test cx,cx
    jz failed
    mov ax,[window]
    mov es,ax
    mov di,[received]
    add [received],cx
    shr cx,1
    mov dx,170h
data_loop:
    in ax,dx
    stosw
    cmp word [proof+10],12
    jne data_next
    cmp di,128
    jne data_next
data_partial:
    nop
data_next:
    loop data_loop
    push cs
    pop es
    jmp packet_phase
packet_done:
    mov al,[last_status]
    and al,1
    cmp al,[want_error]
    jne failed
command_done:
    nop
    ret
; Wait in the original own real/V86 client, with actual IRQ recognition after the STI shadow.
next_irq:
irq_wait:
    sti
    nop
wait_return:
    cli
    mov ax,[proof+44]
    cmp ax,[consumed]
    je irq_wait
    mov [consumed],ax
    ret
; Capture one actual small IRQ15 frame, nonacknowledging phase view, status ACK, then both PIC EOIs.
irq15:
    pushad
    push ds
    push cs
    pop ds
    mov bp,sp
    mov dx,376h
    in al,dx
    cmp word [proof+10],0
    je irq_drain
    mov bx,[proof+44]
    cmp bx,128
    jae irq_failed
    shl bx,5
    add bx,irq_records
    mov [bx+16],al
    mov dx,172h
    in al,dx
    and al,3
    mov [last_reason],al
    mov [bx+17],al
    mov dx,174h
    in al,dx
    mov [last_count],al
    mov [bx+18],al
    inc dx
    in al,dx
    mov [last_count+1],al
    mov [bx+19],al
    mov dx,177h
    in al,dx
    mov [last_status],al
    mov [bx+20],al
    mov word [bx],15
    mov ax,[proof+10]
    mov [bx+2],ax
    mov ax,[proof+44]
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
    inc word [proof+44]
irq_commit:
    nop
    jmp irq_eoi
irq_drain:
    mov dx,177h
    in al,dx
    inc word [proof+46]
irq_eoi:
    mov al,20h
    out 0a0h,al
    out 20h,al
    pop ds
    popad
irq_iret:
    iret
irq_failed:
    mov word [proof+138],0eeeeh
    cli
    hlt
    jmp $
; Preserve a whole actually read CD file through DOS while retaining the separate proof handle.
window_file:
    push cx
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    pop cx
    push ds
    mov ax,[window]
    mov ds,ax
    xor dx,dx
    push cx
    mov ah,40h
    int 21h
    pop cx
    pop ds
    jc failed
    cmp ax,cx
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    ret
; Write one own fixed file with actual DOS create/write/close calls.
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
; Program 512 owned C0 bytes via real DMA1 and CT1740 8000-Hz commands.
pcm_start:
    mov dx,226h
    mov al,1
    out dx,al
    mov cx,128
.delay: loop .delay
    xor al,al
    out dx,al
    mov dx,22eh
.ready: in al,dx
    test al,80h
    jz .ready
    mov dx,22ah
    in al,dx
    cmp al,0aah
    jne failed
    mov ax,[window]
    add ax,1800h
    mov es,ax
    xor di,di
    mov al,0c0h
    mov cx,512
    rep stosb
    push cs
    pop es
    mov al,5
    out 0ah,al
    xor al,al
    out 0ch,al
    mov eax,[proof+32]
    add eax,18000h
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
    ret
; Respect actual DSP input-busy admission before every command byte.
dsp:
    push ax
    mov dx,22ch
.busy: in al,dx
    test al,80h
    jnz .busy
    pop ax
    out dx,al
    ret
; Perform mechanism-one reads without synthetic configuration access.
cfg_read:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    ret
; Perform a real PCI command word write through CFC.
cfg_word:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov ax,bx
    out dx,ax
    ret
; Perform a real independent channel decode configuration dword write.
cfg_dword:
    mov dx,0cf8h
    out dx,eax
    mov dx,0cfch
    mov eax,ebx
    out dx,eax
    ret
; Print only through the actual guest DOS console.
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
code_end:
allocation dw 0
window dw 0
file_handle dw 0
received dw 0
consumed dw 0
byte_limit dw 512
last_count dw 0
last_reason db 0
last_status db 0
want_error db 0
align 4,db 0
dds dd 20000h,0
    dw 0,0
    dd 0
cdb times 12 db 0
proof db 'ATAPIP01'
    times 248-8 db 0
    db 'ATEND001'
proof_end:
raw_data db 'ATRAW001'
    times 24 db 0
extended dq 0fedcba9876543210h
    dw 4001h
proof_file db 'C:\ATAPI.DAT',0
irq_file db 'C:\ATAPIIRQ.DAT',0
raw_file db 'C:\ATAPIRAW.DAT',0
payload_file db 'C:\CD-COPY.BIN',0
text_file db 'C:\CD-TEXT.TXT',0
done_text db 13,10,'SNAPSHOT-DONE',13,10,'ATAPI-DONE',13,10,0
failed_text db 13,10,'ATAPI-FAILED',13,10,0
irq_records times 128*32 db 0
    times 2048 db 0
stack_top:
    db 'ATMETA01'
    dw 1,28
    dw proof,proof_end,code_end,program_end,cdb_partial,data_partial,command_done,identify_done
    dw irq15,irq_commit,irq_iret,wait_return,stack_top,dds,window,allocation,file_handle,received
    dw vds_lock,vds_disable,vds_enable,vds_unlock,raw_data,cdb,command14_done,irq_records
    dw tsc_before,tsc_after
program_end:
