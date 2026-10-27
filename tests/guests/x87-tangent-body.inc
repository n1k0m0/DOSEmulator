; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Both real tangent opcodes and pending images, executed by DOS.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,MF_COUNT
tangent_mf_loop:
    fninit
    fldcw [si]
    fld tword [si+18]
    fld tword [si+8]
    test word [si+4],32
    jz .not_occupied
    fldz
    fincstp
.not_occupied:
    test word [si+4],1
    jz .second
    ffree st0
.second:
    test word [si+4],2
    jz .dispatch
    ffree st1
.dispatch:
    cmp word [si+2],0
    jne tangent_mf_plus
    push dword 0x47
    popfd
tangent_mf_instruction_x:
    fptan
    jmp tangent_mf_wait
tangent_mf_plus:
    push dword 0x47
    popfd
tangent_mf_instruction_p1:
    fptan
tangent_mf_wait:
    fwait
    o32 fnsave [di+108]
    add si,40
    add di,256
    dec bp
    jnz tangent_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
tangent_loop:
    fninit
    fldcw [si]
    fld tword [si+18]
    fld tword [si+8]
    test word [si+4],32
    jz .not_occupied
    fldz
    fincstp
.not_occupied:
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
    jz .second
    ffree st0
.second:
    test word [si+4],2
    jz .flags
    ffree st1
.flags:
    pushfd
    pop eax
    and eax,~0x8d5
    or eax,0x45
    mov [di+112],eax
    cmp word [si+2],0
    jne tangent_plus
    push eax
    popfd
tangent_instruction_x:
    fptan
    jmp tangent_capture
tangent_plus:
    push eax
    popfd
tangent_instruction_p1:
    fptan
tangent_capture:
    pushfd
    pop dword [di+108]
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz tangent_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
%if PROTECTED
tangent_mf_handler:
    push eax
    mov eax,[ss:esp+4]
    mov [di+216],eax
    mov eax,[ss:esp+8]
    mov [di+220],eax
    mov eax,[ss:esp+12]
    mov [di+224],eax
    o32 fnsave [di]
    o32 frstor [di]
    fnclex
    inc dword [proof+300]
    pop eax
    iretd
%endif
tangent_addresses:
    dd tangent_instruction_x,tangent_instruction_p1
%if PROTECTED
    dd tangent_mf_instruction_x,tangent_mf_instruction_p1,tangent_mf_wait,tangent_mf_handler
%else
    dd 0,0,0,0
%endif
body_end:
