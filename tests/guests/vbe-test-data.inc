; SPDX-License-Identifier: Apache-2.0
; Copyright 2026 Nils Kopal
; Mutable proof only; all executed code and request tables precede ETMUTABL.
vbe_proof db 'VBE12001'
vbe_complete dw 0
vbe_success_count dw 0
vbe_failure_count dw 0
vbe_boundary_bytes dw 0
vbe_mode_sets dw 0
vbe_compared_banks dw 0
vbe_current dw 0
vbe_bank dw 0
vbe_bank_values times 16 dw 0
vbe_far_values times 16 dw 0
vbe_word_values times 16 dw 0
vbe_guard_before times 16 db 0
vbe_controller_info times 256 db 0
vbe_mode_info times 3*256 db 0
vbe_rejected_info times 256 db 0
vbe_guard_after times 16 db 0
    times 1024 db 0
vbe_stack_end:
