// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class CheckpointOptimizationTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition,string detail="checkpoint optimization invariant"){if(!condition)throw new Exception(detail);}
    private sealed class Device(byte[] bytes,Action<int> read,Action<int>? write=null):IMemoryDevice
    {
        public int Reads,Writes;
        /// <summary>Returns a controlled mapped-device byte and records the test observation/fault hook.</summary>
        public byte ReadByte(uint offset){read(Reads++);return bytes[offset%bytes.Length];}
        /// <summary>Applies the controlled mapped-device write and records its test observation/fault hook.</summary>
        public void WriteByte(uint offset,byte value){write?.Invoke(Writes++);bytes[offset%bytes.Length]=value;}
    }
    /// <summary>Registers the named checkpoint optimization regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("ordinary protected RAM/register operations avoid large control checkpoints",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xb8,1,0,0,0,0x83,0xc0,1,0xa3,0,0,3,0,0x81,0x05,0,0,3,0,1,0,0,0]);long before=c.ControlCheckpointsSaved;
            for(int i=0;i<4;i++)c.Step();Check(c.GetRegister(Register.Ax)==2&&m.ReadDword(0x30000)==3&&c.ControlCheckpointsSaved==before);
        });
        test("protected control instructions and instruction observers retain full checkpoints",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x8e,0xd8,0x90]);c.SetRegister(Register.Ax,16);long before=c.ControlCheckpointsSaved;c.Step();Check(c.ControlCheckpointsSaved==before+1);
            c.InstructionStarting+=(_,_)=>{};c.Step();Check(c.ControlCheckpointsSaved==before+2);
        });
        foreach(bool fetch in new[]{false,true})
            test($"opaque {(fetch?"instruction":"data")} device callbacks preserve old hidden segments and GPR on a later GP",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(fetch?[0xa1,0,0,3,0]:[0x83,0x05,0,0,3,0,1]);Descriptor(m,40,0x10000,0xffff,0x92);var old=c.GetSegmentCache(Segment.Ds);uint sp=c.GetRegister(Register.Sp);
                byte[] bytes=fetch?[0xa1,0,0,3,0]:new byte[4096];
                var dev=new Device(bytes,n=>{if(n==0){c.SetSegment(Segment.Ds,40);c.SetRegister(Register.Sp,0x13000);}if(!fetch||n==1)c.SetSegment(Segment.Es,0x200);});
                m.Map(fetch?0x2000u:0x30000u,4096,dev,"observable callback");Fault(c,m,13,0x200);
                Check(c.GetSegment(Segment.Ds)==16&&c.GetSegmentCache(Segment.Ds)==old&&c.GetRegister(Register.Sp)==sp-16,"old control context not restored");
            });
        foreach(bool write in new[]{false,true}) foreach(bool word in new[]{false,true})
            test($"unpaged {(word?"word":"dword")} {(write?"write":"read")} crossing RAM into MMIO checkpoints before the first callback",()=>
            {
                byte[] instruction=word?[(byte)0x66,(byte)(write?0xa3:0xa1),0xff,0xff,2,0]:[(byte)(write?0xa3:0xa1),0xff,0xff,2,0];
                var(c,m)=ProtectedModeTests.Rig(instruction);Descriptor(m,40,0x10000,0xffff,0x92);
                var old=c.GetSegmentCache(Segment.Ds);uint sp=c.GetRegister(Register.Sp);long checkpoints=c.ControlCheckpointsSaved;
                // Mutates the selected restart-sensitive CPU state before the controlled mapped-access fault.
                void ChangeThenFault(int n){if(n!=0)return;c.SetSegment(Segment.Ds,40);c.SetRegister(Register.Sp,0x13000);c.SetSegment(Segment.Es,0x200);}
                var dev=new Device(new byte[4096],write?_=>{}:ChangeThenFault,write?ChangeThenFault:null);
                m.Map(0x30000,4096,dev,"device after ordinary RAM");Fault(c,m,13,0x200);
                Check(c.GetSegment(Segment.Ds)==16&&c.GetSegmentCache(Segment.Ds)==old&&c.GetRegister(Register.Sp)==sp-16,"cross-page MMIO callback escaped checkpoint");
                Check(c.ControlCheckpointsSaved==checkpoints+1&&dev.Reads+dev.Writes==1,"checkpoint was not taken before the first callback");
            });
        test("read-modify-write still checks write permission before an MMIO read",()=>
        {
            var(c,m)=PagingTests.Create([0x83,0x05,0,0,3,0,1]);var dev=new Device(new byte[4096],_=>{});m.Map(0x40000,4096,dev,"read must not occur");PagingTests.Pte(m,0x30000,0x40000,5);
            Fault(c,m,14,3);Check(dev.Reads==0&&dev.Writes==0);
        });
    }
}
