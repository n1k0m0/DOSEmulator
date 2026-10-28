; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original PE32/i386 guest application. Never executed by the host build script.
; Proof ABI is512 bytes, little endian; raw API results and NOT_CALLED=FFFFFFFFh remain distinct.
BITS 32
ORG 0
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
%define PF_VERSION 1
%define PF_WRITE 2
%define PF_READ 4
%define PF_VERIFIED 8
%define PF_WINDOW 0x10
%define PF_PAINT 0x20
%define PF_WAVE_REQUEST 0x40
%define PF_WAVE_OPEN 0x80
%define PF_WAVE_PREPARE 0x100
%define PF_WAVE_WRITE 0x200
%define PF_WAVE_DONE 0x400
%define PF_WAVE_UNPREPARE 0x800
%define PF_WAVE_CLOSE 0x1000
%define PF_CLOSE 0x2000
%define PF_DESTROY 0x4000
%define PF_WAVE_ABORT 0x10000
%define PF_FAILURE 0x80000000

; Save one actual BOOL and its failure-only GetLastError, without guessing success from an error code.
%macro SAVE_BOOL 2
    mov [VA(%1)],eax
    test eax,eax
    jnz %%success
    call [VA(iat_GetLastError)]
    mov [VA(%2)],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp %%done
%%success:
    mov dword [VA(%2)],0
%%done:
%endmacro

; Minimal valid DOS header/stub followed by a fixed, reproducible Win32 PE header.
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
db 'This application requires Win32 Windows.',13,10,'$'
times 0x80-($-$$) db 0
BITS 32
pe_header:
db 'PE',0,0
dw 0x014c,1
dd 0,0,0
dw 0x00e0,0x0103
optional_header:
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

; Native Win32 entry: actual OS identification and file test precede any visible paint or optional wave queue.
entry:
    cld
    call initialize
    call data_test
    call write_proof
    call make_window
    cmp dword [VA(proof_hwnd)],0
    je exit_failure
    push dword 1
    push dword [VA(proof_hwnd)]
    call [VA(iat_ShowWindow)]
    mov [VA(proof_show_return)],eax
    push dword [VA(proof_hwnd)]
    call [VA(iat_UpdateWindow)]
    call write_proof
    ; Playback begins only after the actual synchronous first UpdateWindow/WM_PAINT has returned.
    test dword [VA(proof_flags)],PF_WAVE_REQUEST
    jz .timer
    call start_wave
.timer:
    push dword 0
    push dword 50
    push dword 1
    push dword [VA(proof_hwnd)]
    call [VA(iat_SetTimer)]
    mov [VA(proof_timer)],eax
    test eax,eax
    jnz .message
    or dword [VA(proof_flags)],PF_FAILURE
.message:
    call write_proof
.loop:
    push dword 0
    push dword 0
    push dword 0
    push dword VA(message)
    call [VA(iat_GetMessageA)]
    mov [VA(proof_getmessage)],eax
    cmp eax,-1
    je .get_error
    test eax,eax
    jz .finished
    push dword VA(message)
    call [VA(iat_TranslateMessage)]
    push dword VA(message)
    call [VA(iat_DispatchMessageA)]
    jmp .loop
.get_error:
    call [VA(iat_GetLastError)]
    mov [VA(proof_getmessage_error)],eax
    or dword [VA(proof_flags)],PF_FAILURE
.finished:
    call write_proof
    xor eax,eax
    test dword [VA(proof_flags)],PF_FAILURE
    setnz al
    push eax
    call [VA(iat_ExitProcess)]
exit_failure:
    call write_proof
    push dword 1
    call [VA(iat_ExitProcess)]

; Generate exact own data/audio bytes, record original version returns and inspect only the explicit /wave switch.
initialize:
    pushad
    xor esi,esi
.data:
    mov eax,esi
    imul eax,eax,73
    mov edx,esi
    shr edx,3
    imul edx,edx,19
    add eax,edx
    add eax,0x5a
    mov [VA(expected_data)+esi],al
    inc esi
    cmp esi,1027
    jb .data
    mov esi,VA(expected_data)
    mov ecx,1027
    call fnv
    mov [VA(proof_expected_fnv)],eax
    xor esi,esi
    xor ebx,ebx
.wave:
    mov eax,ebx
    cmp eax,40
    jb .ascending
    mov eax,79
    sub eax,ebx
.ascending:
    imul eax,eax,6
    add eax,11
    mov [VA(wave_data)+esi],al
    inc ebx
    cmp ebx,80
    jb .phase
    xor ebx,ebx
.phase:
    inc esi
    cmp esi,8000
    jb .wave
    call [VA(iat_GetVersion)]
    mov [VA(proof_version)],eax
    mov dword [VA(proof_osinfo)],148
    push dword VA(proof_osinfo)
    call [VA(iat_GetVersionExA)]
    SAVE_BOOL proof_versionex,proof_version_error
    cmp dword [VA(proof_versionex)],0
    je .metrics
    or dword [VA(proof_flags)],PF_VERSION
.metrics:
    push dword 0
    call [VA(iat_GetSystemMetrics)]
    mov [VA(proof_screen_x)],eax
    push dword 1
    call [VA(iat_GetSystemMetrics)]
    mov [VA(proof_screen_y)],eax
    push dword 19
    call [VA(iat_GetSystemMetrics)]
    mov [VA(proof_mouse_present)],eax
    push dword 43
    call [VA(iat_GetSystemMetrics)]
    mov [VA(proof_mouse_buttons)],eax
    call [VA(iat_GetCommandLineA)]
    mov esi,eax
.scan:
    mov al,[esi]
    test al,al
    jz .done
    cmp al,'/'
    je .maybe
    cmp al,'-'
    jne .next
.maybe:
    ; Read each next byte only after the previous one matched; a short trailing switch never reads beyond its NUL.
    mov al,[esi+1]
    or al,0x20
    cmp al,'w'
    jne .next
    mov al,[esi+2]
    or al,0x20
    cmp al,'a'
    jne .next
    mov al,[esi+3]
    or al,0x20
    cmp al,'v'
    jne .next
    mov al,[esi+4]
    or al,0x20
    cmp al,'e'
    jne .next
    mov al,[esi+5]
    test al,al
    jz .requested
    cmp al,' '
    je .requested
    cmp al,9
    jne .next
.requested:
    or dword [VA(proof_flags)],PF_WAVE_REQUEST
    jmp .done
.next:
    inc esi
    jmp .scan
.done:
    popad
    ret

; FNV1a binds all actual bytes for the outside oracle; it never substitutes for the byte-by-byte comparison below.
fnv:
    push ebx
    push ecx
    push esi
    mov eax,0x811c9dc5
.loop:
    test ecx,ecx
    jz .done
    movzx ebx,byte [esi]
    xor eax,ebx
    imul eax,eax,0x01000193
    inc esi
    dec ecx
    jmp .loop
.done:
    pop esi
    pop ecx
    pop ebx
    ret

; Actual synchronous CreateFileA/WriteFile/Close/Reopen/Read/EOF/Close, with distinct statuses and full data verification.
data_test:
    pushad
    push dword 0
    push dword 0x80
    push dword 2
    push dword 0
    push dword 1
    push dword 0x40000000
    push dword VA(data_name)
    call [VA(iat_CreateFileA)]
    mov [VA(proof_write_handle)],eax
    cmp eax,-1
    jne .write_open
    call [VA(iat_GetLastError)]
    mov [VA(proof_write_open_error)],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .done
.write_open:
    mov dword [VA(proof_write_open_error)],0
    push dword 0
    push dword VA(proof_written)
    push dword 1027
    push dword VA(expected_data)
    push dword [VA(proof_write_handle)]
    call [VA(iat_WriteFile)]
    SAVE_BOOL proof_write_return,proof_write_error
    cmp dword [VA(proof_write_return)],0
    je .close_write
    cmp dword [VA(proof_written)],1027
    jne .write_short
    or dword [VA(proof_flags)],PF_WRITE
    jmp .close_write
.write_short:
    or dword [VA(proof_flags)],PF_FAILURE
.close_write:
    push dword [VA(proof_write_handle)]
    call [VA(iat_CloseHandle)]
    SAVE_BOOL proof_write_close,proof_write_close_error
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 1
    push dword 0x80000000
    push dword VA(data_name)
    call [VA(iat_CreateFileA)]
    mov [VA(proof_read_handle)],eax
    cmp eax,-1
    jne .read_open
    call [VA(iat_GetLastError)]
    mov [VA(proof_read_open_error)],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .done
.read_open:
    mov dword [VA(proof_read_open_error)],0
    push dword 0
    push dword VA(proof_read_count)
    push dword 1027
    push dword VA(read_data)
    push dword [VA(proof_read_handle)]
    call [VA(iat_ReadFile)]
    SAVE_BOOL proof_read_return,proof_read_error
    cmp dword [VA(proof_read_return)],0
    je .eof
    cmp dword [VA(proof_read_count)],1027
    jne .read_short
    or dword [VA(proof_flags)],PF_READ
    mov esi,VA(read_data)
    mov ecx,1027
    call fnv
    mov [VA(proof_read_fnv)],eax
    xor esi,esi
    mov dword [VA(proof_mismatch)],-1
.compare:
    mov al,[VA(expected_data)+esi]
    cmp al,[VA(read_data)+esi]
    jne .mismatch
    inc esi
    cmp esi,1027
    jb .compare
    jmp .eof
.mismatch:
    mov [VA(proof_mismatch)],esi
.read_short:
    or dword [VA(proof_flags)],PF_FAILURE
.eof:
    push dword 0
    push dword VA(proof_eof_count)
    push dword 1
    push dword VA(eof_byte)
    push dword [VA(proof_read_handle)]
    call [VA(iat_ReadFile)]
    SAVE_BOOL proof_eof_return,proof_eof_error
    cmp dword [VA(proof_eof_count)],0
    je .close_read
    or dword [VA(proof_flags)],PF_FAILURE
.close_read:
    push dword [VA(proof_read_handle)]
    call [VA(iat_CloseHandle)]
    SAVE_BOOL proof_read_close,proof_read_close_error
    ; File verification is independent of unrelated version/window/API failures.
    cmp dword [VA(proof_mismatch)],-1
    jne .done
    mov eax,[VA(proof_flags)]
    and eax,PF_WRITE|PF_READ
    cmp eax,PF_WRITE|PF_READ
    jne .done
    cmp dword [VA(proof_write_close)],0
    je .done
    cmp dword [VA(proof_read_close)],0
    je .done
    cmp dword [VA(proof_eof_return)],0
    je .done
    cmp dword [VA(proof_eof_count)],0
    jne .done
    or dword [VA(proof_flags)],PF_VERIFIED
.done:
    mov dword [VA(proof_phase)],1
    popad
    ret

; Create owned GDI brushes and a classic ANSI window class; all handles are original API results.
make_window:
    pushad
    push dword 0
    call [VA(iat_GetModuleHandleA)]
    mov [VA(module_handle)],eax
    mov [VA(window_class)+16],eax
    push dword 32512
    push dword 0
    call [VA(iat_LoadCursorA)]
    mov [VA(window_class)+24],eax
    xor esi,esi
.brush:
    push dword [VA(rgb_colors)+esi*4]
    call [VA(iat_CreateSolidBrush)]
    mov [VA(proof_brushes)+esi*4],eax
    test eax,eax
    jnz .brush_ok
    call [VA(iat_GetLastError)]
    mov [VA(proof_brush_errors)+esi*4],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .next_brush
.brush_ok:
    mov dword [VA(proof_brush_errors)+esi*4],0
.next_brush:
    inc esi
    cmp esi,3
    jb .brush
    push dword VA(window_class)
    call [VA(iat_RegisterClassA)]
    mov [VA(proof_class_atom)],eax
    test eax,eax
    jnz .registered
    call [VA(iat_GetLastError)]
    mov [VA(proof_class_error)],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .done
.registered:
    mov dword [VA(proof_class_error)],0
    push dword 0
    push dword [VA(module_handle)]
    push dword 0
    push dword 0
    push dword 280
    push dword 400
    push dword 32
    push dword 32
    push dword 0x00cf0000
    push dword VA(window_title)
    push dword VA(window_name)
    push dword 0
    call [VA(iat_CreateWindowExA)]
    mov [VA(proof_hwnd)],eax
    test eax,eax
    jnz .created
    call [VA(iat_GetLastError)]
    mov [VA(proof_create_error)],eax
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .done
.created:
    mov dword [VA(proof_create_error)],0
    or dword [VA(proof_flags)],PF_WINDOW
    mov dword [VA(proof_phase)],2
.done:
    popad
    ret

; Classic stdcall window procedure; no firmware calls, host endpoints or synthetic GDI services.
window_proc:
    push ebp
    mov ebp,esp
    push ebx
    push esi
    push edi
    cld
    inc dword [VA(proof_messages)]
    mov eax,[ebp+12]
    cmp eax,0x0f
    je .paint
    cmp eax,0x113
    je .timer
    cmp eax,0x10
    je .close
    cmp eax,2
    je .destroy
    cmp eax,3
    je .geometry
    cmp eax,5
    je .size
.default:
    push dword [ebp+20]
    push dword [ebp+16]
    push dword [ebp+12]
    push dword [ebp+8]
    call [VA(iat_DefWindowProcA)]
    jmp .return
.size:
    and dword [VA(proof_flags)],~PF_PAINT
.geometry:
    push dword [ebp+8]
    call update_geometry
    jmp .default
.paint:
    push dword [ebp+8]
    call paint_window
    call write_proof
    xor eax,eax
    jmp .return
.timer:
    cmp dword [ebp+16],1
    jne .default
    inc dword [VA(proof_wave_polls)]
    call poll_wave
    push dword [ebp+8]
    call update_geometry
    call write_proof
    xor eax,eax
    jmp .return
.close:
    or dword [VA(proof_flags)],PF_CLOSE
    mov dword [VA(proof_phase)],5
    push dword [ebp+8]
    call [VA(iat_DestroyWindow)]
    SAVE_BOOL proof_destroy_return,proof_destroy_error
    call write_proof
    xor eax,eax
    jmp .return
.destroy:
    push dword 1
    push dword [ebp+8]
    call [VA(iat_KillTimer)]
    mov [VA(proof_killtimer)],eax
    push dword 1
    call cleanup_wave
    xor esi,esi
.delete:
    mov eax,[VA(proof_brushes)+esi*4]
    test eax,eax
    jz .skip_delete
    push eax
    call [VA(iat_DeleteObject)]
    ; Keep historical handles, but a real failed release still makes the probe fail.
    test eax,eax
    jnz .skip_delete
    or dword [VA(proof_flags)],PF_FAILURE
.skip_delete:
    inc esi
    cmp esi,3
    jb .delete
    or dword [VA(proof_flags)],PF_DESTROY
    mov dword [VA(proof_phase)],6
    call write_proof
    push dword 0
    call [VA(iat_PostQuitMessage)]
    xor eax,eax
.return:
    pop edi
    pop esi
    pop ebx
    mov esp,ebp
    pop ebp
    ret 16

; Sample actual client/device coordinates; callers provide the real HWND even during CreateWindow's initial messages.
update_geometry:
    push ebp
    mov ebp,esp
    pushad
    push dword VA(proof_client_rect)
    push dword [ebp+8]
    call [VA(iat_GetClientRect)]
    mov [VA(proof_client_return)],eax
    test eax,eax
    jnz .screen
    or dword [VA(proof_flags)],PF_FAILURE
.screen:
    mov dword [VA(proof_client_screen)],0
    mov dword [VA(proof_client_screen)+4],0
    push dword VA(proof_client_screen)
    push dword [ebp+8]
    call [VA(iat_ClientToScreen)]
    mov [VA(proof_screen_return)],eax
    test eax,eax
    jnz .done
    or dword [VA(proof_flags)],PF_FAILURE
.done:
    popad
    pop ebp
    ret 4

; Use original BeginPaint/FillRect/EndPaint and live device caps, including exact unequal last-third pixel boundaries.
paint_window:
    push ebp
    mov ebp,esp
    pushad
    inc dword [VA(proof_paints)]
    ; A new paint must establish all three fresh FillRect results; a minimized/empty client cannot inherit an old success.
    and dword [VA(proof_flags)],~PF_PAINT
    mov dword [VA(paint_call_mask)],0
    mov dword [VA(proof_fill_returns)],-1
    mov dword [VA(proof_fill_returns)+4],-1
    mov dword [VA(proof_fill_returns)+8],-1
    mov dword [VA(proof_endpaint)],-1
    push dword VA(paint_struct)
    push dword [ebp+8]
    call [VA(iat_BeginPaint)]
    mov [VA(proof_paint_hdc)],eax
    test eax,eax
    jnz .geometry
    or dword [VA(proof_flags)],PF_FAILURE
    jmp .done
.geometry:
    push dword [ebp+8]
    call update_geometry
    push dword 12
    push dword [VA(proof_paint_hdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(proof_bits)],eax
    push dword 14
    push dword [VA(proof_paint_hdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(proof_planes)],eax
    push dword 8
    push dword [VA(proof_paint_hdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(proof_horzres)],eax
    push dword 10
    push dword [VA(proof_paint_hdc)]
    call [VA(iat_GetDeviceCaps)]
    mov [VA(proof_vertres)],eax
    cmp dword [VA(proof_client_return)],0
    je .end
    mov eax,[VA(proof_client_rect)+12]
    sub eax,[VA(proof_client_rect)+4]
    jle .end
    mov eax,[VA(proof_client_rect)+8]
    sub eax,[VA(proof_client_rect)]
    cmp eax,3
    jl .end
    xor edx,edx
    mov ecx,3
    div ecx
    mov ebx,eax
    xor esi,esi
    mov edi,[VA(proof_client_rect)]
.draw:
    mov [VA(bar_rect)],edi
    mov eax,[VA(proof_client_rect)+4]
    mov [VA(bar_rect)+4],eax
    mov eax,edi
    add eax,ebx
    cmp esi,2
    jne .right
    mov eax,[VA(proof_client_rect)+8]
.right:
    mov [VA(bar_rect)+8],eax
    mov edi,eax
    mov eax,[VA(proof_client_rect)+12]
    mov [VA(bar_rect)+12],eax
    cmp dword [VA(proof_brushes)+esi*4],0
    je .failed_fill
    push dword [VA(proof_brushes)+esi*4]
    push dword VA(bar_rect)
    push dword [VA(proof_paint_hdc)]
    call [VA(iat_FillRect)]
    bts dword [VA(paint_call_mask)],esi
    mov [VA(proof_fill_returns)+esi*4],eax
    test eax,eax
    jnz .next
.failed_fill:
    or dword [VA(proof_flags)],PF_FAILURE
.next:
    inc esi
    cmp esi,3
    jb .draw
.end:
    push dword VA(paint_struct)
    push dword [ebp+8]
    call [VA(iat_EndPaint)]
    mov [VA(proof_endpaint)],eax
    test eax,eax
    jz .failed
    cmp dword [VA(proof_client_return)],0
    je .done
    cmp dword [VA(proof_screen_return)],0
    je .done
    ; A separate call mask distinguishes an unreached API from its legitimate FFFFFFFFh nonzero return.
    cmp dword [VA(paint_call_mask)],7
    jne .done
    xor esi,esi
.check:
    mov eax,[VA(proof_fill_returns)+esi*4]
    test eax,eax
    jz .done
    inc esi
    cmp esi,3
    jb .check
    or dword [VA(proof_flags)],PF_PAINT
    jmp .done
.failed:
    or dword [VA(proof_flags)],PF_FAILURE
.done:
    popad
    pop ebp
    ret 4

; Open a real guest WAVE_MAPPER device, prepare actual zero-initialized WAVEHDR, then submit8000 immutable PCM bytes.
start_wave:
    pushad
    call [VA(iat_waveOutGetNumDevs)]
    mov [VA(proof_wave_devices)],eax
    push dword 0
    push dword 0
    push dword 0
    push dword VA(wave_format)
    push dword -1
    push dword VA(active_wave)
    call [VA(iat_waveOutOpen)]
    mov [VA(proof_wave_open)],eax
    test eax,eax
    jnz .failed
    or dword [VA(proof_flags)],PF_WAVE_OPEN
    mov eax,[VA(active_wave)]
    mov [VA(proof_wave_handle)],eax
    push dword 32
    push dword VA(wave_header)
    push eax
    call [VA(iat_waveOutPrepareHeader)]
    mov [VA(proof_wave_prepare)],eax
    test eax,eax
    jnz .failed
    or dword [VA(proof_flags)],PF_WAVE_PREPARE
    push dword 32
    push dword VA(wave_header)
    push dword [VA(active_wave)]
    call [VA(iat_waveOutWrite)]
    mov [VA(proof_wave_write)],eax
    test eax,eax
    jnz .failed
    or dword [VA(proof_flags)],PF_WAVE_WRITE
    mov dword [VA(proof_phase)],3
    jmp .done
.failed:
    or dword [VA(proof_flags)],PF_FAILURE
.done:
    mov eax,[VA(wave_header)+16]
    mov [VA(proof_wave_flags)],eax
    popad
    ret

; Observe WHDR_DONE only after actual submission and before any reset can manufacture completion.
poll_wave:
    pushad
    test dword [VA(proof_flags)],PF_WAVE_WRITE
    jz .done
    test dword [VA(proof_flags)],PF_WAVE_CLOSE|PF_WAVE_ABORT
    jnz .done
    mov eax,[VA(wave_header)+16]
    mov [VA(proof_wave_flags)],eax
    test eax,1
    jz .done
    call natural_done
    push dword 0
    call cleanup_wave
.done:
    popad
    ret

; Retain the first real natural completion time separately from forced reset on close.
natural_done:
    test dword [VA(proof_flags)],PF_WAVE_DONE
    jnz .done
    or dword [VA(proof_flags)],PF_WAVE_DONE
    call [VA(iat_GetTickCount)]
    mov [VA(proof_wave_done_tick)],eax
    cmp dword [VA(proof_phase)],5
    jae .done
    mov dword [VA(proof_phase)],4
.done:
    ret

; Clean real waveform resources; forced close is marked ABORT and never relabelled natural playback.
cleanup_wave:
    push ebp
    mov ebp,esp
    pushad
    cmp dword [VA(active_wave)],0
    je .done
    test dword [VA(proof_flags)],PF_WAVE_WRITE
    jz .unprepare
    mov eax,[VA(wave_header)+16]
    mov [VA(proof_wave_flags)],eax
    test eax,1
    jz .playing
    test dword [VA(proof_flags)],PF_WAVE_ABORT
    jnz .unprepare
    call natural_done
    jmp .unprepare
.playing:
    cmp dword [ebp+8],0
    je .done
    or dword [VA(proof_flags)],PF_WAVE_ABORT
    push dword [VA(active_wave)]
    call [VA(iat_waveOutReset)]
    mov [VA(proof_wave_reset)],eax
    test eax,eax
    jnz .failed
.unprepare:
    test dword [VA(proof_flags)],PF_WAVE_PREPARE
    jz .close
    test dword [VA(proof_flags)],PF_WAVE_UNPREPARE
    jnz .close
    push dword 32
    push dword VA(wave_header)
    push dword [VA(active_wave)]
    call [VA(iat_waveOutUnprepareHeader)]
    mov [VA(proof_wave_unprepare)],eax
    test eax,eax
    jnz .failed
    or dword [VA(proof_flags)],PF_WAVE_UNPREPARE
.close:
    push dword [VA(active_wave)]
    call [VA(iat_waveOutClose)]
    mov [VA(proof_wave_close)],eax
    test eax,eax
    jnz .failed
    or dword [VA(proof_flags)],PF_WAVE_CLOSE
    mov dword [VA(active_wave)],0
    jmp .done
.failed:
    or dword [VA(proof_flags)],PF_FAILURE
.done:
    mov eax,[VA(wave_header)+16]
    mov [VA(proof_wave_flags)],eax
    popad
    pop ebp
    ret 4

; Publish a real512-byte file. Its tail reports the complete PREVIOUS attempt; current write/flush/close is never self-certified.
write_proof:
    pushad
    cmp dword [VA(writing_proof)],0
    jne .done
    mov dword [VA(writing_proof)],1
    inc dword [VA(proof_sequence)]
    call [VA(iat_GetTickCount)]
    mov [VA(proof_tick)],eax
    mov esi,VA(last_proof_attempt)
    mov edi,VA(proof_previous)
    mov ecx,9
    rep movsd
    mov edi,VA(last_proof_attempt)
    mov eax,-1
    mov ecx,9
    rep stosd
    mov eax,[VA(proof_sequence)]
    mov [VA(last_proof_attempt)],eax
    mov dword [VA(last_proof_attempt)+16],0
    push dword 0
    push dword 0x80
    push dword 2
    push dword 0
    push dword 1
    push dword 0x40000000
    push dword VA(proof_name)
    call [VA(iat_CreateFileA)]
    mov ebx,eax
    cmp eax,-1
    jne .opened
    call [VA(iat_GetLastError)]
    mov [VA(last_proof_attempt)+4],eax
    jmp .release
.opened:
    mov dword [VA(last_proof_attempt)+4],0
    mov dword [VA(proof_io_count)],0
    push dword 0
    push dword VA(proof_io_count)
    push dword 512
    push dword VA(proof)
    push ebx
    call [VA(iat_WriteFile)]
    mov [VA(last_proof_attempt)+8],eax
    test eax,eax
    jnz .write_ok
    call [VA(iat_GetLastError)]
    mov [VA(last_proof_attempt)+12],eax
    jmp .flush
.write_ok:
    mov dword [VA(last_proof_attempt)+12],0
.flush:
    mov eax,[VA(proof_io_count)]
    mov [VA(last_proof_attempt)+16],eax
    push ebx
    call [VA(iat_FlushFileBuffers)]
    mov [VA(last_proof_attempt)+20],eax
    test eax,eax
    jnz .flush_ok
    call [VA(iat_GetLastError)]
    mov [VA(last_proof_attempt)+24],eax
    jmp .close
.flush_ok:
    mov dword [VA(last_proof_attempt)+24],0
.close:
    push ebx
    call [VA(iat_CloseHandle)]
    mov [VA(last_proof_attempt)+28],eax
    test eax,eax
    jnz .close_ok
    call [VA(iat_GetLastError)]
    mov [VA(last_proof_attempt)+32],eax
    jmp .release
.close_ok:
    mov dword [VA(last_proof_attempt)+32],0
.release:
    mov dword [VA(writing_proof)],0
.done:
    popad
    ret

align 4,db 0
; Fixed binary proof ABI; NOT_CALLED remains FFFFFFFF for APIs never reached.
proof:
    db 'DOSE95P1'
    dd 1,512
proof_phase:dd 0
proof_flags:dd 0
proof_sequence:dd 0
proof_tick:dd 0
proof_version:dd 0
proof_versionex:dd -1
proof_version_error:dd -1
proof_osinfo:times 148 db 0
proof_screen_x:dd 0
proof_screen_y:dd 0
proof_mouse_present:dd 0
proof_mouse_buttons:dd 0
proof_hwnd:dd 0
proof_class_atom:dd 0
proof_class_error:dd -1
proof_create_error:dd -1
proof_client_rect:times 4 dd 0
proof_client_screen:times 2 dd 0
proof_client_return:dd -1
proof_screen_return:dd -1
proof_bits:dd 0
proof_planes:dd 0
proof_horzres:dd 0
proof_vertres:dd 0
proof_paints:dd 0
proof_paint_hdc:dd 0
proof_endpaint:dd -1
proof_brushes:times 3 dd 0
proof_brush_errors:times 3 dd -1
proof_fill_returns:times 3 dd -1
proof_write_handle:dd -1
proof_write_open_error:dd -1
proof_write_return:dd -1
proof_write_error:dd -1
proof_written:dd 0
proof_write_close:dd -1
proof_write_close_error:dd -1
proof_read_handle:dd -1
proof_read_open_error:dd -1
proof_read_return:dd -1
proof_read_error:dd -1
proof_read_count:dd 0
proof_eof_return:dd -1
proof_eof_error:dd -1
proof_eof_count:dd -1
proof_read_close:dd -1
proof_read_close_error:dd -1
proof_mismatch:dd -2
proof_expected_fnv:dd 0
proof_read_fnv:dd 0
proof_wave_open:dd -1
proof_wave_prepare:dd -1
proof_wave_write:dd -1
proof_wave_unprepare:dd -1
proof_wave_close:dd -1
proof_wave_reset:dd -1
proof_wave_devices:dd 0
proof_wave_handle:dd 0
proof_wave_flags:dd 0
proof_wave_polls:dd 0
proof_wave_done_tick:dd 0
proof_timer:dd 0
proof_killtimer:dd -1
proof_getmessage:dd -1
proof_getmessage_error:dd -1
proof_destroy_return:dd -1
proof_destroy_error:dd -1
proof_messages:dd 0
proof_previous:times 9 dd -1
proof_show_return:dd -1
proof_end:
%if proof_end-proof != 512
    %error "Proof ABI must remain exactly512 bytes"
%endif

; Native Win32 storage and immutable API parameters; no host or emulator interfaces.
module_handle:dd 0
active_wave:dd 0
writing_proof:dd 0
proof_io_count:dd 0
paint_call_mask:dd 0
last_proof_attempt:dd 0,-1,-1,-1,0,-1,-1,-1,-1
rgb_colors:dd 0x000000ff,0x0000ff00,0x00ff0000
window_class:dd 3,VA(window_proc),0,0,0,0,0,6,0,VA(window_name)
window_name:db 'DOSE95PROBE',0
window_title:db 'DOSE95PROBE',0
data_name:db 'C:\DOSE95.DAT',0
proof_name:db 'C:\DOSE95.BIN',0
align 4,db 0
message:times 28 db 0
paint_struct:times 64 db 0
bar_rect:times 16 db 0
wave_format:dw 1,1
dd 8000,8000
dw 1,8,0
align 4,db 0
wave_header:dd VA(wave_data),8000,0,0,0,0,0,0
expected_data:times 1027 db 0
read_data:times 1027 db 0
eof_byte:db 0
wave_data:times 8000 db 0
align 4,db 0
; Outside observation metadata contains RVAs, never fake service results.
probe_metadata:
    db 'D95META1'
    dw 1,16
    dd RVA(entry),RVA(window_proc),RVA(proof),512,RVA(wave_header),RVA(wave_data),8000,RVA(expected_data)
    dd RVA(read_data),1027,RVA(proof_hwnd),RVA(active_wave),RVA(writing_proof),RVA(poll_wave),RVA(paint_window),RVA(write_proof)
align 4,db 0
; PE imports bind only original classic Win32A/GDI/output-wave APIs.
import_directory:
    dd RVA(import_lookup_0),0,0,RVA(import_dll_0),RVA(import_iat_0)
    dd RVA(import_lookup_1),0,0,RVA(import_dll_1),RVA(import_iat_1)
    dd RVA(import_lookup_2),0,0,RVA(import_dll_2),RVA(import_iat_2)
    dd RVA(import_lookup_3),0,0,RVA(import_dll_3),RVA(import_iat_3)
    times 5 dd 0
import_directory_end:
align 4,db 0
import_lookup_0:
    dd RVA(import_name_GetVersion)
    dd RVA(import_name_GetVersionExA)
    dd RVA(import_name_GetCommandLineA)
    dd RVA(import_name_GetModuleHandleA)
    dd RVA(import_name_GetLastError)
    dd RVA(import_name_GetTickCount)
    dd RVA(import_name_CreateFileA)
    dd RVA(import_name_WriteFile)
    dd RVA(import_name_ReadFile)
    dd RVA(import_name_CloseHandle)
    dd RVA(import_name_FlushFileBuffers)
    dd RVA(import_name_ExitProcess)
    dd 0
import_iat_0:
iat_GetVersion:dd RVA(import_name_GetVersion)
iat_GetVersionExA:dd RVA(import_name_GetVersionExA)
iat_GetCommandLineA:dd RVA(import_name_GetCommandLineA)
iat_GetModuleHandleA:dd RVA(import_name_GetModuleHandleA)
iat_GetLastError:dd RVA(import_name_GetLastError)
iat_GetTickCount:dd RVA(import_name_GetTickCount)
iat_CreateFileA:dd RVA(import_name_CreateFileA)
iat_WriteFile:dd RVA(import_name_WriteFile)
iat_ReadFile:dd RVA(import_name_ReadFile)
iat_CloseHandle:dd RVA(import_name_CloseHandle)
iat_FlushFileBuffers:dd RVA(import_name_FlushFileBuffers)
iat_ExitProcess:dd RVA(import_name_ExitProcess)
    dd 0
import_dll_0:db 'KERNEL32.DLL',0
align 2,db 0
import_name_GetVersion:dw 0
    db 'GetVersion',0
align 2,db 0
import_name_GetVersionExA:dw 0
    db 'GetVersionExA',0
align 2,db 0
import_name_GetCommandLineA:dw 0
    db 'GetCommandLineA',0
align 2,db 0
import_name_GetModuleHandleA:dw 0
    db 'GetModuleHandleA',0
align 2,db 0
import_name_GetLastError:dw 0
    db 'GetLastError',0
align 2,db 0
import_name_GetTickCount:dw 0
    db 'GetTickCount',0
align 2,db 0
import_name_CreateFileA:dw 0
    db 'CreateFileA',0
align 2,db 0
import_name_WriteFile:dw 0
    db 'WriteFile',0
align 2,db 0
import_name_ReadFile:dw 0
    db 'ReadFile',0
align 2,db 0
import_name_CloseHandle:dw 0
    db 'CloseHandle',0
align 2,db 0
import_name_FlushFileBuffers:dw 0
    db 'FlushFileBuffers',0
align 2,db 0
import_name_ExitProcess:dw 0
    db 'ExitProcess',0
align 4,db 0
import_lookup_1:
    dd RVA(import_name_GetSystemMetrics)
    dd RVA(import_name_LoadCursorA)
    dd RVA(import_name_RegisterClassA)
    dd RVA(import_name_CreateWindowExA)
    dd RVA(import_name_ShowWindow)
    dd RVA(import_name_UpdateWindow)
    dd RVA(import_name_GetMessageA)
    dd RVA(import_name_TranslateMessage)
    dd RVA(import_name_DispatchMessageA)
    dd RVA(import_name_DefWindowProcA)
    dd RVA(import_name_BeginPaint)
    dd RVA(import_name_EndPaint)
    dd RVA(import_name_FillRect)
    dd RVA(import_name_GetClientRect)
    dd RVA(import_name_ClientToScreen)
    dd RVA(import_name_SetTimer)
    dd RVA(import_name_KillTimer)
    dd RVA(import_name_DestroyWindow)
    dd RVA(import_name_PostQuitMessage)
    dd 0
import_iat_1:
iat_GetSystemMetrics:dd RVA(import_name_GetSystemMetrics)
iat_LoadCursorA:dd RVA(import_name_LoadCursorA)
iat_RegisterClassA:dd RVA(import_name_RegisterClassA)
iat_CreateWindowExA:dd RVA(import_name_CreateWindowExA)
iat_ShowWindow:dd RVA(import_name_ShowWindow)
iat_UpdateWindow:dd RVA(import_name_UpdateWindow)
iat_GetMessageA:dd RVA(import_name_GetMessageA)
iat_TranslateMessage:dd RVA(import_name_TranslateMessage)
iat_DispatchMessageA:dd RVA(import_name_DispatchMessageA)
iat_DefWindowProcA:dd RVA(import_name_DefWindowProcA)
iat_BeginPaint:dd RVA(import_name_BeginPaint)
iat_EndPaint:dd RVA(import_name_EndPaint)
iat_FillRect:dd RVA(import_name_FillRect)
iat_GetClientRect:dd RVA(import_name_GetClientRect)
iat_ClientToScreen:dd RVA(import_name_ClientToScreen)
iat_SetTimer:dd RVA(import_name_SetTimer)
iat_KillTimer:dd RVA(import_name_KillTimer)
iat_DestroyWindow:dd RVA(import_name_DestroyWindow)
iat_PostQuitMessage:dd RVA(import_name_PostQuitMessage)
    dd 0
import_dll_1:db 'USER32.DLL',0
align 2,db 0
import_name_GetSystemMetrics:dw 0
    db 'GetSystemMetrics',0
align 2,db 0
import_name_LoadCursorA:dw 0
    db 'LoadCursorA',0
align 2,db 0
import_name_RegisterClassA:dw 0
    db 'RegisterClassA',0
align 2,db 0
import_name_CreateWindowExA:dw 0
    db 'CreateWindowExA',0
align 2,db 0
import_name_ShowWindow:dw 0
    db 'ShowWindow',0
align 2,db 0
import_name_UpdateWindow:dw 0
    db 'UpdateWindow',0
align 2,db 0
import_name_GetMessageA:dw 0
    db 'GetMessageA',0
align 2,db 0
import_name_TranslateMessage:dw 0
    db 'TranslateMessage',0
align 2,db 0
import_name_DispatchMessageA:dw 0
    db 'DispatchMessageA',0
align 2,db 0
import_name_DefWindowProcA:dw 0
    db 'DefWindowProcA',0
align 2,db 0
import_name_BeginPaint:dw 0
    db 'BeginPaint',0
align 2,db 0
import_name_EndPaint:dw 0
    db 'EndPaint',0
align 2,db 0
import_name_FillRect:dw 0
    db 'FillRect',0
align 2,db 0
import_name_GetClientRect:dw 0
    db 'GetClientRect',0
align 2,db 0
import_name_ClientToScreen:dw 0
    db 'ClientToScreen',0
align 2,db 0
import_name_SetTimer:dw 0
    db 'SetTimer',0
align 2,db 0
import_name_KillTimer:dw 0
    db 'KillTimer',0
align 2,db 0
import_name_DestroyWindow:dw 0
    db 'DestroyWindow',0
align 2,db 0
import_name_PostQuitMessage:dw 0
    db 'PostQuitMessage',0
align 4,db 0
import_lookup_2:
    dd RVA(import_name_CreateSolidBrush)
    dd RVA(import_name_DeleteObject)
    dd RVA(import_name_GetDeviceCaps)
    dd 0
import_iat_2:
iat_CreateSolidBrush:dd RVA(import_name_CreateSolidBrush)
iat_DeleteObject:dd RVA(import_name_DeleteObject)
iat_GetDeviceCaps:dd RVA(import_name_GetDeviceCaps)
    dd 0
import_dll_2:db 'GDI32.DLL',0
align 2,db 0
import_name_CreateSolidBrush:dw 0
    db 'CreateSolidBrush',0
align 2,db 0
import_name_DeleteObject:dw 0
    db 'DeleteObject',0
align 2,db 0
import_name_GetDeviceCaps:dw 0
    db 'GetDeviceCaps',0
align 4,db 0
import_lookup_3:
    dd RVA(import_name_waveOutGetNumDevs)
    dd RVA(import_name_waveOutOpen)
    dd RVA(import_name_waveOutPrepareHeader)
    dd RVA(import_name_waveOutWrite)
    dd RVA(import_name_waveOutUnprepareHeader)
    dd RVA(import_name_waveOutClose)
    dd RVA(import_name_waveOutReset)
    dd 0
import_iat_3:
iat_waveOutGetNumDevs:dd RVA(import_name_waveOutGetNumDevs)
iat_waveOutOpen:dd RVA(import_name_waveOutOpen)
iat_waveOutPrepareHeader:dd RVA(import_name_waveOutPrepareHeader)
iat_waveOutWrite:dd RVA(import_name_waveOutWrite)
iat_waveOutUnprepareHeader:dd RVA(import_name_waveOutUnprepareHeader)
iat_waveOutClose:dd RVA(import_name_waveOutClose)
iat_waveOutReset:dd RVA(import_name_waveOutReset)
    dd 0
import_dll_3:db 'WINMM.DLL',0
align 2,db 0
import_name_waveOutGetNumDevs:dw 0
    db 'waveOutGetNumDevs',0
align 2,db 0
import_name_waveOutOpen:dw 0
    db 'waveOutOpen',0
align 2,db 0
import_name_waveOutPrepareHeader:dw 0
    db 'waveOutPrepareHeader',0
align 2,db 0
import_name_waveOutWrite:dw 0
    db 'waveOutWrite',0
align 2,db 0
import_name_waveOutUnprepareHeader:dw 0
    db 'waveOutUnprepareHeader',0
align 2,db 0
import_name_waveOutClose:dw 0
    db 'waveOutClose',0
align 2,db 0
import_name_waveOutReset:dw 0
    db 'waveOutReset',0
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
