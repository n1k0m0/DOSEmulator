// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>The one pending original-PIIX reset request consumed at a real owner instruction boundary.</summary>
public enum PiixResetRequest { None, Init, Hard }
/// <summary>Physical reset domains held by the own board; CPU release is adopted only at an owner boundary.</summary>
public enum PiixResetPhase { Idle, AllAsserted, CpuOnly }
/// <summary>Factory-fixed board action at PCI/ISA release; never an arbitrary host delegate.</summary>
public interface IPiixResetOwner
{
    /// <summary>Releases only the board's reset-connected devices while the CPU still remains held.</summary>
    void ReleasePiixPciIsaReset();
}

/// <summary>Original CF9 edge latch and an explicit own nominal reset pulse sequencer, separate from CONFADD.</summary>
public sealed partial class PiixResetController : IIoPortDevice
{
    /// <summary>Own nominal one-millisecond peripheral hold and two-millisecond CPU hold; no electrical timing calibration.</summary>
    public const ulong PciIsaHoldNanoseconds = 1_000_000, CpuHoldNanoseconds = 2_000_000;
    private readonly DeviceScheduler _clock;
    private readonly PciConfigurationBus _pci;
    private readonly IPiixResetOwner _owner;
    private byte _control;
    private PiixResetRequest _pending;
    private PiixResetPhase _phase;
    private long _pciEvent, _cpuEvent;
    private bool _cpuReleaseReady;
    private ulong _assertedAt, _pciReleaseAt, _cpuReleaseAt;
    /// <summary>Original edge counts; readback and sustained high writes never manufacture another edge.</summary>
    public long SoftEdges { get; private set; }
    /// <summary>Original hard-request edges, distinct from soft INIT and the later signal deassertion.</summary>
    public long HardEdges { get; private set; }
    /// <summary>Factory binds the same PCI address latch and owning board without registering a bus mapping.</summary>
    public PiixResetController(DeviceScheduler clock, PciConfigurationBus pci, IPiixResetOwner owner)
    { ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(pci); ArgumentNullException.ThrowIfNull(owner); _clock = clock; _pci = pci; _owner = owner; }
    /// <summary>Strongest request waiting for its next owner boundary.</summary>
    public PiixResetRequest PendingRequest => _pending;
    /// <summary>Current physical domain phase in the own pulse sequence.</summary>
    public PiixResetPhase Phase => _phase;
    /// <summary>Whether CPURST still inhibits CPU fetch.</summary>
    public bool CpuResetHeld => _phase != PiixResetPhase.Idle;
    /// <summary>Whether PCIRST and the connected ISA domain remain asserted.</summary>
    public bool PciIsaResetHeld => _phase == PiixResetPhase.AllAsserted;
    /// <summary>Whether the deadline callback requests owner adoption of CPU deassertion.</summary>
    public bool CpuReleaseReady => _cpuReleaseReady;
    /// <summary>Virtual nanoseconds of the latest complete hard-reset assertion.</summary>
    public ulong AssertedAt => _assertedAt;
    /// <summary>Scheduled PCI/ISA release deadline.</summary>
    public ulong PciIsaReleaseAt => _pciReleaseAt;
    /// <summary>Minimum CPU deassertion deadline, adopted only at the next owner boundary.</summary>
    public ulong CpuReleaseAt => _cpuReleaseAt;
    /// <summary>Original readback: SRST only, with RCPU always reading zero.</summary>
    public byte ControlReadback => (byte)(_control & 2);
    /// <summary>Diagnostic stored edge latch, including hidden RCPU history.</summary>
    public byte ControlStored => _control;
    /// <summary>Checks complete 1/2/4-byte transactions before handling a start-address-selected decoder.</summary>
    private static uint Mask(int width) => width switch { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>CF9 drives only its low byte; CF8 dwords remain one undivided host configuration transaction.</summary>
    public uint Read(ushort port, int width)
    { uint mask = Mask(width); return port == 0xcf9 ? (mask & ~255u) | ControlReadback : _pci.Read(port, width); }
    /// <summary>Latches an actual RCPU rising edge; a read never rearms the hidden stored bit.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Mask(width);
        if (port != 0xcf9) { _pci.Write(port, value, width); return; }
        if (_phase != PiixResetPhase.Idle) return; // Own held-reset programming policy, not a new queued bus master.
        byte before = _control; _control = (byte)(value & 6);
        if ((before & 4) != 0 || (_control & 4) == 0) return;
        var request = (_control & 2) != 0 ? PiixResetRequest.Hard : PiixResetRequest.Init;
        if (request == PiixResetRequest.Hard) HardEdges++; else SoftEdges++;
        if ((int)request > (int)_pending) _pending = request; // Stronger pending domain wins before the next owner boundary.
    }
    /// <summary>Returns one accepted request without changing clocks, pins, or the CF9 edge latch.</summary>
    public PiixResetRequest ConsumePending()
    { var request = _pending; _pending = PiixResetRequest.None; return request; }
    /// <summary>Starts the own minimum pulse sequence after all deadlines can be represented, outside dispatched callbacks.</summary>
    public void BeginHardReset()
    {
        if (_clock.IsDispatching || _phase != PiixResetPhase.Idle) throw new InvalidOperationException("Hard reset requires an idle owner boundary.");
        ulong cpuAt = checked(_clock.Now + CpuHoldNanoseconds), pciAt = checked(_clock.Now + PciIsaHoldNanoseconds);
        _pending = PiixResetRequest.None; _control = 0; _phase = PiixResetPhase.AllAsserted; _cpuReleaseReady = false;
        _assertedAt = _clock.Now; _pciReleaseAt = pciAt; _cpuReleaseAt = cpuAt;
        _pciEvent = _clock.ScheduleAfter(PciIsaHoldNanoseconds, () =>
        { _pciEvent = 0; _phase = PiixResetPhase.CpuOnly; _owner.ReleasePiixPciIsaReset(); }, this, "release-pci-isa");
        _cpuEvent = _clock.ScheduleAfter(CpuHoldNanoseconds, () =>
        { _cpuEvent = 0; _cpuReleaseReady = true; }, this, "release-cpu-ready");
    }
    /// <summary>Completes CPU deassertion only between instructions; the board reanchors TSC before any subsequent opcode.</summary>
    public bool ConsumeCpuRelease()
    {
        if (!_cpuReleaseReady) return false;
        if (_clock.IsDispatching || _phase != PiixResetPhase.CpuOnly) throw new InvalidOperationException("CPU release requires its owner boundary.");
        _cpuReleaseReady = false; _phase = PiixResetPhase.Idle; return true;
    }
    /// <summary>Identity guard for the complete owner/clock/PCI graph; captures never serialize arbitrary callbacks.</summary>
    public bool HasBindings(DeviceScheduler clock, PciConfigurationBus pci, IPiixResetOwner owner) => ReferenceEquals(_clock, clock) && ReferenceEquals(_pci, pci) && ReferenceEquals(_owner, owner);
}
