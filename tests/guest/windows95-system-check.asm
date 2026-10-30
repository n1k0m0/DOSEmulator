; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own read-only Windows95/i386 diagnostic. Assemble as PE32; NEVER run on the host.
; Registry access is KEY_READ only. Only the NEW C:\DOSEINFO.BIN file is written.
; No registry repair, mode change, software licensing query, window, timer or endpoint.
; ABI: 64-byte DOSEINF1 header, then aligned 32-byte TLV headers and bounded payloads.
; Header DWORDs after magic: version,headerBytes,count,total,flags,stage,GetVersion,
; guardFailures,droppedRecords,indexedModes,pciNodes,rootDisplayNodes,classNodes,FNV1a(records).
; TLV DWORDs: kind,context,actualResult,meta,arg0,arg1,payloadBytes,guardOK.
; 1=void API(struct),2=metric/DC,3=EnumDisplaySettings,4=key open(path),
; 5=RegEnumKey termination(path),6=RegQueryValueEx(raw),8=RegCloseKey.
; A result FFFFFFFF means a void return (kind1) or no call at an explicit cap(kind5).
; Registry query meta=actual type,arg0=allowlisted name ID,arg1=actual returned byte count.
; Failed/ERROR_MORE_DATA query buffers are NEVER interpreted or copied to the report.
; Classic Win4.0 DEVMODEA=148B: size36,extra38,fields40,bpp104,width108,
; height112,flags116,Hz120; six Win95 ICM/reserved DWORDs124..147, no later panning.
; https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsa
; https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-devmodea
; https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regqueryvalueexa
BITS 32
ORG 0
%define IMAGE_BASE 0x00400000
%define RVA(x) ((x)-section_start+0x1000)
%define VA(x) (IMAGE_BASE+RVA(x))
%define NOT_CALLED 0xffffffff
%define KEY_READ 0x20019
%define HKLM 0x80000002
%define REPORT_CAPACITY 262144
%define BUFFER_CAPACITY 2048
%define MODE_BYTES 148
%define MODE_LIMIT 512
%define FAMILY_LIMIT 128
%define INSTANCE_LIMIT 32
%define CLASS_LIMIT 64
%define F_GUARD 1
%define F_REPORT_FULL 2
%define F_MODE_CAP 4
%define F_REG_CAP 8
%define F_BAD_LENGTH 16
%define F_REG_ERROR 32

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
db 'DOSE System Check requires Windows 95.',13,10,'$'
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

; No output or registry access occurs on a non-Win95 OS, even if launched accidentally.
entry:
    cld
    call [VA(iat_GetVersion)]
    mov [VA(header_version_raw)],eax
    test eax,0x80000000
    jz unsupported_os
    cmp ax,4
    jne unsupported_os
    mov dword [VA(header_stage)],1
    call system_information
    call display_modes
    mov dword [VA(scan_group)],1
    mov dword [VA(base_path)],VA(path_pci)
    call scan_devices
    mov dword [VA(scan_group)],2
    mov dword [VA(base_path)],VA(path_root)
    call scan_devices
    call scan_display_class
    call scan_own_run_values
    mov dword [VA(header_stage)],2
    call finish_report
    call write_report
    push eax
    call [VA(iat_ExitProcess)]
unsupported_os:
    push dword 3
    call [VA(iat_ExitProcess)]

; Prepare exactly ECX writable API bytes between two16-byte D6 guards.
prepare_buffer:
    pushad
    mov [VA(buffer_size)],ecx
    mov edi,VA(scratch)-16
    add ecx,32
    mov al,0xd6
    rep stosb
    mov edi,VA(scratch)
    mov ecx,[VA(buffer_size)]
    xor eax,eax
    rep stosb
    popad
    ret

; Check every guard byte. EAX=1/0 and last_guard are explicit, not assumed from API status.
check_buffer:
    pushad
    mov esi,VA(scratch)-16
    mov ecx,16
.before:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .before
    mov esi,VA(scratch)
    add esi,[VA(buffer_size)]
    mov ecx,16
.after:
    cmp byte [esi],0xd6
    jne .bad
    inc esi
    loop .after
    mov dword [VA(last_guard)],1
    mov dword [esp+28],1
    popad
    ret
.bad:
    inc dword [VA(header_guard_failures)]
    or dword [VA(header_flags)],F_GUARD
    mov dword [VA(last_guard)],0
    mov dword [esp+28],0
    popad
    ret

; Reset only the next TLV descriptor; no payload or API state is normalized.
reset_record:
    pushad
    mov edi,VA(record_fields)
    mov ecx,9
    xor eax,eax
    rep stosd
    mov dword [VA(r_guard)],1
    popad
    ret

; Append one complete TLV or count a dropped record without crossing the report boundary.
emit:
    pushad
    mov eax,[VA(r_length)]
    add eax,3
    and eax,~3
    add eax,32
    mov edx,[VA(header_total)]
    mov ebx,REPORT_CAPACITY
    sub ebx,edx
    cmp eax,ebx
    ja .full
    mov edi,VA(report)
    add edi,edx
    mov esi,VA(record_fields)
    mov ecx,8
    rep movsd
    mov esi,[VA(r_data)]
    mov ecx,[VA(r_length)]
    test ecx,ecx
    jz .padding
    rep movsb
.padding:
    mov ecx,[VA(r_length)]
    neg ecx
    and ecx,3
    xor ebx,ebx
.pad:
    test ecx,ecx
    jz .stored
    mov [edi],bl
    inc edi
    dec ecx
    jmp .pad
.stored:
    add [VA(header_total)],eax
    inc dword [VA(header_count)]
    popad
    ret
.full:
    or dword [VA(header_flags)],F_REPORT_FULL
    inc dword [VA(header_dropped)]
    popad
    ret

; ESI NUL text, ECX maximum bytes. EAX=length excluding NUL, or FFFFFFFF.
bounded_length:
    push esi
    push ecx
    xor eax,eax
.next:
    test ecx,ecx
    jz .bad
    cmp byte [esi],0
    je .done
    inc esi
    inc eax
    dec ecx
    jmp .next
.bad:
    mov eax,NOT_CALLED
.done:
    pop ecx
    pop esi
    ret

; ESI parent, EDX child, EDI own512-byte target. Never truncate a registry path.
join_path:
    pushad
    mov ebx,edx
    mov ecx,511
.parent:
    lodsb
    test al,al
    jz .slash
    test ecx,ecx
    jz .bad
    stosb
    dec ecx
    jmp .parent
.slash:
    test ecx,ecx
    jz .bad
    mov al,'\'
    stosb
    dec ecx
    mov esi,ebx
.child:
    lodsb
    test al,al
    jz .done
    test ecx,ecx
    jz .bad
    stosb
    dec ecx
    jmp .child
.done:
    mov byte [edi],0
    mov dword [esp+28],1
    popad
    ret
.bad:
    mov byte [edi],0
    or dword [VA(header_flags)],F_BAD_LENGTH
    mov dword [esp+28],0
    popad
    ret

; ESI actual bytes, EDI own NUL prefix, ECX available actual bytes. Case-insensitive ASCII.
ascii_prefix:
    push esi
    push edi
    push ecx
    push edx
.next:
    mov dl,[edi]
    test dl,dl
    jz .yes
    test ecx,ecx
    jz .no
    mov al,[esi]
    or al,0x20
    or dl,0x20
    cmp al,dl
    jne .no
    inc esi
    inc edi
    dec ecx
    jmp .next
.yes:
    mov eax,1
    jmp .done
.no:
    xor eax,eax
.done:
    pop edx
    pop ecx
    pop edi
    pop esi
    ret

; Actual Windows local civil time,32-bit MEMORYSTATUS and current display metrics/DC caps.
system_information:
    mov ecx,16
    call prepare_buffer
    push dword VA(scratch)
    call [VA(iat_GetLocalTime)]
    call check_buffer
    call reset_record
    mov dword [VA(r_kind)],1
    mov dword [VA(r_context)],1
    mov dword [VA(r_result)],NOT_CALLED
    mov dword [VA(r_arg0)],16
    mov dword [VA(r_arg1)],1
    mov dword [VA(r_length)],16
    mov eax,[VA(last_guard)]
    mov [VA(r_guard)],eax
    mov dword [VA(r_data)],VA(scratch)
    call emit
    mov ecx,32
    call prepare_buffer
    mov dword [VA(scratch)],32
    push dword VA(scratch)
    call [VA(iat_GlobalMemoryStatus)]
    call check_buffer
    call reset_record
    mov dword [VA(r_kind)],1
    mov dword [VA(r_context)],2
    mov dword [VA(r_result)],NOT_CALLED
    mov dword [VA(r_arg0)],32
    mov dword [VA(r_arg1)],1
    mov dword [VA(r_length)],32
    mov eax,[VA(last_guard)]
    mov [VA(r_guard)],eax
    mov dword [VA(r_data)],VA(scratch)
    call emit
    xor ebx,ebx
.metric:
    push ebx
    call [VA(iat_GetSystemMetrics)]
    call reset_record
    mov dword [VA(r_kind)],2
    mov [VA(r_context)],ebx
    mov [VA(r_result)],eax
    call emit
    inc ebx
    cmp ebx,2
    jb .metric
    push dword 0
    call [VA(iat_GetDC)]
    mov [VA(display_dc)],eax
    call reset_record
    mov dword [VA(r_kind)],2
    mov dword [VA(r_context)],0x1000
    mov [VA(r_result)],eax
    call emit
    cmp dword [VA(display_dc)],0
    je .done
    mov esi,VA(cap_ids)
.cap:
    mov ebx,[esi]
    cmp ebx,NOT_CALLED
    je .release
    push ebx
    push dword [VA(display_dc)]
    call [VA(iat_GetDeviceCaps)]
    call reset_record
    mov dword [VA(r_kind)],2
    mov edx,ebx
    or edx,0x2000
    mov [VA(r_context)],edx
    mov [VA(r_result)],eax
    call emit
    add esi,4
    jmp .cap
.release:
    push dword [VA(display_dc)]
    push dword 0
    call [VA(iat_ReleaseDC)]
    call reset_record
    mov dword [VA(r_kind)],2
    mov dword [VA(r_context)],0x1001
    mov [VA(r_result)],eax
    call emit
.done:
    ret

; EDX mode index or ENUM_CURRENT/REGISTRY_SETTINGS. Never calls ChangeDisplaySettings.
one_mode:
    pushad
    mov [VA(mode_index)],edx
    mov ecx,MODE_BYTES
    call prepare_buffer
    mov word [VA(scratch)+32],0x0400
    mov word [VA(scratch)+36],MODE_BYTES
    mov word [VA(scratch)+38],0
    push dword 0
    call [VA(iat_SetLastError)]
    push dword VA(scratch)
    push dword [VA(mode_index)]
    push dword 0
    call [VA(iat_EnumDisplaySettingsA)]
    mov [VA(api_result)],eax
    mov dword [VA(api_error)],0
    test eax,eax
    jnz .guard
    call [VA(iat_GetLastError)]
    mov [VA(api_error)],eax
.guard:
    call check_buffer
    call reset_record
    mov dword [VA(r_kind)],3
    mov eax,[VA(mode_index)]
    mov [VA(r_context)],eax
    mov eax,[VA(api_result)]
    mov [VA(r_result)],eax
    mov eax,[VA(api_error)]
    mov [VA(r_meta)],eax
    mov dword [VA(r_arg0)],MODE_BYTES
    mov eax,[VA(scratch)+36]
    mov [VA(r_arg1)],eax
    mov dword [VA(r_length)],MODE_BYTES
    mov eax,[VA(last_guard)]
    mov [VA(r_guard)],eax
    mov dword [VA(r_data)],VA(scratch)
    call emit
    mov eax,[VA(api_result)]
    mov [esp+28],eax
    popad
    ret
; Enumerate bounded indexed modes and preserve the original special-request results separately.
display_modes:
    xor ebx,ebx
.next:
    mov edx,ebx
    call one_mode
    test eax,eax
    jz .special
    inc dword [VA(header_modes)]
    inc ebx
    cmp ebx,MODE_LIMIT
    jb .next
    or dword [VA(header_flags)],F_MODE_CAP
.special:
    mov edx,0xffffffff
    call one_mode
    mov edx,0xfffffffe
    call one_mode
    ret

; EBX parent HKEY, ESI subkey, EDI own DWORD handle result. KEY_READ only.
open_key:
    mov dword [edi],0
    push edi
    push dword KEY_READ
    push dword 0
    push esi
    push ebx
    call [VA(iat_RegOpenKeyExA)]
    mov [VA(api_result)],eax
    ret

; EAX actual open LSTATUS, ESI complete HKLM-relative path, EDX group, ECX parent context.
record_open:
    pushad
    call reset_record
    inc dword [VA(next_context)]
    mov eax,[VA(next_context)]
    mov [VA(r_context)],eax
    mov dword [VA(r_kind)],4
    mov eax,[esp+28]
    mov [VA(r_result)],eax
    mov eax,[esp+20]
    mov [VA(r_meta)],eax
    mov eax,[esp+24]
    mov [VA(r_arg0)],eax
    mov dword [VA(r_arg1)],KEY_READ
    mov ecx,512
    call bounded_length
    cmp eax,NOT_CALLED
    je .bad
    inc eax
    mov [VA(r_length)],eax
    mov [VA(r_data)],esi
    call emit
    mov eax,[VA(next_context)]
    mov [esp+28],eax
    popad
    ret
.bad:
    or dword [VA(header_flags)],F_BAD_LENGTH
    mov dword [esp+28],0
    popad
    ret

; EBX handle, ECX recorded context, EDX group. Every reported result is the actual RegCloseKey LSTATUS.
close_key:
    pushad
    push ebx
    call [VA(iat_RegCloseKey)]
    call reset_record
    mov dword [VA(r_kind)],8
    mov [VA(r_result)],eax
    mov eax,[esp+24]
    mov [VA(r_context)],eax
    mov eax,[esp+20]
    mov [VA(r_meta)],eax
    call emit
    popad
    ret

; EBX parent, EDX index. The complete256-byte name output and both guards are bounded.
enum_name:
    mov ecx,256
    call prepare_buffer
    push dword 256
    push dword VA(scratch)
    push edx
    push ebx
    call [VA(iat_RegEnumKeyA)]
    mov [VA(api_result)],eax
    call check_buffer
    mov eax,[VA(api_result)]
    test eax,eax
    jnz .done
    mov esi,VA(scratch)
    mov ecx,256
    call bounded_length
    cmp eax,NOT_CALLED
    jne .length
    or dword [VA(header_flags)],F_BAD_LENGTH
    mov dword [VA(last_guard)],0
.length:
    mov [VA(name_length)],eax
.done:
    mov eax,[VA(api_result)]
    ret

; EBX HKEY, ESI fixed allowlisted value name. ERROR_MORE_DATA never licenses a larger read.
quiet_query:
    mov ecx,512
    call prepare_buffer
    mov dword [VA(query_type)],NOT_CALLED
    mov dword [VA(query_bytes)],512
    push dword VA(query_bytes)
    push dword VA(scratch)
    push dword VA(query_type)
    push dword 0
    push esi
    push ebx
    call [VA(iat_RegQueryValueExA)]
    mov [VA(api_result)],eax
    call check_buffer
    cmp dword [VA(api_result)],0
    jne .done
    cmp dword [VA(query_bytes)],512
    jbe .done
    or dword [VA(header_flags)],F_BAD_LENGTH
.done:
    mov eax,[VA(api_result)]
    ret

; ESI own ID/name-pointer table. current_key/current_context stay fixed during the complete value list.
dump_values:
    pushad
    mov ebp,esi
.next:
    cmp dword [ebp],0
    je .done
    mov esi,[ebp+4]
    mov ebx,[VA(current_key)]
    call quiet_query
    call reset_record
    mov dword [VA(r_kind)],6
    mov eax,[VA(current_context)]
    mov [VA(r_context)],eax
    mov eax,[VA(api_result)]
    mov [VA(r_result)],eax
    mov eax,[VA(query_type)]
    mov [VA(r_meta)],eax
    mov eax,[ebp]
    mov [VA(r_arg0)],eax
    mov eax,[VA(query_bytes)]
    mov [VA(r_arg1)],eax
    mov eax,[VA(last_guard)]
    mov [VA(r_guard)],eax
    cmp dword [VA(api_result)],0
    jne .record
    cmp dword [VA(query_bytes)],512
    ja .record
    mov eax,[VA(query_bytes)]
    mov [VA(r_length)],eax
    mov dword [VA(r_data)],VA(scratch)
.record:
    call emit
    add ebp,8
    jmp .next
.done:
    popad
    ret

; Root instances qualify only from an actual Class=Display or Driver=Display\... value.
root_is_display:
    mov ebx,[VA(node_handle)]
    mov esi,VA(value_Class)
    call quiet_query
    test eax,eax
    jnz .driver
    cmp dword [VA(last_guard)],1
    jne .driver
    cmp dword [VA(query_bytes)],512
    ja .driver
    cmp dword [VA(query_type)],1
    je .class_string
    cmp dword [VA(query_type)],2
    jne .driver
.class_string:
    mov esi,VA(scratch)
    mov edi,VA(text_Display)
    mov ecx,[VA(query_bytes)]
    call ascii_prefix
    test eax,eax
    jz .driver
    cmp dword [VA(query_bytes)],7
    je .yes
    cmp byte [VA(scratch)+7],0
    je .yes
.driver:
    mov ebx,[VA(node_handle)]
    mov esi,VA(value_Driver)
    call quiet_query
    test eax,eax
    jnz .no
    cmp dword [VA(last_guard)],1
    jne .no
    cmp dword [VA(query_bytes)],512
    ja .no
    cmp dword [VA(query_type)],1
    je .driver_string
    cmp dword [VA(query_type)],2
    jne .no
.driver_string:
    mov esi,VA(scratch)
    mov edi,VA(text_DisplayPrefix)
    mov ecx,[VA(query_bytes)]
    call ascii_prefix
    ret
.yes:
    mov eax,1
    ret
.no:
    xor eax,eax
    ret

; Set up one recorded node, query only its fixed hardware/driver fields and its LogConf resource child.
dump_node:
    mov eax,[VA(node_open_status)]
    mov esi,VA(node_path)
    mov edx,[VA(scan_group)]
    mov ecx,[VA(base_context)]
    call record_open
    mov [VA(node_context)],eax
    mov [VA(current_context)],eax
    mov eax,[VA(node_handle)]
    mov [VA(current_key)],eax
    mov esi,VA(node_values)
    call dump_values
    mov esi,VA(node_path)
    mov edx,VA(text_LogConf)
    mov edi,VA(child_path)
    call join_path
    test eax,eax
    jz .done
    mov ebx,[VA(node_handle)]
    mov esi,VA(text_LogConf)
    mov edi,VA(child_handle)
    call open_key
    mov [VA(child_open_status)],eax
    mov esi,VA(child_path)
    mov edx,[VA(scan_group)]
    mov ecx,[VA(node_context)]
    call record_open
    mov [VA(child_context)],eax
    cmp dword [VA(child_open_status)],0
    jne .done
    mov [VA(current_context)],eax
    mov eax,[VA(child_handle)]
    mov [VA(current_key)],eax
    mov esi,VA(resource_values)
    call dump_values
    mov ebx,[VA(child_handle)]
    mov ecx,[VA(child_context)]
    mov edx,[VA(scan_group)]
    call close_key
.done:
    ret

; ESI parent path, ECX context, EAX terminal APIstatus (or NOT_CALLED at cap), EDX index, EBX cap.
record_enumeration:
    pushad
    call reset_record
    mov dword [VA(r_kind)],5
    mov eax,[esp+24]
    mov [VA(r_context)],eax
    mov eax,[esp+28]
    mov [VA(r_result)],eax
    mov eax,[VA(scan_group)]
    mov [VA(r_meta)],eax
    mov eax,[esp+20]
    mov [VA(r_arg0)],eax
    mov eax,[esp+16]
    mov [VA(r_arg1)],eax
    mov eax,[VA(last_guard)]
    mov [VA(r_guard)],eax
    mov ecx,512
    call bounded_length
    cmp eax,NOT_CALLED
    je .done
    inc eax
    mov [VA(r_length)],eax
    mov [VA(r_data)],esi
    call emit
.done:
    popad
    ret

; Bounded Enum\PCI (only5333:8811 families) and Enum\Root (Display instances only).
scan_devices:
    mov ebx,HKLM
    mov esi,[VA(base_path)]
    mov edi,VA(base_handle)
    call open_key
    mov [VA(base_open_status)],eax
    mov esi,[VA(base_path)]
    mov edx,[VA(scan_group)]
    xor ecx,ecx
    call record_open
    mov [VA(base_context)],eax
    cmp dword [VA(base_open_status)],0
    jne .done
    mov dword [VA(top_index)],0
.family:
    mov ebx,[VA(base_handle)]
    mov edx,[VA(top_index)]
    call enum_name
    test eax,eax
    jnz .top_end
    cmp dword [VA(last_guard)],1
    jne .top_end
    cmp dword [VA(scan_group)],1
    jne .accepted_family
    mov esi,VA(scratch)
    mov edi,VA(text_PciPrefix)
    mov ecx,[VA(name_length)]
    call ascii_prefix
    test eax,eax
    jz .next_family
    cmp byte [VA(scratch)+17],0
    je .accepted_family
    cmp byte [VA(scratch)+17],'&'
    jne .next_family
.accepted_family:
    mov esi,[VA(base_path)]
    mov edx,VA(scratch)
    mov edi,VA(family_path)
    call join_path
    test eax,eax
    jz .next_family
    mov ebx,[VA(base_handle)]
    mov esi,VA(scratch)
    mov edi,VA(family_handle)
    call open_key
    mov [VA(family_open_status)],eax
    mov dword [VA(family_context)],0
    cmp dword [VA(scan_group)],1
    jne .opened_family
    mov esi,VA(family_path)
    mov edx,1
    mov ecx,[VA(base_context)]
    call record_open
    mov [VA(family_context)],eax
.opened_family:
    cmp dword [VA(family_open_status)],0
    jne .next_family
    mov dword [VA(sub_index)],0
.instance:
    mov ebx,[VA(family_handle)]
    mov edx,[VA(sub_index)]
    call enum_name
    test eax,eax
    jnz .sub_end
    cmp dword [VA(last_guard)],1
    jne .sub_end
    mov esi,VA(family_path)
    mov edx,VA(scratch)
    mov edi,VA(node_path)
    call join_path
    test eax,eax
    jz .next_instance
    mov ebx,[VA(family_handle)]
    mov esi,VA(scratch)
    mov edi,VA(node_handle)
    call open_key
    mov [VA(node_open_status)],eax
    mov dword [VA(node_context)],0
    test eax,eax
    jnz .node_open_failed
    cmp dword [VA(scan_group)],1
    je .pci_node
    call root_is_display
    test eax,eax
    jz .close_unselected_node
    inc dword [VA(header_root_nodes)]
    jmp .selected_node
.pci_node:
    inc dword [VA(header_pci_nodes)]
.selected_node:
    call dump_node
    mov ebx,[VA(node_handle)]
    mov ecx,[VA(node_context)]
    mov edx,[VA(scan_group)]
    call close_key
    jmp .next_instance
.node_open_failed:
    cmp dword [VA(scan_group)],1
    jne .next_instance
    mov esi,VA(node_path)
    mov edx,1
    mov ecx,[VA(base_context)]
    call record_open
    jmp .next_instance
.close_unselected_node:
    push dword [VA(node_handle)]
    call [VA(iat_RegCloseKey)]
.next_instance:
    inc dword [VA(sub_index)]
    cmp dword [VA(sub_index)],INSTANCE_LIMIT
    jb .instance
    or dword [VA(header_flags)],F_REG_CAP
    mov eax,NOT_CALLED
    jmp .sub_summary
.sub_end:
    mov eax,[VA(api_result)]
.sub_summary:
    mov esi,VA(family_path)
    mov ecx,[VA(family_context)]
    mov edx,[VA(sub_index)]
    mov ebx,INSTANCE_LIMIT
    call record_enumeration
    cmp dword [VA(family_context)],0
    je .close_unselected_family
    mov ebx,[VA(family_handle)]
    mov ecx,[VA(family_context)]
    mov edx,[VA(scan_group)]
    call close_key
    jmp .next_family
.close_unselected_family:
    push dword [VA(family_handle)]
    call [VA(iat_RegCloseKey)]
.next_family:
    inc dword [VA(top_index)]
    cmp dword [VA(top_index)],FAMILY_LIMIT
    jb .family
    or dword [VA(header_flags)],F_REG_CAP
    mov eax,NOT_CALLED
    jmp .top_summary
.top_end:
    mov eax,[VA(api_result)]
.top_summary:
    mov esi,[VA(base_path)]
    mov ecx,[VA(base_context)]
    mov edx,[VA(top_index)]
    mov ebx,FAMILY_LIMIT
    call record_enumeration
    mov ebx,[VA(base_handle)]
    mov ecx,[VA(base_context)]
    mov edx,[VA(scan_group)]
    call close_key
.done:
    ret

; The display class is limited to numeric0000..9999 children; no software hive enumeration.
scan_display_class:
    mov dword [VA(scan_group)],3
    mov ebx,HKLM
    mov esi,VA(path_class)
    mov edi,VA(base_handle)
    call open_key
    mov [VA(base_open_status)],eax
    mov esi,VA(path_class)
    mov edx,3
    xor ecx,ecx
    call record_open
    mov [VA(base_context)],eax
    cmp dword [VA(base_open_status)],0
    jne .done
    mov dword [VA(top_index)],0
.next:
    mov ebx,[VA(base_handle)]
    mov edx,[VA(top_index)]
    call enum_name
    test eax,eax
    jnz .end
    cmp dword [VA(last_guard)],1
    jne .end
    cmp dword [VA(name_length)],4
    jne .advance
    xor ebx,ebx
.digit:
    mov al,[VA(scratch)+ebx]
    cmp al,'0'
    jb .advance
    cmp al,'9'
    ja .advance
    inc ebx
    cmp ebx,4
    jb .digit
    mov esi,VA(path_class)
    mov edx,VA(scratch)
    mov edi,VA(node_path)
    call join_path
    test eax,eax
    jz .advance
    mov ebx,[VA(base_handle)]
    mov esi,VA(scratch)
    mov edi,VA(node_handle)
    call open_key
    mov [VA(node_open_status)],eax
    mov esi,VA(node_path)
    mov edx,3
    mov ecx,[VA(base_context)]
    call record_open
    mov [VA(node_context)],eax
    cmp dword [VA(node_open_status)],0
    jne .advance
    inc dword [VA(header_class_nodes)]
    mov [VA(current_context)],eax
    mov eax,[VA(node_handle)]
    mov [VA(current_key)],eax
    mov esi,VA(class_values)
    call dump_values
    mov esi,VA(node_path)
    mov edx,VA(text_Default)
    mov edi,VA(child_path)
    call join_path
    test eax,eax
    jz .close
    mov ebx,[VA(node_handle)]
    mov esi,VA(text_Default)
    mov edi,VA(child_handle)
    call open_key
    mov [VA(child_open_status)],eax
    mov esi,VA(child_path)
    mov edx,3
    mov ecx,[VA(node_context)]
    call record_open
    mov [VA(child_context)],eax
    cmp dword [VA(child_open_status)],0
    jne .close
    mov [VA(current_context)],eax
    mov eax,[VA(child_handle)]
    mov [VA(current_key)],eax
    mov esi,VA(default_values)
    call dump_values
    mov ebx,[VA(child_handle)]
    mov ecx,[VA(child_context)]
    mov edx,3
    call close_key
.close:
    mov ebx,[VA(node_handle)]
    mov ecx,[VA(node_context)]
    mov edx,3
    call close_key
.advance:
    inc dword [VA(top_index)]
    cmp dword [VA(top_index)],CLASS_LIMIT
    jb .next
    or dword [VA(header_flags)],F_REG_CAP
    mov eax,NOT_CALLED
    jmp .summary
.end:
    mov eax,[VA(api_result)]
.summary:
    mov esi,VA(path_class)
    mov ecx,[VA(base_context)]
    mov edx,[VA(top_index)]
    mov ebx,CLASS_LIMIT
    call record_enumeration
    mov ebx,[VA(base_handle)]
    mov ecx,[VA(base_context)]
    mov edx,3
    call close_key
.done:
    ret

; Only two own value names: the requested DOSETIME and the existing helper's actual DOSE Time name.
scan_own_run_values:
    mov ebx,HKLM
    mov esi,VA(path_run)
    mov edi,VA(base_handle)
    call open_key
    mov [VA(base_open_status)],eax
    mov esi,VA(path_run)
    mov edx,4
    xor ecx,ecx
    call record_open
    mov [VA(base_context)],eax
    cmp dword [VA(base_open_status)],0
    jne .done
    mov [VA(current_context)],eax
    mov eax,[VA(base_handle)]
    mov [VA(current_key)],eax
    mov esi,VA(run_values)
    call dump_values
    mov ebx,[VA(base_handle)]
    mov ecx,[VA(base_context)]
    mov edx,4
    call close_key
.done:
    ret

; Hash the exact complete TLV bytes, excluding the fixed header that contains this checksum.
finish_report:
    mov esi,VA(report)+64
    mov ecx,[VA(header_total)]
    sub ecx,64
    mov eax,0x811c9dc5
.byte:
    test ecx,ecx
    jz .done
    movzx edx,byte [esi]
    xor eax,edx
    imul eax,eax,0x01000193
    inc esi
    dec ecx
    jmp .byte
.done:
    mov [VA(header_fnv)],eax
    ret

; CREATE_NEW preserves any earlier own report. Exit0 additionally requires actual exact Write/Flush/Close success.
write_report:
    mov dword [VA(writer_status)],1
    push dword 0
    push dword 0x80
    push dword 1
    push dword 0
    push dword 1
    push dword 0x40000000
    push dword VA(output_path)
    call [VA(iat_CreateFileA)]
    mov [VA(output_handle)],eax
    cmp eax,-1
    je .failed
    push dword 0
    push dword VA(written)
    push dword [VA(header_total)]
    push dword VA(report)
    push eax
    call [VA(iat_WriteFile)]
    test eax,eax
    jz .close_failed
    mov eax,[VA(written)]
    cmp eax,[VA(header_total)]
    jne .close_failed
    push dword [VA(output_handle)]
    call [VA(iat_FlushFileBuffers)]
    test eax,eax
    jz .close_failed
    push dword [VA(output_handle)]
    call [VA(iat_CloseHandle)]
    test eax,eax
    jz .failed
    mov dword [VA(writer_status)],0
    xor eax,eax
    test dword [VA(header_flags)],F_GUARD|F_REPORT_FULL|F_BAD_LENGTH
    jz .return
    mov eax,2
.return:
    mov [VA(exit_code)],eax
    ret
.close_failed:
    push dword [VA(output_handle)]
    call [VA(iat_CloseHandle)]
.failed:
    mov eax,1
    mov [VA(exit_code)],eax
    ret

align 4,db 0
report:
    db 'DOSEINF1'
    dd 1,64
header_count: dd 0
header_total: dd 64
header_flags: dd 0
header_stage: dd 0
header_version_raw: dd 0
header_guard_failures: dd 0
header_dropped: dd 0
header_modes: dd 0
header_pci_nodes: dd 0
header_root_nodes: dd 0
header_class_nodes: dd 0
header_fnv: dd 0
    times REPORT_CAPACITY-($-report) db 0
record_fields:
r_kind: dd 0
r_context: dd 0
r_result: dd 0
r_meta: dd 0
r_arg0: dd 0
r_arg1: dd 0
r_length: dd 0
r_guard: dd 1
r_data: dd 0
buffer_size: dd 0
last_guard: dd 1
    times 16 db 0
scratch: times BUFFER_CAPACITY+16 db 0
api_result: dd 0
api_error: dd 0
query_type: dd 0
query_bytes: dd 0
name_length: dd 0
mode_index: dd 0
display_dc: dd 0
base_path: dd 0
base_handle: dd 0
base_open_status: dd 0
base_context: dd 0
family_handle: dd 0
family_open_status: dd 0
family_context: dd 0
node_handle: dd 0
node_open_status: dd 0
node_context: dd 0
child_handle: dd 0
child_open_status: dd 0
child_context: dd 0
current_key: dd 0
current_context: dd 0
next_context: dd 0
scan_group: dd 0
top_index: dd 0
sub_index: dd 0
family_path: times 512 db 0
node_path: times 512 db 0
child_path: times 512 db 0
output_handle: dd 0
written: dd 0
writer_status: dd NOT_CALLED
exit_code: dd NOT_CALLED
cap_ids: dd 8,10,12,14,38,88,90,116,NOT_CALLED
output_path: db 'C:\DOSEINFO.BIN',0
path_pci: db 'Enum\PCI',0
path_root: db 'Enum\Root',0
path_class: db 'System\CurrentControlSet\Services\Class\Display',0
path_run: db 'Software\Microsoft\Windows\CurrentVersion\Run',0
text_PciPrefix: db 'VEN_5333&DEV_8811',0
text_Display: db 'Display',0
text_DisplayPrefix: db 'Display\',0
text_LogConf: db 'LogConf',0
text_Default: db 'Default',0

%macro VALUE_NAME 2
value_ %+ %1: db %2,0
%endmacro
VALUE_NAME Class,'Class'
VALUE_NAME Driver,'Driver'
VALUE_NAME DeviceDesc,'DeviceDesc'
VALUE_NAME FriendlyName,'FriendlyName'
VALUE_NAME Mfg,'Mfg'
VALUE_NAME HardwareID,'HardwareID'
VALUE_NAME CompatibleIDs,'CompatibleIDs'
VALUE_NAME ConfigFlags,'ConfigFlags'
VALUE_NAME Problem,'Problem'
VALUE_NAME StatusFlags,'StatusFlags'
VALUE_NAME ClassGUID,'ClassGUID'
VALUE_NAME BootConfig,'BootConfig'
VALUE_NAME AllocConfig,'AllocConfig'
VALUE_NAME ForcedConfig,'ForcedConfig'
VALUE_NAME BasicConfigVector,'BasicConfigVector'
VALUE_NAME DriverDesc,'DriverDesc'
VALUE_NAME DevLoader,'DevLoader'
VALUE_NAME DeviceVxDs,'DeviceVxDs'
VALUE_NAME InfPath,'InfPath'
VALUE_NAME InfSection,'InfSection'
VALUE_NAME InfOption,'InfOption'
VALUE_NAME ProviderName,'ProviderName'
VALUE_NAME DriverVersion,'DriverVersion'
VALUE_NAME MatchingDeviceId,'MatchingDeviceId'
VALUE_NAME drv,'drv'
VALUE_NAME vdd,'vdd'
VALUE_NAME minivdd,'minivdd'
VALUE_NAME ExtLoader,'ExtLoader'
VALUE_NAME Mode,'Mode'
VALUE_NAME RawMode,'RawMode'
VALUE_NAME DOSETIME,'DOSETIME'
VALUE_NAME DOSE_Time,'DOSE Time'
align 4,db 0
%macro VALUE 2
    dd %1,VA(value_ %+ %2)
%endmacro
node_values:
    VALUE 1,Class
    VALUE 2,Driver
    VALUE 3,DeviceDesc
    VALUE 4,FriendlyName
    VALUE 5,Mfg
    VALUE 6,HardwareID
    VALUE 7,CompatibleIDs
    VALUE 8,ConfigFlags
    VALUE 9,Problem
    VALUE 10,StatusFlags
    VALUE 11,ClassGUID
    dd 0,0
resource_values:
    VALUE 12,BootConfig
    VALUE 13,AllocConfig
    VALUE 14,ForcedConfig
    VALUE 15,BasicConfigVector
    dd 0,0
class_values:
    VALUE 20,DriverDesc
    VALUE 21,DevLoader
    VALUE 22,DeviceVxDs
    VALUE 23,InfPath
    VALUE 24,InfSection
    VALUE 25,InfOption
    VALUE 26,ProviderName
    VALUE 27,DriverVersion
    VALUE 28,MatchingDeviceId
    dd 0,0
default_values:
    VALUE 30,drv
    VALUE 31,vdd
    VALUE 32,minivdd
    VALUE 33,ExtLoader
    VALUE 34,Mode
    VALUE 35,RawMode
    dd 0,0
run_values:
    VALUE 100,DOSETIME
    VALUE 101,DOSE_Time
    dd 0,0

align 4,db 0
; Passive own RVA metadata permits Root to diagnose a partial run without changing the guest's execution.
metadata:
    db 'DOSESCM1'
    dw 1,8
    dd RVA(entry),RVA(report),REPORT_CAPACITY,RVA(header_stage)
    dd RVA(header_total),RVA(writer_status),RVA(exit_code),RVA(scratch)

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
    %1 GetLocalTime
    %1 GlobalMemoryStatus
    %1 GetLastError
    %1 SetLastError
    %1 CreateFileA
    %1 WriteFile
    %1 FlushFileBuffers
    %1 CloseHandle
    %1 ExitProcess
%endmacro
%macro USER_IMPORTS 1
    %1 GetSystemMetrics
    %1 GetDC
    %1 ReleaseDC
    %1 EnumDisplaySettingsA
%endmacro
%macro GDI_IMPORTS 1
    %1 GetDeviceCaps
%endmacro
%macro REG_IMPORTS 1
    %1 RegOpenKeyExA
    %1 RegEnumKeyA
    %1 RegQueryValueExA
    %1 RegCloseKey
%endmacro
align 4,db 0
import_directory:
    dd RVA(lookup_kernel),0,0,RVA(dll_kernel),RVA(iat_kernel)
    dd RVA(lookup_user),0,0,RVA(dll_user),RVA(iat_user)
    dd RVA(lookup_gdi),0,0,RVA(dll_gdi),RVA(iat_gdi)
    dd RVA(lookup_registry),0,0,RVA(dll_registry),RVA(iat_registry)
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
lookup_registry:
    REG_IMPORTS LOOKUP
    dd 0
iat_registry:
    REG_IMPORTS IAT
    dd 0
dll_kernel: db 'KERNEL32.DLL',0
dll_user: db 'USER32.DLL',0
dll_gdi: db 'GDI32.DLL',0
dll_registry: db 'ADVAPI32.DLL',0
    KERNEL_IMPORTS NAME
    USER_IMPORTS NAME
    GDI_IMPORTS NAME
    REG_IMPORTS NAME
section_end:
section_file_size equ ((section_end-section_start+0x1ff)&~0x1ff)
times section_file_size-(section_end-section_start) db 0
