; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual FSQRT and pending-image capture, loaded and executed by DOS.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,3
sqrt_mf_loop:
    fninit
    fldcw [si]
    fld tword [si+8]
    push dword 0x47
    popfd
sqrt_mf_instruction:
    fsqrt
sqrt_mf_wait:
    fwait
    o32 fnsave [di+108]
    add si,40
    add di,256
    dec bp
    jnz sqrt_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
sqrt_loop:
    fninit
    fldcw [si]
    fld tword [si+18]
    fld tword [si+8]
    o32 fnstenv [seed_env]
    and word [seed_env+4],~0x4700
    or word [seed_env+4],0x0200
    mov ax,[si+4]
    and ax,12
    cmp ax,4
    je .seeded
    cmp ax,8
    je .less
    cmp ax,12
    je .equal
    or word [seed_env+4],0x4500
    jmp .seeded
.less:
    or word [seed_env+4],0x0100
    jmp .seeded
.equal:
    or word [seed_env+4],0x4000
.seeded:
    test word [si+4],16
    jz .load
    or word [seed_env+4],4
.load:
    o32 fldenv [seed_env]
    test word [si+4],1
    jz .source
    ffree st0
.source:
    test word [si+4],2
    jz .flags
    ffree st1
.flags:
    pushfd
    pop eax
    and eax,~0x8d5
    or eax,0x45
    mov [di+112],eax
    push eax
    popfd
sqrt_instruction:
    fsqrt
    pushfd
    pop dword [di+108]
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz sqrt_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
%if PROTECTED
sqrt_mf_handler:
    push eax
    mov eax,[ss:esp+4]
    mov [di+216],eax
    mov eax,[ss:esp+8]
    mov [di+220],eax
    mov eax,[ss:esp+12]
    mov [di+224],eax
    o32 fnsave [di]         ; Capture the still-pending exact register image.
    o32 frstor [di]         ; Re-establish it before the explicit repair.
    fnclex
    inc dword [proof+300]
    pop eax
    iretd
%endif
sqrt_addresses:
    dd sqrt_instruction
%if PROTECTED
    dd sqrt_mf_instruction,sqrt_mf_wait,sqrt_mf_handler
%else
    dd 0,0,0
%endif
body_end:
