; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own APM 1.1 CPU-idle firmware. All services execute genuine guest instructions.
; Intel/Microsoft APM BIOS interface, Sept1993 with Feb1996 compatibility notes.
; No timer/clock throttling, battery, automatic standby, suspend or power-off is advertised.
; Off/standby/suspend requests fail honestly. CPU Idle uses STI/HLT until a real IRQ.
; EBDA 180h..185h is disjoint from mouse/video, AH87's 100h..142h and the
; ATA/FDC bounce sector at 200h..3FFh. These bytes are saved as ordinary guest RAM.
%define apm_connection 0x180
%define apm_disabled   0x181
%define apm_disengaged 0x182
%define apm_version    0x184

; The real interface resolves the live EBDA, including a legitimate DOS relocation.
apm_real:
    enter_service
    call bios_ebda_segment
    mov ds,ax
    mov eax,dword [ss:bp-4]
    call apm_service_real
    mov dword [ss:bp-4],eax
    mov dword [ss:bp-8],ecx
    mov dword [ss:bp-12],edx
    mov dword [ss:bp-16],ebx
    mov dword [ss:bp-28],esi
    mov dword [ss:bp-32],edi
    jc .failed
    and rFLAGS,0xfffe
    jmp .done
.failed:
    or rFLAGS,1
.done:
    leave_service

; Each entry owns its return width. Specified consecutive descriptors provide DS,
; without assuming the caller's segment registers or switching the caller's stack.
apm_pm16:
    push ds
    push ax
    mov ax,cs
    add ax,8
    mov ds,ax
    pop ax
    call apm_service_pm16
    pop ds
    retf

bits 32
apm_pm32:
    push ds
    push eax
    mov ax,cs
    add ax,16
    mov ds,ax
    pop eax
    call apm_service_pm32
    pop ds
    retf
bits 16

; Expands the same contract in each actual caller width; no host BIOS interception.
%macro apm_services 2
%1:
    cmp ah,0x53
    jne %%unsupported
    cmp al,0
    je %%check
    cmp al,3
    jbe %%connect
    cmp al,0x0a
    je %%status
    cmp al,0x0c
    je %%state
    cmp byte [apm_connection],0
    je %%not_connected
    cmp al,4
    je %%disconnect
    cmp al,8
    je %%enable
    cmp al,0x0f
    je %%engage
    cmp al,9
    je %%defaults
    cmp al,0x0e
    je %%version
    cmp byte [apm_disengaged],0
    jne %%not_engaged
    cmp al,5
    je %%idle
    cmp al,6
    je %%ok
    cmp al,7
    je %%set_state
    cmp al,0x0b
    je %%no_event
    jmp %%unsupported
%%check:
%if %2 != 0
    jmp %%unsupported
%endif
    test bx,bx
    jnz %%bad_device
    mov ax,0x0101
    mov bx,0x504d
    mov cx,3
    cmp byte [apm_disabled],0
    je %%check_engaged
    or cx,8
%%check_engaged:
    cmp byte [apm_disengaged],0
    je %%check_done
    or cx,16
%%check_done:
    clc
    ret
%%connect:
%if %2 != 0
    jmp %%unsupported
%endif
    test bx,bx
    jnz %%bad_device
    cmp byte [apm_connection],0
    jne %%already
    mov [apm_connection],al
    mov word [apm_version],0x0100
    cmp al,1
    je %%ok
    cmp al,2
    je %%connect16
    mov ax,0xf000
    mov ebx,apm_pm32
    mov cx,0xf000
    mov dx,ds
    mov esi,0xa000a000
    mov di,0x0400
    clc
    ret
%%connect16:
    mov ax,0xf000
    mov bx,apm_pm16
    mov cx,ds
    mov si,0xa000
    mov di,0x0400
    clc
    ret
%%already:
    mov ah,2
    cmp byte [apm_connection],1
    je %%error
    mov ah,5
    cmp byte [apm_connection],2
    je %%error
    mov ah,7
    jmp %%error
%%disconnect:
    test bx,bx
    jnz %%bad_device
    mov byte [apm_connection],0
    mov word [apm_version],0x0100
    jmp %%ok
%%enable:
    cmp bx,1
    je %%enable_id
    cmp bx,0xffff
    jne %%bad_device
%%enable_id:
    cmp cx,1
    ja %%bad_parameter
    mov [apm_disabled],cl
    xor byte [apm_disabled],1
    jmp %%ok
%%engage:
    cmp byte [apm_disabled],0
    jne %%disabled
    cmp word [apm_version],0x0101
    jb %%unsupported
    cmp bx,1
    jne %%bad_device
    cmp cx,1
    ja %%bad_parameter
    mov [apm_disengaged],cl
    xor byte [apm_disengaged],1
    jmp %%ok
%%defaults:
    cmp bx,1
    je %%defaults_id
    cmp bx,0xffff
    jne %%bad_device
%%defaults_id:
    mov byte [apm_disabled],0
    mov byte [apm_disengaged],0
    jmp %%ok
%%version:
    test bx,bx
    jnz %%bad_device
    cmp cx,0x0100
    je %%version_ok
    cmp cx,0x0101
    jne %%bad_parameter
%%version_ok:
    mov [apm_version],cx
    mov ax,cx
    clc
    ret
%%idle:
    cmp byte [apm_disabled],0
    jne %%ok
%if %2 = 32
    pushfd
%else
    pushf
%endif
    sti
    hlt
%if %2 = 32
    popfd
%else
    popf
%endif
    jmp %%ok
%%set_state:
    cmp bx,1
    jne %%bad_device
    cmp byte [apm_disabled],0
    jne %%disabled
    test cx,cx
    jz %%ok
    cmp cx,3
    ja %%bad_parameter
    mov ah,0x60
    jmp %%error
%%status:
    cmp bx,1
    jne %%bad_device
    mov bx,0x01ff
    mov cl,0xff
    cmp byte [apm_connection],0
    je %%no_battery
    cmp word [apm_version],0x0101
    jb %%status_done
%%no_battery:
    mov ch,0x80
%%status_done:
    mov dx,0xffff
    jmp %%ok
%%state:
    cmp bx,1
    jne %%bad_device
    cmp byte [apm_disabled],0
    jne %%disabled
    xor cx,cx
    jmp %%ok
%%no_event:
    mov ah,0x80
    jmp %%error
%%bad_device:
    mov ah,9
    jmp %%error
%%bad_parameter:
    mov ah,0x0a
    jmp %%error
%%not_connected:
    mov ah,3
    jmp %%error
%%not_engaged:
    mov ah,0x0b
    jmp %%error
%%disabled:
    mov ah,1
    jmp %%error
%%unsupported:
    mov ah,0xff
%%error:
    stc
    ret
%%ok:
    xor ah,ah
    clc
    ret
%endmacro

apm_services apm_service_real,0
apm_services apm_service_pm16,16
bits 32
apm_services apm_service_pm32,32
bits 16
