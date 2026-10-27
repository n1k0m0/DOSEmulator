; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Shared actual x87 instruction sequence, assembled in real/V86 or PE32 code.
body_start:
    fninit
    mov esi,values
    mov ecx,8
load_loop:
    fld tword [si]
    add si,10
    loop load_loop
rotate_top:
    fdecstp
    fldcw [own_control]
    o16 fnstenv [env14]
    o32 fnstenv [env28]
    o16 fnsave [save94]
    fnstsw [proof+192]
    o16 frstor [save94]
    o32 fnsave [save108]
    fnstsw [proof+196]
    o32 frstor [save108]
    ffree st7
    fld st0
    fxch st3
    fstp st2
    fchs
absolute_value:
    fabs
    o32 fnsave [mutated108]
    o32 frstor [mutated108]
    mov edi,drain80
    mov ecx,8
drain_loop:
    fstp tword [di]
    add di,10
    loop drain_loop
    fnstsw [proof+200]
    fnclex
    fnstsw [proof+204]
    fninit
    fldz
constant_one:
    fld1
    o32 fnsave [constants108]
store_empty:
    fstp tword [empty_value]
    fnstsw [proof+208]
    fnclex
    fnstsw [proof+212]
    o32 fnsave [empty108]
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    mov [proof+224],esp
    smsw ax
    movzx eax,ax
    mov [proof+228],eax
    mov dword [proof+232],1
body_end:
