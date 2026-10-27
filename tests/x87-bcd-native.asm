; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Optional independent reference, same 160-byte ABI; never used by the emulator.
bits 64
    sub rsp,520
    fxsave64 [rsp]
    fninit
    fldcw [rcx]
    movzx edx,word [rcx+4]
    and edx,15
    jz .seed
.fill:
    test edx,1
    jnz .odd
    fld tword [rcx+8]
    jmp .next
.odd:
    fld tword [rcx+18]
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
    or word [rcx+44],4
.load:
    fldenv [rcx+40]
    cmp word [rcx+2],0
    jne .store
    fbld [rcx+148]
    jmp .done
.store:
    fbstp [rcx+148]
.done:
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
