// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

if (!WhpxRealModeMachine.IsAvailable())
{
    Console.WriteLine("SKIP: Windows Hypervisor Platform is unavailable.");
    return;
}

var ports = new IoPortBus();
var device = new ProofPort();
ports.Map(0xe9, 1, device);
using var machine = new WhpxRealModeMachine(ports);
// MOV AL,'A'; OUT E9,AL; IN AL,E9; MOV [2000h],AL; HLT.
byte[] program = [0xb0, 0x41, 0xe6, 0xe9, 0xe4, 0xe9, 0xa2, 0x00, 0x20, 0xf4];
machine.WriteRam(0x1000, program);
machine.SetEntry(0, 0x1000);
var result = machine.RunUntilHalt(TimeSpan.FromSeconds(5));
if (result.IoExits != 2 || result.TotalExits != 3 || device.Writes != 1 || device.Reads != 1 ||
    device.LastWrite != 0x41 || machine.ReadRam(0x2000, 1)[0] != 0x7a ||
    !machine.TakeDirtyPages().Contains(2))
    throw new InvalidOperationException("WHP/DOSE bus or RAM result differs from the own real-mode oracle.");
Console.WriteLine($"PASS: WHPX real-mode RAM, DOSE I/O bus (2 exits), dirty page, HLT; RIP={result.Rip:X}.");
machine.Dispose();

var boardMemory = new MemoryBus(1 << 20);
var board = new DevelopmentBoardPorts(boardMemory);
var boardPorts = new IoPortBus();
boardPorts.Map(0xe9, 1, board);
using var boardMachine = new WhpxRealModeMachine(boardPorts);
boardMachine.WriteRam(0x1010, [0xb0, (byte)'W', 0xe6, 0xe9, 0xf4]);
boardMachine.SetEntry(0x100, 0x10);
var boardResult = boardMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
if (boardResult.IoExits != 1 || board.DebugText != "W")
    throw new InvalidOperationException("Actual DOSE development-board I/O did not receive the WHP exit.");
Console.WriteLine("PASS: WHPX exit reached the actual DOSE E9 board device.");

sealed class ProofPort : IIoPortDevice
{
    public int Writes { get; private set; }
    public int Reads { get; private set; }
    public uint LastWrite { get; private set; }
    public uint Read(ushort port, int width) { Reads++; return 0x7a; }
    public void Write(ushort port, uint value, int width) { Writes++; LastWrite = value; }
}
