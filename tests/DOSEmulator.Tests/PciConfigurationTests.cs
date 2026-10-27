// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Independent lane/BDF arithmetic, rejection isolation and actual guest IN/OUT checks for the first M4 bus primitive.</summary>
internal static class PciConfigurationTests
{
    /// <summary>Requires a concrete decoding or no-side-effect invariant.</summary>
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>A test-only local function with explicit bytes and no claimed commercial identity or BAR behavior.</summary>
    private sealed class Function(byte seed) : IPciConfigurationFunction
    {
        public readonly byte[] Bytes = Enumerable.Range(0,256).Select(i => unchecked((byte)(seed+i*73))).ToArray();
        public readonly List<(bool Write,int Offset,int Width,uint Value)> Calls = new();
        /// <summary>Assembles selected bytes independently from the bus's address calculation.</summary>
        public uint ReadConfiguration(int offset, int width)
        {
            uint value = 0; for (int i = 0; i < width; i++) value |= (uint)Bytes[offset+i] << (i*8);
            Calls.Add((false,offset,width,value)); return value;
        }
        /// <summary>Records one original transaction and applies its selected byte lanes.</summary>
        public void WriteConfiguration(int offset, uint value, int width)
        {
            Calls.Add((true,offset,width,value));
            for (int i = 0; i < width; i++) Bytes[offset+i] = (byte)(value >> (i*8));
        }
    }
    /// <summary>Registers byte-lane, bus separation, invalid transaction and CPU-selected configuration tests.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int bus in new[] { 0,1,127,255 }) foreach (int device in new[] { 0,1,15,31 }) foreach (int function in new[] { 0,1,7 })
        {
            test($"M4 PCI mechanism1 selects bus {bus} device {device} function {function} without alias", () =>
            {
                var pci = new PciConfigurationBus(); var endpoint = new Function(19);
                pci.RegisterFunction((byte)bus,device,function,endpoint);
                uint bdf = (uint)((bus << 16) | (device << 11) | (function << 8));
                pci.Write(0xcf8,0x80000000|bdf|0x70,4);
                Check(pci.Read(0xcfc,4) == 0xde954c03 && endpoint.Calls.Count == 1 && endpoint.Calls[0].Offset == 0x70,"Exact endpoint and register selection");
                foreach (uint wrong in new[] { bdf^0x100u,bdf^0x800u,bdf^0x10000u })
                {
                    pci.Write(0xcf8,0x80000070|wrong,4); Check(pci.Read(0xcfc,4) == uint.MaxValue,"Absent BDF floats high");
                    pci.Write(0xcfc,0xaabbccdd,4);
                }
                Check(endpoint.Calls.Count == 1,"Absent addresses never reach another function");
            });
        }
        foreach (int offset in new[] { 0,4,0x40,0x80,0xfc }) foreach (int width in new[] { 1,2,4 })
        {
            test($"M4 PCI mechanism1 register {offset:X2} width {width} preserves selected lanes", () =>
            {
                for (int lane = 0; lane+width <= 4; lane++)
                {
                    var pci = new PciConfigurationBus(); var endpoint = new Function(7); pci.RegisterFunction(0,3,2,endpoint);
                    pci.Write(0xcf8,(uint)(0x80001a00|offset),4); byte[] before = endpoint.Bytes.ToArray();
                    ushort port = (ushort)(0xcfc+lane); uint expected = 0;
                    for (int i = 0; i < width; i++) expected |= (uint)before[offset+lane+i] << (i*8);
                    Check(pci.Read(port,width) == expected,"Independent little-endian lane read");
                    pci.Write(port,0x87654321,width);
                    for (int i = 0; i < 256; i++) Check(endpoint.Bytes[i] == (i >= offset+lane && i < offset+lane+width ? (byte)(0x87654321u >> ((i-offset-lane)*8)) : before[i]),"Only requested bytes commit");
                    Check(endpoint.Calls.Count == 2 && endpoint.Calls.All(c => c.Offset == offset+lane && c.Width == width),"One original callback per valid bus transfer");
                }
            });
        }
        foreach (ushort port in new ushort[] { 0xcf7,0xcf8,0xcf9,0xcfa,0xcfb,0xcfc,0xcfd,0xcfe,0xcff,0xd00 })
        foreach (int width in new[] { 1,2,4 })
        {
            if (port == 0xcf8 && width == 4 || port >= 0xcfc && port <= 0xcff && port-0xcfc+width <= 4) continue;
            test($"M4 PCI mechanism1 unsupported address/width {port:X4}/{width} has no partial effect", () =>
            {
                var pci = new PciConfigurationBus(); var endpoint = new Function(0); pci.RegisterFunction(0,0,0,endpoint);
                pci.Write(0xcf8,0x800000fc,4); uint address = pci.ConfigurationAddress;
                uint expected = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
                Check(pci.Read(port,width) == expected,"Undriven transaction"); pci.Write(port,0x11223344,width);
                Check(endpoint.Calls.Count == 0 && pci.ConfigurationAddress == address,"No hidden subaccess, lane wrap or address mutation");
            });
        }
        test("M4 PCI mechanism1 disable and reset isolate all registered functions", () =>
        {
            var pci = new PciConfigurationBus(); var endpoint = new Function(3); pci.RegisterFunction(0,0,0,endpoint);
            pci.Write(0xcf8,0x7fffffff,4); Check(pci.ConfigurationAddress == 0xfffffc,"Reserved bits read zero");
            Check(pci.Read(0xcfc,4) == uint.MaxValue,"Enable bit required"); pci.Write(0xcfc,0,4);
            Check(endpoint.Calls.Count == 0,"Disabled writes are silent"); pci.Write(0xcf8,0x80000000,4); pci.ResetAddress();
            Check(pci.ConfigurationAddress == 0 && pci.FunctionCount == 1 && pci.Read(0xcfc,4) == uint.MaxValue,"Latch reset retains explicit local topology");
        });
        test("M4 PCI mechanism1 duplicate and invalid registration leave live topology usable", () =>
        {
            var pci = new PciConfigurationBus(); var endpoint = new Function(1); pci.RegisterFunction(4,5,6,endpoint);
            int rejected = 0;
            foreach (var action in new Action[] { () => pci.RegisterFunction(4,5,6,new Function(2)), () => pci.RegisterFunction(0,32,0,endpoint),
                () => pci.RegisterFunction(0,-1,0,endpoint), () => pci.RegisterFunction(0,0,8,endpoint), () => pci.RegisterFunction(0,0,-1,endpoint),
                () => pci.RegisterFunction(0,0,0,null!) })
            { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { rejected++; } }
            Check(rejected == 6 && pci.FunctionCount == 1,"Prevalidated registration");
            pci.Write(0xcf8,0x80042e00,4); Check(pci.Read(0xcfc,1) == 1 && endpoint.Calls.Count == 1,"Old function survives rejected registration");
        });
        foreach (bool wide in new[] { false,true })
        {
            test($"M4 PCI mechanism1 actual guest OUT/IN operand {(wide?32:16)}", () =>
            {
                var ports = new IoPortBus(); var pci = new PciConfigurationBus(); var endpoint = new Function(0x34);
                pci.RegisterFunction(2,5,3,endpoint); ports.Map(0xcf8,8,pci);
                byte[] code = [0xba,0xf8,0x0c,0,0,0xb8,0x00,0x2b,0x02,0x80,0xef,0xba,0xfc,0x0c,0,0,
                    ..(wide ? Array.Empty<byte>() : new byte[] {0x66}),0xed,0xa3,0,0,3,0,0xf4];
                var (c,m) = ProtectedModeTests.Rig(code,io:ports);
                for (int i=0;i<10 && !c.Halted;i++) c.Step();
                uint expected = wide ? 0x0fc67d34u : 0x80027d34u;
                Check(c.Halted && !c.TripleFaulted && m.ReadDword(0x30000) == expected,"Real CPU transfer and 16-bit high-half preservation");
                Check(endpoint.Calls.Count == 1 && endpoint.Calls[0].Width == (wide?4:2) && pci.ConfigurationAddress == 0x80022b00,"Decoded bus address and operand width");
            });
        }
        test("M4 PCI foundation does not silently enable PCI or Pentium on existing M3 machines", () =>
        {
            using var m = PcMachine.CreateDemo(); Check(m.Ports.Read(0xcf8,4) == uint.MaxValue && m.Ports.Read(0xcfc,4) == uint.MaxValue,"Existing 486 bus remains explicitly ISA");
        });
        test("M4 PCI foundation rejects unregistered snapshot state while the old machine remains usable", () =>
        {
            using var m = PcMachine.CreateDemo(); var pci = new PciConfigurationBus(); m.Ports.Map(0xcf8,8,pci);
            uint ip=m.Cpu.Eip; long before=m.Cpu.Instructions; bool rejected=false;
            try { m.CaptureHardwareState(); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected && m.Cpu.Eip==ip && m.Cpu.Instructions==before && pci.ConfigurationAddress==0,"Incomplete topology must not produce a partial full snapshot");
            m.RunSteps(1); Check(m.Fault is null && m.Cpu.Instructions==before+1,"Owner can continue after rejected capture");
        });
        test("M4 PCI mechanism1 rejects invalid transfer widths before endpoint or latch changes", () =>
        {
            var pci=new PciConfigurationBus(); var endpoint=new Function(1); pci.RegisterFunction(0,0,0,endpoint);
            pci.Write(0xcf8,0x80000000,4); int rejected=0;
            foreach(int width in new[]{-1,0,3,8})
            {
                try { pci.Read(0xcfc,width); } catch(ArgumentOutOfRangeException) { rejected++; }
                try { pci.Write(0xcf8,0,width); } catch(ArgumentOutOfRangeException) { rejected++; }
            }
            Check(rejected==8 && endpoint.Calls.Count==0 && pci.ConfigurationAddress==0x80000000,"Invalid I/O is atomic");
        });
    }
}
