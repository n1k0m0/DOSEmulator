; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual FBLD/FBSTP and pending-state capture inside DOS.
body_start:
%if PROTECTED
    mov eax,cr0
    or eax,0x20
    mov cr0,eax
    mov si,mf_vectors
    mov di,mf_results
    mov bp,3
bcd_mf_loop:
    fninit
    fldcw [si]
    mov cx,[si+4]
    call bcd_fill
    call bcd_input
    mov dword [di+112],0x47
    call bcd_execute
    mov eax,[packed_operand]
    mov [di+228],eax
    mov eax,[packed_operand+4]
    mov [di+232],eax
    mov ax,[packed_operand+8]
    mov [di+236],ax
bcd_mf_wait:
    fwait
    o32 fnsave [di+108]
    mov eax,[packed_operand]
    mov [di+238],eax
    mov eax,[packed_operand+4]
    mov [di+242],eax
    mov ax,[packed_operand+8]
    mov [di+246],ax
    add si,40
    add di,320
    dec bp
    jnz bcd_mf_loop
%endif
    mov si,vector_data
    mov di,result_data
    mov bp,VECTOR_COUNT
bcd_loop:
    fninit
    fldcw [si]
    mov cx,[si+4]
    and cx,15
    call bcd_fill
    call bcd_input
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
    call bcd_execute
    pushfd
    pop dword [di+108]
    mov eax,[packed_operand]
    mov [di+116],eax
    mov eax,[packed_operand+4]
    mov [di+120],eax
    mov ax,[packed_operand+8]
    mov [di+124],ax
    o32 fnsave [di]
    add si,40
    add di,144
    dec bp
    jnz bcd_loop
    mov dword [proof+232],VECTOR_COUNT
    mov ax,cs
    movzx eax,ax
    mov [proof+216],eax
    mov ax,ds
    movzx eax,ax
    mov [proof+220],eax
    jmp body_end
; Initializes the explicit raw x87 stack used by the following BCD case.
bcd_fill:
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
; Copies the original packed-BCD case bytes into the guarded operand buffer.
bcd_input:
    mov eax,[si+28]
    mov [packed_operand],eax
    mov eax,[si+32]
    mov [packed_operand+4],eax
    mov ax,[si+36]
    mov [packed_operand+8],ax
    ret
; Dispatches the case's actual FBLD/FBSTP instruction before saving its observable result.
bcd_execute:
    cmp word [si+2],0
    jne bcd_prepare1
bcd_prepare0:
    push dword [di+112]
    popfd
bcd_instruction0:
    fbld [packed_operand]
    ret
bcd_prepare1:
    push dword [di+112]
    popfd
bcd_instruction1:
    fbstp [packed_operand]
    ret
%if PROTECTED
bcd_mf_handler:
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
bcd_addresses:
    dd bcd_instruction0,bcd_instruction1
%if PROTECTED
    dd bcd_mf_wait,bcd_mf_handler
%else
    dd 0,0
%endif
body_end:
