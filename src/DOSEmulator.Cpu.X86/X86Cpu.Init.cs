// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using System.Text.Json;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>
    /// Applies the implemented P54C INIT signal at an owner boundary. Integer/system
    /// state restarts while the complete x87 state, FERR level and virtual TSC epoch
    /// remain unchanged. Software diagnostic counters remain cumulative.
    /// </summary>
    public void Init()
    {
        RequireP54cSignalBoundary();
        uint retainedCacheControl = Cr0 & 0x60000000;
        Array.Clear(_registers);
        _registers[(int)Register.Dx] = 0x00000524;
        InitializeP54cDescriptorState();
        Eip = 0xfff0; Eflags = 2; Cr0 = retainedCacheControl | 0x10;
        Cr2 = Cr3 = Cr4 = 0;
        Array.Clear(_debugAddresses); Dr6 = 0xffff0ff0; Dr7 = 0x400;
        TripleFaulted = false;
        ClearP54cInstructionState();
        // INIT invalidates translation state, but is neither a clock sample nor
        // FNINIT. There are no implemented cache/BTB/SMBASE state fields to reset.
        FlushTranslations();
    }

    /// <summary>
    /// Applies physical P54C CPURST, independently of the older Reset API. In
    /// addition to integer restart and TSC zeroing, the historical power-on x87
    /// state is CW=0040h, SW=0, tags=5555h and eight raw positive-zero registers.
    /// The external IGNNE input remains owned by the board.
    /// </summary>
    public void ResetSignal()
    {
        RequireP54cSignalBoundary();
        // Preserve the existing Reset() API and its diagnostic reset policy. It
        // samples the bound scheduler before mutating any CPU state.
        Reset();
        InitializeP54cDescriptorState();
        ClearP54cInstructionState();
        Array.Clear(_fpuRegisters); _fpuEmpty = 0;
        FpuControlWord = 0x0040; FpuStatusWord = 0;
        _fpuIp = _fpuData = _fpuRealIp = _fpuRealData = 0;
        _fpuCs = _fpuDs = _fpuOpcode = 0;
        PublishFpuError();
    }

    /// <summary>
    /// Reanchors only the virtual TSC at physical RESET deassertion. The board
    /// owns reset duration and fetch inhibition; this operation does not repeat
    /// integer or x87 initialization and does not create a CPU pin latch.
    /// </summary>
    public void ResetSignalReleased()
    {
        RequireP54cSignalBoundary();
        ulong epoch = _timestampSource?.ClockTicks(TimestampFrequency) ?? 0;
        _tscEpochTicks = epoch;
    }
    /// <summary>
    /// Validates a sealed P54C image held by the board's physical CPURST domain,
    /// before any medium is opened. The board has also lowered its external IGNNE
    /// pin. Diagnostic counters and the TSC epoch are deliberately unconstrained;
    /// the owning board separately validates clock binding and assertion phase.
    /// </summary>
    public static void ValidateP54cResetImage(StateBlock state)
    {
        try
        {
            var r = new StateReader(state, "DOSEmulator.Cpu.X86.X86Cpu");
            // Reject one semantically impossible held architectural field.
            void Require(bool condition, string field)
            { if (!condition) throw new InvalidDataException("Held P54C CPURST image differs: " + field); }
            // Require an unsigned architectural or transient value to be zero.
            void ZeroU(string field) => Require(r.Value<uint>(field) == 0, field);
            // Require a signed decode counter or privilege index to be zero.
            void ZeroI(string field) => Require(r.Value<int>(field) == 0, field);
            // Require a pin, execution latch or suspended-work flag to be clear.
            void False(string field) => Require(!r.Value<bool>(field), field);
            var model = r.Value<X86CpuModel>("Model");
            Require(model is X86CpuModel.IntelP54cB5Subset or X86CpuModel.IntelP55cMmxSubset, "Model");
            uint[] registers = r.Value<uint[]>("_registers");
            Require(registers is { Length: 8 }, "_registers shape");
            uint signature = model == X86CpuModel.IntelP55cMmxSubset ? 0x543u : 0x524u;
            for (int i = 0; i < registers.Length; i++) Require(registers[i] == (i == (int)Register.Dx ? signature : 0), "GPR " + i);
            Require(r.Value<uint>("Eip") == 0xfff0 && r.Value<uint>("Eflags") == 2, "EIP/EFLAGS");
            Require(r.Value<uint>("Cr0") == 0x60000010, "CR0"); ZeroU("Cr2"); ZeroU("Cr3"); ZeroU("Cr4"); ZeroI("_cpl");
            False("Halted"); False("TripleFaulted");
            ushort[] segments = r.Value<ushort[]>("_segments");
            SegmentDescriptorCache[] caches = r.Value<SegmentDescriptorCache[]>("_segmentCaches");
            Require(segments is { Length: 6 } && caches is { Length: 6 }, "segment shapes");
            for (int i = 0; i < segments.Length; i++)
            {
                bool cs = i == (int)Segment.Cs;
                Require(segments[i] == (cs ? 0xf000 : 0) && caches[i] == new SegmentDescriptorCache(cs ? 0xffff0000u : 0, 0xffff, 0x93, 0, true), "segment " + i);
            }
            Require(r.Value<DescriptorTableRegister>("Gdtr") == new DescriptorTableRegister(0xffff, 0) && r.Value<DescriptorTableRegister>("Idtr") == new DescriptorTableRegister(0xffff, 0), "GDTR/IDTR");
            Require(r.Value<ushort>("Ldtr") == 0 && r.Value<ushort>("Tr") == 0, "LDTR/TR");
            var system = new SegmentDescriptorCache(0, 0xffff, 0x82, 0, false);
            Require(r.Value<SegmentDescriptorCache>("_ldtCache") == system && r.Value<SegmentDescriptorCache>("_trCache") == system, "LDT/TSS caches");
            uint[] debug = r.Value<uint[]>("_debugAddresses"); Require(debug is { Length: 4 } && debug.All(v => v == 0), "DR0-3");
            Require(r.Value<uint>("Dr6") == 0xffff0ff0 && r.Value<uint>("Dr7") == 0x400, "DR6/DR7");
            Require(r.Value<ushort>("FpuControlWord") == 0x0040 && r.Value<ushort>("FpuStatusWord") == 0 && r.Value<byte>("_fpuEmpty") == 0, "x87 control/status/tags");
            Float80Value[] fp = r.Value<Float80Value[]>("_fpuRegisters");
            Require(fp is { Length: 8 } && fp.All(v => v == Float80Value.Zero), "raw x87 registers");
            ZeroU("_fpuIp"); ZeroU("_fpuData"); ZeroU("_fpuRealIp"); ZeroU("_fpuRealData");
            Require(r.Value<ushort>("_fpuCs") == 0 && r.Value<ushort>("_fpuDs") == 0 && r.Value<ushort>("_fpuOpcode") == 0, "x87 selectors/opcode");
            False("_publishedFpuError"); False("IgnoreNumericError"); False("_waitingForNumericError");
            ZeroI("_instructionBytes"); ZeroU("_instructionStart"); ZeroU("_firstInstructionPhysical");
            False("_operand32"); False("_address32"); Require(r.Value<Segment?>("_override") is null && r.Value<byte>("_repeat") == 0, "decode prefixes");
            ZeroI("_interruptShadow"); False("_lockPrefix"); False("_lockedMemory");
            False("_debugDataActive"); False("_debugStepShadowed"); False("_debugInterruptTransfer");
            ZeroU("_debugDataMatches"); ZeroU("_debugDeferredStatus"); False("_debugDataTriggered");
            False("_debugDeferredTrigger"); False("_debugDeferredData"); ZeroI("_debugShadow");
            False("_checkpointProtected"); False("_controlCheckpointSaved");
            ZeroU("_checkpointIp"); ZeroU("_checkpointFlags"); ZeroU("_checkpointEsp");
            Require(r.Value<ControlCheckpoint>("_controlCheckpoint") == default, "control restart checkpoint");
            False("_repValid"); False("_repAddress32"); False("_repCode32");
            Require(r.Value<byte>("_repOpcode") == 0, "REP opcode"); ZeroI("_repLength"); ZeroI("_repSize");
            ZeroU("_repStart"); ZeroU("_repEnd"); ZeroU("_repCsBase");
            Require(r.Value<ushort>("_repCs") == 0 && r.Value<Segment>("_repSource") == default && r.Value<long>("_repTask") == 0, "REP context");
            byte[] rep = r.Value<byte[]>("_repCode"); Require(rep is { Length: 15 } && rep.All(v => v == 0), "REP bytes");
            Translation[] translations = r.Value<Translation[]>("_tlb");
            Require(translations is { Length: 32 } && translations.All(v => !v.Valid), "TLB validity");
            // This is a semantic subset validator. The graph importer separately
            // checks the complete field schema and constructor-fixed invariants.
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or OverflowException or ArgumentException)
        { throw new InvalidDataException("Malformed held P54C CPURST image.", error); }
    }
    /// <summary>Rejects unsupported models and callback reentry before changing any state.</summary>
    private void RequireP54cSignalBoundary()
    {
        if (!IsPentiumCore)
            throw new InvalidOperationException("INIT/CPURST signals require the explicitly selected P54C subset.");
        if (_timestampSource?.IsDispatching == true)
            throw new InvalidOperationException("INIT/CPURST signals require an owner boundary outside a dispatched callback.");
    }

    /// <summary>Uses Intel 241428-004 table 4-2's explicit hidden startup values.</summary>
    private void InitializeP54cDescriptorState()
    {
        Array.Clear(_segments);
        _segments[(int)Segment.Cs] = 0xf000;
        for (int i = 0; i < _segmentCaches.Length; i++)
            _segmentCaches[i] = new(0, 0xffff, 0x93, 0, true);
        _segmentCaches[(int)Segment.Cs] = new(0xffff0000, 0xffff, 0x93, 0, true);
        _cpl = 0; Ldtr = Tr = 0;
        // The historical hidden bytes are retained, but this interpreter must
        // still reject a null LDT/TSS selector until an explicit valid load.
        _ldtCache = _trCache = new(0, 0xffff, 0x82, 0, false);
        Gdtr = Idtr = new(0xffff, 0);
    }

    /// <summary>Discards suspended/deferred instruction work without executing it or changing the FPU.</summary>
    private void ClearP54cInstructionState()
    {
        Halted = false; _interruptShadow = 0; _waitingForNumericError = false;
        _instructionBytes = 0; _instructionStart = _firstInstructionPhysical = 0;
        _operand32 = _address32 = false; _override = null; _repeat = 0;
        _lockPrefix = _lockedMemory = false;
        _debugDataActive = _debugStepShadowed = _debugInterruptTransfer = false;
        _debugDataMatches = 0; _debugDataTriggered = false;
        ClearDebugDeferral();
        _checkpointProtected = _controlCheckpointSaved = false;
        _checkpointIp = _checkpointFlags = _checkpointEsp = 0;
        _controlCheckpoint = default;
        Array.Clear(_repCode); _repValid = _repAddress32 = _repCode32 = false;
        _repOpcode = 0; _repLength = _repSize = 0;
        _repStart = _repEnd = _repCsBase = 0; _repCs = 0;
        _repSource = default; _repTask = 0;
    }
}
