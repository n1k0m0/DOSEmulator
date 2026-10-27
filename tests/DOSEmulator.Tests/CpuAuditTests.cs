// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Fixed-work CPU audit checks and paired-process evidence without relaxing architectural faults.</summary>
internal static class CpuAuditTests
{
    private sealed class Device : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Records mapped data reads independently of CPU checkpoint bookkeeping.</summary>
        public byte ReadByte(uint address) { Reads++; return 0x35; }
        /// <summary>Records mapped writes while keeping this synthetic peer deterministic.</summary>
        public void WriteByte(uint address, byte value) => Writes++;
    }
    private sealed class OperandDevice(byte[] data) : IMemoryDevice
    {
        public readonly byte[] Data = data;
        public int Reads, Writes;
        /// <summary>Counts a real fallback bus read of the independently initialized operand.</summary>
        public byte ReadByte(uint offset) { Reads++; return Data[(int)offset]; }
        /// <summary>Counts and retains a real fallback byte write for comparison with the RAM path.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; Data[(int)offset] = value; }
    }
    /// <summary>Requires exact guest results, fault frames or independently counted work.</summary>
    private static void Check(bool value) { if (!value) throw new Exception("CPU audit contract failed"); }
    /// <summary>Exercises real pure opcode families and both retained conservative checkpoint paths.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (byte[] code in new byte[][] {
            [0x0f,0x85,0,0,0,0], [0x0f,0xb6,0xc0], [0x0f,0xbe,0xc0], [0x0f,0xaf,0xc3],
            [0x0f,0x94,0xc0], [0x0f,0xc8], [0x0f,0xbc,0xc3], [0x0f,0xa4,0xc3,1],
            [0x0f,0xba,0xe0,1], [0x0f,0xc1,0xc3], [0xff,0xc0], [0xff,0xc8] })
            test("Audit CPU pure checkpoint " + Convert.ToHexString(code), () =>
            {
                var (c,m)=ProtectedModeTests.Rig(code);c.SetRegister(Register.Ax,0x35);c.SetRegister(Register.Bx,0x41);c.Eflags=2;
                long count=c.ControlCheckpointsSaved;c.Step();Check(c.ControlCheckpointsSaved==count && !c.TripleFaulted && c.Eip==0x2000+code.Length);
            });
        foreach (byte[] code in new byte[][] { [0x0f,0xb6,0x05,0,0,3,0], [0xff,0x05,0,0,3,0] })
            test("Audit CPU mapped path still captures lazily " + Convert.ToHexString(code), () =>
            {
                var (c,m)=ProtectedModeTests.Rig(code);var d=new Device();m.Map(0x30000,4,d,"Own audit MMIO");long count=c.ControlCheckpointsSaved;c.Step();
                Check(c.ControlCheckpointsSaved==count+1 && d.Reads>0 && c.Eip==0x2000+code.Length);
            });
        test("Audit CPU observer retains full checkpoint",()=>
        {
            var (c,_)=ProtectedModeTests.Rig([0x0f,0xb6,0xc0]);c.InstructionStarting+=(_,_)=>{};long n=c.ControlCheckpointsSaved;c.Step();Check(c.ControlCheckpointsSaved==n+1);
        });
        foreach (byte[] code in new byte[][] { [0x0f,0xa1], [0x0f,0x20,0xc0], [0xff,0xd0] })
            test("Audit CPU control opcode keeps full checkpoint " + Convert.ToHexString(code),()=>
            {
                var (c,m)=ProtectedModeTests.Rig(code);m.WriteDword(0x18000,16);c.SetRegister(Register.Ax,0x2000u+(uint)code.Length);long n=c.ControlCheckpointsSaved;c.Step();Check(c.ControlCheckpointsSaved==n+1 && !c.TripleFaulted);
            });
        foreach (byte[] code in new byte[][] { [0x0f,0xb6,0x05,0,0,3,0], [0xff,0x05,0,0,3,0] })
            test("Audit CPU pure-family page fault restores GPR and operand " + Convert.ToHexString(code),()=>
            {
                var (c,m)=PagingTests.Create(code);PagingTests.Pte(m,0x30000,0,0);c.SetRegister(Register.Ax,0x76543210);uint sp=c.GetRegister(Register.Sp);
                ProtectedModeTests.Fault(c,m,14,code[0]==0xff?2u:0u);Check(c.GetRegister(Register.Ax)==0x76543210 && c.GetRegister(Register.Sp)==sp-16 && c.Cr2==0x30000);
            });
        foreach (int size in new[] {4,8,10})
        foreach (bool store in new[] {false,true})
            test($"Audit CPU x87 RAM versus actual MMIO fallback size {size} store {store}",()=>
            {
                byte op=(byte)(size==4?0xd9:size==8?0xdd:0xdb),mod=(byte)(size==10?(store?0x3d:0x2d):(store?0x1d:0x05));
                byte[] code=[..(store?new byte[]{0xd9,0xe8}:[]),op,mod,0,0,3,0];
                var(a,ma)=ProtectedModeTests.Rig(code);var(b,mb)=ProtectedModeTests.Rig(code);
                byte[] bytes=Convert.FromHexString(size==4?"0000803F":size==8?"000000000000F03F":"0000000000000080FF3F");
                bytes.CopyTo(ma.Ram,0x30000);bytes.CopyTo(mb.Ram,0x30000);var d=new OperandDevice((byte[])bytes.Clone());mb.Map(0x30000,(uint)size,d,"Own actual x87 fallback");
                for(int i=0;i<(store?2:1);i++){a.Step();b.Step();}
                var sa=a.SaveState();var sb=b.SaveState();
                foreach(var field in sa.Fields.Where(f=>f.Key.StartsWith("_fpu")||f.Key.StartsWith("Fpu")||f.Key=="X87Instructions"))
                    Check(field.Value.GetRawText()==sb.Fields[field.Key].GetRawText());
                Check(a.Eip==b.Eip&&a.Eflags==b.Eflags&&ma.Ram.AsSpan(0x30000,size).SequenceEqual(d.Data));
                Check(d.Reads==(store?0:size)&&d.Writes==(store?size:0));
            });
        test("Audit CPU x87 plain RAM path respects physical A20 fold",()=>
        {
            var(a,ma)=ProtectedModeTests.Rig([0xdd,0x05,0,0,3,0]);var(b,mb)=ProtectedModeTests.Rig([0xdd,0x05,0,0,0x13,0]);
            ma.WriteDword(0x30000,0);ma.WriteDword(0x30004,0x3ff80000);mb.WriteDword(0x30000,0);mb.WriteDword(0x30004,0x3ff80000);mb.A20Enabled=false;
            a.Step();b.Step();Check(a.SaveState().Fields["_fpuRegisters"].GetRawText()==b.SaveState().Fields["_fpuRegisters"].GetRawText()&&a.FpuStatusWord==b.FpuStatusWord);
        });
        foreach(bool remap in new[]{false,true})
            test("Audit CPU committing precision store honors FERR memory change "+remap,()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0xdd,0x05,0,0,3,0,0xd9,0x2d,0,0,4,0,0xdf,0x1d,0,0x10,0x10,0]);
                m.WriteDword(0x30000,0);m.WriteDword(0x30004,0x3ff80000);m.WriteWord(0x40000,0x035f);m.WriteWord(0x1000,0xa5a5);m.WriteWord(0x101000,0x5a5a);
                var device=new OperandDevice(new byte[]{0x5a,0x5a});int edges=0;
                c.FpuErrorChanged+=asserted=>{if(asserted){edges++;if(remap)m.Map(0x101000,2,device,"Own FERR-time mapping");else m.A20Enabled=false;}};
                c.Step();c.Step();c.Step();Check(edges==1&&(c.FpuStatusWord&0x20)!=0&&c.FpuTop==0);
                if(remap)Check(device.Writes==2&&device.Data.SequenceEqual(new byte[]{2,0})&&m.Ram[0x101000]==0x5a);
                else Check(m.ReadWord(0x1000)==2&&m.Ram[0x101000]==0x5a);
            });

    }
    /// <summary>Captures semantic CPU state while excluding diagnostic count and an explicitly inactive old restart record.</summary>
    private static string Semantic(X86Cpu cpu)
    {
        StateBlock state=cpu.SaveState();state.Fields.Remove("ControlCheckpointsSaved");
        // An explicitly inactive old restart record is overwritten before use on the next Step.
        // Keep the active flag and every architectural/debug/FPU value in the comparison.
        if (!state.Fields["_controlCheckpointSaved"].GetBoolean()) state.Fields.Remove("_controlCheckpoint");
        return JsonSerializer.Serialize(state,StateValues.Json);
    }
    /// <summary>Runs isolated fixed-work samples with exact state/RAM hashes for baseline/candidate comparison.</summary>
    public static int Benchmark(string output)
    {
        if(File.Exists(output))throw new IOException("Evidence already exists.");
        var rows=new List<object>();
        foreach(var item in new (string Name,byte[] Code)[]{
            ("near-jcc",[0x0f,0x85,0,0,0,0,0xeb,0xf8]),
            ("movzx",[0x0f,0xb6,0xc0,0xeb,0xfb]),
            ("ff-inc",[0xff,0xc0,0xeb,0xfc]),
            ("x87-m64",[0xdd,0x05,0,0,2,0,0xdd,0xd8,0xeb,0xf6]),
            ("x87-store",[0xd9,0xe8,0xdd,0x1d,0,0,3,0,0xeb,0xf6]),
            ("x87-store-subscribed",[0xd9,0xe8,0xdd,0x1d,0,0,3,0,0xeb,0xf6])})
        {
            var (c,m)=ProtectedModeTests.Rig(item.Code);c.SetRegister(Register.Ax,0x35);c.Eflags=2;m.WriteDword(0x20000,0);m.WriteDword(0x20004,0x3ff80000);
            if(item.Name.EndsWith("subscribed",StringComparison.Ordinal))c.FpuErrorChanged+=static _=>{};
            for(int i=0;i<60000;i++)c.Step();
            var samples=new List<object>();const int steps=600000;
            for(int sample=0;sample<7;sample++)
            {
                long checkpoints=c.ControlCheckpointsSaved,allocated=GC.GetAllocatedBytesForCurrentThread(),begin=Stopwatch.GetTimestamp();
                for(int i=0;i<steps;i++)c.Step();
                long ticks=Stopwatch.GetTimestamp()-begin;allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;checkpoints=c.ControlCheckpointsSaved-checkpoints;
                samples.Add(new{ticks,allocated,checkpoints});
            }
            rows.Add(new{name=item.Name,stepsPerSample=steps,samples,semantic=Semantic(c),ramSha256=Convert.ToHexString(SHA256.HashData(m.Ram))});
        }
        var result=new{passed=true,scope="Fixed-work interpreter microbenchmarks, not Doom FPS or historical CPU clocks",stopwatchFrequency=Stopwatch.Frequency,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,rows};
        File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(output);return 0;
    }
}
