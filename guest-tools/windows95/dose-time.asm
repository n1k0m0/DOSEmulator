; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own classic Win95/i386 clock helper. NEVER execute this PE on the host.
; Default=English installer; /background=hidden500ms timer; /once=512B guest proof.
; CMOS century32h is the explicit DOSE RTC contract; no2000-year pivot.
; FileTime conversions compare two local CIVIL calendars, not UTC/time-zone policy.
BITS 32
ORG 0
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
%define ONE_SECOND 10000000
%define F_VERSION 1
%define F_STABLE 2
%define F_DECODED 4
%define F_TARGET_FT 8
%define F_BEFORE_FT 0x10
%define F_SET_ATTEMPT 0x20
%define F_SET_OK 0x40
%define F_AFTER_FT 0x80
%define F_VERIFIED 0x100
%define F_NO_SET 0x200
%define F_CLI 0x400
%define F_BLOCKED 0x800
%define F_IF_SAME 0x1000
%define F_INDEX_SAME 0x2000
%define NOT_CALLED 0xffffffff

; Microsoft winnt.h x86 CONTEXT prefix, derived fields rather than magic offsets.
; https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-context-r2
; FLOATING_SAVE_AREA=7DWORD+80register bytes+Cr0NpxStateDWORD=112B.
struc WIN32_CONTEXT
 .ContextFlags: resd 1
 .DebugRegisters: resd 6
 .FloatSave: resb 112
 .SegmentsGsFsEsDs: resd 4
 .IntegerEdiEsiEbxEdxEcxEax: resd 6
 .Ebp: resd 1
 .Eip: resd 1
 .SegCs: resd 1
 .EFlags: resd 1
 .Esp: resd 1
 .SegSs: resd 1
endstruc

; Fixed PE32 header; a DOS launch exits with an English message instead.
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
db 'DOSE Tools requires Windows 95.',13,10,'$'
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

; Identify the actual classic OS before any CMOS access or file/registry mutation.
entry:
    cld
    call parse_mode
    call [VA(iat_GetVersion)]
    mov [VA(p_version)],eax
    test eax,0x80000000
    jz .unsupported
    cmp ax,4
    jne .unsupported
    or dword [VA(p_flags)],F_VERSION
    mov dword [VA(p_stage)],1
    cmp dword [VA(p_mode)],3
    je .failed
    cmp dword [VA(p_mode)],1
    je .once
    cmp dword [VA(p_mode)],2
    je background
    call install
    jmp exit_with_result
.once:
    call synchronize
    mov ebx,eax
    mov dword [VA(p_stage)],99
    mov eax,2
    test ebx,ebx
    jz .terminal
    mov eax,1
.terminal:
    mov [VA(p_terminal)],eax
    call write_once_proof
    test eax,eax
    jz .failed
    xor eax,eax
    test ebx,ebx
    jnz exit_with_result
.failed:
    mov eax,1
    jmp exit_with_result
.unsupported:
    cmp dword [VA(p_mode)],0
    jne .failed
    push dword 0x10
    push dword VA(title)
    push dword VA(msg_version)
    push dword 0
    call [VA(iat_MessageBoxA)]
    jmp .failed

; Exit only after normal guest APIs have returned; no native host entry point exists.
exit_with_result:
    push eax
    call [VA(iat_ExitProcess)]

; Parse one exact case-insensitive ASCII switch after the quoted/unquoted module path.
parse_mode:
    pushad
    call [VA(iat_GetCommandLineA)]
    mov esi,eax
    cmp byte [esi],34
    jne .plain
    inc esi
.quoted:
    lodsb
    test al,al
    jz .done
    cmp al,34
    jne .quoted
    jmp .spaces
.plain:
    lodsb
    test al,al
    jz .done
    cmp al,' '
    ja .plain
.spaces:
    cmp byte [esi],' '
    je .skip
    cmp byte [esi],9
    jne .switch
.skip:
    inc esi
    jmp .spaces
.switch:
    cmp byte [esi],0
    je .done
    push esi
    mov edi,VA(arg_once)
    call ascii_argument
    pop esi
    test eax,eax
    jz .background
    mov dword [VA(p_mode)],1
    jmp .done
.background:
    mov edi,VA(arg_background)
    call ascii_argument
    test eax,eax
    jz .invalid
    mov dword [VA(p_mode)],2
    jmp .done
.invalid:
    mov dword [VA(p_mode)],3
.done:
    popad
    ret

; Compare a single complete switch, permitting only trailing spaces/tabs afterwards.
ascii_argument:
    push esi
    push edi
.loop:
    mov al,[esi]
    cmp al,'A'
    jb .folded
    cmp al,'Z'
    ja .folded
    add al,32
.folded:
    cmp al,[edi]
    jne .no
    inc esi
    inc edi
    cmp byte [edi],0
    jne .loop
.tail:
    mov al,[esi]
    test al,al
    jz .yes
    cmp al,' '
    je .next
    cmp al,9
    jne .no
.next:
    inc esi
    jmp .tail
.yes:
    mov eax,1
    jmp .return
.no:
    xor eax,eax
.return:
    pop edi
    pop esi
    ret

; Read/decode one actual RTC target, compare civil64-bit times and verify the API's real result.
synchronize:
    pushad
    cmp dword [VA(sync_busy)],0
    jne .busy
    mov dword [VA(sync_busy)],1
    inc dword [VA(p_sequence)]
    mov dword [VA(sync_ok)],0
    and dword [VA(p_flags)],F_VERSION
    mov dword [VA(p_error)],0
    mov dword [VA(p_set_bool)],NOT_CALLED
    mov dword [VA(p_set_error)],0
    mov dword [VA(p_notify_bool)],NOT_CALLED
    mov dword [VA(p_notify_error)],0
    mov dword [VA(p_stage)],2
    call rtc_read_stable
    test eax,eax
    jz .rtc_failure
    or dword [VA(p_flags)],F_STABLE
    mov dword [VA(p_stage)],3
    call decode_calendar
    test eax,eax
    jz .calendar_failure
    or dword [VA(p_flags)],F_DECODED
    push dword VA(p_before)
    call [VA(iat_GetLocalTime)]
    mov dword [VA(p_stage)],4
    push dword VA(p_target_ft)
    push dword VA(p_target)
    call [VA(iat_SystemTimeToFileTime)]
    mov [VA(p_target_bool)],eax
    test eax,eax
    jz .conversion_failure
    or dword [VA(p_flags)],F_TARGET_FT
    push dword VA(p_before_ft)
    push dword VA(p_before)
    call [VA(iat_SystemTimeToFileTime)]
    mov [VA(p_before_bool)],eax
    test eax,eax
    jz .conversion_failure
    or dword [VA(p_flags)],F_BEFORE_FT
    mov edi,VA(p_target_ft)
    mov esi,VA(p_before_ft)
    call absolute_filetime_difference
    mov [VA(p_delta_before)],eax
    mov [VA(p_delta_before)+4],edx
    mov ecx,[VA(initial_sync)]
    mov [VA(p_initial)],ecx
    test ecx,ecx
    jnz .set
    test edx,edx
    jnz .set
    cmp eax,ONE_SECOND
    ja .set
    or dword [VA(p_flags)],F_NO_SET
    mov dword [VA(p_set_needed)],0
    jmp .verify
.set:
    mov dword [VA(p_set_needed)],1
    mov dword [VA(p_stage)],5
    or dword [VA(p_flags)],F_SET_ATTEMPT
    push dword VA(p_target)
    call [VA(iat_SetLocalTime)]
    mov [VA(p_set_bool)],eax
    test eax,eax
    jz .set_failure
    or dword [VA(p_flags)],F_SET_OK
    ; Notify the actual shell asynchronously; a hung receiver must never block this timer thread.
    push dword 0
    push dword 0
    push dword 0x001e
    push dword 0xffff
    call [VA(iat_PostMessageA)]
    mov [VA(p_notify_bool)],eax
    test eax,eax
    jnz .verify
    call [VA(iat_GetLastError)]
    mov [VA(p_notify_error)],eax
.verify:
    mov dword [VA(p_stage)],6
    push dword VA(p_after)
    call [VA(iat_GetLocalTime)]
    push dword VA(p_after_ft)
    push dword VA(p_after)
    call [VA(iat_SystemTimeToFileTime)]
    mov [VA(p_after_bool)],eax
    test eax,eax
    jz .conversion_failure
    or dword [VA(p_flags)],F_AFTER_FT
    mov edi,VA(p_target_ft)
    mov esi,VA(p_after_ft)
    call absolute_filetime_difference
    mov [VA(p_delta_after)],eax
    mov [VA(p_delta_after)+4],edx
    test edx,edx
    jnz .verification_failure
    cmp eax,ONE_SECOND
    ja .verification_failure
    or dword [VA(p_flags)],F_VERIFIED
    test dword [VA(p_flags)],F_SET_ATTEMPT
    jz .complete
    cmp dword [VA(p_notify_bool)],0
    je .notification_failure
.complete:
    mov dword [VA(initial_sync)],0
    mov dword [VA(sync_ok)],1
    jmp .done
.rtc_failure:
    mov dword [VA(p_error)],1
    jmp .done
.calendar_failure:
    mov dword [VA(p_error)],2
    jmp .done
.conversion_failure:
    call [VA(iat_GetLastError)]
    mov [VA(p_conversion_error)],eax
    mov dword [VA(p_error)],3
    jmp .done
.set_failure:
    call [VA(iat_GetLastError)]
    mov [VA(p_set_error)],eax
    mov dword [VA(p_error)],4
    jmp .done
.verification_failure:
    mov dword [VA(p_error)],5
    jmp .done
.notification_failure:
    ; Time verification remains separately true; arm an initial sync to retry notification next tick.
    mov dword [VA(initial_sync)],1
    mov dword [VA(p_error)],6
.done:
    mov dword [VA(sync_busy)],0
.busy:
    popad
    mov eax,[VA(sync_ok)]
    ret

; Return the absolute difference of two independently converted civil FILETIME values in EDX:EAX.
absolute_filetime_difference:
    mov eax,[edi]
    mov edx,[edi+4]
    sub eax,[esi]
    sbb edx,[esi+4]
    test edx,0x80000000
    jz .done
    neg eax
    adc edx,0
    neg edx
.done:
    ret

; At most8 pairs of COMPLETE calendars, including century/status flags. IOPL0 never executesCLI.
; An application I/O bitmap can permit70/71 despite IOPL0; no IOPL-only refusal is used.
rtc_read_stable:
    pushad
    mov dword [VA(rtc_ok)],0
    mov dword [VA(p_attempts)],0
    mov dword [VA(p_index_valid)],0
    mov dword [VA(p_exception)],0
    mov dword [VA(p_fault_ip)],0
    pushfd
    pop eax
    mov [VA(p_saved_flags)],eax
    and eax,0x3000
    mov [VA(p_iopl)],eax
    ; The raw frame handler is __cdecl: registration atFS:[0], no Win32 calls inside this region.
    push dword VA(rtc_exception_handler)
    push dword [fs:0]
    mov [fs:0],esp
rtc_io_index_in:
    in al,0x70
    movzx eax,al
    mov [VA(p_saved_index)],eax
    mov dword [VA(p_index_valid)],1
    cmp dword [VA(p_iopl)],0x3000
    jne rtc_pair
rtc_io_cli:
    cli
    or dword [VA(p_flags)],F_CLI
rtc_pair:
    inc dword [VA(p_attempts)]
    mov edi,VA(p_raw_a)
    call rtc_complete_calendar
    mov edi,VA(p_raw_b)
    call rtc_complete_calendar
    test byte [VA(p_raw_a)],0x80
    jnz rtc_retry
    test byte [VA(p_raw_a)+11],0x80
    jnz rtc_retry
    test byte [VA(p_raw_b)],0x80
    jnz rtc_retry
    test byte [VA(p_raw_b)+11],0x80
    jnz rtc_retry
    test byte [VA(p_raw_a)+1],0x80
    jnz rtc_retry
    mov al,[VA(p_raw_a)+1]
    cmp al,[VA(p_raw_a)+10]
    jne rtc_retry
    mov al,[VA(p_raw_b)+1]
    cmp al,[VA(p_raw_b)+10]
    jne rtc_retry
    mov esi,VA(p_raw_a)
    mov edi,VA(p_raw_b)
    mov ecx,12
    repe cmpsb
    jne rtc_retry
    mov dword [VA(rtc_ok)],1
    jmp rtc_restore
rtc_retry:
    cmp dword [VA(p_attempts)],8
    jb rtc_pair
rtc_restore:
    cmp dword [VA(p_index_valid)],0
    je rtc_restore_flags
    mov eax,[VA(p_saved_index)]
rtc_io_restore_out:
    out 0x70,al
rtc_io_restore_in:
    in al,0x70
    movzx eax,al
    mov [VA(p_after_index)],eax
    cmp eax,[VA(p_saved_index)]
    jne rtc_restore_failed
    or dword [VA(p_flags)],F_INDEX_SAME
    jmp rtc_restore_flags
rtc_restore_failed:
    mov dword [VA(rtc_ok)],0
rtc_restore_flags:
    test dword [VA(p_flags)],F_CLI
    jz .unchanged
    push dword [VA(p_saved_flags)]
    popfd
.unchanged:
    pushfd
    pop eax
    mov [VA(p_after_flags)],eax
    xor eax,[VA(p_saved_flags)]
    test eax,0x200
    jnz .bad_if
    or dword [VA(p_flags)],F_IF_SAME
    jmp .unregister
.bad_if:
    mov dword [VA(rtc_ok)],0
.unregister:
    mov eax,[esp]
    mov [fs:0],eax
    add esp,8
    popad
    mov eax,[VA(rtc_ok)]
    ret

; Read A, B, second, minute, hour, weekday, day, month, year, century, B, A into EDI; advance it by12.
rtc_complete_calendar:
    push esi
    push ecx
    mov esi,VA(rtc_registers)
    mov ecx,12
rtc_calendar_loop:
    lodsb
    ; OnlyNMI bit is retained; the saved index's low7bits never contaminate requested registers.
    and al,0x7f
    test byte [VA(p_saved_index)],0x80
    jz rtc_select_pending
    or al,0x80
rtc_select_pending:
rtc_io_select:
    out 0x70,al
rtc_io_data_in:
    in al,0x71
    stosb
    dec ecx
    jnz rtc_calendar_loop
    pop ecx
    pop esi
    ret

; Handle only continuable access/privileged faults at our exact CMOS/CLI instruction sites.
; Raw EXCEPTION_DISPOSITION is0=ContinueExecution,1=ContinueSearch (NOT C filter-1/0).
rtc_exception_handler:
    push ebp
    mov ebp,esp
    push ebx
    push esi
    push edi
    mov edx,[ebp+8]
    test dword [edx+4],0x67
    jnz .search
    mov eax,[edx]
    cmp eax,0xc0000096
    je .code
    cmp eax,0xc0000005
    jne .search
.code:
    mov ecx,[ebp+16]
    mov ebx,[ecx+WIN32_CONTEXT.Eip]
    cmp ebx,[edx+12]
    jne .search
    cmp ebx,VA(rtc_io_index_in)
    je .recognized
    cmp ebx,VA(rtc_io_cli)
    je .recognized
    cmp ebx,VA(rtc_io_select)
    je .recognized
    cmp ebx,VA(rtc_io_data_in)
    je .recognized
    cmp ebx,VA(rtc_io_restore_out)
    je .restore_failed
    cmp ebx,VA(rtc_io_restore_in)
    jne .search
.restore_failed:
    mov edi,VA(rtc_restore_failed)
    jmp .caught
.recognized:
    mov edi,VA(rtc_restore)
.caught:
    mov [VA(p_exception)],eax
    mov [VA(p_fault_ip)],ebx
    mov dword [VA(rtc_ok)],0
    or dword [VA(p_flags)],F_BLOCKED
    mov [ecx+WIN32_CONTEXT.Eip],edi
    mov ebx,[ebp+12]
    mov [ecx+WIN32_CONTEXT.Esp],ebx
    ; Discard only our nested calendar-call temporaries back to the exact registration frame.
    ; A fault afterCLI must resume with the original IF, never call a Win32 API with our IF suppressed.
    mov eax,[ecx+WIN32_CONTEXT.EFlags]
    and eax,~0x200
    mov ebx,[VA(p_saved_flags)]
    and ebx,0x200
    or eax,ebx
    mov [ecx+WIN32_CONTEXT.EFlags],eax
    xor eax,eax
    jmp .done
.search:
    mov eax,1
.done:
    pop edi
    pop esi
    pop ebx
    pop ebp
    ret

; Decode BCD or binary plus12-/24-hourPM; validate Gregorian date including nonleap2100.
decode_calendar:
    pushad
    xor ebx,ebx
    mov bl,[VA(p_raw_a)+1]
    mov esi,VA(p_raw_a)+2
    mov edi,VA(calendar_values)
    mov ecx,8
.loop:
    movzx eax,byte [esi]
    cmp ecx,6
    jne .not_hour
    and eax,0x7f
.not_hour:
    call decode_rtc_number
    jc .invalid
    mov [edi],eax
    inc esi
    add edi,4
    dec ecx
    jnz .loop
    mov eax,[VA(calendar_values)]
    cmp eax,59
    ja .invalid
    mov [VA(p_target)+12],ax
    mov eax,[VA(calendar_values)+4]
    cmp eax,59
    ja .invalid
    mov [VA(p_target)+10],ax
    mov eax,[VA(calendar_values)+8]
    test bl,2
    jnz .hour24
    test eax,eax
    jz .invalid
    cmp eax,12
    ja .invalid
    cmp eax,12
    jne .pm
    xor eax,eax
.pm:
    test byte [VA(p_raw_a)+4],0x80
    jz .hour_done
    add eax,12
    jmp .hour_done
.hour24:
    test byte [VA(p_raw_a)+4],0x80
    jnz .invalid
    cmp eax,23
    ja .invalid
.hour_done:
    mov [VA(p_target)+8],ax
    mov eax,[VA(calendar_values)+12]
    test eax,eax
    jz .invalid
    cmp eax,7
    ja .invalid
    dec eax
    mov [VA(p_target)+4],ax
    mov eax,[VA(calendar_values)+20]
    test eax,eax
    jz .invalid
    cmp eax,12
    ja .invalid
    mov [VA(p_target)+2],ax
    mov eax,[VA(calendar_values)+28]
    cmp eax,99
    ja .invalid
    imul eax,eax,100
    add eax,[VA(calendar_values)+24]
    cmp dword [VA(calendar_values)+24],99
    ja .invalid
    cmp eax,1601
    jb .invalid
    cmp eax,9999
    ja .invalid
    mov [VA(p_target)],ax
    mov ecx,[VA(calendar_values)+20]
    movzx edi,byte [VA(month_lengths)+ecx-1]
    cmp ecx,2
    jne .day
    xor edx,edx
    mov ecx,4
    div ecx
    test edx,edx
    jnz .day
    movzx eax,word [VA(p_target)]
    xor edx,edx
    mov ecx,100
    div ecx
    test edx,edx
    jnz .leap
    movzx eax,word [VA(p_target)]
    xor edx,edx
    mov ecx,400
    div ecx
    test edx,edx
    jnz .day
.leap:
    inc edi
.day:
    mov eax,[VA(calendar_values)+16]
    test eax,eax
    jz .invalid
    cmp eax,edi
    ja .invalid
    mov [VA(p_target)+6],ax
    mov word [VA(p_target)+14],0
    mov dword [VA(calendar_ok)],1
    jmp .done
.invalid:
    mov dword [VA(calendar_ok)],0
.done:
    popad
    mov eax,[VA(calendar_ok)]
    ret

; InputAL/raw, BL/statusB; returnEAX/integer andCF for malformedBCD; no Win32 calls.
decode_rtc_number:
    test bl,4
    jnz .binary
    mov edx,eax
    and eax,15
    cmp eax,9
    ja .invalid
    shr edx,4
    cmp edx,9
    ja .invalid
    imul edx,edx,10
    add eax,edx
.binary:
    clc
    ret
.invalid:
    stc
    ret

; Minimal English installer. CopyFile's fail-if-exists preserves every unknown existing executable.
install:
    push ebx
    push esi
    push edi
    push dword 0
    call [VA(iat_GetModuleHandleA)]
    test eax,eax
    jz .failure
    push dword 0
    push dword VA(install_dialog_proc)
    push dword 0
    push dword VA(install_dialog_template)
    push eax
    call [VA(iat_DialogBoxIndirectParamA)]
    cmp eax,-1
    je .failure
    cmp eax,1
    jne .cancel
    mov esi,VA(install_directory)
    call ensure_directory
    test eax,eax
    jz .failure
    mov esi,VA(share_directory)
    call ensure_directory
    test eax,eax
    jz .failure
    push dword 260
    push dword VA(module_path)
    push dword 0
    call [VA(iat_GetModuleFileNameA)]
    test eax,eax
    jz .failure
    cmp eax,260
    jae .failure
    push dword 1
    push dword VA(installed_path)
    push dword VA(module_path)
    call [VA(iat_CopyFileA)]
    test eax,eax
    jz .failure
    push dword VA(reg_disposition)
    push dword VA(reg_key)
    push dword 0
    push dword 3
    push dword 0
    push dword 0
    push dword 0
    push dword VA(run_key)
    push dword 0x80000002
    call [VA(iat_RegCreateKeyExA)]
    test eax,eax
    jnz .failure
    push dword run_command_end-run_command
    push dword VA(run_command)
    push dword 1
    push dword 0
    push dword VA(run_value)
    push dword [VA(reg_key)]
    call [VA(iat_RegSetValueExA)]
    mov ebx,eax
    push dword [VA(reg_key)]
    call [VA(iat_RegCloseKey)]
    test ebx,ebx
    jnz .failure
    test eax,eax
    jnz .failure
    ; Start the installed copy, so this install medium can be removed after setup exits.
    push dword 0
    push dword VA(run_command)
    call [VA(iat_WinExec)]
    cmp eax,31
    jbe .failure
    push dword 0x40
    push dword VA(title)
    push dword VA(msg_installed)
    push dword 0
    call [VA(iat_MessageBoxA)]
.cancel:
    xor eax,eax
    jmp .return
.failure:
    push dword 0x10
    push dword VA(title)
    push dword VA(msg_install_failed)
    push dword 0
    call [VA(iat_MessageBoxA)]
    mov eax,1
.return:
    pop edi
    pop esi
    pop ebx
    ret

; Let the native dialog manager implement Tab/Enter/Escape; only explicit English IDs terminate this modal dialog.
install_dialog_proc:
    push ebp
    mov ebp,esp
    mov eax,[ebp+12]
    cmp eax,0x110
    je .handled
    cmp eax,0x111
    je .command
    cmp eax,0x10
    je .cancel
    xor eax,eax
    jmp .return
.command:
    movzx edx,word [ebp+16]
    cmp edx,1
    je .end
    cmp edx,2
    je .end
    xor eax,eax
    jmp .return
.cancel:
    mov edx,2
.end:
    push edx
    push dword [ebp+8]
    call [VA(iat_EndDialog)]
.handled:
    mov eax,1
.return:
    pop ebp
    ret 16

; Create only a directory, or confirm ERROR_ALREADY_EXISTS names an actual directory.
ensure_directory:
    push dword 0
    push esi
    call [VA(iat_CreateDirectoryA)]
    test eax,eax
    jnz .yes
    call [VA(iat_GetLastError)]
    cmp eax,183
    jne .no
    push esi
    call [VA(iat_GetFileAttributesA)]
    cmp eax,-1
    je .no
    and eax,0x10
    ret
.yes:
    mov eax,1
    ret
.no:
    xor eax,eax
    ret

; A hidden ordinary window receives timer/end-session messages; there is no network, audio or permanent polling log.
background:
    push dword 0
    push dword VA(window_class)
    call [VA(iat_FindWindowA)]
    test eax,eax
    jnz .already_running
    call synchronize
    ; An unstable read is retryable; an actual denied-port exception is an explicit unsupported backend.
    test dword [VA(p_flags)],F_BLOCKED
    jnz .failure
    push dword 0
    call [VA(iat_GetModuleHandleA)]
    mov [VA(wndclass)+16],eax
    mov [VA(instance)],eax
    push dword VA(wndclass)
    call [VA(iat_RegisterClassA)]
    test eax,eax
    jz .failure
    push dword 0
    push dword [VA(instance)]
    push dword 0
    push dword 0
    push dword 0
    push dword 0
    push dword 0
    push dword 0
    push dword 0
    push dword VA(title)
    push dword VA(window_class)
    push dword 0
    call [VA(iat_CreateWindowExA)]
    mov [VA(window)],eax
    test eax,eax
    jz .failure
    push dword 0
    push dword 500
    push dword 1
    push eax
    call [VA(iat_SetTimer)]
    test eax,eax
    jz .destroy_failure
.loop:
    push dword 0
    push dword 0
    push dword 0
    push dword VA(message)
    call [VA(iat_GetMessageA)]
    cmp eax,-1
    je .destroy_failure
    test eax,eax
    jz .already_running
    push dword VA(message)
    call [VA(iat_TranslateMessage)]
    push dword VA(message)
    call [VA(iat_DispatchMessageA)]
    jmp .loop
.destroy_failure:
    push dword [VA(window)]
    call [VA(iat_DestroyWindow)]
.failure:
    mov eax,1
    jmp exit_with_result
.already_running:
    xor eax,eax
    jmp exit_with_result

; True stdcall WNDPROC; cancellation of a queried shutdown re-arms the timer.
window_proc:
    push ebp
    mov ebp,esp
    push ebx
    push esi
    push edi
    mov ebx,[ebp+8]
    mov eax,[ebp+12]
    cmp eax,0x113
    je .timer
    cmp eax,0x11
    je .query_end
    cmp eax,0x16
    je .end
    cmp eax,0x10
    je .close
    cmp eax,2
    je .destroy
    push dword [ebp+20]
    push dword [ebp+16]
    push eax
    push ebx
    call [VA(iat_DefWindowProcA)]
    jmp .return
.timer:
    cmp dword [ebp+16],1
    jne .zero
    cmp dword [VA(stopping)],0
    jne .zero
    call synchronize
    jmp .zero
.query_end:
    mov dword [VA(stopping)],1
    push dword 1
    push ebx
    call [VA(iat_KillTimer)]
    mov eax,1
    jmp .return
.end:
    cmp dword [ebp+16],0
    jne .close
    mov dword [VA(stopping)],0
    push dword 0
    push dword 500
    push dword 1
    push ebx
    call [VA(iat_SetTimer)]
    test eax,eax
    jz .close
    jmp .zero
.close:
    push ebx
    call [VA(iat_DestroyWindow)]
    jmp .zero
.destroy:
    mov dword [VA(stopping)],1
    push dword 1
    push ebx
    call [VA(iat_KillTimer)]
    push dword 0
    call [VA(iat_PostQuitMessage)]
.zero:
    xor eax,eax
.return:
    pop edi
    pop esi
    pop ebx
    pop ebp
    ret 16

; Two bounded writes: persisted writer fields describe the COMPLETED previous pass, not their own futureClose.
write_once_proof:
    push ebx
    push esi
    push edi
    mov dword [VA(proof_write_ok)],0
    mov esi,1
.pass:
    push dword 0
    push dword 0x80
    push dword 2
    push dword 0
    push dword 0
    push dword 0x40000000
    push dword VA(proof_path)
    call [VA(iat_CreateFileA)]
    cmp eax,-1
    je .failed
    mov ebx,eax
    mov [VA(current_writer)+4],eax
    mov dword [VA(current_writer)+8],0
    mov dword [VA(writer_count)],0
    push dword 0
    push dword VA(writer_count)
    push dword 512
    push dword VA(proof)
    push ebx
    call [VA(iat_WriteFile)]
    mov [VA(current_writer)+12],eax
    test eax,eax
    jz .write_failure
    cmp dword [VA(writer_count)],512
    jne .write_failure
    mov dword [VA(current_writer)+16],0
    mov eax,[VA(writer_count)]
    mov [VA(current_writer)+20],eax
    push ebx
    call [VA(iat_FlushFileBuffers)]
    mov [VA(current_writer)+24],eax
    test eax,eax
    jz .write_failure
    mov dword [VA(current_writer)+28],0
    push ebx
    call [VA(iat_CloseHandle)]
    mov [VA(current_writer)+32],eax
    test eax,eax
    jz .failed
    mov dword [VA(current_writer)+36],0
    mov [VA(current_writer)],esi
    push esi
    mov esi,VA(current_writer)
    mov edi,VA(p_previous_writer)
    mov ecx,10
    rep movsd
    pop esi
    inc esi
    cmp esi,2
    jbe .pass
    mov dword [VA(proof_write_ok)],1
    jmp .return
.write_failure:
    call [VA(iat_GetLastError)]
    mov [VA(writer_error)],eax
    push ebx
    call [VA(iat_CloseHandle)]
.failed:
    mov dword [VA(proof_write_ok)],0
.return:
    pop edi
    pop esi
    pop ebx
    mov eax,[VA(proof_write_ok)]
    ret

align 4,db 0
; Fixed own proof ABI: bytes80..97 are BOTH raw12B calendars;98/A8/B8 are threeSYSTEMTIMEs.
proof:
    db 'DOSETM01'
    dd 1,512
p_stage: dd 0
p_terminal: dd 0
p_flags: dd 0
p_error: dd 0
p_version: dd 0
p_mode: dd 0
p_attempts: dd 0
p_iopl: dd 0
p_saved_flags: dd 0
p_after_flags: dd 0
p_saved_index: dd 0
p_after_index: dd 0
p_exception: dd 0
p_fault_ip: dd 0
p_initial: dd 0
p_set_needed: dd 0
p_target_bool: dd 0
p_conversion_error: dd 0
p_before_bool: dd 0
p_notify_bool: dd NOT_CALLED
p_set_bool: dd NOT_CALLED
p_set_error: dd 0
p_after_bool: dd 0
p_notify_error: dd 0
p_delta_before: dq 0
p_delta_after: dq 0
p_raw_a: times 12 db 0
p_raw_b: times 12 db 0
p_target: times 16 db 0
p_before: times 16 db 0
p_after: times 16 db 0
p_target_ft: dq 0
p_before_ft: dq 0
p_after_ft: dq 0
p_index_valid: dd 0
    dd 0
p_sequence: dd 0
    times 0x100-($-proof) db 0
; PreviousCOMPLETED proof-writepass: seq/create/rawGLE/write/rawGLE/count/flush/GLE/close/GLE.
p_previous_writer: times 10 dd NOT_CALLED
    times 512-($-proof) db 0

align 4,db 0
initial_sync: dd 1
sync_busy: dd 0
sync_ok: dd 0
rtc_ok: dd 0
calendar_ok: dd 0
calendar_values: times 8 dd 0
rtc_registers: db 0x0a,0x0b,0,2,4,6,7,8,9,0x32,0x0b,0x0a
month_lengths: db 31,28,31,30,31,30,31,31,30,31,30,31
arg_once: db '/once',0
arg_background: db '/background',0
title: db 'DOSE Tools - Time Synchronization',0
msg_version: db 'This clock helper supports the explicitly installed DOSE Windows 95 PC only.',0
; Standard DLGTEMPLATE with Unicode strings even for DialogBoxIndirectParamA.
; WS_POPUP/CAPTION/SYSMENU + DS_MODALFRAME/SETFONT/CENTER; all controls fit the classic800x600 desktop.
align 4,db 0
install_dialog_template:
    dd 0x80c808c0,0
    dw 3,0,0,340,164
    dw 0,0
    dw __utf16__('DOSE Tools - Time Synchronization'),0
    dw 8,__utf16__('MS Sans Serif'),0
align 4,db 0
    dd 0x50000000,0
    dw 12,12,316,106,0xffff
    dw 0xffff,0x0082
    dw __utf16__('Install DOSE time synchronization for this Windows 95 PC?'),13,10,13,10
    dw __utf16__('The clock helper will start automatically and check the DOSE RTC every 500 ms.'),13,10
    dw __utf16__('Creates C:\DOSETOOLS and C:\Share. Existing shared files are preserved.'),13,10
    dw __utf16__('No file transfer component is included.'),0
    dw 0
align 4,db 0
    dd 0x50010001,0
    dw 208,132,56,14,1
    dw 0xffff,0x0080
    dw __utf16__('Install'),0
    dw 0
align 4,db 0
    dd 0x50010000,0
    dw 272,132,56,14,2
    dw 0xffff,0x0080
    dw __utf16__('Cancel'),0
    dw 0
msg_installed: db 'Time synchronization is installed and the background helper was started.',13,10
    db 'The installation disk can now be removed. Existing C:\Share files were preserved.',0
msg_install_failed: db 'Installation did not complete. An existing DOSETIME.EXE is never overwritten.',13,10
    db 'Check the directories and registry permissions before trying again.',0
install_directory: db 'C:\DOSETOOLS',0
share_directory: db 'C:\Share',0
installed_path: db 'C:\DOSETOOLS\DOSETIME.EXE',0
run_command: db '"C:\DOSETOOLS\DOSETIME.EXE" /background',0
run_command_end:
run_key: db 'Software\Microsoft\Windows\CurrentVersion\Run',0
run_value: db 'DOSE Time',0
proof_path: db 'C:\DOSETIME.BIN',0
window_class: db 'DOSE.Tools.TimeSynchronization.1',0
align 4,db 0
module_path: times 260 db 0
reg_key: dd 0
reg_disposition: dd 0
instance: dd 0
window: dd 0
stopping: dd 0
message: times 7 dd 0
wndclass: dd 0,VA(window_proc),0,0,0,0,0,0,0,VA(window_class)
proof_write_ok: dd 0
writer_count: dd 0
writer_error: dd 0
current_writer: times 10 dd NOT_CALLED

align 4,db 0
; Passive20DWORD RVA map. It never drives guest code, ownership or timer admission.
metadata:
    db 'DOSETMM1'
    dw 1,20
    dd RVA(entry),RVA(proof),512,RVA(p_stage),RVA(p_terminal),RVA(p_mode)
    dd RVA(synchronize),RVA(rtc_read_stable),RVA(rtc_io_index_in),RVA(rtc_io_select)
    dd RVA(rtc_io_data_in),RVA(rtc_io_restore_out),RVA(rtc_exception_handler),RVA(window_proc)
    dd RVA(p_target),RVA(p_before),RVA(p_after),RVA(installed_path),RVA(run_command),RVA(proof_path)

; Imports are only classic Win32 OS/file/window/registry APIs; no socket, multimedia, service or host API.
import_directory:
    dd RVA(import_lookup_0),0,0,RVA(import_dll_0),RVA(import_iat_0)
    dd RVA(import_lookup_1),0,0,RVA(import_dll_1),RVA(import_iat_1)
    dd RVA(import_lookup_2),0,0,RVA(import_dll_2),RVA(import_iat_2)
    times 5 dd 0
import_directory_end:

align 4,db 0
import_lookup_0:
    dd RVA(import_name_GetVersion)
    dd RVA(import_name_GetCommandLineA)
    dd RVA(import_name_GetLocalTime)
    dd RVA(import_name_SystemTimeToFileTime)
    dd RVA(import_name_SetLocalTime)
    dd RVA(import_name_GetLastError)
    dd RVA(import_name_GetModuleFileNameA)
    dd RVA(import_name_GetModuleHandleA)
    dd RVA(import_name_CreateDirectoryA)
    dd RVA(import_name_GetFileAttributesA)
    dd RVA(import_name_CopyFileA)
    dd RVA(import_name_WinExec)
    dd RVA(import_name_CreateFileA)
    dd RVA(import_name_WriteFile)
    dd RVA(import_name_FlushFileBuffers)
    dd RVA(import_name_CloseHandle)
    dd RVA(import_name_ExitProcess)
    dd 0
import_iat_0:
iat_GetVersion: dd RVA(import_name_GetVersion)
iat_GetCommandLineA: dd RVA(import_name_GetCommandLineA)
iat_GetLocalTime: dd RVA(import_name_GetLocalTime)
iat_SystemTimeToFileTime: dd RVA(import_name_SystemTimeToFileTime)
iat_SetLocalTime: dd RVA(import_name_SetLocalTime)
iat_GetLastError: dd RVA(import_name_GetLastError)
iat_GetModuleFileNameA: dd RVA(import_name_GetModuleFileNameA)
iat_GetModuleHandleA: dd RVA(import_name_GetModuleHandleA)
iat_CreateDirectoryA: dd RVA(import_name_CreateDirectoryA)
iat_GetFileAttributesA: dd RVA(import_name_GetFileAttributesA)
iat_CopyFileA: dd RVA(import_name_CopyFileA)
iat_WinExec: dd RVA(import_name_WinExec)
iat_CreateFileA: dd RVA(import_name_CreateFileA)
iat_WriteFile: dd RVA(import_name_WriteFile)
iat_FlushFileBuffers: dd RVA(import_name_FlushFileBuffers)
iat_CloseHandle: dd RVA(import_name_CloseHandle)
iat_ExitProcess: dd RVA(import_name_ExitProcess)
    dd 0
import_dll_0: db 'KERNEL32.DLL',0
align 2,db 0
import_name_GetVersion: dw 0
    db 'GetVersion',0
align 2,db 0
import_name_GetCommandLineA: dw 0
    db 'GetCommandLineA',0
align 2,db 0
import_name_GetLocalTime: dw 0
    db 'GetLocalTime',0
align 2,db 0
import_name_SystemTimeToFileTime: dw 0
    db 'SystemTimeToFileTime',0
align 2,db 0
import_name_SetLocalTime: dw 0
    db 'SetLocalTime',0
align 2,db 0
import_name_GetLastError: dw 0
    db 'GetLastError',0
align 2,db 0
import_name_GetModuleFileNameA: dw 0
    db 'GetModuleFileNameA',0
align 2,db 0
import_name_GetModuleHandleA: dw 0
    db 'GetModuleHandleA',0
align 2,db 0
import_name_CreateDirectoryA: dw 0
    db 'CreateDirectoryA',0
align 2,db 0
import_name_GetFileAttributesA: dw 0
    db 'GetFileAttributesA',0
align 2,db 0
import_name_CopyFileA: dw 0
    db 'CopyFileA',0
align 2,db 0
import_name_WinExec: dw 0
    db 'WinExec',0
align 2,db 0
import_name_CreateFileA: dw 0
    db 'CreateFileA',0
align 2,db 0
import_name_WriteFile: dw 0
    db 'WriteFile',0
align 2,db 0
import_name_FlushFileBuffers: dw 0
    db 'FlushFileBuffers',0
align 2,db 0
import_name_CloseHandle: dw 0
    db 'CloseHandle',0
align 2,db 0
import_name_ExitProcess: dw 0
    db 'ExitProcess',0

align 4,db 0
import_lookup_1:
    dd RVA(import_name_MessageBoxA)
    dd RVA(import_name_FindWindowA)
    dd RVA(import_name_RegisterClassA)
    dd RVA(import_name_CreateWindowExA)
    dd RVA(import_name_SetTimer)
    dd RVA(import_name_KillTimer)
    dd RVA(import_name_GetMessageA)
    dd RVA(import_name_TranslateMessage)
    dd RVA(import_name_DispatchMessageA)
    dd RVA(import_name_DefWindowProcA)
    dd RVA(import_name_DestroyWindow)
    dd RVA(import_name_PostQuitMessage)
    dd RVA(import_name_PostMessageA)
    dd RVA(import_name_DialogBoxIndirectParamA)
    dd RVA(import_name_EndDialog)
    dd 0
import_iat_1:
iat_MessageBoxA: dd RVA(import_name_MessageBoxA)
iat_FindWindowA: dd RVA(import_name_FindWindowA)
iat_RegisterClassA: dd RVA(import_name_RegisterClassA)
iat_CreateWindowExA: dd RVA(import_name_CreateWindowExA)
iat_SetTimer: dd RVA(import_name_SetTimer)
iat_KillTimer: dd RVA(import_name_KillTimer)
iat_GetMessageA: dd RVA(import_name_GetMessageA)
iat_TranslateMessage: dd RVA(import_name_TranslateMessage)
iat_DispatchMessageA: dd RVA(import_name_DispatchMessageA)
iat_DefWindowProcA: dd RVA(import_name_DefWindowProcA)
iat_DestroyWindow: dd RVA(import_name_DestroyWindow)
iat_PostQuitMessage: dd RVA(import_name_PostQuitMessage)
iat_PostMessageA: dd RVA(import_name_PostMessageA)
iat_DialogBoxIndirectParamA: dd RVA(import_name_DialogBoxIndirectParamA)
iat_EndDialog: dd RVA(import_name_EndDialog)
    dd 0
import_dll_1: db 'USER32.DLL',0
align 2,db 0
import_name_MessageBoxA: dw 0
    db 'MessageBoxA',0
align 2,db 0
import_name_FindWindowA: dw 0
    db 'FindWindowA',0
align 2,db 0
import_name_RegisterClassA: dw 0
    db 'RegisterClassA',0
align 2,db 0
import_name_CreateWindowExA: dw 0
    db 'CreateWindowExA',0
align 2,db 0
import_name_SetTimer: dw 0
    db 'SetTimer',0
align 2,db 0
import_name_KillTimer: dw 0
    db 'KillTimer',0
align 2,db 0
import_name_GetMessageA: dw 0
    db 'GetMessageA',0
align 2,db 0
import_name_TranslateMessage: dw 0
    db 'TranslateMessage',0
align 2,db 0
import_name_DispatchMessageA: dw 0
    db 'DispatchMessageA',0
align 2,db 0
import_name_DefWindowProcA: dw 0
    db 'DefWindowProcA',0
align 2,db 0
import_name_DestroyWindow: dw 0
    db 'DestroyWindow',0
align 2,db 0
import_name_PostQuitMessage: dw 0
    db 'PostQuitMessage',0
align 2,db 0
import_name_PostMessageA: dw 0
    db 'PostMessageA',0
align 2,db 0
import_name_DialogBoxIndirectParamA: dw 0
    db 'DialogBoxIndirectParamA',0
align 2,db 0
import_name_EndDialog: dw 0
    db 'EndDialog',0

align 4,db 0
import_lookup_2:
    dd RVA(import_name_RegCreateKeyExA)
    dd RVA(import_name_RegSetValueExA)
    dd RVA(import_name_RegCloseKey)
    dd 0
import_iat_2:
iat_RegCreateKeyExA: dd RVA(import_name_RegCreateKeyExA)
iat_RegSetValueExA: dd RVA(import_name_RegSetValueExA)
iat_RegCloseKey: dd RVA(import_name_RegCloseKey)
    dd 0
import_dll_2: db 'ADVAPI32.DLL',0
align 2,db 0
import_name_RegCreateKeyExA: dw 0
    db 'RegCreateKeyExA',0
align 2,db 0
import_name_RegSetValueExA: dw 0
    db 'RegSetValueExA',0
align 2,db 0
import_name_RegCloseKey: dw 0
    db 'RegCloseKey',0
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
