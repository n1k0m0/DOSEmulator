; Test-only actual host x87, same 160-byte ABI as X87.NativeReference.
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
    fldenv [rcx+40]
    test word [rcx+4],1
    jz .source
    ffree st0
.source:
    test word [rcx+4],2
    jz .operation
    ffree st1
.operation:
    movzx eax,word [rcx+2]
    lea rdx,[rel table]
    movsxd rax,dword [rdx+rax*4]
    add rax,rdx
    jmp rax
%macro operation 2
%1:
    %2
    jmp done
%endmacro
operation ordered, {fcom st1}
operation ordered_pop, {fcomp st1}
operation ordered_twice, {fcompp}
operation unordered, {fucom st1}
operation unordered_pop, {fucomp st1}
operation unordered_twice, {fucompp}
operation zero, {ftst}
operation real32, {fcom dword [rcx+32]}
operation real32_pop, {fcomp dword [rcx+32]}
operation real64, {fcom qword [rcx+32]}
operation real64_pop, {fcomp qword [rcx+32]}
operation integer32, {ficom dword [rcx+32]}
operation integer32_pop, {ficomp dword [rcx+32]}
operation integer16, {ficom word [rcx+32]}
operation integer16_pop, {ficomp word [rcx+32]}
done:
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
table:
    dd ordered-table,ordered_pop-table,ordered_twice-table,unordered-table,unordered_pop-table,unordered_twice-table,zero-table
    dd real32-table,real32_pop-table,real64-table,real64_pop-table,integer32-table,integer32_pop-table,integer16-table,integer16_pop-table
