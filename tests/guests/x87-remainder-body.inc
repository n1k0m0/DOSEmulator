; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Real instruction-by-instruction partial reduction loops and actual #MF returns.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,5
remainder_mf_loop:
    fninit
    fldcw [si]
    fld tword [si+18]
    fld tword [si+8]
    test word [si+4],1
    jz .second
    ffree st0
.second:
    test word [si+4],2
    jz .flags
    ffree st1
.flags:
    push dword 0x47
    popfd
remainder_mf_instruction:
    fprem
remainder_mf_wait:
    fwait
    o32 fnsave [di+108]
    add si,40
    add di,256
    dec bp
    jnz remainder_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
remainder_loop:
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
    xor ebx,ebx
remainder_reduce:
    test word [si+2],1
    jnz remainder_nearest_branch
    push dword [di+112]
    popfd
remainder_instruction:
    fprem
    jmp remainder_after_instruction
remainder_nearest_branch:
    push dword [di+112]
    popfd
remainder_nearest_instruction:
    fprem1
remainder_after_instruction:
    pushfd
    pop dword [di+108]
    mov eax,[di+108]
    cmp eax,[di+112]
    je .state
    inc dword [proof+20]
.state:
    inc ebx
    fnstsw ax
    test ax,0x81
    jnz .done
    test word [si+2],2
    jz .done
    test ax,0x400
    jz .done
    cmp ebx,2048
    jb remainder_reduce
    inc dword [proof+20]
.done:
    mov [di+120],ebx
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz remainder_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
%if PROTECTED
remainder_mf_handler:
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
remainder_addresses:
    dd remainder_instruction,remainder_nearest_instruction
%if PROTECTED
    dd remainder_mf_instruction,remainder_mf_wait,remainder_mf_handler
%else
    dd 0,0,0
%endif
body_end:
