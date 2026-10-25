; Separate reference only. Same 160-byte ABI; never called by the emulator.
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
    movzx eax,word [rcx+2]
%assign OP 0
%rep 6
    cmp eax,OP
    je .op %+ OP
%assign OP OP+1
%endrep
    fldz
    jmp .done
%macro constant 2
.op%1:
    %2
    jmp .done
%endmacro
    constant 0,fld1
    constant 1,fldl2t
    constant 2,fldl2e
    constant 3,fldpi
    constant 4,fldlg2
    constant 5,fldln2
.done:
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
