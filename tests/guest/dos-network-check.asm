; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own finite real-mode COM witness. Assemble only; execute only in the DOS guest.
; Requires the original Crynwr NE2000.COM already loaded at INT60, for example:
;   NE2000 0x60 10 0x300
; This client uses the packet-driver API, not NIC ports or a host filesystem shim.
; ABI references (original Crynwr sources, consulted without copying their code):
; https://raw.githubusercontent.com/fragglet/crynwr_mirror/main/head.asm
; https://raw.githubusercontent.com/fragglet/crynwr_mirror/main/pktaddr.asm
; https://raw.githubusercontent.com/fragglet/crynwr_mirror/main/ne2000.asm
;
; C:\DOSENIC.BIN is CREATE_NEW and exactly256 bytes. No existing file is replaced.
; All words/dwords in its header are little endian; Ethernet/ARP bytes retain wire order.
; 0 signature DOSENIC1,8 version,10 length,12 phase,14 result,16 flags,18 handle.
; 20/22/24 access AX/FLAGS/DX,26/28/30 get-address AX/FLAGS/DX,
; 32/34/36 send AX/FLAGS/DX,38/40/42 release AX/FLAGS/DX.
; 44 offers,46 deliveries,48 refused offers,50 unrelated ARPs,52 callback errors,
; 54 damaged guard/TX bytes,56 actual RX length,58 actual TX length60,
; 60/62 allocation-callback SS/SP,64/68 start/end BDA ticks,
; 72 actual/fallback MAC6,78 validated gateway MAC6,84 driver vector offset/segment,
; 88/90 offer handle/length,92/94 delivery handle/length,
; 96 actual TX60,156 validated RX prefix64 (zero beyond a shorter actual frame),
; 220 source IPv4,224 gateway IPv4,228 successful get-address returned length,
; 230 reserved,232/234 first DOS AH40 AX/FLAGS,236/238 first AH68 AX/FLAGS,
; 240/242 CREATE_NEW AX/FLAGS,244/246 final close AX/FLAGS (RAM-only after final write),
; 248 END1 marker,252 polling iterations.
; Flags:1 signature,2 access,4 actualMAC,8 send,10h validatedARP,20h release,40h guards.
; Phase99/result0 requires the network proof; natural DOS exit0 additionally requires
; both complete writes, both commits and the final close. The persisted header cannot
; attest its own later final close; that call's results remain in this program's RAM.
; Results:1 signature,2 access,3 MAC,4 send,5 timeout,6 callback,7 release,8 guards,9 file.
; No RDTSC, DOS from callback, resident client, TCP stack or host endpoint is used.

BITS 16
CPU 386
ORG 0x100

%define P_PHASE        (proof+12)
%define P_RESULT       (proof+14)
%define P_FLAGS        (proof+16)
%define P_HANDLE       (proof+18)
%define P_OFFERS       (proof+44)
%define P_DELIVERIES   (proof+46)
%define P_REFUSED      (proof+48)
%define P_UNRELATED    (proof+50)
%define P_CALLBACK_ERR (proof+52)
%define P_GUARD_ERR    (proof+54)
%define P_RX_LENGTH    (proof+56)
%define P_MAC          (proof+72)
%define P_RX_PREFIX    (proof+156)
%define MAX_FRAME      1514
%define TIMEOUT_TICKS  182
%define HARD_POLLS     64000000

%macro INC_SAT 1
    cmp word [cs:%1],0xffff
    je %%full
    inc word [cs:%1]
%%full:
%endmacro

%macro RESTORE_CALLBACK 0
    pop es
    pop ds
    popa
    popf
%endmacro

    jmp entry

; Own main stack and all callback buffers remain in the allocated COM segment.
entry:
    cli
    mov ax,cs
    mov ss,ax
    mov sp,stack_top
    mov ds,ax
    mov es,ax
    cld
    sti
    mov word [P_HANDLE],0xffff
    mov word [proof+58],60
    mov dword [proof+220],0x0f02000a
    mov dword [proof+224],0x0202000a
    mov si,fallback_mac
    mov di,P_MAC
    mov cx,6
    rep movsb

    ; Verify the standard resident signature before issuing any INT60 service.
    mov ax,0x3560
    int 0x21
    mov [proof+84],bx
    mov [proof+86],es
    cmp bx,0xfff4
    ja signature_failed
    mov di,bx
    add di,3
    mov si,packet_signature
    mov cx,9
    cld
    repe cmpsb
    jne signature_failed
    or word [P_FLAGS],1

    ; Access only Ethernet-II ARP: type bytes08,06; generic board type/number.
    push cs
    pop es
    mov ax,0x0201
    mov bx,0xffff
    xor dx,dx
    mov cx,2
    mov si,arp_type
    mov di,receiver
    call packet_call
    mov [proof+20],ax
    pushf
    pop word [proof+22]
    mov [proof+24],dx
    jc access_failed
    mov [P_HANDLE],ax
    mov byte [have_handle],1
    or word [P_FLAGS],2
    mov word [P_PHASE],1

    ; Use the actual address if AH06 exists; only BAD_COMMAND permits fallback.
    mov ax,0x0600
    mov bx,[P_HANDLE]
    mov cx,6
    mov di,get_mac_buffer
    call packet_call
    mov [proof+26],ax
    pushf
    pop word [proof+28]
    mov [proof+30],dx
    jc .no_get_address
    mov [proof+228],cx
    cmp cx,6
    jne mac_failed
    test byte [get_mac_buffer],1
    jnz mac_failed
    mov si,get_mac_buffer
    xor al,al
    mov cx,6
.nonzero_mac:
    or al,[si]
    inc si
    loop .nonzero_mac
    test al,al
    jz mac_failed
    push cs
    pop es
    mov si,get_mac_buffer
    mov di,P_MAC
    mov cx,6
    rep movsb
    or word [P_FLAGS],4
    jmp .prepare_request
.no_get_address:
    cmp dh,11
    jne mac_failed
.prepare_request:
    push cs
    pop es
    cld
    mov si,P_MAC
    mov di,tx_frame+6
    mov cx,6
    rep movsb
    mov si,P_MAC
    mov di,tx_frame+22
    mov cx,6
    rep movsb
    mov si,tx_frame
    mov di,proof+96
    mov cx,60
    rep movsb

    call read_ticks
    mov [proof+64],eax
    mov ax,0x0400
    xor bx,bx
    mov cx,60
    mov si,tx_frame
    call packet_call
    mov [proof+32],ax
    pushf
    pop word [proof+34]
    mov [proof+36],dx
    jc send_failed
    or word [P_FLAGS],8
    mov word [P_PHASE],2
.wait_reply:
    cmp byte [got_reply],1
    je .network_done
    cmp word [P_CALLBACK_ERR],0
    jne callback_failed
    call read_ticks
    mov [proof+68],eax
    sub eax,[proof+64]
    jnc .elapsed
    add eax,0x001800b0         ; BIOS tick counter wraps at midnight.
.elapsed:
    cmp eax,TIMEOUT_TICKS
    jae timeout_failed
    inc dword [proof+252]
    cmp dword [proof+252],HARD_POLLS
    jb .wait_reply
    jmp timeout_failed
.network_done:
    mov word [P_PHASE],3
    jmp cleanup

signature_failed:
    mov word [P_RESULT],1
    jmp cleanup
access_failed:
    mov word [P_RESULT],2
    jmp cleanup
mac_failed:
    mov word [P_RESULT],3
    jmp cleanup
send_failed:
    mov word [P_RESULT],4
    jmp cleanup
timeout_failed:
    mov word [P_RESULT],5
    jmp cleanup
callback_failed:
    mov word [P_RESULT],6

cleanup:
    mov byte [refusing],1
    cmp byte [have_handle],0
    je .released
    mov ax,0x0300
    mov bx,[P_HANDLE]
    call packet_call
    mov [proof+38],ax
    pushf
    pop word [proof+40]
    mov [proof+42],dx
    jc .release_error
    mov byte [have_handle],0
    or word [P_FLAGS],0x20
    jmp .released
.release_error:
    cmp word [P_RESULT],0
    jne .released
    mov word [P_RESULT],7
.released:
    push cs
    pop ds
    push cs
    pop es
    cld
    cmp byte [receive_busy],0
    je .no_pending_callback
    or word [P_CALLBACK_ERR],0x20
.no_pending_callback:
    cmp word [P_CALLBACK_ERR],0
    je .check_guards
    cmp word [P_RESULT],0
    jne .check_guards
    mov word [P_RESULT],6
.check_guards:
    call check_guards
    cmp word [P_GUARD_ERR],0
    je .guards_ok
    cmp word [P_RESULT],0
    jne .proof_ready
    mov word [P_RESULT],8
    jmp .proof_ready
.guards_ok:
    or word [P_FLAGS],0x40
.proof_ready:
    call read_ticks
    mov [proof+68],eax
    mov dword [proof+248],0x31444e45
    mov ax,[P_RESULT]
    or ax,ax
    jnz .failed_phase
    mov word [P_PHASE],99
    jmp .save
.failed_phase:
    or ax,0x8000
    mov [P_PHASE],ax
.save:
    call save_proof
    jnc .exit
    mov word [P_RESULT],9
.exit:
    mov al,[P_RESULT]
    mov ah,0x4c
    int 0x21

; The packet driver may change DS/ES as API results; this client needs neither.
; Preserve their caller values without changing the API's returned carry flag.
packet_call:
    push ds
    push es
    int 0x60
    pop es
    pop ds
    ret

; Read one atomic BDA0040:006C tick value while preserving the caller's IF/DF.
read_ticks:
    pushf
    cli
    push es
    push dx
    mov dx,0x40
    mov es,dx
    mov eax,[es:0x6c]
    pop dx
    pop es
    popf
    ret

; Standard two-stage FAR callback, using the driver's current stack. Its fixed
; maximum own stack use is22 bytes plus one near call/temporary push in validation.
; No DOS, packet API, STI, allocation or unbounded polling is performed here.
receiver:
    pushf
    cli
    pusha
    push ds
    push es
    cld
    cmp ax,0
    je .offer
    cmp ax,1
    je .delivery
    or word [cs:P_CALLBACK_ERR],1
    jmp .return
.offer:
    INC_SAT P_OFFERS
    cmp byte [cs:refusing],0
    jne .refuse
    cmp byte [cs:got_reply],0
    jne .refuse
    cmp byte [cs:receive_busy],0
    jne .refuse
    cmp cx,42
    jb .bad_offer
    cmp cx,MAX_FRAME
    ja .bad_offer
    mov [cs:proof+88],bx
    mov [cs:proof+90],cx
    mov dx,ss
    mov [cs:proof+60],dx
    mov dx,sp
    add dx,22
    mov [cs:proof+62],dx
    mov [cs:offered_length],cx
    mov [cs:offered_handle],bx
    mov byte [cs:receive_busy],1
    RESTORE_CALLBACK
    push cs
    pop es
    mov di,rx_frame
    retf
.bad_offer:
    or word [cs:P_CALLBACK_ERR],2
.refuse:
    INC_SAT P_REFUSED
    RESTORE_CALLBACK
    mov di,0
    mov es,di
    retf
.delivery:
    INC_SAT P_DELIVERIES
    mov [cs:proof+92],bx
    mov [cs:proof+94],cx
    mov dx,ss
    mov [cs:delivery_ss],dx
    mov dx,sp
    add dx,22
    mov [cs:delivery_sp],dx
    mov dx,ds
    mov [cs:delivery_ds],dx
    mov [cs:delivery_si],si
    cmp byte [cs:receive_busy],1
    jne .bad_pair
    cmp cx,[cs:offered_length]
    jne .bad_pair
    cmp bx,[cs:offered_handle]
    jne .bad_pair
    cmp bx,[cs:P_HANDLE]
    jne .bad_pair
    mov dx,cs
    mov ax,ds
    cmp ax,dx
    jne .bad_pointer
    cmp si,rx_frame
    jne .bad_pointer
    push cs
    pop ds
    push cs
    pop es
    mov [P_RX_LENGTH],cx
    call validate_reply
    jc .unrelated
    mov si,rx_frame+22
    mov di,proof+78
    mov cx,6
    rep movsb
    mov si,rx_frame
    mov di,P_RX_PREFIX
    mov cx,[P_RX_LENGTH]
    cmp cx,64
    jbe .copy_prefix
    mov cx,64
.copy_prefix:
    rep movsb
    or word [P_FLAGS],0x10
    mov byte [got_reply],1
    jmp .delivered
.unrelated:
    INC_SAT P_UNRELATED
.delivered:
    mov byte [receive_busy],0
    jmp .return
.bad_pair:
    or word [cs:P_CALLBACK_ERR],4
    mov byte [cs:receive_busy],0
    jmp .return
.bad_pointer:
    or word [cs:P_CALLBACK_ERR],8
    mov byte [cs:receive_busy],0
.return:
    RESTORE_CALLBACK
    retf

; Called only after a complete bounded delivery into our own conventional buffer.
; All fields are checked against the actual request and independently fixed IPs.
validate_reply:
    cmp word [rx_frame+12],0x0608
    jne .bad
    cmp word [rx_frame+14],0x0100
    jne .bad
    cmp word [rx_frame+16],0x0008
    jne .bad
    cmp word [rx_frame+18],0x0406
    jne .bad
    cmp word [rx_frame+20],0x0200
    jne .bad
    cmp dword [rx_frame+28],0x0202000a
    jne .bad
    cmp dword [rx_frame+38],0x0f02000a
    jne .bad
    mov si,rx_frame
    mov di,P_MAC
    mov cx,6
    repe cmpsb
    jne .bad
    mov si,rx_frame+32
    mov di,P_MAC
    mov cx,6
    repe cmpsb
    jne .bad
    mov si,rx_frame+6
    mov di,rx_frame+22
    mov cx,6
    repe cmpsb
    jne .bad
    test byte [rx_frame+22],1
    jnz .bad
    xor al,al
    mov si,rx_frame+22
    mov cx,6
.nonzero:
    or al,[si]
    inc si
    loop .nonzero
    test al,al
    jz .bad
    clc
    ret
.bad:
    stc
    ret

; Compare every surrounding watch byte, and the complete actual transmitted frame.
check_guards:
    xor bx,bx
    mov si,proof_before
    mov al,0xa5
    call check_watch
    mov si,proof_after
    mov al,0x5a
    call check_watch
    mov si,get_mac_before
    mov al,0x31
    call check_watch
    mov si,get_mac_after
    mov al,0x13
    call check_watch
    mov si,tx_before
    mov al,0xc3
    call check_watch
    mov si,tx_after
    mov al,0x3c
    call check_watch
    mov si,rx_before
    mov al,0xa5
    call check_watch
    mov si,rx_after
    mov al,0x5a
    call check_watch
    mov si,stack_before
    mov al,0x69
    call check_watch
    mov si,stack_after
    mov al,0x96
    call check_watch
    test word [P_FLAGS],8
    jz .done
    mov si,tx_frame
    mov di,proof+96
    mov cx,60
.tx_bytes:
    cmpsb
    je .tx_next
    inc bx
.tx_next:
    loop .tx_bytes
.done:
    mov [P_GUARD_ERR],bx
    ret
; Count damaged bytes in one explicitly bounded conventional-memory guard.
check_watch:
    mov cx,16
.next:
    cmp [si],al
    je .same
    inc bx
.same:
    inc si
    loop .next
    ret

; Keep the first actual write/commit returns in the final proof by rewriting only
; this newly created own handle. All final writes/commits/close must succeed to exit0.
save_proof:
    mov ax,0x5b00
    xor cx,cx
    mov dx,proof_path
    call dos_call
    mov [proof+240],ax
    pushf
    pop word [proof+242]
    jc .failed
    mov [file_handle],ax
    mov bx,ax
    mov ah,0x40
    mov cx,256
    mov dx,proof
    call dos_call
    mov [proof+232],ax
    pushf
    pop word [proof+234]
    jc .close_failed
    cmp ax,256
    jne .close_failed
    mov bx,[file_handle]
    mov ah,0x68
    call dos_call
    mov [proof+236],ax
    pushf
    pop word [proof+238]
    jc .close_failed
    mov bx,[file_handle]
    mov ax,0x4200
    xor cx,cx
    xor dx,dx
    call dos_call
    jc .close_failed
    or ax,dx
    jnz .close_failed
    mov bx,[file_handle]
    mov ah,0x40
    mov cx,256
    mov dx,proof
    call dos_call
    jc .close_failed
    cmp ax,256
    jne .close_failed
    mov bx,[file_handle]
    mov ah,0x68
    call dos_call
    jc .close_failed
    mov bx,[file_handle]
    mov ah,0x3e
    call dos_call
    mov [proof+244],ax
    pushf
    pop word [proof+246]
    ret
.close_failed:
    mov bx,[file_handle]
    mov ah,0x3e
    call dos_call
.failed:
    stc
    ret
; Preserve the client's data segments around a genuine DOS service call.
dos_call:
    push ds
    push es
    int 0x21
    pop es
    pop ds
    ret

packet_signature db 'PKT DRVR',0
arp_type db 0x08,0x06
fallback_mac db 0x02,0x44,0x4f,0x53,0x19,0x02
proof_path db 'C:\DOSENIC.BIN',0
have_handle db 0
refusing db 0
got_reply db 0
receive_busy db 0
offered_length dw 0
offered_handle dw 0
delivery_ss dw 0
delivery_sp dw 0
delivery_ds dw 0
delivery_si dw 0
file_handle dw 0xffff

align 2
proof_before times 16 db 0xa5
proof:
    db 'DOSENIC1'
    dw 1,256
    times 256-($-proof) db 0
proof_after times 16 db 0x5a
get_mac_before times 16 db 0x31
get_mac_buffer times 6 db 0
get_mac_after times 16 db 0x13
tx_before times 16 db 0xc3
tx_frame:
    times 6 db 0xff
    db 0x02,0x44,0x4f,0x53,0x19,0x02
    db 0x08,0x06,0x00,0x01,0x08,0x00,6,4,0x00,0x01
    db 0x02,0x44,0x4f,0x53,0x19,0x02,10,0,2,15
    times 6 db 0
    db 10,0,2,2
    times 18 db 0
tx_after times 16 db 0x3c
rx_before times 16 db 0xa5
rx_frame times MAX_FRAME db 0xcc
rx_after times 16 db 0x5a
align 2
stack_before times 16 db 0x69
stack_space times 1024 db 0
stack_top:
stack_after times 16 db 0x96
image_end:
%if image_end-$$+0x100 > 0xff00
%error Own COM image, buffers and stack must remain inside one conventional segment.
%endif
