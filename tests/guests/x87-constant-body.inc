; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual constant loads, full-stack overflow and pending image capture inside DOS.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,3
constant_mf_loop:
    fninit
    fldcw [si]
    mov cx,8
    call constant_fill
    mov dword [di+112],0x47
    call constant_execute
constant_mf_wait:
    fwait
    o32 fnsave [di+108]
    add si,40
    add di,256
    dec bp
    jnz constant_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
constant_loop:
    fninit
    fldcw [si]
    mov cx,[si+4]
    and cx,15
    call constant_fill
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
    or word [seed_env+4],4
.load:
    o32 fldenv [seed_env]
    pushfd
    pop eax
    and eax,~0x8d5
    or eax,0x45
    mov [di+112],eax
    call constant_execute
    pushfd
    pop dword [di+108]
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz constant_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
; Initializes the controlled x87 stack before the constant-push case.
constant_fill:
    jcxz .done
.loop:
    test cx,1
    jnz .odd
    fld tword [si+8]
    jmp .next
.odd:
    fld tword [si+18]
.next:
    loop .loop
.done:
    ret
; Executes the selected actual x87 constant-load instruction.
constant_execute:
    mov ax,[si+2]
%assign OP 0
%rep 6
    cmp ax,OP
    je constant_prepare %+ OP
%assign OP OP+1
%endrep
    jmp constant_prepare6
%macro constant 2
constant_prepare%1:
    push dword [di+112]
    popfd
constant_instruction%1:
    %2
    ret
%endmacro
    constant 0,fld1
    constant 1,fldl2t
    constant 2,fldl2e
    constant 3,fldpi
    constant 4,fldlg2
    constant 5,fldln2
    constant 6,fldz
%if PROTECTED
constant_mf_handler:
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
constant_addresses:
    dd constant_instruction0,constant_instruction1,constant_instruction2,constant_instruction3
    dd constant_instruction4,constant_instruction5,constant_instruction6
%if PROTECTED
    dd constant_mf_wait,constant_mf_handler
%else
    dd 0,0
%endif
body_end:
