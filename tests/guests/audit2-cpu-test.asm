; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own reset-booted STI/PIT/#SS/restart/FILD kernel. No DOS, monitor or host injection.
bits 16
cpu 486
org 0
%define BASE 08000h
%define CODE32 8
%define FLAT_DATA 10h
%define STACK32 18h
%define REAL_DATA 20h
%define CODE16 28h
; Use the BIOS-installed IRQ0 handler and actual PIT/PIC wiring while executing a pure STI stream.
start:
 cli
 mov ax,0800h
 mov ds,ax
 mov es,ax
 mov ss,ax
 mov esp,7000h
 mov dword [proof+8],1
 xor ax,ax
 mov fs,ax
 mov eax,[fs:046ch]
 mov [proof+12],eax
 mov al,0ffh
 out 0a1h,al
 mov al,0feh
 out 21h,al
 mov al,34h
 out 43h,al
 xor al,al
 out 40h,al
 mov al,1
 out 40h,al
; Consecutive STI with IF already one must not repeatedly suppress the real timer interrupts.
sti_stream:
 times 16384 db 0fbh
sti_end:
 cli
 mov eax,[fs:046ch]
 mov [proof+16],eax
%ifndef SKIP_STI_ASSERT
 cmp eax,[proof+12]
 jbe failed
%endif
 mov al,0ffh
 out 21h,al
 mov dword [proof+8],2
 sgdt [old_gdtr]
 sidt [old_idtr]
 mov eax,cr0
 mov [old_cr0],eax
 mov di,idt
 mov cx,256
; Build the own diagnostic gates; only the expected stack fault may return.
.gates:
 mov word [di],pm_failed
 mov word [di+2],CODE32
 mov word [di+4],08e00h
 mov word [di+6],0
 add di,8
 loop .gates
 mov word [idt+12*8],stack_fault
 lgdt [gdtr]
 lidt [idtr]
 mov eax,cr0
 or eax,1
 mov cr0,eax
 jmp dword CODE32:protected_entry
bits 32
; Execute ENTER on a genuinely limited 32-bit SS and retry the actual faulting instruction after repairing its immediate.
protected_entry:
 mov ax,FLAT_DATA
 mov ds,ax
 mov es,ax
 mov fs,ax
 mov gs,ax
 mov ax,STACK32
 mov ss,ax
 mov esp,100h
 mov ebp,4242h
 push dword 2
 popfd
 mov dword [BASE+proof+8],3
bad_enter:
 enter 0200h,0
enter_return:
 mov [BASE+proof+48],esp
 mov [BASE+proof+52],ebp
 cmp esp,7ch
 jne pm_failed
 cmp ebp,0fch
 jne pm_failed
 cmp dword [BASE+proof+20],1
 jne pm_failed
 mov eax,[ss:0fch]
 mov [BASE+proof+44],eax
 leave
 mov [BASE+proof+56],esp
 mov [BASE+proof+60],ebp
 cmp esp,100h
 jne pm_failed
 cmp ebp,4242h
 jne pm_failed
 fninit
 fild qword [BASE+integer_min64]
 fstp tword [BASE+proof+64]
 fild qword [BASE+integer_max64]
 fstp tword [BASE+proof+74]
 fild dword [BASE+integer_min32]
 fstp tword [BASE+proof+84]
 fild dword [BASE+integer_max32]
 fstp tword [BASE+proof+94]
 fild word [BASE+integer_min16]
 fstp tword [BASE+proof+104]
 fild word [BASE+integer_max16]
 fstp tword [BASE+proof+114]
 fnstcw [BASE+proof+124]
 fnstsw [BASE+proof+126]
 jmp CODE16:leave_protected
; Preserve the exact delivered fault frame before IRET retries ENTER at its original EIP.
stack_fault:
 inc dword [BASE+proof+20]
 mov [BASE+proof+40],esp
 mov eax,[ss:esp]
 mov [BASE+proof+24],eax
 mov eax,[ss:esp+4]
 mov [BASE+proof+28],eax
 mov eax,[ss:esp+8]
 mov [BASE+proof+32],eax
 mov eax,[ss:esp+12]
 mov [BASE+proof+36],eax
 mov word [BASE+bad_enter+1],0080h
 add esp,4
 iretd
; Unexpected faults or a missing required effect can never count as success.
pm_failed:
 cli
 mov ax,REAL_DATA
 mov ds,ax
 mov es,ax
 mov fs,ax
 mov gs,ax
 mov ss,ax
 mov esp,7000h
 jmp CODE16:leave_failed
bits 16
; Restore real-compatible hidden segment attributes before leaving the own protected interval.
leave_protected:
 mov ax,REAL_DATA
 mov ds,ax
 mov es,ax
 mov fs,ax
 mov gs,ax
 mov ss,ax
 mov esp,7000h
 mov eax,[cs:old_cr0]
 mov cr0,eax
 jmp 0800h:done
; Return through the same real-compatible descriptor path while preserving failure identity.
leave_failed:
 mov eax,[cs:old_cr0]
 mov cr0,eax
 jmp 0800h:failed
; Finish at an actual CLI/HLT boundary with the owned timer still scheduled.
done:
 mov ax,0800h
 mov ds,ax
 mov es,ax
 mov fs,ax
 mov gs,ax
 mov ss,ax
 mov esp,7000h
 lgdt [old_gdtr]
 lidt [old_idtr]
 mov dword [proof+8],4
 mov si,done_text
 call print
halted:
 cli
 hlt
 jmp halted
; Emit only a real guest failure marker and halt.
failed:
 mov ax,0800h
 mov ds,ax
 mov es,ax
 mov fs,ax
 mov gs,ax
 mov ss,ax
 mov esp,7000h
 mov si,failed_text
 call print
 cli
 hlt
 jmp $
; Write the own diagnostic string using actual OUT instructions.
print:
 lodsb
 test al,al
 jz .done
 out 0e9h,al
 jmp print
.done:
 ret
code_end:
align 8,db 0
gdt dq 0
 dq 00409b008000ffffh
 dq 00cf93000000ffffh
 dq 004093020000ffffh
 dq 000093008000ffffh
 dq 00009b008000ffffh
gdt_end:
gdtr dw gdt_end-gdt-1
 dd BASE+gdt
idtr dw 2047
 dd BASE+idt
old_gdtr times 6 db 0
old_idtr times 6 db 0
old_cr0 dd 0
integer_min64 dq 8000000000000000h
integer_max64 dq 7fffffffffffffffh
integer_min32 dd 80000000h
integer_max32 dd 7fffffffh
integer_min16 dw 8000h
integer_max16 dw 7fffh
done_text db 'AUDIT2-CPU-DONE',13,10,0
failed_text db 'AUDIT2-CPU-FAILED',13,10,0
align 16,db 0
proof db 'AU2CPU01'
 times 144 db 0
 db 'AU2END01'
proof_end:
align 16,db 0
idt times 2048 db 0
metadata db 'AU2META1'
 dw proof,proof_end,sti_stream,sti_end,bad_enter,enter_return,halted,start,metadata,code_end,old_gdtr,old_idtr,old_cr0,idt,stack_fault,pm_failed
payload_end:
