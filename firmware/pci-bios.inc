; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Original PCI BIOS 2.1 / BIOS32 implementation for the fixed own DOSE board.
; PCI-SIG, 26 August 1994, BIOS32 directory proposal revision 0.4.
; All configuration and IRQ effects are guest CF8/CFC/ELCR operations.
; No writable BIOS data, CS data reads, host callback or invented device IDs.

%define PCI_BIOS_TOPOLOGY 039381800h
%define PCI_BIOS_ROUTE_BYTES 48
%ifdef DOSE_NETWORK
    %undef PCI_BIOS_ROUTE_BYTES
    %define PCI_BIOS_ROUTE_BYTES 80
%endif

; One canonical twelve-dword table supplies both ROM data and the DS-independent
; 16-bit compatibility path. Each entry specifies its byte offset and dword value.
%macro PCI_BIOS_ROUTING_VALUES 1
    %1 0, 0
    %1 4, 0
    %1 8, 0
    %1 12, 0
    %1 16, 000601800h
    %1 20, 8
    %1 24, 0
    %1 28, 0
    %1 32, 000003800h
    %1 36, 0
    %1 40, 0
    %1 44, 0
%ifdef DOSE_NETWORK
    ; Own optional PCI slots 8 and 9 share the board's real PIRQA/IRQ11 wire.
    %1 48, 000604000h
    %1 52, 8
    %1 56, 0
    %1 60, 0
    %1 64, 000604800h
    %1 68, 8
    %1 72, 0
    %1 76, 0
%endif
%endmacro

; Emit ordinary word stores, preserving the previous word-alignment contract.
%macro PCI_BIOS_ROUTE_STORE16 2
    mov word [es:di+%1], (%2 & 0ffffh)
    mov word [es:di+%1+2], ((%2 >> 16) & 0ffffh)
%endmacro

; Serialize the identical constants into the readonly table used by 32-bit callers.
%macro PCI_BIOS_ROUTE_DATA 2
    dd %2
%endmacro

align 16, db 0ffh
pci_bios_directory_header:
    db '_32_'
    dd 0f0000h + pci_bios_directory_entry - $$
    db 0, 1
    db (-(05fh+033h+032h+05fh+1+ \
        ((0f0000h+pci_bios_directory_entry-$$) & 255)+ \
        (((0f0000h+pci_bios_directory_entry-$$) >> 8) & 255)+ \
        (((0f0000h+pci_bios_directory_entry-$$) >> 16) & 255)+ \
        (((0f0000h+pci_bios_directory_entry-$$) >> 24) & 255))) & 255
    times 5 db 0

; Own read-only ROM offset manifest for independent instruction/frame oracles.
pci_bios_metadata:
    db 'PCIMETA1'
    dw 1, 14
    dd pci_bios_directory_header-$$, pci_bios_directory_entry-$$
    dd pci_bios_int1a-$$, pci_bios_service32-$$
    dd pci_bios16_config_out-$$, pci_bios16_config_cfc-$$
    dd pci_bios32_config_out-$$, pci_bios32_config_cfc-$$
    dd pci_bios16_return-$$, pci_bios32_return-$$
    dd pci_bios_routing_table-$$, pci_bios_routing_table_end-$$
    dd pci_bios_bdf_table-$$, pci_bios_service_description-$$

; BIOS32 accepts either flat CS/DS or equal ROM-based selectors. The entry
; computes its addressable ROM base from its actual EIP; DS may be read-only.
bits 32
pci_bios_directory_entry:
    pushfd
    cli
    push ebp
    mov ebp, esp
    pushad
    call .position
.position:
    pop esi
    sub esi, .position-$$
    cmp byte [ss:ebp-16], 0
    jne .bad_function
    cmp dword [ss:ebp-4], 049435024h
    jne .not_present
    mov eax, [ds:esi+pci_bios_service_description-$$]
    mov [ss:ebp-16], eax
    mov eax, [ds:esi+pci_bios_service_description-$$+4]
    mov [ss:ebp-8], eax
    mov eax, [ds:esi+pci_bios_service_description-$$+8]
    mov [ss:ebp-12], eax
    mov byte [ss:ebp-4], 0
    jmp .return
.not_present:
    mov byte [ss:ebp-4], 080h
    jmp .return
.bad_function:
    ; Decode BL only; reserved upper EBX is ignored by this own compatibility policy.
    mov byte [ss:ebp-4], 081h
.return:
    popad
    pop ebp
    popfd
    retf

; These immutable values are within the directory entry's current/next page.
pci_bios_service_description:
    dd 0f0000h, 10000h, pci_bios_service32-$$
pci_bios_bdf_table:
    dd PCI_BIOS_TOPOLOGY

; Generate matching 16-bit INT and 32-bit FAR-call bodies. All scratch is
; on the caller's correctly sized stack. There is no shared writable state.
%macro PCI_BIOS_BODY 3
bits %1
%if %1 = 16
    %define PBASE bp
    %define PSTACK sp
    %define PSEGMENTS 36
    %define PFLAGS 6
%else
    %define PBASE ebp
    %define PSTACK esp
    %define PSEGMENTS 40
    %define PFLAGS 4
%endif
    %assign PCF8 -(PSEGMENTS+4)
    %assign PUSED -(PSEGMENTS+8)
    %assign PROM -(PSEGMENTS+12)
    %assign PPACK -(PSEGMENTS+16)
    %assign PLEFT -(PSEGMENTS+20)
    %assign PORDINAL -(PSEGMENTS+24)
    %assign PDOFFSET -(PSEGMENTS+28)
    %assign PDSELECTOR -(PSEGMENTS+32)
    %assign PLOCAL 32

; The 16-bit entry receives a real/simulated INT frame, not a bare FAR call.
%2:
%if %1 = 16
    cli
    push bp
    mov bp, sp
%else
    pushfd
    cli
    push ebp
    mov ebp, esp
%endif
    pushad
    push ds
    push es
    sub PSTACK, PLOCAL
    cld
    mov dword [ss:PBASE+PUSED], 0
%if %1 = 32
    call %%position
%%position:
    pop esi
    sub esi, %%position-$$
    mov [ss:PBASE+PROM], esi
%endif
    cmp byte [ss:PBASE-3], 0b1h
    jne %%unsupported
    mov al, [ss:PBASE-4]
    cmp al, 1
    je %%present
    cmp al, 2
    je %%find_device
    cmp al, 3
    je %%find_class
    cmp al, 8
    jb %%unsupported
    cmp al, 0dh
    jbe %%configuration
    cmp al, 0eh
    je %%get_routes
    cmp al, 0fh
    je %%set_route
    jmp %%unsupported

%%present:
    mov word [ss:PBASE-4], 0001h
    mov word [ss:PBASE-16], 0210h
    mov byte [ss:PBASE-8], 0
    mov dword [ss:PBASE-12], 020494350h
    jmp %%success

%%find_device:
    cmp word [ss:PBASE-12], 0ffffh
    je %%bad_vendor
%%find_class:
    movzx eax, word [ss:PBASE-28]
    mov [ss:PBASE+PORDINAL], eax
    mov dword [ss:PBASE+PLEFT], 4
%ifdef DOSE_NETWORK
    mov dword [ss:PBASE+PLEFT], 6
%endif
%if %1 = 16
    mov dword [ss:PBASE+PPACK], PCI_BIOS_TOPOLOGY
%else
    mov esi, [ss:PBASE+PROM]
    mov eax, [ds:esi+pci_bios_bdf_table-$$]
    mov [ss:PBASE+PPACK], eax
%endif
    call %%save_cf8
%%find_next:
%ifdef DOSE_NETWORK
    cmp dword [ss:PBASE+PLEFT], 2
    jne %%network_topology_ready
    mov dword [ss:PBASE+PPACK], 000004840h
%%network_topology_ready:
%endif
    movzx ebx, byte [ss:PBASE+PPACK]
    ror dword [ss:PBASE+PPACK], 8
    mov eax, ebx
    shl eax, 8
    cmp byte [ss:PBASE-4], 3
    jne %%find_offset
    or eax, 8
%%find_offset:
    or eax, 080000000h
    mov dx, 0cf8h
%3 %+ _find_out:
    out dx, eax
    mov dx, 0cfch
%3 %+ _find_cfc:
    in eax, dx
    cmp byte [ss:PBASE-4], 3
    je %%compare_class
    movzx ecx, word [ss:PBASE-8]
    shl ecx, 16
    mov cx, [ss:PBASE-12]
    cmp eax, ecx
    jne %%find_advance
    jmp %%find_match
%%compare_class:
    shr eax, 8
    mov ecx, [ss:PBASE-8]
    and ecx, 00ffffffh
    cmp eax, ecx
    jne %%find_advance
%%find_match:
    cmp dword [ss:PBASE+PORDINAL], 0
    je %%found
    dec dword [ss:PBASE+PORDINAL]
%%find_advance:
    dec dword [ss:PBASE+PLEFT]
    jnz %%find_next
    mov al, 086h
    jmp %%error
%%found:
    mov [ss:PBASE-16], bx
    jmp %%success

%%configuration:
    movzx edi, word [ss:PBASE-32]
    cmp edi, 255
    ja %%bad_register
    mov al, [ss:PBASE-4]
    cmp al, 8
    je %%register_valid
    cmp al, 0bh
    je %%register_valid
    cmp al, 9
    je %%word_alignment
    cmp al, 0ch
    je %%word_alignment
    test di, 3
    jnz %%bad_register
    jmp %%register_valid
%%word_alignment:
    test di, 1
    jnz %%bad_register
%%register_valid:
    call %%save_cf8
    movzx eax, word [ss:PBASE-16]
    shl eax, 8
    mov ecx, edi
    and ecx, 0fch
    or eax, ecx
    or eax, 080000000h
    mov dx, 0cf8h
%3 %+ _config_out:
    out dx, eax
%3 %+ _config_cfc:
    mov dx, 0cfch
    mov cx, di
    and cx, 3
    add dx, cx
    mov al, [ss:PBASE-4]
    cmp al, 8
    je %%read_byte
    cmp al, 9
    je %%read_word
    cmp al, 0ah
    je %%read_dword
    cmp al, 0bh
    je %%write_byte
    cmp al, 0ch
    je %%write_word
    mov eax, [ss:PBASE-8]
    out dx, eax
    jmp %%success
%%write_byte:
    mov al, [ss:PBASE-8]
    out dx, al
    jmp %%success
%%write_word:
    mov ax, [ss:PBASE-8]
    out dx, ax
    jmp %%success
%%read_byte:
    in al, dx
    mov [ss:PBASE-8], al
    jmp %%success
%%read_word:
    in ax, dx
    mov [ss:PBASE-8], ax
    jmp %%success
%%read_dword:
    in eax, dx
    mov [ss:PBASE-8], eax
    jmp %%success

%%get_routes:
%if %1 = 16
    movzx edi, word [ss:PBASE-32]
    cmp di, 0fffah
%else
    mov edi, [ss:PBASE-32]
    cmp edi, 0fffffff8h
%endif
    ja %%small_buffer
    ; Ownership is restricted to the one connected board link and IRQ11.
    ; Arbitrary direct programming of unused PIRQ inputs does not reserve ISA IRQs.
    call %%save_cf8
    mov dx, 0cf8h
    mov eax, 080003860h
    out dx, eax
    mov dx, 0cfch
    in al, dx
    xor bx, bx
    cmp al, 00bh
    jne %%exclusive_ready
    mov bx, 0800h
%%exclusive_ready:
    mov [ss:PBASE-16], bx
%if %1 = 16
    mov cx, [es:di]
    mov word [es:di], PCI_BIOS_ROUTE_BYTES
%else
    mov cx, [es:edi]
    mov word [es:edi], PCI_BIOS_ROUTE_BYTES
%endif
    cmp cx, PCI_BIOS_ROUTE_BYTES
    jb %%small_buffer
%if %1 = 16
    movzx eax, word [es:di+2]
    movzx edx, word [es:di+4]
    cmp ax, (010000h-PCI_BIOS_ROUTE_BYTES)
%else
    mov eax, [es:edi+2]
    movzx edx, word [es:edi+6]
    cmp eax, (0100000000h-PCI_BIOS_ROUTE_BYTES)
%endif
    ja %%small_buffer
    mov [ss:PBASE+PDOFFSET], eax
    mov [ss:PBASE+PDSELECTOR], edx
    mov es, dx
%if %1 = 16
    ; The normative DS=F0000 caller still works. Accept an ordinary DOS caller
    ; DS as an own extension without loading an execute-only CS selector into DS.
    mov di, ax
    PCI_BIOS_ROUTING_VALUES PCI_BIOS_ROUTE_STORE16
%else
    mov edi, eax
    mov esi, [ss:PBASE+PROM]
    add esi, pci_bios_routing_table-$$
    mov ecx, PCI_BIOS_ROUTE_BYTES/2
    rep movsw
%endif
    jmp %%success

%%set_route:
%ifdef DOSE_NETWORK
    cmp word [ss:PBASE-16], 0040h
    je %%route_device_ready
    cmp word [ss:PBASE-16], 0048h
    je %%route_device_ready
%endif
    cmp word [ss:PBASE-16], 0018h
    jne %%set_failed
%ifdef DOSE_NETWORK
%%route_device_ready:
%endif
    cmp word [ss:PBASE-8], 00b0ah
    jne %%set_failed
    ; PIIX ELCR is external to the PIC; leave PIC modes/masks and PCI3C to caller.
    mov dx, 04d1h
    in al, dx
    or al, 8
    out dx, al
    call %%save_cf8
    mov dx, 0cf8h
    mov eax, 080003860h
    out dx, eax
    mov dx, 0cfch
    mov al, 0bh
    out dx, al
    jmp %%success

; Save the actual CONFIG_ADDRESS exactly once in this stack-local invocation.
%%save_cf8:
    cmp dword [ss:PBASE+PUSED], 0
    jne %%saved_cf8
    mov dx, 0cf8h
    in eax, dx
    mov [ss:PBASE+PCF8], eax
    mov dword [ss:PBASE+PUSED], 1
%%saved_cf8:
    ret

%%bad_vendor:
    mov al, 083h
    jmp %%error
%%bad_register:
    mov al, 087h
    jmp %%error
%%set_failed:
    mov al, 088h
    jmp %%error
%%small_buffer:
    mov al, 089h
    jmp %%error
%%unsupported:
    mov al, 081h
%%error:
    mov [ss:PBASE-3], al
%if %1 = 16
    or word [ss:PBASE+PFLAGS], 1
%else
    or dword [ss:PBASE+PFLAGS], 1
%endif
    jmp %3 %+ _return
%%success:
    mov byte [ss:PBASE-3], 0
%if %1 = 16
    and word [ss:PBASE+PFLAGS], 0fffeh
%else
    and dword [ss:PBASE+PFLAGS], 0fffffffeh
%endif
%3 %+ _return:
    cmp dword [ss:PBASE+PUSED], 0
    je %%no_cf8_restore
    mov dx, 0cf8h
    mov eax, [ss:PBASE+PCF8]
    out dx, eax
%%no_cf8_restore:
    add PSTACK, PLOCAL
    pop es
    pop ds
    popad
%if %1 = 16
    pop bp
    iret
%else
    pop ebp
    ; CF8 and the caller stack are stable before IF restoration. POPFD/RETF
    ; is the conventional two-instruction epilogue, not an atomic IRQ boundary.
    popfd
    retf
%endif
    %undef PBASE
    %undef PSTACK
    %undef PSEGMENTS
    %undef PFLAGS
%endmacro

PCI_BIOS_BODY 16, pci_bios_int1a, pci_bios16
PCI_BIOS_BODY 32, pci_bios_service32, pci_bios32
%unmacro PCI_BIOS_BODY 3

; One fixed own motherboard topology, not an invented historical plug-in slot.
; Host and PIIX have no PCI INTx peer here. S3 INTA alone is wired to PIRQA;
; IRQ11 is the sole BIOS allocation option, while S3 IRQ production is outside scope.
pci_bios_routing_table:
    PCI_BIOS_ROUTING_VALUES PCI_BIOS_ROUTE_DATA
pci_bios_routing_table_end:
%if pci_bios_routing_table_end-pci_bios_routing_table != PCI_BIOS_ROUTE_BYTES
    %error Wrong fixed PCI routing table size.
%endif
bits 16
%unmacro PCI_BIOS_ROUTING_VALUES 1
%unmacro PCI_BIOS_ROUTE_STORE16 2
%unmacro PCI_BIOS_ROUTE_DATA 2
%undef PCI_BIOS_TOPOLOGY
%undef PCI_BIOS_ROUTE_BYTES
