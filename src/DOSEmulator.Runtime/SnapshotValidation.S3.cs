// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

internal static partial class SnapshotValidation
{
    /// <summary>Validates the fixed original-Trio64 graph and all indexes/decoder controls before opening any media.</summary>
    private static void CheckTrio64(HardwareState state, MachineProfile profile)
    {
        if (!state.Components.TryGetValue("video", out var block)) throw new InvalidDataException("Missing video state.");
        var video = new StateReader(block, "DOSEmulator.Hardware.VgaAdapter");
        video.Invariant("IsEt4000", profile.IsEt4000);
        video.Invariant("IsTrio64", profile.IsTrio64);
        // All CPU-transfer cursors and register shapes are checked before a media branch can be opened.
        VgaAdapter.ValidateTrio64EngineState(block, profile.IsTrio64);
        VgaAdapter.ValidateTrio64CursorState(block, profile.IsTrio64);
        if (!profile.IsTrio64)
        {
            if (state.Components.ContainsKey("s3")) throw new InvalidDataException("This earlier board has no Trio64 PCI target.");
            if (video.Value<byte>("_trioSetup") != 0 || video.Value<byte>("_trioPos") != 0 || video.Value<ushort>("_trioAdvanced") != 0)
                throw new InvalidDataException("An earlier VGA adapter cannot contain Trio64 control latches.");
            return;
        }
        if (!state.Components.TryGetValue("s3", out var target)) throw new InvalidDataException("Missing Trio64 PCI target.");
        var s3 = new StateReader(target, "DOSEmulator.Hardware.S3Trio64");
        byte[] config = s3.Value<byte[]>("_config") ?? throw new InvalidDataException("Null Trio64 configuration.");
        S3Trio64.ValidateConfigurationBytes(config);
        byte[] crtc = video.Value<byte[]>("_crtc") ?? throw new InvalidDataException("Null Trio64 CRTC.");
        byte[] seq = video.Value<byte[]>("_seq") ?? throw new InvalidDataException("Null Trio64 sequencer.");
        byte setup = video.Value<byte>("_trioSetup"), pos = video.Value<byte>("_trioPos");
        ushort advanced = video.Value<ushort>("_trioAdvanced");
        VgaAdapter.ValidateTrio64Registers(crtc, seq, advanced, setup, pos);
        byte[][] planes = video.Value<byte[][]>("_planes") ?? throw new InvalidDataException("Null Trio64 VRAM.");
        if (planes.Length != 4 || planes.Any(p => p is null || p.Length != 524288))
            throw new InvalidDataException("Trio64 requires four complete shared two-MiB DRAM planes.");
        byte[] gc = video.Value<byte[]>("_gc") ?? throw new InvalidDataException("Null Trio64 graphics controller.");
        byte[] attr = video.Value<byte[]>("_attr") ?? throw new InvalidDataException("Null Trio64 attribute controller.");
        byte[] latch = video.Value<byte[]>("_latch") ?? throw new InvalidDataException("Null Trio64 VGA latches.");
        byte[] dac = video.Value<byte[]>("_dac") ?? throw new InvalidDataException("Null Trio64 DAC.");
        uint[] rgb = video.Value<uint[]>("_rgb") ?? throw new InvalidDataException("Null Trio64 palette cache.");
        byte readComponent = video.Value<byte>("_readComponent"), writeComponent = video.Value<byte>("_writeComponent"), seqIndex = video.Value<byte>("_seqIndex"), crtcIndex = video.Value<byte>("_crtcIndex"), gcIndex = video.Value<byte>("_gcIndex"), attrIndex = video.Value<byte>("_attrIndex"), dacState = video.Value<byte>("_dacState"), misc = video.Value<byte>("_misc"), readIndex = video.Value<byte>("_readIndex"), writeIndex = video.Value<byte>("_writeIndex"), pelMask = video.Value<byte>("_pelMask");
        if (gc.Length != 9 || attr.Length != 21 || latch.Length != 4 || dac.Length != 768 || rgb.Length != 256 ||
            readComponent > 2 || writeComponent > 2 ||
            seqIndex > 31 ||
            gcIndex > 15 || attrIndex > 31 ||
            (misc & 0x10) != 0 ||
            dacState is not (0 or 3) || dac.Any(c => c > 63))
            throw new InvalidDataException("Invalid Trio64 palette, VGA array or partial index state.");
        for (int i = 0; i < rgb.Length; i++)
        {
            uint red = dac[i * 3], green = dac[i * 3 + 1], blue = dac[i * 3 + 2];
            uint expected = 0xff000000u | ((red << 2) | (red >> 4)) << 16 |
                ((green << 2) | (green >> 4)) << 8 | ((blue << 2) | (blue >> 4));
            if (rgb[i] != expected) throw new InvalidDataException("Trio64 DAC and derived palette disagree.");
        }
        if (video.Value<(bool Graphics, bool Indexed, int Width, int Height)>("_modeKey") !=
            VgaAdapter.GetTrio64ModeKey(crtc, seq, gc, attr, advanced))
            throw new InvalidDataException("Trio64 derived display mode differs from its actual guest registers.");
        if (state.Scheduler.Events.Any(e => e.Owner is "s3" or "video"))
            throw new InvalidDataException("The current Trio64/VGA subset owns no scheduler callbacks.");
        var reset = new StateReader(state.Components["piix-reset"], "DOSEmulator.Hardware.PiixResetController");
        bool held = reset.Value<PiixResetPhase>("_phase") != PiixResetPhase.Idle;
        if (held &&
            (!S3Trio64.IsHardResetConfiguration(config) || !VgaAdapter.IsTrio64EngineResetState(block) ||
             !VgaAdapter.IsTrio64CursorResetState(block) ||
             setup != 0 || pos != 0 || advanced != 0 || crtc[0x59] != 0 || crtc[0x5a] != 0))
            throw new InvalidDataException("Held PCI reset retained an active or reprogrammed Trio64 decoder.");
        if (held)
        {
            Span<byte> resetCrtc = stackalloc byte[128];
            resetCrtc.Clear();
            resetCrtc[1] = 79; resetCrtc[7] = 2; resetCrtc[9] = 15; resetCrtc[0x0a] = 0x20;
            resetCrtc[0x12] = 0x8f; resetCrtc[0x13] = 40;
            resetCrtc[0x2d] = 0x88; resetCrtc[0x2e] = 0x11; resetCrtc[0x30] = 0xe1;
            resetCrtc[0x36] = 0x8e; resetCrtc[0x37] = 0x0a; resetCrtc[0x40] = 0x30; resetCrtc[0x68] = 0x80;
            ReadOnlySpan<byte> resetGc = [0, 0, 0, 0, 0, 0x10, 0x0e, 0, 255];
            ReadOnlySpan<byte> resetAttr = [0, 1, 2, 3, 4, 5, 0x14, 7, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0, 0, 0, 0, 0];
            if (!crtc.AsSpan().SequenceEqual(resetCrtc) || seq.Any(b => b != 0) ||
                !gc.AsSpan().SequenceEqual(resetGc) || !attr.AsSpan().SequenceEqual(resetAttr) || latch.Any(b => b != 0) ||
                !dac.AsSpan().SequenceEqual(VgaPalette.Planar()) || misc != 0 ||
                seqIndex != 0 || gcIndex != 0 ||
                crtcIndex != 0 || attrIndex != 0 ||
                readIndex != 0 || writeIndex != 0 ||
                readComponent != 0 || writeComponent != 0 ||
                dacState != 0 || pelMask != 255 ||
                video.Value<bool>("_attributeData") || !video.Value<bool>("_displayEnabled"))
                throw new InvalidDataException("Held Trio64 registers differ from their actual reset-domain image.");
        }

    }
}
