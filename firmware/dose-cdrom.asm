; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; DOSECD: original DOS character driver for the fixed secondary-master
; SFF-8020i Rev 2.5 PIO0 device. This is not an MSCDEX redirector.
; The Microsoft 000080010-100-O00-1186 request ABI is implemented below.
; No BIOS, host filesystem, interrupt injection, RDTSC or channel reset is used.
bits 16
cpu 486
org 0

%define DATA_PORT 0170h
%define ERROR_PORT 0171h
%define REASON_PORT 0172h
%define SECTOR_PORT 0173h
%define COUNT_LOW_PORT 0174h
%define COUNT_HIGH_PORT 0175h
%define SELECT_PORT 0176h
%define STATUS_PORT 0177h
%define CONTROL_PORT 0376h
%define POLL_LIMIT 200000

; The extended MSCDEX header is 22 bytes; two ordinary padding bytes follow.
driver_header:
    dd 0ffffffffh
    dw 0c800h
    dw strategy
    dw interrupt
    db 'DOSECD01'
    dw 0
    db 0
    db 1
    dw 0

; Read-only symbol offsets support independent resident-byte/request oracles.
; They do not provide any execution control or host-to-guest back channel.
metadata:
    db 'DOSECDM1'
    dw 1, 22
    dw strategy, interrupt, resident_end
    dw request_pending, request_active, driver_busy
    dw command_counter, packet_counter, sectors_read
    dw last_request_lba, current_lba, destination_pointer, sectors_remaining
    dw packet_buffer, scratch_buffer, transfer_received
    dw last_status, last_reason, last_sense, current_command
    dw read_requested, read_completed

request_pending: dd 0
request_active: dd 0
driver_busy: db 0
driver_installed: db 0
current_command: db 0
command_counter: dd 0
packet_counter: dd 0
sectors_read: dd 0
last_request_lba: dd 0
current_lba: dd 0
destination_pointer: dd 0
sectors_remaining: dw 0
read_requested: dw 0
read_completed: dw 0
head_position: dd 0
control_pointer: dd 0
control_length: dw 0
result_status: dw 0100h
expected_bytes: dw 0
transfer_received: dw 0
phase_bytes: dw 0
last_status: db 0
last_reason: db 0
last_transport: db 0
last_sense: db 0, 0, 0
media_changed: db 0
volume_blocks: dd 0
packet_buffer: times 12 db 0
scratch_buffer: times 2048 db 0

; Save the request address supplied by DOS/MSCDEX without changing its caller.
strategy:
    mov [cs:request_pending], bx
    mov [cs:request_pending+2], es
    retf

; Dispatch one synchronous driver request and restore all caller registers/flags.
interrupt:
    pushf
    pushad
    push ds
    push es
    push fs
    push gs
    cld
    push cs
    pop ds
    les bx, [request_pending]
    cmp byte [driver_busy], 0
    jne .reentrant
    mov byte [driver_busy], 1
    mov [request_active], bx
    mov [request_active+2], es
    mov word [result_status], 0100h
    cmp dword [command_counter], 0ffffffffh
    je .counted
    inc dword [command_counter]
.counted:
    call load_request
    cmp byte [es:bx], 13
    jb .bad_length
    cmp byte [es:bx+1], 0
    jne .bad_unit
    mov al, [es:bx+2]
    mov [current_command], al
    cmp al, 0
    je .init
    cmp al, 3
    je .ioctl_in
    cmp byte [driver_installed], 1
    jne .not_ready
    cmp al, 12
    je .ioctl_out
    cmp al, 080h
    je .read_long
    cmp al, 7
    je .success
    cmp al, 13
    je .success
    cmp al, 14
    je .success
    cmp al, 085h
    je .success                    ; no audio capability: STOP is ignored
    mov ax, 3                      ; unsupported writes/audio/seek/prefetch
    jmp .finish
.init:
    call request_init
    jmp .finish
.ioctl_in:
    call request_ioctl_input
    jmp .finish
.ioctl_out:
    call request_ioctl_output
    jmp .finish
.read_long:
    call request_read_long
    jmp .finish
.bad_length:
    mov ax, 5
    jmp .finish
.bad_unit:
    mov ax, 1
    jmp .finish
.not_ready:
    mov ax, 2
    jmp .finish
.success:
    xor ax, ax
.finish:
    test ax, ax
    jz .write_status
    and ax, 00ffh
    or ax, 08100h
    mov [result_status], ax
.write_status:
    call load_request
    mov ax, [result_status]
    mov [es:bx+3], ax
    mov byte [driver_busy], 0
    jmp .restore
.reentrant:
    ; A nested strategy can replace only the pending pointer, never the active one.
    ; The physical transport remains owned by the outer request.
    mov word [es:bx+3], 0810ch
.restore:
    pop gs
    pop fs
    pop es
    pop ds
    popad
    popf
    retf

; Normalize a conventional request pointer without changing its recorded ABI value.
load_request:
    push eax
    push edx
    les bx, [request_active]
    xor eax, eax
    mov ax, es
    shl eax, 4
    movzx edx, bx
    add eax, edx
    mov bx, ax
    and bx, 15
    shr eax, 4
    mov es, ax
    pop edx
    pop eax
    ret

; Install one fixed-name unit; loading without an inserted medium is legitimate.
request_init:
    call load_request
    cmp byte [es:bx], 23
    jb .length
    mov byte [es:bx+13], 0
    mov byte [es:bx+22], 0
    mov word [es:bx+14], 0
    mov [es:bx+16], cs
    call identify_device
    test ax, ax
    jnz .return
    call read_sense
    test ax, ax
    jnz .return
    mov byte [driver_installed], 1
    mov byte [media_changed], 0
    call load_request
    mov word [es:bx+14], resident_end
    mov [es:bx+16], cs
    xor ax, ax
.return:
    ret
.length:
    mov ax, 5
    ret

; DOS 4402/4403 uses the normal 22-byte envelope; the CD extension may use 26.
; Only the shared pointer/count fields are accessed, before any control-block I/O.
setup_control:
    call load_request
    cmp byte [es:bx], 22
    jb .length
    mov ax, [es:bx+18]
    mov [control_length], ax
    mov word [es:bx+18], 0
    test ax, ax
    jz .length
    mov eax, [es:bx+14]
    mov [control_pointer], eax
    ; This driver's far buffers are conventional, contiguous real-mode spans.
    movzx edx, word [control_pointer+2]
    shl edx, 4
    movzx eax, word [control_pointer]
    add eax, edx
    movzx edx, word [control_length]
    add edx, eax
    cmp edx, 0100000h
    ja .length
    call load_control
    xor ax, ax
    ret
.length:
    mov ax, 5
    ret

; Address a small IOCTL block using a paragraph-normalized far pointer.
load_control:
    push eax
    push edx
    movzx eax, word [control_pointer+2]
    shl eax, 4
    movzx edx, word [control_pointer]
    add eax, edx
    mov di, ax
    and di, 15
    shr eax, 4
    mov es, ax
    pop edx
    pop eax
    ret

; Return exact control-block counts through the original IOCTL request.
control_success:
    push ax
    call load_request
    mov [es:bx+18], cx
    pop ax
    ret

; Implement the supported Microsoft input-IOCTL subset using actual ATAPI state.
request_ioctl_input:
    call setup_control
    test ax, ax
    jnz .return
    mov al, [es:di]
    cmp al, 0
    je .header
    cmp byte [driver_installed], 1
    jne .not_ready
    cmp al, 6
    je .status
    cmp al, 7
    je .sector_size
    cmp al, 8
    je .volume
    cmp al, 9
    je .media
    mov ax, 3
    ret
.header:
    cmp word [control_length], 5
    jb .length
    mov word [es:di+1], driver_header
    mov [es:di+3], cs
    mov cx, 5
    xor ax, ax
    jmp control_success
.status:
    cmp word [control_length], 5
    jb .length
    call read_device_status
    test ax, ax
    jnz .return
    call load_control
    movzx eax, dl
    mov [es:di+1], eax
    mov cx, 5
    xor ax, ax
    jmp control_success
.sector_size:
    cmp word [control_length], 4
    jb .length
    cmp byte [es:di+1], 0
    jne .unknown
    mov word [es:di+2], 2048
    mov cx, 4
    xor ax, ax
    jmp control_success
.volume:
    cmp word [control_length], 5
    jb .length
    call read_capacity
    test ax, ax
    jnz .return
    call load_control
    mov eax, [volume_blocks]
    mov [es:di+1], eax
    mov cx, 5
    xor ax, ax
    jmp control_success
.media:
    cmp word [control_length], 2
    jb .length
    call clear_packet
    mov word [expected_bytes], 0
    call packet_command           ; TEST UNIT READY, not a synthetic timer heuristic
    cmp ax, 0
    je .known_media
    cmp ax, 2
    je .unknown_media
    cmp ax, 15
    jne .return
.unknown_media:
    xor dl, dl
    jmp .media_latch
.known_media:
    mov dl, 1
.media_latch:
    cmp byte [media_changed], 0
    je .media_store
    mov dl, 0ffh
    mov byte [media_changed], 0
.media_store:
    call load_control
    mov [es:di+1], dl
    mov cx, 2
    xor ax, ax
    jmp control_success
.not_ready:
    mov ax, 2
    ret
.unknown:
    mov ax, 3
    ret
.length:
    mov ax, 5
.return:
    ret

; Implement lock, unlock/eject, device reset and close without touching its peer HDD.
request_ioctl_output:
    call setup_control
    test ax, ax
    jnz .return
    mov al, [es:di]
    cmp al, 0
    je .eject
    cmp al, 1
    je .lock
    cmp al, 2
    je .reset
    cmp al, 5
    je .close
    mov ax, 3
    ret
.eject:
    call clear_packet
    mov byte [packet_buffer], 01eh
    mov word [expected_bytes], 0
    call packet_command           ; Microsoft EJECT first unlocks the door
    test ax, ax
    jnz .return
    call clear_packet
    mov byte [packet_buffer], 01bh
    mov byte [packet_buffer+4], 2
    mov word [expected_bytes], 0
    call packet_command
    test ax, ax
    jnz .return
    mov byte [media_changed], 1
    mov cx, 1
    jmp control_success
.lock:
    cmp word [control_length], 2
    jb .length
    cmp byte [es:di+1], 1
    ja .unknown
    mov dl, [es:di+1]
    call clear_packet
    mov byte [packet_buffer], 01eh
    mov [packet_buffer+4], dl
    mov word [expected_bytes], 0
    call packet_command
    test ax, ax
    jnz .return
    mov cx, 2
    jmp control_success
.reset:
    call reset_device
    test ax, ax
    jnz .return
    call identify_device
    test ax, ax
    jnz .return
    call read_sense
    test ax, ax
    jnz .return
    mov cx, 1
    jmp control_success
.close:
    call clear_packet
    mov byte [packet_buffer], 01bh
    mov byte [packet_buffer+4], 3
    mov word [expected_bytes], 0
    call packet_command
    test ax, ax
    jnz .return
    mov byte [media_changed], 1
    mov cx, 1
    jmp control_success
.length:
    mov ax, 5
    ret
.unknown:
    mov ax, 3
.return:
    ret

; Read complete cooked blocks; a failed block never increments the returned count.
request_read_long:
    call load_request
    cmp byte [es:bx], 27
    jb .length
    mov cx, [es:bx+18]
    mov [read_requested], cx
    mov [sectors_remaining], cx
    mov word [read_completed], 0
    mov word [es:bx+18], 0
    cmp byte [es:bx+13], 0
    jne .unknown
    cmp byte [es:bx+24], 0
    jne .unknown
    mov eax, [es:bx+20]
    mov [last_request_lba], eax
    mov [current_lba], eax
    mov eax, [es:bx+14]
    mov [destination_pointer], eax
    test cx, cx
    jz .empty
    ; Validate both full LBA arithmetic and the complete conventional transfer span.
    mov eax, [current_lba]
    movzx edx, cx
    dec edx
    add eax, edx
    jc .sector_error
    movzx eax, word [destination_pointer+2]
    shl eax, 4
    movzx edx, word [destination_pointer]
    add eax, edx
    movzx edx, cx
    shl edx, 11
    add eax, edx
    cmp eax, 0100000h
    ja .length
.next_sector:
    call clear_packet
    mov byte [packet_buffer], 028h
    mov eax, [current_lba]
    bswap eax
    mov [packet_buffer+2], eax
    mov byte [packet_buffer+8], 1
    mov word [expected_bytes], 2048
    call packet_command
    test ax, ax
    jnz .return
    cmp word [transfer_received], 2048
    jne .read_fault
    call copy_sector
    mov eax, [current_lba]
    mov [head_position], eax
    inc dword [current_lba]
    inc word [read_completed]
    dec word [sectors_remaining]
    cmp dword [sectors_read], 0ffffffffh
    je .sector_counted
    inc dword [sectors_read]
.sector_counted:
    call load_request
    mov ax, [read_completed]
    mov [es:bx+18], ax
    cmp word [sectors_remaining], 0
    jne .next_sector
.empty:
    xor ax, ax
    ret
.length:
    mov ax, 5
    ret
.unknown:
    mov ax, 3
    ret
.sector_error:
    mov ax, 8
    ret
.read_fault:
    mov ax, 11
.return:
    ret

; Store every ATA word exactly once, splitting only a far-buffer word at FFFFh.
copy_sector:
    les di, [destination_pointer]
    mov si, scratch_buffer
    mov cx, 1024
.next_word:
    lodsw
    cmp di, 0ffffh
    je .split
    mov [es:di], ax
    add di, 2
    jnc .advance
    mov dx, es
    add dx, 01000h
    mov es, dx
    jmp .advance
.split:
    mov [es:di], al
    xor di, di
    mov dx, es
    add dx, 01000h
    mov es, dx
    mov [es:di], ah
    inc di
.advance:
    loop .next_word
    mov [destination_pointer], di
    mov [destination_pointer+2], es
    ret

; Clear only the twelve-byte CDB; transfer data/history are independent diagnostics.
clear_packet:
    push ax
    push cx
    push di
    xor ax, ax
    mov cx, 6
    mov di, packet_buffer
.next:
    mov [di], ax
    add di, 2
    loop .next
    pop di
    pop cx
    pop ax
    ret

; Select the fixed target and suppress channel INTRQ while synchronous polling owns it.
select_device:
    mov dx, CONTROL_PORT
    mov al, 2
    out dx, al
    mov dx, SELECT_PORT
    mov al, 0a0h
    out dx, al
    mov dx, CONTROL_PORT
    in al, dx
    in al, dx
    in al, dx
    in al, dx
    ret

; Bound every busy wait by guest instruction count; no privileged clock is required.
wait_not_busy:
    mov ecx, POLL_LIMIT
    mov dx, CONTROL_PORT
.poll:
    in al, dx
    mov [last_status], al
    cmp al, 0ffh
    je .timeout
    test al, 080h
    jz .ready
    dec ecx
    jnz .poll
.timeout:
    stc
    ret
.ready:
    clc
    ret

; Verify the particular installed PIO0/PACKET12 CD-ROM interface, even without a disc.
identify_device:
    call select_device
    call wait_not_busy
    jc .not_ready
    test al, 8
    jnz .not_ready
    mov dx, STATUS_PORT
    mov al, 0a1h
    out dx, al
    call wait_not_busy
    jc .not_ready
    test al, 1
    jnz .not_ready
    test al, 8
    jz .not_ready
    mov dx, STATUS_PORT
    in al, dx                    ; acknowledge the real IDENTIFY interrupt latch
    mov dx, DATA_PORT
    mov di, scratch_buffer
    mov cx, 256
.words:
    in ax, dx
    mov [di], ax
    add di, 2
    loop .words
    cmp word [scratch_buffer], 085a0h
    jne .not_ready
    mov ax, [scratch_buffer+49*2]
    and ax, 0300h
    cmp ax, 0200h
    jne .not_ready
    cmp word [scratch_buffer+51*2], 0
    jne .not_ready
    xor ax, ax
    ret
.not_ready:
    mov ax, 2
    ret

; Device Reset 08h affects this selected PACKET target, never channel SRST/the HDD.
reset_device:
    call select_device
    mov dx, STATUS_PORT
    mov al, 8
    out dx, al
    call wait_not_busy
    jc .not_ready
    test al, 8
    jnz .not_ready
    xor ax, ax
    ret
.not_ready:
    mov ax, 2
    ret

; Execute one CDB and translate CHECK CONDITION through a real REQUEST SENSE.
packet_command:
    call packet_raw
    jnc .success
    cmp byte [last_transport], 1
    jne .transport_error
    call read_sense
    test ax, ax
    jnz .return
    mov al, [last_sense]
    cmp al, 6
    je .attention
    cmp al, 2
    je .not_ready
    cmp al, 3
    je .read_fault
    cmp al, 5
    jne .general
    cmp byte [last_sense+1], 021h
    je .sector_error
    cmp byte [last_sense+1], 020h
    je .unknown
    cmp byte [last_sense+1], 024h
    je .unknown
    jmp .general
.attention:
    cmp byte [last_sense+1], 028h
    jne .not_ready               ; reset/power-on 29h is not a fabricated disc change
    mov byte [media_changed], 1
    mov ax, 15
    ret
.not_ready:
    mov ax, 2
    ret
.read_fault:
    mov ax, 11
    ret
.sector_error:
    mov ax, 8
    ret
.unknown:
    mov ax, 3
    ret
.general:
    mov ax, 12
    ret
.transport_error:
    ; Cancel a broken/expired local transport so later calls cannot inherit DRQ data.
    call reset_device
    mov ax, 2
.return:
    ret
.success:
    xor ax, ax
    ret

; Read fixed-format sense and retain its three independently observable error bytes.
read_sense:
    call clear_packet
    mov byte [packet_buffer], 3
    mov byte [packet_buffer+4], 18
    mov word [expected_bytes], 18
    call packet_raw
    jc .failure
    cmp word [transfer_received], 18
    jne .failure
    mov al, [scratch_buffer+2]
    and al, 15
    mov [last_sense], al
    mov al, [scratch_buffer+12]
    mov [last_sense+1], al
    mov al, [scratch_buffer+13]
    mov [last_sense+2], al
    xor ax, ax
    ret
.failure:
    mov ax, 12
    ret

; Implement PACKET12 command/data/status phases with one word-port read per datum.
packet_raw:
    mov word [transfer_received], 0
    mov byte [last_transport], 0
    call select_device
    call wait_not_busy
    jc .timeout
    test al, 8
    jnz .protocol
    mov dx, ERROR_PORT
    xor al, al
    out dx, al
    mov dx, REASON_PORT
    out dx, al
    mov dx, SECTOR_PORT
    out dx, al
    mov dx, COUNT_LOW_PORT
    out dx, al
    inc dx
    mov al, 8
    out dx, al
    mov dx, STATUS_PORT
    mov al, 0a0h
    out dx, al
    cmp dword [packet_counter], 0ffffffffh
    je .packet_counted
    inc dword [packet_counter]
.packet_counted:
    call wait_not_busy
    jc .timeout
    test al, 1
    jnz .check
    test al, 8
    jz .protocol
    mov dx, REASON_PORT
    in al, dx
    and al, 3
    mov [last_reason], al
    cmp al, 1
    jne .protocol
    mov dx, STATUS_PORT
    in al, dx
    mov dx, DATA_PORT
    mov si, packet_buffer
    mov cx, 6
    rep outsw
.next_phase:
    call wait_not_busy
    jc .timeout
    test al, 1
    jnz .check
    test al, 8
    jz .completion
    mov dx, REASON_PORT
    in al, dx
    and al, 3
    mov [last_reason], al
    cmp al, 2
    jne .protocol
    mov dx, COUNT_LOW_PORT
    in al, dx
    mov bl, al
    inc dx
    in al, dx
    mov bh, al
    test bx, bx
    jz .protocol
    mov [phase_bytes], bx
    mov ax, [transfer_received]
    add ax, bx
    jc .protocol
    cmp ax, 2048
    ja .protocol
    cmp ax, [expected_bytes]
    ja .protocol
    mov dx, STATUS_PORT
    in al, dx                    ; acknowledge DRQ without consuming a data word
    mov di, scratch_buffer
    add di, [transfer_received]
    mov dx, DATA_PORT
.data_word:
    in ax, dx
    mov [di], al
    inc di
    dec bx
    jz .data_done                 ; never store an odd final high-pad byte
    mov [di], ah
    inc di
    dec bx
    jnz .data_word
.data_done:
    mov ax, [phase_bytes]
    add [transfer_received], ax
    jmp .next_phase
.completion:
    mov dx, REASON_PORT
    in al, dx
    and al, 3
    mov [last_reason], al
    cmp al, 3
    jne .protocol
    mov dx, STATUS_PORT
    in al, dx
    mov [last_status], al
    test al, 081h
    jnz .protocol
    clc
    ret
.check:
    mov dx, STATUS_PORT
    in al, dx
    mov [last_status], al
    mov byte [last_transport], 1
    stc
    ret
.timeout:
    mov byte [last_transport], 2
    stc
    ret
.protocol:
    mov byte [last_transport], 3
    stc
    ret

; Derive the accessible HSG block count from READ CAPACITY, never from a fake MSF bias.
read_capacity:
    call clear_packet
    mov byte [packet_buffer], 025h
    mov word [expected_bytes], 8
    call packet_command
    test ax, ax
    jnz .return
    cmp word [transfer_received], 8
    jne .failure
    mov eax, [scratch_buffer+4]
    bswap eax
    cmp eax, 2048
    jne .failure
    mov eax, [scratch_buffer]
    bswap eax
    inc eax
    jz .failure                   ; the DOS count cannot describe 2^32 blocks
    mov [volume_blocks], eax
    xor ax, ax
.return:
    ret
.failure:
    mov ax, 12
    ret

; Fetch actual tray/lock state; only the two implemented Microsoft status bits are set.
read_device_status:
    call .mode_sense
    test ax, ax
    jz .decode
    cmp byte [last_sense], 6
    jne .return
    call .mode_sense               ; inquiry-like status survives a cleared UA once
    test ax, ax
    jnz .return
.decode:
    cmp word [transfer_received], 32
    jne .failure
    xor dl, dl
    cmp byte [scratch_buffer+2], 071h
    jne .door_known
    or dl, 1
.door_known:
    test byte [scratch_buffer+14], 2
    jnz .locked
    or dl, 2
.locked:
    xor ax, ax
.return:
    ret
.mode_sense:
    call clear_packet
    mov byte [packet_buffer], 05ah
    mov byte [packet_buffer+2], 02ah
    mov byte [packet_buffer+8], 32
    mov word [expected_bytes], 32
    call packet_command
    ret
.failure:
    mov ax, 12
    ret

align 16, db 0
resident_end:
