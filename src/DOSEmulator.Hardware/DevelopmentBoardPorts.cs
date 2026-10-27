// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class DevelopmentBoardPorts(MemoryBus memory) : IIoPortDevice
{
    private readonly StringBuilder _debug = new();
    private string? _debugSnapshot = "";
    private byte _port92 = 2;
    /// <summary>Immutable cached E9 diagnostic text, invalidated only by an actual appended guest byte.</summary>
    public string DebugText => _debugSnapshot ??= _debug.ToString();
    /// <summary>Most recent guest POST byte written to the reference diagnostic port.</summary>
    public byte PostCode { get; private set; }
    /// <summary>Number of actual firmware POST-10 writes observed, distinct from hardware-reset requests.</summary>
    public long Post10Count { get; private set; }
    /// <summary>Whether an emulated reset request is pending consumption by the platform owner.</summary>
    public bool ResetRequested { get; private set; }
    /// <summary>Consumes a pending edge-triggered reset request exactly once.</summary>
    public bool ConsumeResetRequest()
    {
        bool requested = ResetRequested; ResetRequested = false; return requested;
    }
    /// <summary>Reads the implemented DOSE board control port or its open-bus fallback.</summary>
    public uint Read(ushort port, int width) => port == 0x92 ? _port92 : 0xffu;
    /// <summary>Handles POST/debug/A20/reset ports, invalidating diagnostic text only when E9 actually appends a byte.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == 0xe9)
        {
            if (_debug.Length < 8192) { _debug.Append((char)(value & 255)); _debugSnapshot = null; }
        }
        else if (port == 0x80) { PostCode = (byte)value; if (PostCode == 0x10) Post10Count++; }
        else if (port == 0x92)
        {
            if ((value & 1) != 0 && (_port92 & 1) == 0) ResetRequested = true;
            _port92 = (byte)value;
            memory.A20Enabled = (value & 2) != 0;
        }
    }
}
