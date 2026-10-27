; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
bits 16
cpu pentium
org 1000h
jmp near start
db 'DOSEDBG1'
dw gdt_template, gdt_end-gdt_template, irq_handler, db_handler, ud_handler, gp_handler, user_start, finish
dd ud_fault, ud_after, gd_fault, gd_after, out_none, out_word, in_dword, rep_out, rep_out_after, rep_in, rep_in_after, user_out, user_after, wait_irq
dd pop32_test, pop32_after, pop16_test, pop16_after

%macro descriptor 4
dw %2 & 0ffffh, %1 & 0ffffh
db (%1 >> 16) & 0ffh, %3, ((%2 >> 16) & 15) | ((%4 & 15) << 4), (%1 >> 24) & 0ffh
%endmacro
align 8
gdt_template:
dq 0
descriptor 0, 0fffffh, 0f2h, 0ch ; 08 user data/stack32
descriptor 0, 0fffffh, 0fah, 0ch ; 10 user code32
descriptor 0, 0fffffh, 092h, 0ch ; 18 kernel data/stack32
descriptor 0, 0fffffh, 09ah, 0ch ; 20 kernel code32
descriptor 7000h, 67h, 89h, 0 ; 28 own TSS32, I/O bitmap absent
gdt_end:
gdtr dw gdt_end-gdt_template-1
dd 500h
idtr dw 7ffh
dd 8000h

; Copies own immutable tables, reserves bounded proof RAM and enters protected mode.
start:
    cli
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 9000h
    cld
    mov si, gdt_template
    mov di, 500h
    mov cx, gdt_end-gdt_template
    rep movsb
    mov di, 5000h
    mov cx, 180h
    rep stosb
    mov di, 6000h
    mov cx, 800h
    rep stosb
    mov dword [5ffch], 0a5c33c5ah
    mov dword [6680h], 05a3cc3a5h
    fninit
    fldpi
    fstp tword [5120h]
    lgdt [gdtr]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp dword 20h:kernel32

bits 32
%macro idt_entry 3
    mov dword [8000h+%1*8], (20h << 16) | ((%2-$$+1000h) & 0ffffh)
    mov dword [8004h+%1*8], ((%2-$$+1000h) & 0ffff0000h) | %3
%endmacro

; Installs private TSS/IDT and validates aliases before enabling P54C debug extensions.
kernel32:
    mov ax, 18h
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov esp, 9000h
    mov edi, 7000h
    mov ecx, 68h
    xor eax, eax
    rep stosb
    mov dword [7004h], 0e000h
    mov word [7008h], 18h
    mov word [7066h], 68h
    mov ax, 28h
    ltr ax
    mov edi, 8000h
    mov ecx, 256
    mov eax, (20h << 16) | ((fatal-$$+1000h) & 0ffffh)
    mov edx, ((fatal-$$+1000h) & 0ffff0000h) | 8e00h

; Fills every remaining vector with a bounded fatal guest handler.
idt_loop:
    mov [edi], eax
    mov [edi+4], edx
    add edi, 8
    loop idt_loop
    idt_entry 1, db_handler, 8e00h
    idt_entry 6, ud_handler, 8e00h
    idt_entry 13, gp_handler, 8e00h
    idt_entry 20h, irq_handler, 8e00h
    idt_entry 34h, finish, 0ee00h
    lidt [idtr]
    mov eax, dr4
    mov [5118h], eax
    mov eax, dr5
    mov [511ch], eax
    mov al, 11h
    out 20h, al
    out 0a0h, al
    mov al, 20h
    out 21h, al
    mov al, 70h
    out 0a1h, al
    mov al, 4
    out 21h, al
    mov al, 2
    out 0a1h, al
    mov al, 1
    out 21h, al
    out 0a1h, al
    mov al, 0feh
    out 21h, al
    mov al, 0ffh
    out 0a1h, al
    mov al, 34h
    out 43h, al
    mov al, 1
    out 40h, al
    mov al, 6
    out 40h, al
    mov eax, 8
    mov cr4, eax
    mov eax, 2e1h
    mov dr0, eax
    mov eax, 22401h
    mov dr7, eax
    sti
    hlt

; Proves POP memory destinations use the incremented ESP, preserving independent adjacent guards.
pop_probe:
    mov ebp, esp
    mov dword [52fch], 0a5c33c5ah
    mov dword [5308h], 05a3cc3a5h
    mov esp, 5300h
    mov dword [esp], 11223344h
    mov dword [esp+4], 0aabbccddh
pop32_test:
    pop dword [esp]
pop32_after:
    mov eax, [esp]
    mov [5190h], eax
    mov [5198h], esp
    mov dword [533ch], 08badf00dh
    mov dword [5348h], 0d00dfeedh
    mov esp, 5340h
    mov dword [esp], 0c0debeefh
    mov dword [esp+4], 0a5a55a5ah
pop16_test:
    pop word [esp+4]
pop16_after:
    mov eax, [5344h]
    mov [5194h], eax
    mov [519ch], esp
    mov esp, ebp
    mov [51a0h], esp
    mov dword [5104h], 1
ud_fault:
    mov eax, dr4
ud_after:
    mov dword [5104h], 2
gd_fault:
    mov eax, dr6
gd_after:
    mov [5158h], eax
    mov eax, 20401h
    mov dr7, eax
    mov dx, 2e0h
    mov eax, 12345678h
    mov dword [5104h], 3
out_none:
    out dx, al
    mov dword [5104h], 4
out_word:
    out dx, ax
    mov dword [5104h], 5
in_dword:
    in eax, dx
    mov [5140h], eax
    mov esi, send_bytes
    mov ecx, 3
    mov dx, 2e1h
    mov dword [5104h], 6
rep_out:
    rep outsb
rep_out_after:
    mov [5144h], esi
    mov [5148h], ecx
    mov edi, 5180h
    mov ecx, 2
    mov dx, 2e0h
    mov dword [5104h], 7
rep_in:
    rep insw
rep_in_after:
    mov [514ch], edi
    mov [5150h], ecx
    xor eax, eax
    mov cr4, eax
    mov dword [5104h], 8
    mov eax, 0aabbccddh
    out dx, eax
    mov eax, 8
    mov cr4, eax
    mov dword [5104h], 9
    push dword 0bh
    push dword 1f000h
    push dword 202h
    push dword 13h
    push dword user_start
    iretd

; Attempts denied I/O with a real outer stack, then returns to the privileged completion gate.
user_start:
    mov ax, 0bh
    mov ds, ax
    mov es, ax
    mov dx, 2e0h
    mov eax, 0badc0deh
user_out:
    out dx, eax
user_after:
    mov dword [515ch], 0c001d00dh
    int 34h

; Captures committed debug frames and GPRs, clears sticky status and retries or resumes normally.
db_handler:
    pushad
    mov edi, [5108h]
    shl edi, 6
    add edi, 6000h
    inc dword [5108h]
    mov eax, [5104h]
    mov [edi], eax
    mov eax, dr6
    mov [edi+4], eax
    mov eax, dr7
    mov [edi+8], eax
    mov eax, [esp+32]
    mov [edi+12], eax
    mov eax, [esp+36]
    mov [edi+16], eax
    mov eax, [esp+40]
    mov [edi+20], eax
    mov eax, [esp+28]
    mov [edi+24], eax
    mov eax, [esp+24]
    mov [edi+28], eax
    mov eax, [esp+20]
    mov [edi+32], eax
    mov eax, [esp+4]
    mov [edi+36], eax
    mov eax, [esp]
    mov [edi+40], eax
    xor eax, eax
    mov dr6, eax
    popad
    iretd

; Advances only the known three-byte invalid MOV DR4 without touching GD or debug registers.
ud_handler:
    cmp dword [esp], ud_fault
    jne fatal
    inc dword [510ch]
    push eax
    mov eax, [esp+4]
    mov [6500h], eax
    mov eax, [esp+8]
    mov [6504h], eax
    mov eax, [esp+12]
    mov [6508h], eax
    pop eax
    add dword [esp], 3
    iretd

; Captures the real CPL3 I/O protection frame and resumes after its one-byte OUT.
gp_handler:
    cmp dword [esp], 0
    jne fatal
    cmp dword [esp+4], user_out
    jne fatal
    inc dword [5110h]
    pushad
    lea esi, [esp+32]
    mov edi, 6540h
    mov ecx, 6
    rep movsd
    popad
    add dword [esp+4], 1
    add esp, 4
    iretd

; Acknowledges only a physical PIT IRQ, preserving all interrupted registers and flags.
irq_handler:
    pushad
    inc dword [5100h]
    mov al, 20h
    out 20h, al
    popad
    iretd

; Keeps the selected graph active until four physical timer handler returns have completed.
finish:
    sti
wait_irq:
    cmp dword [5100h], 4
    jae publish
    hlt
    jmp wait_irq

; Publishes bounded diagnostics only after successful fault returns and actual I/O effects.
publish:
    cli
    cld
    mov eax, cr4
    mov [5154h], eax
    mov esi, message

; Emits the fixed private completion text via real byte output.
print:
    lodsb
    test al, al
    jz done
    out 0e9h, al
    jmp print

; Marks completion and halts without further device accesses.
done:
    mov byte [5114h], 1
    hlt

; Marks unexpected exceptions explicitly instead of accepting a partial guest result.
fatal:
    cli
    mov byte [5114h], 0ffh
    hlt

send_bytes db 11h, 22h, 33h
message db 'DOSE P54C DEBUG DONE', 13, 10, 0
