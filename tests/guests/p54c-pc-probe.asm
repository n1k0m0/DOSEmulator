; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own full P54C DOS guest with independent CPU proof: raw extended state, active sound/ports, private file writes and real DOS return.
bits 16
cpu pentium
org 100h
; Enter an own DOS process, retain real stacks and prepare resumable device operations.
start:
    push cs
    pop ds
    mov sp,stack_top
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    call cpu_probe
    fninit
    fld tword [extended]
    fldpi
    mov si,begin_text
    call print
    mov dx,226h
    mov al,1
    out dx,al
    ; Hold reset over the documented3us minimum at the fixed90MHz/four-cycle test clock.
    mov cx,128
.delay: loop .delay
    xor al,al
    out dx,al
    mov dx,22eh
.reset: in al,dx
    test al,80h
    jz .reset
    mov dx,22ah
    in al,dx
    ; Start an 8-bit 512-component PCM stream through actual ISA DMA1.
    mov al,5
    out 0ah,al
    xor al,al
    out 0ch,al
    xor eax,eax
    mov ax,cs
    shl eax,4
    add eax,samples
    mov ebx,eax
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
    ; Enable one timed OPL3 voice and a timer through real byte ports.
    mov dx,222h
    mov al,5
    out dx,al
    inc dx
    mov al,1
    out dx,al
    mov si,fm_values
    mov cx,11
.fm:
    lodsw
    mov dx,220h
    out dx,al
    inc dx
    mov al,ah
    out dx,al
    loop .fm
    ; Begin an actual UART frame and an MPU-UART frame before the snapshot cut.
    mov dx,3fbh
    mov al,80h
    out dx,al
    mov dx,3f8h
    mov al,12
    out dx,al
    inc dx
    xor al,al
    out dx,al
    mov dx,3fbh
    mov al,3
    out dx,al
    mov dx,3f8h
    mov al,69h
    out dx,al
    mov dx,331h
    mov al,3fh
    out dx,al
.mpu:
    in al,dx
    test al,80h
    jnz .mpu
    dec dx
    in al,dx
    cmp al,0feh
    jne failed
    mov al,90h
    out dx,al
    mov dx,201h
    out dx,al
    mov si,cut_text
    call print
    xor ax,ax
    int 16h
    ; Force a later tick boundary while the saved PCM, RC pulse and frame deadlines run.
    mov ah,0
    int 1ah
    mov bx,dx
.wait:
    mov ah,0
    int 1ah
    sub dx,bx
    cmp dx,3
    jb .wait
    call cpu_future
    fstp tword [proof+8]
    fstp tword [proof+18]
    fnstcw [proof+28]
    fnstsw [proof+30]
    mov dx,filename
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,proof
    mov cx,32
    mov ah,40h
    int 21h
    jc failed
    cmp ax,32
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov dx,cpu_filename
    xor cx,cx
    mov ah,3ch
    int 21h
    jc failed
    mov bx,ax
    mov dx,cpu_proof
    mov cx,cpu_proof_end-cpu_proof
    mov ah,40h
    int 21h
    jc failed
    cmp ax,cpu_proof_end-cpu_proof
    jne failed
    mov ah,3eh
    int 21h
    jc failed
    mov si,done_text
    call print
    mov ax,4c00h
    int 21h
failed:
    mov si,failed_text
    call print
    mov ax,4c01h
    int 21h
; Send one DSP command only after its real busy latch permits the byte.
dsp:
    push ax
    mov dx,22ch
.busy:
    in al,dx
    test al,80h
    jnz .busy
    pop ax
    out dx,al
    ret
; Print an own string through DOS; DOSEBios mirrors the resulting console bytes once to E9.
print:
    pushad
.next:
    lodsb
    test al,al
    jz .out
    mov dl,al
    mov ah,2
    int 21h
    jmp .next
.out:
    popad
    ret
; Exercise actual Pentium instructions without changing the existing DOS monitor's privilege rules.
cpu_probe:
    pushad
    pushfd
    pop eax
    mov [id_before],eax
    xor eax,200000h
    push eax
    popfd
    pushfd
    pop eax
    mov [id_toggled],eax
    push dword [id_before]
    popfd
    pushfd
    pop eax
    mov [id_restored],eax
    xor eax,eax
pc_cpuid0:
    cpuid
    mov [max_leaf],eax
    mov [vendor],ebx
    mov [vendor+4],edx
    mov [vendor+8],ecx
    mov eax,1
pc_cpuid1:
    cpuid
    mov [signature],eax
    mov [features],edx
    cmp eax,524h
    jne failed
    cmp edx,135h
    jne failed
    mov eax,76543210h
    mov edx,0fedcba98h
    mov ebx,89abcdefh
    mov ecx,1234567h
pc_cx8_match:
    lock cmpxchg8b [cx8_slot]
    setz byte [cx8_match_zf]
    jnz failed
    xor eax,eax
    xor edx,edx
pc_cx8_miss:
    lock cmpxchg8b [cx8_slot]
    setz byte [cx8_miss_zf]
    jz failed
    mov [cx8_miss_value],eax
    mov [cx8_miss_value+4],edx
    smsw ax
    and ax,1
    mov [cpu_mode],ax
    jnz cpu_monitored
    mov eax,cr4
    mov [saved_cr4],eax
    mov eax,12
    mov cr4,eax
    mov ecx,10h
    mov eax,0ffffffe0h
    mov edx,12345678h
pc_wrmsr:
    wrmsr
pc_rdmsr:
    rdmsr
    mov [msr_read],eax
    mov [msr_read+4],edx
pc_rdtsc:
    rdtsc
    mov [tsc_read],eax
    mov [tsc_read+4],edx
cpu_monitored:
    popad
    ret
cpu_probe_end:
; Sample the same virtual counter after the actual resumed timer/DMA work in permitted real mode.
cpu_future:
    pushad
    cmp word [cpu_mode],0
    jne cpu_future_done
pc_future_tsc:
    rdtsc
    mov [future_tsc],eax
    mov [future_tsc+4],edx
    mov eax,[saved_cr4]
    mov cr4,eax
cpu_future_done:
    popad
    ret
cpu_proof:
    db 'P54CPC01'
cpu_mode: dw 0
id_before: dd 0
id_toggled: dd 0
id_restored: dd 0
max_leaf: dd 0
vendor: times 12 db 0
signature: dd 0
features: dd 0
cx8_slot: dq 0fedcba9876543210h
cx8_miss_value: dq 0
cx8_match_zf: db 0
cx8_miss_zf: db 0
saved_cr4: dd 0
msr_read: dq 0
tsc_read: dq 0
future_tsc: dq 0
    db 'P54CEND1'
cpu_proof_end:
cpu_filename: db 'C:\P54C.DAT',0

begin_text: db 13,10,'SNAPSHOT-BEGIN',13,10,0
cut_text: db 13,10,'SNAPSHOT-CUT',13,10,0
done_text: db 13,10,'SNAPSHOT-DONE',13,10,0
failed_text: db 13,10,'SNAPSHOT-FAILED',13,10,0
filename: db 'C:\SNAP.DAT',0
extended: dq 0fedcba9876543210h
          dw 4001h
fm_values: dw 0120h,0123h,1040h,0043h,0f060h,0f063h,0780h,0783h,30c0h,98a0h,31b0h
proof: db 'SNAPRAW1'
       times 24 db 0
align 16
samples: times 512 db 0c0h
         times 2048 db 0
stack_top:
oracle_table:
    db 'P54META1'
    dw cpu_proof,cpu_proof_end,cpu_probe,cpu_probe_end,cx8_slot
    dw pc_cpuid0,pc_cpuid1,pc_cx8_match,pc_cx8_miss,pc_wrmsr,pc_rdmsr,pc_rdtsc,pc_future_tsc
    dw proof
program_end:
