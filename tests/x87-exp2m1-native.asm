; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Separate test oracle: actual host x87, same 160-byte ABI as X87.NativeReference.
bits 64
    sub rsp,520
    fxsave64 [rsp]
    fninit
    fldcw [rcx]
    fld tword [rcx+18]
    fld tword [rcx+8]
    fnstenv [rcx+40]
    and word [rcx+44],~0x4700
    or word [rcx+44],0x0200
    movzx eax,word [rcx+4]
    and eax,12
    cmp eax,4
    je .seeded
    cmp eax,8
    je .less
    cmp eax,12
    je .equal
    or word [rcx+44],0x4500
    jmp .seeded
.less:
    or word [rcx+44],0x0100
    jmp .seeded
.equal:
    or word [rcx+44],0x4000
.seeded:
    test word [rcx+4],16
    jz .load
    or word [rcx+44],4 ; Already masked sticky ZE: F2XM1 must not clear it.
.load:
    fldenv [rcx+40]
    test word [rcx+4],1
    jz .source
    ffree st0
.source:
    test word [rcx+4],2
    jz .round
    ffree st1
.round:
    f2xm1
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
