; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
bits 16
cpu pentium
org 1000h
jmp near start
db 'DOSESEL1'
dw gdt_template, gdt_end-gdt_template, user_start, irq_handler
dd after0, after1, after2, after3, after4, after5

%macro descriptor 4
dw %2 & 0ffffh, %1 & 0ffffh
db (%1 >> 16) & 0ffh, %3, ((%2 >> 16) & 15) | ((%4 & 15) << 4), (%1 >> 24) & 0ffh
%endmacro

align 8
gdt_template:
dq 0
descriptor 0, 0fffffh, 0f2h, 0ch ; 08 user data/32-bit stack
descriptor 0, 0fffffh, 0fah, 0ch ; 10 user code32
descriptor 0, 0fffffh, 092h, 0ch ; 18 kernel data/stack32
descriptor 0, 0fffffh, 09ah, 0ch ; 20 kernel code32
descriptor 0, 0ffffh, 09ah, 0 ; 28 kernel code16
descriptor 0, 0ffffh, 092h, 0 ; 30 kernel stack16
descriptor 0, 0ffffh, 0fah, 0 ; 38 user code16, retained for topology
descriptor 0, 0ffffh, 0f2h, 0 ; 40 user stack16
descriptor 7000h, 67h, 89h, 0 ; 48 own TSS32
dw call16_0, 28h
db 0, 0e4h
dw 0 ; 50 call gate16, no parameters
dw call32, 20h
db 0, 0ech
dw 0 ; 58 call gate32
dw call16_2, 28h
db 2, 0e4h
dw 0 ; 60 call gate16 with two parameters
gdt_end:
gdtr dw gdt_end-gdt_template-1
dd 500h
idtr dw 7ffh
dd 8000h

; Initializes private RAM and enters the protected kernel through a real far jump.
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
    mov dword [6180h], 05a3cc3a5h
    fninit
    fldpi
    fstp tword [5120h]
    lgdt [gdtr]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp dword 20h:kernel32

bits 32
; Installs an own TSS, IDT, timer and user frame using actual guest instructions.
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
    mov ax, 48h
    ltr ax
    mov edi, 8000h
    mov ecx, 256
    mov eax, (20h << 16) | ((fatal-$$+1000h) & 0ffffh)
    mov edx, ((fatal-$$+1000h) & 0ffff0000h) | 8e00h

; Builds all bounded IDT entries in private RAM before installing the explicit gates.
idt_loop:
    mov [edi], eax
    mov [edi+4], edx
    add edi, 8
    loop idt_loop
    mov dword [8000h+20h*8], (20h << 16) | ((irq_handler-$$+1000h) & 0ffffh)
    mov dword [8004h+20h*8], ((irq_handler-$$+1000h) & 0ffff0000h) | 8e00h
    mov dword [8000h+30h*8], (28h << 16) | ((interrupt16-$$+1000h) & 0ffffh)
    mov dword [8004h+30h*8], ((interrupt16-$$+1000h) & 0ffff0000h) | 0e600h
    mov dword [8000h+31h*8], (28h << 16) | ((interrupt16-$$+1000h) & 0ffffh)
    mov dword [8004h+31h*8], ((interrupt16-$$+1000h) & 0ffff0000h) | 0e700h
    mov dword [8000h+32h*8], (20h << 16) | ((interrupt32-$$+1000h) & 0ffffh)
    mov dword [8004h+32h*8], ((interrupt32-$$+1000h) & 0ffff0000h) | 0ee00h
    mov dword [8000h+34h*8], (20h << 16) | ((finish-$$+1000h) & 0ffffh)
    mov dword [8004h+34h*8], ((finish-$$+1000h) & 0ffff0000h) | 0ee00h
    lidt [idtr]
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
    push dword 0bh
    push dword 1f000h
    push dword 3202h
    push dword 13h
    push dword user_start
    iretd

%macro record_user 1
    mov eax, esp
    mov [6020h+64*%1], eax
    mov ax, ss
    mov [6024h+64*%1], ax
    mov ax, cs
    mov [6026h+64*%1], ax
    pushfd
    pop edx
    mov [6028h+64*%1], edx
    push dword 12340000h+%1
    pop edx
    mov [602ch+64*%1], edx
%endmacro

; Exercises real call/interrupt/trap gates and observes each restored user stack through actual pushes.
user_start:
    mov ax, 0bh
    mov ds, ax
    mov es, ax
    mov fs, ax
    mov gs, ax
    mov dword [5104h], 0
    call 53h:0
after0:
    record_user 0
    mov dword [5104h], 1
    mov dword [7004h], 0abcde000h
    mov word [7008h], 30h
    mov esp, 2f000h
    mov word [esp], 1234h
    mov word [esp+2], 5678h
    call 63h:0
after1:
    record_user 1
    mov dword [5104h], 2
    mov dword [7004h], 0e000h
    mov word [7008h], 18h
    mov esp, 1f010h
    int 30h
after2:
    record_user 2
    mov dword [5104h], 3
    mov dword [7004h], 0abcde000h
    mov word [7008h], 30h
    mov esp, 1f020h
    int 31h
after3:
    record_user 3
    mov dword [5104h], 4
    mov dword [7004h], 0e000h
    mov word [7008h], 18h
    mov esp, 1f100h
    call 5bh:0
after4:
    record_user 4
    mov dword [5104h], 5
    mov dword [7004h], 0abcde000h
    mov word [7008h], 30h
    mov esp, 1f200h
    int 32h
after5:
    record_user 5
    mov esp, 1d800h
    push es
    pop eax
    mov [6600h], eax
    push cs
    pop eax
    mov [6604h], eax
    push ss
    pop eax
    mov [6608h], eax
    push ds
    pop eax
    mov [660ch], eax
    push fs
    pop eax
    mov [6610h], eax
    push gs
    pop eax
    mov [6614h], eax
    mov dword [esp-4], 0a5a5a5a5h
    o16 push ds
    pop ax
    mov eax, [esp-4]
    mov [6618h], eax
    push dword 0a5a5000bh
    pop ds
    mov ax, ds
    mov [661ch], ax
    push dword 0a5a5000bh
    pop ss
    mov eax, esp
    mov [6620h], eax
    int 34h

%macro capture_kernel 0
    pushfd
    pop edi
    mov eax, esp
    mov esi, [5104h]
    shl esi, 6
    mov [esi+6000h], eax
    mov ax, ss
    mov [esi+6004h], ax
    mov ax, cs
    mov [esi+6006h], ax
    mov bx, sp
    %assign i 0
    %rep 12
        mov ax, [ss:bx+2*i]
        mov [esi+6008h+2*i], ax
        %assign i i+1
    %endrep
%endmacro

bits 16
; Captures an actual 16-bit call frame, clocks a real IRQ, and returns without parameter cleanup.
call16_0:
    capture_kernel
    sti
    hlt
    push edi
    popfd
    retf

; Returns through a 16-bit call gate while discarding actual copied arguments on both stacks.
call16_2:
    capture_kernel
    sti
    hlt
    push edi
    popfd
    retf 4

; Captures actual 16-bit interrupt/trap frames and resumes their outer user stack.
interrupt16:
    capture_kernel
    sti
    hlt
    push edi
    popfd
    iret

bits 32
; Retains a 32-bit call-frame control beside the changed narrow-return contract.
call32:
    capture_kernel
    push edi
    popfd
    retf

; Retains a 32-bit interrupt-frame control, including a sixteen-bit kernel stack.
interrupt32:
    capture_kernel
    push edi
    popfd
    iretd

; Acknowledges only the actual physical PIT IRQ and restores interrupted kernel registers/flags.
irq_handler:
    pushad
    inc word [5100h]
    mov al, 20h
    out 20h, al
    popad
    iretd

; Publishes the completed own guest and leaves a privileged CLI/HLT marker.
finish:
    cli
    cld
    mov esi, message

; Emits only a bounded private diagnostic string through the guest's actual E9 port.
print:
    lodsb
    test al, al
    jz done
    out 0e9h, al
    jmp print

; Marks bounded completion only after the actual outer gate returns and selector instructions.
done:
    mov byte [5140h], 1
    hlt

; Stops unexpected architectural faults with a distinct RAM marker for the independent oracle.
fatal:
    cli
    mov byte [5140h], 0ffh
    hlt

message db 'DOSE SELECTOR GATES DONE', 13, 10, 0
