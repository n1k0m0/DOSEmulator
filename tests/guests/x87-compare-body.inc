; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own actual comparison instructions; the native oracle is never a guest service.
body_start:
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
compare_loop:
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
    o32 fldenv [seed_env]
    test word [si+4],1
    jz .source
    ffree st0
.source:
    test word [si+4],2
    jz .dispatch
    ffree st1
.dispatch:
    mov bx,[si+2]
%if PROTECTED
    shl bx,2
    jmp dword [compare_jumps+bx]
%else
    shl bx,1
    jmp word [compare_jumps+bx]
%endif
%macro comparison 2
compare_%1:
    pushfd
    pop eax
    and eax,~0x8d5
    or eax,0x45
    mov [di+112],eax
    push eax
    popfd
compare_instruction_%1:
    %2
    pushfd
    pop dword [di+108]
    jmp compare_done
%endmacro
comparison 0,{fcom st1}
comparison 1,{fcomp st1}
comparison 2,{fcompp}
comparison 3,{fucom st1}
comparison 4,{fucomp st1}
comparison 5,{fucompp}
comparison 6,{ftst}
comparison 7,{fcom dword [si+32]}
comparison 8,{fcomp dword [si+32]}
comparison 9,{fcom qword [si+32]}
comparison 10,{fcomp qword [si+32]}
comparison 11,{ficom dword [si+32]}
comparison 12,{ficomp dword [si+32]}
comparison 13,{ficom word [si+32]}
comparison 14,{ficomp word [si+32]}
compare_done:
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz compare_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
compare_jumps:
%assign OP 0
%rep 15
%if PROTECTED
    dd 0x10000+compare_%+OP
%else
    dw compare_%+OP
%endif
%assign OP OP+1
%endrep
compare_addresses:
%assign OP 0
%rep 15
    dd compare_instruction_%+OP
%assign OP OP+1
%endrep
body_end:
