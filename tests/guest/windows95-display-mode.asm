; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own bounded Win95/i386 mode witness. Assemble as PE32; NEVER execute on the host.
; Define MODE_WIDTH, MODE_HEIGHT and MODE_BPP explicitly at assembly time.
; Select only an actual EnumDisplaySettingsA DEVMODE, call CDS_TEST, then call
; CDS_UPDATEREGISTRY only after test return0 OR1 and intact guards. No direct registry,
; driver, time, restart, window, input or host endpoint operation exists here.
; Proof=C:\DOSEMODE.BIN, CREATE_NEW, exactly128 bytes, little endian.
; Offsets:0 magic8;8 version;12 bytes;16 flags;20 stage;24/28/32 requestedW/H/BPP;
; 36 selectedIndex;40 enumCalls;44 lastEnumBOOL;48 failureLastError;52 testLONG;
; 56 applyLONG;60/64 beforeMetricsW/H;68/72 afterMetricsW/H;76/80 capsW/H;
; 84/88 capsBPP/planes;92 GetDC handle;96 ReleaseDC return;100 guardChecks;
; 104 guardFailures;108 FNV1a(selected148B);112 Hz;116 dmFields;120 size|extra<<16;
; 124 terminal(0=apply0,1=CDS error,2=apply1/restart needed,3=no usable mode,4=guard failure).
; FFFFFFFF LONG is also valid DISP_CHANGE_FAILED(-1): flags distinguish NOT_CALLED.
; Flags:1 selected,2 testCalled,4 test0,8 applyCalled,10h apply0,20h apply1/restart,
; 40h guardFailure,80h enumCap,100h badModeSize,200h noMatch,
; 400h testInputChanged,800h applyInputChanged,1000h test1Observed. Test1 alone
; is not a persisted change or terminal restart result. Input changes are observations;
; the apply call always receives a fresh copy of the original enumerated mode.
; Classic ANSI Win4.0 DEVMODEA148: size36,extra38,fields40,bpp104,W108,H112,Hz120.
; https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsa
; https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-changedisplaysettingsa
BITS 32
ORG 0
%ifndef MODE_WIDTH
    %error Define MODE_WIDTH explicitly.
    %define MODE_WIDTH 0
%endif
%ifndef MODE_HEIGHT
    %error Define MODE_HEIGHT explicitly.
    %define MODE_HEIGHT 0
%endif
%ifndef MODE_BPP
    %error Define MODE_BPP explicitly.
    %define MODE_BPP 0
%endif
%if !((MODE_WIDTH == 640 && MODE_HEIGHT == 480 && (MODE_BPP == 16 || MODE_BPP == 32)) || (MODE_WIDTH == 800 && MODE_HEIGHT == 600 && MODE_BPP == 16) || (MODE_WIDTH == 1024 && MODE_HEIGHT == 768 && MODE_BPP == 16) || (MODE_WIDTH == 1280 && MODE_HEIGHT == 1024 && MODE_BPP == 8))
    %error Requested mode is outside the explicit diagnostic whitelist.
%endif
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
%define MODE_BYTES 148
%define MODE_LIMIT 512
%define NOT_CALLED 0xffffffff
%define F_SELECTED 1
%define F_TEST_CALLED 2
%define F_TEST_OK 4
%define F_APPLY_CALLED 8
%define F_APPLY_OK 0x10
%define F_RESTART 0x20
%define F_GUARD 0x40
%define F_ENUM_CAP 0x80
%define F_BAD_MODE 0x100
%define F_NO_MATCH 0x200
%define F_TEST_CHANGED 0x400
%define F_APPLY_CHANGED 0x800
%define F_TEST_RESTART 0x1000

db 'MZ'
dw ((0x200+section_file_size)&0x1ff),((0x200+section_file_size+0x1ff)>>9),0,4,0,0xffff,0,0xb8,0,0,0,0x40,0
times 0x3c-($-$$) db 0
dd pe_header
BITS 16
push cs
pop ds
mov dx,0x0e
mov ah,9
int 0x21
mov ax,0x4c01
int 0x21
db 'DOSE Display Mode Check requires Windows 95.',13,10,'$'
times 0x80-($-$$) db 0
BITS 32
pe_header:
db 'PE',0,0
dw 0x014c,1
dd 0,0,0
dw 0x00e0,0x0103
dw 0x010b
db 0,0
dd section_file_size,0,0
dd RVA(entry),0x1000,0x1000
dd IMAGE_BASE,0x1000,0x200
dw 4,0,0,0,4,0
dd 0,((section_end-section_start+0x1fff)&~0xfff),0x200,0
dw 2,0
dd 0x10000,0x1000,0x10000,0x1000,0,16
dd 0,0
dd RVA(import_directory),import_directory_end-import_directory
times 14 dq 0
db '.text',0,0,0
dd section_end-section_start,0x1000,section_file_size,0x200
dd 0,0
dw 0,0
dd 0xe0000060
times 0x200-($-$$) db 0
section_start:

; The existing system probe's GetVersion guard is repeated before any potentially changing call.
entry:
    cld
    call [VA(iat_GetVersion)]
    test eax,0x80000000
    jz unsupported
    cmp ax,4
    jne unsupported
    mov dword [VA(p_stage)],1
    push dword 0
    call [VA(iat_GetSystemMetrics)]
    mov [VA(p_before_width)],eax
    push dword 1
    call [VA(iat_GetSystemMetrics)]
    mov [VA(p_before_height)],eax
    mov dword [VA(p_stage)],2
    call select_mode
    test eax,eax
    jz .no_mode
    or dword [VA(p_flags)],F_SELECTED
    mov dword [VA(p_stage)],3
    call selected_to_call
    mov dword [VA(p_stage)],4
    or dword [VA(p_flags)],F_TEST_CALLED
    push dword 2                       ; CDS_TEST: this call does not apply the mode.
    push dword VA(call_mode)
    call [VA(iat_ChangeDisplaySettingsA)]
    mov [VA(p_test_return)],eax
    mov dword [VA(p_stage)],5
    call check_guards
    mov ebx,eax
    call call_hash
    cmp eax,[VA(p_selected_fnv)]
    je .test_input_same
    or dword [VA(p_flags)],F_TEST_CHANGED
.test_input_same:
    test ebx,ebx
    jz .guard_failed
    cmp dword [VA(p_test_return)],0
    jne .test_not_zero
    or dword [VA(p_flags)],F_TEST_OK
.apply_mode:
    call selected_to_call             ; pass exactly the original enumerated bytes again.
    mov dword [VA(p_stage)],6
    or dword [VA(p_flags)],F_APPLY_CALLED
    push dword 1                       ; CDS_UPDATEREGISTRY, no CDS_NORESET or CDS_GLOBAL.
    push dword VA(call_mode)
    call [VA(iat_ChangeDisplaySettingsA)]
    mov [VA(p_apply_return)],eax
    mov dword [VA(p_stage)],7
    call check_guards
    mov ebx,eax
    call call_hash
    cmp eax,[VA(p_selected_fnv)]
    je .apply_input_same
    or dword [VA(p_flags)],F_APPLY_CHANGED
.apply_input_same:
    test ebx,ebx
    jz .guard_failed
    cmp dword [VA(p_apply_return)],0
    je .applied
    cmp dword [VA(p_apply_return)],1
    je .restart_needed
    mov dword [VA(p_terminal)],1
    jmp .observe
.test_not_zero:
    cmp dword [VA(p_test_return)],1
    jne .test_failed
    or dword [VA(p_flags)],F_TEST_RESTART
    jmp .apply_mode
.test_failed:
    mov dword [VA(p_terminal)],1
    jmp .observe
.applied:
    or dword [VA(p_flags)],F_APPLY_OK
    mov dword [VA(p_terminal)],0
    jmp .observe
.restart_needed:
    or dword [VA(p_flags)],F_RESTART
    mov dword [VA(p_terminal)],2
    jmp .observe
.no_mode:
    test dword [VA(p_flags)],F_GUARD
    jnz .guard_failed
    or dword [VA(p_flags)],F_NO_MATCH
    mov dword [VA(p_terminal)],3
    jmp .observe
.guard_failed:
    mov dword [VA(p_terminal)],4
.observe:
    call after_metrics
    mov dword [VA(p_stage)],9
    call write_proof
    test eax,eax
    jnz .write_failure
    push dword [VA(p_terminal)]
    call [VA(iat_ExitProcess)]
.write_failure:
    push dword 5
    call [VA(iat_ExitProcess)]
unsupported:
    push dword 10
    call [VA(iat_ExitProcess)]

; Reset the exact public DEVMODE bytes and both16-byte guards; no larger structure is exposed.
prepare_call:
    pushad
    mov edi,VA(call_guard_before)
    mov ecx,MODE_BYTES+32
    mov al,0xd6
    rep stosb
    mov edi,VA(call_mode)
    mov ecx,MODE_BYTES
    xor eax,eax
    rep stosb
    popad
    ret

; Check all32 guard bytes, return EAX1/0. A damaged buffer never reaches the apply call.
check_guards:
    pushad
    inc dword [VA(p_guard_checks)]
    mov esi,VA(call_guard_before)
    mov ecx,16
.before:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .before
    mov esi,VA(call_guard_after)
    mov ecx,16
.after:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .after
    mov dword [esp+28],1
    popad
    ret
.bad:
    inc dword [VA(p_guard_failures)]
    or dword [VA(p_flags)],F_GUARD
    mov dword [esp+28],0
    popad
    ret

; Match all three requested fields in the actual offered modes. Stop after the first false return or512 calls.
select_mode:
    xor ebx,ebx
.next:
    call prepare_call
    mov word [VA(call_mode)+32],0x0400
    mov word [VA(call_mode)+36],MODE_BYTES
    mov word [VA(call_mode)+38],0
    push dword 0
    call [VA(iat_SetLastError)]
    inc dword [VA(p_enum_calls)]
    push dword VA(call_mode)
    push ebx
    push dword 0
    call [VA(iat_EnumDisplaySettingsA)]
    mov [VA(p_enum_bool)],eax
    mov dword [VA(p_enum_error)],0
    test eax,eax
    jnz .guard
    call [VA(iat_GetLastError)]
    mov [VA(p_enum_error)],eax
.guard:
    call check_guards
    test eax,eax
    jz .none
    cmp dword [VA(p_enum_bool)],0
    je .none
    cmp dword [VA(call_mode)+104],MODE_BPP
    jne .advance
    cmp dword [VA(call_mode)+108],MODE_WIDTH
    jne .advance
    cmp dword [VA(call_mode)+112],MODE_HEIGHT
    jne .advance
    cmp word [VA(call_mode)+36],124
    jb .bad_size
    cmp word [VA(call_mode)+36],MODE_BYTES
    ja .bad_size
    cmp word [VA(call_mode)+38],0
    jne .bad_size
    mov [VA(p_selected_index)],ebx
    mov esi,VA(call_mode)
    mov edi,VA(selected_mode)
    mov ecx,MODE_BYTES
    rep movsb
    call call_hash
    mov [VA(p_selected_fnv)],eax
    mov eax,[VA(selected_mode)+120]
    mov [VA(p_frequency)],eax
    mov eax,[VA(selected_mode)+40]
    mov [VA(p_mode_fields)],eax
    mov eax,[VA(selected_mode)+36]
    mov [VA(p_mode_size)],eax
    mov eax,1
    ret
.bad_size:
    or dword [VA(p_flags)],F_BAD_MODE
    jmp .none
.advance:
    inc ebx
    cmp ebx,MODE_LIMIT
    jb .next
    or dword [VA(p_flags)],F_ENUM_CAP
.none:
    xor eax,eax
    ret

; Every ChangeDisplaySettings call receives a fresh exact copy of the selected OS-provided structure.
selected_to_call:
    pushad
    call prepare_call
    mov esi,VA(selected_mode)
    mov edi,VA(call_mode)
    mov ecx,MODE_BYTES
    rep movsb
    popad
    ret

; Return FNV1a over every supplied public DEVMODE byte, including actual flags and refresh values.
call_hash:
    push esi
    push ecx
    push edx
    mov esi,VA(call_mode)
    mov ecx,MODE_BYTES
    mov eax,0x811c9dc5
.next:
    movzx edx,byte [esi]
    xor eax,edx
    imul eax,eax,0x01000193
    inc esi
    loop .next
    pop edx
    pop ecx
    pop esi
    ret

; Actual post-call display state is observed even after a test/apply error or a restart-required return.
after_metrics:
    mov dword [VA(p_stage)],8
    push dword 0
    call [VA(iat_GetSystemMetrics)]
    mov [VA(p_after_width)],eax
    push dword 1
    call [VA(iat_GetSystemMetrics)]
    mov [VA(p_after_height)],eax
    push dword 0
    call [VA(iat_GetDC)]
    mov [VA(p_getdc)],eax
    test eax,eax
    jz .done
    push dword 8
    push dword [VA(p_getdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(p_caps_width)],eax
    push dword 10
    push dword [VA(p_getdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(p_caps_height)],eax
    push dword 12
    push dword [VA(p_getdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(p_caps_bpp)],eax
    push dword 14
    push dword [VA(p_getdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(p_caps_planes)],eax
    push dword [VA(p_getdc)]
    push dword 0
    call [VA(iat_ReleaseDC)]
    mov [VA(p_releasedc)],eax
.done:
    ret

; Write only the new own128-byte proof; writer_status and process exit expose transport failure separately.
write_proof:
    mov dword [VA(writer_status)],1
    push dword 0
    push dword 0x80
    push dword 1                       ; CREATE_NEW, preserve any earlier own proof.
    push dword 0
    push dword 1
    push dword 0x40000000
    push dword VA(proof_path)
    call [VA(iat_CreateFileA)]
    mov [VA(file_handle)],eax
    cmp eax,-1
    je .failed
    push dword 0
    push dword VA(written)
    push dword 128
    push dword VA(proof)
    push eax
    call [VA(iat_WriteFile)]
    test eax,eax
    jz .close_failed
    cmp dword [VA(written)],128
    jne .close_failed
    push dword [VA(file_handle)]
    call [VA(iat_FlushFileBuffers)]
    test eax,eax
    jz .close_failed
    push dword [VA(file_handle)]
    call [VA(iat_CloseHandle)]
    test eax,eax
    jz .failed
    mov dword [VA(writer_status)],0
    xor eax,eax
    ret
.close_failed:
    push dword [VA(file_handle)]
    call [VA(iat_CloseHandle)]
.failed:
    mov eax,1
    ret

align 4,db 0
proof:
    db 'DOSEMOD1'
    dd 1,128
p_flags: dd 0
p_stage: dd 0
    dd MODE_WIDTH,MODE_HEIGHT,MODE_BPP
p_selected_index: dd NOT_CALLED
p_enum_calls: dd 0
p_enum_bool: dd NOT_CALLED
p_enum_error: dd 0
p_test_return: dd NOT_CALLED
p_apply_return: dd NOT_CALLED
p_before_width: dd 0
p_before_height: dd 0
p_after_width: dd 0
p_after_height: dd 0
p_caps_width: dd 0
p_caps_height: dd 0
p_caps_bpp: dd 0
p_caps_planes: dd 0
p_getdc: dd 0
p_releasedc: dd NOT_CALLED
p_guard_checks: dd 0
p_guard_failures: dd 0
p_selected_fnv: dd 0
p_frequency: dd 0
p_mode_fields: dd 0
p_mode_size: dd 0
p_terminal: dd NOT_CALLED
    times 128-($-proof) db 0
call_guard_before: times 16 db 0
call_mode: times MODE_BYTES db 0
call_guard_after: times 16 db 0
selected_mode: times MODE_BYTES db 0
written: dd 0
file_handle: dd 0
writer_status: dd NOT_CALLED
proof_path: db 'C:\DOSEMODE.BIN',0

align 4,db 0
metadata:
    db 'DOSEMOM1'
    dw 1,10
    dd RVA(entry),RVA(proof),128,RVA(p_stage),RVA(p_test_return)
    dd RVA(p_apply_return),RVA(selected_mode),MODE_BYTES,RVA(p_terminal),RVA(writer_status)

%macro LOOKUP 1
    dd RVA(name_ %+ %1)
%endmacro
%macro IAT 1
iat_ %+ %1: dd RVA(name_ %+ %1)
%endmacro
%macro NAME 1
align 2,db 0
name_ %+ %1: dw 0
    db %str(%1),0
%endmacro
%macro KERNEL_IMPORTS 1
    %1 GetVersion
    %1 GetLastError
    %1 SetLastError
    %1 CreateFileA
    %1 WriteFile
    %1 FlushFileBuffers
    %1 CloseHandle
    %1 ExitProcess
%endmacro
%macro USER_IMPORTS 1
    %1 EnumDisplaySettingsA
    %1 ChangeDisplaySettingsA
    %1 GetSystemMetrics
    %1 GetDC
    %1 ReleaseDC
%endmacro
%macro GDI_IMPORTS 1
    %1 GetDeviceCaps
%endmacro
align 4,db 0
import_directory:
    dd RVA(lookup_kernel),0,0,RVA(dll_kernel),RVA(iat_kernel)
    dd RVA(lookup_user),0,0,RVA(dll_user),RVA(iat_user)
    dd RVA(lookup_gdi),0,0,RVA(dll_gdi),RVA(iat_gdi)
    times 5 dd 0
import_directory_end:
lookup_kernel:
    KERNEL_IMPORTS LOOKUP
    dd 0
iat_kernel:
    KERNEL_IMPORTS IAT
    dd 0
lookup_user:
    USER_IMPORTS LOOKUP
    dd 0
iat_user:
    USER_IMPORTS IAT
    dd 0
lookup_gdi:
    GDI_IMPORTS LOOKUP
    dd 0
iat_gdi:
    GDI_IMPORTS IAT
    dd 0
dll_kernel: db 'KERNEL32.DLL',0
dll_user: db 'USER32.DLL',0
dll_gdi: db 'GDI32.DLL',0
    KERNEL_IMPORTS NAME
    USER_IMPORTS NAME
    GDI_IMPORTS NAME
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
