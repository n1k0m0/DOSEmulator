; Own actual numeric instruction sequence; no host numerical emulation.
body_start:
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
numeric_loop:
    fninit
    fldcw [si]
    movzx eax,word [si+4]
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
    fld tword [si+8]
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
    fld tword [si+18]
    movzx eax,word [si+2]
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
    fld dword [si+32]
    jmp done
load64:
    fld qword [si+32]
    jmp done
loadint64:
    fild qword [si+32]
    jmp done
loadint32:
    fild dword [si+32]
    jmp done
loadint16:
    fild word [si+32]
    jmp done
store32:
    fstp dword [di+108]
    jmp done
store64:
    fstp qword [di+108]
    jmp done
storeint64:
    fistp qword [di+108]
    jmp done
storeint32:
    fistp dword [di+108]
    jmp done
storeint16:
    fistp word [di+108]
    jmp done
%macro memory_arithmetic 2
%1:
    movzx eax,word [si+2]
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
        fadd dword [si+32]
    %elif %2=8
        fadd qword [si+32]
    %elif %2=16
        fiadd word [si+32]
    %else
        fiadd dword [si+32]
    %endif
    jmp done
%%subtract:
    %if %2=4
        fsub dword [si+32]
    %elif %2=8
        fsub qword [si+32]
    %elif %2=16
        fisub word [si+32]
    %else
        fisub dword [si+32]
    %endif
    jmp done
%%multiply:
    %if %2=4
        fmul dword [si+32]
    %elif %2=8
        fmul qword [si+32]
    %elif %2=16
        fimul word [si+32]
    %else
        fimul dword [si+32]
    %endif
    jmp done
%%divide:
    %if %2=4
        fdiv dword [si+32]
    %elif %2=8
        fdiv qword [si+32]
    %elif %2=16
        fidiv word [si+32]
    %else
        fidiv dword [si+32]
    %endif
    jmp done
%%reverse_subtract:
    %if %2=4
        fsubr dword [si+32]
    %elif %2=8
        fsubr qword [si+32]
    %elif %2=16
        fisubr word [si+32]
    %else
        fisubr dword [si+32]
    %endif
    jmp done
%%reverse_divide:
    %if %2=4
        fdivr dword [si+32]
    %elif %2=8
        fdivr qword [si+32]
    %elif %2=16
        fidivr word [si+32]
    %else
        fidivr dword [si+32]
    %endif
    jmp done
%endmacro
memory_arithmetic memory32,4
memory_arithmetic memory64,8
memory_arithmetic memoryint32,32
memory_arithmetic memoryint16,16
done:
    o32 fnsave [di]
    add si,40
    add di,128
    dec bp
    jnz numeric_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
body_end:
