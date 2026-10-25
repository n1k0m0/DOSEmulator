; Original VCPI client. No host mode switch, memory-manager or DOS hooks.
; DE01 supplies the server mappings/descriptors; DE0C transfers both ways.
bits 16
cpu 486
org 0x100
%define CLIENT_CODE 8
%define FLAT_DATA 0x10
%define CLIENT_TSS 0x18
%define SERVER_CODE 0x20
%define SERVER_DATA 0x28
%define ALIAS 0x400000
start:
    push cs
    pop ds
    push cs
    pop es
    cld
    mov [saved_sp],sp
    mov [saved_cs],cs
    xor ebp,ebp
    mov bp,cs
    shl ebp,4
    mov [proof+24],ebp
    smsw ax
    test al,1
    jz failed
    mov ax,0xde00
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+28],bx
    mov ax,0xde03
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+32],edx
    mov ax,0xde04
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+36],edx
    inc byte [allocated]
    mov ax,0xde04
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+40],edx
    inc byte [allocated]
    or dword [proof+16],1
    lea eax,[ebp+table_storage+4095]
    and eax,0xfffff000
    mov [proof+44],eax
    mov [switch_data],eax
    sub eax,ebp
    mov di,ax
    mov cx,8192
    xor ax,ax
    rep stosw
    ; Translate each actual conventional table page through the resident VCPI.
    mov edi,[proof+44]
    mov bx,44
    mov cx,4
.physical:
    push cx
    mov ecx,edi
    shr ecx,12
    mov ax,0xde06
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+bx+4],edx
    add edi,4096
    add bx,4
    pop cx
    loop .physical
    ; +44 = linear PD, +48..60 = physical PD/PT0/PT1/spare.
    mov eax,[proof+48]
    mov [switch_data],eax
    mov edi,[proof+44]
    add edi,4096
    sub edi,ebp
    mov si,server_descriptors
    mov ax,0xde01
    int 0x67
    test ah,ah
    jnz failed
    mov [server_entry],ebx
    mov [proof+64],ebx
    mov [proof+68],di
    mov di,[proof+44]
    sub di,bp
    mov eax,[proof+52]
    or eax,7
    mov [di],eax
    mov eax,[proof+56]
    or eax,3
    mov [di+4],eax
    add di,8192
    mov eax,[proof+36]
    or eax,3
    mov [di],eax
    mov eax,[proof+40]
    or eax,3
    mov [di+4],eax
    ; Own code base; flat data and TSS; all tables are conventional/shared.
    mov eax,ebp
    mov di,code_descriptor
    call descriptor_base
    lea eax,[ebp+tss]
    mov di,tss_descriptor
    call descriptor_base
    lea eax,[ebp+gdt]
    mov [gdtr+2],eax
    lea eax,[ebp+idt]
    mov [idtr+2],eax
    lea eax,[ebp+gdtr]
    mov [switch_data+4],eax
    lea eax,[ebp+idtr]
    mov [switch_data+8],eax
    mov word [tss+102],104
    ; Faults stop at a real guest diagnostic handler; no successful fallback.
    mov di,idt
    mov cx,256
.gate:
    mov word [di],pm_fault
    mov word [di+2],CLIENT_CODE
    mov word [di+4],0x8e00
    mov word [di+6],0
    add di,8
    loop .gate
    in al,0x70
    mov [saved_rtc],al
    or al,0x80
    out 0x70,al
    or dword [proof+16],2
enter_client:
    ; Each entry must load an available TSS, including the second entry.
    and byte [tss_descriptor+5],0xfd
    cli
    lea esi,[ebp+switch_data]
    mov ax,0xde0c
    int 0x67
    jmp failed
v86_return:
    push cs
    pop ds
    push cs
    pop es
    cld
    inc dword [proof+76]
    cmp dword [proof+76],2
    jb enter_client
    mov al,[saved_rtc]
    out 0x70,al
    sti
    call release_pages
    cmp byte [allocated],0
    jne failed
    mov ax,0xde03
    int 0x67
    test ah,ah
    jnz failed
    mov [proof+80],edx
    cmp edx,[proof+32]
    jne failed
    or dword [proof+16],8
    mov dword [proof+12],1
    jmp report
failed:
    mov [proof+20],eax
    call release_pages
report:
    mov si,marker
    call debug_string
    lea ebx,[ebp+proof]
    mov cx,8
.hex:
    rol ebx,4
    mov al,bl
    and al,15
    add al,'0'
    cmp al,'9'
    jbe .digit
    add al,7
.digit:
    out 0xe9,al
    loop .hex
    mov si,newline
    call debug_string
    mov dx,success
    cmp dword [proof+12],1
    je .print
    mov dx,failure
.print:
    mov ah,9
    int 0x21
    mov ax,0x4c00
    int 0x21
descriptor_base:
    mov [di+2],ax
    shr eax,16
    mov [di+4],al
    mov [di+7],ah
    ret
release_pages:
    cmp byte [allocated],0
    je .done
    movzx bx,byte [allocated]
    dec bx
    shl bx,2
    mov edx,[proof+36+bx]
    mov ax,0xde05
    int 0x67
    test ah,ah
    jnz .done
    dec byte [allocated]
    jmp release_pages
.done:
    ret
debug_string:
    lodsb
    test al,al
    jz .done
    out 0xe9,al
    jmp debug_string
.done:
    ret
bits 32
pm_entry:
    mov ax,FLAT_DATA
    mov ds,ax
    mov es,ax
    mov fs,ax
    mov gs,ax
    mov ss,ax
    lea esp,[ebp+pm_stack_top]
    cld
    inc dword [ebp+proof+72]
    mov eax,cr3
    mov [ebp+proof+84],eax
    mov eax,cr0
    mov [ebp+proof+88],eax
    pushfd
    pop eax
    mov [ebp+proof+92],eax
    sgdt [ebp+proof+96]
    sidt [ebp+proof+104]
    str [ebp+proof+112]
    sldt [ebp+proof+114]
    mov [ebp+proof+116],cs
    mov [ebp+proof+118],ss
    mov [ebp+proof+120],esp
    ; 32-bit far frames use the actual server entry and descriptors from DE01.
    mov ax,0xde03
    call far [ebp+server_entry]
    test ah,ah
    jnz pm_fault
    mov [ebp+proof+124],edx
    mov ax,0xde04
    call far [ebp+server_entry]
    test ah,ah
    jnz pm_fault
    mov [ebp+proof+128],edx
    mov edi,[ebp+proof+44]
    add edi,8192
    mov eax,edx
    or eax,3
    mov [edi+8],eax
    invlpg [ALIAS+8192]
    ; Distinct whole-page patterns, written directly through client paging.
    mov edi,ALIAS
    mov ecx,3072
    mov eax,0x13579bdf
.fill:
    stosd
    add eax,0x1020304
    loop .fill
    mov esi,ALIAS
    mov ecx,3072
    mov eax,0x13579bdf
.check:
    cmp [esi],eax
    jne pm_fault
    add esi,4
    add eax,0x1020304
    loop .check
    mov edx,[ebp+proof+128]
    mov ax,0xde05
    call far [ebp+server_entry]
    test ah,ah
    jnz pm_fault
    mov ax,0xde03
    call far [ebp+server_entry]
    test ah,ah
    jnz pm_fault
    mov [ebp+proof+132],edx
    cmp edx,[ebp+proof+124]
    jne pm_fault
    or dword [ebp+proof+16],4
    ; Return frame and stack remain inside the server's shared first MB.
    movzx eax,word [ebp+saved_cs]
    push eax ; GS
    push eax ; FS
    push eax ; DS
    push eax ; ES
    push eax ; SS
    movzx ecx,word [ebp+saved_sp]
    push ecx ; ESP
    push dword 0 ; reserved EFLAGS, supplied by server
    push eax ; CS
    push dword v86_return
    mov ax,SERVER_DATA
    mov ds,ax
    mov ax,0xde0c
    call far [ebp+server_entry]
pm_fault:
    mov ax,FLAT_DATA
    mov ds,ax
    mov dword [ebp+proof+136],0x4641554c
    mov [ebp+proof+140],esp
    cli
    hlt
    jmp $
bits 16
saved_sp dw 0
saved_cs dw 0
saved_rtc db 0
allocated db 0
server_entry dd 0
    dw SERVER_CODE
switch_data dd 0,0,0
    dw 0,CLIENT_TSS
    dd pm_entry
    dw CLIENT_CODE
gdtr dw gdt_end-gdt-1
    dd 0
idtr dw 2047
    dd 0
align 8,db 0
gdt dq 0
code_descriptor dw 0xffff,0
    db 0,0x9b,0x40,0
flat_descriptor dw 0xffff,0
    db 0,0x93,0xcf,0
tss_descriptor dw 103,0
    db 0,0x89,0,0
server_descriptors times 24 db 0
gdt_end:
tss times 104 db 0
align 4,db 0
proof:
    db 'CPVCP001'
    dd 1
    times 61 dd 0
; Label map lets an independent verifier inspect actual executed guest bytes.
    db 'CPVCADDR'
    dw start,enter_client,pm_entry,v86_return,pm_fault,gdt,server_descriptors
    dw switch_data,gdtr,idtr,tss,proof,pm_stack_top,server_entry,table_storage,saved_sp
marker db 'VCPI-PROTECTED-PROOF=',0
newline db 13,10,0
success db 'VCPI-PROTECTED-PASS',13,10,'$'
failure db 'VCPI-PROTECTED-FAIL',13,10,'$'
idt times 2048 db 0
pm_stack times 4096 db 0
pm_stack_top:
table_storage times 4*4096+4095 db 0
