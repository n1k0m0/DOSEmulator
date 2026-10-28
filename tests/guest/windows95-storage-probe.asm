; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own synchronous classic Win32 storage guest. Never execute this PE on the host.
; Proof4096/DOSEIO95 and observation-only DOSEIOM1 metadata are versioned separately.
BITS 32
ORG 0
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
%define PAYLOAD_BYTES 262157
%define CHUNK_BYTES 32768
%define EVENT_CAPACITY 80
%define PF_CD 1
%define PF_SOURCE 2
%define PF_COPY 4
%define PF_FLUSH 8
%define PF_VERIFY 0x10
%define PF_SEEK 0x20
%define PF_EOF 0x40
%define PF_MISSING 0x80
%define PF_READONLY 0x100
%define PF_TEMP 0x200
%define PF_RENAME 0x400
%define PF_ENUM 0x800
%define PF_DELETE 0x1000
%define PF_CLOSED 0x2000
%define PF_FAILURE 0x80000000

; Prepare the actual next API record; the result helper immediately samples failure-only last error.
%macro API 2
    mov dword [VA(log_api)],%1
    mov dword [VA(log_tag)],%2
    mov dword [VA(log_arg0)],0
    mov dword [VA(log_arg1)],0
    mov dword [VA(log_extra)],0
    mov dword [VA(io_count)],0
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

; Native entry performs only the bounded own fixture scenario and exits normally after real handle cleanup.
entry:
    cld
    call initialize
    call checkpoint
    call discover_cd
    mov dword [VA(proof_phase)],2
    call checkpoint
    call make_directory
    call open_copy_files
    call copy_loop
    mov dword [VA(proof_phase)],3
    call flush_copy
    call checkpoint
    mov dword [VA(proof_phase)],4
    call verify_loop
    call checkpoint
    mov dword [VA(proof_phase)],5
    call seek_tests
    mov edi,VA(proof_main_handle)
    mov edx,42
    call close_file
    call must_bool
    call enumerate_main
    mov dword [VA(proof_phase)],6
    call negative_tests
    call checkpoint
    mov dword [VA(proof_phase)],7
    call temporary_tests
    call check_guards
    cmp dword [VA(proof_cd_handle)],-1
    jne failure_flags
    cmp dword [VA(proof_main_handle)],-1
    jne failure_flags
    cmp dword [VA(proof_temp_handle)],-1
    jne failure_flags
    cmp dword [VA(proof_find_handle)],-1
    jne failure_flags
    or dword [VA(proof_flags)],PF_CLOSED
    test dword [VA(proof_flags)],PF_FAILURE
    jnz failure_existing
    cmp dword [VA(proof_flags)],0x3fff
    jne failure_flags
    mov dword [VA(proof_terminal)],1
    jmp finalize

; Initializes exact payload checksum and actual guards before file operations.
initialize:
    pushad
    call [VA(iat_GetVersion)]
    mov [VA(proof_os_version)],eax
    mov edi,VA(io_guard_before)
    mov ecx,16
    mov al,0xd6
    rep stosb
    mov edi,VA(io_guard_after)
    mov ecx,16
    rep stosb
    xor esi,esi
    mov ebx,0x811c9dc5
.expected:
    mov eax,esi
    call expected_byte
    movzx eax,al
    xor ebx,eax
    imul ebx,ebx,0x01000193
    inc esi
    cmp esi,PAYLOAD_BYTES
    jb .expected
    mov [VA(proof_expected_fnv)],ebx
    cmp ebx,0x0c078ebe
    jne .bad
    popad
    ret
.bad:
    popad
    mov eax,8
    jmp failure

; Returns byte(EAX)=(29*i+7*(i>>8)+11*(i>>16)+53h)&FF while preserving loop registers.
expected_byte:
    push edx
    push ecx
    mov edx,eax
    mov ecx,eax
    imul eax,eax,29
    shr edx,8
    imul edx,edx,7
    add eax,edx
    shr ecx,16
    imul ecx,ecx,11
    add eax,ecx
    add eax,0x53
    pop ecx
    pop edx
    ret

; Searches real CDROM logical drives for the complete own64-byte marker, without a fixed drive letter.
discover_cd:
    mov dword [VA(proof_phase)],1
    API 1,0
    call [VA(iat_GetLogicalDrives)]
    call record_bool
    call must_bool
    mov eax,[VA(last_ret)]
    mov [VA(proof_drive_mask)],eax
    xor ebx,ebx
.next_drive:
    cmp ebx,26
    jae .not_found
    bt [VA(proof_drive_mask)],ebx
    jnc .advance
    mov eax,ebx
    shl eax,4
    lea edi,[VA(drive_table)+eax]
    mov eax,ebx
    add al,'A'
    mov [VA(root_path)],al
    mov [VA(cd_marker_path)],al
    mov [VA(cd_payload_path)],al
    inc dword [VA(proof_discovery_count)]
    push dword VA(root_path)
    call [VA(iat_GetDriveTypeA)]
    mov [edi],eax
    cmp eax,5
    jne .advance
    API 3,1
    mov [VA(log_arg0)],ebx
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 1
    push dword 0x80000000
    push dword VA(cd_marker_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    mov eax,[VA(last_ret)]
    mov [edi+4],eax
    mov edx,[VA(last_error)]
    mov [edi+8],edx
    cmp eax,-1
    je .advance
    mov [VA(proof_cd_handle)],eax
    API 4,1
    mov dword [VA(log_arg0)],64
    mov eax,[VA(proof_cd_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 64
    push dword VA(marker_buffer)
    push eax
    call [VA(iat_ReadFile)]
    call record_bool
    mov edx,[VA(last_ret)]
    mov eax,[VA(io_count)]
    mov [VA(marker_count)],eax
    mov [VA(marker_return)],edx
    push edi
    mov edi,VA(proof_cd_handle)
    mov edx,2
    call close_file
    pop edi
    cmp dword [VA(last_ret)],0
    je failure_api
    cmp dword [VA(marker_return)],0
    je .advance
    cmp dword [VA(marker_count)],64
    jne .advance
    push edi
    mov esi,VA(marker_expected)
    mov edi,VA(marker_buffer)
    mov ecx,64
    repe cmpsb
    pop edi
    jne .advance
    mov dword [edi+12],1
    mov eax,ebx
    add eax,'A'
    mov [VA(proof_cd_letter)],eax
    or dword [VA(proof_flags)],PF_CD
    ret
.advance:
    inc ebx
    jmp .next_drive
.not_found:
    mov eax,7
    jmp failure

; Creates the own directory without replacing a preexisting file.
make_directory:
    API 6,10
    push dword 0
    push dword VA(directory_path)
    call [VA(iat_CreateDirectoryA)]
    call record_bool
    mov eax,[VA(last_ret)]
    mov [VA(proof_directory_return)],eax
    mov eax,[VA(last_error)]
    mov [VA(proof_directory_error)],eax
    cmp dword [VA(last_ret)],0
    jne .done
    cmp eax,183
    jne failure_api
    API 16,10
    push dword VA(directory_path)
    call [VA(iat_GetFileAttributesA)]
    call record_handle
    cmp dword [VA(last_ret)],-1
    je failure_api
    test dword [VA(last_ret)],0x10
    jz failure_data
.done:
    ret

; Opens the existing own CD payload and a strictly CREATE_NEW long destination name.
open_copy_files:
    API 3,11
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 1
    push dword 0x80000000
    push dword VA(cd_payload_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_cd_handle)],eax
    API 3,12
    push dword 0
    push dword 0x80
    push dword 1
    push dword 0
    push dword 0
    push dword 0x40000000
    push dword VA(main_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_main_handle)],eax
    mov dword [VA(proof_current_offset)],0
    mov dword [VA(proof_cd_fnv)],0x811c9dc5
    ret

; Copies actual CD bytes, checks every source byte, and handles partial synchronous reads/writes with a bounded ledger.
copy_loop:
    mov eax,PAYLOAD_BYTES
    sub eax,[VA(proof_current_offset)]
    jz .all_read
    cmp eax,CHUNK_BYTES
    jbe .bounded
    mov eax,CHUNK_BYTES
.bounded:
    mov [VA(request_bytes)],eax
    API 4,20
    mov eax,[VA(request_bytes)]
    mov [VA(log_arg0)],eax
    mov edx,[VA(proof_current_offset)]
    mov [VA(log_arg1)],edx
    mov edx,[VA(proof_cd_handle)]
    mov [VA(log_extra)],edx
    push dword 0
    push dword VA(io_count)
    push eax
    push dword VA(io_buffer)
    push edx
    call [VA(iat_ReadFile)]
    call record_bool
    call must_bool
    inc dword [VA(proof_source_reads)]
    call check_guards
    mov ecx,[VA(io_count)]
    test ecx,ecx
    jz failure_count
    cmp ecx,[VA(request_bytes)]
    ja failure_count
    mov [VA(block_count)],ecx
    call verify_buffer
    mov eax,[VA(proof_cd_fnv)]
    mov esi,VA(io_buffer)
    call fnv
    mov [VA(proof_cd_fnv)],eax
    mov dword [VA(block_written)],0
.write:
    mov eax,[VA(block_count)]
    sub eax,[VA(block_written)]
    jz .next
    mov [VA(request_bytes)],eax
    API 5,21
    mov eax,[VA(request_bytes)]
    mov [VA(log_arg0)],eax
    mov edx,[VA(proof_copy_bytes)]
    mov [VA(log_arg1)],edx
    mov edx,[VA(proof_main_handle)]
    mov [VA(log_extra)],edx
    mov ecx,VA(io_buffer)
    add ecx,[VA(block_written)]
    push dword 0
    push dword VA(io_count)
    push eax
    push ecx
    push edx
    call [VA(iat_WriteFile)]
    call record_bool
    call must_bool
    inc dword [VA(proof_copy_writes)]
    call check_guards
    mov eax,[VA(io_count)]
    test eax,eax
    jz failure_count
    cmp eax,[VA(request_bytes)]
    ja failure_count
    add [VA(block_written)],eax
    add [VA(proof_copy_bytes)],eax
    jmp .write
.next:
    mov eax,[VA(block_count)]
    add [VA(proof_current_offset)],eax
    jmp copy_loop
.all_read:
    cmp dword [VA(proof_copy_bytes)],PAYLOAD_BYTES
    jne failure_count
    mov eax,[VA(proof_cd_fnv)]
    cmp eax,[VA(proof_expected_fnv)]
    jne failure_data
    or dword [VA(proof_flags)],PF_SOURCE|PF_COPY
    API 4,22
    mov dword [VA(log_arg0)],1
    mov dword [VA(log_arg1)],PAYLOAD_BYTES
    mov eax,[VA(proof_cd_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 1
    push dword VA(io_buffer)
    push eax
    call [VA(iat_ReadFile)]
    call record_bool
    call must_bool
    call check_guards
    cmp dword [VA(io_count)],0
    jne failure_count
    ret

; Flushes completed HDD data and closes both real copy handles before the long-name reopen.
flush_copy:
    API 7,30
    mov eax,[VA(proof_main_handle)]
    mov [VA(log_extra)],eax
    push eax
    call [VA(iat_FlushFileBuffers)]
    call record_bool
    call must_bool
    or dword [VA(proof_flags)],PF_FLUSH
    mov edi,VA(proof_cd_handle)
    mov edx,31
    call close_file
    call must_bool
    mov edi,VA(proof_main_handle)
    mov edx,32
    call close_file
    call must_bool
    ret

; Reopens the real long file and verifies every stored byte rather than trusting only checksums.
verify_loop:
    API 3,40
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 1
    push dword 0x80000000
    push dword VA(main_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_main_handle)],eax
    mov dword [VA(proof_current_offset)],0
    mov dword [VA(proof_hdd_fnv)],0x811c9dc5
.next:
    mov eax,PAYLOAD_BYTES
    sub eax,[VA(proof_current_offset)]
    jz .eof
    cmp eax,CHUNK_BYTES
    jbe .bounded
    mov eax,CHUNK_BYTES
.bounded:
    mov [VA(request_bytes)],eax
    API 4,41
    mov eax,[VA(request_bytes)]
    mov [VA(log_arg0)],eax
    mov edx,[VA(proof_current_offset)]
    mov [VA(log_arg1)],edx
    mov edx,[VA(proof_main_handle)]
    mov [VA(log_extra)],edx
    push dword 0
    push dword VA(io_count)
    push eax
    push dword VA(io_buffer)
    push edx
    call [VA(iat_ReadFile)]
    call record_bool
    call must_bool
    inc dword [VA(proof_verify_reads)]
    call check_guards
    mov ecx,[VA(io_count)]
    test ecx,ecx
    jz failure_count
    cmp ecx,[VA(request_bytes)]
    ja failure_count
    call verify_buffer
    mov eax,[VA(proof_hdd_fnv)]
    mov esi,VA(io_buffer)
    call fnv
    mov [VA(proof_hdd_fnv)],eax
    mov eax,[VA(io_count)]
    add [VA(proof_current_offset)],eax
    add [VA(proof_verify_bytes)],eax
    jmp .next
.eof:
    cmp dword [VA(proof_verify_bytes)],PAYLOAD_BYTES
    jne failure_count
    mov eax,[VA(proof_hdd_fnv)]
    cmp eax,[VA(proof_expected_fnv)]
    jne failure_data
    API 4,43
    mov dword [VA(log_arg0)],1
    mov dword [VA(log_arg1)],PAYLOAD_BYTES
    mov eax,[VA(proof_main_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 1
    push dword VA(io_buffer)
    push eax
    call [VA(iat_ReadFile)]
    call record_bool
    mov eax,[VA(last_ret)]
    mov [VA(proof_eof_return)],eax
    mov eax,[VA(io_count)]
    mov [VA(proof_eof_count)],eax
    call must_bool
    call check_guards
    cmp dword [VA(io_count)],0
    jne failure_count
    or dword [VA(proof_flags)],PF_VERIFY|PF_EOF
    ret

; Exercises real seeks across2KiB/64KiB plus end/current seeks with cleared-last-error ambiguity handling.
seek_tests:
    mov dword [VA(seek_offset)],2039
    mov dword [VA(seek_length)],37
    call seek_and_read
    or dword [VA(proof_seek_mask)],1
    mov dword [VA(seek_offset)],65529
    call seek_and_read
    or dword [VA(proof_seek_mask)],2
    mov dword [VA(seek_offset)],131063
    call seek_and_read
    or dword [VA(proof_seek_mask)],4
    mov dword [VA(seek_distance)],0
    mov dword [VA(seek_method)],2
    mov dword [VA(seek_offset)],PAYLOAD_BYTES
    call set_pointer
    mov dword [VA(seek_distance)],-13
    mov dword [VA(seek_method)],1
    mov dword [VA(seek_offset)],262144
    call set_pointer
    mov dword [VA(proof_current_offset)],262144
    mov dword [VA(seek_length)],13
    call read_seek_bytes
    or dword [VA(proof_seek_mask)],8
    cmp dword [VA(proof_seek_mask)],15
    jne failure_data
    API 4,53
    mov dword [VA(log_arg0)],1
    mov dword [VA(log_arg1)],PAYLOAD_BYTES
    mov eax,[VA(proof_main_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 1
    push dword VA(io_buffer)
    push eax
    call [VA(iat_ReadFile)]
    call record_bool
    call must_bool
    call check_guards
    cmp dword [VA(io_count)],0
    jne failure_count
    or dword [VA(proof_flags)],PF_SEEK
    ret

; Performs one absolute seek and exact readback against the same independent absolute payload function.
seek_and_read:
    mov eax,[VA(seek_offset)]
    mov [VA(seek_distance)],eax
    mov dword [VA(seek_method)],0
    call set_pointer
    mov eax,[VA(seek_offset)]
    mov [VA(proof_current_offset)],eax
    call read_seek_bytes
    ret

; Records the actual SetFilePointer return and immediate last error after explicit SetLastError0.
set_pointer:
    API 9,50
    mov eax,[VA(seek_distance)]
    mov [VA(log_arg0)],eax
    mov eax,[VA(seek_method)]
    mov [VA(log_arg1)],eax
    mov eax,[VA(proof_main_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    call [VA(iat_SetLastError)]
    push dword [VA(seek_method)]
    push dword 0
    push dword [VA(seek_distance)]
    push dword [VA(proof_main_handle)]
    call [VA(iat_SetFilePointer)]
    call record_pointer
    cmp dword [VA(last_ret)],-1
    jne .position
    cmp dword [VA(last_error)],0
    jne failure_api
.position:
    mov eax,[VA(last_ret)]
    cmp eax,[VA(seek_offset)]
    jne failure_data
    ret

; Reads one prescribed seek slice with exact count and complete content verification.
read_seek_bytes:
    API 4,51
    mov eax,[VA(seek_length)]
    mov [VA(log_arg0)],eax
    mov edx,[VA(proof_current_offset)]
    mov [VA(log_arg1)],edx
    mov edx,[VA(proof_main_handle)]
    mov [VA(log_extra)],edx
    push dword 0
    push dword VA(io_count)
    push eax
    push dword VA(io_buffer)
    push edx
    call [VA(iat_ReadFile)]
    call record_bool
    call must_bool
    call check_guards
    mov ecx,[VA(io_count)]
    cmp ecx,[VA(seek_length)]
    jne failure_count
    call verify_buffer
    mov eax,[VA(io_count)]
    add [VA(proof_current_offset)],eax
    ret

; Binds complete WIN32_FIND_DATAA to the actual long main name, exact size and nondirectory attribute.
enumerate_main:
    API 10,44
    push dword VA(find_main)
    push dword VA(main_path)
    call [VA(iat_FindFirstFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_find_handle)],eax
    test dword [VA(find_main)],0x10
    jnz failure_data
    cmp dword [VA(find_main)+28],0
    jne failure_data
    cmp dword [VA(find_main)+32],PAYLOAD_BYTES
    jne failure_count
    mov esi,VA(main_name)
    mov edi,VA(find_main)+44
    call strings_equal
    test eax,eax
    jz failure_data
    API 12,45
    mov eax,[VA(proof_find_handle)]
    mov [VA(log_extra)],eax
    push eax
    call [VA(iat_FindClose)]
    call record_bool
    mov dword [VA(proof_find_handle)],-1
    call must_bool
    ret

; Requires actual missing-file failure and real CD write rejection even if a write-open succeeds.
negative_tests:
    API 3,60
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 1
    push dword 0x80000000
    push dword VA(missing_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_missing_handle)],eax
    mov eax,[VA(last_error)]
    mov [VA(proof_missing_error)],eax
    cmp dword [VA(last_ret)],-1
    jne .unexpected_missing_handle
    cmp eax,2
    jne failure_api
    or dword [VA(proof_flags)],PF_MISSING
    API 3,61
    push dword 0
    push dword 0x80
    push dword 3
    push dword 0
    push dword 3
    push dword 0x40000000
    push dword VA(cd_payload_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_cd_write_return)],eax
    mov eax,[VA(last_error)]
    mov [VA(proof_cd_write_error)],eax
    cmp dword [VA(last_ret)],-1
    jne .actual_write
    test eax,eax
    jz failure_api
    mov dword [VA(proof_cd_write_mode)],1
    or dword [VA(proof_flags)],PF_READONLY
    ret
.actual_write:
    mov eax,[VA(last_ret)]
    mov [VA(proof_cd_handle)],eax
    API 5,62
    mov dword [VA(log_arg0)],1
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 1
    push dword VA(write_test_byte)
    push eax
    call [VA(iat_WriteFile)]
    call record_bool
    mov eax,[VA(last_ret)]
    mov [VA(proof_cd_write_return)],eax
    mov eax,[VA(last_error)]
    mov [VA(proof_cd_write_error)],eax
    mov eax,[VA(io_count)]
    mov [VA(proof_cd_write_count)],eax
    cmp dword [VA(last_ret)],0
    jne failure_unexpected
    test eax,eax
    jnz failure_count
    cmp dword [VA(last_error)],0
    je failure_api
    mov dword [VA(proof_cd_write_mode)],2
    mov edi,VA(proof_cd_handle)
    mov edx,63
    call close_file
    call must_bool
    or dword [VA(proof_flags)],PF_READONLY
    ret
.unexpected_missing_handle:
    mov eax,[VA(last_ret)]
    mov [VA(proof_cd_handle)],eax
    jmp failure_unexpected

; Creates one own temporary sibling, renames/enumerates/deletes it while retaining the main file.
temporary_tests:
    API 3,70
    push dword 0
    push dword 0x80
    push dword 1
    push dword 0
    push dword 0
    push dword 0x40000000
    push dword VA(temp_path)
    call [VA(iat_CreateFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_temp_handle)],eax
    xor esi,esi
.generate:
    mov eax,esi
    call expected_byte
    mov [VA(io_buffer)+esi],al
    inc esi
    cmp esi,197
    jb .generate
    API 5,71
    mov dword [VA(log_arg0)],197
    mov eax,[VA(proof_temp_handle)]
    mov [VA(log_extra)],eax
    push dword 0
    push dword VA(io_count)
    push dword 197
    push dword VA(io_buffer)
    push eax
    call [VA(iat_WriteFile)]
    call record_bool
    call must_bool
    call check_guards
    cmp dword [VA(io_count)],197
    jne failure_count
    API 7,72
    mov eax,[VA(proof_temp_handle)]
    mov [VA(log_extra)],eax
    push eax
    call [VA(iat_FlushFileBuffers)]
    call record_bool
    call must_bool
    mov edi,VA(proof_temp_handle)
    mov edx,73
    call close_file
    call must_bool
    or dword [VA(proof_flags)],PF_TEMP
    API 13,74
    push dword VA(renamed_path)
    push dword VA(temp_path)
    call [VA(iat_MoveFileA)]
    call record_bool
    call must_bool
    or dword [VA(proof_flags)],PF_RENAME
    API 16,75
    push dword VA(temp_path)
    call [VA(iat_GetFileAttributesA)]
    call record_handle
    cmp dword [VA(last_ret)],-1
    jne failure_unexpected
    cmp dword [VA(last_error)],2
    jne failure_api
    API 10,76
    push dword VA(find_temp)
    push dword VA(temp_pattern)
    call [VA(iat_FindFirstFileA)]
    call record_handle
    call must_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_find_handle)],eax
    cmp dword [VA(find_temp)+28],0
    jne failure_data
    cmp dword [VA(find_temp)+32],197
    jne failure_count
    test dword [VA(find_temp)],0x10
    jnz failure_data
    mov esi,VA(renamed_name)
    mov edi,VA(find_temp)+44
    call strings_equal
    test eax,eax
    jz failure_data
    API 11,77
    mov eax,[VA(proof_find_handle)]
    mov [VA(log_extra)],eax
    push dword VA(find_next)
    push eax
    call [VA(iat_FindNextFileA)]
    call record_bool
    cmp dword [VA(last_ret)],0
    jne failure_unexpected
    cmp dword [VA(last_error)],18
    jne failure_api
    API 12,78
    mov eax,[VA(proof_find_handle)]
    mov [VA(log_extra)],eax
    push eax
    call [VA(iat_FindClose)]
    call record_bool
    mov dword [VA(proof_find_handle)],-1
    call must_bool
    or dword [VA(proof_flags)],PF_ENUM
    API 14,79
    push dword VA(renamed_path)
    call [VA(iat_DeleteFileA)]
    call record_bool
    call must_bool
    API 16,80
    push dword VA(renamed_path)
    call [VA(iat_GetFileAttributesA)]
    call record_handle
    mov eax,[VA(last_ret)]
    mov [VA(proof_deleted_attributes)],eax
    cmp eax,-1
    jne failure_unexpected
    cmp dword [VA(last_error)],2
    jne failure_api
    or dword [VA(proof_flags)],PF_DELETE
    ret

; Verifies every returned byte by its absolute offset; checksum equality is supplementary only.
verify_buffer:
    pushad
    mov ecx,[VA(io_count)]
    mov edi,VA(io_buffer)
    mov esi,[VA(proof_current_offset)]
.next:
    test ecx,ecx
    jz .done
    mov eax,esi
    call expected_byte
    cmp al,[edi]
    jne .mismatch
    inc edi
    inc esi
    dec ecx
    jmp .next
.done:
    popad
    ret
.mismatch:
    mov [VA(proof_mismatch)],esi
    popad
    jmp failure_data

; Tests all32 actual guard bytes and records observed sentinels plus independent check/failure counts.
check_guards:
    pushad
    inc dword [VA(proof_guard_checks)]
    mov eax,[VA(io_guard_before)]
    mov [VA(proof_guard_before_first)],eax
    mov eax,[VA(io_guard_before)+12]
    mov [VA(proof_guard_before_last)],eax
    mov eax,[VA(io_guard_after)]
    mov [VA(proof_guard_after_first)],eax
    mov eax,[VA(io_guard_after)+12]
    mov [VA(proof_guard_after_last)],eax
    mov esi,VA(io_guard_before)
    mov ecx,16
.before:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .before
    mov esi,VA(io_guard_after)
    mov ecx,16
.after:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .after
    popad
    ret
.bad:
    inc dword [VA(proof_guard_failures)]
    popad
    mov eax,6
    jmp failure

; Updates modulo32-bit FNV1a in EAX for actual ESI/ECX bytes while preserving pointer/count registers.
fnv:
    push esi
    push ecx
    push edx
.next:
    test ecx,ecx
    jz .done
    movzx edx,byte [esi]
    xor eax,edx
    imul eax,eax,0x01000193
    inc esi
    dec ecx
    jmp .next
.done:
    pop edx
    pop ecx
    pop esi
    ret

; Compares bounded own NUL names; a mismatch stops before reading past either known expected terminator.
strings_equal:
    push esi
    push edi
    mov ecx,260
.next:
    mov al,[esi]
    cmp al,[edi]
    jne .different
    inc esi
    inc edi
    test al,al
    jz .same
    loop .next
.different:
    xor eax,eax
    jmp .done
.same:
    mov eax,1
.done:
    pop edi
    pop esi
    ret

; Samples BOOL return and failure-only GetLastError immediately before another API can alter it.
record_bool:
    mov [VA(last_ret)],eax
    mov dword [VA(last_error)],0
    test eax,eax
    jnz append_record
    call [VA(iat_GetLastError)]
    mov [VA(last_error)],eax
    jmp append_record

; Records only FFFFFFFF as an invalid HANDLE/attribute result; zero handles remain valid.
record_handle:
    mov [VA(last_ret)],eax
    mov dword [VA(last_error)],0
    cmp eax,-1
    jne append_record
    call [VA(iat_GetLastError)]
    mov [VA(last_error)],eax
    jmp append_record

; Samples the explicitly cleared last-error state after SetFilePointer, retaining any FFFFFFFF ambiguity.
record_pointer:
    mov [VA(last_ret)],eax
    call [VA(iat_GetLastError)]
    mov [VA(last_error)],eax
    jmp append_record

; Stores actual finished API outcomes; overflow sets a bounded failure without overwriting adjacent proof data.
append_record:
    pushad
    mov eax,[VA(log_api)]
    mov [VA(proof_last_api)],eax
    mov ecx,[VA(proof_event_count)]
    cmp ecx,EVENT_CAPACITY
    jae .overflow
    shl ecx,5
    lea edi,[VA(event_table)+ecx]
    mov [edi],eax
    mov eax,[VA(log_tag)]
    mov [edi+4],eax
    mov eax,[VA(last_ret)]
    mov [edi+8],eax
    mov eax,[VA(last_error)]
    mov [edi+12],eax
    mov eax,[VA(io_count)]
    mov [edi+16],eax
    mov eax,[VA(log_arg0)]
    mov [edi+20],eax
    mov eax,[VA(log_arg1)]
    mov [edi+24],eax
    mov eax,[VA(log_extra)]
    mov [edi+28],eax
    inc dword [VA(proof_event_count)]
    popad
    ret
.overflow:
    or dword [VA(proof_flags)],PF_FAILURE
    cmp dword [VA(proof_failure_stage)],0
    jne .done
    mov eax,[VA(proof_phase)]
    mov [VA(proof_failure_stage)],eax
    mov dword [VA(proof_failure_code)],9
    mov eax,[VA(proof_last_api)]
    mov [VA(proof_failure_api)],eax
.done:
    popad
    ret

; Requires actual nonzero BOOL success rather than the particular integer value1.
must_bool:
    cmp dword [VA(last_ret)],0
    je failure_api
    ret

; Applies only the documented INVALID_HANDLE_VALUE sentinel.
must_handle:
    cmp dword [VA(last_ret)],-1
    je failure_api
    ret

; Closes an owned file pointer EDI using tag EDX and records only a real CloseHandle call.
close_file:
    pushad
    mov ebx,[edi]
    cmp ebx,-1
    je .not_open
    API 8,0
    mov [VA(log_tag)],edx
    mov [VA(log_extra)],ebx
    push ebx
    call [VA(iat_CloseHandle)]
    call record_bool
    mov dword [edi],-1
.not_open:
    popad
    ret

; Marks a true API failure while preserving its actual return/error in the ledger.
failure_api:
    mov eax,1
    jmp failure
; Marks a count/no-progress violation separately from a raw API error.
failure_count:
    mov eax,2
    jmp failure
; Marks a byte/name/position mismatch separately from raw API errors.
failure_data:
    mov eax,3
    jmp failure
; Marks unexpected success of a required negative operation.
failure_unexpected:
    mov eax,4
    jmp failure
; Marks a missing expected terminal flag after individual operations were checked.
failure_flags:
    mov eax,5
    jmp failure
; Retains an earlier bounded ledger error as the first cause.
failure_existing:
    mov eax,9
; Routes any first failure through actual cleanup and normal proof writing.
failure:
    or dword [VA(proof_flags)],PF_FAILURE
    mov dword [VA(proof_terminal)],-1
    cmp dword [VA(proof_failure_stage)],0
    jne finalize
    mov [VA(proof_failure_code)],eax
    mov eax,[VA(proof_phase)]
    mov [VA(proof_failure_stage)],eax
    mov eax,[VA(proof_last_api)]
    mov [VA(proof_failure_api)],eax

; Cleans up only real open handles and writes two terminal attempts to bind one completed prior terminal writer.
finalize:
    cmp dword [VA(proof_cd_handle)],-1
    je .main
    mov edi,VA(proof_cd_handle)
    mov edx,90
    call close_file
    call cleanup_bool
.main:
    cmp dword [VA(proof_main_handle)],-1
    je .temp
    mov edi,VA(proof_main_handle)
    mov edx,91
    call close_file
    call cleanup_bool
.temp:
    cmp dword [VA(proof_temp_handle)],-1
    je .find
    mov edi,VA(proof_temp_handle)
    mov edx,92
    call close_file
    call cleanup_bool
.find:
    cmp dword [VA(proof_find_handle)],-1
    je .closed
    API 12,93
    mov eax,[VA(proof_find_handle)]
    mov [VA(log_extra)],eax
    push eax
    call [VA(iat_FindClose)]
    call record_bool
    mov dword [VA(proof_find_handle)],-1
    call cleanup_bool
.closed:
    mov dword [VA(proof_phase)],99
    call checkpoint
    call checkpoint
    xor eax,eax
    cmp dword [VA(proof_terminal)],1
    setne al
    push eax
    call [VA(iat_ExitProcess)]

; Records a true cleanup failure without recursive cleanup or a fabricated successful terminal status.
cleanup_bool:
    cmp dword [VA(last_ret)],0
    jne .done
    or dword [VA(proof_flags)],PF_FAILURE
    mov dword [VA(proof_terminal)],-1
    cmp dword [VA(proof_failure_stage)],0
    jne .done
    mov eax,[VA(proof_phase)]
    mov [VA(proof_failure_stage)],eax
    mov eax,[VA(proof_last_api)]
    mov [VA(proof_failure_api)],eax
    mov dword [VA(proof_failure_code)],1
.done:
    ret

; A checkpoint is real file I/O; failed creation/write/flush/close exits rather than advancing a success proof.
checkpoint:
    call write_proof
    cmp dword [VA(writer_good)],1
    jne exit_writer_failure
    ret

; Writes only the own root proof and carries the previous completed create/write/flush/close outcomes into the next attempt.
write_proof:
    pushad
    inc dword [VA(proof_sequence)]
    mov esi,VA(writer_last)
    mov edi,VA(proof_previous_writer)
    mov ecx,16
    rep movsd
    mov edi,VA(writer_last)
    mov ecx,16
    mov eax,-1
    rep stosd
    mov eax,[VA(proof_sequence)]
    mov [VA(writer_last)],eax
    mov dword [VA(writer_good)],1
    mov dword [VA(writer_count)],0
    push dword 0
    push dword 0x80
    push dword 2
    push dword 0
    push dword 0
    push dword 0x40000000
    push dword VA(proof_path)
    call [VA(iat_CreateFileA)]
    mov [VA(writer_handle)],eax
    mov [VA(writer_last)+4],eax
    cmp eax,-1
    jne .write
    call [VA(iat_GetLastError)]
    mov [VA(writer_last)+8],eax
    mov dword [VA(writer_good)],0
    jmp .done
.write:
    mov dword [VA(writer_last)+8],0
    push dword 0
    push dword VA(writer_count)
    push dword 4096
    push dword VA(proof)
    push dword [VA(writer_handle)]
    call [VA(iat_WriteFile)]
    mov [VA(writer_last)+12],eax
    test eax,eax
    jnz .count
    call [VA(iat_GetLastError)]
    mov [VA(writer_last)+16],eax
    mov dword [VA(writer_good)],0
    jmp .flush
.count:
    mov dword [VA(writer_last)+16],0
    cmp dword [VA(writer_count)],4096
    je .flush
    mov dword [VA(writer_good)],0
.flush:
    mov eax,[VA(writer_count)]
    mov [VA(writer_last)+20],eax
    push dword [VA(writer_handle)]
    call [VA(iat_FlushFileBuffers)]
    mov [VA(writer_last)+24],eax
    test eax,eax
    jnz .flushed
    call [VA(iat_GetLastError)]
    mov [VA(writer_last)+28],eax
    mov dword [VA(writer_good)],0
    jmp .close
.flushed:
    mov dword [VA(writer_last)+28],0
.close:
    push dword [VA(writer_handle)]
    call [VA(iat_CloseHandle)]
    mov [VA(writer_last)+32],eax
    test eax,eax
    jnz .closed
    call [VA(iat_GetLastError)]
    mov [VA(writer_last)+36],eax
    mov dword [VA(writer_good)],0
    jmp .done
.closed:
    mov dword [VA(writer_last)+36],0
.done:
    popad
    ret

; Does not synthesize a successful file when the actual proofwriter failed.
exit_writer_failure:
    push dword 1
    call [VA(iat_ExitProcess)]

align 4
; Fixed4096-byte little-endian own proof; persisted API records and observation buffers have separate fixed regions.
proof:
    db 'DOSEIO95'
    dd 1,4096
proof_phase:dd 0
proof_flags:dd 0
proof_sequence:dd 0
proof_terminal:dd 0
proof_failure_stage:dd 0
proof_failure_api:dd 0
proof_failure_code:dd 0
proof_os_version:dd 0
proof_cd_letter:dd 0
proof_drive_mask:dd 0
proof_event_count:dd 0
    dd EVENT_CAPACITY
proof_copy_bytes:dd 0
proof_verify_bytes:dd 0
proof_cd_fnv:dd 0
proof_hdd_fnv:dd 0
proof_expected_fnv:dd 0
proof_mismatch:dd -1
    dd PAYLOAD_BYTES,CHUNK_BYTES
proof_last_api:dd 0
proof_current_offset:dd 0
proof_cd_handle:dd -1
proof_main_handle:dd -1
proof_temp_handle:dd -1
proof_find_handle:dd -1
proof_seek_mask:dd 0
proof_discovery_count:dd 0
proof_source_reads:dd 0
proof_copy_writes:dd 0
proof_verify_reads:dd 0
proof_eof_return:dd -1
proof_eof_count:dd -1
proof_missing_handle:dd -1
proof_missing_error:dd -1
proof_cd_write_mode:dd 0
proof_cd_write_return:dd -1
proof_cd_write_error:dd -1
proof_cd_write_count:dd 0
proof_deleted_attributes:dd -1
proof_directory_return:dd -1
proof_directory_error:dd -1
    times 0xd0-($-proof) db 0
proof_guard_checks:dd 0
proof_guard_failures:dd 0
proof_guard_before_first:dd 0
proof_guard_before_last:dd 0
proof_guard_after_first:dd 0
proof_guard_after_last:dd 0
    times 0x100-($-proof) db 0
event_table:times EVENT_CAPACITY*8 dd -1
; Each actually queried drive retains type/open-result/open-error/marker-match; other entries stay unqueried.
drive_table:times 26 dd -1,-1,-1,0
; The actual marker ReadFile destination is persisted directly, rather than replaced by a generated expected copy.
marker_buffer:times 64 db 0
    times 0xd00-($-proof) db 0
find_main:times 320 db 0
find_temp:times 320 db 0
proof_previous_writer:times 16 dd -1
    times 4096-($-proof) db 0
proof_end:
%if proof_end-proof != 4096
    %error Wrong_storage_proof_size
%endif
%if event_table-proof != 0x100 || drive_table-proof != 0xb00 || marker_buffer-proof != 0xca0 || find_main-proof != 0xd00 || find_temp-proof != 0xe40 || proof_previous_writer-proof != 0xf80
    %error Wrong_storage_proof_layout
%endif
%if proof_phase-proof != 0x10 || proof_terminal-proof != 0x1c || proof_current_offset-proof != 0x64 || proof_guard_checks-proof != 0xd0
    %error Wrong_storage_proof_header
%endif

align 4
; Private runtime scratch remains ordinary guest memory and does not claim persisted service success.
last_ret:dd -1
last_error:dd -1
log_api:dd 0
log_tag:dd 0
log_arg0:dd 0
log_arg1:dd 0
log_extra:dd 0
io_count:dd 0
request_bytes:dd 0
block_count:dd 0
block_written:dd 0
marker_count:dd 0
marker_return:dd 0
seek_offset:dd 0
seek_length:dd 0
seek_distance:dd 0
seek_method:dd 0
writer_last:times 16 dd -1
writer_handle:dd -1
writer_count:dd 0
writer_good:dd 0
find_next:times 320 db 0
root_path:db 'A:\',0
cd_marker_path:db 'A:\DOSEIO.MRK',0
cd_payload_path:db 'A:\PAYLOAD.BIN',0
directory_path:db 'C:\DOSEIO',0
main_name:db 'Long File Name 95.bin',0
main_path:db 'C:\DOSEIO\Long File Name 95.bin',0
temp_path:db 'C:\DOSEIO\Temporary File 95.tmp',0
renamed_name:db 'Renamed File 95.tmp',0
renamed_path:db 'C:\DOSEIO\Renamed File 95.tmp',0
temp_pattern:db 'C:\DOSEIO\*.tmp',0
missing_path:db 'C:\DOSEIO\Missing File 95.none',0
proof_path:db 'C:\DOSEIO.BIN',0
; This attempted byte is itself the first byte of the own payload; no original OS-disc content is a write target.
write_test_byte:db 0x53
marker_expected:
    db 'DOSE95-STORAGE-CD-1',13,10,'OWN DATA ONLY; ISO9660',13,10,'PAYLOAD=262157',13,10,'   '
%if $-marker_expected != 64
    %error Wrong_storage_marker_size
%endif
align 4
io_guard_before:times 16 db 0xd6
io_buffer:times CHUNK_BYTES db 0
io_guard_after:times 16 db 0xd6

; Observation-only own-image metadata contains RVAs/sizes, never a fake OS-service response.
probe_metadata:
    db 'DOSEIOM1'
    dd 1,20
    dd RVA(entry),RVA(proof),4096,RVA(proof_phase),RVA(proof_terminal)
    dd RVA(event_table),32,EVENT_CAPACITY,RVA(io_buffer),CHUNK_BYTES
    dd RVA(cd_payload_path),RVA(main_path),RVA(marker_expected),64,PAYLOAD_BYTES
    dd RVA(copy_loop),RVA(verify_loop),RVA(write_proof),RVA(finalize),RVA(proof_current_offset)

; Only classic KERNEL32 imports; all ReadFile/WriteFile calls use synchronous handles and NULL OVERLAPPED.
import_directory:
    dd RVA(import_lookup_0),0,0,RVA(import_dll_0),RVA(import_iat_0)
    times 5 dd 0
import_directory_end:
align 4
import_lookup_0:
    dd RVA(import_name_GetLogicalDrives)
    dd RVA(import_name_GetDriveTypeA)
    dd RVA(import_name_CreateFileA)
    dd RVA(import_name_ReadFile)
    dd RVA(import_name_WriteFile)
    dd RVA(import_name_CreateDirectoryA)
    dd RVA(import_name_FlushFileBuffers)
    dd RVA(import_name_CloseHandle)
    dd RVA(import_name_SetFilePointer)
    dd RVA(import_name_FindFirstFileA)
    dd RVA(import_name_FindNextFileA)
    dd RVA(import_name_FindClose)
    dd RVA(import_name_MoveFileA)
    dd RVA(import_name_DeleteFileA)
    dd RVA(import_name_GetLastError)
    dd RVA(import_name_GetFileAttributesA)
    dd RVA(import_name_SetLastError)
    dd RVA(import_name_GetVersion)
    dd RVA(import_name_ExitProcess)
    dd 0
import_iat_0:
iat_GetLogicalDrives:dd RVA(import_name_GetLogicalDrives)
iat_GetDriveTypeA:dd RVA(import_name_GetDriveTypeA)
iat_CreateFileA:dd RVA(import_name_CreateFileA)
iat_ReadFile:dd RVA(import_name_ReadFile)
iat_WriteFile:dd RVA(import_name_WriteFile)
iat_CreateDirectoryA:dd RVA(import_name_CreateDirectoryA)
iat_FlushFileBuffers:dd RVA(import_name_FlushFileBuffers)
iat_CloseHandle:dd RVA(import_name_CloseHandle)
iat_SetFilePointer:dd RVA(import_name_SetFilePointer)
iat_FindFirstFileA:dd RVA(import_name_FindFirstFileA)
iat_FindNextFileA:dd RVA(import_name_FindNextFileA)
iat_FindClose:dd RVA(import_name_FindClose)
iat_MoveFileA:dd RVA(import_name_MoveFileA)
iat_DeleteFileA:dd RVA(import_name_DeleteFileA)
iat_GetLastError:dd RVA(import_name_GetLastError)
iat_GetFileAttributesA:dd RVA(import_name_GetFileAttributesA)
iat_SetLastError:dd RVA(import_name_SetLastError)
iat_GetVersion:dd RVA(import_name_GetVersion)
iat_ExitProcess:dd RVA(import_name_ExitProcess)
    dd 0
import_dll_0:db 'KERNEL32.DLL',0
align 2
import_name_GetLogicalDrives:dw 0
    db 'GetLogicalDrives',0
align 2
import_name_GetDriveTypeA:dw 0
    db 'GetDriveTypeA',0
align 2
import_name_CreateFileA:dw 0
    db 'CreateFileA',0
align 2
import_name_ReadFile:dw 0
    db 'ReadFile',0
align 2
import_name_WriteFile:dw 0
    db 'WriteFile',0
align 2
import_name_CreateDirectoryA:dw 0
    db 'CreateDirectoryA',0
align 2
import_name_FlushFileBuffers:dw 0
    db 'FlushFileBuffers',0
align 2
import_name_CloseHandle:dw 0
    db 'CloseHandle',0
align 2
import_name_SetFilePointer:dw 0
    db 'SetFilePointer',0
align 2
import_name_FindFirstFileA:dw 0
    db 'FindFirstFileA',0
align 2
import_name_FindNextFileA:dw 0
    db 'FindNextFileA',0
align 2
import_name_FindClose:dw 0
    db 'FindClose',0
align 2
import_name_MoveFileA:dw 0
    db 'MoveFileA',0
align 2
import_name_DeleteFileA:dw 0
    db 'DeleteFileA',0
align 2
import_name_GetLastError:dw 0
    db 'GetLastError',0
align 2
import_name_GetFileAttributesA:dw 0
    db 'GetFileAttributesA',0
align 2
import_name_SetLastError:dw 0
    db 'SetLastError',0
align 2
import_name_GetVersion:dw 0
    db 'GetVersion',0
align 2
import_name_ExitProcess:dw 0
    db 'ExitProcess',0
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
