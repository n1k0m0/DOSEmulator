; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Own finite protected-mode MMX/SST-1 client. No DOS/BIOS replacement or host service.
bits 16
org 1000h
; Enters the finite client through its private protected-mode setup.
start:
    cli
    xor ax,ax
    mov ds,ax
    mov ss,ax
    mov sp,9000h
    lgdt [gdtr]
    mov eax,60000011h
    mov cr0,eax
    jmp dword 8:protected
align 8
gdt:
    dq 0,00cf9a000000ffffh,00cf92000000ffffh
gdtr:
    dw 23
    dd gdt
bits 32
protected:
    mov ax,10h
    mov ds,ax
    mov es,ax
    mov ss,ax
    mov esp,9000h
    fninit
    mov eax,1
    cpuid
    mov [7000h],eax
    mov [7004h],edx
    movq mm0,[packed_a]
    paddusw mm0,[packed_b]
    movq [7008h],mm0
    ; Mechanism-1 enumeration is performed by the client itself.
    mov dx,0cf8h
    mov eax,80003000h
    out dx,eax
    mov dx,0cfch
    in eax,dx
    mov [7010h],eax
%macro CFG 2
    mov dx,0cf8h
    mov eax,80003000h+%1
    out dx,eax
    mov dx,0cfch
    mov eax,%2
    out dx,eax
%endmacro
    CFG 10h,0d0000000h
    CFG 4,2
    CFG 40h,3
%macro REG 2
    mov dword [0d0000000h+%1],%2
%endmacro
    REG 210h,0
    REG 214h,20a8h
    REG 218h,(150<<11)
    REG 20ch,(480<<16)|639
    REG 21ch,(479<<22)
    ; Upload a square 32x32 RGB565 mip level through the original LOD/T/S aperture fields.
    REG 300h,(10<<8)|(1<<12)|(1<<18)
    REG 304h,12|(12<<6)
    REG 30ch,100h
    xor ebx,ebx
.texture_row:
    xor ecx,ecx
.texture_pair:
    mov edi,ebx
    shl edi,9
    lea edi,[edi+ecx*4+0d0860000h]
    mov eax,ebx
    shr eax,2
    mov edx,ecx
    shr edx,1
    xor eax,edx
    test eax,1
    mov eax,001f001fh
    jz .texture_store
    mov eax,07e007e0h
.texture_store:
    movd mm2,eax
    movd eax,mm2
    mov [edi],eax
    inc ecx
    cmp ecx,16
    jb .texture_pair
    inc ebx
    cmp ebx,32
    jb .texture_row
    REG 118h,640
    REG 11ch,480
    REG 110h,4600h
    REG 148h,00203050h
    REG 130h,0ffffh
    REG 124h,0
    ; One red Gouraud triangle with exact integer gradients, drawn into the back buffer.
    REG 8,80*16
    REG 0ch,60*16
    REG 10h,560*16
    REG 14h,60*16
    REG 18h,80*16
    REG 1ch,420*16
    REG 20h,255<<12
    REG 24h,0
    REG 28h,0
    REG 30h,255<<12
    REG 44h,(255<<12)/480
    REG 68h,(255<<12)/360
    REG 2ch,200<<12
    REG 80h,0
    ; A second primitive samples the real point-filtered RGB565 mip, using interpolated affine S/T.
    REG 8,380*16
    REG 0ch,160*16
    REG 10h,600*16
    REG 14h,160*16
    REG 18h,380*16
    REG 1ch,360*16
    REG 34h,0
    REG 38h,0
    REG 54h,(256<<18)/220
    REG 78h,(256<<18)/200
    REG 104h,(1<<27)|1
    REG 80h,0
    ; A future synchronized swap guarantees an active queue at the natural terminal HLT.
    REG 128h,511
    mov dword [7014h],314f4f56h
    hlt
align 8
packed_a: dw 65530,100,32768,0
packed_b: dw 20,200,32768,1
