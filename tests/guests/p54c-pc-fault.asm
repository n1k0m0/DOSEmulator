; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Actual DOS/V86 negative: original EMM386 owns #GP; the host only observes its real first frame.
bits 16
cpu pentium
org 100h
; Enter the own DOS process, reserve a private stack and wait for the actual fault observation trigger.
start:
    push cs
    pop ds
    mov sp,stack_top
    mov bx,(program_end-$$+10fh)/16
    mov ah,4ah
    int 21h
    jc failed
    mov dx,ready
    mov ah,9
    int 21h
    xor ax,ax
    int 16h
    mov eax,0a55a0ff0h
    mov edx,012345678h
    mov ecx,10h
    mov ebx,0abcddcbah
fault_instruction:
%if PROBE=0
    rdtsc
%elif PROBE=1
    rdmsr
%elif PROBE=2
    wrmsr
%else
%error unknown probe
%endif
    mov dx,unexpected
    jmp finish
failed:
    mov dx,failure
finish:
    mov ah,9
    int 21h
    mov ax,4c01h
    int 21h
ready: db 13,10,'P54C-FAULT-READY',13,10,'$'
unexpected: db 13,10,'P54C-FAULT-UNEXPECTED',13,10,'$'
failure: db 13,10,'P54C-FAULT-SETUP-FAILED',13,10,'$'
immutable_end:
    times 1024 db 0
stack_top:
oracle_table: db 'P54FAULT'
    dw fault_instruction,immutable_end
program_end:
