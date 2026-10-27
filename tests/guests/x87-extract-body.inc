; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual FXTRACT two-result/stack tests and real pending #MF returns inside DOS.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,5
extract_mf_loop:
    fninit
    fldcw [si]
    call extract_fill
    push dword 0x47
    popfd
extract_mf_instruction:
    fxtract
extract_mf_wait:
    fwait
    o32 fnsave [di+108]
    add si,40
    add di,256
    dec bp
    jnz extract_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
extract_loop:
    fninit
    fldcw [si]
    call extract_fill
    o32 fnstenv [seed_env]
    and word [seed_env+4],~0x4700
    test word [si+4],32
    jz .cc
    or word [seed_env+4],0x200
.cc:
    mov ax,[si+4]
    and ax,0xc0
    cmp ax,0x40
    je .less
    cmp ax,0x80
    je .equal
    cmp ax,0xc0
    jne .sticky
    or word [seed_env+4],0x4500
    jmp .sticky
.less:
    or word [seed_env+4],0x100
    jmp .sticky
.equal:
    or word [seed_env+4],0x4000
.sticky:
    test word [si+4],16
    jz .load
    or word [seed_env+4],8
.load:
    o32 fldenv [seed_env]
    test word [si+4],0x100
    jz .target
    ffree st0
.target:
    test word [si+4],0x200
    jz .hole
    ffree st7
.hole:
    test word [si+4],0x400
    jz .flags
    ffree st3
.flags:
    pushfd
    pop eax
    and eax,~0x8d5
    or eax,0x45
    mov [di+112],eax
    push eax
    popfd
extract_instruction:
    fxtract
    pushfd
    pop dword [di+108]
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz extract_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
; Initializes raw source registers and push-space conditions for the FXTRACT case.
extract_fill:
    mov cx,[si+4]
    and cx,15
    jz .done
.loop:
    cmp cx,1
    je .source
    fld tword [si+18]
    jmp .next
.source:
    fld tword [si+8]
.next:
    loop .loop
.done:
    ret
%if PROTECTED
extract_mf_handler:
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
extract_addresses:
    dd extract_instruction
%if PROTECTED
    dd extract_mf_instruction,extract_mf_wait,extract_mf_handler
%else
    dd 0,0,0
%endif
body_end:
