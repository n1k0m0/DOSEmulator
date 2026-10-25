; Independent numerical test oracle only: actual host x87, never used by the emulator.
; Windows x64 ABI: RCX points to a private 160-byte input/output block.
bits 64
    sub rsp,520
    fxsave64 [rsp]
    fninit
    fldcw [rcx]
    movzx eax,word [rcx+4]
    cmp eax,1
    je load32
    cmp eax,2
    je load64
    cmp eax,3
    je loadint64
    cmp eax,9
    je loadint32
    cmp eax,10
    je loadint16
    fld tword [rcx+8]
    cmp eax,4
    je store32
    cmp eax,5
    je store64
    cmp eax,6
    je storeint64
    cmp eax,7
    je storeint32
    cmp eax,8
    je storeint16
    cmp eax,11
    je memory32
    cmp eax,12
    je memory64
    cmp eax,13
    je memoryint32
    cmp eax,14
    je memoryint16
    fld tword [rcx+18]
    movzx eax,word [rcx+2]
    cmp eax,1
    je subtract
    cmp eax,2
    je multiply
    cmp eax,3
    je divide
    faddp st1,st0
    jmp done
subtract:
    fsubp st1,st0
    jmp done
multiply:
    fmulp st1,st0
    jmp done
divide:
    fdivp st1,st0
    jmp done
load32:
    fld dword [rcx+32]
    jmp done
load64:
    fld qword [rcx+32]
    jmp done
loadint64:
    fild qword [rcx+32]
    jmp done
loadint32:
    fild dword [rcx+32]
    jmp done
loadint16:
    fild word [rcx+32]
    jmp done
store32:
    fstp dword [rcx+148]
    jmp done
store64:
    fstp qword [rcx+148]
    jmp done
storeint64:
    fistp qword [rcx+148]
    jmp done
storeint32:
    fistp dword [rcx+148]
    jmp done
storeint16:
    fistp word [rcx+148]
    jmp done
%macro memory_arithmetic 2
%1:
    movzx eax,word [rcx+2]
    cmp eax,1
    je %%subtract
    cmp eax,2
    je %%multiply
    cmp eax,3
    je %%divide
    cmp eax,4
    je %%reverse_subtract
    cmp eax,5
    je %%reverse_divide
    %if %2=4
        fadd dword [rcx+32]
    %elif %2=8
        fadd qword [rcx+32]
    %elif %2=16
        fiadd word [rcx+32]
    %else
        fiadd dword [rcx+32]
    %endif
    jmp done
%%subtract:
    %if %2=4
        fsub dword [rcx+32]
    %elif %2=8
        fsub qword [rcx+32]
    %elif %2=16
        fisub word [rcx+32]
    %else
        fisub dword [rcx+32]
    %endif
    jmp done
%%multiply:
    %if %2=4
        fmul dword [rcx+32]
    %elif %2=8
        fmul qword [rcx+32]
    %elif %2=16
        fimul word [rcx+32]
    %else
        fimul dword [rcx+32]
    %endif
    jmp done
%%divide:
    %if %2=4
        fdiv dword [rcx+32]
    %elif %2=8
        fdiv qword [rcx+32]
    %elif %2=16
        fidiv word [rcx+32]
    %else
        fidiv dword [rcx+32]
    %endif
    jmp done
%%reverse_subtract:
    %if %2=4
        fsubr dword [rcx+32]
    %elif %2=8
        fsubr qword [rcx+32]
    %elif %2=16
        fisubr word [rcx+32]
    %else
        fisubr dword [rcx+32]
    %endif
    jmp done
%%reverse_divide:
    %if %2=4
        fdivr dword [rcx+32]
    %elif %2=8
        fdivr qword [rcx+32]
    %elif %2=16
        fidivr word [rcx+32]
    %else
        fidivr dword [rcx+32]
    %endif
    jmp done
%endmacro
memory_arithmetic memory32,4
memory_arithmetic memory64,8
memory_arithmetic memoryint32,32
memory_arithmetic memoryint16,16
done:
    ; Nonwaiting save records pending unmasked errors without executing FWAIT.
    ; It also initializes the host FPU before the original context is restored.
    fnsave [rcx+40]
    fxrstor64 [rsp]
    add rsp,520
    ret
