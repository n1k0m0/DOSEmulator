; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Separate real host-x87 test reference, never used for emulator execution.
bits 64
    sub rsp,520
    fxsave64 [rsp]
    fninit
    fldcw [rcx]
    movzx edx,word [rcx+4]
    and edx,15
    jz .seed
.fill:
    cmp edx,1
    je .source
    fld tword [rcx+18]
    jmp .next
.source:
    fld tword [rcx+8]
.next:
    dec edx
    jnz .fill
.seed:
    fnstenv [rcx+40]
    and word [rcx+44],~0x4700
    movzx eax,word [rcx+4]
    test eax,32
    jz .cc
    or word [rcx+44],0x200
.cc:
    and eax,0xc0
    cmp eax,0x40
    je .less
    cmp eax,0x80
    je .equal
    cmp eax,0xc0
    jne .sticky
    or word [rcx+44],0x4500
    jmp .sticky
.less:
    or word [rcx+44],0x100
    jmp .sticky
.equal:
    or word [rcx+44],0x4000
.sticky:
    test word [rcx+4],16
    jz .load
    or word [rcx+44],8 ; unrelated, masked sticky overflow
.load:
    fldenv [rcx+40]
    test word [rcx+4],0x100
    jz .target
    ffree st0
.target:
    test word [rcx+4],0x200
    jz .hole
    ffree st7
.hole:
    test word [rcx+4],0x400
    jz .run
    ffree st3
.run:
    fxtract
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
